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
    private GameItemElement bag, held, pinned, first, second, container, irregular;
    private GameGridInventory inner;
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
                InvokeHoldMenu(); Advance(3); return false;
            case 3:
            {
                if (!HoldVisible(held)) return false;
                Require(!ItemPinHelper.IsPinned(held) && ItemPinHelper.IsPinned(pinned), "Temporary hold modified native pin state");
                var before = new[] { first, second, held, pinned, container, irregular }.ToDictionary(item => item, item => item.modifiedShape.Clone());
                InvokeSortMenu();
                Require(CustomUIManager.Instance.GetWindow(Mod + ".inventory_organizer") == null,
                    "Right-click sorting opened the obsolete preview window");
                Require(callbacks == 0, "Same-grid sorting invoked inventory entry or exit callbacks");
                Require(before.Keys.All(item => ReferenceEquals(item.parentInventory, inner)), "Sorting changed an item parent");
                Require(SamePose(held.modifiedShape, before[held]) && SamePose(pinned.modifiedShape, before[pinned]) &&
                    SamePose(container.modifiedShape, before[container]), "A held item, pinned item, or container moved");
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
                var before = new[] { first, second, held, pinned, container, irregular }.ToDictionary(item => item, item => item.modifiedShape.Clone());
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
                foreach (var item in new[] { first, second, held, pinned, container })
                    saved[item.GetDisplayName(true)] = (item.modifiedShape.minX, item.modifiedShape.minY,
                        item.modifiedShape.orientation, item.modifiedShape.flipped, item.unitCount);
                bag.contentWindow.Hide(); Advance(5); return false;
            }
            case 5:
                if (HoldVisible(held)) return false;
                Require(ItemPinHelper.IsPinned(pinned) && !ItemPinHelper.IsPinned(held), "Closing the window changed native pins");
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
                    if (pair.Key == "QA Held " + token) Require(!ItemPinHelper.IsPinned(item), "Temporary hold wrote a native pin");
                }
                ItemMouseDoubleClickHandler.current.OpenContentAction(loadedBag, Vector2.zero);
                var loadedHeld = loaded.childItems.Single(item => item.GetDisplayName(true) == "QA Held " + token) as GameItemElement;
                Require(loadedHeld != null && !HoldVisible(loadedHeld), "Temporary hold survived closing and reloading");
                result = "PASS native right-click direct sort, fixed container and holds, same-kind rows, irregular cells, safe refusal and same-day reload";
                return true;
            default: throw new InvalidOperationException("Unknown organizer QA step");
        }
    }

    private void Prepare()
    {
        TradeScenario.SetOptions(true, "f05.enabled", "f06.enabled"); TradeScenario.SetOptions(false, "f05.rotate");
        bag = NewItem("backpack_large", "QA Sort Bag " + token); PutAny(EmporiumEntry.Instance.invElement, bag);
        inner = bag.contentWindow?.childElement as GameGridInventory;
        Require(inner != null, "Native backpack has no ordinary grid");
        held = NewItem("processed_milk", "QA Held " + token); pinned = NewItem("processed_juice", "QA Pinned " + token);
        first = NewItem("processed_meat", "QA First " + token); second = NewItem("processed_meat", "QA Second " + token);
        container = NewItem("fanny_pack", "QA Fixed Container " + token); irregular = NewItem("cup_noodle", "QA Irregular " + token);
        // Native backpacks reject ordinary nested containers; this fixture uses the game's
        // own explicit nestable-device exception and still exercises a real content window.
        container.EnableTag("NESTABLE_DEVICE_TAG");
        Require(ContainerHelper.IsNestingAllowed(container, bag) && inner.MayHaveValidInventorySlot(container),
            "The native nestable-container exception is unavailable");
        irregular.shape = new GridShapeBuilder(2, 2).SetData("1011", 2).Build(); irregular.modifiedShape = irregular.shape.Clone();
        foreach (var item in new[] { container, irregular, held, pinned, first, second }) PutFar(inner, item);
        ItemPinHelper.SetPinned(pinned, true);
        foreach (var item in new[] { first, second, held, pinned, container, irregular })
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

    private void InvokeHoldMenu()
    {
        var handler = ItemContextHandler.current; var type = typeof(ItemContextHandler);
        var itemField = type.GetField("currentItem", Fields); var menu = type.GetField("contextWindow", Fields)?.GetValue(handler) as PixelWindow;
        var press = type.GetMethod("OnEventPress", Fields); var trigger = type.GetMethod("TriggerButton", Fields);
        Require(itemField != null && menu != null && press != null && trigger != null, "Native item context handler changed");
        itemField.SetValue(handler, held); menu.Show(); press.Invoke(handler, new object[] { new HashSet<KeyCode>() });
        var node = menu.handler.transform.Find("ShopAssist_HoldMenu");
        var button = node != null ? node.GetComponent<TreeNodeRender>()?.element as RichTextElement : null;
        Require(button != null && node.gameObject.activeInHierarchy, "Temporary hold context item was not attached"); trigger.Invoke(handler, new object[] { button });
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
        var node = menu.handler.transform.Find("ShopAssist_SortMenu");
        var button = node != null ? node.GetComponent<TreeNodeRender>()?.element as RichTextElement : null;
        Require(button != null && node.gameObject.activeInHierarchy, "Direct sort context item was not attached"); trigger.Invoke(handler, new object[] { button });
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
    private static bool HoldVisible(GameItemElement item)
    {
        var node = item?.rotator?.transform.Find("ShopAssist_HoldOutline"); return node != null && node.gameObject.activeInHierarchy;
    }
    private static bool SamePose(GridShape a, GridShape b) => a.minX == b.minX && a.minY == b.minY && a.orientation == b.orientation && a.flipped == b.flipped;
    private void Advance(int value) { step = value; since = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
