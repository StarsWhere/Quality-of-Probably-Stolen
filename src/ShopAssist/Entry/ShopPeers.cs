using System;

namespace ShopAssist
{
    internal static class ShopPeers
    {
        internal static bool Running(string id) => Active(id) != null;

        internal static LoadedMod Active(string id)
        {
            foreach (LoadedMod mod in ModLoader.Mods)
                if (mod != null && mod.IsEnabled && mod.Instance != null && string.IsNullOrEmpty(mod.LoadError) &&
                    string.Equals(mod.Manifest?.ID, id, StringComparison.OrdinalIgnoreCase)) return mod;
            return null;
        }
    }
}
