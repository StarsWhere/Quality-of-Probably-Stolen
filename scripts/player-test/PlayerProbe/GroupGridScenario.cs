using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Exercises the native group state/ghosts and the adapter's exact transaction in an isolated save.
public sealed class GroupGridScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private int step;
    private GameItem sourceBag, targetBag;
    private GameGridInventory source, target;
    private GameItemElement first, second;

    public bool Tick(out string result)
    {
        result = null;
        Check(Time.realtimeSinceStartup < deadline, "Group scenario timed out at step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || ItemMultiSelectHandler.current == null ||
                ItemMouseDoubleClickHandler.current == null || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(true, "f40.enabled", "f41.enabled");
            CheckIndependentSettings();
            sourceBag = New("fanny_pack"); targetBag = New("fanny_pack");
            GeneralHelper.SetCustomName(sourceBag, "QA Group Source"); GeneralHelper.SetCustomName(targetBag, "QA Group Target");
            Put(EmporiumEntry.Instance.invElement, sourceBag); Put(EmporiumEntry.Instance.invElement, targetBag);
            source = sourceBag.contentWindow.childElement as GameGridInventory; target = targetBag.contentWindow.childElement as GameGridInventory;
            Check(source != null && target != null, "Missing native group inventories");
            first = New("wine_yeast") as GameItemElement; second = New("wine_yeast") as GameItemElement;
            Check(first != null && second != null, "Missing native group items"); Put(source, first); Put(source, second);
            ItemMouseDoubleClickHandler.current.OpenContentAction(sourceBag, new Vector2(120, 180));
            step++; return false;
        }
        if (step == 2)
        {
            var handler = ItemMultiSelectHandler.current;
            var assembly = ModLoader.Mods.Find(value => value.Manifest?.ID == Mod)?.Instance?.GetType().Assembly;
            var adapter = assembly?.GetType("ShopAssist.GroupPlacement");
            Check(adapter != null, "Group adapter is not loaded");
            var selected = (IList)typeof(ItemMultiSelectHandler).GetField("selectedItems", F).GetValue(handler);
            var homes = (IDictionary)typeof(ItemMultiSelectHandler).GetField("selectedHomes", F).GetValue(handler);
            selected.Clear(); homes.Clear(); selected.Add(first); selected.Add(second); homes.Add(first, source); homes.Add(second, source);
            FieldInfo state = typeof(ItemMultiSelectHandler).GetField("state", F);
            state.SetValue(handler, Enum.Parse(state.FieldType, "PendingGroupDrag"));
            typeof(ItemMultiSelectHandler).GetMethod("StartGroupDrag", F).Invoke(handler, null);
            Check(handler.IsGroupDragging, "Native group ghosts did not start");
            var offsets = typeof(ItemMultiSelectHandler).GetField("dragOffsets", F).GetValue(handler);
            adapter.GetMethod("Rotate", F).Invoke(null, new[] { (object)true, offsets });
            var active = (IList)adapter.GetField("active", F).GetValue(null);
            Check(active?.Count == 2 && first.tempShape.orientation == (first.modifiedShape.orientation + 1) % 4 &&
                second.tempShape.orientation == (second.modifiedShape.orientation + 1) % 4, "Group ghosts did not rotate together");
            GridShape a = (GridShape)active[0].GetType().GetField("Pose", F).GetValue(active[0]);
            GridShape b = (GridShape)active[1].GetType().GetField("Pose", F).GetValue(active[1]);
            Check(a.minX - b.minX == first.modifiedShape.minY - second.modifiedShape.minY &&
                a.minY - b.minY == second.modifiedShape.minX - first.modifiedShape.minX, "Rotated offsets are incorrect");
            var legal = adapter.GetMethod("Legal", F); int dx = 0, dy = 0; bool found = false;
            for (int y = -8; y <= 8 && !found; y++) for (int x = -8; x <= 8 && !found; x++)
                if ((bool)legal.Invoke(null, new object[] { target, x, y })) { dx = x; dy = y; found = true; }
            Check(found, "No valid exact destination for the formation");
            // A native add callback can fail after the first member moves. Both original poses must return.
            Action<GameItem, GameInventory, SlotMarker> veto = (item, inventory, slot) =>
            { if (inventory == target) throw new InvalidOperationException("QA injected second-item veto"); };
            second.onSlotAddItemFunc += veto;
            var commit = adapter.GetMethod("Commit", F);
            bool rejected = !(bool)commit.Invoke(null, new object[] { target, dx, dy });
            second.onSlotAddItemFunc -= veto;
            Check(rejected && first.parentInventory == source && second.parentInventory == source &&
                source.childItems.Contains(first) && source.childItems.Contains(second), "A failed group move did not restore both source items");
            Check((bool)commit.Invoke(null, new object[] { target, dx, dy }), "Valid rotated group did not commit");
            handler.Cancel();
            Check(first.parentInventory == target && second.parentInventory == target && !source.childItems.Contains(first) &&
                !source.childItems.Contains(second) && first.modifiedShape.minX == a.minX + dx && first.modifiedShape.minY == a.minY + dy &&
                second.modifiedShape.minX == b.minX + dx && second.modifiedShape.minY == b.minY + dy &&
                first.modifiedShape.orientation == a.orientation && second.modifiedShape.orientation == b.orientation,
                "Group commit did not retain both exact rotated poses");
            step = 3; return false;
        }
        if (step == 3)
        {
            var handler = ItemMultiSelectHandler.current;
            var assembly = ModLoader.Mods.Find(value => value.Manifest?.ID == Mod)?.Instance?.GetType().Assembly;
            var adapter = assembly?.GetType("ShopAssist.GroupPlacement");
            Check(adapter != null && first.parentInventory == target && second.parentInventory == target, "Same-grid source disappeared");
            GridShape firstHome = first.modifiedShape.Clone(), secondHome = second.modifiedShape.Clone();
            int firstCount = first.unitCount, secondCount = second.unitCount;
            var selected = (IList)typeof(ItemMultiSelectHandler).GetField("selectedItems", F).GetValue(handler);
            var homes = (IDictionary)typeof(ItemMultiSelectHandler).GetField("selectedHomes", F).GetValue(handler);
            selected.Clear(); homes.Clear(); selected.Add(first); selected.Add(second); homes.Add(first, target); homes.Add(second, target);
            FieldInfo state = typeof(ItemMultiSelectHandler).GetField("state", F);
            state.SetValue(handler, Enum.Parse(state.FieldType, "PendingGroupDrag"));
            typeof(ItemMultiSelectHandler).GetMethod("StartGroupDrag", F).Invoke(handler, null);
            Check(handler.IsGroupDragging, "Same-grid native group ghosts did not start");
            var offsets = typeof(ItemMultiSelectHandler).GetField("dragOffsets", F).GetValue(handler);
            adapter.GetMethod("Rotate", F).Invoke(null, new[] { (object)true, offsets });
            var active = (IList)adapter.GetField("active", F).GetValue(null);
            Check(active?.Count == 2, "Same-grid formation snapshot is unavailable");
            GridShape a = (GridShape)active[0].GetType().GetField("Pose", F).GetValue(active[0]);
            GridShape b = (GridShape)active[1].GetType().GetField("Pose", F).GetValue(active[1]);
            var legal = adapter.GetMethod("Legal", F); int dx = 0, dy = 0; bool found = false;
            GridShape firstDestination = null, secondDestination = null;
            for (int y = -8; y <= 8 && !found; y++) for (int x = -8; x <= 8 && !found; x++)
            {
                if (!(bool)legal.Invoke(null, new object[] { target, x, y })) continue;
                GridShape p = new GridShapeBuilder(a).SetPosition(a.minX + x, a.minY + y).Build();
                GridShape q = new GridShapeBuilder(b).SetPosition(b.minX + x, b.minY + y).Build();
                bool overlapsOld = p.Intersects(firstHome) || p.Intersects(secondHome) || q.Intersects(firstHome) || q.Intersects(secondHome);
                if (!overlapsOld || SamePose(p, firstHome) && SamePose(q, secondHome)) continue;
                dx = x; dy = y; firstDestination = p; secondDestination = q; found = true;
            }
            Check(found, "No valid same-grid rotation overlaps the original cells");
            int removed = 0, added = 0, vetoes = 0;
            Action<GameItem, GameInventory, SlotMarker> onRemove = (item, inventory, slot) => { if (inventory == target) removed++; };
            Action<GameItem, GameInventory, SlotMarker> onAdd = (item, inventory, slot) => { if (inventory == target) added++; };
            Action<GameItem, GameInventory, SlotMarker> veto = (item, inventory, slot) =>
            { if (inventory == target && vetoes == 0) { vetoes++; throw new InvalidOperationException("QA injected same-grid second-item veto"); } };
            first.onSlotRemoveItemFunc += onRemove; second.onSlotRemoveItemFunc += onRemove;
            first.onSlotAddItemFunc += onAdd; second.onSlotAddItemFunc += onAdd; second.onSlotAddItemFunc += veto;
            var commit = adapter.GetMethod("Commit", F);
            try
            {
                bool rejected = !(bool)commit.Invoke(null, new object[] { target, dx, dy });
                Check(rejected && vetoes == 1 && removed >= 2 && added >= 2 && first.parentInventory == target && second.parentInventory == target &&
                    first.unitCount == firstCount && second.unitCount == secondCount && SamePose(first.modifiedShape, firstHome) &&
                    SamePose(second.modifiedShape, secondHome), "Same-grid callback failure did not restore the entire original formation");
                second.onSlotAddItemFunc -= veto;
                int beforeRemove = removed, beforeAdd = added;
                Check((bool)commit.Invoke(null, new object[] { target, dx, dy }) && removed >= beforeRemove + 2 && added >= beforeAdd + 2,
                    "Same-grid rotation did not invoke both native remove/add callbacks");
            }
            finally
            {
                first.onSlotRemoveItemFunc -= onRemove; second.onSlotRemoveItemFunc -= onRemove;
                first.onSlotAddItemFunc -= onAdd; second.onSlotAddItemFunc -= onAdd; second.onSlotAddItemFunc -= veto;
                handler.Cancel();
            }
            Check(first.parentInventory == target && second.parentInventory == target && first.unitCount == firstCount && second.unitCount == secondCount &&
                SamePose(first.modifiedShape, firstDestination) && SamePose(second.modifiedShape, secondDestination),
                "Same-grid rotation did not commit both exact poses");
            result = "PASS native group ghosts, cross-grid and overlapping same-grid rotation, native callbacks and both failure rollbacks";
            return true;
        }
        throw new InvalidOperationException("Unknown group scenario step");
    }

    private static GameItem New(string id)
    {
        GameItem item = DirectoryMaster.Item(id); Check(item != null, "Missing fixture " + id); GeneralHelper.SetItemOwned(item, true); return item;
    }
    private static void Put(GameInventory inventory, GameItem item)
    {
        SlotMarker slot = inventory.TryFindOneValidInventorySlot(item);
        Check(slot != null && slot.targetItem == null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory,
            "Could not place fixture " + item.identifier);
    }
    private static void Check(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private static void CheckIndependentSettings()
    {
        var assembly = ModLoader.Mods.Find(value => value.Manifest?.ID == Mod)?.Instance?.GetType().Assembly;
        var adapter = assembly?.GetType("ShopAssist.GroupPlacement");
        Check(adapter != null, "Group adapter is not loaded for settings test");
        bool Read(string name) => ((Func<bool>)adapter.GetField(name, F).GetValue(null))();
        TradeScenario.SetOptions(false, "f40.enabled");
        Check(!Read("rotationEnabled") && Read("preciseEnabled"), "F40 changed precise placement instead of rotation");
        TradeScenario.SetOptions(true, "f40.enabled");
        TradeScenario.SetOptions(false, "f41.enabled");
        Check(Read("rotationEnabled") && !Read("preciseEnabled"), "F41 changed rotation instead of precise placement");
        TradeScenario.SetOptions(true, "f41.enabled");
        Check(Read("rotationEnabled") && Read("preciseEnabled"), "Group options were not restored");
    }
    private static bool SamePose(GridShape a, GridShape b) => a != null && b != null && a.minX == b.minX && a.minY == b.minY &&
        a.orientation == b.orientation && a.flipped == b.flipped;
}
