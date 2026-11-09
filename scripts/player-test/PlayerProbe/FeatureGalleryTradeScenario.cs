using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Real purchase/sale, budget selection and receipt destination in one isolated run.
public sealed class FeatureGalleryTradeScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private int phase, cash, readyFrame;
    private GameItemElement bag, purchased, first, second;

    public bool Tick(out string result)
    {
        result = null;
        QaFeatureGallery.Need(Time.realtimeSinceStartup < deadline, "Trade gallery timed out at " + phase);
        if (phase == 0) { if (!gallery.ChineseMainMenu()) return false; phase = 1; }
        if (phase == 1) { if (!start.Tick()) return false; phase = 2; }
        if (phase == 2)
        {
            if (!gallery.Screen1920() || NegociationUIManager.Instance == null || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(true, "f01.enabled", "f28.enabled", "f34.enabled", "f24.enabled");
            TradeScenario.SetOptions(false, "f15.enabled", "f16.enabled");
            var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
            bag = QaFeatureGallery.Item("backpack_medium", e.invElement, "回单存放箱");
            purchased = DirectoryMaster.Item("processed_meat") as GameItemElement;
            QaFeatureGallery.Need(purchased != null, "Missing native purchase item");
            GeneralHelper.SetItemOwned(purchased, false); QaFeatureGallery.Put(purchased, e.backInvinvElement);
            p.playerCash = 200000;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly()); p.isClientArrived = true;
            e.backInvWindow.Show(new Vector2(Screen.width * .24f, Screen.height * .72f), false);
            NegociationUIManager.Instance.OpenUI();
            readyFrame = Time.frameCount + 5; phase = 20; return false;
        }
        if (phase == 20)
        {
            if (Time.frameCount < readyFrame) return false;
            gallery.Capture("f01-before"); phase = 3; return false;
        }
        if (phase == 3)
        {
            if (!gallery.CaptureReady()) return false;
            cash = PlayerStore.Instance.playerCash;
            PlayerStore.Instance.BuyItem(purchased);
            QaFeatureGallery.Need(purchased.parentInventory == EmporiumEntry.Instance.backInvinvElement &&
                GeneralHelper.IsItemOwned(purchased) && PlayerStore.Instance.playerCash < cash,
                "Native purchase did not pay and retain the purchased item on the counter");
            NegociationUIManager.Instance.RefreshUI(); readyFrame = Time.frameCount + 5; phase = 30; return false;
        }
        if (phase == 30)
        {
            if (Time.frameCount < readyFrame) return false;
            gallery.Capture("f01"); phase = 4; return false;
        }
        if (phase == 4)
        {
            if (!gallery.CaptureReady()) return false;
            NegociationUIManager.Instance.CloseUI();
            TradeScenario.SetOptions(true, "f16.enabled", "f16.money", "f16.rep");
            var p = PlayerStore.Instance; var e = EmporiumEntry.Instance;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateSpacerClientFood()); p.isClientArrived = true;
            first = QaFeatureGallery.Item("processed_meat", e.invElement);
            second = QaFeatureGallery.Item("processed_juice", e.invElement);
            cash = p.playerCash; p.SellItem(purchased);
            QaFeatureGallery.Need(p.playerCash > cash, "Native sale did not produce a ledger entry");
            phase = 40; return false;
        }
        if (phase == 40)
        {
            if (!UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>().Any(text => text.gameObject.activeInHierarchy &&
                text.name.StartsWith(Mod + ".receipt.") && text.color.a >= .9f)) return false;
            gallery.Capture("f16"); phase = 41; return false;
        }
        if (phase == 41)
        {
            if (!gallery.CaptureReady()) return false;
            QaFeatureGallery.Action("f28.open");
            QaFeatureGallery.Need(QaFeatureGallery.Window("ledger")?.IsAlive == true, "Ledger did not open");
            gallery.Capture("f28"); phase = 5; return false;
        }
        if (phase == 5)
        {
            if (!gallery.CaptureReady()) return false;
            QaFeatureGallery.Window("ledger").Close();
            var p = PlayerStore.Instance;
            var buyer = p.currentClientInstance.GetClientBlueprint(); buyer.SetBudget(9000); buyer.isIntroduced = true;
            NegociationUIManager.Instance.OpenUI(); phase = 6; return false;
        }
        if (phase == 6)
        {
            if (!NegociationUIManager.Instance.IsOpen) return false;
            QaFeatureGallery.Action("f34.open");
            var basket = QaFeatureGallery.Window("basket");
            QaFeatureGallery.Need(basket?.Get("limit") != null && QaItemUi.Hit(basket.root, first) != null &&
                QaItemUi.Hit(basket.root, second) != null, "Budget selection did not show the native goods");
            basket.Get("limit").SetString("9000", true); gallery.Capture("f34-selection"); phase = 7; return false;
        }
        if (phase == 7)
        {
            if (!gallery.CaptureReady()) return false;
            QaFeatureGallery.Window("basket").Get("compare").button.onClick.Invoke();
            QaFeatureGallery.Need(QaFeatureGallery.Window("basket")?.IsAlive == true, "Budget comparison closed unexpectedly");
            gallery.Capture("f34"); phase = 8; return false;
        }
        if (phase == 8)
        {
            if (!gallery.CaptureReady()) return false;
            QaFeatureGallery.Window("basket").Close(); NegociationUIManager.Instance.CloseUI();
            var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
            var settings = CustomUIManager.Instance.GetWindow("game_options");
            settings.Get(Mod + ":f24.destination").SetIndex(1, true); settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
            QaFeatureGallery.Need(((ConfigEntry<int>)ModConfig.Find(Mod).Get("f24.destination")).Value == 1,
                "Receipt destination setting did not apply");
            gallery.RestoreSavedLocale(); result = "PASS gallery F01 purchase, F28 ledger, F34 selection and F24 destination setting"; return true;
        }
        return false;
    }
}
