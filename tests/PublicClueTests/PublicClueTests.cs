using System;
using System.Collections.Generic;
using ShopAssist;

internal static class PublicClueTests
{
    private static int assertions;
    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new Exception(message);
    }

    private static void CategoryMatch()
    {
        Check(PublicClues.ContainsShownType("Bread [Food] [Fresh]", "Food"), "shown type can be recorded");
        Check(!PublicClues.ContainsShownType("Bread [Fresh]", "Food"), "unshown type cannot be inferred");
        Check(PublicClues.AllowedByExclusions(null, null), "unknown types remain selectable without a category filter");
        Check(!PublicClues.AllowedByExclusions(new[] { "WATER" }, null), "unknown types are not presumed to pass an exclusion");
        Check(!PublicClues.AllowedByExclusions(new[] { "WATER" }, new[] { "FOOD", "WATER" }),
            "an item with an excluded visible category is left behind");
        Check(PublicClues.AllowedByExclusions(new[] { "WATER" }, new[] { "FOOD" }),
            "a known item with only allowed categories remains selectable");
        Check(!PublicClues.AllowedByExclusions(new[] { "WATER" }, new[] { "FOOD" }, false),
            "a custom tooltip showing one type does not prove that other types are absent");
    }

    private static void EvidenceLifecycle()
    {
        var book = new KnownRiskLedger<object, object>();
        PublicRisk Inside(object parent) => book.InsideSnapshot().TryGetValue(parent, out var risk) ? risk : PublicRisk.None;
        object item = new object(), bag = new object(), box = new object();
        object bagInventory = new object(), boxInventory = new object(), unrelatedInventory = new object();
        book.Remember(item, PublicRisk.Stolen, new[] { bag, box }, new[] { bagInventory, boxInventory });
        Check(Inside(bag) == PublicRisk.Stolen && Inside(box) == PublicRisk.Stolen,
            "observed risk follows the observed parent path");
        var other = new object();
        book.Remember(other, PublicRisk.Contraband, new[] { box }, new[] { boxInventory });
        Check(Inside(box) == (PublicRisk.Stolen | PublicRisk.Contraband) && Inside(bag) == PublicRisk.Stolen,
            "the refresh snapshot combines risks from separate observed paths");
        book.InvalidateInventory(unrelatedInventory, new object());
        Check(Inside(box) == (PublicRisk.Stolen | PublicRisk.Contraband), "unrelated changes preserve evidence");
        book.InvalidateInventory(bagInventory, new object());
        Check(Inside(bag) == PublicRisk.None && Inside(box) == PublicRisk.Contraband,
            "a path change removes only affected evidence");
        book.InvalidateInventory(boxInventory, other);
        book.Remember(item, PublicRisk.Contraband, new[] { bag, box }, new[] { bagInventory, boxInventory });
        book.InvalidateItem(bag);
        Check(Inside(box) == PublicRisk.None, "changing an ancestor invalidates the path");
        book.Remember(item, PublicRisk.Stolen, new[] { box }, new[] { boxInventory });
        book.Clear();
        Check(Inside(box) == PublicRisk.None, "session reset removes evidence");
    }

    private static void ShapeBorders()
    {
        var single = OutlineGeometry.Build(new[] { true }, 1, 1, 16, false);
        Check(single.Count == 4, "one occupied cell has four outer edges");
        var two = OutlineGeometry.Build(new[] { true, true }, 2, 1, 16, false);
        Check(two.Count == 6, "shared edges do not draw through the shape");
        var irregular = OutlineGeometry.Build(new[] { true, false, true, true }, 2, 2, 16, false);
        Check(irregular.Count == 8, "an L shape retains its actual boundary");
        Check(OutlineGeometry.Build(new[] { true }, 1, 1, 16, true).Count == 8, "contained risk uses dashed edges");
        Check(OutlineGeometry.Build(new[] { true }, 2, 1, 16, false).Count == 0, "invalid masks fail closed");
    }

    private static void Main()
    {
        CategoryMatch();
        EvidenceLifecycle();
        ShapeBorders();
        Console.WriteLine($"Public clues: {assertions} assertions passed.");
    }
}
