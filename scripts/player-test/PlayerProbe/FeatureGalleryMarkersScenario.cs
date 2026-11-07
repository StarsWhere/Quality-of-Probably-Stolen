using System;
using UnityEngine;

// Keeps the game's own exposed-condition and known-stolen overlays separate.
public sealed class FeatureGalleryMarkersScenario
{
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private int phase;
    private GameItemElement water, bag, stolen;

    public bool Tick(out string result)
    {
        result = null;
        QaFeatureGallery.Need(Time.realtimeSinceStartup < deadline, "Marker gallery timed out at " + phase);
        if (phase == 0) { if (!gallery.ChineseMainMenu()) return false; phase = 1; }
        if (phase == 1)
        {
            if (!start.Tick()) return false;
            phase = 10; return false;
        }
        if (phase == 10)
        {
            if (!gallery.Screen1920() || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(false, "f02.enabled", "f03.enabled", "f04.enabled", "f15.enabled", "f21.enabled", "f39.enabled");
            TradeScenario.SetOptions(true, "f38.enabled");
            var stock = EmporiumEntry.Instance.invElement;
            water = QaFeatureGallery.Item("bottled_water", stock);
            WaterHelper.AddWater(water, 2, WaterHelper.GetTotalCapacity(water));
            WaterFeatureHelper.HandleWaterCheckTestingStrip(water);
            bag = QaFeatureGallery.Item("backpack_medium", stock);
            stolen = QaFeatureGallery.Item("wine_yeast", stock);
            var inside = QaFeatureGallery.Item("wine_yeast", (GameGridInventory)bag.contentWindow.childElement);
            StolenHelper.SetHeat(stolen, 42); StolenHelper.SetHeat(inside, 42);
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width * .76f, Screen.height * .70f));
            phase = 2; return false;
        }
        if (phase == 2)
        {
            if (!QaFeatureGallery.Visible(water?.middle?.transform.Find("ShopAssist_PublicLabels"))) return false;
            gallery.Capture("f38"); phase = 3; return false;
        }
        if (phase == 3)
        {
            if (!gallery.CaptureReady()) return false;
            TradeScenario.SetOptions(false, "f38.enabled");
            TradeScenario.SetOptions(true, "f04.enabled", "f39.enabled");
            PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateInspectionClient());
            PlayerStore.Instance.isClientArrived = true;
            phase = 4; return false;
        }
        if (phase == 4)
        {
            if (!QaFeatureGallery.Visible(stolen?.middle?.transform.Find("ShopAssist_KnownStolen")) ||
                !QaFeatureGallery.Visible(bag?.middle?.transform.Find("ShopAssist_KnownStolen"))) return false;
            gallery.Capture("f39"); phase = 5; return false;
        }
        if (!gallery.CaptureReady()) return false;
        gallery.RestoreSavedLocale(); result = "PASS gallery F38 public item labels and F39 known stolen markers"; return true;
    }
}
