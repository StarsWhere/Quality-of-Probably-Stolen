using System;
using ShopAssist;

static class AppraisalWorksheetTests
{
    static int count;
    static void Check(bool condition, string detail) { count++; if (!condition) throw new Exception(detail); }
    static int Total(AppraisalWorksheetChoices.Catalog catalog, params int[] choices)
    { Check(AppraisalWorksheetChoices.TryTotal(catalog, choices, out int total), "A complete observation must calculate"); return total; }
    static void Main()
    {
        var hand = AppraisalWorksheetChoices.Hand; var rib = AppraisalWorksheetChoices.Ribwich; var plasma = AppraisalWorksheetChoices.Plasma;
        foreach (var catalog in new[] { hand, rib, plasma })
        {
            Check(!AppraisalWorksheetChoices.TryTotal(catalog, new int[catalog.Groups.Length], out _), "New sheets must not guess any observation");
            var choices = new int[catalog.Groups.Length]; Array.Fill(choices, 1);
            for (int i = 0; i < choices.Length; i++)
            {
                choices[i] = 0; Check(!AppraisalWorksheetChoices.TryTotal(catalog, choices, out _), "Any unknown observation must block applying an estimate");
                choices[i] = catalog.Groups[i].Options.Length + 1;
                Check(!AppraisalWorksheetChoices.TryTotal(catalog, choices, out _), "A stale dropdown index must not be interpreted as an observation"); choices[i] = 1;
            }
        }
        Check(Total(hand, 1, 1, 1, 1, 1) == 100, "Standard hand value");
        Check(Total(hand, 2, 2, 1, 2, 2) == 425, "Player-selected tactical set must include its handbook bonus");
        Check(Total(hand, 3, 3, 1, 1, 1) == 620, "Player-selected deluxe set must include its handbook bonus");
        Check(Total(hand, 1, 4, 3, 1, 1) == 50, "Hand minimum value");
        Check(Total(rib, 1, 2, 2, 2, 2, 2) == 1430, "All known ribwich handbook adjustments");
        Check(Total(rib, 2, 2, 2, 2, 2, 2) == 40 && Total(rib, 3, 2, 2, 2, 2, 2) == 40,
            "Opened and counterfeit values must override additive bonuses");
        Check(Total(rib, 2, 0, 0, 0, 0, 0) == 40, "An opened package does not require irrelevant unknown observations");
        Check(!AppraisalWorksheetChoices.TryTotal(plasma, new[] { 1, 1, 1, 1, 3, 5 }, out _) &&
            !AppraisalWorksheetChoices.TryTotal(plasma, new[] { 3, 2, 2, 1, 2, 1 }, out _),
            "Fuel handbook disagreement must block calculation even when every observation is explicitly selected");
        Check(plasma.Basis == 100, "Displayed fuel basis must quote the public handbook rather than infer an unpublished valuation rule");
        Check(AppraisalWorksheetChoices.For("modded_hand") == null && AppraisalWorksheetChoices.For(null) == null,
            "Unknown appraisal families must be rejected");
        Check(!AppraisalWorksheetChoices.TryTotal(new AppraisalWorksheetChoices.Catalog("unknown", 0, 0), Array.Empty<int>(), out _),
            "An unknown catalog must fail closed instead of guessing or indexing an unsupported structure");
        Check(hand.Book == 6 && rib.Book == 4 && plasma.Book == 0, "Guide selectors must match original item activation entries");
        Console.WriteLine($"PASS {count} appraisal choices, unknown-state guards, handbook totals and guide mappings");
    }
}
