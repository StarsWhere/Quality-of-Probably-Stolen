using System;
using System.Collections;
using System.Linq;
using System.Reflection;
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
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private int step;
    private GameItem bag, old, oldStack, oldPart, loot;
    private GameGridInventory contents;

    public bool Tick(out string result)
    {
        result = null;
        QaTripCargo.Need(Time.realtimeSinceStartup < deadline, "Cargo origin timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true) return false;
            TradeScenario.SetOptions(true, "f25.enabled");
            bag = QaTripCargo.New("backpack_medium", e.invElement);
            contents = (GameGridInventory)bag.contentWindow.childElement;
            old = QaTripCargo.New("wine_yeast", contents);
            oldStack = DirectoryMaster.Item("wine_berry"); GeneralHelper.SetItemOwned(oldStack, true); oldStack.SetStack(10);
            QaNativeStackFixture.PutWhole(contents, oldStack);
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
            object[] gains = QaTripCargo.Gains();
            QaTripCargo.Need(gains.Length == 1 && (int)QaTripCargo.Field(gains[0], "id") == loot.uniqueId &&
                !(bool)QaTripCargo.Field(gains[0], "manual") && (int)QaTripCargo.Field(gains[0], "qty") == 1 &&
                gains.All(value => (int)QaTripCargo.Field(value, "id") != old.uniqueId &&
                    (int)QaTripCargo.Field(value, "id") != oldPart.uniqueId),
                "Same-type ground pickup or old split provenance is wrong: " + ModHelper.GetModData(QaTripCargo.Mod, "tripCargo", ""));
            RoutingScenario.RunFeatureAction("f25.open");
            var window = QaTripCargo.Window();
            QaTripCargo.Need(window?.Get("unload")?.button != null && QaItemUi.Hit(window.root, loot) != null &&
                QaItemUi.Hit(window.root, oldStack) == null, "Confirmed pickup is missing from cargo UI");
            window.Get("unload").button.onClick.Invoke();
            QaTripCargo.Need(QaTripCargo.Window().Get("confirm")?.button?.interactable == true,
                "Confirmed pickup cannot be unloaded: " + QaTripCargo.Text(QaTripCargo.Window()));
            QaTripCargo.Window().Get("confirm").button.onClick.Invoke();
            QaTripCargo.Need(loot.parentInventory == e.invElement && oldStack.parentInventory == contents &&
                QaTripCargo.Gains().Length == 0, "Unload lost the old stack or did not clear the moved pickup");
            RoutingScenario.RunFeatureAction("f25.open");
            QaTripCargo.Need(QaTripCargo.Window().Get("unload") == null, "Pickup could be unloaded twice");
            result = "PASS same-type replacement, old split lineage and exactly-once unloading";
            return true;
        }
        return false;
    }
}

public sealed class CargoUncertainScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private bool directory;
    private int step;
    private GameItem bag, old, mixed, unobserved;
    private GameGridInventory contents;

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
            TradeScenario.SetOptions(true, "f25.enabled");
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
            RoutingScenario.RunFeatureAction("f25.open");
            var window = QaTripCargo.Window();
            string text = QaTripCargo.Text(window);
            QaTripCargo.Need(text.Contains(old.GetDisplayName(true)) && text.Contains(unobserved.GetDisplayName(true)) &&
                text.Contains(ModHelper.GetLocalized(QaTripCargo.Mod, "f25.check")) && QaTripCargo.Button(window, "f25.locate") != null &&
                QaTripCargo.Button(window, "f25.handled") != null && QaItemUi.Hit(window.root, old) == null,
                "Uncertain items cannot be located and marked handled: " + text);
            result = "PASS mixed origin and missing event stay visible for manual review";
            return true;
        }
        return false;
    }
}
