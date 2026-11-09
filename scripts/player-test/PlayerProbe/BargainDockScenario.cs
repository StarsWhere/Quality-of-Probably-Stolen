using System;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Uses a fresh run in the isolated player-test profile.
public sealed class BargainDockScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private int step;
    private float after;
    private int cash;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup > deadline) throw new InvalidOperationException("Bargain dock QA timed out at " + step);
        if (step == 0)
        {
            if (!start.Tick()) return false;
            TradeScenario.SetOptions(true, "f14.enabled", "f34.enabled");
            var p = PlayerStore.Instance;
            p.isBargainIntroduced = true;
            var buyer = StoreClientList.CreateSpacerClientFood();
            buyer.isIntroduced = true;
            buyer.isBargainAvailable = true;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer);
            p.isClientArrived = true;
            var options = StoreUIManager.Instance.gameOptionUIManager;
            options.OpenUI();
            var window = CustomUIManager.Instance.GetWindow("game_options");
            window.Get(Mod + ":f14.side").SetIndex(1, true);
            window.Get("apply").button.onClick.Invoke();
            options.CloseUI();
            NegociationUIManager.Instance.OpenUI();
            var tablet = NegociationUIManager.Instance.uiObject.GetComponent<NegotiationTabletFrame>();
            if (tablet?.bargainButton == null || !tablet.bargainButton.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Native bargaining control unavailable");
            tablet.bargainButton.onClick.Invoke();
            if (!BargainUIManager.Instance.IsOpen) throw new InvalidOperationException("Native bargain panel did not open");
            after = Time.realtimeSinceStartup + 1f;
            step = 1;
            return false;
        }
        if (Time.realtimeSinceStartup < after) return false;
        var panel = BargainUIManager.Instance.uiObject;
        var frame = panel.GetComponent<BargainPanelFrame>();
        var tabletFrame = NegociationUIManager.Instance.uiObject.GetComponent<NegotiationTabletFrame>();
        if (step == 1)
        {
            CheckChrome(frame, tabletFrame, true);
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-bargain-left.png"));
            after = Time.realtimeSinceStartup + .5f; step = 2; return false;
        }
        if (step == 2)
        {
            var options = StoreUIManager.Instance.gameOptionUIManager;
            options.OpenUI();
            var window = CustomUIManager.Instance.GetWindow("game_options");
            window.Get(Mod + ":f14.side").SetIndex(2, true);
            window.Get("apply").button.onClick.Invoke(); options.CloseUI();
            after = Time.realtimeSinceStartup + .5f; step = 3; return false;
        }
        if (step == 3)
        {
            CheckChrome(frame, tabletFrame, false);
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-bargain-right.png"));
            after = Time.realtimeSinceStartup + .5f; step = 4; return false;
        }
        if (step == 4)
        {
            var close = frame.panel.Find("ShopAssist Bargain Close");
            if (close == null || EventSystem.current == null) throw new InvalidOperationException("Bargain close control is missing");
            cash = PlayerStore.Instance.playerCash;
            var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, pointerId = -1 };
            ExecuteEvents.Execute(close.gameObject, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(close.gameObject, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(close.gameObject, data, ExecuteEvents.pointerClickHandler);
            if (!BargainUIManager.Instance.IsOpen) throw new InvalidOperationException("Bargain panel closed before pointer release was consumed");
            after = Time.realtimeSinceStartup + .2f; step = 5; return false;
        }
        if (BargainUIManager.Instance.IsOpen || PlayerStore.Instance.playerCash != cash)
            throw new InvalidOperationException("Bargain close changed cash or failed to close");
        result = "PASS left/right native chrome symmetry and native-style close"; return true;
    }

    private static void CheckChrome(BargainPanelFrame frame, NegotiationTabletFrame tablet, bool left)
    {
        var root = frame?.panel;
        var plate = root?.Find("Plate") as RectTransform;
        var spine = root?.Find("Spine") as RectTransform;
        var close = root?.Find("ShopAssist Bargain Close") as RectTransform;
        if (root == null || plate == null || spine == null || close == null || tablet?.closeButton == null)
            throw new InvalidOperationException("Native bargain chrome is unavailable");
        Rect p = Bounds(root, plate), s = Bounds(root, spine), c = Bounds(root, close);
        if ((left ? s.center.x >= p.center.x : s.center.x <= p.center.x) ||
            (left ? spine.localScale.x >= 0 : spine.localScale.x <= 0))
            throw new InvalidOperationException("Bargain spine was not mirrored to the outer side");
        if (c.xMin < p.xMin || c.xMax > p.xMax || c.yMin < p.yMin || c.yMax > p.yMax ||
            close.GetComponent<Image>().sprite != tablet.closeButton.GetComponent<Image>().sprite)
            throw new InvalidOperationException("Bargain close does not use the native icon inside the panel artwork");
        RectTransform tabletRoot = tablet.panel;
        var basket = tabletRoot.Find("ShopAssist_CustomerBasket") as RectTransform;
        var reset = tabletRoot.Find("ShopAssist_BargainReset") as RectTransform;
        if (basket == null || reset == null || basket.GetComponent<TabletButton>() == null || reset.GetComponent<TabletButton>() == null)
            throw new InvalidOperationException("Tablet header actions are not using native buttons");
        Rect basketBounds = Bounds(tabletRoot, basket), resetBounds = Bounds(tabletRoot, reset), tabletBounds = tabletRoot.rect;
        Rect closeBounds = Bounds(tabletRoot, tablet.closeButton.transform as RectTransform);
        if (!tabletBounds.Contains(basketBounds.min) || !tabletBounds.Contains(basketBounds.max) ||
            !tabletBounds.Contains(resetBounds.min) || !tabletBounds.Contains(resetBounds.max) ||
            basketBounds.Overlaps(resetBounds) || basketBounds.Overlaps(closeBounds) || resetBounds.Overlaps(closeBounds))
            throw new InvalidOperationException("Tablet header actions overlap or leave the native window");
        Rect panelScreen = ScreenBounds(root), tabletScreen = ScreenBounds(tablet.transform as RectTransform);
        if (panelScreen.Overlaps(tabletScreen) || panelScreen.xMin < -1 || panelScreen.xMax > Screen.width + 1 ||
            tabletScreen.xMin < -1 || tabletScreen.xMax > Screen.width + 1)
            throw new InvalidOperationException("Docked bargain panel overlaps the tablet or leaves the screen");
    }

    private static Rect Bounds(RectTransform root, RectTransform child)
    {
        var corners = new Vector3[4]; child.GetWorldCorners(corners);
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        foreach (var corner in corners)
        {
            Vector3 p = root.InverseTransformPoint(corner);
            minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y);
            maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y);
        }
        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    private static Rect ScreenBounds(RectTransform rect)
    {
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        foreach (var corner in corners)
        {
            Vector2 p = RectTransformUtility.WorldToScreenPoint(camera, corner);
            minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y);
            maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y);
        }
        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }
}
