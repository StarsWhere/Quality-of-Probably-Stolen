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
    private static RoutingRule Rule(string location, int priority) => new RoutingRule
    { location = location, priority = priority, receive = true };

    private static void Main()
    {
        var highCategory = Rule("root:stock", 100);
        highCategory.categories.Add("FOOD");
        var specific = Rule("item:4:contents:-1", -10);
        specific.itemIds.Add("cup_noodle");
        var tieFirst = Rule("item:5:contents:-1", 0);
        tieFirst.categories.Add("FOOD");
        var tieSecond = Rule("item:6:storage:1", 0);
        tieSecond.categories.Add("FOOD");
        var book = new RoutingBook { rules = new List<RoutingRule> { highCategory, specific, tieFirst, tieSecond } };
        var types = new[] { "FOOD" };
        Check(RoutingRules.Valid(book, types), "bounded rules and instance keys are valid");
        Check(RoutingRules.Ranked(book, "cup_noodle", types, true).SequenceEqual(new[] { specific, highCategory, tieFirst, tieSecond }),
            "specific item rules outrank categories before priority; ties follow player order");
        specific.excludedCategories.Add("FOOD");
        Check(RoutingRules.Match(specific, "cup_noodle", types, true) == 0, "a category exclusion overrides a specific item");
        Check(RoutingRules.Match(specific, "cup_noodle", null, false) == 0,
            "unknown categories cannot bypass an exclusion");
        specific.excludedCategories.Clear();
        specific.excludedIds.Add("cup_noodle");
        Check(RoutingRules.Match(specific, "cup_noodle", types, true) == 0, "a specific exclusion overrides inclusion");
        tieFirst.receive = false;
        Check(RoutingRules.Match(tieFirst, "other", types, true) == 0, "a label without permission does not receive goods");
        tieFirst.receive = true;
        book.rules.Add(tieFirst.Copy());
        Check(!RoutingRules.Valid(book, types), "duplicate location bindings fail the whole saved book");
        book.rules.RemoveAt(book.rules.Count - 1);
        highCategory.categories.Add("UNKNOWN");
        Check(!RoutingRules.Valid(book, types), "unknown categories are not silently discarded");
        Check(!RoutingRules.ValidLocation("item:0:contents:-1") && !RoutingRules.ValidLocation("item:4:contents:9999"),
            "malformed instance and page identities fail validation");
        Console.WriteLine($"Routing rules: {assertions} assertions passed.");
    }
}
