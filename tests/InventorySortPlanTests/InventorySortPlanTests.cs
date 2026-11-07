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
    private static InventorySortPlan.ApplyResult Apply(InventorySortPlan plan, GameGridInventory grid, out InventoryArrangeTransaction transaction)
    {
        transaction = null;
        if (!plan.StillValid()) return InventorySortPlan.ApplyResult.Stale;
        transaction = InventoryArrangeTransaction.Capture(new[] { grid });
        if (transaction == null || !plan.CanApply()) return InventorySortPlan.ApplyResult.Unsupported;
        return transaction.Commit(plan.Placements.ToDictionary(p => p.Item,
            p => new InventoryArrangeTransaction.Destination { Grid = grid, Shape = p.After }));
    }
    private static void Main()
    {
        InventoryArrangeTransaction.GridScope = grid => grid.scope;
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
            "reserved items are excluded without touching native pins");
        var noSelection = InventorySortPlan.Create(grid, new HashSet<GameItem>(), new HashSet<GameItem> { unselected }, true, false);
        Check(noSelection != null && noSelection.Placements.Count == 1 && ReferenceEquals(noSelection.Placements[0].Item, moving) &&
            !noSelection.Placements[0].After.Intersects(anchored.modifiedShape) && !noSelection.Placements[0].After.Intersects(unselected.modifiedShape),
            "an empty selection arranges all eligible items around pinned and reserved obstacles");

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
        Check(Apply(grouped, groups, out _) == InventorySortPlan.ApplyResult.Applied && groups.validations == 0 &&
            a1.validations == 1 && a2.validations == 1 && b.validations == 1,
            "application rechecks planned placements before refreshing each changed item");
        Check(a1.modifiedShape.minX == 0 && a2.modifiedShape.minX == 1 && b.modifiedShape.minX == 0 && b.modifiedShape.minY == 1 &&
            ReferenceEquals(a1.parentInventory, groups) && ReferenceEquals(a2.parentInventory, groups),
            "application preserves every item's parent and applies the planned positions");

        var restoreGrid = Grid(3, 2);
        var restoreItem = Add(restoreGrid, "a", 2, 1);
        var restorePlan = InventorySortPlan.Create(restoreGrid, null, null, true, false);
        restoreItem.onValidate = () => { if (restoreItem.validations == 1) throw new InvalidOperationException("refresh failed"); };
        Check(Apply(restorePlan, restoreGrid, out var restoreTransaction) == InventorySortPlan.ApplyResult.Restored && restoreItem.modifiedShape.minX == 2 &&
            restoreItem.modifiedShape.minY == 1 && restoreItem.validations == 2 && restoreGrid.validations == 0 && restoreTransaction.LastError != null,
            "a refresh failure restores original positions and retains the cause");

        var partialGrid = Grid(4, 2);
        var earlier = Add(partialGrid, "a", 3, 1);
        var later = Add(partialGrid, "a", 2, 1);
        var partialPlan = InventorySortPlan.Create(partialGrid, null, null, true, false);
        later.onValidate = () => { if (later.validations == 1) throw new InvalidOperationException("second refresh failed"); };
        Check(Apply(partialPlan, partialGrid, out _) == InventorySortPlan.ApplyResult.Restored && earlier.modifiedShape.minX == 3 &&
            earlier.modifiedShape.minY == 1 && later.modifiedShape.minX == 2 && later.modifiedShape.minY == 1 &&
            earlier.validations == 2 && later.validations == 2,
            "a later item failure restores every earlier item as well as the failing item");

        var irregularGrid = Grid(4, 3);
        var container = Add(irregularGrid, "container", 0, 2);
        container.contentWindow = new object();
        container.movable = false;
        var irregular = Add(irregularGrid, "a", 2, 1);
        irregular.modifiedShape = new GridShapeBuilder(2, 2, 1, 0, 1, 1).SetPosition(2, 1);
        irregular.shape = irregular.modifiedShape.Clone();
        var plain = Add(irregularGrid, "b", 0, 1);
        var irregularPlan = InventorySortPlan.Create(irregularGrid, null, null, true, false);
        Check(irregularPlan != null && irregularPlan.Placements.Count == 2 &&
            !irregularPlan.Placements.Any(p => ReferenceEquals(p.Item, container)),
            "a pinned container remains a fixed obstacle");
        Check(Apply(irregularPlan, irregularGrid, out _) == InventorySortPlan.ApplyResult.Applied && container.modifiedShape.minX == 0 &&
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
        Check(Apply(hookedPlan, changedByHook, out _) == InventorySortPlan.ApplyResult.Unsupported && hookedItem.modifiedShape.minX == 2 &&
            hookedItem.modifiedShape.minY == 1 && hookedItem.validations == 0,
            "a native placement hook changing the snapshot is caught by the final recheck");

        grouped = InventorySortPlan.Create(groups, null, null, true, false);
        Add(groups, "new", 3, 1);
        Check(!grouped.StillValid() && Apply(grouped, groups, out _) == InventorySortPlan.ApplyResult.Stale,
            "an item added after planning invalidates the entire plan");

        grid.denyPlacements = true;
        Check(InventorySortPlan.Create(grid, selected, null, false, true) == null && moving.modifiedShape.minX == 3,
            "failure to form a full legal plan leaves the original layout intact");

        var undoGrid = Grid(4, 2);
        var undoItem = Add(undoGrid, "undo", 3, 1);
        var undoPlan = InventorySortPlan.Create(undoGrid, null, null, true, false);
        Check(Apply(undoPlan, undoGrid, out var undoTransaction) == InventorySortPlan.ApplyResult.Applied && ((IInventoryArrangeUndo)undoTransaction).CanUndo(),
            "a completed arrangement is available for undo");
        undoItem.unitCount++;
        Check(!((IInventoryArrangeUndo)undoTransaction).CanUndo() && ((IInventoryArrangeUndo)undoTransaction).Undo() == InventorySortPlan.ApplyResult.Stale && undoItem.modifiedShape.minX == 0,
            "a changed quantity prevents undo without changing the current layout");
        undoItem.unitCount--;
        undoItem.onValidate = () => { if (undoItem.validations == 2) throw new InvalidOperationException("undo refresh failed"); };
        Check(((IInventoryArrangeUndo)undoTransaction).Undo() == InventorySortPlan.ApplyResult.Restored && ((IInventoryArrangeUndo)undoTransaction).CanUndo() && undoItem.modifiedShape.minX == 0,
            "a failed undo restores and retains the post-arrangement layout");
        undoItem.onValidate = null;
        Check(((IInventoryArrangeUndo)undoTransaction).Undo() == InventorySortPlan.ApplyResult.Applied && !((IInventoryArrangeUndo)undoTransaction).CanUndo() &&
            undoItem.modifiedShape.minX == 3 && undoItem.modifiedShape.minY == 1,
            "undo restores the original arrangement once");

        var containerGrid = Grid(4, 2);
        var movableContainer = Add(containerGrid, "bag", 3, 1);
        movableContainer.contentWindow = new object();
        var includeContainers = InventorySortPlan.Create(containerGrid, null, null, true, false);
        InventoryArrangeTransaction containerTransaction = null;
        Check(includeContainers != null && Apply(includeContainers, containerGrid, out containerTransaction) == InventorySortPlan.ApplyResult.Applied && movableContainer.modifiedShape.minX == 0,
            "ordinary arrange can include a safe container");
        Check(((IInventoryArrangeUndo)containerTransaction).Undo() == InventorySortPlan.ApplyResult.Applied && movableContainer.modifiedShape.minX == 3,
            "a moved container returns to its original pose");

        var source = Grid(4, 2);
        var target = Grid(4, 2);
        var transfer = Add(source, "transfer", 3, 1);
        var resident = Add(target, "resident", 3, 1);
        resident.itemFeatures.Add(new ItemFeature { identifier = "quality", condition = new ItemCondition { value = 4 } });
        bool callbacksSafe = true;
        var transaction = InventoryArrangeTransaction.Capture(new[] { source, target },
            gridGuard: value => callbacksSafe, itemGuard: value => callbacksSafe, operationGuard: () => callbacksSafe);
        var destinations = new Dictionary<GameItem, InventoryArrangeTransaction.Destination>
        {
            [transfer] = new InventoryArrangeTransaction.Destination { Grid = target, Shape = new GridShapeBuilder(transfer.modifiedShape).SetPosition(0, 0).Build() },
            [resident] = new InventoryArrangeTransaction.Destination { Grid = target, Shape = new GridShapeBuilder(resident.modifiedShape).SetPosition(1, 0).Build() }
        };
        Check(transaction.Commit(destinations) == InventorySortPlan.ApplyResult.Applied && transfer.parentInventory == target && resident.modifiedShape.minX == 1,
            "a cross-grid transaction moves an intact instance and rearranges the target");
        var reversible = (IInventoryArrangeUndo)transaction;
        transfer.modifiedState.dict["lazy"] = new TagState { valueString = "" };
        Check(reversible.CanUndo(), "a lazily created disabled default tag does not invalidate undo");
        transfer.modifiedState.dict["lazy"].valueInt = 1;
        Check(!reversible.CanUndo(), "a disabled tag with a non-default value still invalidates undo");
        transfer.modifiedState.dict.Remove("lazy");
        callbacksSafe = false;
        Check(!reversible.CanUndo() && reversible.Undo() == InventorySortPlan.ApplyResult.Stale && transfer.parentInventory == target,
            "changed or unverified callbacks prevent undo before either grid is changed");
        callbacksSafe = true;
        source.scope = "moved";
        Check(!reversible.CanUndo(), "moving a source container to another scope invalidates undo");
        source.scope = "open";
        transfer.reserved = true;
        Check(!reversible.CanUndo(), "a new reservation invalidates undo");
        transfer.reserved = false;
        transfer.itemFeatures.Add(new ItemFeature { identifier = "fresh", value = 3, condition = new ItemCondition { value = 4 } });
        Check(!reversible.CanUndo(), "adding an item feature invalidates undo");
        transfer.itemFeatures.Clear();
        resident.itemFeatures[0].condition.value++;
        Check(!reversible.CanUndo(), "changing a nested item feature invalidates undo");
        resident.itemFeatures[0].condition.value--;
        transfer.state.dict["changed"] = new TagState { valueInt = 1 };
        Check(!reversible.CanUndo() && reversible.Undo() == InventorySortPlan.ApplyResult.Stale && transfer.parentInventory == target,
            "item state changes prevent undo without touching either grid");
        transfer.state.dict.Clear();
        source.onAccept = item => { source.onAccept = null; throw new InvalidOperationException("injected undo failure"); };
        Check(reversible.Undo() == InventorySortPlan.ApplyResult.Restored && reversible.CanUndo() &&
            transfer.parentInventory == target && resident.modifiedShape.minX == 1,
            "a rejected cross-grid undo restores the completed operation and remains retryable");
        Check(reversible.CanUndo() && reversible.Undo() == InventorySortPlan.ApplyResult.Applied &&
            transfer.parentInventory == source && transfer.modifiedShape.minX == 3 && resident.modifiedShape.minX == 3,
            "cross-grid undo restores both inventories and their original positions");

        var groupSource = Grid(5, 2);
        var groupTarget = Grid(5, 2);
        var nativeFirst = Add(groupSource, "first", 3, 1);
        var nativeSecond = Add(groupSource, "second", 4, 1);
        var group = InventoryArrangeTransaction.Capture(new[] { groupSource, groupTarget });
        new SlotMarker { inventory = groupTarget, item = nativeFirst, shape = new GridShapeBuilder(nativeFirst.modifiedShape).SetPosition(0, 0) }.TryAcceptOnce(1);
        Check(group.NativeMovedOnly(new HashSet<GameItem> { nativeFirst, nativeSecond }, groupTarget),
            "native placement of an intact group member is recognized before automatic insertion");
        var groupDestinations = new Dictionary<GameItem, InventoryArrangeTransaction.Destination>
        {
            [nativeSecond] = new InventoryArrangeTransaction.Destination { Grid = groupTarget, Shape = new GridShapeBuilder(nativeSecond.modifiedShape).SetPosition(1, 0).Build() }
        };
        Check(group.Commit(groupDestinations) == InventorySortPlan.ApplyResult.Applied &&
            ((IInventoryArrangeUndo)group).Undo() == InventorySortPlan.ApplyResult.Applied &&
            nativeFirst.parentInventory == groupSource && nativeSecond.parentInventory == groupSource &&
            nativeFirst.modifiedShape.minX == 3 && nativeSecond.modifiedShape.minX == 4,
            "one undo covers both a native group transfer and the later automatic transfer");

        group = InventoryArrangeTransaction.Capture(new[] { groupSource, groupTarget });
        new SlotMarker { inventory = groupTarget, item = nativeFirst, shape = new GridShapeBuilder(nativeFirst.modifiedShape).SetPosition(0, 0) }.TryAcceptOnce(1);
        groupTarget.onAccept = item => { groupTarget.onAccept = null; throw new InvalidOperationException("injected group insertion failure"); };
        Check(group.Commit(groupDestinations) == InventorySortPlan.ApplyResult.Restored &&
            nativeFirst.parentInventory == groupSource && nativeSecond.parentInventory == groupSource &&
            nativeFirst.modifiedShape.minX == 3 && nativeSecond.modifiedShape.minX == 4,
            "a failed helper also rolls back the earlier native partial group transfer");

        var failedSource = Grid(4, 2);
        var failedTarget = Grid(4, 2);
        var failedItem = Add(failedSource, "failed", 3, 1);
        var failedResident = Add(failedTarget, "resident", 3, 1);
        var failure = InventoryArrangeTransaction.Capture(new[] { failedSource, failedTarget });
        var failedDestinations = new Dictionary<GameItem, InventoryArrangeTransaction.Destination>
        {
            [failedItem] = new InventoryArrangeTransaction.Destination { Grid = failedTarget, Shape = new GridShapeBuilder(failedItem.modifiedShape).SetPosition(0, 0).Build() },
            [failedResident] = new InventoryArrangeTransaction.Destination { Grid = failedTarget, Shape = new GridShapeBuilder(failedResident.modifiedShape).SetPosition(1, 0).Build() }
        };
        failedTarget.onAccept = item => { failedTarget.onAccept = null; throw new InvalidOperationException("injected insertion failure"); };
        Check(failure.Commit(failedDestinations) == InventorySortPlan.ApplyResult.Restored && failedItem.parentInventory == failedSource &&
            failedItem.modifiedShape.minX == 3 && failedResident.modifiedShape.minX == 3,
            "an interrupted native insertion restores the complete starting layout");

        var third = Grid(4, 2);
        failure = InventoryArrangeTransaction.Capture(new[] { failedSource, failedTarget });
        failedTarget.onAccept = item =>
        {
            failedTarget.childItems.Remove(item); item.parentInventory = third; third.childItems.Add(item);
            throw new InvalidOperationException("injected third-party move");
        };
        Check(failure.Commit(failedDestinations) == InventorySortPlan.ApplyResult.Inspect && failedItem.parentInventory == third && third.childItems.Contains(failedItem),
            "recovery does not take an item from an unrelated inventory");
        Console.WriteLine($"Inventory sort: {assertions} assertions passed.");
    }
}
