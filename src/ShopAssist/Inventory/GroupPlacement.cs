using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    // The native selection and ghost renderer remain the owners of the gesture.
    // This adapter keeps its entire formation together and intercepts its non-atomic commit.
    internal static class GroupPlacement
    {
        private sealed class Snapshot
        {
            internal GameItemElement Item;
            internal GameGridInventory Source;
            internal GridShape Original, Pose;
            internal int Count;
        }

        private static Func<bool> preciseEnabled, rotationEnabled;
        private static Action<string> log;
        private static List<Snapshot> active;
        private static Dictionary<GameItemElement, Snapshot> activeByItem;
        private static PlayerStore owner;
        private static string run;
        private static int saveSlot;
        private static bool startedPrecise, startedRotation;
        private static readonly MethodInfo GetDelta = AccessTools.Method(typeof(ItemMultiSelectHandler), "TryGetPreciseDelta");
        private static readonly MethodInfo ValidFormation = AccessTools.Method(typeof(ItemMultiSelectHandler), "IsFormationValid");
        private static readonly FieldInfo SelectedItems = AccessTools.Field(typeof(ItemMultiSelectHandler), "selectedItems");

        internal static void Register(Func<bool> precise, Func<bool> rotation, Action<string> warning)
        {
            if (preciseEnabled != null || precise == null || rotation == null || warning == null)
                throw new InvalidOperationException("Group placement is already registered or invalid.");
            preciseEnabled = precise; rotationEnabled = rotation; log = warning;
            ModHook.OnGameLoadedInit += ResetSession;
            ModHook.OnLeavingStoreEarly += ResetSession;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        internal static void Unregister()
        {
            ModHook.OnGameLoadedInit -= ResetSession;
            ModHook.OnLeavingStoreEarly -= ResetSession;
            SceneManager.activeSceneChanged -= SceneChanged;
            ResetSession();
            Forget(); preciseEnabled = null; rotationEnabled = null; log = null;
        }

        private static bool PreciseEnabled => preciseEnabled?.Invoke() == true;
        private static bool RotationEnabled => rotationEnabled?.Invoke() == true;
        internal static bool AutomaticPlacementAllowed => !PreciseEnabled &&
            !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift);
        private static bool SameGesture => active != null && ShopInventory.InStore &&
            ReferenceEquals(owner, PlayerStore.Instance) && PlayerStore.Instance.runID == run &&
            PlayerStore.Instance.saveSlotId == saveSlot && startedPrecise == PreciseEnabled && startedRotation == RotationEnabled;
        private static void Forget() { active = null; activeByItem = null; owner = null; }
        private static void SceneChanged(Scene previous, Scene current) => ResetSession();
        private static void ResetSession()
        {
            if (active == null) return;
            try
            {
                ItemMultiSelectHandler handler = ItemMultiSelectHandler.current;
                if (handler == null) { Forget(); return; }
                var selection = SelectedItems?.GetValue(handler) as List<GameItemElement>;
                Cancel(handler, selection);
            }
            catch (Exception error) { Forget(); log?.Invoke("Could not clear a group drag during scene change: " + error); }
        }

        private static void Cancel(ItemMultiSelectHandler handler, List<GameItemElement> selection)
        {
            try
            {
                // Native cancellation destroys detached objects; leave them intact for inspection.
                if (active != null && selection != null)
                    foreach (Snapshot part in active)
                        if (part.Item != null && part.Item.parentInventory == null) selection.Remove(part.Item);
                handler?.Cancel();
            }
            finally { Forget(); }
        }

        private static bool Ordinary(GameGridInventory inventory) => inventory != null && ShopInventory.InStore &&
            ShopInventory.OwnedLocations(false).Any(location => location.Ordinary && ReferenceEquals(location.Inventory, inventory));

        private static ShopInventory.Location Ordinary(GameGridInventory inventory, List<ShopInventory.Location> locations) =>
            inventory == null ? null : locations.Find(location => location.Ordinary && ReferenceEquals(location.Inventory, inventory));

        private static bool Begin(IReadOnlyList<GameItemElement> selected)
        {
            Forget();
            bool precise = PreciseEnabled, rotation = RotationEnabled;
            if ((!precise && !rotation) || !ShopInventory.InStore) return true;
            // Unadapted inventories and mixed-source selections keep the game's own behavior.
            if (selected == null || selected.Count < 2 || !(selected[0]?.parentInventory is GameGridInventory source) ||
                selected.Any(item => item == null || item.parentInventory != source)) return true;
            var locations = ShopInventory.OwnedLocations(false);
            var sourceLocation = Ordinary(source, locations);
            if (sourceLocation == null) return true;
            if (selected.Any(item => item.IsDestroyed() || item.modifiedShape == null ||
                    !source.childItems.Contains(item) || !ShopInventory.Movable(item) || item.MaxNumRemove() < item.unitCount) ||
                selected.Any(RequestBook.ReservedItems(new[] { sourceLocation }).Contains))
            {
                // F40 only extends same-grid formations; unrelated native drags still work.
                if (!precise) return true;
                ShopNotice.Show(ShopSettings.Text("group.unavailable"));
                return false;
            }
            var snapshots = selected.Select(item => new Snapshot { Item = item, Source = source, Original = item.modifiedShape.Clone(),
                Pose = item.modifiedShape.Clone(), Count = item.unitCount }).ToList();
            var byItem = new Dictionary<GameItemElement, Snapshot>();
            foreach (Snapshot part in snapshots)
            {
                if (!byItem.ContainsKey(part.Item)) { byItem.Add(part.Item, part); continue; }
                if (!precise) return true;
                ShopNotice.Show(ShopSettings.Text("group.unavailable"));
                return false;
            }
            active = snapshots;
            activeByItem = byItem;
            owner = PlayerStore.Instance; run = owner.runID; saveSlot = owner.saveSlotId;
            startedPrecise = precise; startedRotation = rotation;
            return true;
        }

        private static bool Current(List<ShopInventory.Location> locations)
        {
            if (!SameGesture || active.Count < 2) return false;
            var source = active[0].Source;
            var sourceLocation = Ordinary(source, locations);
            if (sourceLocation == null) return false;
            foreach (Snapshot snapshot in active)
                if (snapshot.Item == null || snapshot.Item.IsDestroyed() || snapshot.Item.parentInventory != snapshot.Source ||
                    snapshot.Item.unitCount != snapshot.Count || !ItemGridMove.SameShape(snapshot.Item.modifiedShape, snapshot.Original) ||
                    !snapshot.Source.childItems.Contains(snapshot.Item) || !ShopInventory.Movable(snapshot.Item) ||
                    snapshot.Item.MaxNumRemove() < snapshot.Count) return false;
            var reserved = RequestBook.ReservedItems(new[] { sourceLocation });
            foreach (Snapshot snapshot in active)
                if (snapshot.Item.parentInventory != snapshot.Source || !snapshot.Source.childItems.Contains(snapshot.Item) ||
                    snapshot.Item.unitCount != snapshot.Count || !ItemGridMove.SameShape(snapshot.Item.modifiedShape, snapshot.Original) ||
                    reserved.Contains(snapshot.Item)) return false;
            return true;
        }

        private static GridShape At(Snapshot snapshot, int dx, int dy) => new GridShapeBuilder(snapshot.Pose)
            .SetPosition(snapshot.Pose.minX + dx, snapshot.Pose.minY + dy).Build();

        private static void Rotate(bool ccw, Dictionary<GameItemElement, Vector2> offsets)
        {
            if (active == null || offsets == null || !Current(ShopInventory.OwnedLocations(false))) return;
            List<GridShape> next = GroupGeometry.Rotate(active.Select(part => part.Pose).ToList(),
                active[0].Original.minX, active[0].Original.minY, ccw);
            for (int i = 0; i < active.Count; i++)
            {
                Snapshot part = active[i];
                part.Pose = next[i];
                if (offsets.TryGetValue(part.Item, out Vector2 offset))
                    offsets[part.Item] = ccw ? new Vector2(-offset.y, offset.x) : new Vector2(offset.y, -offset.x);
                if (ccw) part.Item.RotCCW(); else part.Item.RotCW();
            }
        }

        private static bool Legal(GameGridInventory target, int dx, int dy)
        {
            if (!SameGesture || target == null) return false;
            var locations = ShopInventory.OwnedLocations(false);
            if (!Current(locations) || Ordinary(target, locations) == null || target.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(target)) return false;
            var ignored = active.Select(part => (GameItem)part.Item).ToList();
            var poses = new List<GridShape>();
            foreach (Snapshot part in active)
            {
                GridShape pose = At(part, dx, dy);
                if (ShopInventory.WouldContainSelf(part.Item, target) || !target.CheckShapePlacement(part.Item, pose, ignored) ||
                    poses.Any(previous => previous.Intersects(pose))) return false;
                if (target != part.Source && (!target.MayHaveValidInventorySlot(part.Item) || GridPlacements.Exact(target, part.Item, pose) == null)) return false;
                poses.Add(pose);
            }
            return true;
        }

        private static bool Commit(GameGridInventory target, int dx, int dy)
        {
            if (!Legal(target, dx, dy)) return false;
            GameGridInventory source = active[0].Source;
            if (source == target)
            {
                try
                {
                    // Remove the entire old formation first so rotations can overlap its former cells.
                    // Expel/UncheckedAccept invoke the same inventory callbacks as a native move.
                    foreach (Snapshot part in active)
                        if (part.Item.parentInventory != source || part.Item.unitCount != part.Count ||
                            !ItemGridMove.SameShape(part.Item.modifiedShape, part.Original) || !ShopInventory.Movable(part.Item) ||
                            RequestBook.Reserved(part.Item) || !source.Expel(part.Item) || part.Item.parentInventory != null ||
                            source.childItems.Contains(part.Item) || part.Item.unitCount != part.Count)
                            throw new InvalidOperationException("Native inventory rejected a group removal");
                    foreach (Snapshot part in active)
                    {
                        GridShape pose = At(part, dx, dy);
                        if (GridPlacements.Exact(source, part.Item, pose) == null) throw new InvalidOperationException("Group destination changed");
                        part.Item.modifiedShape = new GridShapeBuilder(part.Item.modifiedShape).SetTransform(pose);
                        if (!source.UncheckedAccept(part.Item) || part.Item.parentInventory != source ||
                            !source.childItems.Contains(part.Item) || part.Item.unitCount != part.Count ||
                            !ItemGridMove.SameShape(part.Item.modifiedShape, pose)) throw new InvalidOperationException("Native inventory rejected a group insertion");
                    }
                    source.Validate();
                    if (active.All(part => part.Item.parentInventory == source && part.Item.unitCount == part.Count &&
                        source.childItems.Contains(part.Item) && ItemGridMove.SameShape(part.Item.modifiedShape, At(part, dx, dy)))) return true;
                }
                catch (Exception error) { log?.Invoke("Group placement interrupted: " + error); }
                try
                {
                    // Clear already-inserted new poses before restoring the original formation.
                    foreach (Snapshot part in active)
                        if (part.Item.parentInventory == source && !ItemGridMove.SameShape(part.Item.modifiedShape, part.Original))
                            source.Expel(part.Item);
                    foreach (Snapshot part in active)
                    {
                        if (part.Item.parentInventory != null || part.Item.unitCount != part.Count) continue;
                        SlotMarker slot = GridPlacements.Exact(source, part.Item, part.Original);
                        if (slot != null) slot.TryAcceptOnce(part.Count);
                    }
                    source.Validate();
                }
                catch (Exception error) { log?.Invoke("Group placement recovery interrupted: " + error); }
                return false;
            }
            var moved = new List<Snapshot>();
            bool failed = false;
            foreach (Snapshot part in active)
            {
                if (part.Item.parentInventory != source || part.Item.unitCount != part.Count ||
                    !ItemGridMove.SameShape(part.Item.modifiedShape, part.Original) ||
                    !ShopInventory.Movable(part.Item) || RequestBook.Reserved(part.Item) ||
                    !ItemGridMove.TryMove(part.Item, target, At(part, dx, dy), out bool restored))
                { failed = true; break; }
                moved.Add(part);
            }
            if (!failed) return true;
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                Snapshot part = moved[i];
                if (!ItemGridMove.TryMove(part.Item, source, part.Original, out _))
                    log?.Invoke("A group item could not be returned to its original cell.");
            }
            return false;
        }

        private static bool Finish(ItemMultiSelectHandler handler, List<GameItemElement> selection)
        {
            if (active == null) return true;
            if (!SameGesture) { Cancel(handler, selection); return false; }
            try
            {
                GameItem hit = RenderHandler.RaycastElement<GameItem>(Input.mousePosition);
                if (hit is GameItemElement selected && selection.Contains(selected)) hit = null;
                var target = hit == null ? RenderHandler.RaycastFindParentElement<GameGridInventory>(Input.mousePosition) : null;
                bool nativePrecise = target != null && target.SupportsGroupPlacement &&
                    (ReferenceEquals(target, active[0].Source) || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
                if (!PreciseEnabled && !nativePrecise)
                {
                    // Native automatic placement uses modifiedShape and chooses its own orientation.
                    // Leave that entire transaction, including stacking and callbacks, to the game.
                    Forget();
                    return true;
                }
                object[] args = { target, 0, 0 };
                bool possible = target != null && GetDelta != null && ValidFormation != null &&
                    (bool)GetDelta.Invoke(handler, args) && Legal(target, (int)args[1], (int)args[2]) &&
                    (bool)ValidFormation.Invoke(handler, args);
                bool success = possible && Commit(target, (int)args[1], (int)args[2]);
                if (!success)
                {
                    bool intact = active.All(part => part.Item.parentInventory == part.Source && part.Item.unitCount == part.Count &&
                        ItemGridMove.SameShape(part.Item.modifiedShape, part.Original));
                    ShopNotice.Show(ShopSettings.Text(intact ? "group.cancelled" : "group.inspect"));
                }
            }
            catch (Exception error)
            {
                log?.Invoke("Group drop failed: " + error);
                ShopNotice.Show(ShopSettings.Text("group.inspect"));
            }
            finally { if (active != null) Cancel(handler, selection); }
            return false;
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "StartGroupDrag")]
        private static class BeginPatch
        {
            private static bool Prefix(ItemMultiSelectHandler __instance, List<GameItemElement> ___selectedItems)
            {
                if (Begin(___selectedItems)) return true;
                __instance.Cancel(); return false;
            }
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "OnEventUpdate")]
        private static class RotatePatch
        {
            private static void Prefix(ItemMultiSelectHandler __instance, Dictionary<GameItemElement, Vector2> ___dragOffsets,
                List<GameItemElement> ___selectedItems)
            {
                if (active == null) return;
                if (!__instance.IsGroupDragging) { Forget(); return; }
                if (!SameGesture) { Cancel(__instance, ___selectedItems); return; }
                if (!RotationEnabled) return;
                ItemMouseDragHandler keys = ItemMouseDragHandler.current;
                if (keys == null) return;
                float wheel = Input.mouseScrollDelta.y;
                bool cw = Input.GetKeyDown(keys.rotateCWKey) || wheel < 0;
                bool ccw = Input.GetKeyDown(keys.rotateCCWKey) || wheel > 0;
                if (cw != ccw) Rotate(ccw, ___dragOffsets);
            }
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "TranslatedShape")]
        private static class ShapePatch
        {
            private static bool Prefix(GameItemElement element, int deltaX, int deltaY, ref GridShape __result)
            {
                if (element == null || activeByItem == null || !activeByItem.TryGetValue(element, out Snapshot part)) return true;
                __result = At(part, deltaX, deltaY); return false;
            }
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "IsPreciseModifierHeld")]
        private static class PrecisePatch
        {
            private static void Postfix(ref bool __result) { if (active != null && PreciseEnabled) __result = true; }
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "IsFormationValid")]
        private static class ValidPatch
        {
            private static void Postfix(GameInventory inventory, int deltaX, int deltaY, ref bool __result)
            { if (active != null) __result = __result && Legal(inventory as GameGridInventory, deltaX, deltaY); }
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "UpdateDropTarget")]
        private static class TargetPatch
        {
            private static void Postfix(GameItem ___hoverItem, GameInventory ___hoverInventory,
                List<TreeNodeRender> ___hoverHighlightNodes, List<TreeNodeRender> ___hoverInventoryNodes)
            {
                if (active == null || !PreciseEnabled) return;
                if (___hoverItem != null)
                {
                    foreach (TreeNodeRender node in ___hoverHighlightNodes) if (node?.image != null) node.image.enabled = false;
                    ___hoverHighlightNodes.Clear();
                    List<TreeNodeRender> red = ___hoverItem.parentInventory?.HighlightSlot(___hoverItem.GetIncompleteSlotInInventory(),
                        RenderHandler.BackgroundColor.Red, doReplace: true);
                    if (red != null) ___hoverHighlightNodes.AddRange(red);
                }
                else if (___hoverInventory != null && !Ordinary(___hoverInventory as GameGridInventory))
                {
                    foreach (TreeNodeRender node in ___hoverInventoryNodes) if (node?.image != null) node.image.enabled = false;
                    ___hoverInventoryNodes.Clear();
                    List<TreeNodeRender> red = ___hoverInventory.HighlightWholeInventory(RenderHandler.BackgroundColor.Red);
                    if (red != null) ___hoverInventoryNodes.AddRange(red);
                }
            }
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "EndGroupDrag")]
        private static class FinishPatch
        {
            private static bool Prefix(ItemMultiSelectHandler __instance, List<GameItemElement> ___selectedItems) => Finish(__instance, ___selectedItems);
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "OnEventReset")]
        private static class ResetPatch
        {
            private static void Postfix() { Forget(); }
        }
    }
}
