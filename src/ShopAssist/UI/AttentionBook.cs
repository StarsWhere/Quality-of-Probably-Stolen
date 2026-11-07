using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    internal static class AttentionBook
    {
        [Serializable] private sealed class SavedIssue
        {
            [JsonRequired] public string key, feature, title, location, reason;
            [JsonRequired] public int item;
        }
        [Serializable] private sealed class SavedIssues
        {
            [JsonRequired] public int version = 1;
            [JsonRequired] public string run;
            [JsonRequired] public int slot;
            [JsonRequired] public List<SavedIssue> issues = new List<SavedIssue>();
        }
        [Serializable] private sealed class RecoveryAttempt
        {
            [JsonRequired] public int version = 1;
            [JsonRequired] public string run, backupKey, digest, written;
            [JsonRequired] public int slot;
            [JsonRequired] public bool completed, markerReset;
            [JsonIgnore] internal string markerRaw;
        }

        private const string DataKey = "attention";
        private const string RecoveryKey = "attention.recovery";
        private const string BackupPrefix = "attention.unreadable.";
        private const string MarkerBackupPrefix = "attention.recovery.unreadable.";
        private static readonly Dictionary<string, Func<IEnumerable<AttentionItem>>> providers = new Dictionary<string, Func<IEnumerable<AttentionItem>>>();
        private static readonly Dictionary<string, string> providerErrors = new Dictionary<string, string>();
        private static readonly Dictionary<string, SavedIssues> unsaved = new Dictionary<string, SavedIssues>(StringComparer.Ordinal);
        private static SavedIssues data;
        private static string run, signature, invalidRaw, invalidKey;
        private static int slot;
        private static bool loaded, invalid, dirty, saveFailed;
        private static RecoveryAttempt recovery;
        private static CustomUIWindow window;
        private static Action<string> log;
        private static float refreshAt;
        private static readonly HashSet<string> announced = new HashSet<string>(StringComparer.Ordinal);

        internal static void Start(ShopSettings settings, Action<string> warning)
        {
            log = warning;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }
        internal static void Register(string owner, Func<IEnumerable<AttentionItem>> provider)
        {
            if (string.IsNullOrEmpty(owner) || provider == null || providers.ContainsKey(owner))
                throw new ArgumentException("Invalid or duplicate attention provider: " + owner);
            providers.Add(owner, provider); Changed();
        }
        internal static void Unregister(string owner) { providers.Remove(owner); providerErrors.Remove(owner); Changed(); }
        internal static void Changed() { dirty = true; refreshAt = 0; MachineTools.OverviewAttentionChanged(); }

        private static bool Ensure()
        {
            if (!PlayerStore.HasInstance || string.IsNullOrEmpty(PlayerStore.Instance.runID) ||
                PlayerStore.Instance.saveSlotId < 0 || PlayerStore.Instance.ModData() == null) return false;
            var store = PlayerStore.Instance;
            if (loaded && run == store.runID && slot == store.saveSlotId) return !invalid;
            Reset(); loaded = true; run = store.runID; slot = store.saveSlotId;
            return Read();
        }

        private static void Validate(SavedIssues value, string expectedRun, int expectedSlot)
        {
            if (value == null || value.version != 1 || value.run != expectedRun || value.slot != expectedSlot || value.issues == null ||
                value.issues.Count > 4096 || value.issues.Any(issue => issue == null || string.IsNullOrEmpty(issue.key) ||
                    issue.key.Length > 256 || string.IsNullOrEmpty(issue.feature) || issue.feature.Length > 128 ||
                    issue.title == null || issue.title.Length > 4096 || issue.location == null || issue.location.Length > 8192 ||
                    string.IsNullOrEmpty(issue.reason) || issue.reason.Length > 256 || issue.item < 0) ||
                value.issues.Select(issue => issue.key).Distinct(StringComparer.Ordinal).Count() != value.issues.Count)
                throw new FormatException("Invalid attention data or save identity");
        }

        private static string EncodeChecked(SavedIssues value)
        {
            Validate(value, run, slot);
            string raw = ShopData.Encode(value);
            Validate(ShopData.Decode<SavedIssues>(raw, 1048576), run, slot);
            return raw;
        }

        private static string Digest(string value)
        {
            var bytes = new byte[checked(value.Length * 2)];
            for (int i = 0; i < value.Length; i++) { bytes[i * 2] = (byte)value[i]; bytes[i * 2 + 1] = (byte)(value[i] >> 8); }
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes));
        }

        private static void ValidateRecovery(RecoveryAttempt attempt)
        {
            string prefix = attempt?.markerReset == true ? MarkerBackupPrefix : BackupPrefix;
            if (attempt == null || attempt.version != 1 || attempt.run != run || attempt.slot != slot ||
                attempt.backupKey == null || !attempt.backupKey.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(attempt.backupKey.Substring(prefix.Length), "N", out _) ||
                string.IsNullOrEmpty(attempt.digest) || (attempt.markerReset && (!attempt.completed || attempt.written != "")) ||
                (!attempt.markerReset && attempt.written == null))
                throw new FormatException("Invalid attention recovery data or save identity");
            if (!attempt.markerReset) Validate(ShopData.Decode<SavedIssues>(attempt.written, 1048576), run, slot);
        }

        private static SavedIssues Merge(SavedIssues pending, SavedIssues persisted)
        {
            var keys = new HashSet<string>(pending.issues.Select(issue => issue.key), StringComparer.Ordinal);
            return new SavedIssues { run = run, slot = slot, issues = pending.issues.Concat(persisted.issues.Where(issue => keys.Add(issue.key))).ToList() };
        }

        private static bool Read()
        {
            invalid = saveFailed = false; invalidRaw = invalidKey = null; recovery = null; data = null;
            string marker;
            try
            {
                marker = ModHelper.GetModData(ModEntry.Id, RecoveryKey, null);
            }
            catch (Exception error)
            {
                invalid = true; log?.Invoke("Could not read attention recovery data: " + error);
                return false;
            }
            if (marker != null)
            {
                RecoveryAttempt attempt;
                try
                {
                    attempt = ShopData.Decode<RecoveryAttempt>(marker, 8388608);
                    ValidateRecovery(attempt);
                }
                catch (Exception error)
                {
                    invalid = true; invalidKey = RecoveryKey; invalidRaw = marker;
                    log?.Invoke("Retained unreadable attention recovery data: " + error);
                    return false;
                }
                try
                {
                    string backup = ModHelper.GetModData(ModEntry.Id, attempt.backupKey, null);
                    if (backup == null || Digest(backup) != attempt.digest)
                        throw new InvalidOperationException("Attention backup is unavailable or changed.");
                    if (!attempt.completed)
                    {
                        // A previous rebuild has not been fully verified, even if its write took effect.
                        ModHelper.GetModData(ModEntry.Id, DataKey, null);
                        attempt.markerRaw = marker; recovery = attempt; invalid = true; invalidKey = DataKey; invalidRaw = backup;
                        SavedIssues candidate = ShopData.Decode<SavedIssues>(attempt.written, 1048576);
                        string scope = run + ":" + slot;
                        unsaved[scope] = unsaved.TryGetValue(scope, out SavedIssues pending) ? Merge(pending, candidate) : candidate;
                        return false;
                    }
                }
                catch (Exception error)
                {
                    invalid = true; log?.Invoke("Could not verify attention recovery data: " + error);
                    return false;
                }
            }
            string raw;
            try
            {
                raw = ModHelper.GetModData(ModEntry.Id, DataKey, null);
            }
            catch (Exception error)
            {
                invalid = true; log?.Invoke("Could not read attention data: " + error);
                return false;
            }
            try
            {
                data = raw == null ? new SavedIssues { run = run, slot = slot } : ShopData.Decode<SavedIssues>(raw, 1048576);
                Validate(data, run, slot);
                if (unsaved.TryGetValue(run + ":" + slot, out SavedIssues pending))
                {
                    // The saved data may have changed after a failed write. Keep both sets until checked.
                    data = Merge(pending, data);
                    unsaved[run + ":" + slot] = data; saveFailed = true;
                }
                return true;
            }
            catch (Exception error)
            {
                invalid = true; invalidKey = DataKey; invalidRaw = raw; data = null; log?.Invoke("Retained unreadable attention data: " + error);
                return false;
            }
        }

        private static bool Save(SavedIssues next)
        {
            try
            {
                string raw = EncodeChecked(next);
                ModHelper.SetModData(ModEntry.Id, DataKey, raw);
                if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != raw) throw new InvalidOperationException("Attention update was not retained.");
                data = next; saveFailed = false; unsaved.Remove(next.run + ":" + next.slot); Changed(); return true;
            }
            catch (Exception error)
            {
                log?.Invoke("Could not save attention state: " + error);
                data = next; saveFailed = true; unsaved[next.run + ":" + next.slot] = next; Changed();
                ShopNotice.Show(ShopSettings.Text("attention.save_failed")); return false;
            }
        }
        private static string IssueKey(string action, OperationResult.ItemResult item) => action + ":" +
            (item.ItemId > 0 ? item.ItemId.ToString() : item.Name + ":" + item.Location);

        internal static void Report(OperationResult result, bool alreadyNotified = false)
        {
            if (result == null) return;
            bool readable = Ensure();
            if (!readable && (!loaded || !invalid || !SameScope(run, slot))) return;
            SavedIssues current = readable ? data : (unsaved.TryGetValue(run + ":" + slot, out SavedIssues pending) ? pending : null);
            var next = new SavedIssues { run = run, slot = slot, issues = current == null ? new List<SavedIssue>() : new List<SavedIssue>(current.issues) };
            var notified = new List<string>();
            bool changed = false;
            foreach (var item in result.Items)
            {
                string key = IssueKey(result.ActionKey, item);
                if (key.Length > 256) key = key.Substring(0, 256);
                if (item.Status == OperationItemStatus.Completed)
                { if (item.ItemId > 0) changed |= next.issues.RemoveAll(issue => issue.key == key) != 0; continue; }
                if (item.Status != OperationItemStatus.NeedsInspection) continue;
                next.issues.RemoveAll(issue => issue.key == key);
                next.issues.Add(new SavedIssue { key = key, feature = result.ActionKey, title = item.Name,
                    location = item.Location, reason = item.Reason ?? "inv.inspect", item = Math.Max(0, item.ItemId) });
                notified.Add(key);
                changed = true;
            }
            if (!changed) return;
            if (!readable) { unsaved[run + ":" + slot] = next; Changed(); return; }
            if (Save(next) && alreadyNotified) foreach (string key in notified) announced.Add(key);
        }

        internal static bool HasInspection(string actionKey, int itemId) => PlayerStore.HasInstance && !string.IsNullOrEmpty(PlayerStore.Instance.runID) &&
            (!Ensure() || saveFailed || itemId > 0 && data.issues.Any(issue => issue.feature == actionKey && issue.item == itemId));

        private static void Remove(string key)
        {
            bool readable = Ensure();
            if (!readable && (!loaded || !invalid || !SameScope(run, slot))) return;
            SavedIssues current = readable ? data : (unsaved.TryGetValue(run + ":" + slot, out SavedIssues pending) ? pending : null);
            if (current == null) return;
            var next = new SavedIssues { run = run, slot = slot, issues = current.issues.Where(issue => issue.key != key).ToList() };
            if (next.issues.Count == current.issues.Count) return;
            if (readable) Save(next);
            else { unsaved[run + ":" + slot] = next; Changed(); }
        }

        private static void RetryRead()
        {
            if (!loaded || !invalid || !SameScope(run, slot)) return;
            Read(); Changed();
        }

        private static void ConfirmRebuild()
        {
            if (!loaded || !invalid || invalidRaw == null || !SameScope(run, slot)) return;
            var prompt = GenericConfirmationUIManager.Instance;
            if (prompt == null) { ShopNotice.Show(ShopSettings.Text("attention.action_failed")); return; }
            string expectedRun = run, expectedKey = invalidKey, expectedRaw = invalidRaw; int expectedSlot = slot;
            prompt.OpenPanel(ShopSettings.Text("attention.rebuild_warning"), ShopSettings.Text("attention.rebuild"),
                ShopSettings.Text("attention.cancel"), () =>
                {
                    prompt.ClosePanel();
                    Rebuild(expectedRun, expectedSlot, expectedKey, expectedRaw);
                }, () => prompt.ClosePanel());
        }

        private static void Rebuild(string expectedRun, int expectedSlot, string expectedKey, string expectedRaw)
        {
            if (!loaded || !invalid || run != expectedRun || slot != expectedSlot || invalidKey != expectedKey || invalidRaw != expectedRaw ||
                !SameScope(expectedRun, expectedSlot))
            { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            if (expectedKey == RecoveryKey) { RebuildMarker(expectedRun, expectedSlot, expectedRaw); return; }
            if (expectedKey != DataKey) { ShopNotice.Show(ShopSettings.Text("attention.action_failed")); return; }
            try
            {
                string current = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                RecoveryAttempt attempt = recovery;
                if (current != expectedRaw && (attempt == null || attempt.written == null || current != attempt.written))
                { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                if (!SameScope(expectedRun, expectedSlot)) throw new InvalidOperationException("Attention save scope changed.");
                string scope = expectedRun + ":" + expectedSlot;
                if (attempt == null)
                {
                    SavedIssues candidate = unsaved.TryGetValue(scope, out SavedIssues pending) ? pending :
                        new SavedIssues { run = expectedRun, slot = expectedSlot };
                    string candidateRaw = EncodeChecked(candidate);
                    if (!unsaved.ContainsKey(scope)) unsaved[scope] = candidate;
                    string backupKey = BackupPrefix + Guid.NewGuid().ToString("N");
                    if (ModHelper.GetModData(ModEntry.Id, backupKey, null) != null)
                        throw new InvalidOperationException("Attention backup key already exists.");
                    if (!SameScope(expectedRun, expectedSlot)) throw new InvalidOperationException("Attention save scope changed.");
                    ModHelper.SetModData(ModEntry.Id, backupKey, expectedRaw);
                    if (!SameScope(expectedRun, expectedSlot) || ModHelper.GetModData(ModEntry.Id, backupKey, null) != expectedRaw)
                        throw new InvalidOperationException("Attention backup was not retained.");
                    if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != expectedRaw || !SameScope(expectedRun, expectedSlot))
                        throw new InvalidOperationException("Attention data changed before recovery.");
                    attempt = new RecoveryAttempt { run = expectedRun, slot = expectedSlot, backupKey = backupKey,
                        digest = Digest(expectedRaw), written = candidateRaw };
                    string marker = ShopData.Encode(attempt);
                    ValidateRecovery(ShopData.Decode<RecoveryAttempt>(marker, 8388608));
                    ModHelper.SetModData(ModEntry.Id, RecoveryKey, marker);
                    if (!SameScope(expectedRun, expectedSlot) || ModHelper.GetModData(ModEntry.Id, RecoveryKey, null) != marker)
                        throw new InvalidOperationException("Attention recovery marker was not retained.");
                    attempt.markerRaw = marker;
                    recovery = attempt;
                    log?.Invoke("Backed up unreadable attention data under " + backupKey);
                }
                ValidateRecovery(attempt);
                if (attempt.completed || Digest(expectedRaw) != attempt.digest ||
                    ModHelper.GetModData(ModEntry.Id, attempt.backupKey, null) != expectedRaw ||
                    ModHelper.GetModData(ModEntry.Id, RecoveryKey, null) != attempt.markerRaw ||
                    !SameScope(expectedRun, expectedSlot) || ModHelper.GetModData(ModEntry.Id, DataKey, null) != current)
                    throw new InvalidOperationException("Attention recovery data changed.");
                if (current != attempt.written)
                {
                    if (!SameScope(expectedRun, expectedSlot)) throw new InvalidOperationException("Attention save scope changed.");
                    ModHelper.SetModData(ModEntry.Id, DataKey, attempt.written);
                }
                if (!SameScope(expectedRun, expectedSlot) || ModHelper.GetModData(ModEntry.Id, DataKey, null) != attempt.written ||
                    ModHelper.GetModData(ModEntry.Id, attempt.backupKey, null) != expectedRaw ||
                    ModHelper.GetModData(ModEntry.Id, RecoveryKey, null) != attempt.markerRaw || !SameScope(expectedRun, expectedSlot))
                    throw new InvalidOperationException("Attention recovery was not retained.");
                var completed = new RecoveryAttempt { run = attempt.run, slot = attempt.slot, backupKey = attempt.backupKey,
                    digest = attempt.digest, written = attempt.written, completed = true };
                string finishedMarker = ShopData.Encode(completed);
                ValidateRecovery(ShopData.Decode<RecoveryAttempt>(finishedMarker, 8388608));
                ModHelper.SetModData(ModEntry.Id, RecoveryKey, finishedMarker);
                if (!SameScope(expectedRun, expectedSlot) || ModHelper.GetModData(ModEntry.Id, RecoveryKey, null) != finishedMarker ||
                    ModHelper.GetModData(ModEntry.Id, DataKey, null) != attempt.written ||
                    ModHelper.GetModData(ModEntry.Id, attempt.backupKey, null) != expectedRaw || !SameScope(expectedRun, expectedSlot))
                    throw new InvalidOperationException("Attention recovery completion was not retained.");
                SavedIssues next = unsaved.TryGetValue(scope, out SavedIssues latest) ? latest :
                    ShopData.Decode<SavedIssues>(attempt.written, 1048576);
                string nextRaw = EncodeChecked(next);
                data = next; invalid = false; invalidRaw = invalidKey = null; recovery = null;
                if (nextRaw != attempt.written)
                {
                    saveFailed = true;
                    if (!Save(next)) return;
                }
                else { saveFailed = false; unsaved.Remove(scope); }
                Changed(); ShopNotice.Show(ShopSettings.Text("attention.rebuilt"));
            }
            catch (Exception error)
            {
                log?.Invoke("Could not rebuild attention data: " + error);
                Changed(); ShopNotice.Show(ShopSettings.Text("attention.save_failed"));
            }
        }

        private static void RebuildMarker(string expectedRun, int expectedSlot, string expectedRaw)
        {
            try
            {
                if (ModHelper.GetModData(ModEntry.Id, RecoveryKey, null) != expectedRaw || !SameScope(expectedRun, expectedSlot))
                { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                string backupKey = MarkerBackupPrefix + Guid.NewGuid().ToString("N");
                if (ModHelper.GetModData(ModEntry.Id, backupKey, null) != null || !SameScope(expectedRun, expectedSlot))
                    throw new InvalidOperationException("Attention recovery backup key is unavailable.");
                ModHelper.SetModData(ModEntry.Id, backupKey, expectedRaw);
                if (!SameScope(expectedRun, expectedSlot) || ModHelper.GetModData(ModEntry.Id, backupKey, null) != expectedRaw ||
                    ModHelper.GetModData(ModEntry.Id, RecoveryKey, null) != expectedRaw || !SameScope(expectedRun, expectedSlot))
                    throw new InvalidOperationException("Attention recovery backup was not retained or changed.");
                var marker = new RecoveryAttempt { run = expectedRun, slot = expectedSlot, backupKey = backupKey,
                    digest = Digest(expectedRaw), written = "", completed = true, markerReset = true };
                string raw = ShopData.Encode(marker);
                ValidateRecovery(ShopData.Decode<RecoveryAttempt>(raw, 8388608));
                ModHelper.SetModData(ModEntry.Id, RecoveryKey, raw);
                if (!SameScope(expectedRun, expectedSlot) || ModHelper.GetModData(ModEntry.Id, RecoveryKey, null) != raw ||
                    ModHelper.GetModData(ModEntry.Id, backupKey, null) != expectedRaw || !SameScope(expectedRun, expectedSlot))
                    throw new InvalidOperationException("Attention recovery marker reset was not retained.");
                log?.Invoke("Backed up unreadable attention recovery data under " + backupKey);
                if (!Read() || saveFailed) { Changed(); ShopNotice.Show(ShopSettings.Text("attention.save_failed")); return; }
                Changed(); ShopNotice.Show(ShopSettings.Text("attention.rebuilt"));
            }
            catch (Exception error)
            {
                log?.Invoke("Could not rebuild attention recovery data: " + error);
                Changed(); ShopNotice.Show(ShopSettings.Text("attention.save_failed"));
            }
        }

        internal static List<AttentionItem> Snapshot()
        {
            var result = new Dictionary<string, AttentionItem>(StringComparer.Ordinal);
            if (!ShopInventory.InStore) return result.Values.ToList();
            bool readable = Ensure();
            SavedIssues visible = readable ? data : (loaded && invalid && unsaved.TryGetValue(run + ":" + slot, out SavedIssues pending) ? pending : null);
            if (visible != null)
                foreach (SavedIssue issue in visible.issues)
                {
                    string key = issue.key;
                    result[key] = new AttentionItem { Key = key, Feature = issue.feature, ItemId = issue.item, Title = issue.title,
                        Location = issue.location, ReasonKey = issue.reason, NeedsInspection = true, Resolve = () => Remove(key) };
                }
            if (!readable) result["attention.invalid"] = new AttentionItem { Key = "attention.invalid", Title = ShopSettings.Text("attention.open"),
                ReasonKey = "attention.unreadable", NeedsInspection = true, Retry = RetryRead,
                ResolveLabelKey = "attention.rebuild", Resolve = invalidRaw == null ? (Action)null : ConfirmRebuild };
            if (saveFailed && data != null) result["attention.unsaved"] = new AttentionItem { Key = "attention.unsaved", Title = ShopSettings.Text("attention.open"),
                ReasonKey = "attention.save_failed", NeedsInspection = true, Retry = () => { if (Ensure()) Save(data); } };
            foreach (var provider in providers.ToArray())
            {
                try
                {
                    foreach (AttentionItem item in provider.Value() ?? Enumerable.Empty<AttentionItem>())
                        if (item != null && !string.IsNullOrEmpty(item.Key))
                        {
                            string key = provider.Key + ":" + item.Key;
                            result[key] = new AttentionItem { Key = key, Feature = item.Feature, Title = item.Title, Location = item.Location,
                                ReasonKey = item.ReasonKey, ItemId = item.ItemId, NeedsInspection = item.NeedsInspection,
                                ActionLabelKey = item.ActionLabelKey, ResolveLabelKey = item.ResolveLabelKey, Retry = item.Retry, Resolve = item.Resolve };
                        }
                    providerErrors.Remove(provider.Key);
                }
                catch (Exception error)
                {
                    string message = error.ToString();
                    if (!providerErrors.TryGetValue(provider.Key, out string previous) || previous != message)
                        log?.Invoke("Attention provider " + provider.Key + " failed: " + message);
                    providerErrors[provider.Key] = message;
                    result[provider.Key + ":unavailable"] = new AttentionItem { Key = provider.Key + ":unavailable", Title = provider.Key,
                        ReasonKey = "attention.provider_failed", NeedsInspection = true };
                }
            }
            return result.Values.OrderByDescending(item => item.NeedsInspection).ThenBy(item => item.Title, StringComparer.CurrentCulture).ToList();
        }
        internal static List<AttentionItem> ForItem(int id) => id <= 0 ? new List<AttentionItem>() : Snapshot().Where(item => item.ItemId == id).ToList();
        internal static int Count => Snapshot().Count;
        internal static void Open()
        {
            if (!ShopInventory.InStore || CustomUIManager.Instance == null) return;
            Ensure(); Render(Snapshot());
        }
        private static string Signature(IEnumerable<AttentionItem> items, Dictionary<int, GameItem> locations) => string.Join("|", items.Select(item =>
            item.Key + ":" + item.Title + ":" + item.Location + ":" + item.ReasonKey + ":" + (item.Retry != null) + ":" + (item.Resolve != null) +
            ":" + item.ActionLabelKey + ":" + item.ResolveLabelKey + ":" + locations.ContainsKey(item.ItemId)));

        private static void Render(List<AttentionItem> items, Dictionary<int, GameItem> locations = null)
        {
            locations = locations ?? LocateItems(items);
            string ownRun = run; int ownSlot = slot;
            var builder = UiLayout.Rebuild(ref window, ModEntry.Id + ".attention", ShopSettings.Text("attention.open"), 620, 440);
            CustomUIWindow own = window;
            builder.OnClose(() => { if (ReferenceEquals(window, own)) window = null; });
            builder.BeginScrollFlexible(160);
            if (items.Count == 0) builder.AddLabel(ShopSettings.Text("attention.empty"));
            foreach (AttentionItem item in items)
            {
                builder.AddLabel(item.Title ?? "");
                if (builder.Last.text != null) builder.Last.text.richText = false;
                if (!string.IsNullOrEmpty(item.Location))
                { builder.AddLabel(item.Location); if (builder.Last.text != null) builder.Last.text.richText = false; }
                builder.AddLabel(ShopSettings.Text(item.ReasonKey ?? "availability.unknown"));
                builder.BeginRow();
                if (locations.ContainsKey(item.ItemId)) builder.AddButton(ShopSettings.Text("attention.locate"), () =>
                { if (SameScope(ownRun, ownSlot)) TryLocate(item.ItemId, true); });
                if (item.Retry != null) builder.AddButton(ShopSettings.Text(item.ActionLabelKey ?? "attention.retry"), () =>
                    Invoke(item.Key, ownRun, ownSlot, false));
                if (item.Resolve != null) builder.AddButton(ShopSettings.Text(item.ResolveLabelKey ?? "attention.resolve"), () =>
                    Invoke(item.Key, ownRun, ownSlot, true));
                builder.End();
            }
            builder.End().BeginRow();
            if (ShopNotice.HasResult) builder.AddButton(ShopSettings.Text("operation.last"), ShopNotice.OpenLastResult, "last_result");
            builder.AddButton(ShopSettings.Text("attention.refresh"), () => { if (SameScope(ownRun, ownSlot)) Render(Snapshot()); }).End().Show();
            signature = Signature(items, locations); dirty = false; refreshAt = Time.unscaledTime + 0.5f;
        }
        private static bool SameScope(string expectedRun, int expectedSlot) => ShopInventory.InStore && PlayerStore.HasInstance &&
            PlayerStore.Instance.runID == expectedRun && PlayerStore.Instance.saveSlotId == expectedSlot && PlayerStore.Instance.ModData() != null;
        private static void Invoke(string key, string ownRun, int ownSlot, bool resolve)
        {
            if (!SameScope(ownRun, ownSlot) || !ActionReady()) return;
            AttentionItem item = Snapshot().FirstOrDefault(entry => entry.Key == key);
            if (item == null) { Changed(); return; }
            try { (resolve ? item.Resolve : item.Retry)?.Invoke(); Changed(); }
            catch (Exception error) { log?.Invoke("Attention action failed: " + error); ShopNotice.Show(ShopSettings.Text("attention.action_failed")); }
        }
        private static bool ActionReady()
        {
            var ui = StoreUIManager.Instance;
            return ui != null && !KeyBindHelper.IsCapturing && !UIDragHandler.isDraggingAnyUI &&
                ItemMouseDragHandler.current?.IsDraggingItem != true &&
                (ui.blackscreen == null || !ui.blackscreen.gameObject.activeInHierarchy) &&
                (GenericConfirmationUIManager.Instance == null || !GenericConfirmationUIManager.Instance.panel.activeInHierarchy) &&
                (ui.confirmPanelUIManager == null || !ui.confirmPanelUIManager.panel.activeInHierarchy);
        }
        private static bool TryLocate(int id, bool perform)
        {
            if (id <= 0 || !ShopInventory.InStore) return false;
            foreach (var location in ShopInventory.MachineLocations())
                foreach (GameItem item in location.Inventory.childItems)
                {
                    if (item?.uniqueId != id || !ShopInventory.Owned(item)) continue;
                    if (!perform) return true;
                    if (ItemLocator.TryLocate(item, out string reason)) return true;
                    ShopNotice.Show(ShopSettings.Text(reason)); return false;
                }
            if (perform) ShopNotice.Show(ShopSettings.Text("inv.changed"));
            return false;
        }
        private static Dictionary<int, GameItem> LocateItems(IEnumerable<AttentionItem> items)
        {
            var result = new Dictionary<int, GameItem>();
            var wanted = new HashSet<int>(items.Where(item => item.ItemId > 0).Select(item => item.ItemId));
            if (wanted.Count == 0 || !ShopInventory.InStore) return result;
            foreach (var location in ShopInventory.MachineLocations())
                foreach (GameItem item in location.Inventory.childItems)
                    if (item != null && wanted.Contains(item.uniqueId) && ShopInventory.Owned(item))
                        result[item.uniqueId] = item;
            return result;
        }
        internal static void Tick()
        {
            if (ShopInventory.InStore) Ensure();
            else
            {
                if (loaded && (!PlayerStore.HasInstance || run != PlayerStore.Instance.runID || slot != PlayerStore.Instance.saveSlotId)) Reset();
                else if (window != null) { CustomUIWindow old = window; window = null; old.Close(); }
                return;
            }
            if (dirty)
            {
                List<AttentionItem> current = Snapshot();
                var added = current.Where(item => !announced.Contains(item.Key)).ToList();
                announced.Clear();
                foreach (AttentionItem item in current) announced.Add(item.Key);
                if (added.Count > 0) ShopNotice.Show(added.Count == 1 ?
                    added[0].Title + " · " + ShopSettings.Text(added[0].ReasonKey ?? "availability.unknown") :
                    ShopSettings.Format("attention.summary", added.Count), 6f);
                if (window?.IsAlive == true) Render(current);
                dirty = false;
            }
            if (window == null) return;
            if (!window.IsAlive) { window = null; return; }
            if (!SameScope(run, slot)) { Reset(); return; }
            if (!dirty && Time.unscaledTime < refreshAt) return;
            refreshAt = Time.unscaledTime + 0.5f;
            List<AttentionItem> items = Snapshot();
            var locations = LocateItems(items);
            if (dirty || Signature(items, locations) != signature) Render(items, locations);
        }
        private static void SceneChanged(Scene before, Scene after) => Reset();
        private static void Reset()
        {
            CustomUIWindow old = window; window = null; old?.Close();
            data = null; run = invalidRaw = invalidKey = null; slot = 0; loaded = invalid = saveFailed = false; recovery = null; signature = null; dirty = true;
            announced.Clear();
            ShopNotice.ResetSession();
        }
        internal static void Dispose()
        {
            ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset(); providers.Clear(); providerErrors.Clear(); log = null;
        }
    }
}
