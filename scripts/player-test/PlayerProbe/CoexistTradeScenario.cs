using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

// Run with the real Workshop peers enabled in a fresh, isolated QA save.
public sealed class CoexistTradeScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step, initialCash, purchasedTotal, soldTotal, wholesaleTotal;
    private GameItem first, second;
    private float started = Time.realtimeSinceStartup;

    [Serializable] private sealed class Ledger { public Row[] entries = Array.Empty<Row>(); }
    [Serializable] private sealed class Row { public int kind = -1, day = -1, events = -1; public long income = -1, expense = -1; }

    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Peer(string id)
    {
        var mod = ModLoader.Mods.Find(m => m.Manifest != null && m.Manifest.ID == id);
        Need(mod != null && mod.IsEnabled && mod.Instance != null && string.IsNullOrEmpty(mod.LoadError), "Peer not running: " + id);
    }
    private static GameItem Put(string id, GameInventory grid)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing item " + id);
        GeneralHelper.SetItemOwned(item, false);
        var slot = grid.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.targetItem == null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid,
            "Could not place " + id);
        return item;
    }
    private static string Notice()
    {
        var go = GameObject.Find(Mod + ".notice");
        return go == null ? "" : string.Join(" ", go.GetComponentsInChildren<TMP_Text>().Select(t => t.text));
    }
    private static double Pending(string faction)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ReputationPop.Pop", false)).FirstOrDefault(t => t != null);
        var pending = type?.GetField("pending", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary;
        Need(pending != null, "Reputation Pop pending state unavailable");
        return pending.Contains(faction) ? Convert.ToDouble(pending[faction]) : 0;
    }
    private static int Purchase(GameItem item, StoreClient client, bool throughTable)
    {
        var p = PlayerStore.Instance; var rep = StoreReputation.GetStoreReputation(client.clientFaction);
        int cash = p.playerCash, price = (int)item.GetNegociatedValue();
        double before = rep.GetReputationExact(), queued = Pending(client.clientFaction);
        if (throughTable) { p.currentNegociatedItem = item; p.BuyItemFromTable(); }
        else p.BuyItem(item);
        double delta = rep.GetReputationExact() - before;
        Need(price > 0 && p.playerCash == cash - price && GeneralHelper.IsItemOwned(item) &&
            item.parentInventory == EmporiumEntry.Instance.backInvinvElement &&
            EmporiumEntry.Instance.backInvinvElement.childItems.Count(i => ReferenceEquals(i, item)) == 1,
            "Purchase was charged or moved more than once");
        Need(delta >= 0.005 && Math.Abs(Pending(client.clientFaction) - queued - delta) < 0.000001,
            "The peer did not record the actual reputation change");
        string receipt = Notice();
        Need(receipt.Contains(price.ToString()) && !receipt.Contains(rep.factionDisplay),
            "Receipt omitted money or repeated the peer's reputation: " + receipt);
        return price;
    }
    private static Row ReadRow(int kind)
    {
        string raw = ModHelper.GetModData(Mod, "ledger", null);
        var book = raw == null ? null : JsonUtility.FromJson<Ledger>(raw);
        var row = book?.entries?.SingleOrDefault(r => r.day == StoreStation.GetDayCounter() && r.kind == kind);
        Need(row != null, "Missing ledger row " + kind + ": " + raw);
        return row;
    }
    private static string[] Visible(CustomUIWindow window) => window.root.GetComponentsInChildren<TMP_Text>(true)
        .Where(t => t.gameObject.activeInHierarchy).Select(t => t.text).ToArray();
    private static bool HasLedgerRow(CustomUIWindow window, int kind, long income, long expense)
    {
        string label = ModHelper.GetLocalized(Mod, kind == 1 ? "f28.purchase" : kind == 2 ? "f28.sale" : "f28.wholesale");
        return window.root.GetComponentsInChildren<UnityEngine.UI.HorizontalLayoutGroup>(true).Any(row =>
        {
            var fields = row.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text).ToArray();
            return fields.Length == 3 && fields[0] == label && fields[1] == income.ToString("N0") && fields[2] == expense.ToString("N0");
        });
    }

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - started < 90, "Trade coexistence timed out at step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var p = PlayerStore.Instance; var e = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (e.storeInteractables?.activeInHierarchy != true || NegociationUIManager.Instance == null) return false;
            Peer("purchase_stay_counter"); Peer("reputation_pop");
            TradeScenario.SetOptions(false, "f28.enabled");
            p.playerCash = 200000;
            TradeScenario.SetOptions(true, "f01.enabled", "f01.wholesale", "f16.enabled", "f16.money", "f16.rep", "f28.enabled");
            initialCash = p.playerCash;
            var client = StoreClientList.CreateJunkerSellOnly(); client.clientFaction = "FACTION_LOWER_LEVEL";
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(client); p.isClientArrived = true;
            first = Put("system_module_quality", e.backInvinvElement);
            second = Put("system_module_performance", e.backInvinvElement);
            purchasedTotal += Purchase(first, client, false);
            purchasedTotal += Purchase(second, client, true);
            var row = ReadRow(1);
            Need(row.events == 2 && row.income == 0 && row.expense == purchasedTotal, "Purchase ledger was duplicated");
            step++; return false;
        }
        if (step == 2)
        {
            var buyer = StoreClientList.CreateSpacerClientFood(); buyer.clientFaction = "FACTION_LOWER_LEVEL";
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer);
            int cash = p.playerCash; p.SellItem(first); soldTotal = p.playerCash - cash;
            Need(soldTotal > 0 && !GeneralHelper.IsItemOwned(first), "Native sale failed");
            var row = ReadRow(2);
            Need(row.events == 1 && row.income == soldTotal && row.expense == 0, "Sale ledger was duplicated");
            foreach (var item in e.GetAllNonOwnedItem().ToArray()) item.Destroy();
            var supplier = StoreClientList.CreateJunkerSellOnly(); supplier.clientFaction = "FACTION_LOWER_LEVEL";
            supplier.isOfferWholesale = true; supplier.wholesaleDiscount = 20;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(supplier);
            var one = Put("wine_yeast", e.frontInvinvElement);
            var two = Put("wine_superyeast", e.backInvinvElement);
            wholesaleTotal = p.GetWholesalePrice(); cash = p.playerCash;
            NegociationUIManager.Instance.OnWholesaleClick();
            Need(wholesaleTotal > 0 && p.playerCash == cash - wholesaleTotal && GeneralHelper.IsItemOwned(one) &&
                GeneralHelper.IsItemOwned(two) && one.parentInventory == e.backInvinvElement && two.parentInventory == e.backInvinvElement &&
                e.backInvinvElement.childItems.Count(i => ReferenceEquals(i, one) || ReferenceEquals(i, two)) == 2,
                "Wholesale settlement charged or moved goods more than once");
            row = ReadRow(3);
            Need(row.events == 1 && row.income == 0 && row.expense == wholesaleTotal, "Wholesale ledger was duplicated");
            step++; return false;
        }
        if (step == 3)
        {
            var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
            RoutingScenario.OpenOptionAction("f28.open");
            var book = CustomUIManager.Instance.GetWindow(Mod + ".ledger");
            Need(book?.IsAlive == true && book.Get("kind")?.dropdown != null, "Ledger type selector unavailable");
            var texts = Visible(book);
            Need(HasLedgerRow(book, 1, 0, purchasedTotal) && HasLedgerRow(book, 2, soldTotal, 0) && HasLedgerRow(book, 3, 0, wholesaleTotal), "All-types ledger omits transactions");
            int saleIndex = book.Get("kind").dropdown.options.FindIndex(o => o.text == ModHelper.GetLocalized(Mod, "f28.sale"));
            Need(saleIndex > 0, "Sale filter unavailable"); book.Get("kind").SetIndex(saleIndex, true);
            book = CustomUIManager.Instance.GetWindow(Mod + ".ledger"); texts = Visible(book);
            Need(HasLedgerRow(book, 2, soldTotal, 0) && !HasLedgerRow(book, 1, 0, purchasedTotal) && !HasLedgerRow(book, 3, 0, wholesaleTotal) &&
                texts.Contains(ModHelper.GetLocalized(Mod, "f28.total", soldTotal)), "Filtered rows or total are incorrect");
            book.Get("kind").SetIndex(0, true); book = CustomUIManager.Instance.GetWindow(Mod + ".ledger");
            texts = Visible(book);
            Need(HasLedgerRow(book, 1, 0, purchasedTotal) && HasLedgerRow(book, 2, soldTotal, 0) && HasLedgerRow(book, 3, 0, wholesaleTotal) &&
                texts.Contains(ModHelper.GetLocalized(Mod, "f28.total", (long)p.playerCash - initialCash)),
                "Resetting the type filter did not restore the complete totals: " + string.Join(" | ", texts) +
                "; initial=" + initialCash + "; final=" + p.playerCash + "; ledger=" + ModHelper.GetModData(Mod, "ledger", null));
            book.Close();
            // The native counter callback can open this paper panel while setting up
            // the customer. Close it before the process tears down its parent canvas.
            NegociationUIManager.Instance.CloseUI();
            Need(!NegociationUIManager.Instance.IsOpen, "Native negotiation panel remained open");
            step++; return false;
        }
        if (step == 4)
        {
            Need(!NegociationUIManager.Instance.IsOpen, "Native negotiation panel reopened during cleanup");
            result = "PASS peer coexistence for native single/table purchases, one reputation popup, single-charge wholesale, ledger and type filter";
            return true;
        }
        return false;
    }
}
