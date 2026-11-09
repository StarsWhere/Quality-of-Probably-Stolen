using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private sealed class GameMachineRuleStore : IMachineRuleStore
        {
            public string Read(string key) => ModHelper.GetModData(ModEntry.Id, key, null);
            public void Write(string key, string value) => CheckedModData.Write(key, value);
        }

        private sealed class ResetAttempt
        {
            internal string Key, Run, Original, Replacement;
            internal int Slot;
            internal bool Feed;
        }

        private readonly IMachineRuleStore ruleResetStore = new GameMachineRuleStore();
        private readonly Dictionary<string, ResetAttempt> ruleResetAttempts = new Dictionary<string, ResetAttempt>();

        private static string ResetAttemptKey(string key, string run, int slot) => key + ":" + run + ":" + slot;
        private bool HasResetAttempt(string key, string run, int slot) => ruleResetAttempts.ContainsKey(ResetAttemptKey(key, run, slot));
        private bool ResetScope(string run, int slot, bool feed) => started && SameFeedScope(run, slot) &&
            slot >= 0 && PlayerStore.Instance.ModData() != null && (!feed || furnaceInCycle.Count == 0);

        private static bool InvalidRules(string key, string raw, string run, int slot)
        {
            if (raw == null) return false;
            try
            {
                if (key == FeedKey) DecodeFeedRules(raw, run, slot);
                else if (key == MaintenanceKey && Guid.TryParse(run, out Guid id) && id != Guid.Empty) DecodeMaintenance(raw, id, slot);
                else throw new InvalidOperationException("Invalid reset scope.");
                return false;
            }
            catch (FormatException) { return true; }
            catch (JsonException) { return true; }
        }

        private static bool ValidEmptyRules(string key, string raw, string run, int slot)
        {
            if (raw == null) return false;
            if (key == FeedKey) return DecodeFeedRules(raw, run, slot).rules.Count == 0;
            return key == MaintenanceKey && Guid.TryParse(run, out Guid id) && id != Guid.Empty &&
                DecodeMaintenance(raw, id, slot).rules.Count == 0;
        }

        private static string EmptyRules(string key, string run, int slot)
        {
            if (key == FeedKey) return ShopData.Encode(new FeedRules { run = run, slot = slot });
            if (!Guid.TryParse(run, out Guid id) || id == Guid.Empty) throw new InvalidOperationException("Invalid reset scope.");
            return ShopData.Encode(new MaintenanceData { version = MaintenanceVersion, run = id.ToString("N"), slot = slot, rules = new List<MaintenanceRule>() });
        }

        private void ConfirmRuleReset(string key, string run, int slot)
        {
            bool feed = key == FeedKey;
            if (!ResetScope(run, slot, feed)) { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            string originalRaw;
            MachineRuleReadStatus state = feed ? ReadFeedState(out _, out originalRaw) : ReadMaintenanceState(out _, out originalRaw);
            if (state != MachineRuleReadStatus.Invalid || originalRaw == null)
            { ShopNotice.Show(ShopSettings.Text(state == MachineRuleReadStatus.Unavailable ? "maintenance.read_failed" : "inv.changed")); return; }
            var prompt = GenericConfirmationUIManager.Instance;
            if (prompt == null) { ShopNotice.Show(ShopSettings.Text("attention.action_failed")); return; }
            string original = originalRaw;
            prompt.OpenPanel(ShopSettings.Text(feed ? "f33.reset_warning" : "maintenance.reset_warning"),
                ShopSettings.Text(feed ? "f33.reset_confirm" : "maintenance.reset_confirm"), ShopSettings.Text("f33.finish_cancel"), () =>
                {
                    prompt.ClosePanel();
                    if (!ResetScope(run, slot, feed)) { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                    string replacement;
                    try { replacement = EmptyRules(key, run, slot); }
                    catch (Exception error) { log("Could not prepare machine rule reset: " + error); ShopNotice.Show(ShopSettings.Text("maintenance.reset_failed")); return; }
                    var attempt = new ResetAttempt { Key = key, Run = run, Slot = slot, Original = original,
                        Replacement = replacement, Feed = feed };
                    MachineRuleResetResult result = MachineRuleRecovery.Begin(ruleResetStore, key, run, slot, original, replacement, true,
                        () => ResetScope(run, slot, feed), raw => InvalidRules(key, raw, run, slot),
                        raw => ValidEmptyRules(key, raw, run, slot), out Exception failure);
                    HandleResetResult(attempt, result, failure);
                }, () => prompt.ClosePanel());
        }

        private void RetryRuleReset(string key, string run, int slot)
        {
            bool feed = key == FeedKey;
            if (!ResetScope(run, slot, feed)) { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            string attemptKey = ResetAttemptKey(key, run, slot);
            ruleResetAttempts.TryGetValue(attemptKey, out ResetAttempt attempt);
            try
            {
                MachineRuleRecovery.ReadPending(ruleResetStore, key, run, slot, true, out MachineRuleResetJournal journal);
                if (journal != null)
                {
                    attempt = attempt ?? new ResetAttempt { Key = key, Run = run, Slot = slot, Original = journal.original,
                        Replacement = journal.replacement, Feed = feed };
                    MachineRuleResetResult result = MachineRuleRecovery.Resume(ruleResetStore, key, run, slot, true,
                        () => ResetScope(run, slot, feed), raw => InvalidRules(key, raw, run, slot),
                        raw => ValidEmptyRules(key, raw, run, slot), out Exception failure);
                    HandleResetResult(attempt, result, failure);
                    return;
                }
                if (attempt == null) { AttentionBook.Changed(); return; }
                string currentRaw = ruleResetStore.Read(key);
                if (currentRaw == attempt.Replacement && ValidEmptyRules(key, currentRaw, run, slot) &&
                    MachineRuleRecovery.EvidenceRetained(ruleResetStore, key, attempt.Original))
                { HandleResetResult(attempt, MachineRuleResetResult.Complete, null); return; }
                MachineRuleResetResult retry = MachineRuleRecovery.Begin(ruleResetStore, key, run, slot, attempt.Original,
                    attempt.Replacement, true, () => ResetScope(run, slot, feed), raw => InvalidRules(key, raw, run, slot),
                    raw => ValidEmptyRules(key, raw, run, slot), out Exception retryFailure);
                HandleResetResult(attempt, retry, retryFailure);
            }
            catch (Exception error) { log("Machine rule reset retry failed: " + error); ShopNotice.Show(ShopSettings.Text("maintenance.reset_failed")); AttentionBook.Changed(); }
        }

        private void HandleResetResult(ResetAttempt attempt, MachineRuleResetResult result, Exception error)
        {
            string attemptKey = ResetAttemptKey(attempt.Key, attempt.Run, attempt.Slot);
            if (result == MachineRuleResetResult.Complete)
            {
                if (!ResetScope(attempt.Run, attempt.Slot, attempt.Feed))
                { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                ruleResetAttempts.Remove(attemptKey);
                if (attempt.Feed) { UnwrapFurnaces(); feedNeedsAttach = true; feedDirty = true; }
                else { pendingFilters.Clear(); maintenanceDirty = true; }
                machinesDirty = true; AttentionBook.Changed();
                ShopNotice.Show(ShopSettings.Text(attempt.Feed ? "f33.reset_done" : "maintenance.reset_done"));
                return;
            }
            if (result == MachineRuleResetResult.Failed)
            {
                ruleResetAttempts[attemptKey] = attempt;
                log("Machine rule reset did not complete: " + error);
                ShopNotice.Show(ShopSettings.Text("maintenance.reset_failed"));
            }
            else ShopNotice.Show(ShopSettings.Text("inv.changed"));
            AttentionBook.Changed();
        }

        private void CancelRuleReset(string key, string run, int slot)
        {
            bool feed = key == FeedKey;
            ruleResetAttempts.TryGetValue(ResetAttemptKey(key, run, slot), out ResetAttempt attempt);
            MachineRuleResetResult result = MachineRuleRecovery.Cancel(ruleResetStore, key, run, slot,
                attempt?.Original, attempt?.Replacement, () => ResetScope(run, slot, feed), out Exception failure);
            if (result == MachineRuleResetResult.Complete)
            {
                ruleResetAttempts.Remove(ResetAttemptKey(key, run, slot));
                if (feed) feedDirty = true; else maintenanceDirty = true;
                machinesDirty = true;
                ShopNotice.Show(ShopSettings.Text("maintenance.reset_cancelled"));
            }
            else if (result == MachineRuleResetResult.Failed)
            {
                log("Machine rule reset cancellation failed: " + failure);
                ShopNotice.Show(ShopSettings.Text("maintenance.reset_failed"));
            }
            else ShopNotice.Show(ShopSettings.Text("inv.changed"));
            AttentionBook.Changed();
        }
    }
}
