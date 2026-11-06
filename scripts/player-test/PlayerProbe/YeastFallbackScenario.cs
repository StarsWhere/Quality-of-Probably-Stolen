using System;
using System.Linq;
using UnityEngine;

public sealed class YeastFallbackScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private float started = Time.realtimeSinceStartup;
    [Serializable] private sealed class Origins { public Origin[] entries = Array.Empty<Origin>(); }
    [Serializable] private sealed class Origin { public int yeast = -1, bottle = -1; }
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static GameItem Item(string id, GameInventory grid, GridShape shape = null)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing " + id); GeneralHelper.SetItemOwned(item);
        Move(item, grid, shape); return item;
    }
    private static void Move(GameItem item, GameInventory grid, GridShape shape = null)
    {
        var slot = shape == null ? grid.TryFindOneValidInventorySlot(item) : ((GameGridInventory)grid).TryInventorySlot(item, item.unitCount, shape);
        Need(slot != null && slot.targetItem == null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid,
            "Could not place " + item.identifier);
    }
    private static GameGridInventory Ingredients(GameItem bottle) =>
        MachineryContainerUtils.Collect(((MachineryPanelRow)bottle.contentWindow.childElement).GetPanel("ingredients").content)
            .OfType<GameGridInventory>().Single();
    private static Origins ReadOrigins()
    {
        string raw = ModHelper.GetModData(Mod, "yeastOrigins", null);
        var book = raw == null ? null : JsonUtility.FromJson<Origins>(raw);
        Need(book?.entries != null, "Origin records unavailable: " + raw); return book;
    }

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup - started < 75, "Yeast fallback scenario timed out");
        if (!start.Tick()) return false;
        var e = EmporiumEntry.Instance;
        if (e.storeInteractables?.activeInHierarchy != true) return false;
        TradeScenario.SetOptions(true, "f37.enabled");
        var bag = Item("backpack_medium", e.invElement); var home = (GameGridInventory)bag.contentWindow.childElement;
        var first = Item("wine_yeast_infinite", home); GridShape firstPose = first.modifiedShape.Clone();
        var second = DirectoryMaster.Item("wine_yeast_infinite"); Need(second != null, "Second yeast unavailable");
        GeneralHelper.SetItemOwned(second); Move(second, home, new GridShapeBuilder(second.modifiedShape).SetTransform(3, 3, false, 0));
        GridShape secondPose = second.modifiedShape.Clone();
        var firstBottle = Item("wine_bottle", e.invElement); var secondBottle = Item("wine_bottle", e.invElement);
        var firstGrid = Ingredients(firstBottle); var secondGrid = Ingredients(secondBottle);
        Item("wine_berry", firstGrid); Item("wine_berry", secondGrid);
        WaterHelper.AddWater(firstBottle, 2, 750000); WaterHelper.AddWater(secondBottle, 2, 750000);
        Move(first, firstGrid); Move(second, secondGrid);
        int firstId = first.uniqueId, secondId = second.uniqueId;
        Need(firstId > 0 && secondId > 0 && ReadOrigins().entries.Any(o => o.yeast == firstId) &&
            ReadOrigins().entries.Any(o => o.yeast == secondId), "Both bottle origins were not recorded");
        Item("wine_yeast_infinite", home, firstPose);
        var roots = new GameInventory[] { e.invElement, e.backInvinvElement, e.showcaseElement, e.frontInvinvElement };
        bool[] locks = roots.Select(g => g.overrideLockInsert).ToArray();
        try
        {
            foreach (var root in roots) root.overrideLockInsert = true;
            Need(!WineHelper.TryStartFermentation(firstBottle) && GenericConfirmationUIManager.Instance.panel.activeSelf &&
                first.parentInventory == firstGrid && firstBottle.GetTagReadonly(WineHelper.WINE_CONTAINER_STATE).GetInt() == 0,
                "Full destinations did not pause brewing before consumption");
        }
        finally { for (int i = 0; i < roots.Length; i++) roots[i].overrideLockInsert = locks[i]; }
        GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
        Need(firstBottle.GetTagReadonly(WineHelper.WINE_CONTAINER_STATE).GetInt() == 1 && first.parentInventory != null,
            "Explicit native fallback did not complete after space was restored");
        var remaining = ReadOrigins().entries;
        Need(!remaining.Any(o => o.yeast == firstId) && remaining.Count(o => o.yeast == secondId) == 1,
            "Native fallback left a completed origin or removed an unrelated bottle's origin");
        Need(WineHelper.TryStartFermentation(secondBottle) && second.parentInventory == home &&
            second.modifiedShape.minX == secondPose.minX && second.modifiedShape.minY == secondPose.minY &&
            second.modifiedShape.orientation == secondPose.orientation && second.modifiedShape.flipped == secondPose.flipped,
            "The other bottle could not return its yeast to the recorded origin");
        result = "PASS explicit native fallback clears only the completed bottle's origin and preserves the next bottle's return";
        return true;
    }
}
