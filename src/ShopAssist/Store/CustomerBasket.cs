using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    internal sealed class CustomerBasket : IDisposable
    {
        private sealed class Quote { internal int Price, Count, Revision; }
        private sealed class Offer { internal GameItem Item; internal int Price, Count; }
        private sealed class Choice { internal ulong Items; internal int Total, Units; }
        private static CustomerBasket active;
        private readonly CustomerIndicators clues;
        private readonly CustomerInventory inventory;
        private readonly ShopSettings settings;
        private readonly ConfigEntry<bool> enabled, allowMove;
        private readonly Dictionary<GameItem, Quote> quotes = new Dictionary<GameItem, Quote>();
        private readonly HashSet<GameItem> excluded = new HashSet<GameItem>();
        private readonly Queue<Offer> pending = new Queue<Offer>();
        private readonly HashSet<GameItem> plannedItems = new HashSet<GameItem>(), staged = new HashSet<GameItem>();
        private StoreClientInstance customer;
        private bool reading;
        private int? shownBudget;
        private int revision, expectedBudget, waitingAfter, page;
        private string typed = "";
        private CustomUIWindow window;
        private Offer waiting;
        private float until;

        internal CustomerBasket(ShopSettings settings, CustomerIndicators clues, CustomerInventory inventory)
        {
            this.settings = settings; this.clues = clues; this.inventory = inventory;
            enabled = settings.Toggle("f34.enabled", "f34.tip");
            allowMove = settings.Toggle("f34.move", "f34.move.tip");
            settings.Action("f34.open", Draw, () => enabled.Value && clues.HasPublicDemand);
        }
        internal void Start()
        {
            active = this; inventory.ExtraActions += AddAction; ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset; settings.Changed += Changed;
        }
        private void AddAction(CustomUIBuilder builder)
        {
            if (enabled.Value && clues.HasPublicDemand) builder.AddButton(ShopSettings.Text("f34.open"), Draw, "basket");
        }
        private void Changed() { if (!enabled.Value) Reset(); else if (!allowMove.Value) StopPlace(); }
        private void StopPlace() { pending.Clear(); staged.Clear(); plannedItems.Clear(); waiting = null; }
        private void Reset()
        {
            if (window != null && window.IsAlive) window.Close();
            window = null; StopPlace(); customer = null; quotes.Clear(); excluded.Clear(); typed = ""; shownBudget = null; reading = false; page = 0;
        }
        private bool Context()
        {
            if (!enabled.Value || !ShopInventory.InStore || clues.Customer == null) return false;
            if (customer != clues.Customer) { Reset(); customer = clues.Customer; }
            return true;
        }
        [HarmonyPatch(typeof(NegociationUIManager), "BuildBuying")]
        private static class Buying
        {
            private static void Prefix(NegociationUIManager __instance, StoreClient storeClient)
            {
                var a = active;
                if (a == null || !a.Context() || a.customer.GetClientBlueprint() != storeClient || __instance.uiObject?.activeInHierarchy != true) return;
                a.reading = true; a.revision++;
            }
            private static void Postfix(StoreClient storeClient)
            {
                var a = active;
                if (a == null || !a.reading) return;
                // This native view has just drawn its budget footer if the buyer has a budget.
                a.shownBudget = storeClient.useClientBudget ? storeClient.GetBudget() : (int?)null; a.reading = false;
            }
            private static Exception Finalizer(Exception __exception) { if (active != null) active.reading = false; return __exception; }
        }
        [HarmonyPatch(typeof(NegociationUIManager), "BuildItemRow")]
        private static class PricedRow
        {
            private static void Postfix(GameItem item, string refusal, string right)
            {
                var a = active;
                if (a == null || !a.reading || item == null) return;
                if (!string.IsNullOrEmpty(refusal)) { a.quotes.Remove(item); return; }
                string icon = GeneralHelper.CreditIcon();
                if (string.IsNullOrEmpty(icon) || right == null || !right.EndsWith(icon, StringComparison.Ordinal) ||
                    !int.TryParse(right.Substring(0, right.Length - icon.Length), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) || n <= 0)
                { a.quotes.Remove(item); return; }
                a.quotes[item] = new Quote { Price = n, Count = item.unitCount, Revision = a.revision };
            }
        }
        private bool Budget(out int limit)
        {
            if (shownBudget.HasValue && string.IsNullOrWhiteSpace(typed)) { limit = shownBudget.Value; return limit > 0; }
            if (!int.TryParse(typed, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit <= 0) return false;
            if (shownBudget.HasValue) limit = Math.Min(limit, shownBudget.Value);
            return limit > 0;
        }
        private bool Eligible(GameItem item) => EligibleStock(item) && !RequestBook.Reserved(item);
        private bool EligibleStock(GameItem item) => ShopInventory.Movable(item) &&
            !item.IsTag("IMPORTANT_TAG") && !item.IsTag("STOLEN_TAG") && !item.IsTag("CONTRABAND_ITEM_TAG") &&
            clues.Match(item) == PublicMatch.Match;
        private List<GameItem> Stock()
        {
            var locations = ShopInventory.OwnedLocations(false);
            var reserved = RequestBook.ReservedItems(locations);
            return locations.Where(l => l.Ordinary || l.Role == "counter").SelectMany(l => l.Inventory.childItems)
                .Where(item => !reserved.Contains(item) && EligibleStock(item)).Distinct().ToList();
        }
        private CustomUIBuilder Window()
        {
            var b = CustomUIManager.Window(ModEntry.Id + ".basket", ShopSettings.Text("f34.open"), CustomUIManager.LAYER_META)
                .SetSize(600, 435).SetCloseOnEscape(true).SetDraggable(true);
            window = b.GetWindow(); b.OnClose(() => { window = null; StopPlace(); }); return b;
        }
        private void Draw()
        {
            if (!Context() || !clues.HasPublicDemand) return;
            var stock = Stock();
            var b = Window();
            b.AddLabel(shownBudget.HasValue ? ShopSettings.Format("f34.budget", shownBudget) : ShopSettings.Text("f34.unshown"))
                .AddInput(ShopSettings.Text("f34.limit"), typed, s => typed = s, "limit", s => typed = s);
            b.BeginScrollFlexible(120);
            page = Math.Max(0, Math.Min(page, (stock.Count - 1) / 24));
            ItemGrid.Add(b, stock.Skip(page * 24).Take(24).Select(item => new ItemGrid.Entry(item)
            {
                Selected = !excluded.Contains(item), Enabled = quotes.TryGetValue(item, out var quote) && quote.Count == item.unitCount,
                Badge = quotes.TryGetValue(item, out var price) && price.Count == item.unitCount ? price.Price.ToString() : ShopSettings.Text("f34.unpriced")
            }), new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 130, CardHeight = 100,
                OnClick = entry => { if (!excluded.Add(entry.Item)) excluded.Remove(entry.Item); entry.Selected = !excluded.Contains(entry.Item); } });
            b.End().BeginRow().AddButton("‹", () => { if (page > 0) { page--; Draw(); } })
                .AddButton("›", () => { if ((page + 1) * 24 < stock.Count) { page++; Draw(); } })
                .AddButton(ShopSettings.Text("f34.compare"), Compare, "compare").End().Show();
        }
        private void Compare()
        {
            if (!Context() || !Budget(out int limit)) { ShopNotice.Show(ShopSettings.Text("f34.unshown")); return; }
            var stock = Stock().Where(i => !excluded.Contains(i) && quotes.TryGetValue(i, out var q) && q.Count == i.unitCount && q.Price <= limit)
                .Select(i => new Offer { Item = i, Count = i.unitCount, Price = quotes[i].Price }).OrderBy(o => o.Price).ToList();
            int eligibleCount = stock.Count;
            if (stock.Count > 48) stock = stock.Take(24).Concat(stock.Skip(stock.Count - 24)).ToList();
            var states = new Dictionary<int, Choice> { [0] = new Choice() };
            for (int i = 0; i < stock.Count; i++)
            {
                var offer = stock[i];
                foreach (var prev in states.Values.ToArray())
                {
                    if (prev.Total > limit - offer.Price) continue;
                    int sum = prev.Total + offer.Price;
                    if (!states.TryGetValue(sum, out var known) || known.Units < prev.Units + offer.Count)
                        states[sum] = new Choice { Total = sum, Items = prev.Items | (1UL << i), Units = prev.Units + offer.Count };
                }
                if (states.Count > 1024)
                    states = states.Values.OrderByDescending(c => c.Total).Take(512)
                        .Concat(states.Values.OrderByDescending(c => c.Units).ThenBy(c => c.Total).Take(512))
                        .Concat(new[] { new Choice() }).GroupBy(c => c.Total).ToDictionary(g => g.Key, g => g.First());
            }
            var options = states.Values.Where(c => c.Total > 0).ToList();
            var b = Window();
            if (eligibleCount > stock.Count) b.AddLabel(ShopSettings.Format("f34.limited", stock.Count, eligibleCount));
            if (options.Count == 0) b.AddLabel(ShopSettings.Text("f34.noOffer"));
            else foreach (var choice in new[] { options.OrderByDescending(c => c.Total).First(),
                options.OrderByDescending(c => c.Units).ThenBy(c => c.Total).First() }.Distinct())
            {
                var picked = stock.Where((o, i) => (choice.Items & (1UL << i)) != 0).ToList();
                b.AddLabel(ShopSettings.Format("f34.summary", choice.Units, choice.Total, limit - choice.Total));
                ItemGrid.Add(b, picked.Select(o => new ItemGrid.Entry(o.Item) { Badge = o.Price.ToString(), Selected = true }),
                    new ItemGrid.Options { Columns = 20, CellSize = 20 });
                if (allowMove.Value) b.AddButton(ShopSettings.Text("f34.place"), () => BeginPlace(picked, limit), "place_" + choice.Items);
            }
            b.AddButton(ShopSettings.Text("f34.back"), Draw).Show();
        }
        private void BeginPlace(List<Offer> items, int budget)
        {
            if (!Context() || !allowMove.Value || waiting != null || pending.Count > 0 || !Budget(out int live) || live != budget) return;
            var desk = EmporiumEntry.Instance.backInvinvElement;
            var occupied = new List<GridShape>();
            var eligible = new HashSet<GameItem>(Stock());
            if (NegociationUIManager.Instance?.uiObject.activeInHierarchy != true ||
                desk.childItems.Any(i => !items.Any(o => o.Item == i)) || items.Any(o => !eligible.Contains(o.Item) ||
                !quotes.TryGetValue(o.Item, out var q) || q.Price != o.Price || q.Count != o.Count))
            { ShopNotice.Show(ShopSettings.Text("f34.desk")); return; }
            foreach (var o in items.Where(o => o.Item.parentInventory != desk))
            {
                var at = GridPlacements.Empty(desk, o.Item, occupied);
                if (at == null) { ShopNotice.Show(ShopSettings.Text("inv.noSpace")); return; }
                occupied.Add(at.itemGridShape.Clone());
            }
            expectedBudget = budget;
            plannedItems.Clear(); staged.Clear();
            foreach (var o in items) { plannedItems.Add(o.Item); if (o.Item.parentInventory == desk) staged.Add(o.Item); }
            foreach (var o in items.Where(o => o.Item.parentInventory != desk)) pending.Enqueue(o);
            if (pending.Count == 0) ShopNotice.Show(ShopSettings.Text("f34.ready"));
        }
        internal void Tick()
        {
            if (customer != null && (!ShopInventory.InStore || customer != clues.Customer)) { Reset(); return; }
            if (waiting == null && pending.Count == 0) return;
            var nego = NegociationUIManager.Instance;
            var desk = EmporiumEntry.Instance.backInvinvElement;
            if (!allowMove.Value || nego?.uiObject.activeInHierarchy != true || !Budget(out int limit) || limit != expectedBudget ||
                desk.childItems.Any(i => !plannedItems.Contains(i)) || staged.Any(i => i.parentInventory != desk))
            { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.changed")); return; }
            if (waiting != null)
            {
                if (quotes.TryGetValue(waiting.Item, out var fresh) && fresh.Revision > waitingAfter)
                {
                    if (fresh.Price != waiting.Price || fresh.Count != waiting.Count) { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.changed")); return; }
                    waiting = null;
                    if (pending.Count == 0) { ShopNotice.Show(ShopSettings.Text("f34.ready")); return; }
                }
                else if (Time.unscaledTime < until) return;
                else { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.changed")); return; }
            }
            var move = pending.Dequeue();
            var from = ShopInventory.OwnedLocations(false).Find(l => l.Ordinary && l.Inventory == move.Item.parentInventory);
            if (from == null || !Eligible(move.Item) || move.Item.unitCount != move.Count || !quotes.TryGetValue(move.Item, out var old) ||
                old.Price != move.Price) { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.changed")); return; }
            var slot = GridPlacements.Empty(desk, move.Item);
            if (move.Item.parentInventory != from.Inventory || move.Item.unitCount != move.Count || !Eligible(move.Item))
            { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.changed")); return; }
            waitingAfter = revision;
            bool restored = true;
            if (slot == null || !ItemGridMove.TryMove(move.Item, desk, slot.itemGridShape, out restored))
            {
                var result = new OperationResult("f34.open");
                result.Skip(move.Item, move.Count, restored ? slot == null ? "inv.noSpace" : "inv.changed" : "inv.inspect", from.Name, !restored);
                foreach (var remaining in pending) result.Skip(remaining.Item, remaining.Count, "operation.not_attempted");
                StopPlace(); ShopNotice.Report(result); return;
            }
            staged.Add(move.Item); waiting = move; until = Time.unscaledTime + 2f; nego.RefreshUI();
        }
        public void Dispose()
        {
            if (active == this) active = null;
            inventory.ExtraActions -= AddAction; ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Reset; settings.Changed -= Changed; Reset();
        }
    }
}
