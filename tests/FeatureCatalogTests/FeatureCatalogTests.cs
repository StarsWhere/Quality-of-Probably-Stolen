using System;
using System.Collections.Generic;
using System.Linq;
using ShopAssist;

internal static class FeatureCatalogTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Main()
    {
        var expected = new Dictionary<FeatureCategory, int[]> {
            [FeatureCategory.Items] = new[] { 2, 5, 7, 8, 9, 20, 22, 25, 26, 36, 38, 39, 40, 41 },
            [FeatureCategory.Trade] = new[] { 1, 3, 4, 11, 16, 23, 24, 27, 28, 29, 34 },
            [FeatureCategory.Machines] = new[] { 10, 12, 19, 30, 31, 32, 33, 35, 37 },
            [FeatureCategory.Calendar] = new[] { 13, 14, 15, 17, 18, 21 },
            [FeatureCategory.Playback] = new[] { 42 }
        };
        Check(FeatureCatalog.All.Count == 41 && FeatureCatalog.All.Select(feature => feature.Id).Distinct().Count() == 41,
            "Features must have one descriptor each");
        foreach (var pair in expected)
            Check(FeatureCatalog.All.Where(feature => feature.Category == pair.Key).Select(feature => feature.Id).SequenceEqual(pair.Value),
                "Wrong category: " + pair.Key);
        var tiers = new Dictionary<FeatureTier, int[]> {
            [FeatureTier.Basic] = new[] { 2, 3, 4, 5, 13, 14, 15, 16, 17, 18, 19, 20, 21, 23, 28, 38, 39, 40, 41, 42 },
            [FeatureTier.RuleBased] = new[] { 1, 7, 8, 9, 22, 24, 25, 26, 27, 29, 31, 32, 33, 35, 37 },
            [FeatureTier.Planning] = new[] { 10, 11, 12, 30, 34, 36 }
        };
        foreach (var pair in tiers)
            Check(FeatureCatalog.All.Where(feature => feature.Tier == pair.Key).Select(feature => feature.Id).SequenceEqual(pair.Value),
                "Wrong tier: " + pair.Key);
        int[] enabledByDefault = { 2, 3, 4, 5, 38, 39, 40, 41 };
        Check(FeatureCatalog.All.Where(feature => feature.DefaultEnabled).Select(feature => feature.Id).SequenceEqual(enabledByDefault),
            "Wrong primary feature defaults");
        foreach (var feature in FeatureCatalog.All)
        {
            Check(ReferenceEquals(FeatureCatalog.For(feature.SettingKey), feature), "Feature key resolves to the wrong ID: " + feature.Id);
            Check(FeatureCatalog.TierFor(feature.SettingKey) == feature.Tier, "Primary tier differs from descriptor: " + feature.Id);
            Check(FeatureCatalog.ToggleDefault(feature.SettingKey, false) == feature.DefaultEnabled &&
                FeatureCatalog.ToggleDefault(feature.SettingKey, true) == feature.DefaultEnabled,
                "Primary default depends on the registration site's requested value: " + feature.Id);
        }
        Check(FeatureCatalog.TierFor("f01.wholesale") == FeatureTier.RuleBased &&
            FeatureCatalog.TierFor("f05.auto_insert") == FeatureTier.RuleBased &&
            FeatureCatalog.TierFor("f04.unseen") == FeatureTier.Planning, "Cross-tier suboption classification missing");
        Check(!FeatureCatalog.ToggleDefault("f01.wholesale", true) && !FeatureCatalog.ToggleDefault("f05.auto_insert", true) &&
            !FeatureCatalog.ToggleDefault("f04.unseen", true),
            "Cross-tier suboptions must default off");
        foreach (string key in new[] { "f04.stolen", "f04.contraband", "f29.price", "f40.occupied_drag" })
            Check(FeatureCatalog.ToggleDefault(key, true) && !FeatureCatalog.ToggleDefault(key, false),
                "An ordinary safety or behavior suboption lost its requested default: " + key);
        bool rejectedDefault = false;
        try { new FeatureDescriptor(34, FeatureCategory.Trade, FeatureTier.Planning, true); }
        catch (ArgumentException) { rejectedDefault = true; }
        Check(rejectedDefault, "Planning features may not declare an enabled primary default");
        Check(FeatureCatalog.RequiredSettings("f07.open").SequenceEqual(new[] { "f07.enabled" }), "Rule editing depends on routing");
        Check(FeatureCatalog.RequiredSettings("f08.open").SequenceEqual(new[] { "f08.enabled" }), "Routing depends on rule editing");
        Check(FeatureCatalog.RequiredSettings("f09.mode").SequenceEqual(new[] { "f09.enabled", "f08.enabled" }), "Preview dependency missing");
        Check(FeatureCatalog.RequiredSettings("f12.enabled").SequenceEqual(new[] { "f12.enabled", "f10.enabled" }), "Module cabinet dependency missing");
        Check(FeatureCatalog.RequiredSettings("f04.unseen").SequenceEqual(new[] { "f04.unseen", "f04.enabled" }), "Inspection dependency missing");
        Check(FeatureCatalog.RequiredSettings("f05.auto_insert").SequenceEqual(new[] { "f05.auto_insert", "f05.enabled" }), "Automatic arrangement dependency missing");
        Check(FeatureCatalog.RequiredSettings("f01.wholesale").SequenceEqual(new[] { "f01.wholesale" }), "Wholesale switch is independent");
        Check(FeatureCatalog.TierKey(FeatureTier.Basic) == "section.tier1" &&
            FeatureCatalog.TierDescriptionKey(FeatureTier.Planning) == "section.tier3.description", "Tier labels must use the shared tier identity");
        Check(FeatureCatalog.For("f07.open").Group == 7 && FeatureCatalog.For("f08.open").Group == 7 &&
            FeatureCatalog.For("f09.mode").Group == 7 && FeatureCatalog.For("f10.open").Group == 10 &&
            FeatureCatalog.For("f12.enabled").Group == 10, "Related features do not share a visual group");
        Check(FeatureCatalog.Order("f07.enabled") < FeatureCatalog.Order("f08.enabled") &&
            FeatureCatalog.Order("f08.enabled") < FeatureCatalog.Order("f09.enabled") &&
            FeatureCatalog.Order("f10.enabled") < FeatureCatalog.Order("f12.enabled") &&
            FeatureCatalog.Order("f12.enabled") < FeatureCatalog.Order("f19.enabled"), "Related features are not contiguous");
        Check(FeatureCatalog.Order("f05.enabled") < FeatureCatalog.Order("f03.enabled") &&
            FeatureCatalog.Order("f03.enabled") < FeatureCatalog.Order("f19.enabled") &&
            FeatureCatalog.Order("f19.enabled") < FeatureCatalog.Order("f13.enabled"),
            "Purpose groups do not have a stable order within each level");
        Check(FeatureCatalog.For("f42.speed").Category == FeatureCategory.Playback &&
            FeatureCatalog.RequiredSettings("f42.key").SequenceEqual(new[] { "f42.enabled" }), "Playback settings are not discoverable");
        foreach (string invalid in new[] { "", "f00.enabled", "f06.enabled", "f06.hold", "f43.enabled", "abc", "f1.enabled" })
        {
            bool rejected = false;
            try { FeatureCatalog.For(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid key was accepted: " + invalid);
        }
        Console.WriteLine("PASS 41 categories, tiers, primary defaults, cross-tier options, groups and dependencies");
    }
}
