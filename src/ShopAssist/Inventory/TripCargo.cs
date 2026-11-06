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
        internal static bool CanOpenFor(GameItem item) => active?.enabled.Value == true && ShopInventory.InStore &&
            item != null && active.Load() && active.trip.returned && item.uniqueId == active.trip.bag;
        internal static void OpenFor(GameItem item) { if (CanOpenFor(item)) active.Draw(); }
        private readonly ShopSettings settings;
        private readonly ConfigEntry<bool> enabled;
        private readonly Action<string> log;
        private CustomUIWindow window;
        private string run;
        private int slot;
        private Trip trip;

        private sealed class Trip
        {
            [JsonRequired] public int version = 1;
            [JsonRequired] public int bag;
            [JsonRequired] public bool returned;
            [JsonRequired] public List<Goods> outward = new List<Goods>();
            [JsonRequired] public List<Goods> gains = new List<Goods>();
            public string dest = "root:stock";
        }
        private sealed class Goods
        {
            [JsonRequired] public int id, qty;
            [JsonRequired] public string type;
            public bool manual;
        }

        private readonly HashSet<int> selected = new HashSet<int>();
        private int page;

        internal TripCargo(ShopSettings settings, Action<string> log)
        {
            this.settings = settings;
            this.log = log;
            enabled = settings.Toggle("f25.enabled", "f25.tip");
            settings.Action("f25.open", Draw, () => enabled.Value && ShopInventory.InStore);
        }
        internal void Start()
        {
            active = this;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Close;
            settings.Changed += Changed;
        }
        private void Changed() { if (!enabled.Value) Close(); }
        private void Close() { if (window != null && window.IsAlive) window.Close(); window = null; selected.Clear(); }
        private void Reset() { Close(); trip = null; run = null; page = 0; }
        private static GameGridInventory Grid(GameItem bag) => ShopInventory.SafeContainerForPeek(bag) && bag.IsTag("BACKPACK_TAG")
            ? bag.contentWindow.childElement as GameGridInventory : null;

        private bool Load()
        {
            if (!PlayerStore.HasInstance) return false;
            var p = PlayerStore.Instance;
            if (p.saveSlotId < 0 || string.IsNullOrEmpty(p.runID)) return false;
            if (p.runID == run && p.saveSlotId == slot) return trip != null;
            Reset(); run = p.runID; slot = p.saveSlotId;
            try
            {
                var raw = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                trip = raw == null ? new Trip() : ShopData.Decode<Trip>(raw, 131072);
                if (trip == null || trip.version != 1 || trip.bag < 0 || !Valid(trip.outward) || !Valid(trip.gains)) throw new FormatException("Invalid trip cargo");
            }
            catch (Exception e) { trip = null; log("F25: unreadable trip book retained. " + e.Message); }
            return trip != null;
        }
        private static bool Valid(List<Goods> list) => list != null && list.Count <= 512 &&
            list.All(g => g != null && g.id > 0 && g.qty > 0 && !string.IsNullOrEmpty(g.type)) &&
            list.Select(g => g.id).Distinct().Count() == list.Count;
        private void Save() => ModHelper.SetModData(ModEntry.Id, DataKey, ShopData.Encode(trip));
        private void RecordFailed(Exception error)
        {
            if (error != null) log("F25 trip record failed: " + error);
            ShopNotice.Show(ShopSettings.Text("f25.record_failed"));
        }
        private static List<Goods> Snapshot(GameGridInventory grid) => grid.childItems.Where(ShopInventory.Owned)
            .Select(i => new Goods { id = i.GetUniqueID(), type = i.identifier, qty = i.unitCount }).ToList();

        [HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.TransferBackpackToInv))]
        private static class Departure
        {
            private static void Prefix(EmporiumEntry __instance, out GameItem __state) => __state = __instance?.selectedBackpack;
            private static void Postfix(EmporiumEntry __instance, GameItem __state)
            {
                var a = active;
                if (a == null || !a.enabled.Value) return;
                try
                {
                    if (!a.Load()) { a.RecordFailed(null); return; }
                    // The map may open without an outing. Only this native transfer starts a new record.
                    var bag = __instance.afterhourPocketSlotInvBackpack.childItem;
                    var grid = bag != null && ReferenceEquals(bag, __state) ? ShopInventory.OutingBag(bag) : null;
                    string destination = a.trip.dest;
                    a.trip = new Trip { dest = destination };
                    if (grid != null) { a.trip.bag = bag.GetUniqueID(); a.trip.outward = Snapshot(grid); }
                    a.Save();
                }
                catch (Exception error) { a.RecordFailed(error); }
            }
        }

        [HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.TransferAfterhourToInv))]
        private static class Arrival
        {
            private static void Prefix(EmporiumEntry __instance, out GameItem __state) => __state = __instance?.afterhourPocketSlotInvBackpack?.childItem;
            private static void Postfix(GameItem __state)
            {
                var a = active;
                if (a == null || !a.enabled.Value) return;
                try
                {
                    if (!a.Load()) { a.RecordFailed(null); return; }
                    if (__state == null || a.trip.returned || __state.uniqueId <= 0 || __state.uniqueId != a.trip.bag) return;
                    var grid = Grid(__state);
                    if (grid == null || !ShopInventory.OwnedLocations(false).Any(l => l.Inventory == grid && l.Ordinary)) return;
                    var before = a.trip.outward;
                    var after = Snapshot(grid);
                    foreach (var group in after.GroupBy(s => s.type))
                    {
                        var old = before.Where(o => o.type == group.Key).ToList();
                        long gain = group.Sum(g => (long)g.qty) - old.Sum(g => (long)g.qty);
                        bool merged = old.Any(o => !group.Any(g => g.id == o.id && g.qty >= o.qty));
                        foreach (var g in group)
                        {
                            int initial = old.Find(o => o.id == g.id)?.qty ?? 0;
                            int extra = (int)Math.Max(0, Math.Min(gain, g.qty - (long)initial));
                            if (extra == 0) continue;
                            a.trip.gains.Add(new Goods { id = g.id, type = g.type, qty = extra, manual = merged || extra != g.qty });
                            gain -= extra;
                        }
                    }
                    a.trip.returned = true;
                    a.Save();
                }
                catch (Exception error) { a.RecordFailed(error); }
            }
        }

        private void Draw()
        {
            if (!enabled.Value || !ShopInventory.InStore || !Load()) return;
            bool fresh = window == null || !window.IsAlive;
            var b = UiLayout.Rebuild(ref window, Id, ShopSettings.Text("f25.open"), 570, 400);
            b.OnClose(() => window = null);
            if (!trip.returned || trip.gains.Count == 0) { b.AddLabel(ShopSettings.Text("f25.none")).Show(); return; }
            if (fresh) { selected.Clear(); foreach (var g in trip.gains) if (!g.manual) selected.Add(g.id); }
            var locations = ShopInventory.OwnedLocations(false);
            var bag = locations.Find(l => l.Ordinary && l.Owner?.uniqueId == trip.bag);
            if (bag == null) { b.AddLabel(ShopSettings.Text("f25.bagmissing")).Show(); return; }
            var dests = locations.Where(l => l.Ordinary && l != bag && !l.Inventory.IsInsertLocked() &&
                !l.Path.Contains(bag.Owner)).ToList();
            int dest = dests.FindIndex(l => l.Key() == trip.dest);
            VisualChoices.Location(b, ShopSettings.Text("visual.target"), dest >= 0 ? dests[dest] : null, dests, live =>
            { if (enabled.Value && Load()) { trip.dest = live.Key(true); Save(); Draw(); } }, "destination");
            page = Math.Max(0, Math.Min(page, (trip.gains.Count - 1) / 18));
            b.BeginScrollFlexible(120);
            var tiles = new List<ItemGrid.Entry>();
            foreach (var gain in trip.gains.Skip(page * 18).Take(18))
            {
                var item = bag.Inventory.childItems.Find(i => i.uniqueId == gain.id && i.identifier == gain.type);
                if (item == null || item.unitCount != gain.qty)
                {
                    string label = item == null ? gain.type : item.GetDisplayName(true) + " × " + gain.qty;
                    b.BeginRow().AddLabel(label + " · " + ShopSettings.Text(item == null ? "inv.changed" : "f25.split"))
                        .AddButton(ShopSettings.Text("f25.handled"), () =>
                        {
                            if (Load() && trip.gains.Remove(gain)) { selected.Remove(gain.id); Save(); Draw(); }
                        }).End();
                    continue;
                }
                tiles.Add(new ItemGrid.Entry(item) { Selected = selected.Contains(gain.id),
                    Badge = gain.manual ? ShopSettings.Text("f25.check") : "" });
            }
            ItemGrid.Add(b, tiles, new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 125, CardHeight = 92,
                OnClick = entry => { if (!selected.Add(entry.Item.uniqueId)) selected.Remove(entry.Item.uniqueId); entry.Selected = selected.Contains(entry.Item.uniqueId); } });
            b.End();
            UiLayout.Pager(b, page, trip.gains.Count, 18, next => { page = next; Draw(); });
            b.AddButton(ShopSettings.Text("f25.unload"), Unload, "unload").Show();
        }

        private void Unload()
        {
            if (!enabled.Value || !ShopInventory.InStore || !Load() || !trip.returned) return;
            var all = ShopInventory.OwnedLocations(false);
            var bag = all.Find(l => l.Ordinary && l.Owner?.uniqueId == trip.bag);
            var dest = all.Find(l => l.Ordinary && l.Key() == trip.dest);
            if (bag == null) { ShopNotice.Show(ShopSettings.Text("f25.bagmissing")); return; }
            if (dest == null || bag.Inventory == dest.Inventory || dest.Path.Contains(bag.Owner))
            { ShopNotice.Show(ShopSettings.Text("f25.choose")); return; }
            var chosen = trip.gains.Where(gain => selected.Contains(gain.id)).ToArray();
            if (chosen.Length == 0) { ShopNotice.Show(ShopSettings.Text("f25.select")); return; }
            var result = new OperationResult("f25.open");
            string path = bag.Name + " → " + dest.Name;
            bool stopped = false;
            foreach (var gain in chosen)
            {
                var item = bag.Inventory.childItems.Find(i => i.uniqueId == gain.id && i.identifier == gain.type);
                if (stopped)
                {
                    if (item == null) result.SkipMissing(gain.type, gain.qty, "operation.not_attempted", path, gain.id);
                    else result.Skip(item, gain.qty, "operation.not_attempted", path);
                    continue;
                }
                if (item == null) { result.SkipMissing(gain.type, gain.qty, "inv.changed", path, gain.id); continue; }
                if (!ShopInventory.Movable(item) || RequestBook.Reserved(item))
                { result.Skip(item, gain.qty, "f08.locked", path); continue; }
                // Keep the outbound quantity even if the player moved or split the stack after returning.
                int floor = trip.outward.Find(g => g.id == item.uniqueId)?.qty ?? 0;
                int qty = Math.Min(gain.qty, item.unitCount - floor);
                // Vanilla GetSplitItem reconstructs from the identifier, losing per-instance state.
                if (qty != item.unitCount || qty != gain.qty)
                { result.Skip(item, gain.qty, "f25.split", path); continue; }
                if (ShopInventory.WouldContainSelf(item, dest.Inventory))
                { result.Skip(item, gain.qty, "f08.cycle", path); continue; }
                if (!ShopInventory.TryMoveIntact(item, bag, dest, out string reason))
                {
                    result.Skip(item, gain.qty, reason, path);
                    if (reason == "inv.inspect") stopped = true;
                    continue;
                }
                gain.qty -= qty;
                if (gain.qty == 0) trip.gains.Remove(gain);
                try { Save(); result.Complete(item, qty, path); }
                catch (Exception error)
                {
                    log("F25 could not save completed unload: " + error);
                    result.Skip(item, qty, "inv.inspect", path, true);
                    stopped = true;
                }
            }
            Draw(); ShopNotice.Report(result);
        }

        public void Dispose()
        {
            settings.Changed -= Changed; ModHook.OnLeavingStoreEarly -= Close; ModHook.OnGameLoadedInit -= Reset;
            if (ReferenceEquals(active, this)) active = null;
            Reset();
        }
    }
}
