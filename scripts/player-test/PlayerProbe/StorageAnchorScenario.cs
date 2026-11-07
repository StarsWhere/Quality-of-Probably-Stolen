using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Uses the original scene's fixed main storage, without calling PixelWindow.Show on it.
public sealed class StorageAnchorScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120;
    private readonly int targetWidth, targetHeight, targetResolution;
    private PixelWindow stock;
    private Transform originalParent;
    private Vector2 originalPosition;
    private GameItem machine, bag;
    private GameItemElement cornerItem;
    private GridShape cornerShape;
    private int cornerCount;
    private Vector2 cornerPoint;
    private QaSyntheticInput input;
    private MachineryButton stockButton;
    private int step, frame;
    private int originalMode, originalResolution, originalDivisor;
    private float stableAfter;
    private bool restoreDisplay, displayReady;

    public StorageAnchorScenario() { }

    internal StorageAnchorScenario(int width, int height, int resolution)
    {
        targetWidth = width; targetHeight = height; targetResolution = resolution;
    }

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch
        {
            try { RestoreDisplay(); }
            catch (Exception error) { Debug.LogWarning("[PLAYER-QA] Could not restore display settings: " + error.Message); }
            throw;
        }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Storage anchor QA timed out at step " + step +
            " target=" + targetWidth + "x" + targetHeight + " actual=" + Screen.width + "x" + Screen.height);
        if (step == 0) { if (start.Tick()) step = targetWidth > 0 ? 10 : 1; return false; }
        if (step == 10)
        {
            if (EmporiumOption.Instance == null || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true) return false;
            Need(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null,
                "Storage scale scenario requires a rendered Player");
            Need(EmporiumOption.GetResolutionValueFromDimensions(targetWidth, targetHeight) == targetResolution,
                "The requested native resolution index changed");
            originalMode = EmporiumOption.Instance.fullscreenMode;
            originalResolution = EmporiumOption.Instance.resolution;
            originalDivisor = EmporiumOption.Instance.borderlessDivisor;
            restoreDisplay = true;
            EmporiumOption.Instance.fullscreenMode = 2;
            EmporiumOption.Instance.resolution = targetResolution;
            EmporiumOption.Instance.ApplySetting();
            stableAfter = Time.realtimeSinceStartup + 1.5f; frame = Time.frameCount + 10;
            step = 11; return false;
        }
        if (step == 11)
        {
            if (Screen.width != targetWidth || Screen.height != targetHeight ||
                Time.realtimeSinceStartup < stableAfter || Time.frameCount < frame) return false;
            Need(EmporiumOption.Instance.fullscreenMode == 2 && EmporiumOption.Instance.resolution == targetResolution,
                "Native display options changed before the stock check");
            displayReady = true; step = 1; return false;
        }
        if (displayReady)
            Need(Screen.width == targetWidth && Screen.height == targetHeight,
                "Native display size changed during the stock check: " + Screen.width + "x" + Screen.height);
        if (step == 1)
        {
            var entry = EmporiumEntry.Instance;
            if (entry?.invWindow == null || entry.invElement == null || entry.storeInteractables?.activeInHierarchy != true ||
                StoreUIManager.Instance?.gameOptionUIManager == null || ItemMouseDoubleClickHandler.current == null || ItemMouseDragHandler.current == null ||
                EventSystem.current == null || RenderHandler.current?.windowUICanvasObject == null) return false;
            stock = entry.invWindow;
            Need(stock.IsVisible() && !stock.canMove && StoreUIManager.Instance.IsInnateWindow(stock), "Main storage is not the native fixed scene inventory");
            Debug.Log("[PLAYER-QA] Storage anchor " + Screen.width + "x" + Screen.height +
                " canvasScale=" + stock.handler.GetComponentInParent<Canvas>()?.scaleFactor + " gridSize=" + UISettings.current?.gridSize);
            originalParent = stock.rectTransform.parent; originalPosition = stock.rectTransform.anchoredPosition;
            entry.Validate();
            CheckAnchor("the native scene layout validation");
            TradeScenario.SetOptions(true, "f07.enabled", "f10.enabled", "f30.enabled");
            CheckAnchor("changing options");
            machine = FeedScenario.New("hydroponic"); bag = FeedScenario.New("backpack_medium");
            FeedScenario.Put(entry.invElement, machine); FeedScenario.Put(entry.invElement, bag);
            cornerItem = PlaceTopRight(entry.invElement);
            cornerShape = cornerItem.modifiedShape.Clone(); cornerCount = cornerItem.unitCount;
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(180, Screen.height - 180));
            Need(machine.contentWindow.IsVisible(), "Machine window did not open");
            CheckAnchor("opening a machine");
            frame = Time.frameCount + 3; step = 2; return false;
        }
        if (Time.frameCount < frame) return false;
        if (step == 2)
        {
            var button = Button(machine.contentWindow, "ShopAssist_Action_f10_open");
            if (button == null || button.isDisabled) return false;
            button.Select();
            var browser = Ui("f10"); Need(browser?.IsAlive == true, "Machine shortcut did not open the module workbench");
            CheckAnchor("opening the module workbench");
            foreach (string tab in new[] { "installed_tab", "storage_tab", "idle_tab" })
            {
                Ui("f10").Get(tab).button.onClick.Invoke();
                CheckAnchor("switching module tab " + tab);
            }
            Canvas.ForceUpdateCanvases();
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath,
                    "qa-storage-anchor-" + Screen.width + "x" + Screen.height + ".png"));
            frame = Time.frameCount + 3; step = 3; return false;
        }
        if (step == 3)
        {
            CheckAnchor("after rendering the workbench");
            Ui("f10")?.Close();
            if (machine.contentWindow.IsVisible()) machine.contentWindow.Hide();
            stockButton = Button(stock, "ShopAssist_Action_f30_open");
            if (stockButton == null || stockButton.isDisabled) return false;
            Canvas.ForceUpdateCanvases();
            CheckStockGeometry(stockButton, EmporiumEntry.Instance.invElement, cornerItem);
            Need(HitNativeButton(stockButton), "Native main storage machine-status button is not reachable");
            cornerPoint = ItemPoint(EmporiumEntry.Instance.invElement, cornerItem);
            Need(ReferenceEquals(RenderHandler.RaycastElement<GameItem>(cornerPoint), cornerItem),
                "The upper-right item cannot be reached by the native inventory raycast");
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath,
                    "qa-stock-overview-placement-" + Screen.width + "x" + Screen.height + ".png"));
            input = new QaSyntheticInput(keyboard: true, mouseButtons: true); input.Mouse = cornerPoint;
            frame = Time.frameCount + 3; step = 30; return false;
        }
        if (step == 30)
        {
            input.MouseDown = input.MouseHeld = 0; input.Held = KeyCode.Mouse0;
            frame = Time.frameCount + 1; step = 31; return false;
        }
        if (step == 31)
        {
            input.MouseDown = -1; input.Mouse = cornerPoint + new Vector2(-Mathf.Max(48f, UISettings.current.gridSize * 2f), 0);
            frame = Time.frameCount + 20; step = 32; return false;
        }
        if (step == 32)
        {
            var held = typeof(ItemMouseDragHandler).GetField("currentItem", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(ItemMouseDragHandler.current);
            Need(ItemMouseDragHandler.current?.IsDraggingItem == true && ReferenceEquals(held, cornerItem),
                "The upper-right item did not start the native drag gesture");
            CheckAnchor("dragging the upper-right stock item");
            input.Mouse = cornerPoint; frame = Time.frameCount + 3; step = 33; return false;
        }
        if (step == 33)
        {
            input.MouseDown = input.MouseHeld = -1; input.Held = KeyCode.None; input.MouseUp = 0;
            frame = Time.frameCount + 3; step = 34; return false;
        }
        if (step == 34)
        {
            input.MouseUp = -1;
            Need(ItemMouseDragHandler.current?.IsDraggingItem != true && ReferenceEquals(cornerItem.parentInventory, EmporiumEntry.Instance.invElement) &&
                cornerItem.unitCount == cornerCount && SameShape(cornerItem.modifiedShape, cornerShape),
                "The upper-right item changed when its drag returned to the original cell");
            CheckAnchor("returning the upper-right stock item");
            Need(HitNativeButton(stockButton), "Native main storage machine-status button became unreachable after dragging");
            input.Mouse = ButtonPoint(stockButton); frame = Time.frameCount + 3; step = 35; return false;
        }
        if (step == 35)
        {
            input.MouseDown = input.MouseHeld = 0; input.Held = KeyCode.Mouse0;
            frame = Time.frameCount + 1; step = 36; return false;
        }
        if (step == 36)
        {
            input.MouseDown = input.MouseHeld = -1; input.Held = KeyCode.None; input.MouseUp = 0;
            frame = Time.frameCount + 3; step = 37; return false;
        }
        if (step == 37)
        {
            input.MouseUp = -1; input.Dispose(); input = null;
            var overview = Ui("f30"); Need(overview?.IsAlive == true, "Main storage machine status did not open");
            CheckAnchor("opening machine status");
            overview.Close();
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width * .7f, Screen.height * .75f));
            Need(bag.contentWindow != null && bag.contentWindow.IsVisible() && bag.contentWindow.canMove,
                "Ordinary container did not open as a movable window");
            CheckAnchor("opening an ordinary container");
            frame = Time.frameCount + 3; step = 4; return false;
        }
        if (step == 4)
        {
            DragNativeContainer(bag.contentWindow);
            CheckAnchor("dragging an ordinary container");
            bag.contentWindow.Hide(); machine.contentWindow.Hide();
            result = "PASS " + Screen.width + "x" + Screen.height +
                " native main-storage anchor across module tabs and machine status; ordinary container remains draggable";
            RestoreDisplay();
            return true;
        }
        throw new InvalidOperationException("Unknown storage anchor QA step " + step);
    }

    private void RestoreDisplay()
    {
        if (!restoreDisplay || EmporiumOption.Instance == null) return;
        restoreDisplay = false;
        EmporiumOption.Instance.fullscreenMode = originalMode;
        EmporiumOption.Instance.resolution = originalResolution;
        EmporiumOption.Instance.borderlessDivisor = originalDivisor;
        EmporiumOption.Instance.ApplySetting();
    }

    private void CheckAnchor(string action)
    {
        Need(stock != null && stock.IsVisible() && ReferenceEquals(stock.rectTransform.parent, originalParent) &&
            Vector2.Distance(stock.rectTransform.anchoredPosition, originalPosition) < .1f,
            "Main storage moved while " + action + ": parent=" + stock?.rectTransform.parent?.name +
            " position=" + stock?.rectTransform.anchoredPosition + " expected=" + originalPosition);
    }

    private static MachineryButton Button(PixelWindow window, string name) =>
        window?.handler?.transform.Find(name)?.GetComponent<TreeNodeRender>()?.element as MachineryButton;

    private static CustomUIWindow Ui(string id) => CustomUIManager.Instance.GetWindow(Mod + "." + id);

    private static bool HitNativeButton(MachineryButton button)
    {
        return Plate(button) != null && ReferenceEquals(RenderHandler.RaycastElement<SelectableElement>(ButtonPoint(button)), button);
    }

    private static TreeNodeRender Plate(MachineryButton button) => button?.handler?.GetComponentsInChildren<TreeNodeRender>(true)
        .FirstOrDefault(node => node != button.handler && node.image != null && node.image.isActiveAndEnabled &&
            node.image.raycastTarget && node.rectTransform.rect.width > 1);

    private static Vector2 ButtonPoint(MachineryButton button)
    {
        var plate = Plate(button); Need(plate != null, "Native stock machine-status plate is missing");
        Rect bounds = ScreenBounds(plate.rectTransform);
        return bounds.center;
    }

    private static void CheckStockGeometry(MachineryButton button, GameGridInventory entry, GameItemElement item)
    {
        var plate = Plate(button);
        Need(plate != null && entry?.inner?.rectTransform != null && item?.background?.rectTransform != null,
            "Native stock geometry is unavailable");
        Rect hit = ScreenBounds(plate.rectTransform), grid = ScreenBounds(entry.inner.rectTransform), drawn = ScreenBounds(item.background.rectTransform);
        Need(hit.width > 1 && hit.height > 1 && hit.xMin >= 0 && hit.yMin >= 0 && hit.xMax <= Screen.width && hit.yMax <= Screen.height,
            "The native machine-status button is not fully visible: " + hit);
        Need(!hit.Overlaps(grid) && !hit.Overlaps(drawn),
            "The machine-status button covers the native grid or its upper-right item: button=" + hit + " grid=" + grid + " item=" + drawn);
        foreach (GameItem child in entry.childItems)
            if (child is GameItemElement element && element.handler != null && element.handler.gameObject.activeInHierarchy &&
                element.background?.rectTransform != null)
                Need(!hit.Overlaps(ScreenBounds(element.background.rectTransform)), "The machine-status button covers a visible item");
    }

    private static Rect ScreenBounds(RectTransform rect)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 p = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        float minX = p.x, minY = p.y, maxX = p.x, maxY = p.y;
        for (int i = 1; i < corners.Length; i++)
        {
            p = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
            minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y); maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y);
        }
        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    private static GameItemElement PlaceTopRight(GameGridInventory grid)
    {
        // A new game's stock is not empty. Reuse an ordinary, intact item if it
        // already occupies the row being tested, before creating another one.
        for (int x = grid.inventoryShape.width - 1; x >= grid.inventoryShape.width / 2; x--)
        {
            var existing = grid.childItems.OfType<GameItemElement>().FirstOrDefault(candidate =>
                candidate.modifiedShape != null && candidate.modifiedShape.globalWidth == 1 &&
                candidate.modifiedShape.globalHeight == 1 && candidate.modifiedShape.Intersects(x, 0) &&
                candidate.unitCount == 1 && CanMoveWhole(candidate));
            if (existing != null)
            {
                Debug.Log("[PLAYER-QA] Reusing upper-right stock item " + existing.identifier + " at " + x + ",0");
                return existing;
            }
        }

        GameItem[] residents = grid.childItems.ToArray();
        int[] counts = residents.Select(resident => resident.unitCount).ToArray();
        GameItemElement item = null;
        foreach (string id in new[] { "small_morsel", "processed_cheese", "cat_bar", "cup_noodle", "processed_meat" })
        {
            var candidate = DirectoryMaster.Item(id) as GameItemElement;
            if (candidate == null) continue;
            if (candidate.modifiedShape != null && candidate.modifiedShape.globalWidth == 1 &&
                candidate.modifiedShape.globalHeight == 1 && candidate.unitCount == 1) { item = candidate; break; }
            candidate.Destroy();
        }
        Need(item != null, "No native one-cell item is available for the stock fixture");
        GeneralHelper.SetItemOwned(item, true);
        for (int x = grid.inventoryShape.width - 1; x >= grid.inventoryShape.width / 2; x--)
        {
            GameItem[] blockers = residents.Where(resident => resident.modifiedShape != null &&
                resident.modifiedShape.Intersects(x, 0)).ToArray();
            GameItem displaced = null;
            GridShape previous = null;
            if (blockers.Length > 0 && (blockers.Length != 1 ||
                !TryMoveAway(grid, blockers[0], x, out displaced, out previous))) continue;
            var shape = new GridShapeBuilder(item.modifiedShape).SetPosition(x, 0).Build();
            SlotMarker marker = grid.TryInventorySlot(item, item.unitCount, shape);
            if (marker?.targetItem == null && marker?.numTransfer >= item.unitCount)
            {
                Need(marker.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, grid),
                    "Native upper-right fixture placement did not complete");
                CheckResidents(grid, residents, counts, 1);
                Debug.Log("[PLAYER-QA] Placed upper-right stock fixture at " + x + ",0" +
                    (displaced == null ? "" : " after moving intact " + displaced.identifier));
                return item;
            }
            if (displaced != null)
            {
                SlotMarker restore = grid.TryInventorySlot(displaced, displaced.unitCount, previous);
                Need(restore?.targetItem == null && restore?.numTransfer >= displaced.unitCount &&
                    restore.TryAcceptOnce(displaced.unitCount) == displaced.unitCount && SameShape(displaced.modifiedShape, previous),
                    "Could not restore the intact stock item after an unavailable fixture cell");
                CheckResidents(grid, residents, counts, 0);
            }
        }
        throw new InvalidOperationException("No upper-right stock cell is available for the fixture");
    }

    private static bool CanMoveWhole(GameItem item) => item?.parentInventory != null && item.unitCount > 0 &&
        !item.parentInventory.IsRemoveLocked() && !ItemPinHelper.IsPinned(item) && !DebrisHelper.IsDebris(item) &&
        !item.IsTag("ITEM_HIDDEN_TAG") && !item.IsTag("ITEM_BEHIND_LOCK_TAG") &&
        GeneralHelper.IsItemEffectivelyOwned(item) && item.MayRemove() && item.MaxNumRemove() >= item.unitCount;

    private static bool TryMoveAway(GameGridInventory grid, GameItem item, int cornerX,
        out GameItem displaced, out GridShape previous)
    {
        displaced = null; previous = null;
        if (!CanMoveWhole(item)) return false;
        for (int y = grid.inventoryShape.height - item.modifiedShape.globalHeight; y >= 0; y--)
        for (int x = 0; x <= grid.inventoryShape.width - item.modifiedShape.globalWidth; x++)
        {
            GridShape pose = new GridShapeBuilder(item.modifiedShape).SetPosition(x, y).Build();
            if (pose.Intersects(cornerX, 0)) continue;
            SlotMarker destination = grid.TryInventorySlot(item, item.unitCount, pose);
            if (destination?.targetItem != null || destination?.numTransfer < item.unitCount || destination == null) continue;
            int count = item.unitCount;
            GridShape original = item.modifiedShape.Clone();
            Need(destination.TryAcceptOnce(count) == count && ReferenceEquals(item.parentInventory, grid) &&
                item.unitCount == count && SameShape(item.modifiedShape, pose),
                "Could not move an intact upper-right stock item to a legal empty location");
            displaced = item; previous = original;
            return true;
        }
        return false;
    }

    private static void CheckResidents(GameGridInventory grid, GameItem[] residents, int[] counts, int added)
    {
        GameItem[] current = grid.childItems.ToArray();
        Need(current.Length == residents.Length + added, "Fixture setup changed the stock's existing item count");
        for (int i = 0; i < residents.Length; i++)
            Need(ReferenceEquals(residents[i].parentInventory, grid) && residents[i].unitCount == counts[i] &&
                current.Any(item => ReferenceEquals(item, residents[i])),
                "Fixture setup changed an existing stock item or its quantity");
    }

    private static Vector2 ItemPoint(GameGridInventory grid, GameItem item)
    {
        Canvas canvas = grid.handler.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Vector3 point = new Vector3((item.modifiedShape.minX + .5f) * UISettings.current.gridSize,
            -(item.modifiedShape.minY + .5f) * UISettings.current.gridSize);
        return RectTransformUtility.WorldToScreenPoint(camera, grid.rectTransform.TransformPoint(point));
    }

    private static bool SameShape(GridShape a, GridShape b) => a != null && b != null && a.minX == b.minX && a.minY == b.minY &&
        a.width == b.width && a.height == b.height && a.orientation == b.orientation && a.flipped == b.flipped;

    private static void DragNativeContainer(PixelWindow window)
    {
        var background = (TreeNodeRender)typeof(PixelWindow).GetField("background", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
        Need(background?.rectTransform != null, "Native container drag surface is missing");
        Canvas canvas = background.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, background.rectTransform.TransformPoint(background.rectTransform.rect.center));
        var pointer = new PointerEventData(EventSystem.current) { position = point, pressPosition = point, button = PointerEventData.InputButton.Left };
        Vector2 before = window.rectTransform.anchoredPosition;
        ExecuteEvents.Execute(background.gameObject, pointer, ExecuteEvents.beginDragHandler);
        pointer.position += new Vector2(-36, -24);
        ExecuteEvents.Execute(background.gameObject, pointer, ExecuteEvents.dragHandler);
        ExecuteEvents.Execute(background.gameObject, pointer, ExecuteEvents.endDragHandler);
        Need(Vector2.Distance(before, window.rectTransform.anchoredPosition) > 1, "Ordinary container did not respond to native drag events");
    }

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

public sealed class StorageAnchor1280Scenario
{
    private readonly StorageAnchorScenario scenario = new StorageAnchorScenario(1280, 720, 3);
    public bool Tick(out string result) => scenario.Tick(out result);
}

public sealed class StorageAnchor1920Scenario
{
    private readonly StorageAnchorScenario scenario = new StorageAnchorScenario(1920, 1080, 0);
    public bool Tick(out string result) => scenario.Tick(out result);
}
