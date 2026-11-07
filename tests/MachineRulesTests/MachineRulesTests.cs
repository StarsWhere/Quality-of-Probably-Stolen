using System;
using ShopAssist;

internal static class MachineRulesTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static FeedRecoveryDecision Decide(bool staged = false, bool pending = false, bool inInput = false,
        bool inSource = false, bool inOutput = false, bool delivered = false, bool inputEmpty = true,
        bool outputEmpty = true, bool requireEmpty = true) => FeedRecoveryRules.Decide(staged, pending, inInput,
            inSource, inOutput, delivered, inputEmpty, outputEmpty, requireEmpty);

    private static void Main()
    {
        Check(MaintenanceSourceKey.TryParse("root:stock", 31, out int owner, out string role, out int page) &&
            owner == 0 && role == "stock" && page == -1, "The main stock must be a valid battery source.");
        Check(MaintenanceSourceKey.TryParse("root:stock", 35, out _, out _, out _), "The main stock must be a valid filter source.");
        Check(MaintenanceSourceKey.TryParse("item:42:contents:-1", 35, out _, out _, out _),
            "An owned container must be a valid filter source.");
        Check(MaintenanceSourceKey.TryParse("item:42:batteries:-1", 31, out _, out _, out _),
            "An owned recharger must be a valid battery source.");
        Check(!MaintenanceSourceKey.TryParse("item:42:batteries:-1", 35, out _, out _, out _) &&
            !MaintenanceSourceKey.TryParse("root:counter", 31, out _, out _, out _) &&
            !MaintenanceSourceKey.TryParse("item:0:contents:-1", 31, out _, out _, out _) &&
            !MaintenanceSourceKey.TryParse("item:42:contents:5", 31, out _, out _, out _) &&
            !MaintenanceSourceKey.TryParse("item:042:contents:-1", 31, out _, out _, out _),
            "Unsupported or ambiguous source identities must be rejected.");

        Check(FeedRecoveryRules.MustRetain(2, 0, false, false) && FeedRecoveryRules.MustRetain(0, 9, false, false) &&
            FeedRecoveryRules.MustRetain(0, 0, true, false) && FeedRecoveryRules.MustRetain(0, 0, false, true) &&
            !FeedRecoveryRules.MustRetain(0, 0, false, false), "Stopping must retain every uncertain transaction.");
        Check(FeedRecoveryRules.CanRestart(3, 3, 0, 0, false, false, false) &&
            !FeedRecoveryRules.CanRestart(2, 3, 0, 0, false, false, false) &&
            !FeedRecoveryRules.CanRestart(3, 3, 0, 1, false, false, false) &&
            !FeedRecoveryRules.CanRestart(3, 3, 2, 0, false, false, false) &&
            !FeedRecoveryRules.CanRestart(3, 3, 0, 0, true, false, false) &&
            !FeedRecoveryRules.CanRestart(3, 3, 0, 0, false, true, false) &&
            !FeedRecoveryRules.CanRestart(3, 3, 0, 0, false, false, true),
            "Only a complete, settled and unpaused batch may be restarted.");
        Check(FeedRecoveryRules.AfterCycle(true, 0, true) == FeedCycleDecision.Record &&
            FeedRecoveryRules.AfterCycle(true, 2, false) == FeedCycleDecision.NoProgress &&
            FeedRecoveryRules.AfterCycle(true, 1, true) == FeedCycleDecision.Inspect &&
            FeedRecoveryRules.AfterCycle(false, 0, true) == FeedCycleDecision.Inspect &&
            FeedRecoveryRules.AfterCycle(true, 0, false) == FeedCycleDecision.Inspect,
            "Only a verified native cycle may count a batch.");
        Check(FeedRecoveryRules.CanTransfer(false, false, false) && !FeedRecoveryRules.CanTransfer(true, false, false) &&
            !FeedRecoveryRules.CanTransfer(false, true, false) && !FeedRecoveryRules.CanTransfer(false, false, true),
            "Observed, paused or stopped cycles must leave output pending.");

        Check(Decide(staged: true, inInput: true, inputEmpty: false) == FeedRecoveryDecision.KeepStaged,
            "Inputs already in the furnace must remain tracked until the native cycle completes.");
        Check(Decide(staged: true, inSource: true) == FeedRecoveryDecision.ClearStaged,
            "Both original inputs returned to the source may clear their staged intent.");
        Check(Decide(staged: true, inSource: true, inputEmpty: false) == FeedRecoveryDecision.Inspect &&
            Decide(staged: true, inInput: true, outputEmpty: false) == FeedRecoveryDecision.Inspect &&
            Decide(staged: true) == FeedRecoveryDecision.Inspect,
            "Partial, missing or ambiguous inputs must remain in inspection.");
        Check(Decide(pending: true, inOutput: true, outputEmpty: false) == FeedRecoveryDecision.KeepPending &&
            Decide(pending: true, delivered: true) == FeedRecoveryDecision.ClearPending,
            "The exact output stays pending until in the destination.");
        Check(Decide(pending: true) == FeedRecoveryDecision.Inspect &&
            Decide(staged: true, pending: true) == FeedRecoveryDecision.Inspect &&
            Decide(inputEmpty: false) == FeedRecoveryDecision.Inspect,
            "Missing output, conflicting intent and occupied inspection cannot be cleared.");
        Console.WriteLine("PASS machine source identity and furnace recovery boundaries");
    }
}
