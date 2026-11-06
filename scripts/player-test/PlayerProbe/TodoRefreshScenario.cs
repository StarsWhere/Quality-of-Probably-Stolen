using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Exercises the native task editor and shutter controls in an isolated QA run.
public sealed class TodoRefreshScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100f;
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 8);
    private string onceName, dailyName, onceId, dailyId, run;
    private Button oldOnce, oldDaily;
    private PlayerStore.StoreState originalState;
    private Vector3 shutterPosition;
    private int day, step, nextFrame;
    private float checkAt;
    private bool phaseChanged;
    private static int fault, failedWrites, successfulWrites, failedReads;
    private static bool failNextRead;

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CustomUIWindow Todo() => CustomUIManager.Instance.GetWindow(Mod + ".todo");
    private static string Raw() => ModHelper.GetModData(Mod, "todo", null);
    private static BatchAScenario.Row[] Rows()
    {
        string raw = Raw();
        if (raw == null) return Array.Empty<BatchAScenario.Row>();
        var book = JsonUtility.FromJson<BatchAScenario.Book>(raw);
        Need(book != null && book.schemaVersion == 1 && book.rows != null, "Saved task book is invalid");
        return book.rows;
    }
    private static BatchAScenario.Row Row(string text) => Rows().Single(value => value.text == text);

    private static bool BeforeWrite(string __0, string __1)
    {
        if (fault == 1 && __0 == Mod && __1 == "todo")
        { fault = 0; failedWrites++; throw new InvalidOperationException("QA injected task write failure"); }
        return true;
    }
    private static void AfterWrite(string __0, string __1)
    {
        if (fault == 2 && __0 == Mod && __1 == "todo") { successfulWrites++; failNextRead = true; }
    }
    private static bool BeforeRead(string __0, string __1)
    {
        if (fault == 2 && failNextRead && __0 == Mod && __1 == "todo")
        { failNextRead = false; failedReads++; throw new InvalidOperationException("QA injected task readback failure"); }
        return true;
    }
    private static void SaveWithFault(int mode)
    {
        const string patchId = "qa.todo-save-failure";
        var harmony = new Harmony(patchId);
        fault = mode; failNextRead = false;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ModHelper), nameof(ModHelper.SetModData), new[] { typeof(string), typeof(string), typeof(string) }),
                prefix: new HarmonyMethod(typeof(TodoRefreshScenario).GetMethod(nameof(BeforeWrite), Static)),
                postfix: new HarmonyMethod(typeof(TodoRefreshScenario).GetMethod(nameof(AfterWrite), Static)));
            if (mode == 2)
                harmony.Patch(AccessTools.Method(typeof(ModHelper), nameof(ModHelper.GetModData), new[] { typeof(string), typeof(string), typeof(string) }),
                    prefix: new HarmonyMethod(typeof(TodoRefreshScenario).GetMethod(nameof(BeforeRead), Static)));
            Todo().Get("task_save").button.onClick.Invoke();
        }
        finally { harmony.UnpatchAll(patchId); fault = 0; failNextRead = false; }
    }

    private static void SetDraft(string text, int repeat)
    {
        Need(Todo()?.IsAlive == true, "The task editor is not open");
        Todo().Get("task_text").SetString(text, true);
        Todo().Get("task_kind").SetIndex(repeat, true);
    }
    private static void RetainedDraft(string text, int repeat)
    {
        Need(Todo()?.IsAlive == true && Todo().Get("task_text").GetString() == text &&
            Todo().Get("task_kind").GetIndex() == repeat &&
            Todo().Get("feedback").GetText() == ModHelper.GetLocalized(Mod, "f17.save_failed"),
            "A failed task save lost the draft or did not report the failure");
    }
    private void ExerciseSaveFailures()
    {
        onceName = "QA retry once " + token; dailyName = "QA retry daily " + token;
        RoutingScenario.OpenOptionAction("f17.open");
        SetDraft(onceName, 0);
        string before = Raw();
        SaveWithFault(1);
        Need(failedWrites == 1 && Raw() == before, "A rejected write changed the original task book");
        RetainedDraft(onceName, 0);
        Todo().Get("task_save").button.onClick.Invoke();
        var once = Row(onceName);
        Need(Rows().Count(value => value.text == onceName) == 1 && once.repeat == 0 && once.checkedDay == 0,
            "Retrying a rejected write did not create exactly one Once task");
        onceId = once.id;

        SetDraft(dailyName, 1);
        SaveWithFault(2);
        Need(successfulWrites == 1 && failedReads == 1, "The write succeeded without injecting the immediate readback failure");
        RetainedDraft(dailyName, 1);
        var written = Row(dailyName);
        Need(Rows().Count(value => value.text == dailyName) == 1 && written.repeat == 1 && written.checkedDay == 0,
            "The successful write was not retained after readback failed");
        string writtenId = written.id;
        Todo().Get("task_save").button.onClick.Invoke();
        var retried = Row(dailyName);
        Need(Rows().Count(value => value.text == dailyName) == 1 && retried.id == writtenId && retried.repeat == 1 &&
            Row(onceName).id == onceId, "Retry after failed readback duplicated or replaced a task");
        dailyId = retried.id;
        Need(Todo().Get("task_text").GetString() == "", "Successful retry did not clear the draft");
        Todo().Close();
    }

    private static Button Task(string id)
    {
        string name = "ShopAssist_DoorTask_" + id;
        return Resources.FindObjectsOfTypeAll<Button>().FirstOrDefault(button => button != null && button.name == name &&
            button.gameObject.scene.IsValid() && button.gameObject.activeInHierarchy);
    }
    private static bool TaskText(string name) => Resources.FindObjectsOfTypeAll<TMP_Text>().Any(text => text != null &&
        text.name == "task-text" && text.gameObject.scene.IsValid() && text.gameObject.activeInHierarchy && text.text == name);
    private void CheckScope()
    {
        Need(PlayerStore.HasInstance && PlayerStore.Instance.runID == run && StoreStation.GetDayCounter() == day &&
            StoreShutterButton.Instance != null && StoreShutterButton.Instance.shutterGameObject.transform.position == shutterPosition,
            "The test changed run, day, or physical shutter position");
    }
    private void RestorePhase()
    {
        if (phaseChanged && PlayerStore.HasInstance && PlayerStore.Instance.runID == run)
            PlayerStore.Instance.storeState = originalState;
        phaseChanged = false;
    }
    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch { RestorePhase(); throw; }
    }
    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Task retry/phase test timed out at step " + step);
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
                if (StoreUIManager.Instance.controlHintOverlay?.activeSelf == true) StoreUIManager.Instance.ToggleControlHintOverlay();
                TradeScenario.SetOptions(true, "f17.enabled");
                TradeScenario.SetOptions(false, "f15.enabled", "f21.enabled", "f17.reminders");
                ExerciseSaveFailures();
                run = PlayerStore.Instance.runID; day = StoreStation.GetDayCounter(); originalState = PlayerStore.Instance.storeState;
                Need(day > 0 && originalState == PlayerStore.StoreState.MORNING, "The fresh QA run is not in its native morning");
                shutterPosition = StoreShutterButton.Instance.shutterGameObject.transform.position;
                Need((shutterPosition - StoreShutterButton.Instance.startPosition).sqrMagnitude < .00001f, "The shutter is not settled closed");
                step = 1; return false;
            case 1:
                if (Task(onceId) == null || Task(dailyId) == null || !TaskText(onceName) || !TaskText(dailyName)) return false;
                oldOnce = Task(onceId); oldDaily = Task(dailyId);
                checkAt = Time.unscaledTime + .65f; step = 2; return false;
            case 2:
                if (Time.unscaledTime < checkAt) return false;
                CheckScope();
                Need(ReferenceEquals(oldOnce, Task(onceId)) && ReferenceEquals(oldDaily, Task(dailyId)) &&
                    Row(onceName).checkedDay == 0 && Row(dailyName).checkedDay == 0,
                    "The unchanged task board did not remain stable before the phase change");
                // Hold the native shutter still to isolate the same-day phase change from the door animation.
                PlayerStore.Instance.storeState = PlayerStore.StoreState.CLOSED; phaseChanged = true;
                nextFrame = Time.frameCount + 1; step = 3; return false;
            case 3:
                if (Time.frameCount < nextFrame) return false;
                CheckScope();
                var nightOnce = Task(onceId); var nightDaily = Task(dailyId);
                Need(nightOnce != null && nightDaily != null && !ReferenceEquals(nightOnce, oldOnce) &&
                    !ReferenceEquals(nightDaily, oldDaily) && TaskText(onceName) && TaskText(dailyName),
                    "An unchanged Once/Daily board kept the old day-phase buttons");
                string before = Raw();
                if (oldOnce != null) oldOnce.onClick.Invoke();
                Need(Raw() == before, "A stale day-phase button changed the night task");
                nightOnce.onClick.Invoke();
                Need(Row(onceName).checkedDay == day && Row(dailyName).checkedDay == 0,
                    "The first night-phase click did not check the Once task");
                nextFrame = Time.frameCount + 1; step = 4; return false;
            case 4:
                if (Time.frameCount < nextFrame) return false;
                CheckScope(); oldDaily = Task(dailyId);
                Need(oldDaily != null && Row(onceName).checkedDay == day && Row(dailyName).checkedDay == 0,
                    "Night task board did not settle after checking Once");
                PlayerStore.Instance.storeState = originalState;
                nextFrame = Time.frameCount + 1; step = 5; return false;
            case 5:
                if (Time.frameCount < nextFrame) return false;
                CheckScope();
                var dayDaily = Task(dailyId);
                Need(dayDaily != null && !ReferenceEquals(dayDaily, oldDaily) && TaskText(dailyName),
                    "The unchanged Daily task kept its old night-phase button");
                string old = Raw();
                if (oldDaily != null) oldDaily.onClick.Invoke();
                Need(Raw() == old, "A stale night-phase button changed the day task");
                dayDaily.onClick.Invoke();
                Need(Row(dailyName).checkedDay == day && Row(onceName).checkedDay == day,
                    "The first day-phase click did not check Daily without changing Once");
                RestorePhase();
                result = "PASS task write/readback failure retries and same-day Once/Daily phase refresh with successful first clicks";
                return true;
            default: throw new InvalidOperationException("Unknown task refresh step");
        }
    }
}
