using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Exercises the installed AquaSense and Item Details without changing their settings.
public sealed class CoexistLiquidScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem source, target, controlSource, controlTarget, filteredTarget, oldFilter, filterSourceA, filterSourceB;
    private GameSlotInventory filterSlot;
    private string originalPeerSettings, beforeTooltip, restored;
    private int step;
    private float started = Time.realtimeSinceStartup;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Peer(string id)
    {
        var mod = ModLoader.Mods.Find(m => m.Manifest != null && m.Manifest.ID == id);
        Need(mod != null && mod.IsEnabled && mod.Instance != null && string.IsNullOrEmpty(mod.LoadError), "Peer not running: " + id);
    }
    private static GameItem Item(string id, string name)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing " + id);
        GeneralHelper.SetItemOwned(item); GeneralHelper.SetCustomName(item, name);
        var root = EmporiumEntry.Instance.invElement; var slot = root.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.targetItem == null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == root,
            "Could not place " + id);
        return item;
    }
    private static bool AquaScannerReady()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("AquaSense.Hooks", false)).FirstOrDefault(t => t != null);
        var cache = type?.GetField("_scannerCache", BindingFlags.NonPublic | BindingFlags.Static);
        Need(cache != null, "AquaSense scanner state unavailable");
        return (bool)cache.GetValue(null);
    }
    private static string PeerSettings()
    {
        var aqua = ModConfig.Find("chiqingsan.AquaSense"); var item = ModConfig.Find("item_details");
        Need(aqua != null && item != null, "Peer settings unavailable");
        return string.Join("|", new[] { "ShowCompositionWithoutScanner", "AutoReplaceFilters", "AutoReplaceCloth", "ShowFilterDurability", "AutoUpdateWaterLabels" }
            .Select(key => ((ConfigEntry<bool>)aqua.Get(key)).Value.ToString())) + "|" + ((ConfigEntry<string>)item.Get("liquid_analysis")).Value;
    }
    private static string Tooltip(GameItem item) => ((GameItemElement)item).GetTooltip().nakedText;
    private static bool HasAquaText(string text) => text.Contains("水质详情 (") || text.Contains("Water composition (");
    private static bool HasDetailRow(string text, string liquidId, int volume, int total)
    {
        string name = Liquid.Liquids.First(liquid => liquid.identifier == liquidId).displayName;
        // Item Details uses <pos> columns, or ideographic spaces when a peer requests flowing text.
        const string column = @"(?:<pos=[^>\r\n]+>|\u3000+)";
        string pattern = Regex.Escape(name) + @"[^<\r\n]*?" + column +
            @"\s*(?<amount>\d+(?:[.,]\d+)?)\s*mL" + column + @"\s*(?<percent>\d+(?:[.,]\d+)?)\s*%";
        foreach (Match row in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
        {
            if (double.TryParse(row.Groups["amount"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) &&
                double.TryParse(row.Groups["percent"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) &&
                Math.Abs(amount - volume / 1000d) <= 0.051 && Math.Abs(percent - 100d * volume / total) <= 0.011) return true;
        }
        return false;
    }
    private static bool DetailsWaterEnabled()
    {
        var entry = (ConfigEntry<string>)ModConfig.Find("item_details").Get("liquid_analysis");
        return entry.Choices != null && Array.IndexOf(entry.Choices, entry.Value) > 0;
    }
    private static Transform Bars() => WaterScannerUIManager.Instance.panel.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "ShopAssist_LiquidBars" && t.gameObject.activeInHierarchy);
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".water");
    private static void Action(string key) => RoutingScenario.OpenOptionAction(key);

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - started < 90, "Liquid coexistence timed out at step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (e.storeInteractables?.activeInHierarchy != true || WaterScannerUIManager.Instance == null) return false;
            Peer("chiqingsan.AquaSense"); Peer("item_details"); originalPeerSettings = PeerSettings();
            Item("aquascan", "QA water scanner");
            source = Item("bottled_water", "QA coexist source"); target = Item("water_jug", "QA coexist target");
            controlSource = Item("bottled_water", "QA manual source"); controlTarget = Item("water_jug", "QA manual target");
            filteredTarget = Item("water_pitcher", "QA coexist filtered target");
            oldFilter = Item("water_filter", "QA low filter"); Item("water_filter", "QA spare filter");
            filterSourceA = Item("bottled_water", "QA filter source A");
            filterSourceB = Item("bottled_water", "QA filter source B");
            filterSlot = filteredTarget.contentWindow?.childElement as GameSlotInventory;
            var filterMarker = filterSlot?.TryFindOneValidInventorySlot(oldFilter);
            Need(filterMarker != null && filterMarker.TryAcceptOnce(oldFilter.unitCount) == oldFilter.unitCount &&
                ReferenceEquals(filterSlot.childItem, oldFilter), "Could not install QA filter");
            DurabilityHelper.SetCurrentDurability(oldFilter, 1);
            foreach (var i in new[] { filterSourceA, filterSourceB })
            {
                WaterHelper.Remove(i, WaterHelper.GetCurrentCapacityUL(i));
                WaterHelper.AddWater(i, 5, 250000); WaterFeatureHelper.HandleWaterCheckTestingStrip(i);
            }
            foreach (var i in new[] { source, target, controlSource, controlTarget }) WaterHelper.Remove(i, WaterHelper.GetCurrentCapacityUL(i));
            foreach (var i in new[] { source, controlSource })
            {
                WaterHelper.AddLiquid(i, "water", 50000); WaterHelper.AddLiquid(i, "organic_waste", 2000);
                WaterFeatureHelper.HandleWaterCheckTestingStrip(i);
            }
            foreach (var i in new[] { target, controlTarget })
            {
                WaterHelper.AddLiquid(i, "water", 30000); WaterFeatureHelper.HandleWaterCheckTestingStrip(i);
            }
            TradeScenario.SetOptions(true, "f19.enabled", "f26.enabled", "f26.mix");
            step++; return false;
        }
        if (step == 2)
        {
            if (!AquaScannerReady()) return false;
            beforeTooltip = Tooltip(target);
            Need(HasAquaText(beforeTooltip), "AquaSense water tooltip unavailable despite owned scanner: " + beforeTooltip);
            if (DetailsWaterEnabled()) Need(HasDetailRow(beforeTooltip, "water", 30000, 30000),
                "Item Details did not show the initial water component and amount: " + beforeTooltip);
            var scanner = WaterScannerUIManager.Instance; scanner.Open(null, target);
            Need(Bars() != null && Bars().GetComponentsInChildren<Image>(true).Any(i => i.gameObject.activeInHierarchy),
                "Composition bars unavailable with the two peer tooltips enabled");
            scanner.contaminantTMP.text = "QA external scanner output";
            step++; return false;
        }
        if (step == 3)
        {
            var scanner = WaterScannerUIManager.Instance;
            Need(scanner.contaminantTMP.text == "QA external scanner output", "A later scanner writer was overwritten");
            if (Bars() != null) return false;
            scanner.RefreshUI(); Need(Bars() != null && scanner.contaminantTMP.text != "QA external scanner output",
                "Native refresh did not restore current composition and bars");
            controlSource.Target(controlTarget);
            if (DetailsWaterEnabled())
            {
                string nativeTooltip = Tooltip(controlTarget);
                Need(HasDetailRow(nativeTooltip, "water", 80000, 82000) && HasDetailRow(nativeTooltip, "organic_waste", 2000, 82000),
                    "Item Details did not update water and waste after a native pour: " + nativeTooltip);
            }
            Action("f26.open");
            QaItemUi.Choose(Window(), "target", "QA coexist target");
            QaItemUi.Click(Window(), source); Window().Get("preview").button.onClick.Invoke();
            int prior = WaterHelper.GetCurrentCapacityUL(target);
            Need(prior == 30000 && WaterHelper.GetCurrentCapacityUL(source) == 52000, "Preview changed liquid");
            Window().Get("pour").button.onClick.Invoke();
            Need(WaterHelper.GetCurrentCapacityUL(source) == WaterHelper.GetCurrentCapacityUL(controlSource) &&
                WaterHelper.GetCurrentCapacityUL(target) == WaterHelper.GetCurrentCapacityUL(controlTarget) &&
                WaterHelper.GetLiquidParts(target).SequenceEqual(WaterHelper.GetLiquidParts(controlTarget)),
                "F26 and a single native pour produced different amounts or composition");
            string afterTooltip = Tooltip(target);
            Need(HasAquaText(afterTooltip) && afterTooltip != beforeTooltip && (!DetailsWaterEnabled() ||
                HasDetailRow(afterTooltip, "water", 80000, 82000) && HasDetailRow(afterTooltip, "organic_waste", 2000, 82000)),
                "Peer water tooltip was lost or stale after pouring: " + afterTooltip);
            Need(PeerSettings() == originalPeerSettings, "A peer setting was changed");
            scanner.RefreshUI();
            string output = scanner.contaminantTMP.text;
            string waste = Liquid.Liquids.First(l => l.identifier == "organic_waste").displayName;
            Need(output.Contains(waste + ":") && Bars() != null &&
                Bars().GetComponentsInChildren<Transform>(true).Count(t => t.name.StartsWith("Component_", StringComparison.Ordinal)) ==
                output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length,
                "Scanner composition or bars did not refresh after the pour: " + output);
            TradeScenario.SetOptions(false, "f19.enabled");
            restored = scanner.contaminantTMP.text;
            step++; return false;
        }
        if (step == 4)
        {
            if (Bars() != null) return false;
            var scanner = WaterScannerUIManager.Instance; scanner.RefreshUI();
            Need(restored == scanner.contaminantTMP.text && Bars() == null, "Disabling F19 did not restore native scanner output");
            scanner.Close();
            if (((ConfigEntry<bool>)ModConfig.Find("chiqingsan.AquaSense").Get("AutoReplaceFilters")).Value)
            {
                Action("f26.open");
                QaItemUi.Choose(Window(), "target", "QA coexist filtered target");
                QaItemUi.Click(Window(), filterSourceA); QaItemUi.Click(Window(), filterSourceB);
                Window().Get("preview").button.onClick.Invoke(); Window().Get("pour").button.onClick.Invoke();
                int leftA = WaterHelper.GetCurrentCapacityUL(filterSourceA), leftB = WaterHelper.GetCurrentCapacityUL(filterSourceB);
                Need(DurabilityHelper.GetRealDurability(oldFilter) == 0 &&
                    ((leftA == 0 && leftB == 250000) || (leftB == 0 && leftA == 250000)),
                    "F26 continued pouring through a newly exhausted filter");
                string remaining = leftA > 0 ? "QA filter source A" : "QA filter source B";
                Need(QaItemUi.Selected(Window(), leftA > 0 ? filterSourceA : filterSourceB), "Remaining source was not kept selected");
                Window().Get("preview").button.onClick.Invoke();
                Need(Window().Get("pour").GetText() == ModHelper.GetLocalized(Mod, "f26.pourUnfiltered"),
                    "A depleted filter did not require an explicit unfiltered pour");
                step++; return false;
            }
            result = "PASS peer water tooltips/settings, later-writer ownership, native-pour conservation and refreshed scanner composition; peer auto-replacement is disabled";
            return true;
        }
        if (step == 5)
        {
            if (ReferenceEquals(filterSlot.childItem, oldFilter)) return false;
            Need(filterSlot.childItem != null && DurabilityHelper.GetRealDurability(filterSlot.childItem) > 0 && oldFilter.parentInventory != null,
                "The peer's delayed filter replacement lost a filter");
            int beforeA = WaterHelper.GetCurrentCapacityUL(filterSourceA), beforeB = WaterHelper.GetCurrentCapacityUL(filterSourceB);
            Window().Get("pour").button.onClick.Invoke();
            Need(WaterHelper.GetCurrentCapacityUL(filterSourceA) == beforeA && WaterHelper.GetCurrentCapacityUL(filterSourceB) == beforeB,
                "A preview for the exhausted filter was accepted after the filter changed");
            Window().Get("preview").button.onClick.Invoke(); Window().Get("pour").button.onClick.Invoke();
            Need(WaterHelper.GetCurrentCapacityUL(filterSourceA) == 0 && WaterHelper.GetCurrentCapacityUL(filterSourceB) == 0 &&
                PeerSettings() == originalPeerSettings, "Resuming F26 lost liquid or changed peer settings");
            result = "PASS peer water tooltips/settings, scanner ownership and refresh, native-pour conservation and resume after delayed filter replacement";
            return true;
        }
        return false;
    }
}
