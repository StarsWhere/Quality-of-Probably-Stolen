using System;
using System.Globalization;

namespace ShopAssist
{
    internal readonly struct DesktopInspectionPosition
    {
        internal DesktopInspectionPosition(Guid run, int slot, float x, float y)
        { Run = run; Slot = slot; X = x; Y = y; }
        internal Guid Run { get; }
        internal int Slot { get; }
        internal float X { get; }
        internal float Y { get; }
        internal string Encode()
        {
            if (Run == Guid.Empty || Slot < 0 || !DesktopLayoutRules.Finite(X) || !DesktopLayoutRules.Finite(Y))
                throw new InvalidOperationException("Invalid inspection position.");
            return "1|" + Run.ToString("N") + "|" + Slot.ToString(CultureInfo.InvariantCulture) + "|" +
                X.ToString("R", CultureInfo.InvariantCulture) + "|" + Y.ToString("R", CultureInfo.InvariantCulture);
        }
        internal static bool TryDecode(string raw, Guid expectedRun, int expectedSlot, out DesktopInspectionPosition value)
        {
            value = default;
            if (string.IsNullOrEmpty(raw) || raw.Length > 128) return false;
            string[] parts = raw.Split('|');
            if (parts.Length != 5 || parts[0] != "1" ||
                !Guid.TryParseExact(parts[1], "N", out Guid run) || run == Guid.Empty || run != expectedRun ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int slot) || slot < 0 || slot != expectedSlot ||
                !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !DesktopLayoutRules.Finite(x) ||
                !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) || !DesktopLayoutRules.Finite(y)) return false;
            value = new DesktopInspectionPosition(run, slot, x, y);
            return true;
        }
    }

    internal static class DesktopInspectionRules
    {
        // Keeping offset/scale constant keeps the image-space point beneath the
        // centre of the lens unchanged when magnification changes.
        internal static float Recenter(float offset, float oldScale, float newScale) => offset * newScale / oldScale;
        internal static float CounterMove(float initialOffset, float lensDelta, float baseMultiplier, float zoom) =>
            (initialOffset - lensDelta * baseMultiplier) * zoom;
    }
}
