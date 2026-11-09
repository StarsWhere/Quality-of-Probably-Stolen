using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ShopAssist
{
    internal static class MaintenanceSwap
    {
        private static readonly MethodBase[] SwapMethods = {
            AccessTools.Method(typeof(GraphUtils), nameof(GraphUtils.SwapItemsInPlace)),
            AccessTools.Method(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce)),
            AccessTools.Method(typeof(GameSlotInventory), nameof(GameSlotInventory.UncheckedAccept)),
            AccessTools.Method(typeof(GameSlotInventory), nameof(GameSlotInventory.Expel)),
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.UncheckedAccept)),
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.Expel))
        };
        // The native swap uses a shared buffer and can fail after moving an item.
        internal static bool Ready(GameItem host, GameSlotInventory slot, GameGridInventory source, GameItem oldItem, GameItem spare,
            bool allowOtherFilter = false, bool allowOtherBattery = false)
        {
            bool alternate = oldItem != null && spare != null && oldItem.identifier != spare.identifier;
            if (!ShopInventory.InStore || !ShopInventory.Owned(host) || slot == null || source == null || !InventoryRoutes.AutoTakeAllowed(source) ||
                oldItem == null || spare == null || oldItem == spare ||
                (alternate && !(allowOtherFilter && oldItem.IsTag("WATER_FILTER") && spare.IsTag("WATER_FILTER") ||
                    allowOtherBattery && oldItem.IsTag("power_source_item") && spare.IsTag("power_source_item"))) ||
                oldItem.unitCount != 1 || spare.unitCount != 1 || slot.childItem != oldItem || oldItem.parentInventory != slot ||
                spare.parentInventory != source || !ShopInventory.Owned(oldItem) || !oldItem.MayRemove() || !ShopInventory.Movable(spare) ||
                ItemPinHelper.IsPinned(oldItem) ||
                oldItem.MaxNumRemove() < 1 || spare.MaxNumRemove() < 1 ||
                slot.IsInsertLocked() || slot.IsRemoveLocked() || source.IsInsertLocked() || source.IsRemoveLocked() ||
                !ReferenceEquals(slot.GetParentItem(), host) || oldItem.modifiedShape == null || spare.modifiedShape == null ||
                EmporiumEntry.Instance?.swapBufferElement == null || EmporiumEntry.Instance.swapBufferElement.childItem != null) return false;

            GameSlotInventory buffer = EmporiumEntry.Instance.swapBufferElement;
            if (!NativeMethods() || !NativeCallbacks(slot) || !NativeCallbacks(source) || !NativeCallbacks(buffer) ||
                !NativeCallback(oldItem.onSlotAddItemFunc) || !NativeCallback(oldItem.onSlotRemoveItemFunc) ||
                !NativeCallback(spare.onSlotAddItemFunc) || !NativeCallback(spare.onSlotRemoveItemFunc)) return false;
            SlotMarker bufferSlot = buffer.TryFindOneValidInventorySlot(spare);
            if (bufferSlot == null || bufferSlot.targetItem != null || bufferSlot.inventory != buffer || bufferSlot.numTransfer < 1 ||
                !slot.MayHaveValidInventorySlot(spare) || !source.MayHaveValidInventorySlot(oldItem)) return false;
            GridShape oldShape = alternate ? GridPlacements.Empty(source, oldItem)?.itemGridShape :
                new GridShapeBuilder(oldItem.modifiedShape).SetTransform(spare.modifiedShape);
            var newShape = new GridShapeBuilder(spare.modifiedShape).SetTransform(oldItem.modifiedShape);
            if (oldShape == null || !source.CheckShapePlacement(oldItem, oldShape, new List<GameItem> { spare })) return false;
            if (!alternate && (!ModuleLayout.SameShape(oldShape, spare.modifiedShape) || !ModuleLayout.SameShape(newShape, oldItem.modifiedShape))) return false;
            var back = new SlotMarker { item = oldItem, inventory = source, itemGridShape = oldShape, numTransfer = 1 };
            var into = new SlotMarker { item = spare, inventory = slot, itemGridShape = newShape, numTransfer = 1 };
            return oldItem.CheckIsStateValid(source, back, oldShape, oldItem.modifiedState) &&
                source.CheckAcceptParentShapeStateModifier(oldItem, back, oldShape, oldItem.modifiedState) && oldItem.MaxNumAdd(back) >= 1 &&
                spare.CheckIsStateValid(slot, into, newShape, spare.modifiedState) &&
                slot.CheckAcceptParentShapeStateModifier(spare, into, newShape, spare.modifiedState) && spare.MaxNumAdd(into) >= 1;
        }

        internal static bool Try(GameItem host, GameSlotInventory slot, GameGridInventory source, GameItem oldItem, GameItem spare,
            bool allowOtherFilter = false, bool allowOtherBattery = false)
        {
            if (!Ready(host, slot, source, oldItem, spare, allowOtherFilter, allowOtherBattery)) return false;
            if (oldItem.identifier == spare.identifier) GraphUtils.SwapItemsInPlace(spare, oldItem);
            else
            {
                // Vanilla SwapItemsInPlace assigns the other item's footprint. Different materials
                // instead need an actual empty return cell and each item's own native slot marker.
                var oldHome = oldItem.modifiedShape.Clone(); var spareHome = spare.modifiedShape.Clone();
                try
                {
                    var back = GridPlacements.Empty(source, oldItem);
                    if (back == null || back.TryAcceptOnce(1) != 1 || oldItem.parentInventory != source) return false;
                    var into = slot.TryFindOneValidInventorySlot(spare);
                    if (into == null || into.inventory != slot || into.targetItem != null || into.TryAcceptOnce(1) != 1) return false;
                }
                finally
                {
                    if (slot.childItem != spare || spare.parentInventory != slot || oldItem.parentInventory != source)
                    {
                        if (spare.parentInventory != source)
                            GridPlacements.Exact(source, spare, spareHome)?.TryAcceptOnce(1);
                        if (oldItem.parentInventory != slot && slot.childItem == null)
                            slot.TryFindOneValidInventorySlot(oldItem)?.TryAcceptOnce(1);
                        RestorePose(spare, source, spareHome); RestorePose(oldItem, slot, oldHome);
                    }
                }
            }
            return slot.childItem == spare && spare.parentInventory == slot && oldItem.parentInventory == source &&
                EmporiumEntry.Instance.swapBufferElement.childItem == null && oldItem.unitCount == 1 && spare.unitCount == 1;
        }

        private static void RestorePose(GameItem item, GameInventory parent, GridShape shape)
        {
            if (item.parentInventory != parent || ModuleLayout.SameShape(item.modifiedShape, shape)) return;
            if (parent is GameGridInventory grid && !grid.CheckShapePlacement(item, shape, new List<GameItem> { item })) return;
            item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shape);
            item.ValidateShapeState();
        }

        private static bool NativeCallbacks(GameInventory inventory) =>
            NativeCallback(inventory.onSlotAddItemFunc) && NativeCallback(inventory.onSlotAddItemEndFunc) &&
            NativeCallback(inventory.onSlotAddItemLateFunc) && NativeCallback(inventory.onSlotRemoveItemFunc) &&
            NativeCallback(inventory.onSlotRemoveItemEndFunc) && NativeCallback(inventory.onSlotRemoveItemLateFunc);

        private static bool NativeCallback(Delegate callback) => callback == null || callback.GetInvocationList().All(d =>
            d.Method.DeclaringType?.Assembly == typeof(GameItem).Assembly && NoForeignPatches(d.Method));

        internal static bool NoForeignPatches(MethodBase method)
        {
            if (method == null) return false;
            var patches = Harmony.GetPatchInfo(method);
            string owner = "mod." + ModEntry.Id;
            return patches == null || patches.Owners.All(id => id == owner || id.StartsWith(owner + ".", StringComparison.Ordinal));
        }

        internal static ActionAvailability CallbackCapability(GameSlotInventory slot, GameGridInventory source, GameItem oldItem, GameItem spare)
        {
            GameSlotInventory buffer = EmporiumEntry.Instance?.swapBufferElement;
            if (slot == null || source == null || oldItem == null || spare == null || buffer == null)
                return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "maintenance.host_unavailable");
            if (!NativeCallbacks(slot) || !NativeCallbacks(source) || !NativeCallbacks(buffer) ||
                !NativeCallback(oldItem.onSlotAddItemFunc) || !NativeCallback(oldItem.onSlotRemoveItemFunc) ||
                !NativeCallback(spare.onSlotAddItemFunc) || !NativeCallback(spare.onSlotRemoveItemFunc))
                return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "maintenance.conflict");
            return ActionAvailability.Ready;
        }

        private static bool NativeMethods() => MethodCapability().Allowed;

        internal static bool NativeTransferAllowed(GameInventory source, GameInventory destination, GameItem item) =>
            source != null && destination != null && item != null && InventoryRoutes.AutoTakeAllowed(source) &&
            NativeMethods() && NativeCallbacks(source) && NativeCallbacks(destination) &&
            NativeCallback(item.onSlotAddItemFunc) && NativeCallback(item.onSlotRemoveItemFunc);

        internal static ActionAvailability MethodCapability()
        {
            if (SwapMethods.Any(method => method == null))
                return ActionAvailability.Blocked(ActionUnavailableReason.Unsupported, "maintenance.unsupported");
            return SwapMethods.All(NoForeignPatches) ? ActionAvailability.Ready :
                ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "maintenance.conflict");
        }
    }
}
