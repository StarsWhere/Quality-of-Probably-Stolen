using System;
using System.Collections.Generic;
using ShopAssist;

internal static class RoutingPlanTests
{
    private static int assertions;
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static GameGridInventory Grid(string key, int capacity, GameItem owner = null)
    {
        var grid = new GameGridInventory { capacity = capacity, owner = owner };
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = grid, id = key, Name = key });
        return grid;
    }
    private static GameItemElement Add(GameGridInventory grid, string id, int x)
    {
        var item = new GameItemElement { identifier = id, parentInventory = grid, modifiedShape = new Shape { minX = x } };
        item.types.Add("FOOD"); grid.childItems.Add(item); return item;
    }
    private static RoutingRule Rule(string key, int priority, bool exclusive = false)
    {
        var rule = new RoutingRule { location = key, priority = priority, receive = true, exclusive = exclusive };
        rule.categories.Add("FOOD"); return rule;
    }
    private static void Main()
    {
        ShopInventory.Locations.Clear();
        var source = Grid("source", 4);
        var target = Grid("target", 1);
        var a = Add(source, "apple", 0);
        var b = Add(source, "bread", 1);
        var selection = new List<GameItem> { a, b, a };
        var book = new RoutingBook { rules = new List<RoutingRule> { Rule("target", 0) } };
        var clues = new CustomerIndicators();
        var plan = RoutingPlan.Create(book, selection, clues);
        Check(plan.Moves.Count == 1 && plan.Skipped.Count == 1 && plan.Skipped[0].Reason == "f08.full",
            "shadow capacity is consumed and duplicate selected instances are ignored");
        Check(ReferenceEquals(a.parentInventory, source) && ReferenceEquals(b.parentInventory, source) && target.childItems.Count == 0,
            "preview does not move goods");
        Check(plan.SameAs(RoutingPlan.Create(book, selection, clues)), "the same unchanged selection yields the same plan");
        a.modifiedShape = new Shape { minX = 3 };
        Check(!plan.SameAs(RoutingPlan.Create(book, selection, clues)), "a moved source invalidates the preview");
        a.modifiedShape = new Shape { minX = 0 };
        Add(target, "block", 0);
        Check(!plan.SameAs(RoutingPlan.Create(book, selection, clues)), "a newly occupied destination invalidates the preview");

        var fallback = Grid("fallback", 2);
        book.rules[0].exclusive = true;
        book.rules.Add(Rule("fallback", -1));
        var exclusive = RoutingPlan.Create(book, selection, clues);
        Check(exclusive.Moves.Count == 0 && exclusive.Skipped.Count == 2, "a full exclusive destination does not divert goods");
        book.rules[0].exclusive = false;
        var alternative = RoutingPlan.Create(book, selection, clues);
        Check(alternative.Moves.Count == 2 && alternative.Moves[0].Target.Inventory == fallback,
            "a nonexclusive full destination may fall back to another matching area");

        var settled = Add(target, "settled", 0);
        var settledPlan = RoutingPlan.Create(book, new List<GameItem> { settled }, clues);
        Check(settledPlan.Moves.Count == 0 && settledPlan.Skipped.Count == 1 && settledPlan.Skipped[0].Reason == "f08.already",
            "goods already in the highest-ranked matching destination do not fall back");

        var parent = Add(source, "box", 2);
        var inner = Grid("inner", 2, parent);
        var child = Add(inner, "child", 0);
        var nested = RoutingPlan.Create(book, new List<GameItem> { parent, child }, clues);
        Check(nested.Skipped.Exists(row => ReferenceEquals(row.Item, child) && row.Reason == "f08.parent"),
            "a selected child is not separated from its selected parent");
        book.rules[1].excludedCategories.Add("FOOD");
        a.ordinaryTooltip = false;
        Check(RoutingPlan.Create(book, new List<GameItem> { a }, clues).Moves.Count == 0,
            "an unknown exclusion cannot be bypassed by a replacement tooltip");
        var counter = Grid("counter", 4);
        var counterLocation = ShopInventory.Locations.Find(location => location.id == "counter");
        counterLocation.Ordinary = false;
        counterLocation.MainStock = false;
        var paid = Add(counter, "paid", 0);
        var unpaid = Add(counter, "unpaid", 1);
        paid.counterAllowed = true;
        book.rules[1].excludedCategories.Clear();
        var counterPlan = RoutingPlan.Create(book, new List<GameItem> { paid, unpaid }, clues);
        Check(counterPlan.Moves.Count == 1 && ReferenceEquals(counterPlan.Moves[0].Item, paid) &&
            counterPlan.Skipped.Exists(row => ReferenceEquals(row.Item, unpaid) && row.Reason == "f08.special"),
            "only counter items approved by the shared settlement guard can enter a routing plan");
        paid.counterAllowed = false;
        Check(!counterPlan.SameAs(RoutingPlan.Create(book, new List<GameItem> { paid, unpaid }, clues)),
            "the plan becomes stale when settlement evidence stops being valid");
        foreach (string root in new[] { "counter", "front", "showcase" })
        {
            var host = Add(Grid(root + "_root", 2), "host_" + root, 0);
            var hiddenBranch = Grid(root + "_nested", 4, host);
            ShopInventory.Locations.Find(location => location.id == root + "_nested").MainStock = false;
            var nestedItem = Add(hiddenBranch, "nested_" + root, 0);
            var sourcePlan = RoutingPlan.Create(book, new List<GameItem> { nestedItem }, clues);
            Check(sourcePlan.Moves.Count == 0 && sourcePlan.Skipped.Count == 1 && sourcePlan.Skipped[0].Reason == "f08.special",
                "an ordinary box inside the " + root + " cannot act as a source");
            var staleRule = new RoutingBook { rules = new List<RoutingRule> { Rule(root + "_nested", 100, true) } };
            var targetPlan = RoutingPlan.Create(staleRule, new List<GameItem> { b }, clues);
            Check(targetPlan.Moves.Count == 0 && targetPlan.Skipped.Count == 1 && targetPlan.Skipped[0].Reason == "f08.special",
                "a previously tagged box inside the " + root + " cannot receive stock");
        }
        Console.WriteLine($"Routing preview: {assertions} assertions passed.");
    }
}
