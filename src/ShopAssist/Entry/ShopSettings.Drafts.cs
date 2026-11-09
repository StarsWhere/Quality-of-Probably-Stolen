using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    internal sealed partial class ShopSettings
    {
        private sealed class DraftWatch
        {
            internal bool Native;
            internal SettingsDraftState State;
        }

        private sealed class RecoveryPrompt
        {
            internal CustomUIWindow Owner;
            internal DraftWatch Watch;
            internal string[] Keys;
        }

        private static readonly System.Reflection.FieldInfo PendingKeys = AccessTools.Field(typeof(GameOptionUIManager), "pendingKeys");
        private static readonly System.Reflection.MethodInfo RefreshKey = AccessTools.Method(typeof(GameOptionUIManager), "RefreshKeyBindButton");
        private static readonly System.Reflection.MethodInfo RefreshRules = AccessTools.Method(typeof(GameOptionUIManager), "RefreshRules");
        private readonly Dictionary<CustomUIWindow, DraftWatch> drafts = new Dictionary<CustomUIWindow, DraftWatch>();
        private readonly Dictionary<bool, Dictionary<string, object>> recovered = new Dictionary<bool, Dictionary<string, object>>();
        private readonly Queue<RecoveryPrompt> recoveryPrompts = new Queue<RecoveryPrompt>();
        private CustomUIWindow discardPrompt;
        private bool discardConfirmed;

        private sealed class DraftObserver : MonoBehaviour
        {
            internal ShopSettings Owner;
            internal CustomUIWindow Window;
            internal DraftWatch Watch;
            private void LateUpdate() => Capture();
            private void OnDisable() => Capture();
            private void Capture()
            {
                if (Owner == null || Window?.IsAlive != true || Watch == null) return;
                try
                {
                    // Missing controls during scene teardown retain their last observed values.
                    Watch.State.Observe(Owner.DraftValues(Window, Watch.Native));
                }
                catch (Exception error) { Debug.LogError("[ShopAssist] Could not capture settings draft: " + error); }
            }
            private void OnDestroy() => Owner?.PreserveDestroyedDraft(Window, Watch);
        }

        private Dictionary<string, object> DraftValues(CustomUIWindow window, bool native)
        {
            var values = new Dictionary<string, object>();
            if (window?.IsAlive != true) return values;
            if (native)
            {
                var manager = GameOptionUIManager.Instance;
                var keys = manager != null ? PendingKeys?.GetValue(manager) as Dictionary<string, KeyCode> : null;
                foreach (var entry in GameOptionRegistry.Sections.SelectMany(section => section.entries))
                {
                    CustomUIElement control = window.Get(entry.id);
                    if (control?.IsAlive != true) continue;
                    if (entry.kind == GameOptionRegistry.Kind.KeyBind)
                    {
                        if (keys != null && keys.TryGetValue(entry.id, out KeyCode key)) values[entry.id] = key;
                    }
                    else if (control.toggle != null) values[entry.id] = control.GetBool();
                    else if (control.dropdown != null) values[entry.id] = control.GetIndex();
                    else if (control.slider != null) values[entry.id] = control.GetFloat();
                    else if (control.input != null) values[entry.id] = control.GetString();
                }
            }
            else
            {
                foreach (ConfigEntry entry in config.Entries)
                {
                    if (entry.Key == "f19.pinned") continue;
                    CustomUIElement control = window.Get(entry.Key);
                    if (control?.IsAlive != true) continue;
                    if (control.toggle != null) values[entry.Key] = control.GetBool();
                    else if (control.dropdown != null) values[entry.Key] = control.GetIndex();
                    else if (control.slider != null) values[entry.Key] = control.GetFloat();
                    else if (control.input != null) values[entry.Key] = control.GetString();
                }
            }
            return values;
        }

        private void WatchDraft(CustomUIWindow window, bool native, bool restore = false)
        {
            if (window?.IsAlive != true) return;
            foreach (CustomUIWindow old in drafts.Keys.Where(value => value?.IsAlive != true).ToArray())
                PreserveDestroyedDraft(old, drafts[old]);
            var values = DraftValues(window, native);
            if (!restore && drafts.TryGetValue(window, out DraftWatch existing) && existing.Native == native)
            { existing.State.Reset(values); return; }
            var watch = new DraftWatch { Native = native, State = new SettingsDraftState(values) };
            drafts[window] = watch;
            var observer = window.Rect.gameObject.GetComponent<DraftObserver>() ?? window.Rect.gameObject.AddComponent<DraftObserver>();
            observer.Owner = this; observer.Window = window; observer.Watch = watch;
            if (!restore || !recovered.TryGetValue(native, out Dictionary<string, object> saved)) return;
            var applicable = watch.State.Applicable(saved);
            if (applicable.Count == 0) return;
            WriteDraft(window, native, applicable);
            watch.State.Observe(DraftValues(window, native));
            recovered.Remove(native);
            ConfirmRecovered(window, watch, applicable.Keys.ToArray());
        }

        private bool DraftChanged(CustomUIWindow window, DraftWatch watch)
        {
            watch.State.Observe(DraftValues(window, watch.Native));
            return watch.State.Changes().Count != 0;
        }

        private void PreserveDestroyedDraft(CustomUIWindow window, DraftWatch watch)
        {
            if (watch == null || window == null || !drafts.TryGetValue(window, out DraftWatch current) || !ReferenceEquals(current, watch)) return;
            drafts.Remove(window);
            var changed = watch.State.Changes();
            if (changed.Count == 0) return;
            if (!recovered.TryGetValue(watch.Native, out var saved)) recovered.Add(watch.Native, saved = new Dictionary<string, object>());
            foreach (var pair in changed) saved[pair.Key] = pair.Value;
        }

        private static void WriteDraft(CustomUIWindow window, bool native, Dictionary<string, object> values)
        {
            var manager = GameOptionUIManager.Instance;
            var keys = native && manager != null ? PendingKeys?.GetValue(manager) as Dictionary<string, KeyCode> : null;
            foreach (var pair in values)
            {
                CustomUIElement control = window.Get(pair.Key);
                if (control?.IsAlive != true) continue;
                if (keys != null && pair.Value is KeyCode key && keys.ContainsKey(pair.Key))
                { keys[pair.Key] = key; RefreshKey?.Invoke(manager, new object[] { pair.Key }); }
                else if (control.toggle != null && pair.Value is bool toggle) control.SetBool(toggle, true);
                else if (control.dropdown != null && pair.Value is int index) control.SetIndex(index, true);
                else if (control.slider != null && pair.Value is float number) control.SetFloat(number, true);
                else if (control.input != null && pair.Value is string text) control.SetString(text, true);
            }
            if (native && manager != null) RefreshRules?.Invoke(manager, null);
        }

        private void ConfirmRecovered(CustomUIWindow owner, DraftWatch watch, string[] keys)
        {
            if (discardPrompt?.IsAlive == true)
            {
                recoveryPrompts.Enqueue(new RecoveryPrompt { Owner = owner, Watch = watch, Keys = keys });
                discardPrompt.BringToFront();
                return;
            }
            string layer = StoreUIManager.Instance == null ? "menu" : CustomUIManager.LAYER_META;
            Vector2 size = UiLayout.Fit(480, 230);
            var builder = CustomUIManager.Window(ModEntry.Id + ".settings.discard", Text("settings.unsaved_title"), layer)
                .SetSize(size.x, size.y).SetDraggable(true).SetCloseOnEscape(true);
            discardPrompt = builder.GetWindow();
            CustomUIWindow prompt = discardPrompt;
            builder.OnClose(() =>
            {
                if (!ReferenceEquals(discardPrompt, prompt)) return;
                discardPrompt = null;
                ShowNextRecovery();
            });
            builder.AddLabel(Text("settings.recovered_message"));
            builder.BeginRow().AddButton(Text("settings.continue_editing"), () => prompt.Close(), "continue")
                .AddButton(Text("settings.discard_draft"), () =>
                {
                    prompt.Close();
                    if (owner?.IsAlive != true || !drafts.TryGetValue(owner, out DraftWatch current) || !ReferenceEquals(watch, current)) return;
                    WriteDraft(owner, watch.Native, keys.Where(watch.State.Baseline.ContainsKey).ToDictionary(key => key, key => watch.State.Baseline[key]));
                    watch.State.Observe(DraftValues(owner, watch.Native));
                }, "discard").End().Show();
        }

        private void ShowNextRecovery()
        {
            while (discardPrompt?.IsAlive != true && recoveryPrompts.Count != 0)
            {
                RecoveryPrompt next = recoveryPrompts.Dequeue();
                if (next.Owner?.IsAlive == true && drafts.TryGetValue(next.Owner, out DraftWatch watch) && ReferenceEquals(next.Watch, watch))
                    ConfirmRecovered(next.Owner, watch, next.Keys);
            }
        }

        private void ConfirmDiscard(CustomUIWindow owner)
        {
            if (discardPrompt?.IsAlive == true) { discardPrompt.BringToFront(); return; }
            string layer = StoreUIManager.Instance == null ? "menu" : CustomUIManager.LAYER_META;
            Vector2 size = UiLayout.Fit(480, 230);
            var builder = CustomUIManager.Window(ModEntry.Id + ".settings.discard", Text("settings.unsaved_title"), layer)
                .SetSize(size.x, size.y).SetDraggable(true).SetCloseOnEscape(true);
            discardPrompt = builder.GetWindow();
            CustomUIWindow prompt = discardPrompt;
            builder.OnClose(() =>
            {
                if (!ReferenceEquals(discardPrompt, prompt)) return;
                discardPrompt = null;
                ShowNextRecovery();
            });
            builder.AddLabel(Text("settings.unsaved_message"));
            builder.BeginRow().AddButton(Text("settings.continue_editing"), () => prompt.Close(), "continue")
                .AddButton(Text("settings.discard"), () =>
                {
                    prompt.Close();
                    if (owner?.IsAlive != true) return;
                    discardConfirmed = true;
                    try { owner.Close(); }
                    finally { discardConfirmed = false; }
                    if (owner.id == "game_options") CloseFinder();
                }, "discard").End().Show();
        }

        private void ClearDrafts()
        {
            drafts.Clear();
            recovered.Clear();
            recoveryPrompts.Clear();
            CustomUIWindow prompt = discardPrompt;
            discardPrompt = null;
            prompt?.Close();
        }

        [HarmonyPatch(typeof(GameOptionUIManager), nameof(GameOptionUIManager.OpenUI))]
        private static class TrackNativeDraft
        {
            private static bool Prefix(ref bool __state)
            {
                var existing = attached == null ? null : CustomUIManager.Instance?.GetWindow("game_options");
                if (existing?.IsAlive == true) { existing.Show(); existing.BringToFront(); return false; }
                __state = true;
                return true;
            }
            private static void Postfix(bool __state)
            { if (__state) attached?.WatchDraft(CustomUIManager.Instance?.GetWindow("game_options"), true, true); }
        }

        [HarmonyPatch(typeof(GameOptionUIManager), "OnApplySetting")]
        private static class NativeDraftApplied
        {
            private static void Postfix() => attached?.WatchDraft(CustomUIManager.Instance?.GetWindow("game_options"), true);
        }

        [HarmonyPatch(typeof(ModConfigWindow), "Apply")]
        private static class ConfigDraftApplied
        {
            private static void Postfix(CustomUIWindow window, ModConfig config)
            {
                if (attached != null && config?.ModId == ModEntry.Id) attached.WatchDraft(window, false);
            }
        }

        [HarmonyPatch(typeof(CustomUIWindow), nameof(CustomUIWindow.Close))]
        private static class GuardDraftClose
        {
            private static bool Prefix(CustomUIWindow __instance, ref bool __state)
            {
                var owner = attached;
                if (owner == null || !owner.drafts.TryGetValue(__instance, out DraftWatch watch)) return true;
                if (!owner.discardConfirmed && owner.DraftChanged(__instance, watch))
                {
                    owner.ConfirmDiscard(__instance);
                    return false;
                }
                __state = true;
                return true;
            }
            private static void Postfix(CustomUIWindow __instance, bool __state)
            {
                if (__state) attached?.drafts.Remove(__instance);
            }
        }
    }
}
