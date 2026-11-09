using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Shared capture plumbing for real, isolated Player gallery scenarios.
internal sealed class QaFeatureGallery
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private bool requested, ready, hadLocale, screenRequested;
    private string savedLocale, screenshot;
    private int readyFrame, shotFrame, screenFrame;
    private float readyTime, screenAfter;

    internal bool ChineseMainMenu()
    {
        if (!requested)
        {
            if (MainMenuUIController.Instance == null || string.IsNullOrEmpty(LocHelper.GetCurrentLocaleCode())) return false;
            hadLocale = ES3.KeyExists("locale");
            savedLocale = hadLocale ? ES3.LoadString("locale", "", null, null) : null;
            ES3.Save("locale", "zh");
            requested = true;
            if (LocHelper.GetCurrentLocaleCode() == "zh") ready = true;
            else MainMenuUIController.Instance.StartCoroutine(SetChinese());
            return false;
        }
        if (!ready || LocHelper.GetCurrentLocaleCode() != "zh") return false;
        if (readyTime == 0) { readyTime = Time.realtimeSinceStartup + .5f; readyFrame = Time.frameCount + 3; }
        return Time.realtimeSinceStartup >= readyTime && Time.frameCount >= readyFrame;
    }
    private IEnumerator SetChinese() { yield return LocHelper.SetLocale("zh"); ready = true; }
    internal void RestoreSavedLocale()
    {
        if (!requested) return;
        if (hadLocale) ES3.Save("locale", savedLocale); else ES3.DeleteKey("locale");
        requested = false;
    }
    internal bool Screen1920()
    {
        if (EmporiumOption.Instance == null) return false;
        if (!screenRequested)
        {
            if (StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(false, "f11.enabled", "f15.enabled", "f17.enabled", "f21.enabled");
            EmporiumOption.Instance.fullscreenMode = 2;
            EmporiumOption.Instance.resolution = 0;
            EmporiumOption.Instance.ApplySetting();
            screenRequested = true; screenFrame = Time.frameCount + 12; screenAfter = Time.realtimeSinceStartup + .5f;
        }
        return Time.frameCount >= screenFrame && Time.realtimeSinceStartup >= screenAfter && Screen.width == 1920 && Screen.height == 1080;
    }
    internal void Capture(string name)
    {
        Need(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "Gallery requires the rendered Player");
        Canvas.ForceUpdateCanvases();
        screenshot = Path.Combine(Application.persistentDataPath, "gallery-" + name + ".png");
        ScreenCapture.CaptureScreenshot(screenshot);
        Debug.Log("[PLAYER-QA] gallery capture " + screenshot + " " + Screen.width + "x" + Screen.height);
        shotFrame = Time.frameCount + 5;
    }
    internal bool CaptureReady() => Time.frameCount >= shotFrame && screenshot != null && File.Exists(screenshot) && new FileInfo(screenshot).Length > 0;
    internal static void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
    internal static CustomUIWindow Window(string name) => CustomUIManager.Instance.GetWindow(Mod + "." + name);
    internal static bool NoticeVisible()
    {
        var mod = ModLoader.Mods.First(value => value.Manifest?.ID == Mod).Instance;
        Type type = mod.GetType().Assembly.GetType("ShopAssist.ShopNotice", true);
        var panel = type.GetField("panel", Flags)?.GetValue(null) as GameObject;
        return panel != null && panel.activeInHierarchy;
    }
    internal static bool CanRun(string key)
    {
        var mod = ModLoader.Mods.First(value => value.Manifest?.ID == Mod).Instance;
        Type type = mod.GetType().Assembly.GetType("ShopAssist.ShopSettings", true);
        object settings = type.GetField("attached", Flags).GetValue(null);
        MethodInfo available = type.GetMethod("CanRun", Flags);
        return settings != null && available != null && (bool)available.Invoke(settings, new object[] { key });
    }
    internal static void Action(string key)
    {
        var mod = ModLoader.Mods.First(value => value.Manifest?.ID == Mod).Instance;
        Type type = mod.GetType().Assembly.GetType("ShopAssist.ShopSettings", true);
        object settings = type.GetField("attached", Flags).GetValue(null);
        MethodInfo run = type.GetMethod("Run", Flags);
        Need(settings != null && run != null && CanRun(key),
            "Gallery action unavailable: " + key);
        run.Invoke(settings, new object[] { key });
    }
    internal static GameItemElement Item(string id, GameInventory inventory, string name = null)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement;
        Need(item != null, "Missing native gallery item " + id);
        GeneralHelper.SetItemOwned(item, true);
        if (!string.IsNullOrEmpty(name)) GeneralHelper.SetCustomName(item, name);
        Put(item, inventory);
        return item;
    }
    internal static void Put(GameItem item, GameInventory inventory)
    {
        var marker = inventory.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == inventory && marker.targetItem == null && marker.numTransfer >= item.unitCount &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, inventory),
            "Could not place native gallery item " + item.identifier);
    }
    internal static object Field(object target, string name) => target?.GetType().GetField(name, Flags)?.GetValue(target);
    internal static object Invoke(object target, string name, params object[] args)
    {
        MethodInfo method = target?.GetType().GetMethod(name, Flags);
        Need(method != null, "Gallery fixture method missing: " + name);
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    internal static bool Visible(Transform node) => node != null && node.gameObject.activeInHierarchy;
}

public abstract class ChineseCaptureScenario
{
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private bool ready;

    protected abstract bool Run(out string result);

    public bool Tick(out string result)
    {
        result = null;
        if (!ready && !(ready = gallery.ChineseMainMenu())) return false;
        if (!Run(out result)) return false;
        gallery.RestoreSavedLocale();
        return true;
    }
}

public sealed class ChineseArrangeCaptureScenario : ChineseCaptureScenario
{
    private readonly ArrangeInputScenario scenario = new ArrangeInputScenario();
    protected override bool Run(out string result) => scenario.Tick(out result);
}

public sealed class ChineseInspectionCaptureScenario : ChineseCaptureScenario
{
    private readonly DesktopScenario scenario = new DesktopScenario();
    private QaSyntheticInput input;

    protected override bool Run(out string result)
    {
        if (input == null) input = new QaSyntheticInput();
        input.Mouse = new Vector3(Screen.width * .7f, Screen.height * .55f);
        if (!scenario.Tick(out result)) return false;
        input.Dispose();
        return true;
    }
}

public sealed class ChineseScannerCaptureScenario : ChineseCaptureScenario
{
    private readonly ScannerPointerScenario scenario = new ScannerPointerScenario();
    protected override bool Run(out string result) => scenario.Tick(out result);
}

public sealed class ChinesePlaybackCaptureScenario : ChineseCaptureScenario
{
    private readonly FastPlaybackInputScenario scenario = new FastPlaybackInputScenario();
    protected override bool Run(out string result) => scenario.Tick(out result);
}

public sealed class ChinesePeekCaptureScenario : ChineseCaptureScenario
{
    private readonly DailyPeekInputScenario scenario = new DailyPeekInputScenario();
    protected override bool Run(out string result) => scenario.Tick(out result);
}

public sealed class ChinesePeekSmallCaptureScenario : ChineseCaptureScenario
{
    private readonly DailyPeekInputScenario scenario = new DailyPeekInputScenario();
    private bool changed;
    private int originalMode, originalResolution, originalDivisor, frame;
    private float readyAt;

    protected override bool Run(out string result)
    {
        result = null;
        if (!changed)
        {
            if (!scenario.ReadyForFixtures) return scenario.Tick(out result);
            if (EmporiumOption.Instance == null) return false;
            int target = EmporiumOption.GetResolutionValueFromDimensions(1280, 720);
            QaFeatureGallery.Need(target >= 0, "Native 1280x720 option unavailable");
            originalMode = EmporiumOption.Instance.fullscreenMode;
            originalResolution = EmporiumOption.Instance.resolution;
            originalDivisor = EmporiumOption.Instance.borderlessDivisor;
            EmporiumOption.Instance.fullscreenMode = 2;
            EmporiumOption.Instance.resolution = target;
            EmporiumOption.Instance.ApplySetting();
            frame = Time.frameCount + 12; readyAt = Time.realtimeSinceStartup + 2.5f; changed = true;
            return false;
        }
        if (Time.frameCount < frame || Time.realtimeSinceStartup < readyAt || Screen.width != 1280 || Screen.height != 720) return false;
        if (!scenario.Tick(out result)) return false;
        EmporiumOption.Instance.fullscreenMode = originalMode;
        EmporiumOption.Instance.resolution = originalResolution;
        EmporiumOption.Instance.borderlessDivisor = originalDivisor;
        EmporiumOption.Instance.ApplySetting();
        return true;
    }
}

public sealed class ChineseStockCaptureScenario : ChineseCaptureScenario
{
    private readonly StockSearchScenario scenario = new StockSearchScenario();
    protected override bool Run(out string result) => scenario.Tick(out result);
}
