using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Confirms that a native scanner renders and receives input in front of the door.
public sealed class DoorLayerScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step, frame;
    private float since = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 70) throw new InvalidOperationException("Door layering QA timed out at step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || WaterScannerUIManager.Instance == null ||
                StoreUIManager.Instance?.gameOptionUIManager == null || EventSystem.current == null) return false;
            if (StoreUIManager.Instance.controlHintOverlay?.activeSelf == true) StoreUIManager.Instance.ToggleControlHintOverlay();
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            TradeScenario.SetOptions(true, "f15.enabled");
            TradeScenario.SetOptions(false, "f17.enabled", "f21.enabled");
            frame = Time.frameCount + 3; step++; return false;
        }
        if (step == 2)
        {
            if (Time.frameCount < frame) return false;
            var board = Find("ShopAssist_DoorBoard");
            if (board == null) return false;
            Transform blocker = StoreShutterButton.Instance.blockerGameObject.transform.parent;
            Need(blocker != null && board.transform.parent.parent == blocker.parent && board.transform.parent.GetSiblingIndex() > blocker.GetSiblingIndex(),
                "Door board is not immediately above the native shutter art");
            GameItem sample = DirectoryMaster.Item("wine_bottle"); Need(sample != null, "Scanner sample unavailable");
            GeneralHelper.SetItemOwned(sample, true);
            SlotMarker marker = EmporiumEntry.Instance.invElement.TryFindOneValidInventorySlot(sample);
            Need(marker != null && marker.TryAcceptOnce(sample.unitCount) == sample.unitCount, "Could not place scanner sample");
            WaterHelper.AddLiquid(sample, "water", 730000);
            WaterScannerUIManager.Instance.Open(null, sample);
            frame = Time.frameCount + 3; step++; return false;
        }
        if (step == 3)
        {
            if (Time.frameCount < frame) return false;
            var board = Find("ShopAssist_DoorBoard"); var panel = WaterScannerUIManager.Instance.panel;
            Need(board != null && panel.activeInHierarchy, "Scanner or door is not visible");
            LogCanvas("door", board.transform); LogCanvas("scanner", panel.transform);
            LogCanvas("blocker", StoreShutterButton.Instance.blockerGameObject.transform);
            LogCanvas("blocker mask", StoreShutterButton.Instance.blockerMaskObject.transform);
            foreach (Canvas c in Resources.FindObjectsOfTypeAll<Canvas>().Where(c => c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy))
                Debug.Log("[PLAYER-QA] canvas " + c.name + " mode=" + c.renderMode + " order=" + c.sortingOrder +
                    " layer=" + c.sortingLayerID + " root=" + c.rootCanvas.name + " camera=" + c.worldCamera?.name);
            Button close = panel.transform.Find("CloseButton")?.GetComponent<Button>();
            Need(close != null && close.gameObject.activeInHierarchy && Hit(close), "Scanner close button is blocked by the door");
            var overlap = Overlap((RectTransform)board.transform, (RectTransform)panel.transform);
            Need(overlap.width > 10 && overlap.height > 10, "Scanner does not overlap the board for this test");
            var filter = board.GetComponents<MonoBehaviour>().OfType<ICanvasRaycastFilter>().FirstOrDefault();
            Need(filter != null && !filter.IsRaycastLocationValid(overlap.center, null),
                "Door pointer filter did not reject the foreground scanner area");
            var pointer = new PointerEventData(EventSystem.current) { position = overlap.center };
            var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(pointer, hits);
            Need(!hits.Any() || !hits[0].gameObject.transform.IsChildOf(board.transform),
                "Door controls steal the foreground scanner's overlap");
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-door-behind-scanner.png"));
            frame = Time.frameCount + 3; step++; return false;
        }
        if (step == 4)
        {
            if (Time.frameCount < frame) return false;
            WaterScannerUIManager.Instance.Close(); frame = Time.frameCount + 3; step++; return false;
        }
        if (Time.frameCount < frame) return false;
        if (step == 5)
        {
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-door-after-scanner.png"));
            frame = Time.frameCount + 3; step++; return false;
        }
        if (step == 6)
        {
            var visibleBoard = Find("ShopAssist_DoorBoard");
            var day = Find("ShopAssist_DoorDay_" + StoreStation.GetDayCounter())?.GetComponent<Button>();
            Need(visibleBoard != null && day != null, "Door did not reappear after scanner close");
            var group = visibleBoard.GetComponent<CanvasGroup>(); bool previous = group.blocksRaycasts;
            group.blocksRaycasts = true;
            try { Need(Hit(day), "Uncovered calendar date is not reachable by native raycast"); }
            finally { group.blocksRaycasts = previous; }
            Vector2 point = ScreenRect((RectTransform)day.transform).center;
            var filter = visibleBoard.GetComponents<MonoBehaviour>().OfType<ICanvasRaycastFilter>().FirstOrDefault();
            Need(filter != null && filter.IsRaycastLocationValid(point, null), "Door did not accept an uncovered point");

            var popup = CustomUIManager.Window("door.qa.foreground", "QA", CustomUIManager.LAYER_META).SetSize(200, 150).GetWindow();
            popup.Show(); Canvas.ForceUpdateCanvases();
            var popupFrame = (RectTransform)popup.frame.transform;
            Need(RectTransformUtility.ScreenPointToWorldPointInRectangle(popupFrame, point, CameraFor(popupFrame), out Vector3 world),
                "Cannot position foreground test window");
            popup.root.transform.position += world - popupFrame.TransformPoint(popupFrame.rect.center);
            Need(RectTransformUtility.RectangleContainsScreenPoint(popupFrame, point, CameraFor(popupFrame)), "Foreground test window does not overlap the date");
            Need(!filter.IsRaycastLocationValid(point, null), "A newly shown foreground window did not block the door in the same frame");
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            Need(!hits.Any() || !hits[0].gameObject.transform.IsChildOf(visibleBoard.transform), "A new foreground window allowed a click through to the door");
            popup.Close();

            var map = MapUIManager.Instance?.mapPanel;
            Need(map != null && !map.activeInHierarchy, "Map modal is unavailable or already open");
            map.SetActive(true);
            try { Need(!filter.IsRaycastLocationValid(point, null), "A newly opened map did not block the door in the same frame"); }
            finally { map.SetActive(false); }
            frame = Time.frameCount + 3; step++; return false;
        }
        var reopenedDay = Find("ShopAssist_DoorDay_" + StoreStation.GetDayCounter())?.GetComponent<Button>();
        Need(reopenedDay != null && Hit(reopenedDay), "Door did not accept input after foreground windows closed");
        result = "PASS door layering, same-frame foreground/modal blocking and restored native raycasts";
        return true;
    }

    private static void LogCanvas(string label, Transform node)
    {
        var path = new List<string>();
        for (Transform t = node; t != null; t = t.parent)
        {
            var c = t.GetComponent<Canvas>();
            path.Add(t.name + (c == null ? "" : "[canvas " + c.renderMode + " order=" + c.sortingOrder + "]"));
        }
        Debug.Log("[PLAYER-QA] " + label + " hierarchy " + string.Join(" / ", path));
    }
    private static Camera CameraFor(RectTransform r)
    {
        Canvas c = r.GetComponentInParent<Canvas>();
        return c == null || c.renderMode == RenderMode.ScreenSpaceOverlay ? null : c.worldCamera;
    }
    private static Rect ScreenRect(RectTransform r)
    {
        var corners = new Vector3[4]; r.GetWorldCorners(corners);
        Vector2 min = RectTransformUtility.WorldToScreenPoint(CameraFor(r), corners[0]);
        Vector2 max = RectTransformUtility.WorldToScreenPoint(CameraFor(r), corners[2]);
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }
    private static Rect Overlap(RectTransform a, RectTransform b)
    {
        Rect x = ScreenRect(a), y = ScreenRect(b);
        return Rect.MinMaxRect(Math.Max(x.xMin, y.xMin), Math.Max(x.yMin, y.yMin), Math.Min(x.xMax, y.xMax), Math.Min(x.yMax, y.yMax));
    }
    private static bool Hit(Button button)
    {
        Rect rect = ScreenRect((RectTransform)button.transform);
        var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = rect.center }, hits);
        bool result = hits.Count > 0 && (hits[0].gameObject == button.gameObject || hits[0].gameObject.transform.IsChildOf(button.transform));
        if (!result) Debug.Log("[PLAYER-QA] UI hit at " + rect.center + " target=" + PathOf(button.transform) + " hits=" +
            string.Join(" | ", hits.Take(6).Select(hit => PathOf(hit.gameObject.transform) + " via " + hit.module.name)));
        return result;
    }
    private static string PathOf(Transform t)
    {
        string path = "";
        for (int i = 0; t != null && i < 8; i++, t = t.parent) path = t.name + "/" + path;
        return path;
    }
    private static GameObject Find(string name) => Resources.FindObjectsOfTypeAll<GameObject>()
        .FirstOrDefault(item => item.name == name && item.scene.IsValid() && item.activeInHierarchy);
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
