using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

public class RepeatedPurchaseScenario
{
    private const string Mod = "starswhere.shopassist";
    private const string CargoMode = "f25.return_mode";
    private readonly bool allFeatures;
    private Dictionary<string, bool> originalOptions;
    private int originalCargoMode;
    public RepeatedPurchaseScenario() { }
    protected RepeatedPurchaseScenario(bool allFeatures) { this.allFeatures = allFeatures; }
    private readonly QaNewRun start = new QaNewRun();
    private int step;
    private static GameItem outer, inner, special;
    private static bool throwTransfer, hookSeen;
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
        try { return Run(out result); }
        catch (Exception error)
        {
            if (originalOptions != null)
            {
                try { SetFeatures(null); originalOptions = null; }
                catch (Exception restoreError) { throw new AggregateException("Purchase scenario failed and settings could not be restored", error, restoreError); }
            }
            throw;
        }
    }

    private bool Run(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup - started < 75, "Repeated purchase scenario timed out");
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance; var ui = NegociationUIManager.Instance;
        if (step == 1)
        {
            if (e.storeInteractables?.activeInHierarchy != true || ui == null) return false;
            if (allFeatures)
            {
                var config = ModConfig.Find(Mod);
                var assembly = ModLoader.Mods.First(value => value.Manifest?.ID == Mod).Instance.GetType().Assembly;
                var catalog = assembly.GetType("ShopAssist.FeatureCatalog", true);
                var features = catalog.GetProperty("All", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(null) as System.Collections.IEnumerable;
                Need(features != null, "Feature catalog is unavailable");
                string[] keys = features.Cast<object>().Select(feature => (string)feature.GetType().GetProperty("SettingKey",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(feature)).ToArray();
                Need(keys.Length > 0 && keys.Distinct().Count() == keys.Length && keys.Contains(CargoMode), "Invalid feature catalog");
                var snapshot = new Dictionary<string, bool>();
                foreach (string key in keys.Where(key => key != CargoMode))
                {
                    var option = config?.Get(key) as ConfigEntry<bool>;
                    Need(option != null, "Missing feature toggle " + key);
                    snapshot.Add(key, option.Value);
                }
                var cargo = config?.Get(CargoMode) as ConfigEntry<int>;
                Need(cargo != null, "Missing cargo return mode");
                originalCargoMode = cargo.Value; originalOptions = snapshot;
                SetFeatures(true);
                if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            }
            else TradeScenario.SetOptions(true, "f01.enabled");
            p.playerCash = 200000;
            foreach (var item in e.GetAllNonOwnedItem().ToArray()) item.Destroy();
            var client = StoreClientList.CreateJunkerSellOnly(); client.isMultiBuyDisabled = false;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(client); p.isClientArrived = true;
            if (!allFeatures) CheckPurchaseRouting(p);
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
            SetFeatures(false);
            SetFeatures(null); originalOptions = null;
        }
        result = "PASS native buy button excludes retained goods across repeated and subsequent offers" +
            (allFeatures ? " with all live feature settings enabled, disabled and restored" :
                "; native auto-storage, independent switches, barter, lottery, nested and failed transfers");
        return true;
    }

    private void SetFeatures(bool? enabled)
    {
        var options = StoreUIManager.Instance.gameOptionUIManager;
        options.OpenUI();
        var window = CustomUIManager.Instance.GetWindow("game_options");
        Need(window?.IsAlive == true, "Feature settings window is unavailable");
        foreach (var option in originalOptions)
        {
            var control = window.Get(Mod + ":" + option.Key);
            Need(control?.toggle != null, "Missing feature toggle control " + option.Key);
            control.SetBool(enabled ?? option.Value, true);
        }
        var cargo = window.Get(Mod + ":" + CargoMode);
        Need(cargo?.dropdown != null && cargo.dropdown.options.Count > 1, "Missing cargo return mode control");
        int cargoMode = enabled.HasValue ? (enabled.Value ? 1 : 0) : originalCargoMode;
        cargo.SetIndex(cargoMode, true);
        window.Get("apply").button.onClick.Invoke();
        var config = ModConfig.Find(Mod);
        foreach (var option in originalOptions)
            Need((config.Get(option.Key) as ConfigEntry<bool>)?.Value == (enabled ?? option.Value), "Apply did not set " + option.Key);
        Need((config.Get(CargoMode) as ConfigEntry<int>)?.Value == cargoMode, "Apply did not set " + CargoMode);
        options.CloseUI();
    }

    private static void CheckPurchaseRouting(PlayerStore player)
    {
        var config = ModConfig.Find(Mod);
        var ordinary = (ConfigEntry<bool>)config.Get("f01.enabled");
        var wholesale = (ConfigEntry<bool>)config.Get("f01.wholesale");
        bool oldOrdinary = ordinary.Value, oldWholesale = wholesale.Value;
        bool oldNative = EmporiumOption.Instance.moveBoughtItemsToInventory;
        var oldClient = player.currentClientInstance;
        var observer = new Harmony("qa.purchase.scope." + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (bool native in new[] { false, true })
            foreach (bool keep in new[] { false, true })
            foreach (bool bulk in new[] { false, true })
            {
                EmporiumOption.Instance.moveBoughtItemsToInventory = native;
                ordinary.Set(keep); wholesale.Set(bulk);
                var item = Offer("wine_yeast");
                player.BuyItem(item);
                Need(GeneralHelper.IsItemOwned(item) && (item.parentInventory == EmporiumEntry.Instance.backInvinvElement) == (!native || keep),
                    "Ordinary purchase routing differs for native=" + native + ", ordinary=" + keep + ", wholesale=" + bulk);
                item.Destroy();
            }

            EmporiumOption.Instance.moveBoughtItemsToInventory = true;
            ordinary.Set(true); wholesale.Set(false);
            foreach (string tag in new[] { ScratcherHelper.SCRATCHER_TAG, LotteryHelper.LOTTERY_TICKET_TAG })
            {
                var ticket = Offer("wine_yeast");
                ticket.EnableTag(tag, false);
                Need(ticket.IsTag(tag), "Lottery fixture tag was not applied");
                player.BuyItem(ticket);
                Need(GeneralHelper.IsItemOwned(ticket) && ticket.parentInventory != EmporiumEntry.Instance.backInvinvElement,
                    "Lottery purchase did not use native routing for " + tag);
                ticket.Destroy();
            }

            var barterClient = StoreClientList.CreateJunkerSellOnly();
            barterClient.barterOffer = new BarterOffer { isFreeReward = true };
            player.currentClientInstance = StoreClientInstance.CreateClientInstance(barterClient);
            var previouslyOwned = EmporiumEntry.Instance.backInvinvElement.childItems.Where(GeneralHelper.IsItemOwned).ToArray();
            var barterItem = DirectoryMaster.Item("wine_yeast");
            GeneralHelper.SetItemOwned(barterItem, false);
            var slot = EmporiumEntry.Instance.frontInvinvElement.TryFindOneValidInventorySlot(barterItem);
            Need(slot != null && slot.TryAcceptOnce(barterItem.unitCount) == barterItem.unitCount, "Could not place barter offer");
            player.AcceptBarter();
            foreach (var item in previouslyOwned) GeneralHelper.SetItemOwned(item, true);
            Need(GeneralHelper.IsItemOwned(barterItem) && barterItem.parentInventory != EmporiumEntry.Instance.backInvinvElement,
                "Barter incorrectly entered the ordinary purchase scope");
            barterItem.Destroy();

            player.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
            var method = AccessTools.Method(typeof(PlayerStore), "MoveDealItemToInventory", new[] { typeof(GameItem) });
            Need(method != null, "Native deal transfer is unavailable");
            observer.Patch(method, prefix: new HarmonyMethod(typeof(RepeatedPurchaseScenario), nameof(NestedTransfer)));
            outer = Offer("system_module_quality"); inner = Offer("system_module_performance");
            special = Offer("wine_yeast"); GeneralHelper.SetItemOwned(special, true);
            hookSeen = false; throwTransfer = false;
            player.BuyItem(outer);
            Need(hookSeen && RetainedOnce(outer) && RetainedOnce(inner) && special.parentInventory != EmporiumEntry.Instance.backInvinvElement,
                "Nested purchase did not restore its enclosing scope or leaked it to a different deal");
            outer.Destroy(); inner.Destroy(); special.Destroy();

            outer = Offer("wine_yeast"); inner = special = null;
            hookSeen = false; throwTransfer = true;
            try { player.BuyItem(outer); throw new InvalidOperationException("Transfer fixture did not throw"); }
            catch (InvalidOperationException error) when (error.Message == "QA transfer failure") { }
            Need(hookSeen, "Transfer failure fixture did not run");
            throwTransfer = false;
            var failed = outer; outer = null;
            player.MoveDealItemToInventory(failed);
            Need(failed.parentInventory != EmporiumEntry.Instance.backInvinvElement,
                "Failed purchase leaked its transfer scope");
            failed.Destroy();
        }
        finally
        {
            observer.UnpatchAll(observer.Id);
            outer = inner = special = null; throwTransfer = hookSeen = false;
            ordinary.Set(oldOrdinary); wholesale.Set(oldWholesale);
            EmporiumOption.Instance.moveBoughtItemsToInventory = oldNative;
            player.currentClientInstance = oldClient;
        }
    }

    private static void NestedTransfer(PlayerStore __instance, GameItem item)
    {
        if (!ReferenceEquals(item, outer)) return;
        hookSeen = true;
        if (throwTransfer) throw new InvalidOperationException("QA transfer failure");
        __instance.BuyItem(inner);
        __instance.MoveDealItemToInventory(special);
    }
}

public sealed class AllFeaturesScenario : RepeatedPurchaseScenario { public AllFeaturesScenario() : base(true) { } }
