using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    internal static class AttentionBook
    {
        [Serializable] private sealed class SavedIssue
        {
            [JsonRequired] public string key, feature, title, location, reason;
            [JsonRequired] public int item;
        }
        [Serializable] private sealed class SavedIssues
        {
            [JsonRequired] public int version = 1;
            [JsonRequired] public string run;
            [JsonRequired] public int slot;
            [JsonRequired] public List<SavedIssue> issues = new List<SavedIssue>();
        }

        private const string DataKey = "attention";
        private static readonly Dictionary<string, Func<IEnumerable<AttentionItem>>> providers = new Dictionary<string, Func<IEnumerable<AttentionItem>>>();
        private static readonly Dictionary<string, string> providerErrors = new Dictionary<string, string>();
        private static readonly Dictionary<string, SavedIssues> unsaved = new Dictionary<string, SavedIssues>(StringComparer.Ordinal);
        private static SavedIssues data;
        private static string run, signature;
        private static int slot;
        private static bool loaded, invalid, dirty, saveFailed;
        private static CustomUIWindow window;
        private static Action<string> log;
        private static float refreshAt;
        private static readonly HashSet<string> announced = new HashSet<string>(StringComparer.Ordinal);

        internal static void Start(ShopSettings settings, Action<string> warning)
        {
            log = warning;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }
        internal static void Register(string owner, Func<IEnumerable<AttentionItem>> provider)
        {
            if (string.IsNullOrEmpty(owner) || provider == null || providers.ContainsKey(owner))
                throw new ArgumentException("Invalid or duplicate attention provider: " + owner);
            providers.Add(owner, provider); Changed();
        }
        internal static void Unregister(string owner) { providers.Remove(owner); providerErrors.Remove(owner); Changed(); }
        internal static void Changed() { dirty = true; refreshAt = 0; }

        private static bool Ensure()
        {
            if (!PlayerStore.HasInstance || string.IsNullOrEmpty(PlayerStore.Instance.runID) ||
                PlayerStore.Instance.saveSlotId < 0 || PlayerStore.Instance.ModData() == null) return false;
            var store = PlayerStore.Instance;
            if (loaded && run == store.runID && slot == store.saveSlotId) return !invalid;
            Reset(); loaded = true; run = store.runID; slot = store.saveSlotId;
            try
            {
                string raw = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                data = raw == null ? new SavedIssues { run = run, slot = slot } : ShopData.Decode<SavedIssues>(raw, 1048576);
                if (data == null || data.version != 1 || data.run != run || data.slot != slot || data.issues == null ||
                    data.issues.Count > 4096 || data.issues.Any(issue => issue == null || string.IsNullOrEmpty(issue.key) ||
                        issue.key.Length > 256 || string.IsNullOrEmpty(issue.feature) || issue.feature.Length > 128 ||
                        issue.title == null || issue.title.Length > 4096 || issue.location == null || issue.location.Length > 8192 ||
                        string.IsNullOrEmpty(issue.reason) || issue.reason.Length > 256 || issue.item < 0) ||
                    data.issues.Select(issue => issue.key).Distinct(StringComparer.Ordinal).Count() != data.issues.Count)
                    throw new FormatException("Invalid attention data or save identity");
                if (unsaved.TryGetValue(run + ":" + slot, out SavedIssues pending)) { data = pending; saveFailed = true; }
                return true;
            }
            catch (Exception error)
            {
                invalid = true; data = null; log?.Invoke("Retained unreadable attention data: " + error);
                return false;
            }
        }

        private static bool Save(SavedIssues next)
        {
            try
            {
                string raw = ShopData.Encode(next);
                ModHelper.SetModData(ModEntry.Id, DataKey, raw);
                if (ModHelper.GetModData(ModEntry.Id, DataKey, null) != raw) throw new InvalidOperationException("Attention update was not retained.");
                data = next; saveFailed = false; unsaved.Remove(next.run + ":" + next.slot); Changed(); return true;
            }
            catch (Exception error)
            {
                log?.Invoke("Could not save attention state: " + error);
                data = next; saveFailed = true; unsaved[next.run + ":" + next.slot] = next; Changed();
                ShopNotice.Show(ShopSettings.Text("attention.save_failed")); return false;
            }
        }
        private static string IssueKey(string action, OperationResult.ItemResult item) => action + ":" +
            (item.ItemId > 0 ? item.ItemId.ToString() : item.Name + ":" + item.Location);

        internal static void Report(OperationResult result, bool alreadyNotified = false)
        {
            if (result == null || !Ensure()) return;
            var next = new SavedIssues { run = run, slot = slot, issues = new List<SavedIssue>(data.issues) };
            var notified = new List<string>();
            bool changed = false;
            foreach (var item in result.Items)
            {
                string key = IssueKey(result.ActionKey, item);
                if (key.Length > 256) key = key.Substring(0, 256);
                if (item.Status == OperationItemStatus.Completed)
                { if (item.ItemId > 0) changed |= next.issues.RemoveAll(issue => issue.key == key) != 0; continue; }
                if (item.Status != OperationItemStatus.NeedsInspection) continue;
                next.issues.RemoveAll(issue => issue.key == key);
                next.issues.Add(new SavedIssue { key = key, feature = result.ActionKey, title = item.Name,
                    location = item.Location, reason = item.Reason ?? "inv.inspect", item = Math.Max(0, item.ItemId) });
                notified.Add(key);
                changed = true;
            }
            if (changed && Save(next) && alreadyNotified) foreach (string key in notified) announced.Add(key);
        }

        internal static bool HasInspection(string actionKey, int itemId) => PlayerStore.HasInstance && !string.IsNullOrEmpty(PlayerStore.Instance.runID) &&
            (!Ensure() || saveFailed || itemId > 0 && data.issues.Any(issue => issue.feature == actionKey && issue.item == itemId));

        private static void Remove(string key)
        {
            if (!Ensure()) return;
            var next = new SavedIssues { run = run, slot = slot, issues = data.issues.Where(issue => issue.key != key).ToList() };
            if (next.issues.Count != data.issues.Count) Save(next);
        }

        internal static List<AttentionItem> Snapshot()
        {
            var result = new Dictionary<string, AttentionItem>(StringComparer.Ordinal);
            if (!ShopInventory.InStore) return result.Values.ToList();
            if (Ensure())
                foreach (SavedIssue issue in data.issues)
                {
                    string key = issue.key;
                    result[key] = new AttentionItem { Key = key, Feature = issue.feature, ItemId = issue.item, Title = issue.title,
                        Location = issue.location, ReasonKey = issue.reason, NeedsInspection = true, Resolve = () => Remove(key) };
                }
            else result["attention.invalid"] = new AttentionItem { Key = "attention.invalid", Title = ShopSettings.Text("attention.open"),
                ReasonKey = "attention.unreadable", NeedsInspection = true };
            if (saveFailed && data != null) result["attention.unsaved"] = new AttentionItem { Key = "attention.unsaved", Title = ShopSettings.Text("attention.open"),
                ReasonKey = "attention.save_failed", NeedsInspection = true, Retry = () => { if (Ensure()) Save(data); } };
            foreach (var provider in providers.ToArray())
            {
                try
                {
                    foreach (AttentionItem item in provider.Value() ?? Enumerable.Empty<AttentionItem>())
                        if (item != null && !string.IsNullOrEmpty(item.Key))
                        {
                            string key = provider.Key + ":" + item.Key;
                            result[key] = new AttentionItem { Key = key, Feature = item.Feature, Title = item.Title, Location = item.Location,
                                ReasonKey = item.ReasonKey, ItemId = item.ItemId, NeedsInspection = item.NeedsInspection,
                                ActionLabelKey = item.ActionLabelKey, ResolveLabelKey = item.ResolveLabelKey, Retry = item.Retry, Resolve = item.Resolve };
                        }
                    providerErrors.Remove(provider.Key);
                }
                catch (Exception error)
                {
                    string message = error.ToString();
                    if (!providerErrors.TryGetValue(provider.Key, out string previous) || previous != message)
                        log?.Invoke("Attention provider " + provider.Key + " failed: " + message);
                    providerErrors[provider.Key] = message;
                    result[provider.Key + ":unavailable"] = new AttentionItem { Key = provider.Key + ":unavailable", Title = provider.Key,
                        ReasonKey = "attention.provider_failed", NeedsInspection = true };
                }
            }
            return result.Values.OrderByDescending(item => item.NeedsInspection).ThenBy(item => item.Title, StringComparer.CurrentCulture).ToList();
        }
        internal static List<AttentionItem> ForItem(int id) => id <= 0 ? new List<AttentionItem>() : Snapshot().Where(item => item.ItemId == id).ToList();
        internal static int Count => Snapshot().Count;
        internal static void Open()
        {
            if (!ShopInventory.InStore || CustomUIManager.Instance == null) return;
            Ensure(); Render(Snapshot());
        }
        private static string Signature(IEnumerable<AttentionItem> items, Dictionary<int, PixelWindow> locations) => string.Join("|", items.Select(item =>
            item.Key + ":" + item.Title + ":" + item.Location + ":" + item.ReasonKey + ":" + (item.Retry != null) + ":" + (item.Resolve != null) +
            ":" + item.ActionLabelKey + ":" + item.ResolveLabelKey + ":" + locations.ContainsKey(item.ItemId)));

        private static void Render(List<AttentionItem> items, Dictionary<int, PixelWindow> locations = null)
        {
            locations = locations ?? LocateItems(items);
            string ownRun = run; int ownSlot = slot;
            Vector2? position = window?.IsAlive == true ? window.Rect.anchoredPosition : (Vector2?)null;
            var builder = CustomUIManager.Window(ModEntry.Id + ".attention", ShopSettings.Text("attention.open"), CustomUIManager.LAYER_META)
                .SetSize(620, 440).SetDraggable(true).SetCloseOnEscape(true);
            window = builder.GetWindow(); CustomUIWindow own = window;
            builder.OnClose(() => { if (ReferenceEquals(window, own)) window = null; });
            builder.BeginScrollFlexible(160);
            if (items.Count == 0) builder.AddLabel(ShopSettings.Text("attention.empty"));
            foreach (AttentionItem item in items)
            {
                builder.AddLabel(item.Title ?? "");
                if (builder.Last.text != null) builder.Last.text.richText = false;
                if (!string.IsNullOrEmpty(item.Location))
                { builder.AddLabel(item.Location); if (builder.Last.text != null) builder.Last.text.richText = false; }
                builder.AddLabel(ShopSettings.Text(item.ReasonKey ?? "availability.unknown"));
                builder.BeginRow();
                if (locations.ContainsKey(item.ItemId)) builder.AddButton(ShopSettings.Text("attention.locate"), () =>
                { if (SameScope(ownRun, ownSlot)) TryLocate(item.ItemId, true); });
                if (item.Retry != null) builder.AddButton(ShopSettings.Text(item.ActionLabelKey ?? "attention.retry"), () =>
                    Invoke(item.Key, ownRun, ownSlot, false));
                if (item.Resolve != null) builder.AddButton(ShopSettings.Text(item.ResolveLabelKey ?? "attention.resolve"), () =>
                    Invoke(item.Key, ownRun, ownSlot, true));
                builder.End();
            }
            builder.End().BeginRow();
            if (ShopNotice.HasResult) builder.AddButton(ShopSettings.Text("operation.last"), ShopNotice.OpenLastResult);
            builder.AddButton(ShopSettings.Text("attention.refresh"), () => { if (SameScope(ownRun, ownSlot)) Render(Snapshot()); }).End().Show();
            if (position.HasValue) window.Rect.anchoredPosition = position.Value;
            signature = Signature(items, locations); dirty = false; refreshAt = Time.unscaledTime + 0.5f;
        }
        private static bool SameScope(string expectedRun, int expectedSlot) => ShopInventory.InStore &&
            PlayerStore.Instance.runID == expectedRun && PlayerStore.Instance.saveSlotId == expectedSlot;
        private static void Invoke(string key, string ownRun, int ownSlot, bool resolve)
        {
            if (!SameScope(ownRun, ownSlot) || !ActionReady()) return;
            AttentionItem item = Snapshot().FirstOrDefault(entry => entry.Key == key);
            if (item == null) { Changed(); return; }
            try { (resolve ? item.Resolve : item.Retry)?.Invoke(); Changed(); }
            catch (Exception error) { log?.Invoke("Attention action failed: " + error); ShopNotice.Show(ShopSettings.Text("attention.action_failed")); }
        }
        private static bool ActionReady()
        {
            var ui = StoreUIManager.Instance;
            return ui != null && !KeyBindHelper.IsCapturing && !UIDragHandler.isDraggingAnyUI &&
                ItemMouseDragHandler.current?.IsDraggingItem != true &&
                (ui.blackscreen == null || !ui.blackscreen.gameObject.activeInHierarchy) &&
                (GenericConfirmationUIManager.Instance == null || !GenericConfirmationUIManager.Instance.panel.activeInHierarchy) &&
                (ui.confirmPanelUIManager == null || !ui.confirmPanelUIManager.panel.activeInHierarchy);
        }
        private static bool TryLocate(int id, bool perform)
        {
            if (id <= 0 || !ShopInventory.InStore) return false;
            foreach (var location in ShopInventory.MachineLocations())
                foreach (GameItem item in location.Inventory.childItems)
                {
                    if (item?.uniqueId != id || !(item is GameItemElement element) || !VisibleInventoryViews.Visible(element)) continue;
                    PixelWindow parent = element.handler.FindParentWindow();
                    if (parent == null) return false;
                    if (perform) parent.ToFront();
                    return true;
                }
            return false;
        }
        private static Dictionary<int, PixelWindow> LocateItems(IEnumerable<AttentionItem> items)
        {
            var result = new Dictionary<int, PixelWindow>();
            var wanted = new HashSet<int>(items.Where(item => item.ItemId > 0).Select(item => item.ItemId));
            if (wanted.Count == 0 || !ShopInventory.InStore) return result;
            foreach (var location in ShopInventory.MachineLocations())
                foreach (GameItem item in location.Inventory.childItems)
                    if (item != null && wanted.Contains(item.uniqueId) && item is GameItemElement element &&
                        VisibleInventoryViews.Visible(element))
                    {
                        PixelWindow parent = element.handler.FindParentWindow();
                        if (parent != null) result[item.uniqueId] = parent;
                    }
            return result;
        }
        internal static void Tick()
        {
            if (ShopInventory.InStore) Ensure();
            else
            {
                if (loaded && (!PlayerStore.HasInstance || run != PlayerStore.Instance.runID || slot != PlayerStore.Instance.saveSlotId)) Reset();
                else if (window != null) { CustomUIWindow old = window; window = null; old.Close(); }
                return;
            }
            if (dirty)
            {
                List<AttentionItem> current = Snapshot();
                var added = current.Where(item => !announced.Contains(item.Key)).ToList();
                announced.Clear();
                foreach (AttentionItem item in current) announced.Add(item.Key);
                if (added.Count > 0) ShopNotice.Show(added.Count == 1 ?
                    added[0].Title + " · " + ShopSettings.Text(added[0].ReasonKey ?? "availability.unknown") :
                    ShopSettings.Format("attention.summary", added.Count), 6f);
                if (window?.IsAlive == true) Render(current);
                dirty = false;
            }
            if (window == null) return;
            if (!window.IsAlive) { window = null; return; }
            if (!SameScope(run, slot)) { Reset(); return; }
            if (!dirty && Time.unscaledTime < refreshAt) return;
            refreshAt = Time.unscaledTime + 0.5f;
            List<AttentionItem> items = Snapshot();
            var locations = LocateItems(items);
            if (dirty || Signature(items, locations) != signature) Render(items, locations);
        }
        private static void SceneChanged(Scene before, Scene after) => Reset();
        private static void Reset()
        {
            CustomUIWindow old = window; window = null; old?.Close();
            data = null; run = null; slot = 0; loaded = invalid = saveFailed = false; signature = null; dirty = true;
            announced.Clear();
            ShopNotice.ResetSession();
        }
        internal static void Dispose()
        {
            ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset(); providers.Clear(); providerErrors.Clear(); log = null;
        }
    }
}
