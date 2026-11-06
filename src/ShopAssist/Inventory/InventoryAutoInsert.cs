using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    // Native drop remains in charge. Only a fully captured, reversible remainder
    // can cause an automatic arrangement after the native attempt.
    internal static class InventoryAutoInsert
    {
        private sealed class Request
        {
            internal InventoryArrangeTransaction Transaction;
            internal GameGridInventory Target;
            internal GameItem[] Items;
            internal readonly Dictionary<GameItem, GridShape> Poses = new Dictionary<GameItem, GridShape>();
        }

        private static readonly MethodBase[] NativeMutations = {
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.UncheckedAccept)),
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.Expel)),
            AccessTools.Method(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce)),
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.TryFindOneValidInventorySlot), new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.TryFindOneValidInventorySlot), new[] { typeof(GameItem), typeof(bool) }),
            AccessTools.Method(typeof(GraphUtils), nameof(GraphUtils.TryAcceptAll), new[] { typeof(GraphNodeStorage), typeof(GameItem), typeof(int) }),
            AccessTools.Method(typeof(GameItemElement), nameof(GameItemElement.Validate)),
            AccessTools.Method(typeof(GameItem), "RemoveTemporaryClientFeature"),
            AccessTools.Method(typeof(GameItem), "RemoveContrabandFeature"),
            AccessTools.Method(typeof(PlayerStore), "AddedItemToMainInvArea"),
            AccessTools.Method(typeof(PlayerStore), "RemovedItemToMainInvArea"),
            AccessTools.Method(typeof(PlayerStore), "OnMainInvAreaChange")
        };
        private static readonly Guid VerifiedGameModule = new Guid("83045710-adce-4e78-8ff1-b43a9399ec0d");
        private static bool KnownCallbacks(Delegate callbacks, Func<Delegate, bool> allowed)
        {
            if (callbacks == null) return true;
            foreach (Delegate callback in callbacks.GetInvocationList())
                if (callback.Method.Module.ModuleVersionId != VerifiedGameModule || !KnownPatches(callback.Method) || !allowed(callback)) return false;
            return true;
        }
        private static bool Method(Delegate callback, string type, string name) =>
            callback.Method.DeclaringType?.FullName == type && callback.Method.Name == name;
        private static bool SafeAddRule(GameGridInventory grid) => KnownCallbacks(grid.mayInventoryAddItemFunc, callback =>
            (ReferenceEquals(grid, EmporiumEntry.Instance?.invElement) && ReferenceEquals(callback.Target, EmporiumEntry.Instance) &&
                Method(callback, "EmporiumEntry", "<Start>b__184_12")) ||
            (grid.GetParentItem() != null && Method(callback, "ContainerHelper+<>c__DisplayClass21_0", "<InitContainerItem>b__0")));
        private static bool SafeStockNotification(GameGridInventory grid, Delegate callbacks, bool adding) => KnownCallbacks(callbacks, callback =>
            ReferenceEquals(grid, EmporiumEntry.Instance?.invElement) && Method(callback, "EmporiumEntry+<>c",
                adding ? "<Start>b__184_19" : "<Start>b__184_20"));
        private static bool SafePriority(GameGridInventory grid) => KnownCallbacks(grid.slotPriorityFunc, callback =>
            ReferenceEquals(grid, EmporiumEntry.Instance?.invElement) && Method(callback, "EmporiumEntry+<>c", "<Start>b__184_6"));
        private static bool SafeItemAdd(GameItem item) => KnownCallbacks(item.onSlotAddItemFunc, callback =>
            ReferenceEquals(callback.Target, item) && Method(callback, "GameItem", "<.ctor>b__177_0"));
        private static bool KnownPatches(MethodBase method)
        {
            if (method == null) return false;
            var patches = Harmony.GetPatchInfo(method);
            if (patches == null) return true;
            foreach (var patch in patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers).Concat(patches.Finalizers))
                if (patch.PatchMethod?.DeclaringType?.Assembly != typeof(InventoryAutoInsert).Assembly) return false;
            return true;
        }
        private static bool KnownMutationPatches() => NativeMutations.All(KnownPatches);

        private static bool SafeGrid(GameGridInventory grid) => grid != null && grid.GetType() == typeof(GameGridInventory) &&
            SafeAddRule(grid) && SafePriority(grid) && grid.mayInventoryRemoveItemFunc == null &&
            grid.maxSlotAddItemFunc == null && grid.maxSlotRemoveItemFunc == null && grid.maxSlotAmountItemFunc == null &&
            grid.minSlotAmountItemFunc == null && grid.isItemStateValidFunc == null &&
            grid.parentItemShapeModifierForItemFunc == null && grid.parentItemStateModifierForItemFunc == null &&
            grid.parentItemSpriteModifierForItemFunc == null && grid.onSlotAddItemFunc == null &&
            SafeStockNotification(grid, grid.onSlotAddItemEndFunc, true) && grid.onSlotAddItemLateFunc == null && grid.onSlotRemoveItemFunc == null &&
            SafeStockNotification(grid, grid.onSlotRemoveItemEndFunc, false) && grid.onSlotRemoveItemLateFunc == null && grid.onSlotChangeAmountItemFunc == null;

        private static bool SafeItem(GameItem item) => item != null && item.GetType() == typeof(GameItemElement) &&
            item.contentWindow == null && item.modifiedShape != null && !string.IsNullOrEmpty(item.identifier) && item.identifier != "tutorial_page" &&
            item.GetParentWithIdentifier("backInv") == null && GraphUtils.FindAllChildrenType<GameItem>(item, null, null).Count == 0 &&
            item.itemFeatures != null && item.itemFeatures.All(feature => feature != null &&
                feature.featureType != ItemFeature.FeatureType.ClientTemporary && feature.category != null && feature.category != "contrabandMarkUp") &&
            item.mayInventoryAddItemFunc == null && item.mayInventoryRemoveItemFunc == null && item.mayStackItemFunc == null && item.slotPriorityFunc == null &&
            item.maxSlotAddItemFunc == null && item.maxSlotRemoveItemFunc == null && item.maxSlotAmountItemFunc == null &&
            item.minSlotAmountItemFunc == null && item.isItemStateValidFunc == null &&
            item.parentItemShapeModifierForItemFunc == null && item.parentItemStateModifierForItemFunc == null &&
            item.parentItemSpriteModifierForItemFunc == null && item.getItemSpriteModifierForItemFunc == null &&
            SafeItemAdd(item) && item.onSlotRemoveItemFunc == null &&
            item.onSlotChangeAmountItemFunc == null && item.onChangeAmountItemFunc == null;

        private static Request Capture(GameGridInventory target, IReadOnlyList<GameItem> items, bool group)
        {
            if (!InventoryOrganizer.AutomaticGrid(target) || !SafeGrid(target) || !KnownMutationPatches() ||
                items == null || items.Count == 0 || items.Count > 256) return null;
            if (!(items[0]?.parentInventory is GameGridInventory source) || source == target ||
                !InventoryOrganizer.AutomaticGrid(source) || !SafeGrid(source)) return null;
            var fixedItems = InventoryOrganizer.AutomaticFixed(source);
            var unique = new HashSet<GameItem>();
            foreach (GameItem item in items)
                if (!unique.Add(item) || item.parentInventory != source || !SafeItem(item) || !ShopInventory.Movable(item) ||
                    item.MaxNumRemove() < item.unitCount || fixedItems.Contains(item) || (group && item.unitCount != 1)) return null;
            if (group)
            {
                // The native group path may stack into a resident or an earlier member.
                // It also follows slot-search callbacks into child inventories;
                // those paths cannot be included in this two-grid transaction.
                if (target.slotSearchPassthroughForItemFunc != null || target.childItems.Any(resident =>
                    resident == null || resident.slotSearchPassthroughForItemFunc != null)) return null;
                foreach (GameItem item in items)
                {
                    if (target.childItems.Any(resident => resident == null || resident.GetType() != typeof(GameItemElement) ||
                        resident.mayStackItemFunc != null || resident.MayStack(item))) return null;
                    if (items.Any(other => !ReferenceEquals(other, item) && other.MayStack(item))) return null;
                }
            }
            var transaction = InventoryArrangeTransaction.Capture(new[] { source, target }, SafeGrid, SafeItem, KnownMutationPatches);
            if (transaction == null) return null;
            var request = new Request { Transaction = transaction, Target = target, Items = items.ToArray() };
            foreach (GameItem item in items)
                request.Poses.Add(item, (item is GameItemElement element && element.tempShape != null ? element.tempShape : item.modifiedShape).Clone());
            return request;
        }

        private static void Complete(Request request)
        {
            if (request == null || !InventoryOrganizer.AutomaticGrid(request.Target) ||
                !request.Transaction.NativeMovedOnly(new HashSet<GameItem>(request.Items), request.Target)) return;
            GameItem[] remaining = request.Items.Where(item => !ReferenceEquals(item.parentInventory, request.Target)).ToArray();
            if (remaining.Length == 0) return;
            var clock = Stopwatch.StartNew();
            Dictionary<GameItem, InventoryArrangeTransaction.Destination> placements = null;
            int accepted = 0;
            for (int count = remaining.Length; count > 0 && clock.ElapsedMilliseconds <= 150; count--)
            {
                placements = Plan(request, remaining.Take(count).ToArray(), clock);
                if (placements != null) { accepted = count; break; }
            }
            if (placements == null) { ShopNotice.Show(ShopSettings.Text("f05.auto_no_plan")); return; }
            var result = request.Transaction.Commit(placements);
            if (request.Transaction.LastError != null) InventoryOrganizer.AutomaticFailure(request.Transaction.LastError.ToString());
            if (result == InventorySortPlan.ApplyResult.Applied)
            {
                InventoryOrganizer.Remember(request.Transaction);
                VisibleInventoryViews.NotifyChanged();
                ShopNotice.Show(ShopSettings.Format(accepted == remaining.Length ? "f05.auto_done" : "f05.auto_partial", accepted));
            }
            else ShopNotice.Show(ShopSettings.Text(result == InventorySortPlan.ApplyResult.Restored ? "f05.restored" :
                result == InventorySortPlan.ApplyResult.Inspect ? "f05.inspect" : "f05.changed"));
        }

        private static Dictionary<GameItem, InventoryArrangeTransaction.Destination> Plan(Request request, GameItem[] incoming, Stopwatch clock)
        {
            GameGridInventory target = request.Target;
            var fixedItems = InventoryOrganizer.AutomaticFixed(target);
            var residents = target.childItems.Where(item => SafeItem(item) && ShopInventory.Movable(item) &&
                item.MaxNumRemove() >= item.unitCount && !fixedItems.Contains(item)).ToList();
            var moving = residents.Concat(incoming).Distinct().ToList();
            if (moving.Count > 256 || target.inventoryShape.width > 64 || target.inventoryShape.height > 64) return null;
            var result = new Dictionary<GameItem, InventoryArrangeTransaction.Destination>();
            int candidates = 0;
            int Area(GameItem item)
            {
                GridShape shape = item.modifiedShape;
                int area = 0;
                for (int y = 0; y < shape.height; y++) for (int x = 0; x < shape.width; x++) if (shape.GetLocal(x, y) != 0) area++;
                return area;
            }
            foreach (GameItem item in incoming.OrderByDescending(Area).Concat(residents.OrderByDescending(Area)))
            {
                GridShape initial = request.Poses.TryGetValue(item, out GridShape dragged) ? dragged : item.modifiedShape;
                var pose = new GridShapeBuilder(initial);
                GridShape chosen = null;
                for (int turn = 0; turn < (InventoryOrganizer.AutomaticRotate ? 4 : 1) && chosen == null; turn++)
                {
                    pose.SetOrientation((initial.orientation + turn) % 4);
                    for (int y = 0; y < target.inventoryShape.height && chosen == null; y++)
                    for (int x = 0; x < target.inventoryShape.width; x++)
                    {
                        if (++candidates > 1000000 || clock.ElapsedMilliseconds > 150) return null;
                        pose.SetPosition(x, y);
                        if (!target.CheckShapePlacement(item, pose, moving) || result.Values.Any(value => value.Shape.Intersects(pose))) continue;
                        chosen = pose.Build(); break;
                    }
                }
                if (chosen == null) return null;
                result.Add(item, new InventoryArrangeTransaction.Destination { Grid = target, Shape = chosen });
            }
            return result;
        }

        [HarmonyPatch(typeof(ItemMouseDragHandler), "EndDrag")]
        private static class SingleDrop
        {
            private static void Prefix(GameItemElement ___currentItem, int ___currentRemove, GameInventory ___lastInventory,
                GameItem ___lastItem, out Request __state)
            {
                __state = null;
                if (!InventoryOrganizer.AutomaticEnabled || ___lastItem != null || ___currentItem == null ||
                    ___currentRemove != ___currentItem.unitCount || !(___lastInventory is GameGridInventory target)) return;
                try
                {
                    if (!(___currentItem.parentInventory is GameGridInventory source) || source == target ||
                        !InventoryOrganizer.AutomaticGrid(source) || !InventoryOrganizer.AutomaticGrid(target) ||
                        !SafeGrid(source) || !SafeGrid(target) || !SafeItem(___currentItem)) return;
                    SlotMarker direct = target.TryInventorySlot(___currentItem, ___currentRemove,
                        ___currentItem.GetFakeRotatorCenteredPosition(), ___currentItem.tempShape, ___currentItem.modifiedState);
                    // Any native direct, partial, or stack transfer stays wholly native.
                    if (direct != null && (direct.targetItem != null || direct.numTransfer > 0)) return;
                    __state = Capture(target, new GameItem[] { ___currentItem }, false);
                }
                catch (Exception error) { InventoryOrganizer.AutomaticFailure("Capture: " + error); }
            }
            private static void Postfix(Request __state)
            {
                try { Complete(__state); }
                catch (Exception error) { InventoryOrganizer.AutomaticFailure("Drop: " + error); ShopNotice.Show(ShopSettings.Text("f05.failed")); }
            }
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "EndGroupDrag")]
        private static class GroupDrop
        {
            private static void Prefix(List<GameItemElement> ___selectedItems, out Request __state)
            {
                __state = null;
                if (!InventoryOrganizer.AutomaticEnabled || !GroupPlacement.AutomaticPlacementAllowed || ___selectedItems == null) return;
                try
                {
                    GameItem hit = RenderHandler.RaycastElement<GameItem>(Input.mousePosition);
                    if (hit != null && !___selectedItems.Contains(hit as GameItemElement)) return;
                    var target = RenderHandler.RaycastFindParentElement<GameGridInventory>(Input.mousePosition);
                    __state = Capture(target, ___selectedItems.Cast<GameItem>().ToArray(), true);
                }
                catch (Exception error) { InventoryOrganizer.AutomaticFailure("Group capture: " + error); }
            }
            private static void Postfix(Request __state)
            {
                try { Complete(__state); }
                catch (Exception error) { InventoryOrganizer.AutomaticFailure("Group drop: " + error); ShopNotice.Show(ShopSettings.Text("f05.failed")); }
            }
        }
    }
}
