using System;
using System.Linq;

namespace ShopAssist
{
    // Rule data follows the game's save and never uses a separate sidecar.
    internal sealed class RoutingStore
    {
        private const string DataKey = "routing.v2";
        private readonly Action<string> log;
        private RoutingBook book;
        private string run;
        private int slot;
        private bool attempted;
        internal int Revision { get; private set; }

        internal RoutingStore(Action<string> log) { this.log = log ?? throw new ArgumentNullException(nameof(log)); }

        internal void Reset()
        {
            book = null;
            run = null;
            attempted = false;
            Revision++;
        }

        internal bool Load()
        {
            if (!ShopInventory.InStore) return false;
            if (attempted && run == PlayerStore.Instance.runID && slot == PlayerStore.Instance.saveSlotId) return book != null;
            Reset();
            attempted = true;
            run = PlayerStore.Instance.runID;
            slot = PlayerStore.Instance.saveSlotId;
            try
            {
                if (PlayerStore.Instance.ModData() == null) throw new InvalidOperationException("Save data is unavailable.");
                string raw = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                book = raw == null ? new RoutingBook() : ShopData.Decode<RoutingBook>(raw, 65536);
                if (!RoutingRules.Valid(book, TypeHelper.ALL_TYPES_LIST)) throw new FormatException("Invalid routing data or unsupported version.");
                return true;
            }
            catch (Exception error)
            {
                book = null;
                log("F07 kept unreadable routing data: " + error.Message);
                return false;
            }
        }

        internal RoutingBook Snapshot()
        {
            if (!Load()) return null;
            return new RoutingBook { rules = book.rules.Select(rule => rule.Copy()).ToList() };
        }

        internal RoutingRule Get(ShopInventory.Location location)
        {
            if (!Load() || location == null || !ShopInventory.IsMainStockBranch(location)) return null;
            string key = location.Key();
            return key == null ? null : book.rules.Find(rule => rule.location == key)?.Copy();
        }

        internal bool Save(ShopInventory.Location previous, RoutingRule draft)
        {
            if (!Load() || previous == null || draft == null || !ShopInventory.TryRefresh(previous, out ShopInventory.Location location) ||
                !ShopInventory.IsMainStockBranch(location)) return false;
            RoutingRule candidate = draft.Copy();
            candidate.location = location.Key() ?? "item:1:" + location.Role + ":" + location.Page;
            if (!RoutingRules.Valid(candidate, TypeHelper.ALL_TYPES_LIST)) return false;
            if (book.rules.Count >= RoutingRules.MaxRules && book.rules.All(rule => rule.location != location.Key())) return false;
            // A permanent ID is allocated only for the player's explicit Save action.
            string key = location.Key(true);
            if (!RoutingRules.ValidLocation(key)) return false;
            int matches = ShopInventory.OwnedLocations(false).Count(other => other.Key() == key);
            if (matches != 1 || !ShopInventory.TryRefresh(location, out ShopInventory.Location refreshed) ||
                !ShopInventory.IsMainStockBranch(refreshed)) return false;
            candidate.location = key;
            RoutingBook next = Snapshot();
            if (next == null) return false;
            int existing = next.rules.FindIndex(rule => rule.location == key);
            if (existing < 0) next.rules.Add(candidate); else next.rules[existing] = candidate;
            return Write(next);
        }

        internal bool Remove(ShopInventory.Location previous)
        {
            if (!Load() || previous == null || !ShopInventory.TryRefresh(previous, out ShopInventory.Location location) ||
                !ShopInventory.IsMainStockBranch(location)) return false;
            string key = location.Key();
            if (key == null) return false;
            RoutingBook next = Snapshot();
            if (next == null || next.rules.RemoveAll(rule => rule.location == key) != 1) return false;
            return Write(next);
        }

        private bool Write(RoutingBook next)
        {
            if (!ShopInventory.InStore || PlayerStore.Instance.runID != run || PlayerStore.Instance.saveSlotId != slot ||
                PlayerStore.Instance.ModData() == null || !RoutingRules.Valid(next, TypeHelper.ALL_TYPES_LIST)) return false;
            try
            {
                string raw = ShopData.Encode(next);
                if (raw.Length > 65536) return false;
                ModHelper.SetModData(ModEntry.Id, DataKey, raw);
                if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != raw) throw new InvalidOperationException("The game did not retain routing data.");
                book = next;
                Revision++;
                return true;
            }
            catch (Exception error) { log("F07 routing data could not be saved: " + error); return false; }
        }
    }
}
