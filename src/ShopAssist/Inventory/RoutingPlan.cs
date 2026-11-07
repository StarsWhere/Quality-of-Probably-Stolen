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
        internal sealed class Skip { internal GameItem Item; internal string Reason; }

        internal readonly List<Move> Moves = new List<Move>();
        internal readonly List<Skip> Skipped = new List<Skip>();

        internal static RoutingPlan Create(RoutingBook book, IReadOnlyList<GameItem> selection, CustomerIndicators clues)
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
            foreach (GameItem item in selection)
            {
                if (item == null || !seen.Add(item)) continue;
                string reason = "f08.changed";
                if (!WholeStackTransfer.Ready) { result.Skipped.Add(new Skip { Item = item, Reason = "f08.pending" }); continue; }
                if (!(item is GameItemElement element) || !VisibleInventoryViews.Visible(element) ||
                    !byInventory.TryGetValue(item.parentInventory, out ShopInventory.Location source) ||
                    !source.Inventory.childItems.Contains(item)) { result.Skipped.Add(new Skip { Item = item, Reason = reason }); continue; }
                if (HasSelectedAncestor(item, chosen)) { result.Skipped.Add(new Skip { Item = item, Reason = "f08.parent" }); continue; }
                if (source.IsMachine || (!(source.Ordinary && ShopInventory.IsMainStockBranch(source)) &&
                    !ShopInventory.CanRouteFromCounter(item, source)))
                { result.Skipped.Add(new Skip { Item = item, Reason = "f08.special" }); continue; }
                if (!(source.Inventory is GameGridInventory sourceGrid) || sourceGrid.GetType() != typeof(GameGridInventory) ||
                    !InventorySortPlan.SafeToCheck(sourceGrid, item) || !WholeStackTransfer.SafeSource(item))
                { result.Skipped.Add(new Skip { Item = item, Reason = "f08.unknown" }); continue; }
                if (!ShopInventory.Movable(item) || RequestBook.Reserved(item))
                { result.Skipped.Add(new Skip { Item = item, Reason = "f08.locked" }); continue; }
                if (item.MaxNumRemove() < item.unitCount)
                { result.Skipped.Add(new Skip { Item = item, Reason = "f08.partial" }); continue; }
                ICollection<string> types = clues.KnownTypes(element);
                bool complete = CustomerIndicators.HasOrdinaryTooltip(element);
                var candidates = RoutingRules.Ranked(book, item.identifier, types, complete).ToList();
                if (candidates.Count == 0) { result.Skipped.Add(new Skip { Item = item, Reason = NoRuleReason(book, item.identifier, types, complete) }); continue; }
                Move move = null;
                reason = "f08.missing";
                foreach (RoutingRule rule in candidates)
                {
                    if (!byKey.TryGetValue(rule.location, out ShopInventory.Location target)) reason = "f08.missing";
                    else if (!target.Ordinary || target.IsMachine || !ShopInventory.IsMainStockBranch(target) ||
                        !(target.Inventory is GameGridInventory grid)) reason = "f08.special";
                    else if (target.Inventory == source.Inventory)
                    {
                        // A matching destination is already satisfied. Do not send the
                        // item to a lower-ranked fallback on the next routing pass.
                        reason = "f08.already";
                        break;
                    }
                    else if (ShopInventory.WouldContainSelf(item, target.Inventory)) reason = "f08.cycle";
                    else if (target.Inventory.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(target.Inventory)) reason = "f08.locked";
                    else
                    {
                        WholeStackTransfer.Plan transfer = WholeStackTransfer.Stack(item, grid, reserved);
                        if (transfer == null) transfer = WholeStackTransfer.Empty(item, grid, reserved);
                        if (transfer == null)
                            reason = WholeStackTransfer.SafeTarget(grid) ? "f08.full" : "f08.unknown";
                        else
                        {
                            move = new Move { Transfer = transfer, Source = source, Target = target };
                            break;
                        }
                    }
                    if (rule.exclusive) break;
                }
                if (move == null) result.Skipped.Add(new Skip { Item = item, Reason = reason });
                else result.Moves.Add(move);
            }
            return result;
        }

        private static string NoRuleReason(RoutingBook book, string id, ICollection<string> types, bool complete)
        {
            bool excluded = false, unknown = false;
            foreach (RoutingRule rule in book.rules.Where(value => value.receive))
            {
                bool itemMatch = rule.itemIds.Contains(id);
                bool categoryMatch = types != null && types.Any(rule.categories.Contains);
                bool possible = itemMatch || categoryMatch || !complete && rule.categories.Count != 0;
                if (!possible) continue;
                bool blocked = rule.excludedIds.Contains(id) || types != null && types.Any(rule.excludedCategories.Contains);
                if (blocked) excluded = true;
                else if (!complete && rule.excludedCategories.Count != 0) unknown = true;
            }
            return unknown ? "f08.unknown_type" : excluded ? "f08.excluded" : "f08.no_rule";
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
                if (!ReferenceEquals(Skipped[i].Item, other.Skipped[i].Item) || Skipped[i].Reason != other.Skipped[i].Reason) return false;
            return true;
        }
    }
}
