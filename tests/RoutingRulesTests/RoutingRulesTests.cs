using System;
using System.Collections.Generic;
using System.Linq;
using ShopAssist;

namespace Newtonsoft.Json { [AttributeUsage(AttributeTargets.Field)] public sealed class JsonRequiredAttribute : Attribute { } }

internal static class RoutingRulesTests
{
    private static int assertions;
    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new Exception(message);
    }
    private static RoutingRule Rule(string location, params string[] categories) => new RoutingRule
    { location = location, receive = true, categories = categories.ToList() };

    private static void Main()
    {
        var first = Rule("item:4:contents:-1", "FOOD", "DRINK");
        var tieFirst = Rule("item:5:contents:-1", "FOOD");
        var tieSecond = Rule("item:6:storage:1", "FOOD", "TOOLS");
        var book = new RoutingBook { rules = new List<RoutingRule> { tieSecond, first, tieFirst } };
        var known = new[] { "FOOD", "DRINK", "TOOLS" };
        var types = new[] { "FOOD", "DRINK" };
        Check(first.ship, "new area rules allow automatic taking by default");
        first.ship = false;
        var copy = first.Copy();
        Check(!copy.ship && copy.receive, "shipping and receiving permissions remain independent");
        copy.categories.Clear();
        Check(first.categories.Count == 2, "editing a draft cannot mutate the stored category selection");
        Check(RoutingRules.Valid(book, known), "bounded category rules and instance keys are valid");
        book.schemaVersion = 1;
        Check(!RoutingRules.Valid(book, known), "legacy routing books cannot activate the new rules");
        book.schemaVersion = 2;
        Check(RoutingRules.Match(first, types) == 2, "ranking counts matching public categories");
        Check(RoutingRules.Ranked(book, types).SequenceEqual(new[] { first, tieFirst, tieSecond }),
            "more matching categories win, with stable location keys breaking ties");
        book.rules.Reverse();
        Check(RoutingRules.Ranked(book, types).SequenceEqual(new[] { first, tieFirst, tieSecond }),
            "saved rule order cannot change routing destinations");
        Check(RoutingRules.Match(first, null) == 0 && RoutingRules.Match(first, Array.Empty<string>()) == 0,
            "unknown or absent public categories cannot match a receiving area");
        Check(RoutingRules.Match(Rule("root:stock"), types) == 0, "an empty category selection receives nothing");
        first.receive = false;
        Check(RoutingRules.Match(first, types) == 0, "receiving disabled excludes an otherwise matching area");
        first.receive = true;
        book.rules.Add(tieFirst.Copy());
        Check(!RoutingRules.Valid(book, known), "duplicate location bindings reject the saved book");
        book.rules.RemoveAt(book.rules.Count - 1);
        first.categories.Add("UNKNOWN");
        Check(!RoutingRules.Valid(book, known), "unknown categories are not silently discarded");
        first.categories.Remove("UNKNOWN");
        first.categories.Add("FOOD");
        Check(!RoutingRules.Valid(book, known), "duplicate categories cannot inflate matching priority");
        Check(!RoutingRules.ValidLocation("item:0:contents:-1") && !RoutingRules.ValidLocation("item:4:contents:9999"),
            "malformed instance and page identities fail validation");
        Console.WriteLine($"Routing rules: {assertions} assertions passed.");
    }
}
