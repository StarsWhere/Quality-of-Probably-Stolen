using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// These scenarios use new isolated native runs; the fixed scene inventory is never shown again.
public sealed class LabelOverwriteScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private readonly string label = "QA batch " + Guid.NewGuid().ToString("N").Substring(0, 6);
    private GameItem tool, sample, fresh, named, different;
    private int step;

    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".label");
    private static GameItem Put(string id)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing fixture " + id);
        GeneralHelper.SetItemOwned(item);
        var root = EmporiumEntry.Instance.invElement; var slot = root.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == root && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == root, "Cannot place " + id);
        return item;
    }
    private static bool Idle()
    {
        var assembly = ModLoader.Mods.FirstOrDefault(m => m.Manifest?.ID == Mod)?.Instance?.GetType().Assembly;
        var type = assembly?.GetType("ShopAssist.BatchLabeler");
        var active = type?.GetField("active", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        var phase = type?.GetField("phase", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(active);
        return phase is int value && value == 0;
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Label overwrite QA timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || ItemSelectHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f22.enabled"); TradeScenario.SetOptions(false, "f22.overwrite");
            tool = Put("labeler_blue"); sample = Put("wine_yeast"); fresh = Put("wine_yeast");
            named = Put("wine_yeast"); different = Put("wine_yeast_red");
            GeneralHelper.SetCustomName(named, "QA existing label");
            ItemSelectHandler.current.TryEquipItem(tool); tool.Target(sample);
            var ui = LabelerBlueUIManager.Instance;
            Need(ui.panel.activeSelf, "Native blue labeler did not open");
            ui.inputField.SetTextWithoutNotify(label); ui.OnEditEnd(label); ui.CloseUI();
            RoutingScenario.RunFeatureAction("f22.open"); var window = Window();
            Need(window?.Get("apply")?.button != null && QaItemUi.Hit(window.root, fresh) != null &&
                QaItemUi.Hit(window.root, named) == null && QaItemUi.Hit(window.root, different) == null,
                "Overwrite disabled did not limit the grid to unlabeled same-type items");
            QaItemUi.Click(window, fresh); window.Get("apply").button.onClick.Invoke(); step++; return false;
        }
        if (step == 2)
        {
            if (!Idle()) return false;
            Need(GeneralHelper.GetCustomName(fresh) == label && GeneralHelper.GetCustomName(named) == "QA existing label" &&
                fresh.parentInventory == EmporiumEntry.Instance.invElement && named.parentInventory == fresh.parentInventory,
                "First batch changed an existing label or failed to label the selected instance");
            TradeScenario.SetOptions(true, "f22.overwrite"); RoutingScenario.RunFeatureAction("f22.open");
            var window = Window();
            Need(window?.Get("apply")?.button != null && QaItemUi.Hit(window.root, named) != null &&
                QaItemUi.Hit(window.root, different) == null, "Overwrite enabled did not expose the existing same-type label");
            QaItemUi.Click(window, named); window.Get("apply").button.onClick.Invoke(); step++; return false;
        }
        if (!Idle()) return false;
        Need(GeneralHelper.GetCustomName(named) == label && GeneralHelper.GetCustomName(fresh) == label &&
            named.parentInventory == EmporiumEntry.Instance.invElement && different.parentInventory == named.parentInventory,
            "Overwrite did not change exactly the selected existing label");
        result = "PASS F22 blue label overwrite off/on through native sample and selected item cards"; return true;
    }
}

public sealed class DisplaySourceScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private readonly string name = "QA display source " + Guid.NewGuid().ToString("N").Substring(0, 6);
    private GameItem bag, outside, removed;
    private GameItem[] goods;
    private GameGridInventory inside;
    private int baseline, step;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".restock");
    private static GameItem Put(string id, GameInventory destination)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing fixture " + id); GeneralHelper.SetItemOwned(item);
        Move(item, destination); return item;
    }
    private static void Move(GameItem item, GameInventory destination)
    {
        var slot = destination.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == destination && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == destination, "Cannot move " + item.identifier);
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Display source QA timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f29.enabled", "f29.price");
            bag = Put("backpack_medium", e.invElement); GeneralHelper.SetCustomName(bag, name);
            inside = bag.contentWindow?.childElement as GameGridInventory; Need(inside != null, "No source grid");
            goods = Enumerable.Range(0, 4).Select(_ => Put("energy_drink", inside)).ToArray();
            outside = Put("energy_drink", e.invElement);
            foreach (var item in goods.Concat(new[] { outside })) GeneralHelper.TrackBuyPrice(item, 15);
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width * .7f, Screen.height * .7f));
            baseline = e.showcaseElement.childItems.Where(i => i.identifier == "energy_drink" && GeneralHelper.IsItemOwned(i)).Sum(i => i.unitCount);
            Need(baseline + 2 <= 99, "Unexpectedly full display fixture");
            var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
            var settings = CustomUIManager.Instance.GetWindow("game_options");
            settings.Get(Mod + ":f29.goal").SetFloat(baseline + 2, true);
            settings.Get(Mod + ":f29.keep").SetFloat(1, true); settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
            Need(((ConfigEntry<int>)ModConfig.Find(Mod).Get("f29.goal")).Value == baseline + 2 &&
                ((ConfigEntry<int>)ModConfig.Find(Mod).Get("f29.keep")).Value == 1, "Custom stock limits were not applied");
            RoutingScenario.RunFeatureAction("f29.open"); QaItemUi.Choose(Window(), "source", name);
            QaItemUi.Choose(Window(), "type", goods[0].GetDisplayName(true));
            Window().Get("add").button.onClick.Invoke(); Window().Get("preview").button.onClick.Invoke();
            var planned = goods.Where(i => QaItemUi.Hit(Window().root, i) != null).ToArray();
            Need(planned.Length == 2 && goods.All(i => i.parentInventory == inside) && outside.parentInventory == e.invElement,
                "Preview did not select exactly two objects from the chosen source");
            removed = planned[0]; Move(removed, e.invElement);
            Window().Get("apply").button.onClick.Invoke();
            Need(goods.All(i => i.parentInventory != e.showcaseElement) && outside.parentInventory == e.invElement,
                "Changed source committed a stale preview");
            step++; return false;
        }
        // Reopen to obtain a fresh plan after the stale preview was rejected.
        Window().Close(); RoutingScenario.RunFeatureAction("f29.open"); Window().Get("preview").button.onClick.Invoke();
        Need(goods.Count(i => QaItemUi.Hit(Window().root, i) != null) == 2, "Fresh preview did not retain two available items");
        Window().Get("apply").button.onClick.Invoke();
        Need(goods.Count(i => i.parentInventory == e.showcaseElement) == 2 && goods.Count(i => i.parentInventory == inside) == 1 &&
            removed.parentInventory == e.invElement && outside.parentInventory == e.invElement,
            "Restock ignored the selected source, target amount, or keep-one limit");
        result = "PASS F29 chosen nested source, custom target/keep quantities, stale preview and exact real showcase moves"; return true;
    }
}

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
    [Serializable] private sealed class Ledger { public Row[] entries = Array.Empty<Row>(); }
    [Serializable] private sealed class Row
    { public int day = -1, kind = -1, events = -1, unknownCost = -1, knownCostCount = -1; public long income = -1, expense = -1, knownMargin = -1; }
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
        string raw = ModHelper.GetModData(Mod, "ledger", null);
        var rows = raw == null ? null : JsonUtility.FromJson<Ledger>(raw)?.entries;
        var sale = rows?.SingleOrDefault(r => r.day == day && r.kind == 2);
        Need(sale != null && sale.events == 2 && sale.income == knownSale + unknownSale && sale.expense == 0 &&
            sale.knownCostCount == 1 && sale.unknownCost == 1 && sale.knownMargin == knownSale - cost,
            "Known/unknown sale costs or margin differ from native transactions: " + raw);
        RoutingScenario.RunFeatureAction("f28.open");
        var book = CustomUIManager.Instance.GetWindow(Mod + ".ledger");
        Need(book?.IsAlive == true && book.Get("kind")?.dropdown != null, "Ledger UI unavailable");
        int index = book.Get("kind").dropdown.options.FindIndex(o => o.text == ModHelper.GetLocalized(Mod, "f28.sale"));
        Need(index > 0, "Sale filter unavailable"); book.Get("kind").SetIndex(index, true);
        book = CustomUIManager.Instance.GetWindow(Mod + ".ledger");
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
        Verify(); result = "PASS F28 real purchase and two sales, known margin and unknown cost, visible filter/cash and same-slot reload"; return true;
    }
}

public sealed class WaterFilterPauseScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private GameItem destination, first, second, exhausted, replacement;
    private GameSlotInventory filterSlot;
    private int step, firstBefore, secondBefore;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".water");
    private static GameItem Put(string id, string label, GameInventory destination)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing fixture " + id);
        GeneralHelper.SetItemOwned(item); GeneralHelper.SetCustomName(item, label); Move(item, destination); return item;
    }
    private static void Move(GameItem item, GameInventory destination)
    {
        var marker = destination.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == destination && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == destination,
            "Cannot place " + item.identifier);
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Water filter QA timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true) return false;
            TradeScenario.SetOptions(true, "f26.enabled", "f26.mix");
            destination = Put("water_pitcher", "QA manual filter target", e.invElement);
            filterSlot = destination.contentWindow?.childElement as GameSlotInventory;
            Need(filterSlot != null, "Pitcher filter slot unavailable");
            exhausted = Put("water_filter", "QA nearly spent filter", filterSlot);
            replacement = Put("water_filter", "QA replacement filter", e.invElement);
            DurabilityHelper.SetCurrentDurability(exhausted, 1);
            first = Put("bottled_water", "QA filter source A", e.invElement);
            second = Put("bottled_water", "QA filter source B", e.invElement);
            WaterHelper.Remove(destination, WaterHelper.GetCurrentCapacityUL(destination));
            foreach (var source in new[] { first, second })
            {
                WaterHelper.Remove(source, WaterHelper.GetCurrentCapacityUL(source));
                WaterHelper.AddWater(source, 5, 250000); WaterFeatureHelper.HandleWaterCheckTestingStrip(source);
            }
            RoutingScenario.RunFeatureAction("f26.open"); QaItemUi.Choose(Window(), "target", "QA manual filter target");
            QaItemUi.Click(Window(), first); QaItemUi.Click(Window(), second);
            Window().Get("preview").button.onClick.Invoke();
            Need(QaItemUi.Hit(Window().root, first) != null && QaItemUi.Hit(Window().root, second) != null &&
                WaterHelper.GetCurrentCapacityUL(first) == 250000 && WaterHelper.GetCurrentCapacityUL(second) == 250000,
                "Two-source preview changed contents or omitted a chosen vessel");
            Window().Get("pour").button.onClick.Invoke();
            firstBefore = WaterHelper.GetCurrentCapacityUL(first); secondBefore = WaterHelper.GetCurrentCapacityUL(second);
            Need(DurabilityHelper.GetRealDurability(exhausted) == 0 &&
                ((firstBefore == 0 && secondBefore == 250000) || (secondBefore == 0 && firstBefore == 250000)),
                "F26 did not pause after the first source exhausted the filter: A=" + firstBefore + ", B=" + secondBefore +
                ", filter=" + DurabilityHelper.GetRealDurability(exhausted));
            var remaining = firstBefore > 0 ? first : second;
            Need(QaItemUi.Selected(Window(), remaining), "Paused source was not retained for resumption");
            Window().Get("preview").button.onClick.Invoke();
            Need(Window().Get("pour").GetText() == ModHelper.GetLocalized(Mod, "f26.pourUnfiltered"),
                "An exhausted filter did not require the unfiltered confirmation");
            Window().Close(); RoutingScenario.RunFeatureAction("f26.open");
            QaItemUi.Choose(Window(), "target", "QA manual filter target");
            Need(!QaItemUi.Selected(Window(), remaining), "A closed water session retained its previous selection");
            QaItemUi.Click(Window(), remaining); Window().Get("preview").button.onClick.Invoke();
            Need(Window().Get("pour").GetText() == ModHelper.GetLocalized(Mod, "f26.pourUnfiltered"),
                "Closing the water window lost the exhausted-filter confirmation");
            Move(exhausted, e.invElement); Move(replacement, filterSlot);
            Window().Get("pour").button.onClick.Invoke();
            Need(WaterHelper.GetCurrentCapacityUL(first) == firstBefore && WaterHelper.GetCurrentCapacityUL(second) == secondBefore,
                "A stale preview was executed after the filter was replaced");
            step++; return false;
        }
        Window().Get("preview").button.onClick.Invoke(); Window().Get("pour").button.onClick.Invoke();
        Need(WaterHelper.GetCurrentCapacityUL(first) == 0 && WaterHelper.GetCurrentCapacityUL(second) == 0 &&
            ReferenceEquals(filterSlot.childItem, replacement) && exhausted.parentInventory == e.invElement &&
            WaterHelper.GetCurrentCapacityUL(destination) > 0,
            "Resuming after manual filter replacement did not pour the remaining source or conserve filters");
        result = "PASS F26 single-MOD exhaustion pauses, stale filter preview is rejected, manual replacement resumes"; return true;
    }
}

public sealed class BasketBudgetScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 6);
    private GameItem[] items, chosen;
    private int step, cash, price, limit;
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
        var entry = Field(QaItemUi.Hit(window.root, item), "entry");
        Need(int.TryParse(Field(entry, "Badge") as string, out int quote) && quote > 0, "A real customer quote is unavailable");
        return quote;
    }
    private GameItem[][] ResultGrids(CustomUIWindow window) => window.root.GetComponentsInChildren<Transform>(true)
        .Where(t => t.gameObject.activeInHierarchy && t.name == "ShopAssist_ItemGrid")
        .Select(t => items.Where(item => QaItemUi.Hit(t.gameObject, item) != null).ToArray()).Where(g => g.Length > 0).ToArray();
    private GameItem[] Compare(bool shouldAllowMove)
    {
        RoutingScenario.RunFeatureAction("f34.open"); var w = Window();
        Need(w?.Get("limit") != null && w.Get("compare")?.button != null, "Basket controls unavailable");
        w.Get("limit").SetString(limit.ToString(), true); w.Get("compare").button.onClick.Invoke(); w = Window();
        var grids = ResultGrids(w);
        Need(grids.Length > 0 && grids.All(g => g.Length == 2 && g.Sum(_ => price) == limit),
            "Budget comparison did not show only two-item combinations within the entered limit");
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
            int[] quotes = items.Select(i => Quote(w, i)).ToArray();
            Need(quotes.All(q => q == quotes[0]) && quotes[0] * 2 < 9000 && items.All(i => i.unitCount == 1),
                "Native quotes are not three equal positive single-item offers");
            price = quotes[0]; limit = 2 * price; cash = p.playerCash; w.Close();
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

public sealed class DisplayCapacityScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private GameItem goods;
    private int step;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".restock");
    private static bool CanPlace(GameGridInventory grid, GameItem item)
    {
        var pose = new GridShapeBuilder(item.modifiedShape);
        for (int flip = 0; flip < 2; flip++) for (int turn = 0; turn < 4; turn++)
        {
            pose.SetFlipped(flip != 0).SetOrientation(turn);
            for (int y = 0; y < grid.inventoryShape.height; y++) for (int x = 0; x < grid.inventoryShape.width; x++)
            {
                pose.SetPosition(x, y); var marker = grid.TryInventorySlot(item, item.unitCount, pose);
                if (marker != null && marker.inventory == grid && marker.targetItem == null && marker.numTransfer >= item.unitCount) return true;
            }
        }
        return false;
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Full showcase QA timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance;
        if (e?.storeInteractables?.activeInHierarchy != true) return false;
        TradeScenario.SetOptions(true, "f29.enabled", "f29.price");
        goods = DirectoryMaster.Item("energy_drink"); GeneralHelper.SetItemOwned(goods); GeneralHelper.TrackBuyPrice(goods, 15);
        var source = e.invElement.TryFindOneValidInventorySlot(goods);
        Need(source != null && source.inventory == e.invElement && source.targetItem == null && source.TryAcceptOnce(goods.unitCount) == goods.unitCount,
            "Could not place restock candidate");
        var display = e.showcaseElement;
        int baseline = display.childItems.Where(i => i.identifier == goods.identifier && GeneralHelper.IsItemOwned(i)).Sum(i => i.unitCount);
        for (int n = 0; n < 200 && CanPlace(display, goods); n++)
        {
            var paper = PreBuiltItemHelper.CreatePaperWithText("QA display filler " + Guid.NewGuid().ToString("N"), "QA display filler");
            GeneralHelper.SetItemOwned(paper);
            var marker = display.TryFindOneValidInventorySlot(paper);
            if (marker == null || marker.inventory != display || marker.targetItem != null || marker.numTransfer < paper.unitCount)
            { paper.Destroy(); break; }
            Need(marker.TryAcceptOnce(paper.unitCount) == paper.unitCount && paper.parentInventory == display,
                "Cannot fill showcase with native objects");
        }
        Need(!CanPlace(display, goods), "QA fixture did not physically fill the showcase for every candidate orientation");
        var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
        var settings = CustomUIManager.Instance.GetWindow("game_options");
        settings.Get(Mod + ":f29.goal").SetFloat(baseline + 1, true); settings.Get(Mod + ":f29.keep").SetFloat(0, true);
        settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
        RoutingScenario.RunFeatureAction("f29.open"); QaItemUi.Choose(Window(), "type", goods.GetDisplayName(true));
        Window().Get("add").button.onClick.Invoke(); Window().Get("preview").button.onClick.Invoke();
        Need(QaItemUi.Hit(Window().root, goods) == null && goods.parentInventory == e.invElement,
            "Full showcase preview offered or moved a candidate without physical room");
        Window().Get("apply").button.onClick.Invoke();
        Need(goods.parentInventory == e.invElement, "Full showcase commit lost or moved the candidate");
        result = "PASS F29 physically full showcase retains the real source item through preview and apply"; return true;
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
    private static bool SaleRow(CustomUIWindow book, int amount) => book.root.GetComponentsInChildren<HorizontalLayoutGroup>(true).Any(row =>
    {
        var fields = row.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text).ToArray();
        return fields.Length == 3 && fields[0] == ModHelper.GetLocalized(Mod, "f28.sale") && fields[1] == amount.ToString("N0") && fields[2] == "0";
    });
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

public sealed class RequestColdScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private readonly string label = "QA cold request " + Guid.NewGuid().ToString("N").Substring(0, 6);
    private GameItem bag, goods;
    private StoreRequest request;
    private int step, cash;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Put(GameItem item, GameInventory inventory)
    {
        var slot = inventory.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == inventory && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory, "Cannot place request fixture");
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Request producer timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true || PCUIManager.Instance == null || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f27.enabled");
            foreach (var item in e.GetAllOwnedItems(false, true).Where(i => i.identifier == "wine_yeast_red").ToArray()) item.Destroy();
            bag = DirectoryMaster.Item("backpack_medium"); goods = DirectoryMaster.Item("wine_yeast_red");
            Need(bag != null && goods != null, "Request fixtures unavailable");
            GeneralHelper.SetItemOwned(bag); GeneralHelper.SetItemOwned(goods); GeneralHelper.SetCustomName(bag, label);
            Put(bag, e.invElement); Put(goods, e.invElement);
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width * .7f, Screen.height * .7f));
            request = new StoreRequest { title = label, id = label, description = label, requiredItem = goods.identifier,
                requiredItemCount = 1, requesterName = "QA", sourceEventId = label, duration = 2, creditReward = 155,
                factionId = "FACTION_LOWER_LEVEL" };
            p.storeRequests.Add(request); cash = p.playerCash; PCUIManager.Instance.OpenUI(); step++; return false;
        }
        var row = UnityEngine.Object.FindObjectsOfType<StoreRequestElement>().FirstOrDefault(r => r.storeRequest == request);
        if (row == null) return false;
        var track = row.acceptButton.transform.parent.Find(Mod + ".track")?.GetComponent<Button>();
        Need(track != null && track.interactable, "Native request tracking button unavailable"); track.onClick.Invoke();
        var book = CustomUIManager.Instance.GetWindow(Mod + ".requests");
        Need(book?.Get("track")?.button != null, "Tracking control unavailable"); book.Get("track").button.onClick.Invoke();
        book = CustomUIManager.Instance.GetWindow(Mod + ".requests"); QaItemUi.Choose(book, "destination", label);
        book = CustomUIManager.Instance.GetWindow(Mod + ".requests"); QaItemUi.Click(book, goods); book.Get("prepare").button.onClick.Invoke();
        Need(goods.parentInventory == bag.contentWindow.childElement && goods.uniqueId > 0 && bag.uniqueId > 0 && p.playerCash == cash &&
            p.storeRequests.Contains(request) && RequestColdReloadScenario.Reserved(goods), "Native preparation failed or delivered before confirmation");
        book = CustomUIManager.Instance.GetWindow(Mod + ".requests"); book?.Close(); PCUIManager.Instance.HideUI();
        p.SaveGame(); File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-request-cold-run.txt"),
            string.Join("|", p.runID, p.saveSlotId, label, bag.uniqueId, goods.uniqueId, cash));
        result = "PASS F27 native tracking and physical reservation saved for cold reload"; return true;
    }
}

public sealed class RequestColdReloadScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private QaLoadSlot load;
    private string[] context;
    private GameItem goods;
    private StoreRequest request;
    private int step;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    internal static bool Reserved(GameItem item)
    {
        var assembly = ModLoader.Mods.FirstOrDefault(m => m.Manifest?.ID == Mod)?.Instance?.GetType().Assembly;
        var method = assembly?.GetType("ShopAssist.RequestBook")?.GetMethod("Reserved", BindingFlags.Static | BindingFlags.NonPublic);
        Need(method != null, "Reservation state unavailable"); return (bool)method.Invoke(null, new object[] { item });
    }
    private static object Field(object obj, string key) => obj?.GetType().GetField(key, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(obj);
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Request cold reload timed out at " + step);
        if (load == null)
        {
            string path = Path.Combine(Application.persistentDataPath, "qa-request-cold-run.txt");
            Need(File.Exists(path), "Run RequestColdScenario first"); context = File.ReadAllText(path).Split('|');
            Need(context.Length == 6, "Invalid request save context"); load = new QaLoadSlot(int.Parse(context[1]), Guid.Parse(context[0]));
        }
        if (step == 0)
        {
            if (!load.Tick()) return false;
            var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
            var bag = e.invElement.childItems.SingleOrDefault(i => i.uniqueId == int.Parse(context[3]));
            Need(bag?.contentWindow?.childElement is GameGridInventory, "Cold load lost the selected box");
            goods = ((GameGridInventory)bag.contentWindow.childElement).childItems.SingleOrDefault(i => i.uniqueId == int.Parse(context[4]));
            request = p.storeRequests.SingleOrDefault(r => r.id == context[2]);
            Need(goods != null && request != null && p.playerCash == int.Parse(context[5]) && Reserved(goods),
                "Cold load lost the exact physical reservation or request");
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width * .7f, Screen.height * .7f));
            RoutingScenario.RunFeatureAction("f27.open"); var book = CustomUIManager.Instance.GetWindow(Mod + ".requests");
            var open = book?.root.GetComponentsInChildren<Button>(true).FirstOrDefault(b =>
                b.GetComponentInChildren<TMP_Text>(true)?.text == ModHelper.GetLocalized(Mod, "f27.prepare") &&
                b.transform.parent.GetComponentsInChildren<TMP_Text>(true).Any(t => t.text == context[2]));
            Need(open != null, "Cold-loaded tracked request is not visible"); open.onClick.Invoke();
            book = CustomUIManager.Instance.GetWindow(Mod + ".requests");
            var entry = Field(QaItemUi.Hit(book.root, goods), "entry");
            Need((Field(entry, "Badge") as string) == ModHelper.GetLocalized(Mod, "f27.ready"),
                "Cold-loaded request did not display the same reserved item as ready");
            book.Close(); PCUIManager.Instance.OpenUI(); step++; return false;
        }
        var nativeRow = UnityEngine.Object.FindObjectsOfType<StoreRequestElement>().FirstOrDefault(r => r.storeRequest == request);
        if (nativeRow == null) return false;
        Need(nativeRow.acceptButton != null && nativeRow.acceptButton.interactable && request.CanFillRequest(), "Native delivery action unavailable");
        int before = PlayerStore.Instance.playerCash; nativeRow.acceptButton.onClick.Invoke();
        Need(PlayerStore.Instance.playerCash == before + 155 && !PlayerStore.Instance.storeRequests.Contains(request) &&
            ((GameItemElement)goods).IsDestroyed() && !Reserved(goods),
            "Native delivery did not consume the reserved instance, pay once and release the completed request");
        result = "PASS F27 exact reservation survives cold reload and native delivery consumes it and pays once"; return true;
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
