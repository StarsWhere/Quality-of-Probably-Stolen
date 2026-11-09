using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed class CustomerBasket : IDisposable
    {
        private sealed class Quote { internal int Price, Count, Revision; }
        private sealed class Offer
        {
            internal GameItem Item;
            internal int Price, Count;
            internal bool OnCounter;
            internal GameInventory Source;
            internal GridShape SourceShape;
            internal string State;
        }
        private sealed class Stamp
        {
            internal GameItem Item;
            internal GameInventory Source;
            internal GridShape Shape;
            internal int Count, Price;
            internal bool Accepted;
            internal string State;
        }
        private sealed class Basket
        {
            internal readonly List<Offer> Offers = new List<Offer>();
            internal readonly List<GridShape> Shapes = new List<GridShape>();
            internal List<Stamp> Counter;
            internal GridShape DeskShape;
            internal GameGridInventory Desk;
            internal string Callbacks;
            internal int Total, Units, Budget;
            internal int? CustomerBudget;
        }
        private static readonly MethodInfo Sellable = AccessTools.Method(typeof(NegociationUIManager), "GetSellableBackInvItems");
        private static readonly MethodInfo SumQuotes = AccessTools.Method(typeof(NegociationUIManager), "SumNegociatedValue");
        private static readonly FieldInfo AcceptPreference = AccessTools.Field(typeof(StoreClient), "willAcceptItemPreference");
        private static readonly FieldInfo LastFilterLineItem = AccessTools.Field(typeof(StoreClient), "lastFilterLineItem");
        private static readonly Guid PreviousCounterBuild = new Guid("efd8b252-19aa-489b-96d3-e172c92c0b75");
        private static readonly Guid VerifiedCounterBuild = new Guid("911508a6-e0eb-4cc0-91e8-e14b16ea28d7");
        private static bool VerifiedCounterGameBuild(Guid module) => module == PreviousCounterBuild || module == VerifiedCounterBuild;
        private static readonly MethodBase[] CounterMethods = {
            AccessTools.Method(typeof(GameItem), "OnAddedToWeightedArea"),
            AccessTools.Method(typeof(PlayerStore), "AddedItemToWeightedArea"), AccessTools.Method(typeof(PlayerStore), "OnWeightedAreaChanged"),
            AccessTools.Method(typeof(PlayerStore), "PlacedItemForSelling"),
            AccessTools.Method(typeof(StoreClient), "IsClientRefusingItem", new[] { typeof(GameItem), typeof(bool) }),
            AccessTools.Method(typeof(StoreClient), "<IsClientRefusingItem>b__136_0"),
            AccessTools.Method(typeof(StoreClient), "IsRefusingLuxuryItem"), AccessTools.Method(typeof(StoreClient), "IsRefusingForFilterInside"),
            AccessTools.Method(typeof(StoreClient), "TrySayRemoveFilterLine"), AccessTools.Method(typeof(StoreClient), "IsItemInVarietyList"),
            AccessTools.Method(typeof(StoreClient), "GetInspectableChildren"), AccessTools.Method(typeof(StoreClient), "CanClientExposeAnyFeature"),
            AccessTools.Method(typeof(StoreClient), "CanClientExposeThisFeature"), AccessTools.Method(typeof(ItemFeature), "IsExposed"),
            AccessTools.Method(typeof(ItemFeature), "IsFeatureMatch"), AccessTools.Method(typeof(AmmoBoxHelper), "IsAmmoBox"),
            AccessTools.Method(typeof(WaterHelper), "GetFilterItem"), AccessTools.Method(typeof(GameItem), "AddItemFeature"),
            AccessTools.Method(typeof(GameItem), "AddFeatureRelatedToEvent"), AccessTools.Method(typeof(GameItem), "AddFeatureRelatedToBuyer"),
            AccessTools.Method(typeof(StoreEventManager), "GetActiveEvents"), AccessTools.Method(typeof(StoreEventManager), "SortEventByPriority"),
            AccessTools.Method(typeof(StoreEventManager).GetNestedType("<>c", BindingFlags.NonPublic), "<SortEventByPriority>b__6_0"),
            AccessTools.Method(typeof(StoreEvent), "IsItemAffectedByThisEvent"),
            AccessTools.Method(typeof(ContrabandHelper), "ApplyContrabandFeature"), AccessTools.Method(typeof(PermitHelper), "ApplyRestrictedFeature"),
            AccessTools.Method(typeof(ContrabandHelper), "IsContraband"), AccessTools.Method(typeof(ChemicalProductHelper), "HasChemicalPurity"),
            AccessTools.Method(typeof(ChemicalProductHelper), "WillAcceptNarcotic"), AccessTools.Method(typeof(WaterFeatureHelper), "IsLabelledHazardousWater"),
            AccessTools.Method(typeof(GameItem), "GetGameItemType"), AccessTools.Method(typeof(GameItem), "ApplyContrabandFeatureToChildren"),
            AccessTools.Method(typeof(ItemFeature), "SetPreExposedValueMod"), AccessTools.PropertySetter(typeof(ItemFeature), "isFeatureMatch"),
            AccessTools.Method(typeof(ItemFeatureList), "RetailMarkUp"), AccessTools.Method(typeof(ItemFeatureList), "NeuroActivePerfumeSelling"),
            AccessTools.Method(typeof(ItemFeatureList), "PheromonePerfumeSelling"), AccessTools.Method(typeof(ItemFeatureList), "AddictionSellPenalty"),
            AccessTools.Method(typeof(ItemFeatureList), "AwfulNegotiatorSellPenalty"), AccessTools.Method(typeof(ItemFeatureList), "Pourboire"),
            AccessTools.Method(typeof(ItemFeatureList), "ContrabandLowMarkUp"), AccessTools.Method(typeof(ItemFeatureList), "ContrabandMidMarkUp"),
            AccessTools.Method(typeof(ItemFeatureList), "ContrabandHighMarkUp"), AccessTools.Method(typeof(ItemFeatureList), "ContrabandCriticalMarkUp"),
            AccessTools.Method(typeof(ItemFeatureList), "RestrictedLowMarkUp"), AccessTools.Method(typeof(ItemFeatureList), "RestrictedMidMarkUp"),
            AccessTools.Method(typeof(ItemFeatureList), "RestrictedHighMarkUp"),
            AccessTools.Method(typeof(GameItem), "GetNegociatedValue"), Sellable, SumQuotes,
            AccessTools.Method(typeof(StoreClient), "IsClientBuyingThisItem", new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(StoreClient), "IsClientBuyingThisItem", new[] { typeof(GameItem), typeof(bool) })
        };
        private static CustomerBasket active;
        private readonly CustomerIndicators clues;
        private readonly Func<GameItem, string> purchaseLabel;
        private readonly ShopSettings settings;
        private readonly ConfigEntry<bool> enabled, allowMove;
        private readonly Dictionary<GameItem, Quote> quotes = new Dictionary<GameItem, Quote>();
        private readonly HashSet<GameItem> excluded = new HashSet<GameItem>();
        private readonly HashSet<string> publicDemand = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<Offer> pending = new Queue<Offer>();
        private readonly List<Stamp> expectedCounter = new List<Stamp>();
        private Basket placing;
        private int nextPlace;
        private bool excludeStolen, excludeContraband, excludeLuxury, demandOnly;
        private StoreClientInstance customer;
        private string run;
        private int saveSlot;
        private bool reading, showingResults;
        private int? shownBudget;
        private int revision, expectedBudget, waitingAfter, page;
        private string typed = "";
        private CustomUIWindow window;
        private Offer waiting;
        private float until;

        internal CustomerBasket(ShopSettings settings, CustomerIndicators clues, Func<GameItem, string> purchaseLabel)
        {
            this.settings = settings; this.clues = clues; this.purchaseLabel = purchaseLabel;
            enabled = settings.Toggle("f34.enabled", "f34.tip");
            allowMove = settings.Toggle("f34.move", "f34.move.tip");
            settings.Action("f34.open", Draw, () => enabled.Value && clues.HasBuyingDemand);
        }
        internal void Start()
        {
            active = this; ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset; settings.Changed += Changed;
        }
        private void Changed()
        {
            if (!enabled.Value) { Reset(); return; }
            if (!allowMove.Value) StopPlace();
            if (window?.IsAlive != true) return;
            if (showingResults) Compare(); else Draw();
        }
        private void StopPlace() { pending.Clear(); expectedCounter.Clear(); waiting = null; placing = null; nextPlace = 0; }
        private void Reset()
        {
            if (window != null && window.IsAlive) window.Close();
            window = null; StopPlace(); customer = null; run = null; quotes.Clear(); excluded.Clear(); publicDemand.Clear(); typed = ""; shownBudget = null;
            reading = showingResults = false; page = revision = 0;
        }
        private bool CaptureContext()
        {
            if (!ShopInventory.InStore || !clues.HasBuyingDemand || clues.Customer == null) return false;
            if (customer != clues.Customer || run != PlayerStore.Instance.runID || saveSlot != PlayerStore.Instance.saveSlotId)
            {
                Reset(); customer = clues.Customer; run = PlayerStore.Instance.runID; saveSlot = PlayerStore.Instance.saveSlotId;
            }
            return true;
        }
        private bool Context() => enabled.Value && CaptureContext();
        private bool TryQuote(GameItem item, out int price, out bool onCounter)
        {
            price = 0; onCounter = false;
            return item != null && CaptureContext() && clues.Matches(item) &&
                CustomerQuote.TryGet(item, customer.GetClientBlueprint(), out price, out onCounter);
        }
        private static string FormatQuote(int value, bool onCounter) =>
            ShopSettings.Format(onCounter ? "f11.quoted" : "f11.estimated", value);
        [HarmonyPatch(typeof(NegociationUIManager), "BuildBuying")]
        private static class Buying
        {
            private static void Prefix(NegociationUIManager __instance, StoreClient storeClient)
            {
                var a = active;
                if (a == null || !a.CaptureContext() || a.customer.GetClientBlueprint() != storeClient || __instance.uiObject?.activeInHierarchy != true) return;
                a.reading = true; a.revision++;
            }
            private static void Postfix(StoreClient storeClient)
            {
                var a = active;
                if (a == null || !a.reading) return;
                // This native view has just drawn its budget footer if the buyer has a budget.
                a.shownBudget = storeClient.useClientBudget ? storeClient.GetBudget() : (int?)null; a.reading = false;
                a.publicDemand.Clear();
                if (InventoryAutoInsert.VerifiedGameBuild(typeof(GameItem).Module.ModuleVersionId))
                    foreach (string type in storeClient.clientBuyingTagList)
                    {
                        string displayed = type == "ALL_TYPE_EXCEPT_CONTRABAND" ? "ALL_TYPE" : type;
                        if (TypeHelper.ALL_TYPES_LIST.Contains(displayed)) a.publicDemand.Add(displayed);
                    }
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
                if (!string.IsNullOrEmpty(refusal)) { if (a.quotes.Remove(item)) a.revision++; return; }
                string icon = GeneralHelper.CreditIcon();
                if (string.IsNullOrEmpty(icon) || right == null || !right.EndsWith(icon, StringComparison.Ordinal) ||
                    !int.TryParse(right.Substring(0, right.Length - icon.Length), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) || n < 0)
                { if (a.quotes.Remove(item)) a.revision++; return; }
                bool changed = !a.quotes.TryGetValue(item, out var old) || old.Price != n || old.Count != item.unitCount;
                a.quotes[item] = new Quote { Price = n, Count = item.unitCount, Revision = a.revision };
                if (changed) a.revision++;
            }
        }
        private bool Budget(out int limit)
        {
            if (shownBudget.HasValue && string.IsNullOrWhiteSpace(typed)) { limit = shownBudget.Value; return limit > 0; }
            if (!int.TryParse(typed, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit <= 0) return false;
            if (shownBudget.HasValue) limit = Math.Min(limit, shownBudget.Value);
            return limit > 0;
        }
        private bool Eligible(GameItem item) => EligibleStock(item);
        private bool EligibleStock(GameItem item) => ShopInventory.Movable(item) &&
            !item.IsTag("IMPORTANT_TAG") && (!demandOnly || PublicDemandMatches(item)) && AllowedByFilters(item);
        private bool PublicDemandMatches(GameItem item)
        {
            ICollection<string> types = item is GameItemElement element ? CustomerIndicators.PublicTypes(element) : null;
            return CustomerBasketSearch.MatchesDemand(types, publicDemand);
        }
        private bool AllowedByFilters(GameItem item)
        {
            if (!(item is GameItemElement element)) return false;
            if ((excludeStolen || excludeContraband || excludeLuxury) && !CustomerIndicators.HasOrdinaryTooltip(element)) return false;
            PublicRisk risk = excludeStolen || excludeContraband ? clues.KnownRisk(element) : PublicRisk.None;
            return (!excludeStolen || (risk & PublicRisk.Stolen) == 0) &&
                (!excludeContraband || (risk & PublicRisk.Contraband) == 0) &&
                (!excludeLuxury || !item.IsTag("LUXURY_ITEM"));
        }
        private List<GameItem> Stock()
        {
            var locations = ShopInventory.OwnedLocations(false);
            return locations.Where(l => l.Ordinary || l.Role == "counter").SelectMany(l => l.Inventory.childItems)
                .Where(EligibleStock).Distinct().ToList();
        }
        private CustomUIBuilder Window()
        {
            var b = UiLayout.Rebuild(ref window, ModEntry.Id + ".basket", ShopSettings.Text("f34.open"), 600, 435);
            b.OnClose(() => { window = null; StopPlace(); }); return b;
        }
        private void Draw()
        {
            if (!Context() || !clues.HasBuyingDemand) return;
            showingResults = false;
            var stock = Stock();
            var b = Window();
            b.AddLabel(shownBudget.HasValue ? ShopSettings.Format("f34.budget", shownBudget) : ShopSettings.Text("f34.unshown"))
                .AddInput(ShopSettings.Text("f34.limit"), typed, s => typed = s, "limit", s => typed = s);
            b.BeginRow().AddToggle(ShopSettings.Text("f34.exclude_stolen"), excludeStolen, value => { excludeStolen = value; page = 0; Draw(); }, "exclude_stolen")
                .AddToggle(ShopSettings.Text("f34.exclude_contraband"), excludeContraband, value => { excludeContraband = value; page = 0; Draw(); }, "exclude_contraband").End();
            b.BeginRow().AddToggle(ShopSettings.Text("f34.exclude_luxury"), excludeLuxury, value => { excludeLuxury = value; page = 0; Draw(); }, "exclude_luxury")
                .AddToggle(ShopSettings.Text("f34.demand_only"), demandOnly, value => { demandOnly = value; page = 0; Draw(); }, "demand_only").End();
            b.BeginScrollFlexible(120);
            page = Math.Max(0, Math.Min(page, (stock.Count - 1) / 24));
            ItemGrid.Add(b, stock.Skip(page * 24).Take(24).Select(item =>
            {
                bool priced = TryQuote(item, out int price, out bool onCounter);
                return new ItemGrid.Entry(item)
                {
                    Selected = !excluded.Contains(item), Enabled = priced,
                    Badge = priced ? price.ToString() : ShopSettings.Text("f34.unpriced"),
                    QuoteLabel = priced ? FormatQuote(price, onCounter) : ShopSettings.Text("f34.unpriced"),
                    PurchaseLabel = purchaseLabel?.Invoke(item)
                };
            }), new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 130, CardHeight = 100,
                OnClick = entry => { if (!excluded.Add(entry.Item)) excluded.Remove(entry.Item); entry.Selected = !excluded.Contains(entry.Item); } });
            b.End();
            UiLayout.Pager(b, page, stock.Count, 24, next => { page = next; Draw(); });
            b.AddButton(ShopSettings.Text("f34.compare"), Compare, "compare").Show();
        }
        private Stamp Capture(GameItem item)
        {
            bool accepted = customer.GetClientBlueprint().IsClientBuyingThisItem(item) && GeneralHelper.IsItemOwned(item);
            int price = 0;
            if (accepted && (!TryQuote(item, out price, out bool onCounter) || !onCounter)) return null;
            return new Stamp { Item = item, Source = item.parentInventory, Count = item.unitCount, Shape = item.modifiedShape?.Clone(),
                Accepted = accepted, Price = price, State = InventoryArrangeTransaction.StateSignature(item) };
        }
        private bool SameStamp(Stamp stamp)
        {
            GameItem item = stamp.Item;
            if (item == null || !ReferenceEquals(item.parentInventory, stamp.Source) || item.unitCount != stamp.Count ||
                !ItemGridMove.SameShape(item.modifiedShape, stamp.Shape) ||
                InventoryArrangeTransaction.StateSignature(item) != stamp.State) return false;
            bool accepted = customer.GetClientBlueprint().IsClientBuyingThisItem(item) && GeneralHelper.IsItemOwned(item);
            return accepted == stamp.Accepted && (!accepted || TryQuote(item, out int price, out bool onCounter) && onCounter && price == stamp.Price);
        }
        private bool Counter(out List<Stamp> stamps, out int total, out int units)
        {
            stamps = new List<Stamp>(); total = units = 0;
            var desk = EmporiumEntry.Instance?.backInvinvElement;
            if (desk?.inventoryShape == null || Sellable == null || SumQuotes == null) return false;
            foreach (GameItem item in desk.childItems)
            {
                Stamp stamp = Capture(item);
                if (stamp == null || stamp.Shape == null) return false;
                stamps.Add(stamp);
                if (!stamp.Accepted) continue;
                if (stamp.Price > int.MaxValue - total || item.unitCount > int.MaxValue - units) return false;
                total += stamp.Price; units += item.unitCount;
            }
            var nativeItems = Sellable.Invoke(null, new object[] { customer.GetClientBlueprint() }) as List<GameItem>;
            return nativeItems != null && new HashSet<GameItem>(nativeItems).SetEquals(stamps.Where(stamp => stamp.Accepted).Select(stamp => stamp.Item)) &&
                SumQuotes.Invoke(null, new object[] { nativeItems }) is int nativeTotal && nativeTotal == total;
        }
        private void Compare()
        {
            if (!Context() || !Budget(out int limit)) { ShopNotice.Show(ShopSettings.Text("f34.unshown")); return; }
            StopPlace(); showingResults = true;
            if (!Counter(out var existing, out int baseline, out int baseUnits))
            { ShopNotice.Show(ShopSettings.Text("f34.quote_changed")); return; }
            var desk = EmporiumEntry.Instance.backInvinvElement;
            var stock = Stock().Where(item => item.parentInventory != desk && !excluded.Contains(item)).Select(item =>
                TryQuote(item, out int price, out bool onCounter) && price <= limit && price >= 0
                    ? new Offer { Item = item, Count = item.unitCount, Price = price, OnCounter = onCounter, Source = item.parentInventory,
                        SourceShape = item.modifiedShape?.Clone(), State = InventoryArrangeTransaction.StateSignature(item) } : null)
                .Where(offer => offer != null).OrderBy(offer => offer.Price).ToList();
            int eligibleCount = stock.Count;
            if (stock.Count > 48) stock = stock.Take(24).Concat(stock.Skip(stock.Count - 24)).ToList();
            var entries = stock.Select(offer => new CustomerBasketSearch.Entry<Offer> { Item = offer, Price = offer.Price, Units = offer.Count }).ToList();
            var options = CustomerBasketSearch.Compare<Offer, GridShape>(entries, baseline, baseUnits, limit,
                (offer, shapes) => GridPlacements.Empty(desk, offer.Item, shapes)?.itemGridShape?.Clone());
            var b = Window();
            if (eligibleCount > stock.Count) b.AddLabel(ShopSettings.Format("f34.limited", stock.Count, eligibleCount));
            if (baseline > limit) b.AddLabel(ShopSettings.Text("f34.counter_over_budget"));
            else if (options.Count == 0) b.AddLabel(ShopSettings.Text("f34.noOffer"));
            b.BeginScrollFlexible(120);
            ScrollRect scroll = b.CurrentContainer.GetComponentInParent<ScrollRect>();
            if (scroll != null) scroll.horizontal = true;
            float boardWidth = Mathf.Max(12, UiLayout.Fit(600, 435).x - 64);
            int index = 0;
            foreach (var choice in options)
            {
                var basket = new Basket { Counter = existing, Total = choice.Total, Units = choice.Units, Budget = limit,
                    CustomerBudget = shownBudget, Desk = desk, DeskShape = desk.inventoryShape.Clone(), Callbacks = CallbackSignature(desk) };
                basket.Offers.AddRange(choice.Items.Select(entry => entry.Item));
                basket.Shapes.AddRange(choice.Shapes);
                b.AddLabel(ShopSettings.Format("f34.summary", basket.Units, basket.Total, limit - basket.Total));
                var display = basket.Counter.Select(stamp => new ItemGrid.Entry(stamp.Item, stamp.Source, stamp.Shape)
                {
                    Enabled = stamp.Accepted, Selectable = false, Draggable = false,
                    QuoteLabel = stamp.Accepted ? FormatQuote(stamp.Price, true) : null,
                    PurchaseLabel = purchaseLabel?.Invoke(stamp.Item)
                }).ToList();
                for (int i = 0; i < basket.Offers.Count; i++)
                {
                    Offer offer = basket.Offers[i];
                    display.Add(new ItemGrid.Entry(offer.Item, offer.Source, basket.Shapes[i])
                    {
                        Selected = true, Selectable = false, Draggable = false,
                        QuoteLabel = FormatQuote(offer.Price, offer.OnCounter), PurchaseLabel = purchaseLabel?.Invoke(offer.Item)
                    });
                }
                var preview = ItemGrid.Add(b, display, new ItemGrid.Options { Board = basket.DeskShape,
                    CellSize = Mathf.Clamp(boardWidth / basket.DeskShape.width, 12, 34), HighContrast = true });
                preview.name = "ShopAssist_CounterPreview";
                if (allowMove.Value && basket.Offers.Count != 0)
                    b.AddButton(ShopSettings.Text("f34.place"), () => BeginPlace(basket), "place_" + index++);
            }
            b.End().AddButton(ShopSettings.Text("f34.back"), Draw).Show();
        }
        private static readonly string[] CallbackFields = {
            "mayInventoryAddItemFunc", "mayInventoryRemoveItemFunc", "minSlotAmountItemFunc", "maxSlotRemoveItemFunc", "maxSlotAmountItemFunc", "maxSlotAddItemFunc",
            "isItemStateValidFunc", "slotPriorityFunc", "parentItemShapeModifierForItemFunc", "parentItemStateModifierForItemFunc", "parentItemSpriteModifierForItemFunc",
            "slotSearchPassthroughForItemFunc", "onSlotAddItemFunc", "onSlotAddItemEndFunc", "onSlotAddItemLateFunc", "onSlotRemoveItemFunc", "onSlotRemoveItemEndFunc",
            "onSlotRemoveItemLateFunc", "onSlotChangeAmountItemFunc" };
        private string CallbackSignature(GameGridInventory desk)
        {
            var parts = new List<string>();
            foreach (string name in CallbackFields)
                if (CallbackValue(desk, name) is Delegate callback) parts.Add(name + ":" + CallbackIdentity(callback));
            foreach (object owner in new object[] { desk, customer.GetClientBlueprint() })
                foreach (FieldInfo field in owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(field => field.Name))
                    if (field.GetValue(owner) is Delegate callback)
                        parts.Add(field.Name + ":" + CallbackIdentity(callback));
            return string.Join("|", parts);
        }
        private static string CallbackIdentity(Delegate callback) => string.Join(";", callback.GetInvocationList().Select(value =>
            value.Method.Module.ModuleVersionId + ":" + value.Method.MetadataToken + ":" +
            (value.Target == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Target))));
        private static object CallbackValue(GameGridInventory desk, string name) =>
            AccessTools.Property(typeof(GameGridInventory), name)?.GetValue(desk) ?? AccessTools.Field(typeof(GameGridInventory), name)?.GetValue(desk);
        private bool SafeCounter(GameGridInventory desk)
        {
            if (!InventoryAutoInsert.KnownMutationPatches() || !VerifiedCounterGameBuild(typeof(GameItem).Module.ModuleVersionId) ||
                desk == null || desk.GetType() != typeof(GameGridInventory)) return false;
            foreach (string name in CallbackFields)
            {
                object value = CallbackValue(desk, name);
                if (value == null) continue;
                if (!(value is Delegate callbacks) || !InventoryAutoInsert.KnownCallbacks(callbacks, callback =>
                    InventoryAutoInsert.Method(callback, "EmporiumEntry+<>c", name == "mayInventoryAddItemFunc" ? "<Start>b__184_16" :
                        name == "slotPriorityFunc" ? "<Start>b__184_7" :
                        name == "onSlotAddItemEndFunc" ? "<Start>b__184_17" : name == "onSlotRemoveItemEndFunc" ? "<Start>b__184_18" : ""))) return false;
            }
            StoreClient client = customer.GetClientBlueprint();
            if (client == null || client.GetType() != typeof(StoreClient) || AcceptPreference == null || LastFilterLineItem == null ||
                AcceptPreference.GetValue(client) != null ||
                client.clientIntent != StoreClient.ClientIntent.BUY && client.clientIntent != StoreClient.ClientIntent.SELLNBUY ||
                client.isRequireVariety || client.placeRightItemWhenSellingToDialogue != null ||
                StoreStation.Instance?.storeEventManager == null) return false;
            foreach (FieldInfo field in typeof(StoreClient).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (field.GetValue(client) is Delegate callback && !InventoryAutoInsert.KnownCallbacks(callback, _ => true)) return false;
            if (client.onItemAddedToCounters != null || client.onItemRemovedToCounters != null) return false;
            if (!InventoryAutoInsert.KnownCallbacks(client.onItemAddedToWeighted, callback =>
                ReferenceEquals(callback.Target, client) && callback.Method.DeclaringType == typeof(StoreClient) &&
                (callback.Method.Name == "<CompleteClientCreation>b__143_0" || callback.Method.Name == "<CompleteClientCreation>b__143_2"))) return false;
            if (!InventoryAutoInsert.KnownCallbacks(client.onItemRemovedToWeighted, callback =>
                ReferenceEquals(callback.Target, client) && callback.Method.DeclaringType == typeof(StoreClient) &&
                callback.Method.Name == "<CompleteClientCreation>b__143_1")) return false;
            return CounterMethods.All(InventoryAutoInsert.KnownPatches) &&
                StoreStation.Instance.storeEventManager.GetActiveEvents().All(SafeEvent);
        }
        private static bool SafeEvent(StoreEvent current)
        {
            // Cosmetic events with no negotiation entries do not add item features or affect prices.
            return current != null && current.GetType() == typeof(StoreEvent) && current.eventType == StoreEvent.EventType.COSMETIC &&
                current.negociationDatas != null && current.negociationDatas.Count == 0 && current.AddClientFromEvent == null &&
                string.IsNullOrEmpty(current.onUpdateActionId) && string.IsNullOrEmpty(current.onActiveActionId) &&
                string.IsNullOrEmpty(current.onRemovedActionId) && string.IsNullOrEmpty(current.addClientFromEventActionId) &&
                current.civilUnrestMod == 0 && current.upperLevelFriendlinessMod == 0 && current.secPowerMod == 0 &&
                current.revPowerMod == 0 && current.bmPowerMod == 0 && !current.isLockDown && !current.isBlockByLockDown &&
                !current.isFactionOperation;
        }
        private bool SafeOffer(GameItem item)
        {
            // The native add callbacks can start dialogue for filters and luxury items;
            // the following selling step can expose features or inspect ammunition.
            if (item == null || LastFilterLineItem == null || ReferenceEquals(LastFilterLineItem.GetValue(customer.GetClientBlueprint()), item) ||
                item.contentWindow != null || item.IsTag("FILTER_CONTAINER_TAG") || item.IsTag("LUXURY_ITEM") ||
                item.IsTag("AMMO_BOX_PACKAGED_TAG") || item.itemFeatures == null || !GeneralHelper.IsItemOwned(item) ||
                !customer.GetClientBlueprint().IsClientBuyingThisItem(item)) return false;
            return item.itemFeatures.All(feature => feature != null && feature.GetType() == typeof(ItemFeature) &&
                (feature.isDisabled || !feature.useCondition && !feature.isExposable || feature.IsExposed() ||
                    (!feature.useCondition || feature.realCondition?.identifier != null && feature.fakeCondition?.identifier != null) && feature.IsFeatureMatch()));
        }
        private bool CounterUnchanged(Basket basket)
        {
            var desk = EmporiumEntry.Instance?.backInvinvElement;
            return ReferenceEquals(desk, basket.Desk) && ItemGridMove.SameShape(desk.inventoryShape, basket.DeskShape) &&
                basket.CustomerBudget == shownBudget && customer.GetClientBlueprint().useClientBudget == basket.CustomerBudget.HasValue &&
                (!basket.CustomerBudget.HasValue || customer.GetClientBlueprint().GetBudget() == basket.CustomerBudget.Value) &&
                CallbackSignature(desk) == basket.Callbacks && desk.childItems.Count == expectedCounter.Count &&
                expectedCounter.All(stamp => desk.childItems.Contains(stamp.Item) && SameStamp(stamp));
        }
        private void BeginPlace(Basket basket)
        {
            if (!Context() || !allowMove.Value || placing != null || !Budget(out int budget) || budget != basket.Budget) return;
            var nego = NegociationUIManager.Instance;
            if (nego?.uiObject?.activeInHierarchy != true || !SafeCounter(basket.Desk))
            { ShopNotice.Show(ShopSettings.Text("f34.callbacks")); return; }
            expectedCounter.Clear(); expectedCounter.AddRange(basket.Counter);
            if (!CounterUnchanged(basket) || !ValidateOffers(basket, 0))
            { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.changed")); return; }
            placing = basket; nextPlace = 0; expectedBudget = budget;
            foreach (Offer offer in basket.Offers) pending.Enqueue(offer);
        }
        private bool ValidateOffers(Basket basket, int from)
        {
            var stock = new HashSet<GameItem>(Stock());
            var shapes = new List<GridShape>();
            for (int i = from; i < basket.Offers.Count; i++)
            {
                Offer offer = basket.Offers[i];
                if (!stock.Contains(offer.Item) || !Eligible(offer.Item) || excluded.Contains(offer.Item) || offer.Item.unitCount != offer.Count ||
                    !ReferenceEquals(offer.Item.parentInventory, offer.Source) || !ItemGridMove.SameShape(offer.Item.modifiedShape, offer.SourceShape) ||
                    InventoryArrangeTransaction.StateSignature(offer.Item) != offer.State ||
                    !InventoryRoutes.AutoTakeAllowed(offer.Item.parentInventory) || !WholeStackTransfer.SafeSource(offer.Item) || !SafeOffer(offer.Item) ||
                    !TryQuote(offer.Item, out int price, out bool onCounter) || onCounter || price != offer.Price ||
                    GridPlacements.Overlaps(basket.Shapes[i], shapes) || GridPlacements.Exact(basket.Desk, offer.Item, basket.Shapes[i]) == null) return false;
                shapes.Add(basket.Shapes[i]);
            }
            return true;
        }
        internal void Tick()
        {
            try { TickStaging(); }
            catch (Exception error)
            {
                StopPlace();
                Debug.LogWarning("[ShopAssist] Customer basket staging stopped: " + error);
                ShopNotice.Show(ShopSettings.Text("f34.changed"));
            }
        }
        private void TickStaging()
        {
            if (customer != null && (!ShopInventory.InStore || !clues.HasBuyingDemand || customer != clues.Customer ||
                run != PlayerStore.Instance.runID || saveSlot != PlayerStore.Instance.saveSlotId)) { Reset(); return; }
            if (placing == null) return;
            var nego = NegociationUIManager.Instance;
            if (!enabled.Value || !allowMove.Value || nego?.uiObject?.activeInHierarchy != true || !Budget(out int limit) || limit != expectedBudget ||
                !SafeCounter(placing.Desk) || !CounterUnchanged(placing))
            { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.changed")); return; }
            if (waiting != null)
            {
                if (quotes.TryGetValue(waiting.Item, out var fresh) && fresh.Revision > waitingAfter)
                {
                    if (fresh.Price != waiting.Price || fresh.Count != waiting.Count || !Counter(out _, out int total, out _) ||
                        total != expectedCounter.Where(stamp => stamp.Accepted).Sum(stamp => stamp.Price))
                    { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.quote_changed")); return; }
                    waiting = null;
                    if (pending.Count == 0) { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.ready")); return; }
                }
                else if (Time.unscaledTime < until) return;
                else { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.quote_changed")); return; }
            }
            if (!ValidateOffers(placing, nextPlace))
            { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.changed")); return; }
            Offer move = pending.Dequeue();
            var from = ShopInventory.OwnedLocations(false).Find(location => location.Ordinary && location.Inventory == move.Item.parentInventory);
            if (from == null) { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.changed")); return; }
            waitingAfter = revision;
            bool restored;
            if (!ItemGridMove.TryMove(move.Item, placing.Desk, placing.Shapes[nextPlace++], out restored))
            {
                var result = new OperationResult("f34.open");
                result.Skip(move.Item, move.Count, restored ? "inv.changed" : "inv.inspect", from.Name, !restored);
                foreach (Offer remaining in pending) result.Skip(remaining.Item, remaining.Count, "operation.not_attempted");
                StopPlace(); ShopNotice.Report(result); return;
            }
            Stamp staged = Capture(move.Item);
            if (staged == null || !staged.Accepted || staged.Price != move.Price || staged.Count != move.Count)
            { StopPlace(); ShopNotice.Show(ShopSettings.Text("f34.quote_changed")); return; }
            expectedCounter.Add(staged); waiting = move; until = Time.unscaledTime + 2f; nego.RefreshUI();
        }
        public void Dispose()
        {
            if (active == this) active = null;
            ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Reset; settings.Changed -= Changed; Reset();
        }
    }
}
