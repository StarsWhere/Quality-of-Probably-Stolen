using System;
using System.IO;
using System.Linq;
using UnityEngine;

// Exercises F17 through the native settings, tools and checklist controls in two QA saves.
public sealed class BatchAScenario
{
    [Serializable] internal sealed class Row { public string id = null; public string text = null; public int repeat = 0; public int checkedDay = 0; }
    [Serializable] internal sealed class Book { public int schemaVersion = 0; public Row[] rows = null; }
    [Serializable] internal sealed class Context
    {
        public int version = 1;
        public string runA, rowA, textA, runB, rowB, textB;
        public int slotA, slotB, dayA;
    }

    private const string ModId = "starswhere.shopassist";
    private const string WindowId = ModId + ".todo";
    private readonly Context context = new Context();
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 10);
    private QaLoadSlot load;
    private QaNewRun newRun;
    private AreaLabelScenario labels;
    private int step;
    private float stepStarted = Time.realtimeSinceStartup;
    private float captureUntil;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - stepStarted > 75) throw new InvalidOperationException("F17 QA timed out at step " + step);
        switch (step)
        {
            case 0:
            {
                if (MainMenuUIController.Instance == null) return false;
                string path = Path.Combine(Application.persistentDataPath, "qa-latest-run.txt");
                string[] source = File.ReadAllText(path).Trim().Split('|');
                if (source.Length != 4 || source[0] != "1" || !Guid.TryParseExact(source[1], "N", out Guid run) ||
                    !int.TryParse(source[2], out context.slotA) || context.slotA < 0)
                    throw new InvalidOperationException("Invalid F15-created QA run context");
                context.runA = run.ToString("N");
                context.textA = "QA-A-" + token;
                context.textB = "QA-B-" + token;
                load = new QaLoadSlot(context.slotA, run);
                Advance(1, "Loading the first isolated QA save"); return false;
            }
            case 1:
                if (!load.Tick() || !StoreReady()) return false;
                CloseCalendar();
                VerifyOptionsAndCreate();
                CloseWindows();
                labels = new AreaLabelScenario();
                Advance(2, "F17 draft, Apply, add, edit and completion passed"); return false;
            case 2:
                if (!labels.Tick(out string labelResult)) return false;
                Debug.Log("[PLAYER-QA] " + labelResult);
                OpenTodoAndCheck(context.rowA, true);
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    var overlay = StoreUIManager.Instance.controlHintOverlay;
                    if (overlay != null && overlay.activeSelf) StoreUIManager.Instance.ToggleControlHintOverlay();
                    ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-todo.png"));
                    captureUntil = Time.realtimeSinceStartup + 1f;
                }
                Advance(6, "F02 visible label and enable/disable passed"); return false;
            case 6:
                if (Time.realtimeSinceStartup < captureUntil) return false;
                VerifyA();
                PlayerStore.Instance.SaveGame();
                Require(File.Exists(SaveFiles.AbsolutePath(SaveFiles.Slot(context.slotA))), "Native SaveGame did not produce the first QA save");
                CloseWindows();
                StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                newRun = new QaNewRun();
                Advance(3, "Saved the first checklist through PlayerStore.SaveGame"); return false;
            case 3:
            {
                if (!newRun.Tick() || !StoreReady()) return false;
                var player = PlayerStore.Instance;
                Require(Guid.TryParse(player.runID, out Guid run) && run.ToString("N") != context.runA && player.saveSlotId != context.slotA,
                    "New QA run reused the first run identity or slot");
                context.runB = run.ToString("N"); context.slotB = player.saveSlotId;
                Require(ModHelper.GetModData(ModId, "todo", null) == null, "New QA run inherited the first checklist");
                CloseCalendar();
                var options = StoreUIManager.Instance.gameOptionUIManager;
                options.OpenUI();
                var action = ToolsAction("f17.open");
                Require(action?.button != null && action.button.interactable, "Checklist action unavailable in second QA run");
                action.button.onClick.Invoke();
                var book = TodoWindow();
                book.Get("task_text").SetString(context.textB, true);
                book.Get("task_save").button.onClick.Invoke();
                var row = ParseBook().rows.Single(r => r.text == context.textB);
                Require(row.checkedDay == 0 && row.repeat == 0, "Second checklist row has unexpected state");
                context.rowB = row.id;
                PlayerStore.Instance.SaveGame();
                File.WriteAllText(ContextPath(), JsonUtility.ToJson(context));
                CloseWindows();
                StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                load = new QaLoadSlot(context.slotA, Guid.ParseExact(context.runA, "N"));
                Advance(4, "Created and saved a separate second checklist"); return false;
            }
            case 4:
                if (!load.Tick() || !StoreReady()) return false;
                VerifyA();
                CloseCalendar();
                OpenTodoAndCheck(context.rowA, true);
                CloseWindows();
                StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                load = new QaLoadSlot(context.slotB, Guid.ParseExact(context.runB, "N"));
                Advance(5, "Returning to the first QA save preserved its edited, completed task"); return false;
            case 5:
                if (!load.Tick() || !StoreReady()) return false;
                VerifyB();
                result = "PASS F17 draft/Apply, add/edit/check, native SaveGame and same-process A→B→A→B save isolation";
                return true;
            default: throw new InvalidOperationException("Unknown F17 QA step");
        }
    }

    private void VerifyOptionsAndCreate()
    {
        var config = ModConfig.Find(ModId);
        var entry = config?.Get("f17.enabled") as ConfigEntry<bool>;
        Require(entry != null, "F17 setting was not registered");
        var options = StoreUIManager.Instance.gameOptionUIManager;
        options.OpenUI();
        var window = CustomUIManager.Instance.GetWindow("game_options");
        var toggle = window?.Get(ModId + ":f17.enabled");
        var apply = window?.Get("apply");
        Require(toggle != null && window?.Get(ModId + ":tools")?.button != null && apply?.button != null, "F17 native controls are absent");
        if (entry.Value) { toggle.SetBool(false, true); apply.button.onClick.Invoke(); }
        var action = ToolsAction("f17.open");
        Require(action?.button != null, "F17 tools control is absent");
        Require(!entry.Value && !action.button.interactable, "F17 was not disabled after Apply");
        toggle.SetBool(true, true);
        Require(!entry.Value && !action.button.interactable, "F17 draft took effect before Apply");
        action.button.onClick.Invoke();
        Require(CustomUIManager.Instance.GetWindow(WindowId) == null, "Disabled checklist opened before Apply");
        apply.button.onClick.Invoke();
        action = ToolsAction("f17.open");
        Require(entry.Value && action.button.interactable, "F17 Apply did not enable its action");
        action.button.onClick.Invoke();
        var book = TodoWindow();
        book.Get("task_text").SetString(context.textA, true);
        Require(book.Get("task_text").GetString() == context.textA, "Checklist input rejected the test text");
        book.Get("task_kind").SetIndex(1, true);
        book.Get("task_save").button.onClick.Invoke();
        Debug.Log("[PLAYER-QA] after add: raw=" + (ModHelper.GetModData(ModId, "todo", null) ?? "<null>") +
            "; feedback=" + (CustomUIManager.Instance.GetWindow(WindowId)?.Get("feedback")?.GetText() ?? "<none>"));
        var created = ParseBook().rows.Single(r => r.text == context.textA);
        Require(created.repeat == 1 && created.checkedDay == 0 && Guid.TryParseExact(created.id, "N", out _), "Created task has unexpected data");
        context.rowA = created.id;
        context.textA += "-edited";
        book = TodoWindow();
        book.Get("edit_" + context.rowA).button.onClick.Invoke();
        book = TodoWindow();
        book.Get("task_text").SetString(context.textA, true);
        book.Get("task_save").button.onClick.Invoke();
        book = TodoWindow();
        book.Get("done_" + context.rowA).SetBool(true, true);
        context.dayA = StoreStation.GetDayCounter();
        VerifyA();
    }

    private void VerifyA()
    {
        var rows = ParseBook().rows;
        var row = rows.Single(r => r.id == context.rowA);
        Require(row.text == context.textA && row.repeat == 1 && row.checkedDay == context.dayA, "First checklist lost its edited/completed state");
        Require(!rows.Any(r => r.text == context.textB), "Second checklist leaked into the first");
    }

    private void VerifyB()
    {
        var rows = ParseBook().rows;
        var row = rows.Single(r => r.id == context.rowB);
        Require(row.text == context.textB && row.checkedDay == 0 && row.repeat == 0, "Second checklist state changed");
        Require(!rows.Any(r => r.text == context.textA), "First checklist leaked into the second");
    }

    internal static Book ParseBook()
    {
        string raw = ModHelper.GetModData(ModId, "todo", null);
        var book = string.IsNullOrEmpty(raw) ? null : JsonUtility.FromJson<Book>(raw);
        Require(book != null && book.schemaVersion == 1 && book.rows != null, "Checklist data is missing or invalid");
        return book;
    }

    private static CustomUIWindow TodoWindow()
    {
        var window = CustomUIManager.Instance.GetWindow(WindowId);
        Require(window != null && window.IsAlive, "Checklist window did not open");
        return window;
    }

    internal static void OpenTodoAndCheck(string rowId, bool expected)
    {
        var options = StoreUIManager.Instance.gameOptionUIManager;
        options.OpenUI();
        var action = ToolsAction("f17.open");
        Require(action?.button != null && action.button.interactable, "Checklist action unavailable");
        action.button.onClick.Invoke();
        Require(TodoWindow().Get("done_" + rowId).GetBool() == expected, "Checklist control does not reflect saved completion");
    }

    internal static CustomUIElement ToolsAction(string key)
    {
        var options = CustomUIManager.Instance.GetWindow("game_options");
        var open = options?.Get(ModId + ":tools")?.button;
        Require(open != null && open.interactable, "Shop tools entry is unavailable");
        open.onClick.Invoke();
        var tools = CustomUIManager.Instance.GetWindow(ModId + ".tools");
        Require(tools?.IsAlive == true, "Shop tools panel did not open");
        tools.Get("show_disabled").SetBool(true, true);
        for (int category = 0; category < 4; category++)
        {
            tools.Get("category_" + category).button.onClick.Invoke();
            tools = CustomUIManager.Instance.GetWindow(ModId + ".tools");
            CustomUIElement action = tools?.Get(ModId + ":" + key);
            if (action != null) return action;
        }
        return null;
    }

    private static bool StoreReady() => PlayerStore.HasInstance && EmporiumEntry.Instance != null &&
        EmporiumEntry.Instance.storeInteractables != null && EmporiumEntry.Instance.storeInteractables.activeInHierarchy &&
        StoreUIManager.Instance != null && StoreUIManager.Instance.gameOptionUIManager != null && CustomUIManager.Instance != null;

    private static void CloseCalendar()
    {
        var calendar = AdvCalendarUIManager.Instance;
        if (calendar != null && calendar.UIPanel != null && calendar.UIPanel.activeSelf && StoreUIManager.Instance.storeCalendar != null)
            StoreUIManager.Instance.storeCalendar.OnClick();
    }

    private static void CloseWindows()
    {
        CustomUIManager.Instance.GetWindow(WindowId)?.Close();
        StoreUIManager.Instance.gameOptionUIManager.CloseUI();
    }

    internal static string ContextPath() => Path.Combine(Application.persistentDataPath, "qa-batch-a.json");
    private void Advance(int next, string message) { Debug.Log("[PLAYER-QA] " + message); step = next; stepStarted = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
