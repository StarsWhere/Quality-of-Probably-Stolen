using System;

namespace ShopAssist
{
    internal sealed class AttentionItem
    {
        internal string Key, Feature, Title, Location, ReasonKey;
        internal int ItemId;
        internal bool NeedsInspection;
        internal string ActionLabelKey, ResolveLabelKey;
        internal Action Retry, Resolve;
    }
}
