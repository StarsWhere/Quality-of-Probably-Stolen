using System;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

// Purchases and routing occur solely in a new isolated save through native game APIs.
public sealed class CounterRoutingScenario
{
    private const string Mod = "starswhere.shopassist", Preview = Mod + ".route_preview";
    private readonly QaNewRun start = new QaNewRun();
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 8);
    private GameItemElement bag, paid, seed, unpriced, unpaid;
    private GameGridInventory target;
    private QaLoadSlot load;
    private string run;
    private int slot, day, balance, price, step;
    private float since = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 75) throw new InvalidOperationException("Counter routing QA timed out at step " + step + ": " + Status());
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                Advance(1); return false;
            case 1:
                if (StoreUIManager.Instance?.gameOptionUIManager == null || EmporiumEntry.Instance?.backInvinvElement == null ||
                    ItemMouseDoubleClickHandler.current == null || ItemMultiSelectHandler.current == null ||
                    NegociationUIManager.Instance == null || StoreUIManager.Instance.clientGameObject == null) return false;
                Prepare();
                Advance(2); return false;
            case 2:
                RoutingScenario.CreateFoodRule(bag);
                BeginCustomer();
                var player = PlayerStore.Instance;
                int before = player.playerCash;
                int paidPrice = checked((int)paid.GetNegociatedValue());
                int seedPrice = checked((int)seed.GetNegociatedValue());
                Require(paidPrice > 0 && seedPrice > 0 && before >= paidPrice + seedPrice, "Fixture purchase prices are invalid");
                player.BuyItem(paid);
                player.BuyItem(seed);
                price = paidPrice;
                Require(player.playerCash == before - paidPrice - seedPrice && paid.IsTag("BOUGHT_PRICE_TAG") &&
                    paid.GetTagReadonly("BOUGHT_PRICE_TAG").GetInt() == paidPrice && GeneralHelper.IsItemOwned(paid) &&
                    paid.parentInventory == EmporiumEntry.Instance.backInvinvElement, "F01 did not retain the purchased native instance");
                Put(EmporiumEntry.Instance.backInvinvElement, unpaid);
                OpenPreview(paid, seed, unpriced, unpaid);
                RequireNoMoves("The active seller or unbought goods were accepted by F08");
                RoutingScenario.Window(Preview).Close();
                EndCustomer();
                Advance(3); return false;
            case 3:
                if (!NoCustomer()) return false;
                Require(Allowed(paid) && !Allowed(seed) && !Allowed(unpriced), "Settlement evidence mismatch after departure: " + Status());
                slot = PlayerStore.Instance.saveSlotId; run = PlayerStore.Instance.runID; day = StoreStation.GetDayCounter();
                PlayerStore.Instance.SaveGame();
                StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                load = new QaLoadSlot(slot, Guid.Parse(run));
                Advance(4); return false;
            case 4:
                if (!load.Tick()) return false;
                if (!NoCustomer()) return false;
                RestoreFixtures();
                Require(Allowed(paid) && !Allowed(seed) && !Allowed(unpriced), "Saved purchase evidence did not revalidate: " + Status());
                OpenPreview(paid, seed, unpriced);
                RequireHasOneMove();
                BeginCustomer();
                RoutingScenario.Button(RoutingScenario.Window(Preview), "f08.apply").onClick.Invoke();
                Require(paid.parentInventory == EmporiumEntry.Instance.backInvinvElement && !target.childItems.Contains(paid),
                    "A new customer did not invalidate the frozen counter plan");
                EndCustomer();
                Advance(5); return false;
            case 5:
                if (!NoCustomer()) return false;
                Require(Allowed(paid), "Paid item is not eligible after the second customer left: " + Status());
                OpenPreview(paid, seed, unpriced);
                RequireHasOneMove();
                balance = PlayerStore.Instance.playerCash;
                int itemCount = paid.unitCount;
                PlayerStore.Instance.currentNegociatedItem = paid;
                RoutingScenario.Button(RoutingScenario.Window(Preview), "f08.apply").onClick.Invoke();
                Require(paid.parentInventory == target && target.childItems.Contains(paid) && paid.unitCount == itemCount &&
                    GeneralHelper.IsItemOwned(paid) && paid.GetTagReadonly("BOUGHT_PRICE_TAG").GetInt() == price &&
                    PlayerStore.Instance.playerCash == balance && PlayerStore.Instance.currentNegociatedItem != paid,
                    "F08 did not move the exact paid item or failed to run the native counter removal callback");
                Require(seed.parentInventory == EmporiumEntry.Instance.backInvinvElement &&
                    unpriced.parentInventory == EmporiumEntry.Instance.backInvinvElement,
                    "F08 moved a special or unpriced counter item");
                result = "PASS native F01 purchase evidence and same-day reload; active/new customer, unpriced and special goods rejected; settled F08 invokes native counter removal";
                return true;
            default: throw new InvalidOperationException("Unknown counter routing QA step");
        }
    }

    private void Prepare()
    {
        TradeScenario.SetOptions(true, "f01.enabled", "f07.enabled", "f08.enabled", "f09.enabled");
        var options = StoreUIManager.Instance.gameOptionUIManager;
        options.OpenUI();
        var mode = CustomUIManager.Instance.GetWindow("game_options").Get(Mod + ":f09.mode");
        Require(mode?.dropdown != null, "Preview mode control missing");
        mode.SetIndex(0, true);
        CustomUIManager.Instance.GetWindow("game_options").Get("apply").button.onClick.Invoke();
        options.CloseUI();
        EmporiumOption.Instance.autoCallNext = false;
        bag = New("backpack_medium", "QA Counter Bag " + token, true);
        paid = New("processed_meat", "QA Bought " + token, false);
        seed = New("nutrifruit_seed", "QA Seed " + token, false);
        unpriced = New("processed_milk", "QA Unpriced " + token, true);
        unpaid = New("processed_juice", "QA Unpaid " + token, false);
        var entry = EmporiumEntry.Instance;
        Put(entry.invElement, bag);
        Put(entry.backInvinvElement, paid); Put(entry.backInvinvElement, seed); Put(entry.backInvinvElement, unpriced);
        target = bag.contentWindow?.childElement as GameGridInventory;
        Require(target != null && !unpriced.IsTag("BOUGHT_PRICE_TAG"), "Invalid destination or unpriced fixture");
        entry.backInvWindow.Show();
        ItemMouseDoubleClickHandler.current.OpenContentAction(bag, Vector2.zero);
    }

    private static void BeginCustomer()
    {
        var player = PlayerStore.Instance;
        Require(player.currentClientInstance == null, "Unexpected existing customer");
        player.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
        StoreUIManager.Instance.QueueClientArriveAnimation();
        StoreUIManager.Instance.clientGameObject.SetImmediatlyArrive();
        player.isClientArrived = true;
    }
    private static void EndCustomer()
    {
        NegociationUIManager.Instance.CloseUI();
        BargainUIManager.Instance?.CloseUI();
        PlayerStore.Instance.DismissCurrentClient(callNextWhenPossible: false, right: false);
    }
    private static bool NoCustomer()
    {
        var player = PlayerStore.Instance;
        var ui = StoreUIManager.Instance;
        return player != null && player.currentClientInstance == null && player.lockNextBellFlag == 0 && !player.callNextWhenPossible &&
            ui?.clientGameObject != null && !ui.clientGameObject.gameObject.activeInHierarchy &&
            NegociationUIManager.Instance != null && !NegociationUIManager.Instance.IsOpen &&
            (BargainUIManager.Instance == null || !BargainUIManager.Instance.IsOpen) &&
            DialogUIManager.Instance != null && DialogUIManager.Instance.GetCurrentDialog() == null;
    }
    private void RestoreFixtures()
    {
        var entry = EmporiumEntry.Instance;
        bag = Find(entry.invElement, "QA Counter Bag ");
        paid = Find(entry.backInvinvElement, "QA Bought ");
        seed = Find(entry.backInvinvElement, "QA Seed ");
        unpriced = Find(entry.backInvinvElement, "QA Unpriced ");
        target = bag.contentWindow?.childElement as GameGridInventory;
        Require(target != null && StoreStation.GetDayCounter() == day, "Reloaded target or day differs");
        entry.backInvWindow.Show();
    }
    private GameItemElement Find(GameInventory inventory, string prefix)
    {
        var item = inventory.childItems.SingleOrDefault(value => value.GetDisplayName(true) == prefix + token) as GameItemElement;
        Require(item != null, "Missing reloaded fixture " + prefix);
        return item;
    }
    private static GameItemElement New(string id, string name, bool own)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement;
        Require(item != null, "Missing native fixture " + id);
        GeneralHelper.SetItemOwned(item, own); GeneralHelper.SetCustomName(item, name);
        return item;
    }
    private static void Put(GameInventory inventory, GameItem item)
    {
        var marker = inventory.TryFindOneValidInventorySlot(item);
        Require(marker != null && marker.inventory == inventory && marker.targetItem == null && marker.numTransfer >= item.unitCount &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory, "Could not place fixture " + item.identifier);
    }
    private static void OpenPreview(params GameItemElement[] items)
    {
        RoutingScenario.SetSelection(items);
        RoutingScenario.OpenOptionAction("f08.open");
    }
    private static void RequireNoMoves(string error)
    {
        var window = RoutingScenario.Window(Preview);
        var apply = RoutingScenario.Button(window, "f08.apply");
        Require(apply != null && !apply.interactable, error);
    }
    private void RequireHasOneMove()
    {
        var window = RoutingScenario.Window(Preview);
        var apply = RoutingScenario.Button(window, "f08.apply");
        string text = string.Join(" ", window.root.GetComponentsInChildren<TMP_Text>(true).Select(value => value.text));
        Require(apply != null && apply.interactable && text.Contains(bag.GetDisplayName(true)) &&
            text.Contains(seed.GetDisplayName(true)) && text.Contains(unpriced.GetDisplayName(true)),
            "F09 did not show one eligible target and the rejected goods: " + text);
    }
    private static bool Allowed(GameItem item)
    {
        var mod = ModLoader.Mods.Find(value => value.Manifest != null && value.Manifest.ID == Mod);
        var type = mod?.Instance?.GetType().Assembly.GetType("ShopAssist.SettledCounterEvidence");
        var method = type?.GetMethod("Allows", BindingFlags.Static | BindingFlags.NonPublic);
        Require(method != null, "Counter settlement guard missing");
        return (bool)method.Invoke(null, new object[] { item });
    }
    private static string Status()
    {
        var p = PlayerStore.Instance; var ui = StoreUIManager.Instance;
        return "client=" + (p?.currentClientInstance != null) + " flag=" + p?.lockNextBellFlag + " next=" + p?.callNextWhenPossible +
            " visible=" + ui?.clientGameObject?.gameObject.activeInHierarchy + " nego=" + NegociationUIManager.Instance?.IsOpen +
            " dialog=" + (DialogUIManager.Instance?.GetCurrentDialog() != null);
    }
    private void Advance(int value) { step = value; since = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
