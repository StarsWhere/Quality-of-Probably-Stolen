using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly ConfigEntry<bool> sortEnabled, holdEnabled, rotate;
        private readonly Func<KeyCode> key;
        private readonly Dictionary<GameGridInventory, Held> holds = new Dictionary<GameGridInventory, Held>();
        private readonly ViewOutlines holdOutlines = new ViewOutlines("ShopAssist_HoldOutline");
        private readonly HashSet<GameItemElement> drawn = new HashSet<GameItemElement>();
        private bool started, dirty = true;
        private static bool PeerSort => ShopPeers.Running("smart_inventory_arrange");

        internal InventoryOrganizer(ShopSettings settings, Action<string> log)
        {
            this.settings = settings;
            this.log = log;
            sortEnabled = settings.Toggle("f05.enabled", "f05.tip", true);
            holdEnabled = settings.Toggle("f06.enabled", "f06.tip");
            rotate = settings.Toggle("f05.rotate", "f05.rotate.tip", true);
            key = settings.Key("f05.key", "f05.key.tip");
            settings.Action("f05.open", () => Sort(null), CanOpen);
        }
        public void Start()
        {
            if (started) return;
            current = this;
            InventoryContextActions.RegisterGrid("f05.open", source => ShopSettings.Text("f05.open"),
                source => started && sortEnabled.Value && !PeerSort && Locations().Any(l => l.Inventory == source), Sort);
            InventoryContextActions.RegisterItem("f06.hold", item => ShopSettings.Text(IsHeld(item) ? "f06.release" : "f06.hold"),
                CanHold, ToggleHold);
            started = true;
            settings.Changed += SettingsChanged;
            VisibleInventoryViews.Changed += ViewsChanged;
            VisibleInventoryViews.ItemChanged += ItemChanged;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }
        private bool CanOpen() => started && sortEnabled.Value && !PeerSort && ShopInventory.InStore && Locations().Count != 0;
        public void Tick()
        {
            if (!started) return;
            PruneHolds();
            if (dirty) RefreshHolds();
            KeyCode hotkey = key();
            if (hotkey != KeyCode.None && Input.GetKeyDown(hotkey) && ShopInput.Ready())
                Sort(RenderHandler.RaycastFindParentElement<GameGridInventory>(Input.mousePosition));
        }
        private void SettingsChanged()
        {
            if (!sortEnabled.Value) InventoryContextActions.Hide("f05.open");
            if (!holdEnabled.Value) { ClearHolds(); InventoryContextActions.Hide("f06.hold"); }
            dirty = true;
        }
        private void ViewsChanged() { PruneHolds(); dirty = true; }
        private void ItemChanged(GameItemElement item) { PruneHolds(); dirty = true; }
        private void SceneChanged(Scene before, Scene after) => Reset();
        private static List<ShopInventory.Location> Locations() => ShopInventory.OwnedLocations(true)
            .Where(l => l.Ordinary && l.Inventory is GameGridInventory && !l.Inventory.IsInsertLocked() &&
                !l.Inventory.IsRemoveLocked() && GeneralHelper.MayPlayerInsertInto(l.Inventory)).ToList();

        private void Sort(GameGridInventory requested)
        {
            if (!started || !sortEnabled.Value || PeerSort || !ShopInventory.InStore) return;
            var locations = Locations();
            var grid = requested ?? locations.OrderByDescending(l => (l.Inventory as GameGridInventory)?.handler?.FindParentWindow()?.focusStamp ?? 0)
                .Select(l => l.Inventory as GameGridInventory).FirstOrDefault();
            if (grid == null || !locations.Any(l => l.Inventory == grid)) return;
            PruneHolds();
            try
            {
                var selected = ShopSelection.Current().Where(i => i.parentInventory == grid).ToHashSet();
                ISet<GameItem> keep = holds.TryGetValue(grid, out Held held) ? held.Items : null;
                var plan = InventorySortPlan.Create(grid, selected, keep, true, rotate.Value);
                if (plan == null) { ShopNotice.Show(ShopSettings.Text("f05.no_plan")); return; }
                int moved = plan.Placements.Count(p => !InventorySortPlan.SameShape(p.Before, p.After));
                var result = plan.Apply();
                if (plan.LastError != null) log("Smart arrange: " + plan.LastError);
                ShopNotice.Show(result == InventorySortPlan.ApplyResult.Applied ? ShopSettings.Format("f05.applied", moved) :
                    ShopSettings.Text(result == InventorySortPlan.ApplyResult.Restored ? "f05.restored" :
                        result == InventorySortPlan.ApplyResult.Inspect ? "f05.inspect" : "f05.changed"));
                VisibleInventoryViews.NotifyChanged();
            }
            catch (Exception error) { log("Smart arrange failed: " + error); ShopNotice.Show(ShopSettings.Text("f05.failed")); }
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
                    holds.Remove(source);
                    dirty = true;
                    continue;
                }
                int removed = held.Items.RemoveWhere(item => item == null || item.parentInventory != source ||
                    !(item is GameItemElement element) || !VisibleInventoryViews.Visible(element));
                if (removed != 0) { dirty = true; }
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
            InventoryContextActions.Hide("f05.open");
            InventoryContextActions.Hide("f06.hold");
        }

        public void Dispose()
        {
            if (!started) return;
            if (ReferenceEquals(current, this)) current = null;
            started = false;
            InventoryContextActions.Unregister("f05.open");
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
