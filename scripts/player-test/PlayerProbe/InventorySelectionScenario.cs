using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Both scenarios use a new QA run and the game's real inventory and UI callbacks.
public sealed class CargoDestinationScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120;
    private readonly string destinationName = "QA cargo destination " + Guid.NewGuid().ToString("N").Substring(0, 6);
    private GameItem bag, destination, keep, first, second;
    private GameGridInventory inside, target;
    private int step;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".cargo");
    private static GameItem Put(string id, GameInventory target)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing cargo fixture " + id);
        GeneralHelper.SetItemOwned(item); Move(item, target); return item;
    }
    private static void Move(GameItem item, GameInventory target)
    {
        var marker = target.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == target && marker.targetItem == null && marker.numTransfer >= item.unitCount &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == target,
            "Could not move cargo fixture " + item.identifier);
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Cargo destination QA timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f25.enabled");
            bag = Put("backpack_medium", e.invElement); destination = Put("backpack_medium", e.invElement);
            GeneralHelper.SetCustomName(destination, destinationName);
            inside = bag.contentWindow?.childElement as GameGridInventory;
            target = destination.contentWindow?.childElement as GameGridInventory;
            Need(inside != null && target != null && inside != target, "The outing bag and destination do not have distinct native grids");
            keep = Put("wine_yeast_red", inside); e.selectedBackpack = bag; p.visitLeftTonight = 2;
            var site = AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND);
            Need(site != null && site.IsAvailable(), "Native outing is unavailable");
            MapUIManager.Instance.Travel(site); step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !e.afterhourWindow.IsVisible() ||
                e.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            first = Put("wine_berry", e.afterhourInventory); second = Put("wine_yeast", e.afterhourInventory);
            GeneralHelper.SetCustomName(first, "QA selected trip gain"); GeneralHelper.SetCustomName(second, "QA unselected trip gain");
            Move(first, inside); Move(second, inside);
            Need(keep.parentInventory == inside && first.parentInventory == inside && second.parentInventory == inside,
                "Native trip bag did not contain the original item and both gains");
            MapUIManager.Instance.ReturnNow(); step++; return false;
        }
        if (step == 3)
        {
            if (MapUIManager.Instance.IsOutside || p.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
                bag.parentInventory == e.afterhourPocketSlotInvBackpack || e.storeInteractables?.activeInHierarchy != true) return false;
            RoutingScenario.RunFeatureAction("f25.open"); var cargo = Window();
            Need(cargo?.Get("unload")?.button != null && QaItemUi.Hit(cargo.root, first) != null &&
                QaItemUi.Hit(cargo.root, second) != null && QaItemUi.Hit(cargo.root, keep) == null &&
                QaItemUi.Selected(cargo, first) && QaItemUi.Selected(cargo, second),
                "Cargo grid did not distinguish both new items from the original equipment");
            QaItemUi.Choose(cargo, "destination", destinationName); cargo = Window();
            Need(cargo != null && QaItemUi.Selected(cargo, first) && QaItemUi.Selected(cargo, second),
                "Selecting a non-default destination lost the gain selection");
            QaItemUi.Click(cargo, second);
            Need(QaItemUi.Selected(cargo, first) && !QaItemUi.Selected(cargo, second), "Could not deselect exactly the second trip gain");
            cargo.Get("unload").button.onClick.Invoke();
            var preview = Window();
            Need(preview?.Get("confirm")?.button?.interactable == true && first.parentInventory == inside &&
                second.parentInventory == inside && keep.parentInventory == inside,
                "Unload preview changed inventory or did not offer confirmation");
            preview.Get("confirm").button.onClick.Invoke();
            Need(first.parentInventory == target && second.parentInventory == inside && keep.parentInventory == inside &&
                destination.parentInventory == e.invElement && !ReferenceEquals(first.parentInventory, e.invElement),
                "Unloading did not move only the selected gain to the chosen container");
            step++; return false;
        }
        var remaining = Window();
        Need(remaining?.Get("unload")?.button != null && QaItemUi.Hit(remaining.root, first) == null &&
            QaItemUi.Hit(remaining.root, second) != null && !QaItemUi.Selected(remaining, second),
            "The remaining unselected gain was not retained after the first unload");
        QaItemUi.Click(remaining, second); remaining.Get("unload").button.onClick.Invoke();
        var retry = Window();
        Need(retry?.Get("confirm")?.button?.interactable == true && second.parentInventory == inside,
            "Retry preview changed inventory or did not offer confirmation");
        retry.Get("confirm").button.onClick.Invoke();
        Need(first.parentInventory == target && second.parentInventory == target && keep.parentInventory == inside,
            "Retry did not move the remaining selected gain to the same target or retained the original equipment incorrectly");
        result = "PASS F25 chosen non-default container and partial selection, exact separate unloading and original equipment"; return true;
    }
}

public sealed class BasketDifferentQuotesScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120;
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 6);
    private readonly Dictionary<GameItem, int> quotes = new Dictionary<GameItem, int>();
    private readonly HashSet<GameItem> seen = new HashSet<GameItem>();
    private GameItem[] offered, pair;
    private GameItem unquoted;
    private int step, cash, frame, pages;
    private bool fullBudget;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".basket");
    private static object Field(object obj, string name) => obj?.GetType().GetField(name, Flags)?.GetValue(obj);
    private static void Move(GameItem item, GameInventory destination)
    {
        var marker = destination.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == destination && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == destination,
            "Could not place quoted item " + item.identifier);
    }
    private GameItem Make(string id, string label, GameInventory destination)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing food fixture " + id);
        GeneralHelper.SetItemOwned(item); GeneralHelper.SetCustomName(item, label + " " + token); Move(item, destination); return item;
    }
    private static bool TryNativeQuote(GameItem item, out int price)
    {
        price = 0;
        var ui = NegociationUIManager.Instance;
        if (ui?.uiObject == null || !ui.IsOpen) return false;
        string name = item.GetDisplayName(false), icon = GeneralHelper.CreditIcon(false);
        foreach (var row in ui.uiObject.GetComponentsInChildren<TabletListRow>(true).Where(r => r.gameObject.activeInHierarchy))
        {
            var labels = row.GetComponentsInChildren<TMP_Text>(true);
            var title = labels.FirstOrDefault(t => t.name == "Name" && t.isActiveAndEnabled && t.text == name &&
                t.textInfo != null && t.textInfo.characterCount > 0);
            var right = labels.FirstOrDefault(t => t.name == "Right" && t.isActiveAndEnabled && t.text != null &&
                t.text.EndsWith(icon, StringComparison.Ordinal) && t.textInfo != null && t.textInfo.characterCount > 0);
            if (title == null || right == null) continue;
            if (int.TryParse(right.text.Substring(0, right.text.Length - icon.Length), out price) && price > 0) return true;
        }
        price = 0; return false;
    }
    private static GameItem[] Items(Transform grid) => grid.GetComponentsInChildren<MonoBehaviour>(true)
        .Where(hit => hit != null && hit.gameObject.activeInHierarchy && hit.gameObject.name == "item_cell" &&
            hit.GetType().FullName == "ShopAssist.ItemGrid+Hit")
        .Select(hit => Field(Field(hit, "entry"), "Item") as GameItem).Where(item => item != null).Distinct().ToArray();
    private static GameItem[][] Grids(CustomUIWindow window) => window.root.GetComponentsInChildren<Transform>(true)
        .Where(t => t.gameObject.activeInHierarchy && t.name == "ShopAssist_CounterPreview")
        .Select(Items).Where(items => items.Length > 0).ToArray();
    private bool ScanPage(bool initial)
    {
        var window = Window();
        var hits = window.root.GetComponentsInChildren<MonoBehaviour>(true)
            .Where(hit => hit != null && hit.gameObject.activeInHierarchy && hit.gameObject.name == "item_cell" &&
                hit.GetType().FullName == "ShopAssist.ItemGrid+Hit")
            .GroupBy(hit => Field(Field(hit, "entry"), "Item") as GameItem).Select(group => group.First()).ToArray();
        Need(hits.Length > 0 && ++pages <= 512, "Basket pagination did not reach its end");
        foreach (var hit in hits)
        {
            var entry = Field(hit, "entry"); var item = Field(entry, "Item") as GameItem;
            Need(item != null && seen.Add(item), "Basket pagination repeated or omitted an item");
            bool enabled = Field(entry, "Enabled") is bool isEnabled && isEnabled;
            bool selected = Field(entry, "Selected") is bool isSelected && isSelected;
            if (offered.Contains(item))
                Need(enabled && Field(entry, "Badge") as string == quotes[item].ToString(),
                    "The selection grid did not retain a native quoted price for " + item.identifier);
            if (item == unquoted)
                Need(enabled && Field(entry, "Badge") as string != ModHelper.GetLocalized(Mod, "f34.unpriced"),
                    "A matching item in storage did not receive a calculated quote");
            if (initial && enabled && !pair.Contains(item) && selected)
            {
                QaItemUi.Click(hit);
                Need(Field(entry, "Selected") is bool deselected && !deselected, "Quoted item could not be excluded");
            }
            else if (pair.Contains(item)) Need(enabled && selected, "Selected quote was not retained");
            else if (!initial && enabled) Need(!selected, "Excluded quote was selected again");
        }
        var next = window.Get("next")?.button;
        if (next != null && next.interactable) { next.onClick.Invoke(); return false; }
        Need(offered.All(seen.Contains) && seen.Contains(unquoted), "Basket omitted a quoted or storage fixture");
        return true;
    }
    private GameItem[][] Compare(int limit, bool allowMove)
    {
        RoutingScenario.RunFeatureAction("f34.open"); var window = Window();
        Need(window?.Get("limit") != null && window.Get("compare")?.button != null, "Budget comparison controls unavailable");
        window.Get("limit").SetString(limit.ToString(), true); window.Get("compare").button.onClick.Invoke(); window = Window();
        var grids = Grids(window);
        Need(grids.Length > 0, "No quoted combination was offered within the entered budget");
        var text = window.root.GetComponentsInChildren<TMP_Text>(true).Where(t => t.isActiveAndEnabled).Select(t => t.text).ToArray();
        foreach (var grid in grids)
        {
            Need(grid.All(item => pair.Contains(item)) && grid.Distinct().Count() == grid.Length,
                "A result included an explicitly excluded instance");
            int total = grid.Sum(item => quotes[item]);
            Need(total <= limit && text.Contains(ModHelper.GetLocalized(Mod, "f34.summary", grid.Sum(item => item.unitCount), total, limit - total)),
                "Displayed combination or total exceeded the entered budget or differed from the observed quotes");
        }
        Need(offered.All(i => i.parentInventory == EmporiumEntry.Instance.invElement) &&
            unquoted.parentInventory == EmporiumEntry.Instance.invElement && PlayerStore.Instance.playerCash == cash,
            "Comparing candidates changed physical stock or cash");
        var buttons = window.root.GetComponentsInChildren<Button>(true).Where(button => button.gameObject.activeInHierarchy &&
            button.GetComponentInChildren<TMP_Text>(true)?.text == ModHelper.GetLocalized(Mod, "f34.place")).ToArray();
        Need(allowMove ? buttons.Length == grids.Length : buttons.Length == 0, "Placement permission did not control the combination buttons");
        if (!allowMove) { window.Close(); return grids; }
        int selected = Array.FindIndex(grids, grid => grid.Length == 2 && pair.All(grid.Contains));
        Need(selected >= 0, "A budget covering the two selected quotes did not offer their two-item combination");
        buttons[selected].onClick.Invoke(); return grids;
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Different-quote basket QA timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true || NegociationUIManager.Instance == null) return false;
            TradeScenario.SetOptions(true, "f34.enabled"); TradeScenario.SetOptions(false, "f34.move");
            var buyer = StoreClientList.CreateSpacerClientFood(); buyer.SetBudget(50000); buyer.isIntroduced = true;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer); p.isClientArrived = true;
            offered = new[] { Make("cup_noodle", "QA quoted noodle", e.backInvinvElement),
                Make("processed_meat", "QA quoted meat", e.backInvinvElement),
                Make("processed_juice", "QA quoted juice", e.backInvinvElement) };
            unquoted = Make("processed_milk", "QA not quoted", e.invElement);
            Need(offered.All(item => item.unitCount == 1), "Quoted fixtures are not single physical items");
            NegociationUIManager.Instance.OpenUI(); frame = Time.frameCount + 3; step++; return false;
        }
        if (step == 2)
        {
            if (Time.frameCount < frame || !NegociationUIManager.Instance.IsOpen) return false;
            quotes.Clear();
            foreach (var item in offered)
            {
                if (!TryNativeQuote(item, out int price)) return false;
                quotes[item] = price;
            }
            pair = offered.OrderBy(item => quotes[item]).Take(1)
                .Concat(offered.Where(item => quotes[item] > quotes.Values.Min()).OrderBy(item => quotes[item]).Take(1)).ToArray();
            Need(pair.Length == 2 && quotes[pair[0]] != quotes[pair[1]] && quotes[pair[0]] + quotes[pair[1]] <= 50000,
                "Native food fixtures did not yield two different quotes within the buyer's budget: " +
                string.Join(", ", offered.Select(item => item.identifier + "=" + quotes[item])));
            cash = p.playerCash;
            RoutingScenario.RunFeatureAction("f34.open"); step++; return false;
        }
        if (step == 3)
        {
            if (!ScanPage(true)) return false;
            Window().Close(); foreach (var item in offered) Move(item, e.invElement); NegociationUIManager.Instance.RefreshUI();
            RoutingScenario.RunFeatureAction("f34.open"); step++; return false;
        }
        if (step == 4)
        {
            var previous = Window().Get("previous")?.button;
            if (previous != null && previous.interactable) { previous.onClick.Invoke(); return false; }
            seen.Clear(); pages = 0; step++; return false;
        }
        if (step == 5)
        {
            if (!ScanPage(false)) return false;
            if (!fullBudget)
            {
                var limited = Compare(Math.Max(quotes[pair[0]], quotes[pair[1]]), false);
                Need(limited.All(grid => grid.Length == 1), "A strict budget offered both differently priced items above the limit");
                TradeScenario.SetOptions(true, "f34.move"); fullBudget = true;
                RoutingScenario.RunFeatureAction("f34.open"); step = 4; return false;
            }
            Compare(quotes[pair[0]] + quotes[pair[1]], true); step++; return false;
        }
        if (pair.Any(item => item.parentInventory != e.backInvinvElement)) return false;
        Need(offered.Except(pair).All(item => item.parentInventory == e.invElement) && unquoted.parentInventory == e.invElement &&
            offered.All(GeneralHelper.IsItemOwned) && p.playerCash == cash, "Staging a selected pair moved other goods or completed a sale");
        result = "PASS F34 distinct native displayed quotes, strict budget, displayed combinations and selected pair staged without sale"; return true;
    }
}
