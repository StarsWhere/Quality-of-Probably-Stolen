using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class RepeatedPurchaseScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly bool allFeatures;
    private Dictionary<string, bool> originalOptions;
    public RepeatedPurchaseScenario() { }
    protected RepeatedPurchaseScenario(bool allFeatures) { this.allFeatures = allFeatures; }
    private readonly QaNewRun start = new QaNewRun();
    private int step;
    private float started = Time.realtimeSinceStartup;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static GameItem Offer(string id)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing offer " + id);
        GeneralHelper.SetItemOwned(item, false); var back = EmporiumEntry.Instance.backInvinvElement;
        var slot = back.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.targetItem == null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == back,
            "Could not place offer " + id);
        return item;
    }
    private static bool RetainedOnce(GameItem item) => GeneralHelper.IsItemOwned(item) &&
        item.parentInventory == EmporiumEntry.Instance.backInvinvElement &&
        EmporiumEntry.Instance.backInvinvElement.childItems.Count(i => ReferenceEquals(item, i)) == 1;

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup - started < 75, "Repeated purchase scenario timed out");
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance; var ui = NegociationUIManager.Instance;
        if (step == 1)
        {
            if (e.storeInteractables?.activeInHierarchy != true || ui == null) return false;
            var peer = ModLoader.Mods.Find(m => m.Manifest?.ID == "purchase_stay_counter");
            Need(peer == null || peer.IsEnabled && peer.Instance != null && string.IsNullOrEmpty(peer.LoadError), "Installed peer failed to start");
            if (allFeatures)
            {
                var config = ModConfig.Find(Mod);
                originalOptions = Enumerable.Range(1, 41).ToDictionary(i => "f" + i.ToString("00") + ".enabled",
                    i => ((ConfigEntry<bool>)config.Get("f" + i.ToString("00") + ".enabled")).Value);
                TradeScenario.SetOptions(true, originalOptions.Keys.ToArray());
                if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            }
            else TradeScenario.SetOptions(true, "f01.enabled");
            p.playerCash = 200000;
            foreach (var item in e.GetAllNonOwnedItem().ToArray()) item.Destroy();
            var client = StoreClientList.CreateJunkerSellOnly(); client.isMultiBuyDisabled = false;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(client); p.isClientArrived = true;
            var first = Offer("system_module_quality"); var second = Offer("system_module_performance");
            int price = checked((int)(first.GetNegociatedValue() + second.GetNegociatedValue())), before = p.playerCash;
            ui.OnBuyAllClick();
            Need(price > 0 && p.playerCash == before - price && RetainedOnce(first) && RetainedOnce(second),
                "The native buy button did not charge once for both retained items");
            client.isBusinessComplete = false;
            before = p.playerCash; ui.OnBuyAllClick();
            Need(p.playerCash == before && RetainedOnce(first) && RetainedOnce(second),
                "Pressing buy again charged or moved already-owned items");
            client.isBusinessComplete = false;
            var third = Offer("wine_yeast"); int thirdPrice = checked((int)third.GetNegociatedValue());
            before = p.playerCash; ui.OnBuyAllClick();
            Need(thirdPrice > 0 && p.playerCash == before - thirdPrice && RetainedOnce(first) && RetainedOnce(second) && RetainedOnce(third),
                "A new offer repurchased an already-owned item or charged the new item more than once");
            before = p.playerCash; ui.OnBuyAllClick();
            Need(p.playerCash == before, "An empty follow-up purchase changed cash");
            ui.CloseUI(); Need(!ui.IsOpen, "Negotiation panel did not close");
            step++; return false;
        }
        Need(!ui.IsOpen, "Negotiation panel reopened during cleanup");
        if (allFeatures)
        {
            TradeScenario.SetOptions(false, originalOptions.Keys.ToArray());
            var enabled = originalOptions.Where(kv => kv.Value).Select(kv => kv.Key).ToArray();
            if (enabled.Length > 0) TradeScenario.SetOptions(true, enabled);
            Need(originalOptions.All(kv => ((ConfigEntry<bool>)ModConfig.Find(Mod).Get(kv.Key)).Value == kv.Value),
                "Restoring all 37 settings failed");
        }
        result = "PASS native buy button excludes retained goods across repeated and subsequent offers" +
            (allFeatures ? " with all 41 live settings enabled, disabled and restored" : "");
        return true;
    }
}

public sealed class AllFeaturesScenario : RepeatedPurchaseScenario { public AllFeaturesScenario() : base(true) { } }
