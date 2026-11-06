using UnityEngine;

namespace ShopAssist
{
    internal static class ShopInput
    {
        internal static bool Ready()
        {
            var ui = StoreUIManager.Instance;
            return PlayerStore.HasInstance && ui != null && !StoreUIManager.IsAnyTyping() &&
                !KeyBindHelper.IsCapturing && (ui.gameOptionUIManager == null || !ui.gameOptionUIManager.IsOpen) &&
                (ui.escapeUIManager == null || !ui.escapeUIManager.menuPanel.activeInHierarchy) &&
                (GenericConfirmationUIManager.Instance == null || !GenericConfirmationUIManager.Instance.panel.activeInHierarchy) &&
                (ui.confirmPanelUIManager == null || !ui.confirmPanelUIManager.panel.activeInHierarchy) &&
                (ui.blackscreen == null || !ui.blackscreen.gameObject.activeInHierarchy) &&
                !Input.GetMouseButton(0);
        }
    }
}
