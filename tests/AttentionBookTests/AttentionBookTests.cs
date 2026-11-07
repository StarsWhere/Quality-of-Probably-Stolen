using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using ShopAssist;

internal static class AttentionBookTests
{
    private const string Corrupt = "{unreadable attention data";
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void ResetBook() => typeof(AttentionBook).GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    private static void SimulateRestart()
    {
        ResetBook();
        ((IDictionary)typeof(AttentionBook).GetField("unsaved", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Clear();
    }
    private static void NewScope()
    {
        AttentionBook.Dispose();
        PlayerStore.Instance.runID = Guid.NewGuid().ToString("N"); PlayerStore.Instance.saveSlotId = 1;
        ModHelper.Data.Clear(); ModHelper.FailRead = ModHelper.FailBeforeWrite = ModHelper.FailAfterWrite = null;
        ModHelper.OnWrite = null; ShopNotice.Last = null;
    }
    private static void SetRaw(string raw) => ModHelper.SetModData(ModEntry.Id, "attention", raw);
    private static string Raw() => ModHelper.GetModData(ModEntry.Id, "attention", null);
    private static AttentionItem Invalid() => AttentionBook.Snapshot().Single(item => item.Key == "attention.invalid");
    private static void AskToRebuild()
    {
        var action = Invalid().Resolve;
        Need(action != null, "A corrupt record has no rebuild action");
        action();
        Need(GenericConfirmationUIManager.Instance.panel.activeInHierarchy &&
            GenericConfirmationUIManager.Instance.Message == "attention.rebuild_warning", "Rebuild did not request confirmation");
    }
    private static void Confirm() => GenericConfirmationUIManager.Instance.Confirm();
    private static void Report(int id, string title = "pending")
    {
        var result = new OperationResult("qa.action");
        result.SkipMissing(title, 1, "inv.inspect", itemId: id);
        AttentionBook.Report(result);
    }
    private static void Provider()
    {
        AttentionBook.Register("independent", () => new[] { new AttentionItem
        { Key = "pending", Title = "provider issue", ReasonKey = "inv.inspect", NeedsInspection = true } });
    }
    private static bool HasProvider() => AttentionBook.Snapshot().Any(item => item.Key == "independent:pending");
    private static string Backup() => ModHelper.Data.Single(entry => entry.Key.Contains(":attention.unreadable.")).Value;

    private static void TransientReadIsNotRebuildable()
    {
        NewScope(); SetRaw("{\"version\":1,\"run\":\"" + PlayerStore.Instance.runID + "\",\"slot\":1,\"issues\":[]}");
        string before = Raw(); bool once = true;
        ModHelper.FailRead = key => key == "attention" && once && !(once = false);
        var invalid = Invalid();
        Need(invalid.Resolve == null && invalid.Retry != null && Raw() == before && AttentionBook.HasInspection("qa.action", 1),
            "A read exception allowed destructive recovery or did not block");
        invalid.Retry();
        Need(!AttentionBook.HasInspection("qa.action", 1) && Raw() == before, "A successful read retry did not recover");
    }

    private static void ConfirmationPreservesPendingAndProviders()
    {
        NewScope(); SetRaw(Corrupt); Provider(); Report(41);
        Need(AttentionBook.HasInspection("qa.action", 42) && HasProvider(), "Corrupt data did not block or retain its provider");
        AskToRebuild(); GenericConfirmationUIManager.Instance.Cancel();
        Need(Raw() == Corrupt && !ModHelper.Data.Keys.Any(key => key.Contains(":attention.unreadable.")), "Cancel changed the original");
        AskToRebuild(); Confirm();
        Need(Backup() == Corrupt && Raw() != Corrupt && Raw().Contains("\"item\":41"), "Rebuild lost the original or a pending issue");
        Need(AttentionBook.HasInspection("qa.action", 41) && !AttentionBook.HasInspection("qa.action", 42) && HasProvider(),
            "Rebuild did not restore item-specific blocking or changed an independent provider");
        Need(ShopNotice.Last == "attention.rebuilt", "Verified recovery did not report completion");
    }

    private static void BackupAndMarkerFailuresRemainBlocked()
    {
        NewScope(); SetRaw(Corrupt); Provider();
        bool once = true;
        ModHelper.FailBeforeWrite = key => key.StartsWith("attention.unreadable.") && once && !(once = false);
        AskToRebuild(); Confirm();
        Need(Raw() == Corrupt && AttentionBook.HasInspection("qa.action", 12) && ShopNotice.Last == "attention.save_failed" && HasProvider(),
            "A failed backup changed data or lifted the block");
        ModHelper.FailBeforeWrite = null;
        once = true;
        ModHelper.FailAfterWrite = key => key == "attention.recovery" && once && !(once = false);
        AskToRebuild(); Confirm();
        Need(Raw() == Corrupt && AttentionBook.HasInspection("qa.action", 12), "An unverified recovery marker changed data");
        ModHelper.FailAfterWrite = null; SimulateRestart();
        Need(Invalid().Resolve != null && AttentionBook.HasInspection("qa.action", 12), "Pending recovery did not survive restart");
        AskToRebuild(); Confirm();
        Need(!AttentionBook.HasInspection("qa.action", 12) && HasProvider(), "Pending recovery could not be resumed");
    }

    private static void WrittenButUnverifiedSurvivesRestart()
    {
        NewScope(); SetRaw(Corrupt); Provider(); Report(51); bool once = true;
        ModHelper.FailAfterWrite = key => key == "attention" && once && !(once = false);
        AskToRebuild(); Confirm();
        Need(Raw() != Corrupt && Backup() == Corrupt && AttentionBook.HasInspection("qa.action", 52) &&
            ShopNotice.Last == "attention.save_failed", "An unverified write was reported as successful");
        ModHelper.FailAfterWrite = null; SimulateRestart();
        Need(Invalid().Resolve != null && AttentionBook.HasInspection("qa.action", 52) && HasProvider(),
            "Unverified recovery did not remain blocked after restart");
        Need(AttentionBook.Snapshot().Any(item => item.ItemId == 51), "Recovery journal lost an in-session issue after restart");
        AskToRebuild(); Confirm();
        Need(AttentionBook.HasInspection("qa.action", 51) && !AttentionBook.HasInspection("qa.action", 52) && HasProvider(),
            "Verified retry lost the pending issue or the provider");
    }

    private static void CompletionReadbackFailureRequiresVerification()
    {
        NewScope(); SetRaw(Corrupt); Report(61); bool once = true;
        ModHelper.FailAfterWrite = key => key == "attention.recovery" &&
            ModHelper.Data.Values.Any(value => value.Contains("\"completed\":true")) && once && !(once = false);
        AskToRebuild(); Confirm();
        Need(AttentionBook.HasInspection("qa.action", 62) && ShopNotice.Last == "attention.save_failed", "Unverified completion lifted the block");
        ModHelper.FailAfterWrite = null; ResetBook();
        var retry = AttentionBook.Snapshot().Single(item => item.Key == "attention.unsaved");
        Need(AttentionBook.HasInspection("qa.action", 62), "Re-reading a completed marker discarded an unsaved candidate");
        retry.Retry();
        Need(AttentionBook.HasInspection("qa.action", 61) && !AttentionBook.HasInspection("qa.action", 62), "Verified retry failed to preserve pending data");
    }

    private static void StaleConfirmationAndInvalidCandidate()
    {
        NewScope(); SetRaw(Corrupt); AskToRebuild(); SetRaw("changed"); Confirm();
        Need(Raw() == "changed" && !ModHelper.Data.Keys.Any(key => key.Contains(":attention.unreadable.")) &&
            AttentionBook.HasInspection("qa.action", 1), "Stale confirmation overwrote changed content");
        NewScope(); SetRaw(Corrupt); AskToRebuild(); PlayerStore.Instance.saveSlotId = 2; Confirm();
        PlayerStore.Instance.saveSlotId = 1;
        Need(Raw() == Corrupt && !ModHelper.Data.Keys.Any(key => key.Contains(":attention.unreadable.")), "Stale confirmation crossed save slots");
        NewScope(); SetRaw(Corrupt); Report(71, new string('x', 4097)); AskToRebuild(); Confirm();
        Need(Raw() == Corrupt && AttentionBook.HasInspection("qa.action", 72) && ShopNotice.Last == "attention.save_failed",
            "Invalid pending data was written or unblocked");
        AttentionBook.Snapshot().Single(item => item.ItemId == 71).Resolve();
        AskToRebuild(); Confirm();
        Need(!AttentionBook.HasInspection("qa.action", 72) && Backup() == Corrupt, "Checked invalid pending data could not be cleared before retry");
    }

    private static void ReportsDuringRecoveryAreRetained()
    {
        NewScope(); SetRaw(Corrupt); Report(81);
        ModHelper.OnWrite = (key, value) => { if (key == "attention") { ModHelper.OnWrite = null; Report(82); } };
        AskToRebuild(); Confirm();
        Need(AttentionBook.HasInspection("qa.action", 81) && AttentionBook.HasInspection("qa.action", 82) &&
            !AttentionBook.HasInspection("qa.action", 83) && Raw().Contains("\"item\":82"),
            "An issue reported during recovery was lost");
    }

    private static void UnreadableMarkerAndMissingBackup()
    {
        NewScope();
        string valid = "{\"version\":1,\"run\":\"" + PlayerStore.Instance.runID + "\",\"slot\":1,\"issues\":[]}";
        SetRaw(valid); ModHelper.SetModData(ModEntry.Id, "attention.recovery", "broken-marker"); Report(91);
        AskToRebuild(); Confirm();
        Need(Raw() == valid && ModHelper.Data.Any(entry => entry.Key.Contains(":attention.recovery.unreadable.") && entry.Value == "broken-marker"),
            "Marker recovery did not preserve the broken marker or changed the main data");
        Need(AttentionBook.HasInspection("qa.action", 92), "Marker recovery discarded an unsaved issue");
        AttentionBook.Snapshot().Single(item => item.Key == "attention.unsaved").Retry();
        Need(AttentionBook.HasInspection("qa.action", 91) && !AttentionBook.HasInspection("qa.action", 92),
            "Marker recovery could not save the retained issue");

        NewScope(); SetRaw(Corrupt); bool once = true;
        ModHelper.FailAfterWrite = key => key == "attention" && once && !(once = false);
        AskToRebuild(); Confirm(); ModHelper.FailAfterWrite = null;
        string backupKey = ModHelper.Data.Keys.Single(key => key.Contains(":attention.unreadable."));
        ModHelper.Data[backupKey] = "damaged-backup"; SimulateRestart();
        Need(Invalid().Resolve == null && AttentionBook.HasInspection("qa.action", 92),
            "A missing or changed backup allowed the unverified recovery to proceed");
        Invalid().Retry();
        Need(Invalid().Resolve == null && AttentionBook.HasInspection("qa.action", 92), "Retry ignored a damaged backup");
    }

    private static void ReadbackFailureRemainsBlocked()
    {
        NewScope(); SetRaw(Corrupt); bool armed = false;
        ModHelper.OnWrite = (key, value) => { if (key == "attention") armed = true; };
        ModHelper.FailRead = key => { if (key != "attention" || !armed) return false; armed = false; return true; };
        AskToRebuild(); Confirm();
        Need(AttentionBook.HasInspection("qa.action", 101) && ShopNotice.Last == "attention.save_failed", "Failed readback did not block");
        ModHelper.OnWrite = null; ModHelper.FailRead = null; SimulateRestart();
        Need(Invalid().Resolve != null && AttentionBook.HasInspection("qa.action", 101), "Readback failure was forgotten after restart");
        AskToRebuild(); Confirm();
        Need(!AttentionBook.HasInspection("qa.action", 101), "Readback failure could not be verified on retry");
    }

    public static int Main()
    {
        TransientReadIsNotRebuildable(); ConfirmationPreservesPendingAndProviders(); BackupAndMarkerFailuresRemainBlocked();
        WrittenButUnverifiedSurvivesRestart(); CompletionReadbackFailureRequiresVerification(); StaleConfirmationAndInvalidCandidate();
        ReportsDuringRecoveryAreRetained(); UnreadableMarkerAndMissingBackup(); ReadbackFailureRemainsBlocked();
        Console.WriteLine("AttentionBook recovery tests passed");
        return 0;
    }
}
