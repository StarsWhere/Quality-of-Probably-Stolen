using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

internal sealed class QaCargoStockFallback
{
    internal enum Case { Fallback, Denied, Classified, Pinned, Locked }

    [Serializable] private sealed class Book
    {
        public int schemaVersion = 2;
        public Rule[] rules;
    }
    [Serializable] private sealed class Rule
    {
        public string location;
        public bool receive, ship = true;
        public string[] categories;
    }

    private readonly Case test;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private int step;
    private GameItem bag, target, loot;
    private GameGridInventory contents, destination, lockedInventory;
    private bool lockSet, originalLock;

    internal QaCargoStockFallback(Case test) { this.test = test; }

    internal bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch { RestoreLock(); throw; }
    }

    private bool TickCore(out string result)
    {
        result = null;
        QaTripCargo.Need(Time.realtimeSinceStartup < deadline, "Cargo stock fallback timed out: " + test + " step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (entry?.storeInteractables?.activeInHierarchy != true) return false;
            QaTripCargo.SetMode(2);
            TradeScenario.SetOptions(true, "f07.enabled");
            bag = QaTripCargo.New("backpack_medium", entry.invElement);
            target = QaTripCargo.New("backpack_medium", entry.invElement);
            contents = bag.contentWindow?.childElement as GameGridInventory;
            destination = target.contentWindow?.childElement as GameGridInventory;
            QaTripCargo.Need(contents != null && destination != null, "Cargo test bags lack their native grids");
            entry.selectedBackpack = bag;
            PlayerStore.Instance.visitLeftTonight = 2;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND));
            step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !entry.afterhourWindow.IsVisible() ||
                entry.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true)
                return false;
            loot = QaTripCargo.New("wine_yeast", entry.afterhourInventory);
            QaTripCargo.Put(contents, loot, 1);
            var assembly = QaTripCargo.Cargo().GetType().Assembly;
            var publicTypes = assembly.GetType("ShopAssist.CustomerIndicators", true).GetMethod("PublicTypes", QaTripCargo.Static);
            var categories = publicTypes?.Invoke(null, new object[] { loot }) as ICollection<string>;
            string category = categories?.FirstOrDefault(value => TypeHelper.ALL_TYPES_LIST.Contains(value));
            QaTripCargo.Need(category != null, "Cargo fixture has no public category");
            var rules = new Book { rules = new[] {
                new Rule { location = "root:stock", receive = test != Case.Denied, categories = Array.Empty<string>() },
                new Rule { location = "item:" + target.GetUniqueID() + ":contents:-1", receive = true,
                    categories = test == Case.Classified ? new[] { category } : Array.Empty<string>() }
            } };
            ModHelper.SetModData(QaTripCargo.Mod, "routing.v2", JsonUtility.ToJson(rules));
            object routes = assembly.GetType("ShopAssist.InventoryRoutes", true).GetField("current", QaTripCargo.Static).GetValue(null);
            object store = QaTripCargo.Field(routes, "store");
            store.GetType().GetMethod("Reset", QaTripCargo.Instance).Invoke(store, null);
            if (test == Case.Pinned) ItemPinHelper.SetPinned(loot, true);
            MapUIManager.Instance.ReturnNow();
            if (test == Case.Locked)
            {
                lockedInventory = entry.invElement;
                originalLock = lockedInventory.overrideLockInsert;
                lockedInventory.overrideLockInsert = true;
                lockSet = true;
            }
            step++; return false;
        }
        if (step == 3)
        {
            if (MapUIManager.Instance.IsOutside || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
                bag.parentInventory == entry.afterhourPocketSlotInvBackpack) return false;
            bool shouldMove = test == Case.Fallback || test == Case.Classified;
            if (shouldMove)
            {
                GameInventory expected = test == Case.Classified ? destination : entry.invElement;
                if (!ReferenceEquals(loot.parentInventory, expected) || QaTripCargo.Gains().Length != 0) return false;
                QaTripCargo.Need(expected.childItems.Count(item => ReferenceEquals(item, loot)) == 1 && loot.unitCount == 1 &&
                    QaTripCargo.Window()?.IsAlive != true, "Automatic unload duplicated cargo or opened the old unload window");
            }
            else
            {
                string expectedReason = test == Case.Denied ? "f25.no_destination" : test == Case.Locked ? "inv.noSpace" : "f08.locked";
                var issue = QaTripCargo.Issues().FirstOrDefault(value => (int)QaTripCargo.Field(value, "ItemId") == loot.uniqueId &&
                    (string)QaTripCargo.Field(value, "ReasonKey") == expectedReason);
                if (issue == null) return false;
                QaTripCargo.Need(ReferenceEquals(loot.parentInventory, contents) && loot.unitCount == 1 &&
                    !entry.invElement.childItems.Contains(loot) && QaTripCargo.Gains().Length == 1,
                    "Blocked cargo moved or lost its retry record");
            }

            var routesType = QaTripCargo.Cargo().GetType().Assembly.GetType("ShopAssist.InventoryRoutes", true);
            var receive = routesType.GetMethod("AutoReceiveAllowed", QaTripCargo.Static);
            QaTripCargo.Need(receive != null && !(bool)receive.Invoke(null, new object[] { entry.invElement, loot, false }) &&
                (bool)receive.Invoke(null, new object[] { entry.invElement, loot, true }) == (test != Case.Denied),
                "Main-stock fallback permission did not preserve category matching or explicit denial");
            QaTripCargo.Need((bool)receive.Invoke(null, new object[] { destination, loot, true }) == (test == Case.Classified),
                "The unmatched exception was incorrectly applied to a nested storage area");
            RestoreLock();
            result = "PASS automatic cargo main-stock " + test + " and receiving scope";
            return true;
        }
        return false;
    }

    private void RestoreLock()
    {
        if (!lockSet) return;
        lockedInventory.overrideLockInsert = originalLock;
        lockSet = false;
    }
}

public sealed class CargoStockFallbackScenario
{
    private readonly QaCargoStockFallback test = new QaCargoStockFallback(QaCargoStockFallback.Case.Fallback);
    public bool Tick(out string result) => test.Tick(out result);
}

public sealed class CargoStockDeniedScenario
{
    private readonly QaCargoStockFallback test = new QaCargoStockFallback(QaCargoStockFallback.Case.Denied);
    public bool Tick(out string result) => test.Tick(out result);
}

public sealed class CargoStockClassifiedScenario
{
    private readonly QaCargoStockFallback test = new QaCargoStockFallback(QaCargoStockFallback.Case.Classified);
    public bool Tick(out string result) => test.Tick(out result);
}

public sealed class CargoStockPinnedScenario
{
    private readonly QaCargoStockFallback test = new QaCargoStockFallback(QaCargoStockFallback.Case.Pinned);
    public bool Tick(out string result) => test.Tick(out result);
}

public sealed class CargoStockLockedScenario
{
    private readonly QaCargoStockFallback test = new QaCargoStockFallback(QaCargoStockFallback.Case.Locked);
    public bool Tick(out string result) => test.Tick(out result);
}
