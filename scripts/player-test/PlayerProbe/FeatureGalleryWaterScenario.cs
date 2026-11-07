using System;
using UnityEngine;

// Shows the actual inspected-water source, target and preview without pouring during capture.
public sealed class FeatureGalleryWaterScenario
{
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100f;
    private int phase;

    public bool Tick(out string result)
    {
        result = null;
        QaFeatureGallery.Need(Time.realtimeSinceStartup < deadline, "Water gallery timed out at " + phase);
        if (phase == 0) { if (!gallery.ChineseMainMenu()) return false; phase = 1; }
        if (phase == 1) { if (!start.Tick()) return false; phase = 2; }
        if (phase == 2)
        {
            if (!gallery.Screen1920() || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(true, "f26.enabled");
            var stock = EmporiumEntry.Instance.invElement;
            var source = QaFeatureGallery.Item("bottled_water", stock, "已检测水瓶");
            var target = QaFeatureGallery.Item("water_jug", stock, "集水壶");
            WaterHelper.Remove(source, WaterHelper.GetCurrentCapacityUL(source));
            WaterHelper.Remove(target, WaterHelper.GetCurrentCapacityUL(target));
            int sourceAmount = Math.Min(500000, WaterHelper.GetTotalCapacity(source));
            int targetAmount = Math.Min(1000000, WaterHelper.GetTotalCapacity(target));
            QaFeatureGallery.Need(sourceAmount > 0 && targetAmount > 0, "Native water capacities unavailable");
            WaterHelper.AddWater(source, 0, sourceAmount); WaterHelper.AddWater(target, 0, targetAmount);
            WaterFeatureHelper.HandleWaterCheckTestingStrip(source);
            WaterFeatureHelper.HandleWaterCheckTestingStrip(target);
            QaFeatureGallery.Action("f26.open");
            var window = QaFeatureGallery.Window("water");
            QaFeatureGallery.Need(window?.IsAlive == true, "Water collection window unavailable");
            QaItemUi.Choose(window, "target", target.GetDisplayName(true)); window = QaFeatureGallery.Window("water");
            QaFeatureGallery.Need(QaItemUi.Hit(window.root, source) != null, "Inspected source unavailable in collection window");
            QaItemUi.Click(window, source); window.Get("preview").button.onClick.Invoke();
            QaFeatureGallery.Need(WaterHelper.GetCurrentCapacityUL(source) == sourceAmount, "Water preview moved the source water");
            gallery.Capture("f26"); phase = 3; return false;
        }
        if (!gallery.CaptureReady()) return false;
        gallery.RestoreSavedLocale(); result = "PASS gallery F26 native inspected source, target and water preview"; return true;
    }
}
