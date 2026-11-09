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
        private const string WindowId = ModEntry.Id + ".customer_inventory";
        private static readonly FieldInfo WindowElements = typeof(CustomUIWindow).GetField("elements", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class Row
        {
            internal GameItem Item;
            internal ShopInventory.Location Location;
            internal GameInventory SourceInventory;
            internal GameItem SourceOwner;
            internal GameItem[] SourcePath;
            internal string Name, Source, SourceRole, Purchase;
            internal int SourcePage, Quantity, Available;
            internal bool Pinned, InUse, Draggable;
        }

        private readonly ShopSettings settings;
        private readonly CustomerIndicators indicators;
        private readonly Action<string> log;
        private readonly ConfigEntry<bool> enabled, showPurchase;
        private readonly Func<KeyCode> key;
        private CustomUIWindow window;
        private RectTransform scrollContent, resultsRoot;
        private string run;
        private int slot, page;
        private bool started, dirty, forceRender, resetScroll, collapsed;
        private string query = "";
        private float refreshAt, nextPoll, savedScroll = 1f;
        private int restoreScrollAt = -1;
        private float pendingScroll;
        private Vector2 expandedPosition, expandedSize;
        private List<Row> renderedRows;
        private CanvasGroup dragGroup;
        private float dragAlpha;
        private bool dragBlocksRaycasts;

        internal CustomerInventory(ShopSettings settings, CustomerIndicators indicators, Action<string> log)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.indicators = indicators ?? throw new ArgumentNullException(nameof(indicators));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            enabled = settings.Toggle("f11.enabled", "f11.tip");
            showPurchase = settings.Toggle("f11.purchase", "f11.purchase.tip");
            key = settings.Key("f11.key", "f11.key.tip");
            settings.Action("f11.open", Open, CanOpen);
        }

        public void Start()
        {
            if (started) return;
            started = true;
            WholeStackTransfer.Acquire();
            settings.Changed += SettingsChanged;
            indicators.Changed += Invalidate;
            VisibleInventoryViews.Changed += Invalidate;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        internal bool CanOpen() => started && enabled.Value && ShopInventory.InStore;
        internal string PurchaseLabel(GameItem item)
        {
            if (!showPurchase.Value) return null;
            int? price = ShopLedger.PurchasePrice(item);
            return price.HasValue ? ShopSettings.Format("f11.purchase_record", price.Value) : ShopSettings.Text("f11.purchase_unknown");
        }
        private bool SameContext() => CanOpen() && PlayerStore.Instance.saveSlotId == slot && PlayerStore.Instance.runID == run;

        public void Tick()
        {
            if (!started) return;
            if (window != null)
            {
                if (!window.IsAlive || !CanOpen() || PlayerStore.Instance.saveSlotId != slot || PlayerStore.Instance.runID != run)
                { Close(); return; }
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
        private void SceneChanged(Scene before, Scene after) => Reset();

        internal void Open()
        {
            if (!CanOpen() || CustomUIManager.Instance == null) return;
            if (window != null && window.IsAlive && PlayerStore.Instance.saveSlotId == slot && PlayerStore.Instance.runID == run)
            {
                if (collapsed) Expand();
                window.BringToFront();
                return;
            }
            Close();
            run = PlayerStore.Instance.runID;
            slot = PlayerStore.Instance.saveSlotId;
            page = 0;
            Render();
        }

        private List<Row> Query()
        {
            var rows = new List<Row>();
            var seen = new HashSet<GameItem>();
            List<ShopInventory.Location> locations = ShopInventory.OwnedLocations(false).Where(Searchable).ToList();
            WholeStackTransfer.SourceSafetySnapshot sourceSafety = null;
            foreach (ShopInventory.Location location in locations)
            {
                foreach (GameItem item in location.Inventory.childItems)
                {
                    if (item == null || !seen.Add(item) || !ShopInventory.Owned(item) ||
                        !ReferenceEquals(item.parentInventory, location.Inventory)) continue;
                    string name = item.GetDisplayName(true);
                    if (!string.IsNullOrWhiteSpace(query) && (name ?? "").IndexOf(query.Trim(), StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                    bool pinned = ItemPinHelper.IsPinned(item);
                    int available = 0;
                    if (CustomerIndicators.DemandLocation(location) && !pinned && ShopInventory.Movable(item))
                        available = Math.Max(0, Math.Min(item.unitCount, item.MaxNumRemove()));
                    bool eligible = !pinned && CustomerIndicators.DemandLocation(location) && available == item.unitCount && available > 0;
                    if (eligible && sourceSafety == null) sourceSafety = new WholeStackTransfer.SourceSafetySnapshot();
                    rows.Add(new Row { Item = item, Location = location, Name = name, Source = location.Name,
                        SourceInventory = location.Inventory, SourceOwner = location.Owner, SourceRole = location.Role,
                        SourcePage = location.Page, SourcePath = location.Path?.ToArray(),
                        Quantity = item.unitCount, Available = available, Pinned = pinned,
                        InUse = !CustomerIndicators.DemandLocation(location),
                        Draggable = eligible && sourceSafety.Allows(item),
                        Purchase = PurchaseLabel(item) });
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
                    a.Quantity != b.Quantity || a.Available != b.Available || a.Pinned != b.Pinned ||
                    a.InUse != b.InUse || a.Draggable != b.Draggable || a.Purchase != b.Purchase) return false;
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
                shell.OnClose(() =>
                {
                    if (!ReferenceEquals(window, thisView)) return;
                    ItemGrid.CancelDragWithin(thisView.root);
                    EndWindowDrag();
                    window = null;
                    Close();
                });
                shell.Show();
            }
            if (WindowElements?.GetValue(window) is not Dictionary<string, CustomUIElement> elements)
                throw new InvalidOperationException("Customer list elements are unavailable.");
            window.SetTitle(ShopSettings.Text("f11.open"));
            if (resultsRoot == null)
            {
                elements.Clear();
                CustomUIBuilder.ClearContainer(window.ContentRoot);
                var shell = new CustomUIBuilder(CustomUIManager.Instance, window, window.ContentRoot);
                shell.AddInput(ShopSettings.Text("f11.query"), query, ChangeQuery, "query", ChangeQuery);
                shell.BeginScrollFlexible(96); resultsRoot = shell.CurrentContainer;
                shell.End();
            }
            UiLayout.Clear(resultsRoot, window);
            var builder = new CustomUIBuilder(CustomUIManager.Instance, window, resultsRoot);
            builder.AddLabel(ShopSettings.Format("f11.count", rows.Count));
            UiLayout.Pager(builder, page, rows.Count, perPage, selected => { page = selected; resetScroll = true; Render(); });
            var shown = rows.Skip(page * perPage).Take(perPage).ToList();
            var cells = shown.Select(row => new ItemGrid.Entry(row.Item)
            {
                Caption = row.Name,
                SourceLabel = row.Source,
                StatusLabel = row.Pinned ? ShopSettings.Text("f11.pinned") : row.InUse
                    ? ShopSettings.Text("f11.in_use") : row.Available <= 0 ? ShopSettings.Text("f11.unavailable") :
                    row.Available < row.Quantity ? ShopSettings.Format("f11.partial", row.Available) :
                    !row.Draggable ? ShopSettings.Text("f11.manual_only") : ShopSettings.Format("f11.available", row.Available),
                PurchaseLabel = row.Purchase,
                Draggable = row.Draggable,
                TooltipHint = ShopSettings.Text(row.Draggable ? "f11.drag_tip" : "f11.locate_tip")
            }).ToList();
            scrollContent = resultsRoot;
            var options = new ItemGrid.Options
            {
                Cards = true, Columns = 5, CardWidth = 116, CardHeight = 96,
                RequireSourceShape = true,
                OnClick = entry => Locate(shown[cells.IndexOf(entry)]),
                OnDragStarted = BeginWindowDrag,
                OnDragEnded = EndWindowDrag,
                CanDropTarget = (entry, target, resident, pose) => PlanMove(shown[cells.IndexOf(entry)], target, resident, pose, out _) != null,
                OnDropTarget = (entry, target, resident, pose) =>
                {
                    var row = shown[cells.IndexOf(entry)];
                    var plan = PlanMove(row, target, resident, pose, out ShopInventory.Location destination);
                    if (plan == null)
                    { RejectMove(row, target, resident); return; }
                    string sourceKey = row.Location.Key(true), targetKey = destination.Key(true);
                    var outcome = WholeStackTransfer.Apply(plan, "f11.open", row.Source + " → " + destination.Name,
                        sourceKey, targetKey, () => MoveScope(row, destination));
                    if (outcome != WholeStackTransfer.Result.Applied)
                        ShopNotice.Show(ShopSettings.Text(outcome == WholeStackTransfer.Result.Unchanged ? "inv.changed" :
                            outcome == WholeStackTransfer.Result.Pending || !WholeStackTransfer.Ready ? "f08.pending" : "f08.journal_failed"));
                    dirty = true;
                },
                OnRejectTarget = (entry, target, resident, pose) =>
                    RejectMove(shown[cells.IndexOf(entry)], target, resident)
            };
            ItemGrid.Add(builder, cells, options);
            if (rows.Count == 0) builder.AddLabel(ShopSettings.Text("f11.no_results"));
            CustomUIBuilder.RebuildLayout(resultsRoot);
            UiLayout.RestoreScroll(scrollContent, scrollPosition);
            savedScroll = scrollPosition;
            pendingScroll = scrollPosition;
            restoreScrollAt = Time.frameCount + 2;
            renderedRows = rows;
            dirty = forceRender = resetScroll = false;
            nextPoll = Time.unscaledTime + 0.5f;
        }

        private void ChangeQuery(string value)
        {
            value ??= "";
            if (query == value) return;
            query = value; page = 0; resetScroll = true;
            if (window?.IsAlive == true && !collapsed) Render();
        }

        private bool CanMoveListedSource(Row row)
        {
            return SameContext() && row?.Item != null && row.Item.unitCount == row.Quantity &&
                RefreshLocation(row.Location, out ShopInventory.Location source) && SameSource(row, source) &&
                ReferenceEquals(row.Item.parentInventory, source.Inventory) && source.Inventory.childItems.Contains(row.Item) &&
                ShopInventory.Movable(row.Item) && row.Item.MaxNumRemove() >= row.Quantity && !row.Pinned;
        }

        private bool TargetLocation(GameInventory target, out ShopInventory.Location location)
        {
            location = null;
            if (!(target is PixelElement element) || element.handler == null || !element.handler.gameObject.activeInHierarchy ||
                !VisibleInventoryViews.Visible(element.handler.FindParentWindow())) return false;
            location = ShopInventory.OwnedLocations(true).FirstOrDefault(l => ReferenceEquals(l.Inventory, target) &&
                (l.IsMachine && target is GameSlotInventory || !l.IsMachine &&
                    (l.Ordinary || l.Owner == null && (l.Role == "front" || l.Role == "counter" || l.Role == "showcase"))));
            return location != null;
        }

        private WholeStackTransfer.Plan PlanMove(Row row, GameInventory target, GameItem resident, GridShape shape,
            out ShopInventory.Location location)
        {
            location = null;
            if (!CanMoveListedSource(row) || !WholeStackTransfer.Ready || !TargetLocation(target, out location)) return null;
            if (resident != null)
                return target is GameGridInventory && !location.IsMachine
                    ? WholeStackTransfer.ExactStack(row.Item, target, resident) : null;
            if (target is GameGridInventory grid && !location.IsMachine)
                return WholeStackTransfer.ExactEmpty(row.Item, grid, shape);
            return location.IsMachine && target is GameSlotInventory slot && slot.childItems.Count == 0
                ? WholeStackTransfer.EmptySlot(row.Item, slot) : null;
        }

        private bool MoveScope(Row row, ShopInventory.Location destination)
        {
            return SameContext() && RefreshLocation(row.Location, out ShopInventory.Location source) && SameSource(row, source) &&
                TargetLocation(destination?.Inventory, out ShopInventory.Location target) && SameLocation(destination, target);
        }

        private static bool SameLocation(ShopInventory.Location a, ShopInventory.Location b)
        {
            if (a == null || b == null || !ReferenceEquals(a.Inventory, b.Inventory) || !ReferenceEquals(a.Owner, b.Owner) ||
                a.Role != b.Role || a.Page != b.Page || a.Path == null || b.Path == null || a.Path.Length != b.Path.Length) return false;
            for (int i = 0; i < a.Path.Length; i++) if (!ReferenceEquals(a.Path[i], b.Path[i])) return false;
            return true;
        }

        private void RejectMove(Row row, GameInventory target, GameItem resident)
        {
            dirty = forceRender = true;
            string reason = "f11.move_manual";
            try
            {
                if (!CanMoveListedSource(row)) reason = "inv.changed";
                else if (!WholeStackTransfer.Ready) reason = "f08.pending";
                else if (target?.IsInsertLocked() == true) reason = "f11.target_locked";
                else if (target is GameSlotInventory && target.childItems.Count != 0) reason = "f11.target_occupied";
                else if (target is GameGridInventory && resident != null && ReferenceEquals(resident.parentInventory, target))
                {
                    if (ItemPinHelper.IsPinned(resident)) reason = "f11.target_pinned";
                    else if (!ReferenceEquals(resident, row.Item))
                    {
                        SlotMarker stack = resident.GetStackSlot(row.Item);
                        if (stack == null || stack.targetItem != resident || stack.inventory != target) reason = "f11.stack_mismatch";
                        else if (stack.numTransfer < row.Quantity) reason = "f11.stack_full";
                    }
                }
            }
            catch (Exception error) { log("F11 could not classify a rejected target: " + error); }
            ShopNotice.Show(ShopSettings.Text(reason));
        }

        private static bool SameSource(Row row, ShopInventory.Location location)
        {
            if (location == null || !ReferenceEquals(row.SourceInventory, location.Inventory) ||
                !ReferenceEquals(row.SourceOwner, location.Owner) || row.SourceRole != location.Role ||
                row.SourcePage != location.Page || row.SourcePath == null || location.Path == null ||
                row.SourcePath.Length != location.Path.Length) return false;
            for (int i = 0; i < row.SourcePath.Length; i++)
                if (!ReferenceEquals(row.SourcePath[i], location.Path[i])) return false;
            return true;
        }

        private void Locate(Row row)
        {
            try
            {
                if (!SameContext() || row?.Item == null || !RefreshLocation(row.Location, out ShopInventory.Location location) ||
                    !ReferenceEquals(row.Item.parentInventory, location.Inventory) || !location.Inventory.childItems.Contains(row.Item) ||
                    !ShopInventory.Owned(row.Item))
                {
                    dirty = true;
                    ShopNotice.Show(ShopSettings.Text("f11.changed"));
                    return;
                }
                if (!ItemLocator.TryLocate(row.Item, location, out string failure))
                { dirty = true; ShopNotice.Show(ShopSettings.Text(failure)); return; }
                if (!SameContext() || !RefreshLocation(location, out location) ||
                    !ReferenceEquals(row.Item.parentInventory, location.Inventory) || !location.Inventory.childItems.Contains(row.Item) ||
                    !ShopInventory.Owned(row.Item))
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

        private bool RefreshLocation(ShopInventory.Location location, out ShopInventory.Location refreshed)
        {
            if (!ShopInventory.TryRefresh(location, out refreshed)) return false;
            return Searchable(refreshed);
        }

        private static bool Searchable(ShopInventory.Location location) => ShopInventory.SearchableGoods(location);

        private void BeginWindowDrag()
        {
            EndWindowDrag();
            if (window?.IsAlive != true || window.root == null) return;
            dragGroup = window.root.GetComponent<CanvasGroup>() ?? window.root.AddComponent<CanvasGroup>();
            dragAlpha = dragGroup.alpha; dragBlocksRaycasts = dragGroup.blocksRaycasts;
            dragGroup.alpha = .2f;
            dragGroup.blocksRaycasts = false;
        }

        private void EndWindowDrag()
        {
            if (dragGroup == null) return;
            dragGroup.alpha = dragAlpha;
            dragGroup.blocksRaycasts = dragBlocksRaycasts;
            dragGroup = null;
        }

        private void Collapse(GameItemElement target)
        {
            if (target?.handler == null || window?.IsAlive != true || collapsed) return;
            savedScroll = scrollContent?.GetComponentInParent<ScrollRect>()?.verticalNormalizedPosition ?? 1f;
            expandedPosition = window.Rect.anchoredPosition;
            expandedSize = window.Rect.sizeDelta;
            collapsed = true;
            restoreScrollAt = -1;
            scrollContent = resultsRoot = null;
            if (WindowElements?.GetValue(window) is not Dictionary<string, CustomUIElement> elements)
                throw new InvalidOperationException("Customer list elements are unavailable.");
            elements.Clear();
            CustomUIBuilder.ClearContainer(window.ContentRoot);
            const float width = 240f, height = 94f;
            window.SetSize(width, height);
            Canvas targetCanvas = target.handler.GetComponentInParent<Canvas>();
            Camera camera = targetCanvas == null || targetCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : targetCanvas.worldCamera;
            var corners = new Vector3[4];
            target.handler.rectTransform.GetWorldCorners(corners);
            Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, target.handler.rectTransform.position);
            float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
            foreach (Vector3 corner in corners)
            {
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, corner);
                left = Mathf.Min(left, screen.x); right = Mathf.Max(right, screen.x);
                bottom = Mathf.Min(bottom, screen.y); top = Mathf.Max(top, screen.y);
            }
            Canvas canvas = window.root.GetComponentInParent<Canvas>();
            float scale = canvas != null && canvas.scaleFactor > 0 ? canvas.scaleFactor : 1f;
            float x = Mathf.Max(0, (Screen.width / scale - width) / 2f - 8f);
            float y = Mathf.Max(0, (Screen.height / scale - height) / 2f - 8f);
            Vector2 best = Vector2.zero;
            float bestOverlap = float.MaxValue, bestDistance = -1;
            foreach (float px in new[] { -x, x }) foreach (float py in new[] { -y, y })
            {
                float cx = Screen.width / 2f + px * scale, cy = Screen.height / 2f + py * scale;
                float overlapX = Mathf.Max(0, Mathf.Min(right, cx + width * scale / 2f) - Mathf.Max(left, cx - width * scale / 2f));
                float overlapY = Mathf.Max(0, Mathf.Min(top, cy + height * scale / 2f) - Mathf.Max(bottom, cy - height * scale / 2f));
                float overlap = overlapX * overlapY;
                float distance = (cx - point.x) * (cx - point.x) + (cy - point.y) * (cy - point.y);
                if (overlap < bestOverlap || overlap == bestOverlap && distance > bestDistance)
                { best = new Vector2(px, py); bestOverlap = overlap; bestDistance = distance; }
            }
            window.Rect.anchoredPosition = best;
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
            if (window?.root != null) ItemGrid.CancelDragWithin(window.root);
            EndWindowDrag();
            if (window != null && window.IsAlive) window.Close();
            window = null;
            run = null;
            dirty = forceRender = resetScroll = collapsed = false;
            query = "";
            scrollContent = resultsRoot = null; renderedRows = null; savedScroll = 1f; restoreScrollAt = -1;
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
            WholeStackTransfer.Release();
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
