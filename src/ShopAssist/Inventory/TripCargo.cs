using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;

namespace ShopAssist
{
    internal sealed class TripCargo : IDisposable
    {
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
            internal bool Exposed;
        }
        private sealed class Trace
        {
            internal GameItem Item;
            internal int Quantity;
            internal string Type;
            internal CargoOrigin Origin;
            internal bool Exposed;
        }
        private sealed class Transfer
        {
            internal GameItem Source, Target;
            internal GameInventory From, To;
            internal int SourceCount, TargetCount;
            internal string SourceType, TargetType;
            internal CargoOrigin SourceOrigin, TargetOrigin;
            internal bool SourceExposed, TargetExposed;
            internal List<ContainedTrace> Contents;
        }
        private sealed class ContainedTrace { internal Seen Before; internal CargoOrigin Origin; internal bool Exposed; }
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
            internal ShopInventory.Location Target;
            internal WholeStackTransfer.Plan Transfer;
            internal List<Goods> Included;
        }
        private sealed class UnloadSkip
        {
            internal int Id, Quantity;
            internal string Type, Reason, Location;
            internal GameItem Item;
            internal List<Goods> Included;
        }
        private sealed class UnloadPlan
        {
            internal string Run;
            internal int Slot, Mode;
            internal readonly List<UnloadMove> Moves = new List<UnloadMove>();
            internal readonly List<UnloadSkip> Skipped = new List<UnloadSkip>();
        }

        private readonly ShopSettings settings;
        private readonly ConfigEntry<int> unloadMode;
        private readonly Action<string> log;
        private readonly HashSet<int> selected = new HashSet<int>();
        private readonly Dictionary<int, Trace> traces = new Dictionary<int, Trace>();
        private readonly Dictionary<int, Seen> observed = new Dictionary<int, Seen>();
        private readonly HashSet<GameItem> returningItems = new HashSet<GameItem>();
        private string run;
        private int slot, transferDepth;
        private Trip trip;
        private bool continuous, returning, lastEnabled, autoUnloadPending;
        private bool Enabled => unloadMode.Value > 0 && unloadMode.Value <= 2;
        private readonly Dictionary<int, string> unloadFailures = new Dictionary<int, string>();

        internal static bool AppliesTo(GameItem item) => active != null && item != null && item.IsTag("BACKPACK_TAG") &&
            ShopInventory.InStore && ShopInventory.Owned(item) && (!active.Load() || active.trip.returned &&
            item.uniqueId == (active.trip.returnBag > 0 ? active.trip.returnBag : active.trip.bag));

        internal static bool IsReturnedCargoInCounter(GameItem item)
        {
            if (active == null || !active.Enabled || item?.parentInventory == null || !ShopInventory.InStore ||
                PlayerStore.Instance.currentClientInstance != null || !active.Load() || !active.trip.started ||
                !active.trip.returned || active.trip.uncertain || !active.continuous || !NativeTrackingSafe()) return false;
            var counter = EmporiumEntry.Instance?.backInvinvElement;
            if (counter == null || !ShopInventory.Owned(item) || !active.Confirmed(item) ||
                !active.Exposed(item) && !PubliclyVisible(item)) return false;
            bool atCounter = ReferenceEquals(item.parentInventory, counter) && counter.childItems.Contains(item);
            if (!atCounter)
            {
                var seen = new HashSet<GameInventory>();
                GameInventory inventory = item.parentInventory;
                while (inventory != null && !ReferenceEquals(inventory, counter) && seen.Add(inventory))
                {
                    GameItem owner = inventory.GetParentItem();
                    if (owner == null || !ShopInventory.SafeContainerForPeek(owner) ||
                        !ReferenceEquals(owner.contentWindow.childElement, inventory) || owner.parentInventory == null ||
                        !owner.parentInventory.childItems.Contains(owner)) break;
                    inventory = owner.parentInventory;
                }
                atCounter = ReferenceEquals(inventory, counter);
            }
            return atCounter &&
                active.trip.gains.Any(g => !g.manual && g.id == item.uniqueId && g.type == item.identifier && g.qty == item.unitCount);
        }

        internal static ActionAvailability AvailabilityFor(GameItem item)
        {
            if (active == null) return ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "availability.unknown");
            if (!active.Enabled) return ActionAvailability.Blocked(ActionUnavailableReason.Disabled, "availability.disabled", "f25.return_mode");
            if (!ShopInventory.InStore) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.store");
            if (!active.Load()) return ShopInventory.Owned(item) && item.IsTag("BACKPACK_TAG") ? ActionAvailability.Ready :
                ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "f25.record_failed");
            if (!AppliesTo(item)) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "f25.bagmissing");
            if (!WholeStackTransfer.Ready) return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "f08.pending");
            if (active.trip.gains.Count == 0 && !active.trip.uncertain)
                return ActionAvailability.Blocked(ActionUnavailableReason.MissingResource, "f25.none");
            return ActionAvailability.Ready;
        }
        internal static void OpenFor(GameItem item) { if (AvailabilityFor(item).Allowed) active.RequestRetry(); }

        internal TripCargo(ShopSettings settings, Action<string> log)
        {
            this.settings = settings;
            this.log = log;
            unloadMode = settings.Choice("f25.return_mode", "f25.mode.tip", new[] { "f25.mode_off", "f25.mode_all", "f25.mode_new" }, settings.LegacyCargoMode());
            lastEnabled = Enabled;
        }
        internal void Start()
        {
            active = this;
            WholeStackTransfer.Acquire();
            WholeStackTransfer.ResolveTrip = ResolvePending;
            AttentionBook.Register("trip_cargo", CargoAttention);
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Close;
            settings.Changed += Changed;
        }

        internal void Tick()
        {
            if (!autoUnloadPending || returning || !Enabled || !ShopInventory.InStore) return;
            if (!Load()) { autoUnloadPending = false; RecordFailed(null); AttentionBook.Changed(); return; }
            if (!trip.returned) { autoUnloadPending = false; return; }
            if (trip.uncertain) { autoUnloadPending = false; ShopNotice.Show(ShopSettings.Text("f25.uncertain")); AttentionBook.Changed(); return; }
            // A return spans several native UI transitions. Keep the request until
            // dialogs, dragging and other transfer journals have actually cleared.
            if (!TripCargoRules.ShouldUnload(unloadMode.Value, returning, ShopInventory.InStore, AutomaticReady(), trip.returned, trip.uncertain)) return;
            autoUnloadPending = false;
            selected.Clear();
            foreach (Goods gain in trip.gains)
                if (!gain.manual) selected.Add(gain.id);
            if (selected.Count == 0)
            {
                if (trip.gains.Count > 0) ShopNotice.Show(ShopSettings.Text("f25.uncertain"));
                return;
            }
            try
            {
                var plan = PlanUnload();
                if (plan != null) CommitUnload(plan);
            }
            catch (Exception error) { unloadFailures[0] = "f25.record_failed"; RecordFailed(error); }
            AttentionBook.Changed();
        }
        private static bool AutomaticReady() => WholeStackTransfer.Ready && AutomaticEnvironmentReady();
        private static bool AutomaticEnvironmentReady() => ShopInput.Ready() &&
            PlayerStore.Instance.currentClientInstance == null && !UnityEngine.Input.GetMouseButton(0) &&
            !UIDragHandler.isDraggingAnyUI && ItemMouseDragHandler.current?.IsDraggingItem != true && !ItemGrid.DragActive &&
            DialogUIManager.Instance?.currentDialog == null && DialoguePhoneUIManager.Instance?.currentDialog == null &&
            StoreUIManager.Instance?.isAboutToStartMainDialog != true;

        private void RequestRetry()
        {
            if (!Enabled || !ShopInventory.InStore || !Load() || !trip.returned) return;
            autoUnloadPending = true; AttentionBook.Changed();
        }

        private IEnumerable<AttentionItem> CargoAttention()
        {
            var issues = new List<AttentionItem>();
            if (!ShopInventory.InStore) return issues;
            if (!Load())
            {
                issues.Add(new AttentionItem { Key = "f25:record", Feature = "f25.open", Title = ShopSettings.Text("f25.open"),
                    Location = "", ReasonKey = "f25.unreadable", NeedsInspection = true,
                    ResolveLabelKey = "f25.reset_record", Resolve = ConfirmRecordReset });
                return issues;
            }
            string expectedRun = run; int expectedSlot = slot;
            if (trip.gains.Count == 0 && trip.uncertain)
                issues.Add(new AttentionItem { Key = "f25:uncertain", Feature = "f25.open", Title = ShopSettings.Text("f25.open"),
                    Location = "", ReasonKey = "f25.uncertain", NeedsInspection = true, ResolveLabelKey = "f25.handled",
                    Resolve = () => AcknowledgeCargo(expectedRun, expectedSlot, 0) });
            var foundItems = trip.gains.Count == 0 ? null : FindAll(trip.gains, Locations());
            var concealed = new Dictionary<int, (GameItem Owner, List<ConcealedEntry> Entries)>();
            foreach (Goods gain in trip.gains)
            {
                Found found = foundItems[gain.id];
                if (!CargoDetailsVisible(found, gain))
                {
                    GameItem owner = found.Location?.Path?.FirstOrDefault(item => item is GameItemElement element && VisibleInventoryViews.Visible(element));
                    int ownerId = owner?.uniqueId ?? 0;
                    if (!concealed.TryGetValue(ownerId, out var group))
                        concealed[ownerId] = group = (owner, new List<ConcealedEntry>());
                    bool childrenKnown = Contents(found.Item, out var descendants);
                    var nested = found.Item?.contentWindow?.childElement as GameGridInventory;
                    group.Entries.Add(new ConcealedEntry { Gain = new Goods { id = gain.id, qty = gain.qty, type = gain.type, manual = gain.manual },
                        Item = found.Item, ItemCount = found.Item?.unitCount ?? 0, Parent = found.Item?.parentInventory, Shape = found.Item?.modifiedShape?.Clone(),
                        Location = found.Location, State = found.Item == null ? null : InventoryArrangeTransaction.StateSignature(found.Item),
                        Contents = nested, ContentsShape = nested?.inventoryShape?.Clone(),
                        ChildrenKnown = childrenKnown, Children = descendants.ToArray() });
                    continue;
                }
                string reason = Reason(gain, found);
                bool inspect = trip.uncertain || gain.manual || reason == "f25.check";
                if (reason == null && !unloadFailures.TryGetValue(gain.id, out reason)) reason = Enabled ? "f25.waiting" : "f25.paused";
                int id = gain.id;
                issues.Add(new AttentionItem { Key = "f25:" + id, Feature = "f25.open", ItemId = id,
                    Title = found.Item.GetDisplayName(true), Location = PublicCargoLocation(found), ReasonKey = reason,
                    NeedsInspection = inspect, ActionLabelKey = "f25.retry", ResolveLabelKey = "f25.handled",
                    Retry = !inspect && Enabled ? (Action)(() => { if (Load() && run == expectedRun && slot == expectedSlot) RequestRetry(); }) : null,
                    Resolve = () => AcknowledgeCargo(expectedRun, expectedSlot, id) });
            }
            foreach (var pair in concealed)
            {
                var group = pair.Value; ConcealedEntry[] entries = group.Entries.ToArray();
                issues.Add(new AttentionItem { Key = "f25:concealed:" + pair.Key, Feature = "f25.open", ItemId = pair.Key,
                    Title = group.Owner?.GetDisplayName(true) ?? ShopSettings.Text("f25.open"), ReasonKey = "f25.check",
                    NeedsInspection = true, ResolveLabelKey = "f25.handled",
                    Resolve = () => ConfirmConcealed(expectedRun, expectedSlot, pair.Key, entries) });
            }
            return issues;
        }

        private void AcknowledgeCargo(string expectedRun, int expectedSlot, int id) =>
            AcknowledgeCargo(expectedRun, expectedSlot, id > 0 ? new[] { id } : Array.Empty<int>());

        private sealed class ConcealedEntry
        {
            internal Goods Gain;
            internal GameItem Item;
            internal int ItemCount;
            internal GameInventory Parent;
            internal GridShape Shape;
            internal GameGridInventory Contents;
            internal GridShape ContentsShape;
            internal bool ChildrenKnown;
            internal Seen[] Children;
            internal ShopInventory.Location Location;
            internal string State;
        }

        private void ConfirmConcealed(string expectedRun, int expectedSlot, int ownerId, ConcealedEntry[] expected)
        {
            if (expected == null || expected.Length == 0 || GenericConfirmationUIManager.Instance == null) return;
            var prompt = GenericConfirmationUIManager.Instance;
            prompt.OpenPanel(ShopSettings.Text("f25.uncertain"), ShopSettings.Text("f25.handled"), ShopSettings.Text("f25.reset_cancel"), () =>
            {
                prompt.ClosePanel();
                if (!ShopInventory.InStore || !Load() || run != expectedRun || slot != expectedSlot || WholeStackTransfer.PendingFor("f25.open")) return;
                var found = FindAll(trip.gains, Locations());
                var current = new List<int>();
                foreach (Goods gain in trip.gains)
                {
                    Found item = found[gain.id];
                    if (CargoDetailsVisible(item, gain)) continue;
                    GameItem owner = item.Location?.Path?.FirstOrDefault(value => value is GameItemElement element && VisibleInventoryViews.Visible(element));
                    if ((owner?.uniqueId ?? 0) == ownerId) current.Add(gain.id);
                }
                if (!current.OrderBy(id => id).SequenceEqual(expected.Select(value => value.Gain.id).OrderBy(id => id)))
                { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                foreach (ConcealedEntry entry in expected)
                {
                    Goods gain = trip.gains.Find(value => value.id == entry.Gain.id);
                    Found item = found[entry.Gain.id];
                    if (gain == null || gain.qty != entry.Gain.qty || gain.type != entry.Gain.type || gain.manual != entry.Gain.manual ||
                        !ReferenceEquals(item.Item, entry.Item) || !ReferenceEquals(item.Item?.parentInventory, entry.Parent) ||
                        entry.Item != null && (entry.Item.unitCount != entry.ItemCount || !SameLocation(entry.Location, item.Location) ||
                            !InventorySortPlan.SameShape(entry.Shape, item.Item.modifiedShape) ||
                            entry.State == null || entry.State != InventoryArrangeTransaction.StateSignature(item.Item) ||
                            !ReferenceEquals(entry.Contents, item.Item.contentWindow?.childElement as GameGridInventory) ||
                            entry.Contents != null && !InventorySortPlan.SameShape(entry.ContentsShape, entry.Contents.inventoryShape) ||
                            !entry.ChildrenKnown || !Contents(item.Item, out var descendants) || descendants.Count != entry.Children.Length ||
                            !entry.Children.Zip(descendants, Same).All(same => same)))
                    { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                }
                AcknowledgeCargo(expectedRun, expectedSlot, expected.Select(entry => entry.Gain.id).ToArray());
            }, () => prompt.ClosePanel());
        }

        private void AcknowledgeCargo(string expectedRun, int expectedSlot, int[] ids)
        {
            if (!ShopInventory.InStore || !Load() || run != expectedRun || slot != expectedSlot ||
                WholeStackTransfer.PendingFor("f25.open")) return;
            var acknowledged = new HashSet<int>(ids);
            trip.gains.RemoveAll(gain => acknowledged.Contains(gain.id));
            if (trip.gains.Count == 0) { trip.started = trip.uncertain = false; trip.outward.Clear(); }
            try { Save(); foreach (int id in ids) unloadFailures.Remove(id); AttentionBook.Changed(); }
            catch (Exception error) { RecordFailed(error); }
        }

        private void ConfirmRecordReset()
        {
            if (!ShopInventory.InStore || Load() || WholeStackTransfer.PendingFor("f25.open")) return;
            string expectedRun = PlayerStore.Instance.runID; int expectedSlot = PlayerStore.Instance.saveSlotId;
            string original;
            try { original = ModHelper.GetModData(ModEntry.Id, DataKey, null); }
            catch (Exception error) { RecordFailed(error); return; }
            var confirmation = GenericConfirmationUIManager.Instance;
            if (original == null || confirmation == null) return;
            confirmation.OpenPanel(ShopSettings.Text("f25.reset_warning"), ShopSettings.Text("f25.reset_record"), ShopSettings.Text("f25.reset_cancel"), () =>
            {
                confirmation.ClosePanel();
                try
                {
                    if (!ShopInventory.InStore || PlayerStore.Instance.runID != expectedRun || PlayerStore.Instance.saveSlotId != expectedSlot ||
                        WholeStackTransfer.PendingFor("f25.open") || ModHelper.GetModData(ModEntry.Id, DataKey, null) != original) return;
                    CheckedModData.Write(DataKey + ".unreadable." + Guid.NewGuid().ToString("N"), original);
                    if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != original) return;
                    Reset(); run = expectedRun; slot = expectedSlot; trip = new Trip { run = run, slot = slot };
                    Save(); AttentionBook.Changed();
                }
                catch (Exception error) { RecordFailed(error); }
            }, () => confirmation.ClosePanel());
        }
        private void Changed()
        {
            bool disabled = lastEnabled && !Enabled;
            lastEnabled = Enabled;
            if (Enabled) { if (Load() && trip.returned && trip.gains.Count > 0) RequestRetry(); return; }
            autoUnloadPending = false;
            Close();
            if (!disabled || trip == null || !trip.started || trip.returned && trip.gains.Count == 0 && !trip.uncertain) return;
            if (trip.returned) { AttentionBook.Changed(); return; }
            LoseContinuity();
            foreach (Goods gain in trip.gains) gain.manual = true;
            try { Save(); }
            catch (Exception error) { RecordFailed(error); }
        }
        private void Close() { selected.Clear(); }
        private void Reset()
        {
            Close(); trip = null; run = null; continuous = returning = autoUnloadPending = false; unloadFailures.Clear();
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
            GameItem source = PlayerStore.Instance.FindAllItem().FirstOrDefault(item => item.uniqueId == sourceId);
            bool inspect = TripCargoRules.NeedsTripInspection(source != null, source?.contentWindow != null || source?.IsTag("BACKPACK_TAG") == true);
            if (gain == null && !inspect) return true;
            if (inspect)
            {
                trip.uncertain = true;
                foreach (Goods child in trip.gains) child.manual = true;
            }
            if (gain != null) trip.gains.Remove(gain);
            try { Save(); selected.Remove(sourceId); unloadFailures.Remove(sourceId); AttentionBook.Changed(); return true; }
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
                    bool direct = ReferenceEquals(inventory, e.afterhourPocketSlotInvLeft) || ReferenceEquals(inventory, e.afterhourPocketSlotInvRight) ||
                        ReferenceEquals(inventory, bag == null ? null : ShopInventory.OutingBag(bag));
                    result.Add(new Seen { Item = item, Parent = inventory, Id = id, Quantity = item.unitCount, Type = item.identifier,
                        Exposed = direct || PubliclyVisible(item) });
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
            bool known = traces.TryGetValue(value.Id, out var previous) && ReferenceEquals(previous.Item, value.Item) && previous.Type == value.Type && previous.Exposed;
            traces[value.Id] = new Trace { Item = value.Item, Quantity = value.Quantity, Type = value.Type, Origin = origin, Exposed = known || value.Exposed };
        }
        private void SetTrace(GameItem item, CargoOrigin origin, bool exposed = false)
        {
            if (item == null || item.unitCount <= 0 || string.IsNullOrEmpty(item.identifier)) return;
            int id = item.GetUniqueID();
            if (id <= 0) { LoseContinuity(); return; }
            if (traces.TryGetValue(id, out var old) && !ReferenceEquals(old.Item, item))
            { origin = CargoOrigin.Unknown; LoseContinuity(); }
            traces[id] = new Trace { Item = item, Quantity = item.unitCount, Type = item.identifier, Origin = origin,
                Exposed = exposed || old != null && ReferenceEquals(old.Item, item) && old.Type == item.identifier && old.Exposed || PubliclyVisible(item) };
        }
        private CargoOrigin Origin(GameItem item)
        {
            if (item == null || item.uniqueId <= 0 || !traces.TryGetValue(item.uniqueId, out var value) ||
                !ReferenceEquals(value.Item, item) || value.Quantity != item.unitCount || value.Type != item.identifier)
                return CargoOrigin.Unknown;
            return value.Origin;
        }
        // Exposure is session-only; reloaded unfinished trips already require inspection.
        private bool Exposed(GameItem item) => item != null && item.uniqueId > 0 && traces.TryGetValue(item.uniqueId, out var value) &&
            ReferenceEquals(value.Item, item) && value.Quantity == item.unitCount && value.Type == item.identifier && value.Exposed;
        private static bool PubliclyVisible(GameItem item) => item is GameItemElement element && VisibleInventoryViews.Visible(element);
        private static bool Same(Seen a, Seen b) => a != null && b != null && ReferenceEquals(a.Item, b.Item) &&
            ReferenceEquals(a.Parent, b.Parent) && a.Id == b.Id && a.Quantity == b.Quantity && a.Type == b.Type;

        private static bool Contents(GameItem root, out List<Seen> contents)
        {
            contents = new List<Seen>();
            if (root?.contentWindow == null) return true;
            var pending = new Queue<GameItem>(); var items = new HashSet<GameItem> { root }; var ids = new HashSet<int>();
            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                GameItem owner = pending.Dequeue();
                if (!ShopInventory.SafeContainerForPeek(owner) || !(owner.contentWindow.childElement is GameGridInventory grid) ||
                    grid.GetParentItem() != owner || grid.childItems.Count > 512) return false;
                foreach (GameItem child in grid.childItems)
                {
                    if (child == null || child.parentInventory != grid || !ShopInventory.Owned(child) || !items.Add(child) ||
                        child.unitCount <= 0 || string.IsNullOrEmpty(child.identifier) || contents.Count >= 512) return false;
                    int id = child.GetUniqueID();
                    if (id <= 0 || !ids.Add(id)) return false;
                    contents.Add(new Seen { Item = child, Parent = grid, Id = id, Quantity = child.unitCount, Type = child.identifier });
                    if (child.contentWindow != null) pending.Enqueue(child);
                }
            }
            return true;
        }

        private static bool GroundContainer(GameInventory inventory)
        {
            var ground = EmporiumEntry.Instance?.afterhourInventory; var seen = new HashSet<GameInventory>();
            while (inventory != null && !ReferenceEquals(inventory, ground) && seen.Add(inventory))
            {
                GameItem owner = inventory.GetParentItem();
                if (owner == null || !ShopInventory.SafeContainerForPeek(owner) ||
                    !ReferenceEquals(owner.contentWindow.childElement, inventory)) return false;
                inventory = owner.parentInventory;
            }
            return ground != null && ReferenceEquals(inventory, ground);
        }
        private bool Confirmed(GameItem item) => item != null &&
            (Origin(item) == CargoOrigin.New || unloadMode.Value == 1 && Origin(item) == CargoOrigin.Old);
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
                else if (live.Exposed && traces.TryGetValue(live.Id, out var trace) && ReferenceEquals(trace.Item, live.Item) &&
                    trace.Quantity == live.Quantity && trace.Type == live.Type) trace.Exposed = true;
            }
            observed.Clear();
            foreach (Seen live in current) observed[live.Id] = live;
        }
        private bool Tracking => trip != null && trip.started && !trip.returned && (returning || continuous && Enabled) &&
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
                fromGround && GroundContainer(to) ||
                (source.uniqueId > 0 && traces.ContainsKey(source.uniqueId));
            if (!relevant) return null;
            if (!NativeTrackingSafe()) LoseContinuity();
            CargoOrigin sourceOrigin = Origin(source);
            Trace known = null;
            bool tracked = source.uniqueId > 0 && traces.TryGetValue(source.uniqueId, out known);
            if (tracked && !ReferenceEquals(known.Item, source)) { LoseContinuity(); sourceOrigin = CargoOrigin.Unknown; }
            if (fromGround && (inventories.Contains(to) || GroundContainer(to)) && !tracked &&
                TripCargoRules.CanTrustGround(continuous, false, CargoOrigin.Unknown))
                sourceOrigin = CargoOrigin.New;
            var parts = new List<ContainedTrace>();
            if (!Contents(source, out var contents)) LoseContinuity();
            else foreach (Seen part in contents)
            {
                bool knownPart = traces.TryGetValue(part.Id, out Trace trace);
                if (knownPart && !ReferenceEquals(trace.Item, part.Item)) LoseContinuity();
                bool exposed = Exposed(part.Item) || PubliclyVisible(part.Item);
                parts.Add(new ContainedTrace { Before = part, Exposed = exposed,
                    Origin = TripCargoRules.ContainedOrigin(knownPart, Origin(part.Item),
                        continuous && fromGround && inventories.Contains(to), exposed) });
            }
            return new Transfer { Source = source, Target = target, From = source.parentInventory, To = to,
                SourceCount = source.unitCount, TargetCount = target?.unitCount ?? 0,
                SourceType = source.identifier, TargetType = target?.identifier,
                SourceOrigin = sourceOrigin, TargetOrigin = Origin(target), SourceExposed = Exposed(source) || PubliclyVisible(source),
                TargetExposed = Exposed(target) || PubliclyVisible(target), Contents = parts };
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
            if (value.Contents.Count > 0)
            {
                valid &= Contents(value.Source, out var after) && after.Count == value.Contents.Count &&
                    value.Contents.Zip(after, (before, current) => Same(before.Before, current)).All(same => same);
            }
            if (value.Target != null)
            {
                valid &= value.Target.identifier == value.TargetType && value.Target.unitCount == (long)value.TargetCount + amount &&
                    value.Source.unitCount == value.SourceCount - amount && value.Target.parentInventory == value.To;
                if (valid)
                {
                    SetTrace(value.Source, value.SourceOrigin, value.SourceExposed);
                    SetTrace(value.Target, TripCargoRules.Merge(value.SourceOrigin, value.TargetOrigin), value.SourceExposed || value.TargetExposed);
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
                    SetTrace(value.Source, value.SourceOrigin, value.SourceExposed);
                    SetTrace(placed, value.SourceOrigin, value.SourceExposed);
                    if (returning) returningItems.Add(placed);
                }
            }
            if (!valid)
            {
                LoseContinuity(); SetTrace(value.Source, CargoOrigin.Unknown); SetTrace(value.Target, CargoOrigin.Unknown);
                SetTrace(placed, CargoOrigin.Unknown);
            }
            if (valid)
                foreach (ContainedTrace child in value.Contents) SetTrace(child.Before.Item, child.Origin, child.Exposed);
            if (!returning) Reconcile(new[] { value.Source, value.Target, placed }.Concat(value.Contents.Select(child => child.Before.Item)));
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
                    else { a.SetTrace(__state.Source, __state.SourceOrigin, __state.SourceExposed); a.SetTrace(__result, __state.SourceOrigin, __state.SourceExposed); }
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
                    if (!a.Load()) { if (a.Enabled) a.RecordFailed(null); return; }
                    if (WholeStackTransfer.PendingFor("f25.open"))
                    {
                        if (a.trip.started)
                        {
                            a.LoseContinuity();
                            foreach (Goods gain in a.trip.gains) gain.manual = true;
                            a.Save();
                        }
                        if (a.Enabled) ShopNotice.Show(ShopSettings.Text("f25.pending_departure")); return;
                    }
                    if (a.trip.returned && (a.trip.gains.Count > 0 || a.trip.uncertain))
                    {
                        a.LoseContinuity();
                        foreach (Goods gain in a.trip.gains) gain.manual = true;
                        a.Save();
                        if (a.Enabled) ShopNotice.Show(ShopSettings.Text("f25.pending_departure"));
                        return;
                    }
                    var bag = __instance.afterhourPocketSlotInvBackpack?.childItem;
                    string destination = a.trip.dest;
                    a.trip = new Trip { run = a.run, slot = a.slot, started = true, bag = bag?.GetUniqueID() ?? 0, dest = destination };
                    a.traces.Clear(); a.observed.Clear(); a.returningItems.Clear();
                    a.continuous = a.Enabled && NativeTrackingSafe();
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
                    if (!a.Load()) { if (a.Enabled) a.RecordFailed(null); return; }
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
                        if (origin == CargoOrigin.Old && a.unloadMode.Value != 1) continue;
                        int id = item.GetUniqueID();
                        if (id <= 0 || a.trip.gains.Any(g => g.id == id) || a.trip.gains.Count >= 512)
                        { a.LoseContinuity(); continue; }
                        a.trip.gains.Add(new Goods { id = id, type = item.identifier, qty = item.unitCount,
                            manual = origin != CargoOrigin.New && !(a.unloadMode.Value == 1 && origin == CargoOrigin.Old) });
                    }
                    a.trip.returned = true;
                    if (a.unloadMode.Value != 0) a.trip.dest = "root:stock";
                    a.Save();
                    a.autoUnloadPending = a.unloadMode.Value != 0;
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

        private static List<ShopInventory.Location> Locations()
        {
            var locations = ShopInventory.OwnedLocations(false);
            var entry = EmporiumEntry.Instance;
            foreach (var pocket in new[] { (inventory: entry?.afterhourPocketSlotInvLeft, role: "cargo_left", label: "f36.left_pocket"),
                (inventory: entry?.afterhourPocketSlotInvRight, role: "cargo_right", label: "f36.right_pocket") })
                if (pocket.inventory != null && !locations.Any(location => ReferenceEquals(location.Inventory, pocket.inventory)))
                    locations.Add(new ShopInventory.Location { Inventory = pocket.inventory, Role = pocket.role, Ordinary = true,
                        Path = Array.Empty<GameItem>(), Name = ShopSettings.Text(pocket.label) });
            return locations;
        }

        private static bool SourceAllowsAutomaticUnload(ShopInventory.Location source)
        {
            var entry = EmporiumEntry.Instance;
            return source != null && (ReferenceEquals(source.Inventory, entry?.afterhourPocketSlotInvLeft) ||
                ReferenceEquals(source.Inventory, entry?.afterhourPocketSlotInvRight) || InventoryRoutes.AutoTakeAllowed(source.Inventory));
        }
        private static bool CounterRoot(ShopInventory.Location source) => source != null && source.Owner == null &&
            source.Role == "counter" && ReferenceEquals(source.Inventory, EmporiumEntry.Instance?.backInvinvElement);

        private static Dictionary<int, Found> FindAll(IReadOnlyCollection<Goods> gains, List<ShopInventory.Location> locations)
        {
            var requested = gains.ToDictionary(gain => gain.id);
            var found = new Dictionary<int, Found>();
            if (requested.Count == 0) return found;
            foreach (ShopInventory.Location location in locations)
                foreach (GameItem item in location.Inventory.childItems)
                    if (item != null && requested.TryGetValue(item.uniqueId, out Goods gain) &&
                        item.identifier == gain.type && !found.ContainsKey(gain.id) && ShopInventory.Owned(item))
                    {
                        found.Add(gain.id, new Found { Item = item, Location = location, Name = location.Name });
                        if (found.Count == requested.Count) return found;
                    }
            var e = EmporiumEntry.Instance;
            foreach (var pocket in new[] { (inventory: e?.afterhourPocketSlotInvLeft, key: "f36.left_pocket"),
                (inventory: e?.afterhourPocketSlotInvRight, key: "f36.right_pocket") })
            {
                var item = pocket.inventory?.childItem;
                if (item != null && requested.TryGetValue(item.uniqueId, out Goods gain) &&
                    item.identifier == gain.type && !found.ContainsKey(gain.id) && ShopInventory.Owned(item))
                {
                    found.Add(gain.id, new Found { Item = item, Name = ShopSettings.Text(pocket.key) });
                    if (found.Count == requested.Count) return found;
                }
            }
            foreach (Goods gain in gains)
                if (!found.ContainsKey(gain.id)) found.Add(gain.id, new Found { Name = ShopSettings.Text("f25.location_unknown") });
            return found;
        }
        private static bool SafeCargoSource(GameItem item)
        {
            bool previous = WholeStackTransfer.AllowFieldContainerCargo;
            try { WholeStackTransfer.AllowFieldContainerCargo = true; return WholeStackTransfer.SafeSource(item); }
            finally { WholeStackTransfer.AllowFieldContainerCargo = previous; }
        }

        private bool CargoDetailsVisible(Found found, Goods gain) => TripCargoRules.ExposeCargoDetails(found?.Item != null,
            PubliclyVisible(found?.Item), Exposed(found?.Item));

        private static string PublicCargoLocation(Found found) => found?.Location?.Path == null ||
            found.Location.Path.All(item => item is GameItemElement element && VisibleInventoryViews.Visible(element)) ? found?.Name : "";

        private string Reason(Goods gain, Found found)
        {
            if (found.Item == null || found.Item.unitCount != gain.qty) return "inv.changed";
            if (gain.manual || !Confirmed(found.Item)) return "f25.check";
            if (found.Location == null || !(found.Location.Ordinary || CounterRoot(found.Location) && IsReturnedCargoInCounter(found.Item)) ||
                !SafeCargoSource(found.Item)) return "f25.manual_place";
            if (!ShopInventory.Movable(found.Item)) return "f08.locked";
            return null;
        }
        private UnloadPlan PlanUnload()
        {
            bool previous = WholeStackTransfer.AllowFieldContainerCargo;
            try { WholeStackTransfer.AllowFieldContainerCargo = true; return PlanUnloadCore(); }
            finally { WholeStackTransfer.AllowFieldContainerCargo = previous; }
        }

        private static int Depth(GameItem item)
        {
            int depth = 0; var seen = new HashSet<GameItem>();
            while (item?.parentInventory?.GetParentItem() is GameItem parent)
            {
                if (!seen.Add(parent) || depth >= 512) return int.MaxValue;
                item = parent; depth++;
            }
            return depth;
        }

        private List<Goods> IncludeContents(GameItem item, Goods root)
        {
            var included = new List<Goods> { new Goods { id = root.id, qty = root.qty, type = root.type, manual = root.manual } };
            if (item.contentWindow == null) return included;
            if (!WholeStackTransfer.TryFieldContainerItems(item, out var parts)) return null;
            foreach (GameItem part in parts.Skip(1))
            {
                Goods gain = trip.gains.Find(value => value.id == part.uniqueId && value.type == part.identifier && value.qty == part.unitCount);
                if (gain == null || gain.manual || !selected.Contains(gain.id) ||
                    !CargoDetailsVisible(new Found { Item = part }, gain) ||
                    !TripCargoRules.CanUnload(Origin(part), gain.qty, part.unitCount, unloadMode.Value == 1)) return null;
                included.Add(new Goods { id = gain.id, qty = gain.qty, type = gain.type, manual = gain.manual });
            }
            return included;
        }

        private bool IncludedCurrent(List<Goods> included) => included != null && included.All(expected =>
            trip.gains.Any(gain => !gain.manual && gain.id == expected.id && gain.qty == expected.qty && gain.type == expected.type));

        private UnloadPlan PlanUnloadCore()
        {
            if (!Enabled || !ShopInventory.InStore || !Load() || !trip.returned || trip.uncertain) return null;
            if (!WholeStackTransfer.Ready) return null;
            var all = Locations();
            var chosen = trip.gains.Where(gain => selected.Contains(gain.id)).ToArray();
            var foundItems = FindAll(chosen, all);
            var plan = new UnloadPlan { Run = run, Slot = slot, Mode = unloadMode.Value };
            var reserved = new WholeStackTransfer.Reservations();
            reserved.ExcludedStacks.UnionWith(foundItems.Values.Select(found => found.Item).Where(item => item != null));
            bool useRules = settings.FeatureAvailability("f07.enabled").Allowed;
            RoutingBook book = useRules ? new RoutingStore(log).Snapshot() : new RoutingBook();
            var byKey = all.Where(location => location.Key() != null).GroupBy(location => location.Key())
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.First());
            var covered = new HashSet<int>();
            foreach (Goods gain in chosen.OrderBy(value => Depth(foundItems[value.id].Item)))
            {
                if (covered.Contains(gain.id)) continue;
                Found found = foundItems[gain.id];
                string reason = CargoDetailsVisible(found, gain) ? Reason(gain, found) : "f25.check";
                if (reason == null && !TripCargoRules.CanUnload(Origin(found.Item), gain.qty, found.Item.unitCount, unloadMode.Value == 1)) reason = "f25.check";
                List<Goods> included = reason == null ? IncludeContents(found.Item, gain) : null;
                if (reason == null && included == null) reason = "f25.manual_place";
                if (reason == null && !SourceAllowsAutomaticUnload(found.Location)) reason = "f08.shipping_disabled";
                if (reason == null && book == null) reason = "f25.routing_unavailable";
                var targets = new List<ShopInventory.Location>();
                if (reason == null)
                {
                    if (useRules && found.Item is GameItemElement element)
                        foreach (RoutingRule rule in RoutingRules.Ranked(book, CustomerIndicators.PublicTypes(element)))
                            if (byKey.TryGetValue(rule.location, out var target) && !targets.Contains(target)) targets.Add(target);
                    if (byKey.TryGetValue("root:stock", out var stock) && !targets.Contains(stock)) targets.Add(stock);
                }
                WholeStackTransfer.Plan transfer = null;
                ShopInventory.Location destination = null;
                if (reason == null)
                {
                    reason = "f25.no_destination";
                    foreach (ShopInventory.Location target in targets)
                    {
                        if (!target.Ordinary || target.IsMachine || !ShopInventory.IsMainStockBranch(target) ||
                            !InventoryRoutes.AutoReceiveAllowed(target.Inventory, found.Item, allowStockFallback: true) || !WholeStackTransfer.SafeTarget(target.Inventory) ||
                            ShopInventory.WouldContainSelf(found.Item, target.Inventory)) continue;
                        if (ReferenceEquals(target.Inventory, found.Item.parentInventory)) { reason = "f08.already"; break; }
                        transfer = WholeStackTransfer.Stack(found.Item, target.Inventory, reserved) ??
                            WholeStackTransfer.Empty(found.Item, target.Inventory as GameGridInventory, reserved);
                        if (transfer == null) { reason = "inv.noSpace"; continue; }
                        destination = target; reason = null; break;
                    }
                }
                if (reason != null) plan.Skipped.Add(new UnloadSkip { Id = gain.id, Quantity = gain.qty, Type = gain.type,
                    Item = found.Item, Location = found.Name, Reason = reason, Included = reason == "f08.already" ? included : null });
                else plan.Moves.Add(new UnloadMove { Id = gain.id, Quantity = gain.qty, Type = gain.type,
                    Source = found.Location, Target = destination, Transfer = transfer, Included = included });
                if (reason == null || reason == "f08.already")
                    foreach (Goods child in included.Skip(1)) covered.Add(child.id);
            }
            return plan;
        }
        private static bool SameLocation(ShopInventory.Location a, ShopInventory.Location b) => a != null && b != null &&
            ReferenceEquals(a.Inventory, b.Inventory) && ReferenceEquals(a.Owner, b.Owner) && a.Role == b.Role && a.Page == b.Page &&
            a.Path != null && b.Path != null && a.Path.SequenceEqual(b.Path);
        private static bool Same(UnloadPlan a, UnloadPlan b)
        {
            if (a == null || b == null || a.Run != b.Run || a.Slot != b.Slot || a.Mode != b.Mode ||
                a.Moves.Count != b.Moves.Count || a.Skipped.Count != b.Skipped.Count) return false;
            for (int i = 0; i < a.Moves.Count; i++)
                if (a.Moves[i].Id != b.Moves[i].Id || a.Moves[i].Quantity != b.Moves[i].Quantity || a.Moves[i].Type != b.Moves[i].Type ||
                    !SameLocation(a.Moves[i].Source, b.Moves[i].Source) || !SameLocation(a.Moves[i].Target, b.Moves[i].Target) ||
                    !WholeStackTransfer.Same(a.Moves[i].Transfer, b.Moves[i].Transfer) || !SameIncluded(a.Moves[i].Included, b.Moves[i].Included)) return false;
            for (int i = 0; i < a.Skipped.Count; i++)
                if (a.Skipped[i].Id != b.Skipped[i].Id || a.Skipped[i].Quantity != b.Skipped[i].Quantity ||
                    a.Skipped[i].Type != b.Skipped[i].Type || a.Skipped[i].Item != b.Skipped[i].Item || a.Skipped[i].Reason != b.Skipped[i].Reason ||
                    a.Skipped[i].Location != b.Skipped[i].Location || !SameIncluded(a.Skipped[i].Included, b.Skipped[i].Included)) return false;
            return true;
        }
        private static bool SameIncluded(List<Goods> a, List<Goods> b) => a == null || b == null ? a == b :
            a.Count == b.Count && a.Zip(b, (x, y) => x.id == y.id && x.qty == y.qty && x.type == y.type && x.manual == y.manual).All(equal => equal);
        private void CommitUnload(UnloadPlan expected)
        {
            bool previous = WholeStackTransfer.AllowFieldContainerCargo;
            try { WholeStackTransfer.AllowFieldContainerCargo = true; CommitUnloadCore(expected); }
            finally { WholeStackTransfer.AllowFieldContainerCargo = previous; }
        }
        private void CommitUnloadCore(UnloadPlan expected)
        {
            var live = PlanUnload();
            if (!Same(expected, live)) { ShopNotice.Show(ShopSettings.Text("inv.changed")); AttentionBook.Changed(); return; }
            var result = new OperationResult("f25.open");
            bool concealedReported = false;
            foreach (UnloadSkip skipped in live.Skipped)
            {
                if (skipped.Reason == "f08.already")
                {
                    if (!IncludedCurrent(skipped.Included)) { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                    var ids = new HashSet<int>(skipped.Included.Select(gain => gain.id));
                    trip.gains.RemoveAll(gain => ids.Contains(gain.id));
                    try { Save(); foreach (int id in ids) { selected.Remove(id); unloadFailures.Remove(id); } }
                    catch (Exception error) { RecordFailed(error); return; }
                    continue;
                }
                unloadFailures[skipped.Id] = skipped.Reason;
                Found found = new Found { Item = skipped.Item, Location = Locations().FirstOrDefault(location =>
                    skipped.Item != null && ReferenceEquals(location.Inventory, skipped.Item.parentInventory)), Name = skipped.Location };
                Goods gain = trip.gains.Find(item => item.id == skipped.Id);
                if (!CargoDetailsVisible(found, gain))
                {
                    if (!concealedReported) result.SkipMissing(ShopSettings.Text("f25.open"), 1, "f25.check");
                    concealedReported = true; continue;
                }
                string path = PublicCargoLocation(found);
                if (skipped.Item == null) result.SkipMissing(skipped.Type, skipped.Quantity, skipped.Reason, path, skipped.Id);
                else result.Skip(skipped.Item, skipped.Quantity, skipped.Reason, path);
            }
            bool stopped = false;
            foreach (UnloadMove move in live.Moves)
            {
                string sourceName = PublicCargoLocation(new Found { Item = move.Transfer.Item, Location = move.Source, Name = move.Source.Name });
                string path = string.IsNullOrEmpty(sourceName) ? move.Target.Name : sourceName + " → " + move.Target.Name;
                if (stopped) { result.SkipMissing(move.Type, move.Quantity, "operation.not_attempted", path, move.Id); continue; }
                if (!TripCargoRules.CanUnload(Origin(move.Transfer.Item), move.Quantity, move.Transfer.Item.unitCount, live.Mode == 1))
                { unloadFailures[move.Id] = "f25.check"; result.SkipMissing(move.Type, move.Quantity, "f25.check", path, move.Id); stopped = true; continue; }
                var identity = OperationResult.Capture(move.Transfer.Item);
                bool Scope()
                {
                    if (!Enabled || !ShopInventory.InStore || !AutomaticEnvironmentReady() || !Load() || !trip.returned || trip.uncertain || run != live.Run || slot != live.Slot ||
                        unloadMode.Value != live.Mode) return false;
                    var locations = Locations();
                    return ShopInventory.TryRefresh(move.Source, locations, out var from) && ShopInventory.TryRefresh(move.Target, locations, out var to) &&
                        (from.Ordinary || CounterRoot(from) && (!ReferenceEquals(move.Transfer.Item.parentInventory, from.Inventory) ||
                            IsReturnedCargoInCounter(move.Transfer.Item))) &&
                        (!ReferenceEquals(move.Transfer.Item.parentInventory, from.Inventory) ||
                            CargoDetailsVisible(new Found { Item = move.Transfer.Item, Location = from },
                                trip.gains.Find(gain => gain.id == move.Id))) &&
                        to.Ordinary && ShopInventory.IsMainStockBranch(to) && from.Inventory != to.Inventory &&
                        SourceAllowsAutomaticUnload(from) && InventoryRoutes.AutoReceiveAllowed(to.Inventory, move.Transfer.Item, allowStockFallback: true) &&
                        IncludedCurrent(move.Included);
                }
                bool Record()
                {
                    if (!Load() || run != live.Run || slot != live.Slot) return false;
                    if (!IncludedCurrent(move.Included)) return false;
                    var ids = new HashSet<int>(move.Included.Select(gain => gain.id));
                    trip.gains.RemoveAll(gain => ids.Contains(gain.id));
                    try { Save(); foreach (int id in ids) { selected.Remove(id); unloadFailures.Remove(id); } return true; }
                    catch (Exception error) { log("F25 could not save completed unload: " + error); return false; }
                }
                var outcome = WholeStackTransfer.Apply(move.Transfer, "f25.open", path, move.Source.Key(true), move.Target.Key(true), Scope, Record);
                if (outcome == WholeStackTransfer.Result.Applied) result.CompleteCaptured(identity, move.Quantity, path);
                else
                {
                    result.SkipCaptured(identity, move.Quantity, outcome == WholeStackTransfer.Result.Unchanged ? "inv.changed" : "f08.pending", path);
                    unloadFailures[move.Id] = outcome == WholeStackTransfer.Result.Unchanged ? "inv.changed" : "f08.pending";
                    stopped = true;
                }
            }
            AttentionBook.Changed();
            ShopNotice.Report(result);
        }

        public void Dispose()
        {
            settings.Changed -= Changed; ModHook.OnLeavingStoreEarly -= Close; ModHook.OnGameLoadedInit -= Reset;
            AttentionBook.Unregister("trip_cargo");
            if (WholeStackTransfer.ResolveTrip == ResolvePending) WholeStackTransfer.ResolveTrip = null;
            WholeStackTransfer.Release();
            if (ReferenceEquals(active, this)) active = null;
            Reset();
        }
    }
}
