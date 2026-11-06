using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    // Entries are supplied by the player; none are inferred from the item's hidden state.
    internal sealed class AppraisalWorksheet : IDisposable
    {
        private const string WindowId = "shopassist.f23.worksheet";
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<bool> calculate;
        private readonly Func<KeyCode> key;
        private readonly Dictionary<GameItem, Sheet> sheets = new Dictionary<GameItem, Sheet>();
        private CustomUIWindow window;
        private CustomUIElement totalLabel;
        private CustomUIElement applyButton;
        private CustomUIElement undoButton;
        private StoreClientInstance client;
        private GameItem item;
        private string run;
        private int slot;
        private string draftName = "";
        private string draftAmount = "";
        private bool started;

        private sealed class Row
        {
            internal string Name, Amount;
            internal bool Selected;
            internal Row Copy() => new Row { Name = Name, Amount = Amount, Selected = Selected };
        }
        private sealed class Snapshot
        {
            internal string Base;
            internal List<Row> Rows;
        }
        private sealed class Sheet
        {
            internal string Base = "";
            internal List<Row> Rows = new List<Row>();
            internal Stack<Snapshot> Undo = new Stack<Snapshot>();
            internal void Remember()
            {
                if (Undo.Count >= 100)
                {
                    Snapshot[] recent = Undo.ToArray();
                    Undo.Clear();
                    for (int i = Math.Min(recent.Length, 99) - 1; i >= 0; i--) Undo.Push(recent[i]);
                }
                var rows = new List<Row>();
                foreach (Row row in Rows) rows.Add(row.Copy());
                Undo.Push(new Snapshot { Base = Base, Rows = rows });
            }
            internal void Restore()
            {
                if (Undo.Count == 0) return;
                Snapshot previous = Undo.Pop(); Base = previous.Base; Rows = previous.Rows;
            }
        }

        internal AppraisalWorksheet(ShopSettings settings, Action<string> log)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (log == null) throw new ArgumentNullException(nameof(log));
            enabled = settings.Toggle("f23.enabled", "f23.tip");
            calculate = settings.Toggle("f23.calculate", "f23.calculate.tip");
            key = settings.Key("f23.key", "f23.key.tip");
            settings.Action("f23.open", Open, Available);
        }

        public void Start()
        {
            if (started) return;
            started = true;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        public void Tick()
        {
            if (!started) return;
            if (!enabled.Value) { Reset(); return; }
            var activeClient = PlayerStore.HasInstance ? PlayerStore.Instance.currentClientInstance : null;
            if (client != null && (!PlayerStore.HasInstance || activeClient != client || PlayerStore.Instance.runID != run ||
                PlayerStore.Instance.saveSlotId != slot)) Reset();
            if (window != null && (!Available() || !ReferenceEquals(InspectionUIManager.Instance.currentInspectingItem, item))) Close();
            KeyCode binding = key();
            if (binding != KeyCode.None && ShopInput.Ready() && Input.GetKeyDown(binding)) Open();
            if (window != null) RefreshSummary();
        }

        private bool Available()
        {
            var ui = InspectionUIManager.Instance;
            var current = PlayerStore.HasInstance ? PlayerStore.Instance.currentClientInstance : null;
            return enabled.Value && CustomUIManager.Instance != null && ui != null && ui.panel != null && ui.panel.activeInHierarchy &&
                ui.appraisalPanel != null && ui.appraisalPanel.activeInHierarchy && ui.currentInspectingItem != null &&
                ui.currentInspectingItem.IsTag("APPRAISAL_ITEM") && current?.GetClientBlueprint()?.clientIntent == StoreClient.ClientIntent.APPRAISAL_SERVICE;
        }

        private void Open()
        {
            if (!Available()) return;
            if (window != null && window.IsAlive) { Close(); return; }
            var activeClient = PlayerStore.Instance.currentClientInstance;
            if (client != activeClient || run != PlayerStore.Instance.runID || slot != PlayerStore.Instance.saveSlotId)
            {
                Reset(); client = activeClient; run = PlayerStore.Instance.runID; slot = PlayerStore.Instance.saveSlotId;
            }
            item = InspectionUIManager.Instance.currentInspectingItem;
            if (!sheets.ContainsKey(item)) sheets.Add(item, new Sheet());
            draftName = draftAmount = "";
            Build();
        }

        private void Build()
        {
            if (!Available() || item == null || !ReferenceEquals(item, InspectionUIManager.Instance.currentInspectingItem) || !sheets.TryGetValue(item, out Sheet sheet)) return;
            var builder = CustomUIManager.Instance.CreateWindow(WindowId, ShopSettings.Text("f23.title"), CustomUIManager.LAYER_META)
                .SetSize(620f, 460f).SetDraggable(true).SetCloseOnEscape().OnClose(() => { window = null; totalLabel = null; applyButton = null; undoButton = null; });
            ItemGrid.Add(builder, new[] { new ItemGrid.Entry(item) { Caption = item.GetDisplayName(true) } },
                new ItemGrid.Options { Cards = true, Columns = 1, CardWidth = 180, CardHeight = 80 });
            builder.BeginRow();
            builder.AddInput(ShopSettings.Text("f23.base"), sheet.Base, text =>
            {
                string value = Limit(text, 12);
                if (sheet.Base == value) return;
                sheet.Remember(); sheet.Base = value; RefreshSummary();
            }, "base");
            if (builder.Last.input != null) builder.Last.input.characterLimit = 12;
            builder.End();
            builder.BeginRow();
            builder.AddInput(ShopSettings.Text("f23.feature"), draftName, text => draftName = Limit(text, 60), "feature", text => draftName = Limit(text, 60));
            if (builder.Last.input != null) builder.Last.input.characterLimit = 60;
            builder.AddInput(ShopSettings.Text("f23.amount"), draftAmount, text => draftAmount = Limit(text, 12), "amount", text => draftAmount = Limit(text, 12));
            if (builder.Last.input != null) builder.Last.input.characterLimit = 12;
            builder.AddButton(ShopSettings.Text("f23.add"), () => Add(sheet), "add");
            builder.End();
            builder.BeginScrollFlexible(120f);
            for (int i = 0; i < sheet.Rows.Count; i++)
            {
                int index = i;
                Row row = sheet.Rows[index];
                builder.BeginRow();
                string text = row.Name + (string.IsNullOrWhiteSpace(row.Amount) ? "" : "  " + row.Amount);
                builder.AddToggle(text, row.Selected, value =>
                {
                    if (!Current(sheet) || index >= sheet.Rows.Count || sheet.Rows[index].Selected == value) return;
                    sheet.Remember(); sheet.Rows[index].Selected = value; RefreshSummary();
                }, "row_" + index);
                builder.AddFlexibleSpace();
                builder.AddButton("×", () =>
                {
                    if (!Current(sheet) || index >= sheet.Rows.Count) return;
                    sheet.Remember(); sheet.Rows.RemoveAt(index); Rebuild();
                }, "remove_" + index);
                builder.End();
            }
            builder.End();
            builder.BeginRow();
            builder.AddLabel("", "total"); totalLabel = builder.Last;
            builder.AddFlexibleSpace();
            builder.AddButton(ShopSettings.Text("f23.undo"), () => { if (Current(sheet)) { sheet.Restore(); Rebuild(); } }, "undo"); undoButton = builder.Last;
            builder.AddButton(ShopSettings.Text("f23.apply"), Apply, "apply"); applyButton = builder.Last;
            builder.End();
            window = builder.Center().Show();
            RefreshSummary();
        }

        private void Add(Sheet sheet)
        {
            if (!Current(sheet) || sheet.Rows.Count >= 100) return;
            string name = Limit(draftName, 60).Trim();
            if (name.Length == 0) return;
            sheet.Remember(); sheet.Rows.Add(new Row { Name = name, Amount = Limit(draftAmount, 12).Trim(), Selected = true });
            draftName = draftAmount = ""; Rebuild();
        }

        private bool Current(Sheet sheet) => Available() && item != null && ReferenceEquals(item, InspectionUIManager.Instance.currentInspectingItem) &&
            ReferenceEquals(client, PlayerStore.Instance.currentClientInstance) && run == PlayerStore.Instance.runID && slot == PlayerStore.Instance.saveSlotId &&
            sheets.TryGetValue(item, out Sheet current) && ReferenceEquals(current, sheet);

        private bool TryTotal(Sheet sheet, out int total)
        {
            total = 0;
            if (!calculate.Value || !int.TryParse(sheet.Base, NumberStyles.Integer, CultureInfo.InvariantCulture, out int basis) || basis < 0 || basis > 10000000) return false;
            long sum = basis;
            foreach (Row row in sheet.Rows)
            {
                if (!row.Selected) continue;
                if (!int.TryParse(row.Amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount < -10000000 || amount > 10000000) return false;
                sum += amount;
            }
            if (sum < 0 || sum > 10000000) return false;
            total = (int)sum; return true;
        }

        private void RefreshSummary()
        {
            if (item == null || !sheets.TryGetValue(item, out Sheet sheet)) return;
            bool valid = TryTotal(sheet, out int value);
            string text = ShopSettings.Format("f23.total", valid ? value.ToString(CultureInfo.InvariantCulture) : "—");
            if (totalLabel != null && totalLabel.IsAlive && totalLabel.GetText() != text) totalLabel.SetText(text);
            bool canApply = valid && Current(sheet);
            if (applyButton != null && applyButton.IsAlive && applyButton.button != null && applyButton.button.interactable != canApply)
                applyButton.SetInteractable(canApply);
            bool canUndo = sheet.Undo.Count != 0;
            if (undoButton != null && undoButton.IsAlive && undoButton.button != null && undoButton.button.interactable != canUndo)
                undoButton.SetInteractable(canUndo);
        }

        private void Apply()
        {
            if (item == null || !sheets.TryGetValue(item, out Sheet sheet) || !Current(sheet) || !TryTotal(sheet, out int value)) return;
            var ui = InspectionUIManager.Instance;
            if (ui.apprisalServiceInputField == null || ui.isEditingAppraisalField || StoreUIManager.IsAnyTyping() ||
                KeyBindHelper.IsCapturing || UIDragHandler.isDraggingAnyUI || ItemMouseDragHandler.current?.IsDraggingItem == true ||
                DialogUIManager.Instance?.currentDialog != null || DialoguePhoneUIManager.Instance?.currentDialog != null) return;
            string text = value.ToString(CultureInfo.InvariantCulture);
            ui.apprisalServiceInputField.SetTextWithoutNotify(text);
            ui.OnValueChanged(text);
        }

        private static string Limit(string value, int length) => value == null ? "" : value.Length <= length ? value : value.Substring(0, length);
        private void Rebuild() { window?.Close(); window = null; Build(); }
        private void Close() { window?.Close(); window = null; totalLabel = null; applyButton = null; undoButton = null; item = null; draftName = draftAmount = ""; }
        private void Reset() { Close(); sheets.Clear(); client = null; run = null; slot = -1; }
        private void SceneChanged(Scene oldScene, Scene newScene) => Reset();
        public void Dispose()
        {
            if (!started) return;
            if (started) { ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Reset; SceneManager.activeSceneChanged -= SceneChanged; }
            Reset(); started = false;
        }
    }
}
