using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Drives the native selection state machine after an occupied-cell press in an isolated full grid.
public sealed class OccupiedSelectionScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private GameItem bag;
    private GameGridInventory grid;
    private GameItemElement first;
    private int step;
    private float since = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 75) throw new InvalidOperationException("Occupied selection QA timed out at step " + step);
        if (step == 0)
        {
            if (!start.Tick()) return false;
            step = 1; since = Time.realtimeSinceStartup; return false;
        }
        if (step == 1)
        {
            if (StoreUIManager.Instance?.gameOptionUIManager == null || ItemMultiSelectHandler.current == null ||
                ItemMouseDoubleClickHandler.current == null || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true) return false;
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            TradeScenario.SetOptions(true, "f08.enabled", "f40.occupied_drag");
            bag = DirectoryMaster.Item("fanny_pack");
            Require(bag != null, "Fanny pack fixture unavailable");
            GeneralHelper.SetItemOwned(bag, true); Put(EmporiumEntry.Instance.invElement, bag);
            grid = bag.contentWindow?.childElement as GameGridInventory;
            Require(grid != null && grid.inventoryShape.width == 3 && grid.inventoryShape.height == 3, "Expected a small ordinary grid");
            string oneCell = null;
            foreach (string id in new[] { "wine_yeast", "small_morsel", "cup_noodle", "processed_cheese", "cat_bar" })
            {
                var candidate = DirectoryMaster.Item(id) as GameItemElement;
                if (candidate != null && candidate.modifiedShape.width == 1 && candidate.modifiedShape.height == 1 &&
                    candidate.modifiedShape.GetLocal(0, 0) == 1)
                { oneCell = id; first = candidate; break; }
            }
            Require(oneCell != null, "No one-cell native fixture is available");
            GeneralHelper.SetItemOwned(first, true); Put(grid, first);
            for (int i = 1; i < 9; i++)
            {
                var item = DirectoryMaster.Item(oneCell);
                GeneralHelper.SetItemOwned(item, true); Put(grid, item);
            }
            Require(!grid.HasFreeCell(), "The test inventory is not full");
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(320, 320));
            bag.contentWindow.ToFront();
            step = 2; since = Time.realtimeSinceStartup; return false;
        }
        var mod = ModLoader.Mods.Find(value => value.Manifest != null && value.Manifest.ID == "starswhere.shopassist");
        Require(mod?.Instance != null && bag.contentWindow.IsVisible(), "The test scene is unavailable");
        var gesture = mod.Instance.GetType().Assembly.GetType("ShopAssist.OccupiedSelectionGesture", true);
        var begin = gesture.GetMethod("Begin", BindingFlags.Static | BindingFlags.NonPublic);
        var tryBegin = gesture.GetMethod("TryBegin", BindingFlags.Static | BindingFlags.NonPublic);
        var handler = ItemMultiSelectHandler.current;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var move = typeof(ItemMultiSelectHandler).GetMethod("OnEventMouseMove", flags);
        var release = typeof(ItemMultiSelectHandler).GetMethod("OnEventRelease", flags);
        var selected = typeof(ItemMultiSelectHandler).GetField("selectedItems", flags);
        var state = typeof(ItemMultiSelectHandler).GetField("state", flags);
        Require(begin != null && tryBegin != null && move != null && release != null && selected != null && state != null,
            "Native selection interface changed");
        // Stock placement order and canvas scale are game settings. Sweep between actual opposite cells.
        Vector3 Center(GameItemElement item)
        {
            var rect = item.middle.rectTransform;
            float half = UISettings.current.gridSize * 0.5f;
            return rect.position + new Vector3(half * rect.lossyScale.x, -half * rect.lossyScale.y, 0);
        }
        var visible = grid.childItems.OfType<GameItemElement>().OrderByDescending(i => Center(i).y).ThenBy(i => Center(i).x).ToList();
        first = visible.First(); Vector3 position = Center(first), end = Center(visible.Last());
        var nativeRect = Rect.MinMaxRect(Mathf.Min(position.x, end.x), Mathf.Min(position.y, end.y),
            Mathf.Max(position.x, end.x), Mathf.Max(position.y, end.y));
        var ui = StoreUIManager.Instance;
        // Synthetic pointer events must use the original canvas hit tests at those event coordinates.
        // The physical desktop cursor can be over a different paper panel in an isolated player.
        bool PaperAt(Vector3 point)
        {
            var pointer = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { position = point };
            var dock = typeof(StoreUIManager).GetMethod("DockCanvasVerdict", flags);
            var raised = typeof(StoreUIManager).GetMethod("RaisedFloatingWindowHit", flags);
            int verdict = (int)dock.Invoke(ui, new object[] { pointer });
            if (verdict != 0) return verdict > 0;
            if ((bool)raised.Invoke(ui, new object[] { pointer })) return false;
            return ui.PerformRaycastOnCanvas(ui.primaryCanvas, pointer) || ui.PerformRaycastOnCanvas(ui.secondaryCanvas, pointer) ||
                ui.PerformRaycastOnCanvas(ui.additionalCanvas, pointer);
        }
        Require(RenderHandler.RaycastElement<GameItemElement>(position) == first && !PaperAt(position) && !PaperAt(end),
            "A store panel actually covers the synthetic selection coordinates");
        Require((bool)begin.Invoke(null, new object[] { handler, first, position }), "Occupied-cell band did not start");
        Require(state.GetValue(handler).ToString() == "PendingBand", "Gesture did not enter native PendingBand");
        int candidates = ((IList)typeof(ItemMultiSelectHandler).GetMethod("ComputeBandItems", flags).Invoke(handler, new object[] { nativeRect })).Count;
        string moveState;
        try
        {
            ui.hoveringStoreUI = PaperAt(end);
            move.Invoke(handler, new object[] { end, end - position });
            moveState = state.GetValue(handler).ToString();
            release.Invoke(handler, new object[] { new HashSet<KeyCode> { handler.key } });
        }
        finally { ui.CheckRaycast(); }
        var items = selected.GetValue(handler) as IList;
        Require(state.GetValue(handler).ToString() == "Selected" && items != null && items.Count == 9 && candidates == 9,
            "Native full-grid drag: bounds=" + nativeRect + " candidates=" + candidates + " move=" + moveState + " end=" +
            state.GetValue(handler) + " selected=" + items?.Count + " overStoreUI=" + StoreUIManager.Instance.hoveringStoreUI);
        Require(!(bool)tryBegin.Invoke(null, new object[] { handler, new HashSet<KeyCode> { handler.key } }),
            "A normal press without Alt was intercepted after completing the band");
        handler.Cancel();
        result = "PASS occupied-cell press enters native full-grid band selection; release completes and a later non-Alt press is not intercepted";
        return true;
    }

    private static void Put(GameInventory inventory, GameItem item)
    {
        var slot = inventory.TryFindOneValidInventorySlot(item);
        Require(slot != null && slot.inventory == inventory && slot.targetItem == null &&
            slot.numTransfer >= item.unitCount && slot.TryAcceptOnce(item.unitCount) == item.unitCount, "Could not place test item");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
