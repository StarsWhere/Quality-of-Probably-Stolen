using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

// Real scanner lifecycle and visible component/method-card regression.
public sealed class ScannerPresentationScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem sample, empty, nonReactive;
    private Transform chemicalRoot;
    private string nativeWaterName, localeFailure;
    private string raw;
    private int step;
    private float since = Time.realtimeSinceStartup, until, chemicalBottom, localeDeadline;
    private Locale tableLocale;
    private AsyncOperationHandle<StringTable> localeTable;
    private bool tableRequested;
    internal bool ReadyForFixtures => step == 1;
    private static void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
    private static Transform Root(GameObject panel) => panel.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(t => t.name == "ShopAssist_LiquidBars" && t.gameObject.activeInHierarchy);
    private static string AllText(Transform root) => string.Join(" | ", root.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text));
    private static Button ButtonNamed(Transform root, string name) => root?.GetComponentsInChildren<Button>(true)
        .FirstOrDefault(b => b.gameObject.name == "Button_" + name && b.gameObject.activeInHierarchy);
    private static Transform Row(Transform root, string id) => root?.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(t => t.name == "Component_" + id && t.gameObject.activeInHierarchy);
    private static Transform Detail() => CustomUIManager.Instance.GetWindow(Mod + ".f19.detail.water")?.root?.transform;
    private void BeginLocaleWait()
    {
        tableLocale = null; localeTable = default; tableRequested = false;
        localeDeadline = Time.realtimeSinceStartup + 15;
    }

    private bool LocaleReady(string expected)
    {
        var selected = LocalizationSettings.SelectedLocaleAsync;
        if (!selected.IsValid() || !selected.IsDone || selected.Status != AsyncOperationStatus.Succeeded || selected.Result == null ||
            selected.Result.Identifier.Code != expected) return false;
        var initialization = LocalizationSettings.InitializationOperation;
        if (!initialization.IsValid() || !initialization.IsDone) return false;
        Need(initialization.Status == AsyncOperationStatus.Succeeded, "Native localization initialization failed: " + initialization.OperationException);
        if (!ReferenceEquals(tableLocale, selected.Result))
        { tableLocale = selected.Result; localeTable = default; tableRequested = false; }
        if (!tableRequested)
        {
            localeTable = LocalizationSettings.StringDatabase.GetTableAsync("Mechanic", tableLocale);
            tableRequested = true;
        }
        Need(localeTable.IsValid(), "Native Mechanic table handle became invalid");
        if (!localeTable.IsDone) return false;
        Need(localeTable.Status == AsyncOperationStatus.Succeeded && localeTable.Result != null,
            "Native Mechanic table failed: " + localeTable.OperationException);
        return true;
    }

    private bool WaitForLocale(string expected, string screenshot)
    {
        if (LocaleReady(expected)) return true;
        if (Time.realtimeSinceStartup < localeDeadline) return false;
        LocaleTimeout("Native locale/table did not become ready for " + expected, screenshot);
        return false;
    }

    private void LocaleTimeout(string message, string screenshot)
    {
        localeFailure = message;
        Shot(screenshot); until = Time.realtimeSinceStartup + 1; step = 205;
    }
    private static void OpenDetail(Transform root, string id)
    {
        var button = Row(root, id)?.GetComponent<Button>();
        Need(button != null, "Missing component action " + id); button.onClick.Invoke();
    }
    private static void Shot(string name)
    {
        string variant = Screen.width == 1280 && Screen.height == 720 && LocHelper.GetCurrentLocaleCode() == "en"
            ? "-en-1280x720" : "";
        ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-scanner-" + name + variant + ".png"));
    }
    private static Rect ScreenRect(RectTransform rect)
    {
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var points = corners.Select(point => RectTransformUtility.WorldToScreenPoint(camera, point)).ToArray();
        return Rect.MinMaxRect(points.Min(point => point.x), points.Min(point => point.y),
            points.Max(point => point.x), points.Max(point => point.y));
    }
    private static bool Rendered(TMP_Text text, Rect viewport)
    {
        if (text == null || !text.enabled || !text.gameObject.activeInHierarchy || text.color.a <= .01f ||
            text.canvasRenderer.cull || text.canvasRenderer.GetAlpha() <= .01f || text.canvasRenderer.GetInheritedAlpha() <= .01f ||
            text.rectTransform.rect.width <= 0 || text.rectTransform.rect.height <= 0) return false;
        if (text.fontSharedMaterial != null && text.fontSharedMaterial.HasProperty("_FaceColor") &&
            text.fontSharedMaterial.GetColor("_FaceColor").a <= .01f) return false;
        text.ForceMeshUpdate();
        TMP_TextInfo info = text.textInfo;
        bool requireFull = text.name == "Volume" || text.name == "Share" || text.name == "Total";
        if (requireFull && text.isTextTruncated) return false;
        int expected = 0, visible = 0;
        Canvas canvas = text.GetComponentInParent<Canvas>();
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        for (int i = 0; i < info.characterCount; i++)
        {
            TMP_CharacterInfo character = info.characterInfo[i];
            if (char.IsWhiteSpace(character.character)) continue;
            expected++;
            if (!character.isVisible || character.materialReferenceIndex < 0 || character.materialReferenceIndex >= info.meshInfo.Length) continue;
            var mesh = info.meshInfo[character.materialReferenceIndex];
            if (mesh.colors32 == null || character.vertexIndex < 0 || character.vertexIndex >= mesh.colors32.Length ||
                mesh.colors32[character.vertexIndex].a == 0) continue;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, text.rectTransform.TransformPoint(character.bottomLeft));
            Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, text.rectTransform.TransformPoint(character.topRight));
            Rect glyph = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            Rect screen = new Rect(0, 0, Screen.width, Screen.height);
            bool contained = glyph.xMin >= viewport.xMin - 1 && glyph.xMax <= viewport.xMax + 1 &&
                glyph.yMin >= viewport.yMin - 1 && glyph.yMax <= viewport.yMax + 1 &&
                glyph.xMin >= 0 && glyph.yMin >= 0 && glyph.xMax <= Screen.width && glyph.yMax <= Screen.height;
            if (glyph.width > 0 && glyph.height > 0 && (requireFull ? contained : glyph.Overlaps(viewport) && glyph.Overlaps(screen))) visible++;
        }
        return visible > 0 && (!requireFull || visible == expected);
    }
    private static string Describe(TMP_Text text)
    {
        if (text == null) return "missing";
        text.ForceMeshUpdate(true);
        return text.name + "=" + text.text + " rect=" + text.rectTransform.rect + " active=" + text.gameObject.activeInHierarchy +
            " enabled=" + text.enabled + " color=" + text.color + " alpha=" + text.canvasRenderer.GetAlpha() +
            " inherited=" + text.canvasRenderer.GetInheritedAlpha() + " cull=" + text.canvasRenderer.cull +
            " truncated=" + text.isTextTruncated + " characters=" + text.textInfo.characterCount;
    }
    private static void NeedVisibleRow(Transform root)
    {
        Canvas.ForceUpdateCanvases();
        var viewport = root.GetComponentsInChildren<RectMask2D>(true).FirstOrDefault(mask => mask.gameObject.activeInHierarchy);
        Need(viewport != null, "Missing visible scanner viewport");
        Rect visible = ScreenRect((RectTransform)viewport.transform);
        var rows = root.GetComponentsInChildren<Transform>(true).Where(node => node.gameObject.activeInHierarchy &&
            node.name.StartsWith("Component_", StringComparison.Ordinal)).ToArray();
        bool complete = rows.Any(row => new[] { "Name", "Volume", "Share" }.All(name =>
            Rendered(row.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(text => text.name == name), visible)) && VisiblePin(row, visible));
        if (!complete)
        {
            string diagnostics = string.Join("; ", rows.SelectMany(row => row.GetComponentsInChildren<TMP_Text>(true)
                .Where(text => text.name == "Name" || text.name == "Volume" || text.name == "Share").Select(Describe)));
            throw new InvalidOperationException("No scanner row has three rendered fields and a visible pin in the viewport " + visible + ": " + diagnostics);
        }
    }

    private static bool VisiblePin(Transform row, Rect viewport)
    {
        var graphic = row.GetComponentsInChildren<Graphic>(true).FirstOrDefault(item => item.name == "PinIcon");
        if (graphic == null || !graphic.enabled || !graphic.gameObject.activeInHierarchy || graphic.color.a <= .01f ||
            graphic.canvasRenderer.cull || graphic.canvasRenderer.GetInheritedAlpha() <= .01f ||
            !ScreenRect((RectTransform)graphic.transform).Overlaps(viewport)) return false;
        var populate = graphic.GetType().GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(VertexHelper) }, null);
        if (populate == null) return false;
        using (var mesh = new VertexHelper())
        {
            populate.Invoke(graphic, new object[] { mesh });
            return mesh.currentVertCount >= 10 && mesh.currentIndexCount >= 30;
        }
    }

    private static void NeedLocalizedNames(Transform root)
    {
        foreach (var entry in new[] { new[] { "water", "liquid_water" }, new[] { "organic_waste", "liquid_organic_waste" },
            new[] { "microbe", "liquid_microbes" } })
        {
            var name = Row(root, entry[0])?.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(text => text.name == "Name");
            string expected = LocHelper.GetLocalizedMechanic(entry[1]);
            Need(name != null && name.text == expected, "Wrong localized " + entry[0] + " name: " + name?.text + " expected " + expected);
        }
    }

    private static void NeedHeader(Transform root)
    {
        Canvas.ForceUpdateCanvases();
        var title = root.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(text => text.name == "SampleTitle");
        var total = root.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(text => text.name == "Total");
        var quality = root.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(text => text.name == "Tested");
        Rect screen = ScreenRect((RectTransform)root);
        Need(screen.xMin >= 0 && screen.yMin >= 0 && screen.xMax <= Screen.width && screen.yMax <= Screen.height,
            "Scanner screen is outside the display: " + screen);
        Need(Rendered(title, screen) && Rendered(total, screen) && Rendered(quality, screen),
            "Header is not visible: " + Describe(title) + "; " + Describe(total) + "; " + Describe(quality));
        Need(Mathf.Approximately(title.fontSize, 16) && Mathf.Approximately(total.fontSize, 13) &&
            Mathf.Approximately(quality.fontSize, 14), "Fixed header or summary typography changed with row size");
    }

    private static void SetRowSize(Transform root, float size)
    {
        var gear = root.GetComponentsInChildren<Button>(true).FirstOrDefault(button => button.name == "DisplaySettings");
        Need(gear != null, "Missing scanner display settings");
        var options = CustomUIManager.Instance.GetWindow(Mod + ".f19.display.water");
        if (options?.IsAlive != true) { gear.onClick.Invoke(); options = CustomUIManager.Instance.GetWindow(Mod + ".f19.display.water"); }
        Need(options?.IsAlive == true && options.Get("size")?.slider != null && options.Get("apply")?.button != null,
            "Scanner display settings did not open");
        options.Get("size").SetFloat(size, true); options.Get("apply").button.onClick.Invoke();
    }

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - since < 100, "Scanner presentation timed out at step " + step);
        if (step == 0) { if (start.Tick()) { until = Time.realtimeSinceStartup + 4; step++; } return false; }
        if (step == 1)
        {
            if (Time.realtimeSinceStartup < until) return false;
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || WaterScannerUIManager.Instance == null || ChemScannerUIManager.Instance == null) return false;
            var overlay = StoreUIManager.Instance.controlHintOverlay;
            if (overlay != null && overlay.activeSelf) StoreUIManager.Instance.ToggleControlHintOverlay();
            sample = DirectoryMaster.Item("bottled_water"); empty = DirectoryMaster.Item("wine_bottle");
            nonReactive = DirectoryMaster.Item("wine_bottle");
            Need(sample != null && empty != null && nonReactive != null, "Cannot create samples");
            GeneralHelper.SetItemOwned(sample); GeneralHelper.SetItemOwned(empty); GeneralHelper.SetItemOwned(nonReactive);
            var inventory = EmporiumEntry.Instance.invElement;
            foreach (var item in new[] { sample, empty, nonReactive })
            {
                var slot = inventory.TryFindOneValidInventorySlot(item);
                Need(slot != null && slot.TryAcceptOnce(item.unitCount) == item.unitCount, "Cannot place scanner sample");
                WaterHelper.Remove(item, WaterHelper.GetCurrentCapacityUL(item));
            }
            WaterHelper.AddLiquid(sample, "water", 730000); WaterHelper.AddLiquid(sample, "organic_waste", 12000);
            WaterHelper.AddLiquid(sample, "microbe", 23);
            WaterHelper.AddLiquid(nonReactive, "water", 730000); WaterHelper.AddLiquid(nonReactive, "organic_waste", 12000);
            WaterHelper.AddLiquid(nonReactive, "microbe", 23);
            Need(sample.onOneSecondItemFunc != null && nonReactive.onOneSecondItemFunc == null,
                "Fixture does not distinguish reactive and nonreactive liquid containers");
            ((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f19.enabled")).Set(true);
            ((ConfigEntry<int>)ModConfig.Find(Mod).Get("f19.size")).Set(14);
            nativeWaterName = Liquid.Liquids.First(liquid => liquid.identifier == "water").displayName;
            WaterScannerUIManager.Instance.Open(null, sample);
            raw = WaterScannerUIManager.Instance.contaminantTMP.text;
            BeginLocaleWait(); step = 11; return false;
        }
        if (step == 11)
        {
            if (!WaitForLocale(LocHelper.GetCurrentLocaleCode(), "water-startup-timeout")) return false;
            if (Root(WaterScannerUIManager.Instance.panel) == null)
            {
                if (Time.realtimeSinceStartup >= localeDeadline)
                    LocaleTimeout("Scanner view did not appear after native locale loading", "water-startup-timeout");
                return false;
            }
            Shot("water-layout"); until = Time.realtimeSinceStartup + 1; step = 2; return false;
        }
        if (step == 2)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var ui = WaterScannerUIManager.Instance; var root = Root(ui.panel);
            Need(root != null && !ui.contaminantTMP.enabled && ui.contaminantTMP.text == raw, "Water presentation missing or native text replaced");
            Need(Row(root, "water") != null && Row(root, "organic_waste") != null && Row(root, "microbe") != null &&
                AllText(root).Contains("<0.01%"), "Current sample or trace component missing: " + AllText(root));
            Need(root.GetComponentsInChildren<ScrollRect>(true).Any(), "Composition is not scrollable");
            var quality = root.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(text => text.name == "Tested");
            Need(quality != null && !ui.testedTMP.enabled && ui.testedTMP.text.Contains("98") && quality.text.Contains("98") &&
                Mathf.Approximately(quality.fontSize, 14) && !quality.enableAutoSizing,
                "Native quality header was not retained at the selected size");
            NeedHeader(root); NeedVisibleRow(root); NeedLocalizedNames(root);
            var scroll = root.GetComponentsInChildren<ScrollRect>(true).First();
            Need(scroll.content.rect.height <= scroll.viewport.rect.height + .5f && scroll.verticalScrollbar != null &&
                !scroll.verticalScrollbar.gameObject.activeInHierarchy, "An unnecessary scanner scrollbar was visible");
            Need(Liquid.Liquids.First(liquid => liquid.identifier == "water").displayName == nativeWaterName,
                "Displaying localized names modified the native liquid registry");
            step = 21; return false;
        }
        if (step == 205)
        {
            if (Time.realtimeSinceStartup < until) return false;
            throw new InvalidOperationException(localeFailure);
        }
        if (step == 21)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var root = Root(WaterScannerUIManager.Instance.panel);
            var gear = root.GetComponentsInChildren<Button>(true).FirstOrDefault(button => button.name == "DisplaySettings");
            Need(gear != null, "Missing scanner display settings"); gear.onClick.Invoke();
            Shot("display-settings"); until = Time.realtimeSinceStartup + 1; step = 210; return false;
        }
        if (step == 210)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var gear = Root(WaterScannerUIManager.Instance.panel).GetComponentsInChildren<Button>(true)
                .First(button => button.name == "DisplaySettings");
            Need(gear.GetComponent<Image>().sprite != null && gear.GetComponent<Image>().sprite == CustomUIManager.Instance.spriteSetting,
                "Scanner display settings did not use the native icon");
            SetRowSize(Root(WaterScannerUIManager.Instance.panel), 28);
            Shot("water-large-top"); until = Time.realtimeSinceStartup + 1; step = 211; return false;
        }
        if (step == 211)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var largeRoot = Root(WaterScannerUIManager.Instance.panel);
            Need(largeRoot != null && AllText(largeRoot).Contains("<0.01%"), "Large font lost trace percentage");
            var largeQuality = largeRoot.GetComponentsInChildren<TMP_Text>(true).First(text => text.name == "Tested");
            Need(Mathf.Approximately(largeQuality.fontSize, 14) && !largeQuality.enableAutoSizing,
                "The fixed native summary changed with the row font");
            foreach (var text in Row(largeRoot, "microbe").GetComponentsInChildren<TMP_Text>(true)
                .Where(text => text.name == "Name" || text.name == "Volume" || text.name == "Share"))
            {
                text.ForceMeshUpdate();
                Need(Mathf.Approximately(text.fontSize, 28) && !text.enableAutoSizing,
                    "Large scanner text was silently shrunk: " + text.text);
            }
            NeedHeader(largeRoot);
            var largeScroll = largeRoot.GetComponentsInChildren<ScrollRect>(true).First();
            Need(largeScroll.content.rect.height > largeScroll.viewport.rect.height && largeScroll.verticalScrollbar.gameObject.activeInHierarchy,
                "The scrollable large-font results did not show a scrollbar");
            largeScroll.verticalNormalizedPosition = 0; Canvas.ForceUpdateCanvases();
            Shot("water-large"); until = Time.realtimeSinceStartup + 1; step = 212; return false;
        }
        if (step == 212)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var largeRoot = Root(WaterScannerUIManager.Instance.panel);
            NeedHeader(largeRoot); NeedVisibleRow(largeRoot); step = 22; return false;
        }
        if (step == 22)
        {
            if (Time.realtimeSinceStartup < until) return false;
            SetRowSize(Root(WaterScannerUIManager.Instance.panel), 14);
            var root = Root(WaterScannerUIManager.Instance.panel);
            OpenDetail(root, "organic_waste");
            Shot("method-cards"); until = Time.realtimeSinceStartup + 1; step = 3; return false;
        }
        if (step == 3)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var root = Root(WaterScannerUIManager.Instance.panel); var detail = Detail();
            Need(root != null && detail != null && detail.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_water_filter") &&
                detail.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_purifier"), "Verified filter/purifier cards unavailable");
            Need(detail.GetComponentsInChildren<Image>(true).Any(i => i.sprite != null), "Native method sprite missing");
            Canvas.ForceUpdateCanvases();
            var detailScroll = detail.GetComponentsInChildren<ScrollRect>(true).FirstOrDefault();
            Need(detailScroll != null && (detailScroll.content.rect.height <= detailScroll.viewport.rect.height + 1 ||
                detailScroll.verticalNormalizedPosition > .99f), "Treatment details did not open at the first method");
            step = 31; return false;
        }
        if (step == 31)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var detail = CustomUIManager.Instance.GetWindow(Mod + ".f19.detail.water");
            Need(detail?.IsAlive == true, "Missing treatment window");
            var expand = detail.Get("method_more_purifier"); Need(expand?.button != null, "Missing treatment detail control");
            expand.button.onClick.Invoke();
            Need(detail.root.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text.Contains("99.15%")),
                "Treatment parameters did not expand");
            detail.Close();
            step = 4; return false;
        }
        if (step == 4)
        {
            var ui = WaterScannerUIManager.Instance; var root = Root(ui.panel);
            RoutingScenario.RunFeatureAction("f19.choose");
            var picker = CustomUIManager.Instance.GetWindow(Mod + ".f19.pin");
            var clear = picker?.root.GetComponentsInChildren<Button>(true).FirstOrDefault(button =>
                button.GetComponentInChildren<TMP_Text>(true)?.text == ModHelper.GetLocalized(Mod, "f19.none"));
            Need(clear != null, "Missing clear pins action"); clear.onClick.Invoke();
            CustomUIManager.Instance.GetWindow(Mod + ".f19.pin")?.Close();
            var pin = ButtonNamed(Row(root, "organic_waste"), "☆") ?? ButtonNamed(Row(root, "organic_waste"), "★");
            Need(pin != null, "Missing pin button");
            using (var input = new QaSyntheticInput()) { input.Mouse = new Vector3(-1000, -1000); pin.onClick.Invoke(); }
            root = Root(ui.panel);
            Transform organic = Row(root, "organic_waste"), water = Row(root, "water");
            Need(organic.GetSiblingIndex() < water.GetSiblingIndex(), "Pinned row did not move first");
            OpenDetail(root, "microbe");
            step = 41; return false;
        }
        if (step == 41)
        {
            var ui = WaterScannerUIManager.Instance; var detail = Detail();
            Need(detail != null && detail.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_uv") &&
                detail.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_water_tablet"),
                "Native UV or chlorine reaction method unavailable");
            ui.Close();
            Need(ui.contaminantTMP.enabled && ui.contaminantTMP.text == raw, "Closing did not restore original text and visibility");
            ui.Open(null, nonReactive);
            var root = Root(ui.panel); OpenDetail(root, "microbe");
            step = 42; return false;
        }
        if (step == 42)
        {
            var ui = WaterScannerUIManager.Instance; var detail = Detail();
            Need(detail != null && !detail.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_water_tablet") &&
                detail.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_uv"),
                "A container without the reaction callback advertised a chlorine reaction");
            ui.Close();
            ChemScannerUIManager.Instance.Open(null, sample); Shot("chem-layout");
            until = Time.realtimeSinceStartup + 1; step = 5; return false;
        }
        if (step == 5)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var ui = ChemScannerUIManager.Instance; var root = Root(ui.panel);
            Need(root != null && Row(root, "microbe") != null && !ui.productNameTMP.enabled && !ui.totalVolumeTMP.enabled &&
                root.GetComponentsInChildren<TMP_Text>(true).Any(text => text.name == "SampleTitle" && text.text == ui.productNameTMP.text),
                "Chemical scanner layout missing or product title was not retained");
            NeedHeader(root); NeedVisibleRow(root);
            step = 51; return false;
        }
        if (step == 51)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var ui = ChemScannerUIManager.Instance;
            var root = Root(ui.panel);
            chemicalRoot = root;
            chemicalBottom = ScreenRect((RectTransform)root).yMin;
            WaterHelper.AddLiquid(sample, "contraceptive", 1000); ui.RefreshUI();
            Shot("chem-effect"); until = Time.realtimeSinceStartup + 1; step = 511; return false;
        }
        if (step == 511)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var ui = ChemScannerUIManager.Instance;
            var root = Root(ui.panel);
            var effects = ui.panel.transform.Find("chemPropertyGroup");
            Need(effects != null && effects.GetComponentsInChildren<Transform>(false).Any(child =>
                child != effects && child.gameObject.activeInHierarchy), "Chemical effect fixture did not activate");
            Need(root != null && ReferenceEquals(root, chemicalRoot) && ScreenRect((RectTransform)root).yMin > chemicalBottom &&
                !ScreenRect((RectTransform)root).Overlaps(ScreenRect((RectTransform)effects)),
                "Chemical effect region was replaced or clipped by the scanner screen");
            ui.contaminantTMP.text = "external scanner writer";
            step = 6; return false;
        }
        if (step == 6)
        {
            var ui = ChemScannerUIManager.Instance;
            if (Root(ui.panel) != null) return false;
            Need(ui.contaminantTMP.enabled && ui.contaminantTMP.text == "external scanner writer" && ui.productNameTMP.enabled &&
                ui.testedTMP.enabled && ui.labelTMP.enabled && ui.totalVolumeTMP.enabled,
                "A later scanner writer was overwritten or native chemical fields were not restored");
            ui.RefreshUI(); Need(Root(ui.panel) != null, "Native refresh did not rebuild the current sample");
            ui.Close();
            WaterScannerUIManager.Instance.Open(null, empty);
            Need(Root(WaterScannerUIManager.Instance.panel) == null && WaterScannerUIManager.Instance.contaminantTMP.enabled,
                "Empty sample retained stale measurements");
            WaterScannerUIManager.Instance.Close(); until = Time.realtimeSinceStartup + 1; step++; return false;
        }
        if (Time.realtimeSinceStartup < until) return false;
        result = "PASS both scanner layouts, trace component, method sprites, pin, external writer, empty sample and close restoration"; return true;
    }
}

// Run the same real scanner presentation and interaction checks in English at 720p.
public sealed class ScannerScaleScenario
{
    private readonly ScannerPresentationScenario scenario = new ScannerPresentationScenario();
    private string originalSavedLocale, environmentFailure;
    private int stage, originalMode, originalResolution, originalDivisor;
    private bool displayChanged, localeSaved, localeKeyExisted;
    private float deadline = Time.realtimeSinceStartup + 150, stableAfter;
    private int stableFrame;

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch
        {
            try { Restore(); }
            catch (Exception error) { Debug.LogWarning("[PLAYER-QA] Could not restore scanner display: " + error.Message); }
            throw;
        }
    }

    private bool TickCore(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup >= deadline) throw new InvalidOperationException("Scanner 720p/English scenario timed out at stage " + stage +
            ": size=" + Screen.width + "x" + Screen.height + ", locale=" + LocHelper.GetCurrentLocaleCode());
        if (stage == 0)
        {
            var menu = MainMenuUIController.Instance;
            if (menu == null || menu.localeDropdown == null || menu.localeDropdown.options.Count == 0) return false;
            int english = menu.localeDropdown.options.FindIndex(option => option.text == "English");
            if (english < 0) throw new InvalidOperationException("Native main-menu English option unavailable");
            localeKeyExisted = ES3.KeyExists("locale");
            originalSavedLocale = localeKeyExisted ? ES3.LoadString("locale", "", null, null) : null;
            localeSaved = true;
            menu.localeDropdown.SetValueWithoutNotify(english);
            menu.OnLocaleChanged(english);
            stage = 1; return false;
        }
        if (stage == 1)
        {
            var selected = LocalizationSettings.SelectedLocaleAsync;
            if (!selected.IsValid() || !selected.IsDone || selected.Status != AsyncOperationStatus.Succeeded ||
                selected.Result == null || selected.Result.Identifier.Code != "en") return false;
            var initialization = LocalizationSettings.InitializationOperation;
            if (!initialization.IsValid() || !initialization.IsDone) return false;
            if (initialization.Status != AsyncOperationStatus.Succeeded)
                throw new InvalidOperationException("Main-menu English localization failed", initialization.OperationException);
            if (!scenario.ReadyForFixtures)
            {
                if (scenario.Tick(out _)) throw new InvalidOperationException("Scanner scenario completed before changing the display");
                return false;
            }
            if (!displayChanged && StoreUIManager.Instance?.gameOptionUIManager != null && EmporiumOption.Instance != null)
            {
                originalMode = EmporiumOption.Instance.fullscreenMode;
                originalResolution = EmporiumOption.Instance.resolution;
                originalDivisor = EmporiumOption.Instance.borderlessDivisor;
                int target = EmporiumOption.GetResolutionValueFromDimensions(1280, 720);
                if (target < 0) throw new InvalidOperationException("Native 1280x720 option unavailable");
                displayChanged = true;
                EmporiumOption.Instance.fullscreenMode = 2; EmporiumOption.Instance.resolution = target;
                EmporiumOption.Instance.ApplySetting();
                stableAfter = Time.realtimeSinceStartup + 2.5f; stableFrame = Time.frameCount + 12;
                stage = 2; return false;
            }
            return false;
        }
        if (stage == 2)
        {
            if (Screen.width != 1280 || Screen.height != 720 || Time.realtimeSinceStartup < stableAfter || Time.frameCount < stableFrame) return false;
            stage = 3;
        }
        if (stage == 4)
        {
            if (Time.realtimeSinceStartup < stableAfter) return false;
            throw new InvalidOperationException(environmentFailure);
        }
        string locale = LocHelper.GetCurrentLocaleCode();
        if (Screen.width != 1280 || Screen.height != 720 || locale != "en")
        {
            environmentFailure = "Scanner display or English locale changed: size=" + Screen.width + "x" + Screen.height +
                ", locale=" + LocHelper.GetCurrentLocaleCode() + ", savedLocale=" + ES3.LoadString("locale", "", null, null) +
                ", fullscreen=" + (EmporiumOption.Instance == null ? "missing" : EmporiumOption.Instance.fullscreenMode.ToString()) +
                ", resolution=" + (EmporiumOption.Instance == null ? "missing" : EmporiumOption.Instance.resolution.ToString());
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-scanner-en-1280x720-environment-failure.png"));
            stableAfter = Time.realtimeSinceStartup + 1; stage = 4; return false;
        }
        if (!scenario.Tick(out string detail)) return false;
        Restore();
        result = "PASS English at 1280x720: " + detail;
        return true;
    }

    private void Restore()
    {
        if (displayChanged && EmporiumOption.Instance != null)
        {
            displayChanged = false;
            EmporiumOption.Instance.fullscreenMode = originalMode; EmporiumOption.Instance.resolution = originalResolution;
            EmporiumOption.Instance.borderlessDivisor = originalDivisor; EmporiumOption.Instance.ApplySetting();
        }
        if (localeSaved)
        {
            localeSaved = false;
            if (localeKeyExisted) ES3.Save("locale", originalSavedLocale);
            else ES3.DeleteKey("locale");
        }
    }
}
