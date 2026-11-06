namespace ShopAssist
{
    internal enum ReplantPhase { Idle = 0, Pending = 1, Inspect = 2 }
    internal enum ReplantDecision { None, Wait, Clear, Attempt }

    internal static class ReplantTaskRules
    {
        internal static ReplantDecision Decide(ReplantPhase phase, bool enabled, bool inputOccupied, bool ready, bool hasSeed)
        {
            if (phase == ReplantPhase.Idle || phase == ReplantPhase.Inspect) return ReplantDecision.None;
            if (!enabled) return ReplantDecision.Wait;
            if (inputOccupied) return ReplantDecision.Clear;
            return ready && hasSeed ? ReplantDecision.Attempt : ReplantDecision.Wait;
        }
    }
}
