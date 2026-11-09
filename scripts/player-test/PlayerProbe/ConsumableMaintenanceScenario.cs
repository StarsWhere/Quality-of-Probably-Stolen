using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

public abstract class ConsumableMaintenanceFixture : EquipmentFixture
{
    protected const string SupplyName = "QA consumable supply";

    protected static GameGridInventory NewSupply(params GameItem[] items)
    {
        GameItem bag = Stock("backpack_medium");
        GeneralHelper.SetCustomName(bag, SupplyName);
        var source = (GameGridInventory)bag.contentWindow.childElement;
        foreach (GameItem item in items) FeedScenario.Put(source, item);
        Open(bag);
        return source;
    }

    protected static void Configure(GameItem host, int kind, int condition)
    {
        Open(host); host.contentWindow.ToFront(); Action("f" + kind + ".open");
        var window = Ui("maintenance");
        Check(window?.IsAlive == true, "Maintenance settings unavailable for " + host.identifier);
        QaItemUi.Choose(window, "source", SupplyName);
        window = Ui("maintenance");
        Check(window?.Get("condition")?.dropdown != null && window.Get("enable")?.button?.interactable == true,
            "Maintenance condition or save control unavailable for " + host.identifier);
        window.Get("condition").SetIndex(condition, true);
        window.Get("enable").button.onClick.Invoke();
        Check(window.IsAlive != true, "Maintenance settings did not save for " + host.identifier);
    }

    protected static void Conserved(GameGridInventory source, IEnumerable<GameSlotInventory> seats, params GameItem[] expected)
    {
        var items = source.childItems.Concat(seats.Select(seat => seat.childItem).Where(item => item != null)).ToList();
        GameItem buffered = EmporiumEntry.Instance.swapBufferElement.childItem;
        if (buffered != null) items.Add(buffered);
        Check(buffered == null && items.Count == expected.Length && items.Distinct().Count() == expected.Length &&
            new HashSet<GameItem>(items).SetEquals(expected), "Native consumable instances or swap buffer were not conserved");
        foreach (GameItem item in source.childItems) Check(ReferenceEquals(item.parentInventory, source), "Source item has the wrong parent");
        foreach (GameSlotInventory seat in seats)
            if (seat.childItem != null) Check(ReferenceEquals(seat.childItem.parentInventory, seat), "Installed item has the wrong parent");
    }
}

public sealed class BatteryPriorityScenario : ConsumableMaintenanceFixture
{
    private readonly float deadline = Time.realtimeSinceStartup + 100f;
    private GameItem urgent, ordinary, pinned, urgentBattery, ordinaryBattery, pinnedBattery, spare;
    private GameSlotInventory urgentSeat, ordinarySeat, pinnedSeat;
    private GameGridInventory source;
    private float until;

    public bool Tick(out string result)
    {
        result = null;
        Check(Time.realtimeSinceStartup < deadline, "Battery priority QA timed out at step " + step);
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(false, "f15.enabled", "f21.enabled", "f35.enabled");
            TradeScenario.SetOptions(true, "f31.enabled");
            EmporiumEntry.Instance.invElement.SetShape(32, 24);
            var hosts = new[] { "hydroponic", "printer", "furnace", "evaporator" }.Select(Stock).ToArray();
            urgent = hosts.OrderByDescending(MachineryHelper.GetMachinePowerUsage).First();
            ordinary = hosts.OrderBy(MachineryHelper.GetMachinePowerUsage).First();
            int high = MachineryHelper.GetMachinePowerUsage(urgent), low = MachineryHelper.GetMachinePowerUsage(ordinary);
            int urgentEnergy = 2 * high - 1, ordinaryEnergy = 2 * low;
            Check(high > low && low > 0 && ordinaryEnergy < urgentEnergy && urgentEnergy < 100,
                "Native machines cannot form distinct priority tiers: " + string.Join(", ", hosts.Select(host => host.identifier + "=" + MachineryHelper.GetMachinePowerUsage(host))));
            pinned = Stock(urgent.identifier);
            urgentBattery = Stock("energy_credit"); ordinaryBattery = Stock("energy_credit");
            pinnedBattery = Stock("energy_credit"); spare = Stock("energy_credit");
            urgentSeat = MachineHelper.GetBatterySlot(urgent); ordinarySeat = MachineHelper.GetBatterySlot(ordinary);
            pinnedSeat = MachineHelper.GetBatterySlot(pinned);
            Check(urgentSeat != null && ordinarySeat != null && pinnedSeat != null, "Native battery slots unavailable");
            PowerHelper.SetPowerSourceAt(urgentBattery, urgentEnergy); PowerHelper.SetPowerSourceAt(ordinaryBattery, ordinaryEnergy);
            PowerHelper.SetPowerSourceAt(pinnedBattery, 1); PowerHelper.SetPowerSourceAt(spare, 100);
            FeedScenario.Put(urgentSeat, urgentBattery); FeedScenario.Put(ordinarySeat, ordinaryBattery); FeedScenario.Put(pinnedSeat, pinnedBattery);
            ItemPinHelper.SetPinned(pinnedBattery, true); ItemPinHelper.SetPinned(spare, true);
            source = NewSupply(spare);
            Configure(ordinary, 31, 0); Configure(pinned, 31, 0); Configure(urgent, 31, 0);
            ItemPinHelper.SetPinned(spare, false);
            until = Time.realtimeSinceStartup + 8f; step = 1; return false;
        }
        if (ReferenceEquals(urgentSeat.childItem, urgentBattery) && Time.realtimeSinceStartup < until) return false;
        Check(ReferenceEquals(urgentSeat.childItem, spare) && ReferenceEquals(pinnedSeat.childItem, pinnedBattery),
            "Shared battery did not go to the under-two-cycle machine or a pinned installed battery moved");
        Conserved(source, new[] { urgentSeat, ordinarySeat, pinnedSeat }, urgentBattery, ordinaryBattery, pinnedBattery, spare);
        result = "PASS shared battery priority below two runs, pinned installed battery and all native instances conserved";
        return true;
    }
}

public abstract class FilterConditionScenario : ConsumableMaintenanceFixture
{
    private readonly bool ras, best;
    private readonly float deadline = Time.realtimeSinceStartup + 100f;
    private GameItem host, installed, inferior, better;
    private GameSlotInventory seat;
    private GameSlotInventory[] seats;
    private GameGridInventory source;
    private float until;

    protected FilterConditionScenario(bool ras, bool best) { this.ras = ras; this.best = best; }

    public bool Tick(out string result)
    {
        result = null;
        Check(Time.realtimeSinceStartup < deadline, "Filter condition QA timed out at step " + step);
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(false, "f15.enabled", "f21.enabled", "f31.enabled");
            TradeScenario.SetOptions(true, "f30.enabled", "f35.enabled");
            EmporiumEntry.Instance.invElement.SetShape(24, 20);
            host = Stock(ras ? "ras" : "water_pitcher");
            installed = Stock("water_filter"); inferior = Stock("water_filter");
            if (ras)
            {
                var grid = MachineRAS.GetFilterGrid(host);
                seats = Enumerable.Range(0, 4).Select(i => (GameSlotInventory)grid.GetElement(i, 0)).ToArray();
                seat = seats[1];
            }
            else { seat = (GameSlotInventory)host.contentWindow.childElement; seats = new[] { seat }; }
            DurabilityHelper.SetCurrentDurability(installed, 10); DurabilityHelper.SetCurrentDurability(inferior, 5);
            Check(DurabilityHelper.GetRealDurability(installed) > DurabilityHelper.GetRealDurability(inferior) &&
                DurabilityHelper.GetRealDurability(inferior) > 0, "Native filter durability fixture unavailable");
            FeedScenario.Put(seat, installed); source = NewSupply(inferior);
            Configure(host, 35, best ? 1 : 0);
            until = Time.realtimeSinceStartup + 2.5f; step = 1; return false;
        }
        if (step == 1)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Check(ReferenceEquals(seat.childItem, installed) && ReferenceEquals(inferior.parentInventory, source),
                "Filter changed with no better eligible spare");
            Conserved(source, seats, installed, inferior);
            Action("f30.open");
            var overview = Ui("f30"); Check(overview?.IsAlive == true, "Machine overview unavailable");
            string text = Words(overview);
            Check(!text.Contains(ModHelper.GetLocalized(Mod, "f35.missing")) &&
                !text.Contains(ModHelper.GetLocalized(Mod, "f35.pending")) &&
                !text.Contains(ModHelper.GetLocalized(Mod, "maintenance.swap_unavailable")),
                "Healthy filter without a better spare produced a maintenance warning: " + text);
            overview.Close();
            var assembly = ModLoader.Mods.Single(mod => mod.Manifest.ID == Mod).Assembly;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
            var type = assembly.GetType("ShopAssist.MachineTools", true);
            object active = type.GetField("current", flags).GetValue(null);
            var problems = (IDictionary)type.GetField("maintenanceProblems", flags).GetValue(active);
            Check(!problems.Contains("35:" + host.uniqueId), "No-op filter created a maintenance problem");
            if (best)
            {
                better = Stock("water_filter"); DurabilityHelper.SetCurrentDurability(better, 20);
                Check(DurabilityHelper.GetRealDurability(better) > DurabilityHelper.GetRealDurability(installed), "Better filter fixture unavailable");
                FeedScenario.Put(source, better);
            }
            else DurabilityHelper.SetCurrentDurability(installed, 0);
            until = Time.realtimeSinceStartup + 8f; step = 2; return false;
        }
        GameItem replacement = best ? better : inferior;
        if (ReferenceEquals(seat.childItem, installed) && Time.realtimeSinceStartup < until) return false;
        Check(ReferenceEquals(seat.childItem, replacement) && ReferenceEquals(installed.parentInventory, source),
            "Filter was not replaced under the configured condition: " + MaintenanceState());
        Check(seats.Where(value => !ReferenceEquals(value, seat)).All(value => value.childItem == null), "An unrelated RAS filter seat changed");
        if (best) Conserved(source, seats, installed, inferior, better);
        else Conserved(source, seats, installed, inferior);
        result = "PASS " + (ras ? "RAS" : "pitcher") + " " + (best ? "best remaining" : "exhausted") +
            " condition, no false warning and conserved native filter instances";
        return true;
    }
}

public sealed class PitcherExhaustedScenario : FilterConditionScenario { public PitcherExhaustedScenario() : base(false, false) { } }
public sealed class PitcherBestScenario : FilterConditionScenario { public PitcherBestScenario() : base(false, true) { } }
public sealed class RasExhaustedScenario : FilterConditionScenario { public RasExhaustedScenario() : base(true, false) { } }
public sealed class RasBestScenario : FilterConditionScenario { public RasBestScenario() : base(true, true) { } }
