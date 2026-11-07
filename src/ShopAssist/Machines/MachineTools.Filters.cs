using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
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

        private enum FilterOperation { Transfer, Fill, Tank }
        private sealed class FilterFrame
        {
            internal FilterOperation Operation;
            internal GameItem Host, Source, Filter;
            internal GameSlotInventory Seat;
            internal List<GameSlotInventory> Seats;
            internal int Before;
            internal bool Observed, Invalid;
            internal readonly Dictionary<GameSlotInventory, GameItem> Processed = new Dictionary<GameSlotInventory, GameItem>();
        }
        private sealed class FilterCandidate
        {
            internal GameItem Host, Filter;
            internal GameSlotInventory Seat;
        }
        [ThreadStatic] private static FilterProcessingTracker<FilterFrame, FilterCandidate> filterProcessing;

        private static FilterProcessingTracker<FilterFrame, FilterCandidate>.Scope BeginFilterFrame(FilterOperation operation)
        {
            if (current?.started != true || !current.filterEnabled.Value) return null;
            if (filterProcessing == null) filterProcessing = new FilterProcessingTracker<FilterFrame, FilterCandidate>();
            return filterProcessing.Enter(new FilterFrame { Operation = operation });
        }

        private static void BeginFilterTransfer(GameItem __0, GameItem __1,
            out FilterProcessingTracker<FilterFrame, FilterCandidate>.Scope __state)
        {
            __state = BeginFilterFrame(FilterOperation.Transfer);
            PreparePitcher(__state, __1, __0);
        }

        private static void BeginFilterFill(GameItem __0,
            out FilterProcessingTracker<FilterFrame, FilterCandidate>.Scope __state)
        {
            __state = BeginFilterFrame(FilterOperation.Fill);
            PreparePitcher(__state, __0, null);
        }

        private static void PreparePitcher(FilterProcessingTracker<FilterFrame, FilterCandidate>.Scope scope, GameItem host, GameItem source)
        {
            if (scope == null || host?.identifier != "water_pitcher" || !ShopInventory.InStore) return;
            FilterFrame frame = scope.Frame;
            frame.Host = host;
            try
            {
                if (!ShopInventory.Owned(host) || !(host.contentWindow?.childElement is GameSlotInventory seat)) return;
                GameItem filter = seat.childItem;
                if (!EligibleFilter(filter)) return;
                ActionAvailability capability = current.FilterCapability(host, source == null ? FilterPath.Added : FilterPath.Transfer);
                if (!capability.Allowed) { current.RememberMaintenanceFailure(host, 35, capability.ReasonKey, filter: filter); return; }
                frame.Seat = seat; frame.Filter = filter; frame.Source = source;
                frame.Before = WaterHelper.GetCurrentCapacityUL(source ?? host);
            }
            catch (Exception error) { FilterObservationFailed(frame, error); }
        }

        private static void BeginTankProcessing(GameItem __0, GameItem __2, List<GameSlotInventory> __3, bool __5,
            out FilterProcessingTracker<FilterFrame, FilterCandidate>.Scope __state)
        {
            __state = BeginFilterFrame(FilterOperation.Tank);
            if (__state == null || !__5 || __0 == null || __2 == null || __3 == null || !ShopInventory.InStore) return;
            FilterFrame frame = __state.Frame;
            frame.Host = __0;
            try
            {
                if (!IsFilterHost(__0) || !ShopInventory.Owned(__0) || MachineRAS.GetWaterSlot(__0)?.childItem != __2) return;
                ActionAvailability capability = current.FilterCapability(__0, FilterPath.Tank);
                if (!capability.Allowed) { current.RememberMaintenanceFailure(__0, 35, capability.ReasonKey); return; }
                var actual = FilterSlots(__0);
                frame.Seats = __3.Where(seat => actual.Contains(seat)).ToList();
                frame.Source = __2;
            }
            catch (Exception error) { FilterObservationFailed(frame, error); }
        }

        // The native filter changes its amount argument while processing it.
        // Preserve the incoming volume so a completely filtered pour is still observed.
        private static void BeforeFilterProcessing(int __1, out int __state) => __state = __1;

        private static void ObserveFilterProcessing(int __state, GameItem __2, GameItem __3)
        {
            if (__state <= 0 || __3 == null || filterProcessing?.Current == null) return;
            try
            {
                for (var scope = filterProcessing.Current; scope != null; scope = scope.Parent)
                {
                    FilterFrame frame = scope.Frame;
                    if (frame.Invalid) continue;
                    if (frame.Operation == FilterOperation.Tank)
                    {
                        if (frame.Source != __2 || !(__3.parentInventory is GameSlotInventory seat) ||
                            frame.Seats == null || !frame.Seats.Contains(seat) || seat.childItem != __3) continue;
                        frame.Processed[seat] = __3;
                        break;
                    }
                    if (frame.Filter == __3 && frame.Seat?.childItem == __3 && frame.Source == __2)
                    { frame.Observed = true; break; }
                }
            }
            catch (Exception error) { FilterObservationFailed(filterProcessing.Current.Frame, error); }
        }

        private static Exception EndFilterProcessing(Exception __exception,
            FilterProcessingTracker<FilterFrame, FilterCandidate>.Scope __state)
        {
            if (__state == null) return __exception;
            FilterFrame frame = __state.Frame;
            try
            {
                if (__exception == null && !frame.Invalid)
                {
                    if (frame.Operation == FilterOperation.Tank)
                    {
                        foreach (var pair in frame.Processed)
                            __state.Items.Add(new FilterCandidate { Host = frame.Host, Seat = pair.Key, Filter = pair.Value });
                    }
                    else if (frame.Seat != null && (frame.Operation == FilterOperation.Transfer
                        ? FilterProcessingRules.Transferred(frame.Observed, frame.Before, WaterHelper.GetCurrentCapacityUL(frame.Source))
                        : FilterProcessingRules.Filled(frame.Observed, frame.Before, WaterHelper.GetCurrentCapacityUL(frame.Host))))
                        __state.Items.Add(new FilterCandidate { Host = frame.Host, Seat = frame.Seat, Filter = frame.Filter });
                }
                else if (__exception != null && frame.Host != null)
                    FilterObservationFailed(frame, __exception);
                filterProcessing.Complete(__state, __exception == null && !frame.Invalid, candidate =>
                {
                    try { if (candidate.Seat?.childItem == candidate.Filter) current?.QueueFilter(candidate.Host, candidate.Seat); }
                    catch (Exception error) { FilterObservationFailed(new FilterFrame { Host = candidate.Host, Filter = candidate.Filter }, error); }
                });
            }
            catch (Exception error)
            {
                FilterObservationFailed(frame, error);
                // Ensure an observation error does not change the native operation's exception.
                if (filterProcessing?.Current == __state)
                    filterProcessing.Complete(__state, false, _ => { });
            }
            return __exception;
        }

        private static void FilterObservationFailed(FilterFrame frame, Exception error)
        {
            frame.Invalid = true;
            current?.log("F35 could not track filter processing: " + error);
            try { if (frame.Host != null) current?.RememberMaintenanceFailure(frame.Host, 35, "f35.inspect", true, frame.Filter); }
            catch (Exception reportingError) { current?.log("F35 could not report the processing failure: " + reportingError); }
        }

        private void QueueFilter(GameItem host, GameSlotInventory seat)
        {
            GameItem filter = seat?.childItem;
            if (!EligibleFilter(filter) || DurabilityHelper.GetRealDurability(filter) != 0) return;
            pendingFilters[seat] = new PendingFilter { Host = host, Filter = filter, Parent = host.parentInventory, Seat = seat,
                Run = PlayerStore.Instance.runID, Slot = PlayerStore.Instance.saveSlotId };
            machinesDirty = true; machineAttentionDirty = true;
        }
    }
}
