using System;
using System.Linq;
using System.Text.Json;
using ShopAssist;

internal static class TodoBookTests
{
    private static void Need(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static CustomUIWindow Window => CustomUIManager.LastWindow;

    private static void Add(string name, int repeat)
    {
        Window.Get("task_text").SetString(name);
        Window.Get("task_kind").SetIndex(repeat);
        Window.Click("task_save");
    }

    private static System.Collections.Generic.List<TodoBook.DoorTask> Tasks()
    {
        Need(TodoBook.DoorTasks(out var rows), "Could not read door tasks");
        return rows;
    }

    private static void Done(string text)
    {
        var row = Tasks().Single(item => item.Text == text);
        Need(TodoBook.DoorSetDone(row, true), "Could not check " + text);
    }

    private static void Main()
    {
        var settings = new ShopSettings();
        var book = new TodoBook(settings, message => { });
        book.Start();
        settings.Set("f17.enabled", true);
        settings.actions["f17.open"]();

        Add("one", 0);
        Add("two", 0);
        Add("daily", 1);
        Done("one");
        Done("daily");
        Need(Tasks().Select(row => row.Text).SequenceEqual(new[] { "two", "one", "daily" }),
            "Door tasks did not stably place unfinished tasks first");
        Need(Window.taskOrder.SequenceEqual(new[] { "two", "one", "daily" }),
            "Editor did not use the same unfinished-first ordering");

        Window.Click("task_clear_completed");
        Need(Tasks().Count == 3, "First clear click deleted tasks without confirmation");
        Done("two");
        Window.Click("task_clear_completed");
        Need(Tasks().Select(row => row.Text).SequenceEqual(new[] { "two", "daily" }),
            "Clear removed a newly completed or recurring task");

        Window.Click("task_clear_completed");
        ModHelper.FailWrite = true;
        Window.Click("task_clear_completed");
        Need(Tasks().Any(row => row.Text == "two") && Window.Get("feedback").GetString() == "f17.save_failed",
            "Failed clear did not retain the task and report the error");
        Window.Click("task_clear_completed");
        Need(Tasks().Count == 1 && Tasks()[0].Text == "daily", "Retry after a failed write did not clear the intended task");

        Add("readback", 0);
        Done("readback");
        Window.Click("task_clear_completed");
        ModHelper.FailReadAfterWrite = true;
        Window.Click("task_clear_completed");
        Need(!Tasks().Any(row => row.Text == "readback") && Window.Get("feedback").GetString() == "f17.save_failed",
            "A failed readback did not reload the actual saved state");
        Window.Click("task_clear_completed");
        Need(Tasks().Count == 1 && Tasks()[0].Text == "daily", "Retry after readback failure changed another task");

        PlayerStore.Instance.runID = "limit-run";
        book.Tick();
        string raw = JsonSerializer.Serialize(new { schemaVersion = 1, rows = Enumerable.Range(0, 64).Select(index => new
        {
            id = Guid.NewGuid().ToString("N"), text = "task " + index, repeat = 0, checkedDay = index < 32 ? 1 : 0
        }).ToArray() });
        ModHelper.SetModData(ModEntry.Id, "todo", raw);
        settings.actions["f17.open"]();
        Need(Tasks()[0].Text == "task 32", "Completed tasks still occupied the first door position");
        Window.Get("task_text").SetString("new task");
        Window.Click("task_save");
        Need(Window.Get("feedback").GetString() == "f17.limit", "Task limit was not enforced");
        Window.Click("task_clear_completed");
        Window.Click("task_clear_completed");
        Need(Window.Get("task_text").GetString() == "new task", "Clearing completed tasks lost the new-task draft");
        Window.Click("task_save");
        Need(Tasks().Count == 33 && Tasks().Any(row => row.Text == "new task"), "Clear did not free task capacity");

        PlayerStore.Instance.runID = "damaged-run";
        book.Tick();
        const string damaged = "unreadable checklist";
        ModHelper.SetModData(ModEntry.Id, "todo", damaged);
        settings.actions["f17.open"]();
        Window.Click("task_reset");
        Need(ModHelper.GetModData(ModEntry.Id, "todo", null) == damaged, "Opening recovery changed the original checklist");
        ModHelper.FailWrite = true;
        Window.Click("task_reset_confirm");
        Need(ShopNotice.Last == "f17.reset_failed" && ModHelper.GetModData(ModEntry.Id, "todo", null) == damaged &&
            !TodoBook.DoorTasks(out _), "Failed recovery did not retain the original checklist and remain blocked");
        ModHelper.FailReadAfterKeyWrite = "todo";
        Window.Click("task_reset_confirm");
        Need(ShopNotice.Last == "f17.reset_failed" && ModHelper.GetModData(ModEntry.Id, "todo", null) == damaged &&
            ModHelper.GetModData(ModEntry.Id, "todoUnreadableBackup", null) == damaged && !TodoBook.DoorTasks(out _),
            "Failed readback did not retain the original checklist and its backup");
        Window.Click("task_reset_confirm");
        Need(TodoBook.DoorTasks(out var restored) && restored.Count == 0 && ShopNotice.Last == "f17.reset_done",
            "Confirmed recovery did not start a usable empty checklist");
        Add("new after recovery", 0);
        Need(Tasks().Single().Text == "new after recovery", "Recovered checklist could not be edited");

        PlayerStore.Instance.runID = "changed-damaged-run";
        book.Tick();
        ModHelper.SetModData(ModEntry.Id, "todo", damaged);
        settings.actions["f17.open"]();
        Window.Click("task_reset");
        ModHelper.SetModData(ModEntry.Id, "todo", "changed checklist");
        Window.Click("task_reset_confirm");
        Need(ModHelper.GetModData(ModEntry.Id, "todo", null) == "changed checklist" && ShopNotice.Last == "inv.changed",
            "A changed checklist was overwritten by a stale recovery prompt");

        PlayerStore.Instance.runID = "read-error-run";
        book.Tick();
        ModHelper.SetModData(ModEntry.Id, "todo", damaged);
        ModHelper.FailNextRead();
        settings.actions["f17.open"]();
        Need(Window.buttons.ContainsKey("task_retry") && !Window.buttons.ContainsKey("task_reset"),
            "A storage read error must offer retry without overwriting unknown data");
        Window.Click("task_retry");
        Need(Window.buttons.ContainsKey("task_reset"), "A successful retry did not identify the unreadable checklist");

        book.Dispose();
        Console.WriteLine("PASS task ordering, confirmed scoped clearing, unreadable-data recovery, and capacity");
    }
}
