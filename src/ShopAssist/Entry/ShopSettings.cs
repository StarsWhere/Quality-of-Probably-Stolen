using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    internal sealed partial class ShopSettings
    {
        private static readonly string[] Sections = { "shopassist", "shopassist.batch", "shopassist.advanced", "shopassist.calendar" };
        private static readonly string[] GearSections = {
            "物品与仓储 / Items and storage", "交易与委托 / Trade and requests",
            "机器与生产 / Machines and production", "日历与工作台 / Calendar and workspace" };
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
            internal CustomUIElement Button, Reason, SettingButton;
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

        internal Func<KeyCode> Key(string key, string tipKey)
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

        internal static void UpdateAvailabilityControls(CustomUIElement button, CustomUIElement reason,
            CustomUIElement setting, ActionAvailability availability)
        {
            if (button?.IsAlive != true) return;
            if (button.button != null && button.button.interactable != availability.Allowed)
                button.SetInteractable(availability.Allowed);
            if (reason?.root != null)
            {
                string message = availability.Message;
                if (reason.GetText() != message) reason.SetText(message);
                if (reason.root.activeSelf == availability.Allowed) reason.root.SetActive(!availability.Allowed);
            }
            if (setting?.root != null)
            {
                bool show = !availability.Allowed && availability.SettingKey != null;
                if (setting.root.activeSelf != show) setting.root.SetActive(show);
            }
        }

        internal void RefreshCommands()
        {
            if (toolsWindow?.IsAlive != true) return;
            foreach (var command in commands)
                if (command.Button?.IsAlive == true)
                    UpdateAvailabilityControls(command.Button, command.Reason, command.SettingButton, Evaluate(command.Label));
        }

        private void ClearCommandViews()
        {
            foreach (var command in commands) command.Button = command.Reason = command.SettingButton = null;
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
        internal void OpenTools(int tier = 0)
        {
            if (CustomUIManager.Instance == null) return;
            toolsWindow?.Close();
            ClearCommandViews();
            var builder = CustomUIManager.Window(ModEntry.Id + ".tools", Text("visual.tools"), CustomUIManager.LAYER_META)
                .SetSize(650, 500).SetDraggable(true).SetCloseOnEscape(true);
            toolsWindow = builder.GetWindow();
            builder.BeginRow();
            for (int n = 0; n < Sections.Length; n++)
            {
                int category = n;
                builder.AddButton(Text(FeatureCatalog.CategoryKey((FeatureCategory)n)), () => OpenTools(category), "category_" + n);
                builder.Last.SetPreferredSize(145, 40);
                if (builder.Last.text != null)
                {
                    builder.Last.text.enableWordWrapping = true;
                    builder.Last.text.enableAutoSizing = true;
                    builder.Last.text.fontSizeMin = 10;
                    builder.Last.text.fontSizeMax = 16;
                }
            }
            builder.End().AddButton(Text("visual.attention"), () => { toolsWindow?.Close(); toolsWindow = null; AttentionBook.Open(); }, "attention");
            builder.BeginScrollFlexible(180).BeginGrid(3, 195, 135, 7);
            foreach (var command in commands.OrderBy(value => OptionOrder(value.Label)))
            {
                if (Level(command.Label) != tier) continue;
                string label = command.Label;
                builder.BeginPanel(195, 135, padding: 4);
                builder.AddImage(tier == 2 ? MachineryStyle.IconPower : MachineryStyle.IconNote, 25, 25);
                builder.AddButton(Text(label), () => Run(label), ModEntry.Id + ":" + label);
                command.Button = builder.Last;
                builder.Last.SetPreferredSize(185, 40);
                if (builder.Last.text != null)
                {
                    builder.Last.text.enableWordWrapping = true;
                    builder.Last.text.enableAutoSizing = true;
                    builder.Last.text.fontSizeMin = 10;
                    builder.Last.text.fontSizeMax = 18;
                }
                builder.AddLabel("", "reason_" + label);
                command.Reason = builder.Last;
                if (builder.Last.text != null)
                {
                    builder.Last.text.enableWordWrapping = true;
                    builder.Last.text.enableAutoSizing = true;
                    builder.Last.text.fontSizeMin = 9;
                    builder.Last.text.fontSizeMax = 15;
                }
                builder.AddButton(Text("settings.open"), () =>
                {
                    var current = Evaluate(label);
                    if (!current.Allowed && current.SettingKey != null) OpenSetting(current.SettingKey);
                    else RefreshCommands();
                }, "setting_" + label);
                command.SettingButton = builder.Last;
                builder.End();
            }
            builder.End().End().Show();
            RefreshCommands();
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
