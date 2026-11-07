using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;

namespace ShopAssist
{
    internal sealed class TripCargo : IDisposable
    {
        private const string Id = ModEntry.Id + ".cargo";
        private const string DataKey = "tripCargo";
        private static TripCargo active;

        private sealed class Trip
        {
            [JsonRequired] public int version = 3, slot, bag, returnBag;
            [JsonRequired] public string run;
            [JsonRequired] public bool started, returned, uncertain;
            [JsonRequired] public List<Goods> outward = new List<Goods>();
            [JsonRequired] public List<Goods> gains = new List<Goods>();
            [JsonRequired] public string dest = "root:stock";
        }
        private sealed class Goods
        {
            [JsonRequired] public int id, qty;
            [JsonRequired] public string type;
            [JsonRequired] public bool manual;
        }
        private sealed class Seen
        {
            internal GameItem Item;
            internal GameInventory Parent;
            internal int Id, Quantity;
            internal string Type;
        }
        private sealed class Trace
        {
            internal GameItem Item;
            internal int Quantity;
            internal string Type;
            internal CargoOrigin Origin;
        }
        private sealed class Transfer
        {
            internal GameItem Source, Target;
            internal GameInventory From, To;
            internal int SourceCount, TargetCount;
            internal string SourceType, TargetType;
            internal CargoOrigin SourceOrigin, TargetOrigin;
        }
        private sealed class MarkerWatch { internal Transfer Transfer; internal bool Finished; }
        private sealed class Found
        {
            internal GameItem Item;
            internal ShopInventory.Location Location;
            internal string Name;
        }
        private sealed class UnloadMove
        {
            internal int Id, Quantity;
            internal string Type;
            internal ShopInventory.Location Source;
            internal WholeStackTransfer.Plan Transfer;
        }
        private sealed class UnloadSkip
        {
            internal int Id, Quantity;
            internal string Type, Reason, Location;
            internal GameItem Item;
        }
        private sealed class UnloadPlan
        {
            internal string Run;
            internal int Slot;
            internal ShopInventory.Location Target;
            internal readonly List<UnloadMove> Moves = new List<UnloadMove>();
            internal readonly List<UnloadSkip> Skipped = new List<UnloadSkip>();
        }

        private readonly ShopSettings settings;
        private readonly ConfigEntry<bool> enabled;
        private readonly Action<string> log;
        private readonly HashSet<int> selected = new HashSet<int>();
        private readonly Dictionary<int, Trace> traces = new Dictionary<int, Trace>();
        private readonly Dictionary<int, Seen> observed = new Dictionary<int, Seen>();
        private readonly HashSet<GameItem> returningItems = new HashSet<GameItem>();
        private CustomUIWindow window;
        private string run;
        private int slot, page, transferDepth;
        private Trip trip;
        private bool continuous, returning, lastEnabled;

        internal static bool AppliesTo(GameItem item) => active != null && item != null && item.IsTag("BACKPACK_TAG") &&
            ShopInventory.InStore && ShopInventory.Owned(item) && (!active.Load() || active.trip.returned &&
            item.uniqueId == (active.trip.returnBag > 0 ? active.trip.returnBag : active.trip.bag));

        internal static bool IsReturnedCargoInCounter(GameItem item)
        {
            if (active == null || item?.parentInventory == null || !ShopInventory.InStore ||
                PlayerStore.Instance.currentClientInstance != null || !active.Load() || !active.trip.returned) return false;
            GameItem bag = item.parentInventory.GetParentItem();
            var counter = EmporiumEntry.Instance?.backInvinvElement;
            return bag != null && bag.uniqueId == active.trip.returnBag && counter != null && bag.parentInventory == counter &&
                counter.childItems.Contains(bag) && Grid(bag) == item.parentInventory && ShopInventory.Owned(item) &&
                active.Confirmed(item) &&
                active.trip.gains.Any(g => !g.manual && g.id == item.uniqueId && g.type == item.identifier && g.qty == item.unitCount);
        }

        internal static ActionAvailability AvailabilityFor(GameItem item)
        {
            if (active == null) return ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "availability.unknown");
            if (!active.enabled.Value) return ActionAvailability.Blocked(ActionUnavailableReason.Disabled, "availability.disabled", "f25.enabled");
            if (!ShopInventory.InStore) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.store");
            if (!active.Load()) return ShopInventory.Owned(item) && item.IsTag("BACKPACK_TAG") ? ActionAvailability.Ready :
                ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "f25.record_failed");
            if (!AppliesTo(item)) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "f25.bagmissing");
            if (!WholeStackTransfer.Ready) return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "f08.pending");
            if (active.trip.gains.Count == 0 && !active.trip.uncertain)
                return ActionAvailability.Blocked(ActionUnavailableReason.MissingResource, "f25.none");
            return ActionAvailability.Ready;
        }
        internal static void OpenFor(GameItem item) { if (AvailabilityFor(item).Allowed) active.Draw(); }

        internal TripCargo(ShopSettings settings, Action<string> log)
        {
            this.settings = settings;
            this.log = log;
            enabled = settings.Toggle("f25.enabled", "f25.tip");
            lastEnabled = enabled.Value;
            settings.Action("f25.open", Draw, () => enabled.Value && ShopInventory.InStore);
        }
        internal void Start()
        {
            active = this;
            WholeStackTransfer.Acquire();
            WholeStackTransfer.ResolveTrip = ResolvePending;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Close;
            settings.Changed += Changed;
        }
        private void Changed()
        {
            bool disabled = lastEnabled && !enabled.Value;
            lastEnabled = enabled.Value;
            if (enabled.Value) return;
            Close();
            if (!disabled || trip == null || !trip.started || trip.returned && trip.gains.Count == 0 && !trip.uncertain) return;
            LoseContinuity();
            foreach (Goods gain in trip.gains) gain.manual = true;
            try { Save(); }
            catch (Exception error) { RecordFailed(error); }
        }
        private void Close() { if (window != null && window.IsAlive) window.Close(); window = null; selected.Clear(); }
        private void Reset()
        {
            Close(); trip = null; run = null; page = 0; continuous = returning = false;
            transferDepth = 0; traces.Clear(); observed.Clear(); returningItems.Clear();
        }
        private static GameGridInventory Grid(GameItem bag) => ShopInventory.SafeContainerForPeek(bag) && bag.IsTag("BACKPACK_TAG")
            ? bag.contentWindow.childElement as GameGridInventory : null;

        private bool Load()
        {
            if (!PlayerStore.HasInstance) return false;
            var p = PlayerStore.Instance;
            if (p.saveSlotId < 0 || string.IsNullOrEmpty(p.runID) || p.ModData() == null) return false;
            if (p.runID == run && p.saveSlotId == slot) return trip != null;
            Reset(); run = p.runID; slot = p.saveSlotId;
            try
            {
                var raw = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                trip = raw == null ? new Trip { run = run, slot = slot } : ShopData.Decode<Trip>(raw, 131072);
                if (trip == null || trip.version != 3 || trip.run != run || trip.slot != slot || trip.bag < 0 || trip.returnBag < 0 || trip.dest == null ||
                    !Valid(trip.outward) || !Valid(trip.gains)) throw new FormatException("Invalid trip cargo");
                // In-memory mod data is not a durable transaction log. A loaded outing or
                // unprocessed return cannot prove that the saved world and record agree.
                if (raw != null && trip.started && (!trip.returned || trip.gains.Count > 0 || trip.uncertain))
                {
                    trip.uncertain = true;
                    foreach (Goods gain in trip.gains) gain.manual = true;
                }
            }
            catch (Exception e) { trip = null; log("F25: unreadable trip book retained. " + e.Message); }
            return trip != null;
        }
        private static bool Valid(List<Goods> list) => list != null && list.Count <= 512 &&
            list.All(g => g != null && g.id > 0 && g.qty > 0 && !string.IsNullOrEmpty(g.type)) &&
            list.Select(g => g.id).Distinct().Count() == list.Count;
        private void Save()
        {
            if (!PlayerStore.HasInstance || PlayerStore.Instance.runID != run || PlayerStore.Instance.saveSlotId != slot ||
                PlayerStore.Instance.ModData() == null || trip == null || trip.run != run || trip.slot != slot ||
                !Valid(trip.outward) || !Valid(trip.gains))
                throw new InvalidOperationException("Trip cargo scope or contents changed");
            try { CheckedModData.Write(DataKey, ShopData.Encode(trip)); }
            catch { trip = null; run = null; continuous = false; throw; }
        }
        private bool ResolvePending(int sourceId)
        {
            if (!Load()) return false;
            Goods gain = trip.gains.Find(value => value.id == sourceId);
            if (gain == null) return true;
            trip.gains.Remove(gain);
            try { Save(); selected.Remove(sourceId); if (window?.IsAlive == true) Draw(); return true; }
            catch (Exception error) { log("F25 could not resolve pending cargo: " + error); return false; }
        }
        private void RecordFailed(Exception error)
        {
            if (error != null) log("F25 trip record failed: " + error);
            ShopNotice.Show(ShopSettings.Text("f25.record_failed"));
        }

        // Only the native outing slots and accessible ordinary child containers are
        // included. Closed or special containers cannot establish item provenance.
        private static List<Seen> Carried(out HashSet<GameInventory> inventories, out bool complete)
        {
            inventories = new HashSet<GameInventory>();
            var result = new List<Seen>();
            var visited = new HashSet<GameItem>();
            var e = EmporiumEntry.Instance;
            complete = e != null && e.afterhourPocketSlotInvLeft != null && e.afterhourPocketSlotInvRight != null &&
                e.afterhourPocketSlotInvBackpack != null;
            if (!complete) return result;
            var pending = new Queue<GameInventory>();
            pending.Enqueue(e.afterhourPocketSlotInvLeft);
            pending.Enqueue(e.afterhourPocketSlotInvRight);
            GameItem bag = e.afterhourPocketSlotInvBackpack.childItem;
            if (bag != null)
            {
                var grid = ShopInventory.OutingBag(bag);
                if (grid == null) complete = false; else pending.Enqueue(grid);
            }
            while (pending.Count > 0)
            {
                GameInventory inventory = pending.Dequeue();
                if (inventory == null || !inventories.Add(inventory)) continue;
                foreach (GameItem item in inventory.childItems.ToArray())
                {
                    if (item == null || item.parentInventory != inventory || !ShopInventory.Owned(item) || !visited.Add(item))
                    { complete = false; continue; }
                    int id = item.GetUniqueID();
                    if (id <= 0 || item.unitCount <= 0 || string.IsNullOrEmpty(item.identifier) || result.Count >= 512)
                    { complete = false; continue; }
                    result.Add(new Seen { Item = item, Parent = inventory, Id = id, Quantity = item.unitCount, Type = item.identifier });
                    if (item.contentWindow == null) continue;
                    if (ShopInventory.SafeContainerForPeek(item) && item.contentWindow.childElement is GameGridInventory child)
                        pending.Enqueue(child);
                    else complete = false;
                }
            }
            if (result.Select(s => s.Id).Distinct().Count() != result.Count) complete = false;
            return result;
        }
        private void LoseContinuity()
        {
            continuous = false;
            if (trip != null) trip.uncertain = true;
            foreach (Trace trace in traces.Values) if (trace.Origin == CargoOrigin.New) trace.Origin = CargoOrigin.Unknown;
        }
        private void Remember(Seen value, CargoOrigin origin)
        {
            traces[value.Id] = new Trace { Item = value.Item, Quantity = value.Quantity, Type = value.Type, Origin = origin };
        }
        private void SetTrace(GameItem item, CargoOrigin origin)
        {
            if (item == null || item.unitCount <= 0 || string.IsNullOrEmpty(item.identifier)) return;
            int id = item.GetUniqueID();
            if (id <= 0) { LoseContinuity(); return; }
            if (traces.TryGetValue(id, out var old) && !ReferenceEquals(old.Item, item))
            { origin = CargoOrigin.Unknown; LoseContinuity(); }
            traces[id] = new Trace { Item = item, Quantity = item.unitCount, Type = item.identifier, Origin = origin };
        }
        private CargoOrigin Origin(GameItem item)
        {
            if (item == null || item.uniqueId <= 0 || !traces.TryGetValue(item.uniqueId, out var value) ||
                !ReferenceEquals(value.Item, item) || value.Quantity != item.unitCount || value.Type != item.identifier)
                return CargoOrigin.Unknown;
            return value.Origin;
        }
        private static bool Same(Seen a, Seen b) => a != null && b != null && ReferenceEquals(a.Item, b.Item) &&
            ReferenceEquals(a.Parent, b.Parent) && a.Quantity == b.Quantity && a.Type == b.Type;
        private bool Confirmed(GameItem item) => item != null && Origin(item) == CargoOrigin.New;
        private static bool NativeTrackingSafe() => InventoryAutoInsert.KnownMutationPatches() &&
            InventoryAutoInsert.KnownPatches(AccessTools.Method(typeof(GameItem), nameof(GameItem.GetSplitItem))) &&
            InventoryAutoInsert.KnownPatches(AccessTools.Method(typeof(EmporiumEntry), nameof(EmporiumEntry.TransferBackpackToInv))) &&
            InventoryAutoInsert.KnownPatches(AccessTools.Method(typeof(EmporiumEntry), nameof(EmporiumEntry.TransferAfterhourToInv))) &&
            InventoryAutoInsert.KnownPatches(AccessTools.Method(typeof(GraphUtils), nameof(GraphUtils.TryAccept), new[] { typeof(GraphNodeStorage), typeof(GameItem) }));
        private void Reconcile(IEnumerable<GameItem> changed = null)
        {
            var except = new HashSet<GameItem>(changed ?? Enumerable.Empty<GameItem>());
            List<Seen> current = Carried(out _, out bool complete);
            var now = current.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            if (!complete) LoseContinuity();
            foreach (Seen previous in observed.Values)
                if (!except.Contains(previous.Item) && (!now.TryGetValue(previous.Id, out var live) || !Same(previous, live))) LoseContinuity();
            foreach (Seen live in current)
            {
                if (!observed.TryGetValue(live.Id, out var prior) && !except.Contains(live.Item))
                { LoseContinuity(); Remember(live, CargoOrigin.Unknown); }
                else if (!except.Contains(live.Item) && !Same(prior, live)) Remember(live, CargoOrigin.Unknown);
            }
            observed.Clear();
            foreach (Seen live in current) observed[live.Id] = live;
        }
        private bool Tracking => trip != null && trip.started && !trip.returned && (returning || continuous && enabled.Value) &&
            PlayerStore.HasInstance && PlayerStore.Instance.runID == run && PlayerStore.Instance.saveSlotId == slot;
        private Transfer Capture(GameItem source, GameItem target, GameInventory to)
        {
            if (!Tracking || source == null || source.unitCount <= 0) return null;
            var inventories = new HashSet<GameInventory>();
            if (returning)
            {
                if (!returningItems.Contains(source) && (source.uniqueId <= 0 || !traces.ContainsKey(source.uniqueId))) return null;
            }
            else
            {
                Carried(out inventories, out bool complete);
                if (!complete || (EmporiumEntry.Instance.afterhourPocketSlotInvBackpack.childItem?.uniqueId ?? 0) != trip.bag)
                    LoseContinuity();
                Reconcile();
            }
            bool fromGround = ReferenceEquals(source.parentInventory, EmporiumEntry.Instance.afterhourInventory);
            bool relevant = returning || inventories.Contains(source.parentInventory) || inventories.Contains(to) ||
                (source.uniqueId > 0 && traces.ContainsKey(source.uniqueId));
            if (!relevant) return null;
            if (!NativeTrackingSafe()) LoseContinuity();
            CargoOrigin sourceOrigin = Origin(source);
            Trace known = null;
            bool tracked = source.uniqueId > 0 && traces.TryGetValue(source.uniqueId, out known);
            if (tracked && !ReferenceEquals(known.Item, source)) { LoseContinuity(); sourceOrigin = CargoOrigin.Unknown; }
            if (fromGround && inventories.Contains(to) && !tracked && TripCargoRules.CanTrustGround(continuous, false, CargoOrigin.Unknown))
                sourceOrigin = CargoOrigin.New;
            return new Transfer { Source = source, Target = target, From = source.parentInventory, To = to,
                SourceCount = source.unitCount, TargetCount = target?.unitCount ?? 0,
                SourceType = source.identifier, TargetType = target?.identifier,
                SourceOrigin = sourceOrigin, TargetOrigin = Origin(target) };
        }
        private void Completed(Transfer value, GameItem placed, int amount)
        {
            if (value == null) return;
            if (amount <= 0)
            {
                if (value.Source.unitCount != value.SourceCount || value.Source.parentInventory != value.From ||
                    value.Target != null && value.Target.unitCount != value.TargetCount) LoseContinuity();
                if (!returning) Reconcile();
                return;
            }
            bool valid = value.Source.identifier == value.SourceType && amount <= value.SourceCount;
            if (value.Target != null)
            {
                valid &= value.Target.identifier == value.TargetType && value.Target.unitCount == (long)value.TargetCount + amount &&
                    value.Source.unitCount == value.SourceCount - amount && value.Target.parentInventory == value.To;
                if (valid)
                {
                    SetTrace(value.Source, value.SourceOrigin);
                    SetTrace(value.Target, TripCargoRules.Merge(value.SourceOrigin, value.TargetOrigin));
                    if (returning) returningItems.Add(value.Target);
                }
            }
            else
            {
                bool split = amount < value.SourceCount;
                valid &= placed != null && placed.identifier == value.SourceType && placed.unitCount == amount &&
                    placed.parentInventory == value.To && value.To != null && value.To.childItems.Contains(placed) &&
                    (split ? !ReferenceEquals(placed, value.Source) && value.Source.unitCount == value.SourceCount - amount :
                        ReferenceEquals(placed, value.Source));
                if (valid)
                {
                    SetTrace(value.Source, value.SourceOrigin);
                    SetTrace(placed, value.SourceOrigin);
                    if (returning) returningItems.Add(placed);
                }
            }
            if (!valid)
            {
                LoseContinuity(); SetTrace(value.Source, CargoOrigin.Unknown); SetTrace(value.Target, CargoOrigin.Unknown);
                SetTrace(placed, CargoOrigin.Unknown);
            }
            if (!returning) Reconcile(new[] { value.Source, value.Target, placed });
        }

        [HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce))]
        private static class Moved
        {
            private static void Prefix(SlotMarker __instance, out MarkerWatch __state)
            {
                __state = null;
                var a = active;
                if (a == null || !a.Tracking) return;
                try
                {
                    __state = new MarkerWatch();
                    if (a.transferDepth++ != 0) { a.LoseContinuity(); return; }
                    __state.Transfer = a.Capture(__instance.item, __instance.targetItem, __instance.targetItem?.parentInventory ?? __instance.inventory);
                }
                catch (Exception error) { a.LoseContinuity(); a.log("F25 capture failed: " + error); }
            }
            private static void Postfix(SlotMarker __instance, MarkerWatch __state, int __result)
            {
                var a = active;
                if (a == null || __state == null || __state.Finished) return;
                try { if (__state.Transfer != null) a.Completed(__state.Transfer, __instance.item, __result); }
                catch (Exception error) { a.LoseContinuity(); a.log("F25 transfer tracking failed: " + error); }
                finally { __state.Finished = true; a.transferDepth = Math.Max(0, a.transferDepth - 1); }
            }
            private static Exception Finalizer(MarkerWatch __state, Exception __exception)
            {
                var a = active;
                if (__exception != null && a != null && __state != null && !__state.Finished)
                { a.transferDepth = Math.Max(0, a.transferDepth - 1); a.LoseContinuity(); }
                return __exception;
            }
        }

        [HarmonyPatch(typeof(GameItem), nameof(GameItem.GetSplitItem))]
        private static class SplitOutsideTransfer
        {
            private static void Prefix(GameItem __instance, out Transfer __state)
            {
                var a = active; __state = null;
                if (a == null || !a.Tracking || a.transferDepth != 0) return;
                try { __state = a.Capture(__instance, null, null); }
                catch (Exception error) { a.LoseContinuity(); a.log("F25 split capture failed: " + error); }
            }
            private static void Postfix(GameItem __result, Transfer __state)
            {
                var a = active;
                if (a == null || __state == null) return;
                try
                {
                    if (__result == null || __result.unitCount <= 0 || __result.identifier != __state.SourceType ||
                        __state.Source.unitCount != __state.SourceCount - __result.unitCount)
                    { a.LoseContinuity(); a.SetTrace(__state.Source, CargoOrigin.Unknown); a.SetTrace(__result, CargoOrigin.Unknown); }
                    else { a.SetTrace(__state.Source, __state.SourceOrigin); a.SetTrace(__result, __state.SourceOrigin); }
                    if (!a.returning) a.Reconcile(new[] { __state.Source, __result });
                }
                catch (Exception error) { a.LoseContinuity(); a.log("F25 split tracking failed: " + error); }
            }
        }

        [HarmonyPatch(typeof(SlotMarker), "StackItemUnchecked")]
        private static class StackOutsideTransfer
        {
            private static void Prefix(GameItem item, GameItem targetItem, int amount, out Transfer __state)
            {
                var a = active; __state = null;
                if (a == null || !a.Tracking || a.transferDepth != 0) return;
                try { __state = a.Capture(item, targetItem, targetItem?.parentInventory); }
                catch (Exception error) { a.LoseContinuity(); a.log("F25 stack capture failed: " + error); }
            }
            private static void Postfix(Transfer __state, int amount)
            {
                var a = active;
                if (a == null || __state == null) return;
                try { a.Completed(__state, __state.Source, amount < 0 ? __state.SourceCount : amount); }
                catch (Exception error) { a.LoseContinuity(); a.log("F25 stack tracking failed: " + error); }
            }
        }

        [HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.TransferBackpackToInv))]
        private static class Departure
        {
            private static void Postfix(EmporiumEntry __instance)
            {
                var a = active;
                if (a == null) return;
                try
                {
                    if (!a.Load()) { if (a.enabled.Value) a.RecordFailed(null); return; }
                    if (WholeStackTransfer.PendingFor("f25.open"))
                    {
                        if (a.trip.started)
                        {
                            a.LoseContinuity();
                            foreach (Goods gain in a.trip.gains) gain.manual = true;
                            a.Save();
                        }
                        if (a.enabled.Value) ShopNotice.Show(ShopSettings.Text("f25.pending_departure")); return;
                    }
                    if (a.trip.returned && (a.trip.gains.Count > 0 || a.trip.uncertain))
                    {
                        a.LoseContinuity();
                        foreach (Goods gain in a.trip.gains) gain.manual = true;
                        a.Save();
                        if (a.enabled.Value) ShopNotice.Show(ShopSettings.Text("f25.pending_departure"));
                        return;
                    }
                    var bag = __instance.afterhourPocketSlotInvBackpack?.childItem;
                    string destination = a.trip.dest;
                    a.trip = new Trip { run = a.run, slot = a.slot, started = true, bag = bag?.GetUniqueID() ?? 0, dest = destination };
                    a.traces.Clear(); a.observed.Clear(); a.returningItems.Clear();
                    a.continuous = a.enabled.Value && NativeTrackingSafe();
                    var before = Carried(out _, out bool complete);
                    if (!complete || !a.continuous) a.LoseContinuity();
                    foreach (Seen item in before)
                    {
                        a.Remember(item, CargoOrigin.Old); a.observed[item.Id] = item;
                        a.trip.outward.Add(new Goods { id = item.Id, type = item.Type, qty = item.Quantity });
                    }
                    a.Save();
                }
                catch (Exception error) { a.RecordFailed(error); }
            }
        }

        [HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.TransferAfterhourToInv))]
        private static class Arrival
        {
            private static void Prefix(EmporiumEntry __instance, out bool __state)
            {
                __state = false;
                var a = active;
                if (a == null) return;
                try
                {
                    if (!a.Load()) { if (a.enabled.Value) a.RecordFailed(null); return; }
                    if (WholeStackTransfer.PendingFor("f25.open") || a.trip.returned) return;
                    if (!a.trip.started) { a.trip.started = true; a.LoseContinuity(); }
                    if (!NativeTrackingSafe()) a.LoseContinuity();
                    if (!a.Tracking) a.LoseContinuity();
                    else a.Reconcile();
                    var carry = Carried(out _, out bool complete);
                    if (!complete || (__instance.afterhourPocketSlotInvBackpack?.childItem?.uniqueId ?? 0) != a.trip.bag)
                        a.LoseContinuity();
                    if (!a.continuous)
                        foreach (Seen item in carry) a.Remember(item, CargoOrigin.Unknown);
                    a.returningItems.Clear();
                    foreach (Seen item in carry) a.returningItems.Add(item.Item);
                    a.trip.returnBag = __instance.afterhourPocketSlotInvBackpack?.childItem?.GetUniqueID() ?? 0;
                    a.returning = true; __state = true;
                }
                catch (Exception error) { a.returning = false; a.LoseContinuity(); a.RecordFailed(error); }
            }
            private static void Postfix(bool __state)
            {
                var a = active;
                if (a == null || !__state) return;
                try
                {
                    a.returning = false;
                    a.trip.gains.Clear();
                    foreach (GameItem item in a.returningItems)
                    {
                        if (item == null || item.unitCount <= 0 || string.IsNullOrEmpty(item.identifier)) continue;
                        CargoOrigin origin = a.Origin(item);
                        if (!a.continuous) origin = CargoOrigin.Unknown;
                        if (origin == CargoOrigin.Old) continue;
                        int id = item.GetUniqueID();
                        if (id <= 0 || a.trip.gains.Any(g => g.id == id) || a.trip.gains.Count >= 512)
                        { a.LoseContinuity(); continue; }
                        a.trip.gains.Add(new Goods { id = id, type = item.identifier, qty = item.unitCount, manual = origin != CargoOrigin.New });
                    }
                    a.trip.returned = true;
                    a.Save();
                }
                catch (Exception error) { a.RecordFailed(error); }
                finally { a.returning = false; a.returningItems.Clear(); }
            }
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception != null && active != null) { active.returning = false; active.LoseContinuity(); }
                return __exception;
            }
        }

        private static List<ShopInventory.Location> Locations() => ShopInventory.OwnedLocations(false);
        private static Found Find(Goods gain, List<ShopInventory.Location> locations)
        {
            foreach (ShopInventory.Location location in locations)
                foreach (GameItem item in location.Inventory.childItems)
                    if (item.uniqueId == gain.id && item.identifier == gain.type && ShopInventory.Owned(item))
                        return new Found { Item = item, Location = location, Name = location.Name };
            var e = EmporiumEntry.Instance;
            foreach (var pocket in new[] { (inventory: e?.afterhourPocketSlotInvLeft, key: "f36.left_pocket"),
                (inventory: e?.afterhourPocketSlotInvRight, key: "f36.right_pocket") })
            {
                var item = pocket.inventory?.childItem;
                if (item != null && item.uniqueId == gain.id && item.identifier == gain.type && ShopInventory.Owned(item))
                    return new Found { Item = item, Name = ShopSettings.Text(pocket.key) };
            }
            return new Found { Name = ShopSettings.Text("f25.location_unknown") };
        }
        private string Reason(Goods gain, Found found)
        {
            if (found.Item == null || found.Item.unitCount != gain.qty) return "inv.changed";
            if (gain.manual || !Confirmed(found.Item)) return "f25.check";
            if (found.Location == null || !found.Location.Ordinary || !WholeStackTransfer.SafeSource(found.Item)) return "f25.manual_place";
            if (!ShopInventory.Movable(found.Item) || RequestBook.Reserved(found.Item)) return "f08.locked";
            return null;
        }
        private void MarkHandled(Goods gain)
        {
            if (!Load() || !trip.gains.Remove(gain)) return;
            try { Save(); selected.Remove(gain.id); Draw(); }
            catch (Exception error) { RecordFailed(error); Close(); if (Load()) Draw(); }
        }
        private void DrawRecovery()
        {
            var b = UiLayout.Rebuild(ref window, Id, ShopSettings.Text("f25.open"), 570, 300);
            b.OnClose(() => window = null);
            b.AddLabel(ShopSettings.Text("f25.unreadable"));
            if (PlayerStore.HasInstance && PlayerStore.Instance.ModData() != null &&
                !string.IsNullOrEmpty(PlayerStore.Instance.runID) && PlayerStore.Instance.saveSlotId >= 0)
            {
                string expectedRun = PlayerStore.Instance.runID;
                int expectedSlot = PlayerStore.Instance.saveSlotId;
                string raw;
                try { raw = ModHelper.GetModData(ModEntry.Id, DataKey, null); }
                catch (Exception error) { log("F25 cannot inspect record for reset: " + error); b.Show(); return; }
                b.AddButton(ShopSettings.Text("f25.restart"), () =>
                {
                    try
                    {
                        if (!enabled.Value || !ShopInventory.InStore || !PlayerStore.HasInstance ||
                            PlayerStore.Instance.runID != expectedRun || PlayerStore.Instance.saveSlotId != expectedSlot ||
                            PlayerStore.Instance.ModData() == null || ModHelper.GetModData(ModEntry.Id, DataKey, null) != raw)
                        { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                        run = expectedRun; slot = expectedSlot;
                        trip = new Trip { run = run, slot = slot, started = true, returned = true, uncertain = true };
                        continuous = returning = false; traces.Clear(); observed.Clear(); returningItems.Clear(); selected.Clear();
                        Save();
                        ShopNotice.Show(ShopSettings.Text("f25.restarted"));
                        Draw();
                    }
                    catch (Exception error) { RecordFailed(error); }
                }, "restart");
            }
            b.Show();
        }
        private void Draw()
        {
            if (!enabled.Value || !ShopInventory.InStore) return;
            if (!Load()) { DrawRecovery(); return; }
            bool fresh = window == null || !window.IsAlive;
            var b = UiLayout.Rebuild(ref window, Id, ShopSettings.Text("f25.open"), 570, 400);
            b.OnClose(() => window = null);
            if (!trip.returned)
            {
                if (!trip.started) { b.AddLabel(ShopSettings.Text("f25.none")).Show(); return; }
                b.BeginRow().AddLabel(ShopSettings.Text("f25.unfinished"));
                GameItem bag = Locations().SelectMany(l => l.Inventory.childItems).FirstOrDefault(i =>
                    i.uniqueId == trip.bag && ShopInventory.Owned(i));
                if (bag != null) b.AddButton(ShopSettings.Text("f25.locate"), () =>
                { if (!ItemLocator.TryLocate(bag, out string failure)) ShopNotice.Show(ShopSettings.Text(failure)); });
                b.AddButton(ShopSettings.Text("f25.handled"), () =>
                {
                    if (!Load() || trip.returned) return;
                    trip.started = false; trip.uncertain = false; trip.outward.Clear();
                    try { Save(); Draw(); } catch (Exception error) { RecordFailed(error); }
                }).End().Show();
                return;
            }
            if (trip.uncertain)
            {
                b.BeginRow().AddLabel(ShopSettings.Text("f25.uncertain"))
                    .AddButton(ShopSettings.Text("f25.handled"), () =>
                    {
                        if (!Load()) return;
                        trip.uncertain = false;
                        try { Save(); Draw(); } catch (Exception error) { RecordFailed(error); }
                    }).End();
            }
            if (trip.gains.Count == 0) { if (!trip.uncertain) b.AddLabel(ShopSettings.Text("f25.none")); b.Show(); return; }
            var locations = Locations();
            var dests = locations.Where(l => l.Ordinary && !l.Inventory.IsInsertLocked()).ToList();
            int dest = dests.FindIndex(l => l.Key() == trip.dest);
            VisualChoices.Location(b, ShopSettings.Text("visual.target"), dest >= 0 ? dests[dest] : null,
                () => Locations().Where(l => l.Ordinary && !l.Inventory.IsInsertLocked()).ToList(), live =>
                {
                    if (!enabled.Value || !Load()) return;
                    try { trip.dest = live.Key(true); Save(); Draw(); }
                    catch (Exception error) { RecordFailed(error); Close(); if (Load()) Draw(); }
                }, "destination");
            if (fresh)
            {
                selected.Clear();
                foreach (Goods gain in trip.gains) if (Reason(gain, Find(gain, locations)) == null) selected.Add(gain.id);
            }
            page = Math.Max(0, Math.Min(page, (trip.gains.Count - 1) / 18));
            b.BeginScrollFlexible(120);
            var tiles = new List<ItemGrid.Entry>();
            foreach (Goods gain in trip.gains.Skip(page * 18).Take(18))
            {
                var found = Find(gain, locations);
                string reason = Reason(gain, found);
                if (reason != null)
                {
                    selected.Remove(gain.id);
                    string label = (found.Item?.GetDisplayName(true) ?? gain.type) + " × " + gain.qty + " · " + found.Name + " · " + ShopSettings.Text(reason);
                    b.BeginRow().AddLabel(label);
                    if (b.Last.text != null) b.Last.text.richText = false;
                    if (found.Item != null && found.Location != null)
                        b.AddButton(ShopSettings.Text("f25.locate"), () =>
                        { if (!ItemLocator.TryLocate(found.Item, out string failure)) ShopNotice.Show(ShopSettings.Text(failure)); });
                    b.AddButton(ShopSettings.Text("f25.handled"), () => MarkHandled(gain)).End();
                    continue;
                }
                tiles.Add(new ItemGrid.Entry(found.Item) { Selected = selected.Contains(gain.id), SourceLabel = found.Name });
            }
            ItemGrid.Add(b, tiles, new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 125, CardHeight = 92,
                OnClick = entry => { if (!selected.Add(entry.Item.uniqueId)) selected.Remove(entry.Item.uniqueId); entry.Selected = selected.Contains(entry.Item.uniqueId); } });
            b.End();
            UiLayout.Pager(b, page, trip.gains.Count, 18, next => { page = next; Draw(); });
            b.AddButton(ShopSettings.Text("f25.unload"), Unload, "unload").Show();
        }

        private UnloadPlan PlanUnload()
        {
            if (!enabled.Value || !ShopInventory.InStore) return null;
            if (!Load()) { RecordFailed(null); DrawRecovery(); return null; }
            if (!trip.returned) return null;
            if (!WholeStackTransfer.Ready) { ShopNotice.Show(ShopSettings.Text("f08.pending")); return null; }
            var all = Locations();
            var dest = all.Find(l => l.Ordinary && l.Key() == trip.dest);
            if (dest == null) { ShopNotice.Show(ShopSettings.Text("f25.choose")); return null; }
            var chosen = trip.gains.Where(gain => selected.Contains(gain.id)).ToArray();
            if (chosen.Length == 0) { ShopNotice.Show(ShopSettings.Text("f25.select")); return null; }
            var plan = new UnloadPlan { Run = run, Slot = slot, Target = dest };
            var reserved = new WholeStackTransfer.Reservations();
            foreach (Goods gain in chosen)
            {
                var found = Find(gain, all);
                string reason = Reason(gain, found);
                if (reason == null && (!TripCargoRules.CanUnload(CargoOrigin.New, gain.qty, found.Item.unitCount) ||
                    found.Location.Inventory == dest.Inventory)) reason = "f25.manual_place";
                if (reason == null && !WholeStackTransfer.SafeTarget(dest.Inventory)) reason = "f08.unknown";
                if (reason == null && ShopInventory.WouldContainSelf(found.Item, dest.Inventory)) reason = "f08.cycle";
                WholeStackTransfer.Plan transfer = reason == null ? WholeStackTransfer.Stack(found.Item, dest.Inventory, reserved) ??
                    WholeStackTransfer.Empty(found.Item, dest.Inventory as GameGridInventory, reserved) : null;
                if (reason == null && transfer == null) reason = "inv.noSpace";
                if (reason != null) plan.Skipped.Add(new UnloadSkip { Id = gain.id, Quantity = gain.qty, Type = gain.type,
                    Item = found.Item, Location = found.Name, Reason = reason });
                else plan.Moves.Add(new UnloadMove { Id = gain.id, Quantity = gain.qty, Type = gain.type, Source = found.Location, Transfer = transfer });
            }
            return plan;
        }
        private static bool SameLocation(ShopInventory.Location a, ShopInventory.Location b) => a != null && b != null &&
            ReferenceEquals(a.Inventory, b.Inventory) && ReferenceEquals(a.Owner, b.Owner) && a.Role == b.Role && a.Page == b.Page &&
            a.Path != null && b.Path != null && a.Path.SequenceEqual(b.Path);
        private static bool Same(UnloadPlan a, UnloadPlan b)
        {
            if (a == null || b == null || a.Run != b.Run || a.Slot != b.Slot || !SameLocation(a.Target, b.Target) ||
                a.Moves.Count != b.Moves.Count || a.Skipped.Count != b.Skipped.Count) return false;
            for (int i = 0; i < a.Moves.Count; i++)
                if (a.Moves[i].Id != b.Moves[i].Id || a.Moves[i].Quantity != b.Moves[i].Quantity || a.Moves[i].Type != b.Moves[i].Type ||
                    !SameLocation(a.Moves[i].Source, b.Moves[i].Source) || !WholeStackTransfer.Same(a.Moves[i].Transfer, b.Moves[i].Transfer)) return false;
            for (int i = 0; i < a.Skipped.Count; i++)
                if (a.Skipped[i].Id != b.Skipped[i].Id || a.Skipped[i].Quantity != b.Skipped[i].Quantity ||
                    a.Skipped[i].Type != b.Skipped[i].Type || a.Skipped[i].Item != b.Skipped[i].Item || a.Skipped[i].Reason != b.Skipped[i].Reason ||
                    a.Skipped[i].Location != b.Skipped[i].Location) return false;
            return true;
        }
        private void Unload()
        {
            var plan = PlanUnload();
            if (plan == null) return;
            var b = UiLayout.Rebuild(ref window, Id, ShopSettings.Text("f25.open"), 570, 400);
            b.OnClose(() => window = null);
            b.AddLabel(ShopSettings.Format("f25.preview_summary", plan.Moves.Sum(move => (long)move.Quantity), plan.Target.Name));
            b.BeginScrollFlexible(120);
            foreach (UnloadMove move in plan.Moves)
            {
                b.AddLabel(move.Transfer.Item.GetDisplayName(true) + " × " + move.Quantity + " · " + move.Source.Name +
                    (move.Transfer.IsStack ? " · " + ShopSettings.Text("f25.merge") : ""));
                if (b.Last.text != null) b.Last.text.richText = false;
            }
            foreach (UnloadSkip skipped in plan.Skipped)
            {
                b.AddLabel((skipped.Item?.GetDisplayName(true) ?? skipped.Type) + " · " + skipped.Location + " · " + ShopSettings.Text(skipped.Reason));
                if (b.Last.text != null) b.Last.text.richText = false;
            }
            b.End().BeginRow().AddButton(ShopSettings.Text("f34.back"), Draw)
                .AddButton(ShopSettings.Text("f25.confirm"), () => CommitUnload(plan), "confirm");
            b.Last.SetInteractable(plan.Moves.Count > 0);
            b.End().Show();
        }
        private void CommitUnload(UnloadPlan expected)
        {
            var live = PlanUnload();
            if (!Same(expected, live)) { if (live != null) Unload(); ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            var result = new OperationResult("f25.open");
            foreach (UnloadSkip skipped in live.Skipped)
            {
                string path = skipped.Location + " → " + live.Target.Name;
                if (skipped.Item == null) result.SkipMissing(skipped.Type, skipped.Quantity, skipped.Reason, path, skipped.Id);
                else result.Skip(skipped.Item, skipped.Quantity, skipped.Reason, path);
            }
            bool stopped = false;
            foreach (UnloadMove move in live.Moves)
            {
                string path = move.Source.Name + " → " + live.Target.Name;
                if (stopped) { result.SkipMissing(move.Type, move.Quantity, "operation.not_attempted", path, move.Id); continue; }
                var identity = OperationResult.Capture(move.Transfer.Item);
                bool Scope()
                {
                    if (!enabled.Value || !ShopInventory.InStore || !Load() || !trip.returned || run != live.Run || slot != live.Slot) return false;
                    var locations = Locations();
                    return ShopInventory.TryRefresh(move.Source, locations, out var from) && ShopInventory.TryRefresh(live.Target, locations, out var to) &&
                        from.Ordinary && to.Ordinary && to.Key() == trip.dest && from.Inventory != to.Inventory &&
                        trip.gains.Any(g => !g.manual && g.id == move.Id && g.qty == move.Quantity && g.type == move.Type);
                }
                bool Record()
                {
                    if (!Load() || run != live.Run || slot != live.Slot) return false;
                    Goods gain = trip.gains.Find(g => !g.manual && g.id == move.Id && g.qty == move.Quantity && g.type == move.Type);
                    if (gain == null) return false;
                    trip.gains.Remove(gain);
                    try { Save(); selected.Remove(move.Id); return true; }
                    catch (Exception error) { log("F25 could not save completed unload: " + error); return false; }
                }
                var outcome = WholeStackTransfer.Apply(move.Transfer, "f25.open", path, move.Source.Key(true), live.Target.Key(true), Scope, Record);
                if (outcome == WholeStackTransfer.Result.Applied) result.CompleteCaptured(identity, move.Quantity, path);
                else
                {
                    result.SkipCaptured(identity, move.Quantity, outcome == WholeStackTransfer.Result.Unchanged ? "inv.changed" : "f08.pending", path);
                    stopped = true;
                }
            }
            Draw(); ShopNotice.Report(result);
        }

        public void Dispose()
        {
            settings.Changed -= Changed; ModHook.OnLeavingStoreEarly -= Close; ModHook.OnGameLoadedInit -= Reset;
            if (WholeStackTransfer.ResolveTrip == ResolvePending) WholeStackTransfer.ResolveTrip = null;
            WholeStackTransfer.Release();
            if (ReferenceEquals(active, this)) active = null;
            Reset();
        }
    }
}
