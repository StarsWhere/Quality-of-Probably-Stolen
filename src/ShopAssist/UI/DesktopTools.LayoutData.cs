using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShopAssist
{
    [Serializable]
    internal sealed class DesktopLayoutData
    {
        public int schemaVersion;
        public string runId;
        public List<DesktopLayout> layouts;
    }

    [Serializable]
    internal sealed class DesktopLayout
    {
        public int coordinatorId;
        public string members;
        public List<DesktopPosition> positions;
    }

    [Serializable]
    internal sealed class DesktopPosition
    {
        public int id;
        public float x;
        public float y;
        public int order;
    }

    internal static class DesktopLayoutRules
    {
        internal const int MaxGroups = 50;
        internal const int MaxMembers = 64;

        internal static string MembersKey(IEnumerable<int> ids) =>
            string.Join(",", ids.OrderBy(i => i).Select(i => i.ToString(CultureInfo.InvariantCulture)));

        internal static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= 1000000f;

        internal static bool IsValid(DesktopLayoutData data, Guid expectedRun)
        {
            if (data == null || data.schemaVersion != 1 || !Guid.TryParseExact(data.runId, "N", out Guid run) ||
                run != expectedRun || run == Guid.Empty || data.layouts == null || data.layouts.Count > MaxGroups) return false;
            var keys = new HashSet<string>();
            foreach (DesktopLayout layout in data.layouts)
            {
                if (layout == null || layout.coordinatorId <= 0 || string.IsNullOrEmpty(layout.members) ||
                    layout.positions == null || layout.positions.Count < 1 || layout.positions.Count > MaxMembers ||
                    !keys.Add(layout.coordinatorId + ":" + layout.members)) return false;
                var ids = new HashSet<int>();
                var orders = new HashSet<int>();
                foreach (DesktopPosition position in layout.positions)
                    if (position == null || position.id <= 0 || !ids.Add(position.id) || !Finite(position.x) ||
                        !Finite(position.y) || position.order < 0 || position.order >= layout.positions.Count ||
                        !orders.Add(position.order)) return false;
                if (layout.members != MembersKey(ids)) return false;
            }
            return true;
        }
    }
}
