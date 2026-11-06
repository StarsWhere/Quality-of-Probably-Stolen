using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Real EventSystem clicks in the isolated QA Player, with no operating-system input.
public sealed class ScannerPointerScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private GameItem sample;
    private ConfigEntry<bool> enabled;
    private ConfigEntry<int> fontSize;
    private ConfigEntry<string> pins;
    private bool oldEnabled, saved, initiallyPinned;
    private int oldFontSize, step, phase;
    private string oldPins;
    private QaSyntheticInput input;
    private EventSystem focusSystem;
    private bool originalFocus;
    private Button target;
    private PointerSpy targetSpy, rowSpy;

    private sealed class PointerSpy : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        internal int Down, Up, Click;
        public void OnPointerDown(PointerEventData data) { Down++; }
        public void OnPointerUp(PointerEventData data) { Up++; }
        public void OnPointerClick(PointerEventData data) { Click++; }
    }

    private static void Need(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static Transform Root() => WaterScannerUIManager.Instance?.panel?.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(node => node.name == "ShopAssist_LiquidBars" && node.gameObject.activeInHierarchy);
    private static Transform Row(Transform root) => root?.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(node => node.name == "Component_organic_waste" && node.gameObject.activeInHierarchy);
    private static Button Pin(Transform row) => row?.GetComponentsInChildren<Button>(true)
        .FirstOrDefault(button => button.name == "Button_☆" || button.name == "Button_★");
    private static CustomUIWindow Detail() => CustomUIManager.Instance.GetWindow(Mod + ".f19.detail.water");
    private static CustomUIWindow Settings() => CustomUIManager.Instance.GetWindow(Mod + ".f19.display.water");
    private static bool Visible(CustomUIWindow window) => window?.IsAlive == true && window.root.activeInHierarchy;

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch
        {
            try { Cleanup(); }
            catch (Exception error) { Debug.LogWarning("[PLAYER-QA] Scanner pointer cleanup failed: " + error); }
            throw;
        }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Scanner pointer scenario timed out at step " + step + " phase " + phase);
        if (step == 0) { if (start.Tick()) step = 1; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                WaterScannerUIManager.Instance == null || EventSystem.current == null) return false;
            var config = ModConfig.Find(Mod);
            enabled = config?.Get("f19.enabled") as ConfigEntry<bool>;
            fontSize = config?.Get("f19.size") as ConfigEntry<int>;
            pins = config?.Get("f19.pinned") as ConfigEntry<string>;
            Need(enabled != null && fontSize != null && pins != null, "Scanner configuration is unavailable");
            oldEnabled = enabled.Value; oldFontSize = fontSize.Value; oldPins = pins.Value; saved = true;
            enabled.Set(true); fontSize.Set(14);
            sample = DirectoryMaster.Item("wine_bottle"); Need(sample != null, "Scanner sample is unavailable");
            GeneralHelper.SetItemOwned(sample);
            var slot = EmporiumEntry.Instance.invElement.TryFindOneValidInventorySlot(sample);
            Need(slot != null && slot.TryAcceptOnce(sample.unitCount) == sample.unitCount, "Cannot place scanner sample");
            WaterHelper.Remove(sample, WaterHelper.GetCurrentCapacityUL(sample));
            WaterHelper.AddLiquid(sample, "water", 500000); WaterHelper.AddLiquid(sample, "organic_waste", 100000);
            WaterScannerUIManager.Instance.Open(null, sample); step = 2; return false;
        }
        if (step == 2)
        {
            Transform root = Root(), row = Row(root); Button pin = Pin(row);
            if (root == null || row == null || pin == null) return false;
            Need(!Visible(Detail()) && !Visible(Settings()), "Scanner opened an unrelated detail or settings window");
            input = new QaSyntheticInput(true, true);
            focusSystem = EventSystem.current; originalFocus = focusSystem.isFocused;
            if (!originalFocus)
            {
                MethodInfo focus = typeof(EventSystem).GetMethod("OnApplicationFocus", Any);
                Need(focus != null, "EventSystem focus callback is unavailable"); focus.Invoke(focusSystem, new object[] { true });
            }
            initiallyPinned = pin.name == "Button_★";
            Begin(pin, row.GetComponent<Button>(), .5f); step = 3; return false;
        }
        if (step == 3)
        {
            if (!Click()) return false;
            bool nowPinned = Pin(Row(Root()))?.name == "Button_★";
            Need(nowPinned != initiallyPinned && !Visible(Detail()) && !Visible(Settings()),
                "Pin click did not toggle only the pin, or opened a window");
            Need((pins.Value ?? "").Contains("\"organic_waste\"") == nowPinned,
                "Pin click did not persist the selected component");
            if (!nowPinned)
            {
                Button pin = Pin(Row(Root()));
                Begin(pin, Row(Root()).GetComponent<Button>(), .5f); step = 4; return false;
            }
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-scanner-pointer-pin.png"));
            step = 5; return false;
        }
        if (step == 4)
        {
            if (!Click()) return false;
            Need(Pin(Row(Root()))?.name == "Button_★" && (pins.Value ?? "").Contains("\"organic_waste\"") &&
                !Visible(Detail()) && !Visible(Settings()), "Second pin click did not pin only the chosen component");
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-scanner-pointer-pin.png"));
            step = 5; return false;
        }
        if (step == 5)
        {
            Button row = Row(Root())?.GetComponent<Button>();
            Need(row != null, "Component row disappeared after pinning");
            Begin(row, null, .55f); step = 6; return false;
        }
        if (step == 6)
        {
            if (!Click()) return false;
            Need(Visible(Detail()) && !Visible(Settings()), "Component row did not open its detail window");
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-scanner-pointer-detail.png"));
            step = 7; return false;
        }
        if (step == 7)
        {
            Button gear = Root()?.GetComponentsInChildren<Button>(true)
                .FirstOrDefault(button => button.name == "DisplaySettings");
            Need(gear != null, "Scanner settings button disappeared");
            Begin(gear, null, .5f); step = 8; return false;
        }
        if (step == 8)
        {
            if (!Click()) return false;
            Need(Visible(Settings()) && !Visible(Detail()), "Settings click did not replace the detail window");
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-scanner-pointer-settings.png"));
            step = 9; return false;
        }
        if (step == 9)
        {
            Cleanup();
            result = "PASS real EventSystem pin, component and settings clicks with raycast and down/up/click verification";
            return true;
        }
        throw new InvalidOperationException("Unknown scanner pointer step " + step);
    }

    private void Begin(Button button, Button parentRow, float horizontal)
    {
        Need(button != null && button.interactable && button.gameObject.activeInHierarchy, "Pointer target is unavailable");
        Canvas.ForceUpdateCanvases();
        RectTransform rect = (RectTransform)button.transform;
        Vector3 point = new Vector3(Mathf.Lerp(rect.rect.xMin, rect.rect.xMax, horizontal), rect.rect.center.y, 0);
        Canvas canvas = rect.GetComponentInParent<Canvas>()?.rootCanvas;
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        input.Mouse = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(point));
        target = button; phase = 0;
        targetSpy = button.gameObject.AddComponent<PointerSpy>();
        if (parentRow != null) rowSpy = parentRow.gameObject.AddComponent<PointerSpy>();
        CheckRaycast();
    }

    private void CheckRaycast()
    {
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = input.Mouse }, hits);
        Button hit = hits.Count == 0 ? null : hits[0].gameObject.GetComponentInParent<Button>();
        Need(hit == target, "Scanner pointer raycast missed " + target.name + "; first hit=" +
            (hits.Count == 0 ? "none" : hits[0].gameObject.name) + " point=" + input.Mouse);
    }

    private bool Click()
    {
        if (phase == 0) { CheckRaycast(); input.MouseDown = input.MouseHeld = 0; phase++; return false; }
        if (phase == 1) { input.MouseDown = input.MouseHeld = -1; input.MouseUp = 0; phase++; return false; }
        if (phase == 2) { input.MouseUp = -1; phase++; return false; }
        Need(targetSpy != null && targetSpy.Down > 0 && targetSpy.Up > 0 && targetSpy.Click > 0,
            "Scanner target did not receive pointer down/up/click: " + target?.name + " " +
            (targetSpy == null ? "missing" : targetSpy.Down + "/" + targetSpy.Up + "/" + targetSpy.Click));
        Need(rowSpy == null || rowSpy.Click == 0, "Pin click also reached its parent component row");
        if (targetSpy != null) UnityEngine.Object.Destroy(targetSpy);
        if (rowSpy != null) UnityEngine.Object.Destroy(rowSpy);
        targetSpy = rowSpy = null; target = null;
        return true;
    }

    private void Cleanup()
    {
        if (targetSpy != null) UnityEngine.Object.Destroy(targetSpy);
        if (rowSpy != null) UnityEngine.Object.Destroy(rowSpy);
        input?.Dispose(); input = null;
        if (focusSystem != null && !originalFocus)
            typeof(EventSystem).GetMethod("OnApplicationFocus", Any)?.Invoke(focusSystem, new object[] { false });
        focusSystem = null;
        if (Visible(Settings())) Settings().Close();
        if (Visible(Detail())) Detail().Close();
        if (WaterScannerUIManager.Instance?.currentWaterContainer == sample && sample != null)
            WaterScannerUIManager.Instance.Close();
        if (saved)
        {
            saved = false;
            pins.Set(oldPins); fontSize.Set(oldFontSize); enabled.Set(oldEnabled);
        }
    }
}
