using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ShopAssist
{
    [Serializable]
    internal sealed class RoutingBook
    {
        [JsonRequired] public int schemaVersion = 1;
        [JsonRequired] public List<RoutingRule> rules = new List<RoutingRule>();
    }

    [Serializable]
    internal sealed class RoutingRule
    {
        [JsonRequired] public string location;
        [JsonRequired] public int priority;
        [JsonRequired] public bool receive;
        [JsonRequired] public bool exclusive;
        [JsonRequired] public List<string> itemIds = new List<string>();
        [JsonRequired] public List<string> categories = new List<string>();
        [JsonRequired] public List<string> excludedIds = new List<string>();
        [JsonRequired] public List<string> excludedCategories = new List<string>();

        internal RoutingRule Copy() => new RoutingRule
        {
            location = location, priority = priority, receive = receive, exclusive = exclusive,
            itemIds = new List<string>(itemIds), categories = new List<string>(categories),
            excludedIds = new List<string>(excludedIds), excludedCategories = new List<string>(excludedCategories)
        };
    }

    internal static class RoutingRules
    {
        internal const int MaxRules = 256, MaxEntries = 64, MaxTokenLength = 128;

        internal static bool Valid(RoutingBook book, ICollection<string> knownCategories)
        {
            if (book == null || book.schemaVersion != 1 || book.rules == null || book.rules.Count > MaxRules) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (RoutingRule rule in book.rules)
                if (!Valid(rule, knownCategories) || !seen.Add(rule.location)) return false;
            return true;
        }

        internal static bool Valid(RoutingRule rule, ICollection<string> knownCategories)
        {
            return rule != null && ValidLocation(rule.location) && rule.priority >= -100 && rule.priority <= 100 &&
                ValidValues(rule.itemIds, null) && ValidValues(rule.excludedIds, null) &&
                ValidValues(rule.categories, knownCategories) && ValidValues(rule.excludedCategories, knownCategories);
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

        // 0 means excluded or unknown; specific item rules outrank categories.
        internal static int Match(RoutingRule rule, string itemId, ICollection<string> publicTypes, bool completeTypes)
        {
            if (rule == null || !rule.receive || string.IsNullOrEmpty(itemId)) return 0;
            if (rule.excludedCategories.Count != 0 && !completeTypes) return 0;
            if (rule.excludedIds.Contains(itemId) || (publicTypes != null && publicTypes.Any(rule.excludedCategories.Contains))) return 0;
            if (rule.itemIds.Contains(itemId)) return 2;
            return publicTypes != null && publicTypes.Any(rule.categories.Contains) ? 1 : 0;
        }

        internal static IEnumerable<RoutingRule> Ranked(RoutingBook book, string itemId, ICollection<string> publicTypes, bool completeTypes)
        {
            return book.rules.Select((rule, order) => new { rule, order, grade = Match(rule, itemId, publicTypes, completeTypes) })
                .Where(entry => entry.grade > 0)
                .OrderByDescending(entry => entry.grade)
                .ThenByDescending(entry => entry.rule.priority)
                .ThenBy(entry => entry.order)
                .Select(entry => entry.rule);
        }
    }
}
