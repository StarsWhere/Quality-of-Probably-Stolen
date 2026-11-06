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
            [FeatureCategory.Items] = new[] { 2, 5, 6, 7, 8, 9, 20, 22, 25, 26, 36, 38, 39, 40, 41 },
            [FeatureCategory.Trade] = new[] { 1, 3, 4, 11, 16, 23, 24, 27, 28, 29, 34 },
            [FeatureCategory.Machines] = new[] { 10, 12, 19, 30, 31, 32, 33, 35, 37 },
            [FeatureCategory.Calendar] = new[] { 13, 14, 15, 17, 18, 21 }
        };
        Check(FeatureCatalog.All.Count == 41 && FeatureCatalog.All.Select(feature => feature.Id).Distinct().Count() == 41,
            "Features must have one descriptor each");
        foreach (var pair in expected)
            Check(FeatureCatalog.All.Where(feature => feature.Category == pair.Key).Select(feature => feature.Id).SequenceEqual(pair.Value),
                "Wrong category: " + pair.Key);
        Check(FeatureCatalog.RequiredSettings("f07.open").SequenceEqual(new[] { "f07.enabled" }), "Rule editing depends on routing");
        Check(FeatureCatalog.RequiredSettings("f08.open").SequenceEqual(new[] { "f08.enabled" }), "Routing depends on rule editing");
        Check(FeatureCatalog.RequiredSettings("f09.mode").SequenceEqual(new[] { "f09.enabled", "f08.enabled" }), "Preview dependency missing");
        Check(FeatureCatalog.RequiredSettings("f12.enabled").SequenceEqual(new[] { "f12.enabled", "f10.enabled" }), "Module cabinet dependency missing");
        Check(FeatureCatalog.RequiredSettings("f04.unseen").SequenceEqual(new[] { "f04.unseen", "f04.enabled" }), "Inspection dependency missing");
        Check(FeatureCatalog.RequiredSettings("f01.wholesale").SequenceEqual(new[] { "f01.wholesale" }), "Wholesale switch is independent");
        Check(FeatureCatalog.For("f07.open").Group == 7 && FeatureCatalog.For("f08.open").Group == 7 &&
            FeatureCatalog.For("f09.mode").Group == 7 && FeatureCatalog.For("f10.open").Group == 10 &&
            FeatureCatalog.For("f12.enabled").Group == 10, "Related features do not share a visual group");
        Check(FeatureCatalog.Order("f07.enabled") < FeatureCatalog.Order("f08.enabled") &&
            FeatureCatalog.Order("f08.enabled") < FeatureCatalog.Order("f09.enabled") &&
            FeatureCatalog.Order("f10.enabled") < FeatureCatalog.Order("f12.enabled") &&
            FeatureCatalog.Order("f12.enabled") < FeatureCatalog.Order("f19.enabled"), "Related features are not contiguous");
        foreach (string invalid in new[] { "", "f00.enabled", "f42.enabled", "abc", "f1.enabled" })
        {
            bool rejected = false;
            try { FeatureCatalog.For(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid key was accepted: " + invalid);
        }
        Console.WriteLine("PASS 41 unique categories, related groups, independent controls and actual dependencies");
    }
}
