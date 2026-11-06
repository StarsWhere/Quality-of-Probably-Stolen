using System;
using System.Collections.Generic;

namespace ShopAssist
{
    internal enum FeatureCategory { Items, Trade, Machines, Calendar }

    internal sealed class FeatureDescriptor
    {
        internal int Id { get; }
        internal FeatureCategory Category { get; }
        internal int Group { get; }
        internal string Dependency { get; }
        internal string SettingKey => "f" + Id.ToString("00") + ".enabled";
        internal FeatureDescriptor(int id, FeatureCategory category, int group = 0, string dependency = null)
        { Id = id; Category = category; Group = group; Dependency = dependency; }
    }

    // Presentation, search, and command availability share this feature map.
    internal static class FeatureCatalog
    {
        private static readonly FeatureDescriptor[] features = {
            new FeatureDescriptor(1, FeatureCategory.Trade),
            new FeatureDescriptor(2, FeatureCategory.Items),
            new FeatureDescriptor(3, FeatureCategory.Trade),
            new FeatureDescriptor(4, FeatureCategory.Trade),
            new FeatureDescriptor(5, FeatureCategory.Items),
            new FeatureDescriptor(6, FeatureCategory.Items),
            new FeatureDescriptor(7, FeatureCategory.Items, 7),
            new FeatureDescriptor(8, FeatureCategory.Items, 7),
            new FeatureDescriptor(9, FeatureCategory.Items, 7, "f08.enabled"),
            new FeatureDescriptor(10, FeatureCategory.Machines, 10),
            new FeatureDescriptor(11, FeatureCategory.Trade),
            new FeatureDescriptor(12, FeatureCategory.Machines, 10, "f10.enabled"),
            new FeatureDescriptor(13, FeatureCategory.Calendar),
            new FeatureDescriptor(14, FeatureCategory.Calendar),
            new FeatureDescriptor(15, FeatureCategory.Calendar),
            new FeatureDescriptor(16, FeatureCategory.Trade),
            new FeatureDescriptor(17, FeatureCategory.Calendar),
            new FeatureDescriptor(18, FeatureCategory.Calendar),
            new FeatureDescriptor(19, FeatureCategory.Machines),
            new FeatureDescriptor(20, FeatureCategory.Items),
            new FeatureDescriptor(21, FeatureCategory.Calendar),
            new FeatureDescriptor(22, FeatureCategory.Items),
            new FeatureDescriptor(23, FeatureCategory.Trade),
            new FeatureDescriptor(24, FeatureCategory.Trade),
            new FeatureDescriptor(25, FeatureCategory.Items),
            new FeatureDescriptor(26, FeatureCategory.Items),
            new FeatureDescriptor(27, FeatureCategory.Trade),
            new FeatureDescriptor(28, FeatureCategory.Trade),
            new FeatureDescriptor(29, FeatureCategory.Trade),
            new FeatureDescriptor(30, FeatureCategory.Machines),
            new FeatureDescriptor(31, FeatureCategory.Machines),
            new FeatureDescriptor(32, FeatureCategory.Machines),
            new FeatureDescriptor(33, FeatureCategory.Machines),
            new FeatureDescriptor(34, FeatureCategory.Trade),
            new FeatureDescriptor(35, FeatureCategory.Machines),
            new FeatureDescriptor(36, FeatureCategory.Items),
            new FeatureDescriptor(37, FeatureCategory.Machines),
            new FeatureDescriptor(38, FeatureCategory.Items),
            new FeatureDescriptor(39, FeatureCategory.Items),
            new FeatureDescriptor(40, FeatureCategory.Items),
            new FeatureDescriptor(41, FeatureCategory.Items),
        };

        internal static IReadOnlyList<FeatureDescriptor> All => features;
        internal static FeatureDescriptor For(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 4 || key[0] != 'f' || key[3] != '.' ||
                !int.TryParse(key.Substring(1, 2), out int id) || id < 1 || id > features.Length)
                throw new ArgumentException("Missing feature ID: " + key);
            return features[id - 1];
        }
        internal static string CategoryKey(FeatureCategory category) => "section.category" + ((int)category + 1);
        internal static string GroupKey(FeatureDescriptor feature) => feature.Group == 0 ? null : "settings.group" + feature.Group;
        internal static string PrimarySetting(string key) => key == "f01.wholesale" || key == "f04.unseen" ? key : For(key).SettingKey;
        internal static IEnumerable<string> RequiredSettings(string key)
        {
            var feature = For(key);
            string primary = PrimarySetting(key);
            yield return primary;
            if (key == "f04.unseen") yield return "f04.enabled";
            if (feature.Dependency != null) yield return feature.Dependency;
        }
        internal static int Order(string key)
        {
            var feature = For(key);
            int group = feature.Group == 0 ? feature.Id : feature.Group;
            bool primary = key.EndsWith(".enabled", StringComparison.Ordinal) || key == "f04.unseen" || key == "f01.wholesale";
            return group * 1000 + feature.Id * 10 + (primary ? 0 : 1);
        }
    }
}
