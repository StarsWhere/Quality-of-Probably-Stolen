using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Uses the actual attention window and native confirmation in a fresh QA save.
public sealed class WholeStackUnreadableScenario
{
    [Serializable]
    private sealed class ResetRecord
    {
        public string run = "", state = "", sourceKey = "";
        public int slot = -1;
    }

    private const string Mod = "starswhere.shopassist", JournalKey = "wholeStackTransfer";
    private const string Corrupt = "{qa unreadable whole-stack record", Changed = "{qa newer unreadable record";
    private const string OtherAction = "qa.independent", OtherTitle = "QA independent inspection";
    private const int OtherId = 150000101;
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 130f;
    private QaLoadSlot reload;
    private Type attention, transfer;
    private string run, otherRaw, resetRaw, backupKey;
    private int slot, step;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Whole-stack recovery scenario timed out at step " + step);
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                if (GenericConfirmationUIManager.Instance == null || StoreUIManager.Instance?.escapeUIManager == null) return false;
                Prepare();
                OpenAttention(); AskForReset();
                GenericConfirmationUIManager.Instance.CancelButtonPressed();
                Need(Raw(JournalKey) == Corrupt && !Ready() && Raw("attention") == otherRaw && HasOther(),
                    "Cancelling changed the unreadable journal or an unrelated inspection");

                OpenAttention(); AskForReset();
                ModHelper.SetModData(Mod, JournalKey, Changed);
                GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
                Need(Raw(JournalKey) == Changed && !Ready() && Raw("attention") == otherRaw && HasOther(),
                    "A stale confirmation replaced newer journal contents or cleared another inspection");

                ModHelper.SetModData(Mod, JournalKey, Corrupt);
                OpenAttention(); AskForReset();
                Need(Raw(JournalKey) == Corrupt && !Ready() && Raw("attention") == otherRaw,
                    "Opening the confirmation changed data before the player confirmed");
                GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
                VerifyReset();
                resetRaw = Raw(JournalKey);
                PlayerStore.Instance.SaveGame();
                StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                reload = new QaLoadSlot(slot, Guid.Parse(run));
                step = 1; return false;
            case 1:
                if (!reload.Tick() || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                    StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                Need(Raw(JournalKey) == resetRaw && Raw(backupKey) == Corrupt && Raw(backupKey + ".status") == "verified" && Ready(),
                    "The checked reset or original backup did not survive a native save and reload");
                Need(Raw("attention") == otherRaw && HasOther(), "Reload lost the independent attention item");
                OpenAttention(false);
                Need(!HasIssue("whole_stack:unreadable") && HasIssue(OtherAction + ":" + OtherId),
                    "The reopened attention list still reports the reset or lost the other inspection");
                result = "PASS unreadable whole-stack journal is discoverable in attention, confirmation/cancellation and stale checks, " +
                    "verified original backup, unblocked transfers and unrelated inspection preserved across native reload";
                return true;
            default: throw new InvalidOperationException("Unknown whole-stack recovery scenario step");
        }
    }

    private void Prepare()
    {
        var assembly = ModLoader.Mods.Single(value => value.Manifest?.ID == Mod).Instance.GetType().Assembly;
        attention = assembly.GetType("ShopAssist.AttentionBook", true);
        transfer = assembly.GetType("ShopAssist.WholeStackTransfer", true);
        run = PlayerStore.Instance.runID; slot = PlayerStore.Instance.saveSlotId;
        Type operation = assembly.GetType("ShopAssist.OperationResult", true);
        object item = Activator.CreateInstance(operation, Flags, null, new object[] { OtherAction }, null);
        operation.GetMethod("SkipMissing", Flags).Invoke(item, new object[] { OtherTitle, 1, "inv.inspect", "QA location", OtherId, true });
        attention.GetMethod("Report", Flags).Invoke(null, new object[] { item, false });
        otherRaw = Raw("attention");
        Need(!string.IsNullOrEmpty(otherRaw) && otherRaw.Contains(OtherTitle) && HasOther(),
            "The unrelated saved inspection fixture was not recorded");
        ModHelper.SetModData(Mod, JournalKey, Corrupt);
        Need(Raw(JournalKey) == Corrupt && !Ready(), "The unreadable transfer fixture did not block transfers");
    }

    private void OpenAttention(bool expectUnreadable = true)
    {
        attention.GetMethod("Changed", Flags).Invoke(null, null);
        attention.GetMethod("Open", Flags).Invoke(null, null);
        Need(CustomUIManager.Instance.GetWindow(Mod + ".attention")?.IsAlive == true, "The attention window did not open");
        Need(HasIssue("whole_stack:unreadable") == expectUnreadable && HasIssue(OtherAction + ":" + OtherId),
            "The transfer recovery state or unrelated inspection is missing from attention");
    }

    private void AskForReset()
    {
        var window = CustomUIManager.Instance.GetWindow(Mod + ".attention");
        Button action = RoutingScenario.Button(window, "f08.journal_reset_action");
        Need(action != null && action.interactable, "The transfer reset action is missing from the attention window");
        action.onClick.Invoke();
        var prompt = GenericConfirmationUIManager.Instance;
        string warning = ModHelper.GetLocalized(Mod, "f08.journal_reset_warning");
        Need(prompt?.panel?.activeInHierarchy == true && prompt.panel.GetComponentsInChildren<TMP_Text>(true)
            .Any(text => text.text == warning), "The reset did not show the native inspection confirmation");
        Need(Raw(JournalKey) == Corrupt && !Ready(), "The reset changed the journal before confirmation");
    }

    private void VerifyReset()
    {
        string raw = Raw(JournalKey);
        Need(!string.IsNullOrEmpty(raw) && raw != Corrupt, "The confirmed reset did not replace the unreadable journal");
        ResetRecord record = JsonUtility.FromJson<ResetRecord>(raw);
        Need(record != null && record.state == "reset" && record.run == run && record.slot == slot &&
            record.sourceKey != null && record.sourceKey.StartsWith("wholeStackTransfer.unreadable.", StringComparison.Ordinal),
            "The reset marker did not identify its original backup and save");
        backupKey = record.sourceKey;
        Need(Raw(backupKey) == Corrupt && Raw(backupKey + ".status") == "verified" && Ready(),
            "The original was not preserved and verified before transfers were unblocked");
        Need(Raw("attention") == otherRaw && HasOther(), "Resetting the transfer journal changed another attention item");
    }

    private bool HasOther() => (bool)attention.GetMethod("HasInspection", Flags).Invoke(null, new object[] { OtherAction, OtherId });
    private bool Ready() => (bool)transfer.GetProperty("Ready", Flags).GetValue(null);
    private bool HasIssue(string key)
    {
        var values = (IEnumerable)attention.GetMethod("Snapshot", Flags).Invoke(null, null);
        return values.Cast<object>().Any(item => (string)item.GetType().GetField("Key", Flags).GetValue(item) == key);
    }
    private static string Raw(string key) => ModHelper.GetModData(Mod, key, null);
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
