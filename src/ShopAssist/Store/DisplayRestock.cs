using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ShopAssist
{
    internal sealed class DisplayRestock : IDisposable
    {
        private static DisplayRestock active;
        internal static bool CanOpenFor(GameItem item) => active?.enabled.Value == true && ShopInventory.InStore && active.Eligible(item) &&
            ShopInventory.OwnedLocations(false).Any(l => l.Ordinary && l.Inventory == item.parentInventory);
        internal static void OpenFor(GameItem item)
        {
            if (!CanOpenFor(item) || !active.Ready()) return;
            active.source = ShopInventory.OwnedLocations(false).First(l => l.Ordinary && l.Inventory == item.parentInventory);
            active.type = item.identifier; active.Draw();
        }
        private const string Id = ModEntry.Id + ".restock";
        private readonly ShopSettings settings;
        private readonly Action<string> log;
        private readonly ConfigEntry<bool> enabled, costRequired;
        private readonly ConfigEntry<int> goal, keep;
        private Rules rules;
        private string run;
        private int slot;
        private CustomUIWindow window;
        private ShopInventory.Location source;
        private string type;

        private sealed class Rules
        {
            [JsonRequired] public int version = 1;
            [JsonRequired] public List<Line> lines = new List<Line>();
        }
        private sealed class Line
        {
            [JsonRequired] public string source, type, label;
            [JsonRequired] public int goal, keep;
        }
        private sealed class Move
        {
            internal GameItem Item;
            internal GameInventory From;
            internal GridShape Home, To;
            internal int Count, SourceCount;
        }
        private sealed class Shortfall
        {
            internal string Label, Location;
            internal int Count;
            internal readonly List<string> Reasons = new List<string>();
        }

        internal DisplayRestock(ShopSettings settings, Action<string> log)
        {
            this.settings = settings; this.log = log;
            enabled = settings.Toggle("f29.enabled", "f29.tip");
            costRequired = settings.Toggle("f29.price", "f29.price.tip", true);
            goal = settings.Integer("f29.goal", "f29.goal.tip", 4, 1, 99);
            keep = settings.Integer("f29.keep", "f29.keep.tip", 2, 0, 99);
            settings.Action("f29.open", Draw, () => enabled.Value && ShopInventory.InStore);
        }
        internal void Start() { active = this; settings.Changed += Changed; ModHook.OnGameLoadedInit += Reset; ModHook.OnLeavingStoreEarly += Close; }
        private void Changed() { if (!enabled.Value) Close(); }
        private void Close() { if (window != null && window.IsAlive) window.Close(); window = null; }
        private void Reset() { Close(); rules = null; run = null; source = null; type = null; }
        private bool Ready()
        {
            if (!enabled.Value || !ShopInventory.InStore || CustomUIManager.Instance == null) return false;
            var p = PlayerStore.Instance;
            if (run == p.runID && slot == p.saveSlotId) return rules != null;
            Reset(); run = p.runID; slot = p.saveSlotId;
            try
            {
                var raw = ModHelper.GetModData(ModEntry.Id, "displayRules", null);
                rules = raw == null ? new Rules() : ShopData.Decode<Rules>(raw, 65536);
                if (rules == null || rules.version != 1 || rules.lines == null || rules.lines.Count > 64 ||
                    rules.lines.Any(l => l == null || string.IsNullOrEmpty(l.source) || string.IsNullOrEmpty(l.type) ||
                        l.goal < 1 || l.goal > 99 || l.keep < 0 || l.keep > 99 || string.IsNullOrEmpty(l.label))) throw new FormatException("Invalid display rules");
            }
            catch (Exception e) { rules = null; log("F29: unreadable display rules retained. " + e.Message); }
            return rules != null;
        }
        private void Save() => ModHelper.SetModData(ModEntry.Id, "displayRules", ShopData.Encode(rules));
        private bool Eligible(GameItem item) => EligibleStock(item) && !RequestBook.Reserved(item);
        private bool EligibleStock(GameItem item) => ShopInventory.Movable(item) && item.contentWindow == null &&
            !item.IsTag("STOLEN_TAG") && !item.IsTag("CONTRABAND_ITEM_TAG") &&
            !item.IsTag("IMPORTANT_TAG") && (!costRequired.Value || item.IsTag("BOUGHT_PRICE_TAG"));

        private CustomUIBuilder Window()
        {
            var b = UiLayout.Rebuild(ref window, Id, ShopSettings.Text("f29.open"), 590, 420);
            window = b.GetWindow(); b.OnClose(() => window = null);
            return b;
        }
        private void Draw()
        {
            if (!Ready()) return;
            var b = Window();
            var locations = ShopInventory.OwnedLocations(false).Where(l => l.Ordinary).ToList();
            var reserved = RequestBook.ReservedItems(locations);
            if (source != null) source = locations.Find(l => l.Inventory == source.Inventory);
            if (source == null) source = locations.FirstOrDefault();
            if (source == null) { b.AddLabel(ShopSettings.Text("inv.changed")).Show(); return; }
            b.BeginRow();
            VisualChoices.Location(b, ShopSettings.Text("visual.source"), source, locations, picked =>
            { source = picked; type = null; Draw(); }, "source");
            var samples = source.Inventory.childItems.Where(item => !reserved.Contains(item) && EligibleStock(item)).GroupBy(i => i.identifier).Select(g => g.First()).ToList();
            var selected = samples.FirstOrDefault(i => i.identifier == type);
            VisualChoices.Slot(b, ShopSettings.Text("f29.choose"), selected, selected?.GetDisplayName(true), () =>
                VisualChoices.ChooseItem(ShopSettings.Text("f29.choose"), samples, selected, i => { type = i.identifier; Draw(); }), "type");
            b.End();
            b.AddButton(ShopSettings.Format("f29.add", goal.Value, keep.Value), () =>
            {
                if (!Ready() || !ShopInventory.TryRefresh(source, out var live)) return;
                var item = live.Inventory.childItems.FirstOrDefault(i => i.identifier == type && Eligible(i));
                if (item == null) return;
                var line = rules.lines.Find(l => l.type == type);
                if (line == null) { if (rules.lines.Count >= 64) return; rules.lines.Add(line = new Line()); }
                line.type = type; line.source = live.Key(true); line.label = item.GetDisplayName(true);
                line.goal = goal.Value; line.keep = keep.Value; Save(); Draw();
            }, "add");
            b.BeginScrollFlexible(110);
            foreach (var r in rules.lines)
            {
                string where = locations.Find(l => l.Key() == r.source)?.Name ?? ShopSettings.Text("inv.changed");
                int have = Count(EmporiumEntry.Instance.showcaseElement, r.type);
                b.BeginRow().AddLabel(ShopSettings.Format("f29.row", r.label, have, r.goal, r.keep, where))
                    .AddButton("×", () => { if (Ready()) { rules.lines.Remove(r); Save(); Draw(); } }).End();
            }
            b.End().AddButton(ShopSettings.Text("f29.preview"), Preview, "preview").Show();
        }
        private static int Count(GameInventory grid, string id) => grid.childItems.Where(i => i.identifier == id && ShopInventory.Owned(i)).Sum(i => i.unitCount);
        private List<Move> Plan(out int shortfall, out List<Shortfall> reasons)
        {
            var result = new List<Move>();
            var taken = new List<GridShape>();
            var locations = ShopInventory.OwnedLocations(false);
            var reserved = RequestBook.ReservedItems(locations);
            var display = EmporiumEntry.Instance.showcaseElement;
            shortfall = 0;
            reasons = new List<Shortfall>();
            foreach (var r in rules.lines)
            {
                int need = Math.Max(0, r.goal - Count(display, r.type));
                if (need == 0) continue;
                var stock = locations.Find(l => l.Ordinary && l.Key() == r.source);
                if (stock != null)
                {
                    int left = Math.Max(0, Count(stock.Inventory, r.type) - r.keep);
                    bool blocked = false, noSpace = false, noRecovery = false;
                    foreach (var item in stock.Inventory.childItems.Where(i => i.identifier == r.type).OrderByDescending(i => i.unitCount))
                    {
                        if (reserved.Contains(item) || !EligibleStock(item)) { blocked = true; continue; }
                        int remaining = item.unitCount, removable = Math.Min(remaining, item.MaxNumRemove());
                        while (need > 0 && left > 0 && remaining > 0 && removable > 0)
                        {
                            int desired = Math.Min(Math.Min(need, left), Math.Min(remaining, removable));
                            var marker = GridPlacements.EmptyUpTo(display, item, taken, desired);
                            if (marker == null) { noSpace = true; break; }
                            int n = Math.Min(desired, marker.numTransfer);
                            if (n < remaining && !HasRecoverySpace(item, n, locations, display))
                            { noRecovery = true; break; }
                            var to = marker.itemGridShape.Clone();
                            taken.Add(to);
                            result.Add(new Move { Item = item, From = stock.Inventory, Home = item.modifiedShape.Clone(), To = to,
                                Count = n, SourceCount = remaining });
                            need -= n; left -= n; remaining -= n; removable -= n;
                        }
                        if (need > 0 && left > 0 && remaining > 0 && removable == 0) blocked = true;
                        if (need == 0 || left == 0) break;
                    }
                    if (need > 0)
                    {
                        var shortage = new Shortfall { Label = r.label, Location = stock.Name, Count = need };
                        if (noSpace) shortage.Reasons.Add("f29.target_blocked");
                        if (noRecovery) shortage.Reasons.Add("f29.recovery_space");
                        if (blocked) shortage.Reasons.Add("f29.blocked");
                        if (left == 0) shortage.Reasons.Add("f29.keep_limit");
                        if (shortage.Reasons.Count == 0) shortage.Reasons.Add("f29.blocked");
                        reasons.Add(shortage);
                    }
                }
                else
                {
                    var shortage = new Shortfall { Label = r.label, Count = need };
                    shortage.Reasons.Add("f29.source_unavailable"); reasons.Add(shortage);
                }
                shortfall += need;
            }
            return result;
        }

        private static bool HasRecoverySpace(GameItem item, int amount, IEnumerable<ShopInventory.Location> locations, GameGridInventory display)
        {
            foreach (var location in locations.OrderByDescending(l => l.Inventory == item.parentInventory))
            {
                if (!location.Ordinary || location.Inventory == display || !(location.Inventory is GameGridInventory grid) ||
                    ShopInventory.WouldContainSelf(item, grid)) continue;
                // Native placement can ignore the moving item's own footprint.
                // A partial split leaves it occupied, so reserve those cells here.
                var occupied = ReferenceEquals(grid, item.parentInventory) ? new List<GridShape> { item.modifiedShape } : null;
                if (GridPlacements.Empty(grid, item, occupied, amount) != null) return true;
            }
            return false;
        }
        private void Preview()
        {
            if (!Ready()) return;
            var plan = Plan(out int missing, out List<Shortfall> reasons);
            var b = Window();
            b.AddLabel(ShopSettings.Format("f29.summary", plan.Sum(m => m.Count), missing));
            b.BeginScrollFlexible(125);
            var groups = plan.GroupBy(move => move.Item).ToList();
            ItemGrid.Add(b, groups.Select(group => new ItemGrid.Entry(group.Key) { Selected = true, Badge = group.Sum(move => move.Count).ToString() }),
                new ItemGrid.Options { Columns = 20, CellSize = 24 });
            foreach (var group in groups)
            {
                int count = group.Sum(move => move.Count);
                b.AddLabel(ShopSettings.Format("f29.plan_line", group.Key.GetDisplayName(true), count, group.First().SourceCount - count));
                if (b.Last.text != null) b.Last.text.richText = false;
            }
            foreach (Shortfall reason in reasons)
            {
                b.AddLabel(ShopSettings.Format("f29.shortfall_line", reason.Label, reason.Count,
                    string.Join(" · ", reason.Reasons.Select(ShopSettings.Text))));
                if (b.Last.text != null) b.Last.text.richText = false;
            }
            b.End().BeginRow().AddButton(ShopSettings.Text("f29.back"), Draw)
                .AddButton(ShopSettings.Text("f29.apply"), () => Commit(plan, reasons), "apply");
            b.Last.SetInteractable(plan.Count != 0);
            b.End().Show();
        }
        private void Commit(List<Move> expected, List<Shortfall> expectedShortfalls)
        {
            if (!Ready()) return;
            var plan = Plan(out _, out List<Shortfall> shortfalls);
            if (plan.Count == 0) { Preview(); return; }
            if (shortfalls.Count != expectedShortfalls.Count || shortfalls.Where((s, i) => s.Label != expectedShortfalls[i].Label ||
                s.Location != expectedShortfalls[i].Location || s.Count != expectedShortfalls[i].Count ||
                !s.Reasons.SequenceEqual(expectedShortfalls[i].Reasons)).Any() ||
                plan.Count != expected.Count || plan.Where((m, i) => m.Item != expected[i].Item || m.From != expected[i].From ||
                m.Count != expected[i].Count || m.SourceCount != expected[i].SourceCount ||
                !InventorySortPlan.SameShape(m.Home, expected[i].Home) ||
                m.To.minX != expected[i].To.minX || m.To.minY != expected[i].To.minY ||
                m.To.orientation != expected[i].To.orientation || m.To.flipped != expected[i].To.flipped).Any())
            { Preview(); ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            var result = new OperationResult("f29.open");
            foreach (Shortfall shortage in shortfalls)
                result.SkipMissing(shortage.Label, shortage.Count, shortage.Reasons[0], shortage.Location);
            var target = EmporiumEntry.Instance.showcaseElement;
            var locations = ShopInventory.OwnedLocations(false);
            bool stopped = false;
            foreach (var m in plan)
            {
                string where = locations.Find(location => location.Inventory == m.From)?.Name ?? ShopSettings.Text("inv.stock");
                string path = where + " → " + ShopSettings.Text("inv.showcase");
                if (stopped) { result.Skip(m.Item, m.Count, "operation.not_attempted", path); continue; }
                var slot = GridPlacements.Exact(target, m.Item, m.To, m.Count);
                if (slot == null || m.Item.parentInventory != m.From || m.Item.unitCount != m.SourceCount || !Eligible(m.Item))
                { result.Skip(m.Item, m.Count, "inv.changed", path); stopped = true; continue; }
                if (m.Count < m.SourceCount && !HasRecoverySpace(m.Item, m.Count, ShopInventory.OwnedLocations(false), target))
                { result.Skip(m.Item, m.Count, "f29.recovery_space", path); continue; }
                // GetSplitItem replaces marker.item for a partial native transfer.
                // Give the original a stable identity before a failure can occur.
                m.Item.GetUniqueID();
                int accepted = 0;
                try { accepted = slot.TryAcceptOnce(m.Count); }
                catch (Exception error) { log("F29 native placement interrupted: " + error); }
                GameItem placed = slot.item;
                bool partial = m.Count < m.SourceCount;
                bool sourceOk = partial ? m.Item.parentInventory == m.From && m.From.childItems.Contains(m.Item) &&
                    m.Item.unitCount == m.SourceCount - m.Count && InventorySortPlan.SameShape(m.Item.modifiedShape, m.Home) :
                    !m.From.childItems.Contains(m.Item);
                if (accepted == m.Count && placed != null && placed.parentInventory == target && target.childItems.Contains(placed) &&
                    placed.unitCount == m.Count && sourceOk && InventorySortPlan.SameShape(placed.modifiedShape, m.To))
                {
                    placed.GetUniqueID();
                    result.Complete(placed, m.Count, path);
                    if (GenericConfirmationUIManager.Instance?.panel.activeSelf == true) stopped = true;
                    continue;
                }
                bool restored = Restore(m, placed, target);
                GameItem affected = !restored && placed != null && !ReferenceEquals(placed, m.Item) ? placed : m.Item;
                string actual = !restored ? ShopInventory.OwnedLocations(false)
                    .Find(location => location.Inventory == affected?.parentInventory)?.Name : null;
                result.Skip(affected, m.Count, restored ? "inv.changed" : "inv.inspect", actual ?? path, !restored);
                stopped = true;
            }
            Draw(); ShopNotice.Report(result);
        }

        private bool Restore(Move move, GameItem placed, GameGridInventory target)
        {
            try
            {
                if (move.Item.parentInventory != null && move.Item.parentInventory != move.From && move.Item.parentInventory != target)
                    return false;
                if (placed != null && placed.parentInventory != null && placed.parentInventory != move.From && placed.parentInventory != target)
                    return false;
                bool partial = move.Count < move.SourceCount;
                if (partial && placed != null && !ReferenceEquals(placed, move.Item) &&
                    move.Item.parentInventory == move.From && move.Item.unitCount + placed.unitCount == move.SourceCount &&
                    placed.unitCount == move.Count)
                {
                    var back = move.Item.GetStackSlot(placed);
                    if (back != null && back.targetItem == move.Item && back.numTransfer >= move.Count)
                        back.TryAcceptOnce(move.Count);
                    if (placed.unitCount > 0 && (placed.parentInventory == null || placed.parentInventory == target))
                    {
                        var safe = GridPlacements.Empty(move.From as GameGridInventory, placed, null, placed.unitCount);
                        if (safe != null) safe.TryAcceptOnce(placed.unitCount);
                    }
                    if (placed.unitCount > 0 && placed.parentInventory == null)
                    {
                        foreach (var location in ShopInventory.OwnedLocations(false))
                        {
                            if (!location.Ordinary || location.Inventory == move.From || !(location.Inventory is GameGridInventory fallbackGrid) ||
                                ShopInventory.WouldContainSelf(placed, location.Inventory)) continue;
                            var safe = GridPlacements.Empty(fallbackGrid, placed, null, placed.unitCount);
                            if (safe == null) continue;
                            safe.TryAcceptOnce(placed.unitCount);
                            break;
                        }
                    }
                }
                else if (!partial && move.Item.parentInventory != move.From && move.Item.unitCount == move.SourceCount)
                {
                    var back = GridPlacements.Exact(move.From as GameGridInventory, move.Item, move.Home, move.SourceCount);
                    back?.TryAcceptOnce(move.SourceCount);
                }
                if (move.Item.parentInventory == move.From && !InventorySortPlan.SameShape(move.Item.modifiedShape, move.Home) &&
                    move.From is GameGridInventory grid && grid.childItems.Contains(move.Item) &&
                    grid.CheckShapePlacement(move.Item, move.Home, new List<GameItem> { move.Item }))
                {
                    move.Item.modifiedShape = new GridShapeBuilder(move.Item.modifiedShape).SetTransform(move.Home);
                    grid.Validate();
                }
                bool restored = move.Item.parentInventory == move.From && move.From.childItems.Contains(move.Item) &&
                    move.Item.unitCount == move.SourceCount && InventorySortPlan.SameShape(move.Item.modifiedShape, move.Home) &&
                    !target.childItems.Contains(move.Item) && (placed == null || ReferenceEquals(placed, move.Item) ||
                    (placed.unitCount == 0 && placed.parentInventory == null && !target.childItems.Contains(placed)));
                if (restored && placed != null && !ReferenceEquals(placed, move.Item) &&
                    (!(placed is GameItemElement element) || !element.IsDestroyed())) placed.Destroy();
                return restored;
            }
            catch (Exception error) { log("F29 placement recovery interrupted: " + error); return false; }
        }
        public void Dispose() { if (active == this) active = null; settings.Changed -= Changed; ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Close; Reset(); }
    }
}
