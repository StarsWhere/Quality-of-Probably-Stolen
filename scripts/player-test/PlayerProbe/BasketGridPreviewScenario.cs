using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class BasketGridPreviewScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150;
    private int step, waitFrame, cash, foreignCalls;
    private GameItem resident, refused, candidate, blocker;
    private GridShape residentShape, refusedShape, plannedShape;
    private StoreClient buyer;
    private StoreEvent[] originalEvents;
    private StoreEvent cosmetic;
    private string affectedType;

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static object Field(object owner, string name) => owner?.GetType().GetField(name, Fields)?.GetValue(owner);
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".basket");
    private static object Owner() => ModLoader.Mods.Single(mod => mod.Manifest?.ID == Mod).Instance.GetType().Assembly
        .GetType("ShopAssist.CustomerBasket", true).GetField("active", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
    private void ForeignCallback(GameItem item) { foreignCalls++; }
    private bool Safe() => (bool)Owner().GetType().GetMethod("SafeCounter", Fields)
        .Invoke(Owner(), new object[] { EmporiumEntry.Instance.backInvinvElement });
    private bool SafeOffer(GameItem item) => (bool)Owner().GetType().GetMethod("SafeOffer", Fields)
        .Invoke(Owner(), new object[] { item });
    private static List<StoreEvent> Events()
    {
        var field = typeof(StoreEventManager).GetField("activeEvents", Fields);
        var events = field?.GetValue(StoreStation.Instance.storeEventManager) as List<StoreEvent>;
        Need(events != null, "Active event list unavailable");
        return events;
    }
    private string Safety()
    {
        object owner = Owner();
        var assembly = owner.GetType().Assembly;
        var safety = assembly.GetType("ShopAssist.InventoryAutoInsert", true);
        var mutations = (MethodBase[])safety.GetField("NativeMutations", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        MethodInfo known = safety.GetMethod("KnownPatches", BindingFlags.Static | BindingFlags.NonPublic);
        string unknown = string.Join(",", mutations.Where(method => !(bool)known.Invoke(null, new object[] { method }))
            .Select(method => method == null ? "null" : method.DeclaringType?.FullName + "." + method.Name));
        var counterMethods = (MethodBase[])owner.GetType().GetField("CounterMethods", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        string unknownCounter = string.Join(",", counterMethods.Select((method, index) => new { method, index })
            .Where(entry => !(bool)known.Invoke(null, new object[] { entry.method }))
            .Select(entry => entry.method == null ? "null@" + entry.index : entry.method.DeclaringType?.FullName + "." + entry.method.Name));
        var preference = owner.GetType().GetField("AcceptPreference", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) as FieldInfo;
        var filterLine = owner.GetType().GetField("LastFilterLineItem", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) as FieldInfo;
        Guid expectedBuild = (Guid)owner.GetType().GetField("VerifiedCounterBuild", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var events = StoreStation.Instance?.storeEventManager?.GetActiveEvents();
        var desk = EmporiumEntry.Instance.backInvinvElement;
        string callbacks = string.Join(";", typeof(GameGridInventory).GetProperties().Where(property => typeof(Delegate).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name + "=" + string.Join(",", ((Delegate)property.GetValue(desk))?.GetInvocationList()
                .Select(callback => callback.Method.DeclaringType + "." + callback.Method.Name) ?? Array.Empty<string>())));
        string client = string.Join(";", typeof(StoreClient).GetFields(Fields).Where(field => field.GetValue(buyer) is Delegate)
            .Select(field => field.Name + "=" + string.Join(",", ((Delegate)field.GetValue(buyer)).GetInvocationList()
                .Select(callback => callback.Method.DeclaringType + "." + callback.Method.Name +
                    "[client=" + ReferenceEquals(callback.Target, buyer) + ",known=" + known.Invoke(null, new object[] { callback.Method }) + "]"))));
        return "safe=" + owner.GetType().GetMethod("SafeCounter", Fields).Invoke(owner, new object[] { desk }) +
            " mutation=" + safety.GetMethod("KnownMutationPatches", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null) +
            " build=" + typeof(GameItem).Module.ModuleVersionId + " expected=" + expectedBuild +
            " deskType=" + desk.GetType().FullName + " clientType=" + buyer.GetType().FullName + " intent=" + buyer.clientIntent +
            " preferenceField=" + (preference != null) + " preference=" + (preference?.GetValue(buyer) != null) +
            " filterField=" + (filterLine != null) + " variety=" + buyer.isRequireVariety +
            " placementDialogue=" + (buyer.placeRightItemWhenSellingToDialogue != null) +
            " events=" + (events == null ? "null" : string.Join(",", events.Select(current => current?.identifier ?? "null"))) +
            " safeOffer=" + SafeOffer(candidate) + " unknown=" + unknown + " unknownCounter=" + unknownCounter +
            " callbacks=" + callbacks + " client=" + client;
    }
    private static GameItem Put(string id, GameInventory target, string name)
    {
        GameItem item = DirectoryMaster.Item(id);
        Need(item != null, "Missing item " + id);
        GeneralHelper.SetItemOwned(item); GeneralHelper.SetCustomName(item, name); Move(item, target); return item;
    }
    private static void Move(GameItem item, GameInventory target)
    {
        SlotMarker marker = target.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.targetItem == null && marker.numTransfer >= item.unitCount &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount, "Could not place " + item.identifier);
    }
    private static bool Same(GridShape left, GridShape right)
    {
        if (left == null || right == null || left.minX != right.minX || left.minY != right.minY ||
            left.globalWidth != right.globalWidth || left.globalHeight != right.globalHeight ||
            left.orientation != right.orientation || left.flipped != right.flipped) return false;
        for (int y = left.minY; y <= left.maxY; y++) for (int x = left.minX; x <= left.maxX; x++)
            if (left.Get(x, y) != right.Get(x, y)) return false;
        return true;
    }
    private static int Price(MonoBehaviour hit)
    {
        string label = Field(Field(hit, "entry"), "QuoteLabel") as string;
        Match match = Regex.Match(label ?? "", @"\d+");
        Need(match.Success, "Missing quote: " + label);
        return int.Parse(match.Value);
    }
    private static Button Compare()
    {
        RoutingScenario.RunFeatureAction("f34.open");
        CustomUIWindow window = Window();
        Need(window?.Get("compare")?.button != null, "Basket entry unavailable");
        window.Get("limit").SetString("9000", true);
        window.Get("compare").button.onClick.Invoke();
        return Window()?.Get("place_0")?.button;
    }
    private GridShape CheckPreview()
    {
        CustomUIWindow window = Window();
        Canvas.ForceUpdateCanvases();
        RectTransform board = window.root.GetComponentsInChildren<RectTransform>(true)
            .FirstOrDefault(node => node.name == "ShopAssist_CounterPreview" && node.gameObject.activeInHierarchy);
        Need(board != null, "Counter preview board missing");
        MonoBehaviour view = board.GetComponents<MonoBehaviour>().First(component => component.GetType().FullName == "ShopAssist.ItemGrid+View");
        object options = Field(view, "Options");
        var boardShape = Field(options, "Board") as GridShape;
        var counter = EmporiumEntry.Instance.backInvinvElement;
        Need(boardShape != null && boardShape.width == counter.inventoryShape.width && boardShape.height == counter.inventoryShape.height,
            "Preview board dimensions differ from the counter");
        for (int y = 0; y < boardShape.height; y++) for (int x = 0; x < boardShape.width; x++)
            Need(boardShape.Get(x, y) == counter.inventoryShape.Get(x, y), "Preview omitted a blocked counter cell");
        float cell = (float)Field(view, "Cell");
        Need(Mathf.Abs(board.rect.width - boardShape.width * cell) < .1f && Mathf.Abs(board.rect.height - boardShape.height * cell) < .1f,
            "Preview dimensions do not match its real grid");
        ScrollRect scroll = board.GetComponentInParent<ScrollRect>();
        Need(scroll != null && scroll.vertical && scroll.horizontal && scroll.viewport != null && board.rect.width <= scroll.viewport.rect.width + 1,
            "Counter preview is clipped horizontally or cannot scroll vertically");
        Debug.Log("[PLAYER-QA] F34 preview screen=" + Screen.width + "x" + Screen.height + " board=" + board.rect.size +
            " viewport=" + scroll.viewport.rect.size + " cell=" + cell);
        var a = QaItemUi.Hit(board.gameObject, resident);
        var b = QaItemUi.Hit(board.gameObject, refused);
        var c = QaItemUi.Hit(board.gameObject, candidate);
        Need(a != null && b != null && c != null, "Counter preview omitted an existing or proposed item");
        Need(Same((GridShape)Field(a, "shownShape"), residentShape) && Same((GridShape)Field(b, "shownShape"), refusedShape),
            "Existing goods changed positions in the preview");
        GridShape planned = (GridShape)Field(c, "shownShape");
        Need(planned != null && !planned.Intersects(residentShape) && !planned.Intersects(refusedShape),
            "The plan overlaps existing or refused goods");
        Need((bool)Field(Field(c, "entry"), "Selected") && !(bool)Field(Field(a, "entry"), "Selected") &&
            !(bool)Field(Field(b, "entry"), "Enabled") && Field(Field(b, "entry"), "QuoteLabel") == null,
            "Preview does not distinguish incoming and refused goods");
        int total = Price(a) + Price(c);
        string summary = ModHelper.GetLocalized(Mod, "f34.summary", resident.unitCount + candidate.unitCount, total, 9000 - total);
        Need(window.root.GetComponentsInChildren<TMP_Text>(true).Any(label => label.text == summary),
            "The preview total includes refused goods or misses accepted goods: " + summary);
        Need(candidate.parentInventory == EmporiumEntry.Instance.invElement && resident.parentInventory == counter &&
            refused.parentInventory == counter && PlayerStore.Instance.playerCash == cash, "Preview moved goods or completed a sale");
        return planned.Clone();
    }

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Counter preview timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        var player = PlayerStore.Instance;
        if (step == 1)
        {
            if (entry.storeInteractables?.activeInHierarchy != true || NegociationUIManager.Instance == null) return false;
            TradeScenario.SetOptions(true, "f34.enabled", "f34.move");
            originalEvents = Events().ToArray(); Events().Clear();
            buyer = StoreClientList.CreateSpacerClientFood(); buyer.SetBudget(9000); buyer.isIntroduced = true;
            player.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer); player.isClientArrived = true;
            var shape = new GridShapeBuilder(8, 4).SetData("00000001000000000000000000000000", 8).SetDataOutside(1).Build();
            typeof(GameGridInventory).GetProperty("inventoryShape").GetSetMethod(true).Invoke(entry.backInvinvElement, new object[] { shape });
            entry.backInvinvElement.Validate();
            resident = Put("cup_noodle", entry.backInvinvElement, "QA pinned accepted");
            refused = Put("processed_meat", entry.backInvinvElement, "QA refused"); refused.EnableTag("NOT_FOR_RESALE");
            candidate = Put("processed_juice", entry.invElement, "QA planned juice");
            ItemPinHelper.SetPinned(resident, true);
            residentShape = resident.modifiedShape.Clone(); refusedShape = refused.modifiedShape.Clone();
            Need(buyer.IsClientBuyingThisItem(resident) && !buyer.IsClientBuyingThisItem(refused), "Invalid acceptance fixtures");
            NegociationUIManager.Instance.OpenUI(); cash = player.playerCash;
            Screen.SetResolution(640, 480, false); waitFrame = Time.frameCount + 10; step++; return false;
        }
        if (step == 2)
        {
            if (Time.frameCount < waitFrame) return false;
            Need(Screen.width == 640 && Screen.height == 480, "Could not switch the isolated test to 640x480: " + Screen.width + "x" + Screen.height);
            Need(Compare() != null, "No feasible counter plan"); waitFrame = Time.frameCount + 3; step++; return false;
        }
        if (step == 3)
        {
            if (Time.frameCount < waitFrame) return false;
            CheckPreview();
            var original = buyer.onItemAddedToWeighted?.GetInvocationList();
            Need(original != null && original.Length == 2 && original.All(callback => ReferenceEquals(callback.Target, buyer)) &&
                original[0].Method.Name == "<CompleteClientCreation>b__143_0" &&
                original[1].Method.Name == "<CompleteClientCreation>b__143_2", "Original buyer callbacks are missing");
            Need(Safe() && SafeOffer(candidate), "Original customer is not supported: " + Safety());
            cosmetic = EventListCosmetic.RoutineSpacewalkTraining();
            Events().Add(cosmetic);
            Need(Safe(), "Unmodified cosmetic event was rejected: " + Safety());
            affectedType = candidate.GetGameItemType().FirstOrDefault(type => candidate.IsTag(type));
            Need(!string.IsNullOrEmpty(affectedType), "Candidate has no usable event item type");
            cosmetic.negociationDatas.Add(new NegociationData(affectedType, 25, "QA event", "QA event"));
            Need(cosmetic.IsItemAffectedByThisEvent(candidate) && !Safe(), "Price-affecting event was accepted");
            Window().Get("place_0").button.onClick.Invoke();
            Need(candidate.parentInventory == entry.invElement && Field(Owner(), "placing") == null,
                "Price-affecting event accepted an old preview");
            Events().Clear(); cosmetic.negociationDatas.Clear();
            NegociationUIManager.Instance.RefreshUI();
            Need(Compare() != null && Safe(), "Event-free customer did not return to a supported state: " + Safety());
            buyer.onItemAddedToWeighted += ForeignCallback;
            Need(!Safe(), "Unknown customer callback was accepted");
            Window().Get("place_0").button.onClick.Invoke();
            Need(foreignCalls == 0 && candidate.parentInventory == entry.invElement && Field(Owner(), "placing") == null,
                "Unknown callback was invoked by automatic staging");
            buyer.onItemAddedToWeighted -= ForeignCallback;
            NegociationUIManager.Instance.RefreshUI();
            Need(Compare() != null && Safe(), "Original customer did not return to a supported state: " + Safety());
            waitFrame = Time.frameCount + 3; step++; return false;
        }
        if (step == 4)
        {
            if (Time.frameCount < waitFrame) return false;
            CheckPreview();
            blocker = Put("cup_noodle", entry.backInvinvElement, "QA late blocker");
            Window().Get("place_0").button.onClick.Invoke();
            Need(candidate.parentInventory == entry.invElement && Field(Owner(), "placing") == null,
                "Changed counter accepted an old preview");
            Move(blocker, entry.invElement); blocker.EnableTag("NOT_FOR_RESALE"); NegociationUIManager.Instance.RefreshUI();
            Need(Compare() != null, "No refreshed counter plan"); waitFrame = Time.frameCount + 3; step++; return false;
        }
        if (step == 5)
        {
            if (Time.frameCount < waitFrame) return false;
            CheckPreview();
            candidate.EnableTag("LUXURY_ITEM");
            Need(!SafeOffer(candidate), "Luxury item passed the native callback safety check");
            Window().Get("place_0").button.onClick.Invoke();
            Need(candidate.parentInventory == entry.invElement && Field(Owner(), "placing") == null,
                "Changed item accepted an old preview");
            candidate.DisableTag("LUXURY_ITEM");
            NegociationUIManager.Instance.RefreshUI();
            Need(Compare() != null && SafeOffer(candidate), "Restored item did not return to a supported state");
            waitFrame = Time.frameCount + 3; step++; return false;
        }
        if (step == 6)
        {
            if (Time.frameCount < waitFrame) return false;
            Window().Get("place_0").button.onClick.Invoke();
            Need(Field(Owner(), "placing") != null, "Native counter staging did not start: " + Safety());
            Need(buyer.placeRightItemWhenSellingToDialogue == null, "Buyer unexpectedly has a placement dialogue");
            buyer.placeRightItemWhenSellingToDialogue = new Dialogue();
            waitFrame = Time.frameCount + 2; step++; return false;
        }
        if (step == 7)
        {
            if (Time.frameCount < waitFrame) return false;
            Need(Field(Owner(), "placing") == null && candidate.parentInventory == entry.invElement,
                "Changed customer dialogue did not stop queued staging");
            buyer.placeRightItemWhenSellingToDialogue = null;
            NegociationUIManager.Instance.RefreshUI();
            Need(Compare() != null, "No plan after customer dialogue was restored");
            waitFrame = Time.frameCount + 3; step++; return false;
        }
        if (step == 8)
        {
            if (Time.frameCount < waitFrame) return false;
            Window().Get("place_0").button.onClick.Invoke();
            Need(Field(Owner(), "placing") != null, "Staging did not restart");
            buyer.onItemAddedToWeighted += ForeignCallback;
            waitFrame = Time.frameCount + 2; step++; return false;
        }
        if (step == 9)
        {
            if (Time.frameCount < waitFrame) return false;
            Need(Field(Owner(), "placing") == null && candidate.parentInventory == entry.invElement && foreignCalls == 0,
                "Changed callback did not stop queued staging");
            buyer.onItemAddedToWeighted -= ForeignCallback;
            NegociationUIManager.Instance.RefreshUI();
            Need(Compare() != null, "No plan after callbacks were restored");
            waitFrame = Time.frameCount + 3; step++; return false;
        }
        if (step == 10)
        {
            if (Time.frameCount < waitFrame) return false;
            Window().Get("place_0").button.onClick.Invoke();
            Need(Field(Owner(), "placing") != null, "Native counter staging did not start: " + Safety());
            Events().Add(cosmetic);
            cosmetic.negociationDatas.Add(new NegociationData(affectedType, 25, "QA event", "QA event"));
            waitFrame = Time.frameCount + 2; step++; return false;
        }
        if (step == 11)
        {
            if (Time.frameCount < waitFrame) return false;
            Need(Field(Owner(), "placing") == null && candidate.parentInventory == entry.invElement,
                "Price-affecting event did not stop queued staging");
            Events().Clear(); cosmetic.negociationDatas.Clear();
            NegociationUIManager.Instance.RefreshUI();
            Need(Compare() != null && Safe(), "No event-free plan after event removal: " + Safety());
            waitFrame = Time.frameCount + 3; step++; return false;
        }
        if (step == 12)
        {
            if (Time.frameCount < waitFrame) return false;
            plannedShape = CheckPreview();
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-basket-grid-preview.png"));
            Window().Get("place_0").button.onClick.Invoke();
            Need(Field(Owner(), "placing") != null, "Event-free staging did not start: " + Safety());
            step++; return false;
        }
        if (step == 13)
        {
            if (Field(Owner(), "placing") != null) return false;
            Need(candidate.parentInventory == entry.backInvinvElement && Same(candidate.modifiedShape, plannedShape) &&
                Same(resident.modifiedShape, residentShape) && Same(refused.modifiedShape, refusedShape) && player.playerCash == cash,
                "Actual staging differs from the preview or changed existing goods");
            Move(candidate, entry.invElement);
            Events().Add(cosmetic);
            NegociationUIManager.Instance.RefreshUI();
            Need(Compare() != null && Safe(), "No plan with an unmodified cosmetic event: " + Safety());
            waitFrame = Time.frameCount + 3; step++; return false;
        }
        if (step == 14)
        {
            if (Time.frameCount < waitFrame) return false;
            plannedShape = CheckPreview();
            Window().Get("place_0").button.onClick.Invoke();
            Need(Field(Owner(), "placing") != null, "Cosmetic-event staging did not start: " + Safety());
            step++; return false;
        }
        if (step == 15)
        {
            if (Field(Owner(), "placing") != null) return false;
            Need(candidate.parentInventory == entry.backInvinvElement && Same(candidate.modifiedShape, plannedShape) &&
                Same(resident.modifiedShape, residentShape) && Same(refused.modifiedShape, refusedShape) && player.playerCash == cash,
                "Cosmetic-event staging differs from the preview or changed existing goods");
            Events().Clear(); Events().AddRange(originalEvents);
            result = "PASS F34 real counter board and prices, original callbacks, cosmetic events, affecting-event and stale state rejection";
            return true;
        }
        return false;
    }
}
