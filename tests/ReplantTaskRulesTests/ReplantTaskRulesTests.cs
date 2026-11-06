using System;
using ShopAssist;

internal static class ReplantTaskRulesTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Main()
    {
        Check(ReplantTaskRules.Decide(ReplantPhase.Pending, false, false, true, true) == ReplantDecision.Wait,
            "A disabled rule must not move a seed.");
        Check(ReplantTaskRules.Decide(ReplantPhase.Pending, true, true, true, true) == ReplantDecision.Clear,
            "An already occupied input completes the pending task without replacing the player's item.");
        Check(ReplantTaskRules.Decide(ReplantPhase.Idle, true, false, true, true) == ReplantDecision.None,
            "A repeated event must not run a completed task again.");
        Check(ReplantTaskRules.Decide(ReplantPhase.Inspect, true, false, true, true) == ReplantDecision.None,
            "An uncertain move requires inspection even if a matching seed becomes available.");
        Check(ReplantTaskRules.Decide(ReplantPhase.Pending, true, false, false, true) == ReplantDecision.Wait &&
            ReplantTaskRules.Decide(ReplantPhase.Pending, true, false, true, false) == ReplantDecision.Wait,
            "A pending task must wait for both machine readiness and an eligible seed.");
        Check(ReplantTaskRules.Decide(ReplantPhase.Pending, true, false, true, true) == ReplantDecision.Attempt,
            "A ready pending task should attempt the native move.");
        Console.WriteLine("PASS replant task safety transitions");
    }
}
