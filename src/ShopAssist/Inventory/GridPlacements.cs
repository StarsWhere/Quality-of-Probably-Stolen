using System.Collections.Generic;

namespace ShopAssist
{
    internal static class GridPlacements
    {
        internal static SlotMarker Exact(GameGridInventory grid, GameItem item, GridShape shape, int amount = -1)
            => Exact(grid, item, shape, amount, false);

        private static SlotMarker Exact(GameGridInventory grid, GameItem item, GridShape shape, int amount, bool allowPartial)
        {
            if (grid == null || item == null || shape == null || grid.IsInsertLocked() ||
                !GeneralHelper.MayPlayerInsertInto(grid)) return null;
            int count = amount < 0 ? item.unitCount : amount;
            if (count <= 0 || count > item.unitCount) return null;
            var slot = grid.TryInventorySlot(item, count, shape);
            return slot != null && slot.inventory == grid && slot.targetItem == null && slot.itemGridShape != null &&
                (allowPartial ? slot.numTransfer > 0 : slot.numTransfer >= count) ? slot : null;
        }

        internal static SlotMarker Empty(GameGridInventory grid, GameItem item, IReadOnlyList<GridShape> reserved = null, int amount = -1)
            => Empty(grid, item, reserved, amount, false);

        internal static SlotMarker EmptyUpTo(GameGridInventory grid, GameItem item, IReadOnlyList<GridShape> reserved, int amount)
            => Empty(grid, item, reserved, amount, true);

        private static SlotMarker Empty(GameGridInventory grid, GameItem item, IReadOnlyList<GridShape> reserved, int amount, bool allowPartial)
        {
            if (grid == null || grid.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(grid) ||
                !grid.MayHaveValidInventorySlot(item)) return null;
            var pose = new GridShapeBuilder(item.modifiedShape);
            for (int face = 0; face < 2; face++)
            for (int turn = 0; turn < 4; turn++)
            {
                pose.SetOrientation((item.modifiedShape.orientation + turn) % 4);
                pose.SetFlipped(face == 0 ? item.modifiedShape.flipped : !item.modifiedShape.flipped);
                // Vanilla tests every origin. Empty padding on an irregular footprint may overhang an edge.
                for (int y = 0; y < grid.inventoryShape.height; y++)
                for (int x = 0; x < grid.inventoryShape.width; x++)
                {
                    pose.SetPosition(x, y);
                    if (Overlaps(pose, reserved)) continue;
                    var slot = Exact(grid, item, pose, amount, allowPartial);
                    if (slot != null) return slot;
                }
            }
            return null;
        }

        internal static bool Overlaps(GridShape shape, IReadOnlyList<GridShape> reserved)
        {
            if (reserved == null) return false;
            for (int i = 0; i < reserved.Count; i++)
                if (shape.Intersects(reserved[i])) return true;
            return false;
        }
    }
}
