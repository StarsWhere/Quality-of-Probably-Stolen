using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ShopAssist
{
    // One in-memory transaction owns all affected grids, including native moves
    // that preceded a successful automatic insertion.
    internal sealed class InventoryArrangeTransaction : IInventoryArrangeUndo
    {
        private static readonly FieldInfo[] FeatureFields = StateFields(typeof(ItemFeature));
        private static readonly FieldInfo[] ConditionFields = StateFields(typeof(ItemCondition));
        private static FieldInfo[] StateFields(Type type) => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
        internal sealed class Destination
        {
            internal GameGridInventory Grid;
            internal GridShape Shape;
        }
        private sealed class ItemSnapshot
        {
            internal GameItem Item;
            internal GameGridInventory Grid;
            internal GridShape Shape;
            internal int Count;
            internal string State;
            internal bool Movable;
            internal ItemSnapshot Copy() => new ItemSnapshot { Item = Item, Grid = Grid, Shape = Shape.Clone(), Count = Count, State = State, Movable = Movable };
        }
        private sealed class Snapshot
        {
            internal readonly Dictionary<GameGridInventory, GridShape> Grids = new Dictionary<GameGridInventory, GridShape>();
            internal readonly Dictionary<GameGridInventory, string> Scopes = new Dictionary<GameGridInventory, string>();
            internal readonly Dictionary<GameItem, ItemSnapshot> Items = new Dictionary<GameItem, ItemSnapshot>();
            internal Snapshot Copy()
            {
                var copy = new Snapshot();
                foreach (var pair in Grids) copy.Grids.Add(pair.Key, pair.Value.Clone());
                foreach (var pair in Scopes) copy.Scopes.Add(pair.Key, pair.Value);
                foreach (var pair in Items) copy.Items.Add(pair.Key, pair.Value.Copy());
                return copy;
            }
            internal bool Matches()
            {
                foreach (var pair in Grids)
                {
                    GameGridInventory grid = pair.Key;
                    if (grid?.inventoryShape == null || !InventorySortPlan.SameShape(grid.inventoryShape, pair.Value) ||
                        GridScope != null && GridScope(grid) != Scopes[grid]) return false;
                    var expected = Items.Values.Where(item => ReferenceEquals(item.Grid, grid)).ToArray();
                    if (grid.childItems == null || grid.childItems.Count != expected.Length ||
                        new HashSet<GameItem>(grid.childItems).Count != expected.Length) return false;
                    foreach (ItemSnapshot item in expected)
                        if (!grid.childItems.Contains(item.Item) || !ReferenceEquals(item.Item.parentInventory, grid) ||
                            item.Item.unitCount != item.Count || StateSignature(item.Item) != item.State ||
                            SafelyMovable(grid, item.Item) != item.Movable ||
                            !InventorySortPlan.SameShape(item.Shape, item.Item.modifiedShape)) return false;
                }
                return true;
            }
        }

        private readonly Snapshot before;
        private static int applying;
        internal static bool IsApplying => applying != 0;
        private readonly Func<GameGridInventory, bool> gridGuard;
        private readonly Func<GameItem, bool> itemGuard;
        private readonly Func<bool> operationGuard;
        internal static Func<GameGridInventory, string> GridScope;
        private readonly string run;
        private readonly int slot;
        private Snapshot after;
        private bool finished;
        internal Exception LastError { get; private set; }
        Exception IInventoryArrangeUndo.LastError => LastError;

        private InventoryArrangeTransaction(Snapshot original, Func<GameGridInventory, bool> gridGuard,
            Func<GameItem, bool> itemGuard, Func<bool> operationGuard)
        {
            before = original; this.gridGuard = gridGuard; this.itemGuard = itemGuard; this.operationGuard = operationGuard;
            run = PlayerStore.Instance.runID; slot = PlayerStore.Instance.saveSlotId;
        }

        internal static InventoryArrangeTransaction Capture(IEnumerable<GameGridInventory> grids,
            Func<GameGridInventory, bool> gridGuard = null, Func<GameItem, bool> itemGuard = null, Func<bool> operationGuard = null)
        {
            if (!ShopInventory.InStore) return null;
            Snapshot snapshot = CaptureSnapshot(grids);
            return snapshot == null ? null : new InventoryArrangeTransaction(snapshot, gridGuard, itemGuard, operationGuard);
        }

        internal static bool SafelyMovable(GameGridInventory grid, GameItem item) =>
            InventorySortPlan.SafeToCheck(grid, item) && ShopInventory.Movable(item);

        private static Snapshot CaptureSnapshot(IEnumerable<GameGridInventory> grids)
        {
            var snapshot = new Snapshot();
            foreach (GameGridInventory grid in grids.Distinct())
            {
                if (grid?.inventoryShape == null || grid.childItems == null || grid.childItems.Count > 256) return null;
                snapshot.Grids.Add(grid, grid.inventoryShape.Clone());
                string scope = GridScope?.Invoke(grid);
                if (GridScope != null && scope == null) return null;
                snapshot.Scopes.Add(grid, scope);
                foreach (GameItem item in grid.childItems)
                {
                    if (item?.modifiedShape == null || snapshot.Items.ContainsKey(item) || item.parentInventory != grid) return null;
                    string state = StateSignature(item);
                    if (state == null) return null;
                    snapshot.Items.Add(item, new ItemSnapshot { Item = item, Grid = grid, Shape = item.modifiedShape.Clone(),
                        Count = item.unitCount, State = state, Movable = SafelyMovable(grid, item) });
                }
            }
            return snapshot.Grids.Count == 0 ? null : snapshot;
        }

        internal static string StateSignature(GameItem item)
        {
            if (item == null) return null;
            var builder = new StringBuilder();
            builder.Append(item.identifier?.Length ?? -1).Append(':').Append(item.identifier).Append('|').Append(item.uniqueId).Append('|')
                .Append(RequestBook.Reserved(item)).Append('|');
            AddTags(builder, item.state);
            AddTags(builder, item.modifiedState);
            if (item.itemFeatures == null) builder.Append("features:null|");
            else
            {
                builder.Append("features:").Append(item.itemFeatures.Count).Append('|');
                foreach (ItemFeature feature in item.itemFeatures)
                    if (!AddStateValue(builder, feature)) return null;
            }
            return builder.ToString();
        }
        private static bool AddStateValue(StringBuilder builder, object value)
        {
            if (value == null) { builder.Append("n;"); return true; }
            Type type = value.GetType();
            if (type == typeof(string))
            {
                string text = (string)value;
                builder.Append('s').Append(text.Length).Append(':').Append(text).Append(';');
                return true;
            }
            if (type == typeof(int) || type == typeof(bool) || type.IsEnum)
            {
                builder.Append('v').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(';');
                return true;
            }
            if (type == typeof(ItemFeature) || type == typeof(ItemCondition))
            {
                builder.Append(type == typeof(ItemFeature) ? "feature{" : "condition{");
                foreach (FieldInfo field in type == typeof(ItemFeature) ? FeatureFields : ConditionFields)
                    if (!AddStateValue(builder, field.GetValue(value))) return false;
                builder.Append("};");
                return true;
            }
            if (value is HashSet<string> set)
            {
                builder.Append("set:").Append(set.Count).Append('{');
                foreach (string entry in set.OrderBy(entry => entry, StringComparer.Ordinal)) AddStateValue(builder, entry);
                builder.Append("};");
                return true;
            }
            if (value is List<string> list)
            {
                builder.Append("list:").Append(list.Count).Append('{');
                foreach (string entry in list) AddStateValue(builder, entry);
                builder.Append("};");
                return true;
            }
            return false;
        }
        private static void AddTags(StringBuilder builder, TagSystem state)
        {
            if (state?.dict == null) { builder.Append("null|"); return; }
            foreach (var pair in state.dict.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                TagState tag = pair.Value;
                // GetTag creates disabled zero-valued placeholders even for a read;
                // native ValidateShapeState drops those placeholders on rebuild.
                if (tag != null && !tag.valueEnabled && tag.valueInt == 0 && tag.valueFloat == 0f &&
                    tag.valueLong == 0 && tag.valueDouble == 0d && !tag.valueBool && string.IsNullOrEmpty(tag.valueString)) continue;
                builder.Append(pair.Key?.Length ?? -1).Append(':').Append(pair.Key).Append('=');
                if (tag == null) { builder.Append("null;"); continue; }
                string value = tag.valueString;
                builder.Append(tag.valueEnabled).Append(',').Append(tag.valueInt).Append(',')
                    .Append(tag.valueFloat.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(tag.valueLong).Append(',').Append(tag.valueDouble.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(tag.valueBool).Append(',').Append(value?.Length ?? -1).Append(':').Append(value).Append(';');
            }
            builder.Append('|');
        }

        internal bool NativeMovedOnly(ISet<GameItem> allowed, GameGridInventory target)
        {
            if (!SameRun()) return false;
            Snapshot current = CaptureSnapshot(before.Grids.Keys);
            if (current == null || current.Items.Count != before.Items.Count) return false;
            foreach (var pair in before.Grids)
                if (!InventorySortPlan.SameShape(pair.Value, current.Grids[pair.Key]) || before.Scopes[pair.Key] != current.Scopes[pair.Key]) return false;
            foreach (var pair in before.Items)
            {
                if (!current.Items.TryGetValue(pair.Key, out ItemSnapshot actual)) return false;
                ItemSnapshot original = pair.Value;
                if (actual.Count != original.Count || actual.State != original.State || actual.Movable != original.Movable) return false;
                if (ReferenceEquals(actual.Grid, original.Grid))
                {
                    if (!InventorySortPlan.SameShape(actual.Shape, original.Shape)) return false;
                }
                else if (!allowed.Contains(pair.Key) || !ReferenceEquals(actual.Grid, target)) return false;
            }
            return current.Matches();
        }

        internal InventorySortPlan.ApplyResult Commit(IReadOnlyDictionary<GameItem, Destination> destinations)
        {
            LastError = null;
            if (finished || !SameRun()) return InventorySortPlan.ApplyResult.Stale;
            Snapshot current = CaptureSnapshot(before.Grids.Keys);
            if (current == null || !current.Matches()) return InventorySortPlan.ApplyResult.Stale;
            Snapshot desired = current.Copy();
            foreach (var pair in destinations)
            {
                if (!desired.Items.TryGetValue(pair.Key, out ItemSnapshot item) || pair.Value?.Grid == null || pair.Value.Shape == null ||
                    !desired.Grids.ContainsKey(pair.Value.Grid)) return InventorySortPlan.ApplyResult.Unsupported;
                item.Grid = pair.Value.Grid;
                item.Shape = pair.Value.Shape.Clone();
            }
            if (!ValidateTransition(current, desired)) return InventorySortPlan.ApplyResult.Stale;
            InventorySortPlan.ApplyResult result = Transition(current, desired);
            if (result == InventorySortPlan.ApplyResult.Applied)
            {
                after = desired;
                finished = true;
            }
            else if (result == InventorySortPlan.ApplyResult.Restored && !before.Matches())
            {
                // The game may have moved part of a group before the helper ran.
                // A failed helper must roll that native part back as one operation.
                Exception failure = LastError;
                if (!ValidateTransition(current, before)) return InventorySortPlan.ApplyResult.Inspect;
                InventorySortPlan.ApplyResult rollback = Transition(current, before);
                if (LastError != failure && LastError != null) LastError = new AggregateException(failure, LastError);
                if (rollback != InventorySortPlan.ApplyResult.Applied) return InventorySortPlan.ApplyResult.Inspect;
            }
            return result;
        }

        bool IInventoryArrangeUndo.Affects(GameGridInventory inventory) => before.Grids.ContainsKey(inventory);
        IEnumerable<GameGridInventory> IInventoryArrangeUndo.Grids => before.Grids.Keys;
        bool IInventoryArrangeUndo.CanUndo() => CanUndo();
        InventorySortPlan.ApplyResult IInventoryArrangeUndo.Undo() => Undo();
        private bool SameRun() => ShopInventory.InStore && PlayerStore.Instance.runID == run && PlayerStore.Instance.saveSlotId == slot;
        private bool CanUndo() => finished && SameRun() && after.Matches() && ValidateTransition(after, before);
        private InventorySortPlan.ApplyResult Undo()
        {
            LastError = null;
            if (!CanUndo()) return InventorySortPlan.ApplyResult.Stale;
            InventorySortPlan.ApplyResult result = Transition(after, before);
            if (result == InventorySortPlan.ApplyResult.Applied || result == InventorySortPlan.ApplyResult.Inspect) finished = false;
            return result;
        }

        private bool ValidateTransition(Snapshot from, Snapshot to)
        {
            if (operationGuard != null && !operationGuard()) return false;
            if (gridGuard != null && from.Grids.Keys.Any(grid => !gridGuard(grid))) return false;
            if (itemGuard != null && from.Items.Values.Any(item =>
                (!ReferenceEquals(item.Grid, to.Items[item.Item].Grid) || !InventorySortPlan.SameShape(item.Shape, to.Items[item.Item].Shape)) &&
                !itemGuard(item.Item))) return false;
            if (!from.Matches() || from.Items.Count != to.Items.Count || from.Grids.Count != to.Grids.Count) return false;
            foreach (GameGridInventory grid in from.Grids.Keys)
                if (!to.Grids.ContainsKey(grid) || grid.IsInsertLocked() || grid.IsRemoveLocked() || !GeneralHelper.MayPlayerInsertInto(grid)) return false;
            var changed = from.Items.Values.Where(item => !ReferenceEquals(item.Grid, to.Items[item.Item].Grid) ||
                !InventorySortPlan.SameShape(item.Shape, to.Items[item.Item].Shape)).Select(item => item.Item).ToList();
            foreach (ItemSnapshot original in from.Items.Values)
            {
                if (!to.Items.TryGetValue(original.Item, out ItemSnapshot desired) || original.Count != desired.Count || original.State != desired.State) return false;
                if (changed.Contains(original.Item) && (!SafelyMovable(original.Grid, original.Item) || original.Item.MaxNumRemove() < original.Count)) return false;
                if (changed.Contains(original.Item) && !desired.Grid.CheckShapePlacement(original.Item, desired.Shape, changed)) return false;
            }
            var items = to.Items.Values.ToArray();
            for (int i = 0; i < items.Length; i++) for (int j = 0; j < i; j++)
                if (ReferenceEquals(items[i].Grid, items[j].Grid) && items[i].Shape.Intersects(items[j].Shape)) return false;
            return true;
        }

        private InventorySortPlan.ApplyResult Transition(Snapshot from, Snapshot to)
        {
            applying++;
            try
            {
                try
                {
                    SetSnapshot(from, to);
                    if (to.Matches()) return InventorySortPlan.ApplyResult.Applied;
                    throw new InvalidOperationException("Arrangement postcondition changed.");
                }
                catch (Exception error) { LastError = error; }
                try
                {
                    RestoreSnapshot(from, to);
                    if (from.Matches()) return InventorySortPlan.ApplyResult.Restored;
                }
                catch (Exception error) { LastError = new AggregateException(LastError, error); }
                return InventorySortPlan.ApplyResult.Inspect;
            }
            finally { applying--; }
        }

        private static void SetSnapshot(Snapshot from, Snapshot to)
        {
            var moved = from.Items.Values.Where(item => !ReferenceEquals(item.Grid, to.Items[item.Item].Grid)).ToArray();
            var changed = from.Items.Values.Where(item => !ReferenceEquals(item.Grid, to.Items[item.Item].Grid) ||
                !InventorySortPlan.SameShape(item.Shape, to.Items[item.Item].Shape)).ToArray();
            foreach (ItemSnapshot item in moved)
                if (item.Item.parentInventory != item.Grid || !item.Grid.Expel(item.Item) || item.Item.parentInventory != null || item.Item.unitCount != item.Count)
                    throw new InvalidOperationException("Native removal rejected an arrangement item.");
            foreach (ItemSnapshot item in changed) item.Item.modifiedShape = to.Items[item.Item].Shape.Clone();
            foreach (ItemSnapshot item in moved)
            {
                ItemSnapshot target = to.Items[item.Item];
                SlotMarker marker = GridPlacements.Exact(target.Grid, item.Item, target.Shape);
                if (marker == null || marker.TryAcceptOnce(item.Count) != item.Count || item.Item.parentInventory != target.Grid)
                    throw new InvalidOperationException("Native insertion rejected an arrangement item.");
            }
            foreach (ItemSnapshot item in changed) item.Item.Validate();
        }

        private static void RestoreSnapshot(Snapshot restore, Snapshot attempted)
        {
            var moved = restore.Items.Values.Where(item => !ReferenceEquals(item.Grid, attempted.Items[item.Item].Grid)).ToArray();
            var changed = restore.Items.Values.Where(item => !ReferenceEquals(item.Grid, attempted.Items[item.Item].Grid) ||
                !InventorySortPlan.SameShape(item.Shape, attempted.Items[item.Item].Shape)).ToArray();
            foreach (ItemSnapshot item in changed)
            {
                GameInventory parent = item.Item.parentInventory;
                GameGridInventory other = attempted.Items[item.Item].Grid;
                if (parent != null && !ReferenceEquals(parent, item.Grid) && !ReferenceEquals(parent, other))
                    throw new InvalidOperationException("An arrangement item moved to an unrelated inventory.");
                if (parent == null && ReferenceEquals(item.Grid, other))
                    throw new InvalidOperationException("A resident arrangement item became detached.");
                if (item.Item.unitCount != item.Count || StateSignature(item.Item) != item.State)
                    throw new InvalidOperationException("An arrangement item changed during recovery.");
            }
            foreach (ItemSnapshot item in moved)
            {
                GameInventory parent = item.Item.parentInventory;
                if (parent != null && !ReferenceEquals(parent, item.Grid))
                {
                    if (!restore.Grids.ContainsKey(parent as GameGridInventory) || !parent.Expel(item.Item) || item.Item.parentInventory != null)
                        throw new InvalidOperationException("An arrangement item could not be recovered from an expected grid.");
                }
                if (item.Item.unitCount != item.Count || StateSignature(item.Item) != item.State)
                    throw new InvalidOperationException("An arrangement item changed during recovery.");
            }
            foreach (ItemSnapshot item in changed) item.Item.modifiedShape = item.Shape.Clone();
            foreach (ItemSnapshot item in moved)
            {
                if (item.Item.parentInventory == item.Grid) continue;
                SlotMarker marker = GridPlacements.Exact(item.Grid, item.Item, item.Shape);
                if (marker == null || marker.TryAcceptOnce(item.Count) != item.Count || item.Item.parentInventory != item.Grid)
                    throw new InvalidOperationException("An arrangement item could not be returned to its original grid.");
            }
            foreach (ItemSnapshot item in changed) item.Item.Validate();
        }
    }
}
