using System;
using System.Collections.Generic;

namespace ShopAssist
{
    internal enum FeatureCategory { Items, Trade, Machines, Calendar, Playback }
    internal enum FeatureTier { Basic = 1, RuleBased = 2, Planning = 3 }

    internal sealed class FeatureDescriptor
    {
        internal int Id { get; }
        internal FeatureCategory Category { get; }
        internal FeatureTier Tier { get; }
        internal bool DefaultEnabled { get; }
        internal int Group { get; }
        internal string Dependency { get; }
        internal string SettingKey => Id == 25 ? "f25.return_mode" : "f" + Id.ToString("00") + ".enabled";
        internal FeatureDescriptor(int id, FeatureCategory category, FeatureTier tier, bool defaultEnabled = false,
            int group = 0, string dependency = null)
        {
            if (tier != FeatureTier.Basic && defaultEnabled)
                throw new ArgumentException("Only basic features can be enabled by default.", nameof(defaultEnabled));
            Id = id; Category = category; Tier = tier; DefaultEnabled = defaultEnabled; Group = group; Dependency = dependency;
        }
    }

    // Presentation, search, and command availability share this feature map.
    internal static class FeatureCatalog
    {
        private static readonly FeatureDescriptor[] features = {
            new FeatureDescriptor(1, FeatureCategory.Trade, FeatureTier.RuleBased),
            new FeatureDescriptor(2, FeatureCategory.Items, FeatureTier.Basic, true),
            new FeatureDescriptor(3, FeatureCategory.Trade, FeatureTier.Basic, true),
            new FeatureDescriptor(4, FeatureCategory.Trade, FeatureTier.Basic, true),
            new FeatureDescriptor(5, FeatureCategory.Items, FeatureTier.Basic, true),
            new FeatureDescriptor(7, FeatureCategory.Items, FeatureTier.RuleBased, group: 7),
            new FeatureDescriptor(8, FeatureCategory.Items, FeatureTier.RuleBased, group: 7),
            new FeatureDescriptor(9, FeatureCategory.Items, FeatureTier.RuleBased, group: 7, dependency: "f08.enabled"),
            new FeatureDescriptor(10, FeatureCategory.Machines, FeatureTier.Planning, group: 10),
            new FeatureDescriptor(11, FeatureCategory.Trade, FeatureTier.Planning),
            new FeatureDescriptor(12, FeatureCategory.Machines, FeatureTier.Planning, group: 10, dependency: "f10.enabled"),
            new FeatureDescriptor(14, FeatureCategory.Calendar, FeatureTier.Basic),
            new FeatureDescriptor(15, FeatureCategory.Calendar, FeatureTier.Basic),
            new FeatureDescriptor(16, FeatureCategory.Trade, FeatureTier.Basic),
            new FeatureDescriptor(17, FeatureCategory.Calendar, FeatureTier.Basic),
            new FeatureDescriptor(18, FeatureCategory.Calendar, FeatureTier.Basic),
            new FeatureDescriptor(19, FeatureCategory.Machines, FeatureTier.Basic),
            new FeatureDescriptor(20, FeatureCategory.Items, FeatureTier.Basic),
            new FeatureDescriptor(21, FeatureCategory.Calendar, FeatureTier.Basic),
            new FeatureDescriptor(23, FeatureCategory.Trade, FeatureTier.Basic),
            new FeatureDescriptor(24, FeatureCategory.Trade, FeatureTier.RuleBased),
            new FeatureDescriptor(25, FeatureCategory.Items, FeatureTier.RuleBased),
            new FeatureDescriptor(28, FeatureCategory.Trade, FeatureTier.Basic),
            new FeatureDescriptor(30, FeatureCategory.Machines, FeatureTier.Planning),
            new FeatureDescriptor(31, FeatureCategory.Machines, FeatureTier.RuleBased),
            new FeatureDescriptor(32, FeatureCategory.Machines, FeatureTier.RuleBased),
            new FeatureDescriptor(33, FeatureCategory.Machines, FeatureTier.RuleBased),
            new FeatureDescriptor(34, FeatureCategory.Trade, FeatureTier.Planning),
            new FeatureDescriptor(35, FeatureCategory.Machines, FeatureTier.RuleBased),
            new FeatureDescriptor(36, FeatureCategory.Items, FeatureTier.Planning),
            new FeatureDescriptor(37, FeatureCategory.Machines, FeatureTier.RuleBased),
            new FeatureDescriptor(38, FeatureCategory.Items, FeatureTier.Basic, true),
            new FeatureDescriptor(39, FeatureCategory.Items, FeatureTier.Basic, true),
            new FeatureDescriptor(40, FeatureCategory.Items, FeatureTier.Basic, true),
            new FeatureDescriptor(41, FeatureCategory.Items, FeatureTier.Basic, true),
            new FeatureDescriptor(42, FeatureCategory.Playback, FeatureTier.Basic),
        };

        internal static IReadOnlyList<FeatureDescriptor> All => features;
        internal static FeatureDescriptor For(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 4 || key[0] != 'f' || key[3] != '.' ||
                !int.TryParse(key.Substring(1, 2), out int id))
                throw new ArgumentException("Missing feature ID: " + key);
            return Array.Find(features, feature => feature.Id == id) ??
                throw new ArgumentException("Missing feature ID: " + key);
        }
        internal static string CategoryKey(FeatureCategory category) => "section.category" + ((int)category + 1);
        internal static string TierKey(FeatureTier tier) => "section.tier" + (int)tier;
        internal static string TierDescriptionKey(FeatureTier tier) => TierKey(tier) + ".description";
        internal static string GroupKey(FeatureDescriptor feature) => feature.Group == 0 ? null : "settings.group" + feature.Group;
        private static FeatureTier? OptionTier(string key) => key == "f01.wholesale" || key == "f05.auto_insert" ? FeatureTier.RuleBased :
            key == "f04.unseen" ? FeatureTier.Planning : (FeatureTier?)null;
        internal static FeatureTier TierFor(string key) => OptionTier(key) ?? For(key).Tier;
        internal static bool ToggleDefault(string key, bool requestedDefault)
        {
            FeatureDescriptor feature = For(key);
            if (key == feature.SettingKey) return feature.DefaultEnabled;
            return OptionTier(key).HasValue ? false : requestedDefault;
        }
        internal static string PrimarySetting(string key) => key == "f01.wholesale" || key == "f04.unseen" || key == "f05.auto_insert" ? key : For(key).SettingKey;
        internal static IEnumerable<string> RequiredSettings(string key)
        {
            var feature = For(key);
            string primary = PrimarySetting(key);
            yield return primary;
            if (key == "f04.unseen") yield return "f04.enabled";
            if (key == "f05.auto_insert") yield return "f05.enabled";
            if (feature.Dependency != null) yield return feature.Dependency;
        }
        internal static int Order(string key)
        {
            var feature = For(key);
            int group = feature.Group == 0 ? feature.Id : feature.Group;
            bool primary = key.EndsWith(".enabled", StringComparison.Ordinal) || OptionTier(key).HasValue;
            return (int)feature.Category * 1000000 + group * 1000 + feature.Id * 10 +
                (key == feature.SettingKey ? 0 : primary ? 1 : 2);
        }
    }
}
