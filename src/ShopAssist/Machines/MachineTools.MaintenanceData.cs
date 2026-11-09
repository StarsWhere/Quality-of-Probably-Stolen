using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private const int MaintenanceVersion = 5;

        internal sealed class MaintenanceData
        {
            [JsonRequired] public int version;
            [JsonRequired] public string run;
            [JsonRequired] public int slot;
            [JsonRequired] public List<MaintenanceRule> rules;
        }

        internal sealed class MaintenanceRule
        {
            [JsonRequired] public int kind, host, threshold;
            [JsonRequired] public string location;
            public int? condition;
            public int? materials, grade;
        }

        internal static MaintenanceData DecodeMaintenance(string raw, Guid run, int slot)
        {
            MaintenanceData data = raw == null ? new MaintenanceData { version = MaintenanceVersion, run = run.ToString("N"), slot = slot,
                rules = new List<MaintenanceRule>() } : ShopData.Decode<MaintenanceData>(raw, 65536);
            if (data == null || data.version != 3 && data.version != 4 && data.version != MaintenanceVersion ||
                !Guid.TryParseExact(data.run, "N", out Guid stored) || stored != run ||
                data.slot != slot || data.rules == null || data.rules.Count > 128 ||
                data.rules.Any(r => r == null || (r.kind != 31 && r.kind != 35) || r.host <= 0 ||
                    r.threshold < 0 || r.threshold > 100) ||
                data.rules.GroupBy(r => r.kind + ":" + r.host).Any(g => g.Count() != 1)) throw new FormatException("Invalid maintenance data");
            foreach (var rule in data.rules)
            {
                if (data.version == 3)
                {
                    rule.condition = rule.kind == 31 ? 2 : 0;
                    rule.threshold = rule.kind == 31 ? 0 : 20;
                }
                if (!rule.condition.HasValue || rule.condition < 0 || rule.condition > (rule.kind == 31 ? 2 : 1) ||
                    rule.kind == 31 && rule.threshold != 0)
                    throw new FormatException("Invalid replacement condition");
                if (!ParseMaintenanceSource(rule.location, rule.kind, out _, out _, out _))
                    throw new FormatException("Invalid maintenance source");
                if (rule.kind == 35 && (!rule.materials.HasValue || rule.materials < 0 || rule.materials > 7 ||
                    !rule.grade.HasValue || rule.grade < 0 || rule.grade > 2) ||
                    rule.kind == 31 && (rule.materials.HasValue || rule.grade.HasValue)) throw new FormatException("Invalid filter preferences");
            }
            data.version = MaintenanceVersion;
            return data;
        }

        private static bool ParseMaintenanceSource(string key, int kind, out int owner, out string role, out int page) =>
            MaintenanceSourceKey.TryParse(key, kind, out owner, out role, out page);
    }
}
