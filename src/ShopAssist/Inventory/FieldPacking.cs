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
            internal GameItem Item;
            internal GameInventory Target;
            internal GridShape Home, Fit;
            internal int Amount;
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
            var b = CustomUIManager.Window(ModEntry.Id + ".packing", ShopSettings.Text("f36.open"), CustomUIManager.LAYER_META)
                .SetSize(590, 410).SetDraggable(true).SetCloseOnEscape(true);
            window = b.GetWindow();
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
            b.AddDropdown(new List<string> { ShopSettings.Text("f36.priority"), ShopSettings.Text("f36.value") },
                byValue ? 1 : 0, i => byValue = i == 1, "mode");
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
                Selected = CategoryAllowed(item) && (choosingPriority ? priority.Contains(item) : !excluded.Contains(item)),
                Enabled = CategoryAllowed(item),
                Badge = !CategoryAllowed(item) ? ShopSettings.Text("f36.category_excluded") : priority.Contains(item) ? ShopSettings.Text("f36.first") :
                    values.TryGetValue(item, out long amount) ? amount.ToString(CultureInfo.InvariantCulture) : ShopSettings.Text("f36.noPrice")
            }), new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 130, CardHeight = 98, OnClick = entry =>
            {
                if (choosingPriority) { if (!priority.Add(entry.Item)) priority.Remove(entry.Item); }
                else { if (!excluded.Add(entry.Item)) excluded.Remove(entry.Item); }
                Draw();
            } });
            b.End().AddButton(ShopSettings.Text("f36.preview"), Preview, "preview").Show();
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
            var reserved = new List<GridShape>();
            var pockets = new[] { e.afterhourPocketSlotInvLeft, e.afterhourPocketSlotInvRight }.Where(p => p != null && p.childItem == null && !p.IsInsertLocked()).ToList();
            var items = Ground().Where(i => !excluded.Contains(i) && CategoryAllowed(i));
            items = byValue ? items.Where(i => values.ContainsKey(i)).OrderByDescending(i => values[i]).ThenByDescending(priority.Contains) :
                items.OrderByDescending(priority.Contains).ThenBy(i => Cells(i.modifiedShape));
            foreach (var item in items)
            {
                SlotMarker marker = null;
                int cells = Cells(item.modifiedShape);
                if (grid != null && free - cells >= reserve.Value && !ShopInventory.WouldContainSelf(item, grid))
                    marker = GridPlacements.Empty(grid, item, reserved);
                if (marker != null) { free -= cells; reserved.Add(marker.itemGridShape.Clone()); }
                else
                {
                    foreach (var pocket in pockets.ToArray())
                    {
                        marker = pocket.TryFindOneValidInventorySlot(item);
                        if (marker?.inventory == pocket && marker.targetItem == null && marker.numTransfer >= item.unitCount)
                        { pockets.Remove(pocket); break; }
                        marker = null;
                    }
                }
                if (marker != null) picks.Add(new Pick { Item = item, Target = marker.inventory, Amount = item.unitCount,
                    Fit = marker.itemGridShape.Clone(), Home = item.modifiedShape.Clone() });
            }
            return picks;
        }
        private void Preview()
        {
            if (!enabled.Value || !AtGround()) return;
            showingPreview = true;
            redraw = false;
            var b = Window(); var picks = Plan();
            b.AddLabel(ShopSettings.Format("f36.summary", picks.Sum(p => p.Amount), Ground().Count() - picks.Count));
            b.BeginScrollFlexible(110);
            ItemGrid.Add(b, picks.Select(p => new ItemGrid.Entry(p.Item) { Selected = true, Badge = ShopSettings.Text("f36.take") }),
                new ItemGrid.Options { Columns = 20, CellSize = 23 });
            var selected = new HashSet<GameItem>(picks.Select(p => p.Item));
            var left = Ground().Where(item => !selected.Contains(item)).ToList();
            if (left.Count > 0)
            {
                b.AddLabel(ShopSettings.Text("f36.leave"));
                ItemGrid.Add(b, left.Select(item => new ItemGrid.Entry(item)), new ItemGrid.Options { Columns = 20, CellSize = 23 });
            }
            b.End().BeginRow().AddButton(ShopSettings.Text("f34.back"), Draw)
                .AddButton(ShopSettings.Text("f36.apply"), () => Commit(picks), "apply").End().Show();
        }
        private void Commit(List<Pick> expected)
        {
            if (!enabled.Value || !AtGround()) return;
            var live = Plan();
            if (live.Count != expected.Count || live.Where((p, i) => p.Item != expected[i].Item || p.Target != expected[i].Target ||
                p.Amount != expected[i].Amount || p.Fit.minX != expected[i].Fit.minX || p.Fit.minY != expected[i].Fit.minY ||
                p.Fit.orientation != expected[i].Fit.orientation || p.Fit.flipped != expected[i].Fit.flipped ||
                !InventorySortPlan.SameShape(p.Home, expected[i].Home)).Any())
            { Preview(); ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            var ground = EmporiumEntry.Instance.afterhourInventory;
            var result = new OperationResult("f36.open");
            bool stopped = false;
            foreach (var p in live)
            {
                if (stopped) { result.Skip(p.Item, p.Amount, "operation.not_attempted"); continue; }
                if (p.Item.parentInventory != ground || p.Item.unitCount != p.Amount ||
                    !InventorySortPlan.SameShape(p.Item.modifiedShape, p.Home) || !ShopInventory.Movable(p.Item) || p.Target.IsInsertLocked())
                { result.Skip(p.Item, p.Amount, "inv.changed"); stopped = true; continue; }
                var marker = p.Target.TryInventorySlot(p.Item, p.Amount, p.Fit);
                if (marker == null || marker.inventory != p.Target || marker.targetItem != null || marker.numTransfer < p.Amount)
                { result.Skip(p.Item, p.Amount, "inv.noSpace"); stopped = true; continue; }
                int accepted = 0;
                try { accepted = marker.TryAcceptOnce(p.Amount); }
                catch (Exception error) { Debug.LogWarning("[ShopAssist] F36 native placement interrupted: " + error); }
                if (accepted == p.Amount && ReferenceEquals(marker.item, p.Item) && p.Item.parentInventory == p.Target &&
                    p.Item.unitCount == p.Amount && p.Target.childItems.Contains(p.Item) && !ground.childItems.Contains(p.Item) &&
                    InventorySortPlan.SameShape(p.Item.modifiedShape, p.Fit))
                {
                    result.Complete(p.Item, p.Amount);
                    if (GenericConfirmationUIManager.Instance?.panel.activeInHierarchy == true) stopped = true;
                    continue;
                }
                bool restored = Restore(p, ground);
                result.Skip(p.Item, p.Amount, restored ? "inv.changed" : "inv.inspect", needsInspection: !restored);
                stopped = true;
            }
            ShopNotice.ReportField(result, ground, Draw);
        }
        private static bool Restore(Pick pick, GameGridInventory ground)
        {
            try
            {
                if (pick.Item.unitCount != pick.Amount) return false;
                if (pick.Item.parentInventory != null && pick.Item.parentInventory != ground && pick.Item.parentInventory != pick.Target)
                    return false;
                if (pick.Item.parentInventory != ground)
                    GridPlacements.Exact(ground, pick.Item, pick.Home, pick.Amount)?.TryAcceptOnce(pick.Amount);
                if (pick.Item.parentInventory == ground && ground.childItems.Contains(pick.Item) &&
                    !InventorySortPlan.SameShape(pick.Item.modifiedShape, pick.Home) &&
                    ground.CheckShapePlacement(pick.Item, pick.Home, new List<GameItem> { pick.Item }))
                {
                    pick.Item.modifiedShape = new GridShapeBuilder(pick.Item.modifiedShape).SetTransform(pick.Home);
                    pick.Item.ValidateShapeState(); ground.Validate();
                }
                return pick.Item.parentInventory == ground && ground.childItems.Contains(pick.Item) &&
                    !pick.Target.childItems.Contains(pick.Item) && pick.Item.unitCount == pick.Amount &&
                    InventorySortPlan.SameShape(pick.Item.modifiedShape, pick.Home);
            }
            catch (Exception error) { Debug.LogWarning("[ShopAssist] F36 placement recovery interrupted: " + error); return false; }
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
            CustomerIndicators.PublicTooltipShown -= Observe; VisibleInventoryViews.ItemChanged -= Forget;
            ModHook.OnGameLoadedInit -= Reset; ModHook.OnReturningStoreEarly -= Reset; settings.Changed -= Changed; Reset();
        }
    }
}
