using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ShopAssist
{
    internal static class ShopSelection
    {
        internal sealed class Context
        {
            internal readonly Dictionary<GameItemElement, GameInventory> Homes = new Dictionary<GameItemElement, GameInventory>();
        }

        private static readonly FieldInfo Items = typeof(ItemMultiSelectHandler).GetField("selectedItems", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo State = typeof(ItemMultiSelectHandler).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
        private static Context context;

        internal static void CaptureContext()
        {
            context = null;
            var captured = new Context();
            foreach (GameItemElement item in Current().OfType<GameItemElement>()) captured.Homes[item] = item.parentInventory;
            context = captured;
        }

        internal static Context SaveContext() => context;
        internal static void RestoreContext(Context saved) => context = saved;
        internal static void ClearContext() => context = null;
        internal static bool ContextChanged(GameInventory grid) => context != null && context.Homes.Any(pair =>
            ReferenceEquals(pair.Value, grid) && (pair.Key == null || pair.Key.IsDestroyed() || !ReferenceEquals(pair.Key.parentInventory, grid)));

        internal static List<GameItem> Current()
        {
            if (context != null)
                return context.Homes.Where(pair => pair.Key != null && !pair.Key.IsDestroyed() &&
                    ReferenceEquals(pair.Key.parentInventory, pair.Value)).Select(pair => (GameItem)pair.Key).ToList();
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
