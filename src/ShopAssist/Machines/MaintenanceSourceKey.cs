using System;

namespace ShopAssist
{
    internal static class MaintenanceSourceKey
    {
        internal static bool TryParse(string key, int kind, out int owner, out string role, out int page)
        {
            owner = 0; role = null; page = -1;
            if (kind != 31 && kind != 32 && kind != 35) return false;
            if (key == "root:stock") { role = "stock"; return true; }
            if (string.IsNullOrEmpty(key) || key.Length > 160) return false;
            string[] parts = key.Split(':');
            if (parts.Length != 4 || parts[0] != "item" || !int.TryParse(parts[1], out owner) || owner <= 0 ||
                !int.TryParse(parts[3], out page) || page < -1 || page > 4) return false;
            role = parts[2];
            return (role == "contents" || role == "storage" || kind == 31 && role == "batteries") &&
                key == "item:" + owner + ":" + role + ":" + page;
        }

    }
}
