using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

// A fresh run exercises F34 quote cards and compares their estimates with native counter prices.
public sealed class CustomerQuoteScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 6);
    private GameItem meat, stack, bag, nested, excludedItem;
    private StoreClient buyer;
    private int step, callbackCount, meatEstimate, nestedEstimate, bagEstimate, stackEstimate, secondEstimate, cash;
    private int pages;
    private GameItem[] expectedItems;
    private int[] expectedPrices;
    private readonly HashSet<GameItem> found = new HashSet<GameItem>();
    private bool counterQuote, seekFirstPage;
    private float since;

    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".basket");
    private static object Field(object target, string name) => target?.GetType().GetField(name, Flags)?.GetValue(target);
    private static string Label(bool onCounter, int value) => ModHelper.GetLocalized(Mod, onCounter ? "f11.quoted" : "f11.estimated", value);

    private static int Quote(GameItem item, StoreClient client, bool expectedCounter)
    {
        Type type = ModLoader.Mods.First(m => m.Manifest?.ID == Mod).Instance.GetType().Assembly.GetType("ShopAssist.CustomerQuote", true);
        MethodInfo method = type.GetMethod("TryGet", Flags);
        object[] args = { item, client, 0, false };
        Need(method != null && (bool)method.Invoke(null, args), "No estimate for " + item.identifier);
        Need((bool)args[3] == expectedCounter, "Wrong estimate/counter origin for " + item.identifier);
        return (int)args[2];
    }

    private static string PrimitiveFields(object value)
    {
        if (value == null) return "<null>";
        return string.Join(",", value.GetType().GetFields(Flags).Where(f => !f.IsStatic &&
            (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string)))
            .OrderBy(f => f.Name).Select(f => f.Name + "=" + Convert.ToString(f.GetValue(value), CultureInfo.InvariantCulture)));
    }
    private static string Tags(TagSystem tags) => tags?.dict == null ? "<null>" : string.Join("|",
        tags.dict.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + PrimitiveFields(pair.Value)));
    private static string ItemSnapshot(GameItem item) => string.Join(";", item.uniqueId, item.unitCount, item.unitValue,
        item.lateUnitValue, item.unitBaseValue, item.backupUnitValue, Tags(item.state), Tags(item.modifiedState),
        string.Join("|", item.itemFeatures.Select(f => PrimitiveFields(f))));

    private GameItem Make(string id, string name, GameInventory target)
    {
        GameItem item = DirectoryMaster.Item(id);
        Need(item != null, "Missing fixture " + id);
        GeneralHelper.SetItemOwned(item, true); GeneralHelper.SetCustomName(item, name + " " + token);
        Move(item, target);
        return item;
    }
    private static void Move(GameItem item, GameInventory destination)
    {
        var slot = destination.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == destination && slot.targetItem == null && slot.numTransfer >= item.unitCount &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, destination),
            "Could not move fixture " + item.identifier + " × " + item.unitCount);
    }
    private void Advance(int next) { step = next; since = Time.realtimeSinceStartup; }
    private void BeginCards(GameItem[] items, int[] prices, bool onCounter, GameItem excluded = null)
    {
        Need(items.Length == prices.Length && items.Length > 0, "Invalid quote card expectations");
        RoutingScenario.RunFeatureAction("f34.open");
        var list = Window();
        Need(list?.IsAlive == true && list.Get("compare")?.button != null, "F34 selection did not open");
        expectedItems = items; expectedPrices = prices; counterQuote = onCounter; excludedItem = excluded;
        found.Clear(); pages = 0; seekFirstPage = true;
    }
    private bool CheckCards()
    {
        var list = Window();
        Need(list?.IsAlive == true, "F34 selection closed while checking quotes");
        Need(++pages <= 1024, "F34 pagination did not reach the expected cards");
        Need(excludedItem == null || QaItemUi.Hit(list.root, excludedItem) == null,
            "F34 selection included the important backpack");
        for (int i = 0; i < expectedItems.Length; i++)
        {
            var item = expectedItems[i];
            if (found.Contains(item)) continue;
            var hit = QaItemUi.Hit(list.root, item);
            if (hit == null) continue;
            var entry = Field(hit, "entry");
            string actual = Field(entry, "QuoteLabel") as string, expected = Label(counterQuote, expectedPrices[i]);
            Need(Field(entry, "Enabled") is bool enabled && enabled && actual == expected &&
                hit.transform.parent?.parent?.GetComponentsInChildren<TMP_Text>(true)
                    .Any(text => text.isActiveAndEnabled && text.text == expected) == true,
                item.identifier + " F34 price: expected=" + expected + ", actual=" + actual);
            found.Add(item);
        }
        if (found.Count == expectedItems.Length && excludedItem == null) return true;
        if (seekFirstPage)
        {
            var previous = list.Get("previous")?.button;
            if (previous != null && previous.interactable) { previous.onClick.Invoke(); return false; }
            seekFirstPage = false;
        }
        var next = list.Get("next")?.button;
        if (next == null || !next.interactable)
        {
            Need(found.Count == expectedItems.Length, "F34 selection omitted quote cards: " +
                string.Join(", ", expectedItems.Where(item => !found.Contains(item)).Select(item => item.identifier)));
            return true;
        }
        next.onClick.Invoke();
        return false;
    }
    private void CheckCounter(GameItem item, int estimate)
    {
        Move(item, EmporiumEntry.Instance.backInvinvElement);
        int native = checked((int)item.GetNegociatedValue());
        Need(native == estimate && Quote(item, buyer, true) == native,
            item.identifier + " estimated=" + estimate + " native=" + native + " count=" + item.unitCount);
        Need(PlayerStore.Instance.playerCash == cash, "A quote or counter move completed a transaction");
    }

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Customer quote QA timed out at step " + step);
        if (step == 0) { if (start.Tick()) Advance(1); return false; }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true || StoreUIManager.Instance?.gameOptionUIManager == null ||
                NegociationUIManager.Instance == null) return false;
            TradeScenario.SetOptions(true, "f34.enabled");
            TradeScenario.SetOptions(false, "f03.enabled", "f11.purchase");
            meat = Make("processed_meat", "QA quote meat", e.invElement);
            bag = Make("backpack_medium", "QA quote bag", e.invElement);
            var inside = bag.contentWindow?.childElement as GameGridInventory;
            Need(inside != null, "QA container has no item grid");
            nested = Make("cup_noodle", "QA quote nested", inside);
            bag.contentWindow.Hide();
            stack = DirectoryMaster.Item("wine_yeast");
            Need(stack != null, "Missing native stack fixture");
            stack.SetStack(10); GeneralHelper.SetItemOwned(stack, true); GeneralHelper.SetCustomName(stack, "QA quote stack " + token);
            QaNativeStackFixture.PutWhole(e.invElement, stack);
            buyer = StoreClientList.CreateSpacerClientFood(); buyer.SetBudget(50000); buyer.isIntroduced = true;
            buyer.clientBuyingIdList.Add(bag.identifier); buyer.clientBuyingIdList.Add(stack.identifier);
            Need(bag.IsTag("IMPORTANT_TAG") && buyer.IsClientBuyingThisItem(bag),
                "The native backpack fixture must be important and accepted by the buyer");
            buyer.onItemAddedToWeighted += _ => callbackCount++;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer); p.isClientArrived = true;
            NegociationUIManager.Instance.OpenUI();
            Need(NegociationUIManager.Instance.IsOpen, "Native negotiation UI did not open");
            cash = p.playerCash;
            var items = new[] { meat, bag, nested, stack };
            string[] before = items.Select(ItemSnapshot).ToArray();
            GameInventory[] parents = items.Select(item => item.parentInventory).ToArray();
            ItemFeature[][] features = items.Select(item => item.itemFeatures.ToArray()).ToArray();
            int unique = p.currentUniqueId, budget = buyer.GetBudget(), callbacks = callbackCount;
            meatEstimate = Quote(meat, buyer, false); bagEstimate = Quote(bag, buyer, false);
            nestedEstimate = Quote(nested, buyer, false); stackEstimate = Quote(stack, buyer, false);
            foreach (GameItem item in items) Quote(item, buyer, false);
            Need(p.currentUniqueId == unique && p.playerCash == cash && buyer.GetBudget() == budget && callbackCount == callbacks &&
                items.Select(ItemSnapshot).SequenceEqual(before) && items.Select(item => item.parentInventory).SequenceEqual(parents) &&
                items.Select((item, index) => item.itemFeatures.SequenceEqual(features[index])).All(same => same),
                "A warehouse estimate changed item data, global IDs, budget, cash, parent, features or invoked a customer callback");
            Advance(2); return false;
        }
        if (step == 2)
        {
            if (Time.realtimeSinceStartup - since < .8f) return false;
            BeginCards(new[] { meat, nested, stack }, new[] { meatEstimate, nestedEstimate, stackEstimate }, false, bag);
            Advance(20); return false;
        }
        if (step == 20)
        {
            if (!CheckCards()) return false;
            Need(!bag.contentWindow.IsVisible() && stack.unitCount == 10, "F34 opened the bag or changed the stack");
            int before = callbackCount;
            CheckCounter(meat, meatEstimate);
            Need(callbackCount > before, "Real counter placement did not invoke the native customer callback fixture");
            Advance(3); return false;
        }
        if (step == 3)
        {
            if (Time.realtimeSinceStartup - since < .8f) return false;
            BeginCards(new[] { meat }, new[] { meatEstimate }, true); Advance(30); return false;
        }
        if (step == 30)
        {
            if (!CheckCards()) return false;
            Move(meat, e.invElement); CheckCounter(bag, bagEstimate); Advance(4); return false;
        }
        if (step == 4)
        {
            if (Time.realtimeSinceStartup - since < .8f) return false;
            Need(ReferenceEquals(bag.parentInventory, e.backInvinvElement) && buyer.IsClientBuyingThisItem(bag) &&
                Quote(bag, buyer, true) == bagEstimate && bag.GetNegociatedValue() == bagEstimate,
                "The accepted important backpack's native counter quote changed");
            Move(bag, e.invElement); CheckCounter(nested, nestedEstimate); Advance(5); return false;
        }
        if (step == 5)
        {
            if (Time.realtimeSinceStartup - since < .8f) return false;
            BeginCards(new[] { nested }, new[] { nestedEstimate }, true); Advance(50); return false;
        }
        if (step == 50)
        {
            if (!CheckCards()) return false;
            Move(nested, (GameGridInventory)bag.contentWindow.childElement);
            // Native desk slots accept one unit. Vary the count on the intact QA stack,
            // then compare the same ten-unit object after native counter placement.
            stack.SetStack(1); Move(stack, e.backInvinvElement); stack.SetStack(10);
            int native = checked((int)stack.GetNegociatedValue());
            Need(native == stackEstimate && Quote(stack, buyer, true) == native,
                "Stacked quote did not match the ten-unit native counter: estimated=" + stackEstimate + " native=" + native);
            Advance(6); return false;
        }
        if (step == 6)
        {
            if (Time.realtimeSinceStartup - since < .8f) return false;
            BeginCards(new[] { stack }, new[] { stackEstimate }, true); Advance(60); return false;
        }
        if (step == 60)
        {
            if (!CheckCards()) return false;
            stack.SetStack(1); Move(stack, e.invElement); stack.SetStack(10);
            NegociationUIManager.Instance.CloseUI();
            buyer = StoreClientList.CreateSpacerClientFood(); buyer.SetBudget(50000); buyer.isIntroduced = true; buyer.perfumeInfluence = 2;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer); p.isClientArrived = true;
            NegociationUIManager.Instance.OpenUI();
            secondEstimate = Quote(meat, buyer, false);
            Need(secondEstimate != meatEstimate, "The second customer's native modifier did not change this fixture's price");
            Advance(7); return false;
        }
        if (step == 7)
        {
            if (Time.realtimeSinceStartup - since < .8f) return false;
            BeginCards(new[] { meat }, new[] { secondEstimate }, false); Advance(70); return false;
        }
        if (step == 70)
        {
            if (!CheckCards()) return false;
            CheckCounter(meat, secondEstimate); Advance(8); return false;
        }
        if (step == 8)
        {
            if (Time.realtimeSinceStartup - since < .8f) return false;
            BeginCards(new[] { meat }, new[] { secondEstimate }, true); Advance(80); return false;
        }
        if (step == 80)
        {
            if (!CheckCards()) return false;
            result = "PASS F34 paginated estimates and native counter prices, nested contents, stack quantity, customer change and side-effect-free preview";
            return true;
        }
        throw new InvalidOperationException("Unknown customer quote QA step " + step);
    }
}
