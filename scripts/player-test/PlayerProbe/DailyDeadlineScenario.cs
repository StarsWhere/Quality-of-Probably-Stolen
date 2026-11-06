using System;
using System.Linq;
using TMPro;
using UnityEngine;

// Checks the live door against native rent/mortgage state and the real settings UI.
public sealed class DailyDeadlineScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step;
    private float since = Time.realtimeSinceStartup, after;

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
            Need(ColorOf("door.rent", 2).Equals((Color32)Color.white), "Rent outside the reminder window was highlighted");
            SetOption(true, 2); Wait(3); return false;
        }
        if (step == 3)
        {
            Need(ColorOf("door.rent", 2).Equals(new Color32(255, 199, 126, 255)), "Changing the lead time did not highlight rent at the boundary");
            PlayerStore.Instance.dayUntilRent = 3; Wait(4); return false;
        }
        if (step == 4)
        {
            Need(ColorOf("door.rent", 3).Equals((Color32)Color.white), "Rent stayed highlighted beyond the configured lead time");
            PlayerStore.Instance.isPayingMortgage = true; PlayerStore.Instance.dayUntilRent = 1;
            Need(!PlayerStore.Instance.IsPayingRent(), "Mortgage fixture still pays rent");
            Wait(5); return false;
        }
        if (step == 5)
        {
            Need(ColorOf("door.mortgage", 1).Equals(new Color32(255, 199, 126, 255)) && Row("door.rent", 1) == null,
                "Mortgage did not replace rent with a highlighted due date");
            PlayerStore.Instance.isPayingMortgage = false; PlayerStore.Instance.IsPropertyPaid = true;
            Wait(6); return false;
        }
        if (step == 6)
        {
            Need(Row("door.mortgage", 1) == null && Row("door.rent", 1) == null, "Paid property retained a rent or mortgage deadline");
            SetOption(false, 2); Wait(7); return false;
        }
        Need(!Resources.FindObjectsOfTypeAll<GameObject>().Any(value => value.name == "ShopAssist_DoorViewport" &&
            value.scene.IsValid() && value.activeInHierarchy), "Disabled F21 retained the deadline board");
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
    private void Wait(int next) { after = Time.unscaledTime + 0.8f; Next(next); }
    private void Next(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
