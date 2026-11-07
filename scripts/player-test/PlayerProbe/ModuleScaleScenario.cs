using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Exercises both native display sizes and two canvas scales in one locale per process.
public class ModuleScaleScenario : EquipmentFixture
{
    private readonly int[] widths = { 1280, 1280, 1920, 1920 };
    private readonly int[] heights = { 720, 720, 1080, 1080 };
    private readonly int[] resolutions = { 3, 3, 0, 0 };
    private readonly float[] scales = { 1f, 1.25f, 1f, 1.25f };
    private readonly string language;
    private GameItem machine, candidate;
    private GameGridInventory bay;
    private Canvas canvas;
    private CanvasScaler scaler;
    private CanvasScaler.ScaleMode originalScaleMode;
    private CanvasScaler.ScreenMatchMode originalMatchMode;
    private Vector2 originalReference;
    private float originalMatch, originalScaleFactor, normalScale, stableAfter;
    private int originalMode, originalResolution, originalDivisor, pass, frame;
    private bool restore, addedScaler, localeReady;
    private float localeStableAfter;
    private int localeFrame;
    private readonly float deadline = Time.realtimeSinceStartup + 210;

    public ModuleScaleScenario() : this("zh") { }
    protected ModuleScaleScenario(string language) { this.language = language; }

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch
        {
            try { Restore(); }
            catch (Exception error) { Debug.LogWarning("[PLAYER-QA] Could not restore display settings: " + error.Message); }
            throw;
        }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Check(Time.realtimeSinceStartup < deadline, "Module scale QA timed out at step " + step + " pass " + pass);
        if (!localeReady && !VerifyStartupLocale()) return false;
        if (!Ready() || EmporiumOption.Instance == null) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f10.enabled", "f12.enabled");
            machine = Stock("hydroponic"); candidate = Stock("system_module_performance");
            var bag = Stock("backpack_medium"); GeneralHelper.SetCustomName(bag, "QA Scale Storage");
            ModHelper.SetModData(Mod, "module_storage", "");
            Open(machine); bay = Panel<GameGridInventory>(machine, "module"); machine.contentWindow.ToFront(); Action("f10.open");
            Ui("f10").Get("storage_choose_footer").button.onClick.Invoke();
            var picker = Ui("choose");
            MonoBehaviour hit = null;
            for (int page = 0; page < 20 && hit == null; page++)
            {
                hit = QaItemUi.Hit(picker.root, name: "QA Scale Storage");
                if (hit == null && picker.Get("next")?.button != null)
                { picker.Get("next").button.onClick.Invoke(); picker = Ui("choose"); }
            }
            Check(hit != null, "Scale fixture storage is absent from the picker"); QaItemUi.Click(hit);
            originalMode = EmporiumOption.Instance.fullscreenMode;
            originalResolution = EmporiumOption.Instance.resolution;
            originalDivisor = EmporiumOption.Instance.borderlessDivisor;
            var layer = CustomUIManager.Instance.GetLayerRoot(CustomUIManager.LAYER_META);
            canvas = layer?.GetComponentInParent<Canvas>()?.rootCanvas;
            Check(canvas != null, "Module workbench canvas is missing");
            scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null) { scaler = canvas.gameObject.AddComponent<CanvasScaler>(); addedScaler = true; }
            originalScaleMode = scaler.uiScaleMode; originalMatchMode = scaler.screenMatchMode;
            originalReference = scaler.referenceResolution; originalMatch = scaler.matchWidthOrHeight;
            originalScaleFactor = scaler.scaleFactor; restore = true;
            Ui("f10")?.Close(); ChangeDisplay(); step = 1; return false;
        }
        if (step == 1)
        {
            if (Screen.width != widths[pass] || Screen.height != heights[pass] ||
                LocHelper.GetCurrentLocaleCode() != language ||
                Time.realtimeSinceStartup < stableAfter || Time.frameCount < frame) return false;
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0;
            scaler.referenceResolution = new Vector2(1920, 1080) / scales[pass];
            frame = Time.frameCount + 3; step = 2; return false;
        }
        if (step == 2)
        {
            if (Time.frameCount < frame) return false;
            if (pass % 2 == 0) normalScale = canvas.scaleFactor;
            else Check(canvas.scaleFactor > normalScale * 1.15f, "Canvas scale did not actually increase");
            Open(machine); machine.contentWindow.ToFront(); Action("f10.open");
            var browser = Ui("f10"); Check(browser != null, "Module workbench did not open");
            frame = Time.frameCount + 3; step = 20; return false;
        }
        if (Time.frameCount < frame) return false;
        if (step == 20)
        {
            var browser = Ui("f10");
            Check(browser != null, "Module workbench closed before direct actions were checked");
            Canvas.ForceUpdateCanvases();
            CheckDirectActions(browser);
            browser.Get("idle_tab").button.onClick.Invoke(); browser = Ui("f10");
            LayoutScenario.Select(browser, candidate);
            browser.Get("layout").button.onClick.Invoke();
            Ui("f10").Get("preview").button.onClick.Invoke();
            frame = Time.frameCount + 3; step = 3; return false;
        }
        if (step == 3)
        {
            var browser = Ui("f10"); Check(browser?.Get("apply") != null, "Module layout preview was not created");
            Canvas.ForceUpdateCanvases();
            var layer = CustomUIManager.Instance.GetLayerRoot(CustomUIManager.LAYER_META);
            float expectedWidth = Mathf.Min(layer.rect.width * .8f, layer.rect.width - 32);
            Check(browser.Rect.rect.width <= expectedWidth + 3 && browser.Rect.rect.width >= Mathf.Min(760, expectedWidth) - 3,
                "Workbench width does not fit its content and scaled canvas");
            Rect bounds = Bounds(browser.Rect);
            Check(bounds.xMin >= -2 && bounds.yMin >= -2 && bounds.xMax <= Screen.width + 2 && bounds.yMax <= Screen.height + 2,
                "Workbench is clipped by the screen at scale " + scales[pass] + ": " + bounds);
            Check(browser.Get("search").Rect.rect.width <= 221, "Search field is too wide");
            foreach (string id in new[] { "source_title", "storage_status", "selected_count", "layout_performance", "layout_efficiency", "layout_quality" })
                CheckGlyphs(browser.Get(id)?.text, id);
            Check(browser.Get("storage_status").text.text.Contains("QA Scale Storage"), "Bound storage capacity is absent");
            CheckDirectActions(browser);
            Check(browser.Get("selected_count").text.text == ModHelper.GetLocalized(Mod, "grid.selected").Replace("{0}", "1"),
                "Selection count was not updated");
            CheckPreviewWidth(browser);
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath,
                    "qa-module-scale-" + widths[pass] + "x" + heights[pass] + "-" + (pass % 2) + "-" + language + ".png"));
            frame = Time.frameCount + 3; step = 4; return false;
        }
        if (step == 4)
        {
            Ui("f10")?.Close();
            if (++pass < widths.Length) { ChangeDisplay(); step = 1; return false; }
            Restore();
            frame = Time.frameCount + 3; step = 5; return false;
        }
        if (step == 5)
        {
            Check(LocHelper.GetCurrentLocaleCode() == language, "Locale changed during module scale checks");
            result = "PASS " + language + " module workbench at 1280x720 and 1920x1080 with normal and 125% canvas scale, visible direct actions, selection and F12 preview";
            return true;
        }
        throw new InvalidOperationException("Unknown module scale QA step " + step);
    }

    private bool VerifyStartupLocale()
    {
        if (SceneManager.GetActiveScene().name != "EmporiumMenu" || MainMenuUIController.Instance == null || CustomUIManager.Instance == null) return false;
        var initial = LocalizationSettings.InitializationOperation;
        if (!initial.IsValid() || !initial.IsDone) return false;
        Check(initial.Status == AsyncOperationStatus.Succeeded, "Native localization initialization failed");
        var selected = LocalizationSettings.SelectedLocaleAsync;
        if (!selected.IsValid() || !selected.IsDone) return false;
        Check(selected.Status == AsyncOperationStatus.Succeeded && selected.Result != null,
            "Native startup locale could not be loaded");
        Check(selected.Result.Identifier.Code == language && LocHelper.GetCurrentLocaleCode() == language &&
            ES3.LoadString("locale", "", null, null) == language,
            "Incorrect native startup locale: expected=" + language + " actual=" + selected.Result.Identifier.Code);
        if (localeStableAfter == 0)
        {
            localeStableAfter = Time.realtimeSinceStartup + .5f;
            localeFrame = Time.frameCount + 3;
            return false;
        }
        if (Time.realtimeSinceStartup < localeStableAfter || Time.frameCount < localeFrame) return false;
        localeReady = true;
        return true;
    }

    private void ChangeDisplay()
    {
        EmporiumOption.Instance.fullscreenMode = 2; EmporiumOption.Instance.resolution = resolutions[pass];
        EmporiumOption.Instance.ApplySetting();
        stableAfter = Time.realtimeSinceStartup + 1.5f; frame = Time.frameCount + 10;
    }

    private void Restore()
    {
        if (restore)
        {
            restore = false;
            if (scaler != null)
            {
                if (addedScaler) UnityEngine.Object.Destroy(scaler);
                else
                {
                    scaler.uiScaleMode = originalScaleMode; scaler.screenMatchMode = originalMatchMode;
                    scaler.referenceResolution = originalReference; scaler.matchWidthOrHeight = originalMatch; scaler.scaleFactor = originalScaleFactor;
                }
            }
            EmporiumOption.Instance.fullscreenMode = originalMode; EmporiumOption.Instance.resolution = originalResolution;
            EmporiumOption.Instance.borderlessDivisor = originalDivisor; EmporiumOption.Instance.ApplySetting();
        }
    }

    private static void CheckDirectActions(CustomUIWindow browser)
    {
        Check(browser.Get("menu") == null && browser.Get("storage_change") == null, "Collapsed module actions are still present");
        var choose = browser.Get("storage_choose_footer");
        var refresh = browser.Get("refresh");
        Check(choose?.button != null && refresh?.button != null && choose.root.activeInHierarchy && refresh.root.activeInHierarchy &&
            choose.button.IsInteractable() && refresh.button.IsInteractable(), "Direct storage and refresh buttons are not visible and actionable");
        Check(choose.GetText() == ModHelper.GetLocalized(Mod, "grid.choose_storage") &&
            refresh.GetText() == ModHelper.GetLocalized(Mod, "machines.refresh"), "Direct module actions are not localized");
        CheckGlyphs(choose.text, "storage_choose_footer");
        CheckGlyphs(refresh.text, "refresh");
        Check(!choose.text.isTextTruncated && !choose.text.isTextOverflowing && !refresh.text.isTextTruncated && !refresh.text.isTextOverflowing,
            "Direct module action label is truncated");
        Rect first = Bounds(choose.Rect), second = Bounds(refresh.Rect);
        var firstScroll = choose.Rect.GetComponentInParent<ScrollRect>();
        var secondScroll = refresh.Rect.GetComponentInParent<ScrollRect>();
        Rect firstViewport = Bounds(firstScroll?.viewport != null ? firstScroll.viewport : browser.ContentRoot as RectTransform);
        Rect secondViewport = Bounds(secondScroll?.viewport != null ? secondScroll.viewport : browser.ContentRoot as RectTransform);
        Rect window = Bounds(browser.Rect), screen = new Rect(0, 0, Screen.width, Screen.height);
        Check(Contains(firstViewport, first) && Contains(secondViewport, second) && Contains(window, first) && Contains(window, second) &&
            Contains(screen, first) && Contains(screen, second), "Direct module buttons exceed their viewport: " + first + " / " + second);
        Check(first.xMax <= second.xMin + 1 || second.xMax <= first.xMin + 1 ||
            first.yMax <= second.yMin + 1 || second.yMax <= first.yMin + 1,
            "Direct module buttons overlap: " + first + " / " + second);
    }

    private static bool Contains(Rect outer, Rect inner) => inner.xMin >= outer.xMin - 2 && inner.yMin >= outer.yMin - 2 &&
        inner.xMax <= outer.xMax + 2 && inner.yMax <= outer.yMax + 2 && inner.width > 0 && inner.height > 0;

    private static void CheckGlyphs(TMP_Text text, string id)
    {
        Check(text != null && text.gameObject.activeInHierarchy && text.font != null, "Missing module text " + id);
        text.ForceMeshUpdate();
        Check(text.textInfo.characterInfo.Take(text.textInfo.characterCount).Any(character => character.isVisible),
            "Module text has no visible glyphs: " + id + "=" + text.text + " rect=" + text.rectTransform.rect);
    }

    private void CheckPreviewWidth(CustomUIWindow browser)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object Field(object value, string name) => value?.GetType().GetField(name, flags)?.GetValue(value);
        var views = browser.root.GetComponentsInChildren<MonoBehaviour>(true).Where(value => value != null &&
            value.gameObject.activeInHierarchy && value.GetType().FullName == "ShopAssist.ItemGrid+View").ToList();
        var current = views.FirstOrDefault(view => ReferenceEquals(Field(Field(view, "Options"), "Inventory"), bay));
        var preview = views.FirstOrDefault(view => Field(Field(view, "Options"), "Board") != null &&
            Field(Field(view, "Options"), "Inventory") == null);
        Check(current != null && preview != null, "Current and preview bay grids are missing");
        var currentRect = (RectTransform)Field(current, "Rect"); var previewRect = (RectTransform)Field(preview, "Rect");
        var scroll = currentRect.GetComponentInParent<ScrollRect>();
        Check(scroll?.viewport != null && scroll.content != null, "Current and preview bay viewport is missing");
        Rect area = Bounds(scroll.viewport), first = Bounds(currentRect), second = Bounds(previewRect);
        Check(first.xMin >= area.xMin - 2 && first.xMax <= area.xMax + 2 && second.xMin >= area.xMin - 2 && second.xMax <= area.xMax + 2 &&
            scroll.content.rect.width <= scroll.viewport.rect.width + 1 &&
            (scroll.horizontalScrollbar == null || !scroll.horizontalScrollbar.gameObject.activeInHierarchy),
            "Normal current/preview bays need horizontal scrolling: viewport=" + area + " current=" + first + " preview=" + second +
            " content=" + scroll.content.rect.width + "/" + scroll.viewport.rect.width);
    }

    private static Rect Bounds(RectTransform rect)
    {
        Canvas parent = rect.GetComponentInParent<Canvas>();
        Camera camera = parent == null || parent.renderMode == RenderMode.ScreenSpaceOverlay ? null : parent.worldCamera;
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return Rect.MinMaxRect(low.x, low.y, high.x, high.y);
    }
}

public sealed class ModuleScaleEnglishScenario : ModuleScaleScenario
{
    public ModuleScaleEnglishScenario() : base("en") { }
}
