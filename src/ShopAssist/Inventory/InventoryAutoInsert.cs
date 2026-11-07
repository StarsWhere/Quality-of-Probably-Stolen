using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    // Native drop remains in charge. Only a fully captured, reversible remainder
    // can cause an automatic arrangement after the native attempt.
    internal static class InventoryAutoInsert
    {
        private sealed class Request
        {
            internal InventoryArrangeTransaction Transaction;
            internal GameGridInventory Target;
            internal GameItem[] Items;
            internal readonly Dictionary<GameItem, GridShape> Poses = new Dictionary<GameItem, GridShape>();
        }

        private static readonly MethodBase[] NativeMutations = {
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.UncheckedAccept)),
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.Expel)),
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.Validate)),
            AccessTools.Method(typeof(GameSlotInventory), nameof(GameSlotInventory.UncheckedAccept)),
            AccessTools.Method(typeof(GameSlotInventory), nameof(GameSlotInventory.Expel)),
            AccessTools.Method(typeof(GameSlotInventory), nameof(GameSlotInventory.Validate)),
            AccessTools.Method(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce)),
            AccessTools.Method(typeof(SlotMarker), "StackItemUnchecked"),
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.TryFindOneValidInventorySlot), new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.TryFindOneValidInventorySlot), new[] { typeof(GameItem), typeof(bool) }),
            AccessTools.Method(typeof(GameSlotInventory), nameof(GameSlotInventory.TryFindOneValidInventorySlot), new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.MayHaveValidInventorySlot), new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(GameSlotInventory), nameof(GameSlotInventory.MayHaveValidInventorySlot), new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.TryInventorySlot), new[] { typeof(GameItem), typeof(int), typeof(Vector2), typeof(GridShape), typeof(TagSystem) }),
            AccessTools.Method(typeof(GameSlotInventory), nameof(GameSlotInventory.TryInventorySlot), new[] { typeof(GameItem), typeof(int), typeof(Vector2), typeof(GridShape), typeof(TagSystem) }),
            AccessTools.Method(typeof(GameInventory), nameof(GameInventory.TryInventorySlot), new[] { typeof(GameItem), typeof(GridShape), typeof(TagSystem) }),
            AccessTools.Method(typeof(GameInventory), nameof(GameInventory.TryInventorySlot), new[] { typeof(GameItem), typeof(int), typeof(GridShape), typeof(TagSystem) }),
            AccessTools.Method(typeof(GameInventory), nameof(GameInventory.CheckAcceptParentShapeStateModifier)),
            AccessTools.Method(typeof(GraphUtils), nameof(GraphUtils.TryAcceptAll), new[] { typeof(GraphNodeStorage), typeof(GameItem), typeof(int) }),
            AccessTools.Method(typeof(GameItemElement), nameof(GameItemElement.Validate)),
            AccessTools.Method(typeof(GameItemElement), nameof(GameItemElement.ValidateSelect)),
            AccessTools.Method(typeof(GameItemElement), nameof(GameItemElement.Destroy)),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.ValidateShapeState)),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.MaxNumRemove)),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.MaxNumAdd), Type.EmptyTypes),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.MaxNumAdd), new[] { typeof(SlotMarker) }),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.MayStack)),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.GetStackSlot)),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.CheckIsStateValid)),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.ComputeModifiedShapeState)),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.GetParentShapeModifier)),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.GetParentStateModifier)),
            AccessTools.Method(typeof(GameGridInventory), "GetIncompleteItemInventorySlot", new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(GameSlotInventory), "GetIncompleteItemInventorySlot", new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(GameInventory), "CheckRemoveParentShapeModifier", new[] { typeof(GameItem) }),
            AccessTools.PropertySetter(typeof(GameItem), nameof(GameItem.unitCount)),
            AccessTools.Method(typeof(GameItem), "AddParent"),
            AccessTools.Method(typeof(GameItem), "RemoveParent"),
            AccessTools.Method(typeof(PixelElement), "SetRenderingParent"),
            AccessTools.Method(typeof(PixelElement), "RemoveRenderingParent"),
            AccessTools.Method(typeof(OverlayHandler), nameof(OverlayHandler.UpdateInventoryStateUI)),
            AccessTools.Method(typeof(GameItem), "RemoveTemporaryClientFeature"),
            AccessTools.Method(typeof(GameItem), "RemoveContrabandFeature"),
            AccessTools.Method(typeof(PlayerStore), "AddedItemToMainInvArea"),
            AccessTools.Method(typeof(PlayerStore), "RemovedItemToMainInvArea"),
            AccessTools.Method(typeof(PlayerStore), "OnMainInvAreaChange"),
            AccessTools.Method(typeof(PlayerStore), "RemovedItemToWeightedArea"),
            AccessTools.Method(typeof(PlayerStore), "OnWeightedAreaChanged"),
            AccessTools.Method(typeof(GameItem), "OnRemovedFromWeightedArea"),
            AccessTools.Method(typeof(GameItem), "RemoveFeatureRelatedToEvent"),
            AccessTools.Method(typeof(GameItem), "RemoveFeatureRelatedToBuyer"),
            AccessTools.Method(typeof(NegociationUIManager), "ClearUI"),
            AccessTools.Method(typeof(NotebookUIManager), "UpdateUI"),
            AccessTools.Method(typeof(GunModDirectory), "SetRDSMod"),
            AccessTools.Method(typeof(GunModDirectory), "SetSilencerModShape"),
            AccessTools.Method(typeof(GunModDirectory), "SetSilencerMod"),
            AccessTools.Method(typeof(GunModDirectory), "SetSilencerMakeshiftShape"),
            AccessTools.Method(typeof(GunModDirectory), "SetSilencerMakeshiftMod"),
            AccessTools.Method(typeof(GunModDirectory), "SetLaserMod"),
            AccessTools.Method(typeof(GunModDirectory), "SetLaserToyMod"),
            AccessTools.Method(typeof(GunModDirectory), "Set22MagMod"),
            AccessTools.Method(typeof(GunModDirectory), "Set22MagHighMod"),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.IsID)),
            AccessTools.Method(typeof(GeneralHelper), nameof(GeneralHelper.IsItemOwned), new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(GeneralHelper), nameof(GeneralHelper.MayOwnershipAcceptItem), new[] { typeof(GameItem), typeof(GameInventory) }),
            AccessTools.Method(typeof(GeneralHelper), nameof(GeneralHelper.IsHazardousWaste), new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(EmporiumEntry), "CanInsertIntoPlayerInventories", new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(ContainerHelper), nameof(ContainerHelper.IsNestingAllowed)),
            AccessTools.Method(typeof(ContainerHelper), nameof(ContainerHelper.IsNestableDevice)),
            AccessTools.Method(typeof(HopperHelper), nameof(HopperHelper.MayBypassGridRules)),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.IsGameItemType), new[] { typeof(string) }),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.IsOneOfTags), new[] { typeof(List<string>) }),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.IsTag), new[] { typeof(string) }),
            AccessTools.Method(typeof(TagSystem), nameof(TagSystem.IsTag), new[] { typeof(string) }),
            AccessTools.PropertyGetter(typeof(GameItem), nameof(GameItem.modifiedState)),
            AccessTools.PropertyGetter(typeof(TagSystem), nameof(TagSystem.dict)),
            AccessTools.Method(typeof(TagState), nameof(TagState.IsEnabled)),
            AccessTools.Method(typeof(RenderHandler), nameof(RenderHandler.LoadFromAtlas), new[] { typeof(string), typeof(string) })
        };
        private static readonly Guid PreviousGameModule = new Guid("83045710-adce-4e78-8ff1-b43a9399ec0d");
        private static readonly Guid CurrentGameModule = new Guid("efd8b252-19aa-489b-96d3-e172c92c0b75");
        internal static bool VerifiedGameBuild(Guid module) => module == PreviousGameModule || module == CurrentGameModule;
        internal static bool KnownCallbacks(Delegate callbacks, Func<Delegate, bool> allowed)
        {
            if (callbacks == null) return true;
            foreach (Delegate callback in callbacks.GetInvocationList())
                if (callback.Method.Module != typeof(GameItem).Module || !VerifiedGameBuild(callback.Method.Module.ModuleVersionId) ||
                    !KnownPatches(callback.Method) || !allowed(callback)) return false;
            return true;
        }
        internal static bool Method(Delegate callback, string type, string name) =>
            callback.Method.DeclaringType?.FullName == type && callback.Method.Name == name;
        private static bool OutingCallback(Delegate callback, int ordinal) =>
            Method(callback, "EmporiumEntry+<>c", callback.Method.Module.ModuleVersionId == CurrentGameModule
                ? "<SetupAfterhourInv>b__284_" + ordinal : "<SetupAfterhourInv>b__280_" + ordinal);
        private static bool SafeAddRule(GameGridInventory grid) => KnownCallbacks(grid.mayInventoryAddItemFunc, callback =>
            (ReferenceEquals(grid, EmporiumEntry.Instance?.invElement) && ReferenceEquals(callback.Target, EmporiumEntry.Instance) &&
                Method(callback, "EmporiumEntry", "<Start>b__184_12")) ||
            (ReferenceEquals(grid, EmporiumEntry.Instance?.afterhourInventory) &&
                OutingCallback(callback, 0)) ||
            (grid.GetParentItem() != null && Method(callback, "ContainerHelper+<>c__DisplayClass21_0", "<InitContainerItem>b__0")));
        private static bool SafeStockNotification(GameGridInventory grid, Delegate callbacks, bool adding) => KnownCallbacks(callbacks, callback =>
            ReferenceEquals(grid, EmporiumEntry.Instance?.invElement) && Method(callback, "EmporiumEntry+<>c",
                adding ? "<Start>b__184_19" : "<Start>b__184_20"));
        private static bool SafePriority(GameGridInventory grid) => KnownCallbacks(grid.slotPriorityFunc, callback =>
            (ReferenceEquals(grid, EmporiumEntry.Instance?.invElement) && Method(callback, "EmporiumEntry+<>c", "<Start>b__184_6")) ||
            (ReferenceEquals(grid, EmporiumEntry.Instance?.afterhourInventory) && OutingCallback(callback, 1)));
        private static bool SafeItemAdd(GameItem item) => KnownCallbacks(item.onSlotAddItemFunc, callback =>
            ReferenceEquals(callback.Target, item) && Method(callback, "GameItem", "<.ctor>b__177_0"));
        internal static bool KnownPatches(MethodBase method)
        {
            if (method == null) return false;
            var patches = Harmony.GetPatchInfo(method);
            if (patches == null) return true;
            foreach (var patch in patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers).Concat(patches.Finalizers))
                if (patch.PatchMethod?.DeclaringType?.Assembly != typeof(InventoryAutoInsert).Assembly) return false;
            return true;
        }
        internal static bool KnownMutationPatches() => VerifiedGameBuild(typeof(GameItem).Module.ModuleVersionId) && NativeMutations.All(KnownPatches);

        internal static bool SafeGrid(GameGridInventory grid) => grid != null && grid.GetType() == typeof(GameGridInventory) &&
            SafeAddRule(grid) && SafePriority(grid) && grid.mayInventoryRemoveItemFunc == null &&
            grid.maxSlotAddItemFunc == null && grid.maxSlotRemoveItemFunc == null && grid.maxSlotAmountItemFunc == null &&
            grid.minSlotAmountItemFunc == null && grid.isItemStateValidFunc == null &&
            grid.parentItemShapeModifierForItemFunc == null && grid.parentItemStateModifierForItemFunc == null &&
            grid.parentItemSpriteModifierForItemFunc == null && grid.onSlotAddItemFunc == null &&
            SafeStockNotification(grid, grid.onSlotAddItemEndFunc, true) && grid.onSlotAddItemLateFunc == null && grid.onSlotRemoveItemFunc == null &&
            SafeStockNotification(grid, grid.onSlotRemoveItemEndFunc, false) && grid.onSlotRemoveItemLateFunc == null && grid.onSlotChangeAmountItemFunc == null;

        internal static bool SafeItem(GameItem item, bool allowStack = false, bool allowCounter = false) => item != null && item.GetType() == typeof(GameItemElement) &&
            item.contentWindow == null && item.modifiedShape != null && !string.IsNullOrEmpty(item.identifier) && item.identifier != "tutorial_page" &&
            (item.GetParentWithIdentifier("backInv") == null || allowCounter &&
                (SettledCounterEvidence.Allows(item) || TripCargo.IsReturnedCargoInCounter(item))) &&
            GraphUtils.FindAllChildrenType<GameItem>(item, null, null).Count == 0 &&
            item.itemFeatures != null && item.itemFeatures.All(feature => feature != null &&
                feature.featureType != ItemFeature.FeatureType.ClientTemporary && feature.category != null && feature.category != "contrabandMarkUp") &&
            item.mayInventoryAddItemFunc == null && item.mayInventoryRemoveItemFunc == null &&
            (allowStack ? KnownStackPredicate(item.mayStackItemFunc) : item.mayStackItemFunc == null) && item.slotPriorityFunc == null &&
            item.maxSlotAddItemFunc == null && item.maxSlotRemoveItemFunc == null &&
            (allowStack ? KnownStackLimit(item.maxSlotAmountItemFunc) : item.maxSlotAmountItemFunc == null) &&
            item.minSlotAmountItemFunc == null && item.isItemStateValidFunc == null &&
            item.parentItemShapeModifierForItemFunc == null && item.parentItemStateModifierForItemFunc == null &&
            item.parentItemSpriteModifierForItemFunc == null && item.getItemSpriteModifierForItemFunc == null &&
            SafeItemAdd(item) && item.onSlotRemoveItemFunc == null &&
            item.onSlotChangeAmountItemFunc == null && item.onChangeAmountItemFunc == null;

        // These native predicates only compare an identifier/sprite or return a
        // constant. They were checked against the verified game modules.
        private static bool KnownStackPredicate(Delegate callbacks) => KnownCallbacks(callbacks, callback =>
            Method(callback, "UnusedDirectory+<>c", "<ExoticFuel>b__29_0") ||
            Method(callback, "UnusedDirectory+<>c", "<SheetSteel>b__33_0") ||
            Method(callback, "UnusedDirectory+<>c", "<Coin>b__96_0"));
        private static bool KnownStackLimit(Delegate callbacks) => KnownCallbacks(callbacks, callback =>
            Method(callback, "UnusedDirectory+<>c", "<ExoticFuel>b__29_1") ||
            Method(callback, "UnusedDirectory+<>c", "<SheetSteel>b__33_1") ||
            Method(callback, "UnusedDirectory+<>c", "<Coin>b__96_1"));

        private static bool SafeVisualItem(GameItem item) => item != null && item.GetType() == typeof(GameItemElement) &&
            KnownCallbacks(item.parentItemShapeModifierForItemFunc, callback =>
                Method(callback, "GunModDirectory+<>c", "<CreateSilencer>b__13_0") ||
                Method(callback, "GunModDirectory+<>c", "<CreateSilencerMakeshift>b__19_0")) &&
            item.parentItemStateModifierForItemFunc == null &&
            KnownCallbacks(item.parentItemSpriteModifierForItemFunc, callback =>
                Method(callback, "GunModDirectory+<>c", "<CreateRDS>b__9_0") ||
                Method(callback, "GunModDirectory+<>c", "<CreateSilencer>b__13_1") ||
                Method(callback, "GunModDirectory+<>c", "<CreateSilencerMakeshift>b__19_1") ||
                Method(callback, "GunModDirectory+<>c", "<CreateLaser>b__29_0") ||
                Method(callback, "GunModDirectory+<>c", "<CreateLaserToy>b__33_0") ||
                Method(callback, "GunModDirectory+<>c", "<Create22Mag>b__57_0") ||
                Method(callback, "GunModDirectory+<>c", "<Create22MagHigh>b__59_0")) &&
            item.getItemSpriteModifierForItemFunc == null && item.isItemStateValidFunc == null;

        private static bool SafeVisualPath(GameInventory inventory, IDictionary<GameInventory, bool> snapshot = null)
        {
            var seen = new HashSet<GameInventory>();
            var visited = snapshot == null ? null : new List<GameInventory>();
            bool result = true;
            while (inventory != null)
            {
                if (snapshot != null && snapshot.TryGetValue(inventory, out bool known)) { result = known; break; }
                if (!seen.Add(inventory) || inventory.GetType() != typeof(GameGridInventory) && inventory.GetType() != typeof(GameSlotInventory) ||
                    inventory.parentItemShapeModifierForItemFunc != null || inventory.parentItemStateModifierForItemFunc != null ||
                    inventory.parentItemSpriteModifierForItemFunc != null ||
                    !inventory.childItems.All(SafeVisualItem)) { result = false; break; }
                visited?.Add(inventory);
                GameItem parent = inventory.GetParentItem();
                if (parent == null) { result = true; break; }
                if (!SafeVisualItem(parent)) { result = false; break; }
                inventory = parent.parentInventory;
            }
            if (visited != null) foreach (GameInventory value in visited) snapshot[value] = result;
            return result;
        }

        // Expel/accept can validate every ancestor grid. Do not enter the
        // native mutation if those refreshes would run an unknown callback.
        internal static bool SafeTransferInventory(GameInventory inventory, GameItem source = null) =>
            SafeTransferInventoryWithSnapshot(inventory, source, null);

        internal static bool SafeTransferInventoryWithSnapshot(GameInventory inventory, GameItem source,
            IDictionary<GameInventory, bool> visualSnapshot)
        {
            if (inventory == null) return false;
            bool safe;
            if (inventory is GameGridInventory grid && source != null && SettledCounterEvidence.Allows(source))
                safe = ReferenceEquals(grid, EmporiumEntry.Instance.backInvinvElement) && grid.GetType() == typeof(GameGridInventory) &&
                    grid.mayInventoryRemoveItemFunc == null && grid.minSlotAmountItemFunc == null && grid.maxSlotRemoveItemFunc == null &&
                    grid.isItemStateValidFunc == null && grid.parentItemShapeModifierForItemFunc == null &&
                    grid.parentItemStateModifierForItemFunc == null && grid.parentItemSpriteModifierForItemFunc == null &&
                    grid.onSlotRemoveItemFunc == null && grid.onSlotRemoveItemLateFunc == null && grid.onSlotChangeAmountItemFunc == null &&
                    KnownCallbacks(grid.onSlotRemoveItemEndFunc, callback => Method(callback, "EmporiumEntry+<>c", "<Start>b__184_18"));
            else if (inventory is GameGridInventory ordinary) safe = SafeGrid(ordinary);
            else if (inventory.GetType() == typeof(GameSlotInventory))
            {
                var entry = EmporiumEntry.Instance;
                bool outingPocket = ReferenceEquals(inventory, entry?.afterhourPocketSlotInvLeft) ||
                    ReferenceEquals(inventory, entry?.afterhourPocketSlotInvRight);
                var add = inventory.mayInventoryAddItemFunc;
                bool safeAdd = add == null || outingPocket && add.GetInvocationList().Length == 1 &&
                    KnownCallbacks(add, callback => Method(callback, "ContainerHelper+<>c", "<AllowOnlyOwnedItems>b__13_0"));
                safe = safeAdd && inventory.mayInventoryRemoveItemFunc == null && inventory.slotSearchPassthroughForItemFunc == null &&
                    inventory.childItems.All(item => item != null && item.slotSearchPassthroughForItemFunc == null) &&
                    inventory.maxSlotAddItemFunc == null && inventory.maxSlotRemoveItemFunc == null &&
                    inventory.maxSlotAmountItemFunc == null && inventory.minSlotAmountItemFunc == null &&
                    inventory.isItemStateValidFunc == null && inventory.slotPriorityFunc == null &&
                    inventory.parentItemShapeModifierForItemFunc == null && inventory.parentItemStateModifierForItemFunc == null &&
                    inventory.parentItemSpriteModifierForItemFunc == null && inventory.onSlotAddItemFunc == null &&
                    inventory.onSlotAddItemEndFunc == null && inventory.onSlotAddItemLateFunc == null &&
                    inventory.onSlotRemoveItemFunc == null && inventory.onSlotRemoveItemEndFunc == null &&
                    inventory.onSlotRemoveItemLateFunc == null && inventory.onSlotChangeAmountItemFunc == null;
            }
            else return false;
            return safe && SafeVisualPath(inventory, visualSnapshot);
        }

        private static Request Capture(GameGridInventory target, IReadOnlyList<GameItem> items, bool group)
        {
            if (!InventoryOrganizer.AutomaticGrid(target) || !SafeGrid(target) || !KnownMutationPatches() ||
                items == null || items.Count == 0 || items.Count > 256) return null;
            if (!(items[0]?.parentInventory is GameGridInventory source) || source == target ||
                !InventoryOrganizer.AutomaticGrid(source) || !SafeGrid(source)) return null;
            var fixedItems = InventoryOrganizer.AutomaticFixed(source);
            var unique = new HashSet<GameItem>();
            foreach (GameItem item in items)
                if (!unique.Add(item) || item.parentInventory != source || !SafeItem(item) || !ShopInventory.Movable(item) ||
                    item.MaxNumRemove() < item.unitCount || fixedItems.Contains(item) || (group && item.unitCount != 1)) return null;
            if (group)
            {
                // The native group path may stack into a resident or an earlier member.
                // It also follows slot-search callbacks into child inventories;
                // those paths cannot be included in this two-grid transaction.
                if (target.slotSearchPassthroughForItemFunc != null || target.childItems.Any(resident =>
                    resident == null || resident.slotSearchPassthroughForItemFunc != null)) return null;
                foreach (GameItem item in items)
                {
                    if (target.childItems.Any(resident => resident == null || resident.GetType() != typeof(GameItemElement) ||
                        resident.mayStackItemFunc != null || resident.MayStack(item))) return null;
                    if (items.Any(other => !ReferenceEquals(other, item) && other.MayStack(item))) return null;
                }
            }
            var transaction = InventoryArrangeTransaction.Capture(new[] { source, target }, SafeGrid, item => SafeItem(item), KnownMutationPatches);
            if (transaction == null) return null;
            var request = new Request { Transaction = transaction, Target = target, Items = items.ToArray() };
            foreach (GameItem item in items)
                request.Poses.Add(item, (item is GameItemElement element && element.tempShape != null ? element.tempShape : item.modifiedShape).Clone());
            return request;
        }

        private static void Complete(Request request)
        {
            if (request == null || !InventoryOrganizer.AutomaticGrid(request.Target) ||
                !request.Transaction.NativeMovedOnly(new HashSet<GameItem>(request.Items), request.Target)) return;
            GameItem[] remaining = request.Items.Where(item => !ReferenceEquals(item.parentInventory, request.Target)).ToArray();
            if (remaining.Length == 0) return;
            var clock = Stopwatch.StartNew();
            Dictionary<GameItem, InventoryArrangeTransaction.Destination> placements = null;
            int accepted = 0;
            for (int count = remaining.Length; count > 0 && clock.ElapsedMilliseconds <= 150; count--)
            {
                placements = Plan(request, remaining.Take(count).ToArray(), clock);
                if (placements != null) { accepted = count; break; }
            }
            if (placements == null) { ShopNotice.Show(ShopSettings.Text("f05.auto_no_plan")); return; }
            var result = request.Transaction.Commit(placements);
            if (request.Transaction.LastError != null) InventoryOrganizer.AutomaticFailure(request.Transaction.LastError.ToString());
            if (result == InventorySortPlan.ApplyResult.Applied)
            {
                InventoryOrganizer.Remember(request.Transaction);
                VisibleInventoryViews.NotifyChanged();
                ShopNotice.Show(ShopSettings.Format(accepted == remaining.Length ? "f05.auto_done" : "f05.auto_partial", accepted));
            }
            else ShopNotice.Show(ShopSettings.Text(result == InventorySortPlan.ApplyResult.Restored ? "f05.restored" :
                result == InventorySortPlan.ApplyResult.Inspect ? "f05.inspect" : "f05.changed"));
        }

        private static Dictionary<GameItem, InventoryArrangeTransaction.Destination> Plan(Request request, GameItem[] incoming, Stopwatch clock)
        {
            GameGridInventory target = request.Target;
            var fixedItems = InventoryOrganizer.AutomaticFixed(target);
            var residents = target.childItems.Where(item => SafeItem(item) && ShopInventory.Movable(item) &&
                item.MaxNumRemove() >= item.unitCount && !fixedItems.Contains(item)).ToList();
            var moving = residents.Concat(incoming).Distinct().ToList();
            if (moving.Count > 256 || target.inventoryShape.width > 64 || target.inventoryShape.height > 64) return null;
            var result = new Dictionary<GameItem, InventoryArrangeTransaction.Destination>();
            int candidates = 0;
            int Area(GameItem item)
            {
                GridShape shape = item.modifiedShape;
                int area = 0;
                for (int y = 0; y < shape.height; y++) for (int x = 0; x < shape.width; x++) if (shape.GetLocal(x, y) != 0) area++;
                return area;
            }
            foreach (GameItem item in incoming.OrderByDescending(Area).Concat(residents.OrderByDescending(Area)))
            {
                GridShape initial = request.Poses.TryGetValue(item, out GridShape dragged) ? dragged : item.modifiedShape;
                var pose = new GridShapeBuilder(initial);
                GridShape chosen = null;
                for (int turn = 0; turn < (InventoryOrganizer.AutomaticRotate ? 4 : 1) && chosen == null; turn++)
                {
                    pose.SetOrientation((initial.orientation + turn) % 4);
                    for (int y = 0; y < target.inventoryShape.height && chosen == null; y++)
                    for (int x = 0; x < target.inventoryShape.width; x++)
                    {
                        if (++candidates > 1000000 || clock.ElapsedMilliseconds > 150) return null;
                        pose.SetPosition(x, y);
                        if (!target.CheckShapePlacement(item, pose, moving) || result.Values.Any(value => value.Shape.Intersects(pose))) continue;
                        chosen = pose.Build(); break;
                    }
                }
                if (chosen == null) return null;
                result.Add(item, new InventoryArrangeTransaction.Destination { Grid = target, Shape = chosen });
            }
            return result;
        }

        [HarmonyPatch(typeof(ItemMouseDragHandler), "EndDrag")]
        private static class SingleDrop
        {
            private static void Prefix(GameItemElement ___currentItem, int ___currentRemove, GameInventory ___lastInventory,
                GameItem ___lastItem, out Request __state)
            {
                __state = null;
                if (!InventoryOrganizer.AutomaticEnabled || ___lastItem != null || ___currentItem == null ||
                    ___currentRemove != ___currentItem.unitCount || !(___lastInventory is GameGridInventory target)) return;
                try
                {
                    if (!(___currentItem.parentInventory is GameGridInventory source) || source == target ||
                        !InventoryOrganizer.AutomaticGrid(source) || !InventoryOrganizer.AutomaticGrid(target) ||
                        !SafeGrid(source) || !SafeGrid(target) || !SafeItem(___currentItem)) return;
                    SlotMarker direct = target.TryInventorySlot(___currentItem, ___currentRemove,
                        ___currentItem.GetFakeRotatorCenteredPosition(), ___currentItem.tempShape, ___currentItem.modifiedState);
                    // Any native direct, partial, or stack transfer stays wholly native.
                    if (direct != null && (direct.targetItem != null || direct.numTransfer > 0)) return;
                    __state = Capture(target, new GameItem[] { ___currentItem }, false);
                }
                catch (Exception error) { InventoryOrganizer.AutomaticFailure("Capture: " + error); }
            }
            private static void Postfix(Request __state)
            {
                try { Complete(__state); }
                catch (Exception error) { InventoryOrganizer.AutomaticFailure("Drop: " + error); ShopNotice.Show(ShopSettings.Text("f05.failed")); }
            }
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "EndGroupDrag")]
        private static class GroupDrop
        {
            private static void Prefix(List<GameItemElement> ___selectedItems, out Request __state)
            {
                __state = null;
                if (!InventoryOrganizer.AutomaticEnabled || !GroupPlacement.AutomaticPlacementAllowed || ___selectedItems == null) return;
                try
                {
                    GameItem hit = RenderHandler.RaycastElement<GameItem>(Input.mousePosition);
                    if (hit != null && !___selectedItems.Contains(hit as GameItemElement)) return;
                    var target = RenderHandler.RaycastFindParentElement<GameGridInventory>(Input.mousePosition);
                    __state = Capture(target, ___selectedItems.Cast<GameItem>().ToArray(), true);
                }
                catch (Exception error) { InventoryOrganizer.AutomaticFailure("Group capture: " + error); }
            }
            private static void Postfix(Request __state)
            {
                try { Complete(__state); }
                catch (Exception error) { InventoryOrganizer.AutomaticFailure("Group drop: " + error); ShopNotice.Show(ShopSettings.Text("f05.failed")); }
            }
        }
    }
}
