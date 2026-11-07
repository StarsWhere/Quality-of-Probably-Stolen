using System;
using System.IO;
using System.Linq;
using UnityEngine;

// Cold-loads only the QA run produced by MaintenanceScenario.
public sealed class MaintenanceReloadScenario
{
    private const string Mod = "starswhere.shopassist";
    private QaLoadSlot load;
    private string[] context;
    private GameItem pitcher, oldFilter, replacement;
    private GameSlotInventory filterSlot;
    private GameGridInventory source;
    private int step;
    private readonly float deadline = Time.realtimeSinceStartup + 100f;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "F31/F35 reload QA timed out at step " + step);
        if (step == 0)
        {
            context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-maintenance-run.txt")).Trim().Split('|');
            Guid run = Guid.Empty; int slot = -1;
            Need(context.Length == 9 && context[0] == "1" && Guid.TryParse(context[1], out run) &&
                int.TryParse(context[2], out slot) && context.Skip(3).All(s => int.TryParse(s, out _)), "Invalid isolated QA context");
            load = new QaLoadSlot(slot, run); step = 1; return false;
        }
        if (step == 1)
        {
            if (!load.Tick()) return false;
            Need(StoreStation.GetDayCounter() == int.Parse(context[3]), "QA reload restored the wrong day");
            var items = PlayerStore.Instance.FindAllItem();
            GameItem machine = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[4]));
            pitcher = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[5]));
            GameItem box = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[6]));
            GameItem battery = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[7]));
            oldFilter = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[8]));
            filterSlot = pitcher?.contentWindow?.childElement as GameSlotInventory;
            source = box?.contentWindow?.childElement as GameGridInventory;
            string rules = ModHelper.GetModData(Mod, "machineMaintenance", null);
            Need(machine?.identifier == "hydroponic" && pitcher?.identifier == "water_pitcher" && source != null &&
                battery != null && MachineHelper.GetBatterySlot(machine)?.childItem == battery && oldFilter != null && filterSlot?.childItem == oldFilter &&
                !string.IsNullOrEmpty(rules) && rules.Contains("\"host\":" + context[4]) && rules.Contains("\"host\":" + context[5]) &&
                rules.Contains("\"location\":\"item:" + context[6] + ":contents:-1\""), "Rules or exact exchanged items did not survive the native save and reload");
            var calendar = AdvCalendarUIManager.Instance;
            if (calendar?.UIPanel != null && calendar.UIPanel.activeSelf) StoreUIManager.Instance.storeCalendar.OnClick();
            DurabilityHelper.SetCurrentDurability(oldFilter, 0);
            replacement = DirectoryMaster.Item("water_filter"); GeneralHelper.SetItemOwned(replacement);
            var destination = source.TryFindOneValidInventorySlot(replacement);
            Need(destination != null && destination.inventory == source && destination.targetItem == null &&
                destination.TryAcceptOnce(replacement.unitCount) == replacement.unitCount && replacement.parentInventory == source,
                "Could not place QA reload filter into the saved source");
            int capacity = WaterHelper.GetCurrentCapacityML(pitcher);
            if (capacity > 50000) WaterHelper.RemoveMl(pitcher, 50000);
            capacity = WaterHelper.GetCurrentCapacityML(pitcher);
            WaterHelper.AddWater(pitcher, 5, 25000, true, 0, 1);
            Need(WaterHelper.GetCurrentCapacityML(pitcher) > capacity, "Native processing did not occur after reload");
            step = 2; return false;
        }
        if (step == 2)
        {
            if (filterSlot.childItem == oldFilter) return false;
            Need(filterSlot.childItem == replacement && oldFilter.parentInventory == source && DurabilityHelper.GetRealDurability(oldFilter) == 0 &&
                EmporiumEntry.Instance.swapBufferElement.childItem == null, "F35 did not use the saved source after reloading");
            result = "PASS F31/F35 same-slot cold reload preserves exact machines, parts and rules; F35 executes its restored rule";
            return true;
        }
        throw new InvalidOperationException("Unknown maintenance reload step");
    }
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
