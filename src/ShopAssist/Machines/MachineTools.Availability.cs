using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private enum FilterPath { Any, Transfer, Added, Tank }
        private static readonly MethodBase[] BatteryMethods = {
            AccessTools.Method(typeof(MachineHelper), nameof(MachineHelper.CanPower), new[] { typeof(GameItem), typeof(GameSlotInventory) }),
            AccessTools.Method(typeof(PowerHelper), nameof(PowerHelper.DrawPowerSource)),
            AccessTools.Method(typeof(MachineHelper), nameof(MachineHelper.TryDrawCyclePower))
        };
        private static readonly MethodInfo FilterMethod = AccessTools.Method(typeof(WaterHelper), nameof(WaterHelper.HandleFilter));
        private static readonly MethodInfo TankFilterMethod = AccessTools.Method(typeof(AquacultureHelper), "RunFilter", new[] { typeof(GameItem), typeof(GameItem) });
        private static readonly MethodInfo TankCycleMethod = AccessTools.Method(typeof(AquacultureHelper), nameof(AquacultureHelper.TickTank));
        private static readonly MethodInfo TransferMethod = AccessTools.Method(typeof(WaterHelper), nameof(WaterHelper.TransferLiquid));
        private static readonly MethodInfo AddWaterMethod = AccessTools.Method(typeof(WaterHelper), nameof(WaterHelper.AddWater));

        internal static bool SupportsContextAction(string actionKey, GameItem host)
        {
            if (host == null || !ShopInventory.Owned(host)) return false;
            switch (actionKey)
            {
                case "f10.open": return FindWindowTarget(host.contentWindow, host, out _, out _);
                case "f31.open": return BatteryHost(host);
                case "f32.open": return host.identifier == "hydroponic" && host.IsTag("HYDROPONIC_MACHINE_TAG");
                case "f33.open": return host.identifier == "furnace";
                case "f35.open": return IsFilterHost(host);
                default: return false;
            }
        }

        internal static ActionAvailability Availability(string actionKey, GameItem host = null)
        {
            var owner = current;
            if (owner?.started != true) return ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "availability.unknown");
            if (!ShopInventory.InStore) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.store");
            switch (actionKey)
            {
                case "f32.open":
                    if (!owner.replantEnabled.Value) return ActionAvailability.Blocked(ActionUnavailableReason.Disabled, "availability.disabled", "f32.enabled");
                    if (host == null ? !FocusedHydroponic(out _) : !CanOpenReplantFor(host))
                        return ActionAvailability.Blocked(host == null ? ActionUnavailableReason.MissingSelection : ActionUnavailableReason.MissingObject, "f32.need_machine");
                    return ActionAvailability.Ready;
                case "f33.open":
                    if (!owner.feedEnabled.Value) return ActionAvailability.Blocked(ActionUnavailableReason.Disabled, "availability.disabled", "f33.enabled");
                    if (host == null ? !FocusedFurnace(out _) : !CanOpenFeedFor(host))
                        return ActionAvailability.Blocked(host == null ? ActionUnavailableReason.MissingSelection : ActionUnavailableReason.MissingObject, "f33.need_machine");
                    return ActionAvailability.Ready;
                case "f31.open":
                case "f35.open":
                    int kind = actionKey == "f31.open" ? 31 : 35;
                    string setting = kind == 31 ? "f31.enabled" : "f35.enabled";
                    if (!(kind == 31 ? owner.batteryEnabled.Value : owner.filterEnabled.Value))
                        return ActionAvailability.Blocked(ActionUnavailableReason.Disabled, "availability.disabled", setting);
                    if (host != null && (!ShopInventory.Owned(host) || !(kind == 31 ? BatteryHost(host) : IsFilterHost(host))))
                        return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "maintenance.host_unavailable");
                    if (!owner.ReadMaintenance(out _)) return ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "maintenance.invalid");
                    return ActionAvailability.Ready;
                default:
                    return ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "availability.unknown");
            }
        }

        private static ActionAvailability MethodCapability(params MethodBase[] methods)
        {
            if (methods.Any(method => method == null))
                return ActionAvailability.Blocked(ActionUnavailableReason.Unsupported, "maintenance.unsupported");
            if (methods.Any(method => !MaintenanceSwap.NoForeignPatches(method)))
                return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "maintenance.conflict");
            return ActionAvailability.Ready;
        }

        private ActionAvailability BatteryCapability(GameItem host, MethodBase callback = null)
        {
            if (batteryHookFailed || batteryHarmony == null)
                return ActionAvailability.Blocked(ActionUnavailableReason.Unsupported, "maintenance.unsupported");
            if (callback == null && host != null) batteryCycles.TryGetValue(host.identifier, out callback);
            var methods = new System.Collections.Generic.List<MethodBase>(BatteryMethods);
            if (host != null || callback != null) methods.Add(callback);
            else { methods.AddRange(batteryCycles.Values); if (batteryCycles.Count != 2) methods.Add(null); }
            ActionAvailability capability = MethodCapability(methods.ToArray());
            return capability.Allowed ? MaintenanceSwap.MethodCapability() : capability;
        }

        private ActionAvailability FilterCapability(GameItem host, FilterPath path)
        {
            if (filterHookFailed || filterHarmony == null)
                return ActionAvailability.Blocked(ActionUnavailableReason.Unsupported, "maintenance.unsupported");
            ActionAvailability swap = MaintenanceSwap.MethodCapability();
            if (!swap.Allowed || host == null) return swap;
            ActionAvailability handle = MethodCapability(FilterMethod);
            if (!handle.Allowed) return handle;
            if (host.identifier != "water_pitcher")
                return MethodCapability(TankFilterMethod, TankCycleMethod);
            ActionAvailability transfer = MethodCapability(TransferMethod);
            ActionAvailability added = MethodCapability(AddWaterMethod);
            if (path == FilterPath.Transfer) return transfer;
            if (path == FilterPath.Added) return added;
            return transfer.Allowed || added.Allowed ? ActionAvailability.Ready : transfer;
        }

    }
}
