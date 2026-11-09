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
        private readonly ShopSettings settings;
        private readonly Action<string> log;
        private readonly ConfigEntry<bool> sortEnabled, rotate, keepMenu, autoInsert;
        private readonly Func<KeyCode> key;
        private bool started;
        private IInventoryArrangeUndo lastSort;
        private static readonly FieldInfo NativeUndo = typeof(InventorySortHelper).GetField("undoSnapshots", BindingFlags.Static | BindingFlags.NonPublic);

        internal InventoryOrganizer(ShopSettings settings, Action<string> log)
        {
            this.settings = settings;
            this.log = log;
            sortEnabled = settings.Toggle("f05.enabled", "f05.tip", true);
            rotate = settings.Toggle("f05.rotate", "f05.rotate.tip", true);
            keepMenu = settings.Toggle("f05.keep_menu", "f05.keep_menu.tip", true);
            autoInsert = settings.Toggle("f05.auto_insert", "f05.auto_insert.tip");
            key = settings.Key("f05.key", "f05.key.tip");
            settings.Action("f05.open", () => Sort(null), CanOpen);
        }
        public void Start()
        {
            if (started) return;
            current = this;
            InventoryContextActions.RegisterGrid("f05.open", SortLabel, CanSort, Sort);
            InventoryContextActions.RegisterItem("f05.item", item => SortLabel(item?.parentInventory as GameGridInventory),
                item => CanSort(item?.parentInventory as GameGridInventory), item => Sort(item.parentInventory as GameGridInventory));
            InventoryContextActions.RegisterItem("f05.undo_item", item => ShopSettings.Text("f05.undo"),
                item => HasUndo(item?.parentInventory as GameGridInventory) && lastSort.CanUndo(),
                item => Undo(item.parentInventory as GameGridInventory));
            started = true;
            InventoryArrangeTransaction.GridScope = GridScope;
            InventoryContextActions.ConfigureSortMenu(() => started && keepMenu.Value, CanSort);
            settings.Changed += SettingsChanged;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }
        private bool CanOpen() => started && sortEnabled.Value && ShopInventory.InStore && Locations().Count != 0;
        private bool CanSort(GameGridInventory grid) => started && sortEnabled.Value && grid != null &&
            Locations().Any(location => ReferenceEquals(location.Inventory, grid));
        private static string SortLabel(GameGridInventory grid)
        {
            return ShopSettings.Text(HasSelection(grid) ? "f05.selected" : "f05.all");
        }
        private static bool HasSelection(GameGridInventory grid) => grid != null &&
            ShopSelection.Current().Any(item => ReferenceEquals(item.parentInventory, grid));
        public void Tick()
        {
            if (!started) return;
            KeyCode hotkey = key();
            if (hotkey != KeyCode.None && Input.GetKeyDown(hotkey) && ShopInput.Ready())
                Sort(RenderHandler.RaycastFindParentElement<GameGridInventory>(Input.mousePosition));
        }
        private void SettingsChanged()
        {
            if (!sortEnabled.Value)
            {
                lastSort = null;
                foreach (string id in new[] { "f05.open", "f05.item", "f05.undo_item" }) InventoryContextActions.Hide(id);
                InventoryContextActions.DismissSortMenu();
            }
        }
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

        internal static bool AutomaticEnabled => current?.started == true && current.sortEnabled.Value && current.autoInsert.Value && ShopInventory.InStore;
        internal static bool AutomaticGrid(GameGridInventory grid) => AutomaticEnabled && grid != null &&
            Locations().Any(location => ReferenceEquals(location.Inventory, grid) && ShopInventory.IsMainStockBranch(location));
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

        private void Sort(GameGridInventory requested)
        {
            if (!started || !sortEnabled.Value || !ShopInventory.InStore) return;
            var locations = Locations();
            var grid = requested ?? locations.OrderByDescending(l => (l.Inventory as GameGridInventory)?.handler?.FindParentWindow()?.focusStamp ?? 0)
                .Select(l => l.Inventory as GameGridInventory).FirstOrDefault();
            if (grid == null || !locations.Any(l => l.Inventory == grid)) return;
            try
            {
                if (ShopSelection.ContextChanged(grid)) { ShopNotice.Show(ShopSettings.Text("f05.changed")); return; }
                var selected = ShopSelection.Current().Where(i => i.parentInventory == grid).ToHashSet();
                var plan = InventorySortPlan.Create(grid, selected, true, rotate.Value);
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

        private bool HasUndo(GameGridInventory grid) => started && lastSort != null && lastSort.Affects(grid);
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

        private void Reset()
        {
            lastSort = null;
            InventoryContextActions.Hide("f05.open");
            InventoryContextActions.Hide("f05.item");
            InventoryContextActions.Hide("f05.undo_item");
        }

        public void Dispose()
        {
            if (!started) return;
            if (ReferenceEquals(current, this)) current = null;
            InventoryArrangeTransaction.GridScope = null;
            started = false;
            InventoryContextActions.Unregister("f05.open");
            InventoryContextActions.Unregister("f05.item");
            InventoryContextActions.Unregister("f05.undo_item");
            InventoryContextActions.ClearSortMenu();
            settings.Changed -= SettingsChanged;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
        }

    }
}
