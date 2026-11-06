using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private const string AquaSenseId = "chiqingsan.AquaSense";
        private static readonly string[] FilterKinds = { "water_filter", "kotton_fabric", "hydroreed_filter" };
        private static int FilterBit(string id) => id == "water_filter" ? 1 : id == "kotton_fabric" ? 2 : id == "hydroreed_filter" ? 4 : 0;

        private static bool IsFilterHost(GameItem host) => host != null &&
            (host.identifier == "water_pitcher" || host.IsTag("RAS_TAG") && MachineRAS.GetFilterGrid(host) != null);

        private static bool EligibleFilter(GameItem item) => item != null && item.IsTag("WATER_FILTER") && FilterBit(item.identifier) != 0;
        private static bool AllowsFilter(MaintenanceRule rule, GameItem item) => EligibleFilter(item) &&
            (rule.materials.GetValueOrDefault() & FilterBit(item.identifier)) != 0;
        private static bool CompatibleFilter(MaintenanceRule rule, GameItem old, GameItem next)
        {
            if (!AllowsFilter(rule, next) || DurabilityHelper.GetRealDurability(next) <= 0) return false;
            if (old.identifier == next.identifier) return true;
            int previousSize = old.GetTagReadonly("FILTER_SIZE_TAG").GetInt(), nextSize = next.GetTagReadonly("FILTER_SIZE_TAG").GetInt();
            return previousSize > 0 && nextSize > 0 && (rule.grade == 2 || rule.grade == 1 && nextSize <= previousSize);
        }

        private static bool AquaOwns(GameItem host, string filterId)
        {
            if (host?.identifier != "water_pitcher" || !ShopPeers.Running(AquaSenseId)) return false;
            string key = filterId == "water_filter" ? "AutoReplaceFilters" :
                filterId == "kotton_fabric" ? "AutoReplaceCloth" : null;
            return key != null && ((ModConfig.Find(AquaSenseId)?.Get(key) as ConfigEntry<bool>)?.Value ?? true);
        }

        // AquaSense 1.9 observes two native methods even when its automatic replacement is off.
        // Admit only its verified postfix on that method. All unknown patches still veto the exchange.
        private static bool KnownWaterHooks(MethodInfo method, string aquaPostfix)
        {
            if (MaintenanceSwap.NoForeignPatches(method)) return true;
            var aqua = ShopPeers.Active(AquaSenseId);
            if (aqua?.Manifest.ModVersion != "1.9.0" || aquaPostfix == null) return false;
            var patches = Harmony.GetPatchInfo(method);
            string owner = "mod." + ModEntry.Id + ".f35";
            return patches != null && patches.Prefixes.All(p => p.owner == owner) &&
                patches.Finalizers.All(p => p.owner == owner) && patches.Transpilers.All(p => p.owner == owner) &&
                patches.Postfixes.All(p => p.owner == owner || (p.owner == "chiqingsan.water-scan-tooltip" &&
                    p.PatchMethod?.DeclaringType?.Assembly == aqua.Assembly && p.PatchMethod.Name == aquaPostfix));
        }

        private static List<GameSlotInventory> FilterSlots(GameItem host)
        {
            var result = new List<GameSlotInventory>(4);
            if (host?.identifier == "water_pitcher")
            {
                if (host.contentWindow?.childElement is GameSlotInventory slot) result.Add(slot);
            }
            else if (host?.IsTag("RAS_TAG") == true && MachineRAS.GetFilterGrid(host) is GridPixelElement grid)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (!(grid.GetElement(i, 0) is GameSlotInventory slot) || !slot.IsTag("RAS_FILTER_SLOT_TAG") ||
                        slot.GetParentItem() != host || result.Contains(slot)) { result.Clear(); break; }
                    result.Add(slot);
                }
            }
            return result;
        }

        private static void BeforeTankFilter(GameItem __0, GameItem __1, MethodBase __originalMethod, out GameItem __state)
        {
            __state = null;
            if (current?.filterEnabled.Value != true || __0 == null || !EligibleFilter(__1) ||
                !(__1.parentInventory is GameSlotInventory seat) || !seat.IsTag("RAS_FILTER_SLOT_TAG") || !ShopInventory.InStore) return;
            var host = seat.GetParentItem();
            if (!IsFilterHost(host) || !ShopInventory.Owned(host) || MachineRAS.GetWaterSlot(host)?.childItem != __0 || !FilterSlots(host).Contains(seat)) return;
            ActionAvailability capability = current.FilterCapability(host, __1, FilterPath.Tank);
            if (!capability.Allowed) { current.RememberMaintenanceFailure(host, 35, capability.ReasonKey, filter: __1); return; }
            int pore = __1.GetTagReadonly("FILTER_SIZE_TAG").GetInt();
            if (Liquid.Liquids.Any(liquid => liquid.identifier != "water" && liquid.identifier != "microbe" &&
                liquid.particleSize >= pore && __0.GetTagReadonly(liquid.currentPart).GetInt() > 0)) __state = host;
        }

        private static void AfterTankFilter(GameItem __1, GameItem __state)
        {
            if (current != null && __state != null && __1.parentInventory is GameSlotInventory seat && seat.childItem == __1)
                current.QueueFilter(__state, seat);
        }

        private void QueueFilter(GameItem host, GameSlotInventory seat)
        {
            GameItem filter = seat?.childItem;
            if (!EligibleFilter(filter) || DurabilityHelper.GetRealDurability(filter) != 0 || AquaOwns(host, filter.identifier) ||
                (pendingFilters.Count >= 128 && !pendingFilters.ContainsKey(seat))) return;
            pendingFilters[seat] = new PendingFilter { Host = host, Filter = filter, Parent = host.parentInventory, Seat = seat,
                Run = PlayerStore.Instance.runID, Slot = PlayerStore.Instance.saveSlotId };
        }
    }
}
