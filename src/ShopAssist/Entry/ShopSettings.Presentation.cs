using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed partial class ShopSettings
    {
        private readonly Dictionary<string, string[]> choiceLabels = new Dictionary<string, string[]>();
        private readonly Dictionary<string, string> shortcutDescriptions = new Dictionary<string, string>();

        // Keep saved keys and values stable; grouping changes only their presentation.
        private static bool PrimaryOption(string key) => key.EndsWith(".enabled", StringComparison.Ordinal) ||
            key == "f04.unseen" || key == "f01.wholesale";

        private static int OptionOrder(string key) => FeatureCatalog.Order(key);

        private static string SectionLabel(int tier)
        {
            return Text(FeatureCatalog.CategoryKey((FeatureCategory)tier));
        }

        private string Description(string key)
        {
            if (shortcutDescriptions.TryGetValue(key, out string tip)) return Text(tip);
            return config.Get(key)?.Tooltip?.Invoke() ?? config.Get(key.Substring(0, 3) + ".enabled")?.Tooltip?.Invoke() ?? "";
        }

        private static void StyleOption(CustomUIWindow window, string id, string key)
        {
            var label = window?.Get("label_" + id);
            var control = window?.Get(id);
            if (label?.text == null || control?.root == null) return;
            var row = label.root.transform.parent.GetComponent<HorizontalLayoutGroup>();
            if (row == null) return;
            row.gameObject.name = "ShopAssist_Setting_" + key;
            bool primary = PrimaryOption(key);
            var feature = FeatureCatalog.For(key);
            bool groupHead = primary && key == feature.SettingKey && feature.Group == feature.Id;
            bool grouped = feature.Group != 0 && feature.Group != feature.Id;
            row.padding = new RectOffset(primary ? (grouped ? 12 : 0) : (grouped ? 30 : 18), 0,
                primary ? (grouped ? 3 : 9) : 1, primary ? 3 : 1);
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            label.text.fontStyle = primary ? FontStyles.Bold : FontStyles.Normal;
            if (groupHead) label.text.text = "<size=80%>" + Text(FeatureCatalog.GroupKey(feature)) + "</size>\n" + label.text.text;
            label.text.enableWordWrapping = true;
            label.text.overflowMode = TextOverflowModes.Overflow;
            var layout = label.root.GetComponent<LayoutElement>() ?? label.root.AddComponent<LayoutElement>();
            layout.minWidth = 0; layout.preferredWidth = -1; layout.flexibleWidth = 1;
            // The label receives the spare width so long translations wrap beside the control.
            foreach (Transform child in row.transform)
            {
                if (child == label.root.transform || child == control.root.transform) continue;
                var spacer = child.GetComponent<LayoutElement>();
                if (spacer != null && spacer.flexibleWidth > 0)
                { spacer.flexibleWidth = 0; spacer.minWidth = spacer.preferredWidth = 4; }
            }
        }

        [HarmonyPatch(typeof(GameOptionUIManager), "BuildEntry")]
        private static class OptionRows
        {
            private static void Postfix(CustomUIBuilder builder, GameOptionRegistry.Entry entry)
            {
                if (attached == null || entry.source != ModEntry.Id) return;
                StyleOption(builder.GetWindow(), entry.id, entry.id.Substring(ModEntry.Id.Length + 1));
            }
        }

        private static bool OwnChoice(ConfigEntry entry, out string[] labels)
        {
            labels = null;
            return attached != null && entry != null && ReferenceEquals(attached.config.Get(entry.Key), entry) &&
                attached.choiceLabels.TryGetValue(entry.Key, out labels);
        }

        [HarmonyPatch(typeof(ModConfigWindow), "BuildEntry")]
        private static class ConfigRows
        {
            private static bool Prefix(CustomUIBuilder b, ModConfig config, ConfigEntry entry)
            {
                if (config.ModId != ModEntry.Id || !OwnChoice(entry, out string[] labels)) return true;
                string link = "modcfg:" + ModEntry.Id + ":" + entry.Key;
                TMPIconHover.CustomLinks[link] = entry.Tooltip;
                b.BeginRow().AddLabel(entry.Label() + " <link=\"" + link + "\"><sprite name=\"info\" tint=1></link>", "label_" + entry.Key)
                    .AddFlexibleSpace().AddDropdown(Array.ConvertAll(labels, Text), (int)entry.BoxedValue, null, entry.Key);
                b.Last.SetFixedWidth(245);
                b.End();
                return false;
            }
            private static void Postfix(CustomUIBuilder b, ModConfig config, ConfigEntry entry)
            {
                if (attached == null || config.ModId != ModEntry.Id) return;
                var window = b.GetWindow();
                var control = window.Get(entry.Key);
                if (entry.ValueType == typeof(int) && control?.slider != null)
                {
                    var row = control.root.transform.parent;
                    b.AddLabel(Mathf.RoundToInt(control.GetFloat()).ToString(), "value_" + entry.Key);
                    var number = b.Last;
                    number.root.name = "ShopAssist_Value_" + entry.Key;
                    number.root.transform.SetParent(row, false);
                    number.root.transform.SetSiblingIndex(control.root.transform.GetSiblingIndex());
                    number.SetFixedWidth(40);
                    number.text.alignment = TextAlignmentOptions.MidlineRight;
                    control.slider.onValueChanged.AddListener(value => number.SetText(Mathf.RoundToInt(value).ToString()));
                }
                StyleOption(window, entry.Key, entry.Key);
            }
        }

        [HarmonyPatch(typeof(ModConfigWindow), "ReadWidget")]
        private static class ReadChoice
        {
            private static bool Prefix(ConfigEntry entry, CustomUIElement element, ref object __result)
            {
                if (!OwnChoice(entry, out string[] labels) || element?.dropdown == null) return true;
                int index = element.GetIndex();
                __result = index >= 0 && index < labels.Length ? (object)index : null;
                return false;
            }
        }

        [HarmonyPatch(typeof(ModConfigWindow), "WriteWidget")]
        private static class WriteChoice
        {
            private static bool Prefix(ConfigEntry entry, CustomUIElement element, object value)
            {
                if (!OwnChoice(entry, out string[] labels) || element?.dropdown == null) return true;
                element.SetIndex(Mathf.Clamp((int)value, 0, labels.Length - 1), false);
                return false;
            }
            private static void Postfix(ConfigEntry entry, CustomUIElement element)
            {
                if (attached == null || !ReferenceEquals(attached.config.Get(entry.Key), entry) || element?.slider == null) return;
                var number = element.root.transform.parent.Find("ShopAssist_Value_" + entry.Key)?.GetComponentInChildren<TMP_Text>();
                if (number != null) number.text = Mathf.RoundToInt(element.GetFloat()).ToString();
            }
        }

        [HarmonyPatch(typeof(ModConfigWindow), nameof(ModConfigWindow.Open))]
        private static class ConfigGroups
        {
            private static void Postfix(LoadedMod mod, CustomUIWindow __result)
            {
                if (attached == null || mod?.Manifest?.ID != ModEntry.Id || __result == null) return;
                __result.SetTitle(Text("settings.title"));
                for (int tier = 0; tier < GearSections.Length; tier++)
                {
                    __result.Get("section_" + GearSections[tier])?.SetText(SectionLabel(tier));
                    var rows = attached.config.Entries.Where(entry => entry.Section == GearSections[tier])
                        .Select(entry => new { entry.Key, Row = __result.Get("label_" + entry.Key)?.root?.transform.parent })
                        .Where(value => value.Row != null).ToArray();
                    int[] positions = rows.Select(value => value.Row.GetSiblingIndex()).OrderBy(index => index).ToArray();
                    var ordered = rows.OrderBy(value => OptionOrder(value.Key)).ToArray();
                    for (int i = 0; i < ordered.Length; i++) ordered[i].Row.SetSiblingIndex(positions[i]);
                }
                Canvas.ForceUpdateCanvases();
            }
        }
    }
}
