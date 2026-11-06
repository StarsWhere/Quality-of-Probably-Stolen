using System;

namespace ShopAssist
{
    // The engine's bought-price tag is written by OnItemBought. The additional
    // checks prevent routing during settlement, departure or special handling.
    internal static class SettledCounterEvidence
    {
        internal static bool Allows(GameItem item)
        {
            if (!ShopInventory.InStore || item == null || item.GetType() != typeof(GameItemElement) ||
                EmporiumEntry.Instance?.backInvinvElement == null ||
                !ReferenceEquals(item.parentInventory, EmporiumEntry.Instance.backInvinvElement) ||
                !EmporiumEntry.Instance.backInvinvElement.childItems.Contains(item) || !ShopInventory.Owned(item) ||
                item.unitCount != 1 || item.contentWindow != null || item.examineWindow != null || item.isDebugMenu ||
                string.IsNullOrEmpty(item.identifier) || item.itemFeatures == null || item.itemFeatures.Count != 0 ||
                !item.IsTag("BOUGHT_PRICE_TAG") || item.GetTagReadonly("BOUGHT_PRICE_TAG").GetInt() <= 0 ||
                item.IsTag("IMPORTANT_TAG") || item.IsTag("TRACK_OPENED_TAG") || item.IsTag("LOCKED_TAG") ||
                item.IsTag(StolenHelper.STOLEN_TAG) || item.IsTag(ContrabandHelper.CONTRABAND_ITEM_TAG) ||
                item.IsTag(ScratcherHelper.SCRATCHER_TAG) || item.IsTag(LotteryHelper.LOTTERY_TICKET_TAG) ||
                item.IsGameItemType("MACHINE") || item.IsGameItemType("MODULE") || item.IsGameItemType("NODE") ||
                item.IsGameItemType("SEED") || item.IsGameItemType("MODIFIED_SEED") || item.IsGameItemType("DOCUMENT") ||
                item.onSlotRemoveItemFunc != null) return false;

            PlayerStore player = PlayerStore.Instance;
            StoreUIManager ui = StoreUIManager.Instance;
            if (player.currentClientInstance != null || player.isClientBeingArrested || player.isHandlingShootout ||
                player.lockNextBellFlag != 0 || player.callNextWhenPossible || ui == null || ui.isShot ||
                ui.clientGameObject == null || ui.clientGameObject.gameObject.activeInHierarchy ||
                NegociationUIManager.Instance == null || NegociationUIManager.Instance.IsOpen ||
                (BargainUIManager.Instance != null && BargainUIManager.Instance.IsOpen) ||
                DialogUIManager.Instance == null || DialogUIManager.Instance.GetCurrentDialog() != null) return false;

            GameGridInventory source = EmporiumEntry.Instance.backInvinvElement;
            if (source.onSlotRemoveItemFunc != null || source.onSlotRemoveItemLateFunc != null || source.onSlotRemoveItemEndFunc == null)
                return false;
            Delegate[] handlers = source.onSlotRemoveItemEndFunc.GetInvocationList();
            if (handlers.Length != 1) return false;
            var method = handlers[0].Method;
            return method.DeclaringType?.Assembly == typeof(EmporiumEntry).Assembly &&
                method.DeclaringType.FullName == "EmporiumEntry+<>c" && method.Name == "<Start>b__184_18";
        }
    }
}
