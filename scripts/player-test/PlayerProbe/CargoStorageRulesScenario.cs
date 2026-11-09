using System;
using System.Linq;
using UnityEngine;

public sealed class CargoStorageRulesScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private GameItem bag, target, loot;
    private GameGridInventory contents, destination;
    private int step;

    public bool Tick(out string result)
    {
        result = null;
        QaTripCargo.Need(Time.realtimeSinceStartup < deadline, "Storage cargo scenario timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (entry?.storeInteractables?.activeInHierarchy != true) return false;
            QaTripCargo.SetMode(2);
            TradeScenario.SetOptions(true, "f07.enabled");
            bag = QaTripCargo.New("backpack_medium", entry.invElement);
            target = QaTripCargo.New("backpack_medium", entry.invElement);
            contents = (GameGridInventory)bag.contentWindow.childElement;
            destination = (GameGridInventory)target.contentWindow.childElement;
            entry.selectedBackpack = bag; PlayerStore.Instance.visitLeftTonight = 2;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND));
            step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !entry.afterhourWindow.IsVisible() ||
                entry.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            loot = QaTripCargo.New("wine_yeast", entry.afterhourInventory);
            QaTripCargo.Put(contents, loot, 1);
            string category = loot.GetGameItemType().FirstOrDefault(type => TypeHelper.ALL_TYPES_LIST.Contains(type));
            QaTripCargo.Need(category != null, "Cargo fixture has no public native category");
            ModHelper.SetModData(QaTripCargo.Mod, "routing.v2", "{\"schemaVersion\":2,\"rules\":[" +
                "{\"location\":\"root:stock\",\"receive\":false,\"ship\":true,\"categories\":[]}," +
                "{\"location\":\"item:" + target.GetUniqueID() + ":contents:-1\",\"receive\":true,\"ship\":true,\"categories\":[\"" + category + "\"]}]}");
            var assembly = QaTripCargo.Cargo().GetType().Assembly;
            object routes = assembly.GetType("ShopAssist.InventoryRoutes").GetField("current", QaTripCargo.Static).GetValue(null);
            object store = QaTripCargo.Field(routes, "store");
            store.GetType().GetMethod("Reset", QaTripCargo.Instance).Invoke(store, null);
            MapUIManager.Instance.ReturnNow(); step++; return false;
        }
        if (step == 3)
        {
            if (MapUIManager.Instance.IsOutside || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
                bag.parentInventory == entry.afterhourPocketSlotInvBackpack) return false;
            if (loot.parentInventory != destination || QaTripCargo.Gains().Length != 0) return false;
            QaTripCargo.Need(!entry.invElement.childItems.Contains(loot) && QaTripCargo.Window()?.IsAlive != true,
                "Classified cargo ignored its receiving area or opened a normal unload window");
            result = "PASS automatic cargo prefers enabled matching storage and respects main-stock receiving denial";
            return true;
        }
        return false;
    }
}
