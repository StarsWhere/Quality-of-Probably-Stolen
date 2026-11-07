using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

// A fresh run exercises the actual F11 cards and compares their estimates with native counter prices.
public sealed class CustomerQuoteScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 6);
    private GameItem meat, stack, bag, nested;
    private StoreClient buyer;
    private int step, callbackCount, meatEstimate, nestedEstimate, bagEstimate, stackEstimate, secondEstimate, cash;
    private float since;

    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".customer_inventory");
    private static object Field(object target, string name) => target?.GetType().GetField(name, Flags)?.GetValue(target);
    private static string CardQuote(GameItem item)
    {
        var hit = Window() == null ? null : QaItemUi.Hit(Window().root, item);
        return Field(Field(hit, "entry"), "QuoteLabel") as string;
    }
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
    private void OpenList()
    {
        RoutingScenario.RunFeatureAction("f11.open");
        var list = Window();
        var demand = list?.Get("demand")?.button;
        Need(list?.IsAlive == true && demand != null && demand.interactable, "F11 demand mode is unavailable");
        demand.onClick.Invoke();
    }
    private void RequireCard(GameItem item, int price, bool counter)
    {
        string actual = CardQuote(item), expected = Label(counter, price);
        Need(actual == expected, item.identifier + " F11 price: expected=" + expected + ", actual=" + actual);
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
            TradeScenario.SetOptions(true, "f11.enabled");
            TradeScenario.SetOptions(false, "f03.enabled", "f34.enabled", "f11.purchase");
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
            OpenList(); Advance(2); return false;
        }
        if (step == 2)
        {
            if (Time.realtimeSinceStartup - since < .8f) return false;
            RequireCard(meat, meatEstimate, false); RequireCard(bag, bagEstimate, false);
            RequireCard(nested, nestedEstimate, false); RequireCard(stack, stackEstimate, false);
            Need(!bag.contentWindow.IsVisible() && stack.unitCount == 10, "F11 opened the bag or changed the stack");
            int before = callbackCount;
            CheckCounter(meat, meatEstimate);
            Need(callbackCount > before, "Real counter placement did not invoke the native customer callback fixture");
            Advance(3); return false;
        }
        if (step == 3)
        {
            if (Time.realtimeSinceStartup - since < .8f) return false;
            RequireCard(meat, meatEstimate, true);
            Move(meat, e.invElement); CheckCounter(bag, bagEstimate); Advance(4); return false;
        }
        if (step == 4)
        {
            if (Time.realtimeSinceStartup - since < .8f) return false;
            RequireCard(bag, bagEstimate, true);
            Move(bag, e.invElement); CheckCounter(nested, nestedEstimate); Advance(5); return false;
        }
        if (step == 5)
        {
            if (Time.realtimeSinceStartup - since < .8f) return false;
            RequireCard(nested, nestedEstimate, true);
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
            RequireCard(stack, stackEstimate, true);
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
            OpenList(); RequireCard(meat, secondEstimate, false); CheckCounter(meat, secondEstimate); Advance(8); return false;
        }
        if (Time.realtimeSinceStartup - since < .8f) return false;
        RequireCard(meat, secondEstimate, true);
        result = "PASS F11 warehouse estimates and native counter prices, nested contents, stack quantity, customer change and side-effect-free preview";
        return true;
    }
}
