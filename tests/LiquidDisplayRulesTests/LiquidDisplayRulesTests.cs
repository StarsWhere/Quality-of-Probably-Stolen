using System;
using System.Collections.Generic;
using System.Linq;
using ShopAssist;

internal static class LiquidDisplayRulesTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static LiquidDisplayRules.Part Part(string id, int amount, int index) =>
        new LiquidDisplayRules.Part { Id = id, Amount = amount, NativeIndex = index };

    private static void Order(IEnumerable<LiquidDisplayRules.Part> rows, params string[] ids) =>
        Check(rows.Select(row => row.Id).SequenceEqual(ids), "Unexpected component order: " + string.Join(",", rows.Select(row => row.Id)));

    private static void Main()
    {
        var pins = new List<string> { "microbe", "waste" };
        string encoded = LiquidPins.Write(pins);
        Check(LiquidPins.TryRead(encoded, out var decoded) && decoded.SequenceEqual(pins), "Pin order did not round-trip.");
        Check(LiquidPins.TryLegacy("  organic_waste  ", out var migrated) && migrated.SequenceEqual(new[] { "organic_waste" }) &&
            LiquidPins.TryLegacy("", out migrated) && migrated.Count == 0 && !LiquidPins.TryLegacy(new string('x', 129), out _),
            "Legacy single pins were not migrated safely.");
        Check(LiquidPins.TryReadOrMigrate("organic_waste", out migrated, out bool didMigrate) && didMigrate &&
            migrated.SequenceEqual(new[] { "organic_waste" }) &&
            LiquidPins.TryReadOrMigrate(encoded, out migrated, out didMigrate) && !didMigrate && migrated.SequenceEqual(pins) &&
            !LiquidPins.TryReadOrMigrate("{broken", out _, out _), "Pin migration accepted malformed structured data.");
        foreach (string invalid in new[] { "", "{}", "{\"version\":2,\"pins\":[]}",
            "{\"version\":1,\"pins\":[\"water\",\"water\"]}", "{\"version\":1,\"pins\":[\" \"]}",
            "{\"version\":1,\"pins\":[],\"extra\":true}" })
            Check(!LiquidPins.TryRead(invalid, out _), "Invalid pin data was accepted: " + invalid);

        var display = new LiquidDisplayRules();
        object sample = new object();
        var parts = new[] { Part("water", 80, 0), Part("waste", 15, 1), Part("microbe", 5, 2) };
        Order(display.Update(sample, parts, Array.Empty<string>(), true, true), "water", "waste", "microbe");
        Order(display.Update(sample, parts, pins, true, false), "microbe", "waste", "water");
        var held = display.Update(sample, new[] { Part("water", 2, 0), Part("waste", 70, 1), Part("new", 3, 3) }, pins, true, true);
        Order(held, "microbe", "waste", "water", "new");
        Check(held[0].Amount == 0 && held[1].Amount == 70 && held[2].Amount == 2,
            "Hover must retain order while updating live measurements, including a depleted pin.");
        Order(display.Update(sample, new[] { Part("water", 2, 0), Part("waste", 70, 1), Part("new", 3, 3) }, pins, true, false),
            "microbe", "waste", "new", "water");
        Order(display.Update(sample, new[] { Part("water", 2, 0), Part("waste", 70, 1), Part("new", 3, 3) }, Array.Empty<string>(), false, false),
            "water", "waste", "new");
        object anotherSample = new object();
        Order(display.Update(anotherSample, new[] { Part("water", 1, 0) }, pins, true, false), "water");
        Check(display.Update(anotherSample, Array.Empty<LiquidDisplayRules.Part>(), pins, true, false).Count == 0,
            "A different sample must not inherit zero rows from the previous sample.");
        Console.WriteLine("PASS pin schema, stable sorting, hover updates and sample-scoped zero retention");
    }
}
