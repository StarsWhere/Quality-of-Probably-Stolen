using System;
using System.Collections.Generic;
using System.Linq;
using ShopAssist;

internal static class RoutingPlanTests
{
    private static int assertions;
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static GameGridInventory Grid(string key, int capacity, GameItem owner = null)
    {
        var grid = new GameGridInventory { capacity = capacity, owner = owner };
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = grid, id = key, Name = key });
        return grid;
    }
    private static GameItemElement Add(GameGridInventory grid, string id, int x)
    {
        var item = new GameItemElement { identifier = id, parentInventory = grid, modifiedShape = new Shape { minX = x } };
        item.types.Add("FOOD"); grid.childItems.Add(item); return item;
    }
    private static RoutingRule Rule(string key, int priority, bool exclusive = false)
    {
        var rule = new RoutingRule { location = key, priority = priority, receive = true, exclusive = exclusive };
        rule.categories.Add("FOOD"); return rule;
    }
    private static bool CanStack(GameItem first, GameItem second) => first.identifier == second.identifier;
    private static void TransferTests()
    {
        ShopInventory.Locations.Clear(); ModHelper.Data.Clear();
        WholeStackTransfer.Acquire();
        var from = Grid("source", 5); var to = Grid("target", 2);
        var first = Add(from, "fuel", 0); first.unitCount = 3; first.mayStackItemFunc = CanStack;
        var second = Add(from, "fuel", 1); second.unitCount = 4; second.mayStackItemFunc = CanStack;
        var resident = Add(to, "fuel", 0); resident.unitCount = 6; resident.maxStack = 12; resident.mayStackItemFunc = CanStack;
        var reserved = new WholeStackTransfer.Reservations();
        var one = WholeStackTransfer.Stack(first, to, reserved);
        Check(one != null && one.StackBefore == 6, "a compatible source fits entirely into one existing stack");
        Check(WholeStackTransfer.Stack(second, to, reserved) == null, "a preview reserves target capacity for earlier moves");
        var empty = WholeStackTransfer.Empty(second, to, reserved);
        Check(empty != null && !empty.IsStack, "a stack that cannot fit as a whole uses an empty location");
        Check(first.unitCount == 3 && second.unitCount == 4 && resident.unitCount == 6, "preview never changes quantities");
        var result = WholeStackTransfer.Apply(one, "f08.open", "source → target", "root:source", "root:target", () => true);
        Check(result == WholeStackTransfer.Result.Applied && first.destroyed && first.parentInventory == null &&
            resident.unitCount == 9 && second.unitCount == 4 && WholeStackTransfer.Ready,
            "a confirmed merge consumes exactly one source and conserves total units");
        result = WholeStackTransfer.Apply(empty, "f08.open", "source → target", "root:source", "root:target", () => true);
        Check(result == WholeStackTransfer.Result.Applied && second.parentInventory == to && second.unitCount == 4 && resident.unitCount == 9,
            "empty-slot fallback moves the complete source without splitting it");

        var sharedSource = Grid("shared source", 2); var sharedTarget = Grid("shared target", 1);
        var sharedOne = Add(sharedSource, "coin", 0); sharedOne.unitCount = 2; sharedOne.mayStackItemFunc = CanStack;
        var sharedTwo = Add(sharedSource, "coin", 1); sharedTwo.unitCount = 3; sharedTwo.mayStackItemFunc = CanStack;
        var sharedResident = Add(sharedTarget, "coin", 0); sharedResident.unitCount = 4; sharedResident.maxStack = 9; sharedResident.mayStackItemFunc = CanStack;
        var sharedReservations = new WholeStackTransfer.Reservations();
        var sharedPlanOne = WholeStackTransfer.Stack(sharedOne, sharedTarget, sharedReservations);
        var sharedPlanTwo = WholeStackTransfer.Stack(sharedTwo, sharedTarget, sharedReservations);
        Check(sharedPlanOne != null && sharedPlanTwo != null && sharedPlanTwo.StackBefore == 6,
            "two sources can reserve a single target's exact remaining capacity");
        Check(WholeStackTransfer.Apply(sharedPlanOne, "f08.open", "source → target", "root:source", "root:target", () => true) == WholeStackTransfer.Result.Applied &&
            WholeStackTransfer.Apply(sharedPlanTwo, "f08.open", "source → target", "root:source", "root:target", () => true) == WholeStackTransfer.Result.Applied &&
            sharedResident.unitCount == 9 && sharedOne.destroyed && sharedTwo.destroyed,
            "successive reserved merges preserve all units and respect the projected target count");

        var pointed = Grid("pointed", 4);
        var pointedSource = Add(pointed, "coin", 0); pointedSource.unitCount = 3; pointedSource.mayStackItemFunc = CanStack;
        var pointedFull = Add(pointed, "coin", 1); pointedFull.unitCount = 9; pointedFull.maxStack = 10; pointedFull.mayStackItemFunc = CanStack;
        var pointedOther = Add(pointed, "coin", 2); pointedOther.unitCount = 4; pointedOther.maxStack = 10; pointedOther.mayStackItemFunc = CanStack;
        Check(WholeStackTransfer.ExactStack(pointedSource, pointed, pointedFull) == null &&
            pointedSource.unitCount == 3 && pointedFull.unitCount == 9 && pointedOther.unitCount == 4,
            "a pointed stack with insufficient room does not redirect to another compatible stack or partially merge");
        var pointedPlan = WholeStackTransfer.ExactStack(pointedSource, pointed, pointedOther);
        Check(pointedPlan != null && ReferenceEquals(pointedPlan.Stack, pointedOther), "the plan retains the exact pointed stack within the same inventory");
        pointedOther.reserved = true;
        Check(WholeStackTransfer.Apply(pointedPlan, "f11.open", "stock", "root:stock", "root:stock", () => true) == WholeStackTransfer.Result.Unchanged &&
            pointedSource.unitCount == 3 && pointedOther.unitCount == 4, "a newly reserved resident invalidates the plan without mutation");
        pointedOther.reserved = false;
        pointedPlan = WholeStackTransfer.ExactStack(pointedSource, pointed, pointedOther);
        Check(WholeStackTransfer.Apply(pointedPlan, "f11.open", "stock", "root:stock", "root:stock", () => true) == WholeStackTransfer.Result.Applied &&
            pointedSource.destroyed && pointedSource.parentInventory == null && !pointed.childItems.Contains(pointedSource) &&
            pointedOther.unitCount == 7 && pointedFull.unitCount == 9 && pointed.childItems.Sum(item => item.unitCount) == 16,
            "same-inventory merge consumes the selected source exactly once and conserves all units");
        var relocate = WholeStackTransfer.ExactEmpty(pointedOther, pointed, new Shape { minX = 3 });
        Check(relocate != null && WholeStackTransfer.Apply(relocate, "f11.open", "stock", "root:stock", "root:stock", () => true) == WholeStackTransfer.Result.Applied &&
            pointedOther.parentInventory == pointed && pointedOther.modifiedShape.minX == 3 && pointedOther.unitCount == 7,
            "same-inventory empty placement retains the same instance and quantity");
        var interruptedSame = Add(pointed, "coin", 0); interruptedSame.unitCount = 2; interruptedSame.mayStackItemFunc = CanStack;
        var interruptedPlan = WholeStackTransfer.ExactStack(interruptedSame, pointed, pointedOther);
        SlotMarker.Failure = "after_target";
        result = WholeStackTransfer.Apply(interruptedPlan, "f11.open", "stock", "root:stock", "root:stock", () => true);
        SlotMarker.Failure = null;
        Check(result == WholeStackTransfer.Result.Pending && !WholeStackTransfer.Ready && interruptedSame.unitCount == 2 && pointedOther.unitCount == 9,
            "an interrupted same-inventory merge records the uncertain source and target instead of reporting success");
        WholeStackTransfer.Release(); WholeStackTransfer.Acquire();
        var f11Pending = AttentionBook.Providers["whole_stack"]().ToList();
        Check(!WholeStackTransfer.Ready && f11Pending.Count == 2 && f11Pending.All(row => row.Feature == "f11.open"),
            "the F11 source and target remain inspectable and block all further transfers after reload");
        f11Pending[0].Resolve();
        Check(WholeStackTransfer.Ready, "inspection explicitly releases an interrupted F11 transfer");

        var different = Add(from, "fuel", 2); different.unitCount = 2; different.mayStackItemFunc = CanStack; different.intrinsic = "other state";
        Check(WholeStackTransfer.Stack(different, to, new WholeStackTransfer.Reservations()) == null,
            "different instance state cannot be merged despite matching names and native predicates");
        var stale = WholeStackTransfer.Empty(different, Grid("fresh", 1), new WholeStackTransfer.Reservations());
        different.intrinsic = "changed";
        Check(WholeStackTransfer.Apply(stale, "f08.open", "source → fresh", "root:source", "root:fresh", () => true) == WholeStackTransfer.Result.Unchanged &&
            different.parentInventory == from, "a state change after preview prevents the move");
        different.intrinsic = "other state";
        from.onSlotRemoveItemEndFunc = (Action)(() => throw new Exception("unsafe callback must not run"));
        Check(WholeStackTransfer.Empty(different, Grid("unsafe", 1), new WholeStackTransfer.Reservations()) == null,
            "an unknown remove callback rejects the operation before mutation or reentrant save");
        from.onSlotRemoveItemEndFunc = null;
        var unsafeTarget = Grid("unsafe target", 1);
        bool called = false; unsafeTarget.onSlotAddItemFunc = (Action)(() => called = true);
        Check(WholeStackTransfer.Empty(different, unsafeTarget, new WholeStackTransfer.Reservations()) == null && !called,
            "an unknown add callback is never invoked by preview or mutation");

        ModHelper.Data.Clear();
        var interrupted = Add(from, "water", 3); interrupted.unitCount = 5; interrupted.mayStackItemFunc = CanStack;
        var fieldTarget = Grid("field bag", 1);
        var fieldStack = Add(fieldTarget, "water", 0); fieldStack.unitCount = 7; fieldStack.mayStackItemFunc = CanStack;
        var fieldPlan = WholeStackTransfer.Stack(interrupted, fieldTarget, new WholeStackTransfer.Reservations());
        SlotMarker.Failure = "after_target";
        result = WholeStackTransfer.Apply(fieldPlan, "f36.open", "ground → backpack", "field:ground", "field:bag:7", () => true);
        SlotMarker.Failure = null;
        var detail = WholeStackTransfer.FieldPending();
        Check(result == WholeStackTransfer.Result.Pending && !WholeStackTransfer.Ready && detail.Contains("ground → backpack") &&
            detail.Contains("5") && detail.Contains("7"), "partial exceptions retain item, location, source and target counts for field inspection");
        WholeStackTransfer.Release(); WholeStackTransfer.Acquire();
        Check(!WholeStackTransfer.Ready && WholeStackTransfer.FieldPending() != null, "pending field journal survives reload from mod data");
        Check(WholeStackTransfer.ResolveField() && WholeStackTransfer.Ready, "field inspection explicitly resolves its own journal");

        ModHelper.Data.Clear();
        var tripSource = Grid("trip source", 1); var tripTarget = Grid("trip target", 1);
        var tripItem = Add(tripSource, "parts", 0); tripItem.unitCount = 2; tripItem.mayStackItemFunc = CanStack;
        var tripResident = Add(tripTarget, "parts", 0); tripResident.unitCount = 1; tripResident.mayStackItemFunc = CanStack;
        var tripPlan = WholeStackTransfer.Stack(tripItem, tripTarget, new WholeStackTransfer.Reservations());
        var gains = new HashSet<int>(); gains.Add(tripItem.GetUniqueID());
        ModHelper.WritesBeforeReadFailure = 2;
        result = WholeStackTransfer.Apply(tripPlan, "f25.open", "bag → stock", "item:bag", "root:stock", () => true, () =>
        {
            CheckedModData.Write("tripCargo", "remaining gains");
            gains.Remove(tripItem.uniqueId);
            return true;
        });
        var pending = AttentionBook.Providers["whole_stack"]().ToList();
        Check(result == WholeStackTransfer.Result.Pending && !WholeStackTransfer.Ready && gains.Contains(tripItem.uniqueId),
            "failed cargo-record readback leaves the transfer pending and prevents repetition");
        Check(pending.Count == 2 && pending.All(row => row.ResolveLabelKey == "f25.resolve_pending"),
            "both source and target cargo inspection buttons explicitly explain gain removal");
        WholeStackTransfer.ResolveTrip = id => false;
        pending[0].Resolve();
        Check(!WholeStackTransfer.Ready && gains.Contains(tripItem.uniqueId), "a failed gain update cannot resolve the journal");
        WholeStackTransfer.ResolveTrip = id => gains.Remove(id);
        pending[0].Resolve();
        Check(WholeStackTransfer.Ready && !gains.Contains(tripItem.uniqueId), "only a confirmed gain update releases the pending transfer");

        ModHelper.Data.Clear();
        var writeSource = Grid("write source", 1); var writeTarget = Grid("write target", 1);
        var writeItem = Add(writeSource, "screw", 0);
        var writePlan = WholeStackTransfer.Empty(writeItem, writeTarget, new WholeStackTransfer.Reservations());
        ModHelper.FailNextWrite = true;
        Check(WholeStackTransfer.Apply(writePlan, "f08.open", "source → target", "root:source", "root:target", () => true) == WholeStackTransfer.Result.Unavailable &&
            writeItem.parentInventory == writeSource, "a failed prewrite never calls native movement");
        ModHelper.WritesBeforeReadFailure = 2;
        result = WholeStackTransfer.Apply(writePlan, "f08.open", "source → target", "root:source", "root:target", () => true);
        Check(result == WholeStackTransfer.Result.Pending && !WholeStackTransfer.Ready &&
            AttentionBook.Providers["whole_stack"]().Any(), "failed terminal readback remains visible and blocked even when the attempted write succeeded");
        WholeStackTransfer.Release();
        var unconfirmed = (System.Collections.IDictionary)typeof(WholeStackTransfer)
            .GetField("uncertain", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        unconfirmed.Clear(); // Simulate a fresh process; only the mod-data copy remains.
        WholeStackTransfer.Acquire();
        Check(!WholeStackTransfer.Ready && AttentionBook.Providers["whole_stack"]().Any(), "restored pending prewrite remains visible after reload");
        AttentionBook.Providers["whole_stack"]().First().Resolve();
        Check(WholeStackTransfer.Ready, "explicit inspection clears the uncertain terminal write");

        ModHelper.Data["wholeStackTransfer"] = "null";
        WholeStackTransfer.Release(); WholeStackTransfer.Acquire();
        Check(!WholeStackTransfer.Ready && AttentionBook.Providers["whole_stack"]().Single().Key == "unreadable",
            "a present JSON null is corrupt rather than an absent journal");
        ModHelper.Data.Clear(); WholeStackTransfer.Release(); WholeStackTransfer.Acquire();
        ModHelper.FailNextRead = true;
        Check(!WholeStackTransfer.Ready && WholeStackTransfer.Ready,
            "an absent journal becomes available after a transient read failure is retried");
        WholeStackTransfer.ResolveTrip = null; WholeStackTransfer.Release(); ModHelper.Data.Clear();
    }

    private static void UnreadableJournalTests()
    {
        const string journalKey = "wholeStackTransfer", backupPrefix = "wholeStackTransfer.unreadable.";
        ModHelper.Data.Clear(); ShopInventory.InStore = true; ShopNotice.Last = null;
        PlayerStore.Instance.runID = "run"; PlayerStore.Instance.saveSlotId = 1;
        WholeStackTransfer.Acquire();
        var prompt = GenericConfirmationUIManager.Instance;
        ModHelper.Data["otherAttention"] = "keep this pending item";

        var source = Grid("unreadable source", 1); var target = Grid("unreadable target", 1);
        var item = Add(source, "water", 0); item.unitCount = 2; item.mayStackItemFunc = CanStack;
        var resident = Add(target, "water", 0); resident.unitCount = 1; resident.mayStackItemFunc = CanStack;
        var plan = WholeStackTransfer.ExactStack(item, target, resident);
        SlotMarker.Failure = "after_target";
        Check(WholeStackTransfer.Apply(plan, "f11.open", "source → target", "root:source", "root:target", () => true) == WholeStackTransfer.Result.Pending,
            "a genuine pending journal is available to test stale reset confirmation");
        SlotMarker.Failure = null;
        string existingPending = ModHelper.Data[journalKey];
        PlayerStore.Instance.runID = "different save";
        var wrongScope = AttentionBook.Providers["whole_stack"]().Single();
        Check(!WholeStackTransfer.Ready && wrongScope.Retry != null && wrongScope.Resolve == null,
            "a readable pending journal from another save cannot be reset as corruption");
        PlayerStore.Instance.runID = "run";
        AttentionBook.Providers["whole_stack"]().First().Resolve();
        Check(WholeStackTransfer.Ready, "the genuine pending fixture was explicitly resolved");

        object originalJournal = typeof(WholeStackTransfer).GetField("journal", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        object unsupportedJournal = Activator.CreateInstance(originalJournal.GetType(), true);
        foreach (var field in originalJournal.GetType().GetFields()) field.SetValue(unsupportedJournal, field.GetValue(originalJournal));
        unsupportedJournal.GetType().GetField("version").SetValue(unsupportedJournal, 2);
        ModHelper.Data[journalKey] = ShopData.Encode(unsupportedJournal);
        var unsupported = AttentionBook.Providers["whole_stack"]().Single();
        Check(!WholeStackTransfer.Ready && unsupported.Resolve != null && unsupported.ReasonKey == "f08.journal_unreadable",
            "an unsupported version belonging to the current save can be reset after inspection without migration");
        unsupported.Resolve(); prompt.Cancel();
        ModHelper.Data[journalKey] = "{\"version\":2,\"run\":\"another run\",\"slot\":1}";
        var foreignIncomplete = AttentionBook.Providers["whole_stack"]().Single();
        Check(!WholeStackTransfer.Ready && foreignIncomplete.Resolve == null,
            "an incomplete unsupported record that explicitly names another run cannot be reset");
        ModHelper.Data[journalKey] = "{\"version\":2,\"run\":\"run\",\"slot\":9}";
        Check(AttentionBook.Providers["whole_stack"]().Single().Resolve == null,
            "an incomplete unsupported record that explicitly names another slot cannot be reset");

        ModHelper.Data.Remove(journalKey); WholeStackTransfer.Release(); WholeStackTransfer.Acquire();
        ModHelper.FailNextRead = true;
        var transient = AttentionBook.Providers["whole_stack"]().Single();
        Check(transient.ReasonKey == "f08.journal_read_failed" && transient.Retry != null && transient.Resolve == null,
            "a temporary read failure offers only retry, never a destructive reset");
        Check(WholeStackTransfer.Ready, "a transient read failure can recover normally");

        ModHelper.Data[journalKey] = "null";
        void OpenReset()
        {
            var entry = AttentionBook.Providers["whole_stack"]().Single();
            Check(entry.Key == "unreadable" && entry.ReasonKey == "f08.journal_unreadable" && entry.Retry != null && entry.Resolve != null &&
                entry.ResolveLabelKey == "f08.journal_reset_action", "only confirmed unreadable content exposes reset alongside retry");
            entry.Resolve();
            Check(prompt.Visible && prompt.Text == "f08.journal_reset_warning" && prompt.ConfirmText == "f08.journal_reset_confirm" &&
                prompt.CancelText == "f08.journal_reset_cancel", "reset requires an explicit inspection confirmation");
        }

        OpenReset(); prompt.Cancel();
        Check(!prompt.Visible && ModHelper.Data[journalKey] == "null" && !WholeStackTransfer.Ready &&
            !ModHelper.Data.Keys.Any(key => key.StartsWith(backupPrefix)), "cancelling leaves the original intact and transfers blocked");

        OpenReset();
        PlayerStore.Instance.runID = "another run";
        prompt.Confirm();
        Check(ModHelper.Data[journalKey] == "null" && ShopNotice.Last == "inv.changed" && !prompt.Visible,
            "confirmation cannot reset a different save");
        PlayerStore.Instance.runID = "run";
        OpenReset(); PlayerStore.Instance.saveSlotId = 2; prompt.Confirm();
        Check(ModHelper.Data[journalKey] == "null" && ShopNotice.Last == "inv.changed", "confirmation also rechecks the save slot");
        PlayerStore.Instance.saveSlotId = 1;

        OpenReset(); ModHelper.Data[journalKey] = existingPending; prompt.Confirm();
        Check(ModHelper.Data[journalKey] == existingPending && !WholeStackTransfer.Ready && ShopNotice.Last == "inv.changed" &&
            AttentionBook.Providers["whole_stack"]().All(row => row.ReasonKey == "f08.pending"),
            "a changed valid pending transfer cannot be replaced by a stale reset confirmation");
        AttentionBook.Providers["whole_stack"]().First().Resolve();
        Check(WholeStackTransfer.Ready, "the replacement pending transfer remains independently resolvable");

        ModHelper.Data[journalKey] = "null"; OpenReset();
        ModHelper.FailNextWrite = true; ShopNotice.Last = null; prompt.Confirm();
        Check(ModHelper.Data[journalKey] == "null" && !WholeStackTransfer.Ready && ShopNotice.Last == "f08.journal_failed",
            "a failed backup write keeps the raw journal and blocks transfers");

        OpenReset(); ModHelper.WritesBeforeReadFailure = 1; ShopNotice.Last = null; prompt.Confirm();
        Check(ModHelper.Data[journalKey] == "null" && !WholeStackTransfer.Ready && ShopNotice.Last == "f08.journal_failed",
            "a failed backup readback never resets the original journal");

        OpenReset(); ModHelper.WritesBeforeReadFailure = 2; ShopNotice.Last = null; prompt.Confirm();
        Check(ModHelper.Data[journalKey] == "null" && !WholeStackTransfer.Ready && ShopNotice.Last == "f08.journal_failed",
            "a failed prewritten pending-marker readback does not reset the original journal");

        OpenReset(); ModHelper.WritesBeforeReadFailure = 3; ShopNotice.Last = null; prompt.Confirm();
        Check(ModHelper.Data[journalKey] == "null" && !WholeStackTransfer.Ready && ShopNotice.Last == "f08.journal_failed" &&
            ModHelper.Data.Any(pair => pair.Key.StartsWith(backupPrefix) && pair.Value == "null"),
            "a failed reset readback retains the backup and restores the original while blocking transfers");
        WholeStackTransfer.Release(); WholeStackTransfer.Acquire();
        Check(!WholeStackTransfer.Ready && AttentionBook.Providers["whole_stack"]().Single().Resolve != null,
            "the uncertain reset stays blocked and recoverable after the transfer owner restarts");

        ModHelper.Data[journalKey] = existingPending;
        Check(!WholeStackTransfer.Ready && AttentionBook.Providers["whole_stack"]().All(row => row.ReasonKey == "f08.pending"),
            "a different genuine pending journal remains visible instead of being hidden by an uncertain reset");
        AttentionBook.Providers["whole_stack"]().First().Resolve();
        Check(WholeStackTransfer.Ready, "the newer pending journal is resolved separately without erasing it");
        ModHelper.Data[journalKey] = "null";

        OpenReset(); ModHelper.WritesBeforeReadFailure = 3;
        ModHelper.AfterWrite = (key, value) => { if (key == journalKey) { ModHelper.AfterWrite = null; ModHelper.FailWriteKey = journalKey; } };
        ShopNotice.Last = null; prompt.Confirm();
        Check(ModHelper.Data[journalKey] != "null" && !WholeStackTransfer.Ready && ShopNotice.Last == "f08.journal_failed",
            "a reset whose readback and restoration both fail remains blocked even if the attempted marker was stored");
        WholeStackTransfer.Release();
        var unconfirmedReset = (System.Collections.IDictionary)typeof(WholeStackTransfer)
            .GetField("uncertainResets", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        unconfirmedReset.Clear(); // Simulate a fresh process; only mod-data markers remain.
        WholeStackTransfer.Acquire();
        Check(!WholeStackTransfer.Ready && AttentionBook.Providers["whole_stack"]().Single().Resolve != null,
            "a preserved unconfirmed reset remains blocked and recoverable after a fresh process");

        OpenReset(); ShopNotice.Last = null; prompt.Confirm();
        Check(WholeStackTransfer.Ready && ShopNotice.Last == "f08.journal_reset_done" &&
            ModHelper.Data.Any(pair => pair.Key.StartsWith(backupPrefix) && pair.Value == "null") &&
            ModHelper.Data["otherAttention"] == "keep this pending item", "confirmed reset preserves the original and unrelated pending data");
        WholeStackTransfer.Release(); WholeStackTransfer.Acquire();
        Check(WholeStackTransfer.Ready, "a checked reset and its preserved original survive a normal reload");
        object journal = typeof(WholeStackTransfer).GetField("journal", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        string backupKey = (string)journal.GetType().GetField("sourceKey").GetValue(journal);
        ModHelper.Data.Remove(backupKey);
        Check(!WholeStackTransfer.Ready && AttentionBook.Providers["whole_stack"]().Single().Resolve == null,
            "a missing original backup fails closed without offering an unrelated reset");
        ModHelper.Data[backupKey] = "null";
        Check(WholeStackTransfer.Ready, "restoring the original backup recovers the reset marker");

        ModHelper.Data.Remove(backupKey + ".status");
        Check(!WholeStackTransfer.Ready && AttentionBook.Providers["whole_stack"]().Single().Resolve != null,
            "a missing verification marker fails closed while retaining the original for another confirmation");
        OpenReset(); ModHelper.WritesBeforeReadFailure = 4; ShopNotice.Last = null; prompt.Confirm();
        Check(!WholeStackTransfer.Ready && ShopNotice.Last == "f08.journal_failed",
            "a final verified-marker readback failure keeps the transaction blocked");
        OpenReset(); prompt.Confirm();
        Check(WholeStackTransfer.Ready, "a fresh confirmed attempt can recover after verified-marker failure");

        WholeStackTransfer.Release(); ModHelper.Data.Clear();
    }
    private static void Main()
    {
        ShopInventory.Locations.Clear();
        var source = Grid("source", 4);
        var target = Grid("target", 1);
        var a = Add(source, "apple", 0);
        var b = Add(source, "bread", 1);
        var selection = new List<GameItem> { a, b, a };
        var book = new RoutingBook { rules = new List<RoutingRule> { Rule("target", 0) } };
        var clues = new CustomerIndicators();
        var plan = RoutingPlan.Create(book, selection, clues);
        Check(plan.Moves.Count == 1 && plan.Skipped.Count == 1 && plan.Skipped[0].Reason == "f08.full",
            "shadow capacity is consumed and duplicate selected instances are ignored");
        Check(ReferenceEquals(a.parentInventory, source) && ReferenceEquals(b.parentInventory, source) && target.childItems.Count == 0,
            "preview does not move goods");
        Check(plan.SameAs(RoutingPlan.Create(book, selection, clues)), "the same unchanged selection yields the same plan");
        a.modifiedShape = new Shape { minX = 3 };
        Check(!plan.SameAs(RoutingPlan.Create(book, selection, clues)), "a moved source invalidates the preview");
        a.modifiedShape = new Shape { minX = 0 };
        Add(target, "block", 0);
        Check(!plan.SameAs(RoutingPlan.Create(book, selection, clues)), "a newly occupied destination invalidates the preview");

        var fallback = Grid("fallback", 2);
        book.rules[0].exclusive = true;
        book.rules.Add(Rule("fallback", -1));
        var exclusive = RoutingPlan.Create(book, selection, clues);
        Check(exclusive.Moves.Count == 0 && exclusive.Skipped.Count == 2, "a full exclusive destination does not divert goods");
        book.rules[0].exclusive = false;
        var alternative = RoutingPlan.Create(book, selection, clues);
        Check(alternative.Moves.Count == 2 && alternative.Moves[0].Target.Inventory == fallback,
            "a nonexclusive full destination may fall back to another matching area");

        var settled = Add(target, "settled", 0);
        var settledPlan = RoutingPlan.Create(book, new List<GameItem> { settled }, clues);
        Check(settledPlan.Moves.Count == 0 && settledPlan.Skipped.Count == 1 && settledPlan.Skipped[0].Reason == "f08.already",
            "goods already in the highest-ranked matching destination do not fall back");

        var parent = Add(source, "box", 2);
        var inner = Grid("inner", 2, parent);
        var child = Add(inner, "child", 0);
        var nested = RoutingPlan.Create(book, new List<GameItem> { parent, child }, clues);
        Check(nested.Skipped.Exists(row => ReferenceEquals(row.Item, child) && row.Reason == "f08.parent"),
            "a selected child is not separated from its selected parent");
        book.rules[1].excludedCategories.Add("FOOD");
        a.ordinaryTooltip = false;
        Check(RoutingPlan.Create(book, new List<GameItem> { a }, clues).Moves.Count == 0,
            "an unknown exclusion cannot be bypassed by a replacement tooltip");
        var counter = Grid("counter", 4);
        var counterLocation = ShopInventory.Locations.Find(location => location.id == "counter");
        counterLocation.Ordinary = false;
        counterLocation.MainStock = false;
        var paid = Add(counter, "paid", 0);
        var unpaid = Add(counter, "unpaid", 1);
        paid.counterAllowed = true;
        book.rules[1].excludedCategories.Clear();
        var counterPlan = RoutingPlan.Create(book, new List<GameItem> { paid, unpaid }, clues);
        Check(counterPlan.Moves.Count == 1 && ReferenceEquals(counterPlan.Moves[0].Item, paid) &&
            counterPlan.Skipped.Exists(row => ReferenceEquals(row.Item, unpaid) && row.Reason == "f08.special"),
            "only counter items approved by the shared settlement guard can enter a routing plan");
        paid.counterAllowed = false;
        Check(!counterPlan.SameAs(RoutingPlan.Create(book, new List<GameItem> { paid, unpaid }, clues)),
            "the plan becomes stale when settlement evidence stops being valid");
        foreach (string root in new[] { "counter", "front", "showcase" })
        {
            var host = Add(Grid(root + "_root", 2), "host_" + root, 0);
            var hiddenBranch = Grid(root + "_nested", 4, host);
            ShopInventory.Locations.Find(location => location.id == root + "_nested").MainStock = false;
            var nestedItem = Add(hiddenBranch, "nested_" + root, 0);
            var sourcePlan = RoutingPlan.Create(book, new List<GameItem> { nestedItem }, clues);
            Check(sourcePlan.Moves.Count == 0 && sourcePlan.Skipped.Count == 1 && sourcePlan.Skipped[0].Reason == "f08.special",
                "an ordinary box inside the " + root + " cannot act as a source");
            var staleRule = new RoutingBook { rules = new List<RoutingRule> { Rule(root + "_nested", 100, true) } };
            var targetPlan = RoutingPlan.Create(staleRule, new List<GameItem> { b }, clues);
            Check(targetPlan.Moves.Count == 0 && targetPlan.Skipped.Count == 1 && targetPlan.Skipped[0].Reason == "f08.special",
                "a previously tagged box inside the " + root + " cannot receive stock");
        }
        var reasonSource = Grid("reason source", 2); var reasonTarget = Grid("reason target", 2);
        var excludedItem = Add(reasonSource, "excluded", 0);
        var excludedRule = Rule("reason target", 0); excludedRule.excludedCategories.Add("FOOD");
        var reasonBook = new RoutingBook { rules = new List<RoutingRule> { excludedRule } };
        var excludedPlan = RoutingPlan.Create(reasonBook, new List<GameItem> { excludedItem }, clues);
        Check(excludedPlan.Skipped.Count == 1 && excludedPlan.Skipped[0].Reason == "f08.excluded",
            "an explicit category exclusion is explained as excluded");
        var unknownItem = Add(reasonSource, "unknown", 1); unknownItem.types.Clear(); unknownItem.ordinaryTooltip = false;
        excludedRule.itemIds.Add("unknown");
        var unknownPlan = RoutingPlan.Create(reasonBook, new List<GameItem> { unknownItem }, clues);
        Check(unknownPlan.Skipped.Count == 1 && unknownPlan.Skipped[0].Reason == "f08.unknown_type",
            "an unconfirmed category is distinguished from an explicit exclusion");
        TransferTests();
        UnreadableJournalTests();
        Console.WriteLine($"Routing preview and whole-stack transfers: {assertions} assertions passed.");
    }
}
