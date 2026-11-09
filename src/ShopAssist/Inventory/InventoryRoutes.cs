using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed class InventoryRoutes : IDisposable
    {
        private static InventoryRoutes current;
        private static readonly FieldInfo NativeTitle = typeof(PixelWindow).GetField("titleText", BindingFlags.Instance | BindingFlags.NonPublic);
        private sealed class HeaderEntry
        {
            internal GameObject Root;
            internal ShopInventory.Location Location;
            internal Button Button;
            internal RectTransform Title;
            internal Vector2 TitlePosition, TitleSize;
        }
        private readonly Dictionary<PixelWindow, HeaderEntry> headers = new Dictionary<PixelWindow, HeaderEntry>();
        private float nextHeaderRefresh;
        private string automaticError;
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
        private readonly Dictionary<GameInventory, RoutingRule> drafts = new Dictionary<GameInventory, RoutingRule>();
        private List<GameItem> selection;
        private string run;
        private int slot, planRevision;
        private bool started, executing, planRulesEnabled;
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
            if (current != null && !ReferenceEquals(current, this)) throw new InvalidOperationException("Inventory routing is already active.");
            InventoryContextActions.RegisterGrid("f07.open", source => ShopSettings.Text("f07.open"),
                source => started && rulesEnabled.Value && Locations().Any(location => location.Inventory == source), OpenRules);
            try
            {
                InventoryContextActions.RegisterGrid("f08.grid", grid => ShopSettings.Text(
                    ShopSelection.Current().Any(item => ReferenceEquals(item.parentInventory, grid)) ? "f08.selected" : "f08.all"),
                    CanRouteGrid, grid => OpenPlan(grid));
                InventoryContextActions.RegisterItem("f08.context", _ => ShopSettings.Text("f08.open"),
                    item => started && routingEnabled.Value && ShopInventory.InStore && ShopSelection.Current().Contains(item),
                    _ => OpenPlan());
                OccupiedSelectionGesture.Register(() => started && occupiedDrag.Value && (routingEnabled.Value ||
                    settings.FeatureAvailability("f40.enabled").Allowed || settings.FeatureAvailability("f41.enabled").Allowed));
                WholeStackTransfer.Acquire();
            }
            catch { OccupiedSelectionGesture.Unregister(); InventoryContextActions.Unregister("f08.context"); InventoryContextActions.Unregister("f08.grid"); InventoryContextActions.Unregister("f07.open"); throw; }
            started = true;
            current = this;
            AreaLabels.RoutingHeaderInset = HeaderInset;
            settings.Changed += SettingsChanged;
            VisibleInventoryViews.Changed += RefreshHeadersSoon;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        private bool SameRun() => ShopInventory.InStore && PlayerStore.Instance.runID == run && PlayerStore.Instance.saveSlotId == slot;
        private void SetRun() { run = PlayerStore.Instance.runID; slot = PlayerStore.Instance.saveSlotId; }

        public void Tick()
        {
            if (!started) return;
            if (Time.unscaledTime >= nextHeaderRefresh)
            {
                nextHeaderRefresh = Time.unscaledTime + 0.5f;
                RefreshHeaders();
            }
            if ((rulesWindow != null || planWindow != null) && !SameRun()) { CloseRules(); ClosePlan(); }
            if (rulesWindow != null && !rulesWindow.IsAlive) CloseRules();
            if (planWindow != null && (!planWindow.IsAlive || !routingEnabled.Value || planRevision != store.Revision || planRulesEnabled != rulesEnabled.Value)) ClosePlan();
            if (!routingEnabled.Value || key() == KeyCode.None || !Input.GetKeyDown(key()) || !ShopInput.Ready()) return;
            if (planWindow != null) ClosePlan(); else OpenPlan();
        }

        private void SettingsChanged()
        {
            if (!rulesEnabled.Value) { CloseRules(); InventoryContextActions.Hide("f07.open"); }
            if (!routingEnabled.Value) { ClosePlan(); InventoryContextActions.Hide("f08.context"); InventoryContextActions.Hide("f08.grid"); }
            if (plan != null && planRulesEnabled != rulesEnabled.Value) ClosePlan();
            RefreshHeadersSoon();
        }
        private void SceneChanged(Scene before, Scene after) => Reset();

        private static List<ShopInventory.Location> Locations() => ShopInventory.OwnedLocations(false)
            .Where(location => location.Inventory != null && ShopInventory.IsMainStockBranch(location)).ToList();

        private bool CanRouteGrid(GameGridInventory grid) => started && routingEnabled.Value && ShopInventory.InStore &&
            grid != null && ShopInventory.OwnedLocations(true).Any(location => ReferenceEquals(location.Inventory, grid) &&
                !location.IsMachine && (ShopInventory.IsMainStockBranch(location) || location.Owner == null && location.Role == "counter"));

        internal static bool AutoTakeAllowed(GameInventory source) => AutomaticAllowed(source, null, false);
        internal static bool AutoReceiveAllowed(GameInventory target, GameItem item, bool allowStockFallback = false) =>
            AutomaticAllowed(target, item, true, allowStockFallback);

        private static bool AutomaticAllowed(GameInventory inventory, GameItem item, bool receiving, bool allowStockFallback = false)
        {
            InventoryRoutes owner = current;
            if (owner == null || !owner.started || inventory == null) return false;
            if (!owner.rulesEnabled.Value) return true;
            try
            {
                if (!owner.store.Load()) return false;
                var allLocations = receiving ? ShopInventory.OwnedLocations(false) : ShopInventory.MachineLocations();
                var locations = allLocations.Where(location => ReferenceEquals(location.Inventory, inventory)).ToList();
                if (locations.Count != 1) return false;
                ShopInventory.Location location = locations[0];
                if (!ShopInventory.IsMainStockBranch(location)) return true;
                string locationKey = location.Key();
                if (locationKey != null && allLocations.Count(other => other.Key() == locationKey) != 1) return false;
                RoutingRule rule = owner.store.Get(location);
                if (rule == null) return true;
                if (!receiving) return rule.ship;
                if (!(item is GameItemElement element)) return false;
                bool stockFallback = allowStockFallback && locationKey == "root:stock" &&
                    ReferenceEquals(inventory, EmporiumEntry.Instance?.invElement);
                return RoutingRules.AllowsReceiving(rule, CustomerIndicators.PublicTypes(element), stockFallback);
            }
            catch (Exception error)
            {
                string message = error.ToString();
                if (owner.automaticError != message) owner.log("Storage permission could not be checked: " + message);
                owner.automaticError = message;
                return false;
            }
        }

        internal static float HeaderInset(PixelWindow window) => current != null && window != null &&
            current.headers.TryGetValue(window, out HeaderEntry entry) && entry.Root != null && entry.Root.activeSelf ? 24f : 0f;

        private void RefreshHeadersSoon() => nextHeaderRefresh = 0f;

        private void RefreshHeaders()
        {
            var seen = new HashSet<PixelWindow>();
            if (rulesEnabled.Value && ShopInventory.InStore && WindowsHandler.current?.visibleWindows != null && CustomUIManager.Instance != null)
            {
                var locations = Locations();
                foreach (PixelWindow window in WindowsHandler.current.visibleWindows)
                {
                    if (!VisibleInventoryViews.Visible(window) || (!window.canMove && !window.forceRaycast) || window.widthPixels < 48) continue;
                    ShopInventory.Location location = locations.FirstOrDefault(candidate => candidate.Inventory is GameGridInventory grid && grid.handler != null &&
                        grid.handler.gameObject.activeInHierarchy && ReferenceEquals(grid.handler.FindParentWindow(), window));
                    if (location == null) continue;
                    seen.Add(window);
                    if (!headers.TryGetValue(window, out HeaderEntry entry) || entry.Root == null)
                    {
                        GameObject prefab = CustomUIManager.Instance.GetPrefab(CustomUIManager.ElementKind.Button, true);
                        if (prefab == null) continue;
                        GameObject root = UnityEngine.Object.Instantiate(prefab, window.handler.transform, false);
                        root.name = "ShopAssist_RoutingHeader";
                        var rect = root.transform as RectTransform;
                        var button = root.GetComponentInChildren<Button>(true);
                        var text = root.GetComponentInChildren<TMP_Text>(true);
                        if (rect == null || button == null || text == null)
                        { UnityEngine.Object.Destroy(root); continue; }
                        LayoutElement layout = root.GetComponent<LayoutElement>();
                        if (layout != null) layout.ignoreLayout = true;
                        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                        rect.pivot = new Vector2(0f, 1f);
                        rect.anchoredPosition = new Vector2(2f, -1f);
                        rect.sizeDelta = new Vector2(20f, 16f);
                        text.text = "↔"; text.richText = false; text.raycastTarget = false;
                        text.enableAutoSizing = true; text.fontSizeMin = 8f; text.fontSizeMax = 14f;
                        entry = new HeaderEntry { Root = root, Button = button };
                        if (window.hasTitle && NativeTitle?.GetValue(window) is TreeNodeRender title && title.rectTransform != null)
                        {
                            entry.Title = title.rectTransform;
                            entry.TitlePosition = entry.Title.anchoredPosition;
                            entry.TitleSize = entry.Title.sizeDelta;
                            entry.Title.anchoredPosition += new Vector2(24f * (1f - entry.Title.pivot.x), 0f);
                            entry.Title.sizeDelta -= new Vector2(24f, 0f);
                        }
                        HeaderEntry captured = entry;
                        PixelWindow capturedWindow = window;
                        button.onClick = new Button.ButtonClickedEvent();
                        button.onClick.AddListener(() =>
                        {
                            if (started && rulesEnabled.Value && VisibleInventoryViews.Visible(capturedWindow) &&
                                !UIDragHandler.isDraggingAnyUI && !StoreUIManager.IsAnyTyping() && captured.Location?.Inventory is GameGridInventory grid)
                                InventoryContextActions.RunGrid("f07.open", grid);
                        });
                        headers[window] = entry;
                        root.SetActive(true);
                        rect.SetAsLastSibling();
                        AreaLabels.Invalidate();
                    }
                    entry.Location = location;
                    entry.Button.interactable = !UIDragHandler.isDraggingAnyUI && !StoreUIManager.IsAnyTyping();
                }
            }
            foreach (PixelWindow window in headers.Keys.ToArray())
                if (!seen.Contains(window))
                {
                    HeaderEntry entry = headers[window];
                    DestroyHeader(entry);
                    headers.Remove(window);
                    AreaLabels.Invalidate();
                }
        }

        private static void DestroyHeader(HeaderEntry entry)
        {
            if (entry.Title != null)
            {
                entry.Title.anchoredPosition = entry.TitlePosition;
                entry.Title.sizeDelta = entry.TitleSize;
            }
            if (entry.Root != null) { entry.Root.SetActive(false); UnityEngine.Object.Destroy(entry.Root); }
        }

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
            LoadDraft();
            RenderRules();
        }

        private void LoadDraft() => draft = store.Get(editing) ?? new RoutingRule();

        private void SwitchDraft(ShopInventory.Location location)
        {
            if (editing?.Inventory != null && draft != null) drafts[editing.Inventory] = draft;
            editing = location;
            if (drafts.TryGetValue(location.Inventory, out RoutingRule saved)) draft = saved;
            else LoadDraft();
        }

        private void RenderRules()
        {
            if (!SameRun() || !rulesEnabled.Value || CustomUIManager.Instance == null || editing == null) { CloseRules(); return; }
            var locations = Locations();
            int chosen = locations.FindIndex(location => ReferenceEquals(location.Inventory, editing.Inventory));
            bool available = chosen >= 0;
            if (available) editing = locations[chosen];
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
            b.AddToggle(ShopSettings.Text("f07.ship"), draft.ship, value => draft.ship = value, "ship").End();
            b.BeginScrollFlexible(120);
            foreach (string category in TypeHelper.ALL_TYPES_LIST)
            {
                string captured = category;
                bool selected = draft.categories.Contains(category);
                b.AddToggle(TypeHelper.GetTypeDisplayName(category), selected, value =>
                {
                    if (value)
                    {
                        if (!draft.categories.Contains(captured) && draft.categories.Count < RoutingRules.MaxEntries)
                            draft.categories.Add(captured);
                    }
                    else draft.categories.Remove(captured);
                }, "category_" + category);
                b.Last.SetInteractable(selected || draft.categories.Count < RoutingRules.MaxEntries);
            }
            b.End().BeginRow().AddButton(ShopSettings.Text("f07.save"), SaveDraft, "save");
            b.Last.SetInteractable(available);
            b.AddButton(ShopSettings.Text("f07.delete"), DeleteDraft, "delete");
            b.Last.SetInteractable(available && store.Get(editing) != null);
            b.End().Show();
            if (position.HasValue) ((RectTransform)rulesWindow.root.transform).anchoredPosition = position.Value;
        }

        private void SaveDraft()
        {
            if (!SameRun() || draft == null) return;
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

        private void OpenPlan() => OpenPlan(null);

        private void OpenPlan(GameGridInventory requested)
        {
            if (!started || !routingEnabled.Value || !ShopInventory.InStore || CustomUIManager.Instance == null || !store.Load()) return;
            if (requested != null && (!CanRouteGrid(requested) || ShopSelection.ContextChanged(requested)))
            { ShopNotice.Show(ShopSettings.Text("f08.changed")); return; }
            if (requested == null && planWindow != null && planWindow.IsAlive && SameRun()) { planWindow.BringToFront(); return; }
            ClosePlan();
            SetRun();
            selection = ShopSelection.Current();
            if (requested != null)
            {
                selection = selection.Where(item => ReferenceEquals(item.parentInventory, requested) && requested.childItems.Contains(item)).ToList();
                if (selection.Count == 0) selection = requested.childItems.Where(item => item is GameItemElement element &&
                    VisibleInventoryViews.Visible(element) && ShopInventory.Movable(item) &&
                    item.MaxNumRemove() >= item.unitCount).ToList();
            }
            if (selection.Count == 0) { ShopNotice.Show(ShopSettings.Text("f08.select")); return; }
            try
            {
                planRulesEnabled = rulesEnabled.Value;
                plan = RoutingPlan.Create(store.Snapshot(), selection, clues, planRulesEnabled);
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
                ItemGrid.Add(b, group.GroupBy(move => move.Item).Select(items => new ItemGrid.Entry(items.Key) { Selected = true,
                    Badge = items.Sum(move => (long)move.Quantity).ToString() }), new ItemGrid.Options { Columns = 22, CellSize = 22 });
            }
            foreach (RoutingPlan.Skip skipped in plan.Skipped)
            {
                b.AddLabel(skipped.Item.GetDisplayName(true) + " ×" + skipped.Quantity + " · " + ShopSettings.Text(skipped.Reason));
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
                planRevision != store.Revision || planRulesEnabled != rulesEnabled.Value || (previewEnabled.Value && previewMode.Value == 2)) return;
            executing = true;
            var result = new OperationResult("f08.open");
            int index = 0;
            string problem = null;
            OperationResult.ItemIdentity currentIdentity = null;
            try
            {
                var locations = ShopInventory.OwnedLocations(false);
                foreach (RoutingPlan.Skip skipped in plan.Skipped)
                    result.Skip(skipped.Item, skipped.Quantity, skipped.Reason,
                        locations.Find(location => location.Inventory == skipped.Item.parentInventory)?.Name);
                RoutingPlan fresh = RoutingPlan.Create(store.Snapshot(), selection, clues, planRulesEnabled);
                if (!plan.SameAs(fresh)) { ShopNotice.Show(ShopSettings.Text("f08.changed")); ClosePlan(); return; }
                for (; index < plan.Moves.Count; index++)
                {
                    RoutingPlan.Move entry = plan.Moves[index];
                    // The final merge can consume the source before the scope check.
                    ICollection<string> publicTypes = entry.Item is GameItemElement element ? clues.KnownTypes(element)?.ToArray() : null;
                    var currentLocations = ShopInventory.OwnedLocations(false);
                    if (!SameRun() || planRevision != store.Revision || planRulesEnabled != rulesEnabled.Value ||
                        !ShopInventory.TryRefresh(entry.Source, currentLocations, out ShopInventory.Location source) ||
                        !ShopInventory.TryRefresh(entry.Target, currentLocations, out ShopInventory.Location target) ||
                        !ReferenceEquals(entry.Item.parentInventory, source.Inventory) || entry.Item.unitCount != entry.Transfer.SourceBefore ||
                        ItemPinHelper.IsPinned(entry.Item) || !RoutingPlan.AllowsTransfer(store.Snapshot(), source, target, publicTypes, planRulesEnabled))
                    { problem = "f08.changed"; break; }
                    currentIdentity = OperationResult.Capture(entry.Item);
                    bool fromCounter = !ShopInventory.IsMainStockBranch(source);
                    bool Scope()
                    {
                        if (!SameRun() || planRevision != store.Revision || planRulesEnabled != rulesEnabled.Value) return false;
                        var live = ShopInventory.OwnedLocations(false);
                        if (!ShopInventory.TryRefresh(source, live, out var from) || !ShopInventory.TryRefresh(target, live, out var to) ||
                            !to.Ordinary || !ShopInventory.IsMainStockBranch(to) ||
                            !RoutingPlan.AllowsTransfer(store.Snapshot(), from, to, publicTypes, planRulesEnabled)) return false;
                        if (!fromCounter) return from.Ordinary && ShopInventory.IsMainStockBranch(from);
                        return from.Owner == null && from.Role == "counter" &&
                            (entry.Item.parentInventory != from.Inventory || ShopInventory.CanRouteFromCounter(entry.Item, from));
                    }
                    string path = source.Name + " → " + target.Name;
                    var outcome = WholeStackTransfer.Apply(entry.Transfer, "f08.open", path, source.Key(true), target.Key(true), Scope);
                    if (outcome != WholeStackTransfer.Result.Applied)
                    {
                        problem = outcome == WholeStackTransfer.Result.Pending || !WholeStackTransfer.Ready ? "f08.pending" :
                            outcome == WholeStackTransfer.Result.Unchanged || !Scope() ? "f08.changed" : "f08.journal_failed";
                        break;
                    }
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
            rulesWindow = null; editing = null; draft = null; drafts.Clear();
        }
        private void ClosePlan()
        {
            if (planWindow != null && planWindow.IsAlive) planWindow.Close();
            planWindow = null; plan = null; selection = null;
        }

        private void Reset()
        {
            CloseRules(); ClosePlan(); InventoryContextActions.Hide("f07.open");
            InventoryContextActions.Hide("f08.context"); InventoryContextActions.Hide("f08.grid"); store.Reset(); run = null; automaticError = null;
            foreach (HeaderEntry entry in headers.Values)
                DestroyHeader(entry);
            headers.Clear();
            RefreshHeadersSoon();
            AreaLabels.Invalidate();
        }
        public void Dispose()
        {
            if (!started) return;
            started = false;
            if (ReferenceEquals(current, this)) { current = null; AreaLabels.RoutingHeaderInset = null; }
            InventoryContextActions.Unregister("f07.open");
            InventoryContextActions.Unregister("f08.context");
            InventoryContextActions.Unregister("f08.grid");
            OccupiedSelectionGesture.Unregister();
            WholeStackTransfer.Release();
            settings.Changed -= SettingsChanged;
            VisibleInventoryViews.Changed -= RefreshHeadersSoon;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
        }

    }
}
