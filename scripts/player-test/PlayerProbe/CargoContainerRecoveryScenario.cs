using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public abstract class CargoContainerRecoveryFixture
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 160f;
    private const string Owner = "qa.f25-container-record-failure";
    private static GameItem faultBag;
    private static int failures;
    private static bool failAfterWrite;
    private Harmony harmony;
    private GameItem equipped, bag, child, other;
    private GameGridInventory equippedGrid, bagGrid;
    private int step;
    private float retryAt;
    protected abstract bool AfterWrite { get; }

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Type Transfer => QaTripCargo.Cargo().GetType().Assembly.GetType("ShopAssist.WholeStackTransfer", true);
    private static bool Ready() => (bool)Transfer.GetProperty("Ready", QaTripCargo.Static).GetValue(null);
    private static string Journal() => ModHelper.GetModData(QaTripCargo.Mod, "wholeStackTransfer", "");
    private static object Gain(GameItem item)
    {
        object cargo = QaTripCargo.Cargo();
        Need((bool)cargo.GetType().GetMethod("Load", QaTripCargo.Instance).Invoke(cargo, null), "Could not reload the cargo record");
        return QaTripCargo.Gains().FirstOrDefault(gain => (int)QaTripCargo.Field(gain, "id") == item.uniqueId);
    }
    private static bool ShouldFail(string key) => key == "tripCargo" && faultBag != null &&
        faultBag.parentInventory == EmporiumEntry.Instance.invElement && Journal().Contains("\"state\":\"pending\"");
    private static void FailBeforeRecord(string __0, string __1)
    {
        if (failAfterWrite || !ShouldFail(__0)) return;
        failures++;
        throw new InvalidOperationException("QA interrupted F25 container completion record");
    }
    private static void FailAfterRecord(string __0, string __1)
    {
        if (!failAfterWrite || !ShouldFail(__0)) return;
        failures++;
        throw new InvalidOperationException("QA F25 container record readback failure after write");
    }
    private void Cleanup() { harmony?.UnpatchAll(Owner); harmony = null; faultBag = null; }

    public bool Tick(out string result)
    {
        result = null;
        try
        {
            Need(Time.realtimeSinceStartup < deadline, "Container recovery timed out at " + step);
            if (step == 0) { if (start.Tick()) step++; return false; }
            var entry = EmporiumEntry.Instance;
            if (step == 1)
            {
                if (entry?.storeInteractables?.activeInHierarchy != true) return false;
                QaTripCargo.SetMode(2);
                equipped = QaTripCargo.New("backpack_medium", entry.invElement);
                equippedGrid = (GameGridInventory)equipped.contentWindow.childElement;
                entry.selectedBackpack = equipped; PlayerStore.Instance.visitLeftTonight = 2;
                MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND)); step++; return false;
            }
            if (step == 2)
            {
                if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !entry.afterhourWindow.IsVisible() ||
                    entry.afterhourPocketSlotInvBackpack.childItem != equipped || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                bag = QaTripCargo.New("backpack_small", entry.afterhourInventory);
                GeneralHelper.SetCustomName(bag, "QA pending loaded backpack");
                bagGrid = (GameGridInventory)bag.contentWindow.childElement;
                child = DirectoryMaster.Item("wine_yeast"); Need(child != null, "Missing contained recovery fixture");
                GeneralHelper.SetItemOwned(child, true); GeneralHelper.SetCustomName(child, "QA moved recovery child");
                QaTripCargo.Put(bagGrid, child, child.unitCount);
                other = QaTripCargo.New("wine_yeast_red", entry.afterhourInventory);
                QaTripCargo.Put(equippedGrid, other, other.unitCount);
                ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(200, 160)); bag.contentWindow.ToFront();
                step++; return false;
            }
            if (step == 3)
            {
                var element = child as GameItemElement; PixelWindow view = element?.handler?.FindParentWindow();
                if (!bag.contentWindow.IsVisible() || element?.handler?.gameObject.activeInHierarchy != true || view?.IsVisible() != true ||
                    WindowsHandler.current?.visibleWindows?.Contains(view) != true) return false;
                QaTripCargo.Put(entry.afterhourPocketSlotInvLeft, bag, bag.unitCount);
                Need(child.parentInventory == bagGrid, "The native move changed backpack contents");
                bag.contentWindow.Hide(); faultBag = bag; failures = 0; failAfterWrite = AfterWrite;
                harmony = new Harmony(Owner);
                Type checkedData = QaTripCargo.Cargo().GetType().Assembly.GetType("ShopAssist.CheckedModData", true);
                harmony.Patch(checkedData.GetMethod("Write", QaTripCargo.Static),
                    prefix: new HarmonyMethod(typeof(CargoContainerRecoveryFixture).GetMethod(nameof(FailBeforeRecord), QaTripCargo.Static)),
                    postfix: new HarmonyMethod(typeof(CargoContainerRecoveryFixture).GetMethod(nameof(FailAfterRecord), QaTripCargo.Static)));
                MapUIManager.Instance.ReturnNow(); step++; return false;
            }
            if (step == 4)
            {
                if (failures == 0) return false;
                Cleanup();
                Need(!Ready() && Journal().Contains("\"state\":\"pending\"") && bag.parentInventory == entry.invElement &&
                    child.parentInventory == bagGrid && (Gain(bag) == null) == AfterWrite && (Gain(child) == null) == AfterWrite && Gain(other) != null,
                    "Fault did not retain the native move and expected pre/post-write cargo records");
                QaTripCargo.Put(equippedGrid, child, child.unitCount);
                Need(child.parentInventory == equippedGrid, "Could not move the recorded child while inspection was pending");
                step++; return false;
            }
            if (step == 5)
            {
                if (!QaItemUi.OpenAttention()) return false;
                var window = CustomUIManager.Instance.GetWindow(QaTripCargo.Mod + ".attention");
                var confirm = RoutingScenario.Button(window, "f25.resolve_pending");
                Need(confirm != null && confirm.interactable, "Pending container transfer has no inspection action");
                confirm.onClick.Invoke();
                Need(Ready() && Gain(bag) == null && (AfterWrite ? Gain(child) == null :
                    Gain(child) != null && (bool)QaTripCargo.Field(Gain(child), "manual")) &&
                    Gain(other) != null && (bool)QaTripCargo.Field(Gain(other), "manual") && (bool)QaTripCargo.Field(QaTripCargo.Trip(), "uncertain"),
                    "Resolving a changed container did not retain its removed child for manual inspection");
                window.Close(); QaTripCargo.SetMode(2); retryAt = Time.realtimeSinceStartup + 2f; step++; return false;
            }
            if (Time.realtimeSinceStartup < retryAt) return false;
            Need(child.parentInventory == equippedGrid && other.parentInventory == equippedGrid && Gain(other) != null &&
                (bool)QaTripCargo.Field(Gain(other), "manual") && (AfterWrite || Gain(child) != null && (bool)QaTripCargo.Field(Gain(child), "manual")),
                "Retry automatically unloaded goods after pending inspection");
            result = AfterWrite ? "PASS post-write container failure with missing root gain and conservative recovery" :
                "PASS interrupted whole-container unload and removed-child manual recovery";
            return true;
        }
        catch { Cleanup(); throw; }
    }
}

public sealed class CargoContainerRecoveryScenario : CargoContainerRecoveryFixture { protected override bool AfterWrite => false; }
public sealed class CargoContainerWrittenRecoveryScenario : CargoContainerRecoveryFixture { protected override bool AfterWrite => true; }
