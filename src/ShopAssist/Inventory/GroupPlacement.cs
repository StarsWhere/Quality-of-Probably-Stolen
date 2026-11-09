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
        private static readonly FieldInfo DragOffsets = AccessTools.Field(typeof(ItemMultiSelectHandler), "dragOffsets");

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

        private static bool MainStock(GameGridInventory inventory) => inventory != null && ShopInventory.InStore &&
            ReferenceEquals(inventory, EmporiumEntry.Instance.invElement);

        private static bool DirectStockContainer(GameGridInventory inventory)
        {
            if (inventory == null || !ShopInventory.InStore) return false;
            GameGridInventory stock = EmporiumEntry.Instance.invElement;
            GameItem container = inventory.GetParentItem();
            if (stock == null || stock.IsRemoveLocked() || container == null || !ReferenceEquals(container.parentInventory, stock)) return false;
            bool present = false;
            foreach (GameItem child in stock.childItems)
                if (ReferenceEquals(child, container)) { present = true; break; }
            return present && ShopInventory.SafeContainerForPeek(container) &&
                ReferenceEquals(container.contentWindow.childElement, inventory);
        }

        private static bool DirectOrdinary(GameGridInventory inventory) => MainStock(inventory) || DirectStockContainer(inventory);

        private static bool Ordinary(GameGridInventory inventory, List<ShopInventory.Location> locations = null)
        {
            if (inventory == null) return false;
            if (locations == null && DirectOrdinary(inventory)) return true;
            return (locations ?? ShopInventory.OwnedLocations(false))
                .Any(location => location.Ordinary && ReferenceEquals(location.Inventory, inventory));
        }

        private static bool Begin(IReadOnlyList<GameItemElement> selected)
        {
            Forget();
            bool precise = PreciseEnabled, rotation = RotationEnabled;
            if ((!precise && !rotation) || !ShopInventory.InStore) return true;
            // Unadapted inventories and mixed-source selections keep the game's own behavior.
            if (selected == null || selected.Count < 2 || !(selected[0]?.parentInventory is GameGridInventory source) ||
                selected.Any(item => item == null || item.parentInventory != source)) return true;
            if (!Ordinary(source)) return true;
            if (selected.Any(item => item.IsDestroyed() || item.modifiedShape == null ||
                    !source.childItems.Contains(item) || !ShopInventory.Movable(item) || item.MaxNumRemove() < item.unitCount))
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
            if (!Ordinary(source, locations)) return false;
            foreach (Snapshot snapshot in active)
                if (snapshot.Item == null || snapshot.Item.IsDestroyed() || snapshot.Item.parentInventory != snapshot.Source ||
                    snapshot.Item.unitCount != snapshot.Count || !ItemGridMove.SameShape(snapshot.Item.modifiedShape, snapshot.Original) ||
                    !snapshot.Source.childItems.Contains(snapshot.Item) || !ShopInventory.Movable(snapshot.Item) ||
                    snapshot.Item.MaxNumRemove() < snapshot.Count) return false;
            return true;
        }

        private static GridShape At(Snapshot snapshot, int dx, int dy) => new GridShapeBuilder(snapshot.Pose)
            .SetPosition(snapshot.Pose.minX + dx, snapshot.Pose.minY + dy).Build();

        private static void Rotate(bool ccw, Dictionary<GameItemElement, Vector2> offsets)
        {
            if (active == null || offsets == null || !Current(null) ||
                !InventoryAutoInsert.KnownMutationPatches() || active.Any(part =>
                    !InventoryAutoInsert.SafeItem(part.Item, true) || !InventoryAutoInsert.SafeTransferInventory(part.Source)) ||
                !InventoryAutoInsert.KnownPatches(AccessTools.Method(typeof(GameItemElement), nameof(GameItemElement.RotCW))) ||
                !InventoryAutoInsert.KnownPatches(AccessTools.Method(typeof(GameItemElement), nameof(GameItemElement.RotCCW))) ||
                !InventoryAutoInsert.KnownPatches(AccessTools.Method(typeof(GameItemElement), "GridRotCW")) ||
                !InventoryAutoInsert.KnownPatches(AccessTools.Method(typeof(GameItemElement), "GridRotCCW"))) return;
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
            if (!SameGesture || target == null || active.Count < 2) return false;
            var locations = DirectOrdinary(active[0].Source) && DirectOrdinary(target) ? null : ShopInventory.OwnedLocations(false);
            if (!Current(locations) || !Ordinary(target, locations) || target.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(target)) return false;
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
                            !source.Expel(part.Item) || part.Item.parentInventory != null ||
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
                    !ShopInventory.Movable(part.Item) ||
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
        private static class DragSessionPatch
        {
            private static void Prefix(ItemMultiSelectHandler __instance, Dictionary<GameItemElement, Vector2> ___dragOffsets,
                List<GameItemElement> ___selectedItems)
            {
                if (active == null) return;
                if (!__instance.IsGroupDragging) { Forget(); return; }
                if (!SameGesture) { Cancel(__instance, ___selectedItems); return; }
            }
        }

        private static bool CanReceiveRotation(ItemMultiSelectHandler handler) => active != null && RotationEnabled &&
            SameGesture && handler != null && handler.IsGroupDragging && ReferenceEquals(handler, ItemMultiSelectHandler.current);
        private static bool SwapNativeRotation => EmporiumOption.Instance != null && EmporiumOption.Instance.swapDragRotateFlip;
        private static void ApplyRotation(GroupRotationInput.Turn turn, Dictionary<GameItemElement, Vector2> offsets)
        {
            // Native presses apply clockwise first and counterclockwise second,
            // even if both bindings are pressed in the same input dispatch.
            if ((turn & GroupRotationInput.Turn.Clockwise) != 0) Rotate(false, offsets);
            if ((turn & GroupRotationInput.Turn.Counterclockwise) != 0) Rotate(true, offsets);
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "get_keyListeners")]
        private static class RotationKeysPatch
        {
            private static void Postfix(ItemMultiSelectHandler __instance, ref List<KeyCode> __result)
            {
                ItemMouseDragHandler keys = ItemMouseDragHandler.current;
                if (!CanReceiveRotation(__instance) || keys == null || __result == null) return;
                // InputActionManager only dispatches pressed keys registered by
                // this handler. Read the single-item handler's live bindings.
                foreach (KeyCode key in new[] { keys.rotateCWKey, keys.rotateCCWKey, keys.secondaryKey })
                    if (key != KeyCode.None && !__result.Contains(key)) __result.Add(key);
            }
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "OnEventPress")]
        private static class RotationPressPatch
        {
            private static void Prefix(ItemMultiSelectHandler __instance, ISet<KeyCode> keysPressed,
                Dictionary<GameItemElement, Vector2> ___dragOffsets)
            {
                ItemMouseDragHandler keys = ItemMouseDragHandler.current;
                if (!CanReceiveRotation(__instance) || keys == null) return;
                ApplyRotation(GroupRotationInput.Press(keysPressed, keys.rotateCWKey, keys.rotateCCWKey,
                    keys.secondaryKey, SwapNativeRotation), ___dragOffsets);
            }
        }

        // ItemMultiSelectHandler inherits this empty event implementation. The
        // native dispatcher has already checked initialization and enabled state.
        [HarmonyPatch(typeof(EmptyActionHandler), "OnEventMouseScroll")]
        private static class RotationScrollPatch
        {
            private static void Postfix(EmptyActionHandler __instance, Vector2 scrollDelta)
            {
                if (!(__instance is ItemMultiSelectHandler handler) || !CanReceiveRotation(handler)) return;
                ItemMouseDragHandler keys = ItemMouseDragHandler.current;
                if (keys == null || DragOffsets == null) return;
                // Native stack scrolling changes quantity, not orientation. A
                // group keeps complete stacks, so do not reinterpret that input.
                bool ignore = Input.GetKey(keys.scrollIgnoreKey) || active.Any(part => part.Count > 1);
                ApplyRotation(GroupRotationInput.Scroll(scrollDelta, SwapNativeRotation, ignore),
                    DragOffsets.GetValue(handler) as Dictionary<GameItemElement, Vector2>);
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

    internal static class GroupRotationInput
    {
        [Flags] internal enum Turn { None = 0, Clockwise = 1, Counterclockwise = 2 }
        internal static Turn Press(ISet<KeyCode> pressed, KeyCode clockwise, KeyCode counterclockwise,
            KeyCode secondary, bool swap)
        {
            if (pressed == null) return Turn.None;
            Turn turn = Turn.None;
            if (pressed.Contains(clockwise) || swap && pressed.Contains(secondary)) turn |= Turn.Clockwise;
            if (pressed.Contains(counterclockwise)) turn |= Turn.Counterclockwise;
            return turn;
        }
        internal static Turn Scroll(Vector2 delta, bool swap, bool ignore)
        {
            // Match ItemMouseDragHandler: combine both axes and truncate before
            // checking direction. Swapped wheel gestures mean flip, not rotate.
            if (swap || ignore) return Turn.None;
            int amount = (int)(delta.x + delta.y);
            return amount > 0 ? Turn.Counterclockwise : amount < 0 ? Turn.Clockwise : Turn.None;
        }
    }
}
