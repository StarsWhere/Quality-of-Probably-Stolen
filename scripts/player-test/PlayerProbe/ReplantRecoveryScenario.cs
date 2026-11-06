using System;
using System.IO;
using System.Linq;
using UnityEngine;

// Exercises queued replanting on native hydroponic callbacks in a fresh QA run.
public sealed class ReplantRecoveryScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem machine, original, battery, water, selected;
    private GameSlotInventory input;
    private int step, cycles, waitFrames;
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Rules() => ModHelper.GetModData(Mod, "f32.rules", null) ?? "";

    public bool Tick(out string result)
    {
        result = null;
        Require(Time.realtimeSinceStartup < deadline, "Replant recovery timed out at step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance == null || StoreUIManager.Instance?.gameOptionUIManager == null || ItemMouseDoubleClickHandler.current == null) return false;
            var stock = EmporiumEntry.Instance.invElement;
            machine = MachineFeatureQa.Item("hydroponic"); original = MachineFeatureQa.Item("hydroreed_seed");
            battery = MachineFeatureQa.Item("energy_credit_dis"); water = MachineFeatureQa.Item("wine_bottle");
            PlantGeneticsHelper.SetTraitRank(original, "fast_grower", 1);
            MachineFeatureQa.Put(stock, machine); MachineFeatureQa.Put(stock, original);
            MachineFeatureQa.Put(stock, battery); MachineFeatureQa.Put(stock, water);
            WaterHelper.AddWater(water, 2, 750000);
            input = MachineHydroponic.GetInputSlot(machine);
            MachineFeatureQa.Put(input, original); MachineFeatureQa.Put(MachineHelper.GetBatterySlot(machine), battery);
            MachineFeatureQa.Put(MachineHydroponic.GetWaterSlot(machine), water);
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(260, 140)); machine.contentWindow.ToFront();
            TradeScenario.SetOptions(true, "f32.enabled"); RoutingScenario.OpenOptionAction("f32.open");
            var selector = CustomUIManager.Instance.GetWindow(Mod + ".f32");
            Require(selector?.IsAlive == true, "Replant selector unavailable");
            LayoutScenario.Select(selector, original); CustomUIManager.Instance.GetWindow(Mod + ".f32")?.Close();
            step++; return false;
        }
        if (step == 2)
        {
            Require(machine.onCycleEndSlotItemFunc != null && MachineHelper.CanPower(machine), "Hydroponic fixture cannot run");
            GameInventory parent = machine.parentInventory;
            machine.onCycleEndSlotItemFunc(machine, parent, parent.GetIncompleteItemInventorySlot(machine));
            if (ReferenceEquals(input.childItem, original)) { Require(++cycles < 100, "Crop did not finish"); return false; }
            Require(input.childItem == null, "Replanting ran before the deferred safe frame");
            var output = MachineHydroponic.GetOutputSlot(machine);
            var seeds = output.childItems.Where(item => item != null && item.IsTag("SEED_ITEM_TAG") &&
                HydroponicHelper.GetPlantId(item) == "hydroreed_seed" && PlantGeneticsHelper.GetTraitRank(item, "fast_grower") == 1).ToArray();
            Require(seeds.Length > 0 && Rules().Contains("\"version\":2") && Rules().Contains("\"state\":1"), "Harvest did not persist a pending task");
            foreach (GameItem item in seeds) item.unitCount = 2; // Test the native quantity-change event without moving or splitting items.
            selected = seeds[0]; step++; return false;
        }
        if (step == 3)
        {
            if (!Rules().Contains("f32.stack")) return false;
            Require(input.childItem == null, "Stacked seed was moved");
            TradeScenario.SetOptions(false, "f32.enabled"); selected.unitCount = 1;
            waitFrames = 3; step++; return false;
        }
        if (step == 4)
        {
            Require(input.childItem == null, "A disabled task replanted automatically");
            if (--waitFrames > 0) return false;
            var player = PlayerStore.Instance;
            int machineId = machine.GetUniqueID(), seedId = selected.GetUniqueID();
            File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-replant-recovery.txt"),
                string.Join("|", player.runID, player.saveSlotId, machineId, seedId));
            player.SaveGame();
            TradeScenario.SetOptions(true, "f32.enabled"); step++; return false;
        }
        if (step == 5)
        {
            if (input.childItem == null) return false;
            Require(ReferenceEquals(input.childItem, selected) && selected.unitCount == 1 && Rules().Contains("\"state\":0"),
                "Enabling did not replant the matching seed exactly once");
            waitFrames = 3; step++; return false;
        }
        if (step == 6)
        {
            Require(ReferenceEquals(input.childItem, selected), "A repeated event replaced the replanted seed");
            if (--waitFrames > 0) return false;
            string pending = Rules().Replace("\"state\":0", "\"state\":1").Replace("\"reason\":\"\"", "\"reason\":\"f32.pending\"");
            ModHelper.SetModData(Mod, "f32.rules", pending); selected.unitCount = 1;
            step++; return false;
        }
        if (step == 7)
        {
            if (!Rules().Contains("\"state\":0")) return false;
            Require(ReferenceEquals(input.childItem, selected), "Pending task replaced a manually occupied input");
            string inspect = Rules().Replace("\"state\":0", "\"state\":2").Replace("\"reason\":\"\"", "\"reason\":\"f32.inspect\"");
            ModHelper.SetModData(Mod, "f32.rules", inspect); selected.unitCount = 1;
            waitFrames = 3; step++; return false;
        }
        if (step == 8)
        {
            Require(Rules().Contains("\"state\":2") && ReferenceEquals(input.childItem, selected), "Inspection state was retried or cleared without confirmation");
            if (--waitFrames > 0) return false;
            machine.contentWindow.ToFront(); RoutingScenario.OpenOptionAction("f32.open");
            var view = CustomUIManager.Instance.GetWindow(Mod + ".f32");
            Require(view?.Get("resolve")?.button != null, "Inspection has no explicit resolution action");
            view.Get("resolve").button.onClick.Invoke();
            Require(Rules().Contains("\"state\":0"), "Confirming inspection did not clear its state");
            result = "PASS queued replant recovery, disabled pause, manual input and inspection acknowledgement"; return true;
        }
        throw new InvalidOperationException("Unknown recovery step");
    }
}

public sealed class ReplantRecoveryReloadScenario
{
    private const string Mod = "starswhere.shopassist";
    private QaLoadSlot load;
    private string[] context;
    private bool enabled;
    private readonly float deadline = Time.realtimeSinceStartup + 120f;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup >= deadline) throw new InvalidOperationException("Pending replant reload timed out");
        if (load == null)
        {
            context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-replant-recovery.txt")).Split('|');
            if (context.Length != 4 || !Guid.TryParse(context[0], out Guid run) || !int.TryParse(context[1], out int slot))
                throw new InvalidOperationException("Invalid replant recovery context");
            load = new QaLoadSlot(slot, run); return false;
        }
        if (!load.Tick()) return false;
        if (!enabled) { TradeScenario.SetOptions(true, "f32.enabled"); enabled = true; return false; }
        var all = PlayerStore.Instance.FindAllItem();
        var machine = all.SingleOrDefault(item => item.uniqueId == int.Parse(context[2]));
        var seed = all.SingleOrDefault(item => item.uniqueId == int.Parse(context[3]));
        if (machine == null || seed == null) throw new InvalidOperationException("Pending replant fixtures missing after load");
        if (!ReferenceEquals(MachineHydroponic.GetInputSlot(machine)?.childItem, seed)) return false;
        string rules = ModHelper.GetModData(Mod, "f32.rules", null) ?? "";
        if (!rules.Contains("\"state\":0") || seed.unitCount != 1) throw new InvalidOperationException("Reload did not resolve pending replant safely");
        result = "PASS pending replant resumes once after loading the same run and slot"; return true;
    }
}
