using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopAssist
{
    internal static class CustomerBasketSearch
    {
        internal static bool MatchesDemand(ICollection<string> publicTypes, ICollection<string> publicDemand) =>
            publicTypes != null && publicTypes.Count != 0 && publicDemand != null && publicDemand.Count != 0 &&
            (publicDemand.Contains("ALL_TYPE") || publicTypes.All(publicDemand.Contains));
        internal sealed class Entry<T> { internal T Item; internal int Price, Units; }
        internal sealed class Choice<T, TShape>
        {
            internal int Total, Units;
            internal readonly List<Entry<T>> Items = new List<Entry<T>>();
            internal readonly List<TShape> Shapes = new List<TShape>();
        }

        // Keep distinct footprints even at the same price: a cheaper-looking
        // partial state can be the only one that leaves room for a later item.
        internal static List<Choice<T, TShape>> Compare<T, TShape>(IReadOnlyList<Entry<T>> offers,
            int baseline, int baselineUnits, int limit, Func<T, IReadOnlyList<TShape>, TShape> placement) where TShape : class
        {
            if (baseline < 0 || baseline > limit || baselineUnits < 0) return new List<Choice<T, TShape>>();
            var states = new List<Choice<T, TShape>> { new Choice<T, TShape> { Total = baseline, Units = baselineUnits } };
            foreach (Entry<T> offer in offers)
            {
                if (offer.Price < 0 || offer.Units <= 0) continue;
                var next = new List<Choice<T, TShape>>(states);
                foreach (Choice<T, TShape> previous in states)
                {
                    if (offer.Price > limit - previous.Total || offer.Units > int.MaxValue - previous.Units) continue;
                    TShape shape = placement(offer.Item, previous.Shapes);
                    if (shape == null) continue;
                    var candidate = new Choice<T, TShape> { Total = previous.Total + offer.Price, Units = previous.Units + offer.Units };
                    candidate.Items.AddRange(previous.Items); candidate.Items.Add(offer);
                    candidate.Shapes.AddRange(previous.Shapes); candidate.Shapes.Add(shape);
                    next.Add(candidate);
                }
                states = next.OrderByDescending(state => state.Total).ThenByDescending(state => state.Units).Take(128)
                    .Concat(next.OrderByDescending(state => state.Units).ThenBy(state => state.Total).Take(128))
                    .Concat(next.Where(state => state.Items.Count == 0)).Distinct().ToList();
            }
            var available = states.Where(state => state.Items.Count != 0 || baselineUnits != 0).ToList();
            if (available.Count == 0) return available;
            return new[] { available.OrderByDescending(state => state.Total).ThenByDescending(state => state.Units).First(),
                available.OrderByDescending(state => state.Units).ThenBy(state => state.Total).First() }.Distinct().ToList();
        }
    }
}
