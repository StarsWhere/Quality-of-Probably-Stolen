using System;

namespace ShopAssist
{
    internal static class CustomerIndicators
    {
        internal static event Action<GameItemElement, string> PublicTooltipShown;
        internal static bool HasOrdinaryTooltip(GameItemElement item) =>
            item != null && item.HasTooltip() && item.tooltipRichTextFunc == null;
        internal static void ShowTooltip(GameItemElement item, string text) => PublicTooltipShown?.Invoke(item, text);
    }
}
