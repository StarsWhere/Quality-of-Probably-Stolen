using System;

namespace ShopAssist
{
    internal static class CheckedModData
    {
        // Confirms the game's in-memory mod-data store accepted the value. The
        // game persists it later as part of its normal save, not at this call.
        internal static void Write(string key, string value)
        {
            ModHelper.SetModData(ModEntry.Id, key, value);
            if (ModHelper.GetModData(ModEntry.Id, key, null) != value)
                throw new InvalidOperationException("Mod data write was not retained: " + key);
        }
    }
}
