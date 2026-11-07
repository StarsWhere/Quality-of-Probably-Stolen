namespace ShopAssist
{
    internal enum CargoOrigin { Old, New, Unknown }

    internal static class TripCargoRules
    {
        internal static CargoOrigin Merge(CargoOrigin source, CargoOrigin target) =>
            source == target ? source : CargoOrigin.Unknown;

        internal static bool CanTrustGround(bool continuous, bool tracked, CargoOrigin known) =>
            continuous && (!tracked || known == CargoOrigin.New);

        internal static bool CanUnload(CargoOrigin origin, int recorded, int current) =>
            origin == CargoOrigin.New && recorded > 0 && recorded == current;
    }
}
