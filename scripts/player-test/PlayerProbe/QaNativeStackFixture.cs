using System;

// A ten-unit native fixture tests partial transfer even when the destination
// accepts only one unit per slot. No item factory or stack policy is replaced.
internal static class QaNativeStackFixture
{
    internal static void PutWhole(GameGridInventory destination, GameItem item)
    {
        var slot = destination.TryFindOneValidInventorySlot(item);
        if (slot == null || slot.targetItem != null || slot.numTransfer < 1 || slot.itemGridShape == null)
            throw new InvalidOperationException("No native empty cell for test stack: count=" + item.unitCount + " capacity=" + slot?.numTransfer);
        item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(slot.itemGridShape);
        if (!destination.UncheckedAccept(item) || item.parentInventory != destination || item.unitCount != 10)
            throw new InvalidOperationException("Native unchecked placement did not retain ten units");
    }
}

