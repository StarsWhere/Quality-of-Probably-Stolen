using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace ShopAssist
{
    // Shared by routing, trip unloading and field packing. Partial transfers
    // are limited to routing into an existing, compatible stack.
    internal static class WholeStackTransfer
    {
        [ThreadStatic] internal static bool AllowFieldContainerCargo;
        internal enum Result { Applied, Unchanged, Pending, Unavailable }
        internal sealed class Plan
        {
            internal GameItem Item, Stack;
            internal GameInventory From, To;
            internal GridShape Before, After;
            internal int Amount, SourceBefore, StackBefore;
            internal string SourceState, StackState;
            internal List<ContainerPart> Cargo;
            internal bool IsStack => Stack != null;
        }

        internal sealed class ContainerPart
        {
            internal GameItem Item;
            internal GameInventory Parent;
            internal GameGridInventory Contents;
            internal PixelWindow Window;
            internal GridShape Shape, ContentsShape;
            internal string State;
            internal int Id, Count;
        }

        internal sealed class Reservations
        {
            internal readonly Dictionary<GameInventory, List<GridShape>> Shapes = new Dictionary<GameInventory, List<GridShape>>();
            internal readonly Dictionary<GameItem, int> Counts = new Dictionary<GameItem, int>();
            internal readonly HashSet<GameItem> ExcludedStacks = new HashSet<GameItem>();
        }

        internal sealed class FieldReplacement
        {
            internal Plan Leave, IncomingOriginal, Take;
            internal GameItem Incoming;
            internal string Record;
        }

        [Serializable] private sealed class Journal
        {
            [JsonRequired] public int version = 2, slot, source, target, amount, targetBefore, sourceX, sourceY, targetX, targetY;
            public int sourceBefore;
            [JsonRequired] public string run, action, state, sourceName, targetName, location, sourceKey, targetKey;
        }
        private const string DataKey = "wholeStackTransfer";
        private const string BackupPrefix = "wholeStackTransfer.unreadable.";
        private const string ResetStatusSuffix = ".status";
        private static int owners;
        private static bool loaded, invalid;
        private static string run, raw, unreadableRaw;
        private static int slot;
        private static Journal journal;
        // A final write whose readback failed must stay inspectable even if the
        // in-memory store later returns the attempted terminal value.
        private static readonly Dictionary<(string run, int slot), Journal> uncertain = new Dictionary<(string, int), Journal>();
        private sealed class UncertainReset
        {
            internal string Original, BackupKey, Attempted;
        }
        private sealed class JournalRunHint { public JournalRunHint() { } public string run = null; }
        private sealed class JournalSlotHint { public JournalSlotHint() { } public int? slot = null; }
        private static readonly JsonSerializerSettings ScopeHintSettings = new JsonSerializerSettings {
            TypeNameHandling = TypeNameHandling.None, MissingMemberHandling = MissingMemberHandling.Ignore, MaxDepth = 16 };
        private static readonly Dictionary<(string run, int slot), UncertainReset> uncertainResets =
            new Dictionary<(string, int), UncertainReset>();
        internal static Func<int, bool> ResolveTrip;

        internal static void Acquire()
        {
            if (owners == 0) AttentionBook.Register("whole_stack", PendingItems);
            owners++;
        }
        internal static void Release()
        {
            if (owners <= 0) return;
            if (--owners == 0) { AttentionBook.Unregister("whole_stack"); Reset(); }
        }
        private static void Reset() { loaded = invalid = false; run = raw = unreadableRaw = null; slot = -1; journal = null; }

        private static bool Valid(Journal value, string currentRun, int currentSlot)
        {
            if (value == null || (value.version != 1 && value.version != 2 && value.version != 3) || value.run != currentRun || value.slot != currentSlot ||
                value.sourceName == null || value.targetName == null || value.location == null || value.sourceKey == null || value.targetKey == null ||
                value.sourceName.Length > 4096 || value.targetName.Length > 4096 || value.location.Length > 8192 ||
                value.sourceKey.Length > 4096 || value.targetKey.Length > 4096 || value.targetBefore < 0 ||
                value.sourceX < 0 || value.sourceY < 0 || value.targetX < 0 || value.targetY < 0) return false;
            if (value.state == "reset")
                return value.action == "" && value.source == 0 && value.target == 0 && value.amount == 0 &&
                    value.targetBefore == 0 && value.sourceBefore == 0 && value.sourceX == 0 && value.sourceY == 0 && value.targetX == 0 && value.targetY == 0 &&
                    value.sourceName == "" && value.targetName == "" && value.location == "" && value.targetKey == "" &&
                    value.sourceKey.StartsWith(BackupPrefix, StringComparison.Ordinal) &&
                    Guid.TryParseExact(value.sourceKey.Substring(BackupPrefix.Length), "N", out _);
            return value.source > 0 && value.target >= 0 && value.amount > 0 &&
                (value.version == 1 || value.version == 3 && value.action == "f36.open" && value.target > 0 &&
                    value.target != value.source && value.sourceBefore == value.amount && value.targetBefore > 0 &&
                    value.sourceKey.StartsWith("field:", StringComparison.Ordinal) && value.targetKey == "field:ground" ||
                 value.version == 2 && value.sourceBefore >= value.amount &&
                    (value.target > 0 || value.sourceBefore == value.amount) &&
                    (long)value.targetBefore + value.amount <= int.MaxValue) &&
                (value.state == "pending" || value.state == "done" || value.state == "resolved") &&
                (value.action == "f08.open" || value.action == "f11.open" || value.action == "f25.open" || value.action == "f36.open");
        }

        // A failed storage read or a recognisable record from another save/version
        // is not proof that its contents may be reset.
        private static bool ConfirmedUnreadable(string current, string currentRun, int currentSlot)
        {
            if (current == null) return false;
            try
            {
                Journal value = ShopData.Decode<Journal>(current, 32768);
                if (value == null) return true;
                if (value.run != currentRun || value.slot != currentSlot) return false;
                return !Valid(value, currentRun, currentSlot);
            }
            catch (FormatException) { return !ExplicitlyOtherSave(current, currentRun, currentSlot); }
            catch (JsonException) { return !ExplicitlyOtherSave(current, currentRun, currentSlot); }
        }

        private static bool ExplicitlyOtherSave(string current, string currentRun, int currentSlot)
        {
            // Decode identity separately from the full schema: unsupported or
            // incomplete records may still explicitly identify another save.
            string otherRun;
            int? otherSlot;
            try { otherRun = JsonConvert.DeserializeObject<JournalRunHint>(current, ScopeHintSettings)?.run; }
            catch (JsonException) { otherRun = null; }
            if (otherRun != null && otherRun != currentRun) return true;
            try { otherSlot = JsonConvert.DeserializeObject<JournalSlotHint>(current, ScopeHintSettings)?.slot; }
            catch (JsonException) { otherSlot = null; }
            return otherSlot.HasValue && otherSlot.Value != currentSlot;
        }

        private static bool Load()
        {
            if (!PlayerStore.HasInstance || string.IsNullOrEmpty(PlayerStore.Instance.runID) ||
                PlayerStore.Instance.saveSlotId < 0 || PlayerStore.Instance.ModData() == null) return false;
            string currentRun = PlayerStore.Instance.runID;
            int currentSlot = PlayerStore.Instance.saveSlotId;
            if (uncertainResets.TryGetValue((currentRun, currentSlot), out UncertainReset failedReset))
            {
                // A different real pending transfer must remain inspectable; never
                // replace it with the older failed-reset dialog.
                try
                {
                    string latest = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                    if (latest != null && latest != failedReset.Original && latest != failedReset.Attempted)
                    {
                        Journal replacement = ShopData.Decode<Journal>(latest, 32768);
                        if (replacement?.state == "pending" && Valid(replacement, currentRun, currentSlot) &&
                            PlayerStore.HasInstance && PlayerStore.Instance.runID == currentRun && PlayerStore.Instance.saveSlotId == currentSlot)
                        {
                            uncertainResets.Remove((currentRun, currentSlot));
                            journal = replacement; run = currentRun; slot = currentSlot; raw = latest;
                            unreadableRaw = null; loaded = true; invalid = false;
                            return true;
                        }
                    }
                }
                catch (Exception error) { Debug.LogWarning("[ShopAssist] Could not inspect a replacement transfer journal: " + error); }
                run = currentRun; slot = currentSlot; loaded = invalid = true; journal = null;
                raw = null; unreadableRaw = failedReset.Original;
                return false;
            }
            if (uncertain.TryGetValue((currentRun, currentSlot), out Journal unconfirmed))
            {
                journal = unconfirmed; run = currentRun; slot = currentSlot; loaded = true; invalid = false; unreadableRaw = null;
                return true;
            }
            string current = null;
            bool read = false;
            try
            {
                current = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                read = true;
                if (loaded && !invalid && run == currentRun && slot == currentSlot && current == raw && journal?.state != "reset") return true;
                Journal value = current == null ? null : ShopData.Decode<Journal>(current, 32768);
                if (current != null && !Valid(value, currentRun, currentSlot)) throw new FormatException("Invalid whole-stack journal");
                if (value?.state == "reset")
                {
                    string original = ModHelper.GetModData(ModEntry.Id, value.sourceKey, null);
                    if (!ConfirmedUnreadable(original, currentRun, currentSlot)) throw new FormatException("Missing unreadable journal backup");
                    if (ModHelper.GetModData(ModEntry.Id, value.sourceKey + ResetStatusSuffix, null) != "verified")
                    {
                        uncertainResets[(currentRun, currentSlot)] = new UncertainReset {
                            Original = original, BackupKey = value.sourceKey, Attempted = current };
                        journal = null; run = currentRun; slot = currentSlot; raw = null; unreadableRaw = original;
                        loaded = invalid = true;
                        return false;
                    }
                }
                journal = value; run = currentRun; slot = currentSlot; raw = current; unreadableRaw = null; loaded = true; invalid = false;
                return true;
            }
            catch (Exception error)
            {
                loaded = true; invalid = true; run = currentRun; slot = currentSlot; raw = null; journal = null; unreadableRaw = null;
                if (read && current != null)
                    try { if (ConfirmedUnreadable(current, currentRun, currentSlot)) unreadableRaw = current; }
                    catch (Exception classifyError) { Debug.LogWarning("[ShopAssist] Whole-stack journal classification failed: " + classifyError); }
                Debug.LogWarning("[ShopAssist] Whole-stack journal could not be read: " + error);
                return false;
            }
        }

        private static bool Write(Journal value)
        {
            if (!PlayerStore.HasInstance || PlayerStore.Instance.runID != run || PlayerStore.Instance.saveSlotId != slot ||
                PlayerStore.Instance.ModData() == null || !Valid(value, run, slot)) return false;
            try
            {
                string next = ShopData.Encode(value);
                if (next.Length > 32768) return false;
                CheckedModData.Write(DataKey, next);
                journal = value; raw = next; unreadableRaw = null; loaded = true; invalid = false;
                if (value.state != "pending") uncertain.Remove((value.run, value.slot));
                AttentionBook.Changed();
                return true;
            }
            catch (Exception error)
            {
                if (value.state != "pending" && journal?.state == "pending")
                {
                    uncertain[(run, slot)] = journal;
                    // Restore the prewrite in the same mod-data store where possible.
                    // If that also fails, the in-session latch still fails closed.
                    try { ModHelper.SetModData(ModEntry.Id, DataKey, ShopData.Encode(journal)); }
                    catch (Exception restoreError) { Debug.LogWarning("[ShopAssist] Pending transfer could not be restored: " + restoreError); }
                }
                loaded = false;
                Debug.LogWarning("[ShopAssist] Whole-stack journal could not be saved: " + error);
                AttentionBook.Changed();
                return false;
            }
        }

        internal static bool Ready => Load() && journal?.state != "pending";
        internal static bool PendingFor(string action) => !Load() || journal?.state == "pending" && journal.action == action;

        private static bool Begin(Plan plan, string action, string location, string sourceKey, string targetKey)
        {
            if (!Ready) return false;
            var next = new Journal { run = run, slot = slot, action = action, state = "pending",
                source = plan.Item.GetUniqueID(), target = plan.Stack?.GetUniqueID() ?? 0, amount = plan.Amount, sourceBefore = plan.SourceBefore,
                sourceName = plan.Item.GetDisplayName(true) ?? "", targetName = plan.Stack?.GetDisplayName(true) ?? "",
                sourceX = plan.Before.minX, sourceY = plan.Before.minY, targetX = plan.After.minX, targetY = plan.After.minY,
                targetBefore = plan.StackBefore, sourceKey = sourceKey ?? "", targetKey = targetKey ?? "", location = location ?? "" };
            return Write(next);
        }

        internal static FieldReplacement BeginFieldReplacement(Plan leave, GameItem incoming, string location, string sourceKey)
        {
            if (!Ready || leave == null || leave.Stack != null || leave.Item == null || incoming == null ||
                leave.Amount != leave.SourceBefore || incoming.unitCount <= 0 || ReferenceEquals(leave.Item, incoming) ||
                !ReferenceEquals(incoming.parentInventory, leave.To) || !leave.To.childItems.Contains(incoming) ||
                !SafeSource(incoming) || !ShopInventory.Movable(incoming)) return null;
            Plan incomingOriginal = Create(incoming, leave.From, null, incoming.modifiedShape, 0);
            if (incomingOriginal == null) return null;
            var next = new Journal { version = 3, run = run, slot = slot, action = "f36.open", state = "pending",
                source = leave.Item.GetUniqueID(), target = incoming.GetUniqueID(), amount = leave.Amount,
                sourceBefore = leave.SourceBefore, targetBefore = incoming.unitCount,
                sourceName = leave.Item.GetDisplayName(true) ?? "", targetName = incoming.GetDisplayName(true) ?? "",
                sourceX = leave.Before.minX, sourceY = leave.Before.minY, targetX = leave.After.minX, targetY = leave.After.minY,
                sourceKey = sourceKey ?? "", targetKey = "field:ground", location = location ?? "" };
            return Write(next) ? new FieldReplacement { Leave = leave, Incoming = incoming, IncomingOriginal = incomingOriginal, Record = raw } : null;
        }

        private static bool ReplacementMatches(FieldReplacement replacement) => replacement != null && Load() &&
            journal?.version == 3 && journal.state == "pending" && raw == replacement.Record;

        private static bool ReplacementAllows(FieldReplacement replacement, Plan plan) => ReplacementMatches(replacement) &&
            plan != null && plan.Stack == null && plan.Amount == plan.SourceBefore &&
            (ReferenceEquals(plan.Item, replacement.Leave.Item) &&
                (ReferenceEquals(plan.From, replacement.Leave.From) && ReferenceEquals(plan.To, replacement.Leave.To) ||
                 ReferenceEquals(plan.From, replacement.Leave.To) && ReferenceEquals(plan.To, replacement.Leave.From)) ||
             ReferenceEquals(plan.Item, replacement.Incoming) && ReferenceEquals(plan.From, replacement.Leave.To) &&
                ReferenceEquals(plan.To, replacement.Leave.From));

        internal static bool FieldReplacementOriginal(FieldReplacement replacement, GameInventory inventory)
        {
            Plan leave = replacement?.Leave;
            return leave != null && ReplacementMatches(replacement) && ReferenceEquals(leave.Item.parentInventory, inventory) &&
                inventory != null && inventory.childItems.Contains(leave.Item) && leave.Item.unitCount == leave.SourceBefore &&
                State(leave.Item) == leave.SourceState && CargoUnchanged(leave) &&
                (ReferenceEquals(inventory, leave.From) ? !leave.To.childItems.Contains(leave.Item) &&
                    InventorySortPlan.SameShape(leave.Item.modifiedShape, leave.Before) :
                 ReferenceEquals(inventory, leave.To) && !leave.From.childItems.Contains(leave.Item) &&
                    InventorySortPlan.SameShape(leave.Item.modifiedShape, leave.After));
        }

        internal static bool FieldReplacementIncomingOriginal(FieldReplacement replacement)
        {
            Plan original = replacement?.IncomingOriginal;
            return ReplacementMatches(replacement) && original != null && ReferenceEquals(original.Item.parentInventory, original.From) &&
                original.From.childItems.Contains(original.Item) && !original.To.childItems.Contains(original.Item) &&
                original.Item.unitCount == original.SourceBefore && State(original.Item) == original.SourceState &&
                InventorySortPlan.SameShape(original.Item.modifiedShape, original.Before) && CargoUnchanged(original);
        }

        internal static bool RecoverDetachedFieldItem(FieldReplacement replacement, bool incoming, Func<bool> scope)
        {
            Plan original = incoming ? replacement?.IncomingOriginal : replacement?.Leave;
            if (!ReplacementMatches(replacement) || original == null || scope == null || !scope()) return false;
            GameItem item = original.Item;
            GridShape attempted = incoming ? replacement.Take?.After : original.After;
            if (item == null || item.parentInventory != null || original.From.childItems.Contains(item) || original.To.childItems.Contains(item) ||
                item.unitCount != original.SourceBefore || State(item) != original.SourceState ||
                !(InventorySortPlan.SameShape(item.modifiedShape, original.Before) || InventorySortPlan.SameShape(item.modifiedShape, attempted)) ||
                !InventoryAutoInsert.KnownMutationPatches() || !DetachedItemSafe(item) || !CargoUnchanged(original, true) ||
                !SafeTarget(original.From) || original.From.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(original.From)) return false;
            SlotMarker marker = original.From.TryInventorySlot(item, original.SourceBefore, original.Before);
            if (marker?.inventory != original.From || marker.item != item || marker.targetItem != null ||
                marker.numTransfer < original.SourceBefore || !InventorySortPlan.SameShape(marker.itemGridShape, original.Before)) return false;
            try { marker.TryAcceptOnce(original.SourceBefore); }
            catch (Exception error) { Debug.LogWarning("[ShopAssist] Detached field item recovery failed: " + error); }
            return scope() && (incoming ? FieldReplacementIncomingOriginal(replacement) : FieldReplacementOriginal(replacement, original.From));
        }

        internal static bool FinishFieldReplacement(FieldReplacement replacement) =>
            ReplacementMatches(replacement) && Finish("done");

        private static bool Finish(string state)
        {
            if (!Load() || journal?.state != "pending") return false;
            var next = new Journal { version = journal.version, run = journal.run, slot = journal.slot, action = journal.action, state = state,
                source = journal.source, target = journal.target, amount = journal.amount, sourceBefore = journal.sourceBefore,
                sourceName = journal.sourceName, targetName = journal.targetName, location = journal.location,
                sourceX = journal.sourceX, sourceY = journal.sourceY, targetX = journal.targetX, targetY = journal.targetY,
                targetBefore = journal.targetBefore, sourceKey = journal.sourceKey, targetKey = journal.targetKey };
            return Write(next);
        }

        private static string Detail(Journal value)
        {
            int before = value.version == 1 ? value.amount : value.sourceBefore;
            object sourceCount = before == value.amount ? (object)before : before + " -> " + (before - value.amount);
            object targetCount = before == value.amount ? (object)value.targetBefore : value.targetBefore + " -> " + (value.targetBefore + value.amount);
            return ShopSettings.Format("f08.journal_detail", value.location,
                value.sourceX + 1, value.sourceY + 1, value.targetX + 1, value.targetY + 1, sourceCount, targetCount);
        }

        internal static string FieldPending()
        {
            if (!Load() || journal?.state != "pending" || journal.action != "f36.open") return null;
            return journal.sourceName + " · " + Detail(journal);
        }

        internal static bool ResolveField() => Load() && journal?.state == "pending" && journal.action == "f36.open" && Finish("resolved");

        private static int ContainerId(string key)
        {
            string[] parts = (key ?? "").Split(':');
            string candidate = parts.Length > 1 && parts[0] == "item" ? parts[1] :
                parts.Length > 2 && parts[0] == "field" && parts[1] == "bag" ? parts[2] : null;
            return int.TryParse(candidate, out int id) && id > 0 ? id : 0;
        }

        private static bool SameSave(string expectedRun, int expectedSlot) => ShopInventory.InStore && PlayerStore.HasInstance &&
            PlayerStore.Instance.runID == expectedRun && PlayerStore.Instance.saveSlotId == expectedSlot && PlayerStore.Instance.ModData() != null;

        private static bool CaptureUnreadable(string expectedRun, int expectedSlot, string original, out string current, out string backupKey)
        {
            current = backupKey = null;
            if (original == null || !SameSave(expectedRun, expectedSlot) || uncertain.ContainsKey((expectedRun, expectedSlot))) return false;
            try
            {
                current = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                if (!SameSave(expectedRun, expectedSlot) || !ConfirmedUnreadable(original, expectedRun, expectedSlot)) return false;
                if (!uncertainResets.TryGetValue((expectedRun, expectedSlot), out UncertainReset previous)) return current == original;
                if (previous.Original != original || current != previous.Original && (previous.Attempted == null || current != previous.Attempted) ||
                    ModHelper.GetModData(ModEntry.Id, previous.BackupKey, null) != original) return false;
                backupKey = previous.BackupKey;
                return SameSave(expectedRun, expectedSlot);
            }
            catch (Exception error)
            {
                Debug.LogWarning("[ShopAssist] Whole-stack journal could not be rechecked: " + error);
                return false;
            }
        }

        private static void ConfirmUnreadableReset(string expectedRun, int expectedSlot, string original)
        {
            if (!CaptureUnreadable(expectedRun, expectedSlot, original, out string observed, out string backupKey))
            { ShopNotice.Show(ShopSettings.Text("inv.changed")); AttentionBook.Changed(); return; }
            var prompt = GenericConfirmationUIManager.Instance;
            if (prompt == null) { ShopNotice.Show(ShopSettings.Text("f08.journal_failed")); return; }
            prompt.OpenPanel(ShopSettings.Text("f08.journal_reset_warning"), ShopSettings.Text("f08.journal_reset_confirm"),
                ShopSettings.Text("f08.journal_reset_cancel"), () =>
                {
                    prompt.ClosePanel();
                    ResetUnreadable(expectedRun, expectedSlot, original, observed, backupKey);
                }, () => prompt.ClosePanel());
        }

        private static void ResetUnreadable(string expectedRun, int expectedSlot, string original, string observed, string previousBackup)
        {
            if (!CaptureUnreadable(expectedRun, expectedSlot, original, out string current, out string backupKey) ||
                current != observed || backupKey != previousBackup)
            { ShopNotice.Show(ShopSettings.Text("inv.changed")); AttentionBook.Changed(); return; }
            try
            {
                // Each attempt gets its own backup and prewritten barrier. A failed
                // attempt must not be released by a later attempt's verification.
                backupKey = BackupPrefix + Guid.NewGuid().ToString("N");
                if (ModHelper.GetModData(ModEntry.Id, backupKey, null) != null ||
                    ModHelper.GetModData(ModEntry.Id, backupKey + ResetStatusSuffix, null) != null)
                    throw new InvalidOperationException("Unreadable journal backup key already exists");
                CheckedModData.Write(backupKey, original);
                CheckedModData.Write(backupKey + ResetStatusSuffix, "pending");
                if (!SameSave(expectedRun, expectedSlot) || ModHelper.GetModData(ModEntry.Id, DataKey, null) != observed ||
                    ModHelper.GetModData(ModEntry.Id, backupKey, null) != original ||
                    ModHelper.GetModData(ModEntry.Id, backupKey + ResetStatusSuffix, null) != "pending" || !SameSave(expectedRun, expectedSlot))
                { ShopNotice.Show(ShopSettings.Text("inv.changed")); AttentionBook.Changed(); return; }
            }
            catch (Exception error)
            {
                Debug.LogWarning("[ShopAssist] Unreadable whole-stack journal could not be preserved: " + error);
                ShopNotice.Show(ShopSettings.Text("f08.journal_failed")); AttentionBook.Changed(); return;
            }

            var cleared = new Journal { run = expectedRun, slot = expectedSlot, state = "reset", action = "",
                sourceName = "", targetName = "", location = "", sourceKey = backupKey, targetKey = "" };
            string next = null;
            try
            {
                if (!Valid(cleared, expectedRun, expectedSlot)) throw new InvalidOperationException("Invalid journal reset marker");
                next = ShopData.Encode(cleared);
                if (next.Length > 32768) throw new InvalidOperationException("Journal reset marker is too large");
                CheckedModData.Write(DataKey, next);
                if (!SameSave(expectedRun, expectedSlot) || ModHelper.GetModData(ModEntry.Id, backupKey, null) != original ||
                    ModHelper.GetModData(ModEntry.Id, backupKey + ResetStatusSuffix, null) != "pending" || !SameSave(expectedRun, expectedSlot))
                    throw new InvalidOperationException("Journal reset could not be verified");
                CheckedModData.Write(backupKey + ResetStatusSuffix, "verified");
                if (!SameSave(expectedRun, expectedSlot) || ModHelper.GetModData(ModEntry.Id, DataKey, null) != next ||
                    ModHelper.GetModData(ModEntry.Id, backupKey, null) != original || !SameSave(expectedRun, expectedSlot))
                    throw new InvalidOperationException("Verified journal reset changed");
                uncertainResets.Remove((expectedRun, expectedSlot));
                journal = cleared; run = expectedRun; slot = expectedSlot; raw = next; unreadableRaw = null; loaded = true; invalid = false;
                AttentionBook.Changed(); ShopNotice.Show(ShopSettings.Text("f08.journal_reset_done"));
            }
            catch (Exception error)
            {
                uncertainResets[(expectedRun, expectedSlot)] = new UncertainReset { Original = original, BackupKey = backupKey, Attempted = next };
                try
                {
                    if (SameSave(expectedRun, expectedSlot)) CheckedModData.Write(backupKey + ResetStatusSuffix, "pending");
                }
                catch (Exception markerError) { Debug.LogWarning("[ShopAssist] Unconfirmed reset marker could not be saved: " + markerError); }
                // An attempted reset may have been written even if its readback failed.
                // Restore only our own value, never a new journal from another operation.
                try
                {
                    if (SameSave(expectedRun, expectedSlot))
                    {
                        string live = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                        if (live == original || next != null && live == next) CheckedModData.Write(DataKey, original);
                    }
                }
                catch (Exception restoreError) { Debug.LogWarning("[ShopAssist] Unreadable journal could not be restored: " + restoreError); }
                loaded = false; invalid = true; unreadableRaw = original;
                Debug.LogWarning("[ShopAssist] Whole-stack journal reset failed: " + error);
                ShopNotice.Show(ShopSettings.Text("f08.journal_failed")); AttentionBook.Changed();
            }
        }

        private static IEnumerable<AttentionItem> PendingItems()
        {
            if (!Load())
            {
                string expectedRun = run, original = unreadableRaw;
                int expectedSlot = slot;
                return new[] { new AttentionItem { Key = "unreadable", Title = ShopSettings.Text("f08.journal_title"),
                    ReasonKey = original == null ? "f08.journal_read_failed" : "f08.journal_unreadable", NeedsInspection = true,
                    Retry = () => { loaded = false; AttentionBook.Changed(); },
                    ResolveLabelKey = original == null ? null : "f08.journal_reset_action",
                    Resolve = original == null ? null : (Action)(() => ConfirmUnreadableReset(expectedRun, expectedSlot, original)) } };
            }
            if (journal?.state != "pending") return Array.Empty<AttentionItem>();
            var current = journal;
            Action resolve = () =>
            {
                if (!Load() || !ReferenceEquals(current, journal) || current.state != "pending") return;
                if (current.action == "f25.open" && (ResolveTrip == null || !ResolveTrip(current.source)))
                { ShopNotice.Show(ShopSettings.Text("f25.record_failed")); return; }
                if (!Finish("resolved")) ShopNotice.Show(ShopSettings.Text("f08.journal_failed"));
            };
            var result = new List<AttentionItem> { new AttentionItem { Key = "source", Feature = current.action,
                Title = current.sourceName, Location = Detail(current), ReasonKey = "f08.pending", ItemId = current.source,
                NeedsInspection = true, ResolveLabelKey = current.action == "f25.open" ? "f25.resolve_pending" : null, Resolve = resolve } };
            if (current.target > 0 && current.target != current.source)
                result.Add(new AttentionItem { Key = "target", Feature = current.action, Title = current.targetName,
                    Location = Detail(current), ReasonKey = "f08.pending", ItemId = current.target, NeedsInspection = true,
                    ResolveLabelKey = current.action == "f25.open" ? "f25.resolve_pending" : null, Resolve = resolve });
            foreach (var area in new[] { (key: "source-area", label: "f08.source_area", id: ContainerId(current.sourceKey)),
                (key: "target-area", label: "f08.target_area", id: ContainerId(current.targetKey)) })
                if (area.id > 0 && area.id != current.source && area.id != current.target)
                    result.Add(new AttentionItem { Key = area.key, Feature = current.action, Title = ShopSettings.Text(area.label),
                        Location = Detail(current), ReasonKey = "f08.pending", ItemId = area.id, NeedsInspection = true,
                        ResolveLabelKey = current.action == "f25.open" ? "f25.resolve_pending" : null, Resolve = resolve });
            return result;
        }

        // StateSignature contains the instance ID. Strip only its known prefix;
        // all game state, exposed features and pin state remain.
        private static string State(GameItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.identifier)) return null;
            string signature = InventoryArrangeTransaction.StateSignature(item);
            string prefix = item.identifier.Length + ":" + item.identifier + "|" + item.uniqueId + "|";
            if (signature == null || !signature.StartsWith(prefix, StringComparison.Ordinal)) return null;
            return signature.Substring(prefix.Length) + "|" + ShopData.Encode(new object[] { item.name, item.identifierName,
                item.shortDescription, item.longDescription, item.flavorText, item.customText, item.unitValue,
                item.lateUnitValue, item.unitBaseValue, item.backupUnitValue, item.forceDisableActivate, item.forceDisableUse,
                item.activateDefault, item.isCombatBackpack, item.spritePath, item.spriteAtlasPath, item.useSpriteFromMod,
                item.bonusAccuracy, item.triggerOverwatch, item.canUseOutsideCombat, item.recipeManager != null,
                item.contentWindow != null, item.effects?.Count ?? 0, item.itemTypes });
        }

        private static bool Compatible(GameItem first, GameItem second)
        {
            if (first == null || second == null || first.GetType() != typeof(GameItemElement) || second.GetType() != typeof(GameItemElement) ||
                first.contentWindow != null || second.contentWindow != null || first.identifier != second.identifier ||
                first.IsTag("BOUGHT_PRICE_TAG") || second.IsTag("BOUGHT_PRICE_TAG") ||
                first.IsTag(WineHelper.WINE_YEAST_REUSABLE_TAG) || second.IsTag(WineHelper.WINE_YEAST_REUSABLE_TAG) ||
                ItemPinHelper.IsPinned(first) || ItemPinHelper.IsPinned(second) || first.customText != second.customText ||
                first.GetDisplayName(true) != second.GetDisplayName(true) || first.unitValue != second.unitValue ||
                first.lateUnitValue != second.lateUnitValue || first.unitBaseValue != second.unitBaseValue ||
                first.backupUnitValue != second.backupUnitValue || first.forceDisableActivate != second.forceDisableActivate ||
                first.forceDisableUse != second.forceDisableUse || first.activateDefault != second.activateDefault ||
                first.isCombatBackpack != second.isCombatBackpack || first.spritePath != second.spritePath ||
                first.spriteAtlasPath != second.spriteAtlasPath || first.useSpriteFromMod != second.useSpriteFromMod ||
                first.bonusAccuracy != second.bonusAccuracy || first.triggerOverwatch != second.triggerOverwatch ||
                first.canUseOutsideCombat != second.canUseOutsideCombat || first.recipeManager != null || second.recipeManager != null ||
                first.effects?.Count > 0 || second.effects?.Count > 0 || !InventoryAutoInsert.SafeItem(first, true, true) ||
                !InventoryAutoInsert.SafeItem(second, true)) return false;
            string a = State(first), b = State(second);
            return a != null && a == b && (first.itemTypes ?? new List<string>()).SequenceEqual(second.itemTypes ?? new List<string>());
        }

        internal static bool SafeSource(GameItem item) => item != null && InventoryAutoInsert.KnownMutationPatches() &&
            (InventoryAutoInsert.SafeItem(item, true, true) || AllowFieldContainerCargo && SafeFieldContainer(item)) &&
            InventoryAutoInsert.SafeTransferInventory(item.parentInventory, item);

        private static bool SafeFieldContainer(GameItem item) => CaptureCargo(item, out _);

        private static bool SafeContainerItem(GameItem item, bool detached = false) => item != null && item.GetType() == typeof(GameItemElement) &&
            item.unitCount == 1 && item.modifiedShape != null && !string.IsNullOrEmpty(item.identifier) && item.examineWindow == null &&
            (detached ? ShopInventory.IsOrdinaryContainer(item) && item.contentWindow != null && !item.contentWindow.isLocked &&
                !LockHelper.IsCurrentlyLocked(item) && !item.IsTag("LOCKED_TAG") &&
                !(item.IsTag("TRACK_OPENED_TAG") && !item.IsTag("OPENED_TAG")) :
                ShopInventory.SafeContainerForPeek(item) && ShopInventory.Movable(item)) &&
            item.itemFeatures != null && item.itemFeatures.All(feature => feature != null && feature.GetType() == typeof(ItemFeature) &&
                feature.featureType != ItemFeature.FeatureType.ClientTemporary && feature.category != null && feature.category != "contrabandMarkUp") &&
            item.mayInventoryAddItemFunc == null && item.mayInventoryRemoveItemFunc == null && item.mayStackItemFunc == null &&
            item.slotPriorityFunc == null && item.maxSlotAddItemFunc == null && item.maxSlotRemoveItemFunc == null &&
            item.maxSlotAmountItemFunc == null && item.minSlotAmountItemFunc == null && item.isItemStateValidFunc == null &&
            item.parentItemShapeModifierForItemFunc == null && item.parentItemStateModifierForItemFunc == null &&
            item.parentItemSpriteModifierForItemFunc == null && item.getItemSpriteModifierForItemFunc == null &&
            InventoryAutoInsert.KnownCallbacks(item.onSlotAddItemFunc, callback => ReferenceEquals(callback.Target, item) &&
                InventoryAutoInsert.Method(callback, "GameItem", "<.ctor>b__177_0")) &&
            item.onSlotRemoveItemFunc == null && item.onSlotChangeAmountItemFunc == null && item.onChangeAmountItemFunc == null;

        internal static bool TryFieldContainerItems(GameItem item, out List<GameItem> items)
        {
            items = null;
            if (!CaptureCargo(item, out var cargo)) return false;
            items = cargo.Select(part => part.Item).ToList();
            return true;
        }

        private static bool DetachedItemSafe(GameItem item) => item is GameItemElement element && !element.IsDestroyed() && item.parentInventory == null &&
            GeneralHelper.IsItemOwned(item) && !item.IsTag("ITEM_HIDDEN_TAG") && !item.IsTag("ITEM_BEHIND_LOCK_TAG") &&
            !ItemPinHelper.IsPinned(item) && !DebrisHelper.IsDebris(item) &&
            (item.contentWindow == null ? InventoryAutoInsert.SafeItem(item, true, true) : SafeContainerItem(item, true));

        private static bool CaptureCargo(GameItem root, out List<ContainerPart> cargo, bool detachedRoot = false)
        {
            cargo = new List<ContainerPart>();
            if (root == null || !root.IsTag("BACKPACK_TAG") ||
                !InventoryAutoInsert.KnownPatches(typeof(GameGridInventory).GetMethod(nameof(GameGridInventory.CheckShapePlacement)))) return false;
            var pending = new Queue<GameItem>();
            var seen = new HashSet<GameItem>();
            var ids = new HashSet<int>();
            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                GameItem item = pending.Dequeue();
                bool detached = detachedRoot && ReferenceEquals(item, root);
                if (item == null || !seen.Add(item) || cargo.Count >= 256 ||
                    (detached ? !DetachedItemSafe(item) : item.parentInventory == null || !ShopInventory.Movable(item)) ||
                    item.modifiedShape == null || item.unitCount <= 0) return false;
                bool container = item.contentWindow != null;
                if (container ? !SafeContainerItem(item, detached) : !InventoryAutoInsert.SafeItem(item, true, true) ||
                    item.itemFeatures.Any(feature => feature.GetType() != typeof(ItemFeature))) return false;
                int id = item.GetUniqueID();
                if (id <= 0 || !ids.Add(id)) return false;
                string state = State(item);
                if (state == null) return false;
                var part = new ContainerPart { Item = item, Parent = item.parentInventory, Id = id, Count = item.unitCount,
                    Shape = item.modifiedShape.Clone(), State = state };
                if (container)
                {
                    if (!(item.contentWindow.childElement is GameGridInventory grid) || grid.inventoryShape == null ||
                        grid.inventoryShape.width > 64 || grid.inventoryShape.height > 64 || grid.childItems.Count > 256 ||
                        !InventoryAutoInsert.SafeGrid(grid) || grid.GetParentItem() != item || grid.IsRemoveLocked()) return false;
                    part.Window = item.contentWindow; part.Contents = grid; part.ContentsShape = grid.inventoryShape.Clone();
                    foreach (GameItem child in grid.childItems)
                    {
                        if (child == null || child.parentInventory != grid || child.modifiedShape == null ||
                            !grid.CheckShapePlacement(child, child.modifiedShape, new List<GameItem> { child })) return false;
                        pending.Enqueue(child);
                    }
                }
                cargo.Add(part);
            }
            return cargo.Count > 0 && cargo[0].Contents != null;
        }

        private static bool SameCargo(List<ContainerPart> a, List<ContainerPart> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i]; var y = b[i];
                if (!ReferenceEquals(x.Item, y.Item) || i > 0 && (!ReferenceEquals(x.Parent, y.Parent) ||
                    !InventorySortPlan.SameShape(x.Shape, y.Shape)) || !ReferenceEquals(x.Window, y.Window) || !ReferenceEquals(x.Contents, y.Contents) ||
                    x.Contents != null && !InventorySortPlan.SameShape(x.ContentsShape, y.ContentsShape) ||
                    x.Id != y.Id || x.Count != y.Count || x.State != y.State) return false;
            }
            return true;
        }

        private static bool CargoUnchanged(Plan plan, bool detachedRoot = false) => plan.Cargo == null ||
            CaptureCargo(plan.Item, out var now, detachedRoot) && SameCargo(plan.Cargo, now);

        // Result cards share checks that depend only on their inventory path.
        // This snapshot is never used for planning or committing a transfer.
        internal sealed class SourceSafetySnapshot
        {
            private readonly bool mutationsSafe = InventoryAutoInsert.KnownMutationPatches();
            private readonly Dictionary<GameInventory, bool> visuals = new Dictionary<GameInventory, bool>();
            private readonly Dictionary<(GameInventory, bool), bool> inventories = new Dictionary<(GameInventory, bool), bool>();

            internal bool Allows(GameItem item)
            {
                if (item == null || !mutationsSafe || !InventoryAutoInsert.SafeItem(item, true, true) || item.parentInventory == null)
                    return false;
                var key = (item.parentInventory, SettledCounterEvidence.Allows(item));
                if (!inventories.TryGetValue(key, out bool safe))
                {
                    safe = InventoryAutoInsert.SafeTransferInventoryWithSnapshot(item.parentInventory, item, visuals);
                    inventories[key] = safe;
                }
                return safe;
            }
        }

        internal static bool SafeTarget(GameInventory inventory) => InventoryAutoInsert.SafeTransferInventory(inventory);

        private static Plan Create(GameItem item, GameInventory target, GameItem stack, GridShape after, int beforeCount,
            int amount = -1, int sourceBefore = -1)
        {
            string sourceState = State(item), stackState = stack == null ? null : State(stack);
            if (sourceState == null || stack != null && stackState == null || item.modifiedShape == null || after == null) return null;
            if (sourceBefore < 0) sourceBefore = item.unitCount;
            if (amount < 0) amount = sourceBefore;
            if (amount <= 0 || sourceBefore < amount || sourceBefore > item.unitCount || stack == null && amount != sourceBefore) return null;
            List<ContainerPart> cargo = null;
            if (item.contentWindow != null && (stack != null || amount != 1 || sourceBefore != 1 || !CaptureCargo(item, out cargo))) return null;
            return new Plan { Item = item, From = item.parentInventory, To = target, Stack = stack, Amount = amount, SourceBefore = sourceBefore,
                Before = item.modifiedShape.Clone(), After = after.Clone(), StackBefore = beforeCount,
                SourceState = sourceState, StackState = stackState, Cargo = cargo };
        }

        internal static Plan Stack(GameItem item, GameInventory target, Reservations reserved)
        {
            if (item == null || target == null || reserved == null || !SafeSource(item) || !SafeTarget(target) ||
                target == item.parentInventory || target.IsInsertLocked() ||
                !GeneralHelper.MayPlayerInsertInto(target) || !ShopInventory.Movable(item) || item.MaxNumRemove() < item.unitCount ||
                ShopInventory.WouldContainSelf(item, target)) return null;
            foreach (GameItem resident in target.childItems)
            {
                if (resident == null || reserved.ExcludedStacks.Contains(resident) || resident.parentInventory != target ||
                    !ShopInventory.Owned(resident) || !Compatible(item, resident)) continue;
                var marker = resident.GetStackSlot(item);
                int already = reserved.Counts.TryGetValue(resident, out int count) ? count : 0;
                if (marker?.inventory != target || marker.targetItem != resident || marker.item != item ||
                    (long)marker.numTransfer - already < item.unitCount ||
                    (long)resident.unitCount + already + item.unitCount > int.MaxValue) continue;
                Plan plan = Create(item, target, resident, resident.modifiedShape, resident.unitCount + already);
                if (plan == null) continue;
                reserved.Counts[resident] = already + item.unitCount;
                return plan;
            }
            return null;
        }

        internal static Plan StackUpTo(GameItem item, GameInventory target, Reservations reserved, int sourceBefore)
        {
            if (item == null || target == null || reserved == null || sourceBefore <= 0 || sourceBefore > item.unitCount ||
                !SafeSource(item) || !SafeTarget(target) || target == item.parentInventory || target.IsInsertLocked() ||
                !GeneralHelper.MayPlayerInsertInto(target) || !ShopInventory.Movable(item) || item.MaxNumRemove() < item.unitCount ||
                ShopInventory.WouldContainSelf(item, target)) return null;
            foreach (GameItem resident in target.childItems)
            {
                if (resident == null || reserved.ExcludedStacks.Contains(resident) || resident.parentInventory != target ||
                    !ShopInventory.Owned(resident) || !Compatible(item, resident)) continue;
                SlotMarker marker = resident.GetStackSlot(item);
                int already = reserved.Counts.TryGetValue(resident, out int count) ? count : 0;
                if (marker?.inventory != target || marker.targetItem != resident || marker.item != item) continue;
                long available = Math.Min((long)marker.numTransfer - already, (long)int.MaxValue - resident.unitCount - already);
                int amount = (int)Math.Min(sourceBefore, Math.Max(0L, available));
                if (amount == 0) continue;
                Plan plan = Create(item, target, resident, resident.modifiedShape, resident.unitCount + already, amount, sourceBefore);
                if (plan == null) continue;
                reserved.Counts[resident] = already + amount;
                return plan;
            }
            return null;
        }

        // The pointer identifies the resident; never substitute a different stack.
        internal static Plan ExactStack(GameItem item, GameInventory target, GameItem resident)
        {
            if (item == null || target == null || resident == null || ReferenceEquals(item, resident) ||
                !SafeSource(item) || !SafeTarget(target) || target.IsInsertLocked() ||
                !GeneralHelper.MayPlayerInsertInto(target) || !ShopInventory.Movable(item) ||
                item.MaxNumRemove() < item.unitCount || ShopInventory.WouldContainSelf(item, target) ||
                resident.parentInventory != target || !target.childItems.Contains(resident) ||
                !ShopInventory.Owned(resident) || !Compatible(item, resident) ||
                (long)resident.unitCount + item.unitCount > int.MaxValue) return null;
            var marker = resident.GetStackSlot(item);
            if (marker?.inventory != target || marker.targetItem != resident || marker.item != item ||
                marker.numTransfer < item.unitCount) return null;
            return Create(item, target, resident, resident.modifiedShape, resident.unitCount);
        }

        internal static Plan ExactEmpty(GameItem item, GameGridInventory target, GridShape after)
        {
            if (item == null || target == null || after == null || !SafeSource(item) || !SafeTarget(target) ||
                !ShopInventory.Movable(item) || item.MaxNumRemove() < item.unitCount ||
                ShopInventory.WouldContainSelf(item, target)) return null;
            var marker = GridPlacements.Exact(target, item, after);
            return marker?.inventory == target && marker.item == item && marker.targetItem == null &&
                marker.numTransfer >= item.unitCount && InventorySortPlan.SameShape(marker.itemGridShape, after)
                ? Create(item, target, null, marker.itemGridShape, 0) : null;
        }

        internal static Plan Empty(GameItem item, GameGridInventory target, Reservations reserved)
            => EmptyRemainder(item, target, reserved, item?.unitCount ?? 0);

        internal static Plan EmptyRemainder(GameItem item, GameGridInventory target, Reservations reserved, int sourceBefore)
        {
            if (item == null || target == null || reserved == null || sourceBefore <= 0 || sourceBefore > item.unitCount ||
                !SafeSource(item) || !SafeTarget(target) || target == item.parentInventory ||
                !ShopInventory.Movable(item) || item.MaxNumRemove() < item.unitCount ||
                ShopInventory.WouldContainSelf(item, target)) return null;
            if (!reserved.Shapes.TryGetValue(target, out var shapes)) reserved.Shapes[target] = shapes = new List<GridShape>();
            var marker = GridPlacements.Empty(target, item, shapes, sourceBefore);
            if (marker?.inventory != target || marker.item != item || marker.targetItem != null || marker.numTransfer < sourceBefore) return null;
            Plan plan = Create(item, target, null, marker.itemGridShape, 0, sourceBefore, sourceBefore);
            if (plan != null) shapes.Add(plan.After);
            return plan;
        }

        internal static Plan EmptySlot(GameItem item, GameInventory target)
        {
            if (item == null || target == null || !SafeSource(item) || !SafeTarget(target) || target == item.parentInventory || target.IsInsertLocked() ||
                !GeneralHelper.MayPlayerInsertInto(target) || !ShopInventory.Movable(item) ||
                item.MaxNumRemove() < item.unitCount || ShopInventory.WouldContainSelf(item, target)) return null;
            var marker = target.TryFindOneValidInventorySlot(item);
            return marker?.inventory == target && marker.targetItem == null && marker.numTransfer >= item.unitCount
                ? Create(item, target, null, marker.itemGridShape, 0) : null;
        }

        internal static bool Same(Plan a, Plan b) => a != null && b != null && ReferenceEquals(a.Item, b.Item) &&
            ReferenceEquals(a.From, b.From) && ReferenceEquals(a.To, b.To) && ReferenceEquals(a.Stack, b.Stack) &&
            a.Amount == b.Amount && a.SourceBefore == b.SourceBefore && a.StackBefore == b.StackBefore && a.SourceState == b.SourceState && a.StackState == b.StackState &&
            InventorySortPlan.SameShape(a.Before, b.Before) && InventorySortPlan.SameShape(a.After, b.After) && SameCargo(a.Cargo, b.Cargo);

        internal static Result Apply(Plan plan, string action, string location, string sourceKey, string targetKey, Func<bool> scope, Func<bool> recordCompletion = null)
        {
            try { return ApplyCore(plan, action, location, sourceKey, targetKey, scope, recordCompletion, null); }
            catch (Exception error)
            {
                Debug.LogWarning("[ShopAssist] Whole-stack transfer failed: " + error);
                return Ready ? Result.Unchanged : Result.Pending;
            }
        }

        internal static Result ApplyFieldReplacement(FieldReplacement replacement, Plan plan, Func<bool> scope)
        {
            if (!ReplacementAllows(replacement, plan)) return Result.Unavailable;
            if (ReferenceEquals(plan.Item, replacement.Incoming)) replacement.Take = plan;
            try { return ApplyCore(plan, "f36.open", null, null, null, scope, null, replacement); }
            catch (Exception error)
            {
                Debug.LogWarning("[ShopAssist] Field replacement transfer failed: " + error);
                return Result.Pending;
            }
        }

        private static Result ApplyCore(Plan plan, string action, string location, string sourceKey, string targetKey, Func<bool> scope,
            Func<bool> recordCompletion, FieldReplacement replacement)
        {
            if (plan == null || scope == null || !scope() || (replacement == null ? !Ready : !ReplacementAllows(replacement, plan)) ||
                plan.Amount <= 0 || plan.SourceBefore < plan.Amount ||
                plan.SourceBefore != plan.Amount && (action != "f08.open" || !plan.IsStack)) return Result.Unavailable;
            var item = plan.Item; var target = plan.Stack;
            if (item == null || !SafeSource(item) || !SafeTarget(plan.To) || item.parentInventory != plan.From || !plan.From.childItems.Contains(item) ||
                item.unitCount != plan.SourceBefore || !ShopInventory.Movable(item) ||
                item.MaxNumRemove() < plan.Amount || plan.To.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(plan.To) ||
                !InventorySortPlan.SameShape(item.modifiedShape, plan.Before) || State(item) != plan.SourceState ||
                ShopInventory.WouldContainSelf(item, plan.To) || !CargoUnchanged(plan)) return Result.Unchanged;
            SlotMarker marker;
            if (target != null)
            {
                if (target.parentInventory != plan.To || !plan.To.childItems.Contains(target) || target.unitCount != plan.StackBefore ||
                    !InventorySortPlan.SameShape(target.modifiedShape, plan.After) || State(target) != plan.StackState || !Compatible(item, target))
                    return Result.Unchanged;
                marker = target.GetStackSlot(item);
                if (marker?.inventory != plan.To || marker.targetItem != target || marker.item != item || marker.numTransfer < plan.Amount)
                    return Result.Unchanged;
            }
            else
            {
                marker = plan.To.TryInventorySlot(item, plan.Amount, plan.After);
                if (marker?.inventory != plan.To || marker.item != item || marker.targetItem != null || marker.numTransfer < plan.Amount ||
                    !InventorySortPlan.SameShape(marker.itemGridShape, plan.After)) return Result.Unchanged;
            }
            if (replacement == null && !Begin(plan, action, location, sourceKey, targetKey)) return Result.Unavailable;
            int accepted = 0;
            bool threw = false;
            try { accepted = marker.TryAcceptOnce(plan.Amount); }
            catch (Exception error) { threw = true; Debug.LogWarning("[ShopAssist] Whole-stack transfer interrupted: " + error); }
            bool applied = !threw && accepted == plan.Amount && scope() && CargoUnchanged(plan) && (target == null ?
                ReferenceEquals(item.parentInventory, plan.To) && item.unitCount == plan.Amount && plan.To.childItems.Contains(item) &&
                    (ReferenceEquals(plan.From, plan.To) || !plan.From.childItems.Contains(item)) &&
                    InventorySortPlan.SameShape(item.modifiedShape, plan.After) && State(item) == plan.SourceState :
                (plan.SourceBefore == plan.Amount ?
                    item.unitCount == 0 && item.parentInventory == null && !plan.From.childItems.Contains(item) :
                    item.unitCount == plan.SourceBefore - plan.Amount && item.parentInventory == plan.From && plan.From.childItems.Contains(item) &&
                    InventorySortPlan.SameShape(item.modifiedShape, plan.Before) && State(item) == plan.SourceState) &&
                    target.parentInventory == plan.To && plan.To.childItems.Contains(target) &&
                    target.unitCount == plan.StackBefore + plan.Amount && InventorySortPlan.SameShape(target.modifiedShape, plan.After) &&
                    State(target) == plan.StackState);
            if (applied)
            {
                if (target != null && plan.SourceBefore == plan.Amount)
                {
                    try { item.Destroy(); }
                    catch (Exception error) { Debug.LogWarning("[ShopAssist] Consumed stack cleanup failed: " + error); return Result.Pending; }
                }
                try { if (recordCompletion != null && !recordCompletion()) return Result.Pending; }
                catch (Exception error) { Debug.LogWarning("[ShopAssist] Whole-stack completion record failed: " + error); return Result.Pending; }
                return replacement != null || Finish("done") ? Result.Applied : Result.Pending;
            }
            bool unchanged = CargoUnchanged(plan) && item.parentInventory == plan.From && plan.From.childItems.Contains(item) && item.unitCount == plan.SourceBefore &&
                InventorySortPlan.SameShape(item.modifiedShape, plan.Before) && State(item) == plan.SourceState &&
                (target == null ? ReferenceEquals(plan.From, plan.To) || !plan.To.childItems.Contains(item) : target.parentInventory == plan.To &&
                    plan.To.childItems.Contains(target) && target.unitCount == plan.StackBefore && State(target) == plan.StackState);
            if (unchanged && (replacement != null || Finish("done"))) return Result.Unchanged;
            AttentionBook.Changed();
            return Result.Pending;
        }
    }
}
