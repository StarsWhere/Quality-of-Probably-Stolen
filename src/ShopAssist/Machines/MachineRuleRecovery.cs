using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace ShopAssist
{
    internal enum MachineRuleReadStatus { Valid, Invalid, Unavailable, ResetPending }
    internal enum MachineRuleResetResult { Complete, Changed, Failed }

    internal interface IMachineRuleStore
    {
        string Read(string key);
        void Write(string key, string value);
    }

    [Serializable]
    internal sealed class MachineRuleResetJournal
    {
        [JsonRequired] public int version = 1, slot;
        [JsonRequired] public string run, key, original, replacement, backup;
    }

    // A confirmed reset is journaled before the original record is touched.
    internal static class MachineRuleRecovery
    {
        private static string JournalKey(string key) => key + ".reset.pending";

        internal static void ReadPending(IMachineRuleStore store, string key, string run, int slot, bool requireEvidence,
            out MachineRuleResetJournal journal)
        {
            journal = null;
            string raw = store.Read(JournalKey(key));
            if (string.IsNullOrEmpty(raw)) return;
            journal = ShopData.Decode<MachineRuleResetJournal>(raw, int.MaxValue);
            if (journal == null || journal.version != 1 || journal.run != run || journal.slot != slot ||
                journal.key != key || journal.original == null || journal.replacement == null || journal.backup == null ||
                (requireEvidence ? journal.backup != BackupKey(key, journal.original) : journal.backup.Length != 0))
                throw new FormatException("Invalid machine rule reset journal.");
        }

        internal static MachineRuleResetResult Begin(IMachineRuleStore store, string key, string run, int slot,
            string expected, string replacement, bool keepEvidence, Func<bool> sameScope,
            Func<string, bool> invalid, Func<string, bool> validEmpty, out Exception failure)
        {
            failure = null;
            try
            {
                if (!sameScope()) return MachineRuleResetResult.Changed;
                ReadPending(store, key, run, slot, keepEvidence, out MachineRuleResetJournal existing);
                if (existing != null || expected == null || store.Read(key) != expected || !invalid(expected) || !validEmpty(replacement))
                    return MachineRuleResetResult.Changed;
                string backup = keepEvidence ? BackupKey(key, expected) : "";
                if (backup.Length != 0)
                {
                    string previous = store.Read(backup);
                    if (previous != null && previous != expected) throw new InvalidOperationException("Recovery evidence key already exists.");
                    if (previous == null) store.Write(backup, expected);
                    if (store.Read(backup) != expected) throw new InvalidOperationException("Recovery evidence was not retained.");
                }
                if (!sameScope() || store.Read(key) != expected) return MachineRuleResetResult.Changed;
                var journal = new MachineRuleResetJournal { run = run, slot = slot, key = key,
                    original = expected, replacement = replacement, backup = backup };
                string encoded = ShopData.Encode(journal);
                store.Write(JournalKey(key), encoded);
                if (store.Read(JournalKey(key)) != encoded) throw new InvalidOperationException("Reset journal was not retained.");
                return Resume(store, key, run, slot, keepEvidence, sameScope, invalid, validEmpty, out failure);
            }
            catch (Exception error) { failure = error; return MachineRuleResetResult.Failed; }
        }

        internal static MachineRuleResetResult Resume(IMachineRuleStore store, string key, string run, int slot,
            bool keepEvidence, Func<bool> sameScope, Func<string, bool> invalid, Func<string, bool> validEmpty,
            out Exception failure)
        {
            failure = null;
            try
            {
                if (!sameScope()) return MachineRuleResetResult.Changed;
                ReadPending(store, key, run, slot, keepEvidence, out MachineRuleResetJournal journal);
                if (journal == null || !invalid(journal.original) || !validEmpty(journal.replacement))
                    return MachineRuleResetResult.Changed;
                string marker = ShopData.Encode(journal);
                if (store.Read(JournalKey(key)) != marker) return MachineRuleResetResult.Changed;
                if (journal.backup.Length != 0 && store.Read(journal.backup) != journal.original)
                    throw new InvalidOperationException("Recovery evidence is unavailable.");
                string current = store.Read(key);
                if (current != journal.original && current != journal.replacement) return MachineRuleResetResult.Changed;
                if (!sameScope() || store.Read(JournalKey(key)) != marker || store.Read(key) != current)
                    return MachineRuleResetResult.Changed;
                if (current == journal.original) store.Write(key, journal.replacement);
                if (store.Read(key) != journal.replacement || !validEmpty(journal.replacement) ||
                    store.Read(JournalKey(key)) != marker ||
                    journal.backup.Length != 0 && store.Read(journal.backup) != journal.original)
                    throw new InvalidOperationException("Reset data was not retained.");
                if (!sameScope()) return MachineRuleResetResult.Changed;
                store.Write(JournalKey(key), "");
                if (store.Read(JournalKey(key)) != "" || store.Read(key) != journal.replacement || !sameScope() ||
                    journal.backup.Length != 0 && store.Read(journal.backup) != journal.original)
                    throw new InvalidOperationException("Reset completion was not retained.");
                return MachineRuleResetResult.Complete;
            }
            catch (Exception error) { failure = error; return MachineRuleResetResult.Failed; }
        }

        private static string BackupKey(string key, string original)
        {
            using (SHA256 hash = SHA256.Create())
                return key + ".unreadable." + BitConverter.ToString(hash.ComputeHash(Encoding.Unicode.GetBytes(original))).Replace("-", "").ToLowerInvariant();
        }

        internal static bool EvidenceRetained(IMachineRuleStore store, string key, string original) =>
            original != null && store.Read(BackupKey(key, original)) == original;

        internal static MachineRuleResetResult Cancel(IMachineRuleStore store, string key, string run, int slot,
            string fallbackOriginal, string fallbackReplacement, Func<bool> sameScope, out Exception failure)
        {
            failure = null;
            try
            {
                if (!sameScope()) return MachineRuleResetResult.Changed;
                ReadPending(store, key, run, slot, true, out MachineRuleResetJournal journal);
                if (journal == null && fallbackOriginal == null) return MachineRuleResetResult.Complete;
                string original = journal?.original ?? fallbackOriginal;
                string replacement = journal?.replacement ?? fallbackReplacement;
                string marker = journal == null ? "" : ShopData.Encode(journal);
                string savedMarker = store.Read(JournalKey(key));
                if (journal == null ? !string.IsNullOrEmpty(savedMarker) : savedMarker != marker)
                    return MachineRuleResetResult.Changed;
                string current = store.Read(key);
                if (current != original && current != replacement) return MachineRuleResetResult.Changed;
                if (current == replacement && !EvidenceRetained(store, key, original))
                    throw new InvalidOperationException("Recovery evidence is unavailable.");
                if (!sameScope() || store.Read(key) != current || store.Read(JournalKey(key)) != savedMarker)
                    return MachineRuleResetResult.Changed;
                if (current == replacement) store.Write(key, original);
                if (!sameScope() || store.Read(key) != original || store.Read(JournalKey(key)) != savedMarker)
                    throw new InvalidOperationException("Original rules were not restored.");
                if (journal != null)
                {
                    store.Write(JournalKey(key), "");
                    if (store.Read(JournalKey(key)) != "" || store.Read(key) != original || !sameScope())
                        throw new InvalidOperationException("Reset cancellation was not retained.");
                }
                return MachineRuleResetResult.Complete;
            }
            catch (Exception error) { failure = error; return MachineRuleResetResult.Failed; }
        }
    }
}
