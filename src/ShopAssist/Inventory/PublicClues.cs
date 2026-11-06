using System;
using System.Collections.Generic;

namespace ShopAssist
{
    [Flags]
    internal enum PublicRisk { None = 0, Stolen = 1, Contraband = 2 }
    internal enum PublicMatch { Unknown, Match }

    // Only already-public categories enter this calculation. A match denotes
    // the displayed interest category, not a sale or an accepted price.
    internal static class PublicClues
    {
        internal static PublicMatch Match(ICollection<string> interests, ICollection<string> restrictions,
            ICollection<string> types)
        {
            if (interests == null || types == null) return PublicMatch.Unknown;
            if (types.Contains("NOT_FOR_RESALE")) return PublicMatch.Unknown;
            if (restrictions != null)
                foreach (string type in types)
                    if (restrictions.Contains(type)) return PublicMatch.Unknown;
            foreach (string type in types)
                if (interests.Contains(type)) return PublicMatch.Match;
            return PublicMatch.Unknown;
        }

        internal static bool ContainsShownType(string tooltip, string localizedType)
        {
            return !string.IsNullOrEmpty(tooltip) && !string.IsNullOrEmpty(localizedType) &&
                tooltip.IndexOf("[" + localizedType + "]", StringComparison.Ordinal) >= 0;
        }

        internal static bool AllowedByExclusions(ICollection<string> exclusions, ICollection<string> knownTypes, bool complete = true)
        {
            if (exclusions == null || exclusions.Count == 0) return true;
            if (knownTypes == null) return false;
            foreach (string type in knownTypes)
                if (exclusions.Contains(type)) return false;
            return complete;
        }
    }
}
