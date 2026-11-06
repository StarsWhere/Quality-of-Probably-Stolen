using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// A ten-unit native fixture tests partial transfer even when the destination
// accepts only one unit per slot. No item factory or stack policy is replaced.
internal static class QaNativeStackFixture
{
    internal static void PutWhole(GameGridInventory destination, GameItem item)
    {
        var slot = destination.TryFindOneValidInventorySlot(item);
        if (slot == null || slot.targetItem != null || slot.numTransfer < 1 || slot.itemGridShape == null)
            throw new InvalidOperationException("No native empty cell for test stack: count=" + item.unitCount + " capacity=" + slot?.numTransfer);
        item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(slot.itemGridShape);
        if (!destination.UncheckedAccept(item) || item.parentInventory != destination || item.unitCount != 10)
            throw new InvalidOperationException("Native unchecked placement did not retain ten units");
    }
}

public sealed class DisplaySplitScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90;
    private sealed class Snapshot
    {
        internal int Count, Cost;
        internal bool Owned, HasCost, HasFeature;
        internal string Name;
        internal long UnitValue, BaseValue, Quote;
    }
    private GameItem goods, controlBag, manual;
    private Snapshot sourceOutcome, splitOutcome;
    private GameGridInventory manualGrid;
    private int step;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".restock");
    private static string WindowText(CustomUIWindow window) => window?.root == null ? "<closed>" :
        string.Join(" | ", window.root.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(text => text.text));
    private static string Details(GameItem source, GameGridInventory target) => "source=" + source?.unitCount +
        ",sourceId=" + source?.identifier + ",sourceParent=" + source?.parentInventory + ",target=" +
        string.Join(" | ", target.childItems.Select(item => item.identifier + "#" + item.uniqueId + "×" + item.unitCount)) +
        ",restock=" + WindowText(Window()) + ",operation=" + WindowText(CustomUIManager.Instance.GetWindow(Mod + ".operation"));

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Display split timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance;
        if (e?.storeInteractables?.activeInHierarchy != true || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
        if (step == 1)
        {
            TradeScenario.SetOptions(true, "f29.enabled", "f29.price");
            foreach (var existing in e.invElement.childItems.Concat(e.showcaseElement.childItems).Where(i => i.identifier == "wine_yeast").ToArray()) existing.Destroy();
            goods = DirectoryMaster.Item("wine_yeast"); Need(goods?.identifier == "wine_yeast", "Missing registered wine_yeast fixture");
            goods.SetStack(10); GeneralHelper.SetItemOwned(goods, true);
            Configure(goods);
            QaNativeStackFixture.PutWhole(e.invElement, goods);
            controlBag = DirectoryMaster.Item("backpack_medium"); GeneralHelper.SetItemOwned(controlBag, true);
            var marker = e.invElement.TryFindOneValidInventorySlot(controlBag);
            Need(marker != null && marker.TryAcceptOnce(1) == 1 && controlBag.parentInventory == e.invElement, "Cannot place comparison bag");
            manualGrid = controlBag.contentWindow.childElement as GameGridInventory;
            Need(manualGrid != null, "Missing comparison grid");
            manual = DirectoryMaster.Item("wine_yeast"); Need(manual?.identifier == "wine_yeast", "Missing registered manual fixture");
            manual.SetStack(10); GeneralHelper.SetItemOwned(manual, true); Configure(manual);
            QaNativeStackFixture.PutWhole(manualGrid, manual);
            var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
            var settings = CustomUIManager.Instance.GetWindow("game_options");
            settings.Get(Mod + ":f29.goal").SetFloat(4, true); settings.Get(Mod + ":f29.keep").SetFloat(2, true);
            settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
            RoutingScenario.OpenOptionAction("f29.open");
            QaItemUi.Choose(Window(), "type", goods.GetDisplayName(true));
            Window().Get("add").button.onClick.Invoke(); Window().Get("preview").button.onClick.Invoke();
            Need(QaItemUi.Hit(Window().root, goods) != null && goods.unitCount == 10 && goods.parentInventory == e.invElement,
                "Preview did not show the source without changing it");
            Window().Get("apply").button.onClick.Invoke();
            var split = e.showcaseElement.childItems.Where(i => i.identifier == "wine_yeast").ToArray();
            Need(goods.parentInventory == e.invElement && goods.unitCount == 6 && split.Length == 4 &&
                split.All(i => i != goods && i.unitCount == 1 && GeneralHelper.IsItemOwned(i)),
                "Native split did not preserve source six and four separate display units: " + Details(goods, e.showcaseElement));
            sourceOutcome = Capture(goods); splitOutcome = Capture(split[0]);
            foreach (var piece in split) { SameOutcome(splitOutcome, piece, "automatic pieces"); piece.Destroy(); }
            Window().Close();
            e.showcaseWindow.Show();
            ItemMouseDoubleClickHandler.current.OpenContentAction(controlBag, new Vector2(Screen.width * .2f, Screen.height * .7f));
            step++; return false;
        }
        if (step == 2)
        {
            Canvas.ForceUpdateCanvases();
            for (int count = 0; count < 4; count++)
            {
                var previous = e.showcaseElement.childItems.ToArray();
                DragOne((GameItemElement)manual, manualGrid, e.showcaseElement);
                var created = e.showcaseElement.childItems.Except(previous).Where(i => i.identifier == "wine_yeast").ToArray();
                Need(created.Length == 1 && created[0].unitCount == 1, "Native mouse did not split one unit: " + Details(manual, e.showcaseElement));
                SameOutcome(splitOutcome, created[0], "split unit");
            }
            Need(manual.parentInventory == manualGrid && manual.unitCount == 6, "Native mouse did not leave six units");
            SameOutcome(sourceOutcome, manual, "remaining stack");
            result = "PASS F29 moves four native single-slot units and matches four mouse splits for ownership, price, labels and quote";
            return true;
        }
        return false;
    }

    private static void Configure(GameItem item)
    {
        GeneralHelper.TrackBuyPrice(item, 123); GeneralHelper.SetCustomName(item, "QA labelled stack");
        var feature = new ItemFeature(ItemFeature.FeatureType.Special).SetPublicDisplay("QA public").SetActualDisplay("QA actual");
        feature.identifier = "qa_split_public"; item.AddItemFeature(feature);
    }

    private static Snapshot Capture(GameItem item) => new Snapshot
    {
        Count = item.unitCount, Owned = GeneralHelper.IsItemOwned(item), HasCost = item.IsTag("BOUGHT_PRICE_TAG"),
        Cost = item.IsTag("BOUGHT_PRICE_TAG") ? item.GetTagReadonly("BOUGHT_PRICE_TAG").GetInt() : 0,
        Name = GeneralHelper.GetCustomName(item), HasFeature = item.itemFeatures.Any(f => f.identifier == "qa_split_public"),
        UnitValue = item.unitValue, BaseValue = item.GetBaseValue(), Quote = item.GetNegociatedValue()
    };
    private static void SameOutcome(Snapshot expected, GameItem item, string stage)
    {
        Snapshot actual = Capture(item);
        Need(expected.Count == actual.Count && expected.Owned == actual.Owned && expected.HasCost == actual.HasCost &&
            expected.Cost == actual.Cost && expected.Name == actual.Name && expected.HasFeature == actual.HasFeature &&
            expected.UnitValue == actual.UnitValue && expected.BaseValue == actual.BaseValue && expected.Quote == actual.Quote,
            "Auto and mouse split differ in " + stage);
    }

    private static Vector3 Cell(GameGridInventory grid, int x, int y)
    {
        var rect = grid.rectTransform; var canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        float size = UISettings.current.gridSize;
        return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(new Vector3((x + .5f) * size, -(y + .5f) * size)));
    }

    private static void DragOne(GameItemElement item, GameGridInventory source, GameGridInventory target)
    {
        var handler = ItemMouseDragHandler.current; Need(handler != null, "Native mouse drag handler unavailable");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        void Invoke(string method, params object[] args) => typeof(ItemMouseDragHandler).GetMethod(method, flags).Invoke(handler, args);
        object Field(string field) => typeof(ItemMouseDragHandler).GetField(field, flags).GetValue(handler);
        Vector3 start = Vector3.zero; bool found = false;
        for (int y = item.modifiedShape.minY; y <= item.modifiedShape.maxY && !found; y++)
        for (int x = item.modifiedShape.minX; x <= item.modifiedShape.maxX && !found; x++)
            if (item.modifiedShape.Get(x, y) == 1 && ReferenceEquals(RenderHandler.RaycastElement<GameItemElement>(Cell(source, x, y)), item))
            { start = Cell(source, x, y); found = true; }
        Need(found, "Native comparison stack is not reachable by mouse");
        using (var input = new QaSyntheticInput(true))
        {
            try
            {
                input.Mouse = start; input.Held = handler.key; StoreUIManager.Instance.CheckRaycast();
                Invoke("OnEventPress", new HashSet<KeyCode> { handler.key });
                Vector3 previous = start + new Vector3(Mathf.Max(handler.dragThreshold + 2, 12), 0);
                input.Mouse = previous; Invoke("OnEventMouseMove", previous, previous - start);
                Need((bool)Field("isDragging"), "Native drag did not begin");
                Invoke("OnEventMouseScroll", new Vector2(0, -(item.unitCount - 1)));
                Need((int)Field("currentRemove") == 1, "Mouse wheel did not choose one unit");
                bool valid = false;
                for (int y = 0; y < target.inventoryShape.height && !valid; y++)
                for (int x = 0; x < target.inventoryShape.width && !valid; x++)
                {
                    Vector3 point = Cell(target, x, y);
                    if (RenderHandler.RaycastElement<GameItemElement>(point) != null ||
                        !ReferenceEquals(RenderHandler.RaycastFindParentElement<GameGridInventory>(point), target)) continue;
                    input.Mouse = point; StoreUIManager.Instance.CheckRaycast();
                    Invoke("OnEventMouseMove", point, point - previous); previous = point;
                    for (int frame = 0; frame < 80; frame++) Invoke("OnEventUpdate");
                    Invoke("OnEventMouseMove", point, Vector3.zero);
                    var slot = Field("lastSlot") as SlotMarker;
                    valid = ReferenceEquals(Field("lastInventory"), target) && slot != null && slot.targetItem == null && slot.numTransfer >= 1;
                }
                Need(valid, "Native mouse drag found no valid display slot");
                Invoke("OnEventRelease", new HashSet<KeyCode> { handler.key });
                input.Held = KeyCode.None;
            }
            finally { if ((bool)Field("isDragging")) handler.Cancel(); }
        }
    }
}

public sealed class DisplaySplitRecoveryScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Display split recovery timed out");
        if (!start.Tick()) return false;
        var mod = ModLoader.Mods.First(m => m.Manifest?.ID == "starswhere.shopassist");
        var type = mod.Instance.GetType().Assembly.GetType("ShopAssist.DisplayRestock", true);
        var active = type.GetField("active", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var moveType = type.GetNestedType("Move", BindingFlags.NonPublic);
        var restore = type.GetMethod("Restore", BindingFlags.Instance | BindingFlags.NonPublic);
        var preflight = type.GetMethod("HasRecoverySpace", BindingFlags.Static | BindingFlags.NonPublic);
        var locationType = mod.Instance.GetType().Assembly.GetType("ShopAssist.ShopInventory", true)
            .GetNestedType("Location", BindingFlags.NonPublic);
        Need(active != null && moveType != null && restore != null && preflight != null && locationType != null,
            "Display recovery interface unavailable");
        var tightItem = DirectoryMaster.Item("wine_yeast"); Need(tightItem?.identifier == "wine_yeast", "Missing registered preflight fixture");
        tightItem.SetStack(10); GeneralHelper.SetItemOwned(tightItem, true);
        var tight = new GameGridInventory(tightItem.modifiedShape.globalWidth, tightItem.modifiedShape.globalHeight);
        QaNativeStackFixture.PutWhole(tight, tightItem);
        var locations = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(locationType));
        void AddLocation(GameGridInventory grid)
        {
            object location = Activator.CreateInstance(locationType, true);
            locationType.GetField("Inventory", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(location, grid);
            locationType.GetField("Ordinary", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(location, true);
            locations.Add(location);
        }
        AddLocation(tight);
        var emptyTarget = new GameGridInventory(6, 6);
        Need(!(bool)preflight.Invoke(null, new object[] { tightItem, 1, locations, emptyTarget }),
            "Recovery preflight reused the still-occupied source footprint");
        AddLocation(new GameGridInventory(6, 6));
        Need((bool)preflight.Invoke(null, new object[] { tightItem, 1, locations, emptyTarget }),
            "Recovery preflight missed a legal alternative storage grid");
        var source = new GameGridInventory(6, 6);
        var target = new RejectingDisplayGrid(6, 6);
        var item = DirectoryMaster.Item("wine_yeast"); Need(item?.identifier == "wine_yeast", "Missing registered split fixture");
        item.SetStack(10); GeneralHelper.SetItemOwned(item, true);
        QaNativeStackFixture.PutWhole(source, item);
        GridShape original = item.modifiedShape.Clone();
        var destination = target.TryInventorySlot(item, 1, new GridShapeBuilder(item.modifiedShape).SetPosition(0, 0));
        Need(destination != null && destination.targetItem == null && destination.numTransfer >= 1,
            "No destination for native partial transfer");
        target.refuse = true;
        int accepted = destination.TryAcceptOnce(1);
        GameItem split = destination.item;
        Need(accepted == 0 && item.unitCount == 9 && split != null && split != item && split.unitCount == 1,
            "Fixture did not reproduce native rejection after splitting");
        object move = Activator.CreateInstance(moveType, true);
        void Set(string field, object value) => moveType.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(move, value);
        Set("Item", item); Set("From", source); Set("Home", original); Set("Count", 1); Set("SourceCount", 10);
        bool restored = (bool)restore.Invoke(active, new[] { move, split, target });
        Need(item.parentInventory == source && source.childItems.Where(i => i.identifier == item.identifier).Sum(i => i.unitCount) == 10 &&
            !target.childItems.Contains(split) && (restored ? item.unitCount == 10 : split.parentInventory == source),
            "Rejected placement lost units or incorrectly claimed full recovery");
        result = "PASS F29 recovery preflight excludes occupied cells and failed native split retains all units";
        return true;
    }
}

public sealed class RejectingDisplayGrid : GameGridInventory
{
    public bool refuse;
    public RejectingDisplayGrid(int width, int height) : base(width, height) { }
    public override bool UncheckedAccept(GameItem item) => !refuse && base.UncheckedAccept(item);
}

public sealed class ReservationMigrationScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private readonly string name = "QA reservation " + Guid.NewGuid().ToString("N").Substring(0, 6);
    private GameItem first, second, stale, unknown, oldBox, newBox;
    private StoreRequest request;
    private int step, firstId, secondId, unknownId, newBoxId;
    private QaLoadSlot load;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".requests");
    private static void Put(GameItem item, GameInventory dest)
    {
        var slot = dest.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.targetItem == null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == dest,
            "Cannot place request fixture");
    }
    private static GameItem New(string id, GameInventory dest)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing request fixture " + id);
        GeneralHelper.SetItemOwned(item, true); Put(item, dest); return item;
    }
    private static object Book()
    {
        var mod = ModLoader.Mods.First(m => m.Manifest?.ID == Mod);
        return mod.Instance.GetType().Assembly.GetType("ShopAssist.RequestBook", true)
            .GetField("active", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
    }
    private static void Call(object owner, string name, params object[] args) => owner.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(owner, args);

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Reservation migration timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 2)
        {
            if (!load.Tick()) return false;
            var root = EmporiumEntry.Instance.invElement;
            var box = root.childItems.SingleOrDefault(i => i.uniqueId == newBoxId);
            var grid = box?.contentWindow?.childElement as GameGridInventory;
            var a = grid?.childItems.SingleOrDefault(i => i.uniqueId == firstId);
            var b = grid?.childItems.SingleOrDefault(i => i.uniqueId == secondId);
            var pending = root.childItems.SingleOrDefault(i => i.uniqueId == unknownId);
            Need(a != null && b != null && pending != null && RequestColdReloadScenario.Reserved(a) &&
                RequestColdReloadScenario.Reserved(b) && RequestColdReloadScenario.Reserved(pending) &&
                ModHelper.GetModData(Mod, "requests", "").Contains("item:999999999:contents:-1"),
                "Cold reload lost migrated or unresolved reservations");
            result = "PASS F27 legacy verification, unresolved reservation, target change, partial migration, retry and cold reload";
            return true;
        }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        if (e?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;
        if (step == 1)
        {
            TradeScenario.SetOptions(true, "f27.enabled");
            oldBox = New("backpack_medium", e.invElement); newBox = New("backpack_medium", e.invElement);
            GeneralHelper.SetCustomName(oldBox, name + " old"); GeneralHelper.SetCustomName(newBox, name + " new");
            var oldGrid = oldBox.contentWindow.childElement as GameGridInventory;
            Need(oldGrid != null && newBox.contentWindow.childElement is GameGridInventory, "Missing storage grids");
            first = New("wine_yeast_red", oldGrid); second = New("wine_yeast_red", oldGrid);
            stale = New("wine_yeast_red", e.invElement); unknown = New("wine_yeast_red", e.invElement);
            ItemMouseDoubleClickHandler.current.OpenContentAction(oldBox, new Vector2(Screen.width * .6f, Screen.height * .7f));
            ItemMouseDoubleClickHandler.current.OpenContentAction(newBox, new Vector2(Screen.width * .8f, Screen.height * .7f));
            request = new StoreRequest { title = name, id = name, sourceEventId = name, requiredItem = first.identifier,
                requiredItemCount = 2, requesterName = "QA", duration = 2 };
            var unresolved = new StoreRequest { title = name + " pending", id = name + " pending", sourceEventId = name,
                requiredItem = first.identifier, requiredItemCount = 1, requesterName = "QA", duration = 2 };
            p.storeRequests.Add(request); p.storeRequests.Add(unresolved);
            int expires = StoreStation.GetDayCounter() + 2;
            string oldKey = "item:" + oldBox.GetUniqueID() + ":contents:-1";
            string missingKey = "item:999999999:contents:-1";
            string row(string id, int quantity, string dest, string ids) => "{\"id\":\"" + id + "\",\"source\":\"" + name +
                "\",\"item\":\"wine_yeast_red\",\"requester\":\"QA\",\"destination\":\"" + dest + "\",\"quantity\":" +
                quantity + ",\"expires\":" + expires + ",\"reserved\":[" + ids + "]}";
            string raw = "{\"version\":1,\"orders\":[" + row(name, 2, oldKey,
                first.GetUniqueID() + "," + second.GetUniqueID() + "," + stale.GetUniqueID()) + "," +
                row(name + " pending", 1, missingKey, unknown.GetUniqueID().ToString()) + "]}";
            ModHelper.SetModData(Mod, "requests", raw); var book = Book(); Call(book, "Reset"); Call(book, "Draw", request);
            Need(RequestColdReloadScenario.Reserved(first) && RequestColdReloadScenario.Reserved(second) &&
                !RequestColdReloadScenario.Reserved(stale) && RequestColdReloadScenario.Reserved(unknown) &&
                ModHelper.GetModData(Mod, "requests", "").Contains("\"version\":2"),
                "Legacy conversion did not preserve verified and unresolved reservations or discard the stale ID");
            QaItemUi.Choose(Window(), "destination", name + " new");
            Need(RequestColdReloadScenario.Reserved(first) && RequestColdReloadScenario.Reserved(second) && first.parentInventory == oldGrid,
                "Changing destination released or moved reserved goods");
            Window().Get("migrate").button.onClick.Invoke();
            Need(QaItemUi.Hit(Window().root, first) != null && QaItemUi.Hit(Window().root, second) != null,
                "Migration preview omitted reserved goods");
            ItemPinHelper.SetPinned(second, true);
            Window().Get("migrate_apply").button.onClick.Invoke();
            var target = (GameGridInventory)newBox.contentWindow.childElement;
            Need(first.parentInventory == target && second.parentInventory == oldGrid && RequestColdReloadScenario.Reserved(second),
                "A partial migration did not preserve the remaining reservation");
            ItemPinHelper.SetPinned(second, false);
            Window().Get("migrate").button.onClick.Invoke();
            Need(QaItemUi.Hit(Window().root, first) == null && QaItemUi.Hit(Window().root, second) != null,
                "Retry included already migrated goods or omitted the remainder");
            Window().Get("migrate_apply").button.onClick.Invoke();
            Need(first.parentInventory == target && second.parentInventory == target && RequestColdReloadScenario.Reserved(first) &&
                RequestColdReloadScenario.Reserved(second) && RequestColdReloadScenario.Reserved(unknown),
                "Retry did not retain the migrated and unresolved reservations");
            firstId = first.uniqueId; secondId = second.uniqueId; unknownId = unknown.uniqueId; newBoxId = newBox.uniqueId;
            int slot = p.saveSlotId; string run = p.runID; p.SaveGame();
            StoreUIManager.Instance.escapeUIManager.OnMainMenu();
            load = new QaLoadSlot(slot, Guid.Parse(run)); step = 2;
            return false;
        }
        return false;
    }
}

public sealed class ReservationCorruptScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Corrupt reservation test timed out");
        if (!start.Tick()) return false;
        var e = EmporiumEntry.Instance;
        if (e?.storeInteractables?.activeInHierarchy != true) return false;
        TradeScenario.SetOptions(true, "f27.enabled");
        var item = DirectoryMaster.Item("wine_yeast_red"); GeneralHelper.SetItemOwned(item, true);
        var slot = e.invElement.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == e.invElement,
            "Could not place corrupt-data fixture");
        var mod = ModLoader.Mods.First(m => m.Manifest?.ID == Mod);
        Type type = mod.Instance.GetType().Assembly.GetType("ShopAssist.RequestBook", true);
        object book = type.GetField("active", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        MethodInfo reset = type.GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo load = type.GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic);
        int expires = StoreStation.GetDayCounter() + 2;
        foreach (int version in new[] { 1, 2 })
        {
            string raw = "{\"version\":" + version + ",\"orders\":[{\"id\":null,\"source\":null," +
                "\"item\":\"wine_yeast_red\",\"requester\":null,\"destination\":null,\"quantity\":1,\"expires\":" + expires + "}]}";
            ModHelper.SetModData(Mod, "requests", raw); reset.Invoke(book, null);
            Need(!(bool)load.Invoke(book, null) && ModHelper.GetModData(Mod, "requests", null) == raw &&
                RequestColdReloadScenario.Reserved(item), "Missing reserved list was accepted, overwritten, or failed open for v" + version);
        }
        result = "PASS F27 v1/v2 missing reservation data is retained and owned goods fail closed";
        return true;
    }
}

public sealed class ReservationSaveFailureScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".requests");
    private static bool FailRequestWrite(string __0, string __1)
    {
        if (__0 == Mod && __1 == "requests") throw new InvalidOperationException("QA injected reservation write failure");
        return true;
    }
    private static bool RefuseMove(ref string __3, ref bool __result)
    {
        __3 = "inv.changed"; __result = false; return false;
    }

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Reservation write failure test timed out");
        if (!start.Tick()) return false;
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        if (e?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;
        TradeScenario.SetOptions(true, "f27.enabled");
        var box = DirectoryMaster.Item("backpack_medium"); var item = DirectoryMaster.Item("wine_yeast_red");
        GeneralHelper.SetItemOwned(box, true); GeneralHelper.SetItemOwned(item, true);
        foreach (var value in new[] { box, item })
        {
            var slot = e.invElement.TryFindOneValidInventorySlot(value);
            Need(slot != null && slot.TryAcceptOnce(value.unitCount) == value.unitCount && value.parentInventory == e.invElement,
                "Cannot place write-failure fixture");
        }
        string name = "QA write failure " + Guid.NewGuid().ToString("N").Substring(0, 6);
        GeneralHelper.SetCustomName(box, name); ItemMouseDoubleClickHandler.current.OpenContentAction(box, Vector2.zero);
        var request = new StoreRequest { title = name, id = name, sourceEventId = name, requiredItem = item.identifier,
            requiredItemCount = 1, requesterName = "QA", duration = 2 };
        p.storeRequests.Add(request);
        var mod = ModLoader.Mods.First(m => m.Manifest?.ID == Mod);
        Type type = mod.Instance.GetType().Assembly.GetType("ShopAssist.RequestBook", true);
        object book = type.GetField("active", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        type.GetMethod("Draw", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(book, new object[] { request });
        Window().Get("track").button.onClick.Invoke(); QaItemUi.Choose(Window(), "destination", name);
        QaItemUi.Click(Window(), item);
        string before = ModHelper.GetModData(Mod, "requests", null);
        var harmony = new Harmony("qa.reservation-write-failure");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ModHelper), nameof(ModHelper.SetModData), new[] { typeof(string), typeof(string), typeof(string) }),
                prefix: new HarmonyMethod(typeof(ReservationSaveFailureScenario).GetMethod(nameof(FailRequestWrite), BindingFlags.Static | BindingFlags.NonPublic)));
            Window().Get("prepare").button.onClick.Invoke();
        }
        finally { harmony.UnpatchAll("qa.reservation-write-failure"); }
        Need(item.parentInventory == e.invElement && ModHelper.GetModData(Mod, "requests", null) == before &&
            !RequestColdReloadScenario.Reserved(item), "Failed reservation save moved or falsely reserved the goods");

        QaItemUi.Click(Window(), item);
        Type inventoryType = mod.Instance.GetType().Assembly.GetType("ShopAssist.ShopInventory", true);
        var move = inventoryType.GetMethod("TryMoveIntact", BindingFlags.Static | BindingFlags.NonPublic);
        harmony = new Harmony("qa.reservation-move-failure");
        try
        {
            harmony.Patch(move, prefix: new HarmonyMethod(typeof(ReservationSaveFailureScenario).GetMethod(nameof(RefuseMove), BindingFlags.Static | BindingFlags.NonPublic)));
            Window().Get("prepare").button.onClick.Invoke();
        }
        finally { harmony.UnpatchAll("qa.reservation-move-failure"); }
        type.GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(book, null);
        Need(item.parentInventory == e.invElement && RequestColdReloadScenario.Reserved(item),
            "Failed move lost the already persisted reservation after reload");
        result = "PASS F27 failed reservation write leaves goods unmoved; failed move retains saved source reservation";
        return true;
    }
}
