using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Changes display modes only inside the guarded QA Player, then restores them.
public sealed class DoorScaleScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly int[] widths = { 1280, 1920 }, heights = { 720, 1080 }, resolutions = { 3, 0 };
    private readonly string taskId = Guid.NewGuid().ToString("N");
    private int step, pass, frame, originalMode, originalResolution, originalDivisor;
    private float since = Time.realtimeSinceStartup, stableAfter;
    private bool restoreOptions;

    [Serializable] private sealed class Book { public Row[] rows = Array.Empty<Row>(); }
    [Serializable] private sealed class Row { public string id = ""; public int checkedDay = 0; }

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
        if (Time.realtimeSinceStartup - since > 100f) throw new InvalidOperationException("Door scale QA timed out at step " + step + " pass " + pass);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || EmporiumOption.Instance == null ||
                StoreUIManager.Instance?.gameOptionUIManager == null || EventSystem.current == null || StoreShutterButton.Instance == null) return false;
            if (StoreUIManager.Instance.controlHintOverlay?.activeSelf == true) StoreUIManager.Instance.ToggleControlHintOverlay();
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            originalMode = EmporiumOption.Instance.fullscreenMode;
            originalResolution = EmporiumOption.Instance.resolution;
            originalDivisor = EmporiumOption.Instance.borderlessDivisor;
            restoreOptions = true;
            ModHelper.SetModData(Mod, "todo", "{\"schemaVersion\":1,\"rows\":[{\"id\":\"" + taskId +
                "\",\"text\":\"QA task\",\"repeat\":0,\"checkedDay\":0}]}");
            TradeScenario.SetOptions(true, "f15.enabled", "f17.enabled", "f21.enabled");
            ChangeResolution(0); step = 2; return false;
        }
        if (step == 2)
        {
            if (Screen.width != widths[pass] || Screen.height != heights[pass] || Time.realtimeSinceStartup < stableAfter || Time.frameCount < frame) return false;
            if (DoorQaContext.Find("ShopAssist_DoorBoard") == null) return false;
            CheckLayout();
            Button date = ButtonNamed("ShopAssist_DoorDay_" + StoreStation.GetDayCounter());
            Button task = ButtonNamed("ShopAssist_DoorTask_" + taskId);
            Need(date.IsInteractable() && task.IsInteractable() && Hit(date) && Hit(task),
                "A date or task is not reachable through the native raycast");
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-door-scale-" + widths[pass] + "x" + heights[pass] + ".png"));
            frame = Time.frameCount + 3; step = 20; return false;
        }
        if (Time.frameCount < frame) return false;
        if (step == 20)
        {
            Click(ButtonNamed("ShopAssist_DoorDay_" + StoreStation.GetDayCounter()));
            frame = Time.frameCount + 2; step = 3; return false;
        }
        if (step == 3)
        {
            Button back = ButtonNamed("ShopAssist_DoorBack");
            Need(Hit(back) && !AdvCalendarUIManager.Instance.UIPanel.activeSelf, "Date details are not usable inside the door");
            Click(back); frame = Time.frameCount + 2; step = 4; return false;
        }
        if (step == 4)
        {
            Button task = ButtonNamed("ShopAssist_DoorTask_" + taskId);
            Need(Hit(task), "The task is no longer reachable after date details");
            Click(task); frame = Time.frameCount + 2; step = 5; return false;
        }
        if (step == 5)
        {
            Book book = JsonUtility.FromJson<Book>(ModHelper.GetModData(Mod, "todo", ""));
            Row row = book?.rows?.SingleOrDefault(item => item.id == taskId);
            Need(row != null && row.checkedDay == (pass == 0 ? StoreStation.GetDayCounter() : 0),
                "Native task click did not update the shared checklist");
            TMP_Text badge = DoorQaContext.Find("ShopAssist_DoorBadgeButton")?.GetComponentInChildren<TMP_Text>(true);
            Need(badge != null && badge.text == ModHelper.GetLocalized(Mod, "door.badge") + (pass == 0 ? " 0" : " 1"),
                "Task count did not refresh after the native click");
            if (++pass < resolutions.Length) { ChangeResolution(pass); step = 2; return false; }
            Restore();
            result = "PASS 1280x720 and 1920x1080: shutter margins, rendered localized glyphs, native date/task raycasts and checklist updates";
            return true;
        }
        throw new InvalidOperationException("Unknown door scale QA step " + step);
    }

    private void Restore()
    {
        if (!restoreOptions || EmporiumOption.Instance == null) return;
        restoreOptions = false;
        EmporiumOption.Instance.fullscreenMode = originalMode;
        EmporiumOption.Instance.resolution = originalResolution;
        EmporiumOption.Instance.borderlessDivisor = originalDivisor;
        EmporiumOption.Instance.ApplySetting();
    }

    private void ChangeResolution(int index)
    {
        EmporiumOption.Instance.fullscreenMode = 2;
        EmporiumOption.Instance.resolution = resolutions[index];
        EmporiumOption.Instance.ApplySetting();
        stableAfter = Time.realtimeSinceStartup + 1.5f; frame = Time.frameCount + 10;
        since = Time.realtimeSinceStartup;
    }
    private static void CheckLayout()
    {
        GameObject view = DoorQaContext.Find("ShopAssist_DoorViewport"), board = DoorQaContext.Find("ShopAssist_DoorBoard");
        Need(view != null && board != null, "Door objects are not visible");
        Rect area = Bounds((RectTransform)view.transform), card = Bounds((RectTransform)board.transform);
        float wr = card.width / area.width, hr = card.height / area.height;
        float left = (card.xMin - area.xMin) / area.width, right = (area.xMax - card.xMax) / area.width;
        float bottom = (card.yMin - area.yMin) / area.height, top = (area.yMax - card.yMax) / area.height;
        Need(wr >= 0.78f && wr <= 0.84f && hr >= 0.62f && hr <= 0.72f &&
            Math.Abs(left - right) < 0.015f && Math.Abs(top - bottom) < 0.015f && left > 0 && top > 0,
            "Door panel or its margins are misaligned: " + area + " / " + card);
        Need(card.xMin >= 0 && card.yMin >= 0 && card.xMax <= Screen.width && card.yMax <= Screen.height, "Door is outside the viewport");
        Camera sceneCamera = StoreUIManager.Instance.mainCamera.GetComponent<Camera>();
        Rect pixels = sceneCamera.pixelRect;
        Rect expected = Rect.MinMaxRect(pixels.x + pixels.width * 329f / 960f, pixels.y + pixels.height * (1f - 265f / 540f),
            pixels.x + pixels.width * 631f / 960f, pixels.y + pixels.height * (1f - 24f / 540f));
        Need(Math.Abs(area.xMin - expected.xMin) < 2 && Math.Abs(area.xMax - expected.xMax) < 2 &&
            Math.Abs(area.yMin - expected.yMin) < 2 && Math.Abs(area.yMax - expected.yMax) < 2,
            "Door area moved away from the native camera's visible shutter");
        TMP_Text header = DoorQaContext.Find("calendar-title")?.GetComponent<TMP_Text>();
        TMP_Text task = DoorQaContext.Find("task-text")?.GetComponent<TMP_Text>();
        Need(header != null && task != null, "Door labels are missing");
        foreach (TMP_Text text in new[] { header, task })
        {
            text.ForceMeshUpdate();
            Need(text.font != null && text.textInfo.characterInfo.Take(text.textInfo.characterCount).Any(character => character.isVisible) &&
                !text.isTextTruncated, "Door text is not rendered or is truncated: " + text.text);
        }
        Debug.Log("[PLAYER-QA] door scale " + Screen.width + "x" + Screen.height + " canvas=" +
            board.GetComponentInParent<Canvas>()?.scaleFactor + " viewport=" + area + " panel=" + card +
            " nativeMask=" + Bounds(StoreShutterButton.Instance.blockerMaskObject.rectTransform) +
            " margins=" + left + "/" + right + "/" + top + "/" + bottom);
    }
    private static Button ButtonNamed(string name) => DoorQaContext.Find(name)?.GetComponent<Button>() ??
        throw new InvalidOperationException("Missing door button " + name);
    private static Rect Bounds(RectTransform rect)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return Rect.MinMaxRect(low.x, low.y, high.x, high.y);
    }
    private static bool Hit(Button button)
    {
        Vector2 point = Bounds((RectTransform)button.transform).center;
        var results = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, results);
        bool hit = results.Count > 0 && (results[0].gameObject == button.gameObject || results[0].gameObject.transform.IsChildOf(button.transform));
        if (!hit) Debug.Log("[PLAYER-QA] " + Screen.width + "x" + Screen.height + " " + button.name + " hit=" +
            string.Join(" | ", results.Take(6).Select(item => item.gameObject.name + " via " + item.module.name)));
        return hit;
    }
    private static void Click(Button button)
    {
        var pointer = new PointerEventData(EventSystem.current) { position = Bounds((RectTransform)button.transform).center, button = PointerEventData.InputButton.Left };
        ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
