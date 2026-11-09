using System;
using System.Collections.Generic;
using System.Linq;
using ShopAssist;

internal static class CustomerBasketTests
{
    private sealed class Footprint { internal int Mask; }
    private static int assertions;
    private static void Check(bool ok, string message) { assertions++; if (!ok) throw new Exception(message); }
    private static CustomerBasketSearch.Entry<int> Offer(int shape, int price, int units = 1) =>
        new CustomerBasketSearch.Entry<int> { Item = shape, Price = price, Units = units };
    private static Func<int, IReadOnlyList<Footprint>, Footprint> Fit(int desk, int occupied) => (shape, reserved) =>
    {
        int used = occupied | reserved.Aggregate(0, (mask, next) => mask | next.Mask);
        for (int shift = 0; shift < 8; shift++)
        {
            int pose = shape << shift;
            if ((pose & desk) == pose && (pose & used) == 0) return new Footprint { Mask = pose };
        }
        return null;
    };
    private static void Main()
    {
        var candidates = new[] { Offer(1, 30), Offer(1, 20, 3), Offer(1, 10) };
        var choices = CustomerBasketSearch.Compare(candidates, 40, 2, 75, Fit(0b1111, 1));
        Check(choices.All(choice => choice.Total <= 75 && choice.Total == 40 + choice.Items.Sum(item => item.Price)),
            "existing accepted counter goods count toward every displayed total and budget");
        Check(choices.Any(choice => choice.Total == 70) && choices.Any(choice => choice.Units == 6),
            "the price and quantity choices include the retained counter units");
        choices = CustomerBasketSearch.Compare(candidates, 40, 2, 75, Fit(0b11, 1));
        Check(choices.All(choice => choice.Items.Count == 1 && choice.Shapes.Count == 1),
            "existing physical counter occupancy limits additional offers independently of price");
        choices = CustomerBasketSearch.Compare(new[] { Offer(0b11, 7), Offer(0b101, 8) }, 0, 0, 100, Fit(0b11111111, 0b10000001));
        Check(choices.Any(choice => choice.Items.Count == 2) && choices.All(choice =>
            choice.Items.Count == choice.Shapes.Count && choice.Shapes.All(shape => (shape.Mask & 0b10000001) == 0) &&
            choice.Shapes.SelectMany((shape, index) => choice.Shapes.Skip(index + 1).Select(other => (shape.Mask & other.Mask) == 0)).All(clear => clear) &&
            choice.Items.Zip(choice.Shapes, (item, shape) => Enumerable.Range(0, 8).Any(shift => (item.Item << shift) == shape.Mask)).All(same => same)),
            "each result must retain the corresponding planned footprint without overlapping occupied cells or another offer");
        choices = CustomerBasketSearch.Compare(new[] { Offer(0b11, 10) }, 0, 0, 100, Fit(0b10101, 0));
        Check(choices.Count == 0, "a wide item cannot fit disconnected empty cells with the same total area");
        choices = CustomerBasketSearch.Compare(new[] { Offer(1, 99) }, 0, 0, 100, Fit(0b111, 0b111));
        Check(choices.Count == 0, "refused counter goods occupy physical space without contributing sale value");
        choices = CustomerBasketSearch.Compare(candidates, 80, 2, 75, Fit(0b1111, 0));
        Check(choices.Count == 0, "already over-budget counter goods cannot be silently omitted");
        choices = CustomerBasketSearch.Compare(Array.Empty<CustomerBasketSearch.Entry<int>>(), 0, 4, 100, Fit(1, 1));
        Check(choices.Single().Units == 4 && choices[0].Total == 0, "accepted zero-price counter goods remain in the basket");
        choices = CustomerBasketSearch.Compare(new[] { Offer(1, int.MaxValue) }, 1, 1, int.MaxValue, Fit(1, 0));
        Check(choices.Single().Items.Count == 0 && choices[0].Total == 1, "price arithmetic cannot overflow into a feasible offer");
        Check(!CustomerBasketSearch.MatchesDemand(null, new[] { "ALL_TYPE" }) &&
            !CustomerBasketSearch.MatchesDemand(Array.Empty<string>(), new[] { "ALL_TYPE" }), "unknown public classification never passes the tag-only filter");
        Check(CustomerBasketSearch.MatchesDemand(new[] { "FOOD" }, new[] { "FOOD" }) &&
            !CustomerBasketSearch.MatchesDemand(new[] { "FOOD", "DRINK" }, new[] { "FOOD" }),
            "every public category must belong to the displayed demand tags");
        Check(CustomerBasketSearch.MatchesDemand(new[] { "FOOD", "DRINK" }, new[] { "ALL_TYPE" }) &&
            !CustomerBasketSearch.MatchesDemand(new[] { "FOOD" }, Array.Empty<string>()), "broad displayed demand accepts known categories; absent displayed demand accepts none");
        Console.WriteLine($"Customer basket: {assertions} assertions passed.");
    }
}
