using System.Collections.Generic;
using System.Reflection;

namespace ShopAssist
{
    internal static class ShopSelection
    {
        private static readonly FieldInfo Items = typeof(ItemMultiSelectHandler).GetField("selectedItems", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo State = typeof(ItemMultiSelectHandler).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static List<GameItem> Current()
        {
            var result = new List<GameItem>();
            var handler = ItemMultiSelectHandler.current;
            if (handler == null || !handler.HasSelection || handler.IsGroupDragging ||
                State?.GetValue(handler)?.ToString() != "Selected" || Items?.FieldType != typeof(List<GameItemElement>) ||
                !(Items.GetValue(handler) is List<GameItemElement> selected)) return result;
            foreach (var item in selected)
                if (item != null && !item.IsDestroyed() && item.parentInventory != null) result.Add(item);
            return result;
        }
    }
}
