using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Runs the machine's own cycle callback repeatedly on a new QA save; this does not simulate a whole night.
public sealed class ReplantScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem machine, seed, battery, water, geneScanner;
    private GameSlotInventory input;
    private int cycles, step;
    private bool resetContext;
    private float until;
    private float deadline = Time.realtimeSinceStartup + 90;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public bool Tick(out string result)
    {
        result = null;
        Require(Time.realtimeSinceStartup < deadline, "F32 QA timed out at step " + step);
        if (step == 0)
        {
            if (!resetContext) { File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-replant-run.txt"), "pending"); resetContext = true; }
            if (!start.Tick()) return false;
            until = Time.realtimeSinceStartup + 4; step++; return false;
        }
        if (step == 1)
        {
            if (Time.realtimeSinceStartup < until) return false;
            if (EmporiumEntry.Instance == null || StoreUIManager.Instance?.gameOptionUIManager == null ||
                EmporiumEntry.Instance.storeInteractables == null || !EmporiumEntry.Instance.storeInteractables.activeInHierarchy ||
                ItemMouseDoubleClickHandler.current == null) return false;
            var stock = EmporiumEntry.Instance.invElement;
            machine = New("hydroponic"); seed = New("hydroreed_seed");
            battery = New("energy_credit_dis"); water = New("wine_bottle"); geneScanner = New("gene_scanner");
            PlantGeneticsHelper.SetTraitRank(seed, "fast_grower", 1);
            Put(stock, machine); Put(stock, seed); Put(stock, battery); Put(stock, water); Put(stock, geneScanner);
            WaterHelper.AddWater(water, 2, 750000);
            input = MachineHydroponic.GetInputSlot(machine);
            Put(input, seed); Put(MachineHelper.GetBatterySlot(machine), battery); Put(MachineHydroponic.GetWaterSlot(machine), water);
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(260, 140));
            machine.contentWindow.ToFront();
            TradeScenario.SetOptions(true, "f32.enabled");
            RoutingScenario.OpenOptionAction("f32.open");
            var selector = CustomUIManager.Instance.GetWindow(Mod + ".f32");
            Require(selector?.IsAlive == true && selector.root.GetComponentsInChildren<Transform>(true).Any(t => t.name == "ShopAssist_ItemCards"),
                "F32 did not show actual item cards");
            var seedSprite = (seed as GameItemElement)?.background?.image?.sprite;
            Require(seedSprite != null && selector.root.GetComponentsInChildren<Image>(true).Any(image => image.sprite == seedSprite),
                "F32 card did not display the real seed sprite");
            if (!PlantGeneticsHelper.IsScannerOn())
            {
                string traitName = PlantTraitRegistry.Get("fast_grower")?.displayName;
                Require(string.IsNullOrEmpty(traitName) || !Text(selector).Contains(traitName), "F32 revealed a gene before scanning");
            }
            var overlay = StoreUIManager.Instance.controlHintOverlay;
            if (overlay != null && overlay.activeSelf) StoreUIManager.Instance.ToggleControlHintOverlay();
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-replant-cards.png"));
            until = Time.realtimeSinceStartup + 1; step = 10; return false;
        }
        if (step == 10)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Require(geneScanner.onToggleSlotItemFunc != null, "Native gene scanner toggle unavailable");
            geneScanner.onToggleSlotItemFunc(geneScanner, geneScanner.parentInventory, null);
            Require(PlantGeneticsHelper.IsScannerOn(), "Native gene scanner did not turn on");
            step = 12; return false;
        }
        if (step == 12)
        {
            var selector = CustomUIManager.Instance.GetWindow(Mod + ".f32");
            string trait = PlantTraitRegistry.Get("fast_grower")?.displayName ?? "fast_grower";
            if (selector?.IsAlive != true || !Text(selector).Contains(trait)) return false;
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-replant-scanned.png"));
            until = Time.realtimeSinceStartup + 1; step = 13; return false;
        }
        if (step == 13)
        {
            if (Time.realtimeSinceStartup < until) return false;
            geneScanner.onToggleSlotItemFunc(geneScanner, geneScanner.parentInventory, null);
            Require(!PlantGeneticsHelper.IsScannerOn(), "Native gene scanner did not turn off");
            step = 14; return false;
        }
        if (step == 14)
        {
            var selector = CustomUIManager.Instance.GetWindow(Mod + ".f32");
            string trait = PlantTraitRegistry.Get("fast_grower")?.displayName ?? "fast_grower";
            if (selector?.IsAlive != true || Text(selector).Contains(trait)) return false;
            LayoutScenario.Select(selector, seed);
            selector = CustomUIManager.Instance.GetWindow(Mod + ".f32");
            Require(Text(selector).Contains(ModHelper.GetLocalized(Mod, "f32.selected")), "The chosen seed is not labelled");
            Require(!string.IsNullOrEmpty(ModHelper.GetModData(Mod, "f32.rules", null)), "F32 per-machine rule was not saved to QA run data");
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-replant-selected.png"));
            until = Time.realtimeSinceStartup + 1; step = 11; return false;
        }
        if (step == 11)
        {
            if (Time.realtimeSinceStartup < until) return false;
            CustomUIManager.Instance.GetWindow(Mod + ".f32")?.Close();
            step = 2; return false;
        }
        if (step == 2)
        {
            Require(machine.onCycleEndSlotItemFunc != null && MachineHelper.CanPower(machine), "Native hydroponic cycle is unavailable");
            GameInventory parent = machine.parentInventory;
            machine.onCycleEndSlotItemFunc(machine, parent, parent.GetIncompleteItemInventorySlot(machine));
            cycles++;
            GameItem planted = input.childItem;
            if (ReferenceEquals(planted, seed))
            {
                Require(cycles < 100 && MachineHelper.CanPower(machine) && WaterHelper.GetCurrentCapacityML(water) > 0,
                    "Hydroponic fixture did not finish within real battery/water resources");
                return false;
            }
            step = 3;
            return false;
        }
        if (step == 3)
        {
            GameItem planted = input.childItem;
            if (planted == null) return false;
            Require(planted != null && planted.unitCount == 1 && GeneralHelper.IsItemOwned(planted) &&
                HydroponicHelper.GetPlantId(planted) == "hydroreed_seed" &&
                PlantGeneticsHelper.GetTraitRank(planted, "fast_grower") == 1 &&
                MachineProgressHelper.GetMachineState(machine) == MachineProgressHelper.STATE_READY,
                "F32 did not replant the original harvested strain once: input=" + planted?.identifier + " cycles=" + cycles);
            Require(MachineHydroponic.GetOutputSlot(machine).childItems.All(i => !ReferenceEquals(i, planted)),
                "Replanted seed is still in the output");
            int machineId = machine.GetUniqueID(), plantedId = planted.GetUniqueID();
            var p = PlayerStore.Instance;
            File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-replant-run.txt"),
                string.Join("|", "1", p.runID, p.saveSlotId, machineId, plantedId));
            p.SaveGame();
            result = "PASS F32 configured through native options and replanted one inherited-genetics seed after real hydroponic cycles";
            return true;
        }
        throw new InvalidOperationException("Unknown F32 step");
    }
    private static string Text(CustomUIWindow window) => string.Join(" | ",
        window.root.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text));
    private static GameItem New(string id) { var item = DirectoryMaster.Item(id); Require(item != null, "Missing " + id); GeneralHelper.SetItemOwned(item); return item; }
    private static void Put(GameInventory target, GameItem item)
    {
        var slot = target.TryFindOneValidInventorySlot(item);
        Require(slot != null && ReferenceEquals(slot.inventory, target) && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, target), "Cannot place " + item.identifier);
    }
}

public sealed class ReplantReloadScenario
{
    private const string Mod = "starswhere.shopassist";
    private QaLoadSlot load;
    private string[] context;
    public bool Tick(out string result)
    {
        result = null;
        if (load == null)
        {
            context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-replant-run.txt")).Split('|');
            if (context.Length != 5 || context[0] != "1" || !Guid.TryParse(context[1], out Guid run) ||
                !int.TryParse(context[2], out int slot)) throw new InvalidOperationException("Invalid F32 QA run context");
            load = new QaLoadSlot(slot, run); return false;
        }
        if (!load.Tick()) return false;
        var all = GraphUtils.FindChildrenType<GameItem>(EmporiumEntry.Instance.invElement, passthrough: _ => true);
        var machine = all.SingleOrDefault(item => item.uniqueId == int.Parse(context[3]));
        var seed = all.SingleOrDefault(item => item.uniqueId == int.Parse(context[4]));
        string rules = ModHelper.GetModData(Mod, "f32.rules", null);
        if (machine == null || seed == null || !ReferenceEquals(MachineHydroponic.GetInputSlot(machine).childItem, seed) ||
            PlantGeneticsHelper.GetTraitRank(seed, "fast_grower") != 1 || string.IsNullOrEmpty(rules) || !rules.Contains(context[3]))
            throw new InvalidOperationException("F32 seed or rule did not persist across restart");
        result = "PASS F32 machine rule and replanted strain survive cold reload";
        return true;
    }
}
