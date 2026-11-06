using System;

namespace ShopAssist
{
    // Text selection is kept separate from the Unity views so all displays use
    // the same rules for custom names and the optional vanilla fallback.
    internal static class AreaLabelPolicy
    {
        internal static bool SameRenderedName(string left, string right) =>
            left != null && right != null && (string.Equals(left, right, StringComparison.Ordinal) ||
                string.Equals(left.Replace("\r", "").Replace("\n", ""), right.Replace("\r", "").Replace("\n", ""), StringComparison.Ordinal));

        internal static string Name(string customName, string defaultName, bool showDefault)
        {
            if (!string.IsNullOrWhiteSpace(customName)) return customName;
            return showDefault && !string.IsNullOrWhiteSpace(defaultName) ? defaultName : null;
        }

        internal static string Heading(string owner, string page)
        {
            if (string.IsNullOrWhiteSpace(owner)) return string.IsNullOrWhiteSpace(page) ? null : page;
            if (string.IsNullOrWhiteSpace(page) || string.Equals(owner, page, StringComparison.Ordinal)) return owner;
            return owner + " · " + page;
        }

        // Keep item labels inside the middle band, away from the usual corner
        // counters. Tiny views with a counter have no safe room for a label.
        internal static bool ItemLayout(float width, float height, bool hasCounter,
            bool upper, int fontSize, out float labelWidth, out float labelHeight, out float y)
        {
            labelWidth = 0;
            labelHeight = 0;
            y = 0;
            if (width < 12 || height < 12 || (hasCounter && (width < 32 || height < 24))) return false;

            float side = hasCounter ? 8f : 2f;
            labelWidth = width - 2f * side;
            if (labelWidth < 8f) return false;
            labelHeight = Math.Min(Math.Max(5, fontSize + 3), Math.Max(5f, height * 0.60f));
            y = -height * (upper ? 0.25f : 0.5f);
            return true;
        }
    }
}
