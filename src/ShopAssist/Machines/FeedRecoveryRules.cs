namespace ShopAssist
{
    internal enum FeedRecoveryDecision { Ready, KeepStaged, ClearStaged, KeepPending, ClearPending, Inspect }
    internal enum FeedCycleDecision { NoProgress, Inspect, Record }

    internal static class FeedRecoveryRules
    {
        internal static bool MustRetain(int staged, int pending, bool inspect, bool unsaved) =>
            staged != 0 || pending != 0 || inspect || unsaved;

        internal static bool CanRestart(int completed, int limit, int staged, int pending, bool paused, bool stopped, bool unsaved) =>
            limit > 0 && completed == limit && staged == 0 && pending == 0 && !paused && !stopped && !unsaved;

        internal static FeedCycleDecision AfterCycle(bool ran, int remaining, bool expectedOutput)
        {
            if (!ran || remaining != 0) return remaining == 2 ? FeedCycleDecision.NoProgress : FeedCycleDecision.Inspect;
            return expectedOutput ? FeedCycleDecision.Record : FeedCycleDecision.Inspect;
        }

        internal static bool CanTransfer(bool observeOnly, bool paused, bool stopped) => !observeOnly && !paused && !stopped;

        internal static FeedRecoveryDecision Decide(bool staged, bool pending, bool stagedInInput, bool stagedInSource,
            bool productInOutput, bool productDelivered, bool inputEmpty, bool outputEmpty, bool requireEmpty)
        {
            if (staged && pending) return FeedRecoveryDecision.Inspect;
            if (staged)
            {
                if (!outputEmpty) return FeedRecoveryDecision.Inspect;
                if (stagedInInput) return FeedRecoveryDecision.KeepStaged;
                return inputEmpty && stagedInSource ? FeedRecoveryDecision.ClearStaged : FeedRecoveryDecision.Inspect;
            }
            if (pending)
            {
                if (productInOutput) return FeedRecoveryDecision.KeepPending;
                return productDelivered && outputEmpty ? FeedRecoveryDecision.ClearPending : FeedRecoveryDecision.Inspect;
            }
            return requireEmpty && (!inputEmpty || !outputEmpty) ? FeedRecoveryDecision.Inspect : FeedRecoveryDecision.Ready;
        }
    }
}
