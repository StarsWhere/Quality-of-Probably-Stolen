using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Runs only in the guarded Player and exercises the three independent door features.
public sealed class DoorScenario
{
    [Serializable] private sealed class TaskBook { public TaskRow[] rows = Array.Empty<TaskRow>(); }
    [Serializable] private sealed class TaskRow { public string id = ""; public int checkedDay = 0; }
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step, today;
    private int checkFrame;
    private string firstId, secondId;
    private float after, started = Time.realtimeSinceStartup;
    private float closedPaperY;
    private bool sawMotion;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - started > 120f) throw new InvalidOperationException("Door QA timed out at step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance.storeInteractables == null || !EmporiumEntry.Instance.storeInteractables.activeInHierarchy ||
                StoreShutterButton.Instance == null || AdvCalendarUIManager.Instance == null) return false;
            if (AdvCalendarUIManager.Instance.UIPanel.activeSelf) StoreUIManager.Instance.storeCalendar.OnClick();
            if (StoreUIManager.Instance.controlHintOverlay != null && StoreUIManager.Instance.controlHintOverlay.activeSelf)
                StoreUIManager.Instance.ToggleControlHintOverlay();
            today = StoreStation.GetDayCounter();
            var data = new StringBuilder("{\"schemaVersion\":1,\"rows\":[");
            string[] taskNames = { "补充饮料", "整理主仓", "检查滤芯", "准备订单", "清点货架", "查看委托", "检查机器", "核对账本" };
            for (int i = 0; i < 8; i++)
            {
                string id = Guid.NewGuid().ToString("N");
                if (i == 0) firstId = id;
                if (i == 1) secondId = id;
                if (i > 0) data.Append(',');
                data.Append("{\"id\":\"").Append(id).Append("\",\"text\":\"").Append(taskNames[i])
                    .Append("\",\"repeat\":0,\"checkedDay\":0}");
            }
            data.Append("]}");
            ModHelper.SetModData(Mod, "todo", data.ToString());
            TradeScenario.SetOptions(true, "f15.enabled", "f17.enabled", "f21.enabled");
            TradeScenario.SetOptions(false, "f27.enabled");
            after = Time.realtimeSinceStartup + 1f; step++; return false;
        }
        if (Time.realtimeSinceStartup < after) return false;
        switch (step)
        {
            case 2:
            {
                GameObject viewport = Find("ShopAssist_DoorViewport");
                GameObject board = Find("ShopAssist_DoorBoard");
                if (viewport == null || board == null || !viewport.activeInHierarchy) return false;
                Need(FindAll("ShopAssist_DoorDay_").Count == 14, "The complete two-week calendar is not visible");
                Need(Children(board, "ShopAssist_DoorTask_").Count == 5, "The door does not show the first task page");
                Need(Find("ShopAssist_DoorBadge")?.activeInHierarchy == true && Find("deadlines") != null,
                    "The housing badge or deadline strip is missing");
                Need(AdvCalendarUIManager.Instance.UIPanel.activeSelf == false, "Door opened the original calendar");
                TMP_Text heading = Find("calendar-title")?.GetComponent<TMP_Text>();
                if (heading != null)
                {
                    heading.ForceMeshUpdate();
                    Debug.Log("[PLAYER-QA] Door font=" + heading.font?.name + " heading=" + heading.text + " visible glyphs=" +
                        heading.textInfo.characterInfo.Take(heading.textInfo.characterCount).Count(character => character.isVisible));
                    Need(heading.textInfo.characterInfo.Take(heading.textInfo.characterCount).Any(character => character.isVisible),
                        "Calendar heading has no visible glyphs");
                }
                var viewRect = (RectTransform)viewport.transform;
                var canvas = viewRect.GetComponentInParent<Canvas>();
                Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                Vector3[] corners = new Vector3[4]; viewRect.GetWorldCorners(corners);
                Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
                Vector2 high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
                var boardRect = (RectTransform)board.transform;
                closedPaperY = boardRect.anchoredPosition.y;
                Vector3[] panelCorners = new Vector3[4]; boardRect.GetWorldCorners(panelCorners);
                Vector2 panelLow = RectTransformUtility.WorldToScreenPoint(camera, panelCorners[0]);
                Vector2 panelHigh = RectTransformUtility.WorldToScreenPoint(camera, panelCorners[2]);
                float widthRatio = (panelHigh.x - panelLow.x) / (high.x - low.x);
                float heightRatio = (panelHigh.y - panelLow.y) / (high.y - low.y);
                Need(widthRatio >= 0.78f && widthRatio <= 0.84f && heightRatio >= 0.62f && heightRatio <= 0.72f &&
                    panelLow.x > low.x && panelHigh.x < high.x && panelLow.y > low.y && panelHigh.y < high.y,
                    "Door board does not leave the required visible shutter margins");
                Debug.Log("[PLAYER-QA] Door viewport screen=" + low + ".." + high + " screen=" + Screen.width + "x" + Screen.height +
                    " shutter=" + StoreShutterButton.Instance.shutterGameObject.transform.position +
                    " start=" + StoreShutterButton.Instance.startPosition + " end=" + StoreShutterButton.Instance.endPosition +
                    " wire=" + StoreUIManager.Instance.wireRenderer.transform.position);
                Need(high.x > low.x && high.y > low.y && low.x >= 0 && high.x <= Screen.width && low.y >= 0 && high.y <= Screen.height,
                    "Door viewport is outside the screen");
                Capture("qa-door-closed.png");
                ButtonFor("ShopAssist_DoorTask_" + firstId).onClick.Invoke();
                after = 0; checkFrame = Time.frameCount + 1; step++; return false;
            }
            case 3:
            {
                if (Time.frameCount < checkFrame) return false;
                string raw = ModHelper.GetModData(Mod, "todo", "");
                Need(CheckedDay(raw) == today, "A door check did not update the shared task book");
                var badgeText = Find("ShopAssist_DoorBadgeButton")?.GetComponentInChildren<TMP_Text>(true)?.text;
                Need(badgeText == ModHelper.GetLocalized(Mod, "door.badge") + " 7", "Badge was not updated on the next frame: " + badgeText);
                ButtonFor("ShopAssist_DoorDay_" + today).onClick.Invoke();
                after = Time.realtimeSinceStartup + 0.8f; step++; return false;
            }
            case 4:
                Need(Find("ShopAssist_DoorBack") != null && !AdvCalendarUIManager.Instance.UIPanel.activeSelf,
                    "Day details did not open inside the door");
                ButtonFor("ShopAssist_DoorBack").onClick.Invoke();
                TradeScenario.SetOptions(false, "f15.enabled");
                after = Time.realtimeSinceStartup + 0.8f; step++; return false;
            case 5:
                Need(FindAll("ShopAssist_DoorDay_").Count == 0 && Children(Find("ShopAssist_DoorBoard"), "ShopAssist_DoorTask_").Count == 5 && Find("deadlines") != null,
                    "F15 off did not leave F17 and F21 visible");
                TradeScenario.SetOptions(false, "f17.enabled");
                after = Time.realtimeSinceStartup + 0.8f; step++; return false;
            case 6:
                Need(FindAll("ShopAssist_DoorTask_").Count == 0 && Find("deadlines") != null &&
                    Find("ShopAssist_DoorBadge")?.activeInHierarchy != true, "F17 off affected the deadline or left its badge active");
                TradeScenario.SetOptions(false, "f21.enabled");
                after = Time.realtimeSinceStartup + 0.8f; step++; return false;
            case 7:
                Need(Find("ShopAssist_DoorViewport", false)?.activeInHierarchy != true, "Door stayed visible with all three features off");
                TradeScenario.SetOptions(true, "f15.enabled", "f17.enabled", "f21.enabled");
                after = Time.realtimeSinceStartup + 0.8f; step++; return false;
            case 8:
            {
                var p = PlayerStore.Instance;
                if (p.lockShutterButtonFlag > 0 || p.isClientBeingArrested || p.isHandlingShootout ||
                    DialogUIManager.Instance?.currentDialog != null || DialoguePhoneUIManager.Instance?.currentDialog != null) return false;
                Need(p.CanStartDay(), "Native shutter cannot open");
                StoreShutterButton.Instance.OnClick();
                Need(p.storeState == PlayerStore.StoreState.OPEN, "Native shutter did not start opening");
                step++; return false;
            }
            case 9:
            {
                StoreShutterButton door = StoreShutterButton.Instance;
                float amount = Vector3.Distance(door.shutterGameObject.transform.position, door.startPosition);
                float distance = Vector3.Distance(door.startPosition, door.endPosition);
                if (amount > 0.05f && amount < distance - 0.05f)
                {
                    sawMotion = true;
                    GameObject board = Find("ShopAssist_DoorBoard");
                    if (board != null)
                    {
                        var group = board.GetComponent<CanvasGroup>();
                        Need(group != null && !group.blocksRaycasts && !group.interactable,
                            "The moving shutter still accepts clicks");
                        Need(Math.Abs(((RectTransform)board.transform).anchoredPosition.y - closedPaperY) > 1f,
                            "The door panel did not follow shutter motion");
                    }
                }
                if (Vector3.Distance(door.shutterGameObject.transform.position, door.endPosition) > 0.001f) return false;
                Need(sawMotion, "The opening animation was not observed");
                ButtonFor("ShopAssist_DoorBadgeButton").onClick.Invoke();
                after = Time.realtimeSinceStartup + 0.8f; step++; return false;
            }
            case 10:
                Need(Find("ShopAssist_DoorNote")?.activeInHierarchy == true && Children(Find("ShopAssist_DoorNote"), "ShopAssist_DoorTask_").Count == 5,
                    "Open shutter badge did not show the compact task board");
                Capture("qa-door-open.png");
                var task = Children(Find("ShopAssist_DoorNote"), "ShopAssist_DoorTask_" + secondId).SingleOrDefault();
                Need(task != null, "An unfinished task is missing from the compact note");
                task.GetComponent<Button>().onClick.Invoke();
                after = Time.realtimeSinceStartup + 0.8f; step++; return false;
            case 11:
                Need(CheckedDay(ModHelper.GetModData(Mod, "todo", ""), secondId) == today, "Wall note did not update the task book");
                Need(AdvCalendarUIManager.Instance.UIPanel.activeSelf == false, "Original calendar opened unexpectedly");
                result = "PASS shutter calendar and independent switches; shared task checking; motion clipping and noninteractive animation; housing badge and open-shutter note";
                return true;
            default: throw new InvalidOperationException("Unknown door QA step " + step);
        }
    }

    private static GameObject Find(string name, bool active = true) => Resources.FindObjectsOfTypeAll<GameObject>()
        .FirstOrDefault(item => item.name == name && item.scene.IsValid() && (!active || item.activeInHierarchy));
    private static List<GameObject> FindAll(string prefix) => Resources.FindObjectsOfTypeAll<GameObject>()
        .Where(item => item.name.StartsWith(prefix, StringComparison.Ordinal) && item.scene.IsValid() && item.activeInHierarchy).ToList();
    private static List<GameObject> Children(GameObject root, string prefix) => root == null ? new List<GameObject>() :
        root.GetComponentsInChildren<Transform>().Where(item => item.name.StartsWith(prefix, StringComparison.Ordinal)).Select(item => item.gameObject).ToList();
    private int CheckedDay(string raw, string id = null) => JsonUtility.FromJson<TaskBook>(raw)?.rows?.FirstOrDefault(row => row.id == (id ?? firstId))?.checkedDay ?? -1;
    private static Button ButtonFor(string name) => Find(name)?.GetComponent<Button>() ?? throw new InvalidOperationException("Missing button: " + name);
    private static void Capture(string file)
    {
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, file));
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
