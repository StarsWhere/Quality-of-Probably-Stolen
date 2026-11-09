using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// Captures an actual customer arrival and dialogue in a fresh isolated game.
public sealed class GalleryLiveDemandScenario
{
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private GameItemElement food, bag;
    private StoreClient buyer;
    private int step;

    public bool Tick(out string result)
    {
        result = null;
        QaFeatureGallery.Need(Time.realtimeSinceStartup < deadline, "Live customer gallery timed out at " + step);
        if (step == 0) { if (!gallery.ChineseMainMenu()) return false; step = 1; }
        if (step == 1) { if (!start.Tick()) return false; step = 2; }
        if (step == 2)
        {
            if (!gallery.Screen1920() || StoreShutterButton.Instance == null || ItemMouseDoubleClickHandler.current == null) return false;
            var entry = EmporiumEntry.Instance;
            food = QaFeatureGallery.Item("processed_meat", entry.invElement);
            bag = QaFeatureGallery.Item("fanny_pack", entry.invElement, "食品背包");
            QaFeatureGallery.Need(bag.contentWindow?.childElement is GameGridInventory, "Native bag inventory unavailable");
            QaFeatureGallery.Item("processed_milk", (GameGridInventory)bag.contentWindow.childElement);
            TradeScenario.SetOptions(true, "f03.enabled");
            TradeScenario.SetOptions(false, "f34.enabled", "f14.enabled", "f04.enabled");
            EmporiumOption.Instance.autoCallNext = false;
            PlayerStore.Instance.callNextWhenPossible = false;
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            if (!PlayerStore.Instance.CanStartDay()) return false;
            StoreShutterButton.Instance.OnClick();
            step = 3; return false;
        }
        if (step == 3)
        {
            if (PlayerStore.Instance.storeState != PlayerStore.StoreState.OPEN || PlayerStore.Instance.lockShutterButtonFlag > 0) return false;
            QaFeatureGallery.Need(PlayerStore.Instance.currentClientInstance == null, "Unexpected customer before gallery arrival");
            buyer = StoreClientList.CreateSpacerClientFood();
            PlayerStore.Instance.storeClientManager.AddNextClient(buyer);
            PlayerStore.Instance.GetNextClient();
            QaFeatureGallery.Need(ReferenceEquals(PlayerStore.Instance.currentClientInstance?.GetClientBlueprint(), buyer), "Native customer was not selected");
            StoreUIManager.Instance.DisplayClient();
            StoreUIManager.Instance.QueueClientArriveAnimation();
            step = 4; return false;
        }
        if (step == 4)
        {
            if (!PlayerStore.Instance.isClientArrived || StoreUIManager.Instance.clientGameObject == null ||
                !StoreUIManager.Instance.clientGameObject.gameObject.activeInHierarchy || DialogUIManager.Instance?.currentDialog == null) return false;
            var dialog = DialogUIManager.Instance;
            if (dialog.IsLineStillDisplaying(dialog.currentDialog)) return false;
            var bubble = dialog.clientBubble?.textfield != null && dialog.clientBubble.gameObject.activeInHierarchy
                ? dialog.clientBubble.textfield : dialog.playerBubble?.textfield != null && dialog.playerBubble.gameObject.activeInHierarchy
                ? dialog.playerBubble.textfield : null;
            if (bubble == null || string.IsNullOrWhiteSpace(bubble.text) || bubble.text.Length < 3) return false;
            bubble.ForceMeshUpdate();
            if (bubble.maxVisibleCharacters < bubble.textInfo.characterCount ||
                bubble.textInfo.characterInfo.Take(bubble.textInfo.characterCount).Count(character => character.isVisible) < 3) return false;
            var outline = food.rotator?.transform.Find("ShopAssist_Outline");
            if (outline == null || !outline.gameObject.activeInHierarchy ||
                !outline.GetComponentsInChildren<Image>(true).Any(image => image.isActiveAndEnabled && image.color.a > 0)) return false;
            gallery.Capture("f03-live"); step = 5; return false;
        }
        if (!gallery.CaptureReady()) return false;
        gallery.RestoreSavedLocale(); result = "PASS native customer arrival, dialogue and demand highlight"; return true;
    }
}
