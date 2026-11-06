using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Additional native-player checks. Fixtures use only a fresh QA run and native inventories.
internal static class MachineFeatureQa
{
    internal const string Mod = "starswhere.shopassist";
    internal static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    internal static GameItem Item(string id)
    {
        var item = DirectoryMaster.Item(id); Check(item != null, "Missing QA item " + id);
        GeneralHelper.SetItemOwned(item); return item;
    }
    internal static bool TryPut(GameInventory inventory, GameItem item)
    {
        var marker = inventory?.TryFindOneValidInventorySlot(item);
        return marker != null && marker.inventory == inventory && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory;
    }
    internal static void Put(GameInventory inventory, GameItem item) => Check(TryPut(inventory, item), "Cannot place " + item.identifier);
    internal static GameItem Stock(string id)
    {
        var item = Item(id); ReturnToShop(item); return item;
    }
    internal static void ReturnToShop(GameItem item)
    {
        var e = EmporiumEntry.Instance;
        Check(TryPut(e.invElement, item) || TryPut(e.backInvinvElement, item) || TryPut(e.frontInvinvElement, item) || TryPut(e.showcaseElement, item),
            "No shop location accepts " + item.identifier);
    }
    internal static T Panel<T>(GameItem machine, string role) where T : GameInventory =>
        MachineryContainerUtils.Collect(((MachineryPanelRow)machine.contentWindow.childElement).GetPanel(role).content).OfType<T>().Single();
    internal static int Part(GameItem item, string id) => item.GetTagReadonly(Liquid.GetCurrentPartFromId(id)).GetInt();
    internal static void Empty(GameItem item) => WaterHelper.Remove(item, WaterHelper.GetCurrentCapacityUL(item));
    internal static Transform Root() => WaterScannerUIManager.Instance.panel.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(t => t.name == "ShopAssist_LiquidBars" && t.gameObject.activeInHierarchy);
    internal static Transform Node(Transform root, string name) => root?.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(t => t.name == name && t.gameObject.activeInHierarchy);
    internal static string Text(Transform node) => string.Join(" | ", node.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text));
    internal static void OpenMethod(string component)
    {
        var row = Node(Root(), "Component_" + component); Check(row != null, "Missing scanner component " + component);
        var button = row.GetComponent<Button>();
        Check(button != null, "Missing processing action for " + component); button.onClick.Invoke();
    }
    internal static Transform Detail() => CustomUIManager.Instance.GetWindow(Mod + ".f19.detail.water")?.root?.transform;
    internal static void Card(string sprite, string expected)
    {
        var cards = Detail()?.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Method_" + sprite && t.gameObject.activeInHierarchy).ToList();
        if (cards != null && cards.All(card => !Text(card).Contains(expected)))
        {
            var expand = CustomUIManager.Instance.GetWindow(Mod + ".f19.detail.water")?.Get("method_more_" + sprite);
            Check(expand?.button != null, "Missing method detail control for " + sprite);
            expand.button.onClick.Invoke();
            cards = Detail()?.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Method_" + sprite && t.gameObject.activeInHierarchy).ToList();
        }
        Check(cards != null && cards.Any(card => Text(card).Contains(expected)), "Missing or incorrect " + sprite + " method card");
    }
    internal static void Row(string id, int amount, int total)
    {
        var row = Node(Root(), "Component_" + id); Check(row != null, "Missing measured " + id);
        string percent = ((double)amount / total * 100).ToString("F2", CultureInfo.CurrentCulture) + "%";
        string text = Text(row);
        Check(text.Contains(WaterScannerUIManager.FormatAmount(amount)) && text.Contains(percent),
            "Wrong measured " + id + ": " + text + " expected " + amount + "/" + total);
    }
    internal static void Cycle(GameItem machine) => FeedScenario.Cycle(machine);
}

// Check the scanner's filter, UV and tablet descriptions against the actual item callbacks.
public sealed class ScannerTreatmentScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private GameItem bottle, pitcher, filter, reactive;
    private bool uvTransferAdvertised;
    private int step;
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    public bool Tick(out string result)
    {
        result = null; MachineFeatureQa.Check(Time.realtimeSinceStartup < deadline, "Scanner treatments timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (WaterScannerUIManager.Instance == null || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(true, "f19.enabled"); TradeScenario.SetOptions(false, "f35.enabled");
            bottle = MachineFeatureQa.Stock("wine_bottle"); pitcher = MachineFeatureQa.Stock("water_pitcher");
            filter = MachineFeatureQa.Stock("water_filter");
            MachineFeatureQa.Empty(bottle); MachineFeatureQa.Empty(pitcher);
            MachineFeatureQa.Put((GameSlotInventory)pitcher.contentWindow.childElement, filter);
            WaterHelper.AddLiquid(bottle, "water", 730000); WaterHelper.AddLiquid(bottle, "organic_waste", 12000);
            WaterHelper.AddLiquid(bottle, "microbe", 1000);
            WaterScannerUIManager.Instance.Open(null, bottle); step++; return false;
        }
        if (step == 2)
        {
            if (MachineFeatureQa.Root() == null) return false;
            MachineFeatureQa.Row("organic_waste", 12000, 743000); MachineFeatureQa.OpenMethod("organic_waste"); step++; return false;
        }
        if (step == 3)
        {
            string description = string.Format(CultureInfo.CurrentCulture, ModHelper.GetLocalized(MachineFeatureQa.Mod, "f19.filter"), 22, 40, .16, 1.0, 1);
            MachineFeatureQa.Card("water_filter", description); WaterScannerUIManager.Instance.Close();
            MachineFeatureQa.Check(filter.GetTagReadonly("FILTER_SIZE_TAG").GetInt() == 22, "Unexpected native filter pore");
            DurabilityHelper.SetCurrentDurability(filter, 5); WaterHelper.TransferLiquid(bottle, pitcher);
            MachineFeatureQa.Check(MachineFeatureQa.Part(pitcher, "water") == 730000 && MachineFeatureQa.Part(pitcher, "organic_waste") == 7000 &&
                MachineFeatureQa.Part(pitcher, "microbe") == 1000 && DurabilityHelper.GetRealDurability(filter) == 0,
                "Five native filter steps did not remove exactly 5 mL of organic waste at one durability each");
            WaterScannerUIManager.Instance.Open(null, pitcher); step++; return false;
        }
        if (step == 4)
        {
            if (MachineFeatureQa.Root() == null) return false;
            MachineFeatureQa.Row("organic_waste", 7000, 738000); MachineFeatureQa.OpenMethod("microbe"); step++; return false;
        }
        if (step == 5)
        {
            MachineFeatureQa.Card("uv", ModHelper.GetLocalized(MachineFeatureQa.Mod, "f19.uv_direct"));
            uvTransferAdvertised = MachineFeatureQa.Detail().GetComponentsInChildren<Transform>(true)
                .Count(t => t.gameObject.activeInHierarchy && t.name == "Method_uv") > 1;
            WaterScannerUIManager.Instance.Close();
            var uv = MachineFeatureQa.Stock("uv_filter"); int durability = DurabilityHelper.GetRealDurability(uv);
            MachineFeatureQa.Check(uv.onItemTargetItemFunc != null, "Native UV item callback unavailable");
            uv.onItemTargetItemFunc(uv, pitcher);
            MachineFeatureQa.Check(MachineFeatureQa.Part(pitcher, "microbe") == 0 && MachineFeatureQa.Part(pitcher, "water") == 731000 &&
                DurabilityHelper.GetRealDurability(uv) == durability, "Direct UV did not eliminate microbes without wear");
            MachineFeatureQa.Empty(pitcher); MachineFeatureQa.ReturnToShop(filter);
            DurabilityHelper.SetCurrentDurability(filter, 100); int full = DurabilityHelper.GetRealDurability(filter);
            MachineFeatureQa.Put((GameSlotInventory)pitcher.contentWindow.childElement, filter);
            WaterHelper.AddLiquid(bottle, "water", 730000); WaterHelper.AddLiquid(bottle, "organic_waste", 12000);
            WaterHelper.TransferLiquid(bottle, pitcher);
            MachineFeatureQa.Check(MachineFeatureQa.Part(pitcher, "organic_waste") == 19 && full - DurabilityHelper.GetRealDurability(filter) == 12,
                "Native filter did not preserve floor(12000 * 0.16%) uL with 12 durability steps");
            MachineFeatureQa.Empty(pitcher); MachineFeatureQa.ReturnToShop(filter);
            var uvHome = uv.parentInventory;
            MachineFeatureQa.Check(!MachineFeatureQa.TryPut((GameSlotInventory)pitcher.contentWindow.childElement, uv) &&
                ReferenceEquals(uv.parentInventory, uvHome), "Native pitcher unexpectedly accepted a UV filter");
            reactive = MachineFeatureQa.Stock("bottled_water"); MachineFeatureQa.Empty(reactive);
            WaterHelper.AddLiquid(reactive, "water", 400000); WaterHelper.AddLiquid(reactive, "chemical_contaminant", 100000);
            WaterHelper.AddLiquid(reactive, "microbe", 100000);
            MachineFeatureQa.Check(reactive.onOneSecondItemFunc != null, "Native chemical reaction callback unavailable");
            WaterScannerUIManager.Instance.Open(null, reactive); step++; return false;
        }
        if (step == 6)
        {
            if (MachineFeatureQa.Root() == null) return false;
            MachineFeatureQa.OpenMethod("microbe"); step++; return false;
        }
        if (step == 7)
        {
            MachineFeatureQa.Card("water_tablet", ModHelper.GetLocalized(MachineFeatureQa.Mod, "f19.tablet"));
            WaterScannerUIManager.Instance.Close();
            Tablet(reactive);
            MachineFeatureQa.Check(MachineFeatureQa.Part(reactive, "chlorine") == 15000, "Tablet did not add 15 mL chlorine");
            reactive.onOneSecondItemFunc(reactive, reactive.parentInventory, reactive.GetIncompleteSlotInInventory());
            MachineFeatureQa.Check(MachineFeatureQa.Part(reactive, "chlorine") == 0 && MachineFeatureQa.Part(reactive, "chemical_contaminant") == 23500 &&
                MachineFeatureQa.Part(reactive, "microbe") == 100000, "Native chlorine reaction did not spend its 76.5 mL budget on chemicals first");
            MachineFeatureQa.Empty(reactive);
            WaterHelper.AddLiquid(reactive, "water", 400000); WaterHelper.AddLiquid(reactive, "chemical_contaminant", 10000);
            WaterHelper.AddLiquid(reactive, "microbe", 100000); Tablet(reactive);
            reactive.onOneSecondItemFunc(reactive, reactive.parentInventory, reactive.GetIncompleteSlotInInventory());
            MachineFeatureQa.Check(MachineFeatureQa.Part(reactive, "chemical_contaminant") == 3675 &&
                MachineFeatureQa.Part(reactive, "microbe") == 29825 && MachineFeatureQa.Part(reactive, "chlorine") == 0,
                "Native chlorine reaction did not stop chemicals at 0.7% before treating microbes");
            MachineFeatureQa.Empty(reactive); WaterHelper.AddLiquid(reactive, "water", 400000);
            Tablet(reactive); Tablet(reactive);
            reactive.onOneSecondItemFunc(reactive, reactive.parentInventory, reactive.GetIncompleteSlotInInventory());
            MachineFeatureQa.Check(MachineFeatureQa.Part(reactive, "chlorine") == 12000,
                "Native chlorine reaction did not degrade the remaining chlorine");
            Debug.Log("[PLAYER-QA] F19 verified real filter transfers, direct UV and tablet reaction callbacks");
            MachineFeatureQa.Check(!uvTransferAdvertised, "F19 advertised UV transfer even though the native filter vessel rejects UV");
            result = "PASS F19 real filter transfers, direct UV, unavailable UV transfer and tablet reaction callbacks"; return true;
        }
        throw new InvalidOperationException("Unknown scanner treatment step");
    }
    private static void Tablet(GameItem sample)
    {
        var tablet = MachineFeatureQa.Stock("water_tablet");
        MachineFeatureQa.Check(tablet.onItemTargetItemFunc != null, "Native tablet callback unavailable");
        tablet.onItemTargetItemFunc(tablet, sample);
    }
}

// Check every advertised filterable native component against the actual transfer result.
public sealed class ScannerFilterMatrixScenario
{
    private readonly (string id, int particle, double floor, int wear, int removed)[] cases =
    {
        ("physical_contaminant", 100, .16, 2, 1500), ("organic_waste", 40, .16, 1, 1000),
        ("microplastic", 25, .2, 2, 1500), ("mineral", 30, .16, 1, 2000),
        ("dream_cap_mush", 200, 0, 2, 1000), ("plant_matter", 200, 0, 2, 1000),
        ("coffee_mush", 200, 0, 2, 1000), ("nightmare_cap_mush", 200, 0, 2, 1000), ("debris", 200, 0, 2, 1000)
    };
    private readonly QaNewRun start = new QaNewRun();
    private GameItem bottle, pitcher, filter;
    private int step, index;
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    public bool Tick(out string result)
    {
        result = null; MachineFeatureQa.Check(Time.realtimeSinceStartup < deadline, "Scanner filter matrix timed out at " + index + "/" + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (WaterScannerUIManager.Instance == null || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(true, "f19.enabled"); TradeScenario.SetOptions(false, "f35.enabled");
            bottle = MachineFeatureQa.Stock("wine_bottle"); pitcher = MachineFeatureQa.Stock("water_pitcher"); filter = MachineFeatureQa.Stock("water_filter");
            MachineFeatureQa.Put((GameSlotInventory)pitcher.contentWindow.childElement, filter); step++; return false;
        }
        if (step == 2)
        {
            if (index == cases.Length)
            { result = "PASS F19 nine filterable components: displayed particle, residual and per-step limits match native transfers"; return true; }
            MachineFeatureQa.Empty(bottle); MachineFeatureQa.Empty(pitcher); DurabilityHelper.SetCurrentDurability(filter, 6);
            WaterHelper.AddLiquid(bottle, "water", 730000); WaterHelper.AddLiquid(bottle, cases[index].id, 12000);
            WaterScannerUIManager.Instance.Open(null, bottle); step++; return false;
        }
        if (step == 3)
        {
            if (MachineFeatureQa.Root() == null) return false;
            MachineFeatureQa.OpenMethod(cases[index].id); step++; return false;
        }
        if (step == 4)
        {
            var test = cases[index];
            string description = string.Format(CultureInfo.CurrentCulture, ModHelper.GetLocalized(MachineFeatureQa.Mod, "f19.filter"),
                22, test.particle, test.floor, test.removed / 1000.0, test.wear);
            MachineFeatureQa.Card("water_filter", description); WaterScannerUIManager.Instance.Close();
            WaterHelper.TransferLiquid(bottle, pitcher);
            int floor = (int)(12000 * test.floor / 100); int maxSteps = (6 + test.wear - 1) / test.wear;
            int steps = Math.Min(maxSteps, (int)Math.Ceiling((12000 - floor) / (double)test.removed));
            int expected = Math.Max(floor, 12000 - steps * test.removed);
            MachineFeatureQa.Check(MachineFeatureQa.Part(pitcher, test.id) == expected &&
                DurabilityHelper.GetRealDurability(filter) == Math.Max(0, 6 - steps * test.wear),
                "Native filter disagrees with the displayed " + test.id + " limits: expected " + expected + " uL after " + steps + " steps");
            index++; step = 2; return false;
        }
        throw new InvalidOperationException("Unknown scanner filter matrix step");
    }
}

// Exercise both machine processing descriptions through their native cycle callbacks.
public sealed class ScannerMachineTreatmentScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private GameItem bottle, purifier, battery, evaporator, evapBattery;
    private int step;
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    public bool Tick(out string result)
    {
        result = null; MachineFeatureQa.Check(Time.realtimeSinceStartup < deadline, "Scanner machine treatments timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (WaterScannerUIManager.Instance == null || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(true, "f19.enabled");
            bottle = MachineFeatureQa.Stock("water_pitcher"); MachineFeatureQa.Empty(bottle);
            WaterHelper.AddLiquid(bottle, "water", 2400000); WaterHelper.AddLiquid(bottle, "plant_matter", 300000);
            WaterHelper.AddLiquid(bottle, "organic_waste", 200000); WaterHelper.AddLiquid(bottle, "mineral", 100000);
            purifier = MachineFeatureQa.Stock("water_purifier"); battery = MachineFeatureQa.Stock("energy_credit_dis");
            WaterScannerUIManager.Instance.Open(null, bottle); step++; return false;
        }
        if (step == 2)
        {
            if (MachineFeatureQa.Root() == null) return false;
            MachineFeatureQa.Row("plant_matter", 300000, 3000000); MachineFeatureQa.OpenMethod("plant_matter"); step++; return false;
        }
        if (step == 3)
        {
            MachineFeatureQa.Card("purifier", ModHelper.GetLocalized(MachineFeatureQa.Mod, "f19.purifier"));
            WaterScannerUIManager.Instance.Close();
            var input = MachineFeatureQa.Panel<GameSlotInventory>(purifier, "container"); MachineFeatureQa.Put(input, bottle);
            int energyBefore = battery.GetTagReadonly("power_source_item_energy").GetInt();
            MachineFeatureQa.Cycle(purifier);
            MachineFeatureQa.Check(MachineFeatureQa.Part(bottle, "plant_matter") == 300000 && energyBefore == battery.GetTagReadonly("power_source_item_energy").GetInt(),
                "Unpowered purifier changed its sample");
            MachineFeatureQa.Put(MachineHelper.GetBatterySlot(purifier), battery);
            int perf = MachineryHelper.GetCurrentPerformanceBonus(purifier), quality = MachineryHelper.GetCurrentQualityBonus(purifier);
            int total = WaterHelper.GetTotalVolume(bottle);
            double multiplier = Math.Max(0, MachinePurifier.GetDirtBonus(WaterHelper.GetWaterPurity(bottle)) + perf / 100.0) * 1.25;
            int budget = (int)((Math.Min(total, 10000000) * 3L / 1000 + 25000) * multiplier);
            int cap = quality >= 200 ? 10000 : quality >= 100 ? 9990 : quality >= 75 ? 9980 : quality >= 50 ? 9965 : quality >= 25 ? 9935 : quality >= 15 ? 9915 : 9900;
            int removable = 600000 - (int)((long)total * (10000 - cap) / 10000);
            int expected = Math.Min(budget, removable);
            MachineFeatureQa.Check(expected > 300000 && expected < removable, "QA purifier did not exercise its cycle budget and ordering: " + expected);
            int plantRemoved = Math.Min(expected, 300000);
            int organicRemoved = Math.Min(expected - plantRemoved, 200000);
            int mineralRemoved = expected - plantRemoved - organicRemoved;
            MachineFeatureQa.Cycle(purifier);
            MachineFeatureQa.Check(MachineFeatureQa.Part(bottle, "plant_matter") == 300000 - plantRemoved &&
                MachineFeatureQa.Part(bottle, "organic_waste") == 200000 - organicRemoved && MachineFeatureQa.Part(bottle, "mineral") == 100000 - mineralRemoved &&
                MachineFeatureQa.Part(bottle, "water") == 2400000 + expected &&
                battery.GetTagReadonly("power_source_item_energy").GetInt() < energyBefore,
                "Native purifier did not match the displayed budget/priority/power: expected removal " + expected);
            MachineFeatureQa.ReturnToShop(bottle); MachineFeatureQa.Empty(bottle);
            WaterHelper.AddLiquid(bottle, "water", 400000); WaterHelper.AddLiquid(bottle, "alcohol", 200000);
            WaterHelper.AddLiquid(bottle, "organic_waste", 50000);
            evaporator = MachineFeatureQa.Stock("evaporator"); evapBattery = MachineFeatureQa.Stock("energy_credit_dis");
            WaterScannerUIManager.Instance.Open(null, bottle); step++; return false;
        }
        if (step == 4)
        {
            if (MachineFeatureQa.Root() == null) return false;
            MachineFeatureQa.OpenMethod("water"); step++; return false;
        }
        if (step == 5)
        {
            MachineFeatureQa.Card("evaporator", ModHelper.GetLocalized(MachineFeatureQa.Mod, "f19.evaporator_water"));
            WaterScannerUIManager.Instance.Close(); WaterScannerUIManager.Instance.Open(null, bottle); step++; return false;
        }
        if (step == 6)
        {
            if (MachineFeatureQa.Root() == null) return false;
            MachineFeatureQa.OpenMethod("organic_waste"); step++; return false;
        }
        if (step == 7)
        {
            MachineFeatureQa.Card("evaporator", ModHelper.GetLocalized(MachineFeatureQa.Mod, "f19.evaporator_residue"));
            WaterScannerUIManager.Instance.Close();
            MachineFeatureQa.Put(MachineFeatureQa.Panel<GameSlotInventory>(evaporator, "container"), bottle);
            MachineFeatureQa.Cycle(evaporator);
            MachineFeatureQa.Check(MachineFeatureQa.Part(bottle, "water") == 400000 && MachineFeatureQa.Part(bottle, "alcohol") == 200000,
                "Unpowered evaporator changed its sample");
            MachineFeatureQa.Put(MachineHelper.GetBatterySlot(evaporator), evapBattery);
            int energy = evapBattery.GetTagReadonly("power_source_item_energy").GetInt();
            int perf = MachineryHelper.GetCurrentPerformanceBonus(evaporator);
            int budget = Math.Min(600000, (int)Math.Round(300000f * Math.Max(0f, 1f + perf / 100f)));
            MachineFeatureQa.Cycle(evaporator);
            MachineFeatureQa.Check(MachineFeatureQa.Part(bottle, "water") == 400000 - budget * 2 / 3 &&
                MachineFeatureQa.Part(bottle, "alcohol") == 200000 - (budget - budget * 2 / 3) &&
                MachineFeatureQa.Part(bottle, "organic_waste") == 50000 &&
                evapBattery.GetTagReadonly("power_source_item_energy").GetInt() < energy,
                "Native evaporation did not match the 300 mL performance-scaled proportional budget or residue");
            result = "PASS F19 purifier and evaporator method cards versus powered/unpowered native machine cycles"; return true;
        }
        throw new InvalidOperationException("Unknown scanner machine treatment step");
    }
}

// A physically full destination must pause the configured job and resume after space is freed.
public sealed class FeedFullRecoveryScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private GameItem furnace, source, destination, filler;
    private GameGridInventory sourceGrid, destinationGrid;
    private int step;
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    public bool Tick(out string result)
    {
        result = null; MachineFeatureQa.Check(Time.realtimeSinceStartup < deadline, "F33 full-destination recovery timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (StoreUIManager.Instance?.gameOptionUIManager == null || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f33.enabled"); TradeScenario.SetOptions(false, "f15.enabled");
            furnace = MachineFeatureQa.Stock("furnace"); source = MachineFeatureQa.Stock("backpack_medium");
            destination = MachineFeatureQa.Stock("fanny_pack");
            GeneralHelper.SetCustomName(source, "QA full feed source"); GeneralHelper.SetCustomName(destination, "QA full feed destination");
            sourceGrid = source.contentWindow.childElement as GameGridInventory; destinationGrid = destination.contentWindow.childElement as GameGridInventory;
            MachineFeatureQa.Check(sourceGrid != null && destinationGrid != null && destinationGrid.inventoryShape.width == 3 &&
                destinationGrid.inventoryShape.height == 3, "Unexpected source or compact destination");
            MachineFeatureQa.Put(sourceGrid, MachineFeatureQa.Item("scrap_metal"));
            MachineFeatureQa.Put(sourceGrid, MachineFeatureQa.Item("scrap_metal"));
            filler = MachineFeatureQa.Item("metal_ingot"); MachineFeatureQa.Put(destinationGrid, filler);
            var probe = MachineFeatureQa.Item("metal_ingot");
            try
            {
                var shape = new GridShapeBuilder(probe.modifiedShape); int valid = 0;
                for (int flip = 0; flip < 2; flip++) for (int turn = 0; turn < 4; turn++)
                for (int y = 0; y < destinationGrid.inventoryShape.height; y++) for (int x = 0; x < destinationGrid.inventoryShape.width; x++)
                {
                    shape.SetTransform(x, y, flip != 0, turn);
                    var marker = destinationGrid.TryInventorySlot(probe, 1, shape);
                    if (marker != null && marker.targetItem == null && marker.numTransfer >= 1) valid++;
                }
                MachineFeatureQa.Check(valid == 0, "QA destination still has a native placement for an ingot");
            }
            finally { GraphUtils.DestroyIfUnplaced(probe); }
            var battery = MachineFeatureQa.Stock("energy_credit_dis"); MachineFeatureQa.Put(MachineHelper.GetBatterySlot(furnace), battery);
            ItemMouseDoubleClickHandler.current.OpenContentAction(furnace, new Vector2(260, 140)); furnace.contentWindow.ToFront();
            RoutingScenario.OpenOptionAction("f33.open");
            FeedScenario.Select("source", "QA full feed source"); FeedScenario.Select("destination", "QA full feed destination");
            QaItemUi.Choose(FeedScenario.Ui(), "material", sourceGrid.childItems.First(i => i.identifier == "scrap_metal").GetDisplayName(true));
            FeedScenario.Ui().Get("limit").SetFloat(1, true); FeedScenario.Ui().Get("bind").button.onClick.Invoke();
            step++; return false;
        }
        if (step == 2)
        {
            MachineFeatureQa.Cycle(furnace);
            string rules = ModHelper.GetModData(MachineFeatureQa.Mod, "f33.rules", null);
            var input = MachineFeatureQa.Panel<GameGridInventory>(furnace, "input");
            var output = MachineFeatureQa.Panel<GameGridInventory>(furnace, "output");
            MachineFeatureQa.Check(rules != null && rules.Contains("f33.output") && rules.Contains("\"completed\":0") &&
                sourceGrid.childItems.Count(i => i.identifier == "scrap_metal") == 2 && destinationGrid.childItems.Count == 1 &&
                destinationGrid.childItems[0] == filler && input.childItems.Count == 0 && output.childItems.Count == 0,
                "F33 did not pause without moving goods on a truly full destination: " + rules);
            MachineFeatureQa.ReturnToShop(filler);
            FeedScenario.Ui()?.Close(); furnace.contentWindow.ToFront(); RoutingScenario.OpenOptionAction("f33.open");
            var resume = FeedScenario.Ui().Get("resume")?.button;
            MachineFeatureQa.Check(resume != null, "Full-destination pause has no resume action"); resume.onClick.Invoke();
            step++; return false;
        }
        if (step == 3)
        {
            MachineFeatureQa.Cycle(furnace); string rules = ModHelper.GetModData(MachineFeatureQa.Mod, "f33.rules", null);
            MachineFeatureQa.Check(!sourceGrid.childItems.Any(i => i.identifier == "scrap_metal") &&
                destinationGrid.childItems.Count(i => i.identifier == "metal_ingot" && i != filler) == 1 &&
                rules != null && rules.Contains("\"completed\":1") && rules.Contains("\"pending\":0"),
                "F33 did not produce and deliver one batch after freeing capacity: " + rules);
            result = "PASS F33 truly full native destination pauses without moving goods, then resumes and delivers one batch"; return true;
        }
        throw new InvalidOperationException("Unknown F33 full-destination step");
    }
}

// Save one module-storage binding and one low-battery rule on separate native containers.
public sealed class MachineBindingScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private GameItem machine, module, storage, stored, supply, oldBattery, spare;
    private int step;
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    public bool Tick(out string result)
    {
        result = null; MachineFeatureQa.Check(Time.realtimeSinceStartup < deadline, "Machine binding setup timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (StoreUIManager.Instance?.gameOptionUIManager == null || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f10.enabled", "f31.enabled"); TradeScenario.SetOptions(false, "f15.enabled");
            machine = MachineFeatureQa.Stock("hydroponic"); storage = MachineFeatureQa.Stock("backpack_large");
            supply = MachineFeatureQa.Stock("backpack_medium"); GeneralHelper.SetCustomName(storage, "QA cold module storage");
            GeneralHelper.SetCustomName(supply, "QA cold battery supply");
            module = MachineFeatureQa.Item("system_module_performance"); stored = MachineFeatureQa.Item("system_module_quality");
            MachineFeatureQa.Put(MachineFeatureQa.Panel<GameGridInventory>(machine, "module"), module);
            MachineFeatureQa.Put((GameGridInventory)storage.contentWindow.childElement, stored);
            oldBattery = MachineFeatureQa.Item("energy_credit"); spare = MachineFeatureQa.Item("energy_credit");
            PowerHelper.SetPowerSourceAt(oldBattery, 2); PowerHelper.SetPowerSourceAt(spare, 100);
            MachineFeatureQa.Put(MachineHelper.GetBatterySlot(machine), oldBattery);
            MachineFeatureQa.Put((GameGridInventory)supply.contentWindow.childElement, spare);
            MachineFeatureQa.Check(MachineHydroponic.GetInputSlot(machine).childItem == null &&
                !MachineFeatureQa.Panel<GameGridInventory>(machine, "module").IsRemoveLocked(),
                "QA machine is not idle and removable before saving");
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(260, 140)); machine.contentWindow.ToFront();
            RoutingScenario.OpenOptionAction("f10.open");
            var browser = CustomUIManager.Instance.GetWindow(MachineFeatureQa.Mod + ".f10");
            MachineFeatureQa.Check(browser?.Get("storage_choose_footer")?.button != null, "Module storage chooser unavailable");
            browser.Get("storage_choose_footer").button.onClick.Invoke();
            var picker = CustomUIManager.Instance.GetWindow(MachineFeatureQa.Mod + ".choose");
            MonoBehaviour hit = null;
            for (int page = 0; page < 20 && hit == null; page++)
            {
                hit = QaItemUi.Hit(picker.root, name: "QA cold module storage");
                if (hit == null && picker.Get("next")?.button != null) { picker.Get("next").button.onClick.Invoke(); picker = CustomUIManager.Instance.GetWindow(MachineFeatureQa.Mod + ".choose"); }
            }
            MachineFeatureQa.Check(hit != null, "Module storage absent from chooser"); QaItemUi.Click(hit);
            step++; return false;
        }
        if (step == 2)
        {
            string binding = ModHelper.GetModData(MachineFeatureQa.Mod, "module_storage", null);
            MachineFeatureQa.Check(!string.IsNullOrEmpty(binding), "Module storage binding not saved");
            CustomUIManager.Instance.GetWindow(MachineFeatureQa.Mod + ".f10")?.Close(); machine.contentWindow.ToFront();
            RoutingScenario.OpenOptionAction("f31.open");
            var options = CustomUIManager.Instance.GetWindow(MachineFeatureQa.Mod + ".maintenance");
            QaItemUi.Choose(options, "source", "QA cold battery supply"); options = CustomUIManager.Instance.GetWindow(MachineFeatureQa.Mod + ".maintenance");
            options.Get("threshold").SetFloat(50, true); options.Get("enable").button.onClick.Invoke();
            string rules = ModHelper.GetModData(MachineFeatureQa.Mod, "machineMaintenance", null);
            MachineFeatureQa.Check(!string.IsNullOrEmpty(rules), "Battery maintenance rule not saved");
            var player = PlayerStore.Instance;
            File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-machine-bindings.txt"), string.Join("|", "1", player.runID, player.saveSlotId,
                machine.GetUniqueID(), module.GetUniqueID(), storage.GetUniqueID(), stored.GetUniqueID(), supply.GetUniqueID(), oldBattery.GetUniqueID(), spare.GetUniqueID()));
            player.SaveGame(); result = "PASS F10 storage and F31 battery source configured in isolated QA save"; return true;
        }
        throw new InvalidOperationException("Unknown machine binding setup step");
    }
}

// Cold-load the same save, then test both bindings through actual pointer and machine callbacks.
public sealed class MachineBindingReloadScenario
{
    private QaLoadSlot load;
    private string[] context;
    private GameItem machine, module, storage, stored, supply, oldBattery, spare;
    private int step;
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    public bool Tick(out string result)
    {
        result = null; MachineFeatureQa.Check(Time.realtimeSinceStartup < deadline, "Machine binding reload timed out at " + step);
        if (load == null)
        {
            context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-machine-bindings.txt")).Split('|');
            MachineFeatureQa.Check(context.Length == 10 && context[0] == "1" && Guid.TryParse(context[1], out _) &&
                context.Skip(2).All(s => int.TryParse(s, out _)), "Invalid QA machine binding context");
            load = new QaLoadSlot(int.Parse(context[2]), Guid.Parse(context[1])); return false;
        }
        if (!load.Tick()) return false;
        if (step == 0)
        {
            var items = PlayerStore.Instance.FindAllItem();
            machine = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[3]));
            module = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[4]));
            storage = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[5]));
            stored = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[6]));
            supply = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[7]));
            oldBattery = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[8]));
            spare = items.SingleOrDefault(i => i.uniqueId == int.Parse(context[9]));
            MachineFeatureQa.Check(machine != null && module != null && storage != null && stored != null && supply != null && oldBattery != null && spare != null,
                "Missing saved machine binding fixture");
            var calendar = AdvCalendarUIManager.Instance;
            if (calendar?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(260, 140)); machine.contentWindow.ToFront();
            RoutingScenario.OpenOptionAction("f10.open"); step++; return false;
        }
        if (step == 1)
        {
            var browser = CustomUIManager.Instance.GetWindow(MachineFeatureQa.Mod + ".f10");
            var bay = MachineFeatureQa.Panel<GameGridInventory>(machine, "module");
            var storageGrid = storage.contentWindow.childElement as GameGridInventory;
            MachineFeatureQa.Check(browser?.IsAlive == true && module.parentInventory == bay && stored.parentInventory == storageGrid,
                "Saved module fixture was not restored");
            var destination = storageGrid.TryFindOneValidInventorySlot(module);
            MachineFeatureQa.Check(MachineProgressHelper.GetMachineState(machine) == MachineProgressHelper.STATE_READY &&
                !bay.IsRemoveLocked() && module.MayRemove() && destination != null && destination.targetItem == null && destination.numTransfer >= module.unitCount,
                "Cold-loaded module fixture is not idle/removable or its bound storage has no legal space: state=" +
                MachineProgressHelper.GetMachineState(machine) + " locked=" + bay.IsRemoveLocked());
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(ModuleGridScenario);
            var source = type.GetMethod("FindBoardHit", flags).Invoke(null, new object[] { browser, module, bay });
            var proxyHit = type.GetMethod("FindHit", flags).Invoke(null, new object[] { browser, stored });
            var proxy = proxyHit?.GetType().GetField("view", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(proxyHit);
            MachineFeatureQa.Check(source != null && proxy != null, "Restored module workbench lacks the source or bound-storage proxy");
            type.GetMethod("DragToProxy", flags).Invoke(null, new[] { source, proxy });
            MachineFeatureQa.Check(module.parentInventory == storageGrid, "F10 drag did not return the same module to the restored binding");
            browser.Close(); step++; return false;
        }
        if (step == 2)
        {
            var power = MachineHelper.GetBatterySlot(machine); var source = supply.contentWindow.childElement as GameGridInventory;
            MachineFeatureQa.Check(power.childItem == oldBattery && spare.parentInventory == source, "Saved battery fixture not restored");
            var seed = MachineFeatureQa.Item("hydroreed_seed"); var freshWater = MachineFeatureQa.Item("wine_bottle");
            WaterHelper.AddWater(freshWater, 2, 750000);
            MachineFeatureQa.Put(MachineHydroponic.GetInputSlot(machine), seed);
            MachineFeatureQa.Put(MachineHydroponic.GetWaterSlot(machine), freshWater);
            GameItem water = MachineHydroponic.GetWaterSlotItem(machine); int before = WaterHelper.GetCurrentCapacityML(water);
            bool night = CurrentConverterHelper.isNightCycle;
            try { CurrentConverterHelper.isNightCycle = true; MachineFeatureQa.Cycle(machine); }
            finally { CurrentConverterHelper.isNightCycle = night; }
            MachineFeatureQa.Check(power.childItem == spare && oldBattery.parentInventory == source &&
                oldBattery.GetTagReadonly("power_source_item_energy").GetInt() == 2 &&
                spare.GetTagReadonly("power_source_item_energy").GetInt() < 100 && WaterHelper.GetCurrentCapacityML(water) < before,
                "F31 did not restore its source and exchange the exact batteries before a productive night callback");
            result = "PASS F10 same-module drag to cold-loaded storage and F31 productive night callback swaps to cold-loaded supply"; return true;
        }
        throw new InvalidOperationException("Unknown machine binding reload step");
    }
}

// Check actual F30 filtering, output detection and bringing an open source window forward.
public sealed class MachineAttentionScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private GameItem waiting, ready, furnace;
    private int step;
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    public bool Tick(out string result)
    {
        result = null; MachineFeatureQa.Check(Time.realtimeSinceStartup < deadline, "Machine attention timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (StoreUIManager.Instance?.gameOptionUIManager == null || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f30.enabled");
            waiting = MachineFeatureQa.Item("hydroponic");
            MachineFeatureQa.Put(EmporiumEntry.Instance.invElement, waiting); ready = MachineFeatureQa.Stock("hydroponic");
            GeneralHelper.SetCustomName(waiting, "QA overview needs seed"); GeneralHelper.SetCustomName(ready, "QA overview ready");
            SupplyHydro(ready);
            furnace = MachineFeatureQa.Stock("furnace");
            MachineFeatureQa.Put(MachineHelper.GetBatterySlot(furnace), MachineFeatureQa.Item("energy_credit_dis"));
            var input = MachineFeatureQa.Panel<GameGridInventory>(furnace, "input");
            MachineFeatureQa.Put(input, MachineFeatureQa.Item("scrap_metal")); MachineFeatureQa.Put(input, MachineFeatureQa.Item("scrap_metal"));
            MachineFeatureQa.Cycle(furnace);
            MachineFeatureQa.Check(MachineFeatureQa.Panel<GameGridInventory>(furnace, "output").childItems.Any(i => i.identifier == "metal_ingot"),
                "Native furnace did not produce the QA output");
            RoutingScenario.OpenOptionAction("f30.open");
            var overview = Overview();
            MachineFeatureQa.Check(QaItemUi.Hit(overview.root, waiting) != null && QaItemUi.Hit(overview.root, ready) != null &&
                MachineFeatureQa.Text(overview.root.transform).Contains(string.Format(CultureInfo.CurrentCulture,
                    ModHelper.GetLocalized(MachineFeatureQa.Mod, "f30.output"), 1)), "Overview omitted a machine or its native completed output");
            QaItemUi.Click(overview, waiting); overview = Overview();
            var locate = overview.Get("locate")?.button; MachineFeatureQa.Check(locate != null, "Visible source has no locate action");
            var sourceWindow = EmporiumEntry.Instance.invWindow;
            MachineFeatureQa.Check(sourceWindow != null && sourceWindow.IsVisible(), "Native source window is not visible");
            long stamp = sourceWindow.focusStamp; locate.onClick.Invoke();
            MachineFeatureQa.Check(sourceWindow.focusStamp > stamp, "Locate did not focus the real source window");
            var label = overview.root.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.text == ModHelper.GetLocalized(MachineFeatureQa.Mod, "f30.attention"));
            MachineFeatureQa.Check(label != null, "Attention filter label missing");
            Toggle toggle = null;
            for (Transform p = label.transform; p != null && p != overview.root.transform && toggle == null; p = p.parent)
            {
                var choices = p.GetComponentsInChildren<Toggle>(true).Where(t => t.gameObject.activeInHierarchy).ToArray();
                if (choices.Length == 1) toggle = choices[0];
            }
            MachineFeatureQa.Check(toggle != null, "Attention filter control missing"); toggle.isOn = true;
            step++; return false;
        }
        if (step == 2)
        {
            var overview = Overview();
            if (QaItemUi.Hit(overview.root, ready) != null) return false;
            MachineFeatureQa.Check(QaItemUi.Hit(overview.root, waiting) != null && QaItemUi.Hit(overview.root, furnace) != null,
                "Attention filter hid a missing-input or finished-output machine");
            SupplyHydro(waiting); step++; return false;
        }
        if (step == 3)
        {
            var overview = Overview();
            if (QaItemUi.Hit(overview.root, waiting) != null) return false;
            MachineFeatureQa.Check(QaItemUi.Hit(overview.root, furnace) != null, "Live update lost a completed output");
            result = "PASS F30 real finished output, attention filter, live readiness and locate source window"; return true;
        }
        throw new InvalidOperationException("Unknown machine attention step");
    }
    private static CustomUIWindow Overview() => CustomUIManager.Instance.GetWindow(MachineFeatureQa.Mod + ".f30")
        ?? throw new InvalidOperationException("Machine overview unavailable");
    private static void SupplyHydro(GameItem machine)
    {
        var seed = MachineFeatureQa.Item("hydroreed_seed"); var bottle = MachineFeatureQa.Item("wine_bottle");
        WaterHelper.AddWater(bottle, 2, 750000);
        MachineFeatureQa.Put(MachineHydroponic.GetInputSlot(machine), seed);
        MachineFeatureQa.Put(MachineHydroponic.GetWaterSlot(machine), bottle);
        MachineFeatureQa.Put(MachineHelper.GetBatterySlot(machine), MachineFeatureQa.Item("energy_credit_dis"));
    }
}
