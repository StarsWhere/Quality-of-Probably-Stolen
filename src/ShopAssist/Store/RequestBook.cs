using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ShopAssist
{
    internal sealed class RequestBook : IDisposable
    {
        internal readonly struct DoorDeadline
        {
            internal DoorDeadline(string title, int remainingDays) { Title = title; RemainingDays = remainingDays; }
            internal string Title { get; }
            internal int RemainingDays { get; }
        }

        private const string DataKey = "requests";
        private const string RecoveryKey = "requestsUnreadableBackup";
        private const string WindowId = ModEntry.Id + ".requests";
        private static RequestBook active;
        private readonly ConfigEntry<bool> tracking, reminders;
        private readonly ConfigEntry<int> days;
        private readonly ShopSettings settings;
        private readonly Action<string> log;
        private readonly HashSet<GameItem> selected = new HashSet<GameItem>();
        private readonly List<GameObject> buttons = new List<GameObject>();
        private Save data;
        private string run;
        private string unreadableRaw;
        private int slot, page;
        private StoreRequest showing;
        private CustomUIWindow window;
        private CustomUIWindow recoveryWindow;
        private int selectedReservationId;
        private bool viewDirty, migrationPreview;
        private float refreshAt;

        [Serializable]
        private sealed class Save
        {
            [JsonRequired] public int version = 2;
            [JsonRequired] public List<Order> orders = new List<Order>();
        }

        [Serializable]
        private sealed class Reservation
        {
            [JsonRequired] public int id;
        }

        [Serializable]
        private sealed class Order
        {
            [JsonProperty(Required = Required.AllowNull)] public string id = null;
            [JsonProperty(Required = Required.AllowNull)] public string source = null;
            [JsonRequired] public string item = null;
            [JsonProperty(Required = Required.AllowNull)] public string requester = null;
            [JsonProperty(Required = Required.AllowNull)] public string destination = null;
            [JsonRequired] public int quantity = 0, expires = 0;
            [JsonRequired] public List<Reservation> reserved = new List<Reservation>();
            public bool Matches(StoreRequest r, int today) =>
                r != null && r.id == id && r.sourceEventId == source && r.requiredItem == item &&
                r.requesterName == requester && r.requiredItemCount == quantity && today + r.duration == expires;
        }

        private sealed class ReservedMove
        {
            internal GameItem Item;
            internal ShopInventory.Location Source;
            internal GridShape Shape;
            internal int Count;
        }

        internal RequestBook(ShopSettings settings, Action<string> log)
        {
            this.settings = settings;
            this.log = log;
            tracking = settings.Toggle("f27.enabled", "f27.tip");
            reminders = settings.Toggle("f21.enabled", "f21.tip");
            days = settings.Integer("f21.days", "f21.days.tip", 1, 0, 3);
            settings.Action("f27.open", () => Draw(null), () => tracking.Value && ShopInventory.InStore);
        }

        internal void Start()
        {
            active = this;
            AttentionBook.Register("f27", PendingReservations);
            settings.Changed += SettingsChanged;
            VisibleInventoryViews.Changed += InvalidateView;
            VisibleInventoryViews.ItemChanged += ItemChanged;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Close;
        }

        private void InvalidateView()
        {
            if (viewDirty) return;
            viewDirty = true;
            refreshAt = Time.unscaledTime + .2f;
        }

        private void ItemChanged(GameItemElement _) => InvalidateView();

        internal void Tick()
        {
            if (window?.IsAlive != true) { window = null; viewDirty = false; return; }
            if (!tracking.Value || !ShopInventory.InStore || run != PlayerStore.Instance.runID || slot != PlayerStore.Instance.saveSlotId)
            { Close(); return; }
            if (!viewDirty || migrationPreview || Time.unscaledTime < refreshAt || StoreUIManager.IsAnyTyping() ||
                KeyBindHelper.IsCapturing || UIDragHandler.isDraggingAnyUI || ItemGrid.DragActive ||
                Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2) ||
                Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || Input.GetMouseButtonUp(2)) return;
            viewDirty = false;
            Draw(showing);
        }

        private bool Load()
        {
            if (!ShopInventory.InStore) return false;
            var store = PlayerStore.Instance;
            if (store.ModData() == null || store.storeRequests == null) return false;
            if (run == store.runID && slot == store.saveSlotId) return data != null;
            Reset();
            slot = store.saveSlotId;
            run = store.runID;
            string raw = null;
            bool read = false;
            try
            {
                raw = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                read = true;
                if (raw != null && raw.Length > 65536) throw new FormatException("Tracked orders exceed the save limit.");
                data = raw == null ? new Save() : ShopData.Decode<Save>(raw, 65536);
                if (data == null || data.version != 2 || data.orders == null || data.orders.Count > 128 ||
                    data.orders.Any(o => o == null || string.IsNullOrEmpty(o.item) || o.quantity < 1 ||
                    o.expires < 1 || o.reserved == null || o.reserved.Count > 256 ||
                    o.reserved.Any(r => r == null || r.id <= 0) || o.reserved.Select(r => r.id).Distinct().Count() != o.reserved.Count))
                    throw new FormatException("Invalid tracked orders");
                unreadableRaw = null;
                return true;
            }
            catch (Exception ex)
            {
                data = null;
                unreadableRaw = read ? raw : null;
                log("F27 kept unreadable orders: " + ex.Message);
                return false;
            }
        }

        private List<StoreRequest> Current => PlayerStore.Instance.storeRequests;
        private static int Day => StoreStation.GetDayCounter();
        private Order Tracked(StoreRequest r) => data.orders.Find(o => o.Matches(r, Day));
        private bool Unique(StoreRequest r) => Current.Contains(r) && Current.Count(x =>
            x.id == r.id && x.sourceEventId == r.sourceEventId && x.requiredItem == r.requiredItem &&
            x.requiredItemCount == r.requiredItemCount && x.duration == r.duration && x.requesterName == r.requesterName) == 1;
        private void SaveData()
        {
            string raw = ShopData.Encode(data);
            if (raw.Length > 65536) throw new InvalidOperationException("Tracked orders exceed the save limit.");
            ModHelper.SetModData(ModEntry.Id, DataKey, raw);
            if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != raw) throw new InvalidOperationException("Tracked orders were not saved.");
            VisibleInventoryViews.NotifyChanged();
            AttentionBook.Changed();
        }

        private void SaveFailed(string operation, Exception error)
        {
            string expectedRun = run;
            int expectedSlot = slot;
            StoreRequest request = showing;
            bool reopen = window?.IsAlive == true;
            log("F27 could not confirm " + operation + ": " + error);
            // A write may have succeeded even if its verification read failed.
            data = null; run = null;
            VisibleInventoryViews.NotifyChanged();
            AttentionBook.Changed();
            ShopNotice.Show(ShopSettings.Text("f27.save_failed"));
            if (!ShopInventory.InStore || PlayerStore.Instance.runID != expectedRun || PlayerStore.Instance.saveSlotId != expectedSlot)
            { Close(); return; }
            if (!Load()) { ShopNotice.Show(ShopSettings.Text("f27.unreadable")); return; }
            if (reopen && tracking.Value) Draw(request != null && Unique(request) ? request : null);
        }

        private static bool NeedsInspection(int itemId) =>
            AttentionBook.HasInspection("f27.prepare", itemId) || AttentionBook.HasInspection("f27.migrate", itemId);

        internal static bool TryDoorDeadlines(out List<DoorDeadline> result)
        {
            result = new List<DoorDeadline>();
            var book = active;
            if (book == null || !book.Load()) return false;
            foreach (StoreRequest request in book.Current)
                if (request != null && book.Unique(request) && book.Tracked(request) != null)
                    result.Add(new DoorDeadline(request.title, Math.Max(0, request.duration - 1)));
            return true;
        }

        internal static bool DoorRemindersEnabled => active != null && active.reminders.Value && ShopInventory.InStore;
        internal static int DoorReminderDays => active == null ? 0 : active.days.Value;

        internal static bool Reserved(GameItem item)
        {
            var book = active;
            if (book == null || !book.tracking.Value || !ShopInventory.InStore || item == null || !ShopInventory.Owned(item)) return false;
            // If the reservation book is unreadable, do not let automated actions
            // assume an owned item is unreserved.
            if (!book.Load()) return true;
            if (item.uniqueId <= 0) return false;
            return book.data.orders.Any(o => o.reserved.Any(r => r.id == item.uniqueId) && book.Current.Any(r => o.Matches(r, Day)));
        }

        // A list view can resolve all reservations against one inventory snapshot
        // instead of walking the complete shop for every item.
        internal static HashSet<GameItem> ReservedItems(IEnumerable<ShopInventory.Location> locations)
        {
            var result = new HashSet<GameItem>();
            var book = active;
            if (book == null || !book.tracking.Value || locations == null || !ShopInventory.InStore) return result;
            if (!book.Load())
            {
                foreach (var location in locations)
                    if (location?.Inventory != null)
                        foreach (GameItem item in location.Inventory.childItems)
                            if (ShopInventory.Owned(item)) result.Add(item);
                return result;
            }
            var ids = new HashSet<int>();
            int today = Day;
            foreach (Order order in book.data.orders)
            {
                if (!book.Current.Any(request => order.Matches(request, today))) continue;
                foreach (Reservation reservation in order.reserved) ids.Add(reservation.id);
            }
            foreach (ShopInventory.Location location in locations)
            {
                if (location == null || location.Inventory == null) continue;
                foreach (GameItem item in location.Inventory.childItems)
                    if (item != null && item.uniqueId > 0 && ids.Contains(item.uniqueId)) result.Add(item);
            }
            return result;
        }

        private void SettingsChanged()
        {
            if (!tracking.Value) Close();
            foreach (var go in buttons) if (go != null) go.SetActive(tracking.Value);
            AttentionBook.Changed();
        }

        private IEnumerable<AttentionItem> PendingReservations()
        {
            if (!tracking.Value || !ShopInventory.InStore) yield break;
            if (!Load())
            {
                string invalid = unreadableRaw, expectedRun = run;
                int expectedSlot = slot;
                yield return new AttentionItem { Key = "f27:unreadable", Feature = "f27.open", Title = ShopSettings.Text("f27.open"),
                    ReasonKey = "f27.unreadable", NeedsInspection = true, Retry = RetryLoad,
                    ResolveLabelKey = "f27.reset", Resolve = invalid == null ? null : () => ConfirmReset(invalid, expectedRun, expectedSlot) };
                yield break;
            }
        }

        private void ConfirmReset(string invalid, string expectedRun, int expectedSlot)
        {
            if (!ShopInventory.InStore || CustomUIManager.Instance == null || run != expectedRun || slot != expectedSlot ||
                PlayerStore.Instance.runID != expectedRun || PlayerStore.Instance.saveSlotId != expectedSlot ||
                data != null || unreadableRaw != invalid) return;
            var builder = UiLayout.Rebuild(ref recoveryWindow, ModEntry.Id + ".requests_reset",
                ShopSettings.Text("f27.reset"), 480, 220);
            var own = recoveryWindow;
            builder.OnClose(() => { if (ReferenceEquals(recoveryWindow, own)) recoveryWindow = null; });
            builder.AddLabel(ShopSettings.Text("f27.reset_warning"))
                .BeginRow().AddButton(ShopSettings.Text("f27.reset_confirm"), () => ResetUnreadable(invalid, expectedRun, expectedSlot), "confirm")
                .AddButton(ShopSettings.Text("f34.back"), () => own.Close(), "cancel").End().Show();
        }

        private void ResetUnreadable(string invalid, string expectedRun, int expectedSlot)
        {
            if (!ShopInventory.InStore || run != expectedRun || slot != expectedSlot ||
                PlayerStore.Instance.runID != expectedRun || PlayerStore.Instance.saveSlotId != expectedSlot ||
                data != null || unreadableRaw != invalid) return;
            bool attemptedWrite = false;
            try
            {
                if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != invalid)
                { Reset(); AttentionBook.Changed(); ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                ModHelper.SetModData(ModEntry.Id, RecoveryKey, invalid);
                if (ModHelper.GetModData(ModEntry.Id, RecoveryKey, null) != invalid)
                    throw new InvalidOperationException("Reservation backup was not retained.");
                if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != invalid)
                { Reset(); AttentionBook.Changed(); ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                var replacement = new Save();
                string raw = ShopData.Encode(replacement);
                attemptedWrite = true;
                ModHelper.SetModData(ModEntry.Id, DataKey, raw);
                if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != raw) throw new InvalidOperationException("Reservation reset was not retained.");
                data = replacement; unreadableRaw = null;
                recoveryWindow?.Close(); recoveryWindow = null;
                VisibleInventoryViews.NotifyChanged(); AttentionBook.Changed();
                ShopNotice.Show(ShopSettings.Text("f27.reset_done"));
            }
            catch (Exception error)
            {
                log("F27 could not confirm the reservation reset: " + error);
                if (attemptedWrite)
                {
                    try
                    {
                        ModHelper.SetModData(ModEntry.Id, DataKey, invalid);
                        if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != invalid) throw new InvalidOperationException("Original reservation data was not retained.");
                    }
                    catch (Exception restoreError) { unreadableRaw = null; log("F27 could not confirm restoring reservation data: " + restoreError); }
                }
                AttentionBook.Changed();
                ShopNotice.Show(ShopSettings.Text("f27.reset_failed"));
            }
        }

        private void RetryLoad()
        {
            Reset();
            if (!Load()) ShopNotice.Show(ShopSettings.Text("f27.unreadable"));
            else ShopNotice.Show(ShopSettings.Text("f27.recovered"));
            AttentionBook.Changed();
        }

        private void RemoveReservation(Order order, Reservation reservation)
        {
            if (!Load() || !data.orders.Contains(order)) return;
            int index = order.reserved.IndexOf(reservation);
            if (index < 0) return;
            if (NeedsInspection(reservation.id))
            {
                ShopNotice.Show(ShopSettings.Text("f27.inspect_first"));
                AttentionBook.Open();
                return;
            }
            order.reserved.RemoveAt(index);
            try { SaveData(); }
            catch (Exception error)
            {
                SaveFailed("reservation release", error); return;
            }
            selectedReservationId = 0;
            if (showing != null && Tracked(showing) == order) Draw(showing);
        }

        [HarmonyPatch(typeof(StoreRequestElement), nameof(StoreRequestElement.Init))]
        private static class MarketRow
        {
            private static void Postfix(StoreRequestElement __instance)
            {
                if (active != null) active.AddButton(__instance);
            }
        }

        private void AddButton(StoreRequestElement row)
        {
            if (!tracking.Value || row == null || row.acceptButton == null) return;
            var original = row.acceptButton;
            if (original.gameObject == row.gameObject || original.transform.parent == null) return;
            var parent = original.transform.parent;
            string name = ModEntry.Id + ".track";
            if (parent.Find(name) != null) return;
            var button = Object.Instantiate(original, parent, false);
            button.name = name;
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => { if (tracking.Value && row != null) Draw(row.storeRequest); });
            button.interactable = true;
            button.gameObject.SetActive(true);
            var label = button.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (label != null) label.SetText(ShopSettings.Text("f27.trackButton"));
            if (parent.GetComponent<HorizontalLayoutGroup>() == null)
            {
                var rect = (RectTransform)button.transform;
                rect.anchoredPosition -= new Vector2(rect.rect.width + 4, 0);
            }
            buttons.RemoveAll(x => x == null);
            buttons.Add(button.gameObject);
        }

        private void Draw(StoreRequest request)
        {
            if (!tracking.Value || CustomUIManager.Instance == null) return;
            if (!Load()) { ShopNotice.Show(ShopSettings.Text("f27.unreadable")); return; }
            migrationPreview = false;
            viewDirty = false;
            var b = UiLayout.Rebuild(ref window, WindowId, ShopSettings.Text("f27.open"), 600, 470);
            b.OnClose(() => window = null);
            if (!ReferenceEquals(showing, request)) { selected.Clear(); selectedReservationId = 0; page = 0; }
            showing = request;
            if (request == null)
            {
                var visible = new HashSet<StoreRequest>(Object.FindObjectsOfType<StoreRequestElement>()
                    .Where(v => v.gameObject.activeInHierarchy && v.storeRequest != null).Select(v => v.storeRequest));
                b.BeginScrollFlexible(150).BeginGrid(2, 270, 105, 8);
                int count = 0;
                foreach (var r in Current)
                    if (visible.Contains(r) || Tracked(r) != null)
                    {
                        b.BeginPanel(270, 105, padding: 6);
                        b.AddLabel(r.title);
                        b.AddLabel(ShopSettings.Format("f27.order_summary", r.requiredItemCount, Math.Max(0, r.duration - 1)));
                        b.AddButton(ShopSettings.Text("f27.prepare"), () => Draw(r));
                        b.End();
                        count++;
                    }
                b.End();
                if (count == 0) b.AddLabel(ShopSettings.Text("f27.market"));
                b.End().Show();
                return;
            }
            if (!Unique(request)) { b.AddLabel(ShopSettings.Text("f27.expired")).Show(); return; }
            b.AddLabel(request.title);
            var order = Tracked(request);
            if (order == null)
            {
                b.AddButton(ShopSettings.Text("f27.track"), () =>
                {
                    if (!Load() || !Unique(request) || Tracked(request) != null) return;
                    var before = new List<Order>(data.orders);
                    data.orders.RemoveAll(o => !Current.Any(r => o.Matches(r, Day)));
                    if (data.orders.Count >= 128)
                    { data.orders = before; ShopNotice.Show(ShopSettings.Text("f27.limit")); return; }
                    data.orders.Add(new Order { id = request.id, source = request.sourceEventId, item = request.requiredItem,
                        quantity = request.requiredItemCount, requester = request.requesterName, expires = Day + request.duration });
                    try { SaveData(); }
                    catch (Exception error)
                    { SaveFailed("request tracking", error); return; }
                    Draw(request);
                }, "track").Show();
                return;
            }
            var locations = ShopInventory.OwnedLocations(true);
            var all = ShopInventory.OwnedLocations(false);
            var reservedItems = ReservedItems(locations);
            var dest = string.IsNullOrEmpty(order.destination) ? null : all.Find(l => l.Ordinary && l.Key() == order.destination);
            IList<ShopInventory.Location> Destinations() => tracking.Value && ShopInventory.InStore &&
                run == PlayerStore.Instance.runID && slot == PlayerStore.Instance.saveSlotId && ReferenceEquals(showing, request) &&
                data != null && data.orders.Contains(order)
                ? ShopInventory.OwnedLocations(true).FindAll(l => l.Ordinary && l.Owner != null && !l.Inventory.IsInsertLocked())
                : new List<ShopInventory.Location>();
            VisualChoices.Location(b, ShopSettings.Text("visual.target"), dest, Destinations, location =>
            {
                if (!Load() || !Unique(request) || Tracked(request) != order) return;
                if (!ShopInventory.TryRefresh(location, ShopInventory.OwnedLocations(true), out var live) ||
                    !live.Ordinary || live.Owner == null || live.Inventory.IsInsertLocked())
                { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                order.destination = live.Key(true);
                try { SaveData(); }
                catch (Exception error)
                { SaveFailed("destination", error); return; }
                Draw(request);
            }, "destination");
            var reserved = all.Where(l => l.Inventory != null).SelectMany(l => l.Inventory.childItems.Select(it => (l, it)))
                .Where(x => ShopInventory.Owned(x.it) && order.reserved.Any(r => r.id == x.it.uniqueId)).ToList();
            int ready = reserved.Count(x => request.CanThisItemFillRequest(x.it));
            int atTarget = dest == null ? 0 : reserved.Count(x => x.l.Inventory == dest.Inventory &&
                request.CanThisItemFillRequest(x.it));
            b.AddLabel(ShopSettings.Format("f27.count_location", ready, request.requiredItemCount, atTarget));
            var candidates = locations.Where(l => l.Ordinary).SelectMany(l => l.Inventory.childItems.Select(it => (l, it)))
                .Where(x => request.CanThisItemFillRequest(x.it) && ShopInventory.Movable(x.it) && !reservedItems.Contains(x.it)).ToList();
            page = Math.Max(0, Math.Min(page, (candidates.Count - 1) / 20));
            b.BeginScrollFlexible(110);
            ItemGrid.Add(b, reserved.Select(pair => new ItemGrid.Entry(pair.it) {
                Badge = ShopSettings.Text(dest != null && pair.l.Inventory == dest.Inventory ? "f27.ready" : "f27.awaiting_move"),
                Reason = pair.l.Name, Selected = selectedReservationId == pair.it.uniqueId
            }), new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 135, CardHeight = 95,
                OnClick = entry => { selectedReservationId = selectedReservationId == entry.Item.uniqueId ? 0 : entry.Item.uniqueId; Draw(request); } });
            Reservation selectedReservation = order.reserved.Find(r => r.id == selectedReservationId);
            if (selectedReservation != null && reserved.Any(pair => pair.it.uniqueId == selectedReservation.id))
                b.AddButton(ShopSettings.Text("f27.release_item"), () => RemoveReservation(order, selectedReservation), "release_selected");
            foreach (Reservation pending in order.reserved.Where(r => !reserved.Any(pair => pair.it.uniqueId == r.id)).ToArray())
            {
                b.BeginRow().AddLabel(ShopSettings.Format("f27.missing_item", pending.id));
                if (b.Last.text != null) b.Last.text.richText = false;
                b.AddButton(ShopSettings.Text("f27.release_item"), () => RemoveReservation(order, pending)).End();
            }
            ItemGrid.Add(b, candidates.Skip(page * 20).Take(20).Select(pair => new ItemGrid.Entry(pair.it)
            { Selected = selected.Contains(pair.it), Reason = pair.l.Name }),
                new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 135, CardHeight = 95,
                    OnClick = entry => { if (!selected.Add(entry.Item)) selected.Remove(entry.Item); entry.Selected = selected.Contains(entry.Item); } });
            if (candidates.Count == 0 && ready < request.requiredItemCount) b.AddLabel(ShopSettings.Text("f27.open_sources"));
            b.End();
            UiLayout.Pager(b, page, candidates.Count, 20, next => { page = next; Draw(request); });
            b.BeginRow().AddButton(ShopSettings.Text("f27.prepare"), () => Prepare(request), "prepare")
                .AddButton(ShopSettings.Text("f27.migrate"), () => PreviewMigration(request), "migrate")
                .AddButton(ShopSettings.Text("f27.release"), () =>
                {
                    if (!Load() || !Unique(request)) return;
                    int index = data.orders.IndexOf(order);
                    if (index < 0) return;
                    if (order.reserved.Any(reservation => NeedsInspection(reservation.id)))
                    { ShopNotice.Show(ShopSettings.Text("f27.inspect_first")); AttentionBook.Open(); return; }
                    data.orders.RemoveAt(index);
                    try { SaveData(); }
                    catch (Exception error)
                    { SaveFailed("request release", error); return; }
                    selected.Clear(); Draw(null);
                }).End().Show();
        }

        private bool SameOrder(StoreRequest request, Order order, string expectedRun, int expectedSlot, string destination) =>
            tracking.Value && ShopInventory.InStore && PlayerStore.Instance.runID == expectedRun &&
            PlayerStore.Instance.saveSlotId == expectedSlot && Load() && Unique(request) && Tracked(request) == order &&
            order.destination == destination;

        private void Prepare(StoreRequest request)
        {
            if (!tracking.Value || !Load() || !Unique(request)) { Close(); return; }
            var order = Tracked(request);
            if (order == null) return;
            string expectedRun = PlayerStore.Instance.runID, destination = order.destination;
            int expectedSlot = PlayerStore.Instance.saveSlotId;
            var locations = ShopInventory.OwnedLocations(true);
            var target = locations.Find(l => l.Ordinary && l.Owner != null && !string.IsNullOrEmpty(destination) && l.Key() == destination);
            if (target == null) { ShopNotice.Show(ShopSettings.Text("f27.choose")); return; }
            int ready = ShopInventory.OwnedLocations(false).Where(l => l.Inventory != null).SelectMany(l => l.Inventory.childItems)
                .Count(it => ShopInventory.Owned(it) && order.reserved.Any(r => r.id == it.uniqueId) && request.CanThisItemFillRequest(it));
            var result = new OperationResult("f27.prepare");
            bool stopped = false;
            foreach (var item in selected.ToArray())
            {
                var from = locations.Find(l => l.Inventory == item.parentInventory);
                if (stopped || ready >= request.requiredItemCount)
                { result.Skip(item, item.unitCount, stopped ? "operation.not_attempted" : "f27.enough", from?.Name); continue; }
                if (!SameOrder(request, order, expectedRun, expectedSlot, destination))
                { result.Skip(item, item.unitCount, "inv.changed", from?.Name); stopped = true; continue; }
                var current = ShopInventory.OwnedLocations(true);
                if (!ShopInventory.TryRefresh(from, current, out var source) || !ShopInventory.TryRefresh(target, current, out var liveTarget) ||
                    !source.Ordinary || !liveTarget.Ordinary || item.parentInventory != source.Inventory ||
                    !request.CanThisItemFillRequest(item) || Reserved(item) || !ShopInventory.Movable(item))
                { result.Skip(item, item.unitCount, "inv.changed", from?.Name); continue; }
                if (NeedsInspection(item.uniqueId))
                { result.Skip(item, item.unitCount, "f27.inspect_first", from?.Name); stopped = true; continue; }
                if (order.reserved.Count >= 256)
                { result.Skip(item, item.unitCount, "f27.limit", from?.Name); continue; }
                int id = item.GetUniqueID();
                int count = item.unitCount;
                var shape = item.modifiedShape?.Clone();
                var reservation = new Reservation { id = id };
                order.reserved.Add(reservation);
                try { SaveData(); }
                catch (Exception error)
                {
                    SaveFailed("reservation before moving goods", error);
                    result.Skip(item, item.unitCount, "f27.save_failed", from?.Name);
                    stopped = true;
                    continue;
                }
                ready++;
                if (!SameOrder(request, order, expectedRun, expectedSlot, destination))
                { result.Skip(item, count, "inv.changed", from?.Name); stopped = true; continue; }
                current = ShopInventory.OwnedLocations(true);
                if (!ShopInventory.TryRefresh(from, current, out source) || !ShopInventory.TryRefresh(target, current, out liveTarget) ||
                    !source.Ordinary || !liveTarget.Ordinary || item.parentInventory != source.Inventory || item.unitCount != count ||
                    !InventorySortPlan.SameShape(item.modifiedShape, shape) || !request.CanThisItemFillRequest(item) ||
                    !ShopInventory.Movable(item) || !order.reserved.Contains(reservation) || reservation.id != item.uniqueId ||
                    !SameOrder(request, order, expectedRun, expectedSlot, destination))
                { result.Skip(item, count, "inv.changed", from?.Name); continue; }
                if (!ShopInventory.TryMoveIntact(item, source, liveTarget, out string reason))
                {
                    result.Skip(item, item.unitCount, reason, from?.Name, reason == "inv.inspect");
                    if (reason == "inv.inspect") stopped = true;
                    continue;
                }
                result.Complete(item, count, source.Name + " → " + liveTarget.Name);
            }
            if (!ShopInventory.InStore || PlayerStore.Instance.runID != expectedRun || PlayerStore.Instance.saveSlotId != expectedSlot) return;
            selected.Clear(); Draw(request);
            if (result.Items.Count != 0) ShopNotice.Report(result);
            else ShopNotice.Show(ShopSettings.Text("f27.select"));
        }

        private void PreviewMigration(StoreRequest request)
        {
            if (!tracking.Value || !Load() || !Unique(request) || CustomUIManager.Instance == null) return;
            Order order = Tracked(request);
            if (order == null) return;
            string expectedRun = PlayerStore.Instance.runID;
            int expectedSlot = PlayerStore.Instance.saveSlotId;
            var locations = ShopInventory.OwnedLocations(false);
            var target = string.IsNullOrEmpty(order.destination) ? null :
                locations.Find(l => l.Ordinary && l.Owner != null && l.Key() == order.destination);
            if (target == null) { ShopNotice.Show(ShopSettings.Text("f27.choose")); return; }
            var moves = new List<ReservedMove>();
            foreach (Reservation reservation in order.reserved)
            {
                var from = locations.Find(l => l.Inventory.childItems.Any(item => item.uniqueId == reservation.id));
                GameItem item = from?.Inventory.childItems.Find(i => i.uniqueId == reservation.id);
                if (item == null || item.parentInventory == target.Inventory || item.modifiedShape == null) continue;
                moves.Add(new ReservedMove { Item = item, Source = from, Shape = item.modifiedShape.Clone(), Count = item.unitCount });
            }
            migrationPreview = true;
            var b = UiLayout.Rebuild(ref window, WindowId, ShopSettings.Text("f27.migrate"), 600, 470);
            b.OnClose(() => window = null);
            b.AddLabel(target.Name);
            if (b.Last.text != null) b.Last.text.richText = false;
            b.BeginScrollFlexible(120);
            ItemGrid.Add(b, moves.Select(move => new ItemGrid.Entry(move.Item)
                { Selected = true, Selectable = false, Reason = move.Source.Name, Badge = move.Count.ToString() }),
                new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 135, CardHeight = 95 });
            if (moves.Count == 0) b.AddLabel(ShopSettings.Text("f27.migrate_none"));
            b.End().BeginRow().AddButton(ShopSettings.Text("f27.back"), () => Draw(request));
            b.AddButton(ShopSettings.Text("f27.migrate_apply"), () => MigrateReserved(request, order, target, moves, expectedRun, expectedSlot), "migrate_apply");
            b.Last.SetInteractable(moves.Count > 0);
            b.End().Show();
        }

        private void MigrateReserved(StoreRequest request, Order order, ShopInventory.Location expectedTarget, List<ReservedMove> moves,
            string expectedRun, int expectedSlot)
        {
            string destination = expectedTarget.Key();
            if (!SameOrder(request, order, expectedRun, expectedSlot, destination))
            { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            var result = new OperationResult("f27.migrate");
            bool stopped = false;
            foreach (ReservedMove move in moves)
            {
                string path = move.Source.Name + " → " + expectedTarget.Name;
                if (stopped) { result.Skip(move.Item, move.Count, "operation.not_attempted", path); continue; }
                if (!SameOrder(request, order, expectedRun, expectedSlot, destination))
                { result.Skip(move.Item, move.Count, "inv.changed", path); stopped = true; continue; }
                var locations = ShopInventory.OwnedLocations(false);
                if (!ShopInventory.TryRefresh(expectedTarget, locations, out var target) || !target.Ordinary)
                { result.Skip(move.Item, move.Count, "inv.changed", path); stopped = true; continue; }
                if (!order.reserved.Any(r => r.id == move.Item.uniqueId) ||
                    !ShopInventory.TryRefresh(move.Source, locations, out var source) || move.Item.parentInventory != source.Inventory ||
                    move.Item.unitCount != move.Count || !InventorySortPlan.SameShape(move.Item.modifiedShape, move.Shape))
                { result.Skip(move.Item, move.Count, "inv.changed", path); continue; }
                if (NeedsInspection(move.Item.uniqueId))
                { result.Skip(move.Item, move.Count, "f27.inspect_first", path); stopped = true; continue; }
                if (!ShopInventory.TryMoveIntact(move.Item, source, target, out string reason))
                {
                    result.Skip(move.Item, move.Count, reason, path, reason == "inv.inspect");
                    if (reason == "inv.inspect") stopped = true;
                    continue;
                }
                result.Complete(move.Item, move.Count, path);
            }
            if (!ShopInventory.InStore || PlayerStore.Instance.runID != expectedRun || PlayerStore.Instance.saveSlotId != expectedSlot) return;
            VisibleInventoryViews.NotifyChanged();
            AttentionBook.Changed();
            Draw(request);
            ShopNotice.Report(result);
        }

        private void Close()
        {
            if (window != null && window.IsAlive) window.Close();
            if (recoveryWindow != null && recoveryWindow.IsAlive) recoveryWindow.Close();
            recoveryWindow = null;
            window = null; selected.Clear(); selectedReservationId = 0; showing = null;
            migrationPreview = viewDirty = false;
        }
        private void Reset() { Close(); run = null; data = null; unreadableRaw = null; }
        public void Dispose()
        {
            active = null;
            AttentionBook.Unregister("f27");
            settings.Changed -= SettingsChanged;
            VisibleInventoryViews.Changed -= InvalidateView;
            VisibleInventoryViews.ItemChanged -= ItemChanged;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Close;
            foreach (var button in buttons) if (button != null) Object.Destroy(button);
            buttons.Clear(); Reset();
        }
    }
}
