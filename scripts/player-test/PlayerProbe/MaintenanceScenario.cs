using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

// Runs only in the isolated QA Player and its newly created native save.
public sealed class MaintenanceScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly string label = "QA maintenance " + Guid.NewGuid().ToString("N").Substring(0, 8);
    private GameItem machine, oldBattery, spareBattery, otherBattery, pitcher, oldFilter, spareFilter, box, bufferBlocker;
    private GameSlotInventory powerSlot, filterSlot;
    private GameGridInventory source;
    private int day, step, guardNight;
    private float deadline = Time.realtimeSinceStartup + 150f, until;
    private bool checkedForeign;
    private int foreignCalls;

    public bool Tick(out string result)
    {
        result = null;
        Require(Time.realtimeSinceStartup < deadline, "F31/F35 QA timed out at step " + step + DescribeState());
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                step = 1; return false;
            case 1:
            {
                if (StoreUIManager.Instance?.gameOptionUIManager == null || EmporiumEntry.Instance?.invElement == null ||
                    EmporiumEntry.Instance.storeInteractables == null || !EmporiumEntry.Instance.storeInteractables.activeInHierarchy ||
                    ItemMouseDoubleClickHandler.current == null || StoreShutterButton.Instance == null) return false;
                var calendar = AdvCalendarUIManager.Instance;
                if (calendar?.UIPanel != null && calendar.UIPanel.activeSelf) StoreUIManager.Instance.storeCalendar.OnClick();
                day = StoreStation.GetDayCounter();
                Require(day > 0 && PlayerStore.Instance.storeState == PlayerStore.StoreState.MORNING, "QA save is not at a native morning");
                GameInventory stock = EmporiumEntry.Instance.invElement;
                machine = New("hydroponic"); oldBattery = New("energy_credit"); spareBattery = New("energy_credit"); otherBattery = New("energy_credit");
                pitcher = New("water_pitcher"); oldFilter = New("water_filter"); spareFilter = New("water_filter");
                box = New("backpack_medium"); GeneralHelper.SetCustomName(box, label);
                GameItem seed = New("hydroreed_seed"), water = New("wine_bottle");
                Put(stock, machine); Put(stock, oldBattery); Put(stock, spareBattery); Put(stock, otherBattery); Put(stock, pitcher);
                Put(stock, oldFilter); Put(stock, spareFilter); Put(stock, box); Put(stock, seed); Put(stock, water);
                source = box.contentWindow?.childElement as GameGridInventory;
                powerSlot = MachineHelper.GetBatterySlot(machine);
                filterSlot = pitcher.contentWindow?.childElement as GameSlotInventory;
                Require(source != null && powerSlot != null && filterSlot != null, "QA native maintenance slots unavailable");
                PowerHelper.SetPowerSourceAt(oldBattery, 2);
                PowerHelper.SetPowerSourceAt(spareBattery, 100);
                PowerHelper.SetPowerSourceAt(otherBattery, 90);
                Put(powerSlot, oldBattery); Put(source, otherBattery); Put(source, spareBattery);
                Put(filterSlot, oldFilter); Put(source, spareFilter);
                WaterHelper.AddWater(water, 2, 750000);
                Put(MachineHydroponic.GetInputSlot(machine), seed); Put(MachineHydroponic.GetWaterSlot(machine), water);
                ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(210, 100));
                ItemMouseDoubleClickHandler.current.OpenContentAction(pitcher, new Vector2(390, 100));
                ItemMouseDoubleClickHandler.current.OpenContentAction(box, new Vector2(570, 100));
                var opts = StoreUIManager.Instance.gameOptionUIManager;
                opts.OpenUI(); var options = CustomUIManager.Instance.GetWindow("game_options");
                Require(options != null, "Native options unavailable");
                options.Get(Mod + ":f15.enabled").SetBool(false, true);
                options.Get(Mod + ":f21.enabled").SetBool(false, true);
                options.Get(Mod + ":f30.enabled").SetBool(true, true);
                options.Get(Mod + ":f31.enabled").SetBool(true, true);
                options.Get(Mod + ":f35.enabled").SetBool(true, true);
                options.Get("apply").button.onClick.Invoke();
                opts.CloseUI();
                Configure("f31.open", 50);
                Configure("f35.open", -1);
                Require(!string.IsNullOrEmpty(ModHelper.GetModData(Mod, "machineMaintenance", null)), "Maintenance rules not saved");
                DurabilityHelper.SetCurrentDurability(oldFilter, 0);
                WaterHelper.AddWater(pitcher, 5, 0);
                until = Time.realtimeSinceStartup + 1f; step = 2; return false;
            }
            case 2:
                Require(ReferenceEquals(filterSlot.childItem, oldFilter), "F35 replaced a filter although no water was processed");
                if (Time.realtimeSinceStartup < until) return false;
                DurabilityHelper.SetCurrentDurability(oldFilter, 1);
                ItemPinHelper.SetPinned(spareFilter, true);
                int before = WaterHelper.GetCurrentCapacityML(pitcher);
                WaterHelper.AddWater(pitcher, 5, 250000, true, 0, 1);
                Require(WaterHelper.GetCurrentCapacityML(pitcher) > before && DurabilityHelper.GetRealDurability(oldFilter) == 0,
                    "Native water processing did not exhaust the QA filter");
                until = Time.realtimeSinceStartup + 1f; step = 3; return false;
            case 3:
            {
                Require(ReferenceEquals(filterSlot.childItem, oldFilter) && ReferenceEquals(spareFilter.parentInventory, source),
                    "F35 moved a pinned spare filter");
                if (Time.realtimeSinceStartup < until) return false;
                RoutingScenario.OpenOptionAction("f30.open");
                var overview = CustomUIManager.Instance.GetWindow(Mod + ".f30");
                string missing = ModHelper.GetLocalized(Mod, "maintenance.swap_unavailable");
                Require(overview != null && overview.root.GetComponentsInChildren<TMP_Text>(true).Any(t => t.text != null && t.text.Contains(missing)),
                    "F30 did not report the blocked pitcher filter");
                overview.Close();
                ItemPinHelper.SetPinned(spareFilter, false);
                spareFilter.onSlotAddItemFunc += ForeignItemAdded;
                before = WaterHelper.GetCurrentCapacityML(pitcher);
                WaterHelper.AddWater(pitcher, 5, 100000, true, 0, 1);
                Require(WaterHelper.GetCurrentCapacityML(pitcher) > before, "Native water processing did not add water");
                until = Time.realtimeSinceStartup + 1f;
                step = 4; return false;
            }
            case 4:
                if (!checkedForeign)
                {
                    Require(ReferenceEquals(filterSlot.childItem, oldFilter) && ReferenceEquals(spareFilter.parentInventory, source) && foreignCalls == 0,
                        "F35 performed an exchange with an unknown external item callback");
                    if (Time.realtimeSinceStartup < until) return false;
                    spareFilter.onSlotAddItemFunc -= ForeignItemAdded;
                    before = WaterHelper.GetCurrentCapacityML(pitcher);
                    WaterHelper.AddWater(pitcher, 5, 100000, true, 0, 1);
                    Require(WaterHelper.GetCurrentCapacityML(pitcher) > before, "Native retry did not add water");
                    checkedForeign = true; return false;
                }
                if (ReferenceEquals(filterSlot.childItem, oldFilter)) return false;
                Require(ReferenceEquals(filterSlot.childItem, spareFilter) && ReferenceEquals(oldFilter.parentInventory, source) &&
                    DurabilityHelper.GetRealDurability(oldFilter) == 0 && DurabilityHelper.GetRealDurability(spareFilter) > 0 &&
                    EmporiumEntry.Instance.swapBufferElement.childItem == null, "F35 did not conserve and swap the two native filters");
                Require(machine.onCycleEndSlotItemFunc != null && !MachineHelper.CanPower(machine), "QA low-power fixture unavailable");
                GameInventory parent = machine.parentInventory;
                machine.onCycleEndSlotItemFunc(machine, parent, parent.GetIncompleteItemInventorySlot(machine));
                Require(ReferenceEquals(powerSlot.childItem, oldBattery) && ReferenceEquals(spareBattery.parentInventory, source),
                    "F31 exchanged a battery during a daytime native machine cycle");
                step = 5; return false;
            case 5:
                if (!PlayerStore.Instance.CanStartDay() || PlayerStore.Instance.lockShutterButtonFlag > 0) return false;
                StoreShutterButton.Instance.OnClick();
                Require(PlayerStore.Instance.storeState == PlayerStore.StoreState.OPEN, "Native shutter did not open");
                step = 6; return false;
            case 6:
                if (PlayerStore.Instance.lockShutterButtonFlag > 0 || !PlayerStore.Instance.CanEndDay()) return false;
                StoreShutterButton.Instance.OnClick(); step = 7; return false;
            case 7:
                if (PlayerStore.Instance.storeState == PlayerStore.StoreState.OPEN)
                {
                    if (StoreUIManager.Instance.closeShutterConfirmationPanel?.activeInHierarchy == true) StoreShutterButton.Instance.EndAndClose();
                    return false;
                }
                Require(PlayerStore.Instance.storeState == PlayerStore.StoreState.CLOSED, "Native shutter did not close");
                until = Time.realtimeSinceStartup + 3f; step = 8; return false;
            case 8:
                if (Time.realtimeSinceStartup < until) return false;
                var bed = UnityEngine.Object.FindObjectOfType<StoreBedroll>();
                if (bed == null || EndOfDayUIManager.Instance == null) return false;
                bed.OnClick();
                Require(EndOfDayUIManager.Instance.IsOpen, "Native end-of-day screen unavailable");
                EndOfDayUIManager.Instance.OnEndDayButtonClick(); step = 9; return false;
            case 9:
                if (StoreStation.GetDayCounter() != day + 1 || PlayerStore.Instance.storeState != PlayerStore.StoreState.MORNING) return false;
                Require(ReferenceEquals(powerSlot.childItem, spareBattery) && ReferenceEquals(oldBattery.parentInventory, source) &&
                    oldBattery.GetTagReadonly("power_source_item_energy").GetInt() == 2 &&
                    spareBattery.GetTagReadonly("power_source_item_energy").GetInt() < 100 &&
                    ReferenceEquals(otherBattery.parentInventory, source) && otherBattery.GetTagReadonly("power_source_item_energy").GetInt() == 90 &&
                    EmporiumEntry.Instance.swapBufferElement.childItem == null,
                    "F31 did not choose the highest charge before nightly power use or conserve the old battery");
                PowerHelper.SetPowerSourceAt(spareBattery, 2);
                ItemPinHelper.SetPinned(spareBattery, true);
                guardNight = 0; step = 10; return false;
            case 10:
            {
                var calendar = AdvCalendarUIManager.Instance;
                if (calendar?.UIPanel != null && calendar.UIPanel.activeSelf) StoreUIManager.Instance.storeCalendar.OnClick();
                if (!PlayerStore.Instance.CanStartDay() || PlayerStore.Instance.lockShutterButtonFlag > 0) return false;
                StoreShutterButton.Instance.OnClick();
                NeedState(PlayerStore.StoreState.OPEN, "Native shutter did not open for the guard night");
                ClearGuardClients();
                step = 11; return false;
            }
            case 11:
                if (!PlayerStore.Instance.CanEndDay()) ClearGuardClients();
                if (PlayerStore.Instance.lockShutterButtonFlag > 0 || !PlayerStore.Instance.CanEndDay()) return false;
                StoreShutterButton.Instance.OnClick(); step = 12; return false;
            case 12:
                if (PlayerStore.Instance.storeState == PlayerStore.StoreState.OPEN)
                {
                    if (StoreUIManager.Instance.closeShutterConfirmationPanel?.activeInHierarchy == true) StoreShutterButton.Instance.EndAndClose();
                    return false;
                }
                NeedState(PlayerStore.StoreState.CLOSED, "Native shutter did not close for the guard night");
                until = Time.realtimeSinceStartup + 3f; step = 13; return false;
            case 13:
                if (Time.realtimeSinceStartup < until) return false;
                var guardBed = UnityEngine.Object.FindObjectOfType<StoreBedroll>();
                if (guardBed == null || EndOfDayUIManager.Instance == null) return false;
                guardBed.OnClick();
                Require(EndOfDayUIManager.Instance.IsOpen, "Native end-of-day screen unavailable for the guard night");
                EndOfDayUIManager.Instance.OnEndDayButtonClick(); step = 14; return false;
            case 14:
                if (StoreStation.GetDayCounter() != day + 2 + guardNight || PlayerStore.Instance.storeState != PlayerStore.StoreState.MORNING) return false;
                Require(ReferenceEquals(powerSlot.childItem, spareBattery) && spareBattery.GetTagReadonly("power_source_item_energy").GetInt() == 2 &&
                    ReferenceEquals(otherBattery.parentInventory, source) && otherBattery.GetTagReadonly("power_source_item_energy").GetInt() == 90,
                    guardNight == 0 ? "F31 exchanged the pinned installed battery" : "F31 exchanged with an occupied swap buffer");
                if (guardNight == 0)
                {
                    ItemPinHelper.SetPinned(spareBattery, false);
                    bufferBlocker = New("wine_yeast_red");
                    Put(EmporiumEntry.Instance.invElement, bufferBlocker);
                    Put(EmporiumEntry.Instance.swapBufferElement, bufferBlocker);
                    guardNight = 1; step = 10; return false;
                }
                Require(ReferenceEquals(EmporiumEntry.Instance.swapBufferElement.childItem, bufferBlocker), "F31 moved the unrelated buffer item");
                Put(EmporiumEntry.Instance.invElement, bufferBlocker);
                var player = PlayerStore.Instance;
                int batteryId = spareBattery.GetUniqueID(), filterId = spareFilter.GetUniqueID();
                player.SaveGame(); // QA save only; production maintenance never forces a save.
                File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-maintenance-run.txt"),
                    string.Join("|", "1", player.runID, player.saveSlotId, StoreStation.GetDayCounter(), machine.uniqueId,
                        pitcher.uniqueId, box.uniqueId, batteryId, filterId));
                result = "PASS F31 battery priority and three real nights including pinned/buffer guards; F35 water, pinned/no-op/external-callback guards and conserved exchanges";
                return true;
            default: throw new InvalidOperationException("Unknown F31/F35 QA step");
        }
    }

    private void Configure(string action, int threshold)
    {
        RoutingScenario.OpenOptionAction(action);
        CustomUIWindow window = CustomUIManager.Instance.GetWindow(Mod + ".maintenance");
        Require(window != null && window.IsVisible, "Maintenance window unavailable: " + action);
        QaItemUi.Choose(window, "source", label);
        window = CustomUIManager.Instance.GetWindow(Mod + ".maintenance");
        Require(window != null && window.IsVisible, "Maintenance window closed after choosing source: " + action);
        if (threshold >= 0) { window.Get("threshold").SetFloat(threshold, true); window.Get("priority").SetIndex(1, true); }
        window.Get("enable").button.onClick.Invoke();
    }
    private void ForeignItemAdded(GameItem item, GameInventory inventory, SlotMarker slot) { foreignCalls++; }
    private static void NeedState(PlayerStore.StoreState state, string message) { Require(PlayerStore.Instance.storeState == state, message); }
    private static void ClearGuardClients()
    {
        // These extra days exist only to test F31 guards in a freshly created QA run.
        // Keep the native shutter/sleep path and remove unrelated scripted blockers.
        var player = PlayerStore.Instance;
        StoreClient current = player.currentClientInstance?.GetClientBlueprint();
        if (current != null && !current.dismissable)
        {
            Debug.Log("[PLAYER-QA] Dismissing guard-day scripted customer " + current.identifier);
            player.DismissCurrentClient(callNextWhenPossible: false);
        }
        var queue = player.storeClientManager?.clientStack;
        if (queue == null) return;
        var blocked = queue.Where(c => c != null && !c.dismissable).Select(c => c.identifier ?? "unknown").ToArray();
        if (blocked.Length == 0) return;
        queue.RemoveAll(c => c != null && !c.dismissable);
        Debug.Log("[PLAYER-QA] Removed guard-day scripted customer queue: " + string.Join(",", blocked));
    }
    private string DescribeState()
    {
        if (!PlayerStore.HasInstance) return "; no loaded player";
        try
        {
            var player = PlayerStore.Instance;
            StoreClient current = player.currentClientInstance?.GetClientBlueprint();
            var queue = player.storeClientManager?.clientStack;
            string blockers = queue == null ? "unavailable" : string.Join(",", queue.Where(c => c != null && !c.dismissable).Select(c => c.identifier ?? "unknown"));
            return "; day=" + StoreStation.GetDayCounter() + ", state=" + player.storeState + ", shutterLock=" + player.lockShutterButtonFlag +
                ", arrest=" + player.isClientBeingArrested + ", shootout=" + player.isHandlingShootout +
                ", current=" + (current == null ? "none" : (current.identifier ?? "unknown") + "/dismissable=" + current.dismissable) +
                ", blockedQueue=" + blockers + ", guardNight=" + guardNight;
        }
        catch (Exception error) { return "; state unavailable: " + error.Message; }
    }
    private static GameItem New(string id)
    {
        GameItem item = DirectoryMaster.Item(id); Require(item != null, "Missing QA item " + id);
        GeneralHelper.SetItemOwned(item); return item;
    }
    private static void Put(GameInventory target, GameItem item)
    {
        SlotMarker slot = target?.TryFindOneValidInventorySlot(item);
        Require(slot != null && ReferenceEquals(slot.inventory, target) && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, target),
            "Cannot place QA item " + item.identifier);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
