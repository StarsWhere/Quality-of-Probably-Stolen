using System;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;

// Runs real purchases and a sale in the isolated Player QA save.
public sealed class DailyReceiptScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step;
    private float since = Time.realtimeSinceStartup;
    private float shownAt;
    private string expected;
    private GameItem firstPurchase;
    private StoreClient seller;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - since < 60f, "F16 receipt QA timed out at step " + step);
        if (step == 0)
        {
            if (!start.Tick()) return false;
            Next(1); return false;
        }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            var peer = ModLoader.Mods.Find(mod => mod.Manifest != null && mod.Manifest.ID == "reputation_pop");
            Need(peer == null || !peer.IsEnabled || peer.Instance == null, "F16 standalone receipt test requires Reputation Pop disabled");
            SetOptions(true, true, true);
            PlayerStore.Instance.playerCash = 200000;
            seller = StoreClientList.CreateJunkerSellOnly();
            seller.clientFaction = "FACTION_LOWER_LEVEL";
            SetClient(seller);
            firstPurchase = Purchase("system_module_quality", true, true);
            CheckNotice(expected);
            shownAt = Time.unscaledTime; Next(2); return false;
        }
        if (step == 2)
        {
            if (!Expired()) return false;
            var buyer = StoreClientList.CreateSpacerClientFood();
            buyer.clientFaction = "FACTION_LOWER_LEVEL";
            SetClient(buyer);
            var player = PlayerStore.Instance;
            var reputation = Reputation(buyer);
            int before = player.playerCash;
            double repBefore = reputation.GetReputationExact();
            player.SellItem(firstPurchase);
            int change = player.playerCash - before;
            double repChange = reputation.GetReputationExact() - repBefore;
            Need(change > 0 && !GeneralHelper.IsItemOwned(firstPurchase), "Native sale did not settle");
            expected = Expected(change, reputation, repChange, true, true);
            CheckNotice(expected);
            shownAt = Time.unscaledTime; Next(3); return false;
        }
        if (step == 3)
        {
            if (!Expired()) return false;
            SetOptions(true, false, true);
            SetClient(seller);
            Purchase("system_module_performance", false, true);
            CheckNotice(expected);
            shownAt = Time.unscaledTime; Next(4); return false;
        }
        if (step == 4)
        {
            if (!Expired()) return false;
            SetOptions(true, true, false);
            Purchase("system_module_quality", true, false);
            CheckNotice(expected);
            shownAt = Time.unscaledTime; Next(5); return false;
        }
        if (step == 5)
        {
            if (!Expired()) return false;
            SetOptions(true, false, false);
            Purchase("system_module_performance", false, false);
            Need(Notice() == null, "F16 displayed a receipt when both fields were disabled");
            SetOptions(false, true, true);
            Purchase("system_module_quality", false, false);
            Need(Notice() == null, "F16 displayed a receipt while disabled");
            result = "PASS F16 native buy/sell cash and reputation deltas, field switches, disabled state and one-second expiry";
            return true;
        }
        throw new InvalidOperationException("Unknown F16 QA step " + step);
    }

    private GameItem Purchase(string id, bool showCash, bool showRep)
    {
        var player = PlayerStore.Instance;
        var inventory = EmporiumEntry.Instance.backInvinvElement;
        var item = DirectoryMaster.Item(id);
        Need(item != null, "Missing native item " + id);
        GeneralHelper.SetItemOwned(item, false);
        var slot = inventory.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == inventory && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory,
            "Could not place purchase fixture " + id);
        var reputation = Reputation(seller);
        int before = player.playerCash, price = (int)item.GetNegociatedValue();
        double repBefore = reputation.GetReputationExact();
        player.BuyItem(item);
        double repChange = reputation.GetReputationExact() - repBefore;
        Need(price > 0 && player.playerCash == before - price && GeneralHelper.IsItemOwned(item), "Native purchase did not settle");
        if (showRep) Need(Math.Abs(repChange) > 0.000001, "Purchase did not produce a reputation delta to test");
        expected = Expected(-price, reputation, repChange, showCash, showRep);
        return item;
    }

    private static string Expected(int cash, StoreReputation reputation, double rep, bool showCash, bool showRep)
    {
        string money = showCash && cash != 0 ? ModHelper.GetLocalized(Mod, cash > 0 ? "f16.in" : "f16.out", Math.Abs(cash)) : null;
        string standing = showRep && Math.Abs(rep) > 0.000001 ?
            reputation.factionDisplay + " " + rep.ToString("+0.##;-0.##", CultureInfo.CurrentCulture) : null;
        return string.Join("  ·  ", new[] { money, standing }.Where(value => value != null));
    }

    private static void SetClient(StoreClient client)
    {
        PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(client);
        PlayerStore.Instance.isClientArrived = true;
    }

    private static StoreReputation Reputation(StoreClient client)
    {
        var result = StoreReputation.GetStoreReputation(client.clientFaction);
        Need(result != null, "Native customer faction reputation unavailable");
        return result;
    }

    private static void SetOptions(bool enabled, bool money, bool reputation)
    {
        var options = StoreUIManager.Instance.gameOptionUIManager;
        options.OpenUI();
        var window = CustomUIManager.Instance.GetWindow("game_options");
        Need(window != null, "Native options unavailable");
        window.Get(Mod + ":f01.enabled").SetBool(true, true);
        window.Get(Mod + ":f16.enabled").SetBool(enabled, true);
        window.Get(Mod + ":f16.money").SetBool(money, true);
        window.Get(Mod + ":f16.rep").SetBool(reputation, true);
        window.Get(Mod + ":f16.sound").SetBool(false, true);
        window.Get(Mod + ":f16.seconds").SetFloat(1, true);
        window.Get(Mod + ":f28.enabled").SetBool(false, true);
        window.Get("apply").button.onClick.Invoke();
        var config = ModConfig.Find(Mod);
        Need(((ConfigEntry<bool>)config.Get("f16.enabled")).Value == enabled &&
            ((ConfigEntry<bool>)config.Get("f16.money")).Value == money &&
            ((ConfigEntry<bool>)config.Get("f16.rep")).Value == reputation &&
            ((ConfigEntry<int>)config.Get("f16.seconds")).Value == 1, "F16 options did not apply");
        options.CloseUI();
    }

    private static GameObject Notice() => QaTradeReceipt.Latest();

    private static void CheckNotice(string text)
    {
        var notice = Notice();
        Need(!string.IsNullOrEmpty(text) && notice != null, "F16 receipt did not appear");
        Need(QaTradeReceipt.Text() == text, "F16 receipt did not match the actual transaction: " + QaTradeReceipt.Text() + " / " + text);
        Need(notice.GetComponentsInChildren<UnityEngine.UI.Graphic>().All(graphic => !graphic.raycastTarget), "F16 receipt blocked input");
        Need(notice.GetComponentsInChildren<TMP_Text>().All(label => label.enabled && label.fontSize >= 12), "Receipt text is hidden or unreadable");
    }

    private bool Expired()
    {
        float elapsed = Time.unscaledTime - shownAt;
        if (elapsed < 0.35f) return false;
        if (elapsed < 0.8f) { CheckNotice(expected); return false; }
        if (elapsed < 1.3f) return false;
        Need(Notice() == null, "F16 one-second receipt did not expire");
        return true;
    }

    private void Next(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
