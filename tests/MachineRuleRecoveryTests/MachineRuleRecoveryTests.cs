using System;
using System.Collections.Generic;
using System.Linq;
using ShopAssist;

internal static class MachineRuleRecoveryTests
{
    private sealed class Store : IMachineRuleStore
    {
        internal readonly Dictionary<string, string> Data = new Dictionary<string, string>();
        internal Action<string, string> AfterWrite;
        internal string FailRead, FailWrite;
        internal int Writes;
        public string Read(string key)
        {
            if (key == FailRead) throw new InvalidOperationException("read failed");
            return Data.TryGetValue(key, out string value) ? value : null;
        }
        public void Write(string key, string value)
        {
            if (key == FailWrite) throw new InvalidOperationException("write failed");
            Data[key] = value; Writes++;
            AfterWrite?.Invoke(key, value);
        }
    }

    private const string Key = "f33.rules", Run = "current-run", Old = "unreadable raw record", Empty = "valid empty rules";
    private const int Slot = 2;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool Invalid(string raw) => raw == Old;
    private static bool ValidEmpty(string raw) => raw == Empty;
    private static Store NewStore()
    {
        var store = new Store(); store.Data[Key] = Old; store.Data["attention"] = "existing inspection items"; return store;
    }
    private static MachineRuleResetResult Begin(Store store, Func<bool> scope, out Exception error) =>
        MachineRuleRecovery.Begin(store, Key, Run, Slot, Old, Empty, true, scope, Invalid, ValidEmpty, out error);
    private static MachineRuleResetResult Resume(Store store, Func<bool> scope, out Exception error) =>
        MachineRuleRecovery.Resume(store, Key, Run, Slot, true, scope, Invalid, ValidEmpty, out error);

    private static void Main()
    {
        var run = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        string legacy = "{\"version\":4,\"run\":\"" + run.ToString("N") + "\",\"slot\":2,\"rules\":[" +
            "{\"kind\":35,\"host\":8,\"threshold\":37,\"location\":\"root:stock\",\"condition\":1,\"materials\":7,\"grade\":1}," +
            "{\"kind\":31,\"host\":9,\"threshold\":0,\"location\":\"root:stock\",\"condition\":2,\"materials\":null,\"grade\":null}]}";
        var saved = MachineTools.DecodeMaintenance(legacy, run, 2);
        Check(saved.version == 5 && saved.rules.Count == 2 && saved.rules[0].condition == 1 && saved.rules[0].threshold == 37 &&
            saved.rules[0].materials == 7 && saved.rules[0].grade == 1 && saved.rules[1].condition == 2,
            "Version 4 threshold rules and unrelated battery rules must remain readable without losing their fields.");
        var roundTrip = MachineTools.DecodeMaintenance(ShopData.Encode(saved), run, 2);
        Check(roundTrip.rules[0].threshold == 37 && roundTrip.rules[0].condition == 1 && roundTrip.rules[1].host == 9,
            "Reading and saving a legacy rule must preserve the old threshold and the other rules.");
        roundTrip.rules.Add(new MachineTools.MaintenanceRule { kind = 35, host = 10, threshold = 0,
            location = "root:stock", condition = 1, materials = 7, grade = 0 });
        var withNewRule = MachineTools.DecodeMaintenance(ShopData.Encode(roundTrip), run, 2);
        Check(withNewRule.rules[2].condition == 1 && withNewRule.rules[2].threshold == 0 &&
            withNewRule.rules[0].threshold == 37 && withNewRule.rules[1].host == 9,
            "New best-durability rules must round-trip without changing legacy or battery rules.");
        string oldVersion = legacy.Replace("\"version\":4", "\"version\":3");
        var migrated = MachineTools.DecodeMaintenance(oldVersion, run, 2);
        Check(migrated.version == 5 && migrated.rules[0].condition == 0 && migrated.rules[1].condition == 2,
            "Version 3 filter and battery rules must retain their existing migration.");

        var store = NewStore();
        Check(Begin(store, () => false, out _) == MachineRuleResetResult.Changed && store.Writes == 0,
            "A changed save must not be written.");
        store.FailRead = Key;
        Check(Begin(store, () => true, out Exception error) == MachineRuleResetResult.Failed && error != null && store.Writes == 0,
            "A storage read failure must never overwrite the source.");
        store.FailRead = null; store.Data[Key] = "changed after confirmation";
        Check(Begin(store, () => true, out _) == MachineRuleResetResult.Changed && store.Writes == 0,
            "A changed source must never be reset.");

        store = NewStore();
        Check(Begin(store, () => true, out _) == MachineRuleResetResult.Complete && store.Read(Key) == Empty &&
            MachineRuleRecovery.EvidenceRetained(store, Key, Old) && store.Read("attention") == "existing inspection items" &&
            store.Read(Key + ".reset.pending") == "", "A reset must retain the exact original and leave inspection data alone.");

        store = NewStore();
        store.FailWrite = Key;
        Check(Begin(store, () => true, out error) == MachineRuleResetResult.Failed && error != null && store.Read(Key) == Old &&
            MachineRuleRecovery.EvidenceRetained(store, Key, Old), "A target write failure must retain both source and evidence.");
        MachineRuleRecovery.ReadPending(store, Key, Run, Slot, true, out MachineRuleResetJournal pending);
        Check(pending?.original == Old, "A failed reset must retain a retryable journal.");
        store.FailWrite = null;
        Check(Resume(store, () => true, out _) == MachineRuleResetResult.Complete && store.Read(Key) == Empty,
            "The journal must support retrying the confirmed reset.");

        store = NewStore();
        store.AfterWrite = (key, value) => { if (key == Key) throw new InvalidOperationException("readback interrupted"); };
        Check(Begin(store, () => true, out _) == MachineRuleResetResult.Failed && store.Read(Key) == Empty,
            "A write may complete before its verification fails.");
        MachineRuleRecovery.ReadPending(store, Key, Run, Slot, true, out pending);
        Check(pending != null && MachineRuleRecovery.EvidenceRetained(store, Key, Old), "An uncertain write must remain recoverable.");
        store.AfterWrite = null;
        Check(Resume(store, () => true, out _) == MachineRuleResetResult.Complete, "Retry must verify an already-written replacement.");

        store = NewStore();
        store.AfterWrite = (key, value) => { if (key == Key + ".reset.pending" && value != "") store.Data[Key] = "external change"; };
        Check(Begin(store, () => true, out _) == MachineRuleResetResult.Changed && store.Read(Key) == "external change" &&
            MachineRuleRecovery.EvidenceRetained(store, Key, Old), "A concurrent change must not be overwritten after journaling.");
        store.AfterWrite = null;
        Check(MachineRuleRecovery.Cancel(store, Key, Run, Slot, null, null, () => true, out _) == MachineRuleResetResult.Changed &&
            store.Read(Key) == "external change" && store.Read("attention") == "existing inspection items" &&
            MachineRuleRecovery.EvidenceRetained(store, Key, Old) && store.Read(Key + ".reset.pending") != "",
            "Cancel must not overwrite a changed source or discard the pending reset.");

        store = NewStore();
        store.AfterWrite = (key, value) => { if (key == Key && value == Empty) throw new InvalidOperationException("verification failed"); };
        Check(Begin(store, () => true, out _) == MachineRuleResetResult.Failed && store.Read(Key) == Empty,
            "Fixture must retain an uncertain replacement.");
        store.AfterWrite = null;
        Check(MachineRuleRecovery.Cancel(store, Key, Run, Slot, null, null, () => true, out _) == MachineRuleResetResult.Complete &&
            store.Read(Key) == Old && store.Read(Key + ".reset.pending") == "" &&
            store.Read("attention") == "existing inspection items", "Cancel must restore and verify the original before clearing its journal.");

        store = NewStore();
        store.AfterWrite = (key, value) => { if (key == Key && value == Empty) throw new InvalidOperationException("verification failed"); };
        Check(Begin(store, () => true, out _) == MachineRuleResetResult.Failed, "Fixture must create a partially written reset.");
        store.AfterWrite = null; store.FailWrite = Key;
        Check(MachineRuleRecovery.Cancel(store, Key, Run, Slot, null, null, () => true, out error) == MachineRuleResetResult.Failed &&
            error != null && store.Read(Key) == Empty && store.Read(Key + ".reset.pending") != "",
            "A failed restoration must preserve the retryable journal.");

        store = NewStore();
        Check(Begin(store, () => true, out _) == MachineRuleResetResult.Complete, "Fixture must create a verified replacement.");
        Check(MachineRuleRecovery.Cancel(store, Key, Run, Slot, Old, Empty, () => true, out _) == MachineRuleResetResult.Complete &&
            store.Read(Key) == Old, "An in-memory pending reset must also restore its original after the journal was cleared.");

        store = NewStore();
        store.FailWrite = Key;
        Check(Begin(store, () => true, out _) == MachineRuleResetResult.Failed, "Fixture must create an interrupted reset.");
        string evidenceKey = store.Data.Keys.Single(key => key.StartsWith(Key + ".unreadable.", StringComparison.Ordinal));
        store.Data[evidenceKey] = "damaged backup"; store.FailWrite = null;
        Check(Resume(store, () => true, out error) == MachineRuleResetResult.Failed && error != null && store.Read(Key) == Old,
            "Reset must stop if the original evidence cannot be verified.");
    }
}
