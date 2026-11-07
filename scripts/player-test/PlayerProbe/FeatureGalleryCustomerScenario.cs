using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// Reuses native buyer and inspection fixtures; screenshots show the actual overlays.
public sealed class FeatureGalleryCustomerScenario
{
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private readonly CustomerScenario fixture = new CustomerScenario();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private int phase;
    private GameItemElement bag, food, stolen;

    private static bool Outlined(GameItemElement item)
    {
        var node = item?.rotator?.transform.Find("ShopAssist_Outline");
        return QaFeatureGallery.Visible(node) && node.GetComponentsInChildren<Image>(true)
            .Any(image => image.isActiveAndEnabled && image.color.a > 0 && image.rectTransform.rect.width > 0);
    }
    public bool Tick(out string result)
    {
        result = null;
        QaFeatureGallery.Need(Time.realtimeSinceStartup < deadline, "Customer gallery timed out at " + phase);
        if (phase == 0) { if (!gallery.ChineseMainMenu()) return false; phase = 1; }
        if (phase == 1)
        {
            int step = (int)QaFeatureGallery.Field(fixture, "step");
            if (step == 1 && !gallery.Screen1920()) return false;
            if (step < 2) { fixture.Tick(out _); return false; }
            bag = (GameItemElement)QaFeatureGallery.Field(fixture, "bag");
            food = (GameItemElement)QaFeatureGallery.Field(fixture, "ordinary");
            stolen = (GameItemElement)QaFeatureGallery.Field(fixture, "stolen");
            GeneralHelper.SetCustomName(bag, "日常备货背包");
            GeneralHelper.SetCustomName(food, "加工肉");
            GeneralHelper.SetCustomName((GameItem)QaFeatureGallery.Field(fixture, "hiddenRejection"), "果汁");
            GeneralHelper.SetCustomName((GameItem)QaFeatureGallery.Field(fixture, "customTooltip"), "牛奶");
            GeneralHelper.SetCustomName((GameItem)QaFeatureGallery.Field(fixture, "nested"), "方便面");
            GeneralHelper.SetCustomName(stolen, "旧酵母");
            TradeScenario.SetOptions(true, "f02.enabled");
            TradeScenario.SetOptions(false, "f03.enabled", "f04.enabled", "f15.enabled");
            NegociationUIManager.Instance.CloseUI();
            phase = 2; return false;
        }
        if (phase == 2)
        {
            var area = bag?.middle?.transform.Find("ShopAssist_AreaName");
            if (!QaFeatureGallery.Visible(area)) return false;
            gallery.Capture("f02"); phase = 3; return false;
        }
        if (phase == 3)
        {
            if (!gallery.CaptureReady()) return false;
            TradeScenario.SetOptions(true, "f03.enabled");
            NegociationUIManager.Instance.OpenUI(); phase = 30; return false;
        }
        if (phase == 30)
        {
            if (!Outlined(food)) return false;
            gallery.Capture("f03"); phase = 4; return false;
        }
        if (phase == 4)
        {
            if (!gallery.CaptureReady()) return false;
            TradeScenario.SetOptions(false, "f03.enabled");
            TradeScenario.SetOptions(true, "f04.enabled", "f04.stolen");
            NegociationUIManager.Instance.CloseUI();
            PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateInspectionClient());
            PlayerStore.Instance.isClientArrived = true;
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width * .7f, Screen.height * .65f));
            phase = 5; return false;
        }
        if (phase == 5)
        {
            if (!Outlined(stolen)) return false;
            gallery.Capture("f04"); phase = 6; return false;
        }
        if (!gallery.CaptureReady()) return false;
        gallery.RestoreSavedLocale(); result = "PASS gallery F02 native area name, F03 buyer highlight, F04 inspection risk"; return true;
    }
}
