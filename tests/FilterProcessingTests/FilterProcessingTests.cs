using System;
using System.Collections.Generic;
using ShopAssist;

internal static class FilterProcessingTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Main()
    {
        Check(FilterProcessingRules.Transferred(true, 1800, 1500), "Sub-millilitre transfers must be detected.");
        Check(!FilterProcessingRules.Transferred(false, 1800, 1500) &&
            !FilterProcessingRules.Transferred(true, 1800, 1800), "A transfer needs processing and actual source loss.");
        Check(FilterProcessingRules.Filled(true, 1000, 1000) && FilterProcessingRules.Filled(false, 1000, 1001) &&
            !FilterProcessingRules.Filled(false, 1000, 1000), "A fill may be fully filtered but a no-op must not trigger.");

        var tracker = new FilterProcessingTracker<string, int>();
        var published = new List<int>();
        var outer = tracker.Enter("tank");
        var inner = tracker.Enter("fill");
        inner.Items.Add(1);
        tracker.Complete(inner, true, published.Add);
        Check(published.Count == 0, "Nested work must wait for the outer operation.");
        outer.Items.Add(2);
        tracker.Complete(outer, true, published.Add);
        Check(published.Count == 2 && published[0] == 1 && published[1] == 2, "Successful work must be published after completion.");

        published.Clear();
        outer = tracker.Enter("outer");
        inner = tracker.Enter("inner");
        inner.Items.Add(3);
        tracker.Complete(inner, true, published.Add);
        tracker.Complete(outer, false, published.Add);
        Check(published.Count == 0, "An outer failure must discard completed nested observations.");
        outer = tracker.Enter("outer");
        inner = tracker.Enter("inner");
        tracker.Complete(inner, false, published.Add);
        outer.Items.Add(4);
        tracker.Complete(outer, true, published.Add);
        Check(published.Count == 0 && tracker.Current == null, "An inner failure must invalidate the complete operation.");

        outer = tracker.Enter("many slots");
        for (int i = 0; i < 256; i++) outer.Items.Add(i);
        tracker.Complete(outer, true, published.Add);
        Check(published.Count == 256, "The processing tracker must not silently discard requests above 128.");
    }
}
