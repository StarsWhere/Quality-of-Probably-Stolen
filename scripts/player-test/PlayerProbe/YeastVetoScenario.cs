using System;
using System.Linq;
using UnityEngine;

// Uses the same native-grid veto fixture as RoutingVetoScenario in an isolated run.
public sealed class YeastVetoScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private float started = Time.realtimeSinceStartup;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static GameItem Put(string id, GameInventory grid, GridShape shape = null)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing " + id); GeneralHelper.SetItemOwned(item);
        var marker = shape == null ? grid.TryFindOneValidInventorySlot(item) : ((GameGridInventory)grid).TryInventorySlot(item, item.unitCount, shape);
        Need(marker != null && marker.targetItem == null && marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid,
            "Could not place " + id);
        return item;
    }
    private static bool Same(GridShape a, GridShape b) => a.minX == b.minX && a.minY == b.minY &&
        a.orientation == b.orientation && a.flipped == b.flipped;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - started < 75, "Yeast veto scenario timed out");
        if (!start.Tick()) return false;
        var e = EmporiumEntry.Instance;
        if (e.storeInteractables?.activeInHierarchy != true) return false;
        TradeScenario.SetOptions(true, "f37.enabled");
        var bag = Put("backpack_medium", e.invElement); var home = (GameGridInventory)bag.contentWindow.childElement;
        var yeast = DirectoryMaster.Item("wine_yeast_infinite"); Need(yeast != null, "Reusable yeast unavailable");
        GeneralHelper.SetItemOwned(yeast);
        var homePose = new GridShapeBuilder(yeast.modifiedShape).SetTransform(3, 3, false, 1);
        var homeMarker = home.TryInventorySlot(yeast, yeast.unitCount, homePose);
        Need(homeMarker != null && homeMarker.TryAcceptOnce(yeast.unitCount) == yeast.unitCount, "Could not place origin yeast");
        var bottle = Put("wine_bottle", e.invElement); WaterHelper.AddWater(bottle, 2, 750000);
        var row = bottle.contentWindow?.childElement as MachineryPanelRow;
        var panel = row?.GetPanel("ingredients"); Need(panel != null, "Native bottle ingredients panel unavailable");
        var grid = new RefusingGrid(6, 2); panel.SetContent(grid);
        Need(ReferenceEquals(grid.GetParentItem(), bottle) && MachineryContainerUtils.Collect(panel.content).Single() == grid,
            "Veto grid did not attach to the real bottle");
        var slot = grid.TryInventorySlot(yeast, yeast.unitCount, new GridShapeBuilder(yeast.modifiedShape).SetTransform(0, 0, false, 0));
        Need(slot != null && slot.targetItem == null && slot.TryAcceptOnce(yeast.unitCount) == yeast.unitCount && yeast.parentInventory == grid,
            "Could not load reusable yeast");
        var berry = Put("wine_berry", grid);
        GridShape before = yeast.modifiedShape.Clone(); int water = WaterHelper.GetCurrentCapacityUL(bottle);
        grid.refuse = true;
        Need(!WineHelper.TryStartFermentation(bottle) && grid.refusals == 1 && yeast.parentInventory == grid &&
            grid.childItems.Count(i => ReferenceEquals(i, yeast)) == 1 && Same(yeast.modifiedShape, before) &&
            berry.parentInventory == grid && WaterHelper.GetCurrentCapacityUL(bottle) == water &&
            bottle.GetTagReadonly(WineHelper.WINE_CONTAINER_STATE).GetInt() == 0,
            "Vetoed return changed the yeast pose, ingredients, water or fermentation state");
        grid.refuse = false;
        Need(WineHelper.TryStartFermentation(bottle) && yeast.parentInventory == home && Same(yeast.modifiedShape, homePose) &&
            home.childItems.Count(i => ReferenceEquals(i, yeast)) == 1 &&
            bottle.GetTagReadonly(WineHelper.WINE_CONTAINER_STATE).GetInt() == 1 &&
            bottle.GetTagReadonly(WineHelper.WINE_YEAST_POWER_INT).GetInt() == 2,
            "Retry did not return the same yeast to its exact origin and start the native recipe");
        result = "PASS F37 rejected Expel restores the single yeast's pose and ingredients, then a native retry returns it to its origin";
        return true;
    }
}
