using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed partial class ShopSettings
    {
        private static readonly string[] Sections = { "shopassist", "shopassist.batch", "shopassist.advanced", "shopassist.calendar", "shopassist.playback" };
        private static readonly string[] GearSections = {
            "物品与仓储 / Items and storage", "交易与委托 / Trade and requests",
            "机器与生产 / Machines and production", "日历与工作台 / Calendar and workspace", "快速播放 / Fast playback" };
        private static ShopSettings attached;
        private readonly ModConfig config = ModHelper.GetConfig(ModEntry.Id);
        private readonly List<Action> optionRegistrations = new List<Action>();
        private readonly List<Command> commands = new List<Command>();
        private int shortcutNoticeFrame = -1;

        private sealed class Command
        {
            internal string Label;
            internal Action Run;
            internal Func<ActionAvailability> Available;
            internal CustomUIElement Button;
        }

        internal event Action Changed;

        internal static string Text(string key) => ModHelper.GetLocalized(ModEntry.Id, key);
        internal static string Format(string key, params object[] args) => ModHelper.GetLocalized(ModEntry.Id, key, args);

        private static int Level(string key) => (int)FeatureCatalog.For(key).Category;

        internal ConfigEntry<bool> Toggle(string key, string tipKey, bool initial = false)
        {
            Func<string> label = () => Text(key);
            Func<string> tip = () => Text(tipKey);
            int tier = Level(key);
            var entry = config.Define(key, initial, GearSections[tier], label, tip);
            optionRegistrations.Add(() => ModHelper.AddGameOptionToggle(ModEntry.Id, Sections[tier], key,
                label, () => entry.Value, value =>
                {
                    if (entry.Value == value) return;
                    entry.Set(value);
                    Changed?.Invoke();
                }, tip));
            return entry;
        }

        internal ConfigEntry<int> Integer(string key, string tipKey, int initial, int min, int max)
        {
            Func<string> label = () => Text(key);
            Func<string> tip = () => Text(tipKey);
            int tier = Level(key);
            var entry = config.Define(key, initial, GearSections[tier], label, min, max, tip);
            optionRegistrations.Add(() => ModHelper.AddGameOptionSlider(ModEntry.Id, Sections[tier], key, label,
                () => entry.Value, v =>
                {
                    int value = Mathf.Clamp(Mathf.RoundToInt(v), min, max);
                    if (value == entry.Value) return;
                    entry.Set(value);
                    Changed?.Invoke();
                }, min, max, true, tip));
            return entry;
        }

        internal Func<KeyCode> Key(string key, string tipKey, bool raw = false)
        {
            shortcutKeys.Add(key);
            shortcutDescriptions.Add(key, tipKey);
            string preference = ModEntry.Id + "." + key;
            var value = (KeyCode)PlayerPrefs.GetInt(preference, (int)KeyCode.None);
            if (!Enum.IsDefined(typeof(KeyCode), value)) value = KeyCode.None;
            optionRegistrations.Add(() => ModHelper.AddGameOptionKeyBind(ModEntry.Id, ModHelper.OPTION_SECTION_KEYBIND,
                key, () => Text(key), () => value, next =>
                {
                    if (value == next) return;
                    value = next;
                    PlayerPrefs.SetInt(preference, (int)next);
                    PlayerPrefs.Save();
                }, () => Text(tipKey)));
            return () =>
            {
                // Held shortcuts validate conflicts when activating, rather than only on GetKeyDown.
                if (raw) return value;
                if (value == KeyCode.None || !Input.GetKeyDown(value)) return value;
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                    Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ||
                    Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return KeyCode.None;
                string owner = ShortcutOwner(key, value);
                if (owner == null) return value;
                if (shortcutNoticeFrame != Time.frameCount && ShopInput.Ready())
                {
                    shortcutNoticeFrame = Time.frameCount;
                    ShopNotice.Show(Format("settings.key_conflict", KeyBindHelper.KeyName(value), owner));
                }
                return KeyCode.None;
            };
        }

        internal static string ShortcutOwner(string key, KeyCode value)
        {
            if (value == KeyCode.None) return null;
            foreach (var binding in KeyBindHelper.Bindings)
                if (KeyBindHelper.Get(binding.id) == value) return binding.label?.Invoke() ?? binding.id;
            string ownId = ModEntry.Id + ":" + key;
            foreach (var section in GameOptionRegistry.Sections)
                foreach (var entry in section.entries)
                    if (entry.kind == GameOptionRegistry.Kind.KeyBind && entry.id != ownId && entry.getKey?.Invoke() == value)
                        return entry.label?.Invoke() ?? entry.id;
            // These two workshop panels read their own bindings outside the native key registry.
            if (ShopPeers.Running("ShowItemNames"))
            {
                var namesKey = (KeyCode)PlayerPrefs.GetInt("snd.hotkey", (int)KeyCode.F10);
                if (value == (namesKey == KeyCode.None ? KeyCode.F10 : namesKey)) return "Show Item Names";
            }
            const string arrange = "smart_inventory_arrange";
            if (ShopPeers.Running(arrange))
            {
                var prefs = ModConfig.Find(arrange);
                string name = value.ToString();
                bool Matches(string setting, string fallback) => string.Equals(
                    (prefs?.Get(setting) as ConfigEntry<string>)?.Value ?? fallback, name, StringComparison.OrdinalIgnoreCase);
                if (Matches("OpenSettingsHotkey", "F6") || Matches("UndoArrangeHotkey", "None") ||
                    (prefs?.Get("ArrangeHotkeyEnabled") as ConfigEntry<bool>)?.Value == true && Matches("ArrangeOpenContainersHotkey", "None"))
                    return "Smart Inventory Arrange";
            }
            return null;
        }

        internal ConfigEntry<int> Choice(string key, string tipKey, string[] labelKeys, int initial = 0)
        {
            Func<string> label = () => Text(key);
            Func<string> tip = () => Text(tipKey);
            choiceLabels.Add(key, labelKeys);
            int tier = Level(key);
            var entry = config.Define(key, initial, GearSections[tier], label, 0, labelKeys.Length - 1, tip);
            optionRegistrations.Add(() => ModHelper.AddGameOptionDropdown(ModEntry.Id, Sections[tier], key, label,
                () => Array.ConvertAll(labelKeys, Text), () => entry.Value, index =>
                {
                    if (index < 0 || index >= labelKeys.Length || entry.Value == index) return;
                    entry.Set(index);
                    Changed?.Invoke();
                }, tip));
            return entry;
        }

        internal void Action(string labelKey, Action callback, Func<bool> available = null)
        {
            Action(labelKey, callback, () => available == null || available()
                ? ActionAvailability.Ready
                : LegacyUnavailable(labelKey));
        }

        internal void Action(string labelKey, Action callback, Func<ActionAvailability> available)
        {
            if (callback == null || available == null || commands.Exists(command => command.Label == labelKey))
                throw new ArgumentException("Invalid or duplicate command: " + labelKey);
            FeatureCatalog.For(labelKey);
            commands.Add(new Command { Label = labelKey, Run = callback, Available = available });
        }

        internal ActionAvailability FeatureAvailability(string key)
        {
            foreach (string setting in FeatureCatalog.RequiredSettings(key))
                if ((config.Get(setting) as ConfigEntry<bool>)?.Value != true)
                    return ActionAvailability.Blocked(ActionUnavailableReason.Disabled, "availability.disabled", setting);
            return ActionAvailability.Ready;
        }

        internal static ActionAvailability ContextFeatureAvailability(string key) => attached == null
            ? ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "availability.unknown")
            : attached.FeatureAvailability(key);

        private static ActionAvailability LegacyUnavailable(string key)
        {
            if (key == "f05.open" && ShopPeers.Running("smart_inventory_arrange"))
                return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.conflict");
            if (key == "f08.open" && ShopSelection.Current().Count == 0)
                return ActionAvailability.Blocked(ActionUnavailableReason.MissingSelection, "availability.selection");
            string context = key == "f10.open" ? "availability.machine" : key == "f13.groups" ? "availability.coordinator" :
                key == "f19.choose" ? "availability.liquid" : key == "f23.open" ? "availability.appraisal" :
                key == "f34.open" ? "availability.demand" : key == "f36.open" ? "availability.ground" : null;
            if (context != null) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, context);
            // The remaining legacy predicates combine several checks. Do not invent a cause.
            return ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "availability.unknown");
        }

        internal ActionAvailability Evaluate(string label)
        {
            var command = commands.Find(candidate => candidate.Label == label);
            if (command == null) return ActionAvailability.Blocked(ActionUnavailableReason.Unsupported, "availability.unsupported");
            try
            {
                var feature = FeatureAvailability(label);
                if (!feature.Allowed) return feature;
                if (!PlayerStore.HasInstance || StoreUIManager.Instance == null)
                    return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.store");
                return command.Available();
            }
            catch (Exception error)
            {
                Debug.LogError("[ShopAssist] Command availability " + label + " failed: " + error);
                return ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "availability.unknown");
            }
        }

        private bool CanUse(Command command) => Evaluate(command.Label).Allowed;

        internal static void UpdateActionButton(CustomUIElement button, ActionAvailability availability, string description = null)
        {
            if (button?.IsAlive != true) return;
            if (button.button != null && button.button.interactable != availability.Allowed)
                button.SetInteractable(availability.Allowed);
            if (button.text != null)
                button.text.color = availability.Allowed ? new Color(.94f, .90f, .80f, 1f) : new Color(.67f, .65f, .62f, 1f);
            button.SetTooltip(availability.Allowed ? description ?? "" : availability.Message);
        }

        internal void RefreshCommands()
        {
            if (toolsWindow?.IsAlive != true) return;
            int shown = 0;
            foreach (var command in commands)
                if (command.Button?.IsAlive == true)
                {
                    bool visible = showDisabledTools || FeatureAvailability(command.Label).Allowed;
                    if (command.Button.root.activeSelf != visible) command.Button.SetVisible(visible);
                    if (!visible) continue;
                    shown++;
                    UpdateActionButton(command.Button, Evaluate(command.Label), Description(command.Label));
                }
            toolsEmpty?.SetVisible(shown == 0);
            if (toolsAttention != null && Time.unscaledTime >= toolsAttentionAt)
            {
                toolsAttentionAt = Time.unscaledTime + 1f;
                string label = Format("visual.attention_count", AttentionBook.Count);
                if (toolsAttention.text?.text != label)
                {
                    toolsAttention.SetText(label);
                    SizeToolHeader(toolsAttention, 180);
                }
            }
        }

        private void ClearCommandViews()
        {
            foreach (var command in commands) command.Button = null;
            toolsEmpty = null;
        }

        [HarmonyPatch(typeof(GameOptionUIManager), "BuildSection")]
        private static class AddActionButtons
        {
            private static void Prefix(CustomUIBuilder builder, GameOptionRegistry.Section section)
            {
                if (attached != null && section.id == ModEntry.Id + ":" + Sections[0])
                    builder.AddButton(Text("settings.find"), attached.OpenFinder, ModEntry.Id + ":settings.find");
            }
            private static void Postfix(CustomUIBuilder builder, GameOptionRegistry.Section section)
            {
                if (attached != null && section.id == ModEntry.Id + ":" + Sections[0] && StoreUIManager.Instance != null)
                    builder.AddButton(Text("visual.tools"), () => attached.OpenTools(), ModEntry.Id + ":tools");
            }
        }

        internal bool CanRun(string label) => commands.Exists(c => c.Label == label && CanUse(c));
        internal void Run(string label)
        {
            var command = commands.Find(c => c.Label == label);
            var availability = Evaluate(label);
            if (!availability.Allowed) { ShopNotice.Show(availability.Message); return; }
            toolsWindow?.Close(); toolsWindow = null;
            command.Run();
        }
        private CustomUIWindow toolsWindow;
        private RectTransform toolsContent;
        private readonly CustomUIElement[] toolCategories = new CustomUIElement[Sections.Length];
        private readonly float[] toolScroll = Enumerable.Repeat(1f, Sections.Length).ToArray();
        private static readonly string[] ToolSettings = { "f05.enabled", "f01.enabled", "f10.enabled", "f13.enabled", "f42.enabled" };
        private CustomUIElement toolsAttention, toolsEmpty, toolsFilter;
        private CustomUIElement toolsManage;
        private float toolsAttentionAt;
        private bool showDisabledTools;
        private int toolsTier;
        internal static CustomUIWindow OpenToolsWindow => attached?.toolsWindow;
        internal bool ShowDisabledTools => showDisabledTools;
        internal void ShowDisabledToolsChanged(bool value) { showDisabledTools = value; RefreshCommands(); }
        internal void OpenToolSettings(int tier) => OpenSetting(ToolSettings[Mathf.Clamp(tier, 0, ToolSettings.Length - 1)]);

        private const float ToolSidebarWidth = 145f;
        private const float ToolRowHorizontalPadding = 20f;

        private static void SizeToolHeader(CustomUIElement row, float minimumWidth)
        {
            if (row?.text == null) return;
            row.text.enableAutoSizing = false;
            row.text.fontSize = 21;
            row.SetFlexible(0, 0).SetPreferredSize(minimumWidth, 44);
            if (!row.text.isActiveAndEnabled) return;
            float width = Mathf.Ceil(row.text.GetPreferredValues(row.text.text).x) + ToolRowHorizontalPadding + 6;
            row.SetPreferredSize(Mathf.Max(minimumWidth, width), 44);
        }

        private static void SizeToolCategory(CustomUIElement row)
        {
            if (row?.text == null) return;
            row.text.enableAutoSizing = false;
            row.text.fontSize = 19;
            row.text.enableWordWrapping = true;
            if (!row.text.isActiveAndEnabled) return;
            var style = row.text.fontStyle;
            row.text.fontStyle = style | TMPro.FontStyles.Bold;
            float height = Mathf.Max(52, Mathf.Ceil(row.text.GetPreferredValues(row.text.text,
                ToolSidebarWidth - ToolRowHorizontalPadding, 0).y) + 10);
            row.text.fontStyle = style;
            row.SetPreferredSize(-1, height);
            var layout = row.root.GetComponent<LayoutElement>();
            if (layout != null) layout.minHeight = height;
        }

        private void SizeVisibleToolText()
        {
            if (toolsWindow?.root?.activeInHierarchy != true) return;
            Canvas.ForceUpdateCanvases();
            SizeToolHeader(toolsAttention, 180);
            SizeToolHeader(toolsManage, 150);
            foreach (CustomUIElement category in toolCategories) SizeToolCategory(category);
            Canvas.ForceUpdateCanvases();
        }

        private sealed class ToolTextAfterShow : MonoBehaviour
        {
            internal ShopSettings Owner;
            private int firstFrame = -1;
            private void LateUpdate()
            {
                if (Owner?.toolsWindow?.root != gameObject) { Destroy(this); return; }
                if (firstFrame < 0) { firstFrame = Time.frameCount; return; }
                if (Time.frameCount <= firstFrame) return;
                Owner.SizeVisibleToolText();
                Destroy(this);
            }
        }

        internal static void StyleToolRow(CustomUIElement row, float height = 44f)
        {
            if (row == null) return;
            row.SetFlexible(1, 0).SetPreferredSize(-1, height);
            var group = row.root?.GetComponent<HorizontalLayoutGroup>();
            if (group != null)
            {
                group.childAlignment = TextAnchor.MiddleLeft;
                group.childControlWidth = true; group.childForceExpandWidth = true;
                group.padding = new RectOffset(12, 8, 0, 0);
            }
            if (row.text != null)
            {
                row.text.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
                row.text.margin = Vector4.zero;
                row.text.enableWordWrapping = false;
                row.text.enableAutoSizing = true;
                row.text.fontSizeMin = 19;
                row.text.fontSizeMax = 21;
                row.text.color = new Color(.94f, .90f, .80f, 1f);
                var labelLayout = row.text.gameObject.GetComponent<LayoutElement>() ?? row.text.gameObject.AddComponent<LayoutElement>();
                labelLayout.minWidth = 0; labelLayout.preferredWidth = 0; labelLayout.flexibleWidth = 1;
            }
            if (row.button != null)
            {
                row.button.transition = Selectable.Transition.ColorTint;
                ColorBlock colors = row.button.colors;
                colors.normalColor = new Color(.20f, .18f, .25f, 1f);
                colors.highlightedColor = new Color(.29f, .35f, .37f, 1f);
                colors.pressedColor = new Color(.22f, .40f, .41f, 1f);
                colors.selectedColor = colors.highlightedColor;
                colors.disabledColor = new Color(.15f, .14f, .19f, 1f);
                colors.colorMultiplier = 1f;
                row.button.colors = colors;
            }
        }
        internal void OpenTools(int tier = 0)
        {
            if (CustomUIManager.Instance == null) return;
            OverlayHandler.current?.HideTooltip();
            tier = Mathf.Clamp(tier, 0, Sections.Length - 1);
            if (toolsWindow?.IsAlive == true && toolsContent != null)
            {
                var scroll = toolsContent.GetComponentInParent<ScrollRect>();
                if (scroll != null) toolScroll[toolsTier] = scroll.verticalNormalizedPosition;
            }
            else
            {
                Vector2 size = UiLayout.Fit(650, 500);
                var shell = CustomUIManager.Window(ModEntry.Id + ".tools", Text("visual.tools"), CustomUIManager.LAYER_META)
                    .SetSize(size.x, size.y).SetDraggable(true).SetCloseOnEscape(true);
                toolsWindow = shell.GetWindow(); CustomUIWindow own = toolsWindow;
                if (own.frame?.titleText != null) { own.frame.titleText.fontSize = 19; own.frame.titleText.enableAutoSizing = false; }
                shell.OnClose(() => { if (toolsWindow == own) { toolsWindow = null; toolsContent = null; toolsAttention = toolsManage = toolsFilter = null; ClearCommandViews(); } });
                shell.BeginRow(8);
                shell.AddButton(Format("visual.attention_count", AttentionBook.Count), () => AttentionBook.Open(), "attention");
                toolsAttention = shell.Last;
                StyleToolRow(shell.Last); SizeToolHeader(shell.Last, 180);
                shell.AddFlexibleSpace();
                shell.AddButton(Text("visual.manage"), () => OpenToolSettings(toolsTier), "manage");
                toolsManage = shell.Last;
                StyleToolRow(shell.Last); SizeToolHeader(shell.Last, 150);
                shell.End().BeginRow(10);
                var bodyLayout = shell.CurrentContainer.gameObject.AddComponent<LayoutElement>();
                bodyLayout.minHeight = 100; bodyLayout.flexibleHeight = 1; bodyLayout.flexibleWidth = 1;
                shell.BeginColumn(4);
                var sidebar = shell.CurrentContainer.gameObject.AddComponent<LayoutElement>();
                sidebar.minWidth = sidebar.preferredWidth = ToolSidebarWidth; sidebar.flexibleWidth = 0; sidebar.flexibleHeight = 1;
                for (int n = 0; n < Sections.Length; n++)
                {
                    int category = n;
                    shell.AddButton(Text(FeatureCatalog.CategoryKey((FeatureCategory)n)), () => OpenTools(category), "category_" + n);
                    toolCategories[n] = shell.Last;
                    StyleToolRow(shell.Last, 52);
                    SizeToolCategory(shell.Last);
                }
                shell.End().BeginColumn(4);
                var main = shell.CurrentContainer.gameObject.AddComponent<LayoutElement>();
                main.minWidth = 100; main.preferredWidth = 0; main.flexibleWidth = 1; main.flexibleHeight = 1;
                shell.AddToggle(Text("visual.show_disabled"), showDisabledTools, ShowDisabledToolsChanged, "show_disabled");
                toolsFilter = shell.Last;
                shell.Last.SetPreferredSize(-1, 38);
                if (shell.Last.text != null) { shell.Last.text.fontSize = 19; shell.Last.text.enableAutoSizing = false; }
                shell.BeginScrollFlexible(150); toolsContent = shell.CurrentContainer;
                shell.End().End().End().Show();
                SizeVisibleToolText();
                own.root.AddComponent<ToolTextAfterShow>().Owner = this;
            }
            toolsTier = tier;
            toolsFilter?.SetVisible((FeatureCategory)tier != FeatureCategory.Playback);
            for (int n = 0; n < toolCategories.Length; n++)
            {
                CustomUIElement category = toolCategories[n];
                if (category?.button == null) continue;
                ColorBlock colors = category.button.colors;
                colors.disabledColor = new Color(.23f, .40f, .41f, 1f);
                category.button.colors = colors;
                category.SetInteractable(n != tier);
                if (category.text != null) category.text.fontStyle = n == tier ? TMPro.FontStyles.Bold : TMPro.FontStyles.Normal;
            }
            ClearCommandViews();
            UiLayout.Clear(toolsContent, toolsWindow);
            var builder = new CustomUIBuilder(CustomUIManager.Instance, toolsWindow, toolsContent);
            foreach (var command in commands.OrderBy(value => OptionOrder(value.Label)))
            {
                if (Level(command.Label) != tier) continue;
                string label = command.Label;
                builder.AddButton(Text(label), () => Run(label), ModEntry.Id + ":" + label);
                command.Button = builder.Last;
                StyleToolRow(builder.Last);
            }
            builder.AddLabel(Text((FeatureCategory)tier == FeatureCategory.Playback ? "visual.no_quick_actions" : "visual.no_actions"), "empty"); toolsEmpty = builder.Last;
            RefreshCommands();
            CustomUIBuilder.RebuildLayout(toolsContent);
            UiLayout.RestoreScroll(toolsContent, toolScroll[tier]);
        }

        internal void Attach()
        {
            attached = this;
            for (int tier = 0; tier < Sections.Length; tier++)
            {
                int level = tier;
                ModHelper.AddGameOptionSection(ModEntry.Id, Sections[tier], () => SectionLabel(level));
            }
            foreach (var register in optionRegistrations) register();
            foreach (var section in GameOptionRegistry.Sections.Where(section => section.source == ModEntry.Id))
            {
                var ordered = section.entries.OrderBy(entry => OptionOrder(entry.id.Substring(ModEntry.Id.Length + 1))).ToArray();
                section.entries.Clear();
                section.entries.AddRange(ordered);
            }
            Changed += RefreshCommands;
            ModHook.OnModConfigApplied += OnApplied;
        }

        internal void Detach()
        {
            CloseFinder();
            toolsWindow?.Close(); toolsWindow = null;
            ModHook.OnModConfigApplied -= OnApplied;
            Changed -= RefreshCommands;
            if (attached == this) attached = null;
            for (int tier = 0; tier < Sections.Length; tier++) TMPIconHover.CustomLinks.Remove(ModEntry.Id + ":level:" + tier);
            ClearCommandViews();
            ModHelper.RemoveGameOptions(ModEntry.Id);
        }

        private void OnApplied(string id)
        {
            if (id == ModEntry.Id) Changed?.Invoke();
        }
    }
}
