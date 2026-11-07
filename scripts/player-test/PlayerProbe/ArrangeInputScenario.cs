using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Drives InputActionManager with virtual device input, preserving the real raycasts.
public sealed class ArrangeInputScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly ArrangeCapture capture = new ArrangeCapture();
    private QaSyntheticInput input;
    private GameItemElement bag, first, second, container, nested;
    private GameGridInventory grid;
    private GridShape firstBefore, secondBefore, containerBefore, selectedBefore, otherBefore, selectionContainerBefore;
    private int step, readyFrame;
    private float since = Time.realtimeSinceStartup;
    private PixelWindow menu;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 100) throw new InvalidOperationException("Arrange input timed out at step " + step);
        if (Time.frameCount < readyFrame) return false;
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                Next(1); return false;
            case 1:
                if (EmporiumEntry.Instance?.invElement == null || ItemContextHandler.current == null || ItemMouseDoubleClickHandler.current == null) return false;
                TradeScenario.SetOptions(true, "f05.enabled", "f05.keep_menu", "f08.enabled", "f09.enabled");
                TradeScenario.SetOptions(false, "f05.auto_insert", "f05.rotate");
                bag = New("backpack_large"); Put(EmporiumEntry.Instance.invElement, bag);
                grid = bag.contentWindow?.childElement as GameGridInventory;
                Need(grid != null, "Native backpack grid unavailable");
                first = New("processed_meat"); second = New("processed_meat");
                container = New("fanny_pack"); container.EnableTag("NESTABLE_DEVICE_TAG");
                Need(ContainerHelper.IsNestingAllowed(container, bag), "Native nested-container fixture unavailable");
                PutFar(grid, first); PutFar(grid, second); PutFar(grid, container);
                var nestedGrid = container.contentWindow?.childElement as GameGridInventory;
                Need(nestedGrid != null, "Nested container grid unavailable");
                nested = New("wine_yeast"); Put(nestedGrid, nested);
                firstBefore = first.modifiedShape.Clone(); secondBefore = second.modifiedShape.Clone();
                containerBefore = container.modifiedShape.Clone();
                ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(120, 180));
                input = new QaSyntheticInput(keyboard: true, mouseButtons: true);
                input.Mouse = new Vector3(-100, -100);
                Next(2, 5); return false;
            case 2:
                Need(bag.contentWindow.IsVisible(), "Backpack did not open");
                RoutingScenario.SetSelection(nested);
                RightDown(EmptyCell()); Next(3); return false;
            case 3:
                ClearDown(); menu = Window("sortWindow");
                Need(menu?.IsVisible() == true, "Right-click did not open the native grid menu");
                CheckChrome(menu, "ShopAssist_SortMenu");
                CheckArrangeRows(menu, "ShopAssist_SortMenu", "f05.all");
                capture.Start("qa-arrange-grid-menu.png"); Next(300, 4); return false;
            case 300:
                if (!capture.Ready()) return false;
                Need(menu?.IsVisible() == true, "Arrange grid menu closed during capture");
                input.Mouse = new Vector3(4, 4); Next(4, 4); return false;
            case 4:
                Need(menu.IsVisible(), "Arrange menu closed when the pointer moved outside");
                input.Mouse = ButtonPoint(menu, "ShopAssist_SortMenu"); Next(5, 2); return false;
            case 5:
                Need(menu.IsVisible(), "Arrange menu closed while returning to a row");
                LeftDown(); Next(6); return false;
            case 6:
                LeftUp(); Next(7, 2); return false;
            case 7:
                ClearUp();
                Need(!menu.IsVisible(), "Clicking arrange did not close its menu");
                Need(!Same(firstBefore, first.modifiedShape) || !Same(secondBefore, second.modifiedShape), "Arrange row did not move either fixture");
                Need(!Same(containerBefore, container.modifiedShape) && nested.parentInventory == container.contentWindow.childElement && nested.unitCount == 1,
                    "Ordinary arrange did not move the intact container with its contents");
                RightDown(EmptyCell()); Next(70); return false;
            case 70:
                ClearDown(); menu = Window("sortWindow"); Need(menu?.IsVisible() == true, "Arrange undo menu did not open");
                input.Mouse = NativeUndoPoint(); Next(71, 2); return false;
            case 71:
                LeftDown(); Next(72); return false;
            case 72:
                LeftUp(); Next(73, 2); return false;
            case 73:
                ClearUp();
                Need(Same(containerBefore, container.modifiedShape) && Same(firstBefore, first.modifiedShape) && Same(secondBefore, second.modifiedShape) &&
                    nested.parentInventory == container.contentWindow.childElement, "Arrange undo did not restore the container and contents");
                RightDown(EmptyCell()); Next(8); return false;
            case 8:
                ClearDown(); menu = Window("sortWindow");
                Need(menu.IsVisible(), "Arrange menu did not reopen");
                input.Mouse = ItemCell(first); Next(9, 2); return false;
            case 9:
                LeftDown(); Next(10); return false;
            case 10:
                Need(!menu.IsVisible(), "Outside press did not close arrange menu");
                Need(!ItemMouseDragHandler.current.IsDraggingItem && !ItemSelectHandler.current.IsEquipped &&
                    first.parentInventory == grid && Same(firstBefore, first.modifiedShape),
                    "Outside menu press passed through to the native item");
                LeftUp(); Next(11, 2); return false;
            case 11:
                ClearUp();
                Need(!ItemMouseDragHandler.current.IsDraggingItem && !ItemSelectHandler.current.IsEquipped &&
                    first.parentInventory == grid && Same(firstBefore, first.modifiedShape),
                    "Outside menu release passed through to the native item");
                RightDown(EmptyCell()); Next(12); return false;
            case 12:
                ClearDown(); menu = Window("sortWindow");
                Need(menu.IsVisible(), "Arrange menu did not reopen before Escape");
                Need(StoreUIManager.Instance.escapeUIManager?.menuPanel?.activeInHierarchy == false && bag.contentWindow.IsVisible(),
                    "Escape precondition has an unexpected modal or closed backpack");
                input.Down = KeyCode.Escape; Next(13); return false;
            case 13:
                input.Down = KeyCode.None;
                Need(!menu.IsVisible(), "Escape did not close arrange menu");
                Need(StoreUIManager.Instance.escapeUIManager?.menuPanel?.activeInHierarchy == false && bag.contentWindow.IsVisible(),
                    "Escape passed through to another native window or modal");
                TradeScenario.SetOptions(false, "f05.keep_menu");
                RightDown(EmptyCell()); Next(14); return false;
            case 14:
                ClearDown(); menu = Window("sortWindow");
                Need(menu.IsVisible(), "Arrange menu did not open with persistence disabled");
                input.Mouse = new Vector3(4, 4); Next(15, 4); return false;
            case 15:
                Need(!menu.IsVisible(), "Disabled persistence did not restore native hover close");
                TradeScenario.SetOptions(true, "f05.keep_menu");
                RightDown(EmptyCell()); Next(150); return false;
            case 150:
                ClearDown(); menu = Window("sortWindow"); Need(menu?.IsVisible() == true, "Arrange menu did not open");
                input.Mouse = ButtonPoint(menu, "ShopAssist_SortMenu"); Next(151, 2); return false;
            case 151:
                LeftDown(); Next(152); return false;
            case 152:
                LeftUp(); Next(153, 2); return false;
            case 153:
                ClearUp();
                Need(!Same(containerBefore, container.modifiedShape) && nested.parentInventory == container.contentWindow.childElement &&
                    (!Same(firstBefore, first.modifiedShape) || !Same(secondBefore, second.modifiedShape)),
                    "Arrange did not move the unpinned container and the other items");
                Need(InventorySortHelper.MayUndo(grid), "Arrange did not create an undo before the stale check");
                GridShape sorted = first.modifiedShape.Clone();
                SlotMarker away = OtherSlot(grid, first);
                Need(away != null && away.TryAcceptOnce(first.unitCount) == first.unitCount, "Native move away failed");
                SlotMarker back = grid.TryInventorySlot(first, first.unitCount, sorted);
                Need(back != null && back.numTransfer >= first.unitCount && back.TryAcceptOnce(first.unitCount) == first.unitCount && Same(sorted, first.modifiedShape),
                    "Native move back failed");
                Need(!InventorySortHelper.MayUndo(grid), "A native move away and back incorrectly revived the old undo");
                SlotMarker selectionSlot = OtherSlot(grid, first);
                Need(selectionSlot != null && selectionSlot.TryAcceptOnce(first.unitCount) == first.unitCount, "Selection fixture move failed");
                selectedBefore = first.modifiedShape.Clone(); otherBefore = second.modifiedShape.Clone();
                selectionContainerBefore = container.modifiedShape.Clone();
                RoutingScenario.SetSelection(first);
                RightDown(EmptyCell()); Next(154); return false;
            case 154:
                ClearDown(); menu = Window("sortWindow");
                Need(menu?.IsVisible() == true, "Selected grid menu did not open");
                CheckArrangeRows(menu, "ShopAssist_SortMenu", "f05.selected");
                input.Mouse = ButtonPoint(menu, "ShopAssist_SortMenu"); Next(155, 2); return false;
            case 155:
                LeftDown(); Next(156); return false;
            case 156:
                LeftUp(); Next(157, 2); return false;
            case 157:
                ClearUp();
                Need(!Same(selectedBefore, first.modifiedShape) && Same(otherBefore, second.modifiedShape) &&
                    Same(selectionContainerBefore, container.modifiedShape), "Selected arrange changed an unselected item or failed to move the selection");
                RoutingScenario.SetSelection(first);
                RightDown(ItemCell(first)); Next(16); return false;
            case 16:
                ClearDown(); menu = Window("contextWindow");
                Need(menu?.IsVisible() == true, "Right-click did not open the item menu");
                CheckChrome(menu, "ShopAssist_f05_item");
                CheckArrangeRows(menu, "ShopAssist_f05_item", "f05.selected");
                Need(Button(menu, "ShopAssist_f05_undo_item") != null, "Available item undo row is missing");
                capture.Start("qa-arrange-item-menu.png"); Next(160, 4); return false;
            case 160:
                if (!capture.Ready()) return false;
                Need(menu?.IsVisible() == true, "Arrange item menu closed during capture");
                CheckChrome(menu, "ShopAssist_f05_item");
                ItemContextHandler.current.Stop();
                SlotMarker invalidate = OtherSlot(grid, first);
                Need(invalidate != null && invalidate.TryAcceptOnce(first.unitCount) == first.unitCount, "Could not invalidate item undo");
                RightDown(ItemCell(first)); Next(161); return false;
            case 161:
                ClearDown(); menu = Window("contextWindow");
                Need(menu?.IsVisible() == true && Button(menu, "ShopAssist_f05_undo_item") == null,
                    "Item undo row remained without a reversible operation");
                ItemContextHandler.current.Stop();
                RoutingScenario.SetSelection(second);
                RightDown(EmptyCell()); Next(162); return false;
            case 162:
                ClearDown(); menu = Window("sortWindow");
                Need(menu?.IsVisible() == true, "Stale-selection menu did not open");
                CheckArrangeRows(menu, "ShopAssist_SortMenu", "f05.selected");
                var destination = EmporiumEntry.Instance.invElement.TryFindOneValidInventorySlot(second);
                Need(destination != null && destination.targetItem == null && destination.TryAcceptOnce(second.unitCount) == second.unitCount &&
                    second.parentInventory == EmporiumEntry.Instance.invElement, "Could not move the selected item after opening the menu");
                otherBefore = first.modifiedShape.Clone(); selectionContainerBefore = container.modifiedShape.Clone();
                input.Mouse = ButtonPoint(menu, "ShopAssist_SortMenu"); Next(163, 2); return false;
            case 163:
                LeftDown(); Next(164); return false;
            case 164:
                LeftUp(); Next(165, 2); return false;
            case 165:
                ClearUp();
                Need(second.parentInventory == EmporiumEntry.Instance.invElement && Same(otherBefore, first.modifiedShape) &&
                    Same(selectionContainerBefore, container.modifiedShape), "A stale selection caused an unrelated full-grid sort");
                RoutingScenario.SetSelection(first);
                RightDown(ItemCell(first)); Next(166); return false;
            case 166:
                ClearDown(); menu = Window("contextWindow");
                Need(menu?.IsVisible() == true && Button(menu, "ShopAssist_f08_context") != null,
                    "The selected item's routing action is missing from its native menu");
                input.Mouse = ButtonPoint(menu, "ShopAssist_f08_context"); Next(167, 2); return false;
            case 167:
                LeftDown(); Next(168); return false;
            case 168:
                LeftUp(); Next(169, 2); return false;
            case 169:
                ClearUp();
                Need(CustomUIManager.Instance.GetWindow(Mod + ".route_preview")?.IsAlive == true,
                    "Routing did not retain the selected item when the menu closed");
                input.Dispose(); input = null;
                result = "PASS grid/item arrange entries, selection and stale refusal, native and item undo, and selected-item routing from the native menu";
                return true;
            default: throw new InvalidOperationException("Unknown arrange input step");
        }
    }

    private void RightDown(Vector2 position)
    {
        input.Mouse = position; input.MouseDown = 1; input.MouseHeld = 1;
        input.Down = ItemContextHandler.current.triggerKey;
    }
    private void ClearDown() { input.MouseDown = -1; input.MouseHeld = -1; input.Down = KeyCode.None; }
    private void LeftDown() { input.MouseDown = 0; input.MouseHeld = 0; }
    private void LeftUp() { input.MouseDown = -1; input.MouseHeld = -1; input.MouseUp = 0; }
    private void ClearUp() { input.MouseUp = -1; }
    private void Next(int value, int frames = 1) { step = value; readyFrame = Time.frameCount + frames; }
    private static PixelWindow Window(string name) => typeof(ItemContextHandler).GetField(name, Fields)?.GetValue(ItemContextHandler.current) as PixelWindow;
    private static GameItemElement New(string id)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement;
        Need(item != null, "Missing fixture " + id); GeneralHelper.SetItemOwned(item, true); return item;
    }
    private static void Put(GameInventory inventory, GameItem item)
    {
        SlotMarker marker = inventory.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.targetItem == null && marker.TryAcceptOnce(item.unitCount) == item.unitCount, "Fixture placement failed");
    }
    private static void PutFar(GameGridInventory inventory, GameItem item)
    {
        var pose = new GridShapeBuilder(item.modifiedShape);
        for (int y = inventory.inventoryShape.height - pose.globalHeight; y >= 0; y--)
        for (int x = inventory.inventoryShape.width - pose.globalWidth; x >= 0; x--)
        {
            pose.SetPosition(x, y); SlotMarker marker = inventory.TryInventorySlot(item, item.unitCount, pose);
            if (marker?.targetItem == null && marker?.numTransfer >= item.unitCount && marker.TryAcceptOnce(item.unitCount) == item.unitCount) return;
        }
        throw new InvalidOperationException("Could not place an arrange fixture");
    }
    private static SlotMarker OtherSlot(GameGridInventory inventory, GameItem item)
    {
        var pose = new GridShapeBuilder(item.modifiedShape);
        for (int y = inventory.inventoryShape.height - pose.globalHeight; y >= 0; y--)
        for (int x = inventory.inventoryShape.width - pose.globalWidth; x >= 0; x--)
        {
            if (x == item.modifiedShape.minX && y == item.modifiedShape.minY) continue;
            pose.SetPosition(x, y);
            SlotMarker marker = inventory.TryInventorySlot(item, item.unitCount, pose);
            if (marker?.targetItem == null && marker?.numTransfer >= item.unitCount) return marker;
        }
        return null;
    }
    private Vector2 EmptyCell()
    {
        Canvas.ForceUpdateCanvases();
        for (int y = 0; y < grid.inventoryShape.height; y++) for (int x = 0; x < grid.inventoryShape.width; x++)
        {
            if (grid.childItems.Any(item => item.modifiedShape.Intersects(x, y))) continue;
            Vector2 point = Cell(x, y);
            if (RenderHandler.RaycastElement<GameItem>(point) == null && ReferenceEquals(RenderHandler.RaycastElement<GameGridInventory>(point), grid)) return point;
        }
        throw new InvalidOperationException("No raycastable empty cell");
    }
    private Vector2 ItemCell(GameItem item)
    {
        for (int y = item.modifiedShape.minY; y <= item.modifiedShape.maxY; y++)
        for (int x = item.modifiedShape.minX; x <= item.modifiedShape.maxX; x++)
        {
            if (item.modifiedShape.Get(x, y) == 0) continue;
            Vector2 point = Cell(x, y);
            if (ReferenceEquals(RenderHandler.RaycastElement<GameItem>(point), item)) return point;
        }
        throw new InvalidOperationException("No raycastable item cell");
    }
    private Vector2 Cell(int x, int y)
    {
        Canvas canvas = grid.handler.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.WorldToScreenPoint(camera,
            grid.rectTransform.TransformPoint(new Vector3((x + .5f) * UISettings.current.gridSize, -(y + .5f) * UISettings.current.gridSize)));
    }
    private static RichTextElement Button(PixelWindow window, string name)
    {
        Transform node = window.handler.GetComponentsInChildren<Transform>(true).FirstOrDefault(value => value.name == name && value.gameObject.activeInHierarchy);
        return node?.GetComponent<TreeNodeRender>()?.element as RichTextElement;
    }
    private static TreeNodeRender Background(object target, Type type) => type.GetField("background", Fields)?.GetValue(target) as TreeNodeRender;
    private static Vector2 ButtonPoint(PixelWindow window, string name)
    {
        RichTextElement button = Button(window, name);
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
        TreeNodeRender background = button == null ? null : Background(button, typeof(RichTextElement));
        Need(background != null, "Missing arrange row " + name);
        Canvas canvas = ItemContextHandler.current.overlayCanvas;
        Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.WorldToScreenPoint(camera, background.rectTransform.TransformPoint(background.rectTransform.rect.center));
    }
    private static void CheckArrangeRows(PixelWindow window, string expectedNode, string expectedLabel)
    {
        var active = window.handler.GetComponentsInChildren<Transform>(true).Where(node => node.gameObject.activeInHierarchy).ToArray();
        string actual = Button(window, expectedNode)?.GetText()?.text?.text;
        string expected = ModHelper.GetLocalized(Mod, expectedLabel);
        Need(active.Count(node => node.name == expectedNode) == 1 && actual == expected,
            "Smart arrange row does not match this grid's selection: node=" + expectedNode + ", actual=" + actual + ", expected=" + expected);
        foreach (string obsolete in new[] { "ShopAssist_f05_all_grid", "ShopAssist_f05_all_item", "ShopAssist_f05_preserve",
            "ShopAssist_f05_preserve_item", "ShopAssist_f05_preserve_all_grid", "ShopAssist_f05_preserve_all_item", "ShopAssist_f05_undo_grid", "ShopAssist_HoldMenu" })
            Need(!active.Any(node => node.name == obsolete), "Obsolete context row remains: " + obsolete);
    }
    private static void CheckChrome(PixelWindow window, string name)
    {
        Canvas.ForceUpdateCanvases();
        RichTextElement button = Button(window, name);
        TreeNodeRender row = button == null ? null : Background(button, typeof(RichTextElement));
        TreeNodeRender chrome = Background(window, typeof(PixelWindow));
        Need(row != null && chrome != null, "Native menu chrome or arrange row missing");
        var corners = new Vector3[4]; row.rectTransform.GetWorldCorners(corners);
        foreach (Vector3 corner in corners)
        {
            Vector3 local = chrome.rectTransform.InverseTransformPoint(corner);
            Need(chrome.rectTransform.rect.Contains(local), "Arrange row extends beyond the native menu background");
        }
    }
    private static bool Same(GridShape left, GridShape right) => left.minX == right.minX && left.minY == right.minY &&
        left.orientation == right.orientation && left.flipped == right.flipped;
    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
}

// Captures only in the isolated Player's persistent-data directory.
internal sealed class ArrangeCapture
{
    private string path;
    private DateTime started;
    private float deadline;

    internal void Start(string filename)
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) { path = null; return; }
        path = Path.Combine(Application.persistentDataPath, filename);
        started = DateTime.UtcNow;
        deadline = Time.realtimeSinceStartup + 8f;
        ScreenCapture.CaptureScreenshot(path);
    }
    internal bool Ready()
    {
        if (path == null) return true;
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= started && new FileInfo(path).Length > 0) return true;
        if (Time.realtimeSinceStartup >= deadline) throw new InvalidOperationException("Arrange screenshot was not written: " + path);
        return false;
    }
}
