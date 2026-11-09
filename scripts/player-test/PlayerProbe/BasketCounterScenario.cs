using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class BasketCounterScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150;
    private readonly HashSet<GameItem> seen = new HashSet<GameItem>();
    private int step, waitFrame, cash, candidatePrice, pages;
    private GameItem resident, refused, candidate, blocker;
    private GridShape residentShape, refusedShape;
    private StoreClient buyer;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".basket");
    private static object Field(object owner, string name) => owner?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(owner);
    private static object BasketOwner() => typeof(ModLoader).Assembly.GetType("ShopAssist.CustomerBasket")?.GetField("active", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) ??
        AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("ShopAssist.CustomerBasket")).First(type => type != null)
            .GetField("active", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
    private static GameItem Put(string id, GameInventory grid, string name)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing basket fixture " + id);
        GeneralHelper.SetItemOwned(item); GeneralHelper.SetCustomName(item, name); Move(item, grid); return item;
    }
    private static void Move(GameItem item, GameInventory grid)
    {
        var marker = grid.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.targetItem == null && marker.numTransfer >= item.unitCount && marker.TryAcceptOnce(item.unitCount) == item.unitCount,
            "Cannot place basket fixture " + item.identifier);
    }
    private static Button Compare()
    {
        RoutingScenario.RunFeatureAction("f34.open");
        var window = Window(); Need(window?.Get("compare")?.button != null, "Basket compare is absent");
        window.Get("limit").SetString("9000", true);
        window.Get("compare").button.onClick.Invoke();
        return Window().root.GetComponentsInChildren<Button>(true).FirstOrDefault(button => button.gameObject.activeInHierarchy &&
            button.GetComponentInChildren<TMP_Text>(true)?.text == ModHelper.GetLocalized(Mod, "f34.place"));
    }
    private bool SelectCandidate()
    {
        var window = Window();
        var hits = window.root.GetComponentsInChildren<MonoBehaviour>(true)
            .Where(hit => hit != null && hit.gameObject.activeInHierarchy && hit.gameObject.name == "item_cell" &&
                hit.GetType().FullName == "ShopAssist.ItemGrid+Hit")
            .GroupBy(hit => Field(Field(hit, "entry"), "Item") as GameItem).Select(group => group.First()).ToArray();
        Need(hits.Length > 0 && ++pages <= 512, "Basket pagination did not reach its end");
        foreach (var hit in hits)
        {
            var itemEntry = Field(hit, "entry"); var item = Field(itemEntry, "Item") as GameItem;
            Need(item != null && seen.Add(item), "Basket pagination repeated or omitted an item");
            bool enabled = Field(itemEntry, "Enabled") is bool isEnabled && isEnabled;
            bool selected = Field(itemEntry, "Selected") is bool isSelected && isSelected;
            if (ReferenceEquals(item, candidate))
            {
                Need(enabled && selected && int.TryParse(Field(itemEntry, "Badge") as string, out candidatePrice) && candidatePrice > 0,
                    "Candidate was not selected with a positive quote");
            }
            else if (!ReferenceEquals(item, resident) && enabled && selected)
            {
                QaItemUi.Click(hit);
                Need(Field(itemEntry, "Selected") is bool deselected && !deselected, "Other quoted item could not be excluded");
            }
        }
        var next = window.Get("next")?.button;
        if (next != null && next.interactable) { next.onClick.Invoke(); return false; }
        Need(seen.Contains(candidate), "Candidate missing from basket selection");
        return true;
    }
    private void CheckPreview()
    {
        var window = Window();
        var board = window.root.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(node => node.name == "ShopAssist_CounterPreview" && node.gameObject.activeInHierarchy);
        Need(board != null, "Counter preview board missing");
        var accepted = QaItemUi.Hit(board.gameObject, resident);
        var rejected = QaItemUi.Hit(board.gameObject, refused);
        var offered = QaItemUi.Hit(board.gameObject, candidate);
        Need(accepted != null && rejected != null && offered != null, "Counter preview omitted an existing or offered item");
        var acceptedEntry = Field(accepted, "entry"); var rejectedEntry = Field(rejected, "entry"); var offeredEntry = Field(offered, "entry");
        Need(Field(acceptedEntry, "Enabled") is bool acceptedEnabled && acceptedEnabled &&
            Field(acceptedEntry, "Selected") is bool acceptedSelected && !acceptedSelected &&
            Field(rejectedEntry, "Enabled") is bool rejectedEnabled && !rejectedEnabled &&
            Field(rejectedEntry, "Selected") is bool rejectedSelected && !rejectedSelected && Field(rejectedEntry, "QuoteLabel") == null &&
            Field(offeredEntry, "Enabled") is bool offeredEnabled && offeredEnabled &&
            Field(offeredEntry, "Selected") is bool offeredSelected && offeredSelected,
            "Preview did not distinguish accepted, refused and offered items");
        var acceptedShape = Field(accepted, "shownShape") as GridShape;
        var rejectedShape = Field(rejected, "shownShape") as GridShape;
        var offeredShape = Field(offered, "shownShape") as GridShape;
        Need(acceptedShape != null && rejectedShape != null && offeredShape != null &&
            ItemShapeSame(acceptedShape, residentShape) && ItemShapeSame(rejectedShape, refusedShape) &&
            !offeredShape.Intersects(residentShape) && !offeredShape.Intersects(refusedShape),
            "Preview changed existing positions or overlapped the proposed item");
        int residentPrice = checked((int)resident.GetNegociatedValue());
        int total = checked(residentPrice + candidatePrice);
        Need(residentPrice > 0 && total <= 9000 &&
            Field(acceptedEntry, "QuoteLabel") as string == ModHelper.GetLocalized(Mod, "f11.quoted", residentPrice) &&
            Field(offeredEntry, "QuoteLabel") as string == ModHelper.GetLocalized(Mod, "f11.estimated", candidatePrice) &&
            window.root.GetComponentsInChildren<TMP_Text>(true).Any(label => label.gameObject.activeInHierarchy &&
                label.text == ModHelper.GetLocalized(Mod, "f34.summary", resident.unitCount + candidate.unitCount, total, 9000 - total)),
            "Displayed total did not match the accepted and offered quotes");
        Need(candidate.parentInventory == EmporiumEntry.Instance.invElement && resident.parentInventory == EmporiumEntry.Instance.backInvinvElement &&
            refused.parentInventory == EmporiumEntry.Instance.backInvinvElement && PlayerStore.Instance.playerCash == cash,
            "Preview moved goods or completed a sale");
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Basket counter scenario timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance; var player = PlayerStore.Instance;
        if (step == 1)
        {
            var config = ModConfig.Find(Mod);
            ((ConfigEntry<bool>)config.Get("f34.enabled")).Set(true);
            ((ConfigEntry<bool>)config.Get("f34.move")).Set(true);
            ((ConfigEntry<bool>)config.Get("f07.enabled")).Set(false);
            buyer = StoreClientList.CreateSpacerClientFood(); buyer.SetBudget(9000); buyer.isIntroduced = true;
            player.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer); player.isClientArrived = true;
            typeof(GameGridInventory).GetProperty("inventoryShape").GetSetMethod(true)
                .Invoke(entry.backInvinvElement, new object[] { new GridShapeBuilder(8, 4).SetDataFill(0).SetDataOutside(1).Build() });
            entry.backInvinvElement.Validate();
            resident = Put("cup_noodle", entry.backInvinvElement, "QA retained accepted food");
            refused = Put("processed_meat", entry.backInvinvElement, "QA retained refused food");
            refused.EnableTag("NOT_FOR_RESALE");
            candidate = Put("processed_juice", entry.invElement, "QA additional juice");
            residentShape = resident.modifiedShape.Clone(); refusedShape = refused.modifiedShape.Clone();
            Need(buyer.IsClientBuyingThisItem(resident) && !buyer.IsClientBuyingThisItem(refused), "Acceptance fixtures are invalid");
            NegociationUIManager.Instance.OpenUI(); cash = player.playerCash; waitFrame = Time.frameCount + 3; step++; return false;
        }
        if (step == 2)
        {
            if (Time.frameCount < waitFrame) return false;
            RoutingScenario.RunFeatureAction("f34.open"); step++; return false;
        }
        if (step == 3)
        {
            if (!SelectCandidate()) return false;
            Button place = Compare(); Need(place != null, "No spatially feasible basket was offered");
            CheckPreview();
            blocker = Put("cup_noodle", entry.backInvinvElement, "QA late blocker");
            place.onClick.Invoke();
            Need(candidate.parentInventory == entry.invElement && Field(BasketOwner(), "placing") == null,
                "A changed counter was staged from an old spatial preview");
            Move(blocker, entry.invElement); blocker.EnableTag("NOT_FOR_RESALE");
            NegociationUIManager.Instance.RefreshUI(); step++; return false;
        }
        if (step == 4)
        {
            Button place = Compare(); Need(place != null, "Fresh basket is absent");
            var original = entry.backInvinvElement.onSlotAddItemEndFunc;
            bool called = false;
            entry.backInvinvElement.onSlotAddItemEndFunc += (item, inventory, marker) => called = true;
            place.onClick.Invoke();
            Need(!called && candidate.parentInventory == entry.invElement && Field(BasketOwner(), "placing") == null,
                "An unknown counter callback was invoked by automatic staging");
            entry.backInvinvElement.onSlotAddItemEndFunc = original;
            place = Compare(); Need(place != null, "Basket unavailable after restoring native callbacks");
            place.onClick.Invoke();
            object owner = BasketOwner();
            bool safe = (bool)owner.GetType().GetMethod("SafeCounter", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(owner, new object[] { entry.backInvinvElement });
            Need(Field(owner, "placing") != null, "Native counter staging was unexpectedly rejected; safe=" + safe + "; notice=" +
                GameObject.Find(Mod + ".notice")?.GetComponentInChildren<TMP_Text>()?.text + "; callbacks=" +
                string.Join(";", typeof(GameGridInventory).GetProperties().Where(property => typeof(Delegate).IsAssignableFrom(property.PropertyType))
                    .Select(property => property.Name + "=" + string.Join(",", ((Delegate)property.GetValue(entry.backInvinvElement))?.GetInvocationList()
                        .Select(callback => callback.Method.DeclaringType + "." + callback.Method.Name) ?? Array.Empty<string>()))) + "; client=" +
                string.Join(";", typeof(StoreClient).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(field => field.GetValue(buyer) is Delegate).Select(field => field.Name + "=" +
                        string.Join(",", ((Delegate)field.GetValue(buyer)).GetInvocationList().Select(callback => callback.Method.DeclaringType + "." + callback.Method.Name)))) +
                "; mutation=" + owner.GetType().Assembly.GetType("ShopAssist.InventoryAutoInsert").GetMethod("KnownMutationPatches", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null));
            step++; return false;
        }
        if (step == 5)
        {
            if (Field(BasketOwner(), "placing") != null) return false;
            Need(candidate.parentInventory == entry.backInvinvElement && resident.parentInventory == entry.backInvinvElement &&
                refused.parentInventory == entry.backInvinvElement && ItemShapeSame(resident.modifiedShape, residentShape) &&
                ItemShapeSame(refused.modifiedShape, refusedShape) && player.playerCash == cash,
                "Staging changed existing goods, omitted the candidate or completed a sale");
            var sellable = (System.Collections.Generic.List<GameItem>)typeof(NegociationUIManager)
                .GetMethod("GetSellableBackInvItems", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { buyer });
            int nativeTotal = (int)typeof(NegociationUIManager).GetMethod("SumNegociatedValue", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { sellable });
            Need(sellable.Contains(resident) && sellable.Contains(candidate) && !sellable.Contains(refused) &&
                candidate.GetNegociatedValue() == candidatePrice &&
                nativeTotal == resident.GetNegociatedValue() + candidate.GetNegociatedValue(), "Basket and transaction total differ");
            Window()?.Close(); NegociationUIManager.Instance.CloseUI();
            waitFrame = Time.frameCount + 1; step++; return false;
        }
        if (step == 6)
        {
            if (Time.frameCount < waitFrame) return false;
            Need(!NegociationUIManager.Instance.IsOpen, "Native negotiation remained open after the scenario");
            result = "PASS F34 actual counter geometry, accepted baseline, refused occupancy, changed space and callback rejection, native quote total and staging without sale";
            return true;
        }
        return false;
    }
    private static bool ItemShapeSame(GridShape first, GridShape second) => first.minX == second.minX && first.minY == second.minY &&
        first.width == second.width && first.height == second.height && first.orientation == second.orientation && first.flipped == second.flipped;
}
