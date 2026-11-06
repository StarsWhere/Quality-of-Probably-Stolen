using System;
using System.IO;
using System.Linq;
using UnityEngine;

public sealed class YeastOriginScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private int step;
    public bool Tick(out string result)
    {
        result = null;
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance;
        if (e.storeInteractables?.activeInHierarchy != true) return false;
        TradeScenario.SetOptions(true, "f37.enabled");
        var bag = Native("backpack_medium", e.invElement);
        var home = (GameGridInventory)bag.contentWindow.childElement;
        var yeast = DirectoryMaster.Item("wine_yeast_infinite"); GeneralHelper.SetItemOwned(yeast);
        var shape = new GridShapeBuilder(yeast.modifiedShape).SetTransform(2, 3, false, 1);
        var marker = home.TryInventorySlot(yeast, yeast.unitCount, shape);
        if (marker == null || marker.TryAcceptOnce() != 1) throw new InvalidOperationException("Cannot set up yeast origin");
        var bottle = Native("wine_bottle", e.invElement); WaterHelper.AddWater(bottle, 2, 750000);
        var socket = MachineryContainerUtils.Collect(((MachineryPanelRow)bottle.contentWindow.childElement).GetPanel("ingredients").content)
            .OfType<GameGridInventory>().Single();
        Native("wine_berry", socket);
        var to = socket.TryFindOneValidInventorySlot(yeast);
        if (to == null || to.TryAcceptOnce() != 1) throw new InvalidOperationException("Cannot insert reusable yeast");
        var p = PlayerStore.Instance;
        string context = string.Join("|", p.runID, p.saveSlotId, bag.GetUniqueID(), bottle.GetUniqueID(), yeast.GetUniqueID());
        File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-yeast-origin.txt"), context);
        p.SaveGame(); result = "PASS saved unfermented bottle containing reusable yeast originally from rotated bag slot"; return true;
    }
    private static GameItem Native(string id, GameInventory grid)
    {
        var item = DirectoryMaster.Item(id); GeneralHelper.SetItemOwned(item);
        var m = grid.TryFindOneValidInventorySlot(item);
        if (m == null || m.targetItem != null || m.TryAcceptOnce() != 1 || item.parentInventory != grid) throw new InvalidOperationException("Fixture " + id);
        return item;
    }
}

public sealed class YeastColdBrewScenario
{
    private QaLoadSlot load;
    private string[] data;
    public bool Tick(out string result)
    {
        result = null;
        if (load == null)
        {
            data = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-yeast-origin.txt")).Trim().Split('|');
            load = new QaLoadSlot(int.Parse(data[1]), Guid.Parse(data[0]));
        }
        if (!load.Tick()) return false;
        var root = EmporiumEntry.Instance.invElement;
        var bag = root.childItems.Single(i => i.uniqueId == int.Parse(data[2]));
        var bottle = root.childItems.Single(i => i.uniqueId == int.Parse(data[3]));
        var yeast = GraphUtils.FindAllChildrenType<GameItem>(bottle).Single(i => i.uniqueId == int.Parse(data[4]));
        if (!WineHelper.TryStartFermentation(bottle) || yeast.parentInventory != bag.contentWindow.childElement ||
            yeast.modifiedShape.minX != 2 || yeast.modifiedShape.minY != 3 || yeast.modifiedShape.orientation != 1 ||
            yeast.modifiedShape.flipped || bottle.GetTagReadonly(WineHelper.WINE_YEAST_POWER_INT).GetInt() != 2)
            throw new InvalidOperationException("Persisted yeast origin did not survive native reload and brewing");
        result = "PASS cold-loaded unfermented bottle returns the original reusable yeast to its saved backpack, cell and orientation"; return true;
    }
}
