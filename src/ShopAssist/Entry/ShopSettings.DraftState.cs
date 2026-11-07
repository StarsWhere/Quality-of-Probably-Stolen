using System;
using System.Collections.Generic;

namespace ShopAssist
{
    internal sealed class SettingsDraftState
    {
        internal Dictionary<string, object> Baseline { get; private set; }
        internal Dictionary<string, object> Current { get; private set; }

        internal SettingsDraftState(IDictionary<string, object> initial) => Reset(initial);

        internal void Reset(IDictionary<string, object> values)
        {
            Baseline = new Dictionary<string, object>(values);
            Current = new Dictionary<string, object>(values);
        }

        internal void Observe(IDictionary<string, object> values)
        {
            foreach (var pair in values)
            {
                if (!Baseline.ContainsKey(pair.Key)) Baseline.Add(pair.Key, pair.Value);
                Current[pair.Key] = pair.Value;
            }
        }

        internal Dictionary<string, object> Changes()
        {
            var changes = new Dictionary<string, object>();
            foreach (var pair in Current)
                if (Baseline.TryGetValue(pair.Key, out object baseline) && !Equals(pair.Value, baseline))
                    changes.Add(pair.Key, pair.Value);
            return changes;
        }

        internal Dictionary<string, object> Applicable(IDictionary<string, object> changes)
        {
            var values = new Dictionary<string, object>();
            foreach (var pair in changes)
                if (Baseline.ContainsKey(pair.Key)) values.Add(pair.Key, pair.Value);
            return values;
        }
    }
}
