using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

// Exercises queued replanting on native hydroponic callbacks in a fresh QA run.
public sealed class ReplantRecoveryScenario
{
    [Serializable] internal sealed class Gene { public string id = null; public int rank = 0; }
    [Serializable] internal sealed class Rule { public int machine = 0, state = 0; public string plant = null, reason = null; public Gene[] genes = null; }
    [Serializable] private sealed class RuleBook { public int version = 0, slot = 0; public string run = null; public Rule[] rules = null; }
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem machine, original, battery, water, selected;
    private GameSlotInventory input;
    private int step, cycles, waitFrames;
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Rules() => ModHelper.GetModData(Mod, "f32.rules", null) ?? "";

    internal static Rule ReadRule(GameItem machine)
    {
        string raw = Rules();
        var book = string.IsNullOrEmpty(raw) ? null : JsonUtility.FromJson<RuleBook>(raw);
        var player = PlayerStore.Instance;
        Require(book != null && book.version == 3 && book.run == player.runID && book.slot == player.saveSlotId && book.rules != null,
            "Invalid replant rule book for this save: " + raw);
        Rule[] matches = book.rules.Where(rule => rule != null && rule.machine == machine.uniqueId).ToArray();
        Require(matches.Length == 1, "Expected one rule for machine " + machine.uniqueId + ": " + raw);
        return matches[0];
    }

    private static void SetState(GameItem machine, int state, string reason)
    {
        var assembly = ModLoader.Mods.Single(mod => mod.Manifest?.ID == Mod && mod.Instance != null).Instance.GetType().Assembly;
        Type bookType = assembly.GetType("ShopAssist.MachineTools", true).GetNestedType("SeedRules", BindingFlags.NonPublic);
        Type dataType = assembly.GetType("ShopAssist.ShopData", true);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        object book = dataType.GetMethod("Decode", flags).MakeGenericMethod(bookType).Invoke(null, new object[] { Rules(), 65536 });
        var rows = (IEnumerable)bookType.GetField("rules").GetValue(book);
        object rule = rows.Cast<object>().Single(row => (int)row.GetType().GetField("machine").GetValue(row) == machine.uniqueId);
        rule.GetType().GetField("state").SetValue(rule, state);
        rule.GetType().GetField("reason").SetValue(rule, reason);
        string raw = (string)dataType.GetMethod("Encode", flags).MakeGenericMethod(bookType).Invoke(null, new[] { book });
        ModHelper.SetModData(Mod, "f32.rules", raw);
    }

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
            TradeScenario.SetOptions(true, "f32.enabled", "f07.enabled"); RoutingScenario.RunFeatureAction("f32.open");
            var selector = CustomUIManager.Instance.GetWindow(Mod + ".f32");
            Require(selector?.IsAlive == true, "Replant selector unavailable");
            LayoutScenario.Select(selector, original); CustomUIManager.Instance.GetWindow(Mod + ".f32")?.Close();
            Rule configured = ReadRule(machine);
            Require(configured.state == 0 && configured.plant == "hydroreed_seed" &&
                configured.genes != null && configured.genes.Any(gene => gene.id == "fast_grower" && gene.rank == 1),
                "The selected strain was not recorded for this machine: " + Rules());
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
            Require(seeds.Length > 0, "Harvest did not produce a matching seed; output=" +
                string.Join(", ", output.childItems.Select(item => item?.identifier ?? "null")));
            Rule pending = ReadRule(machine);
            Require(pending.state == 1 && pending.reason == "f32.pending" && pending.plant == "hydroreed_seed" &&
                pending.genes != null && pending.genes.Any(gene => gene.id == "fast_grower" && gene.rank == 1),
                "Harvest did not persist the machine's pending task: " + Rules());
            foreach (GameItem item in seeds) item.unitCount = 2; // Test the native quantity-change event without moving or splitting items.
            selected = seeds[0]; step++; return false;
        }
        if (step == 3)
        {
            Rule waiting = ReadRule(machine);
            if (waiting.state != 1 || waiting.reason != "f32.stack") return false;
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
            Require(ReferenceEquals(input.childItem, selected) && selected.unitCount == 1 && ReadRule(machine).state == 0,
                "Enabling did not replant the matching seed exactly once");
            waitFrames = 3; step++; return false;
        }
        if (step == 6)
        {
            Require(ReferenceEquals(input.childItem, selected), "A repeated event replaced the replanted seed");
            if (--waitFrames > 0) return false;
            SetState(machine, 1, "f32.pending"); selected.unitCount = 1;
            step++; return false;
        }
        if (step == 7)
        {
            if (ReadRule(machine).state != 0) return false;
            Require(ReferenceEquals(input.childItem, selected), "Pending task replaced a manually occupied input");
            SetState(machine, 2, "f32.inspect"); selected.unitCount = 1;
            waitFrames = 3; step++; return false;
        }
        if (step == 8)
        {
            Require(ReadRule(machine).state == 2 && ReferenceEquals(input.childItem, selected), "Inspection state was retried or cleared without confirmation");
            if (--waitFrames > 0) return false;
            machine.contentWindow.ToFront(); RoutingScenario.RunFeatureAction("f32.open");
            var view = CustomUIManager.Instance.GetWindow(Mod + ".f32");
            Require(view?.Get("resolve")?.button != null, "Inspection has no explicit resolution action");
            view.Get("resolve").button.onClick.Invoke();
            Require(ReadRule(machine).state == 0, "Confirming inspection did not clear its state");
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

    private static void VerifyRoutingPermissions(GameItem machine)
    {
        var assembly = ModLoader.Mods.Single(mod => mod.Manifest?.ID == Mod && mod.Instance != null).Instance.GetType().Assembly;
        Type routesType = assembly.GetType("ShopAssist.InventoryRoutes", true);
        const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        object routes = routesType.GetField("current", staticFlags).GetValue(null);
        object store = routesType.GetField("store", instanceFlags).GetValue(routes);
        if (!(bool)store.GetType().GetMethod("Load", instanceFlags).Invoke(store, null))
            throw new InvalidOperationException("Could not read routing rules for replant source check");
        object book = store.GetType().GetField("book", instanceFlags).GetValue(store);
        var rules = (IList)book.GetType().GetField("rules").GetValue(book);
        Type inventoryType = assembly.GetType("ShopAssist.ShopInventory", true);
        var locations = (IEnumerable)inventoryType.GetMethod("MachineLocations", staticFlags).Invoke(null, null);
        GameGridInventory output = MachineHydroponic.GetOutputSlot(machine);
        object location = locations.Cast<object>().SingleOrDefault(row =>
            ReferenceEquals(row.GetType().GetField("Inventory", instanceFlags).GetValue(row), output));
        if (location == null || machine.contentWindow.IsVisible())
            throw new InvalidOperationException("Closed replant output is not an identifiable owned machine location");
        string key = (string)location.GetType().GetMethod("Key", instanceFlags).Invoke(location, new object[] { false });
        MethodInfo allowed = routesType.GetMethod("AutoTakeAllowed", staticFlags);
        bool CanTake(GameInventory source) => (bool)allowed.Invoke(null, new object[] { source });
        if (!CanTake(output)) throw new InvalidOperationException("F07 rejected a closed owned hydroponic output");

        Type ruleType = assembly.GetType("ShopAssist.RoutingRule", true);
        object rule = Activator.CreateInstance(ruleType, true);
        FieldInfo ruleKey = ruleType.GetField("location"), ship = ruleType.GetField("ship");
        GameItem other = null; int otherId = 0;
        try
        {
            ruleKey.SetValue(rule, "root:stock"); ship.SetValue(rule, false); rules.Add(rule);
            if (CanTake(EmporiumEntry.Instance.invElement)) throw new InvalidOperationException("F07 ignored the stock shipping prohibition");
            ruleKey.SetValue(rule, key);
            if (CanTake(output)) throw new InvalidOperationException("F07 ignored the closed machine output shipping prohibition");
            ship.SetValue(rule, true);
            if (!CanTake(output)) throw new InvalidOperationException("F07 rejected an allowed machine output");
            rules.Remove(rule);

            other = DirectoryMaster.Item("hydroponic");
            if (other == null) throw new InvalidOperationException("Missing independent hydroponic fixture");
            GeneralHelper.SetItemOwned(other, false);
            GameGridInventory otherOutput = MachineHydroponic.GetOutputSlot(other);
            if (otherOutput == null || CanTake(otherOutput)) throw new InvalidOperationException("F07 accepted an unknown machine output");
            GeneralHelper.SetItemOwned(other, true); MachineFeatureQa.ReturnToShop(other); otherId = other.GetUniqueID();
            GeneralHelper.SetItemOwned(other, false);
            if (CanTake(otherOutput)) throw new InvalidOperationException("F07 accepted a non-owned machine output");
            GeneralHelper.SetItemOwned(other, true); other.uniqueId = machine.uniqueId;
            var duplicates = (IEnumerable)inventoryType.GetMethod("MachineLocations", staticFlags).Invoke(null, null);
            int duplicateCount = duplicates.Cast<object>().Count(row =>
                (string)row.GetType().GetMethod("Key", instanceFlags).Invoke(row, new object[] { false }) == key);
            if (duplicateCount != 2) throw new InvalidOperationException("Duplicate machine source fixture is unavailable: " + duplicateCount);
            if (CanTake(output)) throw new InvalidOperationException("F07 accepted duplicate machine source identities");
        }
        finally
        {
            if (rules.Contains(rule)) rules.Remove(rule);
            if (other != null) { if (otherId > 0) other.uniqueId = otherId; other.Destroy(); }
        }
        if (!CanTake(output)) throw new InvalidOperationException("F07 did not recover after removing an ambiguous source");
    }

    private string Diagnostic()
    {
        var detail = new StringBuilder();
        detail.Append("scene=").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)
            .Append(" context=").Append(context == null ? "not loaded" : string.Join("|", context))
            .Append(" loadStep=").Append(load == null ? "none" : typeof(QaLoadSlot).GetField("step", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(load))
            .Append(" enableRequested=").Append(enabled);
        try
        {
            var setting = ModConfig.Find(Mod)?.Get("f32.enabled") as ConfigEntry<bool>;
            detail.Append(" enabled=").Append(setting?.Value)
                .Append(" optionsOpen=").Append(StoreUIManager.Instance?.gameOptionUIManager?.IsOpen)
                .Append(" blackscreen=").Append(StoreUIManager.Instance?.blackscreen?.gameObject.activeInHierarchy)
                .Append(" typing=").Append(StoreUIManager.Instance != null && StoreUIManager.IsAnyTyping())
                .Append(" dragging=").Append(UIDragHandler.isDraggingAnyUI || ItemMouseDragHandler.current?.IsDraggingItem == true)
                .Append(" dialog=").Append(DialogUIManager.Instance?.currentDialog != null || DialoguePhoneUIManager.Instance?.currentDialog != null)
                .Append(" aboutToDialog=").Append(StoreUIManager.Instance?.isAboutToStartMainDialog)
                .Append(" confirmation=").Append(GenericConfirmationUIManager.Instance?.panel.activeInHierarchy)
                .Append(" mouseDown=").Append(Input.GetMouseButton(0));
        }
        catch (Exception error) { detail.Append(" uiError=").Append(error.Message); }
        try
        {
            if (!PlayerStore.HasInstance) return detail.Append(" player=missing").ToString();
            var player = PlayerStore.Instance;
            detail.Append(" run=").Append(player.runID).Append(" slot=").Append(player.saveSlotId).Append(" location=").Append(player.playerLocation)
                .Append(" rules=").Append(ModHelper.GetModData(Mod, "f32.rules", null));
            int machineId = context != null && context.Length == 4 && int.TryParse(context[2], out int parsedMachine) ? parsedMachine : -1;
            int seedId = context != null && context.Length == 4 && int.TryParse(context[3], out int parsedSeed) ? parsedSeed : -1;
            var all = player.FindAllItem();
            var machines = all.Where(item => item.uniqueId == machineId).ToArray();
            var seeds = all.Where(item => item.uniqueId == seedId).ToArray();
            detail.Append(" machineMatches=").Append(machines.Length).Append(" seedMatches=").Append(seeds.Length);
            GameItem machine = machines.FirstOrDefault(), seed = seeds.FirstOrDefault();
            if (machine != null)
            {
                var input = MachineHydroponic.GetInputSlot(machine);
                var output = MachineHydroponic.GetOutputSlot(machine);
                detail.Append(" machineParent=").Append(machine.parentInventory?.identifier)
                    .Append(" machineState=").Append(MachineProgressHelper.GetMachineState(machine))
                    .Append(" input=").Append(input?.childItem?.identifier).Append(":").Append(input?.childItem?.uniqueId)
                    .Append(" inputLocked=").Append(input?.IsInsertLocked())
                    .Append(" output=").Append(output == null ? "missing" : string.Join(",", output.childItems.Select(item =>
                        (item?.identifier ?? "null") + ":" + item?.uniqueId + "x" + item?.unitCount)));
            }
            if (seed != null)
                detail.Append(" seed=").Append(seed.identifier).Append("x").Append(seed.unitCount)
                    .Append(" seedParent=").Append(seed.parentInventory?.identifier)
                    .Append(" seedParentItem=").Append(seed.parentInventory?.GetParentItem()?.uniqueId)
                    .Append(" fastGrower=").Append(PlantGeneticsHelper.GetTraitRank(seed, "fast_grower"));
        }
        catch (Exception error) { detail.Append(" saveError=").Append(error.Message); }
        try
        {
            var assembly = ModLoader.Mods.Single(mod => mod.Manifest?.ID == Mod && mod.Instance != null).Instance.GetType().Assembly;
            Type tools = assembly.GetType("ShopAssist.MachineTools", true);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            object owner = tools.GetField("current", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            detail.Append(" toolsStarted=").Append(owner == null ? null : tools.GetField("started", fields)?.GetValue(owner))
                .Append(" needsScan=").Append(owner == null ? null : tools.GetField("replantNeedsScan", fields)?.GetValue(owner))
                .Append(" queueRun=").Append(owner == null ? null : tools.GetField("replantQueueRun", fields)?.GetValue(owner))
                .Append(" queueSlot=").Append(owner == null ? null : tools.GetField("replantQueueSlot", fields)?.GetValue(owner))
                .Append(" readError=").Append(owner == null ? null : tools.GetField("replantReadError", fields)?.GetValue(owner));
            var queue = owner == null ? null : tools.GetField("replantQueue", fields)?.GetValue(owner) as IEnumerable;
            detail.Append(" queue=").Append(queue == null ? "missing" : string.Join(",", queue.Cast<object>()));
            Type inputType = assembly.GetType("ShopAssist.ShopInput", true);
            detail.Append(" inputReady=").Append(inputType.GetMethod("Ready", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null));
        }
        catch (Exception error) { detail.Append(" queueError=").Append(error.Message); }
        return detail.ToString();
    }

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup >= deadline) throw new InvalidOperationException("Pending replant reload timed out: " + Diagnostic());
        if (load == null)
        {
            context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-replant-recovery.txt")).Split('|');
            if (context.Length != 4 || !Guid.TryParse(context[0], out Guid run) || !int.TryParse(context[1], out int slot))
                throw new InvalidOperationException("Invalid replant recovery context");
            load = new QaLoadSlot(slot, run); return false;
        }
        if (!load.Tick()) return false;
        if (!enabled)
        {
            Debug.Log("[PLAYER-QA] Replant reload before enabling: " + Diagnostic());
            TradeScenario.SetOptions(true, "f32.enabled", "f07.enabled");
            GameItem sourceMachine = PlayerStore.Instance.FindAllItem().Single(item => item.uniqueId == int.Parse(context[2]));
            VerifyRoutingPermissions(sourceMachine); enabled = true;
            Debug.Log("[PLAYER-QA] Replant reload after enabling: " + Diagnostic());
            return false;
        }
        var all = PlayerStore.Instance.FindAllItem();
        var machine = all.SingleOrDefault(item => item.uniqueId == int.Parse(context[2]));
        var seed = all.SingleOrDefault(item => item.uniqueId == int.Parse(context[3]));
        if (machine == null || seed == null) throw new InvalidOperationException("Pending replant fixtures missing after load: " + Diagnostic());
        if (!ReferenceEquals(MachineHydroponic.GetInputSlot(machine)?.childItem, seed)) return false;
        if (ReplantRecoveryScenario.ReadRule(machine).state != 0 || seed.unitCount != 1)
            throw new InvalidOperationException("Reload did not resolve pending replant safely");
        result = "PASS pending replant resumes once after loading the same run and slot"; return true;
    }
}
