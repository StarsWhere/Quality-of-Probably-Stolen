using System;
using System.Collections.Generic;
using System.Linq;
using ShopAssist;

internal static class InventorySortPlanTests
{
    private static int assertions;
    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new Exception(message);
    }
    private static GameGridInventory Grid(int width, int height) => new GameGridInventory
    { inventoryShape = new GridShapeBuilder(width, height) };
    private static GameItem Add(GameGridInventory grid, string id, int x, int y, bool movable = true)
    {
        var item = new GameItemElement { identifier = id, parentInventory = grid, movable = movable,
            modifiedShape = new GridShapeBuilder(1, 1, 1).SetPosition(x, y) };
        item.shape = item.modifiedShape.Clone();
        grid.childItems.Add(item);
        return item;
    }
    private static void Main()
    {
        var grid = Grid(4, 3);
        var anchored = Add(grid, "pin", 0, 0, false);
        var moving = Add(grid, "move", 3, 0);
        var unselected = Add(grid, "other", 0, 2);
        var selected = new HashSet<GameItem> { moving };
        var plan = InventorySortPlan.Create(grid, selected, new HashSet<GameItem>(), true, false);
        Check(plan != null && plan.Placements.Count == 1 && ReferenceEquals(plan.Placements[0].Item, moving),
            "only selected movable items enter the plan");
        Check(plan.Placements[0].After.minX == 1 && plan.Placements[0].After.minY == 0,
            "pinned and unselected items remain obstacles");
        Check(moving.modifiedShape.minX == 3 && anchored.modifiedShape.minX == 0 && unselected.modifiedShape.minY == 2,
            "planning never changes the live item shapes");
        Check(plan.StillValid(), "unchanged plan remains valid");
        unselected.unitCount++;
        Check(!plan.StillValid(), "changes to an unselected obstacle invalidate the snapshot");
        unselected.unitCount--;
        moving.movable = false;
        Check(!plan.StillValid(), "a newly pinned or locked item invalidates the plan");
        moving.movable = true;
        Check(InventorySortPlan.Create(grid, selected, new HashSet<GameItem> { moving }, true, false) == null,
            "temporary holds exclude the same instance without touching the native pin");

        var groups = Grid(5, 2);
        var a1 = Add(groups, "a", 4, 1);
        var b = Add(groups, "b", 2, 1);
        var a2 = Add(groups, "a", 0, 1);
        GridShapeBuilder.intersectionChecks = 0;
        var grouped = InventorySortPlan.Create(groups, null, null, true, false);
        Check(groups.placementChecks == groups.childItems.Count * groups.inventoryShape.width * groups.inventoryShape.height &&
            GridShapeBuilder.intersectionChecks < 15,
            "all native candidate checks run while worse scores skip shadow collision checks");
        Check(grouped != null && grouped.Placements.Select(p => p.Item.identifier).SequenceEqual(new[] { "a", "a", "b" }),
            "identical items are planned together");
        Check(grouped.Placements[0].After.minX == 0 && grouped.Placements[1].After.minX == 1 &&
            grouped.Placements[0].After.minY == 0 && grouped.Placements[1].After.minY == 0 &&
            grouped.Placements[2].After.minX == 0 && grouped.Placements[2].After.minY == 1,
            "identical goods form a row and a different kind begins a new row");
        Check(a1.modifiedShape.minX == 4 && a2.modifiedShape.minY == 1 && b.modifiedShape.minY == 1,
            "planning leaves the live grid untouched");
        Check(grouped.CanApply(), "an ordinary unchanged plan can be applied");
        groups.placementChecks = 0;
        Check(grouped.Apply() == InventorySortPlan.ApplyResult.Applied && groups.validations == 0 &&
            groups.placementChecks == 2 * grouped.Placements.Count &&
            a1.validations == 1 && a2.validations == 1 && b.validations == 1,
            "application rechecks planned placements before refreshing each changed item");
        Check(a1.modifiedShape.minX == 0 && a2.modifiedShape.minX == 1 && b.modifiedShape.minX == 0 && b.modifiedShape.minY == 1 &&
            ReferenceEquals(a1.parentInventory, groups) && ReferenceEquals(a2.parentInventory, groups),
            "application preserves every item's parent and applies the planned positions");

        var restoreGrid = Grid(3, 2);
        var restoreItem = Add(restoreGrid, "a", 2, 1);
        var restorePlan = InventorySortPlan.Create(restoreGrid, null, null, true, false);
        restoreItem.onValidate = () => { if (restoreItem.validations == 1) throw new InvalidOperationException("refresh failed"); };
        Check(restorePlan.Apply() == InventorySortPlan.ApplyResult.Restored && restoreItem.modifiedShape.minX == 2 &&
            restoreItem.modifiedShape.minY == 1 && restoreItem.validations == 2 && restoreGrid.validations == 0 && restorePlan.LastError != null,
            "a refresh failure restores original positions and retains the cause");

        var partialGrid = Grid(4, 2);
        var earlier = Add(partialGrid, "a", 3, 1);
        var later = Add(partialGrid, "a", 2, 1);
        var partialPlan = InventorySortPlan.Create(partialGrid, null, null, true, false);
        later.onValidate = () => { if (later.validations == 1) throw new InvalidOperationException("second refresh failed"); };
        Check(partialPlan.Apply() == InventorySortPlan.ApplyResult.Restored && earlier.modifiedShape.minX == 3 &&
            earlier.modifiedShape.minY == 1 && later.modifiedShape.minX == 2 && later.modifiedShape.minY == 1 &&
            earlier.validations == 2 && later.validations == 2,
            "a later item failure restores every earlier item as well as the failing item");

        var irregularGrid = Grid(4, 3);
        var container = Add(irregularGrid, "container", 0, 2);
        container.contentWindow = new object();
        var irregular = Add(irregularGrid, "a", 2, 1);
        irregular.modifiedShape = new GridShapeBuilder(2, 2, 1, 0, 1, 1).SetPosition(2, 1);
        irregular.shape = irregular.modifiedShape.Clone();
        var plain = Add(irregularGrid, "b", 0, 1);
        var irregularPlan = InventorySortPlan.Create(irregularGrid, null, null, true, false);
        Check(irregularPlan != null && irregularPlan.Placements.Count == 2 &&
            !irregularPlan.Placements.Any(p => ReferenceEquals(p.Item, container)),
            "containers remain fixed obstacles even when their item is otherwise movable");
        Check(irregularPlan.Apply() == InventorySortPlan.ApplyResult.Applied && container.modifiedShape.minX == 0 &&
            container.modifiedShape.minY == 2 && container.validations == 0 && irregular.modifiedShape.GetLocal(1, 0) == 0 &&
            irregular.modifiedShape.GetLocal(0, 1) == 1 && !irregular.modifiedShape.Intersects(plain.modifiedShape) &&
            !irregular.modifiedShape.Intersects(container.modifiedShape),
            "an irregular footprint is preserved and never overlaps the fixed container or another item");

        var unknown = Grid(3, 2);
        var unknownItem = Add(unknown, "a", 2, 1);
        unknownItem.isItemStateValidFunc = new object();
        Check(InventorySortPlan.Create(unknown, null, null, true, false) == null && unknown.validations == 0,
            "unknown placement callbacks are not invoked while planning");

        var changedByHook = Grid(3, 2);
        var hookedItem = Add(changedByHook, "hooked", 2, 1);
        var hookedPlan = InventorySortPlan.Create(changedByHook, null, null, true, false);
        changedByHook.onPlacementCheck = () => hookedItem.unitCount = 2;
        Check(hookedPlan.Apply() == InventorySortPlan.ApplyResult.Unsupported && hookedItem.modifiedShape.minX == 2 &&
            hookedItem.modifiedShape.minY == 1 && hookedItem.validations == 0,
            "a native placement hook changing the snapshot is caught by the final recheck");

        grouped = InventorySortPlan.Create(groups, null, null, true, false);
        Add(groups, "new", 3, 1);
        Check(!grouped.StillValid() && grouped.Apply() == InventorySortPlan.ApplyResult.Stale,
            "an item added after planning invalidates the entire plan");

        grid.denyPlacements = true;
        Check(InventorySortPlan.Create(grid, selected, null, false, true) == null && moving.modifiedShape.minX == 3,
            "failure to form a full legal plan leaves the original layout intact");
        Console.WriteLine($"Inventory sort: {assertions} assertions passed.");
    }
}
