using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// These scenarios use new isolated native runs; the fixed scene inventory is never shown again.
public sealed class LedgerMarginScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 110;
    private GameItem known, unknown;
    private QaLoadSlot load;
    private int step, cost, knownSale, unknownSale, balance, day, closedFrame;
    private string run;
    private int slot;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static GameItem Put(string id, GameInventory destination, bool owned)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing fixture " + id); GeneralHelper.SetItemOwned(item, owned);
        var slot = destination.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == destination && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == destination, "Cannot place " + id);
        return item;
    }
    private void Verify()
    {
        var history = LedgerProbe.History();
        var sale = history.entries.SingleOrDefault(r => r.day == day && r.kind == 2);
        Need(sale != null && sale.events == 2 && sale.income == knownSale + unknownSale && sale.expense == 0 &&
            sale.knownCostCount == 1 && sale.unknownCost == 1 && sale.knownMargin == knownSale - cost,
            "Known/unknown sale costs or margin differ from native transactions");
        var transactions = history.transactions.Where(r => r.day == day && r.kind == 2).ToArray();
        Need(history.schemaVersion == 2 && transactions.Length == 2 &&
            transactions.Count(r => r.change == knownSale) == (knownSale == unknownSale ? 2 : 1) &&
            transactions.Count(r => r.change == unknownSale) == (knownSale == unknownSale ? 2 : 1),
            "The two sale transactions were not retained separately");
        RoutingScenario.RunFeatureAction("f28.open");
        var book = CustomUIManager.Instance.GetWindow(Mod + ".ledger");
        Need(book?.IsAlive == true && book.Get("kind")?.dropdown != null, "Ledger UI unavailable");
        int index = book.Get("kind").dropdown.options.FindIndex(o => o.text == ModHelper.GetLocalized(Mod, "f28.sale"));
        Need(index > 0, "Sale filter unavailable"); book.Get("kind").SetIndex(index, true);
        book = CustomUIManager.Instance.GetWindow(Mod + ".ledger");
        string saleLabel = ModHelper.GetLocalized(Mod, "f28.sale");
        Need(LedgerProbe.RowCount(book, saleLabel, knownSale, LedgerProbe.Signed(knownSale - cost)) == 1 &&
            LedgerProbe.RowCount(book, saleLabel, unknownSale, ModHelper.GetLocalized(Mod, "f28.unknown")) == 1 &&
            LedgerProbe.Rows(book).Count(row => row[0] == saleLabel) == 2,
            "The sale list omitted a transaction, cash change, known profit or unknown cost");
        var texts = book.root.GetComponentsInChildren<TMP_Text>(true).Where(t => t.gameObject.activeInHierarchy).Select(t => t.text).ToArray();
        Need(texts.Contains(ModHelper.GetLocalized(Mod, "f28.margin", (long)knownSale - cost, 1)) &&
            texts.Contains(ModHelper.GetLocalized(Mod, "f28.total", (long)knownSale + unknownSale)) &&
            texts.Contains(ModHelper.GetLocalized(Mod, "f28.cash", balance)), "Visible ledger omitted margin, unknown cost, sale total or cash");
        book.Close();
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Ledger margin QA timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var p = PlayerStore.Instance; var e = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true || NegociationUIManager.Instance == null) return false;
            TradeScenario.SetOptions(false, "f28.enabled"); p.playerCash = 200000;
            TradeScenario.SetOptions(true, "f28.enabled");
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
            p.isClientArrived = true; known = Put("wine_yeast", e.backInvinvElement, false);
            cost = checked((int)known.GetNegociatedValue()); int before = p.playerCash; p.BuyItem(known);
            Need(cost > 0 && p.playerCash == before - cost && GeneralHelper.IsItemOwned(known) &&
                known.IsTag("BOUGHT_PRICE_TAG") && known.GetTagReadonly("BOUGHT_PRICE_TAG").GetInt() == cost,
                "Native purchase did not establish an exact known cost");
            unknown = Put("wine_yeast_red", e.invElement, true); Need(!unknown.IsTag("BOUGHT_PRICE_TAG"), "Unknown-cost fixture has a purchase price");
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateSpacerClientFood());
            before = p.playerCash; p.SellItem(known); knownSale = p.playerCash - before;
            before = p.playerCash; p.SellItem(unknown); unknownSale = p.playerCash - before;
            Need(knownSale > 0 && unknownSale > 0 && !GeneralHelper.IsItemOwned(known) && !GeneralHelper.IsItemOwned(unknown),
                "Native sales did not complete");
            balance = p.playerCash; day = StoreStation.GetDayCounter(); Verify();
            if (NegociationUIManager.Instance.IsOpen) NegociationUIManager.Instance.CloseUI();
            if (BargainUIManager.Instance?.IsOpen == true) BargainUIManager.Instance.CloseUI();
            foreach (var paper in Resources.FindObjectsOfTypeAll<StoreUI>())
            {
                if (paper == null || !paper.isPaperUI || !paper.gameObject.scene.IsValid() || !paper.gameObject.activeInHierarchy) continue;
                string id = paper.identifier;
                StoreUIManager.Instance.CloseUI(id);
                Need(paper == null || !paper.gameObject.activeInHierarchy, "Native paper panel remained open: " + id);
            }
            Need(!NegociationUIManager.Instance.IsOpen && BargainUIManager.Instance?.IsOpen != true,
                "Native negotiation or bargain panel remained open");
            closedFrame = Time.frameCount; step++; return false;
        }
        if (step == 2)
        {
            if (Time.frameCount <= closedFrame) return false;
            Need(!NegociationUIManager.Instance.IsOpen && BargainUIManager.Instance?.IsOpen != true,
                "Native negotiation or bargain panel reopened before the save");
            slot = p.saveSlotId; run = p.runID; p.SaveGame(); StoreUIManager.Instance.escapeUIManager.OnMainMenu();
            load = new QaLoadSlot(slot, Guid.Parse(run)); step++; return false;
        }
        if (!load.Tick()) return false;
        Need(PlayerStore.Instance.playerCash == balance && StoreStation.GetDayCounter() == day, "Saved cash or day changed on reload");
        Verify(); result = "PASS F28 separate real sales, cash changes, known profit, unknown cost and same-slot reload"; return true;
    }
}

public sealed class BasketBudgetScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 6);
    private readonly int[] quotes = new int[3];
    private readonly System.Collections.Generic.HashSet<GameItem> seen = new System.Collections.Generic.HashSet<GameItem>();
    private GameItem[] items, chosen;
    private int step, cash, price, limit, pages;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".basket");
    private static object Field(object obj, string key) => obj?.GetType().GetField(key, Flags)?.GetValue(obj);
    private static void Move(GameItem item, GameInventory destination)
    {
        var marker = destination.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == destination && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == destination,
            "Cannot place " + item.identifier);
    }
    private static int Quote(CustomUIWindow window, GameItem item)
    {
        var hit = QaItemUi.Hit(window.root, item);
        var entry = Field(hit, "entry");
        string badge = Field(entry, "Badge") as string;
        Need(entry != null, "Basket item card missing: " + QaItemUi.Describe(window.root, item));
        Need(int.TryParse(badge, out int quote) && quote > 0,
            "A real customer quote is unavailable: item=" + item.identifier + " badge=" + badge +
            " accepted=" + PlayerStore.Instance.currentClientInstance?.GetClientBlueprint()?.IsClientBuyingThisItem(item) +
            " native=" + item.GetNegociatedValue());
        Need(quote == item.GetNegociatedValue(), "Displayed basket quote differs from the native counter quote");
        return quote;
    }
    private GameItem[][] ResultGrids(CustomUIWindow window) => window.root.GetComponentsInChildren<Transform>(true)
        .Where(t => t.gameObject.activeInHierarchy && t.name == "ShopAssist_CounterPreview")
        .Select(t => t.GetComponentsInChildren<MonoBehaviour>(true)
            .Where(hit => hit != null && hit.gameObject.activeInHierarchy && hit.gameObject.name == "item_cell" &&
                hit.GetType().FullName == "ShopAssist.ItemGrid+Hit")
            .Select(hit => Field(Field(hit, "entry"), "Item") as GameItem).Where(item => item != null).Distinct().ToArray())
        .Where(g => g.Length > 0).ToArray();
    private GameItem[] Compare(bool shouldAllowMove)
    {
        RoutingScenario.RunFeatureAction("f34.open"); var w = Window();
        Need(w?.Get("limit") != null && w.Get("compare")?.button != null, "Basket controls unavailable");
        w.Get("limit").SetString(limit.ToString(), true); w.Get("compare").button.onClick.Invoke(); w = Window();
        var grids = ResultGrids(w);
        string[] labels = w.root.GetComponentsInChildren<TMP_Text>(true).Where(t => t.gameObject.activeInHierarchy).Select(t => t.text).ToArray();
        Need(grids.Length > 0 && grids.All(g => g.Length == 2 && g.All(items.Contains) && g.Sum(_ => price) == limit) &&
            labels.Contains(ModHelper.GetLocalized(Mod, "f34.summary", 2, limit, 0)),
            "Budget comparison did not show only two-item combinations within the entered limit: price=" + price +
            " grids=" + string.Join(";", grids.Select(g => string.Join(",", g.Select(i => i.identifier + ":" + Array.IndexOf(items, i))))) +
            " labels=" + string.Join(" | ", labels));
        var place = RoutingScenario.Button(w, "f34.place");
        Need((place != null) == shouldAllowMove, "Move permission did not control placement");
        Need(items.All(i => i.parentInventory == EmporiumEntry.Instance.invElement) && PlayerStore.Instance.playerCash == cash,
            "Comparing combinations changed inventory or cash");
        if (shouldAllowMove) place.onClick.Invoke(); else w.Close();
        return grids[0];
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Basket budget QA timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true || NegociationUIManager.Instance == null) return false;
            TradeScenario.SetOptions(true, "f34.enabled"); TradeScenario.SetOptions(false, "f34.move");
            var buyer = StoreClientList.CreateSpacerClientFood(); buyer.SetBudget(9000); buyer.isIntroduced = true;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer); p.isClientArrived = true;
            items = Enumerable.Range(0, 3).Select(i =>
            {
                var item = DirectoryMaster.Item("cup_noodle"); Need(item != null, "Missing food fixture");
                GeneralHelper.SetItemOwned(item); GeneralHelper.SetCustomName(item, "QA basket " + token + " " + i);
                Move(item, e.backInvinvElement); return item;
            }).ToArray();
            NegociationUIManager.Instance.OpenUI(); step++; return false;
        }
        if (step == 2)
        {
            if (!NegociationUIManager.Instance.IsOpen) return false;
            RoutingScenario.RunFeatureAction("f34.open"); var w = Window();
            Need(w?.root != null && p.currentClientInstance.GetClientBlueprint().GetBudget() == 9000 &&
                w.root.GetComponentsInChildren<TMP_Text>(true).Any(t => t.gameObject.activeInHierarchy &&
                    t.text == ModHelper.GetLocalized(Mod, "f34.budget", 9000)), "Native customer budget was not shown");
            cash = p.playerCash; step++; return false;
        }
        if (step == 3)
        {
            var w = Window();
            var hits = w.root.GetComponentsInChildren<MonoBehaviour>(true)
                .Where(hit => hit != null && hit.gameObject.activeInHierarchy && hit.gameObject.name == "item_cell" &&
                    hit.GetType().FullName == "ShopAssist.ItemGrid+Hit")
                .GroupBy(hit => Field(Field(hit, "entry"), "Item") as GameItem).Select(group => group.First()).ToArray();
            Need(hits.Length > 0 && ++pages <= 512, "Basket pagination did not reach its end");
            foreach (var hit in hits)
            {
                var entry = Field(hit, "entry"); var item = Field(entry, "Item") as GameItem;
                Need(item != null && seen.Add(item), "Basket pagination repeated or omitted an item");
                int index = Array.IndexOf(items, item);
                if (index >= 0)
                {
                    quotes[index] = Quote(w, item);
                    Need(Field(entry, "Selected") is bool selected && selected, "Fixture was not selected");
                    if (index == 0)
                    {
                        QaItemUi.Click(hit); Need(Field(entry, "Selected") is bool deselected && !deselected, "Fixture could not be deselected");
                        QaItemUi.Click(hit); Need(Field(entry, "Selected") is bool reselected && reselected, "Fixture could not be selected again");
                    }
                }
                else if (Field(entry, "Enabled") is bool enabled && enabled)
                {
                    Need(Field(entry, "Selected") is bool selected && selected, "Other quoted item was not initially selected");
                    QaItemUi.Click(hit);
                    Need(Field(entry, "Selected") is bool deselected && !deselected, "Other quoted item could not be excluded");
                }
            }
            var next = w.Get("next")?.button;
            if (next != null && next.interactable) { next.onClick.Invoke(); return false; }
            Need(quotes.All(q => q > 0 && q == quotes[0]) && quotes[0] * 2 < 9000 && items.All(i => i.unitCount == 1),
                "Native quotes are not three equal positive single-item offers");
            Need(p.playerCash == cash, "Selecting basket items changed cash");
            price = quotes[0]; limit = 2 * price; w.Close();
            foreach (var item in items) Move(item, e.invElement);
            NegociationUIManager.Instance.RefreshUI();
            Compare(false); TradeScenario.SetOptions(true, "f34.move"); chosen = Compare(true); step++; return false;
        }
        if (chosen.Any(i => i.parentInventory != e.backInvinvElement)) return false;
        Need(items.Except(chosen).Single().parentInventory == e.invElement && p.playerCash == cash && items.All(GeneralHelper.IsItemOwned),
            "Staging the selected pair moved the third item or settled a sale");
        result = "PASS F34 three real quotes, two-item budget comparison, move permission and exact staged pair without sale"; return true;
    }
}

public sealed class LedgerDaysScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 155;
    private int step, day, firstSale, secondSale;
    private float after;
    internal static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    internal static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".ledger");
    private static int Sell(string id)
    {
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing fixture " + id); GeneralHelper.SetItemOwned(item);
        var marker = e.invElement.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == e.invElement && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == e.invElement, "Cannot place day sale fixture");
        p.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateSpacerClientFood()); p.isClientArrived = true;
        int before = p.playerCash; p.SellItem(item); int amount = p.playerCash - before;
        Need(amount > 0 && !GeneralHelper.IsItemOwned(item), "Native day sale failed"); return amount;
    }
    private static void ClearBlockers()
    {
        var p = PlayerStore.Instance; var current = p.currentClientInstance?.GetClientBlueprint();
        if (current != null && !current.dismissable) p.DismissCurrentClient(callNextWhenPossible: false);
        p.storeClientManager?.clientStack?.RemoveAll(c => c != null && !c.dismissable);
    }
    internal static void VerifyDays(int day, int first, int second, int balance)
    {
        var history = LedgerProbe.History();
        Need(history.transactions.Count(row => row.day == day && row.kind == 2 && row.change == first) == 1 &&
            history.transactions.Count(row => row.day == day + 1 && row.kind == 2 && row.change == second) == 1,
            "Saved transactions do not retain the two daily cash changes");
        RoutingScenario.RunFeatureAction("f28.open"); var book = Window();
        Need(book?.IsAlive == true, "Daily ledger UI unavailable");
        Need(Day(book) == day + 1 && SaleRow(book, second) && !SaleRow(book, first) &&
            Text(book, ModHelper.GetLocalized(Mod, "f28.cash", balance)), "Current-day sale or cash mismatch");

        var input = book.Get("day_input");
        Need(input?.input != null && book.Get("day_go")?.button != null, "Ledger date input or jump action unavailable");
        input.SetString("0", true); input.input.onEndEdit.Invoke("0"); book.Get("day_go").button.onClick.Invoke();
        var feedback = book.Get("day_feedback");
        Need(feedback?.root?.activeInHierarchy == true && feedback.GetText() == ModHelper.GetLocalized(Mod, "f28.invalid_day", day + 1) &&
            SaleRow(book, second) && !SaleRow(book, first), "Invalid date changed the viewed ledger or gave no feedback");

        book = Jump(book, day);
        Need(Day(book) == day && SaleRow(book, first) && !SaleRow(book, second),
            "Date input did not jump to the first day's actual sale");
        book.Close(); RoutingScenario.RunFeatureAction("f28.open"); book = Window();
        Need(Day(book) == day && SaleRow(book, first) && !SaleRow(book, second),
            "Reopening the ledger in the same session did not retain the selected day");

        book = Navigate(book, "›");
        Need(Day(book) == day + 1 && SaleRow(book, second) && !SaleRow(book, first),
            "Next-day navigation did not show the second day's actual sale");
        book = Navigate(book, "‹");
        Need(Day(book) == day && SaleRow(book, first) && !SaleRow(book, second),
            "Previous-day navigation did not return to the first day's actual sale");
        book = Jump(book, day + 1);
        Need(Day(book) == day + 1 && SaleRow(book, second) && !SaleRow(book, first),
            "Date input did not jump back to the current day's sale");
        book = Jump(book, day);
        Need(Day(book) == day && SaleRow(book, first), "Could not retain the historical day before closing");
        book.Close();
    }

    private static int Day(CustomUIWindow book)
    {
        var input = book?.Get("day_input");
        Need(input?.input != null && int.TryParse(input.GetString(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out _), "Ledger day input is missing or invalid");
        return int.Parse(input.GetString(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static CustomUIWindow Jump(CustomUIWindow book, int day)
    {
        var input = book.Get("day_input"); var go = book.Get("day_go")?.button;
        Need(input?.input != null && go != null && go.interactable, "Ledger date jump is unavailable");
        string value = day.ToString(System.Globalization.CultureInfo.InvariantCulture);
        input.SetString(value, true); input.input.onEndEdit.Invoke(value); go.onClick.Invoke();
        var updated = Window(); Need(updated?.IsAlive == true, "Ledger closed while changing date"); return updated;
    }

    private static CustomUIWindow Navigate(CustomUIWindow book, string label)
    {
        var button = book.root.GetComponentsInChildren<Button>(true).FirstOrDefault(value => value.gameObject.activeInHierarchy &&
            value.GetComponentInChildren<TMP_Text>(true)?.text == label);
        Need(button != null && button.interactable, "Ledger day navigation unavailable: " + label);
        button.onClick.Invoke();
        var updated = Window(); Need(updated?.IsAlive == true, "Ledger closed while navigating dates"); return updated;
    }
    private static bool Text(CustomUIWindow book, string value) => book.root.GetComponentsInChildren<TMP_Text>(true)
        .Any(t => t.gameObject.activeInHierarchy && t.text == value);
    private static bool SaleRow(CustomUIWindow book, int amount) =>
        LedgerProbe.RowCount(book, ModHelper.GetLocalized(Mod, "f28.sale"), amount, ModHelper.GetLocalized(Mod, "f28.unknown")) == 1;
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Daily ledger QA timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var p = PlayerStore.Instance;
        switch (step)
        {
            case 1:
                if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || StoreShutterButton.Instance == null) return false;
                TradeScenario.SetOptions(true, "f28.enabled"); day = StoreStation.GetDayCounter(); firstSale = Sell("wine_yeast");
                p.currentClientInstance = null; p.isClientArrived = false; EmporiumOption.Instance.autoCallNext = false;
                if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
                step++; return false;
            case 2:
                if (!p.CanStartDay() || p.lockShutterButtonFlag > 0) return false;
                StoreShutterButton.Instance.OnClick(); Need(p.storeState == PlayerStore.StoreState.OPEN, "Native shutter failed to open");
                step++; return false;
            case 3:
                if (!p.CanEndDay()) ClearBlockers();
                if (p.lockShutterButtonFlag > 0 || !p.CanEndDay()) return false;
                StoreShutterButton.Instance.OnClick(); step++; return false;
            case 4:
                if (p.storeState == PlayerStore.StoreState.OPEN)
                { if (StoreUIManager.Instance.closeShutterConfirmationPanel?.activeInHierarchy == true) StoreShutterButton.Instance.EndAndClose(); return false; }
                Need(p.storeState == PlayerStore.StoreState.CLOSED, "Native shutter failed to close");
                after = Time.realtimeSinceStartup + 3; step++; return false;
            case 5:
                if (Time.realtimeSinceStartup < after) return false;
                var bed = UnityEngine.Object.FindObjectOfType<StoreBedroll>(); if (bed == null || EndOfDayUIManager.Instance == null) return false;
                bed.OnClick(); Need(EndOfDayUIManager.Instance.IsOpen, "Native sleep screen unavailable");
                EndOfDayUIManager.Instance.OnEndDayButtonClick(); step++; return false;
            case 6:
                if (StoreStation.GetDayCounter() != day + 1 || p.storeState != PlayerStore.StoreState.MORNING) return false;
                secondSale = Sell("wine_superyeast"); Need(firstSale != secondSale, "Daily fixture requires distinct sale amounts");
                VerifyDays(day, firstSale, secondSale, p.playerCash);
                p.SaveGame(); File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-ledger-days-run.txt"),
                    string.Join("|", p.runID, p.saveSlotId, day, firstSale, secondSale, p.playerCash));
                result = "PASS F28 day input, validation, daily navigation and same-session day memory; saved for cold reload"; return true;
            default: throw new InvalidOperationException("Unknown daily ledger QA step " + step);
        }
    }
}

public sealed class LedgerDaysReloadScenario
{
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private QaLoadSlot load;
    private string[] context;
    public bool Tick(out string result)
    {
        result = null; LedgerDaysScenario.Need(Time.realtimeSinceStartup < deadline, "Daily ledger cold reload timed out");
        if (load == null)
        {
            string path = Path.Combine(Application.persistentDataPath, "qa-ledger-days-run.txt");
            LedgerDaysScenario.Need(File.Exists(path), "Run LedgerDaysScenario before the cold reload");
            context = File.ReadAllText(path).Split('|'); LedgerDaysScenario.Need(context.Length == 6, "Invalid daily ledger save context");
            load = new QaLoadSlot(int.Parse(context[1]), Guid.Parse(context[0]));
        }
        if (!load.Tick()) return false;
        int day = int.Parse(context[2]), first = int.Parse(context[3]), second = int.Parse(context[4]), balance = int.Parse(context[5]);
        LedgerDaysScenario.Need(StoreStation.GetDayCounter() == day + 1 && PlayerStore.Instance.playerCash == balance,
            "Cold reload restored a different day or balance");
        LedgerDaysScenario.VerifyDays(day, first, second, balance);
        result = "PASS F28 cold reload restores both daily transactions and cash, with day input and session navigation"; return true;
    }
}

public sealed class PackingPriorityScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120;
    private GameItem bag, keep, ordinary, priority, left, right;
    private GameGridInventory inside;
    private int step, reserve, attempts;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".packing");
    private static GameItem New(string id, GameInventory destination)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing fixture " + id); GeneralHelper.SetItemOwned(item);
        var marker = destination.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == destination && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == destination, "Cannot place " + id);
        return item;
    }
    private static int Cells(GridShape shape)
    {
        int result = 0;
        for (int y = shape.minY; y <= shape.maxY; y++) for (int x = shape.minX; x <= shape.maxX; x++)
            if (shape.Intersects(x, y)) result++;
        return result;
    }
    private static int Free(GameGridInventory grid)
    {
        int count = 0;
        for (int y = 0; y < grid.inventoryShape.height; y++) for (int x = 0; x < grid.inventoryShape.width; x++)
            if (grid.inventoryShape.Get(x, y) == 0 && grid.childItems.All(i => !i.modifiedShape.Intersects(x, y))) count++;
        return count;
    }
    private static Toggle PriorityToggle()
    {
        string label = ModHelper.GetLocalized(Mod, "f36.priority_edit");
        return Window().root.GetComponentsInChildren<Toggle>(true).FirstOrDefault(t => t.gameObject.activeInHierarchy &&
            t.transform.parent.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == label));
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Packing priority QA timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true) return false;
            TradeScenario.SetOptions(true, "f36.enabled");
            bag = New("backpack_medium", e.invElement); inside = bag.contentWindow?.childElement as GameGridInventory;
            Need(inside != null, "Native backpack grid unavailable"); keep = New("wine_yeast_red", inside);
            var sample = DirectoryMaster.Item("wine_berry"); Need(sample != null, "Missing ground item fixture");
            int cells = Cells(sample.modifiedShape); Need(cells > 0, "Ground fixture has no occupied cells");
            for (int n = 0; n < 100 && Free(inside) > 32 + cells; n++) New("wine_yeast", inside);
            reserve = Free(inside) - cells;
            Need(reserve >= 0 && reserve <= 32 && inside.TryFindOneValidInventorySlot(sample) != null, "Backpack cannot fit the priority fixture");
            sample.Destroy();
            var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
            var settings = CustomUIManager.Instance.GetWindow("game_options");
            settings.Get(Mod + ":f36.space").SetFloat(reserve, true); settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
            Need(((ConfigEntry<int>)ModConfig.Find(Mod).Get("f36.space")).Value == reserve, "Free-space limit was not applied");
            e.selectedBackpack = bag; p.visitLeftTonight = 2;
            var site = AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND); Need(site != null && site.IsAvailable(), "Native outing unavailable");
            MapUIManager.Instance.Travel(site); step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !e.afterhourWindow.IsVisible() ||
                e.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            foreach (var item in e.afterhourInventory.childItems.ToArray()) item.Destroy();
            left = New("wine_yeast", e.afterhourPocketSlotInvLeft); right = New("wine_yeast", e.afterhourPocketSlotInvRight);
            ordinary = New("wine_berry", e.afterhourInventory); priority = New("wine_berry", e.afterhourInventory);
            GeneralHelper.SetCustomName(ordinary, "QA ordinary ground item"); GeneralHelper.SetCustomName(priority, "QA priority ground item");
            attempts = p.scavengingAttempts; step++; return false;
        }
        if (step == 3)
        {
            RoutingScenario.RunFeatureAction("f36.open"); var window = Window();
            Need(window?.Get("preview")?.button != null && QaItemUi.Hit(window.root, ordinary) != null && QaItemUi.Hit(window.root, priority) != null,
                "Packing grid omitted real ground items");
            var toggle = PriorityToggle(); Need(toggle != null && !toggle.isOn, "Priority selection toggle unavailable");
            toggle.isOn = true; QaItemUi.Click(Window(), priority);
            Need(QaItemUi.Selected(Window(), priority) && !QaItemUi.Selected(Window(), ordinary), "Priority selection did not mark only the chosen item");
            toggle = PriorityToggle(); Need(toggle != null && toggle.isOn, "Priority toggle was lost"); toggle.isOn = false;
            Window().Get("preview").button.onClick.Invoke();
            Need(QaItemUi.Selected(Window(), priority) && !QaItemUi.Selected(Window(), ordinary) &&
                priority.parentInventory == e.afterhourInventory && ordinary.parentInventory == e.afterhourInventory,
                "Preview did not reserve space for the priority item without moving either object");
            Window().Get("apply").button.onClick.Invoke();
            Need(priority.parentInventory == inside && ordinary.parentInventory == e.afterhourInventory && Free(inside) >= reserve &&
                keep.parentInventory == inside && left.parentInventory == e.afterhourPocketSlotInvLeft && right.parentInventory == e.afterhourPocketSlotInvRight &&
                p.scavengingAttempts == attempts, "Packing ignored priority, reserved space, occupied pockets or existing equipment");
            result = "PASS F36 explicit item priority and reserved backpack cells with occupied native pockets"; return true;
        }
        throw new InvalidOperationException("Unknown packing priority QA step " + step);
    }
}

public sealed class CustomerCounterDropScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90;
    private GameItem goods;
    private int step, cash, frame;
    private float quoteDeadline;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static object Field(object value, string name) => value?.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
    private static void CheckCounterFallback(MonoBehaviour hit, GameItem item, GameGridInventory target)
    {
        var entry = Field(hit, "entry"); var view = Field(hit, "view");
        var allow = Field(Field(view, "Options"), "CanDropTarget") as Delegate;
        var slot = target.TryFindOneValidInventorySlot(item);
        Need(allow != null && entry != null && slot != null && slot.targetItem == null && slot.itemGridShape != null,
            "The live counter drop predicate or an empty native slot is unavailable");
        bool Allowed() => (bool)allow.DynamicInvoke(entry, target, null, slot.itemGridShape);
        Need((bool)Field(entry, "Draggable") && !Allowed(),
            "The counter's customer callbacks should reject the assisted drop while leaving the source draggable elsewhere");
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Customer counter drag timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true || NegociationUIManager.Instance == null) return false;
            TradeScenario.SetOptions(true, "f11.enabled"); TradeScenario.SetOptions(false, "f34.enabled");
            goods = DirectoryMaster.Item("cup_noodle"); Need(goods != null, "Missing customer food fixture");
            GeneralHelper.SetItemOwned(goods); GeneralHelper.SetCustomName(goods, "QA demand counter drag");
            var marker = e.invElement.TryFindOneValidInventorySlot(goods);
            Need(marker != null && marker.inventory == e.invElement && marker.targetItem == null &&
                marker.TryAcceptOnce(goods.unitCount) == goods.unitCount && goods.parentInventory == e.invElement, "Cannot place demand fixture");
            var buyer = StoreClientList.CreateSpacerClientFood(); buyer.SetBudget(9000); buyer.isIntroduced = true;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer); p.isClientArrived = true;
            NegociationUIManager.Instance.OpenUI(); cash = p.playerCash; step++; return false;
        }
        if (step == 2)
        {
            if (!NegociationUIManager.Instance.IsOpen || !e.backInvWindow.IsVisible()) return false;
            RoutingScenario.RunFeatureAction("f11.open");
            var list = CustomUIManager.Instance.GetWindow(Mod + ".customer_inventory");
            var demandButton = list?.Get("demand")?.button;
            Need(list?.IsAlive == true && demandButton != null && demandButton.interactable, "Demand mode is unavailable");
            demandButton.onClick.Invoke();
            var demandHit = QaItemUi.Hit(list.root, goods);
            Need(demandHit != null && !string.IsNullOrEmpty(Field(Field(demandHit, "entry"), "QuoteLabel") as string),
                "Demand grid does not contain the actual owned item and its customer quote");
            // Keep the card visible while exposing the native negotiation grid for a real raycast.
            var canvas = list.Rect.GetComponentInParent<Canvas>();
            var parent = list.Rect.parent as RectTransform;
            if (parent != null && RectTransformUtility.ScreenPointToWorldPointInRectangle(parent,
                new Vector2(Screen.width * .2f, Screen.height * .7f), canvas?.worldCamera, out Vector3 world)) list.Rect.position = world;
            Canvas.ForceUpdateCanvases(); frame = Time.frameCount + 2; step++; return false;
        }
        if (step == 3)
        {
            if (Time.frameCount < frame) return false;
            var view = CustomUIManager.Instance.GetWindow(Mod + ".customer_inventory");
            var hit = view == null ? null : QaItemUi.Hit(view.root, goods); Need(hit != null, "Demand card disappeared before drag");
            CheckCounterFallback(hit, goods, e.backInvinvElement);
            var drag = typeof(ModuleGridScenario).GetMethod("DragToNative", BindingFlags.Static | BindingFlags.NonPublic);
            Need(drag != null, "Native event/raycast drag helper unavailable");
            bool reached = (bool)drag.Invoke(null, new object[] { hit, goods, e.backInvinvElement });
            Need(reached, "No unobstructed valid position in the native negotiation counter could be reached");
            Need(goods.parentInventory == e.invElement && GeneralHelper.IsItemOwned(goods) && p.playerCash == cash,
                "An assisted drop entered the unsafe customer callback path");
            var native = e.backInvinvElement.TryFindOneValidInventorySlot(goods);
            Need(native != null && native.inventory == e.backInvinvElement && native.targetItem == null &&
                native.TryAcceptOnce(goods.unitCount) == goods.unitCount && goods.parentInventory == e.backInvinvElement,
                "The native counter did not accept the item after the assisted drop was rejected");
            Need(GeneralHelper.IsItemOwned(goods) && p.playerCash == cash && p.currentNegociatedItem == goods,
                "Dragging to counter sold the item or missed the native counter callback");
            Need(e.backInvinvElement.childItems.Count(i => GeneralHelper.IsItemOwned(i) &&
                i.GetDisplayName(false) == goods.GetDisplayName(false)) == 1, "The quoted native item name is ambiguous in the QA counter");
            view.Close();
            quoteDeadline = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        var negotiation = NegociationUIManager.Instance;
        long price = goods.GetNegociatedValue();
        // Both native row layouts use GetDisplayName(false), even for a custom-named item.
        string expectedName = goods.GetDisplayName(false);
        string expectedQuote = price.ToString() + GeneralHelper.CreditIcon(false);
        var rows = negotiation.uiObject.GetComponentsInChildren<TabletListRow>(true)
            .Where(row => row.gameObject.activeInHierarchy).ToArray();
        bool quoted = price > 0 && rows.Any(row =>
        {
            var labels = row.GetComponentsInChildren<TMP_Text>(true);
            return labels.Any(t => t.name == "Name" && t.isActiveAndEnabled && t.text == expectedName &&
                t.textInfo != null && t.textInfo.characterCount > 0) &&
                labels.Any(t => t.name == "Right" && t.isActiveAndEnabled && t.text == expectedQuote &&
                    t.textInfo != null && t.textInfo.characterCount > 0);
        });
        if (!quoted)
        {
            if (Time.realtimeSinceStartup >= quoteDeadline)
            {
                string visible = string.Join(" | ", rows.Select(row => string.Join("; ", row.GetComponentsInChildren<TMP_Text>(true)
                    .Where(t => t.isActiveAndEnabled).Select(t => t.name + "=" + t.text))));
                throw new InvalidOperationException("Native negotiation did not render the dropped item and quote in one row: expected " +
                    expectedName + " / " + expectedQuote + "; visible=" + visible);
            }
            return false;
        }
        Need(goods.parentInventory == e.backInvinvElement && GeneralHelper.IsItemOwned(goods) && p.playerCash == cash &&
            p.currentNegociatedItem == goods,
            "Showing the native quote changed ownership or cash");
        result = "PASS F11 rejects counter callbacks, preserves the source and leaves native counter placement and visible quote available"; return true;
    }
}
