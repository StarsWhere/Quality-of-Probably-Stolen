using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ShopAssist
{
    internal sealed class FieldPacking : IDisposable
    {
        private readonly ShopSettings settings;
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<int> reserve;
        private readonly Func<KeyCode> key;
        private readonly Dictionary<GameItem, long> values = new Dictionary<GameItem, long>();
        private readonly HashSet<GameItem> priority = new HashSet<GameItem>(), excluded = new HashSet<GameItem>();
        private readonly HashSet<string> excludedCategories = new HashSet<string>(StringComparer.Ordinal);
        private CustomUIWindow window;
        private bool byValue, redraw, showingPreview, choosingPriority;
        private string category;
        private string template;
        private Regex amountPattern;

        private sealed class Pick
        {
            internal WholeStackTransfer.Plan Transfer;
            internal GameItem Item => Transfer.Item;
            internal int Amount => Transfer.Amount;
        }
        internal FieldPacking(ShopSettings settings)
        {
            this.settings = settings;
            enabled = settings.Toggle("f36.enabled", "f36.tip");
            reserve = settings.Integer("f36.space", "f36.space.tip", 2, 0, 32);
            key = settings.Key("f36.key", "f36.key.tip");
            settings.Action("f36.open", Draw, () => enabled.Value && AtGround());
        }
        internal void Start()
        {
            WholeStackTransfer.Acquire();
            CustomerIndicators.PublicTooltipShown += Observe; VisibleInventoryViews.ItemChanged += Forget;
            ModHook.OnGameLoadedInit += Reset; ModHook.OnReturningStoreEarly += Reset; settings.Changed += Changed;
        }
        private static bool AtGround() => PlayerStore.HasInstance && MapUIManager.Instance?.Current?.Id == "dumping_ground" &&
            EmporiumEntry.Instance?.afterhourWindow?.IsVisible() == true;
        private void Changed() { if (!enabled.Value) Reset(); }
        private void Forget(GameItemElement item)
        {
            values.Remove(item);
            if (window != null && !showingPreview && item?.parentInventory == EmporiumEntry.Instance?.afterhourInventory) redraw = true;
        }
        private void Observe(GameItemElement item, string text)
        {
            if (!enabled.Value || !AtGround() || item.parentInventory != EmporiumEntry.Instance.afterhourInventory) return;
            if (window != null && !showingPreview) redraw = true;
            if (item.contentWindow != null) return;
            // Match only the localized line actually on screen. Computing GetCurrentValue() here could inspect closed contents.
            const string a = "101009", b = "202029";
            string current = Regex.Replace(LocHelper.GetLocalizedMechanic("mech_tooltip_est_value", 101009, 202029), "<[^>]*>", "");
            if (current != template)
            {
                template = current;
                amountPattern = current.Contains(a) && current.Contains(b) ? new Regex(Regex.Escape(current)
                    .Replace(a, "(?<n>[0-9]+)").Replace(b, "[0-9]+")) : null;
            }
            var found = amountPattern?.Match(Regex.Replace(text, "<[^>]*>", ""));
            if (found?.Success == true && long.TryParse(found.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
                values[item] = n;
            else values.Remove(item);
        }
        private static IEnumerable<GameItem> Ground() => EmporiumEntry.Instance.afterhourInventory.childItems.Where(i =>
            i is GameItemElement el && VisibleInventoryViews.Visible(el) && ShopInventory.Movable(i) && !AttentionBook.HasInspection("f36.open", i.uniqueId));
        private CustomUIBuilder Window()
        {
            var b = UiLayout.Rebuild(ref window, ModEntry.Id + ".packing", ShopSettings.Text("f36.open"), 590, 410);
            CustomUIWindow own = window;
            b.OnClose(() => { if (ReferenceEquals(window, own)) window = null; });
            return b;
        }
        private void Draw()
        {
            if (!enabled.Value || !AtGround()) return;
            showingPreview = false;
            redraw = false;
            var ground = Ground().ToList();
            var categories = ground.SelectMany(item => KnownTypes(item) ?? Array.Empty<string>())
                .Where(type => TypeHelper.ALL_TYPES_LIST.Contains(type)).Concat(excludedCategories)
                .Distinct(StringComparer.Ordinal).OrderBy(TypeHelper.GetTypeDisplayName, StringComparer.CurrentCulture).ToList();
            if (category == null || !categories.Contains(category)) category = categories.FirstOrDefault();
            var b = Window();
            string pending = WholeStackTransfer.FieldPending();
            if (pending != null)
            {
                b.AddLabel(ShopSettings.Text("f08.pending"));
                b.AddLabel(pending);
                if (b.Last.text != null) b.Last.text.richText = false;
                b.AddButton(ShopSettings.Text("f36.resolve_pending"), () =>
                {
                    if (!enabled.Value || !AtGround()) return;
                    if (!WholeStackTransfer.ResolveField()) ShopNotice.Show(ShopSettings.Text("f08.journal_failed"));
                    Draw();
                }, "resolve_pending").Show();
                return;
            }
            b.AddDropdown(new List<string> { ShopSettings.Text("f36.priority"), ShopSettings.Text("f36.value") },
                byValue ? 1 : 0, i => { byValue = i == 1; Draw(); }, "mode");
            if (categories.Count != 0)
            {
                int selected = categories.IndexOf(category);
                b.BeginRow().AddDropdown(categories.Select(TypeHelper.GetTypeDisplayName).ToList(), selected, index =>
                {
                    if (index < 0 || index >= categories.Count) return;
                    category = categories[index];
                    Draw();
                }, "category");
                string selectedCategory = category;
                b.AddToggle(ShopSettings.Text("f36.exclude_category"), excludedCategories.Contains(selectedCategory), on =>
                {
                    if (on) excludedCategories.Add(selectedCategory); else excludedCategories.Remove(selectedCategory);
                    Draw();
                }, "exclude_category").End();
            }
            b.BeginScrollFlexible(120);
            b.AddToggle(ShopSettings.Text("f36.priority_edit"), choosingPriority, value => { choosingPriority = value; Draw(); });
            ItemGrid.Add(b, Ground().Select(item => new ItemGrid.Entry(item)
            {
                Selected = CategoryAllowed(item) && (!byValue || values.ContainsKey(item)) &&
                    (choosingPriority ? priority.Contains(item) : !excluded.Contains(item)),
                Enabled = CategoryAllowed(item) && (!byValue || values.ContainsKey(item)),
                Badge = !CategoryAllowed(item) ? ShopSettings.Text("f36.category_excluded") :
                    byValue && !values.ContainsKey(item) ? ShopSettings.Text("f36.noPrice") : priority.Contains(item) ? ShopSettings.Text("f36.first") :
                    values.TryGetValue(item, out long amount) ? amount.ToString(CultureInfo.InvariantCulture) : ShopSettings.Text("f36.noPrice")
            }), new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 130, CardHeight = 98, OnClick = entry =>
            {
                if (choosingPriority) { if (!priority.Add(entry.Item)) priority.Remove(entry.Item); }
                else { if (!excluded.Add(entry.Item)) excluded.Remove(entry.Item); }
                Draw();
            } });
            b.End().BeginRow().AddButton(ShopSettings.Text("f36.preview"), Preview, "preview");
            if (ShopNotice.HasResult) b.AddButton(ShopSettings.Text("operation.last"), ShopNotice.OpenLastResult, "last_result");
            b.End().Show();
        }
        private static ICollection<string> KnownTypes(GameItem item) => item is GameItemElement element ? CustomerIndicators.PublicTypes(element) : null;
        private bool CategoryAllowed(GameItem item)
        {
            bool complete = item is GameItemElement element && CustomerIndicators.HasOrdinaryTooltip(element);
            return PublicClues.AllowedByExclusions(excludedCategories, KnownTypes(item), complete);
        }
        private static int Cells(GridShape s)
        {
            int count = 0;
            for (int y = s.minY; y <= s.maxY; y++) for (int x = s.minX; x <= s.maxX; x++) if (s.Intersects(x, y)) count++;
            return count;
        }
        private static int Free(GameGridInventory grid)
        {
            if (grid == null) return 0;
            var items = grid.childItems;
            int count = 0;
            for (int y = 0; y < grid.inventoryShape.height; y++)
            for (int x = 0; x < grid.inventoryShape.width; x++)
                if (grid.inventoryShape.Get(x, y) == 0 && items.All(i => !i.modifiedShape.Intersects(x, y))) count++;
            return count;
        }
        private List<Pick> Plan()
        {
            var picks = new List<Pick>();
            var e = EmporiumEntry.Instance;
            var bag = e.afterhourPocketSlotInvBackpack.childItem;
            var grid = ShopInventory.OutingBag(bag);
            var free = Free(grid);
            var reserved = new WholeStackTransfer.Reservations();
            var pockets = new[] { e.afterhourPocketSlotInvLeft, e.afterhourPocketSlotInvRight }.Where(p => p != null && !p.IsInsertLocked()).ToList();
            var items = Ground().Where(i => !excluded.Contains(i) && CategoryAllowed(i));
            items = byValue ? items.Where(i => values.ContainsKey(i)).OrderByDescending(i => values[i]).ThenByDescending(priority.Contains) :
                items.OrderByDescending(priority.Contains).ThenBy(i => Cells(i.modifiedShape));
            foreach (var item in items)
            {
                WholeStackTransfer.Plan transfer = grid == null ? null : WholeStackTransfer.Stack(item, grid, reserved);
                int cells = Cells(item.modifiedShape);
                if (transfer == null)
                {
                    foreach (var pocket in pockets)
                        if ((transfer = WholeStackTransfer.Stack(item, pocket, reserved)) != null) break;
                }
                if (transfer == null && grid != null && free - cells >= reserve.Value && !ShopInventory.WouldContainSelf(item, grid))
                {
                    transfer = WholeStackTransfer.Empty(item, grid, reserved);
                    if (transfer != null) free -= cells;
                }
                if (transfer == null)
                    foreach (var pocket in pockets.ToArray())
                        if (pocket.childItem == null && (transfer = WholeStackTransfer.EmptySlot(item, pocket)) != null)
                        { pockets.Remove(pocket); break; }
                if (transfer != null) picks.Add(new Pick { Transfer = transfer });
            }
            return picks;
        }
        private void Preview()
        {
            if (!enabled.Value || !AtGround()) return;
            if (!WholeStackTransfer.Ready) { ShopNotice.Show(ShopSettings.Text("f08.pending")); return; }
            showingPreview = true;
            redraw = false;
            var b = Window(); var picks = Plan();
            string expectedRun = PlayerStore.Instance.runID; int expectedSlot = PlayerStore.Instance.saveSlotId;
            b.AddLabel(ShopSettings.Format("f36.summary", picks.Sum(p => p.Amount), Ground().Count() - picks.Count));
            b.BeginScrollFlexible(110);
            ItemGrid.Add(b, picks.Select(p => new ItemGrid.Entry(p.Item) { Selected = true,
                Badge = ShopSettings.Text(p.Transfer.IsStack ? "f36.merge" : "f36.take") }),
                new ItemGrid.Options { Columns = 20, CellSize = 23 });
            var selected = new HashSet<GameItem>(picks.Select(p => p.Item));
            var left = Ground().Where(item => !selected.Contains(item)).ToList();
            foreach (var group in left.GroupBy(item => !CategoryAllowed(item) || excluded.Contains(item) ? "f36.leave_excluded" :
                !WholeStackTransfer.SafeSource(item) ? "f36.leave_unsafe" :
                byValue && !values.ContainsKey(item) ? "f36.leave_unknown" : "f36.leave_space"))
            {
                b.AddLabel(ShopSettings.Text(group.Key));
                ItemGrid.Add(b, group.Select(item => new ItemGrid.Entry(item)), new ItemGrid.Options { Columns = 20, CellSize = 23 });
            }
            b.End().BeginRow().AddButton(ShopSettings.Text("f34.back"), Draw)
                .AddButton(ShopSettings.Text("f36.apply"), () => Commit(picks, expectedRun, expectedSlot), "apply").End().Show();
        }
        private void Commit(List<Pick> expected, string expectedRun, int expectedSlot)
        {
            if (!enabled.Value || !AtGround() || PlayerStore.Instance.runID != expectedRun || PlayerStore.Instance.saveSlotId != expectedSlot) return;
            if (!WholeStackTransfer.Ready) { ShopNotice.Show(ShopSettings.Text("f08.pending")); return; }
            var live = Plan();
            if (live.Count != expected.Count || live.Where((p, i) => !WholeStackTransfer.Same(p.Transfer, expected[i].Transfer)).Any())
            { Preview(); ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            var ground = EmporiumEntry.Instance.afterhourInventory;
            var result = new OperationResult("f36.open");
            bool stopped = false;
            foreach (var p in live)
            {
                if (stopped) { result.Skip(p.Item, p.Amount, "operation.not_attempted"); continue; }
                var identity = OperationResult.Capture(p.Item);
                bool Scope()
                {
                    if (!enabled.Value || !AtGround() || PlayerStore.Instance.runID != expectedRun || PlayerStore.Instance.saveSlotId != expectedSlot ||
                        EmporiumEntry.Instance.afterhourInventory != ground) return false;
                    var e = EmporiumEntry.Instance;
                    var bag = e.afterhourPocketSlotInvBackpack.childItem;
                    return ReferenceEquals(p.Transfer.To, ShopInventory.OutingBag(bag)) ||
                        ReferenceEquals(p.Transfer.To, e.afterhourPocketSlotInvLeft) || ReferenceEquals(p.Transfer.To, e.afterhourPocketSlotInvRight);
                }
                var field = EmporiumEntry.Instance;
                var bagItem = field.afterhourPocketSlotInvBackpack.childItem;
                bool toBag = ReferenceEquals(p.Transfer.To, ShopInventory.OutingBag(bagItem));
                bool toLeft = ReferenceEquals(p.Transfer.To, field.afterhourPocketSlotInvLeft);
                string destination = toBag ? ShopSettings.Text("f36.backpack") :
                    ShopSettings.Text(toLeft ? "f36.left_pocket" : "f36.right_pocket");
                string destinationKey = toBag ? "field:bag:" + bagItem.GetUniqueID() : toLeft ? "field:left" : "field:right";
                string path = ShopSettings.Text("f36.ground") + " → " + destination;
                var outcome = WholeStackTransfer.Apply(p.Transfer, "f36.open", path, "field:ground", destinationKey, Scope);
                if (outcome == WholeStackTransfer.Result.Applied)
                {
                    result.CompleteCaptured(identity, p.Amount, path);
                    if (GenericConfirmationUIManager.Instance?.panel.activeInHierarchy == true) stopped = true;
                }
                else { result.SkipCaptured(identity, p.Amount, outcome == WholeStackTransfer.Result.Unchanged ? "inv.changed" : "f08.pending", path); stopped = true; }
            }
            ShopNotice.ReportField(result, ground, Draw);
        }
        internal void Tick()
        {
            if (window != null && (!enabled.Value || !AtGround())) Reset();
            if (redraw && !showingPreview && window != null && window.IsAlive && !StoreUIManager.IsAnyTyping() && !Input.GetMouseButton(0)) Draw();
            if (enabled.Value && key() != KeyCode.None && AtGround() && ShopInput.Ready() && Input.GetKeyDown(key())) Draw();
        }
        private void Reset()
        {
            if (window != null && window.IsAlive) window.Close();
            window = null; excluded.Clear(); priority.Clear(); values.Clear(); excludedCategories.Clear();
            category = null; redraw = false; showingPreview = false;
        }
        public void Dispose()
        {
            WholeStackTransfer.Release();
            CustomerIndicators.PublicTooltipShown -= Observe; VisibleInventoryViews.ItemChanged -= Forget;
            ModHook.OnGameLoadedInit -= Reset; ModHook.OnReturningStoreEarly -= Reset; settings.Changed -= Changed; Reset();
        }
    }
}
