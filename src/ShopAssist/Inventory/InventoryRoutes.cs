using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    internal sealed class InventoryRoutes : IDisposable
    {
        private const string RulesWindow = ModEntry.Id + ".routes", PlanWindow = ModEntry.Id + ".route_preview";
        private readonly ShopSettings settings;
        private readonly CustomerIndicators clues;
        private readonly Action<string> log;
        private readonly RoutingStore store;
        private readonly ConfigEntry<bool> rulesEnabled, routingEnabled, previewEnabled, occupiedDrag;
        private readonly ConfigEntry<int> previewMode;
        private readonly Func<KeyCode> key;
        private CustomUIWindow rulesWindow, planWindow;
        private ShopInventory.Location editing;
        private RoutingRule draft;
        private sealed class DraftState
        {
            internal RoutingRule Rule;
            internal string Priority;
            internal string Sample;
            internal int Category;
            internal bool Excluded;
        }
        private sealed class SampleStamp
        {
            internal GameItem Item;
            internal GameInventory Source;
            internal string Name;
        }
        private readonly Dictionary<GameInventory, DraftState> drafts = new Dictionary<GameInventory, DraftState>();
        private List<GameItem> samples = new List<GameItem>(), selection;
        private string priority = "0", run;
        private int slot, categoryIndex, sampleIndex, planRevision;
        private bool excluded, started, executing, samplesDirty;
        private float nextSamplesRefresh;
        private List<SampleStamp> sampleStamps = new List<SampleStamp>();
        private List<ShopInventory.Location> locationStamps = new List<ShopInventory.Location>();
        private RoutingPlan plan;

        internal InventoryRoutes(ShopSettings settings, CustomerIndicators clues, Action<string> log)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.clues = clues ?? throw new ArgumentNullException(nameof(clues));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            store = new RoutingStore(log);
            rulesEnabled = settings.Toggle("f07.enabled", "f07.tip");
            routingEnabled = settings.Toggle("f08.enabled", "f08.tip");
            occupiedDrag = settings.Toggle("f40.occupied_drag", "f40.occupied_drag.tip", true);
            previewEnabled = settings.Toggle("f09.enabled", "f09.tip");
            previewMode = settings.Choice("f09.mode", "f09.mode.tip", new[] { "f09.always", "f09.conflicts", "f09.simulate" });
            key = settings.Key("f08.key", "f08.key.tip");
            settings.Action("f07.open", () => OpenRules(null), () => rulesEnabled.Value && ShopInventory.InStore);
            settings.Action("f08.open", OpenPlan, () => routingEnabled.Value && ShopInventory.InStore && ShopSelection.Current().Count != 0);
        }

        public void Start()
        {
            if (started) return;
            InventoryContextActions.RegisterGrid("f07.open", source => ShopSettings.Text("f07.open"),
                source => started && rulesEnabled.Value && Locations().Any(location => location.Inventory == source), OpenRules);
            try
            {
                InventoryContextActions.RegisterItem("f08.context", _ => ShopSettings.Text("f08.open"),
                    item => started && routingEnabled.Value && ShopInventory.InStore && ShopSelection.Current().Contains(item),
                    _ => OpenPlan());
                OccupiedSelectionGesture.Register(() => started && occupiedDrag.Value && (routingEnabled.Value ||
                    settings.FeatureAvailability("f40.enabled").Allowed || settings.FeatureAvailability("f41.enabled").Allowed));
                WholeStackTransfer.Acquire();
            }
            catch { OccupiedSelectionGesture.Unregister(); InventoryContextActions.Unregister("f08.context"); InventoryContextActions.Unregister("f07.open"); throw; }
            started = true;
            settings.Changed += SettingsChanged;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        private bool SameRun() => ShopInventory.InStore && PlayerStore.Instance.runID == run && PlayerStore.Instance.saveSlotId == slot;
        private void SetRun() { run = PlayerStore.Instance.runID; slot = PlayerStore.Instance.saveSlotId; }

        public void Tick()
        {
            if (!started) return;
            if ((rulesWindow != null || planWindow != null) && !SameRun()) { CloseRules(); ClosePlan(); }
            if (rulesWindow != null && !rulesWindow.IsAlive) CloseRules();
            if (planWindow != null && (!planWindow.IsAlive || !routingEnabled.Value || planRevision != store.Revision)) ClosePlan();
            if (rulesWindow != null && Time.unscaledTime >= nextSamplesRefresh && !VisualChoices.IsOpen &&
                !StoreUIManager.IsAnyTyping() && !UIDragHandler.isDraggingAnyUI &&
                !Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2))
            {
                nextSamplesRefresh = Time.unscaledTime + 0.5f;
                if (RefreshSamples() || samplesDirty) { samplesDirty = false; RenderRules(); }
            }
            if (!routingEnabled.Value || key() == KeyCode.None || !Input.GetKeyDown(key()) || !ShopInput.Ready()) return;
            if (planWindow != null) ClosePlan(); else OpenPlan();
        }

        private void SettingsChanged()
        {
            if (!rulesEnabled.Value) { CloseRules(); InventoryContextActions.Hide("f07.open"); }
            if (!routingEnabled.Value) { ClosePlan(); InventoryContextActions.Hide("f08.context"); }
        }
        private void SceneChanged(Scene before, Scene after) => Reset();

        private static List<ShopInventory.Location> Locations() => ShopInventory.OwnedLocations(false)
            .Where(location => location.Inventory != null && ShopInventory.IsMainStockBranch(location)).ToList();

        private void OpenRules(GameGridInventory preferred)
        {
            if (!started || !rulesEnabled.Value || !ShopInventory.InStore || CustomUIManager.Instance == null) return;
            var locations = Locations();
            ShopInventory.Location requested = preferred == null ? null : locations.FirstOrDefault(location => ReferenceEquals(location.Inventory, preferred));
            if (preferred != null && requested == null) { ShopNotice.Show(ShopSettings.Text("f08.changed")); return; }
            if (rulesWindow != null && rulesWindow.IsAlive && SameRun())
            {
                if (requested != null && !ReferenceEquals(editing?.Inventory, preferred))
                { SwitchDraft(requested); RenderRules(); }
                rulesWindow?.BringToFront();
                return;
            }
            CloseRules();
            SetRun();
            if (!store.Load()) { ShopNotice.Show(ShopSettings.Text("f07.unreadable")); return; }
            if (locations.Count == 0) { ShopNotice.Show(ShopSettings.Text("f07.no_area")); return; }
            editing = requested ?? locations[0];
            RefreshSamples();
            LoadDraft();
            RenderRules();
        }

        private string CurrentSample() => sampleIndex >= 0 && sampleIndex < samples.Count ? samples[sampleIndex].identifier : null;

        private bool RefreshSamples()
        {
            string previous = CurrentSample();
            var currentLocations = Locations();
            bool locationsChanged = currentLocations.Count != locationStamps.Count || currentLocations.Where((location, i) =>
                !ReferenceEquals(location.Inventory, locationStamps[i].Inventory) || !ReferenceEquals(location.Owner, locationStamps[i].Owner) ||
                location.Name != locationStamps[i].Name || location.Role != locationStamps[i].Role || location.Page != locationStamps[i].Page ||
                !location.Path.SequenceEqual(locationStamps[i].Path)).Any();
            locationStamps = currentLocations;
            var visibleLocations = ShopInventory.OwnedLocations(true).Where(ShopInventory.IsMainStockBranch).ToList();
            var visibleInventories = new HashSet<GameInventory>(visibleLocations.Select(location => location.Inventory));
            var sampleItems = ShopSelection.Current().Where(item => item != null && visibleInventories.Contains(item.parentInventory)).ToList();
            foreach (ShopInventory.Location location in visibleLocations)
            {
                foreach (GameItem item in location.Inventory.childItems)
                    if (item is GameItemElement element && VisibleInventoryViews.Visible(element) && ShopInventory.Owned(item))
                        sampleItems.Add(item);
            }
            var current = sampleItems.Where(item => item != null && !string.IsNullOrEmpty(item.identifier))
                .GroupBy(item => item.identifier, StringComparer.Ordinal).Select(group => group.First()).ToList();
            var stamps = current.Select(item => new SampleStamp { Item = item, Source = item.parentInventory, Name = item.GetDisplayName(true) }).ToList();
            bool changed = stamps.Count != sampleStamps.Count || stamps.Where((stamp, i) =>
                !ReferenceEquals(stamp.Item, sampleStamps[i].Item) || !ReferenceEquals(stamp.Source, sampleStamps[i].Source) ||
                stamp.Name != sampleStamps[i].Name).Any();
            samples = current; sampleStamps = stamps;
            if (changed || locationsChanged) samplesDirty = true;
            int selected = previous == null ? -1 : samples.FindIndex(item => item.identifier == previous);
            sampleIndex = Math.Max(0, selected);
            return changed || locationsChanged;
        }

        private void LoadDraft()
        {
            draft = store.Get(editing) ?? new RoutingRule();
            priority = draft.priority.ToString();
            categoryIndex = sampleIndex = 0;
            excluded = false;
        }

        private void SwitchDraft(ShopInventory.Location location)
        {
            if (editing?.Inventory != null && draft != null)
                drafts[editing.Inventory] = new DraftState { Rule = draft, Priority = priority, Category = categoryIndex, Sample = CurrentSample(), Excluded = excluded };
            editing = location;
            if (drafts.TryGetValue(location.Inventory, out DraftState state))
            {
                draft = state.Rule; priority = state.Priority; categoryIndex = state.Category; excluded = state.Excluded;
                sampleIndex = Math.Max(0, samples.FindIndex(item => item.identifier == state.Sample));
            }
            else LoadDraft();
        }

        private void RenderRules()
        {
            if (!SameRun() || !rulesEnabled.Value || CustomUIManager.Instance == null || editing == null) { CloseRules(); return; }
            var locations = Locations();
            int chosen = locations.FindIndex(location => ReferenceEquals(location.Inventory, editing.Inventory));
            bool available = chosen >= 0;
            if (available) editing = locations[chosen];
            var book = store.Snapshot();
            var ordered = book?.rules ?? new List<RoutingRule>();
            int orderIndex = ordered.FindIndex(rule => rule.location == editing.Key());
            Vector2? position = rulesWindow != null && rulesWindow.IsAlive ? ((RectTransform)rulesWindow.root.transform).anchoredPosition : (Vector2?)null;
            var b = CustomUIManager.Window(RulesWindow, ShopSettings.Text("f07.open"), CustomUIManager.LAYER_META)
                .SetSize(660, 520).SetDraggable(true).SetCloseOnEscape(true);
            rulesWindow = b.GetWindow();
            CustomUIWindow thisView = rulesWindow;
            b.OnClose(() => { if (ReferenceEquals(rulesWindow, thisView)) rulesWindow = null; });
            VisualChoices.Location(b, ShopSettings.Text("f07.open"), available ? editing : null,
                () => Locations(), location =>
            {
                if (!SameRun() || !ShopInventory.TryRefresh(location, out ShopInventory.Location refreshed) || !ShopInventory.IsMainStockBranch(refreshed))
                { ShopNotice.Show(ShopSettings.Text("f08.changed")); return; }
                SwitchDraft(refreshed); RenderRules();
            }, "area");
            if (!available) b.AddLabel(ShopSettings.Text("f07.area_unavailable"));
            b.BeginRow().AddToggle(ShopSettings.Text("f07.receive"), draft.receive, value => draft.receive = value, "receive");
            b.Last.SetInteractable(editing.Ordinary && !editing.IsMachine);
            b.AddToggle(ShopSettings.Text("f07.exclusive"), draft.exclusive, value => draft.exclusive = value, "exclusive").End();
            b.BeginRow().AddInput(ShopSettings.Text("f07.priority"), priority, value => priority = value, "priority", value => priority = value);
            b.AddToggle(ShopSettings.Text("f07.exclude"), excluded, value => excluded = value, "exclude").End();
            string[] categoryNames = TypeHelper.ALL_TYPES_LIST.Select(TypeHelper.GetTypeDisplayName).ToArray();
            b.BeginRow().AddDropdown(categoryNames, categoryIndex, index => categoryIndex = index, "category");
            b.AddButton(ShopSettings.Text("f07.add_category"), () =>
            {
                if (categoryIndex < 0 || categoryIndex >= TypeHelper.ALL_TYPES_LIST.Count) return;
                Add(excluded ? draft.excludedCategories : draft.categories, TypeHelper.ALL_TYPES_LIST[categoryIndex]);
                RenderRules();
            }, "add_category").End();
            if (samples.Count != 0)
            {
                b.BeginRow();
                VisualChoices.Slot(b, ShopSettings.Text("f07.add_item"), samples[Math.Max(0, Math.Min(sampleIndex, samples.Count - 1))], null,
                    () =>
                    {
                        RoutingRule choosingDraft = draft;
                        bool choosingExcluded = excluded;
                        VisualChoices.ChooseItem(ShopSettings.Text("f07.add_item"), () => { RefreshSamples(); return samples; }, null, item =>
                        {
                            if (!SameRun() || !rulesEnabled.Value || !ReferenceEquals(draft, choosingDraft)) return;
                            int index = samples.IndexOf(item);
                            if (index < 0 || string.IsNullOrEmpty(item.identifier)) return;
                            sampleIndex = index;
                            Add(choosingExcluded ? choosingDraft.excludedIds : choosingDraft.itemIds, item.identifier);
                            RenderRules();
                        });
                    }, "sample");
                b.AddButton(ShopSettings.Text("f07.add_item"), () =>
                {
                    if (sampleIndex < 0 || sampleIndex >= samples.Count) return;
                    Add(excluded ? draft.excludedIds : draft.itemIds, samples[sampleIndex].identifier);
                    RenderRules();
                }, "add_item").End();
            }
            b.BeginScrollFlexible(120);
            if (ordered.Count != 0)
            {
                b.AddLabel(ShopSettings.Text("f07.order"));
                for (int i = 0; i < ordered.Count; i++)
                {
                    RoutingRule rule = ordered[i];
                    ShopInventory.Location destination = locations.FirstOrDefault(location => location.Key() == rule.location);
                    string label = (i + 1) + ". " + (destination?.Name ?? ShopSettings.Text("f07.unknown_area"));
                    if (destination != null && ReferenceEquals(destination.Inventory, editing.Inventory)) label = "✓ " + label;
                    if (destination == null) b.AddLabel(label);
                    else b.AddButton(label, () =>
                    {
                        if (!SameRun() || !ShopInventory.TryRefresh(destination, out var live) || !ShopInventory.IsMainStockBranch(live))
                        { ShopNotice.Show(ShopSettings.Text("f08.changed")); return; }
                        SwitchDraft(live); RenderRules();
                    });
                }
            }
            AddRuleRows(b, draft.categories, false, true);
            AddRuleRows(b, draft.itemIds, false, false);
            AddRuleRows(b, draft.excludedCategories, true, true);
            AddRuleRows(b, draft.excludedIds, true, false);
            b.End().BeginRow().AddButton(ShopSettings.Text("f07.save"), SaveDraft, "save");
            b.Last.SetInteractable(available);
            b.AddButton(ShopSettings.Text("f07.delete"), DeleteDraft, "delete");
            b.Last.SetInteractable(available && orderIndex >= 0);
            b.AddButton(ShopSettings.Text("f07.order_up"), () => MoveRule(-1));
            b.Last.SetInteractable(orderIndex > 0);
            b.AddButton(ShopSettings.Text("f07.order_down"), () => MoveRule(1));
            b.Last.SetInteractable(orderIndex >= 0 && orderIndex + 1 < ordered.Count);
            b.End().Show();
            samplesDirty = false;
            if (position.HasValue) ((RectTransform)rulesWindow.root.transform).anchoredPosition = position.Value;
        }

        private void AddRuleRows(CustomUIBuilder builder, List<string> values, bool isExcluded, bool category)
        {
            foreach (string value in values.ToArray())
            {
                string text = (isExcluded ? "− " : "+ ") + (category ? TypeHelper.GetTypeDisplayName(value) : ItemName(value));
                builder.AddButton(text + "  ×", () => { values.Remove(value); RenderRules(); });
                if (builder.Last.text != null) builder.Last.text.richText = false;
            }
        }

        private string ItemName(string id)
        {
            string visible = samples.FirstOrDefault(item => item.identifier == id)?.GetDisplayName(true);
            if (!string.IsNullOrEmpty(visible)) return visible;
            string key = "item_" + id + "_name";
            string localized = LocHelper.GetLocalizedItem(key);
            return string.IsNullOrWhiteSpace(localized) || localized == key ? id : localized;
        }
        private static void Add(List<string> values, string value)
        {
            if (values.Contains(value)) return;
            if (values.Count >= RoutingRules.MaxEntries) { ShopNotice.Show(ShopSettings.Text("f07.limit")); return; }
            values.Add(value);
        }

        private void SaveDraft()
        {
            if (!SameRun() || !int.TryParse(priority, out int value) || value < -100 || value > 100)
            { ShopNotice.Show(ShopSettings.Text("f07.invalid")); return; }
            draft.priority = value;
            if (!store.Save(editing, draft)) { ShopNotice.Show(ShopSettings.Text("f07.failed")); return; }
            LoadDraft();
            ClosePlan();
            ShopNotice.Show(ShopSettings.Text("f07.saved"));
            RenderRules();
        }

        private void DeleteDraft()
        {
            if (!SameRun() || !store.Remove(editing)) { ShopNotice.Show(ShopSettings.Text("f07.failed")); return; }
            LoadDraft();
            ClosePlan();
            RenderRules();
        }

        private void MoveRule(int direction)
        {
            if (!SameRun() || !store.Move(editing.Key(), direction)) return;
            ClosePlan();
            RenderRules();
        }

        private void OpenPlan()
        {
            if (!started || !routingEnabled.Value || !ShopInventory.InStore || CustomUIManager.Instance == null || !store.Load()) return;
            if (planWindow != null && planWindow.IsAlive && SameRun()) { planWindow.BringToFront(); return; }
            ClosePlan();
            SetRun();
            selection = ShopSelection.Current();
            if (selection.Count == 0) { ShopNotice.Show(ShopSettings.Text("f08.select")); return; }
            try
            {
                plan = RoutingPlan.Create(store.Snapshot(), selection, clues);
                planRevision = store.Revision;
                if (!previewEnabled.Value || (previewMode.Value == 1 && plan.Skipped.Count == 0)) ApplyPlan();
                else RenderPlan();
            }
            catch (Exception error)
            {
                log("F08 preview failed: " + error);
                ClosePlan();
                ShopNotice.Show(ShopSettings.Text("f08.failed"));
            }
        }

        private void RenderPlan()
        {
            if (!SameRun() || !routingEnabled.Value || plan == null || CustomUIManager.Instance == null) { ClosePlan(); return; }
            Vector2? position = planWindow != null && planWindow.IsAlive ? ((RectTransform)planWindow.root.transform).anchoredPosition : (Vector2?)null;
            var b = CustomUIManager.Window(PlanWindow, ShopSettings.Text("f09.open"), CustomUIManager.LAYER_META)
                .SetSize(650, 460).SetDraggable(true).SetCloseOnEscape(true);
            planWindow = b.GetWindow();
            CustomUIWindow thisView = planWindow;
            b.OnClose(() => { if (ReferenceEquals(planWindow, thisView)) planWindow = null; });
            b.BeginScrollFlexible(120);
            foreach (var group in plan.Moves.GroupBy(move => move.Target.Inventory))
            {
                b.AddLabel(ShopSettings.Format("f09.target", group.First().Target.Name, group.Sum(move => (long)move.Quantity)));
                if (b.Last.text != null) b.Last.text.richText = false;
                long stacked = group.Where(move => move.Transfer.IsStack).Sum(move => (long)move.Quantity);
                if (stacked > 0) b.AddLabel(ShopSettings.Format("f09.merged", stacked));
                ItemGrid.Add(b, group.Select(move => new ItemGrid.Entry(move.Item) { Selected = true,
                    Badge = move.Quantity.ToString() }), new ItemGrid.Options { Columns = 22, CellSize = 22 });
            }
            foreach (RoutingPlan.Skip skipped in plan.Skipped)
            {
                b.AddLabel(skipped.Item.GetDisplayName(true) + " · " + ShopSettings.Text(skipped.Reason));
                if (b.Last.text != null) b.Last.text.richText = false;
            }
            if (plan.Moves.Count == 0 && plan.Skipped.Count == 0) b.AddLabel(ShopSettings.Text("f08.select"));
            b.End();
            if (!(previewEnabled.Value && previewMode.Value == 2))
            {
                b.AddButton(ShopSettings.Text("f08.apply"), ApplyPlan, "apply");
                b.Last.SetInteractable(plan.Moves.Count != 0);
            }
            b.Show();
            if (position.HasValue) ((RectTransform)planWindow.root.transform).anchoredPosition = position.Value;
        }

        private void ApplyPlan()
        {
            if (executing || plan == null || selection == null || !SameRun() || !routingEnabled.Value ||
                planRevision != store.Revision || (previewEnabled.Value && previewMode.Value == 2)) return;
            executing = true;
            var result = new OperationResult("f08.open");
            int index = 0;
            string problem = null;
            OperationResult.ItemIdentity currentIdentity = null;
            try
            {
                var locations = ShopInventory.OwnedLocations(false);
                foreach (RoutingPlan.Skip skipped in plan.Skipped)
                    result.Skip(skipped.Item, skipped.Item.unitCount, skipped.Reason,
                        locations.Find(location => location.Inventory == skipped.Item.parentInventory)?.Name);
                RoutingPlan fresh = RoutingPlan.Create(store.Snapshot(), selection, clues);
                if (!plan.SameAs(fresh)) { ShopNotice.Show(ShopSettings.Text("f08.changed")); ClosePlan(); return; }
                for (; index < plan.Moves.Count; index++)
                {
                    RoutingPlan.Move entry = plan.Moves[index];
                    var currentLocations = ShopInventory.OwnedLocations(false);
                    if (!SameRun() || planRevision != store.Revision ||
                        !ShopInventory.TryRefresh(entry.Source, currentLocations, out ShopInventory.Location source) ||
                        !ShopInventory.TryRefresh(entry.Target, currentLocations, out ShopInventory.Location target) ||
                        !ReferenceEquals(entry.Item.parentInventory, source.Inventory) || entry.Item.unitCount != entry.Quantity ||
                        RequestBook.Reserved(entry.Item))
                    { problem = "f08.changed"; break; }
                    currentIdentity = OperationResult.Capture(entry.Item);
                    bool fromCounter = !ShopInventory.IsMainStockBranch(source);
                    bool Scope()
                    {
                        if (!SameRun() || planRevision != store.Revision) return false;
                        var live = ShopInventory.OwnedLocations(false);
                        if (!ShopInventory.TryRefresh(source, live, out var from) || !ShopInventory.TryRefresh(target, live, out var to) ||
                            !to.Ordinary || !ShopInventory.IsMainStockBranch(to)) return false;
                        if (!fromCounter) return from.Ordinary && ShopInventory.IsMainStockBranch(from);
                        return from.Owner == null && from.Role == "counter" &&
                            (entry.Item.parentInventory != from.Inventory || ShopInventory.CanRouteFromCounter(entry.Item, from));
                    }
                    string path = source.Name + " → " + target.Name;
                    var outcome = WholeStackTransfer.Apply(entry.Transfer, "f08.open", path, source.Key(true), target.Key(true), Scope);
                    if (outcome != WholeStackTransfer.Result.Applied)
                    { problem = outcome == WholeStackTransfer.Result.Unchanged ? "f08.changed" : "f08.pending"; break; }
                    result.CompleteCaptured(currentIdentity, entry.Quantity, path);
                    currentIdentity = null;
                }
            }
            catch (Exception error) { log("F08 move interrupted: " + error); problem = !WholeStackTransfer.Ready ? "f08.pending" : "f08.failed"; }
            finally { executing = false; }
            if (problem != null)
                for (int i = index; i < plan.Moves.Count; i++)
                {
                    RoutingPlan.Move entry = plan.Moves[i];
                    string path = entry.Source?.Name + " → " + entry.Target?.Name;
                    if (i == index && currentIdentity != null) result.SkipCaptured(currentIdentity, entry.Quantity, problem, path);
                    else result.Skip(entry.Item, entry.Quantity, i == index ? problem : "operation.not_attempted", path);
                }
            ClosePlan();
            ShopNotice.Report(result);
        }

        private void CloseRules()
        {
            if (rulesWindow != null && rulesWindow.IsAlive) rulesWindow.Close();
            rulesWindow = null; editing = null; draft = null; drafts.Clear(); samples.Clear(); sampleStamps.Clear(); locationStamps.Clear(); samplesDirty = false;
        }
        private void ClosePlan()
        {
            if (planWindow != null && planWindow.IsAlive) planWindow.Close();
            planWindow = null; plan = null; selection = null;
        }

        private void Reset()
        {
            CloseRules(); ClosePlan(); InventoryContextActions.Hide("f07.open");
            InventoryContextActions.Hide("f08.context"); store.Reset(); run = null;
        }
        public void Dispose()
        {
            if (!started) return;
            started = false;
            InventoryContextActions.Unregister("f07.open");
            InventoryContextActions.Unregister("f08.context");
            OccupiedSelectionGesture.Unregister();
            WholeStackTransfer.Release();
            settings.Changed -= SettingsChanged;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
        }

    }
}
