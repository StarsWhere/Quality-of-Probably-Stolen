using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Exercises saved maintenance rules through real machines and the rendered controls.
public sealed class MaintenanceCopyScenario : EquipmentFixture
{
    [Serializable] private sealed class Book
    {
        public int version = 0, slot = 0;
        public string run = null;
        public Rule[] rules = null;
    }
    [Serializable] private sealed class Rule
    {
        public int kind = 0, host = 0, threshold = 0, priority = 0;
        public string location = null;
    }

    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private GameItem source, configured, target, installed;
    private GameSlotInventory power;
    private int sourceId, configuredId, targetId;
    private Rule configuredBefore;
    private float noticeDeadline;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public bool Tick(out string result)
    {
        result = null;
        Check(Time.realtimeSinceStartup < deadline, "Maintenance copy timed out at step " + step);
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f31.enabled");
            TradeScenario.SetOptions(false, "f15.enabled", "f21.enabled");
            source = FeedScenario.New("hydroponic");
            configured = FeedScenario.New("printer");
            target = FeedScenario.New("printer");
            GeneralHelper.SetCustomName(source, "QA copy source hydroponic");
            GeneralHelper.SetCustomName(configured, "QA existing printer");
            GeneralHelper.SetCustomName(target, "QA new printer");
            Place(source); Place(configured); Place(target);
            power = MachineHelper.GetBatterySlot(source);
            var seedSlot = MachineHydroponic.GetInputSlot(source);
            var waterSlot = MachineHydroponic.GetWaterSlot(source);
            Check(power != null && seedSlot != null && waterSlot != null, "Native hydroponic maintenance slots are missing");
            installed = FeedScenario.New("energy_credit");
            var seed = FeedScenario.New("hydroreed_seed");
            var water = FeedScenario.New("wine_bottle");
            Place(installed); Place(seed); Place(water);
            PowerHelper.SetPowerSourceAt(installed, 2);
            WaterHelper.AddWater(water, 2, 750000);
            FeedScenario.Put(power, installed); FeedScenario.Put(seedSlot, seed); FeedScenario.Put(waterSlot, water);
            EmptyMatchingStock();
            sourceId = source.GetUniqueID(); configuredId = configured.GetUniqueID(); targetId = target.GetUniqueID();
            Check(sourceId > 0 && configuredId > 0 && targetId > 0 && sourceId != configuredId &&
                sourceId != targetId && configuredId != targetId, "Fixture machines do not have distinct saved identities");

            Configure(configured, 73, 0, false);
            configuredBefore = FindRule(ReadBook(), configuredId);
            Check(configuredBefore.location == "root:stock" && configuredBefore.threshold == 73 && configuredBefore.priority == 0,
                "Existing printer rule was not saved to main stock");
            Configure(source, 47, 1, true);
            Book saved = ReadBook();
            Rule sourceRule = FindRule(saved, sourceId);
            Check(sourceRule.location == "root:stock" && sourceRule.threshold == 47 && sourceRule.priority == 1,
                "Hydroponic rule with an empty main-stock source was not saved");
            Check(saved.rules.All(rule => rule.host != targetId || rule.kind != 31), "Unconfigured printer unexpectedly has a rule");
            step = 1; return false;
        }
        if (step == 1)
        {
            // Produce an actual blocked night attempt so the source has transient state to exclude from the copy.
            bool originalNight = CurrentConverterHelper.isNightCycle;
            try { CurrentConverterHelper.isNightCycle = true; FeedScenario.Cycle(source); }
            finally { CurrentConverterHelper.isNightCycle = originalNight; }
            IDictionary attempts = RuntimeMap("batteryAttemptDay"), problems = RuntimeMap("maintenanceProblems");
            Check(attempts.Contains(sourceId) && problems.Contains("31:" + sourceId) &&
                !attempts.Contains(targetId) && !problems.Contains("31:" + targetId),
                "Native empty-stock cycle did not produce the expected source-only transient state");
            Check(ReferenceEquals(power.childItem, installed) && MatchingInStock() == 0,
                "The empty-stock cycle moved or consumed a battery");

            source.contentWindow.ToFront();
            RoutingScenario.RunFeatureAction("f31.open");
            var view = Maintenance();
            Check(view.Get("copy")?.button?.interactable == true, "Saved hydroponic rule cannot be copied");
            view.Get("copy").button.onClick.Invoke();
            var picker = CustomUIManager.Instance.GetWindow(Mod + ".maintenance.copy");
            Check(picker?.IsAlive == true && picker.root.activeInHierarchy && picker.Get("copy_apply")?.button != null,
                "Maintenance copy target selector did not open");
            Check(QaItemUi.Hit(picker.root, configured) != null && QaItemUi.Hit(picker.root, target) != null,
                "Actual configured and unconfigured printers are not offered as copy targets");
            Check(picker.root.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == ModHelper.GetLocalized(Mod, "maintenance.configured")),
                "Existing target is not marked as configured");
            QaItemUi.Click(picker, configured); QaItemUi.Click(picker, target);
            Check(QaItemUi.Selected(picker, configured) && QaItemUi.Selected(picker, target), "Copy targets were not selected");
            picker.Get("copy_apply").button.onClick.Invoke();
            Check(picker.IsAlive != true, "Copy selector did not close after copying");
            Book after = ReadBook();
            Rule sourceAfter = FindRule(after, sourceId), existingAfter = FindRule(after, configuredId), copied = FindRule(after, targetId);
            Check(SameRule(existingAfter, configuredBefore), "Copy overwrote the configured printer");
            Check(copied.kind == 31 && copied.host == targetId && copied.location == sourceAfter.location &&
                copied.threshold == sourceAfter.threshold && copied.priority == sourceAfter.priority,
                "New printer did not receive exactly the saved source configuration");
            Check(attempts.Contains(sourceId) && problems.Contains("31:" + sourceId) &&
                !attempts.Contains(targetId) && !problems.Contains("31:" + targetId),
                "Copy propagated or removed runtime maintenance state");
            Check(ReferenceEquals(power.childItem, installed) && MatchingInStock() == 0,
                "Copy changed physical batteries");
            noticeDeadline = Time.realtimeSinceStartup + 18f; step = 2; return false;
        }
        if (step == 2)
        {
            string expected = ModHelper.GetLocalized(Mod, "maintenance.copy_result", 1, 1);
            var notice = GameObject.Find(Mod + ".notice");
            string message = notice?.GetComponentInChildren<TMP_Text>()?.text;
            if (notice?.activeInHierarchy != true || message != expected)
            {
                Check(Time.realtimeSinceStartup < noticeDeadline, "Copy/skip summary did not appear: " + message);
                return false;
            }
            // Re-read the current save after the UI reports success.
            Book final = ReadBook();
            Check(SameRule(FindRule(final, configuredId), configuredBefore) && FindRule(final, targetId).location == "root:stock",
                "Saved copy or skipped rule changed after the summary");
            result = "PASS empty main-stock maintenance preset, saved hydroponic-to-printer static copy, existing-rule skip and visible 1/1 summary";
            return true;
        }
        throw new InvalidOperationException("Unknown maintenance copy step " + step);
    }

    private static CustomUIWindow Maintenance()
    {
        var window = CustomUIManager.Instance.GetWindow(Mod + ".maintenance");
        Check(window?.IsAlive == true && window.IsVisible, "Maintenance settings window is unavailable");
        return window;
    }

    private static void Configure(GameItem machine, int threshold, int priority, bool checkEmpty)
    {
        Open(machine); machine.contentWindow.ToFront(); RoutingScenario.RunFeatureAction("f31.open");
        var window = Maintenance();
        Check(window.Get("source")?.button != null, "Main-stock source selector is missing");
        window.Get("source").button.onClick.Invoke();
        var picker = CustomUIManager.Instance.GetWindow(Mod + ".choose");
        if (picker?.IsAlive == true)
        {
            string stockName = ModHelper.GetLocalized(Mod, "inv.stock");
            Button stock = picker.root.GetComponentsInChildren<Button>(true).FirstOrDefault(button =>
                button.gameObject.activeInHierarchy && button.GetComponentInChildren<TMP_Text>(true)?.text == stockName);
            Check(stock != null && stock.interactable, "Main stock is missing from the source picker");
            stock.onClick.Invoke();
        }
        window = Maintenance();
        if (checkEmpty)
        {
            string counts = ModHelper.GetLocalized(Mod, "maintenance.spare_count", 0, 0);
            Check(MatchingInStock() == 0 && window.root.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == counts),
                "Main-stock preset did not show zero matching and exchangeable spares");
        }
        Check(window.Get("threshold")?.slider != null && window.Get("priority")?.dropdown != null &&
            window.Get("enable")?.button?.interactable == true, "An empty main-stock source cannot be saved");
        window.Get("threshold").SetFloat(threshold, true);
        window.Get("priority").SetIndex(priority, true);
        window.Get("enable").button.onClick.Invoke();
        Check(window.IsAlive != true, "Saving the maintenance rule did not close its settings window");
    }

    private static bool TryPlace(GameInventory inventory, GameItem item)
    {
        var marker = inventory?.TryFindOneValidInventorySlot(item);
        return marker != null && ReferenceEquals(marker.inventory, inventory) && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, inventory);
    }

    private static void Place(GameItem item)
    {
        var e = EmporiumEntry.Instance;
        Check(TryPlace(e.invElement, item) || TryPlace(e.backInvinvElement, item) || TryPlace(e.frontInvinvElement, item),
            "Cannot place QA fixture " + item.identifier);
    }

    private static void EmptyMatchingStock()
    {
        var e = EmporiumEntry.Instance;
        foreach (GameItem item in e.invElement.childItems.Where(value => value.identifier == "energy_credit").ToArray())
            Check(TryPlace(e.backInvinvElement, item) || TryPlace(e.frontInvinvElement, item), "Could not clear matching batteries from main stock");
        Check(MatchingInStock() == 0, "Main stock still contains a matching battery");
    }

    private static int MatchingInStock() => EmporiumEntry.Instance.invElement.childItems.Count(item => item.identifier == "energy_credit");

    private static Book ReadBook()
    {
        string raw = ModHelper.GetModData(Mod, "machineMaintenance", null);
        Check(!string.IsNullOrEmpty(raw), "Maintenance data was not written to the current save");
        var allowed = new[] { "version", "run", "slot", "rules", "kind", "host", "location", "threshold", "priority", "materials", "grade" };
        var keys = Regex.Matches(raw, "\\\"([A-Za-z_][A-Za-z_0-9]*)\\\"\\s*:").Cast<Match>().Select(match => match.Groups[1].Value);
        Check(keys.All(allowed.Contains), "Maintenance save unexpectedly contains runtime state: " + raw);
        Book book = JsonUtility.FromJson<Book>(raw);
        Check(book != null && book.version == 3 && book.rules != null && book.slot == PlayerStore.Instance.saveSlotId &&
            Guid.TryParseExact(book.run, "N", out Guid savedRun) && Guid.TryParse(PlayerStore.Instance.runID, out Guid currentRun) && savedRun == currentRun,
            "Maintenance data was not written for the current save: " + raw);
        return book;
    }

    private static Rule FindRule(Book book, int id)
    {
        var matching = book.rules.Where(rule => rule != null && rule.kind == 31 && rule.host == id).ToArray();
        Check(matching.Length == 1, "Expected exactly one battery rule for machine " + id);
        return matching[0];
    }

    private static bool SameRule(Rule a, Rule b) => a != null && b != null && a.kind == b.kind && a.host == b.host &&
        a.location == b.location && a.threshold == b.threshold && a.priority == b.priority;

    private static IDictionary RuntimeMap(string name)
    {
        var assembly = ModLoader.Mods.Single(mod => mod.Manifest?.ID == Mod).Instance.GetType().Assembly;
        Type type = assembly.GetType("ShopAssist.MachineTools", true);
        object current = type.GetField("current", Fields)?.GetValue(null);
        var result = type.GetField(name, Fields)?.GetValue(current) as IDictionary;
        Check(result != null, "Maintenance runtime state is unavailable: " + name);
        return result;
    }
}
