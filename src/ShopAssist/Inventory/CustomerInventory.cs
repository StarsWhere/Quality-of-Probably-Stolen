using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    internal sealed class CustomerInventory : IDisposable
    {
        internal event Action<CustomUIBuilder> ExtraActions;
        private const string WindowId = ModEntry.Id + ".customer_inventory";

        private sealed class Row
        {
            internal GameItem Item;
            internal ShopInventory.Location Location;
            internal string Name;
            internal int Quantity, Available;
            internal bool Reserved;
        }

        private readonly ShopSettings settings;
        private readonly CustomerIndicators indicators;
        private readonly Action<string> log;
        private readonly ConfigEntry<bool> enabled;
        private readonly Func<KeyCode> key;
        private readonly ViewOutlines marker = new ViewOutlines();
        private CustomUIWindow window;
        private StoreClientInstance customer;
        private string run;
        private int slot, page;
        private bool started, dirty;
        private GameItemElement marked;
        private StoreClientInstance markedCustomer;
        private float markerUntil, refreshAt;

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

        internal bool CanOpen() => started && enabled.Value && indicators.HasPublicDemand && ShopInventory.InStore;
        private bool SameContext() => CanOpen() && ReferenceEquals(customer, indicators.Customer) &&
            PlayerStore.Instance.saveSlotId == slot && PlayerStore.Instance.runID == run;

        public void Tick()
        {
            if (!started) return;
            if (marked != null && (Time.unscaledTime >= markerUntil || !VisibleInventoryViews.Visible(marked) ||
                !ReferenceEquals(markedCustomer, indicators.Customer)))
            {
                marker.Dispose();
                marked = null;
                markedCustomer = null;
            }
            if (window != null)
            {
                if (!window.IsAlive || !SameContext()) { Close(); return; }
                if (dirty && Time.unscaledTime >= refreshAt && !StoreUIManager.IsAnyTyping() && !Input.GetMouseButton(0)) Render();
            }
            if (!enabled.Value || key() == KeyCode.None || !Input.GetKeyDown(key()) || !ShopInput.Ready()) return;
            if (window != null) Close(); else Open();
        }

        private void SettingsChanged()
        {
            if (!enabled.Value) Close();
            else dirty = true;
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
            if (window != null && window.IsAlive && SameContext())
            {
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
                        indicators.Match(item) != PublicMatch.Match) continue;
                    bool reserved = reservedItems.Contains(item);
                    int available = 0;
                    if (!location.IsMachine && !reserved && ShopInventory.Movable(item))
                        available = Math.Max(0, Math.Min(item.unitCount, item.MaxNumRemove()));
                    rows.Add(new Row { Item = item, Location = location, Name = item.GetDisplayName(true),
                        Quantity = item.unitCount, Available = available, Reserved = reserved });
                }
            }
            return rows;
        }

        private void Render()
        {
            if (!SameContext() || CustomUIManager.Instance == null) { Close(); return; }
            Vector2? position = window != null && window.IsAlive ? ((RectTransform)window.root.transform).anchoredPosition : (Vector2?)null;
            List<Row> rows;
            try { rows = Query(); }
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
            int visibleRows = Math.Max(1, (Math.Min(perPage, rows.Count - page * perPage) + 4) / 5);
            var builder = CustomUIManager.Window(WindowId, ShopSettings.Text("f11.open"), CustomUIManager.LAYER_META)
                .SetSize(650, Math.Min(480, 190 + visibleRows * 96)).SetDraggable(true).SetCloseOnEscape(true);
            window = builder.GetWindow();
            CustomUIWindow thisView = window;
            builder.OnClose(() => { if (ReferenceEquals(window, thisView)) window = null; });
            builder.BeginRow().AddLabel(ShopSettings.Format("f11.count", rows.Count))
                .AddFlexibleSpace().AddButton("‹", () => { page = Math.Max(0, page - 1); Render(); }, "prev")
                .AddLabel((page + 1) + " / " + pages)
                .AddButton("›", () => { page = Math.Min(pages - 1, page + 1); Render(); }, "next").End();
            var shown = rows.Skip(page * perPage).Take(perPage).ToList();
            var cells = shown.Select(row => new ItemGrid.Entry(row.Item)
            {
                Caption = row.Name,
                Badge = row.Reserved ? ShopSettings.Text("f11.reserved") : row.Location.IsMachine ? ShopSettings.Text("f11.in_use") : "",
                Reason = row.Location.Name
            }).ToList();
            builder.BeginScrollFlexible(96);
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
            builder.Show();
            if (position.HasValue) ((RectTransform)window.root.transform).anchoredPosition = position.Value;
            dirty = false;
        }

        private bool CanMoveListed(Row row, GameGridInventory target, GridShape shape)
        {
            if (!SameContext() || !enabled.Value || row == null || row.Location.IsMachine || !ShopInventory.Movable(row.Item) ||
                row.Reserved || RequestBook.Reserved(row.Item) || indicators.Match(row.Item) != PublicMatch.Match ||
                !ReferenceEquals(row.Item.parentInventory, row.Location.Inventory)) return false;
            // The root counter location is the native negotiation table. A manual
            // drop reveals the buyer's quote there; it must not depend on F34's
            // separately enabled, already-observed quote cache.
            return ShopInventory.OwnedLocations(true).Any(l => l.Inventory == target && (l.Ordinary || l.Role == "front" ||
                (l.Owner == null && l.Role == "counter" && NegociationUIManager.Instance?.IsOpen == true &&
                    customer.GetClientBlueprint()?.IsClientBuying() == true))) &&
                ItemGridMove.CanMove(row.Item, target, shape);
        }

        private void Locate(Row row)
        {
            try
            {
                if (!SameContext() || row?.Item == null || !ShopInventory.TryRefreshCustomer(row.Location, out ShopInventory.Location location) ||
                    !ReferenceEquals(row.Item.parentInventory, location.Inventory) || !location.Inventory.childItems.Contains(row.Item) ||
                    !ShopInventory.Owned(row.Item) || indicators.Match(row.Item) != PublicMatch.Match)
                {
                    dirty = true;
                    ShopNotice.Show(ShopSettings.Text("f11.changed"));
                    return;
                }
                if (!RevealPath(location))
                {
                    dirty = true;
                    ShopNotice.Show(ShopSettings.Text("f11.open_first"));
                    return;
                }
                if (!SameContext() || !ShopInventory.TryRefreshCustomer(location, out location) ||
                    !ReferenceEquals(row.Item.parentInventory, location.Inventory) || !location.Inventory.childItems.Contains(row.Item) ||
                    !ShopInventory.Owned(row.Item) || indicators.Match(row.Item) != PublicMatch.Match)
                {
                    dirty = true;
                    ShopNotice.Show(ShopSettings.Text("f11.changed"));
                    return;
                }
                if (!(row.Item is GameItemElement item) || !VisibleInventoryViews.Visible(item))
                {
                    dirty = true;
                    ShopNotice.Show(ShopSettings.Text("f11.open_first"));
                    return;
                }
                PixelWindow target = item.handler.FindParentWindow();
                target?.ToFront();
                marker.Dispose();
                if (!marker.Show(item, ViewOutlineKind.Locate))
                {
                    ShopNotice.Show(ShopSettings.Text("f11.failed"));
                    return;
                }
                marked = item;
                markedCustomer = indicators.Customer;
                markerUntil = Time.unscaledTime + 4f;
                Close();
            }
            catch (Exception error)
            {
                log("F11 locate failed: " + error);
                ShopNotice.Show(ShopSettings.Text("f11.failed"));
                dirty = true;
            }
        }

        internal static bool RevealPath(ShopInventory.Location location)
        {
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

        private void Close()
        {
            if (window != null && window.IsAlive) window.Close();
            window = null;
            customer = null;
            run = null;
            dirty = false;
        }

        private void Reset()
        {
            Close();
            marker.Dispose();
            marked = null;
            markedCustomer = null;
        }

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
}
