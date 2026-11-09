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
    private static RoutingRule Rule(string key)
    {
        var rule = new RoutingRule { location = key, receive = true };
        rule.categories.Add("FOOD"); return rule;
    }
    private static bool CanStack(GameItem first, GameItem second) => first.identifier == second.identifier;
    private static void FieldReplacementTests()
    {
        ShopInventory.Locations.Clear(); ModHelper.Data.Clear(); WholeStackTransfer.Acquire();
        var carried = Grid("carried", 2); var ground = Grid("ground", 3);
        var resident = Add(carried, "old good", 0); resident.unitCount = 2;
        var incoming = Add(ground, "new good", 1); incoming.unitCount = 3;
        var leave = WholeStackTransfer.Empty(resident, ground, new WholeStackTransfer.Reservations());
        ModHelper.FailNextWrite = true;
        Check(WholeStackTransfer.BeginFieldReplacement(leave, incoming, "carried to ground", "field:left") == null &&
            resident.parentInventory == carried, "a failed replacement prewrite cannot move the resident");
        var replacement = WholeStackTransfer.BeginFieldReplacement(leave, incoming, "carried to ground", "field:left");
        Check(replacement != null && !WholeStackTransfer.Ready && WholeStackTransfer.FieldPending().Contains("old good"),
            "a replacement records its pending resident before the first move");
        string pending = ModHelper.Data["wholeStackTransfer"];
        Check(WholeStackTransfer.ApplyFieldReplacement(null, leave, () => true) == WholeStackTransfer.Result.Unavailable &&
            resident.parentInventory == carried, "a replacement move requires its own live record");
        Check(WholeStackTransfer.ApplyFieldReplacement(replacement, leave, () => true) == WholeStackTransfer.Result.Applied &&
            resident.parentInventory == ground && !WholeStackTransfer.Ready && ModHelper.Data["wholeStackTransfer"] == pending,
            "the first leg leaves the whole replacement pending");
        WholeStackTransfer.Release(); WholeStackTransfer.Acquire();
        var issues = AttentionBook.Providers["whole_stack"]().ToList();
        Check(!WholeStackTransfer.Ready && issues.Count == 2 && issues.Any(item => item.ItemId == resident.GetUniqueID()) &&
            issues.Any(item => item.ItemId == incoming.GetUniqueID()) && WholeStackTransfer.FieldReplacementOriginal(replacement, ground),
            "a reloaded replacement record identifies both goods and the unchanged resident");
        var take = WholeStackTransfer.ExactEmpty(incoming, carried, new Shape { minX = 0 });
        Check(WholeStackTransfer.ApplyFieldReplacement(replacement, take, () => true) == WholeStackTransfer.Result.Applied &&
            incoming.parentInventory == carried && !WholeStackTransfer.Ready && ModHelper.Data["wholeStackTransfer"] == pending,
            "the second leg remains pending until the entire replacement is finalized");
        Check(WholeStackTransfer.FinishFieldReplacement(replacement) && WholeStackTransfer.Ready,
            "a completed replacement releases the shared transfer journal");

        var resident2 = Add(carried, "second resident", 1); var incoming2 = Add(ground, "second incoming", 2);
        var leave2 = WholeStackTransfer.Empty(resident2, ground, new WholeStackTransfer.Reservations());
        var replacement2 = WholeStackTransfer.BeginFieldReplacement(leave2, incoming2, "carried to ground", "field:left");
        Check(replacement2 != null && WholeStackTransfer.ApplyFieldReplacement(replacement2, leave2, () => true) == WholeStackTransfer.Result.Applied,
            "another replacement can start after a verified completion");
        var take2 = WholeStackTransfer.ExactEmpty(incoming2, carried, new Shape { minX = 1 });
        Check(WholeStackTransfer.ApplyFieldReplacement(replacement2, take2, () => true) == WholeStackTransfer.Result.Applied,
            "the new incoming good is moved before testing a completion write failure");
        ModHelper.WritesBeforeReadFailure = 1;
        Check(!WholeStackTransfer.FinishFieldReplacement(replacement2) && !WholeStackTransfer.Ready,
            "an unconfirmed replacement completion stays pending and blocks further transfers");
        WholeStackTransfer.Release();
        var uncertain = (System.Collections.IDictionary)typeof(WholeStackTransfer)
            .GetField("uncertain", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        uncertain.Clear();
        WholeStackTransfer.Acquire();
        Check(!WholeStackTransfer.Ready && AttentionBook.Providers["whole_stack"]().Any(item => item.ItemId == resident2.GetUniqueID()),
            "the pending replacement survives a failed completion readback and a fresh load");
        AttentionBook.Providers["whole_stack"]().First().Resolve();
        Check(WholeStackTransfer.Ready, "inspection can release a replacement after an uncertain completion");

        var original = Grid("detached carried", 2); var field = Grid("detached ground", 3);
        var loaded = Add(original, "loaded resident", 0); loaded.tags.Add("BACKPACK_TAG");
        var loadedContents = new GameGridInventory { owner = loaded, capacity = 2, inventoryShape = new Shape() };
        loaded.contentWindow = new PixelWindow { childElement = loadedContents };
        var content = new GameItemElement { identifier = "contents", parentInventory = loadedContents, modifiedShape = new Shape() };
        loadedContents.childItems.Add(content);
        var found = Add(field, "detached incoming", 1);
        WholeStackTransfer.AllowFieldContainerCargo = true;
        var leave3 = WholeStackTransfer.Empty(loaded, field, new WholeStackTransfer.Reservations());
        var replacement3 = WholeStackTransfer.BeginFieldReplacement(leave3, found, "carried to ground", "field:left");
        SlotMarker.Failure = "after_expel";
        Check(WholeStackTransfer.ApplyFieldReplacement(replacement3, leave3, () => true) == WholeStackTransfer.Result.Pending &&
            loaded.parentInventory == null && !WholeStackTransfer.Ready, "failed ground acceptance leaves the exact resident detached and pending");
        SlotMarker.Failure = null;
        content.intrinsic = "changed";
        Check(!WholeStackTransfer.RecoverDetachedFieldItem(replacement3, false, () => true) && loaded.parentInventory == null,
            "changed container contents prevent automatic detached recovery");
        content.intrinsic = "default";
        Check(WholeStackTransfer.RecoverDetachedFieldItem(replacement3, false, () => true) &&
            loaded.parentInventory == original && content.parentInventory == loadedContents &&
            WholeStackTransfer.FieldReplacementIncomingOriginal(replacement3),
            "an unchanged detached loaded resident returns to its original slot with its contents");
        Check(WholeStackTransfer.FinishFieldReplacement(replacement3), "a verified original replacement can finish");

        var leave4 = WholeStackTransfer.Empty(loaded, field, new WholeStackTransfer.Reservations());
        var replacement4 = WholeStackTransfer.BeginFieldReplacement(leave4, found, "carried to ground", "field:left");
        Check(WholeStackTransfer.ApplyFieldReplacement(replacement4, leave4, () => true) == WholeStackTransfer.Result.Applied,
            "the old resident can be placed before testing the incoming transfer");
        var take4 = WholeStackTransfer.ExactEmpty(found, original, leave4.Before);
        SlotMarker.Failure = "after_expel";
        Check(WholeStackTransfer.ApplyFieldReplacement(replacement4, take4, () => true) == WholeStackTransfer.Result.Pending &&
            found.parentInventory == null && !WholeStackTransfer.FieldReplacementIncomingOriginal(replacement4),
            "failed incoming acceptance remains pending after the incoming item leaves the ground");
        SlotMarker.Failure = null;
        found.intrinsic = "changed";
        Check(!WholeStackTransfer.RecoverDetachedFieldItem(replacement4, true, () => true),
            "a changed detached incoming item cannot be placed by recovery");
        found.intrinsic = "default";
        Check(WholeStackTransfer.RecoverDetachedFieldItem(replacement4, true, () => true) &&
            WholeStackTransfer.FieldReplacementIncomingOriginal(replacement4), "the original ground position safely accepts the unchanged incoming item");
        var restore4 = WholeStackTransfer.ExactEmpty(loaded, original, leave4.Before);
        Check(WholeStackTransfer.ApplyFieldReplacement(replacement4, restore4, () => true) == WholeStackTransfer.Result.Applied &&
            WholeStackTransfer.FieldReplacementOriginal(replacement4, original) && WholeStackTransfer.FinishFieldReplacement(replacement4),
            "the replacement closes only after the resident and incoming item are both restored");
        WholeStackTransfer.AllowFieldContainerCargo = false;
        WholeStackTransfer.Release(); ModHelper.Data.Clear();
    }
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
        unsupportedJournal.GetType().GetField("version").SetValue(unsupportedJournal, 3);
        ModHelper.Data[journalKey] = ShopData.Encode(unsupportedJournal);
        var unsupported = AttentionBook.Providers["whole_stack"]().Single();
        Check(!WholeStackTransfer.Ready && unsupported.Resolve != null && unsupported.ReasonKey == "f08.journal_unreadable",
            "an unsupported version belonging to the current save can be reset after inspection without migration");
        unsupported.Resolve(); prompt.Cancel();
        ModHelper.Data[journalKey] = "{\"version\":3,\"run\":\"another run\",\"slot\":1}";
        var foreignIncomplete = AttentionBook.Providers["whole_stack"]().Single();
        Check(!WholeStackTransfer.Ready && foreignIncomplete.Resolve == null,
            "an incomplete unsupported record that explicitly names another run cannot be reset");
        ModHelper.Data[journalKey] = "{\"version\":3,\"run\":\"run\",\"slot\":9}";
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
    private static void RoutingTests()
    {
        ShopInventory.Locations.Clear();
        var source = Grid("source", 4);
        var target = Grid("target", 1);
        var a = Add(source, "apple", 0);
        var b = Add(source, "bread", 1);
        var selection = new List<GameItem> { a, b, a };
        var targetRule = Rule("target");
        var book = new RoutingBook { rules = new List<RoutingRule> { targetRule } };
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
        var fallbackRule = Rule("fallback");
        book.rules.Add(fallbackRule);
        var alternative = RoutingPlan.Create(book, selection, clues);
        Check(alternative.Moves.Count == 2 && alternative.Moves.All(move => move.Target.Inventory == fallback),
            "a full matching destination falls back without splitting selected stacks");
        book.rules.Reverse();
        Check(alternative.SameAs(RoutingPlan.Create(book, selection, clues)), "reordering saved rules cannot alter a routing plan");

        var settled = Add(target, "settled", 0); settled.types.Add("DRINK");
        fallbackRule.categories.Add("DRINK");
        var settledPlan = RoutingPlan.Create(book, new List<GameItem> { settled }, clues);
        Check(settledPlan.Moves.Count == 0 && settledPlan.Skipped.Single().Reason == "f08.already",
            "a matching receiving source stays put even when another area matches more categories");

        var sourceRule = Rule("source"); sourceRule.receive = false; sourceRule.ship = false;
        book.rules.Add(sourceRule);
        var blocked = RoutingPlan.Create(book, selection, clues);
        Check(blocked.Moves.Count == 0 && blocked.Skipped.All(skip => skip.Reason == "f08.shipping_disabled"),
            "automatic classification respects a configured shipping prohibition");
        Check(RoutingPlan.Create(book, selection, clues, false).Moves.Count == 2,
            "disabled F07 leaves configured destination categories usable without restricting shipping");
        sourceRule.ship = true;
        fallbackRule.ship = false;
        Check(RoutingPlan.Create(book, selection, clues).Moves.Count == 2,
            "receiving disabled does not prevent shipping and shipping disabled does not prevent receiving");
        fallbackRule.receive = false;
        Check(RoutingPlan.Create(book, selection, clues).Moves.Count == 0,
            "a matching category cannot override a receiving prohibition");
        fallbackRule.receive = true;

        var parent = Add(source, "box", 2);
        var inner = Grid("inner", 2, parent);
        var child = Add(inner, "child", 0);
        var nested = RoutingPlan.Create(book, new List<GameItem> { parent, child }, clues);
        Check(nested.Skipped.Exists(row => ReferenceEquals(row.Item, child) && row.Reason == "f08.parent"),
            "a selected child is not separated from its selected parent");
        a.ordinaryTooltip = false;
        Check(RoutingPlan.Create(book, new List<GameItem> { a }, clues).Moves.Count == 1,
            "a publicly observed category can match even when the tooltip does not expose every category");
        var counter = Grid("counter", 4);
        var counterLocation = ShopInventory.Locations.Find(location => location.id == "counter");
        counterLocation.Ordinary = false; counterLocation.MainStock = false;
        var paid = Add(counter, "paid", 0);
        var unpaid = Add(counter, "unpaid", 1);
        paid.counterAllowed = true;
        var counterPlan = RoutingPlan.Create(book, new List<GameItem> { paid, unpaid }, clues);
        Check(counterPlan.Moves.Count == 1 && ReferenceEquals(counterPlan.Moves[0].Item, paid) &&
            counterPlan.Skipped.Exists(row => ReferenceEquals(row.Item, unpaid) && row.Reason == "f08.special"),
            "only counter items approved by the settlement guard can enter a routing plan");
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
            Check(sourcePlan.Moves.Count == 0 && sourcePlan.Skipped.Single().Reason == "f08.special",
                "an ordinary box inside the " + root + " cannot act as a source");
            var staleRule = new RoutingBook { rules = new List<RoutingRule> { Rule(root + "_nested") } };
            var targetPlan = RoutingPlan.Create(staleRule, new List<GameItem> { b }, clues);
            Check(targetPlan.Moves.Count == 0 && targetPlan.Skipped.Single().Reason == "f08.special",
                "a previously tagged box inside the " + root + " cannot receive stock");
        }
        var unknown = Add(source, "unknown", 3); unknown.types.Clear(); unknown.ordinaryTooltip = false;
        Check(RoutingPlan.Create(book, new List<GameItem> { unknown }, clues).Skipped.Single().Reason == "f08.unknown_type",
            "missing public classification is explained without assuming a category");
        unknown.ordinaryTooltip = true;
        Check(RoutingPlan.Create(book, new List<GameItem> { unknown }, clues).Skipped.Single().Reason == "f08.no_rule",
            "a known absence of matching categories is explained as no matching rule");
    }

    private static void RankingAndPermissionTests()
    {
        ShopInventory.Locations.Clear(); ModHelper.Data.Clear();
        WholeStackTransfer.Acquire();
        var source = Grid("source", 4);
        var first = Grid("a-target", 1);
        var better = Grid("z-target", 1);
        var item = Add(source, "water", 0); item.types.Add("DRINK"); item.unitCount = 4;
        item.mayStackItemFunc = CanStack;
        var firstRule = Rule("a-target");
        var betterRule = Rule("z-target"); betterRule.categories.Add("DRINK");
        var sourceRule = Rule("source"); sourceRule.receive = false;
        var book = new RoutingBook { rules = new List<RoutingRule> { firstRule, betterRule, sourceRule } };
        var clues = new CustomerIndicators();
        var chosen = new List<GameItem> { item };
        var plan = RoutingPlan.Create(book, chosen, clues);
        Check(plan.Moves.Single().Target.Inventory == better,
            "more matching categories outrank a lexicographically earlier area");
        betterRule.categories.Remove("DRINK");
        Check(RoutingPlan.Create(book, chosen, clues).Moves.Single().Target.Inventory == first,
            "equal matches choose the ordinal area key independently of rule insertion order");
        betterRule.categories.Add("DRINK");
        var resident = Add(better, "water", 0); resident.unitCount = 8; resident.maxStack = 10;
        resident.mayStackItemFunc = CanStack;
        var fallback = RoutingPlan.Create(book, chosen, clues);
        Check(fallback.Moves.Count == 2 && fallback.Moves[0].Target.Inventory == better && fallback.Moves[0].Quantity == 2 &&
            fallback.Moves[1].Target.Inventory == first && fallback.Moves[1].Quantity == 2 &&
            item.unitCount == 4 && resident.unitCount == 8,
            "preview fills the compatible stack and places the remaining instance in the next area");
        Add(first, "block", 0);
        var full = RoutingPlan.Create(book, chosen, clues);
        Check(full.Moves.Count == 1 && full.Moves[0].Quantity == 2 && full.Skipped.Single().Reason == "f08.full" &&
            full.Skipped[0].Quantity == 2 &&
            item.parentInventory == source && item.unitCount == 4 && resident.unitCount == 8,
            "preview reports only the remainder when matching destinations cannot accept everything");
        first.childItems.Clear();
        resident.unitCount = 10;
        plan = RoutingPlan.Create(book, chosen, clues);
        var move = plan.Moves.Single();
        sourceRule.ship = false;
        Check(!plan.SameAs(RoutingPlan.Create(book, chosen, clues)), "changing shipping permissions invalidates the preview");
        Check(WholeStackTransfer.Apply(move.Transfer, "f08.open", "source to target", "source", "a-target",
            () => RoutingPlan.AllowsTransfer(book, move.Source, move.Target, item.types)) == WholeStackTransfer.Result.Unavailable &&
            item.parentInventory == source && item.unitCount == 4, "the transaction scope rechecks shipping before mutation");
        sourceRule.ship = true; firstRule.receive = false;
        Check(!RoutingPlan.AllowsTransfer(book, move.Source, move.Target, item.types),
            "a receiving prohibition invalidates an already previewed transfer");
        Check(WholeStackTransfer.Apply(move.Transfer, "f08.open", "source to target", "source", "a-target",
            () => RoutingPlan.AllowsTransfer(book, move.Source, move.Target, item.types)) == WholeStackTransfer.Result.Unavailable &&
            item.parentInventory == source && item.unitCount == 4, "the transaction scope also rechecks receiving before mutation");
        firstRule.receive = true; firstRule.categories.Clear();
        Check(!RoutingPlan.AllowsTransfer(book, move.Source, move.Target, item.types),
            "changing the accepted categories invalidates an already previewed transfer");
        firstRule.categories.Add("FOOD"); sourceRule.ship = false;
        Check(RoutingPlan.AllowsTransfer(book, move.Source, move.Target, item.types, false),
            "the transaction shipping check also honors disabled F07");
        sourceRule.ship = true;
        sourceRule.receive = true;
        Check(!RoutingPlan.AllowsTransfer(book, move.Source, move.Target, item.types) &&
            RoutingPlan.Create(book, chosen, clues).Skipped.Single().Reason == "f08.already",
            "a source newly accepting the public category cancels the old transfer and keeps the goods settled");
        sourceRule.receive = false;
        Check(WholeStackTransfer.Apply(move.Transfer, "f08.open", "source to target", "source", "a-target",
            () => RoutingPlan.AllowsTransfer(book, move.Source, move.Target, item.types)) == WholeStackTransfer.Result.Applied &&
            item.parentInventory == first && item.unitCount == 4 && resident.unitCount == 10,
            "an unchanged permitted transfer moves the whole source and preserves total units");
        var fresh = Add(source, "fresh", 0); fresh.unitCount = 4;
        Grid("source", 1);
        var ambiguous = RoutingPlan.Create(book, new List<GameItem> { fresh }, clues);
        Check(ambiguous.Moves.Count == 0 && ambiguous.Skipped.Single().Reason == "f08.changed",
            "an ambiguous source binding cannot bypass shipping permissions");
        ShopInventory.Locations.RemoveAt(ShopInventory.Locations.Count - 1);
        Grid("a-target", 1);
        ambiguous = RoutingPlan.Create(book, new List<GameItem> { fresh }, clues);
        Check(ambiguous.Moves.Count == 0 && fresh.parentInventory == source,
            "duplicate target keys never select an arbitrary receiving area");
        WholeStackTransfer.Release(); ModHelper.Data.Clear();
    }

    private static void MergeBeforeEmptyTests()
    {
        ShopInventory.Locations.Clear(); ModHelper.Data.Clear();
        WholeStackTransfer.Acquire();
        var source = Grid("source", 3);
        var preferred = Grid("preferred", 1);
        var later = Grid("later", 1);
        var item = Add(source, "water", 0); item.types.Add("DRINK"); item.unitCount = 4;
        item.mayStackItemFunc = CanStack;
        var resident = Add(later, "water", 0); resident.unitCount = 6; resident.maxStack = 10;
        resident.mayStackItemFunc = CanStack;
        var preferredRule = Rule("preferred"); preferredRule.categories.Add("DRINK");
        var laterRule = Rule("later");
        var book = new RoutingBook { rules = new List<RoutingRule> { preferredRule, laterRule } };
        var clues = new CustomerIndicators();
        var selection = new List<GameItem> { item };

        var preview = RoutingPlan.Create(book, selection, clues);
        var move = preview.Moves.Single();
        Check(ReferenceEquals(move.Target.Inventory, later) && ReferenceEquals(move.Transfer.Stack, resident) &&
            move.Quantity == 4 && preferred.childItems.Count == 0 && item.unitCount == 4 && resident.unitCount == 6,
            "a complete merge in a lower-ranked area takes priority over the higher-ranked empty area without changing the preview");
        Check(preview.SameAs(RoutingPlan.Create(book, selection, clues)),
            "an unchanged merge-first preview is stable at commit revalidation");

        resident.unitCount = 7;
        var changed = RoutingPlan.Create(book, selection, clues);
        Check(!preview.SameAs(changed) && changed.Moves.Count == 2 && changed.Moves[0].Quantity == 3 &&
            ReferenceEquals(changed.Moves[1].Target.Inventory, preferred) && changed.Moves[1].Quantity == 1 &&
            !changed.Moves[1].Transfer.IsStack,
            "reduced stack capacity invalidates the preview and places the remainder in the ranked empty area");
        Check(WholeStackTransfer.Apply(move.Transfer, "f08.open", "source to later", "source", "later",
            () => RoutingPlan.AllowsTransfer(book, move.Source, move.Target, item.types)) == WholeStackTransfer.Result.Unchanged &&
            ReferenceEquals(item.parentInventory, source) && item.unitCount == 4 && resident.unitCount == 7,
            "the transaction rejects a changed target without moving or merging any units");
        resident.unitCount = 6;
        resident.reserved = true;
        var pinned = RoutingPlan.Create(book, selection, clues);
        Check(ReferenceEquals(pinned.Moves.Single().Target.Inventory, preferred) && !pinned.Moves.Single().Transfer.IsStack &&
            resident.unitCount == 6, "a fixed resident is not used as a merge target");
        resident.reserved = false;
        Check(WholeStackTransfer.Apply(move.Transfer, "f08.open", "source to later", "source", "later",
            () => RoutingPlan.AllowsTransfer(book, move.Source, move.Target, item.types)) == WholeStackTransfer.Result.Applied &&
            item.destroyed && item.parentInventory == null && resident.unitCount == 10 && preferred.childItems.Count == 0,
            "committing the unchanged preview merges the exact whole source into the displayed target");

        var blocked = Add(source, "water", 1); blocked.unitCount = 4; blocked.mayStackItemFunc = CanStack;
        Add(preferred, "block", 0);
        resident.unitCount = 8;
        var full = RoutingPlan.Create(book, new List<GameItem> { blocked }, clues);
        Check(full.Moves.Count == 1 && full.Moves[0].Quantity == 2 && full.Skipped.Single().Reason == "f08.full" &&
            full.Skipped[0].Quantity == 2 &&
            ReferenceEquals(blocked.parentInventory, source) && blocked.unitCount == 4 && resident.unitCount == 8,
            "a partly full target is planned with an accurately reported remainder without mutating the preview");

        WholeStackTransfer.Release(); ModHelper.Data.Clear();
    }

    private static void PartialRoutingTests()
    {
        ShopInventory.Locations.Clear(); ModHelper.Data.Clear(); WholeStackTransfer.Acquire();
        var source = Grid("source", 4);
        var first = Grid("a", 1); var second = Grid("b", 1); var last = Grid("c", 1);
        var item = Add(source, "coin", 0); item.unitCount = 7; item.mayStackItemFunc = CanStack;
        var a = Add(first, "coin", 0); a.unitCount = 8; a.maxStack = 10; a.mayStackItemFunc = CanStack;
        var b = Add(second, "coin", 0); b.unitCount = 6; b.maxStack = 9; b.mayStackItemFunc = CanStack;
        var book = new RoutingBook { rules = new List<RoutingRule> { Rule("a"), Rule("b"), Rule("c") } };
        var clues = new CustomerIndicators();
        var selected = new List<GameItem> { item };
        var preview = RoutingPlan.Create(book, selected, clues);
        Check(preview.Moves.Count == 3 && preview.Skipped.Count == 0 &&
            preview.Moves.Select(move => move.Quantity).SequenceEqual(new[] { 2, 3, 2 }) &&
            preview.Moves.Select(move => move.Transfer.SourceBefore).SequenceEqual(new[] { 7, 5, 2 }) &&
            preview.Moves.Select(move => move.Target.Inventory).SequenceEqual(new GameInventory[] { first, second, last }) &&
            preview.SameAs(RoutingPlan.Create(book, selected, clues)) && item.unitCount == 7 && a.unitCount == 8 && b.unitCount == 6,
            "preview reserves successive partial merges and the exact remaining instance without changing inventory");
        foreach (var move in preview.Moves)
            Check(WholeStackTransfer.Apply(move.Transfer, "f08.open", "source to target", "source", "target",
                () => RoutingPlan.AllowsTransfer(book, move.Source, move.Target, item.types)) == WholeStackTransfer.Result.Applied,
                "each planned transfer accepts its projected source and target quantities");
        Check(item.parentInventory == last && item.unitCount == 2 && !item.destroyed && a.unitCount == 10 && b.unitCount == 9 &&
            first.childItems.Sum(value => value.unitCount) + second.childItems.Sum(value => value.unitCount) +
            last.childItems.Sum(value => value.unitCount) == 21,
            "multiple destination transfers conserve units and preserve the original remainder instance");

        var sharedSource = Grid("shared-source", 2); var sharedTarget = Grid("shared-target", 1);
        var one = Add(sharedSource, "fuel", 0); one.unitCount = 3; one.mayStackItemFunc = CanStack;
        var two = Add(sharedSource, "fuel", 1); two.unitCount = 4; two.mayStackItemFunc = CanStack;
        var stack = Add(sharedTarget, "fuel", 0); stack.unitCount = 5; stack.maxStack = 10; stack.mayStackItemFunc = CanStack;
        var sharedBook = new RoutingBook { rules = new List<RoutingRule> { Rule("shared-target") } };
        var shared = RoutingPlan.Create(sharedBook, new List<GameItem> { one, two }, clues);
        Check(shared.Moves.Count == 2 && shared.Moves[0].Quantity == 3 && shared.Moves[1].Quantity == 2 &&
            shared.Moves[1].Transfer.StackBefore == 8 && shared.Skipped.Single().Item == two && shared.Skipped[0].Quantity == 2,
            "competing sources reserve target capacity once and report only the unmoved remainder");
        foreach (var move in shared.Moves)
            Check(WholeStackTransfer.Apply(move.Transfer, "f08.open", "shared", "source", "target", () => true) == WholeStackTransfer.Result.Applied,
                "reserved partial transfers commit in order");
        Check(one.destroyed && two.parentInventory == sharedSource && two.unitCount == 2 && stack.unitCount == 10,
            "a full destination leaves the exact residual units in the original source");

        var limitedSource = Grid("limited-source", 1); var limitedTarget = Grid("limited-target", 2); limitedTarget.slotLimit = 1;
        var limitedItem = Add(limitedSource, "limited", 0); limitedItem.unitCount = 4;
        var limitedBook = new RoutingBook { rules = new List<RoutingRule> { Rule("limited-target") } };
        var limited = RoutingPlan.Create(limitedBook, new List<GameItem> { limitedItem }, clues);
        Check(limited.Moves.Count == 0 && limited.Skipped.Single().Quantity == 4 && limited.Skipped[0].Reason == "f08.full",
            "a partial empty placement is not attempted when native splitting could recreate different item state");
        limitedItem.maxRemove = 2;
        var restricted = RoutingPlan.Create(limitedBook, new List<GameItem> { limitedItem }, clues);
        Check(restricted.Moves.Count == 0 && restricted.Skipped.Single().Reason == "f08.partial" &&
            restricted.Skipped[0].Quantity == 4, "a source with a restricted removal limit remains untouched");

        var chosenSource = Grid("chosen-source", 1); var chosenTarget = Grid("chosen-target", 1);
        var chosenItem = Add(chosenSource, "chosen", 0); chosenItem.unitCount = 3; chosenItem.mayStackItemFunc = CanStack;
        var chosenStack = Add(chosenTarget, "chosen", 0); chosenStack.unitCount = 6; chosenStack.maxStack = 10; chosenStack.mayStackItemFunc = CanStack;
        var chosenBook = new RoutingBook { rules = new List<RoutingRule> { Rule("chosen-target") } };
        var chosenPlan = RoutingPlan.Create(chosenBook, new List<GameItem> { chosenItem, chosenStack }, clues);
        Check(chosenPlan.Moves.Count == 0 && chosenPlan.Skipped.Exists(skip => skip.Item == chosenItem && skip.Reason == "f08.full") &&
            chosenPlan.Skipped.Exists(skip => skip.Item == chosenStack && skip.Reason == "f08.already"),
            "a selected destination stack is never consumed as another selected item's merge target");

        var failureSource = Grid("failure-source", 1); var failureTarget = Grid("failure-target", 1);
        var failure = Add(failureSource, "parts", 0); failure.unitCount = 5; failure.mayStackItemFunc = CanStack;
        var destination = Add(failureTarget, "parts", 0); destination.unitCount = 8; destination.maxStack = 10; destination.mayStackItemFunc = CanStack;
        var failureBook = new RoutingBook { rules = new List<RoutingRule> { Rule("failure-target") } };
        var failurePlan = RoutingPlan.Create(failureBook, new List<GameItem> { failure }, clues);
        var transfer = failurePlan.Moves.Single().Transfer;
        failure.unitCount = 4;
        Check(!failurePlan.SameAs(RoutingPlan.Create(failureBook, new List<GameItem> { failure }, clues)) &&
            WholeStackTransfer.Apply(transfer, "f08.open", "failure", "source", "target", () => true) == WholeStackTransfer.Result.Unchanged &&
            failure.unitCount == 4 && destination.unitCount == 8, "changing the source quantity invalidates a partial transfer");
        failure.unitCount = 5; failure.reserved = true;
        Check(WholeStackTransfer.Apply(transfer, "f08.open", "failure", "source", "target", () => true) == WholeStackTransfer.Result.Unchanged &&
            failure.unitCount == 5 && destination.unitCount == 8, "pinning the source after preview prevents mutation");
        failure.reserved = false;
        destination.reserved = true;
        Check(WholeStackTransfer.Apply(transfer, "f08.open", "failure", "source", "target", () => true) == WholeStackTransfer.Result.Unchanged &&
            failure.unitCount == 5 && destination.unitCount == 8 && WholeStackTransfer.Ready,
            "pinning the target after preview rejects a partial transfer without mutation");
        destination.reserved = false;
        SlotMarker.Failure = "after_target";
        var outcome = WholeStackTransfer.Apply(transfer, "f08.open", "failure", "source", "target", () => true);
        SlotMarker.Failure = null;
        Check(outcome == WholeStackTransfer.Result.Pending && failure.unitCount == 5 && destination.unitCount == 10 && !WholeStackTransfer.Ready,
            "an interrupted partial merge blocks subsequent transfers instead of claiming success or retrying");
        WholeStackTransfer.Release(); WholeStackTransfer.Acquire();
        var pending = AttentionBook.Providers["whole_stack"]().ToList();
        Check(!WholeStackTransfer.Ready && pending.Count == 2 && pending.All(row => row.Location.Contains("5 -> 3") && row.Location.Contains("8 -> 10")),
            "reloaded pending records expose both original quantities and intended results for inspection");
        pending[0].Resolve();
        Check(WholeStackTransfer.Ready, "explicit inspection releases a partial-transfer journal");

        object current = typeof(WholeStackTransfer).GetField("journal", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        object legacy = Activator.CreateInstance(current.GetType(), true);
        foreach (var field in current.GetType().GetFields()) field.SetValue(legacy, field.GetValue(current));
        legacy.GetType().GetField("version").SetValue(legacy, 1);
        legacy.GetType().GetField("sourceBefore").SetValue(legacy, 0);
        legacy.GetType().GetField("state").SetValue(legacy, "pending");
        ModHelper.Data["wholeStackTransfer"] = ShopData.Encode(legacy);
        WholeStackTransfer.Release(); WholeStackTransfer.Acquire();
        Check(!WholeStackTransfer.Ready && AttentionBook.Providers["whole_stack"]().First().Location.Contains("2"),
            "an older whole-stack pending journal remains readable and blocks transfers");
        AttentionBook.Providers["whole_stack"]().First().Resolve();
        Check(WholeStackTransfer.Ready, "inspection can resolve an older journal without changing its schema");
        WholeStackTransfer.Release(); ModHelper.Data.Clear();
    }

    private static void Main()
    {
        RoutingTests();
        RankingAndPermissionTests();
        MergeBeforeEmptyTests();
        PartialRoutingTests();
        FieldReplacementTests();
        TransferTests();
        UnreadableJournalTests();
        Console.WriteLine($"Routing preview and transfers: {assertions} assertions passed.");
    }
}
