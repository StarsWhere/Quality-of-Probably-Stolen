using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Exercises native single and group drags with real slots and virtual device input.
public sealed class AutoArrangeInputScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    private static AutoArrangeInputScenario active;
    private readonly QaNewRun start = new QaNewRun();
    private readonly ArrangeCapture capture = new ArrangeCapture();
    private readonly List<GameItemElement> residents = new List<GameItemElement>();
    private readonly Dictionary<GameItem, GridShape> targetBefore = new Dictionary<GameItem, GridShape>();
    private readonly Dictionary<GameItem, string> observedStates = new Dictionary<GameItem, string>();
    private QaSyntheticInput input;
    private Harmony observer;
    private GameItemElement targetBag, sourceBag, directBag, single, groupFirst, groupSecond;
    private GameGridInventory target, stock, groupSource, directGrid;
    private GridShape singleBefore, groupFirstBefore, groupSecondBefore;
    private PixelWindow targetWindow, sourceWindow, directWindow, menu;
    private int step, readyFrame, pass;
    private bool observeGroup, nativePartial;
    private float since = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 160) throw new InvalidOperationException("Auto arrange input timed out at step " + step);
        if (Time.frameCount < readyFrame) return false;
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                Next(1); return false;
            case 1:
                if (EmporiumEntry.Instance?.invElement == null || ItemMouseDragHandler.current == null || ItemMultiSelectHandler.current == null) return false;
                var auto = ModConfig.Find(Mod)?.Get("f05.auto_insert") as ConfigEntry<bool>;
                Need(auto != null && !auto.Default, "Automatic insertion does not default off");
                TradeScenario.SetOptions(true, "f05.enabled", "f05.keep_menu", "f05.rotate");
                TradeScenario.SetOptions(false, "f05.auto_insert", "f40.enabled", "f41.enabled");
                stock = EmporiumEntry.Instance.invElement;
                targetBag = New("fanny_pack"); Put(stock, targetBag);
                target = targetBag.contentWindow?.childElement as GameGridInventory;
                Need(target?.inventoryShape != null && target.inventoryShape.width >= 3 && target.inventoryShape.height >= 3, "Target backpack grid unavailable");
                for (int x = 0; x < target.inventoryShape.width; x++)
                {
                    var resident = New("wine_superyeast");
                    Need(resident.modifiedShape.globalWidth == 1 && resident.modifiedShape.globalHeight == 2, "Native resident footprint changed");
                    PutAt(target, resident, x, target.inventoryShape.height - 2); residents.Add(resident);
                }
                ItemPinHelper.SetPinned(residents[0], true);
                single = New("wine_yeast");
                Need(single.modifiedShape.globalWidth == 1 && single.modifiedShape.globalHeight == 2 && single.unitCount == 1, "Native dragged fixture changed");
                Put(stock, single); singleBefore = single.modifiedShape.Clone();
                foreach (GameItem resident in residents) targetBefore.Add(resident, resident.modifiedShape.Clone());
                ObserveCompletion();
                ItemMouseDoubleClickHandler.current.OpenContentAction(targetBag, new Vector2(Screen.width - 240, 170));
                targetWindow = targetBag.contentWindow;
                input = new QaSyntheticInput(keyboard: true, mouseButtons: true); input.Mouse = new Vector3(-100, -100);
                Next(2, 5); return false;
            case 2:
                Need(targetWindow.IsVisible(), "Target backpack did not open");
                BeginSingleDrag(); Next(3); return false;
            case 3:
                MoveToTarget(); Next(4, 20); return false;
            case 4:
                Need(ItemMouseDragHandler.current.IsDraggingItem &&
                    ReferenceEquals(typeof(ItemMouseDragHandler).GetField("lastInventory", Fields)?.GetValue(ItemMouseDragHandler.current), target),
                    "Native single drag never reached the target grid");
                ReleaseLeft(); Next(5, 3); return false;
            case 5:
                ClearInput();
                if (pass < 2)
                {
                    Need(single.parentInventory == stock && single.unitCount == 1 && Same(single.modifiedShape, singleBefore) && TargetOriginal(),
                        "Disabled automatic insertion changed native drop behavior");
                    if (pass == 0)
                    {
                        TradeScenario.SetOptions(true, "f05.auto_insert");
                        TradeScenario.SetOptions(false, "f05.auto_insert");
                        pass = 1;
                    }
                    else { TradeScenario.SetOptions(true, "f05.auto_insert"); pass = 2; }
                    Next(2, 2); return false;
                }
                Need(single.parentInventory == target && ItemPinHelper.IsPinned(residents[0]) &&
                    Same(residents[0].modifiedShape, targetBefore[residents[0]]) &&
                    residents.Skip(1).Any(item => !Same(item.modifiedShape, targetBefore[item])),
                    "Ordinary stock-to-backpack drag did not automatically make room: " + GuardDiagnostics());
                RightDown(EmptyCell(target)); Next(6); return false;
            case 6:
                ClearInput(); menu = Window("sortWindow"); Need(menu?.IsVisible() == true, "Auto undo grid menu did not open");
                Need(!menu.handler.GetComponentsInChildren<Transform>(true).Any(node =>
                    node.name == "ShopAssist_f05_undo_grid" && node.gameObject.activeInHierarchy), "Duplicate grid undo row remains");
                input.Mouse = NativeUndoPoint(); Next(7, 2); return false;
            case 7:
                PressLeft(); Next(8); return false;
            case 8:
                ReleaseLeft(); Next(9, 3); return false;
            case 9:
                ClearInput();
                Need(single.parentInventory == stock && single.unitCount == 1 && Same(single.modifiedShape, singleBefore) && TargetOriginal(),
                    "Automatic insertion undo did not restore stock and target");
                directBag = New("fanny_pack"); Put(stock, directBag);
                directGrid = directBag.contentWindow?.childElement as GameGridInventory;
                Need(directGrid != null && directGrid.childItems.Count == 0, "Direct native target is unavailable");
                ItemMouseDoubleClickHandler.current.OpenContentAction(directBag, new Vector2(Screen.width - 240, 170));
                directWindow = directBag.contentWindow; Next(90, 5); return false;
            case 90:
                Need(directWindow.IsVisible(), "Direct native target did not open");
                BeginSingleDrag(); Next(91); return false;
            case 91:
                ClearDown(); directWindow.ToFront(); input.Mouse = EmptyCell(directGrid); Next(92, 20); return false;
            case 92:
                Need(ItemMouseDragHandler.current.IsDraggingItem &&
                    ReferenceEquals(typeof(ItemMouseDragHandler).GetField("lastInventory", Fields)?.GetValue(ItemMouseDragHandler.current), directGrid),
                    "Native direct drag never reached the empty target");
                ReleaseLeft(); Next(93, 3); return false;
            case 93:
                ClearInput();
                Need(single.parentInventory == directGrid && single.unitCount == 1 && directGrid.childItems.Count == 1 && TargetOriginal(),
                    "Native direct placement changed when auto insertion was enabled");
                directWindow.Hide(); ItemPinHelper.SetPinned(residents[0], false); PrepareGroup(); Next(10, 5); return false;
            case 10:
                Need(sourceWindow.IsVisible(), "Group source backpack did not open");
                sourceWindow.ToFront(); input.Mouse = Cell(groupSource, 0, 0); PressLeft(); Next(11); return false;
            case 11:
                ClearDown(); input.Mouse = Cell(groupSource, 3, 3); Next(12, 3); return false;
            case 12:
                ReleaseLeft(); Next(13, 3); return false;
            case 13:
                ClearInput();
                var selected = typeof(ItemMultiSelectHandler).GetField("selectedItems", Fields)?.GetValue(ItemMultiSelectHandler.current) as List<GameItemElement>;
                Need(selected != null && selected.Count == 2 && selected.Contains(groupFirst) && selected.Contains(groupSecond), "Native band did not select both group fixtures");
                Need(ReferenceEquals(selected[0], groupFirst), "Native selection order did not place the one-cell group member first");
                sourceWindow.ToFront(); input.Mouse = ItemCell(groupSource, groupFirst); PressLeft(); Next(14); return false;
            case 14:
                ClearDown(); targetWindow.ToFront(); input.Mouse = EmptyCell(target); Next(15, 20); return false;
            case 15:
                Need(ItemMultiSelectHandler.current.IsGroupDragging, "Native group drag did not start");
                observeGroup = true; ReleaseLeft(); Next(16, 4); return false;
            case 16:
                ClearInput(); observeGroup = false;
                Need(nativePartial && groupFirst.parentInventory == target && groupSecond.parentInventory == target,
                    "Group drop did not include both a native partial transfer and automatic insertion: nativePartial=" + nativePartial +
                    ", first=" + Parent(groupFirst) + ", second=" + Parent(groupSecond) + ", " + GuardDiagnostics());
                for (int y = 0; y < target.inventoryShape.height; y++)
                    for (int x = 0; x < target.inventoryShape.width; x++)
                        Need(target.childItems.Any(item => item.modifiedShape.Intersects(x, y)), "Group target was not completely full");
                Need(InventorySortHelper.MayUndo(target), "Full target has no reversible automatic insertion");
                RightDown(ItemCell(target, groupFirst)); Next(17); return false;
            case 17:
                ClearInput(); menu = Window("contextWindow"); Need(menu?.IsVisible() == true, "Group undo item menu did not open");
                input.Mouse = ButtonPoint(menu, "ShopAssist_f05_undo_item");
                capture.Start("qa-arrange-auto-undo-menu.png"); Next(170, 4); return false;
            case 170:
                if (!capture.Ready()) return false;
                Need(menu?.IsVisible() == true, "Group undo menu closed during capture");
                Next(18, 2); return false;
            case 18:
                PressLeft(); Next(19); return false;
            case 19:
                ReleaseLeft(); Next(20, 3); return false;
            case 20:
                ClearInput();
                Need(groupFirst.parentInventory == groupSource && groupSecond.parentInventory == groupSource && groupFirst.unitCount == 1 && groupSecond.unitCount == 1 &&
                    Same(groupFirst.modifiedShape, groupFirstBefore) && Same(groupSecond.modifiedShape, groupSecondBefore) && TargetOriginal(),
                    "One group undo did not restore native and automatic transfers together");
                observer?.UnpatchAll("qa.auto-arrange-observer"); active = null; input.Dispose(); input = null;
                result = "PASS auto-insert defaults, pinned obstacle, native single/group input, native grid undo and whole-operation undo";
                return true;
            default: throw new InvalidOperationException("Unknown auto arrange input step");
        }
    }

    private void PrepareGroup()
    {
        // Native group transfer rotates items while searching for a slot. Leave
        // three cells whose first occupied cell makes the other two non-adjacent,
        // while the whole group still fits after rearranging the dominoes.
        Need(target.inventoryShape.width == 3 && target.inventoryShape.height == 3 && residents.Count == 3,
            "Group target fixture requires the native 3x3 backpack");
        foreach (GameItem resident in residents) Need(target.Expel(resident), "Could not prepare group target");
        PutAt(target, residents[0], 1, 0, 0);
        PutAt(target, residents[1], 2, 1, 0);
        PutAt(target, residents[2], 0, 2, 1);
        targetBefore.Clear();
        foreach (GameItem resident in residents) targetBefore.Add(resident, resident.modifiedShape.Clone());
        sourceBag = New("backpack_medium"); Put(stock, sourceBag);
        groupSource = sourceBag.contentWindow?.childElement as GameGridInventory;
        Need(groupSource?.inventoryShape != null && groupSource.inventoryShape.width >= 4 && groupSource.inventoryShape.height >= 4,
            "Group source backpack grid too small");
        groupFirst = FindOneCell(); groupSecond = New("wine_yeast");
        Need(groupFirst.unitCount == 1 && groupSecond.unitCount == 1 && !groupFirst.MayStack(groupSecond), "Group fixtures are not independent intact items");
        PutAt(groupSource, groupFirst, 1, 1); PutAt(groupSource, groupSecond, 2, 1);
        groupFirstBefore = groupFirst.modifiedShape.Clone(); groupSecondBefore = groupSecond.modifiedShape.Clone();
        ItemMouseDoubleClickHandler.current.OpenContentAction(sourceBag, new Vector2(80, 170)); sourceWindow = sourceBag.contentWindow;
    }
    private void ObserveCompletion()
    {
        var mod = ModLoader.Mods.FirstOrDefault(value => value.Manifest?.ID == Mod && value.Instance != null);
        var method = mod?.Instance.GetType().Assembly.GetType("ShopAssist.InventoryAutoInsert")?.GetMethod("Complete", Fields);
        Need(method != null, "Automatic insertion observer target unavailable");
        observer = new Harmony("qa.auto-arrange-observer"); active = this;
        observer.Patch(method, prefix: new HarmonyMethod(typeof(AutoArrangeInputScenario).GetMethod(nameof(ObserveNative), Fields)),
            postfix: new HarmonyMethod(typeof(AutoArrangeInputScenario).GetMethod(nameof(ObserveCompleted), Fields)));
    }
    private static string State(GameItem item)
    {
        var mod = ModLoader.Mods.FirstOrDefault(value => value.Manifest?.ID == Mod && value.Instance != null);
        var method = mod?.Instance.GetType().Assembly.GetType("ShopAssist.InventoryArrangeTransaction")?.GetMethod("StateSignature", Fields);
        return method?.Invoke(null, new object[] { item }) as string;
    }
    private static void ObserveNative(object __0)
    {
        if (active == null) return;
        if (active.observeGroup)
        {
            active.nativePartial = active.groupFirst.parentInventory == active.target && active.groupSecond.parentInventory == active.groupSource;
            string transactionCheck = "none";
            if (__0 != null)
            {
                try
                {
                    Type requestType = __0.GetType();
                    object transaction = requestType.GetField("Transaction", Fields)?.GetValue(__0);
                    var items = requestType.GetField("Items", Fields)?.GetValue(__0) as GameItem[];
                    var destination = requestType.GetField("Target", Fields)?.GetValue(__0) as GameGridInventory;
                    transactionCheck = transaction?.GetType().GetMethod("NativeMovedOnly", Fields)?.Invoke(transaction,
                        new object[] { new HashSet<GameItem>(items), destination })?.ToString() ?? "missing";
                }
                catch (Exception error) { transactionCheck = error.GetBaseException().Message; }
            }
            Debug.Log("[PLAYER-QA] group before helper: request=" + (__0 != null) + ", nativeMovedOnly=" + transactionCheck +
                ", first=" + active.Parent(active.groupFirst) + ", second=" + active.Parent(active.groupSecond));
            return;
        }
        active.observedStates.Clear();
        if (active.pass != 2) return;
        foreach (GameItem item in active.residents.Cast<GameItem>().Concat(new[] { active.single }))
            active.observedStates[item] = State(item);
    }
    private static void ObserveCompleted()
    {
        if (active == null) return;
        foreach (var pair in active.observedStates)
        {
            string after = State(pair.Key);
            if (pair.Value != after) Debug.Log("[PLAYER-QA] arrange state changed " + pair.Key.identifier +
                " before=" + pair.Value + " after=" + after);
        }
        active.observedStates.Clear();
    }
    private string GuardDiagnostics()
    {
        var mod = ModLoader.Mods.FirstOrDefault(value => value.Manifest?.ID == Mod && value.Instance != null);
        var type = mod?.Instance.GetType().Assembly.GetType("ShopAssist.InventoryAutoInsert");
        string Inspect(string name, object value)
        {
            try { return name + "=" + type?.GetMethod(name, Fields)?.Invoke(null, value == null ? null : new[] { value }); }
            catch (Exception error) { return name + "=" + error.GetBaseException().Message; }
        }
        string FieldsOn(object value)
        {
            if (value == null) return "none";
            string[] names = { "mayInventoryAddItemFunc", "mayInventoryRemoveItemFunc", "mayStackItemFunc", "maxSlotAddItemFunc",
                "maxSlotRemoveItemFunc", "maxSlotAmountItemFunc", "minSlotAmountItemFunc", "isItemStateValidFunc", "onSlotAddItemFunc",
                "onSlotAddItemEndFunc", "onSlotAddItemLateFunc", "onSlotRemoveItemFunc", "onSlotRemoveItemEndFunc",
                "onSlotRemoveItemLateFunc", "onSlotChangeAmountItemFunc", "onChangeAmountItemFunc", "parentItemShapeModifierForItemFunc",
                "parentItemStateModifierForItemFunc", "parentItemSpriteModifierForItemFunc", "getItemSpriteModifierForItemFunc", "slotPriorityFunc" };
            return string.Join("/", names.Select(name => (name, callback: value.GetType().GetProperty(name)?.GetValue(value) as Delegate))
                .Where(pair => pair.callback != null).SelectMany(pair => pair.callback.GetInvocationList().Select(callback => pair.name + ":" +
                    callback.Method.DeclaringType?.FullName + "." + callback.Method.Name + "@" + callback.Method.Module.Assembly.GetName().Name +
                    "[" + (callback.Target?.GetType().FullName ?? "static") + "]")));
        }
        GameGridInventory source = groupSource ?? stock;
        GameItem item = groupSecond ?? single;
        return Inspect("KnownMutationPatches", null) + ", " + Inspect("SafeGrid", source) + "[" + FieldsOn(source) + "], target=" +
            Inspect("SafeGrid", target) + "[" + FieldsOn(target) + "], item=" + Inspect("SafeItem", item) + "[" + FieldsOn(item) +
            "]{type=" + item?.GetType().FullName + ",content=" + (item?.contentWindow != null) + ",children=" + item?.children?.Count +
            ",itemDescendants=" + (item == null ? -1 : GraphUtils.FindAllChildrenType<GameItem>(item, null, null).Count) +
            ",backInv=" + (item?.GetParentWithIdentifier("backInv") != null) + ",features=" +
            (item?.itemFeatures == null ? "null" : string.Join("/", item.itemFeatures.Select(feature => feature == null ? "null" :
                feature.featureType + ":" + feature.category))) + "}, first=" + Inspect("SafeItem", groupFirst) + "[" + FieldsOn(groupFirst) + "]";
    }
    private string Parent(GameItem item) => ReferenceEquals(item?.parentInventory, target) ? "target" :
        ReferenceEquals(item?.parentInventory, groupSource) ? "source" : item?.parentInventory?.identifier ?? "none";
    private static GameItemElement FindOneCell()
    {
        foreach (string id in new[] { "small_morsel", "processed_cheese", "cat_bar", "cup_noodle", "processed_meat", "processed_milk" })
        {
            var item = DirectoryMaster.Item(id) as GameItemElement;
            if (item == null) continue;
            if (item.modifiedShape.globalWidth == 1 && item.modifiedShape.globalHeight == 1 && item.identifier != "wine_superyeast")
            { GeneralHelper.SetItemOwned(item, true); return item; }
            item.Destroy();
        }
        throw new InvalidOperationException("No native one-cell group fixture found");
    }
    private void BeginSingleDrag()
    {
        EmporiumEntry.Instance.invWindow.ToFront(); input.Mouse = ItemCell(stock, single); PressLeft();
    }
    private void MoveToTarget() { ClearDown(); targetWindow.ToFront(); input.Mouse = EmptyCell(target); }
    private void PressLeft() { input.MouseDown = 0; input.MouseHeld = 0; input.Held = KeyCode.Mouse0; }
    private void ReleaseLeft() { input.MouseDown = -1; input.MouseHeld = -1; input.Held = KeyCode.None; input.MouseUp = 0; }
    private void ClearDown() { input.MouseDown = -1; }
    private void ClearInput() { input.MouseDown = input.MouseUp = input.MouseHeld = -1; input.Held = input.Down = input.Up = KeyCode.None; }
    private void RightDown(Vector2 point) { input.Mouse = point; input.MouseDown = 1; input.MouseHeld = 1; input.Down = ItemContextHandler.current.triggerKey; }
    private void Next(int value, int frames = 1) { step = value; readyFrame = Time.frameCount + frames; }
    private static PixelWindow Window(string name) => typeof(ItemContextHandler).GetField(name, Fields)?.GetValue(ItemContextHandler.current) as PixelWindow;
    private static GameItemElement New(string id)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement;
        Need(item != null, "Missing native fixture " + id); GeneralHelper.SetItemOwned(item, true); return item;
    }
    private static void Put(GameInventory inventory, GameItem item)
    {
        SlotMarker marker = inventory.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.targetItem == null && marker.TryAcceptOnce(item.unitCount) == item.unitCount, "Native fixture insertion failed");
    }
    private static void PutAt(GameGridInventory grid, GameItem item, int x, int y, int orientation = 0)
    {
        GridShape pose = new GridShapeBuilder(item.modifiedShape).SetOrientation(orientation).SetPosition(x, y).Build();
        SlotMarker marker = grid.TryInventorySlot(item, item.unitCount, pose);
        Need(marker != null && marker.targetItem == null && marker.numTransfer >= item.unitCount && marker.TryAcceptOnce(item.unitCount) == item.unitCount,
            "Native exact fixture placement failed at " + x + "," + y);
    }
    private bool TargetOriginal() => residents.All(item => item.parentInventory == target && item.unitCount == 1 && Same(item.modifiedShape, targetBefore[item]));
    private static Vector2 EmptyCell(GameGridInventory grid)
    {
        Canvas.ForceUpdateCanvases();
        for (int y = 0; y < grid.inventoryShape.height; y++) for (int x = 0; x < grid.inventoryShape.width; x++)
        {
            if (grid.childItems.Any(item => item.modifiedShape.Intersects(x, y))) continue;
            Vector2 point = Cell(grid, x, y);
            if (RenderHandler.RaycastElement<GameItem>(point) == null && ReferenceEquals(RenderHandler.RaycastElement<GameGridInventory>(point), grid)) return point;
        }
        throw new InvalidOperationException("No raycastable target cell");
    }
    private static Vector2 ItemCell(GameGridInventory grid, GameItem item)
    {
        Canvas.ForceUpdateCanvases();
        for (int y = item.modifiedShape.minY; y <= item.modifiedShape.maxY; y++) for (int x = item.modifiedShape.minX; x <= item.modifiedShape.maxX; x++)
        {
            Vector2 point = Cell(grid, x, y);
            if (item.modifiedShape.Get(x, y) != 0 && ReferenceEquals(RenderHandler.RaycastElement<GameItem>(point), item)) return point;
        }
        throw new InvalidOperationException("No raycastable source fixture " + item.identifier);
    }
    private static Vector2 Cell(GameGridInventory grid, int x, int y)
    {
        Canvas canvas = grid.handler.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.WorldToScreenPoint(camera,
            grid.rectTransform.TransformPoint(new Vector3((x + .5f) * UISettings.current.gridSize, -(y + .5f) * UISettings.current.gridSize)));
    }
    private static Vector2 ButtonPoint(PixelWindow window, string name)
    {
        Canvas.ForceUpdateCanvases();
        Transform node = window.handler.GetComponentsInChildren<Transform>(true).FirstOrDefault(value => value.name == name && value.gameObject.activeInHierarchy);
        var button = node?.GetComponent<TreeNodeRender>()?.element as RichTextElement;
        return ButtonPoint(button, name);
    }
    private static Vector2 NativeUndoPoint()
    {
        var button = typeof(ItemContextHandler).GetField("buttonSortUndo", Fields)?.GetValue(ItemContextHandler.current) as RichTextElement;
        Need(button?.handler != null && button.handler.gameObject.activeInHierarchy, "Native grid undo row is missing");
        return ButtonPoint(button, "native grid undo");
    }
    private static Vector2 ButtonPoint(RichTextElement button, string name)
    {
        Canvas.ForceUpdateCanvases();
        var background = button == null ? null : typeof(RichTextElement).GetField("background", Fields)?.GetValue(button) as TreeNodeRender;
        Need(background != null, "Missing auto undo row " + name);
        Canvas canvas = ItemContextHandler.current.overlayCanvas;
        Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.WorldToScreenPoint(camera, background.rectTransform.TransformPoint(background.rectTransform.rect.center));
    }
    private static bool Same(GridShape left, GridShape right) => left.minX == right.minX && left.minY == right.minY &&
        left.orientation == right.orientation && left.flipped == right.flipped;
    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
}
