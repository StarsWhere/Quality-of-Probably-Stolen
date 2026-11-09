using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ShopAssist
{
    internal sealed class FieldPacking : IDisposable
    {
        private readonly ConfigEntry<bool> enabled, replace;
        private readonly ConfigEntry<int> reserve, valuation;
        private readonly Func<KeyCode> key;
        private bool running;
        private const int SearchMilliseconds = 150;
        private sealed class InterruptedReplacement
        {
            internal GameItem Resident;
            internal WholeStackTransfer.FieldReplacement Replacement;
            internal Func<bool> RecoveryScope, SameContext;
            internal string Run;
            internal int Slot;
            internal bool Working, AllowOnce;
        }
        private static InterruptedReplacement interrupted;
        private sealed class Candidate
        {
            internal GameItem Item;
            internal decimal Value;
            internal int Cells;
        }
        private sealed class PackingPlan
        {
            internal readonly List<WholeStackTransfer.Plan> Moves = new List<WholeStackTransfer.Plan>();
            internal decimal Value;
        }
        internal FieldPacking(ShopSettings settings)
        {
            enabled = settings.Toggle("f36.enabled", "f36.tip");
            reserve = settings.Integer("f36.space", "f36.space.tip", 2, 0, 32);
            valuation = settings.Choice("f36.valuation", "f36.valuation.tip", new[] { "f36.estimated", "f36.base" });
            replace = settings.Toggle("f36.replace", "f36.replace.tip", false);
            key = settings.Key("f36.key", "f36.key.tip");
            settings.Action("f36.open", Pack, () => enabled.Value && AtGround() && !running);
        }
        internal void Start() => WholeStackTransfer.Acquire();
        private static bool AtGround() => PlayerStore.HasInstance && MapUIManager.Instance?.Current?.Id == "dumping_ground" &&
            EmporiumEntry.Instance?.afterhourWindow?.IsVisible() == true;
        private static IEnumerable<GameItem> Ground() => EmporiumEntry.Instance.afterhourInventory.childItems.Where(i =>
            i is GameItemElement el && VisibleInventoryViews.Visible(el));
        private static IEnumerable<GameInventory> Hands()
        {
            var e = EmporiumEntry.Instance;
            return new GameInventory[] { e.afterhourPocketSlotInvLeft, e.afterhourPocketSlotInvRight }.Where(p => p != null && !p.IsInsertLocked());
        }
        private static GameGridInventory Bag() => ShopInventory.OutingBag(EmporiumEntry.Instance.afterhourPocketSlotInvBackpack.childItem);

        // Audited native tooltip paths; no tooltip is built by this feature.
        private static bool NativeValuesSafe()
        {
            if (!InventoryAutoInsert.VerifiedGameBuild(typeof(GameItem).Module.ModuleVersionId)) return false;
            var types = new[] { typeof(GameItem), typeof(GameItemElement), typeof(ItemFeature), typeof(ItemCondition), typeof(WaterHelper),
                typeof(PowerHelper), typeof(StoreEventManager), typeof(StoreStation), typeof(PlayerStore), typeof(DebugPanel),
                typeof(GraphUtils), typeof(TagSystem), typeof(TagState) };
            return types.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)).All(InventoryAutoInsert.KnownPatches);
        }
        private static bool SafeItem(GameItem item) => item != null && item.GetType() == typeof(GameItemElement) &&
            CustomerIndicators.HasOrdinaryTooltip((GameItemElement)item) && item.itemFeatures != null &&
            item.itemFeatures.All(feature => feature != null && feature.GetType() == typeof(ItemFeature)) &&
            ShopInventory.Movable(item) &&
            !AttentionBook.HasInspection("f36.open", item.uniqueId) && WholeStackTransfer.SafeSource(item);
        private Candidate ReadValue(GameItem item)
        {
            if (!SafeItem(item)) return null;
            var contents = new List<GameItem> { item };
            if (item.contentWindow != null && !WholeStackTransfer.TryFieldContainerItems(item, out contents)) return null;
            foreach (GameItem part in contents)
                if (part != item && (!(part is GameItemElement element) || !VisibleInventoryViews.Visible(element) ||
                    !CustomerIndicators.HasOrdinaryTooltip(element) || AttentionBook.HasInspection("f36.open", part.uniqueId))) return null;
            long unit = valuation.Value == 1 ? item.GetBaseValue() : item.GetCurrentValue(false, true, true, false, 0);
            return new Candidate { Item = item, Value = TotalValue(unit, item.unitCount), Cells = Cells(item.modifiedShape) };
        }
        internal static decimal TotalValue(long unit, int count) => unit <= 0 || count <= 0 ? 0 : (decimal)unit * count;
        internal static bool Improves(decimal incoming, decimal outgoing) => incoming > outgoing;
        internal static decimal Rank(decimal value, int cells, int mode) => mode == 1 ? value / Math.Max(1, cells) :
            mode == 2 ? -Math.Max(1, cells) : value;
        private static int Cells(GridShape shape)
        {
            int count = 0;
            for (int y = shape.minY; y <= shape.maxY; y++) for (int x = shape.minX; x <= shape.maxX; x++)
                if (shape.Intersects(x, y)) count++;
            return count;
        }
        private static int Free(GameGridInventory grid)
        {
            if (grid == null) return 0;
            int count = 0;
            for (int y = 0; y < grid.inventoryShape.height; y++) for (int x = 0; x < grid.inventoryShape.width; x++)
                if (grid.inventoryShape.Get(x, y) == 0 && grid.childItems.All(i => !i.modifiedShape.Intersects(x, y))) count++;
            return count;
        }
        private PackingPlan Plan(List<Candidate> candidates, Stopwatch clock)
        {
            var best = new PackingPlan(); var grid = Bag(); var hands = Hands().ToList();
            for (int mode = 0; mode < 3 && clock.ElapsedMilliseconds < SearchMilliseconds; mode++)
            for (int handFirst = 0; handFirst < 2 && clock.ElapsedMilliseconds < SearchMilliseconds; handFirst++)
            {
                var plan = new PackingPlan(); var reserved = new WholeStackTransfer.Reservations();
                var freeHands = hands.Where(hand => hand.childItems.Count == 0).ToList(); int free = Free(grid);
                foreach (var candidate in candidates.OrderByDescending(c => Rank(c.Value, c.Cells, mode)).ThenByDescending(c => c.Value))
                {
                    if (clock.ElapsedMilliseconds >= SearchMilliseconds) break;
                    var item = candidate.Item;
                    WholeStackTransfer.Plan move = grid == null ? null : WholeStackTransfer.Stack(item, grid, reserved);
                    if (move == null) foreach (var hand in hands)
                        if ((move = WholeStackTransfer.Stack(item, hand, reserved)) != null) break;
                    if (move == null && handFirst == 1) move = IntoHand(item, freeHands);
                    if (move == null && grid != null && free - candidate.Cells >= reserve.Value)
                    {
                        move = WholeStackTransfer.Empty(item, grid, reserved);
                        if (move != null) free -= candidate.Cells;
                    }
                    if (move == null) move = IntoHand(item, freeHands);
                    if (move == null) continue;
                    plan.Moves.Add(move); plan.Value = Math.Min(decimal.MaxValue - candidate.Value, plan.Value) + candidate.Value;
                }
                if (plan.Value > best.Value || plan.Value == best.Value && plan.Moves.Count > best.Moves.Count) best = plan;
            }
            return best;
        }
        private static WholeStackTransfer.Plan IntoHand(GameItem item, List<GameInventory> hands)
        {
            foreach (var hand in hands.ToArray())
            {
                var move = WholeStackTransfer.EmptySlot(item, hand);
                if (move == null) continue;
                hands.Remove(hand); return move;
            }
            return null;
        }
        private static string AreaKey(GameInventory inventory)
        {
            if (ReferenceEquals(inventory, EmporiumEntry.Instance.afterhourInventory)) return "field:ground";
            if (ReferenceEquals(inventory, Bag())) return "field:bag:" + EmporiumEntry.Instance.afterhourPocketSlotInvBackpack.childItem.GetUniqueID();
            return ReferenceEquals(inventory, EmporiumEntry.Instance.afterhourPocketSlotInvLeft) ? "field:left" : "field:right";
        }
        private static string AreaName(GameInventory inventory) => ShopSettings.Text(
            ReferenceEquals(inventory, EmporiumEntry.Instance.afterhourInventory) ? "f36.ground" :
            ReferenceEquals(inventory, Bag()) ? "f36.backpack" :
            ReferenceEquals(inventory, EmporiumEntry.Instance.afterhourPocketSlotInvLeft) ? "f36.left_pocket" : "f36.right_pocket");
        private static bool Apply(WholeStackTransfer.Plan move, OperationResult result, Func<bool> scope)
        {
            var identity = OperationResult.Capture(move.Item); string path = AreaName(move.From) + " → " + AreaName(move.To);
            var outcome = WholeStackTransfer.Apply(move, "f36.open", path, AreaKey(move.From), AreaKey(move.To), scope);
            if (outcome == WholeStackTransfer.Result.Applied)
            {
                result.CompleteCaptured(identity, move.Amount, path);
                return GenericConfirmationUIManager.Instance?.panel.activeInHierarchy != true;
            }
            result.SkipCaptured(identity, move.Amount, outcome == WholeStackTransfer.Result.Unchanged ? "inv.changed" : "f08.pending", path);
            return false;
        }
        private static WholeStackTransfer.Result ApplyReplacement(WholeStackTransfer.FieldReplacement replacement,
            WholeStackTransfer.Plan move, OperationResult result, Func<bool> scope)
        {
            var identity = OperationResult.Capture(move.Item); string path = AreaName(move.From) + " → " + AreaName(move.To);
            var outcome = WholeStackTransfer.ApplyFieldReplacement(replacement, move, scope);
            if (outcome == WholeStackTransfer.Result.Applied) result.CompleteCaptured(identity, move.Amount, path);
            else result.SkipCaptured(identity, move.Amount, outcome == WholeStackTransfer.Result.Unchanged ? "inv.changed" : "f08.pending", path);
            return outcome;
        }
        private static bool Carried(GameItem item)
        {
            var entry = EmporiumEntry.Instance;
            var seen = new HashSet<GameItem>();
            while (item != null && seen.Add(item) && seen.Count <= 256)
            {
                GameInventory inventory = item.parentInventory;
                if (inventory == null || !inventory.childItems.Contains(item)) return false;
                if (ReferenceEquals(inventory, entry?.afterhourPocketSlotInvLeft) ||
                    ReferenceEquals(inventory, entry?.afterhourPocketSlotInvRight) ||
                    ReferenceEquals(inventory, entry?.afterhourPocketSlotInvBackpack)) return true;
                item = inventory.GetParentItem();
            }
            return false;
        }
        private static bool Detached(GameItem item) => item != null && item.parentInventory == null &&
            (!(item is GameItemElement element) || !element.IsDestroyed());

        internal static bool CanLeave()
        {
            try
            {
                var pending = interrupted;
                if (pending == null) return true;
                if (!PlayerStore.HasInstance || PlayerStore.Instance.runID != pending.Run || PlayerStore.Instance.saveSlotId != pending.Slot)
                { interrupted = null; return true; }
                if (!pending.SameContext()) { interrupted = null; return true; }
                if (!pending.Working && pending.RecoveryScope())
                {
                    bool previous = WholeStackTransfer.AllowFieldContainerCargo;
                    WholeStackTransfer.AllowFieldContainerCargo = true;
                    try
                    {
                        if (Detached(pending.Replacement.Incoming))
                            WholeStackTransfer.RecoverDetachedFieldItem(pending.Replacement, true, pending.RecoveryScope);
                        if (Detached(pending.Resident))
                            WholeStackTransfer.RecoverDetachedFieldItem(pending.Replacement, false, pending.RecoveryScope);
                        if (WholeStackTransfer.FieldReplacementOriginal(pending.Replacement, pending.Replacement.Leave.From) &&
                            WholeStackTransfer.FieldReplacementIncomingOriginal(pending.Replacement))
                            WholeStackTransfer.FinishFieldReplacement(pending.Replacement);
                    }
                    finally { WholeStackTransfer.AllowFieldContainerCargo = previous; }
                }
                if (!pending.Working && !Detached(pending.Replacement.Incoming) && (Carried(pending.Resident) ||
                    pending.Resident is GameItemElement element && element.IsDestroyed())) { interrupted = null; return true; }
                ShopNotice.Show((WholeStackTransfer.FieldPending() ?? ShopSettings.Text("f08.pending")) + " · " + ShopSettings.Text("f36.stopped"), 7f);
                return false;
            }
            catch (Exception error)
            {
                Debug.LogWarning("[ShopAssist] Field replacement return check failed: " + error);
                return false;
            }
        }

        private static bool GuardLeave(Action retry)
        {
            var pending = interrupted;
            if (pending?.AllowOnce == true) { pending.AllowOnce = false; return true; }
            if (CanLeave()) return true;
            pending = interrupted;
            if (pending == null || pending.Working || retry == null ||
                (!Detached(pending.Resident) && !Detached(pending.Replacement.Incoming))) return false;
            var prompt = GenericConfirmationUIManager.Instance;
            if (prompt == null || prompt.panel.activeInHierarchy) return false;
            prompt.OpenPanel(ShopSettings.Text("f36.orphan_warning"), ShopSettings.Text("f36.orphan_leave"),
                ShopSettings.Text("f36.orphan_cancel"), () =>
                {
                    prompt.ClosePanel();
                    if (!ReferenceEquals(interrupted, pending) || !pending.SameContext()) return;
                    pending.AllowOnce = true;
                    try { retry(); }
                    finally { pending.AllowOnce = false; }
                }, () => prompt.ClosePanel());
            return false;
        }

        [HarmonyLib.HarmonyPatch(typeof(MapUIManager), nameof(MapUIManager.ReturnNow))]
        private static class ReturnGuard { private static bool Prefix(MapUIManager __instance) => GuardLeave(__instance.ReturnNow); }

        [HarmonyLib.HarmonyPatch(typeof(MapUIManager), nameof(MapUIManager.Travel))]
        private static class TravelGuard { private static bool Prefix(MapUIManager __instance, AfterhourLocation __0) => GuardLeave(() => __instance.Travel(__0)); }

        [HarmonyLib.HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce))]
        private static class RecoveryMergeGuard
        {
            private static bool Prefix(SlotMarker __instance, ref int __result)
            {
                var pending = interrupted;
                if (pending == null || pending.Working || !ReferenceEquals(__instance.item, pending.Resident) || __instance.targetItem == null)
                    return true;
                __result = 0;
                ShopNotice.Show(ShopSettings.Text("f36.stopped"), 7f);
                return false;
            }
        }
        private static GridShape ReplacementShape(GameItem item, GameItem resident, GameGridInventory grid, Stopwatch clock)
        {
            var ignored = new List<GameItem> { resident }; var pose = new GridShapeBuilder(item.modifiedShape);
            for (int face = 0; face < 2; face++) for (int turn = 0; turn < 4; turn++)
            {
                pose.SetOrientation((item.modifiedShape.orientation + turn) % 4);
                pose.SetFlipped(face == 0 ? item.modifiedShape.flipped : !item.modifiedShape.flipped);
                for (int y = 0; y < grid.inventoryShape.height; y++) for (int x = 0; x < grid.inventoryShape.width; x++)
                {
                    if (clock.ElapsedMilliseconds >= SearchMilliseconds) return null;
                    pose.SetPosition(x, y);
                    GridShape shape = pose.Build();
                    if (shape.Intersects(resident.modifiedShape) && grid.CheckShapePlacement(item, shape, ignored)) return shape;
                }
            }
            return null;
        }
        private bool Replace(Candidate incoming, Dictionary<GameItem, Candidate> prices, OperationResult result,
            HashSet<GameItem> packed, Func<bool> scope, Func<bool> recoveryScope, Stopwatch clock)
        {
            var grid = Bag();
            var targets = Hands().Concat(grid == null ? Array.Empty<GameInventory>() : new GameInventory[] { grid });
            foreach (var inventory in targets)
            foreach (var resident in inventory.childItems.ToArray().OrderBy(item => prices.TryGetValue(item, out var value) ? value.Value : decimal.MaxValue))
            {
                if (clock.ElapsedMilliseconds >= SearchMilliseconds || !scope()) return true;
                if (packed.Contains(resident) || !prices.TryGetValue(resident, out var outgoing) ||
                    !Improves(incoming.Value, outgoing.Value) || !SafeItem(resident) ||
                    !WholeStackTransfer.SafeTarget(inventory) || inventory.IsRemoveLocked()) continue;
                Candidate currentIncoming = ReadValue(incoming.Item), currentOutgoing = ReadValue(resident);
                if (currentIncoming == null || currentOutgoing == null || currentIncoming.Value != incoming.Value ||
                    currentOutgoing.Value != outgoing.Value) continue;
                GridShape shape;
                if (inventory is GameGridInventory bag)
                {
                    if (Free(bag) + outgoing.Cells - incoming.Cells < reserve.Value) continue;
                    shape = ReplacementShape(incoming.Item, resident, bag, clock);
                    if (shape == null) continue;
                    var marker = new SlotMarker { item = incoming.Item, inventory = bag, itemGridShape = shape };
                    if (!bag.MayHaveValidInventorySlot(incoming.Item) ||
                        !incoming.Item.CheckIsStateValid(bag, marker, shape, incoming.Item.modifiedState) ||
                        !bag.CheckAcceptParentShapeStateModifier(incoming.Item, marker, shape, incoming.Item.modifiedState) ||
                        incoming.Item.MaxNumAdd(marker) < incoming.Item.unitCount) continue;
                }
                else
                {
                    // Validate the native slot's state rules without clearing it.
                    if (!(inventory is GameSlotInventory hand) || !hand.MayHaveValidInventorySlot(incoming.Item)) continue;
                    shape = new GridShapeBuilder(incoming.Item.modifiedShape).SetTransform(0, 0, false, hand.defaultOrientation).Build();
                    var marker = new SlotMarker { item = incoming.Item, inventory = hand, itemGridShape = shape };
                    if (!incoming.Item.CheckIsStateValid(hand, marker, incoming.Item.modifiedShape, incoming.Item.modifiedState) ||
                        !hand.CheckAcceptParentShapeStateModifier(incoming.Item, marker, incoming.Item.modifiedShape, incoming.Item.modifiedState) ||
                        incoming.Item.MaxNumAdd(marker) < incoming.Item.unitCount) continue;
                }
                // Require a real empty ground location before removing a good.
                GridShape original = resident.modifiedShape.Clone();
                var leave = WholeStackTransfer.Empty(resident, EmporiumEntry.Instance.afterhourInventory, new WholeStackTransfer.Reservations());
                if (leave == null) continue;
                string path = AreaName(leave.From) + " → " + AreaName(leave.To);
                var replacement = WholeStackTransfer.BeginFieldReplacement(leave, incoming.Item, path, AreaKey(leave.From));
                if (replacement == null) { result.Skip(resident, leave.Amount, "f08.pending", path); return false; }
                string guardRun = PlayerStore.Instance.runID; int guardSlot = PlayerStore.Instance.saveSlotId;
                var guard = new InterruptedReplacement { Resident = resident, Run = guardRun,
                    Slot = guardSlot, Replacement = replacement, RecoveryScope = recoveryScope,
                    SameContext = () => PlayerStore.HasInstance && PlayerStore.Instance.runID == guardRun &&
                        PlayerStore.Instance.saveSlotId == guardSlot && MapUIManager.Instance?.Current?.Id == "dumping_ground" &&
                        EmporiumEntry.Instance?.afterhourInventory == leave.To, Working = true };
                interrupted = guard;
                bool completed = false, restored = false;
                try
                {
                    if (ApplyReplacement(replacement, leave, result, scope) != WholeStackTransfer.Result.Applied ||
                        GenericConfirmationUIManager.Instance?.panel.activeInHierarchy == true) return false;
                    currentIncoming = ReadValue(incoming.Item);
                    var take = currentIncoming == null || currentIncoming.Value != incoming.Value ? null :
                        inventory is GameGridInventory target ? WholeStackTransfer.ExactEmpty(incoming.Item, target, shape) :
                        WholeStackTransfer.EmptySlot(incoming.Item, inventory);
                    if (take == null || ApplyReplacement(replacement, take, result, scope) != WholeStackTransfer.Result.Applied)
                    {
                        if (take == null) result.Skip(incoming.Item, incoming.Item.unitCount, "inv.changed");
                        return false;
                    }
                    completed = true; packed.Add(incoming.Item);
                    if (!WholeStackTransfer.FinishFieldReplacement(replacement))
                    { result.Skip(resident, leave.Amount, "f08.pending", path); return false; }
                    return GenericConfirmationUIManager.Instance?.panel.activeInHierarchy != true;
                }
                finally
                {
                    if (!completed)
                    {
                        bool finished = false;
                        try
                        {
                            if (recoveryScope() && Detached(incoming.Item))
                                WholeStackTransfer.RecoverDetachedFieldItem(replacement, true, recoveryScope);
                            if (recoveryScope() && Detached(resident))
                                WholeStackTransfer.RecoverDetachedFieldItem(replacement, false, recoveryScope);
                            if (recoveryScope() && WholeStackTransfer.FieldReplacementOriginal(replacement, leave.To))
                            {
                                var restore = inventory is GameGridInventory bagTarget ? WholeStackTransfer.ExactEmpty(resident, bagTarget, original) :
                                    WholeStackTransfer.EmptySlot(resident, inventory);
                                if (restore != null) ApplyReplacement(replacement, restore, result, recoveryScope);
                            }
                            restored = recoveryScope() && WholeStackTransfer.FieldReplacementOriginal(replacement, inventory);
                            if (restored && WholeStackTransfer.FieldReplacementIncomingOriginal(replacement))
                                finished = WholeStackTransfer.FinishFieldReplacement(replacement);
                        }
                        catch (Exception error) { Debug.LogWarning("[ShopAssist] Field replacement recovery failed: " + error); }
                        if (!finished) result.Skip(resident, leave.Amount, "f08.pending", path);
                    }
                    guard.Working = false;
                    if ((completed || restored && !Detached(incoming.Item)) && ReferenceEquals(interrupted, guard)) interrupted = null;
                }
            }
            return true;
        }
        private void Pack()
        {
            if (running || !enabled.Value || !AtGround()) return;
            if (!WholeStackTransfer.Ready) { ShopNotice.Show(ShopSettings.Text("f08.pending")); return; }
            running = true; var result = new OperationResult("f36.open"); var ground = EmporiumEntry.Instance.afterhourInventory;
            string run = PlayerStore.Instance.runID; int slot = PlayerStore.Instance.saveSlotId;
            var grid = Bag(); var left = EmporiumEntry.Instance.afterhourPocketSlotInvLeft; var right = EmporiumEntry.Instance.afterhourPocketSlotInvRight;
            List<GameItem> source = null;
            bool RecoveryScope() => AtGround() && PlayerStore.Instance.runID == run && PlayerStore.Instance.saveSlotId == slot &&
                EmporiumEntry.Instance.afterhourInventory == ground && Bag() == grid &&
                EmporiumEntry.Instance.afterhourPocketSlotInvLeft == left && EmporiumEntry.Instance.afterhourPocketSlotInvRight == right;
            bool Scope() => enabled.Value && RecoveryScope();
            WholeStackTransfer.AllowFieldContainerCargo = true;
            try
            {
                if (!NativeValuesSafe()) { ShopNotice.Show(ShopSettings.Text("f36.valuation_unsafe")); return; }
                source = Ground().Take(257).ToList(); var prices = new Dictionary<GameItem, Candidate>();
                if (source.Count > 256 || grid != null && (grid.childItems.Count > 256 ||
                    grid.inventoryShape.width > 64 || grid.inventoryShape.height > 64))
                { ShopNotice.Show(ShopSettings.Text("f36.too_large")); return; }
                foreach (var item in source.Concat(Hands().SelectMany(hand => hand.childItems)).Concat(grid?.childItems ?? new List<GameItem>()).Distinct())
                {
                    var value = ReadValue(item);
                    if (value != null) prices[item] = value;
                }
                var candidates = source.Where(prices.ContainsKey).Select(item => prices[item]).Where(value => value.Value > 0).ToList();
                var clock = Stopwatch.StartNew(); var plan = Plan(candidates, clock); var packed = new HashSet<GameItem>(); bool stopped = false;
                foreach (var move in plan.Moves)
                {
                    if (stopped) break;
                    Candidate current = ReadValue(move.Item);
                    if (current == null || !prices.TryGetValue(move.Item, out var expected) || current.Value != expected.Value)
                    {
                        result.Skip(move.Item, move.Amount, "inv.changed"); stopped = true; break;
                    }
                    stopped = !Apply(move, result, Scope);
                    if (move.Item.parentInventory != ground) packed.Add(move.Item);
                    if (move.Stack != null) packed.Add(move.Stack);
                }
                if (replace.Value && !stopped)
                {
                    clock.Restart();
                    foreach (var incoming in candidates.Where(c => !packed.Contains(c.Item)).OrderByDescending(c => c.Value))
                    {
                        if (clock.ElapsedMilliseconds >= SearchMilliseconds) break;
                        if (!Replace(incoming, prices, result, packed, Scope, RecoveryScope, clock)) { stopped = true; break; }
                    }
                }
                foreach (var item in source.Where(item => !packed.Contains(item)))
                    if (!result.Items.Any(row => row.ItemId == item.GetUniqueID())) result.Skip(item, item.unitCount,
                        stopped ? "operation.not_attempted" : !prices.ContainsKey(item) ? "f36.leave_unsafe" :
                        prices[item].Value == 0 ? "f36.leave_value" : "f36.leave_space");
                result.Detail = ShopSettings.Text(stopped ? "f36.stopped" : "f36.approximate"); ShopNotice.ReportField(result, ground);
                ShopNotice.Show(result.Detail, 7f);
            }
            catch (Exception error)
            {
                Debug.LogWarning("[ShopAssist] Field packing stopped: " + error);
                if (source != null) foreach (var item in source)
                    if (!result.Items.Any(row => row.ItemId == item.GetUniqueID())) result.Skip(item, item.unitCount, "f36.stopped");
                result.Detail = ShopSettings.Text("f36.stopped"); ShopNotice.ReportField(result, ground);
                ShopNotice.Show(ShopSettings.Text("f36.stopped"));
            }
            finally { WholeStackTransfer.AllowFieldContainerCargo = false; running = false; }
        }
        internal void Tick()
        {
            if (enabled.Value && key() != KeyCode.None && AtGround() && ShopInput.Ready() && Input.GetKeyDown(key())) Pack();
        }
        public void Dispose() => WholeStackTransfer.Release();
    }
}
