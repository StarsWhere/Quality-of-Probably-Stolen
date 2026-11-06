using System;
using System.Linq;
using UnityEngine;

public sealed class YeastPageScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private float started = Time.realtimeSinceStartup;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static GameItem Item(string id, GameInventory grid)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing " + id); GeneralHelper.SetItemOwned(item);
        Move(item, grid); return item;
    }
    private static void Move(GameItem item, GameInventory grid, GridShape shape = null)
    {
        var marker = shape == null ? grid.TryFindOneValidInventorySlot(item) : ((GameGridInventory)grid).TryInventorySlot(item, item.unitCount, shape);
        Need(marker != null && marker.targetItem == null && marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid,
            "Could not place " + item.identifier);
    }
    private static GameGridInventory Ingredients(GameItem bottle) =>
        MachineryContainerUtils.Collect(((MachineryPanelRow)bottle.contentWindow.childElement).GetPanel("ingredients").content)
            .OfType<GameGridInventory>().Single();
    private static bool Same(GridShape a, GridShape b) => a.minX == b.minX && a.minY == b.minY &&
        a.orientation == b.orientation && a.flipped == b.flipped;

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup - started < 75, "Yeast page scenario timed out");
        if (!start.Tick()) return false;
        var e = EmporiumEntry.Instance;
        if (e.storeInteractables?.activeInHierarchy != true) return false;
        TradeScenario.SetOptions(true, "f37.enabled");
        var bay = Item("storage_bay", e.invElement);
        var kit = DirectoryMaster.Item("storage_bay_expansion_kit"); Need(kit != null && StorageExpansionHelper.CanExpand(bay), "Storage expansion unavailable");
        StorageExpansionHelper.Expand(bay, kit);
        var tabs = (bay.contentWindow.childElement as MachineryPanelRow)?.GetPanel("storage")?.content as MachineryTabs;
        Need(tabs != null && tabs.count >= 3, "Expanded storage pages unavailable");
        var pageOne = tabs.GetTabInventory(1) as GameGridInventory; var pageTwo = tabs.GetTabInventory(2) as GameGridInventory;
        Need(pageOne != null && pageTwo != null && pageOne != pageTwo, "Distinct native pages unavailable");
        var yeast = DirectoryMaster.Item("wine_yeast_infinite"); Need(yeast != null, "Reusable yeast unavailable"); GeneralHelper.SetItemOwned(yeast);
        tabs.Select(1, true);
        Move(yeast, pageOne, new GridShapeBuilder(yeast.modifiedShape).SetTransform(1, 1, false, 1));
        var firstBottle = Item("wine_bottle", e.invElement); var secondBottle = Item("wine_bottle", e.invElement);
        Move(yeast, Ingredients(firstBottle));
        tabs.Select(2, true);
        var secondPose = new GridShapeBuilder(yeast.modifiedShape).SetTransform(2, 1, false, 0);
        Move(yeast, pageTwo, secondPose);
        var ingredients = Ingredients(secondBottle); Item("wine_berry", ingredients);
        WaterHelper.AddWater(secondBottle, 2, 750000); Move(yeast, ingredients);
        Need(WineHelper.TryStartFermentation(secondBottle) && ReferenceEquals(yeast.parentInventory, pageTwo) &&
            pageTwo.childItems.Count(i => ReferenceEquals(i, yeast)) == 1 && !pageOne.childItems.Contains(yeast) && Same(yeast.modifiedShape, secondPose) &&
            secondBottle.GetTagReadonly(WineHelper.WINE_YEAST_POWER_INT).GetInt() == 2,
            "Changing bottle/source did not return the single yeast to the latest storage page and pose");
        result = "PASS F37 replaces the old bottle origin and returns the same yeast to its latest expanded-storage page and pose";
        return true;
    }
}
