using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ShopAssist
{
    // Ordering and retained zeroes are scoped to the object currently in the scanner.
    internal sealed class LiquidDisplayRules
    {
        internal sealed class Part
        {
            internal string Id;
            internal int Amount;
            internal int NativeIndex;
        }

        private object sample;
        private readonly Dictionary<string, Part> seen = new Dictionary<string, Part>(StringComparer.Ordinal);
        private readonly List<string> previous = new List<string>();

        internal List<Part> Update(object currentSample, IEnumerable<Part> measured, IReadOnlyList<string> pins,
            bool sortByShare, bool holdOrder)
        {
            if (!ReferenceEquals(sample, currentSample))
            {
                sample = currentSample;
                seen.Clear();
                previous.Clear();
            }
            holdOrder = holdOrder && previous.Count > 0;
            var live = new Dictionary<string, Part>(StringComparer.Ordinal);
            foreach (Part part in measured ?? Enumerable.Empty<Part>())
            {
                if (part == null || string.IsNullOrEmpty(part.Id) || part.Amount <= 0 || live.ContainsKey(part.Id)) continue;
                var copy = new Part { Id = part.Id, Amount = part.Amount, NativeIndex = part.NativeIndex };
                live.Add(copy.Id, copy);
                seen[copy.Id] = copy;
            }
            var pinned = new HashSet<string>(pins ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (var part in seen.Values)
                if (!live.ContainsKey(part.Id) && (pinned.Contains(part.Id) || holdOrder && previous.Contains(part.Id)))
                    live.Add(part.Id, new Part { Id = part.Id, Amount = 0, NativeIndex = part.NativeIndex });

            var order = new List<string>();
            void Add(string id) { if (id != null && live.ContainsKey(id) && !order.Contains(id)) order.Add(id); }
            if (holdOrder)
                foreach (string id in previous) Add(id);
            else
                foreach (string id in pins ?? Array.Empty<string>()) Add(id);
            IEnumerable<Part> remaining = live.Values.OrderBy(part => part.NativeIndex);
            if (!holdOrder && sortByShare)
                remaining = remaining.OrderByDescending(part => part.Amount).ThenBy(part => part.NativeIndex);
            foreach (Part part in remaining) Add(part.Id);
            previous.Clear(); previous.AddRange(order);
            return order.Select(id => live[id]).ToList();
        }

        internal void Reset() { sample = null; seen.Clear(); previous.Clear(); }
    }

    internal static class LiquidPins
    {
        private sealed class Data
        {
            [JsonRequired] public int version;
            [JsonRequired] public List<string> pins;
        }

        internal static bool TryLegacy(string legacy, out List<string> pins)
        {
            pins = new List<string>();
            if (string.IsNullOrWhiteSpace(legacy)) return true;
            string value = legacy.Trim();
            if (value.Length > 128) return false;
            pins.Add(value);
            return true;
        }

        internal static bool TryReadOrMigrate(string raw, out List<string> pins, out bool migrated)
        {
            migrated = false;
            if (TryRead(raw, out pins)) return true;
            string value = (raw ?? "").Trim();
            if (value.StartsWith("{", StringComparison.Ordinal) || value.StartsWith("[", StringComparison.Ordinal) ||
                value.StartsWith("\"", StringComparison.Ordinal)) return false;
            if (!TryLegacy(raw, out pins)) return false;
            migrated = true;
            return true;
        }

        internal static bool TryRead(string raw, out List<string> pins)
        {
            pins = null;
            try
            {
                Data data = ShopData.Decode<Data>(raw, 32768);
                if (data == null || data.version != 1 || data.pins == null || data.pins.Count > 128 ||
                    data.pins.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || id != id.Trim()) ||
                    data.pins.Distinct(StringComparer.Ordinal).Count() != data.pins.Count) return false;
                pins = data.pins;
                return true;
            }
            catch (Exception error) when (error is FormatException || error is JsonException || error is ArgumentException)
            { return false; }
        }

        internal static string Write(IReadOnlyList<string> pins) => ShopData.Encode(new Data
        {
            version = 1,
            pins = pins.ToList()
        });
    }
}
