using System;
using System.Collections.Generic;

namespace ShopAssist
{
    internal static class GroupGeometry
    {
        internal static (int x, int y) RotateGrab(GridShape shape, int x, int y, bool ccw) =>
            ccw ? (y, shape.globalWidth - 1 - x) : (shape.globalHeight - 1 - y, x);

        // The first bounding box keeps its original origin; the native cursor delta then
        // translates every member by the same amount.
        internal static List<GridShape> Rotate(IReadOnlyList<GridShape> shapes, int anchorX, int anchorY, bool ccw)
        {
            if (shapes == null || shapes.Count == 0) throw new ArgumentException("A formation needs at least one shape.", nameof(shapes));
            var result = new List<GridShape>(shapes.Count);
            foreach (GridShape shape in shapes)
            {
                if (shape == null) throw new ArgumentException("A formation shape cannot be null.", nameof(shapes));
                int x = ccw ? shape.minY : -shape.minY - shape.globalHeight + 1;
                int y = ccw ? -shape.minX - shape.globalWidth + 1 : shape.minX;
                result.Add(new GridShapeBuilder(shape).SetPosition(x, y).SetOrientation((shape.orientation + (ccw ? 1 : 3)) % 4).Build());
            }
            int shiftX = anchorX - result[0].minX, shiftY = anchorY - result[0].minY;
            for (int i = 0; i < result.Count; i++)
            {
                GridShape shape = result[i];
                result[i] = new GridShapeBuilder(shape).SetPosition(shape.minX + shiftX, shape.minY + shiftY).Build();
            }
            return result;
        }
    }
}
