using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

// Uses the native grid context menu in an isolated save, then checks same-day reload.
public sealed class OrganizerScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 8);
    private GameItemElement bag, pinToRelease, pinned, first, second, container, irregular;
    private GameGridInventory inner;
    private QaSyntheticInput input;
    private QaLoadSlot load;
    private int step, callbacks, day;
    private string run;
    private int slot;
    private readonly Dictionary<string, (int x, int y, int orientation, bool flipped, int count)> saved =
        new Dictionary<string, (int, int, int, bool, int)>();
    private float since = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 90) throw new InvalidOperationException("Organizer QA timed out at step " + step);
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                Advance(1); return false;
            case 1:
                if (StoreUIManager.Instance?.gameOptionUIManager == null || EmporiumEntry.Instance?.invElement == null ||
                    ItemMouseDoubleClickHandler.current == null || ItemContextHandler.current == null) return false;
                Prepare(); Advance(2); return false;
            case 2:
                if (!bag.contentWindow.IsVisible()) return false;
                Require(!ItemPinHelper.IsPinned(pinToRelease), "Pin fixture was already fixed");
                input = new QaSyntheticInput(keyboard: true, mouseButtons: true);
                RightDown(ItemCell(inner, pinToRelease)); Advance(20); return false;
            case 20:
                ClearInput(); input.Mouse = NativePinPoint(); Advance(21); return false;
            case 21:
                PressLeft(); Advance(22); return false;
            case 22:
                ReleaseLeft(); Advance(23); return false;
            case 23:
                ClearInput();
                Require(ItemPinHelper.IsPinned(pinToRelease), "Native item context action did not pin the fixture");
                input.Dispose(); input = null; Advance(3); return false;
            case 3:
            {
                Require(ItemPinHelper.IsPinned(pinToRelease) && ItemPinHelper.IsPinned(pinned), "Native pins were not applied");
                var before = new[] { first, second, pinToRelease, pinned, container, irregular }.ToDictionary(item => item, item => item.modifiedShape.Clone());
                InvokeSortMenu();
                Require(CustomUIManager.Instance.GetWindow(Mod + ".inventory_organizer") == null,
                    "Right-click sorting opened the obsolete preview window");
                Require(callbacks == 0, "Same-grid sorting invoked inventory entry or exit callbacks");
                Require(before.Keys.All(item => ReferenceEquals(item.parentInventory, inner)), "Sorting changed an item parent");
                Require(SamePose(pinToRelease.modifiedShape, before[pinToRelease]) && SamePose(pinned.modifiedShape, before[pinned]),
                    "A pinned item moved during arrangement");
                Require(!SamePose(container.modifiedShape, before[container]), "The unpinned container was not arranged");
                Require(!SamePose(first.modifiedShape, before[first]) || !SamePose(second.modifiedShape, before[second]),
                    "Direct sort did not rearrange either test item");
                Require(first.modifiedShape.minY == second.modifiedShape.minY && second.modifiedShape.minX == first.modifiedShape.maxX + 1 &&
                    first.modifiedShape.orientation == second.modifiedShape.orientation, "Identical items were not placed together in a row");
                Require(irregular.modifiedShape.GetLocal(0, 0) == 1 && irregular.modifiedShape.GetLocal(1, 0) == 0 &&
                    irregular.modifiedShape.GetLocal(0, 1) == 1 && irregular.modifiedShape.GetLocal(1, 1) == 1,
                    "Direct sort changed the irregular footprint");
                foreach (GameItem a in before.Keys) foreach (GameItem b in before.Keys)
                    if (!ReferenceEquals(a, b)) Require(!a.modifiedShape.Intersects(b.modifiedShape), "Sorting overlapped two items");
                Advance(4); return false;
            }
            case 4:
            {
                var before = new[] { first, second, pinToRelease, pinned, container, irregular }.ToDictionary(item => item, item => item.modifiedShape.Clone());
                var counts = before.Keys.ToDictionary(item => item, item => item.unitCount);
                var previous = inner.isItemStateValidFunc;
                inner.isItemStateValidFunc = (item, inventory, marker, shape, state) => true;
                try { InvokeSortMenu(); }
                finally { inner.isItemStateValidFunc = previous; }
                Require(before.All(pair => SamePose(pair.Key.modifiedShape, pair.Value) && pair.Key.parentInventory == inner && pair.Key.unitCount == counts[pair.Key]),
                    "A refused sort changed the original layout");
                Advance(41); return false;
            }
            case 41:
            {
                var notice = CustomUIManager.Instance.GetLayerRoot(CustomUIManager.LAYER_META)?.Find(Mod + ".notice");
                string message = notice == null ? "" : string.Join(" ", notice.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text));
                bool shown = notice != null && notice.gameObject.activeInHierarchy && message.Contains(ModHelper.GetLocalized(Mod, "f05.no_plan"));
                if (!shown && Time.realtimeSinceStartup - since < 12f) return false;
                Require(shown, "A refused sort did not show its queued failure feedback: " + message);
                // The irregular shape is a native in-memory fixture, not a modified item definition to save.
                irregular.Destroy();
                foreach (var item in new[] { first, second, pinToRelease, pinned, container })
                    saved[item.GetDisplayName(true)] = (item.modifiedShape.minX, item.modifiedShape.minY,
                        item.modifiedShape.orientation, item.modifiedShape.flipped, item.unitCount);
                bag.contentWindow.Hide(); Advance(5); return false;
            }
            case 5:
                Require(ItemPinHelper.IsPinned(pinned) && ItemPinHelper.IsPinned(pinToRelease), "Closing the window changed native pins");
                PlayerStore.Instance.SaveGame(); Require(StoreStation.GetDayCounter() == day, "The test advanced the day");
                StoreUIManager.Instance.escapeUIManager.OnMainMenu(); load = new QaLoadSlot(slot, Guid.Parse(run));
                Advance(6); return false;
            case 6:
                if (!load.Tick()) return false;
                Require(StoreStation.GetDayCounter() == day, "Reload changed the day");
                var loadedBag = EmporiumEntry.Instance.invElement.childItems.SingleOrDefault(item => item.GetDisplayName(true) == "QA Sort Bag " + token);
                Require(loadedBag?.contentWindow?.childElement is GameGridInventory, "Saved test backpack did not reload");
                var loaded = (GameGridInventory)loadedBag.contentWindow.childElement;
                foreach (var pair in saved)
                {
                    var item = loaded.childItems.SingleOrDefault(candidate => candidate.GetDisplayName(true) == pair.Key);
                    Require(item != null && item.unitCount == pair.Value.count && item.modifiedShape.minX == pair.Value.x &&
                        item.modifiedShape.minY == pair.Value.y && item.modifiedShape.orientation == pair.Value.orientation &&
                        item.modifiedShape.flipped == pair.Value.flipped, "The native save did not preserve sorted item " + pair.Key);
                    if (pair.Key == "QA Pinned " + token) Require(ItemPinHelper.IsPinned(item), "Native pin did not survive save/load");
                    if (pair.Key == "QA Pin To Release " + token) Require(ItemPinHelper.IsPinned(item), "Second native pin did not survive save/load");
                }
                ItemMouseDoubleClickHandler.current.OpenContentAction(loadedBag, new Vector2(120, 120));
                bag = loadedBag as GameItemElement;
                inner = loaded;
                pinToRelease = loaded.childItems.Single(item => item.GetDisplayName(true) == "QA Pin To Release " + token) as GameItemElement;
                pinned = loaded.childItems.Single(item => item.GetDisplayName(true) == "QA Pinned " + token) as GameItemElement;
                Require(bag != null && pinToRelease != null && pinned != null, "Reloaded pin fixtures unavailable");
                Advance(7); return false;
            case 7:
                if (!bag.contentWindow.IsVisible()) return false;
                Require(ItemPinHelper.IsPinned(pinToRelease) && ItemPinHelper.IsPinned(pinned), "Reloaded native pins are missing");
                input = new QaSyntheticInput(keyboard: true, mouseButtons: true);
                RightDown(ItemCell(inner, pinToRelease)); Advance(70); return false;
            case 70:
                ClearInput(); input.Mouse = NativePinPoint(); Advance(71); return false;
            case 71:
                PressLeft(); Advance(72); return false;
            case 72:
                ReleaseLeft(); Advance(73); return false;
            case 73:
                ClearInput(); input.Dispose(); input = null;
                Require(!ItemPinHelper.IsPinned(pinToRelease) && ItemPinHelper.IsPinned(pinned), "Native unpin did not apply");
                GridShape released = pinToRelease.modifiedShape.Clone();
                GridShape stillPinned = pinned.modifiedShape.Clone();
                InvokeSortMenu();
                Require(!SamePose(pinToRelease.modifiedShape, released) && SamePose(pinned.modifiedShape, stillPinned),
                    "Arrangement did not move the unpinned item while preserving the remaining native pin");
                result = "PASS native right-click sort, pins, unpinned containers, safe refusal, window close, same-day reload and unpin";
                return true;
            default: throw new InvalidOperationException("Unknown organizer QA step");
        }
    }

    private void Prepare()
    {
        TradeScenario.SetOptions(true, "f05.enabled"); TradeScenario.SetOptions(false, "f05.rotate");
        bag = NewItem("backpack_large", "QA Sort Bag " + token); PutAny(EmporiumEntry.Instance.invElement, bag);
        inner = bag.contentWindow?.childElement as GameGridInventory;
        Require(inner != null, "Native backpack has no ordinary grid");
        pinToRelease = NewItem("processed_milk", "QA Pin To Release " + token); pinned = NewItem("processed_juice", "QA Pinned " + token);
        first = NewItem("processed_meat", "QA First " + token); second = NewItem("processed_meat", "QA Second " + token);
        container = NewItem("fanny_pack", "QA Fixed Container " + token); irregular = NewItem("cup_noodle", "QA Irregular " + token);
        // Native backpacks reject ordinary nested containers; this fixture uses the game's
        // own explicit nestable-device exception and still exercises a real content window.
        container.EnableTag("NESTABLE_DEVICE_TAG");
        Require(ContainerHelper.IsNestingAllowed(container, bag) && inner.MayHaveValidInventorySlot(container),
            "The native nestable-container exception is unavailable");
        irregular.shape = new GridShapeBuilder(2, 2).SetData("1011", 2).Build(); irregular.modifiedShape = irregular.shape.Clone();
        foreach (var item in new[] { container, irregular, pinToRelease, pinned, first, second }) PutFar(inner, item);
        ItemPinHelper.SetPinned(pinned, true);
        foreach (var item in new[] { first, second, pinToRelease, pinned, container, irregular })
        {
            item.onSlotAddItemFunc += (value, inventory, marker) => callbacks++;
            item.onSlotRemoveItemFunc += (value, inventory, marker) => callbacks++;
        }
        inner.onSlotAddItemFunc += (value, inventory, marker) => callbacks++;
        inner.onSlotRemoveItemFunc += (value, inventory, marker) => callbacks++;
        day = StoreStation.GetDayCounter(); slot = PlayerStore.Instance.saveSlotId; run = PlayerStore.Instance.runID;
        Require(Guid.TryParse(run, out _), "Missing isolated run ID");
        PlayerStore.Instance.SaveGame(); ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(120, 120));
    }

    private void InvokeSortMenu()
    {
        var handler = ItemContextHandler.current; var type = typeof(ItemContextHandler);
        var open = type.GetMethod("TryOpenSortMenu", Fields); var trigger = type.GetMethod("TriggerButton", Fields);
        var menu = type.GetField("sortWindow", Fields)?.GetValue(handler) as PixelWindow;
        Require(open != null && trigger != null && menu != null, "Native grid context handler changed");
        bool found = false; Vector2 point = default; float cell = UISettings.current.gridSize;
        for (int y = 0; y < inner.inventoryShape.height && !found; y++) for (int x = 0; x < inner.inventoryShape.width && !found; x++)
        {
            Vector3 world = inner.rectTransform.TransformPoint(new Vector3((x + .5f) * cell, -(y + .5f) * cell));
            var canvas = inner.handler.GetComponentInParent<Canvas>();
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            point = RectTransformUtility.WorldToScreenPoint(camera, world);
            found = ReferenceEquals(RenderHandler.RaycastElement<GameGridInventory>(point), inner);
        }
        Require(found && (bool)open.Invoke(handler, new object[] { point }), "Could not open the native grid context menu");
        var node = menu.handler.GetComponentsInChildren<Transform>(true).FirstOrDefault(value => value.name == "ShopAssist_SortMenu" && value.gameObject.activeInHierarchy);
        var button = node != null ? node.GetComponent<TreeNodeRender>()?.element as RichTextElement : null;
        Require(button != null && node.gameObject.activeInHierarchy, "Direct sort context item was not attached"); trigger.Invoke(handler, new object[] { button });
    }

    private void RightDown(Vector2 point)
    {
        input.Mouse = point; input.MouseDown = 1; input.MouseHeld = 1; input.Down = ItemContextHandler.current.triggerKey;
    }
    private void PressLeft() { input.MouseDown = 0; input.MouseHeld = 0; }
    private void ReleaseLeft() { input.MouseDown = -1; input.MouseHeld = -1; input.MouseUp = 0; }
    private void ClearInput() { input.MouseDown = input.MouseUp = input.MouseHeld = -1; input.Down = input.Held = input.Up = KeyCode.None; }

    private static Vector2 ItemCell(GameGridInventory grid, GameItem item)
    {
        Canvas.ForceUpdateCanvases();
        Canvas canvas = grid.handler.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        for (int y = item.modifiedShape.minY; y <= item.modifiedShape.maxY; y++)
            for (int x = item.modifiedShape.minX; x <= item.modifiedShape.maxX; x++)
            {
                if (item.modifiedShape.Get(x, y) == 0) continue;
                var point = RectTransformUtility.WorldToScreenPoint(camera, grid.rectTransform.TransformPoint(
                    new Vector3((x + .5f) * UISettings.current.gridSize, -(y + .5f) * UISettings.current.gridSize)));
                if (ReferenceEquals(RenderHandler.RaycastElement<GameItem>(point), item)) return point;
            }
        throw new InvalidOperationException("Pin fixture has no raycastable cell");
    }

    private Vector2 NativePinPoint()
    {
        var handler = ItemContextHandler.current;
        var type = typeof(ItemContextHandler);
        var menu = type.GetField("contextWindow", Fields)?.GetValue(handler) as PixelWindow;
        var button = type.GetField("buttonPin", Fields)?.GetValue(handler) as RichTextElement;
        var background = button == null ? null : typeof(RichTextElement).GetField("background", Fields)?.GetValue(button) as TreeNodeRender;
        Require(menu?.IsVisible() == true && ReferenceEquals(type.GetField("currentItem", Fields)?.GetValue(handler), pinToRelease) &&
            Equals(type.GetField("canPin", Fields)?.GetValue(handler), true) &&
            button?.handler != null && button.handler.gameObject.activeInHierarchy && background != null,
            "Native item pin row did not open");
        Canvas.ForceUpdateCanvases();
        Canvas canvas = handler.overlayCanvas;
        Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.WorldToScreenPoint(camera, background.rectTransform.TransformPoint(background.rectTransform.rect.center));
    }

    private static GameItemElement NewItem(string id, string name)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement; Require(item != null, "Missing native fixture " + id);
        GeneralHelper.SetItemOwned(item, true); GeneralHelper.SetCustomName(item, name); return item;
    }
    private static void PutAny(GameInventory inventory, GameItem item)
    {
        var marker = inventory.TryFindOneValidInventorySlot(item);
        Require(marker != null && marker.inventory == inventory && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount, "Could not place " + item.identifier);
    }
    private static void PutFar(GameGridInventory inventory, GameItem item)
    {
        var shape = new GridShapeBuilder(item.modifiedShape);
        for (int y = inventory.inventoryShape.height - shape.globalHeight; y >= 0; y--)
        for (int x = inventory.inventoryShape.width - shape.globalWidth; x >= 0; x--)
        {
            shape.SetPosition(x, y); var marker = inventory.TryInventorySlot(item, item.unitCount, shape);
            if (marker?.inventory != inventory || marker.targetItem != null || marker.numTransfer < item.unitCount) continue;
            if (marker.TryAcceptOnce(item.unitCount) == item.unitCount) return;
        }
        throw new InvalidOperationException("Could not place QA fixture " + item.identifier);
    }
    private static bool SamePose(GridShape a, GridShape b) => a.minX == b.minX && a.minY == b.minY && a.orientation == b.orientation && a.flipped == b.flipped;
    private void Advance(int value) { step = value; since = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
