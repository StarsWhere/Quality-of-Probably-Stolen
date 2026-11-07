using System;
using System.IO;
using System.Linq;
using UnityEngine;

// Exercises the furnace's own cycle callback on isolated QA fixtures.
public sealed class FeedScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem furnace, source, destination, battery;
    private Action<GameItem, GameInventory, SlotMarker> originalCycle;
    private GameGridInventory sourceGrid, destinationGrid;
    private int step, peerCalls, attachAt;
    private bool resetContext;
    private float deadline = Time.realtimeSinceStartup + 90;
    private static void Require(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
    public bool Tick(out string result)
    {
        result = null; Require(Time.realtimeSinceStartup < deadline, "F33 timed out at step " + step);
        if (step == 0)
        {
            if (!resetContext) { File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-feed-run.txt"), "pending"); resetContext = true; }
            if (!start.Tick()) return false;
            step++; return false;
        }
        if (step == 1)
        {
            if (EmporiumEntry.Instance == null || StoreUIManager.Instance?.gameOptionUIManager == null ||
                EmporiumEntry.Instance.storeInteractables == null || !EmporiumEntry.Instance.storeInteractables.activeInHierarchy ||
                ItemMouseDoubleClickHandler.current == null) return false;
            var stock = EmporiumEntry.Instance.invElement;
            furnace = New("furnace"); source = New("backpack_medium"); destination = New("satchel");
            GeneralHelper.SetCustomName(source, "QA feed source"); GeneralHelper.SetCustomName(destination, "QA feed destination");
            battery = New("energy_credit_dis");
            Put(stock, furnace); Put(stock, source);
            Require(furnace.onCycleEndSlotItemFunc != null, "Native furnace callback unavailable before binding");
            furnace.onCycleEndSlotItemFunc += (_, __, ___) => peerCalls++;
            originalCycle = furnace.onCycleEndSlotItemFunc;
            sourceGrid = source.contentWindow.childElement as GameGridInventory;
            Require(sourceGrid != null, "QA source grid missing");
            if (!TryPut(stock, destination) && !TryPut(sourceGrid, destination)) Put(EmporiumEntry.Instance.backInvinvElement, destination);
            if (!TryPut(stock, battery)) Put(EmporiumEntry.Instance.backInvinvElement, battery);
            destinationGrid = destination.contentWindow.childElement as GameGridInventory;
            Require(sourceGrid != null && destinationGrid != null, "QA storage grids missing");
            for (int i = 0; i < 4; i++) Put(sourceGrid, New("scrap_metal"));
            ItemMouseDoubleClickHandler.current.OpenContentAction(furnace, new Vector2(260, 140)); furnace.contentWindow.ToFront();
            var options = StoreUIManager.Instance.gameOptionUIManager;
            options.OpenUI(); var window = CustomUIManager.Instance.GetWindow("game_options");
            window.Get(Mod + ":f33.enabled").SetBool(true, true);
            window.Get(Mod + ":f15.enabled").SetBool(false, true);
            window.Get("apply").button.onClick.Invoke();
            options.CloseUI();
            RoutingScenario.RunFeatureAction("f33.open");
            Select("source", "QA feed source"); Select("destination", "QA feed destination");
            var ui = Ui();
            var material = sourceGrid.childItems.FirstOrDefault(item => item.identifier == "scrap_metal");
            Require(material != null, "F33 source material disappeared");
            QaItemUi.Choose(ui, "material", material.GetDisplayName(true)); ui = Ui();
            ui.Get("limit").SetFloat(2, true);
            ui.Get("bind").button.onClick.Invoke();
            Require(!string.IsNullOrEmpty(ModHelper.GetModData(Mod, "f33.rules", null)), "F33 rule was not persisted to the QA save");
            Require(sourceGrid.childItems.Count(i => i.identifier == "scrap_metal") == 4 && destinationGrid.childItems.Count == 0,
                "Binding F33 moved physical items");
            step = 20; return false;
        }
        if (step == 20)
        {
            string rules = ModHelper.GetModData(Mod, "f33.rules", null);
            Require(furnace.onCycleEndSlotItemFunc != originalCycle, "F33 did not install its cycle wrapper");
            TradeScenario.SetOptions(false, "f33.enabled");
            Require(furnace.onCycleEndSlotItemFunc == originalCycle, "Disabling F33 did not restore the original callbacks");
            Cycle(furnace);
            Require(peerCalls == 1 && ModHelper.GetModData(Mod, "f33.rules", null) == rules &&
                sourceGrid.childItems.Count(i => i.identifier == "scrap_metal") == 4,
                "Disabled F33 changed its rules or items, or lost another callback");
            TradeScenario.SetOptions(true, "f33.enabled");
            attachAt = Time.frameCount + 3;
            step = 21; return false;
        }
        if (step == 21)
        {
            if (Time.frameCount < attachAt) return false;
            Require(furnace.onCycleEndSlotItemFunc != originalCycle, "Re-enabling F33 did not restore its wrapper");
            furnace.contentWindow.ToFront(); RoutingScenario.RunFeatureAction("f33.open");
            step = 2; return false;
        }
        if (step == 2)
        {
            Cycle(furnace);
            Require(peerCalls == 2, "A peer furnace callback was lost or duplicated after F33 reattachment");
            string paused = ModHelper.GetModData(Mod, "f33.rules", null);
            Require(paused != null && paused.Contains("f33.power") &&
                sourceGrid.childItems.Count(i => i.identifier == "scrap_metal") == 4 && destinationGrid.childItems.Count == 0,
                "F33 moved material without furnace power: " + paused);
            Put(MachineHelper.GetBatterySlot(furnace), battery);
            Ui().Close();
            RoutingScenario.RunFeatureAction("f33.open");
            Require(Ui().Get("resume")?.button != null, "Paused F33 rule could not be resumed");
            Ui().Get("resume").button.onClick.Invoke();
            step++; return false;
        }
        if (step == 3)
        {
            Cycle(furnace);
            Require(sourceGrid.childItems.Count(i => i.identifier == "scrap_metal") == 2 &&
                destinationGrid.childItems.Count(i => i.identifier == "metal_ingot") == 1,
                "First native furnace cycle did not consume exactly two scrap and deliver one ingot; rules=" + ModHelper.GetModData(Mod, "f33.rules", null));
            string rules = ModHelper.GetModData(Mod, "f33.rules", null);
            Require(rules.Contains("\"completed\":1") && rules.Contains("\"pending\":0"), "First completed batch was not recorded: " + rules);
            int furnaceId = furnace.GetUniqueID(), sourceId = source.GetUniqueID(), destinationId = destination.GetUniqueID();
            var p = PlayerStore.Instance;
            File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-feed-run.txt"),
                string.Join("|", "1", p.runID, p.saveSlotId, furnaceId, sourceId, destinationId));
            p.SaveGame();
            result = "PASS F33 no-power pause/resume and one counted/delivered native furnace batch";
            return true;
        }
        throw new InvalidOperationException("Unknown F33 step");
    }
    internal static CustomUIWindow Ui() { var w = CustomUIManager.Instance.GetWindow(Mod + ".f33"); Require(w?.IsAlive == true, "F33 window missing"); return w; }
    internal static void Select(string control, string text)
    {
        QaItemUi.Choose(Ui(), control, text);
    }
    internal static void Cycle(GameItem machine)
    {
        Require(machine?.onCycleEndSlotItemFunc != null && machine.parentInventory != null, "Native furnace callback unavailable");
        machine.onCycleEndSlotItemFunc(machine, machine.parentInventory, machine.GetIncompleteSlotInInventory());
    }
    internal static GameItem New(string id) { var item = DirectoryMaster.Item(id); Require(item != null, "Missing " + id); GeneralHelper.SetItemOwned(item); return item; }
    internal static void Put(GameInventory target, GameItem item)
    {
        Require(TryPut(target, item), "Cannot place " + item.identifier);
    }
    private static bool TryPut(GameInventory target, GameItem item)
    {
        var marker = target.TryFindOneValidInventorySlot(item);
        return marker != null && ReferenceEquals(marker.inventory, target) && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, target);
    }
}

public sealed class FeedCapacityScenario
{
    private readonly QaNewRun start = new QaNewRun();
    public bool Tick(out string result)
    {
        result = null;
        if (!start.Tick()) return false;
        var e = EmporiumEntry.Instance;
        if (e.storeInteractables?.activeInHierarchy != true) return false;
        var destination = FeedScenario.New("fanny_pack");
        FeedScenario.Put(e.invElement, destination);
        var grid = destination?.contentWindow?.childElement as GameGridInventory;
        if (grid == null || grid.inventoryShape.width != 3 || grid.inventoryShape.height != 3)
            throw new InvalidOperationException("Native compact destination changed");
        FeedScenario.Put(grid, FeedScenario.New("metal_ingot"));
        var sample = FeedScenario.New("metal_ingot");
        try
        {
            var pose = new GridShapeBuilder(sample.modifiedShape);
            int valid = 0;
            for (int flip = 0; flip < 2; flip++) for (int turn = 0; turn < 4; turn++)
            {
                pose.SetFlipped(flip != 0).SetOrientation(turn);
                for (int y = 0; y < grid.inventoryShape.height; y++)
                for (int x = 0; x < grid.inventoryShape.width; x++)
                {
                    pose.SetPosition(x, y);
                    SlotMarker marker = grid.TryInventorySlot(sample, 1, pose);
                    if (marker != null && marker.targetItem == null && marker.numTransfer >= 1) valid++;
                }
            }
            string objects = string.Join("; ", grid.childItems.Select(item => item.identifier + " @" +
                item.modifiedShape.minX + "," + item.modifiedShape.minY + " size=" + item.modifiedShape.globalWidth + "x" + item.modifiedShape.globalHeight));
            Debug.Log("[PLAYER-QA] F33 native capacity: grid=" + grid.inventoryShape.width + "x" + grid.inventoryShape.height +
                " sample=" + sample.modifiedShape.width + "x" + sample.modifiedShape.height + " legal=" + valid + " contents=" + objects);
            if (valid != 0) throw new InvalidOperationException("Native placement exists; F33 may have rejected a valid slot");
            result = "PASS F33 original destination has no native placement for another ingot";
            return true;
        }
        finally { GraphUtils.DestroyIfUnplaced(sample); }
    }
}

public sealed class FeedReloadScenario
{
    private const string Mod = "starswhere.shopassist";
    private QaLoadSlot load;
    private string[] context;
    private int step;
    public bool Tick(out string result)
    {
        result = null;
        if (load == null)
        {
            context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-feed-run.txt")).Split('|');
            if (context.Length != 6 || context[0] != "1" || !Guid.TryParse(context[1], out Guid run) ||
                !int.TryParse(context[2], out int slot)) throw new InvalidOperationException("Invalid F33 QA context");
            load = new QaLoadSlot(slot, run); return false;
        }
        if (!load.Tick()) return false;
        // Allow the mod's load hook to reconnect the actual furnace callback.
        if (step++ == 0) return false;
        var e = EmporiumEntry.Instance;
        var all = new[] { e.invElement, e.backInvinvElement, e.showcaseElement, e.frontInvinvElement }
            .SelectMany(root => GraphUtils.FindChildrenType<GameItem>(root, passthrough: _ => true)).Distinct().ToList();
        var furnace = all.SingleOrDefault(i => i.uniqueId == int.Parse(context[3]));
        var source = all.SingleOrDefault(i => i.uniqueId == int.Parse(context[4]));
        var destination = all.SingleOrDefault(i => i.uniqueId == int.Parse(context[5]));
        var sourceGrid = source?.contentWindow?.childElement as GameGridInventory;
        var destGrid = destination?.contentWindow?.childElement as GameGridInventory;
        if (furnace == null || sourceGrid == null || destGrid == null) throw new InvalidOperationException("F33 QA objects missing on reload");
        FeedScenario.Cycle(furnace);
        string rules = ModHelper.GetModData(Mod, "f33.rules", null);
        if (sourceGrid.childItems.Any(i => i.identifier == "scrap_metal") ||
            destGrid.childItems.Count(i => i.identifier == "metal_ingot") != 2 || rules == null ||
            !rules.Contains("\"completed\":2")) throw new InvalidOperationException("Second F33 batch failed after reload: " + rules);
        FeedScenario.Put(sourceGrid, FeedScenario.New("scrap_metal"));
        FeedScenario.Put(sourceGrid, FeedScenario.New("scrap_metal"));
        FeedScenario.Cycle(furnace);
        if (sourceGrid.childItems.Count(i => i.identifier == "scrap_metal") != 2 ||
            destGrid.childItems.Count(i => i.identifier == "metal_ingot") != 2)
            throw new InvalidOperationException("F33 exceeded the configured batch limit");

        ItemMouseDoubleClickHandler.current.OpenContentAction(furnace, new Vector2(260, 140));
        furnace.contentWindow.ToFront();
        RoutingScenario.RunFeatureAction("f33.open");
        FeedScenario.Ui().Get("stop").button.onClick.Invoke();
        FeedScenario.Select("source", "QA feed source"); FeedScenario.Select("destination", "QA feed destination");
        var ui = FeedScenario.Ui();
        var material = sourceGrid.childItems.FirstOrDefault(item => item.identifier == "scrap_metal");
        if (material == null) throw new InvalidOperationException("F33 reload source material missing");
        QaItemUi.Choose(ui, "material", material.GetDisplayName(true)); ui = FeedScenario.Ui();
        ui.Get("bind").button.onClick.Invoke();
        destGrid.overrideLockInsert = true;
        try
        {
            FeedScenario.Cycle(furnace);
            rules = ModHelper.GetModData(Mod, "f33.rules", null);
            if (sourceGrid.childItems.Count(i => i.identifier == "scrap_metal") != 2 ||
                destGrid.childItems.Count(i => i.identifier == "metal_ingot") != 2 || rules == null || !rules.Contains("f33.output"))
                throw new InvalidOperationException("F33 moved items despite a locked destination: " + rules);
        }
        finally { destGrid.overrideLockInsert = false; }
        result = "PASS F33 resumed after cold reload; respected the two-batch limit and a locked destination";
        return true;
    }
}
