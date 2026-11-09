using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopAssist
{
    internal sealed class RoutingPlan
    {
        internal sealed class Move
        {
            internal WholeStackTransfer.Plan Transfer;
            internal ShopInventory.Location Source, Target;
            internal GameItem Item => Transfer.Item;
            internal int Quantity => Transfer.Amount;
        }
        internal sealed class Skip { internal GameItem Item; internal string Reason; internal int Quantity; }

        internal readonly List<Move> Moves = new List<Move>();
        internal readonly List<Skip> Skipped = new List<Skip>();

        internal static RoutingPlan Create(RoutingBook book, IReadOnlyList<GameItem> selection, CustomerIndicators clues, bool enforceShipping = true)
        {
            var result = new RoutingPlan();
            if (book == null || selection == null || clues == null) return result;
            List<ShopInventory.Location> locations = ShopInventory.OwnedLocations(false);
            var byInventory = locations.ToDictionary(location => location.Inventory);
            var byKey = new Dictionary<string, ShopInventory.Location>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (ShopInventory.Location location in locations)
            {
                string key = location.Key();
                if (key == null) continue;
                if (byKey.ContainsKey(key)) duplicates.Add(key); else byKey.Add(key, location);
            }
            foreach (string duplicate in duplicates) byKey.Remove(duplicate);
            var chosen = new HashSet<GameItem>(selection.Where(item => item != null));
            var seen = new HashSet<GameItem>();
            var reserved = new WholeStackTransfer.Reservations();
            reserved.ExcludedStacks.UnionWith(chosen);
            void Skip(GameItem item, string reason, int quantity) =>
                result.Skipped.Add(new Skip { Item = item, Reason = reason, Quantity = quantity });
            foreach (GameItem item in selection)
            {
                if (item == null || !seen.Add(item)) continue;
                string reason = "f08.changed";
                if (!WholeStackTransfer.Ready) { Skip(item, "f08.pending", item.unitCount); continue; }
                if (!(item is GameItemElement element) || !VisibleInventoryViews.Visible(element) ||
                    !byInventory.TryGetValue(item.parentInventory, out ShopInventory.Location source) ||
                    !source.Inventory.childItems.Contains(item)) { Skip(item, reason, item.unitCount); continue; }
                if (HasSelectedAncestor(item, chosen)) { Skip(item, "f08.parent", item.unitCount); continue; }
                if (source.IsMachine || (!(source.Ordinary && ShopInventory.IsMainStockBranch(source)) &&
                    !ShopInventory.CanRouteFromCounter(item, source)))
                { Skip(item, "f08.special", item.unitCount); continue; }
                if (source.Key() != null && duplicates.Contains(source.Key()))
                { Skip(item, "f08.changed", item.unitCount); continue; }
                if (!(source.Inventory is GameGridInventory sourceGrid) || sourceGrid.GetType() != typeof(GameGridInventory) ||
                    !InventorySortPlan.SafeToCheck(sourceGrid, item) || !WholeStackTransfer.SafeSource(item))
                { Skip(item, "f08.unknown", item.unitCount); continue; }
                if (!ShopInventory.Movable(item))
                { Skip(item, "f08.locked", item.unitCount); continue; }
                if (item.MaxNumRemove() < item.unitCount)
                { Skip(item, "f08.partial", item.unitCount); continue; }
                ICollection<string> types = clues.KnownTypes(element);
                RoutingRule sourceRule = RuleFor(book, source);
                if (RoutingRules.Match(sourceRule, types) > 0)
                { Skip(item, "f08.already", item.unitCount); continue; }
                if (enforceShipping && sourceRule != null && !sourceRule.ship)
                { Skip(item, "f08.shipping_disabled", item.unitCount); continue; }
                var candidates = RoutingRules.Ranked(book, types).ToList();
                if (candidates.Count == 0)
                {
                    reason = !CustomerIndicators.HasOrdinaryTooltip(element) && (types == null || types.Count == 0)
                        ? "f08.unknown_type" : "f08.no_rule";
                    Skip(item, reason, item.unitCount);
                    continue;
                }
                int remaining = item.unitCount;
                reason = "f08.missing";
                // Fill matching existing stacks before placing the remaining instance.
                for (int pass = 0; pass < 2 && remaining > 0; pass++)
                foreach (RoutingRule rule in candidates)
                {
                    if (remaining == 0) break;
                    if (!byKey.TryGetValue(rule.location, out ShopInventory.Location target)) reason = "f08.missing";
                    else if (!target.Ordinary || target.IsMachine || !ShopInventory.IsMainStockBranch(target) ||
                        !(target.Inventory is GameGridInventory grid)) reason = "f08.special";
                    else if (ShopInventory.WouldContainSelf(item, target.Inventory)) reason = "f08.cycle";
                    else if (target.Inventory.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(target.Inventory)) reason = "f08.locked";
                    else
                    {
                        while (remaining > 0)
                        {
                            WholeStackTransfer.Plan transfer = pass == 0
                                ? WholeStackTransfer.StackUpTo(item, grid, reserved, remaining)
                                : WholeStackTransfer.EmptyRemainder(item, grid, reserved, remaining);
                            if (transfer == null)
                            {
                                reason = WholeStackTransfer.SafeTarget(grid) ? "f08.full" : "f08.unknown";
                                break;
                            }
                            result.Moves.Add(new Move { Transfer = transfer, Source = source, Target = target });
                            remaining -= transfer.Amount;
                        }
                    }
                }
                if (remaining > 0) Skip(item, reason, remaining);
            }
            return result;
        }

        private static RoutingRule RuleFor(RoutingBook book, ShopInventory.Location location)
        {
            string key = location.Key();
            return key == null ? null : book.rules.Find(rule => rule.location == key);
        }

        internal static bool AllowsTransfer(RoutingBook book, ShopInventory.Location source, ShopInventory.Location target,
            ICollection<string> publicTypes, bool enforceShipping = true)
        {
            if (book == null || source == null || target == null) return false;
            RoutingRule sourceRule = RuleFor(book, source);
            return RoutingRules.Match(sourceRule, publicTypes) == 0 &&
                (!enforceShipping || sourceRule == null || sourceRule.ship) &&
                RoutingRules.Match(RuleFor(book, target), publicTypes) > 0;
        }

        private static bool HasSelectedAncestor(GameItem item, HashSet<GameItem> chosen)
        {
            var seen = new HashSet<GameItem>();
            for (GameItem parent = item.parentInventory?.GetParentItem(); parent != null; parent = parent.parentInventory?.GetParentItem())
            {
                if (!seen.Add(parent) || chosen.Contains(parent)) return true;
            }
            return false;
        }

        internal bool SameAs(RoutingPlan other)
        {
            if (other == null || Moves.Count != other.Moves.Count || Skipped.Count != other.Skipped.Count) return false;
            for (int i = 0; i < Moves.Count; i++)
            {
                Move a = Moves[i], b = other.Moves[i];
                if (!ReferenceEquals(a.Source.Inventory, b.Source.Inventory) || !ReferenceEquals(a.Target.Inventory, b.Target.Inventory) ||
                    !WholeStackTransfer.Same(a.Transfer, b.Transfer)) return false;
            }
            for (int i = 0; i < Skipped.Count; i++)
                if (!ReferenceEquals(Skipped[i].Item, other.Skipped[i].Item) || Skipped[i].Reason != other.Skipped[i].Reason ||
                    Skipped[i].Quantity != other.Skipped[i].Quantity) return false;
            return true;
        }
    }
}
