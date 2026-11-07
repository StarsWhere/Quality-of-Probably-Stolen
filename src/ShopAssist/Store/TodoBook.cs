using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    internal sealed class TodoBook : IDisposable
    {
        internal readonly struct DoorTask
        {
            internal DoorTask(string id, string text, bool done, string run, int slot, int day, bool night)
            { Id = id; Text = text; Done = done; Run = run; Slot = slot; Day = day; Night = night; }
            internal string Id { get; }
            internal string Text { get; }
            internal bool Done { get; }
            internal string Run { get; }
            internal int Slot { get; }
            internal int Day { get; }
            internal bool Night { get; }
        }

        private static TodoBook active;
        internal static event Action DoorChanged;
        private const string WindowId = ModEntry.Id + ".todo";
        private const string DataKey = "todo";
        private const string RecoveryKey = "todoUnreadableBackup";
        private const int MaxRows = 64, MaxText = 96;

        // Numeric values are part of the stored schema.
        private enum Repeat { Once = 0, Daily = 1, Day = 2, Night = 3 }

        [Serializable]
        private sealed class TaskRow
        {
            public string id;
            public string text;
            public Repeat repeat;
            public int checkedDay;
        }

        [Serializable]
        private sealed class Book
        {
            [JsonRequired] public int schemaVersion = 1;
            [JsonRequired] public List<TaskRow> rows = new List<TaskRow>();
        }

        private readonly ConfigEntry<bool> enabled, reminders;
        private readonly Func<KeyCode> key;
        private readonly Action<string> log;
        private readonly ShopSettings settings;
        private CustomUIWindow window;
        private string run;
        private int slot, day;
        private bool night, allPhases, confirmingDelete;
        private bool reorderPending;
        private bool confirmingReset;
        private string unreadableRaw;
        private HashSet<string> clearIds;
        private string reminderRun;
        private int reminderSlot, reminderDay;
        private bool reminderNight;
        private string editId, draftId, draft = "";
        private Repeat draftRepeat;
        private Book book;
        private Book cachedBook;
        private Exception cachedReadError;
        private string cachedRaw, cachedRun;
        private int cachedSlot;
        private string doorReadError;

        internal TodoBook(ShopSettings settings, Action<string> log)
        {
            this.settings = settings;
            this.log = log;
            enabled = settings.Toggle("f17.enabled", "f17.tip");
            reminders = settings.Toggle("f17.reminders", "f17.reminders.tip");
            key = settings.Key("f17.key", "f17.key.tip");
            settings.Action("f17.open", Open, CanOpen);
        }

        internal void Start()
        {
            active = this;
            settings.Changed += OnSettings;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        private bool CanOpen() => enabled.Value && PlayerStore.HasInstance &&
            PlayerStore.Instance.playerLocation == PlayerStore.PlayerLocation.IN_STORE &&
            !string.IsNullOrEmpty(PlayerStore.Instance.runID);

        private void OnSettings() { if (!enabled.Value) Close(); }
        private void SceneChanged(Scene before, Scene after) => Reset();

        internal void Tick()
        {
            RemindOnPhaseChange();
            if (window != null)
            {
                if (!window.IsAlive || !SameRun()) { Close(); return; }
                if (day != StoreStation.GetDayCounter() || night != IsNight()) Render();
                else if (reorderPending && !StoreUIManager.IsAnyTyping() && !UIDragHandler.isDraggingAnyUI &&
                    !Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2) &&
                    !Input.GetMouseButtonUp(0) && !Input.GetMouseButtonUp(1) && !Input.GetMouseButtonUp(2))
                { reorderPending = false; Render(); }
            }
            if (!enabled.Value || key() == KeyCode.None || !Input.GetKeyDown(key()) || !ShopInput.Ready()) return;
            if (window != null) Close();
            else Open();
        }

        private static bool IsNight() => PlayerStore.Instance.storeState == PlayerStore.StoreState.CLOSED;
        private void RemindOnPhaseChange()
        {
            if (!reminders.Value || !CanOpen()) { reminderRun = null; return; }
            var p = PlayerStore.Instance;
            int today = StoreStation.GetDayCounter();
            bool nowNight = IsNight();
            bool same = reminderRun == p.runID && reminderSlot == p.saveSlotId;
            if (same && today == reminderDay && nowNight == reminderNight) return;
            reminderRun = p.runID; reminderSlot = p.saveSlotId; reminderDay = today; reminderNight = nowNight;
            if (!same || today < 1) return;
            try
            {
                var outstanding = ReadBook().rows.FindAll(r => (r.repeat == Repeat.Once || r.repeat == Repeat.Daily ||
                    r.repeat == (nowNight ? Repeat.Night : Repeat.Day)) &&
                    (r.checkedDay == 0 || (r.repeat != Repeat.Once && r.checkedDay != today)));
                if (outstanding.Count > 0) ShopNotice.Show(ShopSettings.Format("f17.reminder", outstanding.Count, outstanding[0].text));
            }
            catch (Exception error) { log("F17 reminder could not read the current task book: " + error.Message); }
        }
        private bool SameRun() => CanOpen() && slot == PlayerStore.Instance.saveSlotId && run == PlayerStore.Instance.runID;

        // Both the wall board and the editor use the same validated per-run book.
        internal static bool DoorAvailable => active != null && active.CanOpen();

        internal static bool DoorTasks(out List<DoorTask> rows)
        {
            rows = new List<DoorTask>();
            var owner = active;
            if (owner == null || !owner.CanOpen()) return false;
            try
            {
                Book source = owner.ReadBook();
                int today = StoreStation.GetDayCounter();
                if (today < 1) return false;
                bool isNight = IsNight();
                foreach (TaskRow row in source.rows)
                {
                    if (row.repeat != Repeat.Once && row.repeat != Repeat.Daily &&
                        row.repeat != (isNight ? Repeat.Night : Repeat.Day)) continue;
                    rows.Add(new DoorTask(row.id, row.text,
                        row.checkedDay > 0 && (row.repeat == Repeat.Once || row.checkedDay == today),
                        PlayerStore.Instance.runID, PlayerStore.Instance.saveSlotId, today, isNight));
                }
                rows = rows.OrderBy(row => row.Done).ToList();
                owner.doorReadError = null;
                return true;
            }
            catch (Exception error)
            {
                string message = PlayerStore.Instance.runID + ":" + PlayerStore.Instance.saveSlotId + ":" + error.Message;
                if (owner.doorReadError != message) owner.log("F17 could not read tasks for the door: " + error.Message);
                owner.doorReadError = message;
                return false;
            }
        }

        internal static bool DoorSetDone(DoorTask task, bool done)
        {
            var owner = active;
            if (owner == null || !owner.CanOpen() || string.IsNullOrEmpty(task.Id) ||
                PlayerStore.Instance.runID != task.Run || PlayerStore.Instance.saveSlotId != task.Slot ||
                StoreStation.GetDayCounter() != task.Day || IsNight() != task.Night) return false;
            try
            {
                Book source = CopyBook(owner.ReadBook());
                TaskRow row = source.rows.Find(item => item.id == task.Id);
                int today = StoreStation.GetDayCounter();
                if (row == null || today < 1 || (row.repeat != Repeat.Once && row.repeat != Repeat.Daily &&
                    row.repeat != (task.Night ? Repeat.Night : Repeat.Day))) return false;
                bool currentDone = row.checkedDay > 0 && (row.repeat == Repeat.Once || row.checkedDay == today);
                if (currentDone != task.Done) return false;
                row.checkedDay = done ? today : 0;
                owner.WriteBook(source);
                DoorChanged?.Invoke();
                if (owner.window != null && owner.window.IsAlive && owner.SameRun())
                {
                    owner.book = source;
                    owner.Render();
                }
                return true;
            }
            catch (Exception error)
            {
                owner.log("F17 could not update the door task: " + error.Message);
                return false;
            }
        }

        internal static void OpenForDoor() => active?.Open();

        private void Open()
        {
            if (!CanOpen() || CustomUIManager.Instance == null) return;
            if (window != null && window.IsAlive && SameRun())
            {
                window.root.transform.SetAsLastSibling();
                return;
            }
            Close();
            run = PlayerStore.Instance.runID;
            slot = PlayerStore.Instance.saveSlotId;
            try
            {
                book = ReadBook();
            }
            catch (Exception error)
            {
                book = null;
                unreadableRaw = cachedRun == run && cachedSlot == slot && cachedReadError != null ? cachedRaw : null;
                log("F17 kept unreadable task data in slot " + slot + ": " + error.Message);
            }
            Render();
        }

        private static Book Parse(string raw)
        {
            var parsed = ShopData.Decode<Book>(raw, 65536);
            if (parsed == null || parsed.schemaVersion != 1 || parsed.rows == null || parsed.rows.Count > MaxRows)
                throw new FormatException("Invalid schema or task count");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in parsed.rows)
            {
                if (row == null || !Guid.TryParseExact(row.id, "N", out _) || !ids.Add(row.id) ||
                    string.IsNullOrWhiteSpace(row.text) || row.text.Length > MaxText ||
                    !Enum.IsDefined(typeof(Repeat), row.repeat) || row.checkedDay < 0)
                    throw new FormatException("Invalid task");
            }
            return parsed;
        }

        private Book ReadBook()
        {
            var owner = PlayerStore.Instance;
            string raw = ModHelper.GetModData(ModEntry.Id, DataKey, null);
            if (cachedRun == owner.runID && cachedSlot == owner.saveSlotId && cachedRaw == raw)
            {
                if (cachedReadError != null) throw cachedReadError;
                if (cachedBook != null) return cachedBook;
            }
            cachedRaw = raw; cachedRun = owner.runID; cachedSlot = owner.saveSlotId;
            try { cachedBook = raw == null ? new Book() : Parse(raw); cachedReadError = null; return cachedBook; }
            catch (Exception error) { cachedBook = null; cachedReadError = error; throw; }
        }

        private void WriteBook(Book value)
        {
            try
            {
                string raw = ShopData.Encode(value);
                ModHelper.SetModData(ModEntry.Id, DataKey, raw);
                if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != raw) throw new InvalidOperationException("Task book was not saved.");
                cachedRaw = raw; cachedRun = PlayerStore.Instance.runID; cachedSlot = PlayerStore.Instance.saveSlotId; cachedBook = value; cachedReadError = null;
            }
            catch { cachedBook = null; cachedRaw = cachedRun = null; cachedReadError = null; throw; }
        }

        private static Book CopyBook(Book source)
        {
            var copy = new Book();
            foreach (TaskRow row in source.rows)
                copy.rows.Add(new TaskRow { id = row.id, text = row.text, repeat = row.repeat, checkedDay = row.checkedDay });
            return copy;
        }

        private bool TryEdit(out Book copy)
        {
            try { copy = CopyBook(ReadBook()); return true; }
            catch (Exception error) { copy = null; log("F17 could not read tasks for editing: " + error); Feedback("f17.invalid"); return false; }
        }

        private bool IsDone(TaskRow row) => row.checkedDay > 0 && (row.repeat == Repeat.Once || row.checkedDay == day);

        private void RetryUnreadable()
        {
            if (!SameRun()) return;
            confirmingReset = false;
            try { book = ReadBook(); unreadableRaw = null; }
            catch (Exception error)
            {
                book = null;
                unreadableRaw = cachedRun == run && cachedSlot == slot && cachedReadError != null ? cachedRaw : null;
                log("F17 retry could not read the task book: " + error.Message);
            }
            Render();
        }

        private void ResetUnreadable()
        {
            if (!SameRun() || !confirmingReset || book != null || unreadableRaw == null) return;
            string original = unreadableRaw;
            bool attemptedWrite = false;
            try
            {
                if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != original)
                { ShopNotice.Show(ShopSettings.Text("inv.changed")); RetryUnreadable(); return; }
                ModHelper.SetModData(ModEntry.Id, RecoveryKey, original);
                if (ModHelper.GetModData(ModEntry.Id, RecoveryKey, null) != original)
                    throw new InvalidOperationException("Task backup was not retained.");
                if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != original)
                { ShopNotice.Show(ShopSettings.Text("inv.changed")); RetryUnreadable(); return; }
                var replacement = new Book();
                attemptedWrite = true;
                WriteBook(replacement);
                book = replacement; unreadableRaw = null; confirmingReset = false;
                DoorChanged?.Invoke();
                ShopNotice.Show(ShopSettings.Text("f17.reset_done"));
            }
            catch (Exception error)
            {
                log("F17 could not confirm the task reset: " + error);
                if (attemptedWrite)
                {
                    try
                    {
                        ModHelper.SetModData(ModEntry.Id, DataKey, original);
                        if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != original) throw new InvalidOperationException("Original task data was not retained.");
                    }
                    catch (Exception restoreError) { unreadableRaw = null; log("F17 could not confirm restoring task data: " + restoreError); }
                }
                cachedBook = null; cachedRaw = cachedRun = null; cachedReadError = null;
                book = null;
                if (unreadableRaw == null) confirmingReset = false;
                ShopNotice.Show(ShopSettings.Text("f17.reset_failed"));
            }
            Render();
        }
        private bool Visible(TaskRow row) => allPhases || row.repeat == Repeat.Once || row.repeat == Repeat.Daily ||
            row.repeat == (night ? Repeat.Night : Repeat.Day);

        private void Render()
        {
            if (!SameRun() || CustomUIManager.Instance == null) { Close(); return; }
            Vector2? pos = window != null && window.IsAlive ? ((RectTransform)window.root.transform).anchoredPosition : (Vector2?)null;
            day = StoreStation.GetDayCounter();
            night = IsNight();
            var builder = CustomUIManager.Window(WindowId, ShopSettings.Text("f17.enabled"), CustomUIManager.LAYER_META)
                .SetSize(540, 410).SetDraggable(true).SetCloseOnEscape(true);
            window = builder.GetWindow();
            reorderPending = false;
            var thisView = window;
            builder.OnClose(() => { if (ReferenceEquals(window, thisView)) window = null; });
            if (book == null)
            {
                builder.AddLabel(ShopSettings.Text("f17.invalid"));
                if (confirmingReset)
                {
                    builder.AddLabel(ShopSettings.Text("f17.reset_warning"));
                    builder.BeginRow().AddButton(ShopSettings.Text("f17.reset_confirm"), ResetUnreadable, "task_reset_confirm")
                        .AddButton(ShopSettings.Text("f17.cancel"), () => { confirmingReset = false; Render(); }, "task_reset_cancel").End();
                }
                else
                {
                    builder.BeginRow().AddButton(ShopSettings.Text("f17.retry"), RetryUnreadable, "task_retry");
                    if (unreadableRaw != null)
                        builder.AddButton(ShopSettings.Text("f17.reset"), () => { confirmingReset = true; Render(); }, "task_reset");
                    builder.End();
                }
                builder.Show();
                return;
            }
            string[] kinds = { ShopSettings.Text("f17.once"), ShopSettings.Text("f17.daily"),
                ShopSettings.Text("f17.day"), ShopSettings.Text("f17.night") };
            builder.AddLabel(ShopSettings.Format("f17.when", day, night ? kinds[3] : kinds[2]))
                .BeginRow().AddInput(ShopSettings.Text("f17.placeholder"), draft, v => draft = v, "task_text", v => draft = v);
            builder.Last.input.characterLimit = MaxText;
            builder.Last.SetFlexible(1, 0);
            builder.AddDropdown(kinds, (int)draftRepeat, i => draftRepeat = (Repeat)i, "task_kind").End();
            builder.BeginRow().AddButton(ShopSettings.Text(editId == null ? "f17.add" : "f17.save"), SaveDraft, "task_save");
            if (editId != null)
            {
                builder.AddButton(ShopSettings.Text("f17.cancel"), () => { ClearDraft(); Render(); });
                builder.AddButton(ShopSettings.Text(confirmingDelete ? "f17.confirmDelete" : "f17.delete"), DeleteDraft);
            }
            builder.AddFlexibleSpace().AddToggle(ShopSettings.Text("f17.all"), allPhases, value =>
            {
                allPhases = value;
                Render();
            }).End();
            int completedOnce = book.rows.Count(row => row.repeat == Repeat.Once && row.checkedDay > 0);
            if (completedOnce > 0 || clearIds != null)
            {
                builder.BeginRow().AddButton(clearIds == null ? ShopSettings.Format("f17.clear_completed", completedOnce) :
                    ShopSettings.Format("f17.confirm_clear", clearIds.Count), ClearCompleted, "task_clear_completed");
                if (clearIds != null) builder.AddButton(ShopSettings.Text("f17.cancel"), () => { clearIds = null; Render(); }, "task_cancel_clear");
                builder.End();
            }
            builder.AddLabel("", "feedback").BeginScrollFlexible(110);
            bool any = false;
            foreach (var row in book.rows.OrderBy(IsDone))
            {
                if (!Visible(row)) continue;
                any = true;
                builder.BeginRow().AddToggle(row.text, IsDone(row), value =>
                {
                    if (!SameRun()) { Close(); return; }
                    if (TryEdit(out Book next))
                    {
                        TaskRow changed = next.rows.Find(item => item.id == row.id);
                        if (changed != null)
                        {
                            changed.checkedDay = value ? day : 0;
                            if (Persist(next)) { row.checkedDay = changed.checkedDay; reorderPending = true; return; }
                        }
                    }
                    window.Get("done_" + row.id).SetBool(IsDone(row), false);
                }, "done_" + row.id);
                // Task names are player input, not TextMeshPro markup.
                if (builder.Last.text != null) builder.Last.text.richText = false;
                builder.AddFlexibleSpace().AddLabel(kinds[(int)row.repeat])
                    .AddButton(ShopSettings.Text("f17.edit"), () =>
                    {
                        editId = row.id;
                        draft = row.text;
                        draftRepeat = row.repeat;
                        confirmingDelete = false;
                        Render();
                    }, "edit_" + row.id).End();
            }
            if (!any) builder.AddLabel(ShopSettings.Text("f17.empty"));
            builder.End().Show();
            if (pos.HasValue) ((RectTransform)window.root.transform).anchoredPosition = pos.Value;
        }

        private void SaveDraft()
        {
            if (!SameRun()) { Close(); return; }
            string text = window.Get("task_text").GetString().Trim();
            if (text.Length == 0 || text.Length > MaxText) { Feedback("f17.enter"); return; }
            if (!TryEdit(out Book next)) return;
            TaskRow row;
            if (editId == null)
            {
                if (draftId == null) draftId = Guid.NewGuid().ToString("N");
                row = next.rows.Find(item => item.id == draftId);
                if (row == null)
                {
                    if (next.rows.Count >= MaxRows) { Feedback("f17.limit"); return; }
                    row = new TaskRow { id = draftId };
                    next.rows.Add(row);
                }
            }
            else
            {
                row = next.rows.Find(item => item.id == editId);
                if (row == null) { ClearDraft(); Render(); return; }
                if (row.repeat != draftRepeat) row.checkedDay = 0;
            }
            row.text = text;
            row.repeat = draftRepeat;
            if (!Persist(next)) return;
            ClearDraft();
            Render();
        }

        private void DeleteDraft()
        {
            if (!SameRun() || editId == null) { Close(); return; }
            if (!confirmingDelete) { confirmingDelete = true; Render(); return; }
            if (!TryEdit(out Book next)) return;
            next.rows.RemoveAll(row => row.id == editId);
            if (!Persist(next)) return;
            ClearDraft();
            Render();
        }

        private void ClearCompleted()
        {
            if (!SameRun()) { Close(); return; }
            if (!TryEdit(out Book next)) return;
            if (clearIds == null)
            {
                clearIds = new HashSet<string>(next.rows.Where(row => row.repeat == Repeat.Once && row.checkedDay > 0).Select(row => row.id));
                if (clearIds.Count == 0) { clearIds = null; book = next; Render(); return; }
                book = next;
                Render();
                return;
            }
            next.rows.RemoveAll(row => clearIds.Contains(row.id) && row.repeat == Repeat.Once && row.checkedDay > 0);
            if (!Persist(next))
            {
                // Verification can fail after the write. Read the authoritative copy
                // before showing the list again and retain the same confirmation IDs.
                try { book = ReadBook(); DoorChanged?.Invoke(); }
                catch (Exception error) { log("F17 could not reload tasks after clearing: " + error); return; }
                Render();
                Feedback("f17.save_failed");
                return;
            }
            if (editId != null && clearIds.Contains(editId) && !next.rows.Any(row => row.id == editId)) ClearDraft();
            clearIds = null;
            Render();
        }

        private void Feedback(string key) => window.Get("feedback").SetText(ShopSettings.Text(key));
        private bool Persist(Book next)
        {
            try { WriteBook(next); }
            catch (Exception error) { log("F17 could not save tasks: " + error); Feedback("f17.save_failed"); return false; }
            book = next;
            DoorChanged?.Invoke();
            return true;
        }
        private void ClearDraft() { editId = draftId = null; draft = ""; draftRepeat = Repeat.Once; confirmingDelete = false; }
        private void Close()
        {
            if (window != null && window.IsAlive) window.Close();
            window = null;
            book = null;
            run = null;
            allPhases = false;
            clearIds = null; reorderPending = false;
            ClearDraft(); confirmingReset = false; unreadableRaw = null;
        }

        private void Reset() { Close(); cachedBook = null; cachedRaw = cachedRun = null; cachedReadError = null; }

        public void Dispose()
        {
            if (active == this) active = null;
            settings.Changed -= OnSettings;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
        }
    }
}
