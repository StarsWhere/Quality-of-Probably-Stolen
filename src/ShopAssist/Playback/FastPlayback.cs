using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed class FastPlayback
    {
        private const string PatchId = ModEntry.Id + ".fastplay";
        private static readonly int[] Speeds = { 2, 4, 8, 16 };
        private static readonly FieldInfo PhoneEpoch = AccessTools.Field(typeof(DialoguePhoneUIManager), "conversationEpoch");
        private static FastPlayback active;
        private static int epoch;
        private readonly ShopSettings settings;
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<int> mode, speed;
        private readonly Func<KeyCode> key;
        private readonly Action<string> warn;
        private readonly HashSet<string> warnings = new HashSet<string>();
        private Harmony harmony;
        private GameObject driver, indicator;
        private CustomUIElement label;
        private int inputFrame = -1, advanceFrame = -1, previousMode;
        private KeyCode previousKey;
        private bool latched, heldSession, releaseRequired, requested, paused, focused;
        private float nextAdvance;
        private Dialogue finishedPhone;
        private int finishedPhoneEpoch;
        private PlayerStore owner;
        private string run;
        private int slot;

        internal FastPlayback(ShopSettings settings, Action<string> warn)
        {
            this.settings = settings; this.warn = warn;
            enabled = settings.Toggle("f42.enabled", "f42.tip");
            mode = settings.Choice("f42.mode", "f42.mode.tip", new[] { "f42.hold", "f42.toggle" });
            speed = settings.Choice("f42.speed", "f42.speed.tip", new[] { "f42.speed2", "f42.speed4", "f42.speed8", "f42.speed16" }, 2);
            key = settings.Key("f42.key", "f42.key.tip", true);
        }

        internal static int Epoch => epoch;
        internal static float Rate
        {
            get
            {
                var current = active;
                if (current == null) return 1f;
                current.RefreshInput();
                current.paused = current.requested && Blocked();
                return current.requested && !current.paused ? Speeds[Mathf.Clamp(current.speed.Value, 0, 3)] : 1f;
            }
        }

        internal void Start()
        {
            active = this; focused = Application.isFocused;
            previousKey = key(); previousMode = mode.Value;
            settings.Changed += SettingsChanged;
            ModHook.OnGameLoadedInit += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
            harmony = new Harmony(PatchId);
            FastPlaybackDialogue.Install(harmony, Warn);
            FastPlaybackMotion.Install(harmony, Warn);
            FastPlaybackTransitions.Install(harmony, Warn);
            driver = new GameObject(PatchId + ".input");
            UnityEngine.Object.DontDestroyOnLoad(driver);
            driver.AddComponent<FastPlaybackInputLoop>();
        }

        private void Warn(string message)
        {
            if (!warnings.Add(message)) return;
            warn("F42: " + message);
            if (enabled.Value) ShopNotice.Show(ShopSettings.Text("f42.unavailable"));
        }

        internal static void EarlyTick()
        {
            var current = active;
            if (current == null) return;
            try { current.RefreshInput(); FastPlaybackMotion.Tick(); }
            catch (Exception error) { current.Warn("Playback stopped: " + error); current.Dispose(); }
        }

        internal static void FocusChanged(bool focus)
        {
            if (active == null) return;
            active.focused = focus;
            if (!focus) active.StopRequest(true);
            active.inputFrame = -1;
        }

        private void StopRequest(bool requireRelease)
        {
            latched = heldSession = requested = paused = false;
            releaseRequired = requireRelease;
            nextAdvance = 0f;
            if (indicator != null) indicator.SetActive(false);
        }

        private void SettingsChanged()
        {
            if (!enabled.Value || previousMode != mode.Value) StopRequest(true);
            previousMode = mode.Value;
            inputFrame = -1;
        }

        private void RefreshInput()
        {
            if (inputFrame == Time.frameCount) return;
            inputFrame = Time.frameCount;
            KeyCode binding = key();
            if (binding != previousKey || previousMode != mode.Value)
            {
                StopRequest(true); previousKey = binding; previousMode = mode.Value;
            }
            if (!enabled.Value || !focused || !Application.isFocused)
            { StopRequest(true); return; }
            bool held = binding != KeyCode.None && Input.GetKey(binding);
            if (!held) { heldSession = false; releaseRequired = false; }
            bool down = binding != KeyCode.None && Input.GetKeyDown(binding);
            bool entering = StoreUIManager.IsAnyTyping() || KeyBindHelper.SuppressHotkeys ||
                GameOptionUIManager.Instance != null && GameOptionUIManager.Instance.IsOpen;
            bool chord = OtherModifier(binding);
            if (down && (entering || chord)) releaseRequired = true;
            if (down && !releaseRequired && !entering && !chord)
            {
                string conflict = ShopSettings.ShortcutOwner("f42.key", binding);
                if (conflict != null)
                {
                    StopRequest(true);
                    ShopNotice.Show(ShopSettings.Format("settings.key_conflict", KeyBindHelper.KeyName(binding), conflict));
                }
                else if (mode.Value == 1) latched = !latched;
                else heldSession = true;
            }
            requested = mode.Value == 1 ? latched : held && heldSession && !releaseRequired;
            paused = requested && Blocked();
        }

        private static bool Visible(GameObject value) => value != null && value.activeInHierarchy;
        private static bool OtherModifier(KeyCode binding) =>
            binding != KeyCode.LeftControl && Input.GetKey(KeyCode.LeftControl) ||
            binding != KeyCode.RightControl && Input.GetKey(KeyCode.RightControl) ||
            binding != KeyCode.LeftAlt && Input.GetKey(KeyCode.LeftAlt) ||
            binding != KeyCode.RightAlt && Input.GetKey(KeyCode.RightAlt) ||
            binding != KeyCode.LeftShift && Input.GetKey(KeyCode.LeftShift) ||
            binding != KeyCode.RightShift && Input.GetKey(KeyCode.RightShift);
        private static bool ChoicesVisible(List<DialogChoiceBubble> choices)
        {
            if (choices == null) return false;
            foreach (var choice in choices)
                if (choice != null && Visible(choice.gameObject)) return true;
            return false;
        }

        private static bool Blocked()
        {
            if (Time.timeScale <= 0f || KeyBindHelper.SuppressHotkeys || StoreUIManager.IsAnyTyping() ||
                GameOptionUIManager.Instance != null && GameOptionUIManager.Instance.IsOpen ||
                GenericConfirmationUIManager.Instance != null && Visible(GenericConfirmationUIManager.Instance.panel)) return true;
            var ui = StoreUIManager.Instance;
            if (ui != null && (ui.escapeUIManager != null && Visible(ui.escapeUIManager.menuPanel) ||
                ui.confirmPanelUIManager != null && Visible(ui.confirmPanelUIManager.panel) ||
                Visible(ui.closeShutterConfirmationPanel))) return true;
            var menu = MainMenuUIController.Instance;
            if (menu != null && menu.confirmPanelUIManager != null && Visible(menu.confirmPanelUIManager.panel)) return true;
            return DialogUIManager.Instance != null && ChoicesVisible(DialogUIManager.Instance.dialogChoiceBubbles) ||
                DialoguePhoneUIManager.Instance != null && ChoicesVisible(DialoguePhoneUIManager.Instance.dialogChoiceBubbles);
        }

        internal void Tick()
        {
            if (!ReferenceEquals(active, this)) return;
            var currentOwner = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            if (!ReferenceEquals(owner, currentOwner) || currentOwner != null && (run != currentOwner.runID || slot != currentOwner.saveSlotId))
            {
                // Load hooks normally invalidate first. Identity also covers a replacement by another mod.
                if (owner != null) { StopRequest(true); finishedPhone = null; }
                owner = currentOwner; run = owner?.runID; slot = owner == null ? -1 : owner.saveSlotId;
            }
            RefreshInput();
            // A native callback can open a choice after the early input pass.
            paused = requested && Blocked();
            if (requested && !paused && Time.unscaledTime >= nextAdvance && advanceFrame != Time.frameCount)
            {
                advanceFrame = Time.frameCount;
                nextAdvance = Time.unscaledTime + 0.8f / Speeds[Mathf.Clamp(speed.Value, 0, 3)];
                AdvanceDialogue();
                paused = requested && Blocked();
            }
            DrawIndicator();
        }

        private void AdvanceDialogue()
        {
            var phone = DialoguePhoneUIManager.Instance;
            if (phone != null && phone.currentDialog != null &&
                (phone.clientBubble != null && Visible(phone.clientBubble.gameObject) || phone.playerBubble != null && Visible(phone.playerBubble.gameObject)))
            {
                int currentEpoch = PhoneEpoch == null ? -1 : (int)PhoneEpoch.GetValue(phone);
                if (!ReferenceEquals(finishedPhone, phone.currentDialog) || finishedPhoneEpoch != currentEpoch)
                {
                    phone.OnAdvancePressed();
                    return;
                }
            }
            var dialog = DialogUIManager.Instance;
            if (dialog != null && dialog.currentDialog != null) dialog.OnAdvancePressed();
        }

        internal static void PhoneEnded(DialoguePhoneUIManager phone)
        {
            if (active == null || phone == null) return;
            active.finishedPhone = phone.currentDialog;
            active.finishedPhoneEpoch = PhoneEpoch == null ? -1 : (int)PhoneEpoch.GetValue(phone);
        }

        internal static void NoteDialogueAdvance()
        {
            if (active != null) active.advanceFrame = Time.frameCount;
        }

        private void DrawIndicator()
        {
            if (!requested) { if (indicator != null) indicator.SetActive(false); return; }
            var manager = CustomUIManager.Instance;
            if (manager == null) return;
            if (indicator == null)
            {
                var layer = manager.GetLayerRoot(CustomUIManager.LAYER_META);
                if (layer == null) return;
                indicator = new GameObject(PatchId + ".status", typeof(RectTransform), typeof(CanvasGroup));
                var rect = (RectTransform)indicator.transform;
                rect.SetParent(layer, false);
                rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
                rect.anchoredPosition = new Vector2(-12f, -12f); rect.sizeDelta = new Vector2(180f, 32f);
                var canvas = indicator.GetComponent<CanvasGroup>(); canvas.interactable = canvas.blocksRaycasts = false;
                var background = indicator.AddComponent<Image>(); background.raycastTarget = false;
                background.color = new Color(.09f, .10f, .12f, .85f);
                label = manager.BuildInto(rect).AddLabel("", "playback").Last;
                label.text.alignment = TMPro.TextAlignmentOptions.Center;
                label.text.richText = false; label.text.raycastTarget = false;
            }
            string message = paused ? ShopSettings.Text("f42.paused") : ShopSettings.Format("f42.playing", Speeds[Mathf.Clamp(speed.Value, 0, 3)]);
            if (label.GetText() != message) label.SetText(message);
            if (!indicator.activeSelf) indicator.SetActive(true);
        }

        private void SceneChanged(Scene before, Scene after) => Reset();
        private void Reset()
        {
            epoch++; StopRequest(true); inputFrame = -1; finishedPhone = null;
            owner = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            run = owner?.runID; slot = owner == null ? -1 : owner.saveSlotId;
            FastPlaybackDialogue.Reset(); FastPlaybackMotion.Reset(); FastPlaybackTransitions.Reset();
        }

        internal void Dispose()
        {
            if (!ReferenceEquals(active, this)) return;
            StopRequest(true); active = null;
            settings.Changed -= SettingsChanged;
            ModHook.OnGameLoadedInit -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            FastPlaybackMotion.Dispose(); FastPlaybackDialogue.Dispose(); FastPlaybackTransitions.Dispose();
            harmony?.UnpatchAll(PatchId); harmony = null;
            if (driver != null) UnityEngine.Object.Destroy(driver);
            if (indicator != null) UnityEngine.Object.Destroy(indicator);
            driver = indicator = null; label = null;
        }

        // These waits are used only by the explicitly patched presentation call sites.
        internal static object Delay(float seconds) => new PresentationDelay(seconds);
        private sealed class PresentationDelay : CustomYieldInstruction
        {
            private float remaining;
            private int frame;
            internal PresentationDelay(float seconds) { remaining = Mathf.Max(0f, seconds); frame = Time.frameCount; }
            public override bool keepWaiting
            {
                get
                {
                    if (frame == Time.frameCount) return true;
                    frame = Time.frameCount;
                    remaining -= Time.deltaTime * Rate;
                    return remaining > 0f;
                }
            }
        }

        internal static IEnumerator WaitAndDo(float seconds, Action action)
        {
            var own = new PresentationScope();
            yield return Delay(seconds);
            if (own.Current) action();
        }

        private sealed class PresentationScope
        {
            private readonly int generation = Epoch;
            private readonly PlayerStore player;
            private readonly string run;
            private readonly int slot;
            internal PresentationScope()
            {
                var current = PlayerStore.HasInstance ? PlayerStore.Instance : null;
                // The starting fade begins before InitialSave assigns a run and slot.
                // That initialization is not a save replacement; its native owner and epoch still guard it.
                player = current != null && !string.IsNullOrEmpty(current.runID) && current.saveSlotId >= 0 ? current : null;
                run = player?.runID; slot = player == null ? -1 : player.saveSlotId;
            }
            internal bool Current => generation == Epoch && (player == null ||
                PlayerStore.HasInstance && ReferenceEquals(player, PlayerStore.Instance) && player.runID == run && player.saveSlotId == slot);
        }

        internal static IEnumerator Scope(IEnumerator original) => Scoped(original, new PresentationScope());
        private static IEnumerator Scoped(IEnumerator original, PresentationScope own)
        {
            try
            {
                while (own.Current && original.MoveNext()) yield return original.Current;
            }
            finally { (original as IDisposable)?.Dispose(); }
        }
    }

    [DefaultExecutionOrder(-10000)]
    internal sealed class FastPlaybackInputLoop : MonoBehaviour
    {
        private void Update() => FastPlayback.EarlyTick();
        private void OnApplicationFocus(bool focused) => FastPlayback.FocusChanged(focused);
    }
}
