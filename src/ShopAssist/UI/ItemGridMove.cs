using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShopAssist
{
    // The caller must authorize both locations and the item. This class only moves an intact
    // existing item to an exact empty pose and verifies or restores the original snapshot.
    internal static class ItemGridMove
    {
        internal static bool CanMove(GameItem item, GameGridInventory destination, GridShape pose)
        {
            if (item == null || !(item.parentInventory is GameGridInventory source) || destination == null || pose == null ||
                item.unitCount < 1 || item.modifiedShape == null ||
                ShopInventory.WouldContainSelf(item, destination) ||
                source.IsRemoveLocked() || !item.MayRemove() || item.MaxNumRemove() < item.unitCount ||
                destination.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(destination)) return false;
            SlotMarker slot = GridPlacements.Exact(destination, item, pose);
            return slot != null && SameShape(slot.itemGridShape, pose);
        }

        internal static bool TryMove(GameItem item, GameGridInventory destination, GridShape pose, out bool restored)
        {
            restored = true;
            if (item == null || !(item.parentInventory is GameGridInventory source) || destination == null || pose == null || item.modifiedShape == null) return false;
            GridShape original = item.modifiedShape.Clone();
            int count = item.unitCount;
            try
            {
                SlotMarker slot = CanMove(item, destination, pose) && AtSource(item, source, original, count) ?
                    GridPlacements.Exact(destination, item, pose) : null;
                if (slot != null && SameShape(slot.itemGridShape, pose) && AtSource(item, source, original, count) && slot.TryAcceptOnce(count) == count &&
                    item.parentInventory == destination && item.unitCount == count && destination.childItems.Contains(item) &&
                    (ReferenceEquals(source, destination) || !source.childItems.Contains(item)) && SameShape(item.modifiedShape, pose)) return true;
            }
            catch (Exception error) { Debug.LogWarning("[ShopAssist] Grid move interrupted: " + error); }
            try
            {
                if (item.parentInventory != null && item.parentInventory != source && item.parentInventory != destination)
                { restored = false; return false; }
                if (item.unitCount == count && item.parentInventory != source)
                {
                    SlotMarker back = GridPlacements.Exact(source, item, original);
                    if (back != null && SameShape(back.itemGridShape, original)) back.TryAcceptOnce(count);
                }
                if (item.parentInventory == source && !SameShape(item.modifiedShape, original) &&
                    source.CheckShapePlacement(item, original, new List<GameItem> { item }))
                {
                    item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(original);
                    item.ValidateShapeState(); source.Validate();
                }
            }
            catch (Exception error) { Debug.LogWarning("[ShopAssist] Grid move recovery interrupted: " + error); }
            restored = item.parentInventory == source && item.unitCount == count && source.childItems.Contains(item) &&
                (ReferenceEquals(source, destination) || !destination.childItems.Contains(item)) && SameShape(item.modifiedShape, original);
            return false;
        }

        private static bool AtSource(GameItem item, GameGridInventory source, GridShape original, int count) =>
            item.parentInventory == source && source.childItems.Contains(item) && item.unitCount == count && SameShape(item.modifiedShape, original);

        internal static bool SameShape(GridShape a, GridShape b)
        {
            if (a == null || b == null || a.width != b.width || a.height != b.height || a.minX != b.minX || a.minY != b.minY ||
                a.orientation != b.orientation || a.flipped != b.flipped || a.GetOutsideBounds() != b.GetOutsideBounds()) return false;
            for (int y = 0; y < a.height; y++) for (int x = 0; x < a.width; x++) if (a.GetLocal(x, y) != b.GetLocal(x, y)) return false;
            return true;
        }
    }
}
