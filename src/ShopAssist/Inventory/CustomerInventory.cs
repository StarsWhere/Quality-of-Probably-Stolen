using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed class CustomerInventory : IDisposable
    {
        internal event Action<CustomUIBuilder> ExtraActions;
        internal Func<GameItem, string> QuoteLabel;
        private const string WindowId = ModEntry.Id + ".customer_inventory";
        private static readonly FieldInfo WindowElements = typeof(CustomUIWindow).GetField("elements", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class Row
        {
            internal GameItem Item;
            internal ShopInventory.Location Location;
            internal GameInventory SourceInventory;
            internal GameItem SourceOwner;
            internal GameItem[] SourcePath;
            internal string Name, Source, SourceRole, Quote;
            internal int SourcePage, Quantity, Available;
            internal bool Reserved, InUse;
        }

        private readonly ShopSettings settings;
        private readonly CustomerIndicators indicators;
        private readonly Action<string> log;
        private readonly ConfigEntry<bool> enabled;
        private readonly Func<KeyCode> key;
        private CustomUIWindow window;
        private RectTransform scrollContent;
        private StoreClientInstance customer;
        private string run;
        private int slot, page;
        private bool started, dirty, forceRender, resetScroll, collapsed;
        private float refreshAt, nextPoll, savedScroll = 1f;
        private int restoreScrollAt = -1;
        private float pendingScroll;
        private Vector2 expandedPosition, expandedSize;
        private List<Row> renderedRows;

        internal CustomerInventory(ShopSettings settings, CustomerIndicators indicators, Action<string> log)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.indicators = indicators ?? throw new ArgumentNullException(nameof(indicators));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            enabled = settings.Toggle("f11.enabled", "f11.tip");
            key = settings.Key("f11.key", "f11.key.tip");
            settings.Action("f11.open", Open, CanOpen);
        }

        public void Start()
        {
            if (started) return;
            started = true;
            settings.Changed += SettingsChanged;
            indicators.Changed += Invalidate;
            VisibleInventoryViews.Changed += Invalidate;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        internal bool CanOpen() => started && enabled.Value && indicators.HasDemand && ShopInventory.InStore;
        private bool SameContext() => CanOpen() && ReferenceEquals(customer, indicators.Customer) &&
            PlayerStore.Instance.saveSlotId == slot && PlayerStore.Instance.runID == run;

        public void Tick()
        {
            if (!started) return;
            if (window != null)
            {
                if (!window.IsAlive || !CanOpen() || PlayerStore.Instance.saveSlotId != slot || PlayerStore.Instance.runID != run)
                { Close(); return; }
                if (!ReferenceEquals(customer, indicators.Customer))
                {
                    customer = indicators.Customer;
                    page = 0;
                    dirty = forceRender = resetScroll = true;
                }
                bool due = dirty ? Time.unscaledTime >= refreshAt : Time.unscaledTime >= nextPoll;
                bool busy = StoreUIManager.IsAnyTyping() || UIDragHandler.isDraggingAnyUI || ItemGrid.DragActive ||
                    Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2) ||
                    Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || Input.GetMouseButtonUp(2);
                if (busy) restoreScrollAt = -1;
                else if (!collapsed && restoreScrollAt >= 0 && Time.frameCount >= restoreScrollAt)
                { UiLayout.RestoreScroll(scrollContent, pendingScroll); restoreScrollAt = -1; }
                if (due && !busy) Refresh();
            }
            if (!enabled.Value || key() == KeyCode.None || !Input.GetKeyDown(key()) || !ShopInput.Ready()) return;
            if (window != null) Close(); else Open();
        }

        private void SettingsChanged()
        {
            if (!enabled.Value) Close();
            else { dirty = true; forceRender = true; }
        }

        private void Invalidate()
        {
            if (!dirty) refreshAt = Time.unscaledTime + 0.2f;
            dirty = true;
        }
        internal void QuotesChanged() => Invalidate();
        private void SceneChanged(Scene before, Scene after) => Reset();

        internal void Open()
        {
            if (!CanOpen() || CustomUIManager.Instance == null) return;
            if (window != null && window.IsAlive && SameContext())
            {
                if (collapsed) Expand();
                window.BringToFront();
                return;
            }
            Close();
            customer = indicators.Customer;
            run = PlayerStore.Instance.runID;
            slot = PlayerStore.Instance.saveSlotId;
            page = 0;
            Render();
        }

        private List<Row> Query()
        {
            var rows = new List<Row>();
            var seen = new HashSet<GameItem>();
            List<ShopInventory.Location> locations = ShopInventory.CustomerLocations();
            HashSet<GameItem> reservedItems = RequestBook.ReservedItems(locations);
            foreach (ShopInventory.Location location in locations)
            {
                foreach (GameItem item in location.Inventory.childItems)
                {
                    if (item == null || !seen.Add(item) || !ShopInventory.Owned(item) ||
                        !ReferenceEquals(item.parentInventory, location.Inventory) ||
                        !indicators.Matches(item)) continue;
                    bool reserved = reservedItems.Contains(item);
                    int available = 0;
                    if (CustomerIndicators.DemandLocation(location) && !reserved && ShopInventory.Movable(item))
                        available = Math.Max(0, Math.Min(item.unitCount, item.MaxNumRemove()));
                    rows.Add(new Row { Item = item, Location = location, Name = item.GetDisplayName(true), Source = location.Name,
                        SourceInventory = location.Inventory, SourceOwner = location.Owner, SourceRole = location.Role,
                        SourcePage = location.Page, SourcePath = location.Path?.ToArray(),
                        Quantity = item.unitCount, Available = available, Reserved = reserved,
                        InUse = !CustomerIndicators.DemandLocation(location),
                        Quote = QuoteLabel?.Invoke(item) ?? ShopSettings.Text("f11.unquoted") });
                }
            }
            return rows;
        }

        private static bool SameRows(List<Row> left, List<Row> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                Row a = left[i], b = right[i];
                if (!ReferenceEquals(a.Item, b.Item) || !ReferenceEquals(a.SourceInventory, b.SourceInventory) ||
                    !ReferenceEquals(a.SourceOwner, b.SourceOwner) || a.SourceRole != b.SourceRole ||
                    a.SourcePage != b.SourcePage || a.Source != b.Source || a.Name != b.Name ||
                    a.Quantity != b.Quantity || a.Available != b.Available || a.Reserved != b.Reserved ||
                    a.InUse != b.InUse || a.Quote != b.Quote) return false;
                GameItem[] first = a.SourcePath, second = b.SourcePath;
                if (first == null || second == null || first.Length != second.Length) return false;
                for (int n = 0; n < first.Length; n++) if (!ReferenceEquals(first[n], second[n])) return false;
            }
            return true;
        }

        private void Refresh()
        {
            List<Row> rows;
            try { rows = Query(); }
            catch (Exception error)
            {
                log("F11 query failed: " + error);
                Close(); ShopNotice.Show(ShopSettings.Text("f11.failed")); return;
            }
            nextPoll = Time.unscaledTime + 0.5f;
            dirty = false;
            if (!collapsed && (forceRender || !SameRows(renderedRows, rows))) Render(rows);
        }

        private void Render(List<Row> rows = null)
        {
            if (!SameContext() || CustomUIManager.Instance == null) { Close(); return; }
            try { rows = rows ?? Query(); }
            catch (Exception error)
            {
                log("F11 query failed: " + error);
                Close();
                ShopNotice.Show(ShopSettings.Text("f11.failed"));
                return;
            }
            const int perPage = 24;
            int pages = Math.Max(1, (rows.Count + perPage - 1) / perPage);
            page = Math.Max(0, Math.Min(page, pages - 1));
            float scrollPosition = resetScroll ? 1f : scrollContent?.GetComponentInParent<ScrollRect>()?.verticalNormalizedPosition ?? savedScroll;
            if (window == null || !window.IsAlive)
            {
                Vector2 size = UiLayout.Fit(650, 480);
                var shell = CustomUIManager.Window(WindowId, ShopSettings.Text("f11.open"), CustomUIManager.LAYER_META)
                    .SetSize(size.x, size.y).SetDraggable(true).SetCloseOnEscape(true);
                window = shell.GetWindow();
                CustomUIWindow thisView = window;
                shell.OnClose(() => { if (ReferenceEquals(window, thisView)) window = null; });
                shell.Show();
            }
            if (WindowElements?.GetValue(window) is not Dictionary<string, CustomUIElement> elements)
                throw new InvalidOperationException("Customer list elements are unavailable.");
            elements.Clear();
            CustomUIBuilder.ClearContainer(window.ContentRoot);
            var builder = new CustomUIBuilder(CustomUIManager.Instance, window, window.ContentRoot);
            builder.AddLabel(ShopSettings.Format("f11.count", rows.Count));
            UiLayout.Pager(builder, page, rows.Count, perPage, selected => { page = selected; resetScroll = true; Render(); });
            var shown = rows.Skip(page * perPage).Take(perPage).ToList();
            var cells = shown.Select(row => new ItemGrid.Entry(row.Item)
            {
                Caption = row.Name,
                SourceLabel = row.Source,
                StatusLabel = row.Reserved ? ShopSettings.Text("f11.reserved") : row.InUse
                    ? ShopSettings.Text("f11.in_use") : row.Available > 0 ? ShopSettings.Format("f11.available", row.Available) : ShopSettings.Text("f11.unavailable"),
                QuoteLabel = row.Quote
            }).ToList();
            builder.BeginScrollFlexible(96);
            scrollContent = builder.CurrentContainer;
            ItemGrid.Add(builder, cells, new ItemGrid.Options
            {
                Cards = true, Columns = 5, CardWidth = 116, CardHeight = 96,
                OnClick = entry => Locate(shown[cells.IndexOf(entry)]),
                CanDrop = (entry, target, pose) => CanMoveListed(shown[cells.IndexOf(entry)], target, pose),
                OnDrop = (entry, target, pose) =>
                {
                    var row = shown[cells.IndexOf(entry)];
                    if (!CanMoveListed(row, target, pose)) { dirty = true; return; }
                    if (!ItemGridMove.TryMove(entry.Item, target, pose, out bool restored))
                        ShopNotice.Show(ShopSettings.Text(restored ? "inv.changed" : "f12.inspect"));
                    dirty = true;
                }
            });
            if (rows.Count == 0) builder.AddLabel(ShopSettings.Text("f11.empty"));
            builder.End();
            ExtraActions?.Invoke(builder);
            CustomUIBuilder.RebuildLayout(window.ContentRoot);
            UiLayout.RestoreScroll(scrollContent, scrollPosition);
            savedScroll = scrollPosition;
            pendingScroll = scrollPosition;
            restoreScrollAt = Time.frameCount + 2;
            renderedRows = rows;
            dirty = forceRender = resetScroll = false;
            nextPoll = Time.unscaledTime + 0.5f;
        }

        private bool CanMoveListed(Row row, GameGridInventory target, GridShape shape)
        {
            if (!SameContext() || !enabled.Value || row == null || !CustomerIndicators.DemandLocation(row.Location) || !ShopInventory.Movable(row.Item) ||
                row.Reserved || RequestBook.Reserved(row.Item) || !indicators.Matches(row.Item) ||
                !ReferenceEquals(row.Item.parentInventory, row.Location.Inventory)) return false;
            // The root counter location is the native negotiation table. A manual
            // drop reveals the buyer's quote there; it must not depend on F34's
            // separately enabled, already-observed quote cache.
            return ShopInventory.OwnedLocations(true).Any(l => l.Inventory == target && (l.Ordinary || l.Role == "front" ||
                (l.Owner == null && l.Role == "counter" && NegociationUIManager.Instance?.IsOpen == true &&
                    indicators.HasDemand))) &&
                ItemGridMove.CanMove(row.Item, target, shape);
        }

        private void Locate(Row row)
        {
            try
            {
                if (!SameContext() || row?.Item == null || !ShopInventory.TryRefreshCustomer(row.Location, out ShopInventory.Location location) ||
                    !ReferenceEquals(row.Item.parentInventory, location.Inventory) || !location.Inventory.childItems.Contains(row.Item) ||
                !ShopInventory.Owned(row.Item) || !indicators.Matches(row.Item))
                {
                    dirty = true;
                    ShopNotice.Show(ShopSettings.Text("f11.changed"));
                    return;
                }
                if (!ItemLocator.TryLocate(row.Item, location, out string failure))
                { dirty = true; ShopNotice.Show(ShopSettings.Text(failure)); return; }
                if (!SameContext() || !ShopInventory.TryRefreshCustomer(location, out location) ||
                    !ReferenceEquals(row.Item.parentInventory, location.Inventory) || !location.Inventory.childItems.Contains(row.Item) ||
                    !ShopInventory.Owned(row.Item) || !indicators.Matches(row.Item))
                {
                    ItemLocator.Clear();
                    dirty = true;
                    ShopNotice.Show(ShopSettings.Text("f11.changed"));
                    return;
                }
                Collapse(row.Item as GameItemElement);
            }
            catch (Exception error)
            {
                log("F11 locate failed: " + error);
                ShopNotice.Show(ShopSettings.Text("f11.failed"));
                dirty = true;
            }
        }

        private void Collapse(GameItemElement target)
        {
            if (target?.handler == null || window?.IsAlive != true || collapsed) return;
            savedScroll = scrollContent?.GetComponentInParent<ScrollRect>()?.verticalNormalizedPosition ?? 1f;
            expandedPosition = window.Rect.anchoredPosition;
            expandedSize = window.Rect.sizeDelta;
            collapsed = true;
            restoreScrollAt = -1;
            scrollContent = null;
            if (WindowElements?.GetValue(window) is not Dictionary<string, CustomUIElement> elements)
                throw new InvalidOperationException("Customer list elements are unavailable.");
            elements.Clear();
            CustomUIBuilder.ClearContainer(window.ContentRoot);
            const float width = 240f, height = 94f;
            window.SetSize(width, height);
            Canvas targetCanvas = target.handler.GetComponentInParent<Canvas>();
            Camera camera = targetCanvas == null || targetCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : targetCanvas.worldCamera;
            Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, target.handler.rectTransform.position);
            Canvas canvas = window.root.GetComponentInParent<Canvas>();
            float scale = canvas != null && canvas.scaleFactor > 0 ? canvas.scaleFactor : 1f;
            float x = Mathf.Max(0, (Screen.width / scale - width) / 2f - 8f);
            float y = Mathf.Max(0, (Screen.height / scale - height) / 2f - 8f);
            window.Rect.anchoredPosition = new Vector2(point.x < Screen.width / 2f ? x : -x, point.y < Screen.height / 2f ? y : -y);
            var builder = new CustomUIBuilder(CustomUIManager.Instance, window, window.ContentRoot);
            builder.AddButton(ShopSettings.Text("f11.return"), Expand, "return");
            CustomUIBuilder.RebuildLayout(window.ContentRoot);
        }

        private void Expand()
        {
            if (!collapsed || window?.IsAlive != true) return;
            collapsed = false;
            window.SetSize(expandedSize.x, expandedSize.y);
            window.Rect.anchoredPosition = expandedPosition;
            Render();
        }

        private void Close()
        {
            if (window != null && window.IsAlive) window.Close();
            window = null;
            customer = null;
            run = null;
            dirty = forceRender = resetScroll = collapsed = false;
            scrollContent = null; renderedRows = null; savedScroll = 1f; restoreScrollAt = -1;
        }

        private void Reset() => Close();

        public void Dispose()
        {
            if (!started) return;
            started = false;
            settings.Changed -= SettingsChanged;
            indicators.Changed -= Invalidate;
            VisibleInventoryViews.Changed -= Invalidate;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
        }
    }

    // A shared locator never moves an item and opens only ordinary safe containers.
    internal static class ItemLocator
    {
        private static readonly ViewOutlines marker = new ViewOutlines("ShopAssist_LocateOutline");
        private static GameItemElement marked;
        private static string run;
        private static int slot;
        private static float until;

        internal static bool TryLocate(GameItem item) => TryLocate(item, out _);

        internal static bool TryLocate(GameItem item, out string reasonKey)
        {
            reasonKey = "f11.changed";
            if (item == null || !ShopInventory.InStore || !ShopInventory.Owned(item)) return false;
            ShopInventory.Location found = null;
            foreach (ShopInventory.Location candidate in ShopInventory.CustomerLocations())
            {
                if (!ReferenceEquals(candidate.Inventory, item.parentInventory) || !candidate.Inventory.childItems.Contains(item)) continue;
                if (found != null) return false;
                found = candidate;
            }
            return TryLocate(item, found, out reasonKey);
        }

        internal static bool TryLocate(GameItem item, ShopInventory.Location location) => TryLocate(item, location, out _);

        internal static bool TryLocate(GameItem item, ShopInventory.Location location, out string reasonKey)
        {
            reasonKey = "f11.changed";
            try
            {
                if (!ShopInventory.InStore || item == null || !ShopInventory.Owned(item) ||
                    !ShopInventory.TryRefreshCustomer(location, out location) ||
                    !ReferenceEquals(item.parentInventory, location.Inventory) || !location.Inventory.childItems.Contains(item)) return false;
                string expectedRun = PlayerStore.Instance.runID;
                int expectedSlot = PlayerStore.Instance.saveSlotId;
                reasonKey = "f11.open_first";
                if (!RevealPath(location)) return false;
                reasonKey = "f11.changed";
                if (!ShopInventory.InStore || PlayerStore.Instance.runID != expectedRun || PlayerStore.Instance.saveSlotId != expectedSlot ||
                    !ShopInventory.TryRefreshCustomer(location, out location) || !ShopInventory.Owned(item) ||
                    !ReferenceEquals(item.parentInventory, location.Inventory) || !location.Inventory.childItems.Contains(item)) return false;
                reasonKey = "f11.open_first";
                if (item is not GameItemElement element || !VisibleInventoryViews.Visible(element)) return false;
                element.handler.FindParentWindow()?.ToFront();
                Clear();
                reasonKey = "f11.failed";
                if (!marker.Show(element, ViewOutlineKind.Locate)) { marker.Dispose(); return false; }
                marked = element;
                run = PlayerStore.Instance.runID;
                slot = PlayerStore.Instance.saveSlotId;
                until = Time.unscaledTime + 4f;
                reasonKey = null;
                return true;
            }
            catch (Exception error)
            {
                Debug.LogError("[ShopAssist] Item locate failed: " + error);
                reasonKey = "f11.failed";
                Clear();
                return false;
            }
        }

        internal static void Tick()
        {
            if (marked != null && (Time.unscaledTime >= until || !ShopInventory.InStore ||
                PlayerStore.Instance.runID != run || PlayerStore.Instance.saveSlotId != slot || !VisibleInventoryViews.Visible(marked))) Clear();
        }

        internal static void Clear()
        {
            marker.Dispose();
            marked = null;
            run = null;
        }

        internal static bool RevealPath(ShopInventory.Location location)
        {
            if (location == null || location.Path == null) return false;
            for (int i = 0; i < location.Path.Length; i++)
            {
                GameItem host = location.Path[i];
                ShopInventory.Location containing = FindContainingLocation(host, location.Path, i);
                if (containing == null || containing.IsMachine || !ShowLocation(containing)) return false;
                if (host.contentWindow == null) return false;
                if (!host.contentWindow.IsVisible())
                {
                    if (ItemMouseDoubleClickHandler.current == null || !ShopInventory.SafeContainerForPeek(host)) return false;
                    ItemMouseDoubleClickHandler.current.OpenContentAction(host, Input.mousePosition);
                    if (!host.contentWindow.IsVisible()) return false;
                }
            }
            return ShopInventory.TryRefreshCustomer(location, out ShopInventory.Location refreshed) && ShowLocation(refreshed);
        }

        private static ShopInventory.Location FindContainingLocation(GameItem item, GameItem[] path, int length)
        {
            ShopInventory.Location match = null;
            foreach (ShopInventory.Location candidate in ShopInventory.CustomerLocations())
            {
                if (candidate.Inventory != item.parentInventory || candidate.Path.Length != length) continue;
                bool same = true;
                for (int i = 0; i < length; i++) if (!ReferenceEquals(candidate.Path[i], path[i])) { same = false; break; }
                if (!same) continue;
                if (match != null) return null;
                match = candidate;
            }
            return match;
        }

        private static bool ShowLocation(ShopInventory.Location location)
        {
            if (location.Page >= 0)
            {
                if (location.Owner?.contentWindow?.childElement is not MachineryPanelRow row ||
                    row.GetPanel(location.Role)?.content is not MachineryTabs tabs ||
                    location.Page >= tabs.count || !location.Owner.contentWindow.IsVisible()) return false;
                if (tabs.selected != location.Page) tabs.Select(location.Page, notify: true);
            }
            if (!(location.Inventory is PixelElement element) || element.handler == null) return false;
            PixelWindow window = element.handler.FindParentWindow();
            if (window == null || !window.IsVisible() || !window.handler.gameObject.activeInHierarchy) return false;
            window.ToFront();
            return true;
        }

    }
}
