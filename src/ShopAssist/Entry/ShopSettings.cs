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
        private static readonly string[] Sections = { "shopassist", "shopassist.batch", "shopassist.advanced" };
        private static readonly string[] GearSections = { "一级 / Essentials", "二级 / Rules", "三级 / Planning" };
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
        }

        internal event Action Changed;

        internal static string Text(string key) => ModHelper.GetLocalized(ModEntry.Id, key);
        internal static string Format(string key, params object[] args) => ModHelper.GetLocalized(ModEntry.Id, key, args);

        private static int Level(string key) => (int)FeatureCatalog.TierFor(key) - 1;

        internal ConfigEntry<bool> Toggle(string key, string tipKey, bool initial = false)
        {
            Func<string> label = () => Text(key);
            Func<string> tip = () => Text(tipKey);
            int tier = Level(key);
            var entry = config.Define(key, FeatureCatalog.ToggleDefault(key, initial), GearSections[tier], label, tip);
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
                : UnavailableReason(labelKey));
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

        private static ActionAvailability UnavailableReason(string key)
        {
            if (key == "f08.open" && ShopSelection.Current().Count == 0)
                return ActionAvailability.Blocked(ActionUnavailableReason.MissingSelection, "availability.selection");
            string context = key == "f10.open" ? "availability.machine" : key == "f13.groups" ? "availability.coordinator" :
                key == "f19.choose" ? "availability.liquid" : key == "f23.open" ? "availability.appraisal" :
                key == "f34.open" ? "availability.demand" : key == "f36.open" ? "availability.ground" : null;
            if (context != null) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, context);
            // The remaining predicates combine several checks. Do not invent a cause.
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

        [HarmonyPatch(typeof(GameOptionUIManager), "BuildSection")]
        private static class AddActionButtons
        {
            private static void Prefix(CustomUIBuilder builder, GameOptionRegistry.Section section)
            {
                if (attached != null && section.id == ModEntry.Id + ":" + Sections[0])
                    builder.AddButton(Text("settings.find"), attached.OpenFinder, ModEntry.Id + ":settings.find");
            }
        }

        internal bool CanRun(string label) => commands.Exists(c => c.Label == label && CanUse(c));
        internal void Run(string label)
        {
            var command = commands.Find(c => c.Label == label);
            var availability = Evaluate(label);
            if (!availability.Allowed) { ShopNotice.Show(availability.Message); return; }
            command.Run();
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
            ModHook.OnModConfigApplied += OnApplied;
        }

        internal void Detach()
        {
            ClearDrafts();
            CloseFinder();
            ModHook.OnModConfigApplied -= OnApplied;
            if (attached == this) attached = null;
            for (int tier = 0; tier < Sections.Length; tier++) TMPIconHover.CustomLinks.Remove(ModEntry.Id + ":level:" + tier);
            ModHelper.RemoveGameOptions(ModEntry.Id);
        }

        private void OnApplied(string id)
        {
            if (id == ModEntry.Id) Changed?.Invoke();
        }
    }
}
