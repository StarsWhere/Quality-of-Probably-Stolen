using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Captures rendered shop and machine tools at the game's 1280x720 setting.
public sealed class ShopToolsVisualScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly string locale;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private readonly string captureId = Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly Dictionary<string, bool> original = new Dictionary<string, bool>();
    private GameOptionUIManager options;
    private string originalSavedLocale, shopCapture, objectCapture;
    private int originalMode, originalResolution, originalDivisor, step, frame, localeStableFrame;
    private float localeStableAfter;
    private bool prepared, restored, localeRequested, localeReady, localeSaved, localeKeyExisted;
    private GameItem machine;
    private CustomUIWindow tools, card;
    private QaSyntheticInput input;
    private bool tooltipFixtureShown;

    public ShopToolsVisualScenario() : this("zh") { }
    internal ShopToolsVisualScenario(string locale) { this.locale = locale; }

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch { Restore(); throw; }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Tools visual scenario timed out at " + step);
        if (step == 0)
        {
            if (!localeRequested)
            {
                if (MainMenuUIController.Instance == null) return false;
                string currentLocale = LocHelper.GetCurrentLocaleCode();
                if (string.IsNullOrEmpty(currentLocale)) return false;
                localeKeyExisted = ES3.KeyExists("locale");
                originalSavedLocale = localeKeyExisted ? ES3.LoadString("locale", "", null, null) : null;
                ES3.Save("locale", locale); localeSaved = true;
                localeRequested = true;
                if (currentLocale == locale) localeReady = true;
                else MainMenuUIController.Instance.StartCoroutine(SetLocale());
                return false;
            }
            if (!localeReady || LocHelper.GetCurrentLocaleCode() != locale) return false;
            if (localeStableAfter == 0)
            {
                localeStableAfter = Time.realtimeSinceStartup + .5f; localeStableFrame = Time.frameCount + 3;
                return false;
            }
            if (Time.realtimeSinceStartup < localeStableAfter || Time.frameCount < localeStableFrame) return false;
            if (start.Tick()) step++;
            return false;
        }
        if (step == 1)
        {
            options = StoreUIManager.Instance?.gameOptionUIManager;
            if (options == null || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || EmporiumOption.Instance == null) return false;
            Need(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "Tools visual scenario requires rendered Player");
            string[] keys = { "f05.enabled", "f07.enabled", "f08.enabled", "f10.enabled", "f22.enabled", "f25.enabled", "f26.enabled", "f31.enabled", "f32.enabled", "f33.enabled", "f35.enabled" };
            foreach (string key in keys) original[key] = ((ConfigEntry<bool>)ModConfig.Find(Mod).Get(key)).Value;
            originalMode = EmporiumOption.Instance.fullscreenMode;
            originalResolution = EmporiumOption.Instance.resolution;
            originalDivisor = EmporiumOption.Instance.borderlessDivisor;
            prepared = true;
            TradeScenario.SetOptions(true, "f05.enabled", "f07.enabled", "f08.enabled", "f10.enabled", "f22.enabled", "f25.enabled", "f26.enabled", "f31.enabled", "f32.enabled");
            TradeScenario.SetOptions(false, "f33.enabled", "f35.enabled");
            EmporiumOption.Instance.fullscreenMode = 2;
            EmporiumOption.Instance.resolution = 3;
            EmporiumOption.Instance.ApplySetting();
            frame = Time.frameCount + 12; step++; return false;
        }
        if (step == 2)
        {
            if (!localeReady || Time.frameCount < frame || Screen.width != 1280 || Screen.height != 720 || LocHelper.GetCurrentLocaleCode() != locale) return false;
            options.OpenUI();
            CustomUIManager.Instance.GetWindow("game_options").Get(Mod + ":tools").button.onClick.Invoke();
            tools = Window("tools"); frame = Time.frameCount + 6; step++; return false;
        }
        if (step == 3)
        {
            if (Time.frameCount < frame) return false;
            Canvas.ForceUpdateCanvases();
            shopCapture = Path.Combine(Application.persistentDataPath, "qa-shop-tools-" + locale + "-1280x720-" + captureId + ".png");
            ScreenCapture.CaptureScreenshot(shopCapture);
            Debug.Log("[PLAYER-QA] tools diagnostic capture " + shopCapture);
            frame = Time.frameCount + 4; step = 30; return false;
        }
        if (step == 30)
        {
            if (Time.frameCount < frame || !Ready(shopCapture)) return false;
            Canvas.ForceUpdateCanvases();
            LogToolTextLayout("attention", tools.Get("attention"), false, 180);
            LogToolTextLayout("manage", tools.Get("manage"), false, 150);
            for (int i = 0; i < 5; i++) LogToolTextLayout("category_" + i, tools.Get("category_" + i), true, 0);
            var category = tools.Get("category_0");
            var action = tools.Get(Mod + ":f07.open");
            Need(category?.text != null && action?.text != null && category.root.activeInHierarchy && action.root.activeInHierarchy,
                "Small-screen category or action is missing");
            LogLayout("category", category.Rect);
            LogLayout("action", action.Rect);
            Need(category.text.text == ModHelper.GetLocalized(Mod, "section.category1") && action.text.alignment == TextAlignmentOptions.MidlineLeft,
                "Localized sidebar or left-aligned action is incorrect");
            Rect left = Bounds(category.Rect), right = Bounds(action.Rect), frameBounds = Bounds(tools.Rect);
            Need(left.xMax <= right.xMin + 2 && right.width > 180 && right.width > left.width &&
                Mathf.Abs(category.Rect.rect.width - 145) < 2 && action.Rect.rect.width >= 260 && action.Rect.rect.height <= 50,
                "Shop tools columns overlap or action rows are oversized: " + left + " / " + right);
            Need(frameBounds.xMin >= 0 && frameBounds.yMin >= 0 && frameBounds.xMax <= Screen.width && frameBounds.yMax <= Screen.height,
                "Shop tools frame is outside the small screen");
            CheckGlyphs(category.text); CheckGlyphs(action.text);
            CheckReadable(action);
            CheckReadable(category);
            CheckTextWithinButton(action);
            Rect? previousCategory = null;
            for (int categoryIndex = 0; categoryIndex < 5; categoryIndex++)
            {
                var sidebar = tools.Get("category_" + categoryIndex);
                Need(sidebar?.text != null && sidebar.text.text == ModHelper.GetLocalized(Mod, "section.category" + (categoryIndex + 1)),
                    "Missing localized sidebar category " + categoryIndex);
                CheckGlyphs(sidebar.text);
                CheckReadable(sidebar);
                CheckTextWithinButton(sidebar);
                Rect currentCategory = Bounds(sidebar.Rect);
                Need(previousCategory == null || currentCategory.yMax <= previousCategory.Value.yMin + .75f,
                    "Sidebar categories overlap: " + previousCategory + " / " + currentCategory);
                previousCategory = currentCategory;
            }
            Rect textBounds = Bounds(action.text.rectTransform);
            Need(textBounds.xMin - right.xMin < 16, "Action label is not positioned at the left edge: " + textBounds + " / " + right);
            Need(tools.Get("show_disabled")?.toggle != null && !tools.Get("show_disabled").GetBool() &&
                tools.Get("manage")?.button != null && tools.Get("attention")?.button != null,
                "Shop tools default filter or management header is missing");
            var attention = tools.Get("attention");
            var manage = tools.Get("manage");
            CheckGlyphs(attention.text); CheckGlyphs(manage.text);
            CheckReadable(attention); CheckReadable(manage);
            CheckTextWithinButton(attention); CheckTextWithinButton(manage);
            Need(Bounds(attention.Rect).xMax <= Bounds(manage.Rect).xMin + .75f,
                "Shop tools header buttons overlap");
            frame = Time.frameCount + 1; step = 4; return false;
        }
        if (step == 4)
        {
            if (Time.frameCount < frame || !Ready(shopCapture)) return false;
            tools.Close(); options.CloseUI();
            machine = DirectoryMaster.Item("hydroponic"); Need(machine != null, "Missing hydroponic fixture");
            GeneralHelper.SetItemOwned(machine, true); GeneralHelper.SetCustomName(machine, locale == "zh" ? "小屏测试机器" : "QA small-screen machine");
            var marker = EmporiumEntry.Instance.invElement.TryFindOneValidInventorySlot(machine);
            Need(marker != null && marker.targetItem == null && marker.TryAcceptOnce(machine.unitCount) == machine.unitCount,
                "Could not place the machine fixture");
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(200, 300));
            frame = Time.frameCount + 8; step++; return false;
        }
        if (step == 5)
        {
            if (Time.frameCount < frame) return false;
            var button = machine.contentWindow?.handler?.transform.Find("ShopAssist_WindowTools")?.GetComponent<TreeNodeRender>()?.element as MachineryButton;
            if (button == null || button.isDisabled) return false;
            button.Select(); card = Window("context");
            Canvas.ForceUpdateCanvases();
            Rect bounds = Bounds(card.Rect);
            Vector2 point = new Vector2(bounds.xMin + 8, bounds.yMax - 8);
            input = new QaSyntheticInput(); input.Mouse = new Vector3(point.x, point.y, 0);
            OverlayHandler.current.ShowTooltip(point, Vector2.zero, new RichTextBuilder(40).AddLine("QA underlying tooltip").Build(), 60);
            tooltipFixtureShown = OverlayHandler.current.GetTooltipWindow()?.IsVisible() == true;
            frame = Time.frameCount + 8; step++; return false;
        }
        if (step == 6)
        {
            if (Time.frameCount < frame) return false;
            Canvas.ForceUpdateCanvases();
            objectCapture = Path.Combine(Application.persistentDataPath, "qa-object-tools-" + locale + "-1280x720-" + captureId + ".png");
            ScreenCapture.CaptureScreenshot(objectCapture);
            Debug.Log("[PLAYER-QA] object diagnostic capture " + objectCapture);
            frame = Time.frameCount + 4; step = 60; return false;
        }
        if (step == 60)
        {
            if (Time.frameCount < frame || !Ready(objectCapture)) return false;
            Canvas.ForceUpdateCanvases();
            Need(card.Get("f10.open")?.root?.activeInHierarchy == true && card.Get("f31.open")?.root?.activeInHierarchy == true &&
                card.Get("f32.open")?.root?.activeInHierarchy == true && card.Get("f33.open") == null && card.Get("f35.open") == null,
                "Machine tools include unsupported actions or omit supported ones");
            Need(card.Get("manage")?.button != null && card.Get("f31.open").text.alignment == TextAlignmentOptions.MidlineLeft,
                "Object tools lack the unified settings entry or compact rows");
            CheckGlyphs(card.Get("f31.open").text);
            CheckReadable(card.Get("f31.open"));
            CheckTextWithinButton(card.Get("f31.open"));
            CheckGlyphs(card.Get("manage").text);
            CheckReadable(card.Get("manage"));
            CheckTextWithinButton(card.Get("manage"));
            Need(tooltipFixtureShown && OverlayHandler.current.GetTooltipWindow()?.IsVisible() != true,
                "Native item tooltip remained visible while the pointer was over object tools");
            frame = Time.frameCount + 1; step = 7; return false;
        }
        if (step == 7)
        {
            if (Time.frameCount < frame || !Ready(objectCapture)) return false;
            Restore();
            result = "PASS rendered " + locale + " 1280x720 shop/sidebar and machine object tools; screenshots " + shopCapture + " and " + objectCapture;
            return true;
        }
        throw new InvalidOperationException("Unknown tools visual step");
    }

    private void Restore()
    {
        if (restored) return;
        restored = true;
        try
        {
            if (!prepared) return;
            input?.Dispose(); input = null;
            if (card?.IsAlive == true) card.Close();
            if (tools?.IsAlive == true) tools.Close();
            options?.CloseUI();
            ModConfig config = ModConfig.Find(Mod);
            foreach (var pair in original) ((ConfigEntry<bool>)config.Get(pair.Key)).Set(pair.Value);
            config.Save();
            EmporiumOption.Instance.fullscreenMode = originalMode;
            EmporiumOption.Instance.resolution = originalResolution;
            EmporiumOption.Instance.borderlessDivisor = originalDivisor;
            EmporiumOption.Instance.ApplySetting();
        }
        finally
        {
            // The native game switches language in its main menu; this QA process exits after the scene.
            if (localeSaved)
            {
                if (localeKeyExisted) ES3.Save("locale", originalSavedLocale);
                else ES3.DeleteKey("locale");
            }
        }
    }
    private IEnumerator SetLocale()
    {
        yield return LocHelper.SetLocale(locale);
        localeReady = true;
    }
    private static CustomUIWindow Window(string id)
    {
        var value = CustomUIManager.Instance.GetWindow(Mod + "." + id);
        Need(value?.IsAlive == true, "Missing " + id + " window"); return value;
    }
    private static Rect Bounds(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return Rect.MinMaxRect(a.x, a.y, b.x, b.y);
    }
    private static void CheckGlyphs(TMP_Text text)
    {
        Need(text != null && text.isActiveAndEnabled && text.font != null, "Missing rendered text");
        text.ForceMeshUpdate(true, true);
        var characters = text.textInfo.characterInfo.Take(text.textInfo.characterCount)
            .Where(c => c.elementType == TMP_TextElementType.Character && !char.IsWhiteSpace(c.character)).ToArray();
        Need(!text.isTextTruncated && !text.isTextOverflowing && characters.Length > 0 &&
            characters.All(c => c.isVisible && c.textElement != null && c.textElement.unicode == c.character),
            "A localized label is clipped or missing glyphs: " + text.text);
    }
    private static void CheckReadable(CustomUIElement element)
    {
        var button = element?.button;
        var text = element?.text;
        var canvas = text?.GetComponentInParent<Canvas>();
        Need(button != null && text != null && button.transition == Selectable.Transition.ColorTint,
            "Tool row does not apply its actual color transition");
        var image = button.targetGraphic as Image;
        Need(image != null, "Tool row lacks a rendered background");
        Color tint = image.canvasRenderer.GetColor();
        Color background = image.color * tint;
        Color foreground = text.color;
        float relative(Color c) => .2126f * Mathf.GammaToLinearSpace(c.r) + .7152f * Mathf.GammaToLinearSpace(c.g) + .0722f * Mathf.GammaToLinearSpace(c.b);
        float light = relative(foreground), dark = relative(background);
        float contrast = (Mathf.Max(light, dark) + .05f) / (Mathf.Min(light, dark) + .05f);
        float effectiveSize = text.fontSize * (canvas != null ? canvas.scaleFactor : 1f);
        Debug.Log("[PLAYER-QA] readable tool " + text.text + " bg=" + background + " fg=" + foreground +
            " contrast=" + contrast + " pixels=" + effectiveSize + " textRect=" + Bounds(text.rectTransform));
        Need(contrast >= 4.5f && effectiveSize >= 12f, "Tool row lacks contrast or is smaller than 12 screen pixels: " + text.text);
    }
    private static void CheckTextWithinButton(CustomUIElement element)
    {
        var text = element.text;
        var canvas = text.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Rect bounds = Bounds(element.Rect);
        foreach (var character in text.textInfo.characterInfo.Take(text.textInfo.characterCount).Where(value => value.isVisible))
        {
            Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, text.transform.TransformPoint(character.bottomLeft));
            Vector2 topRight = RectTransformUtility.WorldToScreenPoint(camera, text.transform.TransformPoint(character.topRight));
            Need(bottomLeft.x >= bounds.xMin - .75f && bottomLeft.y >= bounds.yMin - .75f &&
                topRight.x <= bounds.xMax + .75f && topRight.y <= bounds.yMax + .75f,
                "Tool label extends outside its button: " + text.text + " glyph=" + character.character +
                " glyphBounds=" + bottomLeft + "/" + topRight + " button=" + bounds);
        }
    }
    private static bool Ready(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
    private static void LogLayout(string label, RectTransform rect)
    {
        var parts = new List<string>();
        for (Transform node = rect; node != null && parts.Count < 5; node = node.parent)
        {
            if (!(node is RectTransform current)) continue;
            var layout = current.GetComponent<LayoutElement>();
            parts.Add(current.name + " size=" + current.rect.size + " min=" + LayoutUtility.GetMinWidth(current) +
                " pref=" + LayoutUtility.GetPreferredWidth(current) + " flex=" + LayoutUtility.GetFlexibleWidth(current) +
                " override=" + (layout == null ? "none" : layout.minWidth + "/" + layout.preferredWidth + "/" + layout.flexibleWidth));
        }
        Debug.Log("[PLAYER-QA] tools layout " + label + ": " + string.Join(" | ", parts));
    }
    private static void LogToolTextLayout(string id, CustomUIElement element, bool category, float minimumWidth)
    {
        if (element?.text == null || element.root == null)
        {
            Debug.Log("[PLAYER-QA] tool measurement " + id + " missing");
            return;
        }
        var text = element.text;
        var layout = element.root.GetComponent<LayoutElement>();
        var parent = element.root.transform.parent?.GetComponent<HorizontalOrVerticalLayoutGroup>();
        float availableWidth = Mathf.Max(1, element.Rect.rect.width - 20);
        Vector2 natural = text.GetPreferredValues(text.text);
        var style = text.fontStyle;
        if (category) text.fontStyle = style | FontStyles.Bold;
        Vector2 wrapped = text.GetPreferredValues(text.text, availableWidth, 0);
        text.fontStyle = style;
        float target = category ? Mathf.Max(52, Mathf.Ceil(wrapped.y) + 10)
            : Mathf.Max(minimumWidth, Mathf.Ceil(natural.x) + 26);
        object orthographic = typeof(TMP_Text).GetField("m_isOrthographic", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(text);
        text.ForceMeshUpdate(true, true);
        Debug.Log("[PLAYER-QA] tool measurement " + id + " label='" + text.text + "' active=" + text.isActiveAndEnabled +
            " font=" + (text.font == null ? "missing" : text.font.name) + " size=" + text.fontSize + " auto=" + text.enableAutoSizing +
            " orthographic=" + orthographic + " row=" + element.Rect.rect.size + " textRect=" + text.rectTransform.rect.size +
            " natural=" + natural + " boldWrapped=" + wrapped + " target=" + target + " lines=" + text.textInfo.lineCount +
            " min=" + (layout == null ? "missing" : layout.minWidth + "/" + layout.minHeight) +
            " preferred=" + (layout == null ? "missing" : layout.preferredWidth + "/" + layout.preferredHeight) +
            " flexible=" + (layout == null ? "missing" : layout.flexibleWidth + "/" + layout.flexibleHeight) +
            " parentControl=" + (parent == null ? "missing" : parent.childControlWidth + "/" + parent.childControlHeight));
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

public sealed class EnglishToolsVisualScenario
{
    private readonly ShopToolsVisualScenario scenario = new ShopToolsVisualScenario("en");
    public bool Tick(out string result) => scenario.Tick(out result);
}
