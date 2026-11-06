using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    internal sealed class InventoryOrganizer : IDisposable
    {
        private static InventoryOrganizer current;
        private sealed class Held
        {
            internal PixelWindow Window;
            internal readonly HashSet<GameItem> Items = new HashSet<GameItem>();
        }
        private readonly ShopSettings settings;
        private readonly Action<string> log;
        private readonly ConfigEntry<bool> sortEnabled, holdEnabled, rotate, keepMenu, autoInsert;
        private readonly Func<KeyCode> key;
        private readonly Dictionary<GameGridInventory, Held> holds = new Dictionary<GameGridInventory, Held>();
        private readonly ViewOutlines holdOutlines = new ViewOutlines("ShopAssist_HoldOutline");
        private readonly HashSet<GameItemElement> drawn = new HashSet<GameItemElement>();
        private bool started, dirty = true;
        private IInventoryArrangeUndo lastSort;
        private static readonly FieldInfo NativeUndo = typeof(InventorySortHelper).GetField("undoSnapshots", BindingFlags.Static | BindingFlags.NonPublic);
        private static bool PeerSort => ShopPeers.Running("smart_inventory_arrange");

        internal InventoryOrganizer(ShopSettings settings, Action<string> log)
        {
            this.settings = settings;
            this.log = log;
            sortEnabled = settings.Toggle("f05.enabled", "f05.tip", true);
            holdEnabled = settings.Toggle("f06.enabled", "f06.tip");
            rotate = settings.Toggle("f05.rotate", "f05.rotate.tip", true);
            keepMenu = settings.Toggle("f05.keep_menu", "f05.keep_menu.tip", true);
            autoInsert = settings.Toggle("f05.auto_insert", "f05.auto_insert.tip");
            key = settings.Key("f05.key", "f05.key.tip");
            settings.Action("f05.open", () => Sort(null, false, true), CanOpen);
        }
        public void Start()
        {
            if (started) return;
            current = this;
            InventoryContextActions.RegisterGrid("f05.open", source => SortLabel(source, false), CanSort, source => Sort(source, false, true));
            InventoryContextActions.RegisterGrid("f05.all_grid", source => ShopSettings.Text("f05.all"),
                source => CanSort(source) && HasSelection(source), source => Sort(source, false, false));
            InventoryContextActions.RegisterGrid("f05.preserve", source => SortLabel(source, true), CanSort, source => Sort(source, true, true));
            InventoryContextActions.RegisterGrid("f05.preserve_all_grid", source => ShopSettings.Text("f05.preserve_all"),
                source => CanSort(source) && HasSelection(source), source => Sort(source, true, false));
            InventoryContextActions.RegisterGrid("f05.undo_grid", source => ShopSettings.Text("f05.undo"), HasUndo, UndoGrid);
            InventoryContextActions.RegisterItem("f05.item", item => SortLabel(item?.parentInventory as GameGridInventory, false),
                item => CanSort(item?.parentInventory as GameGridInventory), item => Sort(item.parentInventory as GameGridInventory, false, true));
            InventoryContextActions.RegisterItem("f05.all_item", item => ShopSettings.Text("f05.all"),
                item => CanSort(item?.parentInventory as GameGridInventory) && HasSelection(item.parentInventory as GameGridInventory),
                item => Sort(item.parentInventory as GameGridInventory, false, false));
            InventoryContextActions.RegisterItem("f05.preserve_item", item => SortLabel(item?.parentInventory as GameGridInventory, true),
                item => CanSort(item?.parentInventory as GameGridInventory), item => Sort(item.parentInventory as GameGridInventory, true, true));
            InventoryContextActions.RegisterItem("f05.preserve_all_item", item => ShopSettings.Text("f05.preserve_all"),
                item => CanSort(item?.parentInventory as GameGridInventory) && HasSelection(item.parentInventory as GameGridInventory),
                item => Sort(item.parentInventory as GameGridInventory, true, false));
            InventoryContextActions.RegisterItem("f05.undo_item", item => ShopSettings.Text("f05.undo"),
                item => HasUndo(item?.parentInventory as GameGridInventory), item => Undo(item.parentInventory as GameGridInventory));
            InventoryContextActions.RegisterItem("f06.hold", item => ShopSettings.Text(IsHeld(item) ? "f06.release" : "f06.hold"),
                CanHold, ToggleHold);
            started = true;
            InventoryArrangeTransaction.GridScope = GridScope;
            InventoryContextActions.ConfigureSortMenu(() => started && keepMenu.Value, CanSort);
            settings.Changed += SettingsChanged;
            VisibleInventoryViews.Changed += ViewsChanged;
            VisibleInventoryViews.ItemChanged += ItemChanged;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }
        private bool CanOpen() => started && sortEnabled.Value && !PeerSort && ShopInventory.InStore && Locations().Count != 0;
        private bool CanSort(GameGridInventory grid) => started && sortEnabled.Value && !PeerSort && grid != null &&
            Locations().Any(location => ReferenceEquals(location.Inventory, grid));
        internal static bool CanOpenSortGrid(GameGridInventory grid) => current?.CanSort(grid) == true;
        private static string SortLabel(GameGridInventory grid, bool preserve)
        {
            bool selected = HasSelection(grid);
            return ShopSettings.Text(preserve ? (selected ? "f05.preserve_selected" : "f05.preserve_all") :
                (selected ? "f05.selected" : "f05.all"));
        }
        private static bool HasSelection(GameGridInventory grid) => grid != null &&
            ShopSelection.Current().Any(item => ReferenceEquals(item.parentInventory, grid));
        public void Tick()
        {
            if (!started) return;
            PruneHolds();
            if (dirty) RefreshHolds();
            KeyCode hotkey = key();
            if (hotkey != KeyCode.None && Input.GetKeyDown(hotkey) && ShopInput.Ready())
                Sort(RenderHandler.RaycastFindParentElement<GameGridInventory>(Input.mousePosition), false, true);
        }
        private void SettingsChanged()
        {
            if (!sortEnabled.Value)
            {
                lastSort = null;
                foreach (string id in new[] { "f05.open", "f05.all_grid", "f05.preserve", "f05.preserve_all_grid", "f05.undo_grid",
                    "f05.item", "f05.all_item", "f05.preserve_item", "f05.preserve_all_item", "f05.undo_item" }) InventoryContextActions.Hide(id);
                InventoryContextActions.DismissSortMenu();
            }
            if (!holdEnabled.Value) { ClearHolds(); InventoryContextActions.Hide("f06.hold"); }
            dirty = true;
        }
        private void ViewsChanged() { PruneHolds(); dirty = true; }
        private void ItemChanged(GameItemElement item) { PruneHolds(); dirty = true; }
        private void SceneChanged(Scene before, Scene after) => Reset();
        private static List<ShopInventory.Location> Locations() => ShopInventory.OwnedLocations(true)
            .Where(l => l.Ordinary && l.Inventory is GameGridInventory && !l.Inventory.IsInsertLocked() &&
                !l.Inventory.IsRemoveLocked() && GeneralHelper.MayPlayerInsertInto(l.Inventory)).ToList();

        private static string GridScope(GameGridInventory grid)
        {
            var location = Locations().FirstOrDefault(value => ReferenceEquals(value.Inventory, grid));
            if (location == null) return null;
            string path = string.Join("/", location.Path.Select(item => RuntimeHelpers.GetHashCode(item).ToString()));
            return location.Role + ":" + location.Page + ":" + (location.Owner == null ? 0 : RuntimeHelpers.GetHashCode(location.Owner)) + ":" + path;
        }

        internal static bool AutomaticEnabled => current?.started == true && current.sortEnabled.Value && current.autoInsert.Value && !PeerSort && ShopInventory.InStore;
        internal static bool AutomaticGrid(GameGridInventory grid) => AutomaticEnabled && grid != null &&
            Locations().Any(location => ReferenceEquals(location.Inventory, grid) && ShopInventory.IsMainStockBranch(location));
        internal static HashSet<GameItem> AutomaticFixed(GameGridInventory grid)
        {
            var fixedItems = RequestBook.ReservedItems(Locations());
            if (grid != null && current != null && current.holds.TryGetValue(grid, out Held held)) fixedItems.UnionWith(held.Items);
            return fixedItems;
        }
        internal static bool AutomaticRotate => current?.rotate.Value == true;
        internal static void Remember(IInventoryArrangeUndo operation)
        {
            if (current?.started != true || operation == null) return;
            if (NativeUndo?.GetValue(null) is System.Collections.IDictionary native)
                foreach (GameGridInventory grid in operation.Grids) native.Remove(grid);
            else current.log("Native arrangement undo store is unavailable.");
            current.lastSort = operation;
        }
        internal static void AutomaticFailure(string detail) => current?.log("Automatic arrangement: " + detail);

        private void Sort(GameGridInventory requested, bool preserveContainers, bool selectedOnly)
        {
            if (!started || !sortEnabled.Value || PeerSort || !ShopInventory.InStore) return;
            var locations = Locations();
            var grid = requested ?? locations.OrderByDescending(l => (l.Inventory as GameGridInventory)?.handler?.FindParentWindow()?.focusStamp ?? 0)
                .Select(l => l.Inventory as GameGridInventory).FirstOrDefault();
            if (grid == null || !locations.Any(l => l.Inventory == grid)) return;
            PruneHolds();
            try
            {
                var selected = selectedOnly ? ShopSelection.Current().Where(i => i.parentInventory == grid).ToHashSet() : null;
                var keep = RequestBook.ReservedItems(locations);
                if (holds.TryGetValue(grid, out Held held)) keep.UnionWith(held.Items);
                var plan = InventorySortPlan.Create(grid, selected, keep, true, rotate.Value, preserveContainers);
                if (plan == null) { ShopNotice.Show(ShopSettings.Text("f05.no_plan")); return; }
                int moved = plan.Placements.Count(p => !InventorySortPlan.SameShape(p.Before, p.After));
                var transaction = InventoryArrangeTransaction.Capture(new[] { grid }, operationGuard: plan.SafeForRefresh);
                if (transaction == null || !plan.CanApply()) { ShopNotice.Show(ShopSettings.Text("f05.changed")); return; }
                var destinations = plan.Placements.ToDictionary(p => p.Item,
                    p => new InventoryArrangeTransaction.Destination { Grid = grid, Shape = p.After });
                var result = transaction.Commit(destinations);
                if (result == InventorySortPlan.ApplyResult.Applied && moved != 0) Remember(transaction);
                if (transaction.LastError != null) log("Smart arrange: " + transaction.LastError);
                ShopNotice.Show(result == InventorySortPlan.ApplyResult.Applied ? ShopSettings.Format("f05.applied", moved) :
                    ShopSettings.Text(result == InventorySortPlan.ApplyResult.Restored ? "f05.restored" :
                        result == InventorySortPlan.ApplyResult.Inspect ? "f05.inspect" : "f05.changed"));
                VisibleInventoryViews.NotifyChanged();
            }
            catch (Exception error) { log("Smart arrange failed: " + error); ShopNotice.Show(ShopSettings.Text("f05.failed")); }
        }

        private bool HasUndo(GameGridInventory grid) => started && !PeerSort && lastSort != null && lastSort.Affects(grid);
        private void UndoGrid(GameGridInventory grid) => Undo(grid);
        private bool Undo(GameGridInventory grid)
        {
            if (!HasUndo(grid)) return false;
            var plan = lastSort;
            var result = plan.Undo();
            if (result != InventorySortPlan.ApplyResult.Restored) lastSort = null;
            if (plan.LastError != null) log("Arrange undo: " + plan.LastError);
            ShopNotice.Show(ShopSettings.Text(result == InventorySortPlan.ApplyResult.Applied ? "f05.undone" :
                result == InventorySortPlan.ApplyResult.Restored ? "f05.undo_restored" :
                result == InventorySortPlan.ApplyResult.Inspect ? "f05.inspect" : "f05.undo_stale"));
            VisibleInventoryViews.NotifyChanged();
            return result == InventorySortPlan.ApplyResult.Applied;
        }

        [HarmonyLib.HarmonyPatch(typeof(InventorySortHelper), nameof(InventorySortHelper.MayUndo))]
        private static class MayUndoPatch
        {
            private static bool Prefix(GameGridInventory inventory, ref bool __result)
            {
                if (current?.HasUndo(inventory) != true) return true;
                __result = current.lastSort.CanUndo(); return false;
            }
        }
        [HarmonyLib.HarmonyPatch(typeof(InventorySortHelper), nameof(InventorySortHelper.CanUndo))]
        private static class CanUndoPatch
        {
            private static bool Prefix(GameGridInventory inventory, ref bool __result)
            {
                if (current?.HasUndo(inventory) != true) return true;
                __result = current.lastSort.CanUndo(); return false;
            }
        }
        [HarmonyLib.HarmonyPatch(typeof(InventorySortHelper), nameof(InventorySortHelper.Undo))]
        private static class UndoPatch
        {
            private static bool Prefix(GameGridInventory inventory, ref bool __result)
            {
                if (current?.HasUndo(inventory) != true) return true;
                __result = current.Undo(inventory); return false;
            }
        }
        [HarmonyLib.HarmonyPatch(typeof(InventorySortHelper), nameof(InventorySortHelper.Sort))]
        private static class NativeSortPatch
        {
            private static void Prefix(GameGridInventory inventory)
            {
                if (current?.HasUndo(inventory) == true) current.lastSort = null;
            }
        }

        private static void InvalidateMovedGrid(GameGridInventory grid)
        {
            if (!InventoryArrangeTransaction.IsApplying && grid != null && current?.lastSort?.Affects(grid) == true)
                current.lastSort = null;
        }

        [HarmonyLib.HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.Expel))]
        private static class NativeRemovePatch
        {
            private static void Postfix(GameGridInventory __instance, bool __result)
            { if (__result) InvalidateMovedGrid(__instance); }
        }

        [HarmonyLib.HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.UncheckedAccept))]
        private static class NativeAcceptPatch
        {
            private static void Postfix(GameGridInventory __instance, bool __result)
            { if (__result) InvalidateMovedGrid(__instance); }
        }

        [HarmonyLib.HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce))]
        private static class NativeTransferPatch
        {
            private sealed class Affected { internal GameGridInventory Source, Target; }
            private static void Prefix(SlotMarker __instance, out Affected __state)
            {
                __state = current?.lastSort == null || InventoryArrangeTransaction.IsApplying ? null : new Affected
                {
                    Source = __instance.item?.parentInventory as GameGridInventory,
                    Target = (__instance.targetItem?.parentInventory ?? __instance.inventory) as GameGridInventory
                };
            }
            private static void Postfix(int __result, Affected __state)
            {
                if (__result <= 0 || __state == null) return;
                InvalidateMovedGrid(__state.Source);
                InvalidateMovedGrid(__state.Target);
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(InventorySortHelper), "IsAnchored")]
        private static class RetainedSortAnchor
        {
            private static void Postfix(GameItem item, ref bool __result)
            {
                if (!__result && current?.started == true && current.holdEnabled.Value && current.IsHeld(item)) __result = true;
            }
        }

        private bool CanHold(GameItem item)
        {
            return started && holdEnabled.Value && item is GameItemElement element && ShopInventory.Movable(item) &&
                item.parentInventory is GameGridInventory source && Locations().Any(x => x.Inventory == source) &&
                VisibleInventoryViews.Visible(element);
        }

        private bool IsHeld(GameItem item) => item?.parentInventory is GameGridInventory source &&
            holds.TryGetValue(source, out Held held) && held.Items.Contains(item);

        private void ToggleHold(GameItem item)
        {
            if (!CanHold(item) || !(item.parentInventory is GameGridInventory source)) return;
            PixelWindow owner = source.handler.FindParentWindow();
            if (!holds.TryGetValue(source, out Held held) || !ReferenceEquals(held.Window, owner))
            {
                held = new Held { Window = owner };
                holds[source] = held;
            }
            if (!held.Items.Add(item)) held.Items.Remove(item);
            if (lastSort?.Affects(source) == true) lastSort = null;
            dirty = true;
        }

        private void PruneHolds()
        {
            foreach (GameGridInventory source in holds.Keys.ToArray())
            {
                Held held = holds[source];
                if (!holdEnabled.Value || !ShopInventory.InStore || source?.handler == null ||
                    !source.handler.gameObject.activeInHierarchy || !VisibleInventoryViews.Visible(held.Window) ||
                    source.handler.FindParentWindow() != held.Window)
                {
                    if (lastSort?.Affects(source) == true) lastSort = null;
                    holds.Remove(source);
                    dirty = true;
                    continue;
                }
                int removed = held.Items.RemoveWhere(item => item == null || item.parentInventory != source ||
                    !(item is GameItemElement element) || !VisibleInventoryViews.Visible(element));
                if (removed != 0) { if (lastSort?.Affects(source) == true) lastSort = null; dirty = true; }
                if (held.Items.Count == 0) holds.Remove(source);
            }
        }

        private void RefreshHolds()
        {
            drawn.Clear();
            foreach (Held held in holds.Values)
                foreach (GameItem item in held.Items)
                    if (item is GameItemElement element && VisibleInventoryViews.Visible(element) &&
                        holdOutlines.Show(element, ViewOutlineKind.Hold)) drawn.Add(element);
            holdOutlines.KeepOnly(drawn);
            dirty = false;
        }

        private void ClearHolds() { holds.Clear(); holdOutlines.Dispose(); drawn.Clear(); dirty = true; }

        private void Reset()
        {
            ClearHolds();
            lastSort = null;
            InventoryContextActions.Hide("f05.open");
            InventoryContextActions.Hide("f06.hold");
        }

        public void Dispose()
        {
            if (!started) return;
            if (ReferenceEquals(current, this)) current = null;
            InventoryArrangeTransaction.GridScope = null;
            started = false;
            InventoryContextActions.Unregister("f05.open");
            InventoryContextActions.Unregister("f05.all_grid");
            InventoryContextActions.Unregister("f05.preserve");
            InventoryContextActions.Unregister("f05.preserve_all_grid");
            InventoryContextActions.Unregister("f05.undo_grid");
            InventoryContextActions.Unregister("f05.item");
            InventoryContextActions.Unregister("f05.all_item");
            InventoryContextActions.Unregister("f05.preserve_item");
            InventoryContextActions.Unregister("f05.preserve_all_item");
            InventoryContextActions.Unregister("f05.undo_item");
            InventoryContextActions.ClearSortMenu();
            InventoryContextActions.Unregister("f06.hold");
            settings.Changed -= SettingsChanged;
            VisibleInventoryViews.Changed -= ViewsChanged;
            VisibleInventoryViews.ItemChanged -= ItemChanged;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
        }

    }
}
