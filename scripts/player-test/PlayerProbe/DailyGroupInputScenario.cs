using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Drives the game's selection, drag, rotation and release callbacks in the QA Player.
public class DailyGroupInputScenario
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private QaSyntheticInput input;
    private GameItem sourceBag, targetBag;
    private GameItemElement first, second, blocker;
    private GameGridInventory source, target;
    private ItemMultiSelectHandler handler;
    private GridShape firstHome, secondHome;
    private int firstX, firstY, secondX, secondY;
    private QaLoadSlot load;
    private int savedBag, savedFirst, savedSecond;
    private int step, readyFrame;
    private float since = Time.realtimeSinceStartup;
    protected virtual bool SwitchOnly => false;

    public bool Tick(out string result)
    {
        try
        {
            bool complete = Advance(out result);
            if (complete) Cleanup();
            return complete;
        }
        catch { Cleanup(); throw; }
    }

    private bool Advance(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - since < 60f, "Group input QA timed out at step " + step);
        if (step == 0) { if (start.Tick()) Next(1); return false; }
        if (step == 1)
        {
            if (StoreUIManager.Instance?.gameOptionUIManager == null || ItemMultiSelectHandler.current == null ||
                ItemMouseDoubleClickHandler.current == null || EmporiumEntry.Instance?.invElement == null) return false;
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            Need(EmporiumEntry.Instance.invWindow.IsVisible() && !EmporiumEntry.Instance.invWindow.canMove &&
                StoreUIManager.Instance.IsInnateWindow(EmporiumEntry.Instance.invWindow), "Native main stock is not naturally ready");
            TradeScenario.SetOptions(false, "f40.enabled");
            TradeScenario.SetOptions(!SwitchOnly, "f41.enabled");
            sourceBag = New("fanny_pack"); targetBag = New("fanny_pack");
            Put(EmporiumEntry.Instance.invElement, sourceBag); Put(EmporiumEntry.Instance.invElement, targetBag);
            source = sourceBag.contentWindow?.childElement as GameGridInventory;
            target = targetBag.contentWindow?.childElement as GameGridInventory;
            Need(source?.inventoryShape != null && target?.inventoryShape != null, "Native container grids unavailable");
            first = New("wine_yeast") as GameItemElement;
            second = New("mini_bottle") as GameItemElement;
            blocker = New("wine_superyeast") as GameItemElement;
            Need(first?.modifiedShape != null && second?.modifiedShape != null && blocker?.modifiedShape != null,
                "Native group fixtures or their shapes are unavailable");
            Need(Separate(first, second) && Separate(first, blocker) && Separate(second, blocker),
                "Native fixtures can stack and would not remain separate");
            firstX = firstY = secondY = 1;
            GridShape firstPose = new GridShapeBuilder(first.modifiedShape).SetTransform(firstX, firstY, false, 0).Build();
            GridShape secondPose = new GridShapeBuilder(second.modifiedShape).SetTransform(0, 0, false, 0).Build();
            GridShape blockerPose = new GridShapeBuilder(blocker.modifiedShape).SetTransform(0, 0, false, 0).Build();
            secondX = firstX + firstPose.globalWidth;
            int blockerX = target.inventoryShape.width - blockerPose.globalWidth;
            int blockerY = target.inventoryShape.height - blockerPose.globalHeight;
            Need(secondX + secondPose.globalWidth <= source.inventoryShape.width &&
                secondY + Math.Max(firstPose.globalHeight, secondPose.globalHeight) <= source.inventoryShape.height &&
                blockerX >= 0 && blockerY >= 0, "Native container grids are too small for the actual fixture shapes");
            PutAt(source, first, firstX, firstY); PutAt(source, second, secondX, secondY);
            PutAt(target, blocker, blockerX, blockerY);
            Need(first.MayRemove() && second.MayRemove() && blocker.MayRemove() &&
                first.unitCount > 0 && second.unitCount > 0 && blocker.unitCount > 0 &&
                first.MaxNumRemove() >= first.unitCount && second.MaxNumRemove() >= second.unitCount &&
                Separate(first, second) && Separate(first, blocker) && Separate(second, blocker),
                "Native group fixtures are not movable or became stackable");
            Debug.Log("[PLAYER-QA] Native group fixtures: " + first.identifier + "=" + first.modifiedShape.globalWidth + "x" + first.modifiedShape.globalHeight +
                ", " + second.identifier + "=" + second.modifiedShape.globalWidth + "x" + second.modifiedShape.globalHeight +
                ", " + blocker.identifier + "=" + blocker.modifiedShape.globalWidth + "x" + blocker.modifiedShape.globalHeight +
                "; grids=" + source.inventoryShape.width + "x" + source.inventoryShape.height + "/" + target.inventoryShape.width + "x" + target.inventoryShape.height);
            firstHome = first.modifiedShape.Clone(); secondHome = second.modifiedShape.Clone();
            ItemMouseDoubleClickHandler.current.OpenContentAction(sourceBag, new Vector2(120, 180));
            ItemMouseDoubleClickHandler.current.OpenContentAction(targetBag, new Vector2(Screen.width - 200, 180));
            Need(sourceBag.contentWindow.IsVisible() && targetBag.contentWindow.IsVisible(), "Native container windows did not open");
            handler = ItemMultiSelectHandler.current;
            input = new QaSyntheticInput(true); input.Mouse = new Vector3(-200, -200);
            readyFrame = Time.frameCount + 6; Next(2); return false;
        }
        if (step == 2)
        {
            if (Time.frameCount < readyFrame) return false;
            SelectAndDrag();
            Rotate();
            Need(first.tempShape.orientation == firstHome.orientation && second.tempShape.orientation == secondHome.orientation,
                "Disabled F40 still rotated the dragged group");
            if (SwitchOnly)
            {
                Need(!NativeModifier(), "Both disabled: native precision modifier was unexpectedly held");
                input.Mouse = EmptyTarget(); UpdateDrag(80);
                Need(!(bool)Field("precisePlacementActive"), "Both disabled: native cross-grid drag unexpectedly entered precision mode");
                Release();
                Need(first.parentInventory == target && second.parentInventory == target && blocker.parentInventory == target,
                    "Both disabled: native cross-grid drop did not move the group");
                RestoreSource();
                Need(Same(first.modifiedShape, firstHome) && Same(second.modifiedShape, secondHome),
                    "Could not restore the source fixtures for the independent switch check");
                input.Held = KeyCode.None; input.Mouse = new Vector3(-200, -200);
                TradeScenario.SetOptions(true, "f40.enabled");
                readyFrame = Time.frameCount + 5; Next(4); return false;
            }
            input.Mouse = ItemPoint(target, blocker);
            UpdateDrag(30);
            Need(HasSprite("hoverHighlightNodes", RenderHandler.current.redSquare), "Blocked group destination was not shown in red");
            Release();
            Need(!handler.IsGroupDragging && first.parentInventory == source && second.parentInventory == source &&
                Same(first.modifiedShape, firstHome) && Same(second.modifiedShape, secondHome) && blocker.parentInventory == target,
                "Dropping on a blocked cell changed the original formation");
            input.Held = KeyCode.None; input.Mouse = new Vector3(-200, -200);
            TradeScenario.SetOptions(true, "f40.enabled");
            readyFrame = Time.frameCount + 5; Next(3); return false;
        }
        if (step == 3)
        {
            if (Time.frameCount < readyFrame) return false;
            SelectAndDrag();
            Rotate();
            Need(first.tempShape.orientation == (firstHome.orientation + 1) % 4 &&
                second.tempShape.orientation == (secondHome.orientation + 1) % 4, "Native rotation key did not rotate both ghosts");
            FindGreen(target);
            Release();
            CheckRotatedDrop(target);
            result = "PASS F40/F41 native band, press/drag, rotation key, red collision rejection and green exact group drop";
            return true;
        }
        if (step == 4)
        {
            if (Time.frameCount < readyFrame) return false;
            SelectAndDrag();
            Need(!NativeModifier(), "F41 disabled while F40 enabled forced the native precision modifier without Shift");
            Rotate();
            Need(first.tempShape.orientation == (firstHome.orientation + 1) % 4 &&
                second.tempShape.orientation == (secondHome.orientation + 1) % 4, "F40 rotation stopped when F41 was disabled");
            input.Mouse = EmptyTarget(); UpdateDrag(80);
            Need(!(bool)Field("precisePlacementActive"), "F41 disabled forced precise placement without Shift");
            input.HeldModifier = KeyCode.LeftShift;
            Need(NativeModifier(), "F41 disabled lost the native Shift precision modifier");
            UpdateDrag(10);
            Need((bool)Field("precisePlacementActive"), "Native Shift did not enable precision mode");
            input.HeldModifier = KeyCode.None;
            UpdateDrag(10);
            Need(!NativeModifier() && !(bool)Field("precisePlacementActive"), "Releasing Shift did not restore the native mode");
            Release();
            Need(first.parentInventory == target && second.parentInventory == target, "Native cross-grid drop failed with F41 disabled");
            Need(Tracking() == null, "Native cross-grid drop retained an old group snapshot");
            RestoreSource();
            firstHome = first.modifiedShape.Clone(); secondHome = second.modifiedShape.Clone();
            readyFrame = Time.frameCount + 5; Next(5); return false;
        }
        if (step == 5)
        {
            if (Time.frameCount < readyFrame) return false;
            SelectAndDrag(); Rotate();
            input.HeldModifier = KeyCode.LeftShift;
            FindGreen(target); Release(); input.HeldModifier = KeyCode.None;
            CheckRotatedDrop(target);
            RestoreSource();
            firstHome = first.modifiedShape.Clone(); secondHome = second.modifiedShape.Clone();
            readyFrame = Time.frameCount + 5; Next(6); return false;
        }
        if (step == 6)
        {
            if (Time.frameCount < readyFrame) return false;
            SelectAndDrag(); Rotate();
            Need(!NativeModifier(), "Same-grid F40 drag unexpectedly requires a precision modifier");
            FindGreen(source); Release();
            CheckRotatedDrop(source);
            Put(target, first); Put(target, second);
            RestoreSource();
            firstHome = first.modifiedShape.Clone(); secondHome = second.modifiedShape.Clone();
            readyFrame = Time.frameCount + 5; Next(7); return false;
        }
        if (step == 7)
        {
            if (Time.frameCount < readyFrame) return false;
            SelectAndDrag(); Rotate();
            TradeScenario.SetOptions(false, "f40.enabled");
            Invoke("OnEventUpdate");
            Need(!handler.IsGroupDragging && Tracking() == null && first.parentInventory == source && second.parentInventory == source &&
                Same(first.modifiedShape, firstHome) && Same(second.modifiedShape, secondHome),
                "Changing F40 during a drag did not cancel and clear the old formation");
            input.Held = KeyCode.None; input.Mouse = new Vector3(-200, -200);
            TradeScenario.SetOptions(true, "f40.enabled");
            readyFrame = Time.frameCount + 5; Next(71); return false;
        }
        if (step == 71)
        {
            if (Time.frameCount < readyFrame) return false;
            SelectAndDrag(); Rotate();
            TradeScenario.SetOptions(true, "f41.enabled");
            Invoke("OnEventUpdate");
            Need(!handler.IsGroupDragging && Tracking() == null && first.parentInventory == source && second.parentInventory == source &&
                Same(first.modifiedShape, firstHome) && Same(second.modifiedShape, secondHome),
                "Changing F41 during a drag did not cancel and clear the old formation");
            input.Held = KeyCode.None; input.Mouse = new Vector3(-200, -200);
            TradeScenario.SetOptions(false, "f41.enabled");
            readyFrame = Time.frameCount + 5; Next(8); return false;
        }
        if (step == 8)
        {
            if (Time.frameCount < readyFrame) return false;
            SelectAndDrag(); Rotate();
            Need(handler.Cancel() && !handler.IsGroupDragging && Tracking() == null &&
                Same(first.modifiedShape, firstHome) && Same(second.modifiedShape, secondHome), "Native cancellation did not clear a rotated group");
            input.Held = KeyCode.None;
            savedBag = sourceBag.GetUniqueID(); savedFirst = first.GetUniqueID(); savedSecond = second.GetUniqueID();
            var player = PlayerStore.Instance;
            Need(Guid.TryParse(player.runID, out Guid run) && run != Guid.Empty, "Isolated run ID unavailable");
            int slot = player.saveSlotId; player.SaveGame();
            readyFrame = Time.frameCount + 5; Next(9);
            load = new QaLoadSlot(slot, run); return false;
        }
        if (step == 9)
        {
            if (Time.frameCount < readyFrame) return false;
            SelectAndDrag(); Rotate();
            input.Held = KeyCode.None;
            StoreUIManager.Instance.escapeUIManager.OnMainMenu();
            Next(10); return false;
        }
        if (step == 10)
        {
            if (!load.Tick()) return false;
            Need(Tracking() == null, "Native load retained a group snapshot from the previous scene");
            var bag = EmporiumEntry.Instance.invElement.childItems.SingleOrDefault(item => item.uniqueId == savedBag);
            var restored = bag?.contentWindow?.childElement as GameGridInventory;
            var one = restored?.childItems.SingleOrDefault(item => item.uniqueId == savedFirst);
            var two = restored?.childItems.SingleOrDefault(item => item.uniqueId == savedSecond);
            Need(one != null && two != null && Same(one.modifiedShape, firstHome) && Same(two.modifiedShape, secondHome),
                "Save/load after a group drag did not preserve the saved source items");
            result = "PASS independent F40/F41: native automatic, Shift and same-grid precision, rotation, switch/cancel/load cleanup";
            return true;
        }
        throw new InvalidOperationException("Unknown group input QA step " + step);
    }

    private Vector3 EmptyTarget()
    {
        foreach (var (x, y) in CellsNearCenter(target))
        {
            Vector3 point = Cell(target, x, y);
            if (RenderHandler.RaycastElement<GameItem>(point) == null &&
                ReferenceEquals(RenderHandler.RaycastFindParentElement<GameGridInventory>(point), target)) return point;
        }
        throw new InvalidOperationException("No visible empty target cell");
    }

    private void FindGreen(GameGridInventory grid)
    {
        float deadline = Time.realtimeSinceStartup + 12f;
        foreach (var (x, y) in CellsNearCenter(grid))
        {
            Need(Time.realtimeSinceStartup < deadline, "Timed out moving the native drag over destination cells");
            Vector3 point = Cell(grid, x, y);
            GameItem hit = RenderHandler.RaycastElement<GameItem>(point);
            if (hit != null && !ReferenceEquals(hit, first) && !ReferenceEquals(hit, second)) continue;
            if (!ReferenceEquals(RenderHandler.RaycastFindParentElement<GameGridInventory>(point), grid)) continue;
            input.Mouse = point; UpdateDrag(80);
            if (HasSprite("precisePreviewNodes", RenderHandler.current.greenSquare)) return;
        }
        throw new InvalidOperationException("Native pointer could not find a green group destination");
    }

    private void CheckRotatedDrop(GameGridInventory destination)
    {
        var firstRotated = Cells(firstHome).Select(cell => (x: cell.y, y: -cell.x)).ToArray();
        var firstActual = Cells(first.modifiedShape).ToArray();
        Need(firstRotated.Length > 0 && firstActual.Length > 0, "Rotated fixture footprint is empty");
        int dx = firstActual.Min(cell => cell.x) - firstRotated.Min(cell => cell.x);
        int dy = firstActual.Min(cell => cell.y) - firstRotated.Min(cell => cell.y);
        var expectedFirst = firstRotated.Select(cell => (cell.x + dx, cell.y + dy)).ToHashSet();
        var expectedSecond = Cells(secondHome).Select(cell => (cell.y + dx, -cell.x + dy)).ToHashSet();
        Need(!handler.IsGroupDragging && first.parentInventory == destination && second.parentInventory == destination &&
            first.modifiedShape.orientation == (firstHome.orientation + 1) % 4 &&
            second.modifiedShape.orientation == (secondHome.orientation + 1) % 4 &&
            expectedFirst.SetEquals(firstActual) && expectedSecond.SetEquals(Cells(second.modifiedShape)) &&
            !first.modifiedShape.Intersects(second.modifiedShape) && blocker.parentInventory == target,
            "Green drop did not retain both rotated items and their relative placement");
    }

    private static IEnumerable<(int x, int y)> Cells(GridShape shape)
    {
        for (int y = shape.minY; y <= shape.maxY; y++) for (int x = shape.minX; x <= shape.maxX; x++)
            if (shape.Get(x, y) == 1) yield return (x, y);
    }

    private static IEnumerable<(int x, int y)> CellsNearCenter(GameGridInventory grid)
    {
        var points = new List<(int x, int y)>();
        for (int y = 0; y < grid.inventoryShape.height; y++) for (int x = 0; x < grid.inventoryShape.width; x++)
            points.Add((x, y));
        float cx = (grid.inventoryShape.width - 1) / 2f, cy = (grid.inventoryShape.height - 1) / 2f;
        return points.OrderBy(point => (point.x - cx) * (point.x - cx) + (point.y - cy) * (point.y - cy));
    }

    private static object Tracking()
    {
        var mod = ModLoader.Mods.Find(value => value.Manifest?.ID == "starswhere.shopassist")?.Instance;
        var type = mod?.GetType().Assembly.GetType("ShopAssist.GroupPlacement");
        var field = type?.GetField("active", BindingFlags.Static | BindingFlags.NonPublic);
        Need(field != null, "Group snapshot state unavailable");
        return field.GetValue(null);
    }

    private static bool NativeModifier()
    {
        var method = typeof(ItemMultiSelectHandler).GetMethod("IsPreciseModifierHeld", BindingFlags.Static | BindingFlags.NonPublic);
        Need(method != null, "Native precision modifier entry unavailable");
        return (bool)method.Invoke(null, null);
    }

    private void SelectAndDrag()
    {
        Canvas.ForceUpdateCanvases();
        int left = Math.Min(first.modifiedShape.minX, second.modifiedShape.minX) - 1;
        int top = Math.Min(first.modifiedShape.minY, second.modifiedShape.minY) - 1;
        int right = Math.Min(source.inventoryShape.width - 1, Math.Max(first.modifiedShape.maxX, second.modifiedShape.maxX) + 1);
        int bottom = Math.Min(source.inventoryShape.height - 1, Math.Max(first.modifiedShape.maxY, second.modifiedShape.maxY) + 1);
        Need(left >= 0 && top >= 0 && right >= Math.Max(first.modifiedShape.maxX, second.modifiedShape.maxX) &&
            bottom >= Math.Max(first.modifiedShape.maxY, second.modifiedShape.maxY), "Cannot encompass the actual formation bounds");
        Vector3 begin = Cell(source, left, top), end = Cell(source, right, bottom);
        Need(RenderHandler.RaycastElement<GameItemElement>(begin) == null &&
            ReferenceEquals(RenderHandler.RaycastFindParentElement<GameGridInventory>(begin), source) &&
            ReferenceEquals(RenderHandler.RaycastFindParentElement<GameGridInventory>(end), source), "Band endpoints do not lie on the source grid");
        input.Mouse = begin; input.Held = handler.key;
        Need(Vector3.Distance(Input.mousePosition, begin) < 0.01f && Input.GetKey(handler.key), "QA input injection is unavailable");
        StoreUIManager.Instance.CheckRaycast();
        Need(!StoreUIManager.Instance.hoveringStoreUI, "A native store panel covers the source grid");
        Invoke("OnEventPress", new HashSet<KeyCode> { handler.key });
        input.Mouse = end; StoreUIManager.Instance.CheckRaycast();
        Need(!StoreUIManager.Instance.hoveringStoreUI, "A native store panel covers the selection end");
        Invoke("OnEventMouseMove", end, end - begin);
        Invoke("OnEventRelease", new HashSet<KeyCode> { handler.key });
        var selected = Field("selectedItems") as IList;
        Need(State() == "Selected" && selected != null && selected.Count == 2 && selected.Contains(first) && selected.Contains(second),
            "Native band did not select the two source items");
        Vector3 itemPoint = ItemPoint(source, first);
        input.Mouse = itemPoint; StoreUIManager.Instance.CheckRaycast();
        Invoke("OnEventPress", new HashSet<KeyCode> { handler.key });
        Need(State() == "PendingGroupDrag", "Native press did not begin the group drag gesture");
        float threshold = handler.dragThreshold;
        Vector3 moved = itemPoint + new Vector3(Mathf.Max(threshold + 2f, 12f), 0);
        input.Mouse = moved; Invoke("OnEventMouseMove", moved, moved - itemPoint);
        Need(handler.IsGroupDragging, "Native group drag did not start");
    }

    private void Rotate()
    {
        Need(ItemMouseDragHandler.current != null, "Native item rotation input is unavailable");
        input.Down = ItemMouseDragHandler.current.rotateCCWKey;
        Need(input.Down != KeyCode.None && Input.GetKeyDown(input.Down), "QA rotation key injection is unavailable");
        try { Invoke("OnEventUpdate"); }
        finally { input.Down = KeyCode.None; }
    }

    private void UpdateDrag(int repetitions)
    {
        for (int i = 0; i < repetitions; i++) Invoke("OnEventUpdate");
    }

    private void Release()
    {
        Invoke("OnEventRelease", new HashSet<KeyCode> { handler.key });
        input.Held = KeyCode.None;
    }

    private bool HasSprite(string field, Sprite sprite)
    {
        var nodes = Field(field) as IEnumerable;
        return sprite != null && nodes != null && nodes.Cast<object>().OfType<TreeNodeRender>()
            .Any(node => node != null && node.image != null && node.image.enabled && node.image.sprite == sprite);
    }

    private object Field(string name) => typeof(ItemMultiSelectHandler).GetField(name, Fields)?.GetValue(handler) ??
        throw new InvalidOperationException("Native group field unavailable: " + name);
    private string State() => Field("state").ToString();
    private void Invoke(string name, params object[] args)
    {
        MethodInfo method = typeof(ItemMultiSelectHandler).GetMethod(name, Fields);
        Need(method != null, "Native input callback unavailable: " + name);
        try { method.Invoke(handler, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }

    private static Vector3 Cell(GameGridInventory grid, int x, int y)
    {
        var rect = grid.rectTransform;
        var canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        float size = UISettings.current.gridSize;
        return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(new Vector3((x + .5f) * size, -(y + .5f) * size)));
    }

    private static Vector3 ItemPoint(GameGridInventory grid, GameItemElement item)
    {
        foreach (var (x, y) in Cells(item.modifiedShape))
        {
            Vector3 point = Cell(grid, x, y);
            if (ReferenceEquals(RenderHandler.RaycastElement<GameItemElement>(point), item)) return point;
        }
        throw new InvalidOperationException("Native pointer cannot hit " + item.identifier + " in its actual occupied cells");
    }

    private static GameItem New(string id)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing native fixture " + id);
        GeneralHelper.SetItemOwned(item, true); return item;
    }
    private static bool Separate(GameItem a, GameItem b) => !a.MayStack(b) && !b.MayStack(a);
    private void RestoreSource() { PutAt(source, first, firstX, firstY); PutAt(source, second, secondX, secondY); }
    private static void Put(GameInventory grid, GameItem item)
    {
        var slot = grid.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == grid && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid, "Could not place " + item.identifier);
    }
    private static void PutAt(GameGridInventory grid, GameItem item, int x, int y)
    {
        var shape = new GridShapeBuilder(item.modifiedShape).SetTransform(x, y, false, 0);
        var slot = grid.TryInventorySlot(item, item.unitCount, shape);
        Need(slot != null && slot.inventory == grid && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid, "Could not place exact fixture " + item.identifier);
    }
    private static bool Same(GridShape a, GridShape b) => a != null && b != null && a.minX == b.minX && a.minY == b.minY &&
        a.orientation == b.orientation && a.flipped == b.flipped;
    private void Cleanup()
    {
        try { if (handler != null && handler.IsGroupDragging) handler.Cancel(); }
        finally { input?.Dispose(); input = null; }
    }
    private void Next(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

public sealed class DailyGroupSwitchScenario : DailyGroupInputScenario
{
    protected override bool SwitchOnly => true;
}
