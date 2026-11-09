using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Runs only in the guarded Player harness, against its isolated QA save.
public sealed class DesktopScenario
{
    private const string ModId = "starswhere.shopassist";
    private readonly FieldInfo multiplier = typeof(CounterMoveChild).GetField("movementMultiplier", BindingFlags.Instance | BindingFlags.NonPublic);
    private QaLoadSlot load;
    private GameItem inspectItem;
    private Transform art, lens, tray;
    private Vector3 baseScale, basePosition;
    private float baseMultiplier, frameAlpha, trayAlpha;
    private Vector2 centerSource;
    private GameObject centerHit;
    private float leftOffset, rightOffset;
    private long cashBeforeClose;
    private int step;
    private float stepStarted = Time.realtimeSinceStartup;
    private int waitFrame;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - stepStarted > 45f)
            throw new InvalidOperationException("Desktop QA timed out at step " + step);
        switch (step)
        {
            case 0:
            {
                if (MainMenuUIController.Instance == null) return false;
                string[] source = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-latest-run.txt")).Trim().Split('|');
                Require(source.Length == 4 && source[0] == "1" && Guid.TryParseExact(source[1], "N", out _) &&
                    int.TryParse(source[2], out int slot) && slot >= 0, "Invalid isolated QA run context");
                load = new QaLoadSlot(int.Parse(source[2]), Guid.ParseExact(source[1], "N"));
                Next(1, "Loading isolated F14/F18 QA save"); return false;
            }
            case 1:
            {
                if (!load.Tick() || !StoreReady()) return false;
                CloseCalendar();
                if (DialogUIManager.Instance?.currentDialog != null || DialoguePhoneUIManager.Instance?.currentDialog != null) return false;
                var options = Options();
                options.Get(ModId + ":f14.enabled").SetBool(true, true);
                options.Get(ModId + ":f14.side").SetIndex(1, true);
                options.Get(ModId + ":f18.enabled").SetBool(true, true);
                options.Get(ModId + ":f18.zoom").SetFloat(100, true);
                options.Get(ModId + ":f18.frame").SetFloat(100, true);
                options.Get(ModId + ":f18.tray").SetFloat(100, true);
                options.Get(ModId + ":f18.hide_lens").SetBool(false, true);
                Apply(options);
                StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                inspectItem = PreBuiltItemHelper.CounterfeitCigarette();
                Require(inspectItem != null && InspectionUIManager.Instance != null, "Native inspection fixture unavailable");
                InspectionUIManager.Instance.OnInspectItem(inspectItem);
                var ui = InspectionUIManager.Instance;
                Require(ui.panel.activeInHierarchy && ReferenceEquals(ui.currentInspectingItem, inspectItem), "Native inspection did not open");
                lens = ui.magnifyingGlass.transform; tray = ui.blockerTray.transform; art = ui.cigaretteUILarge.transform;
                Require(multiplier != null && art.GetComponent<CounterMoveChild>() != null && tray.parent.GetComponent<Mask>() != null,
                    "Native inspection hierarchy changed");
                baseScale = art.localScale; basePosition = art.localPosition;
                baseMultiplier = (float)multiplier.GetValue(art.GetComponent<CounterMoveChild>());
                frameAlpha = lens.GetComponent<Image>().color.a; trayAlpha = tray.GetComponent<Image>().color.a;
                waitFrame = Time.frameCount + 3;
                Next(4, "Enabled desktop features and opened native inspection"); return false;
            }
            case 4:
            {
                if (Time.frameCount < waitFrame) return false;
                Require(Near(art.localScale, baseScale), "Inspection baseline changed unexpectedly");
                centerSource = SourceAtMaskCenter();
                centerHit = ArtHitAtMaskCenter();
                var options = Options();
                options.Get(ModId + ":f18.zoom").SetFloat(200, true);
                options.Get(ModId + ":f18.frame").SetFloat(50, true);
                options.Get(ModId + ":f18.tray").SetFloat(40, true);
                Apply(options); StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                waitFrame = Time.frameCount + 3;
                Next(5, "Applied F18 zoom and opacity through native options"); return false;
            }
            case 5:
            {
                if (Time.frameCount < waitFrame) return false;
                Require(Near(art.localScale.x, baseScale.x * 2) && Near(art.localScale.y, baseScale.y * 2) &&
                    Near((float)multiplier.GetValue(art.GetComponent<CounterMoveChild>()), baseMultiplier * 2), "F18 zoom or native counter-move mapping incorrect");
                Require(Near(lens.GetComponent<Image>().color.a, frameAlpha * 0.5f) && Near(tray.GetComponent<Image>().color.a, trayAlpha * 0.4f),
                    "F18 opacity was not applied to the actual lens and tray");
                Require(tray.parent.GetComponent<Mask>().enabled && Near(SourceAtMaskCenter(), centerSource), "F18 moved the content under the lens center or disabled the native mask");
                if (centerHit != null) Require(ArtHitAtMaskCenter() == centerHit, "F18 changed the hit target at the lens center");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                    ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-desktop-inspection.png"));
                InspectionUIManager.Instance.CloseUI();
                Require(Near(art.localScale, baseScale) && Near(art.localPosition, basePosition) &&
                    Near((float)multiplier.GetValue(art.GetComponent<CounterMoveChild>()), baseMultiplier) &&
                    Near(lens.GetComponent<Image>().color.a, frameAlpha) && Near(tray.GetComponent<Image>().color.a, trayAlpha),
                    "F18 did not restore native inspection components on close");
                InspectionUIManager.Instance.OnInspectItem(inspectItem);
                waitFrame = Time.frameCount + 3;
                Next(6, "F18 preserved center mapping and restored the native view on close"); return false;
            }
            case 6:
            {
                if (Time.frameCount < waitFrame) return false;
                Require(Near(art.localScale.x, baseScale.x * 2), "F18 did not reapply after reopening inspection");
                var options = Options(); options.Get(ModId + ":f18.hide_lens").SetBool(true, true); Apply(options);
                StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                waitFrame = Time.frameCount + 3;
                Next(60, "Enabled the F18 hide-lens option"); return false;
            }
            case 60:
            {
                if (Time.frameCount < waitFrame) return false;
                Require(Near(lens.GetComponent<Image>().color.a, 0) && tray.parent.GetComponent<Mask>().enabled &&
                    art.gameObject.activeInHierarchy, "F18 did not hide the lens while preserving the inspection image");
                RoutingScenario.RunFeatureAction("f18.reset");
                Require(!((ConfigEntry<bool>)ModConfig.Find(ModId).Get("f18.hide_lens")).Value,
                    "F18 reset retained the hide-lens setting");
                waitFrame = Time.frameCount + 3;
                Next(61, "Reset the hidden lens through the feature action"); return false;
            }
            case 61:
            {
                if (Time.frameCount < waitFrame) return false;
                Require(Near(art.localScale, baseScale) && Near(lens.GetComponent<Image>().color.a, frameAlpha) &&
                    Near(tray.GetComponent<Image>().color.a, trayAlpha), "F18 reset did not restore the native view");
                var options = Options(); options.Get(ModId + ":f18.enabled").SetBool(false, true); Apply(options);
                StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                waitFrame = Time.frameCount + 3;
                Next(7, "Disabled F18 after resetting the native view"); return false;
            }
            case 7:
            {
                if (Time.frameCount < waitFrame) return false;
                Require(Near(art.localScale, baseScale) && Near((float)multiplier.GetValue(art.GetComponent<CounterMoveChild>()), baseMultiplier) &&
                    Near(lens.GetComponent<Image>().color.a, frameAlpha) && Near(tray.GetComponent<Image>().color.a, trayAlpha),
                    "F18 disable did not restore native inspection components");
                InspectionUIManager.Instance.CloseUI();
                if (PlayerStore.Instance.storeState == PlayerStore.StoreState.MORNING)
                {
                    Require(StoreShutterButton.Instance != null && PlayerStore.Instance.CanStartDay(), "Native QA shutter cannot open");
                    StoreShutterButton.Instance.OnClick();
                }
                Require(PlayerStore.Instance.isBargainIntroduced, "QA save has not unlocked native bargaining");
                if (PlayerStore.Instance.currentClientInstance == null)
                {
                    var client = StoreClientList.CreateSpacerClientFood();
                    PlayerStore.Instance.storeClientManager.AddNextClient(client);
                    PlayerStore.Instance.GetNextClient();
                    Require(ReferenceEquals(PlayerStore.Instance.currentClientInstance?.GetClientBlueprint(), client), "Native QA customer did not arrive");
                }
                Require(PlayerStore.Instance.currentClientInstance.GetClientBlueprint().isBargainAvailable, "QA customer does not allow bargaining");
                NegociationUIManager.Instance.OpenUI();
                var tablet = NegociationUIManager.Instance.uiObject.GetComponent<NegotiationTabletFrame>();
                Require(tablet != null && tablet.bargainButton.gameObject.activeInHierarchy, "Native bargain button unavailable");
                tablet.bargainButton.onClick.Invoke();
                Require(BargainUIManager.Instance.IsOpen, "Native bargain button did not open the panel");
                waitFrame = Time.frameCount + 3;
                Next(8, "Opened the native bargaining panel for an isolated QA customer"); return false;
            }
            case 8:
            {
                if (Time.frameCount < waitFrame) return false;
                Require(BargainUIManager.Instance.IsOpen, "Native bargaining panel closed unexpectedly");
                leftOffset = BargainOffset();
                var options = Options(); options.Get(ModId + ":f14.side").SetIndex(2, true); Apply(options);
                StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                waitFrame = Time.frameCount + 3;
                Next(9, "Applied F14 left docking and selected right docking"); return false;
            }
            case 9:
            {
                if (Time.frameCount < waitFrame) return false;
                Require(BargainUIManager.Instance.IsOpen, "Native bargaining panel closed before right docking");
                rightOffset = BargainOffset();
                Require(rightOffset > leftOffset + 1f, "F14 left and right did not produce distinct ordered panel positions: " + leftOffset + " / " + rightOffset);
                var options = Options(); options.Get(ModId + ":f14.side").SetIndex(0, true); Apply(options);
                StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                waitFrame = Time.frameCount + 3;
                Next(10, "F14 left and right docking changed the actual panel position"); return false;
            }
            case 10:
            {
                if (Time.frameCount < waitFrame) return false;
                var bargain = BargainUIManager.Instance;
                Require(bargain.IsOpen && !float.IsNaN(BargainOffset()), "F14 automatic docking failed");
                Transform close = FindChild(bargain.uiObject.transform, "ShopAssist Bargain Close");
                Require(close != null && EventSystem.current != null, "F14 close control missing");
                var closeRect = (RectTransform)close;
                Camera camera = CanvasCamera(closeRect);
                Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, closeRect.TransformPoint(closeRect.rect.center));
                var hit = new List<RaycastResult>();
                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hit);
                Require(hit.Count > 0 && (hit[0].gameObject == close.gameObject || hit[0].gameObject.transform.IsChildOf(close)),
                    "F14 close control is not the topmost native raycast target: point=" + point + ", screen=" + Screen.width + "x" + Screen.height +
                    ", close=" + closeRect.position + ", canvas=" + closeRect.GetComponentInParent<Canvas>()?.name + ", hits=" +
                    string.Join("; ", hit.Take(8).Select(h => h.gameObject == null ? "<null>" : PathOf(h.gameObject.transform))));
                cashBeforeClose = PlayerStore.Instance.playerCash;
                Pointer(close.gameObject, point, ExecuteEvents.pointerDownHandler);
                Pointer(close.gameObject, point, ExecuteEvents.pointerUpHandler);
                Pointer(close.gameObject, point, ExecuteEvents.pointerClickHandler);
                Require(bargain.IsOpen, "F14 closed before the pointer release was consumed");
                waitFrame = Time.frameCount + 2;
                Next(11, "F14 close consumed pointer down/up/click at the real raycast target"); return false;
            }
            case 11:
                if (Time.frameCount < waitFrame) return false;
                Require(!BargainUIManager.Instance.IsOpen && PlayerStore.Instance.playerCash == cashBeforeClose,
                    "F14 close did not close exactly the panel or changed cash");
                result = "PASS F18 zoom/opacity/center mapping/close/reopen/hide/reset/disable; F14 left/right/auto and consumed close pointer without a cash change";
                return true;
            default: throw new InvalidOperationException("Unknown Desktop QA step " + step);
        }
    }

    private static bool StoreReady() => PlayerStore.HasInstance && EmporiumEntry.Instance != null &&
        EmporiumEntry.Instance.storeInteractables != null && EmporiumEntry.Instance.storeInteractables.activeInHierarchy &&
        StoreUIManager.Instance != null && StoreUIManager.Instance.gameOptionUIManager != null && CustomUIManager.Instance != null;
    private static void CloseCalendar()
    {
        var calendar = AdvCalendarUIManager.Instance;
        if (calendar?.UIPanel != null && calendar.UIPanel.activeSelf && StoreUIManager.Instance.storeCalendar != null)
            StoreUIManager.Instance.storeCalendar.OnClick();
    }
    private static CustomUIWindow Options()
    {
        StoreUIManager.Instance.gameOptionUIManager.OpenUI();
        var options = CustomUIManager.Instance.GetWindow("game_options");
        Require(options != null && options.IsVisible, "Native options window unavailable");
        return options;
    }
    private static void Apply(CustomUIWindow options) => options.Get("apply").button.onClick.Invoke();
    private Vector2 SourceAtMaskCenter()
    {
        var mask = (RectTransform)tray.parent;
        var content = (RectTransform)art;
        Camera camera = CanvasCamera(mask);
        Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, mask.TransformPoint(mask.rect.center));
        Require(RectTransformUtility.ScreenPointToLocalPointInRectangle(content, point, camera, out Vector2 local), "Cannot map lens center to native art");
        return local;
    }
    private GameObject ArtHitAtMaskCenter()
    {
        var mask = (RectTransform)tray.parent;
        var results = new List<RaycastResult>();
        var pointer = new PointerEventData(EventSystem.current) { position = RectTransformUtility.WorldToScreenPoint(CanvasCamera(mask), mask.TransformPoint(mask.rect.center)) };
        EventSystem.current.RaycastAll(pointer, results);
        return results.Select(r => r.gameObject).FirstOrDefault(go => go != null && (go.transform == art || go.transform.IsChildOf(art)));
    }
    private static Camera CanvasCamera(RectTransform rect)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
    }
    private static float BargainOffset()
    {
        var tablet = NegociationUIManager.Instance.uiObject.transform;
        return tablet.InverseTransformPoint(BargainUIManager.Instance.uiObject.transform.position).x;
    }
    private static Transform FindChild(Transform root, string name) => root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
    private static string PathOf(Transform value)
    {
        var path = new List<string>();
        for (; value != null; value = value.parent) path.Add(value.name);
        path.Reverse(); return string.Join("/", path);
    }
    private static void Pointer<T>(GameObject target, Vector2 point, ExecuteEvents.EventFunction<T> handler) where T : IEventSystemHandler
    {
        var pointer = new PointerEventData(EventSystem.current) { position = point, pointerId = -1, button = PointerEventData.InputButton.Left };
        ExecuteEvents.Execute(target, pointer, handler);
        Require(pointer.used, "F14 did not consume " + typeof(T).Name);
    }
    private static bool Near(float a, float b) => Mathf.Abs(a - b) <= 0.02f;
    private static bool Near(Vector2 a, Vector2 b) => (a - b).sqrMagnitude <= 0.04f;
    private static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude <= 0.04f;
    private void Next(int next, string message) { Debug.Log("[PLAYER-QA] " + message); step = next; stepStarted = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
