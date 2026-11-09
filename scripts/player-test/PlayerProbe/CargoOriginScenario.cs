using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

internal static class QaTripCargo
{
    internal const string Mod = "starswhere.shopassist";
    internal const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static GameItem New(string identifier, GameGridInventory target)
    {
        var item = DirectoryMaster.Item(identifier);
        Need(item != null, "Missing fixture " + identifier);
        GeneralHelper.SetItemOwned(item, true);
        Put(target, item, 1);
        return item;
    }
    internal static void Put(GameInventory target, GameItem item, int quantity)
    {
        var marker = target.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == target && marker.targetItem == null &&
            marker.TryAcceptOnce(quantity) == quantity, "Could not place " + item.identifier);
    }
    internal static object Field(object value, string name) => value?.GetType().GetField(name, Instance)?.GetValue(value);
    internal static object Trip()
    {
        var mod = ModLoader.Mods.First(value => value.Manifest?.ID == Mod && value.Instance != null);
        var type = mod.Instance.GetType().Assembly.GetType("ShopAssist.TripCargo", true);
        return Field(type.GetField("active", Static).GetValue(null), "trip");
    }
    internal static object[] Gains() => ((IEnumerable)Field(Trip(), "gains")).Cast<object>().ToArray();
    internal static object Cargo()
    {
        var mod = ModLoader.Mods.First(value => value.Manifest?.ID == Mod && value.Instance != null);
        return mod.Instance.GetType().Assembly.GetType("ShopAssist.TripCargo", true).GetField("active", Static).GetValue(null);
    }
    internal static object[] Issues() => ((IEnumerable)Cargo().GetType().GetMethod("CargoAttention", Instance).Invoke(Cargo(), null)).Cast<object>().ToArray();
    internal static void SetMode(int mode)
    {
        var entry = ModHelper.GetConfig(Mod).Get("f25.return_mode") as ConfigEntry<int>;
        Need(entry != null, "Automatic cargo mode is missing"); entry.Set(mode);
        Cargo().GetType().GetMethod("Changed", Instance).Invoke(Cargo(), null);
    }
    internal static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".cargo");
    internal static string Text(CustomUIWindow window) => string.Join(" | ",
        window.root.GetComponentsInChildren<TMP_Text>(true).Select(value => value.text));
    internal static Button Button(CustomUIWindow window, string key)
    {
        string caption = ModHelper.GetLocalized(Mod, key);
        return window.root.GetComponentsInChildren<Button>(true).FirstOrDefault(button => button.interactable &&
            button.GetComponentInChildren<TMP_Text>(true)?.text == caption);
    }
}

public sealed class CargoOriginScenario
{
    private readonly bool unloadAll;
    public CargoOriginScenario() : this(false) { }
    internal CargoOriginScenario(bool all) { unloadAll = all; }
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private bool directory;
    private int step;
    private float returnCheck;
    private GameItem bag, old, oldStack, oldPart, loot, stockStack;
    private GameGridInventory contents;

    public bool Tick(out string result)
    {
        result = null;
        QaTripCargo.Need(Time.realtimeSinceStartup < deadline, "Cargo origin timed out at " + step);
        if (!directory) { if (!QaWholeStackRestart.PrepareNativeDirectory()) return false; directory = true; }
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true) return false;
            QaTripCargo.SetMode(unloadAll ? 1 : 2);
            if (unloadAll)
            {
                foreach (GameItem resident in e.invElement.childItems.ToArray()) resident.Destroy();
                stockStack = QaWholeStackRestart.New("exotic_fuel", 3);
                QaWholeStackRestart.Put(stockStack, e.invElement);
            }
            bag = QaTripCargo.New("backpack_medium", e.invElement);
            contents = (GameGridInventory)bag.contentWindow.childElement;
            old = QaTripCargo.New("wine_yeast", contents);
            oldStack = QaWholeStackRestart.New("exotic_fuel", 10);
            QaWholeStackRestart.Put(oldStack, contents);
            e.selectedBackpack = bag; PlayerStore.Instance.visitLeftTonight = 2;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND)); step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND ||
                !e.afterhourWindow.IsVisible() || e.afterhourPocketSlotInvBackpack.childItem != bag ||
                StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            QaTripCargo.Put(e.afterhourPocketSlotInvLeft, old, 1);
            QaTripCargo.Put(e.afterhourPocketSlotInvRight, oldStack, 1);
            oldPart = e.afterhourPocketSlotInvRight.childItem;
            QaTripCargo.Need(oldPart != null && oldPart != oldStack && oldStack.unitCount == 9,
                "Native old stack did not split into a new instance");
            loot = QaTripCargo.New("wine_yeast", e.afterhourInventory);
            QaTripCargo.Put(contents, loot, 1);
            QaTripCargo.Need(old.parentInventory == e.afterhourPocketSlotInvLeft && loot.parentInventory == contents,
                "Same-type replacement fixture is incorrect");
            MapUIManager.Instance.ReturnNow(); step++; return false;
        }
        if (step == 3)
        {
            if (MapUIManager.Instance.IsOutside || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
                bag.parentInventory == e.afterhourPocketSlotInvBackpack) return false;
            if (QaTripCargo.Gains().Length != 0)
            {
                if (returnCheck == 0) returnCheck = Time.realtimeSinceStartup + 3;
                QaTripCargo.Need(Time.realtimeSinceStartup < returnCheck,
                    "Cargo remained after return: " + ModHelper.GetModData(QaTripCargo.Mod, "tripCargo", "") +
                    " journal=" + ModHelper.GetModData(QaTripCargo.Mod, "wholeStackTransfer", "") +
                    " issues=" + string.Join("|", QaTripCargo.Issues().Select(issue => QaTripCargo.Field(issue, "ReasonKey"))) +
                    " oldAt=" + old.parentInventory?.GetType().Name + "/" + old.parentInventory?.GetParentItem()?.identifier +
                    " partAt=" + oldPart.parentInventory?.GetType().Name + "/" + oldPart.parentInventory?.GetParentItem()?.identifier +
                    " oldStack=" + oldStack.unitCount + "/" + oldStack.parentInventory?.GetParentItem()?.identifier +
                    " rootShape=" + e.invElement.inventoryShape.width + "x" + e.invElement.inventoryShape.height +
                     " stockCount=" + e.invElement.childItems.Count);
                return false;
            }
            if (unloadAll)
            {
                QaTripCargo.Need(stockStack.parentInventory == e.invElement && stockStack.unitCount == 13 &&
                    e.invElement.childItems.Where(item => item.identifier == "exotic_fuel").Sum(item => item.unitCount) == 13 &&
                    e.invElement.childItems.Where(item => item.identifier == "wine_yeast").Sum(item => item.unitCount) == 2 &&
                    !contents.childItems.Any(item => item.identifier == "exotic_fuel" || item.identifier == "wine_yeast"),
                    "All mode lost or retained old or new cargo during native stack merging");
            }
            else QaTripCargo.Need(loot.parentInventory == e.invElement && oldStack.parentInventory == contents && oldStack.unitCount == 9 && oldPart.unitCount == 1,
                "Automatic new-only unloading moved the original stack or its split");
            QaTripCargo.Need(QaTripCargo.Window()?.IsAlive != true, "Automatic unloading opened a normal cargo window");
            result = "PASS same-type replacement, old split lineage and exactly-once unloading";
            return true;
        }
        return false;
    }
}

public sealed class CargoAllAutomaticScenario
{
    private readonly CargoOriginScenario scenario = new CargoOriginScenario(true);
    public bool Tick(out string result) => scenario.Tick(out result);
}

public sealed class CargoCounterSafetyScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private int step;
    private GameItem bag, cargo;

    private static void UnknownSplitPrefix() { }

    private static bool Returned(GameItem item)
    {
        var type = QaTripCargo.Cargo().GetType();
        return (bool)type.GetMethod("IsReturnedCargoInCounter", QaTripCargo.Static).Invoke(null, new object[] { item });
    }

    private static bool SafeCounter(GameItem item)
    {
        var type = QaTripCargo.Cargo().GetType().Assembly.GetType("ShopAssist.InventoryAutoInsert", true);
        return (bool)type.GetMethod("SafeTransferInventory", QaTripCargo.Static)
            .Invoke(null, new object[] { EmporiumEntry.Instance.backInvinvElement, item });
    }

    private static object Plan(GameItem item)
    {
        Type transfer = QaWholeStackRestart.Transfer();
        Type reservations = transfer.GetNestedType("Reservations", BindingFlags.NonPublic);
        return transfer.GetMethod("Empty", QaTripCargo.Static).Invoke(null, new[]
        {
            (object)item, EmporiumEntry.Instance.invElement, Activator.CreateInstance(reservations, true)
        });
    }

    private static void NeedBlocked(object plan, GameItem item, string reason)
    {
        QaTripCargo.Need(plan != null, "No initial counter transfer plan for " + reason);
        Type transfer = QaWholeStackRestart.Transfer();
        object result = transfer.GetMethod("Apply", QaTripCargo.Static).Invoke(null, new object[]
        {
            plan, "f25.open", "QA", "root:counter", "root:stock", (Func<bool>)(() => true), null
        });
        QaTripCargo.Need(result.ToString() == "Unchanged" && item.parentInventory == EmporiumEntry.Instance.backInvinvElement &&
            QaWholeStackRestart.Ready(transfer), "A changed " + reason + " allowed a counter transfer");
    }

    public bool Tick(out string result)
    {
        result = null;
        QaTripCargo.Need(Time.realtimeSinceStartup < deadline, "Counter cargo safety timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (entry?.storeInteractables?.activeInHierarchy != true) return false;
            QaTripCargo.SetMode(1);
            bag = QaTripCargo.New("backpack_medium", entry.invElement);
            cargo = QaTripCargo.New("wine_yeast", (GameGridInventory)bag.contentWindow.childElement);
            entry.selectedBackpack = bag; PlayerStore.Instance.visitLeftTonight = 2;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND)); step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !entry.afterhourWindow.IsVisible() ||
                entry.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true)
                return false;
            QaTripCargo.Put(entry.afterhourPocketSlotInvLeft, cargo, 1);
            MapUIManager.Instance.ReturnNow();
            QaTripCargo.Cargo().GetType().GetField("autoUnloadPending", QaTripCargo.Instance).SetValue(QaTripCargo.Cargo(), false);
            step++; return false;
        }
        if (MapUIManager.Instance.IsOutside || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
            bag.parentInventory == entry.afterhourPocketSlotInvBackpack) return false;

        var counter = entry.backInvinvElement;
        QaTripCargo.Need(cargo.parentInventory == counter && Returned(cargo) && SafeCounter(cargo),
            "The recorded carried item was not accepted from the back counter");
        var unrelated = QaTripCargo.New("wine_superyeast", counter);
        QaTripCargo.Need(!Returned(unrelated) && !SafeCounter(unrelated), "An unrelated counter item was accepted as cargo");

        object record = QaTripCargo.Gains().Single(value => (int)QaTripCargo.Field(value, "id") == cargo.uniqueId);
        FieldInfo quantity = record.GetType().GetField("qty", QaTripCargo.Instance);
        try
        {
            quantity.SetValue(record, cargo.unitCount + 1);
            QaTripCargo.Need(!Returned(cargo) && !SafeCounter(cargo), "Changed cargo quantity was accepted");
        }
        finally { quantity.SetValue(record, cargo.unitCount); }

        object owner = QaTripCargo.Cargo();
        FieldInfo manual = record.GetType().GetField("manual", QaTripCargo.Instance);
        try
        {
            manual.SetValue(record, true);
            QaTripCargo.Need(!Returned(cargo) && !SafeCounter(cargo), "Manual cargo was accepted automatically");
        }
        finally { manual.SetValue(record, false); }

        object trip = QaTripCargo.Trip();
        FieldInfo uncertain = trip.GetType().GetField("uncertain", QaTripCargo.Instance);
        try
        {
            uncertain.SetValue(trip, true);
            QaTripCargo.Need(!Returned(cargo) && !SafeCounter(cargo), "Uncertain cargo was accepted automatically");
        }
        finally { uncertain.SetValue(trip, false); }

        FieldInfo continuity = owner.GetType().GetField("continuous", QaTripCargo.Instance);
        try
        {
            continuity.SetValue(owner, false);
            QaTripCargo.Need(!Returned(cargo) && !SafeCounter(cargo), "Untrusted cargo continuity was accepted");
        }
        finally { continuity.SetValue(owner, true); }

        object plan = Plan(cargo);
        try { ItemPinHelper.SetPinned(cargo, true); NeedBlocked(plan, cargo, "pin"); }
        finally { ItemPinHelper.SetPinned(cargo, false); }

        plan = Plan(cargo);
        bool removeLock = counter.overrideLockRemove;
        try { counter.overrideLockRemove = true; NeedBlocked(plan, cargo, "source lock"); }
        finally { counter.overrideLockRemove = removeLock; }

        plan = Plan(cargo);
        bool insertLock = entry.invElement.overrideLockInsert;
        try { entry.invElement.overrideLockInsert = true; NeedBlocked(plan, cargo, "destination lock"); }
        finally { entry.invElement.overrideLockInsert = insertLock; }

        plan = Plan(cargo);
        var original = counter.onSlotRemoveItemEndFunc;
        try
        {
            counter.onSlotRemoveItemEndFunc += (item, inventory, marker) => { };
            QaTripCargo.Need(!SafeCounter(cargo), "An unknown counter callback was accepted");
            NeedBlocked(plan, cargo, "counter callback");
        }
        finally { counter.onSlotRemoveItemEndFunc = original; }

        plan = Plan(cargo);
        var harmony = new Harmony("qa.cargo.counter-unknown-split");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(GameItem), nameof(GameItem.GetSplitItem)),
                prefix: new HarmonyMethod(typeof(CargoCounterSafetyScenario).GetMethod(nameof(UnknownSplitPrefix), QaTripCargo.Static)));
            QaTripCargo.Need(!Returned(cargo) && !SafeCounter(cargo), "An unknown tracking patch was accepted");
            NeedBlocked(plan, cargo, "tracking patch");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        result = "PASS counter provenance and transfer revalidation";
        return true;
    }
}

public sealed class CargoUncertainScenario
{
    private static int inventoryReads;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private bool directory;
    private int step;
    private GameItem bag, old, mixed, unobserved;
    private GameGridInventory contents;

    private static void CountInventoryRead() => inventoryReads++;

    private static object[] IssuesWithOneInventoryRead()
    {
        var inventory = QaTripCargo.Cargo().GetType().Assembly.GetType("ShopAssist.ShopInventory", true);
        var read = inventory.GetMethod("OwnedLocations", QaTripCargo.Static);
        var counter = typeof(CargoUncertainScenario).GetMethod(nameof(CountInventoryRead), QaTripCargo.Static);
        var harmony = new Harmony("qa.cargo.inventory-read");
        try
        {
            harmony.Patch(read, prefix: new HarmonyMethod(counter));
            inventoryReads = 0;
            object[] issues = QaTripCargo.Issues();
            QaTripCargo.Need(inventoryReads == 1, "Cargo attention enumerated inventory " + inventoryReads + " times");
            return issues;
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    public bool Tick(out string result)
    {
        result = null;
        QaTripCargo.Need(Time.realtimeSinceStartup < deadline, "Cargo uncertainty timed out at " + step);
        if (!directory) { if (!QaWholeStackRestart.PrepareNativeDirectory()) return false; directory = true; }
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true) return false;
            QaTripCargo.SetMode(2);
            bag = QaTripCargo.New("backpack_medium", e.invElement);
            contents = (GameGridInventory)bag.contentWindow.childElement;
            old = QaWholeStackRestart.New("exotic_fuel", 3); QaWholeStackRestart.Put(old, contents);
            e.selectedBackpack = bag; PlayerStore.Instance.visitLeftTonight = 2;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND)); step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !e.afterhourWindow.IsVisible() ||
                e.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            mixed = QaWholeStackRestart.New("exotic_fuel", 1); QaWholeStackRestart.Put(mixed, e.afterhourInventory);
            SlotMarker stack = old.GetStackSlot(mixed);
            QaTripCargo.Need(stack != null && stack.targetItem == old && stack.TryAcceptOnce(1) == 1 && old.unitCount == 4,
                "Native mixed-origin stack was not formed");
            unobserved = DirectoryMaster.Item("wine_yeast"); GeneralHelper.SetItemOwned(unobserved, true);
            SlotMarker marker = contents.TryFindOneValidInventorySlot(unobserved);
            QaTripCargo.Need(marker != null && marker.targetItem == null && marker.itemGridShape != null,
                "No cell for missing-event fixture");
            unobserved.modifiedShape = new GridShapeBuilder(unobserved.modifiedShape).SetTransform(marker.itemGridShape);
            QaTripCargo.Need(contents.UncheckedAccept(unobserved), "Missing-event fixture was not inserted");
            MapUIManager.Instance.ReturnNow(); step++; return false;
        }
        if (step == 3)
        {
            if (MapUIManager.Instance.IsOutside || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
                bag.parentInventory == e.afterhourPocketSlotInvBackpack) return false;
            object[] gains = QaTripCargo.Gains();
            QaTripCargo.Need(gains.Any(value => (int)QaTripCargo.Field(value, "id") == old.uniqueId && (bool)QaTripCargo.Field(value, "manual")) &&
                gains.Any(value => (int)QaTripCargo.Field(value, "id") == unobserved.uniqueId && (bool)QaTripCargo.Field(value, "manual")) &&
                (bool)QaTripCargo.Field(QaTripCargo.Trip(), "uncertain"),
                "Mixed or unobserved cargo was not preserved for review: " + ModHelper.GetModData(QaTripCargo.Mod, "tripCargo", ""));
            QaTripCargo.Need(old.parentInventory == contents && unobserved.parentInventory == contents && QaTripCargo.Window()?.IsAlive != true,
                "Uncertain cargo was moved or a normal cargo window was opened");
            var issues = IssuesWithOneInventoryRead();
            QaTripCargo.Need(issues.Any(issue => (int)QaTripCargo.Field(issue, "ItemId") == old.uniqueId &&
                (bool)QaTripCargo.Field(issue, "NeedsInspection") && QaTripCargo.Field(issue, "Resolve") is Action),
                "Uncertain cargo has no actionable Attention entry");
            object before = issues.Single(issue => (int)QaTripCargo.Field(issue, "ItemId") == unobserved.uniqueId);
            string previousLocation = (string)QaTripCargo.Field(before, "Location");
            QaTripCargo.Put(e.invElement, unobserved, 1);
            object after = IssuesWithOneInventoryRead().Single(issue => (int)QaTripCargo.Field(issue, "ItemId") == unobserved.uniqueId);
            QaTripCargo.Need((string)QaTripCargo.Field(after, "Location") != previousLocation &&
                (string)QaTripCargo.Field(after, "ReasonKey") == "f25.check" && unobserved.parentInventory == e.invElement,
                "Cargo attention reused an outdated location or changed the recorded item");
            result = "PASS mixed origin and missing event stay visible with fresh single-scan locations";
            return true;
        }
        return false;
    }
}
