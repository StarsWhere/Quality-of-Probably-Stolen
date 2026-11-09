namespace ShopAssist
{
    internal enum CargoOrigin { Old, New, Unknown }

    internal static class TripCargoRules
    {
        internal static int MigrateMode(bool? enabled, int? mode) => enabled == false ? 0 :
            mode.HasValue && mode >= 0 && mode <= 2 ? mode.Value : enabled == true ? 2 : 0;

        internal static bool ShouldUnload(int mode, bool returning, bool inStore, bool ready, bool returned, bool uncertain) =>
            mode > 0 && mode <= 2 && !returning && inStore && ready && returned && !uncertain;
        internal static CargoOrigin Merge(CargoOrigin source, CargoOrigin target) =>
            source == target ? source : CargoOrigin.Unknown;

        internal static bool CanTrustGround(bool continuous, bool tracked, CargoOrigin known) =>
            continuous && (!tracked || known == CargoOrigin.New);

        internal static CargoOrigin ContainedOrigin(bool tracked, CargoOrigin known, bool trustedGround, bool visible) =>
            tracked ? known : trustedGround && visible ? CargoOrigin.New : CargoOrigin.Unknown;

        internal static bool ExposeCargoDetails(bool found, bool visible, bool confirmed) => found && (visible || confirmed);

        internal static bool NeedsTripInspection(bool sourceFound, bool container) => !sourceFound || container;

        internal static bool CanUnload(CargoOrigin origin, int recorded, int current, bool allowOld = false) =>
            (origin == CargoOrigin.New || allowOld && origin == CargoOrigin.Old) && recorded > 0 && recorded == current;
    }
}
