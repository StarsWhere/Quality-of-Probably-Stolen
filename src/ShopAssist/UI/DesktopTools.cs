using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using LayoutData = ShopAssist.DesktopLayoutData;
using Layout = ShopAssist.DesktopLayout;
using Position = ShopAssist.DesktopPosition;

namespace ShopAssist
{
    internal sealed partial class DesktopTools : IDisposable
    {
        private const string WindowId = "shopassist.f13.groups";
        private const string LayoutKey = "f13.layouts";
        private const int MaxGroups = DesktopLayoutRules.MaxGroups;
        private const int MaxMembers = DesktopLayoutRules.MaxMembers;
        private readonly ConfigEntry<bool> groupsEnabled;
        private readonly Func<KeyCode> closeKey;
        private readonly Func<KeyCode> groupsKey;
        private readonly Action<string> log;
        private CustomUIWindow window;
        private CustomUIElement status;
        private string draftName = "";
        private GameItem coordinator;
        private Guid windowRun;
        private int windowSlot;
        private bool started;
        private bool bargainFaulted;
        private bool inspectionFaulted;

        [Serializable] private sealed class NativePresetList { public List<MachineStorageCoordinator.Preset> presets = null; }

        internal DesktopTools(ShopSettings settings, Action<string> log)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            groupsEnabled = settings.Toggle("f13.enabled", "f13.tip");
            closeKey = settings.Key("f13.close.key", "f13.close.tip");
            groupsKey = settings.Key("f13.groups.key", "f13.groups.tip");
            settings.Action("f13.close", CloseWindows, Available);
            settings.Action("f13.groups", OpenGroups, GroupsAvailable);
            InitBargain(settings);
            InitInspection(settings);
        }

        public void Start()
        {
            if (started) return;
            started = true;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        public void Tick()
        {
            if (!started) return;
            if (!bargainEnabled.Value) bargainFaulted = false;
            if (!bargainFaulted)
            {
                try { TickBargain(); }
                catch (Exception error)
                {
                    log("F14 stopped after an error: " + error);
                    try { ResetBargain(); } catch (Exception cleanup) { log("F14 cleanup failed: " + cleanup); }
                    bargainFaulted = true;
                }
            }
            if (!inspectionEnabled.Value) inspectionFaulted = false;
            if (!inspectionFaulted)
            {
                try { TickInspection(); }
                catch (Exception error)
                {
                    log("F18 stopped after an error: " + error);
                    try { ReleaseInspection(false, true); } catch (Exception cleanup) { log("F18 cleanup failed: " + cleanup); }
                    inspectionFaulted = true;
                }
            }
            if (!groupsEnabled.Value)
            {
                if (window != null) CloseGroups();
                return;
            }
            if (window != null && !SameSession()) CloseGroups();
            if (!CanOperate() || !ShopInput.Ready()) return;
            KeyCode close = closeKey();
            KeyCode groups = groupsKey();
            if (close != KeyCode.None && Input.GetKeyDown(close)) CloseWindows();
            else if (groups != KeyCode.None && Input.GetKeyDown(groups)) OpenGroups();
        }

        private bool Available()
        {
            return groupsEnabled.Value && PlayerStore.HasInstance && EmporiumEntry.Instance != null &&
                PlayerStore.Instance.playerLocation == PlayerStore.PlayerLocation.IN_STORE && WindowsHandler.current != null;
        }

        private bool CanOperate()
        {
            if (!Available()) return false;
            var ui = StoreUIManager.Instance;
            // Explicit controls live in the options window, so those actions
            // must remain available there. Keyboard input is separately gated
            // by ShopInput.Ready() in Tick.
            return ui != null && !StoreUIManager.IsAnyTyping() && !KeyBindHelper.IsCapturing &&
                (ui.escapeUIManager == null || !ui.escapeUIManager.menuPanel.activeInHierarchy) &&
                (GenericConfirmationUIManager.Instance == null || !GenericConfirmationUIManager.Instance.panel.activeInHierarchy) &&
                (ui.confirmPanelUIManager == null || !ui.confirmPanelUIManager.panel.activeInHierarchy) &&
                (ui.blackscreen == null || !ui.blackscreen.gameObject.activeInHierarchy) && !Input.GetMouseButton(0) &&
                !ui.isAboutToStartMainDialog &&
                (ui.closeShutterConfirmationPanel == null || !ui.closeShutterConfirmationPanel.activeInHierarchy) &&
                (DialogUIManager.Instance == null || DialogUIManager.Instance.currentDialog == null) &&
                (DialoguePhoneUIManager.Instance == null || DialoguePhoneUIManager.Instance.currentDialog == null) &&
                (ItemMouseDragHandler.current == null || !ItemMouseDragHandler.current.IsDraggingItem) &&
                !UIDragHandler.isDraggingAnyUI && !MachineStorageCoordinator.IsBusy;
        }

        private bool GroupsAvailable()
        {
            if (!Available() || WindowsHandler.current.visibleWindows == null) return false;
            GameItem item = MachineStorageCoordinator.FindCoordinatorInReach();
            return item != null && IsAllowedItem(item);
        }

        private void CloseWindows()
        {
            if (!CanOperate() || WindowsHandler.current.visibleWindows == null) return;
            PixelWindow[] visible = WindowsHandler.current.visibleWindows.ToArray();
            Array.Sort(visible, (a, b) => (b?.focusStamp ?? 0).CompareTo(a?.focusStamp ?? 0));
            int closed = 0;
            foreach (PixelWindow candidate in visible)
            {
                if (!CanOperate()) break;
                if (!TryGetVisibleOwner(candidate, out _)) continue;
                try
                {
                    WindowsHandler.CloseWindow(candidate);
                    if (!candidate.IsVisible()) closed++;
                }
                catch (Exception error) { log("F13 could not close a window: " + error); break; }
            }
            if (closed > 0) log("F13 closed " + closed + " eligible windows.");
        }

        private void OpenGroups()
        {
            if (!CanOperate()) return;
            if (window != null && window.IsAlive) { window.Close(); return; }
            if (CustomUIManager.Instance == null || !Guid.TryParse(PlayerStore.Instance.runID, out windowRun) || windowRun == Guid.Empty)
                return;
            windowSlot = PlayerStore.Instance.saveSlotId;
            coordinator = MachineStorageCoordinator.FindCoordinatorInReach();
            if (coordinator == null || !IsAllowedItem(coordinator))
            {
                coordinator = null;
                log("F13 requires an accessible storage coordinator for groups.");
                return;
            }
            BuildGroupsWindow();
        }

        private void BuildGroupsWindow()
        {
            if (!SameSession() || !CoordinatorAvailable() || CustomUIManager.Instance == null) return;
            if (!TryPresets(coordinator, out List<MachineStorageCoordinator.Preset> presets))
            {
                log("F13 preserved invalid coordinator presets.");
                return;
            }
            var builder = CustomUIManager.Instance.CreateWindow(WindowId, ShopSettings.Text("f13.title"), "paper")
                .SetSize(620f, 440f).SetDraggable(true).SetCloseOnEscape().OnClose(() => { window = null; status = null; });
            builder.BeginRow();
            builder.AddInput(ShopSettings.Text("f13.name"), draftName, text => draftName = text, "name", text => draftName = text);
            builder.AddButton(ShopSettings.Text("f13.save"), SaveCurrent, "save");
            builder.End();
            builder.BeginRow();
            builder.AddButton(ShopSettings.Text("f13.native"), () =>
            {
                if (!CanOperate() || !CoordinatorAvailable()) return;
                GameItem item = coordinator;
                CloseGroups();
                MachineStorageCoordinator.OpenWindow(item);
            }, "native");
            builder.End();
            builder.AddLabel("", "status");
            status = builder.Last;
            builder.BeginScroll(290f);
            foreach (MachineStorageCoordinator.Preset preset in presets)
            {
                if (preset.itemIds.Count == 0) continue;
                string members = MembersKey(preset.itemIds);
                string name = preset.name ?? "";
                builder.BeginRow();
                builder.AddLabel(name);
                builder.AddFlexibleSpace();
                builder.AddButton(ShopSettings.Text("f13.capture"), () => CaptureExisting(members), null);
                builder.AddButton(ShopSettings.Text("f13.restore"), () => Restore(members), null);
                builder.End();
            }
            builder.End();
            window = builder.Center().Show();
        }

        private void SaveCurrent()
        {
            if (!CanOperate() || !CoordinatorAvailable()) return;
            string name = (draftName ?? "").Trim();
            if (name.Length == 0 || name.Length > 48) { Report("f13.invalid_name"); return; }
            if (!TryPresets(coordinator, out List<MachineStorageCoordinator.Preset> presets) ||
                !TryLayouts(out LayoutData data)) { Report("f13.data_invalid"); return; }
            if (presets.Count >= MaxGroups) { Report("f13.limit"); return; }
            List<PixelWindow> visible = EligibleWindows();
            if (visible.Count == 0 || visible.Count > MaxMembers) { Report("f13.no_windows"); return; }

            var preset = new MachineStorageCoordinator.Preset { name = name };
            foreach (PixelWindow candidate in visible)
            {
                if (!TryGetVisibleOwner(candidate, out GameItem owner)) { Report("f13.changed"); return; }
                int id = owner.GetUniqueID(); // Explicit player action creates the durable binding.
                if (id <= 0 || preset.itemIds.Contains(id)) { Report("f13.changed"); return; }
                preset.itemIds.Add(id);
                preset.SetTabs(id, owner.GetContentTabsSelection());
            }
            string key = MembersKey(preset.itemIds);
            if (presets.Any(p => MembersKey(p.itemIds) == key)) { Report("f13.duplicate"); return; }
            int coordinatorId = coordinator.GetUniqueID();
            var existing = new HashSet<string>(presets.Where(p => p.itemIds.Count > 0).Select(p => MembersKey(p.itemIds)));
            data.layouts.RemoveAll(l => l.coordinatorId == coordinatorId && !existing.Contains(l.members));
            if (data.layouts.Count >= MaxGroups) { Report("f13.limit"); return; }
            Layout layout = CreateLayout(coordinatorId, key, visible);
            if (layout == null) { Report("f13.changed"); return; }
            presets.Add(preset);
            try
            {
                MachineStorageCoordinator.SavePresets(coordinator, presets);
                PutLayout(data, layout);
                SaveLayouts(data);
                draftName = "";
                Rebuild();
                Report("f13.saved");
            }
            catch (Exception error) { log("F13 could not save group/layout: " + error); Report("f13.save_failed"); }
        }

        private void CaptureExisting(string members)
        {
            if (!CanOperate() || !CoordinatorAvailable()) return;
            if (!TryUniquePreset(members, out MachineStorageCoordinator.Preset preset) ||
                !TryLayouts(out LayoutData data)) { Report("f13.data_invalid"); return; }
            var ids = new HashSet<int>(preset.itemIds);
            List<PixelWindow> visible = EligibleWindows().Where(w =>
                TryGetVisibleOwner(w, out GameItem owner) && ids.Contains(owner.uniqueId)).ToList();
            if (visible.Count != ids.Count || visible.Count == 0) { Report("f13.open_members"); return; }
            int coordinatorId = coordinator.GetUniqueID();
            if (data.layouts.Count >= MaxGroups && !data.layouts.Any(l => l.coordinatorId == coordinatorId && l.members == members))
            { Report("f13.limit"); return; }
            Layout layout = CreateLayout(coordinatorId, members, visible);
            if (layout == null) { Report("f13.changed"); return; }
            try { PutLayout(data, layout); SaveLayouts(data); Report("f13.saved"); }
            catch (Exception error) { log("F13 could not save layout: " + error); Report("f13.save_failed"); }
        }

        private void Restore(string members)
        {
            if (!CanOperate() || !CoordinatorAvailable()) return;
            if (!TryUniquePreset(members, out MachineStorageCoordinator.Preset preset) ||
                !TryLayouts(out LayoutData data)) { Report("f13.data_invalid"); return; }
            if (ItemMouseDoubleClickHandler.current == null) { Report("f13.changed"); return; }
            int coordinatorId = coordinator.uniqueId;
            Layout layout = data.layouts.Find(l => l.coordinatorId == coordinatorId && l.members == members);
            Dictionary<int, GameItem> allowed = FindAllowedItems(new HashSet<int>(preset.itemIds));
            int restored = 0;
            IEnumerable<int> order = layout == null ? preset.itemIds : layout.positions.OrderBy(p => p.order).Select(p => p.id);
            foreach (int id in order)
            {
                if (!CanOperate() || !CoordinatorAvailable()) break;
                if (!allowed.TryGetValue(id, out GameItem item) || !IsAllowedItem(item) || !CanOpen(item)) continue;
                PixelWindow itemWindow = item.contentWindow;
                if (!itemWindow.IsVisible()) ItemMouseDoubleClickHandler.current.OpenContentAction(item, Input.mousePosition);
                if (!itemWindow.IsVisible()) continue;
                item.ApplyContentTabsSelection(preset.GetTabs(id));
                Position saved = layout?.positions.Find(p => p.id == id);
                if (saved != null)
                {
                    itemWindow.rectTransform.anchoredPosition = new Vector2(saved.x, saved.y);
                    itemWindow.FixBounds();
                }
                itemWindow.ToFront();
                restored++;
            }
            log("F13 restored " + restored + " of " + preset.itemIds.Count + " group windows.");
            Report(restored == preset.itemIds.Count ? "f13.restored" : "f13.partial");
        }

        private Layout CreateLayout(int coordinatorId, string members, List<PixelWindow> windows)
        {
            if (coordinatorId <= 0 || windows.Count > MaxMembers) return null;
            var result = new Layout { coordinatorId = coordinatorId, members = members, positions = new List<Position>() };
            int order = 0;
            foreach (PixelWindow candidate in windows.OrderBy(w => w.focusStamp))
            {
                if (!TryGetVisibleOwner(candidate, out GameItem owner) || owner.uniqueId <= 0) return null;
                Vector2 value = candidate.rectTransform.anchoredPosition;
                if (!Finite(value.x) || !Finite(value.y)) return null;
                result.positions.Add(new Position { id = owner.uniqueId, x = value.x, y = value.y, order = order++ });
            }
            return result;
        }

        private List<PixelWindow> EligibleWindows()
        {
            if (WindowsHandler.current?.visibleWindows == null) return new List<PixelWindow>();
            return WindowsHandler.current.visibleWindows.Where(w => TryGetVisibleOwner(w, out _)).ToList();
        }

        private bool TryGetVisibleOwner(PixelWindow itemWindow, out GameItem owner)
        {
            owner = null;
            if (itemWindow == null || !itemWindow.canMove || itemWindow.isImportant || itemWindow.isLocked ||
                !itemWindow.IsVisible() || itemWindow.parentItems == null || itemWindow.parentItems.Count != 1 ||
                (StoreUIManager.Instance != null && StoreUIManager.Instance.IsInnateWindow(itemWindow))) return false;
            GameItem item = itemWindow.parentItems[0];
            if (item == null || item.contentWindow != itemWindow || !IsAllowedItem(item)) return false;
            owner = item;
            return true;
        }

        private static bool CanOpen(GameItem item)
        {
            PixelWindow target = item?.contentWindow;
            return target != null && target.canMove && !target.isImportant && !target.isLocked &&
                target.parentItems != null && target.parentItems.Count == 1 && ReferenceEquals(target.parentItems[0], item) &&
                (StoreUIManager.Instance == null || !StoreUIManager.Instance.IsInnateWindow(target)) &&
                (!item.IsTag("TRACK_OPENED_TAG") || item.IsTag("OPENED_TAG"));
        }

        private static GraphNodeStorage[] AllowedRoots()
        {
            EmporiumEntry entry = EmporiumEntry.Instance;
            if (entry == null) return Array.Empty<GraphNodeStorage>();
            return new GraphNodeStorage[] { entry.invWindow, entry.backInvWindow, entry.backInvinvElementCounter,
                entry.docInvWindow, entry.showcaseWindow, entry.vendingMachineWindow, entry.vendingFountainWindow };
        }

        private static bool IsAllowedItem(GameItem item)
        {
            if (item == null || item.parentInventory == null || !GeneralHelper.IsItemOwned(item)) return false;
            var roots = new HashSet<GraphNodeStorage>(AllowedRoots().Where(r => r != null));
            var seen = new HashSet<GraphNodeStorage>();
            GraphNodeStorage node = item;
            for (int depth = 0; node != null && depth < 128; depth++)
            {
                if (!seen.Add(node)) return false;
                if (roots.Contains(node)) return true;
                if (node is GameItem parent && !GeneralHelper.IsItemOwned(parent)) return false;
                if (node is PixelWindow parentWindow && (!parentWindow.IsVisible() || parentWindow.isLocked)) return false;
                List<GraphNodeStorage> parents = node.parents;
                if (parents == null || parents.Count != 1) return false;
                node = parents[0];
            }
            return false;
        }

        private static Dictionary<int, GameItem> FindAllowedItems(HashSet<int> ids)
        {
            var found = new Dictionary<int, GameItem>();
            var ambiguous = new HashSet<int>();
            var queue = new Queue<GraphNodeStorage>(AllowedRoots().Where(r => r != null));
            var visited = new HashSet<GraphNodeStorage>();
            while (queue.Count > 0 && visited.Count < 100000)
            {
                GraphNodeStorage node = queue.Dequeue();
                if (node == null || !visited.Add(node)) continue;
                if (node is GameItem item)
                {
                    if (!GeneralHelper.IsItemOwned(item)) continue;
                    if (ids.Contains(item.uniqueId) && IsAllowedItem(item) && !ambiguous.Contains(item.uniqueId))
                    {
                        if (found.TryGetValue(item.uniqueId, out GameItem other) && !ReferenceEquals(other, item))
                        { found.Remove(item.uniqueId); ambiguous.Add(item.uniqueId); }
                        else found[item.uniqueId] = item;
                    }
                    if (item.contentWindow == null || !item.contentWindow.IsVisible() || item.contentWindow.isLocked) continue;
                }
                List<GraphNodeStorage> children = node.children;
                if (children != null) foreach (GraphNodeStorage child in children) if (child != null) queue.Enqueue(child);
            }
            if (queue.Count > 0) found.Clear(); // Incomplete traversal cannot prove identity.
            return found;
        }

        private bool CoordinatorAvailable()
        {
            return SameSession() && coordinator != null && IsAllowedItem(coordinator) &&
                ReferenceEquals(coordinator, MachineStorageCoordinator.FindCoordinatorInReach());
        }

        private bool SameSession()
        {
            return Available() && Guid.TryParse(PlayerStore.Instance.runID, out Guid run) && run == windowRun &&
                PlayerStore.Instance.saveSlotId == windowSlot;
        }

        private static string MembersKey(IEnumerable<int> ids) => DesktopLayoutRules.MembersKey(ids);
        private static bool Finite(float value) => DesktopLayoutRules.Finite(value);

        private bool TryUniquePreset(string members, out MachineStorageCoordinator.Preset preset)
        {
            preset = null;
            if (!TryPresets(coordinator, out List<MachineStorageCoordinator.Preset> presets)) return false;
            foreach (MachineStorageCoordinator.Preset candidate in presets)
            {
                if (MembersKey(candidate.itemIds) != members) continue;
                if (preset != null) return false;
                preset = candidate;
            }
            return preset != null;
        }

        private static bool TryPresets(GameItem item, out List<MachineStorageCoordinator.Preset> presets)
        {
            presets = null;
            try
            {
                string raw = item.GetTagReadonly(MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING).GetString();
                if (!string.IsNullOrEmpty(raw))
                {
                    if (raw.Length > 131072) return false;
                    NativePresetList parsed = ShopData.Decode<NativePresetList>(raw, 131072);
                    if (parsed?.presets == null) return false;
                }
                presets = MachineStorageCoordinator.LoadPresets(item);
                if (presets == null || presets.Count > MaxGroups) return false;
                foreach (MachineStorageCoordinator.Preset preset in presets)
                    if (preset == null || preset.name == null || preset.name.Length > 128 || preset.itemIds == null ||
                        preset.itemIds.Count > MaxMembers || preset.itemIds.Any(id => id <= 0) ||
                        preset.itemIds.Distinct().Count() != preset.itemIds.Count) return false;
                return true;
            }
            catch (Exception) { return false; }
        }

        private bool TryLayouts(out LayoutData data)
        {
            data = null;
            try
            {
                string raw = ModHelper.GetModData(ModEntry.Id, LayoutKey, null);
                if (raw == null)
                {
                    data = new LayoutData { schemaVersion = 1, runId = windowRun.ToString("N"), layouts = new List<Layout>() };
                    return true;
                }
                if (raw.Length > 131072) return false;
                data = ShopData.Decode<LayoutData>(raw, 131072);
                return DesktopLayoutRules.IsValid(data, windowRun);
            }
            catch (Exception) { return false; }
        }

        private static void PutLayout(LayoutData data, Layout layout)
        {
            data.layouts.RemoveAll(l => l.coordinatorId == layout.coordinatorId && l.members == layout.members);
            data.layouts.Add(layout);
        }

        private static void SaveLayouts(LayoutData data) => ModHelper.SetModData(ModEntry.Id, LayoutKey, ShopData.Encode(data));

        private void Rebuild()
        {
            window?.Close();
            window = null;
            BuildGroupsWindow();
        }

        private void Report(string key) { if (status != null && status.IsAlive) status.SetText(ShopSettings.Text(key)); }
        private void CloseGroups() { window?.Close(); window = null; status = null; }
        private void Reset() { CloseGroups(); coordinator = null; draftName = ""; ResetBargain(); bargainFaulted = false;
            ReleaseInspection(false, true); inspectionFaulted = false; unsupportedInspection = null; unsupportedInspectionItem = null; }
        private void SceneChanged(Scene oldScene, Scene newScene) => Reset();

        public void Dispose()
        {
            if (!started) return;
            if (started)
            {
                ModHook.OnGameLoadedInit -= Reset;
                ModHook.OnLeavingStoreEarly -= Reset;
                SceneManager.activeSceneChanged -= SceneChanged;
            }
            Reset();
            started = false;
        }
    }
}
