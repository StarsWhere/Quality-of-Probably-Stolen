using System;
using System.IO;
using UnityEngine;

// Runs only in the isolated QA Player and its newly created native save.
public sealed class MaintenanceScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly string label = "QA maintenance " + Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly float deadline = Time.realtimeSinceStartup + 100f;
    private GameItem machine, oldBattery, spareBattery, otherBattery, pitcher, oldFilter, spareFilter, box, bufferBlocker;
    private GameSlotInventory powerSlot, filterSlot;
    private GameGridInventory source;
    private int step, foreignCalls;
    private float until;

    public bool Tick(out string result)
    {
        result = null;
        Require(Time.realtimeSinceStartup < deadline, "F31/F35 QA timed out at step " + step);
        if (step == 0)
        {
            if (!start.Tick()) return false;
            step = 1; return false;
        }
        if (step == 1)
        {
            if (StoreUIManager.Instance?.gameOptionUIManager == null || EmporiumEntry.Instance?.invElement == null ||
                EmporiumEntry.Instance.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;
            var calendar = AdvCalendarUIManager.Instance;
            if (calendar?.UIPanel != null && calendar.UIPanel.activeSelf) StoreUIManager.Instance.storeCalendar.OnClick();
            GameGridInventory stock = EmporiumEntry.Instance.invElement;
            stock.SetShape(24, 20);
            machine = New("hydroponic"); oldBattery = New("energy_credit"); spareBattery = New("energy_credit"); otherBattery = New("energy_credit");
            pitcher = New("water_pitcher"); oldFilter = New("water_filter"); spareFilter = New("water_filter"); box = New("backpack_medium");
            GeneralHelper.SetCustomName(box, label);
            foreach (GameItem item in new[] { machine, oldBattery, spareBattery, otherBattery, pitcher, oldFilter, spareFilter, box }) Put(stock, item);
            source = box.contentWindow?.childElement as GameGridInventory;
            powerSlot = MachineHelper.GetBatterySlot(machine);
            filterSlot = pitcher.contentWindow?.childElement as GameSlotInventory;
            Require(source != null && powerSlot != null && filterSlot != null, "QA native maintenance slots unavailable");
            PowerHelper.SetPowerSourceAt(oldBattery, 2); PowerHelper.SetPowerSourceAt(spareBattery, 100); PowerHelper.SetPowerSourceAt(otherBattery, 90);
            Put(powerSlot, oldBattery); Put(source, otherBattery); Put(source, spareBattery);
            Put(filterSlot, oldFilter); Put(source, spareFilter);
            DurabilityHelper.SetCurrentDurability(oldFilter, 1);
            ItemPinHelper.SetPinned(oldBattery, true); ItemPinHelper.SetPinned(spareFilter, true);
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(210, 100));
            ItemMouseDoubleClickHandler.current.OpenContentAction(pitcher, new Vector2(390, 100));
            ItemMouseDoubleClickHandler.current.OpenContentAction(box, new Vector2(570, 100));
            var opts = StoreUIManager.Instance.gameOptionUIManager;
            opts.OpenUI(); var options = CustomUIManager.Instance.GetWindow("game_options");
            Require(options != null, "Native options unavailable");
            options.Get(Mod + ":f15.enabled").SetBool(false, true);
            options.Get(Mod + ":f21.enabled").SetBool(false, true);
            options.Get(Mod + ":f31.enabled").SetBool(true, true);
            options.Get(Mod + ":f35.enabled").SetBool(true, true);
            options.Get("apply").button.onClick.Invoke(); opts.CloseUI();
            Configure(machine, "f31.open", 0);
            Configure(pitcher, "f35.open", 0);
            Require(!string.IsNullOrEmpty(ModHelper.GetModData(Mod, "machineMaintenance", null)), "Maintenance rules not saved");
            int before = WaterHelper.GetCurrentCapacityML(pitcher);
            WaterHelper.AddWater(pitcher, 5, 250000, true, 0, 1);
            Require(WaterHelper.GetCurrentCapacityML(pitcher) > before && DurabilityHelper.GetRealDurability(oldFilter) == 0,
                "Native water processing did not exhaust the installed filter");
            until = Time.realtimeSinceStartup + 2.5f; step = 2; return false;
        }
        if (step == 2)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Require(ReferenceEquals(powerSlot.childItem, oldBattery) && ReferenceEquals(spareBattery.parentInventory, source) &&
                ReferenceEquals(filterSlot.childItem, oldFilter) && ReferenceEquals(spareFilter.parentInventory, source),
                "Maintenance moved a pinned installed battery or pinned spare filter");
            ItemPinHelper.SetPinned(spareFilter, false);
            spareFilter.onSlotAddItemFunc += ForeignItemAdded;
            until = Time.realtimeSinceStartup + 2.5f; step = 3; return false;
        }
        if (step == 3)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Require(ReferenceEquals(filterSlot.childItem, oldFilter) && ReferenceEquals(spareFilter.parentInventory, source) && foreignCalls == 0,
                "F35 exchanged a filter with an unknown external item callback");
            spareFilter.onSlotAddItemFunc -= ForeignItemAdded;
            bufferBlocker = New("wine_yeast_red"); Put(EmporiumEntry.Instance.invElement, bufferBlocker);
            Put(EmporiumEntry.Instance.swapBufferElement, bufferBlocker);
            until = Time.realtimeSinceStartup + 2.5f; step = 4; return false;
        }
        if (step == 4)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Require(ReferenceEquals(filterSlot.childItem, oldFilter) && ReferenceEquals(spareFilter.parentInventory, source) &&
                ReferenceEquals(EmporiumEntry.Instance.swapBufferElement.childItem, bufferBlocker), "F35 moved parts with an occupied swap buffer");
            Put(EmporiumEntry.Instance.invElement, bufferBlocker);
            until = Time.realtimeSinceStartup + 8f; step = 5; return false;
        }
        if (step == 5)
        {
            if (ReferenceEquals(filterSlot.childItem, oldFilter) && Time.realtimeSinceStartup < until) return false;
            Require(ReferenceEquals(filterSlot.childItem, spareFilter) && ReferenceEquals(oldFilter.parentInventory, source) &&
                DurabilityHelper.GetRealDurability(oldFilter) == 0 && DurabilityHelper.GetRealDurability(spareFilter) > 0 &&
                EmporiumEntry.Instance.swapBufferElement.childItem == null, "F35 did not conserve and swap the two native filters");
            Require(ReferenceEquals(powerSlot.childItem, oldBattery), "F31 moved the pinned installed battery");
            ItemPinHelper.SetPinned(oldBattery, false);
            until = Time.realtimeSinceStartup + 8f; step = 6; return false;
        }
        if (step == 6)
        {
            if (ReferenceEquals(powerSlot.childItem, oldBattery) && Time.realtimeSinceStartup < until) return false;
            Require(ReferenceEquals(powerSlot.childItem, spareBattery) && ReferenceEquals(oldBattery.parentInventory, source) &&
                oldBattery.GetTagReadonly("power_source_item_energy").GetInt() == 2 &&
                spareBattery.GetTagReadonly("power_source_item_energy").GetInt() == 100 &&
                ReferenceEquals(otherBattery.parentInventory, source) && otherBattery.GetTagReadonly("power_source_item_energy").GetInt() == 90 &&
                EmporiumEntry.Instance.swapBufferElement.childItem == null, "F31 did not select and conserve the best battery");
            var player = PlayerStore.Instance;
            player.SaveGame();
            File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-maintenance-run.txt"),
                string.Join("|", "1", player.runID, player.saveSlotId, StoreStation.GetDayCounter(), machine.uniqueId,
                    pitcher.uniqueId, box.uniqueId, spareBattery.GetUniqueID(), spareFilter.GetUniqueID()));
            result = "PASS F31/F35 periodic conserved exchanges, pinned parts, external callback and occupied buffer guards";
            return true;
        }
        throw new InvalidOperationException("Unknown F31/F35 QA step");
    }

    private void Configure(GameItem host, string action, int condition)
    {
        host.contentWindow.ToFront(); RoutingScenario.RunFeatureAction(action);
        var window = CustomUIManager.Instance.GetWindow(Mod + ".maintenance");
        Require(window != null && window.IsVisible, "Maintenance window unavailable: " + action);
        QaItemUi.Choose(window, "source", label);
        window = CustomUIManager.Instance.GetWindow(Mod + ".maintenance");
        var choice = window?.Get("condition");
        Require(choice?.dropdown != null && condition >= 0 && condition < choice.dropdown.options.Count,
            "Maintenance condition unavailable: " + action);
        choice.SetIndex(condition, true); window.Get("enable").button.onClick.Invoke();
    }
    private void ForeignItemAdded(GameItem item, GameInventory inventory, SlotMarker slot) { foreignCalls++; }
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
