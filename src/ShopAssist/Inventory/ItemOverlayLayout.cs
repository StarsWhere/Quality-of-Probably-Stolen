using UnityEngine;

namespace ShopAssist
{
    internal static class ItemOverlayLayout
    {
        internal static Vector2 Size(GameItemElement item)
        {
            if (item == null) return Vector2.zero;
            GridShape shape = item.modifiedShape;
            if (shape != null && shape.globalWidth > 0 && shape.globalHeight > 0 && UISettings.current != null)
            {
                float cell = UISettings.current.gridSize;
                if (cell > 0 && !float.IsNaN(cell) && !float.IsInfinity(cell))
                    return new Vector2(shape.globalWidth * cell, shape.globalHeight * cell);
            }
            return new Vector2(item.widthPixels, item.heightPixels);
        }
    }
}
