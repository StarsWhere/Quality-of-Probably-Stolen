using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json;

namespace ShopAssist
{
    internal sealed class YeastReturn : IDisposable
    {
        private const string DataKey = "yeastOrigins";
        private static YeastReturn active;
        private static bool recognized;
        private readonly ConfigEntry<bool> enabled;
        private readonly Action<string> log;
        private OriginBook book;
        private string run;
        private int slot;
        private Brew brewing;
        private GameItem allowOriginal;

        private sealed class OriginBook
        {
            [JsonRequired] public int version = 1;
            [JsonRequired] public List<Origin> entries = new List<Origin>();
        }

        private sealed class Origin
        {
            [JsonRequired] public int yeast, bottle, x, y, rotation;
            [JsonRequired] public string location;
            [JsonRequired] public bool flip;
        }

        private sealed class Transfer
        {
            internal GameItem Yeast, Bottle;
            internal ShopInventory.Location Source;
            internal GameGridInventory From, To;
            internal GridShape Before, After;
            internal int Count;
        }

        private sealed class Brew
        {
            internal GameItem Bottle;
            internal List<GameItem> Contents;
            internal readonly Dictionary<GameItem, Transfer> Returns = new Dictionary<GameItem, Transfer>();
        }

        internal YeastReturn(ShopSettings settings, Action<string> log)
        {
            this.log = log;
            enabled = settings.Toggle("f37.enabled", "f37.tip");
        }

        internal void Start()
        {
            active = this;
            ModHook.OnGameLoadedInit += Reset;
            if (!recognized) log("F37: wine recipe signature not recognized; native brewing retained.");
        }

        private static bool Reusable(GameItem item) => item != null && item.IsTag(WineHelper.WINE_YEAST_REUSABLE_TAG);

        private static GameItem BottleAt(GameInventory inventory)
        {
            var owner = inventory?.GetParentItem();
            if (owner?.identifier != "wine_bottle" || !(owner.contentWindow?.childElement is MachineryPanelRow row)) return null;
            var nodes = MachineryContainerUtils.Collect(row.GetPanel("ingredients")?.content);
            return nodes.Count == 1 && ReferenceEquals(nodes[0], inventory) ? owner : null;
        }

        private void Reset() { run = null; book = null; brewing = null; allowOriginal = null; }

        private void Load()
        {
            var store = PlayerStore.Instance;
            if (run == store.runID && slot == store.saveSlotId) return;
            run = store.runID;
            slot = store.saveSlotId;
            try
            {
                var raw = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                book = raw == null ? new OriginBook() : ShopData.Decode<OriginBook>(raw, 65536);
                if (book == null || book.version != 1 || book.entries == null || book.entries.Count > 256 ||
                    book.entries.Any(o => o == null || o.yeast <= 0 || o.bottle <= 0 || string.IsNullOrEmpty(o.location) ||
                        o.rotation < 0 || o.rotation > 3 || o.x < 0 || o.y < 0) ||
                    book.entries.Select(o => o.yeast).Distinct().Count() != book.entries.Count)
                    throw new FormatException("Invalid yeast origin book");
            }
            catch (Exception ex) { book = null; log("F37: cannot read origin book. Using safe fallback: " + ex.Message); }
        }

        private void Write() => ModHelper.SetModData(ModEntry.Id, DataKey, ShopData.Encode(book));

        [HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce))]
        private static class Moved
        {
            private static void Prefix(SlotMarker __instance, int amount, out Transfer __state)
            {
                __state = null;
                var a = active;
                var item = __instance.item;
                if (a == null || a.brewing != null || !recognized || !Reusable(item) || !ShopInventory.InStore) return;
                var to = __instance.inventory as GameGridInventory;
                var from = item.parentInventory as GameGridInventory;
                if (to == from || from == null || (BottleAt(from) == null && BottleAt(to) == null)) return;
                var bottle = BottleAt(to);
                var source = a.enabled.Value && bottle != null && __instance.targetItem == null &&
                    (amount < 0 || amount == item.unitCount) && __instance.numTransfer >= item.unitCount ?
                    ShopInventory.OwnedLocations(false).Find(l => l.Ordinary && l.Inventory == from) : null;
                __state = new Transfer { Yeast = item, Bottle = bottle, From = from, To = to,
                    Before = item.modifiedShape.Clone(), Source = source };
            }

            private static void Postfix(Transfer __state, int __result)
            {
                var a = active;
                if (__state == null || __result <= 0 || a == null || __state.Yeast.parentInventory == __state.From) return;
                a.Load();
                if (a.book == null) return;
                var item = __state.Yeast;
                bool changed = a.book.entries.RemoveAll(o => o.yeast == item.uniqueId) > 0;
                if (a.enabled.Value && __state.Source != null && __state.Bottle != null && item.parentInventory == __state.To &&
                    __result == item.unitCount && a.book.entries.Count < 256)
                {
                    string key = __state.Source.Key(true);
                    if (key != null)
                    {
                        var p = __state.Before;
                        a.book.entries.Add(new Origin { yeast = item.GetUniqueID(), bottle = __state.Bottle.GetUniqueID(),
                            location = key, x = p.minX, y = p.minY, rotation = p.orientation, flip = p.flipped });
                        changed = true;
                    }
                }
                if (changed) a.Write();
            }
        }

        private bool Prepare(GameItem bottle)
        {
            if (!recognized || !enabled.Value || !ShopInventory.InStore || !ShopInventory.Owned(bottle) ||
                bottle.identifier != "wine_bottle" || bottle.GetTagReadonly(WineHelper.WINE_CONTAINER_STATE).GetInt() != 0) return true;
            if (ReferenceEquals(allowOriginal, bottle)) { allowOriginal = null; return true; }
            if (brewing != null) return false;
            var contents = GraphUtils.FindAllChildrenType<GameItem>(bottle);
            if (!contents.Any(Reusable)) return true;
            Load();
            var brew = new Brew { Bottle = bottle, Contents = contents };
            var places = ShopInventory.OwnedLocations(false);
            var taken = new Dictionary<GameGridInventory, List<GridShape>>();
            var fallback = new[] { EmporiumEntry.Instance.invElement, EmporiumEntry.Instance.backInvinvElement,
                EmporiumEntry.Instance.showcaseElement, EmporiumEntry.Instance.frontInvinvElement };
            foreach (var yeast in contents.Where(Reusable))
            {
                if (!(yeast.parentInventory is GameGridInventory from) || BottleAt(from) != bottle ||
                    !ShopInventory.Movable(yeast)) { NoSpace(bottle); return false; }
                var origin = book?.entries.Find(o => o.yeast == yeast.uniqueId && o.bottle == bottle.uniqueId);
                SlotMarker marker = null;
                if (origin != null)
                {
                    var home = places.Find(l => l.Ordinary && l.Key() == origin.location)?.Inventory as GameGridInventory;
                    if (home != null)
                    {
                        var shape = new GridShapeBuilder(yeast.modifiedShape).SetTransform(origin.x, origin.y, origin.flip, origin.rotation);
                        taken.TryGetValue(home, out var occupied);
                        if (!GridPlacements.Overlaps(shape, occupied)) marker = GridPlacements.Exact(home, yeast, shape);
                    }
                }
                if (marker == null)
                    foreach (var target in fallback)
                    {
                        if (!(target is GameGridInventory grid)) continue;
                        taken.TryGetValue(grid, out var occupied);
                        marker = GridPlacements.Empty(grid, yeast, occupied);
                        if (marker != null) break;
                    }
                if (marker == null) { NoSpace(bottle); return false; }
                var dest = (GameGridInventory)marker.inventory;
                if (!taken.TryGetValue(dest, out var shapes)) taken.Add(dest, shapes = new List<GridShape>());
                var after = marker.itemGridShape.Clone();
                shapes.Add(after);
                brew.Returns.Add(yeast, new Transfer { Yeast = yeast, From = from, To = dest, Before = yeast.modifiedShape.Clone(),
                    After = after, Count = yeast.unitCount });
            }

            // Stage all reusable instances while their ingredient grid still exists. Vanilla then evaluates the
            // original ingredient snapshot, including yeast strength, and consumes the ordinary ingredients.
            brewing = brew;
            var moved = new List<Transfer>();
            try
            {
                foreach (var move in brew.Returns.Values)
                {
                    moved.Add(move);
                    if (!Place(move.Yeast, move.To, move.After) || move.Yeast.unitCount != move.Count ||
                        move.From.childItems.Contains(move.Yeast) || !move.To.childItems.Contains(move.Yeast))
                        throw new InvalidOperationException("return destination changed");
                }
                return true;
            }
            catch (Exception ex)
            {
                bool restored = true;
                for (int i = moved.Count - 1; i >= 0; i--)
                {
                    try { restored &= Rollback(moved[i]); }
                    catch (Exception error) { restored = false; log("F37: could not restore an ingredient: " + error.Message); }
                }
                brewing = null;
                log("F37: brewing cancelled. Original ingredient placements restored=" + restored + ". " + ex.Message);
                ShopNotice.Show(ShopSettings.Text(restored ? "f37.retry" : "inv.inspect"));
                return false;
            }
        }

        private static bool Place(GameItem item, GameGridInventory grid, GridShape shape)
        {
            var slot = GridPlacements.Exact(grid, item, shape);
            return slot != null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid;
        }

        private static bool SamePose(GridShape a, GridShape b) => a != null && b != null &&
            a.minX == b.minX && a.minY == b.minY && a.orientation == b.orientation && a.flipped == b.flipped;

        private static bool RestorePose(GameGridInventory grid, GameItem item, GridShape shape)
        {
            if (grid == null || item == null || !ReferenceEquals(item.parentInventory, grid) || !grid.childItems.Contains(item)) return false;
            if (!SamePose(item.modifiedShape, shape))
            {
                // SlotMarker changes the pose even when an external hook refuses Expel.
                if (!grid.CheckShapePlacement(item, shape, new List<GameItem> { item })) return false;
                item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(shape);
                grid.Validate();
            }
            return SamePose(item.modifiedShape, shape);
        }

        private static bool Rollback(Transfer move)
        {
            var item = move.Yeast;
            if (item == null || item is GameItemElement element && element.IsDestroyed()) return false;
            if (ReferenceEquals(item.parentInventory, move.From))
                return RestorePose(move.From, item, move.Before) && item.unitCount == move.Count && !move.To.childItems.Contains(item);
            var current = item.parentInventory as GameGridInventory;
            // Do not take back an item moved to an unrelated owner by another callback.
            if (item.parentInventory != null && !ReferenceEquals(current, move.To)) return false;
            var previousPose = item.modifiedShape.Clone();
            bool placed = false;
            try { placed = Place(item, move.From, move.Before); }
            finally
            {
                if (!placed && current != null && ReferenceEquals(item.parentInventory, current))
                    RestorePose(current, item, previousPose);
            }
            return placed && item.unitCount == move.Count && move.From.childItems.Contains(item) &&
                !move.To.childItems.Contains(item) && SamePose(item.modifiedShape, move.Before);
        }

        private void NoSpace(GameItem bottle)
        {
            var ui = GenericConfirmationUIManager.Instance;
            if (ui == null) { ShopNotice.Show(ShopSettings.Text("f37.full")); return; }
            string forRun = run;
            ui.OpenPanel(ShopSettings.Text("f37.full"), ShopSettings.Text("f37.original"), ShopSettings.Text("f37.free"),
                () =>
                {
                    ui.ClosePanel();
                    if (!ShopInventory.InStore || PlayerStore.Instance.runID != forRun || !ShopInventory.Owned(bottle)) return;
                    allowOriginal = bottle;
                    try
                    {
                        if (WineHelper.TryStartFermentation(bottle) &&
                            bottle.GetTagReadonly(WineHelper.WINE_CONTAINER_STATE).GetInt() != 0 && book != null &&
                            book.entries.RemoveAll(o => o.bottle == bottle.uniqueId) > 0) Write();
                    }
                    finally { allowOriginal = null; }
                }, () => ui.ClosePanel());
        }

        [HarmonyPatch(typeof(WineHelper), nameof(WineHelper.TryStartFermentation))]
        private static class Attempt
        {
            private static void Postfix(GameItem item, ref bool __result)
            {
                if (active != null && active.enabled.Value && recognized && __result &&
                    item.GetTagReadonly(WineHelper.WINE_CONTAINER_STATE).GetInt() == 0) __result = false;
            }
        }

        [HarmonyPatch(typeof(WineHelper), nameof(WineHelper.StartFermentation))]
        private static class Recipe
        {
            private static bool Prefix(GameItem item, out Brew __state)
            {
                __state = null;
                if (active == null) return true;
                bool ok = active.Prepare(item);
                if (ok && active.brewing?.Bottle == item) __state = active.brewing;
                return ok;
            }

            private static Exception Finalizer(Brew __state, Exception __exception)
            {
                var a = active;
                if (__state != null && a != null && a.brewing == __state)
                {
                    a.brewing = null;
                    if (a.book != null && a.book.entries.RemoveAll(o =>
                        __state.Returns.Keys.Any(y => y.uniqueId == o.yeast)) > 0) a.Write();
                }
                return __exception;
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var originalReturn = AccessTools.Method(typeof(GraphUtils), nameof(GraphUtils.EmporiumTryAdd), new[] { typeof(GameItem) });
                var originalExpel = AccessTools.Method(typeof(GameInventory), nameof(GameInventory.Expel));
                var getContents = AccessTools.Method(typeof(GraphUtils), nameof(GraphUtils.FindAllChildrenType)).MakeGenericMethod(typeof(GameItem));
                var snapshot = code.Where(c => c.Calls(getContents)).ToList();
                var expel = code.Where(c => c.Calls(originalExpel)).ToList();
                var giveBack = code.Where(c => c.Calls(originalReturn)).ToList();
                recognized = snapshot.Count == 1 && expel.Count == 1 && giveBack.Count == 1 &&
                    code.IndexOf(snapshot[0]) < code.IndexOf(expel[0]) && code.IndexOf(giveBack[0]) == code.IndexOf(expel[0]) + 3;
                if (!recognized) return code;
                Replace(snapshot[0], nameof(ReadContents)); Replace(expel[0], nameof(Expel)); Replace(giveBack[0], nameof(Return));
                return code;
            }

            private static void Replace(CodeInstruction code, string name)
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(YeastReturn), name);
            }
        }

        private static List<GameItem> ReadContents(GraphNodeStorage node, Func<GameItem, bool> filter, Func<GameItem, bool> through)
        {
            var brew = active?.brewing;
            return brew != null && ReferenceEquals(node, brew.Bottle) && filter == null && through == null ?
                brew.Contents : GraphUtils.FindAllChildrenType(node, filter, through);
        }

        private static bool Expel(GameInventory source, GameItem item) =>
            active?.brewing?.Returns.ContainsKey(item) == true || source.Expel(item);
        private static void Return(GameItem item)
        {
            if (active?.brewing?.Returns.ContainsKey(item) != true) GraphUtils.EmporiumTryAdd(item);
        }

        public void Dispose()
        {
            ModHook.OnGameLoadedInit -= Reset;
            if (ReferenceEquals(active, this)) active = null;
            Reset();
        }
    }
}
