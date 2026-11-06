using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

// Checks the live door against native rent/mortgage state and the real settings UI.
public sealed class DailyDeadlineScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step;
    private float since = Time.realtimeSinceStartup, after;
    private bool waitingLogged;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - since < 45f, "F21 threshold QA timed out at step " + step);
        if (step == 0) { if (start.Tick()) Next(1); return false; }
        if (step == 1)
        {
            if (StoreUIManager.Instance?.gameOptionUIManager == null || StoreShutterButton.Instance == null) return false;
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            TradeScenario.SetOptions(false, "f15.enabled", "f17.enabled");
            SetOption(true, 1);
            var store = PlayerStore.Instance;
            store.isPayingMortgage = false; store.IsPropertyPaid = false; store.dayUntilRent = 2;
            Need(store.IsPayingRent(), "Native rent fixture is invalid");
            Wait(2); return false;
        }
        if (Time.unscaledTime < after) return false;
        if (step == 2)
        {
            if (!AwaitRow("door.rent", 2)) return false;
            Need(ColorOf("door.rent", 2).Equals((Color32)Color.white), "Rent outside the reminder window was highlighted");
            SetOption(true, 2); Wait(3); return false;
        }
        if (step == 3)
        {
            if (!AwaitRow("door.rent", 2)) return false;
            Need(ColorOf("door.rent", 2).Equals(new Color32(255, 199, 126, 255)), "Changing the lead time did not highlight rent at the boundary");
            PlayerStore.Instance.dayUntilRent = 3; Wait(4); return false;
        }
        if (step == 4)
        {
            if (!AwaitRow("door.rent", 3)) return false;
            Need(ColorOf("door.rent", 3).Equals((Color32)Color.white), "Rent stayed highlighted beyond the configured lead time");
            PlayerStore.Instance.isPayingMortgage = true; PlayerStore.Instance.dayUntilRent = 1;
            Need(!PlayerStore.Instance.IsPayingRent(), "Mortgage fixture still pays rent");
            Wait(5); return false;
        }
        if (step == 5)
        {
            if (!AwaitRow("door.mortgage", 1)) return false;
            Need(ColorOf("door.mortgage", 1).Equals(new Color32(255, 199, 126, 255)) && Row("door.rent", 1) == null,
                "Mortgage did not replace rent with a highlighted due date");
            PlayerStore.Instance.isPayingMortgage = false; PlayerStore.Instance.IsPropertyPaid = true;
            Wait(6); return false;
        }
        if (step == 6)
        {
            if (!AwaitBoard()) return false;
            Need(Row("door.mortgage", 1) == null && Row("door.rent", 1) == null, "Paid property retained a rent or mortgage deadline");
            SetOption(false, 2); Wait(7); return false;
        }
        if (Find("ShopAssist_DoorViewport")?.activeInHierarchy == true && Time.realtimeSinceStartup - since < 8f) return false;
        Need(Find("ShopAssist_DoorViewport")?.activeInHierarchy != true, "Disabled F21 retained the deadline board: " + State());
        result = "PASS F21 live rent/mortgage deadlines, reminder threshold changes, paid property and disable";
        return true;
    }

    private static void SetOption(bool enabled, int days)
    {
        var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
        var window = CustomUIManager.Instance.GetWindow("game_options");
        Need(window != null, "Native options unavailable");
        window.Get(Mod + ":f21.enabled").SetBool(enabled, true);
        window.Get(Mod + ":f21.days").SetFloat(days, true);
        window.Get("apply").button.onClick.Invoke();
        Need(((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f21.enabled")).Value == enabled &&
            ((ConfigEntry<int>)ModConfig.Find(Mod).Get("f21.days")).Value == days, "F21 options did not apply");
        options.CloseUI();
    }

    private static TMP_Text Row(string key, int days)
    {
        string expected = ModHelper.GetLocalized(Mod, "door.deadline", ModHelper.GetLocalized(Mod, key), days);
        return Resources.FindObjectsOfTypeAll<TMP_Text>().FirstOrDefault(text => text.name == "deadline-row" &&
            text.gameObject.scene.IsValid() && text.gameObject.activeInHierarchy && text.text == expected);
    }
    private static Color32 ColorOf(string key, int days)
    {
        var row = Row(key, days); Need(row != null, "Deadline not shown: " + key + " " + days); return row.color;
    }
    private bool AwaitRow(string key, int days)
    {
        if (Row(key, days) != null) return true;
        LogWaiting("missing " + key + " " + days);
        Need(Time.realtimeSinceStartup - since < 8f, "Deadline not shown: " + key + " " + days + "; " + State());
        return false;
    }
    private bool AwaitBoard()
    {
        if (Find("ShopAssist_DoorViewport")?.activeInHierarchy == true && Find("deadlines")?.activeInHierarchy == true) return true;
        LogWaiting("paid-property board is not visible");
        Need(Time.realtimeSinceStartup - since < 8f, "Paid-property deadline board is not visible: " + State());
        return false;
    }
    private void LogWaiting(string label)
    {
        if (waitingLogged) return;
        waitingLogged = true;
        Debug.Log("[PLAYER-QA] " + label + ": " + State());
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
        string path = Path.Combine(Application.persistentDataPath, "qa-daily-deadline-" + step + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
        ScreenCapture.CaptureScreenshot(path);
        Debug.Log("[PLAYER-QA] deadline diagnostic capture " + path);
    }
    private static GameObject Find(string name) => Resources.FindObjectsOfTypeAll<GameObject>()
        .Where(value => value.name == name && value.scene.IsValid())
        .OrderByDescending(value => value.activeInHierarchy).FirstOrDefault();
    private static string State()
    {
        var store = PlayerStore.HasInstance ? PlayerStore.Instance : null;
        var ui = StoreUIManager.Instance;
        var shutter = StoreShutterButton.Instance;
        GameObject viewport = Find("ShopAssist_DoorViewport"), board = Find("ShopAssist_DoorBoard"), deadlines = Find("deadlines");
        var rows = Resources.FindObjectsOfTypeAll<TMP_Text>()
            .Where(value => value.name == "deadline-row" && value.gameObject.scene.IsValid())
            .Select(value => "[" + value.text + "; active=" + value.gameObject.activeInHierarchy + "; color=" + (Color32)value.color + "]").ToArray();
        var mod = ModLoader.Mods.FirstOrDefault(value => value.Manifest?.ID == Mod);
        Type book = mod?.Instance?.GetType().Assembly.GetType("ShopAssist.RequestBook");
        object reminder = book?.GetProperty("DoorRemindersEnabled", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        var camera = GameObject.Find("Emporium_camera")?.GetComponent<Camera>();
        return "store=" + (store != null) + " location=" + store?.playerLocation + " state=" + store?.storeState +
            " rent=" + (store?.IsPayingRent() == true) + " mortgage=" + store?.isPayingMortgage + " paid=" + store?.IsPropertyPaid +
            " days=" + store?.dayUntilRent + " reminders=" + reminder +
            " f21=" + ModConfig.Find(Mod)?.Get("f21.enabled")?.BoxedValue + " lead=" + ModConfig.Find(Mod)?.Get("f21.days")?.BoxedValue +
            " doorDistance=" + (shutter?.shutterGameObject != null ? Vector3.Distance(shutter.shutterGameObject.transform.position, shutter.startPosition).ToString() : "null") +
            " camera=" + (camera?.isActiveAndEnabled == true) + " interactables=" + (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy == true) +
            " options=" + (ui?.gameOptionUIManager?.IsOpen == true) + " blackout=" + (ui?.blackscreen?.gameObject.activeInHierarchy == true) +
            " hint=" + (ui?.controlHintOverlay?.activeInHierarchy == true) + " calendar=" + (AdvCalendarUIManager.Instance?.UIPanel?.activeInHierarchy == true) +
            " dialog=" + (DialogUIManager.Instance?.currentDialog != null) + " viewport=" + (viewport?.activeInHierarchy == true) +
            " board=" + (board?.activeInHierarchy == true) + " deadlines=" + (deadlines?.activeInHierarchy == true) +
            " rows=" + string.Join(" ", rows);
    }
    private void Wait(int next) { after = Time.unscaledTime + 0.8f; waitingLogged = false; Next(next); }
    private void Next(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
