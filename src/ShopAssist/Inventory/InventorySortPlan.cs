using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopAssist
{
    internal interface IInventoryArrangeUndo
    {
        IEnumerable<GameGridInventory> Grids { get; }
        bool Affects(GameGridInventory inventory);
        bool CanUndo();
        InventorySortPlan.ApplyResult Undo();
        Exception LastError { get; }
    }

    // A plan is entirely detached from the live grid. The game is asked whether
    // each candidate shape is legal, while already planned shapes form a shadow grid.
    internal sealed class InventorySortPlan
    {
        internal sealed class Placement
        {
            internal GameItem Item;
            internal GridShape Before, After;
            internal int Count;
        }

        private sealed class Snapshot
        {
            internal GameItem Item;
            internal GridShape Shape;
            internal int Count;
            internal string Identifier;
            internal string State;
            internal bool Movable;
        }

        private const int MaxDimension = 64, MaxItems = 256, MaxCandidates = 1000000;
        private readonly GameGridInventory grid;
        private readonly GridShape gridShape;
        private readonly List<Snapshot> snapshot;
        internal readonly List<Placement> Placements;
        private static string State(GameItem item) => InventoryArrangeTransaction.StateSignature(item);

        private InventorySortPlan(GameGridInventory grid, GridShape shape, List<Snapshot> snapshot, List<Placement> placements)
        {
            this.grid = grid;
            gridShape = shape;
            this.snapshot = snapshot;
            Placements = placements;
        }

        internal static InventorySortPlan Create(GameGridInventory grid, ISet<GameItem> selected, bool groupByItem, bool rotate)
        {
            if (grid?.inventoryShape == null || grid.inventoryShape.width < 1 || grid.inventoryShape.height < 1 ||
                grid.inventoryShape.width > MaxDimension || grid.inventoryShape.height > MaxDimension ||
                grid.IsInsertLocked() || grid.IsRemoveLocked() || !GeneralHelper.MayPlayerInsertInto(grid)) return null;
            List<GameItem> all = grid.childItems;
            if (all == null || all.Count > MaxItems || all.Any(item => item?.modifiedShape == null ||
                item.modifiedShape.width < 1 || item.modifiedShape.height < 1 ||
                item.modifiedShape.width > MaxDimension || item.modifiedShape.height > MaxDimension)) return null;
            var snapshot = all.Select(item => new Snapshot { Item = item, Shape = item.modifiedShape.Clone(), Count = item.unitCount,
                Identifier = item.identifier, Movable = InventoryArrangeTransaction.SafelyMovable(grid, item), State = State(item) }).ToList();
            if (snapshot.Any(item => item.State == null)) return null;
            var order = new Dictionary<GameItem, int>();
            for (int i = 0; i < all.Count; i++)
            {
                if (order.ContainsKey(all[i])) return null;
                order.Add(all[i], i);
            }
            bool limited = selected != null && selected.Count > 0;
            var moving = all.Where(item => (!limited || selected.Contains(item)) && SafeToCheck(grid, item) &&
                ShopInventory.Movable(item) && item.MaxNumRemove() >= item.unitCount).ToList();
            if (moving.Count == 0) return null;
            int Area(GameItem item)
            {
                int area = 0;
                for (int y = 0; y < item.modifiedShape.height; y++)
                    for (int x = 0; x < item.modifiedShape.width; x++)
                        if (item.modifiedShape.GetLocal(x, y) != 0) area++;
                return area;
            }
            moving = groupByItem
                ? moving.OrderBy(item => item.identifier ?? "", StringComparer.Ordinal).ThenByDescending(Area).ThenBy(item => order[item]).ToList()
                : moving.OrderByDescending(Area).ThenBy(item => item.identifier ?? "", StringComparer.Ordinal).ThenBy(item => order[item]).ToList();
            var placements = new List<Placement>();
            int candidates = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (GameItem item in moving)
            {
                GridShape chosen = null;
                long bestScore = long.MaxValue;
                Placement previous = placements.LastOrDefault();
                bool sameKind = previous != null && previous.Item.identifier == item.identifier;
                int preferredRow = previous == null ? 0 : sameKind ? previous.After.minY : placements.Max(p => p.After.maxY) + 1;
                var pose = new GridShapeBuilder(item.modifiedShape);
                for (int turn = 0; turn < (rotate ? 4 : 1); turn++)
                {
                    pose.SetOrientation((item.modifiedShape.orientation + turn) % 4);
                    for (int y = 0; y < grid.inventoryShape.height; y++)
                    for (int x = 0; x < grid.inventoryShape.width; x++)
                    {
                        if (++candidates > MaxCandidates || clock.ElapsedMilliseconds > 150) return null;
                        pose.SetPosition(x, y);
                        if (!grid.CheckShapePlacement(item, pose, moving)) continue;
                        long score = y * grid.inventoryShape.width + x;
                        if (groupByItem)
                        {
                            score += Math.Abs(y - preferredRow) * 4096L;
                            if (sameKind && pose.orientation != previous.After.orientation) score += 2048;
                            if (sameKind && y == preferredRow && x < previous.After.minX) score += 1024;
                        }
                        if (score >= bestScore) continue;
                        bool occupied = false;
                        foreach (Placement prior in placements)
                            if (((GridShape)pose).Intersects(prior.After)) { occupied = true; break; }
                        if (!occupied)
                        {
                            bestScore = score;
                            chosen = pose.Build();
                        }
                    }
                }
                if (chosen == null) return null;
                placements.Add(new Placement { Item = item, Before = item.modifiedShape.Clone(), After = chosen, Count = item.unitCount });
            }
            return new InventorySortPlan(grid, grid.inventoryShape.Clone(), snapshot, placements);
        }

        internal bool StillValid()
        {
            if (grid?.inventoryShape == null || grid.IsInsertLocked() || grid.IsRemoveLocked() ||
                !GeneralHelper.MayPlayerInsertInto(grid) || !SameShape(gridShape, grid.inventoryShape)) return false;
            List<GameItem> all = grid.childItems;
            if (all == null || all.Count != snapshot.Count) return false;
            var current = new HashSet<GameItem>(all);
            if (current.Count != all.Count) return false;
            foreach (Snapshot entry in snapshot)
                if (!current.Contains(entry.Item) || !ReferenceEquals(entry.Item.parentInventory, grid) ||
                    entry.Item.unitCount != entry.Count || entry.Item.identifier != entry.Identifier ||
                    State(entry.Item) != entry.State || InventoryArrangeTransaction.SafelyMovable(grid, entry.Item) != entry.Movable ||
                    !SameShape(entry.Shape, entry.Item.modifiedShape)) return false;
            var moving = Placements.Select(entry => entry.Item).ToList();
            for (int i = 0; i < Placements.Count; i++)
            {
                Placement entry = Placements[i];
                if (!SafeToCheck(grid, entry.Item) || !ShopInventory.Movable(entry.Item) || entry.Item.MaxNumRemove() < entry.Count ||
                    !grid.CheckShapePlacement(entry.Item, entry.After, moving)) return false;
                for (int j = 0; j < i; j++) if (entry.After.Intersects(Placements[j].After)) return false;
            }
            return true;
        }

        // The native placement and removal checks invoke these delegates. An
        // unknown delegate is not run merely to build a preview.
        internal static bool SafeToCheck(GameGridInventory grid, GameItem item) =>
            grid.isItemStateValidFunc == null && item.isItemStateValidFunc == null &&
            grid.mayInventoryRemoveItemFunc == null && item.mayInventoryRemoveItemFunc == null &&
            grid.minSlotAmountItemFunc == null && item.minSlotAmountItemFunc == null &&
            grid.maxSlotRemoveItemFunc == null && item.maxSlotRemoveItemFunc == null &&
            grid.parentItemShapeModifierForItemFunc == null && item.parentItemShapeModifierForItemFunc == null;

        internal enum ApplyResult { Applied, Stale, Unsupported, Restored, Inspect }

        internal bool CanApply()
        {
            return StillValid() && SafeForRefresh();
        }

        internal bool SafeForRefresh()
        {
            if (grid.GetType() != typeof(GameGridInventory) ||
                grid.parentItemStateModifierForItemFunc != null || grid.parentItemSpriteModifierForItemFunc != null) return false;
            // Validate refreshes every item in the grid; do not invoke unknown
            // position/sprite callbacks, descendants or custom Validate overrides.
            foreach (Placement entry in Placements)
            {
                GameItem item = entry.Item;
                if (item.GetType() != typeof(GameItemElement) ||
                    item.parentItemShapeModifierForItemFunc != null || item.parentItemStateModifierForItemFunc != null ||
                    item.parentItemSpriteModifierForItemFunc != null || item.getItemSpriteModifierForItemFunc != null ||
                    item.isItemStateValidFunc != null || item.modifiedXOrigin != 0 || item.modifiedYOrigin != 0 ||
                    !SameCells(item.shape, item.modifiedShape)) return false;
            }
            return true;
        }

        private static bool SameCells(GridShape a, GridShape b)
        {
            if (a == null || b == null || a.width != b.width || a.height != b.height ||
                a.GetOutsideBounds() != b.GetOutsideBounds()) return false;
            for (int y = 0; y < a.height; y++)
                for (int x = 0; x < a.width; x++)
                    if (a.GetLocal(x, y) != b.GetLocal(x, y)) return false;
            return true;
        }

        internal static bool SameShape(GridShape a, GridShape b)
        {
            if (a == null || b == null || a.width != b.width || a.height != b.height || a.minX != b.minX ||
                a.minY != b.minY || a.orientation != b.orientation || a.flipped != b.flipped ||
                a.GetOutsideBounds() != b.GetOutsideBounds()) return false;
            for (int y = 0; y < a.height; y++)
                for (int x = 0; x < a.width; x++)
                    if (a.GetLocal(x, y) != b.GetLocal(x, y)) return false;
            return true;
        }
    }
}
