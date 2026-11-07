using System;
using System.Reflection;
using UnityEngine;

// Reuses the native band selection and virtual-input fixture to capture rotation and exact placement.
public sealed class FeatureGalleryGroupScenario
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private readonly DailyGroupInputScenario fixture = new DailyGroupInputScenario();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private int phase, after;
    private GameGridInventory target;
    private QaSyntheticInput input;

    public bool Tick(out string result)
    {
        result = null;
        QaFeatureGallery.Need(Time.realtimeSinceStartup < deadline, "Group gallery timed out at " + phase);
        if (phase == 0)
        {
            if (!gallery.ChineseMainMenu()) return false;
            phase = 1;
        }
        if (phase == 1)
        {
            if ((int)QaFeatureGallery.Field(fixture, "step") == 1 && !gallery.Screen1920()) return false;
            fixture.Tick(out _);
            if ((int)QaFeatureGallery.Field(fixture, "step") != 2) return false;
            var sourceBag = (GameItem)QaFeatureGallery.Field(fixture, "sourceBag");
            var targetBag = (GameItem)QaFeatureGallery.Field(fixture, "targetBag");
            GeneralHelper.SetCustomName(sourceBag, "随身背包"); GeneralHelper.SetCustomName(targetBag, "目标容器");
            sourceBag.contentWindow.SetTitle(sourceBag.GetDisplayName(true));
            targetBag.contentWindow.SetTitle(targetBag.GetDisplayName(true));
            sourceBag.contentWindow.Show(new Vector2(Screen.width * .30f, Screen.height * .72f), false);
            targetBag.contentWindow.Show(new Vector2(Screen.width * .55f, Screen.height * .72f), false);
            TradeScenario.SetOptions(true, "f40.enabled", "f41.enabled");
            TradeScenario.SetOptions(false, "f15.enabled", "f21.enabled", "f38.enabled", "f39.enabled");
            target = (GameGridInventory)QaFeatureGallery.Field(fixture, "target");
            input = (QaSyntheticInput)QaFeatureGallery.Field(fixture, "input");
            after = Time.frameCount + 8; phase = 2; return false;
        }
        if (phase == 2)
        {
            if (Time.frameCount < after) return false;
            QaFeatureGallery.Invoke(fixture, "SelectAndDrag");
            gallery.Capture("f40-before"); phase = 3; return false;
        }
        if (phase == 3)
        {
            if (!gallery.CaptureReady()) return false;
            QaFeatureGallery.Invoke(fixture, "Rotate");
            QaFeatureGallery.Invoke(fixture, "FindGreen", target);
            gallery.Capture("f40"); phase = 4; return false;
        }
        if (phase == 4)
        {
            if (!gallery.CaptureReady()) return false;
            gallery.Capture("f41-valid"); phase = 5; return false;
        }
        if (phase == 5)
        {
            if (!gallery.CaptureReady()) return false;
            var blocker = (GameItemElement)QaFeatureGallery.Field(fixture, "blocker");
            MethodInfo point = typeof(DailyGroupInputScenario).GetMethod("ItemPoint", Flags);
            QaFeatureGallery.Need(point != null, "Native target position helper is unavailable");
            input.Mouse = (Vector3)point.Invoke(null, new object[] { target, blocker });
            QaFeatureGallery.Invoke(fixture, "UpdateDrag", 30);
            bool red = (bool)QaFeatureGallery.Invoke(fixture, "HasSprite", "hoverHighlightNodes", RenderHandler.current.redSquare);
            QaFeatureGallery.Need(red, "Native group placement did not show the blocked red destination");
            gallery.Capture("f41-blocked"); phase = 6; return false;
        }
        if (!gallery.CaptureReady()) return false;
        QaFeatureGallery.Invoke(fixture, "Cleanup"); gallery.RestoreSavedLocale();
        result = "PASS gallery F40 native rotated formation; F41 native green and red placement previews";
        return true;
    }
}
