using System;
using System.Collections.Generic;

namespace ShopAssist
{
    [Flags]
    internal enum PublicRisk { None = 0, Stolen = 1, Contraband = 2 }
    // Observation helpers remain shared by inventory labels and routing.
    internal static class PublicClues
    {
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
