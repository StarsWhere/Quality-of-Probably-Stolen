namespace ShopAssist
{
    internal static class DesktopLayoutRules
    {
        internal static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && System.Math.Abs(value) <= 1000000f;
    }
}
