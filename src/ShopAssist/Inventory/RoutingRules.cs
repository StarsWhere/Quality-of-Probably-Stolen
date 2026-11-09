using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ShopAssist
{
    [Serializable]
    internal sealed class RoutingBook
    {
        [JsonRequired] public int schemaVersion = 2;
        [JsonRequired] public List<RoutingRule> rules = new List<RoutingRule>();
    }

    [Serializable]
    internal sealed class RoutingRule
    {
        [JsonRequired] public string location;
        [JsonRequired] public bool receive;
        [JsonRequired] public bool ship = true;
        [JsonRequired] public List<string> categories = new List<string>();

        internal RoutingRule Copy() => new RoutingRule
        {
            location = location, receive = receive, ship = ship, categories = new List<string>(categories)
        };
    }

    internal static class RoutingRules
    {
        internal const int MaxRules = 256, MaxEntries = 64, MaxTokenLength = 128;

        internal static bool Valid(RoutingBook book, ICollection<string> knownCategories)
        {
            if (book == null || book.schemaVersion != 2 || book.rules == null || book.rules.Count > MaxRules) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (RoutingRule rule in book.rules)
                if (!Valid(rule, knownCategories) || !seen.Add(rule.location)) return false;
            return true;
        }

        internal static bool Valid(RoutingRule rule, ICollection<string> knownCategories)
        {
            return rule != null && ValidLocation(rule.location) && ValidValues(rule.categories, knownCategories);
        }

        private static bool ValidValues(List<string> values, ICollection<string> allowed)
        {
            if (values == null || values.Count > MaxEntries) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in values)
                if (string.IsNullOrWhiteSpace(value) || value.Length > MaxTokenLength || !seen.Add(value) ||
                    (allowed != null && !allowed.Contains(value))) return false;
            return true;
        }

        internal static bool ValidLocation(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 192) return false;
            string[] parts = key.Split(':');
            if (parts.Length == 2 && parts[0] == "root")
                return parts[1] == "stock" || parts[1] == "counter" || parts[1] == "showcase" || parts[1] == "front";
            return parts.Length == 4 && parts[0] == "item" && int.TryParse(parts[1], out int id) && id > 0 &&
                !string.IsNullOrWhiteSpace(parts[2]) && parts[2].Length <= 96 &&
                int.TryParse(parts[3], out int page) && page >= -1 && page <= 1024;
        }

        internal static int Match(RoutingRule rule, ICollection<string> publicTypes)
        {
            if (rule == null || !rule.receive || publicTypes == null) return 0;
            return rule.categories.Count(publicTypes.Contains);
        }

        internal static bool AllowsReceiving(RoutingRule rule, ICollection<string> publicTypes, bool allowUnmatched) =>
            rule == null || rule.receive && (allowUnmatched || Match(rule, publicTypes) > 0);

        internal static IEnumerable<RoutingRule> Ranked(RoutingBook book, ICollection<string> publicTypes)
        {
            return book.rules.Select(rule => new { rule, grade = Match(rule, publicTypes) })
                .Where(entry => entry.grade > 0)
                .OrderByDescending(entry => entry.grade)
                .ThenBy(entry => entry.rule.location, StringComparer.Ordinal)
                .Select(entry => entry.rule);
        }
    }
}
