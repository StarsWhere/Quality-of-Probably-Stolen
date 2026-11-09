using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

internal static class LedgerProbe
{
    internal const string Mod = "starswhere.shopassist";

    [Serializable] internal sealed class Data
    {
        public int schemaVersion = -1;
        public Entry[] entries = null, legacy = null;
        public Transaction[] transactions = null;
    }
    [Serializable] internal sealed class Entry
    {
        public int day = -1, kind = -1, events = -1, unknownCost = -1, knownCostCount = -1;
        public long income = -1, expense = -1, knownMargin = -1;
    }
    [Serializable] internal sealed class Transaction
    {
        public int day = -1, kind = -1;
        public long change = long.MinValue;
    }

    internal static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static Data History()
    {
        string raw = ModHelper.GetModData(Mod, "ledger", null);
        Data data = raw == null ? null : JsonUtility.FromJson<Data>(raw);
        Need(data != null && data.schemaVersion == 2 && data.entries != null && data.legacy != null && data.transactions != null,
            "Saved ledger does not contain version 2 aggregates, legacy rows and transactions: " + raw);
        return data;
    }
    internal static string Signed(long value) => value.ToString("+0;-0;0", CultureInfo.CurrentCulture);
    internal static IEnumerable<string[]> Rows(CustomUIWindow window) => window.root.GetComponentsInChildren<HorizontalLayoutGroup>(true)
        .Where(row => row.gameObject.activeInHierarchy)
        .Select(row => row.GetComponentsInChildren<TMP_Text>(true).Where(text => text.gameObject.activeInHierarchy).Select(text => text.text).ToArray())
        .Where(row => row.Length == 3);
    internal static int RowCount(CustomUIWindow window, string label, long change, string profit) =>
        Rows(window).Count(row => row[0] == label && row[1] == Signed(change) && row[2] == profit);
    internal static bool Text(CustomUIWindow window, string value) => window.root.GetComponentsInChildren<TMP_Text>(true)
        .Any(text => text.gameObject.activeInHierarchy && text.text == value);
    internal static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".ledger");
    internal static int Day(CustomUIWindow window)
    {
        Need(window?.IsAlive == true && int.TryParse(window.Get("day_input")?.GetString(), NumberStyles.None,
            CultureInfo.InvariantCulture, out _), "Ledger day is unavailable");
        return int.Parse(window.Get("day_input").GetString(), CultureInfo.InvariantCulture);
    }
    internal static int Sell(string identifier)
    {
        var item = DirectoryMaster.Item(identifier);
        Need(item != null, "Missing sale fixture " + identifier);
        GeneralHelper.SetItemOwned(item, true);
        var grid = EmporiumEntry.Instance.invElement;
        var marker = grid.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == grid && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid && !item.IsTag("BOUGHT_PRICE_TAG"),
            "Cannot place an unknown-cost sale fixture");
        var player = PlayerStore.Instance;
        player.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateSpacerClientFood());
        player.isClientArrived = true;
        int before = player.playerCash;
        player.SellItem(item);
        int amount = player.playerCash - before;
        Need(amount > 0 && !GeneralHelper.IsItemOwned(item), "Native sale did not complete");
        return amount;
    }
    internal static void CloseCalendarAndHints()
    {
        if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
        if (StoreUIManager.Instance.controlHintOverlay?.activeSelf == true) StoreUIManager.Instance.ToggleControlHintOverlay();
    }
}

// Uses a saved v1 aggregate, then verifies migration and new transactions across a reload.
public sealed class LedgerMigrationScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 170f;
    private QaLoadSlot load;
    private string run, oldRaw;
    private int step, slot, day, first, second, balance;

    public bool Tick(out string result)
    {
        result = null;
        LedgerProbe.Need(Time.realtimeSinceStartup < deadline, "Ledger migration timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(false, "f28.enabled", "f16.enabled");
            var player = PlayerStore.Instance;
            day = StoreStation.GetDayCounter();
            LedgerProbe.Need(day > 0, "Invalid day for the legacy fixture");
            oldRaw = "{\"schemaVersion\":1,\"entries\":[{\"day\":" + day.ToString(CultureInfo.InvariantCulture) +
                ",\"kind\":2,\"income\":137,\"expense\":0,\"knownMargin\":37,\"events\":2,\"unknownCost\":1,\"knownCostCount\":1}]}";
            ModHelper.SetModData(LedgerProbe.Mod, "ledger", oldRaw);
            run = player.runID; slot = player.saveSlotId;
            player.SaveGame(); StoreUIManager.Instance.escapeUIManager.OnMainMenu();
            load = new QaLoadSlot(slot, Guid.Parse(run)); step++; return false;
        }
        if (step == 2)
        {
            if (!load.Tick()) return false;
            TradeScenario.SetOptions(true, "f28.enabled");
            RoutingScenario.RunFeatureAction("f28.open");
            var window = LedgerProbe.Window();
            LedgerProbe.Need(LedgerProbe.Day(window) == day && ModHelper.GetModData(LedgerProbe.Mod, "ledger", null) == oldRaw,
                "Reading legacy history changed its original saved bytes or day");
            CheckLegacy(window, false);
            window.Close();
            first = LedgerProbe.Sell("wine_yeast"); second = LedgerProbe.Sell("wine_superyeast");
            LedgerProbe.Need(first != second, "Migration fixture requires different same-kind cash changes");
            balance = PlayerStore.Instance.playerCash;
            CheckSavedAndVisible();
            PlayerStore.Instance.currentClientInstance = null; PlayerStore.Instance.isClientArrived = false;
            PlayerStore.Instance.SaveGame(); StoreUIManager.Instance.escapeUIManager.OnMainMenu();
            load = new QaLoadSlot(slot, Guid.Parse(run)); step++; return false;
        }
        if (!load.Tick()) return false;
        LedgerProbe.Need(PlayerStore.Instance.playerCash == balance && StoreStation.GetDayCounter() == day,
            "Migration reload restored the wrong day or cash");
        CheckSavedAndVisible();
        result = "PASS F28 v1 aggregate retained as legacy, two same-kind transactions and totals survive reload";
        return true;
    }

    private void CheckLegacy(CustomUIWindow window, bool withSales)
    {
        string sale = ModHelper.GetLocalized(LedgerProbe.Mod, "f28.sale");
        string legacy = ModHelper.GetLocalized(LedgerProbe.Mod, "f28.legacy", sale, 2);
        string unknown = ModHelper.GetLocalized(LedgerProbe.Mod, "f28.unknown");
        LedgerProbe.Need(LedgerProbe.RowCount(window, legacy, 137, unknown) == 1 &&
            LedgerProbe.Text(window, ModHelper.GetLocalized(LedgerProbe.Mod, "f28.total", 137L + (withSales ? first + second : 0))) &&
            LedgerProbe.Text(window, ModHelper.GetLocalized(LedgerProbe.Mod, "f28.margin", 37L, withSales ? 3 : 1)),
            "Legacy row, cash total or unknown-cost count changed during migration");
        if (withSales)
            LedgerProbe.Need(LedgerProbe.RowCount(window, sale, first, unknown) == 1 &&
                LedgerProbe.RowCount(window, sale, second, unknown) == 1 &&
                LedgerProbe.Rows(window).Count(row => row[0] == sale) == 2,
                "New same-kind transactions were not displayed as two distinct rows");
    }

    private void CheckSavedAndVisible()
    {
        var history = LedgerProbe.History();
        var old = history.legacy.SingleOrDefault(row => row.day == day && row.kind == 2);
        var total = history.entries.SingleOrDefault(row => row.day == day && row.kind == 2);
        LedgerProbe.Need(old != null && old.income == 137 && old.expense == 0 && old.knownMargin == 37 &&
            old.events == 2 && old.unknownCost == 1 && old.knownCostCount == 1 &&
            total != null && total.income == 137L + first + second && total.events == 4 &&
            total.knownMargin == 37 && total.knownCostCount == 1 && total.unknownCost == 3 &&
            history.transactions.Count(row => row.day == day && row.kind == 2 && row.change == first) == 1 &&
            history.transactions.Count(row => row.day == day && row.kind == 2 && row.change == second) == 1,
            "Migrated totals or separate transactions were lost or counted twice");
        RoutingScenario.RunFeatureAction("f28.open");
        var window = LedgerProbe.Window();
        LedgerProbe.Need(LedgerProbe.Day(window) == day, "Migrated ledger opened a different day");
        CheckLegacy(window, true); window.Close();
    }
}
