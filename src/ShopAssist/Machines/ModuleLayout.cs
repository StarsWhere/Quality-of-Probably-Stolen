using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ShopAssist
{
    // A snapshot of native module values and grid shapes. It never moves an item.
    internal static class ModuleLayout
    {
        internal sealed class Part
        {
            internal GameItem Item;
            internal GameInventory Source;
            internal GridShape Original;
            internal bool Existing, Fixed, Module, NodeType, NodeTag, ModuleType;
            internal int Count, Performance, Efficiency, Quality;
            internal string Identifier, Effect1, Effect2;
            internal readonly List<GridShape> Poses = new List<GridShape>();
        }

        internal struct Values
        {
            internal int Performance, Efficiency, Quality;
            internal int At(int index) => index == 0 ? Performance : index == 1 ? Efficiency : Quality;
            internal void Add(Values value) { Performance += value.Performance; Efficiency += value.Efficiency; Quality += value.Quality; }
            internal void AddPercentage(Part source, int percent)
            {
                Performance += source.Performance * percent / 100;
                Efficiency += source.Efficiency * percent / 100;
                Quality += source.Quality * percent / 100;
            }
        }

        internal sealed class Plan
        {
            internal readonly List<Part> Parts;
            internal readonly List<GridShape> Shapes;
            internal Values Before, After;
            internal int Moves, OpenSpace;
            internal Plan(List<Part> parts, List<GridShape> shapes) { Parts = parts; Shapes = shapes; }
        }

        internal enum Outcome { Improved, NoImprovement, Timeout, Unsupported }
        private static readonly (int x, int y)[] Neighbors = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        internal static Part Snapshot(GameItem item, bool existing, bool fixedPosition)
        {
            if (item == null || item.modifiedShape == null) return null;
            return new Part
            {
                Item = item, Source = item.parentInventory, Original = item.modifiedShape.Clone(), Existing = existing, Fixed = fixedPosition,
                Identifier = item.identifier, Count = item.unitCount,
                Module = item.IsTag("MODULE_TAG"), NodeType = item.IsGameItemType("NODE"), NodeTag = item.IsTag("NODE"),
                ModuleType = item.IsGameItemType("MODULE"),
                Performance = item.GetTagReadonly("BONUS_PERCENTAGE_PERFORMANCE_INT").GetInt(),
                Efficiency = item.GetTagReadonly("BONUS_PERCENTAGE_EFFICIENCY_INT").GetInt(),
                Quality = item.GetTagReadonly("BONUS_PERCENTAGE_QUALITY_INT").GetInt(),
                Effect1 = item.IsTag("MODULE_EFFECT1_TAG") ? item.GetTagReadonly("MODULE_EFFECT1_TAG").GetString() : null,
                Effect2 = item.IsTag("MODULE_EFFECT2_TAG") ? item.GetTagReadonly("MODULE_EFFECT2_TAG").GetString() : null
            };
        }

        internal static bool Same(Part part)
        {
            Part now = Snapshot(part?.Item, part != null && part.Existing, part != null && part.Fixed);
            return now != null && ReferenceEquals(now.Source, part.Source) && SameShape(now.Original, part.Original) &&
                now.Identifier == part.Identifier && now.Count == part.Count &&
                now.Module == part.Module && now.NodeType == part.NodeType && now.NodeTag == part.NodeTag && now.ModuleType == part.ModuleType &&
                now.Performance == part.Performance && now.Efficiency == part.Efficiency && now.Quality == part.Quality &&
                now.Effect1 == part.Effect1 && now.Effect2 == part.Effect2;
        }

        internal static bool SameShape(GridShape a, GridShape b) => ItemGridMove.SameShape(a, b);

        private static bool Known(string effect)
        {
            switch (effect)
            {
                case null: case "": case "MODULE_EFFECT_PREMIUM": case "MODULE_EFFECT_INFERIOR":
                case "MODULE_EFFECT_OVERVOLTED": case "MODULE_EFFECT_DEGRADING": case "MODULE_EFFECT_LEARNING_ALGORITHM":
                case "MODULE_EFFECT_RECEIVER": case "MODULE_EFFECT_TOP_MOUNT": case "MODULE_EFFECT_SIDE_MOUNT":
                case "MODULE_EFFECT_NEGATIVE_FEEDBACK": case "NODE_EFFECT_SUPPORT": return true;
                default: return false;
            }
        }

        internal static bool Evaluate(IReadOnlyList<Part> parts, IReadOnlyList<GridShape> shapes, out Values total)
        {
            total = default;
            for (int i = 0; i < parts.Count; i++)
            {
                Part part = parts[i]; GridShape shape = shapes[i];
                if (shape == null) continue;
                if (!Known(part.Effect1) || !Known(part.Effect2)) return false;
                Values value = new Values { Performance = part.Performance, Efficiency = part.Efficiency, Quality = part.Quality };
                Apply(part.Effect1, i, parts, shapes, ref value);
                Apply(part.Effect2, i, parts, shapes, ref value);
                if (part.Module || part.NodeType) total.Add(value);
            }
            return true;
        }

        private static void Apply(string effect, int index, IReadOnlyList<Part> parts, IReadOnlyList<GridShape> shapes, ref Values value)
        {
            Part item = parts[index]; GridShape shape = shapes[index];
            if (effect == "MODULE_EFFECT_TOP_MOUNT" && shape.IsTouchingTop()) value.AddPercentage(item, 20);
            else if (effect == "MODULE_EFFECT_SIDE_MOUNT" && shape.IsTouchingLeft()) value.AddPercentage(item, 20);
            else if (effect == "MODULE_EFFECT_RECEIVER")
            {
                int count = 0;
                for (int i = 0; i < parts.Count; i++) if (shapes[i] != null && parts[i].NodeTag && Adjacent(shape, shapes[i])) count++;
                value.AddPercentage(item, 10 * count);
            }
            else if (effect == "MODULE_EFFECT_NEGATIVE_FEEDBACK" || effect == "NODE_EFFECT_SUPPORT")
            {
                int performance = 0, efficiency = 0, quality = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    Part other = parts[i];
                    if (i == index || shapes[i] == null || !Adjacent(shape, shapes[i])) continue;
                    if (effect == "NODE_EFFECT_SUPPORT")
                    {
                        if (!other.ModuleType) continue;
                        performance += other.Performance; efficiency += other.Efficiency; quality += other.Quality;
                    }
                    else
                    {
                        if (other.Performance < 0) performance += other.Performance;
                        if (other.Efficiency < 0) efficiency += other.Efficiency;
                        if (other.Quality < 0) quality += other.Quality;
                    }
                }
                if (effect == "NODE_EFFECT_SUPPORT")
                    value = new Values { Performance = performance / 5, Efficiency = efficiency / 5, Quality = quality / 5 };
                else
                {
                    value.Performance += performance / 4; value.Efficiency += efficiency / 4; value.Quality += quality / 4;
                }
            }
        }

        // Mirrors GameGridInventory.IsAdjacent, including layer-sensitive overlap.
        private static bool Adjacent(GridShape first, GridShape second)
        {
            var shifted = new GridShapeBuilder(first);
            shifted.minX++;
            if (second.Intersects(shifted)) return true;
            shifted.minX -= 2;
            if (second.Intersects(shifted)) return true;
            shifted.minX++; shifted.minY++;
            if (second.Intersects(shifted)) return true;
            shifted.minY -= 2;
            return second.Intersects(shifted);
        }

        internal static Outcome Search(GameGridInventory bay, List<Part> parts, int target, out Plan result)
        {
            result = null;
            if (bay == null || target < 0 || target > 2 || parts.Count == 0 || parts.Count > 12) return Outcome.Unsupported;
            var beforeShapes = parts.ConvertAll(p => p.Existing ? p.Original : null);
            if (!Evaluate(parts, beforeShapes, out Values before)) return Outcome.Unsupported;
            var clock = Stopwatch.StartNew();
            var ignored = parts.FindAll(p => p.Existing && !p.Fixed).ConvertAll(p => p.Item);
            // Fill free cells first, then allow existing movable items to change pose.
            for (int stage = 0; stage < 2; stage++)
            {
                foreach (Part part in parts)
                {
                    part.Poses.Clear();
                    if (part.Existing && (part.Fixed || stage == 0)) { part.Poses.Add(part.Original); continue; }
                    if (!bay.MayHaveValidInventorySlot(part.Item)) return Outcome.Unsupported;
                    var pose = new GridShapeBuilder(part.Original);
                    for (int flip = 0; flip < 2; flip++) for (int turn = 0; turn < 4; turn++)
                    {
                        pose.SetFlipped(part.Original.flipped ^ (flip != 0)).SetOrientation((part.Original.orientation + turn) % 4);
                        for (int y = 0; y < bay.inventoryShape.height; y++)
                        for (int x = 0; x < bay.inventoryShape.width; x++)
                        {
                            if (clock.ElapsedMilliseconds > 200) return Outcome.Timeout;
                            pose.SetPosition(x, y);
                            if (!bay.CheckShapePlacement(part.Item, pose, stage == 0 ? new List<GameItem>() : ignored)) continue;
                            GridShape clone = pose.Clone();
                            if (!part.Poses.Exists(previous => SameCells(previous, clone))) part.Poses.Add(clone);
                        }
                    }
                    if (part.Existing && part.Poses.Count == 0) return Outcome.Unsupported;
                }
                var search = new SearchState(bay, parts, before, target, clock);
                search.Visit(0);
                if (search.TimedOut) return Outcome.Timeout;
                if (search.Best != null) { result = search.Best; return Outcome.Improved; }
            }
            return Outcome.NoImprovement;
        }

        private static bool SameCells(GridShape a, GridShape b)
        {
            if (a.minX != b.minX || a.minY != b.minY || a.maxX != b.maxX || a.maxY != b.maxY) return false;
            for (int y = a.minY; y <= a.maxY; y++) for (int x = a.minX; x <= a.maxX; x++)
                if (a.Get(x, y) != b.Get(x, y)) return false;
            return true;
        }

        private sealed class SearchState
        {
            private readonly GameGridInventory bay;
            private readonly List<Part> parts;
            private readonly List<GridShape> positions;
            private readonly Values before;
            private readonly int target;
            private readonly Stopwatch clock;
            private int visits;
            internal bool TimedOut;
            internal Plan Best;

            internal SearchState(GameGridInventory bay, List<Part> parts, Values before, int target, Stopwatch clock)
            {
                this.bay = bay; this.parts = parts; this.before = before; this.target = target; this.clock = clock;
                positions = parts.ConvertAll(_ => (GridShape)null);
            }

            internal void Visit(int index)
            {
                if (TimedOut) return;
                if (++visits > 40000 || clock.ElapsedMilliseconds > 200) { TimedOut = true; return; }
                if (index == parts.Count)
                {
                    if (!Evaluate(parts, positions, out Values after) || after.At(target) <= before.At(target)) return;
                    int moves = 0;
                    for (int i = 0; i < parts.Count; i++) if (parts[i].Existing && !SameShape(parts[i].Original, positions[i])) moves++;
                    int rank = Best == null ? 1 : CompareBeforeSpace(after, moves, Best);
                    if (rank < 0) return;
                    int space = LargestEmpty(bay.inventoryShape, positions);
                    if (Best == null || rank > 0 || space > Best.OpenSpace)
                        Best = new Plan(new List<Part>(parts), new List<GridShape>(positions)) { Before = before, After = after, Moves = moves, OpenSpace = space };
                    return;
                }
                Part part = parts[index];
                foreach (GridShape pose in part.Poses)
                {
                    bool clear = true;
                    for (int i = 0; i < index; i++) if (positions[i] != null && pose.Intersects(positions[i])) { clear = false; break; }
                    if (!clear) continue;
                    positions[index] = pose; Visit(index + 1);
                    if (TimedOut) break;
                }
                positions[index] = null;
                if (!part.Existing) Visit(index + 1);
            }

            private int CompareBeforeSpace(Values next, int moves, Plan best)
            {
                int delta = next.At(target).CompareTo(best.After.At(target));
                if (delta != 0) return delta;
                int loss = 0, bestLoss = 0;
                for (int i = 0; i < 3; i++) if (i != target)
                {
                    loss += Math.Max(0, before.At(i) - next.At(i));
                    bestLoss += Math.Max(0, before.At(i) - best.After.At(i));
                }
                return loss != bestLoss ? bestLoss.CompareTo(loss) : best.Moves.CompareTo(moves);
            }
        }

        private static int LargestEmpty(GridShape board, IReadOnlyList<GridShape> shapes)
        {
            int width = board.width, height = board.height, largest = 0;
            var occupied = new bool[width, height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                occupied[x, y] = board.Get(x, y) != 0;
                foreach (GridShape shape in shapes) if (shape != null && shape.Get(x, y) == 1) { occupied[x, y] = true; break; }
            }
            var queue = new Queue<(int x, int y)>();
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                if (occupied[x, y]) continue;
                int area = 0; occupied[x, y] = true; queue.Enqueue((x, y));
                while (queue.Count > 0)
                {
                    var cell = queue.Dequeue(); area++;
                    foreach (var d in Neighbors)
                    {
                        int nx = cell.x + d.x, ny = cell.y + d.y;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height || occupied[nx, ny]) continue;
                        occupied[nx, ny] = true; queue.Enqueue((nx, ny));
                    }
                }
                largest = Math.Max(largest, area);
            }
            return largest;
        }
    }
}
