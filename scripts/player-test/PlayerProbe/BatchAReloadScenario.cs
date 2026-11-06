using System;
using System.IO;
using System.Linq;
using UnityEngine;

// Verifies both checklists in a fresh Player process, then switches slots again.
public sealed class BatchAReloadScenario
{
    private BatchAScenario.Context context;
    private QaLoadSlot load;
    private int step;
    private float stepStarted = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - stepStarted > 75) throw new InvalidOperationException("F17 reload QA timed out at step " + step);
        switch (step)
        {
            case 0:
            {
                if (MainMenuUIController.Instance == null) return false;
                string path = BatchAScenario.ContextPath();
                context = File.Exists(path) ? JsonUtility.FromJson<BatchAScenario.Context>(File.ReadAllText(path)) : null;
                Require(context != null && context.version == 1 && context.slotA >= 0 && context.slotB >= 0 && context.slotA != context.slotB &&
                    Guid.TryParseExact(context.runA, "N", out Guid runA) && Guid.TryParseExact(context.runB, "N", out Guid runB) && runA != runB &&
                    Guid.TryParseExact(context.rowA, "N", out _) && Guid.TryParseExact(context.rowB, "N", out _), "Invalid F17 QA context");
                load = new QaLoadSlot(context.slotA, Guid.ParseExact(context.runA, "N"));
                Advance(1, "Loading the first saved checklist in a fresh Player process"); return false;
            }
            case 1:
            {
                if (!load.Tick() || !Ready()) return false;
                var rows = BatchAScenario.ParseBook().rows;
                var row = rows.Single(r => r.id == context.rowA);
                Require(row.text == context.textA && row.repeat == 1 && row.checkedDay == context.dayA && !rows.Any(r => r.text == context.textB),
                    "First checklist was not preserved by the native save and reload");
                CloseCalendar();
                BatchAScenario.OpenTodoAndCheck(context.rowA, true);
                CustomUIManager.Instance.GetWindow("starswhere.shopassist.todo")?.Close();
                StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                load = new QaLoadSlot(context.slotB, Guid.ParseExact(context.runB, "N"));
                Advance(2, "First task persisted across the Player restart"); return false;
            }
            case 2:
            {
                if (!load.Tick() || !Ready()) return false;
                var rows = BatchAScenario.ParseBook().rows;
                var row = rows.Single(r => r.id == context.rowB);
                Require(row.text == context.textB && row.repeat == 0 && row.checkedDay == 0 && !rows.Any(r => r.text == context.textA),
                    "Second checklist did not remain separate after restart");
                result = "PASS F17 native SaveGame, cold reload and cross-slot isolation for both checklist states";
                return true;
            }
            default: throw new InvalidOperationException("Unknown F17 reload QA step");
        }
    }

    private static bool Ready() => StoreUIManager.Instance != null && StoreUIManager.Instance.gameOptionUIManager != null &&
        EmporiumEntry.Instance != null && EmporiumEntry.Instance.storeInteractables != null && EmporiumEntry.Instance.storeInteractables.activeInHierarchy;
    private static void CloseCalendar()
    {
        var calendar = AdvCalendarUIManager.Instance;
        if (calendar != null && calendar.UIPanel != null && calendar.UIPanel.activeSelf && StoreUIManager.Instance.storeCalendar != null)
            StoreUIManager.Instance.storeCalendar.OnClick();
    }
    private void Advance(int next, string message) { Debug.Log("[PLAYER-QA] " + message); step = next; stepStarted = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
