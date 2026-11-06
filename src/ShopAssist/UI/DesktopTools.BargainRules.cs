using System;
using System.Globalization;

namespace ShopAssist
{
    internal readonly struct DesktopRect
    {
        internal DesktopRect(float left, float bottom, float right, float top)
        { Left = left; Bottom = bottom; Right = right; Top = top; }
        internal float Left { get; }
        internal float Bottom { get; }
        internal float Right { get; }
        internal float Top { get; }
        internal float Area => Math.Max(0, Right - Left) * Math.Max(0, Top - Bottom);
    }

    internal static class DesktopBargainRules
    {
        internal static float Intersection(DesktopRect a, DesktopRect b) =>
            Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
            Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom));

        internal static bool PreferLeft(DesktopRect left, DesktopRect right, DesktopRect viewport, DesktopRect? item)
        {
            float leftPenalty = (left.Area - Intersection(left, viewport)) * 2f;
            float rightPenalty = (right.Area - Intersection(right, viewport)) * 2f;
            if (item.HasValue)
            {
                leftPenalty += Intersection(left, item.Value);
                rightPenalty += Intersection(right, item.Value);
            }
            return leftPenalty < rightPenalty;
        }
    }

    internal readonly struct DesktopBargainPosition
    {
        internal DesktopBargainPosition(Guid run, int slot, int mode, float x, float y)
        { Run = run; Slot = slot; Mode = mode; X = x; Y = y; }
        internal Guid Run { get; }
        internal int Slot { get; }
        internal int Mode { get; }
        internal float X { get; }
        internal float Y { get; }
        internal string Encode()
        {
            if (Run == Guid.Empty || Slot < 0 || Mode < 0 || Mode > 2 || !DesktopLayoutRules.Finite(X) || !DesktopLayoutRules.Finite(Y))
                throw new InvalidOperationException("Invalid bargain position.");
            return "1|" + Run.ToString("N") + "|" + Slot.ToString(CultureInfo.InvariantCulture) + "|" +
                Mode.ToString(CultureInfo.InvariantCulture) + "|" + X.ToString("R", CultureInfo.InvariantCulture) + "|" +
                Y.ToString("R", CultureInfo.InvariantCulture);
        }
        internal static bool TryDecode(string raw, Guid expectedRun, int expectedSlot, out DesktopBargainPosition value)
        {
            value = default;
            if (string.IsNullOrEmpty(raw) || raw.Length > 128) return false;
            string[] parts = raw.Split('|');
            if (parts.Length != 6 || parts[0] != "1" ||
                !Guid.TryParseExact(parts[1], "N", out Guid run) || run == Guid.Empty || run != expectedRun ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int slot) || slot < 0 || slot != expectedSlot ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int mode) || mode < 0 || mode > 2 ||
                !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !DesktopLayoutRules.Finite(x) ||
                !float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) || !DesktopLayoutRules.Finite(y)) return false;
            value = new DesktopBargainPosition(run, slot, mode, x, y);
            return true;
        }
    }
}
