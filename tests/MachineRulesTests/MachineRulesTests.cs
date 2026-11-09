using System;
using System.Linq;
using ShopAssist;

internal static class MachineRulesTests
{
    private sealed class FixedRandom : Random
    {
        private readonly bool swap;
        internal FixedRandom(bool swap) { this.swap = swap; }
        public override int Next(int maxValue) => swap ? 0 : maxValue - 1;
    }

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
        var machines = new[]
        {
            (name: "enough", energy: 2, required: 1),
            (name: "short-high", energy: 9, required: 5),
            (name: "short-low", energy: 6, required: 4),
            (name: "empty", energy: 0, required: 1),
            (name: "large", energy: int.MaxValue, required: int.MaxValue)
        };
        var ordered = ConsumableSelectionRules.OrderBatteryMachines(machines, machine => machine.energy,
            machine => machine.required, new FixedRandom(false)).Select(machine => machine.name);
        Check(ordered.SequenceEqual(new[] { "empty", "short-low", "short-high", "large", "enough" }),
            "Machines below two runs must come first, ordered by absolute energy without overflow.");
        var tied = new[] { (name: "first", energy: 3, required: 2), (name: "second", energy: 3, required: 2) };
        var unchanged = ConsumableSelectionRules.OrderBatteryMachines(tied, machine => machine.energy,
            machine => machine.required, new FixedRandom(false)).Select(machine => machine.name);
        var swapped = ConsumableSelectionRules.OrderBatteryMachines(tied, machine => machine.energy,
            machine => machine.required, new FixedRandom(true)).Select(machine => machine.name);
        Check(unchanged.SequenceEqual(new[] { "first", "second" }) && swapped.SequenceEqual(new[] { "second", "first" }),
            "Equal-priority, equal-energy machines must follow the randomized tie order.");

        Check(ConsumableSelectionRules.BatteryStateValid(0, 100, 1) &&
            ConsumableSelectionRules.BatteryStateValid(int.MaxValue, int.MaxValue, int.MaxValue) &&
            !ConsumableSelectionRules.BatteryStateValid(-1, 100, 1) &&
            !ConsumableSelectionRules.BatteryStateValid(1, 0, 1) &&
            !ConsumableSelectionRules.BatteryStateValid(101, 100, 1) &&
            !ConsumableSelectionRules.BatteryStateValid(1, 100, 0) &&
            !ConsumableSelectionRules.BatteryStateValid(1, 100, -1),
            "A battery must have valid charge, capacity and positive machine power demand.");

        Check(ConsumableSelectionRules.BatteryImproves(0, 40, 100, 60, 200, 10) &&
            !ConsumableSelectionRules.BatteryImproves(1, 40, 100, 60, 200, 10),
            "Energy and percentage must make different choices for different capacities.");
        Check(ConsumableSelectionRules.BatteryImproves(1, 40, 100, 30, 50, 10) &&
            !ConsumableSelectionRules.BatteryImproves(1, 40, 100, 20, 50, 10) &&
            !ConsumableSelectionRules.BatteryImproves(0, 40, 100, 40, 100, 10),
            "Only strict improvement is allowed; ties cannot cause battery ping-pong.");
        Check(ConsumableSelectionRules.BatteryImproves(2, 4, 100, 10, 100, 10) &&
            !ConsumableSelectionRules.BatteryImproves(2, 10, 100, 100, 100, 10) &&
            !ConsumableSelectionRules.BatteryImproves(0, 4, 100, 9, 100, 10),
            "Unable mode triggers only below native demand and spares must meet demand.");
        Check(ConsumableSelectionRules.BatteryImproves(1, int.MaxValue - 1, int.MaxValue, int.MaxValue, int.MaxValue, 1) &&
            !ConsumableSelectionRules.BatteryImproves(1, 0, 0, 10, 100, 1),
            "Percentage comparison must not overflow and invalid capacity must be rejected.");
        Check(ConsumableSelectionRules.FilterDue(0, 0) && !ConsumableSelectionRules.FilterDue(0, 1) &&
            ConsumableSelectionRules.FilterDue(1, 100) && !ConsumableSelectionRules.FilterDue(1, -1) &&
            !ConsumableSelectionRules.FilterDue(2, 0), "Filter modes must reject invalid states and honor exhaustion.");
        Check(ConsumableSelectionRules.FilterImproves(10, 11) && !ConsumableSelectionRules.FilterImproves(10, 10) &&
            !ConsumableSelectionRules.FilterImproves(10, 9) && !ConsumableSelectionRules.FilterImproves(-1, 20),
            "Only strictly better known durability may replace a filter.");
        Check(!ConsumableSelectionRules.FilterNeedsWork(1, 10, true, false) &&
            ConsumableSelectionRules.FilterNeedsWork(1, 10, true, true) &&
            ConsumableSelectionRules.FilterNeedsWork(1, 10, false, false) &&
            ConsumableSelectionRules.FilterNeedsWork(1, 0, true, false) &&
            !ConsumableSelectionRules.FilterNeedsWork(0, 10, false, true),
            "Best-durability mode must skip a healthy filter with no improvement but report missing or exhausted supplies.");
        var filters = new[]
        {
            (id: "same", durability: 20, same: true, allowed: true, ready: true),
            (id: "blocked", durability: 90, same: false, allowed: true, ready: false),
            (id: "best", durability: 80, same: false, allowed: true, ready: true),
            (id: "unapproved", durability: 100, same: false, allowed: false, ready: true),
            (id: "tie", durability: 10, same: true, allowed: true, ready: true)
        };
        var best = ConsumableSelectionRules.OrderFilters(filters.Where(filter => filter.allowed &&
            ConsumableSelectionRules.FilterImproves(10, filter.durability)), filter => filter.durability, filter => filter.same)
            .First(filter => filter.ready);
        Check(best.id == "best", "The highest compatible and exchangeable durability must win over the same material.");

        Check(MaintenanceSourceKey.TryParse("item:42:contents:-1", 32, out _, out _, out _) &&
            !MaintenanceSourceKey.TryParse("item:42:batteries:-1", 32, out _, out _, out _),
            "Seed sources must share canonical container identity without exposing charger batteries.");
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
        Console.WriteLine("PASS consumable conditions, source identity and furnace recovery boundaries");
    }
}
