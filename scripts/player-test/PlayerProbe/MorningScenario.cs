using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

// Fresh, isolated save used by the normal calendar -> calendar-reload driver.
public sealed class MorningScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private int step, frame, day;
    private float since = Time.realtimeSinceStartup;
    private string before;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 75f) throw new InvalidOperationException("Door morning QA timed out at step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || StoreUIManager.Instance?.gameOptionUIManager == null ||
                AdvCalendarUIManager.Instance == null || StoreStation.Instance?.storeEventManager == null || StoreShutterButton.Instance == null) return false;
            Need(PlayerStore.Instance.storeState == PlayerStore.StoreState.MORNING, "Fresh run did not start in the morning");
            if (AdvCalendarUIManager.Instance.UIPanel.activeSelf) StoreUIManager.Instance.storeCalendar.OnClick();
            if (StoreUIManager.Instance.controlHintOverlay?.activeSelf == true) StoreUIManager.Instance.ToggleControlHintOverlay();
            TradeScenario.SetOptions(true, "f15.enabled");
            TradeScenario.SetOptions(false, "f17.enabled", "f21.enabled");
            day = StoreStation.GetDayCounter(); before = DoorQaContext.EventState();
            frame = Time.frameCount + 4; step++; return false;
        }
        if (Time.frameCount < frame) return false;
        if (step == 2)
        {
            GameObject board = DoorQaContext.Find("ShopAssist_DoorBoard");
            if (board == null) return false;
            Need(!AdvCalendarUIManager.Instance.UIPanel.activeSelf, "Door opened the original calendar");
            int dateCount = board.GetComponentsInChildren<Button>().Count(button => button.name.StartsWith("ShopAssist_DoorDay_", StringComparison.Ordinal));
            int expectedDateButtons = day > 1 ? 8 : 7;
            Need(dateCount == expectedDateButtons, "Door has " + dateCount + " date buttons; expected " + expectedDateButtons);
            Need(before == DoorQaContext.EventState(), "Displaying the door changed native event state");
            Button date = DoorQaContext.Find("ShopAssist_DoorDay_" + day)?.GetComponent<Button>();
            Need(date != null, "Today's door date is missing"); date.onClick.Invoke();
            frame = Time.frameCount + 2; step++; return false;
        }
        if (step == 3)
        {
            Need(DoorQaContext.Find("ShopAssist_DoorBack") != null && !AdvCalendarUIManager.Instance.UIPanel.activeSelf,
                "Door date did not open its own details");
            DoorQaContext.Find("ShopAssist_DoorBack").GetComponent<Button>().onClick.Invoke();
            frame = Time.frameCount + 2; step++; return false;
        }
        Need(before == DoorQaContext.EventState(), "Using door details changed native event state");
        PlayerStore.Instance.SaveGame();
        Need(File.Exists(SaveFiles.AbsolutePath(SaveFiles.Slot(PlayerStore.Instance.saveSlotId))), "Native SaveGame did not produce the QA save");
        DoorQaContext.Write(day);
        result = "PASS new isolated morning: 8 visible dates and internal detail, no original popup or event mutation; native save prepared for cold reload";
        return true;
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

internal static class DoorQaContext
{
    [Serializable] internal sealed class Snapshot
    {
        public int version = 1, slot, day;
        public string run = "", events = "";
    }
    private const string Mod = "starswhere.shopassist";
    private static string PathName => Path.Combine(Application.persistentDataPath, "qa-door-readonly.json");

    internal static void Write(int day)
    {
        var p = PlayerStore.Instance;
        var context = new Snapshot { run = p.runID, slot = p.saveSlotId, day = day,
            events = EventState() };
        File.WriteAllText(PathName, JsonUtility.ToJson(context));
    }
    internal static Snapshot Read()
    {
        if (!File.Exists(PathName)) throw new InvalidOperationException("Missing isolated door QA context");
        Snapshot snapshot = JsonUtility.FromJson<Snapshot>(File.ReadAllText(PathName));
        if (snapshot == null || snapshot.version != 1 || !Guid.TryParse(snapshot.run, out Guid run) || run == Guid.Empty ||
            snapshot.slot < 0 || snapshot.day < 1 || snapshot.events == null)
            throw new InvalidOperationException("Invalid isolated door QA context");
        return snapshot;
    }
    internal static string EventState()
    {
        StoreEventManager manager = StoreStation.Instance.storeEventManager;
        var field = typeof(StoreEventManager).GetField("activeEvents", BindingFlags.Instance | BindingFlags.NonPublic);
        var active = field?.GetValue(manager) as IList<StoreEvent>;
        if (active == null || manager.futurEvents == null || manager.futurEventsDay == null ||
            manager.futurEvents.Count != manager.futurEventsDay.Count || manager.eventTracker == null)
            throw new InvalidOperationException("Native event state unavailable");
        var text = new StringBuilder();
        void Append(StoreEvent e) => text.Append(e.identifier).Append(':').Append(e.startDay).Append(':').Append(e.duration)
            .Append(':').Append(e.totalDuration).Append(':').Append(e.IsKnown).Append(';');
        text.Append("active|"); foreach (StoreEvent e in active) Append(e);
        text.Append("future|");
        for (int i = 0; i < manager.futurEvents.Count; i++) { text.Append(manager.futurEventsDay[i]).Append(':'); Append(manager.futurEvents[i]); }
        text.Append("tracker|");
        foreach (var pair in manager.eventTracker.OrderBy(pair => pair.Key, StringComparer.Ordinal)) text.Append(pair.Key).Append(':').Append(pair.Value).Append(';');
        return text.ToString();
    }
    internal static GameObject Find(string name) => Resources.FindObjectsOfTypeAll<GameObject>()
        .FirstOrDefault(item => item.name == name && item.scene.IsValid() && item.activeInHierarchy);
}
