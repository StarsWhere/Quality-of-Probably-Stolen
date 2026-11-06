using System.Collections.Generic;

namespace ShopAssist
{
    // Session-only evidence: never discovers children or stores live item state.
    internal sealed class KnownRiskLedger<TItem, TInventory> where TItem : class where TInventory : class
    {
        private sealed class Record
        {
            internal PublicRisk Risk;
            internal readonly List<TItem> Ancestors = new List<TItem>();
            internal readonly List<TInventory> Inventories = new List<TInventory>();
        }

        private readonly Dictionary<TItem, Record> records = new Dictionary<TItem, Record>();

        internal void Remember(TItem item, PublicRisk risk, IEnumerable<TItem> ancestors, IEnumerable<TInventory> inventories)
        {
            var record = new Record { Risk = risk };
            record.Ancestors.AddRange(ancestors);
            record.Inventories.AddRange(inventories);
            records[item] = record;
        }

        internal Dictionary<TItem, PublicRisk> InsideSnapshot()
        {
            var result = new Dictionary<TItem, PublicRisk>();
            foreach (Record record in records.Values)
                foreach (TItem parent in record.Ancestors)
                {
                    if (parent == null) continue;
                    result.TryGetValue(parent, out PublicRisk known);
                    result[parent] = known | record.Risk;
                }
            return result;
        }

        internal bool InvalidateInventory(TInventory inventory, TItem item)
        {
            bool changed = false;
            foreach (TItem seen in new List<TItem>(records.Keys))
                if (ReferenceEquals(seen, item) || records[seen].Inventories.Contains(inventory)) changed |= records.Remove(seen);
            return changed;
        }

        internal bool InvalidateItem(TItem item)
        {
            bool changed = false;
            foreach (TItem seen in new List<TItem>(records.Keys))
                if (ReferenceEquals(seen, item) || records[seen].Ancestors.Contains(item)) changed |= records.Remove(seen);
            return changed;
        }

        internal void Clear() => records.Clear();
    }
}
