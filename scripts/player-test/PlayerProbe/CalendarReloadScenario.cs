using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// Cold-loads the exact save produced by MorningScenario without any UI receipt.
public sealed class CalendarReloadScenario
{
    private const string Mod = "starswhere.shopassist";
    private DoorQaContext.Snapshot context;
    private QaLoadSlot load;
    private int step, frame;
    private float since = Time.realtimeSinceStartup;
    private string observed;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 75f) throw new InvalidOperationException("Door reload QA timed out at step " + step);
        if (step == 0)
        {
            if (MainMenuUIController.Instance == null) return false;
            context = DoorQaContext.Read();
            string[] latest = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-latest-run.txt")).Trim().Split('|');
            Guid expected = Guid.Empty;
            Need(latest.Length == 4 && latest[0] == "1" && Guid.TryParseExact(latest[1], "N", out Guid recorded) &&
                Guid.TryParse(context.run, out expected) && recorded == expected && int.TryParse(latest[2], out int slot) && slot == context.slot &&
                int.TryParse(latest[3], out int day) && day == context.day, "Door context disagrees with the guarded native save");
            load = new QaLoadSlot(context.slot, expected); step++; return false;
        }
        if (step == 1)
        {
            if (!load.Tick() || StoreStation.Instance?.storeEventManager == null || AdvCalendarUIManager.Instance == null) return false;
            Need(StoreStation.GetDayCounter() == context.day && PlayerStore.Instance.storeState == PlayerStore.StoreState.MORNING,
                "Cold reload did not restore the saved morning");
            var native = AdvCalendarUIManager.Instance;
            if (native.UIPanel.activeSelf)
            {
                Need(native.nightlyReportSection != null && native.nightlyReportSection.activeInHierarchy &&
                    (native.calendarSection == null || !native.calendarSection.activeInHierarchy), "The original calendar opened on cold reload");
                StoreUIManager.Instance.storeCalendar.OnClick();
            }
            if (StoreUIManager.Instance.controlHintOverlay?.activeSelf == true) StoreUIManager.Instance.ToggleControlHintOverlay();
            observed = DoorQaContext.EventState();
            Need(observed == context.events, "Cold reload changed saved native event state");
            frame = Time.frameCount + 5; step++; return false;
        }
        if (Time.frameCount < frame) return false;
        Need(!AdvCalendarUIManager.Instance.UIPanel.activeSelf, "Door opened the original calendar after reload");
        GameObject board = DoorQaContext.Find("ShopAssist_DoorBoard");
        if (board == null) return false;
        Need(board.GetComponentsInChildren<Button>().Count(button => button.name.StartsWith("ShopAssist_DoorDay_", StringComparison.Ordinal)) == 14,
            "Reloaded door is missing dates");
        Need(observed == DoorQaContext.EventState(), "Reloaded door mutated native event state");
        Need((ModHelper.GetModData(Mod, "f15.calendar", "") ?? "") == context.oldMarker, "Reloaded door wrote the obsolete receipt");
        result = "PASS cold reload: same run, slot and morning; visible door without original popup, receipt writes or event mutation";
        return true;
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
