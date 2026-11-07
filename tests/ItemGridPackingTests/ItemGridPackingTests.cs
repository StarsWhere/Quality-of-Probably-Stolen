using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using UnityEngine;

static class ItemGridPackingTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static MethodInfo pack, packedRows, paginate;
    private static ConstructorInfo entryConstructor;
    private static Type entryType;
    private static object fakeItem, fakeInventory;

    static void Main()
    {
        string game = Environment.GetEnvironmentVariable("PROBABLY_STOLEN_GAME_DIR") ?? @"D:\SteamLibrary\steamapps\common\Probably Stolen";
        string managed = Path.Combine(game, "Probably Stolen_Data", "Managed");
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Check();
    }

    private static void Check()
    {
        var grid = typeof(ShopAssist.ModEntry).Assembly.GetType("ShopAssist.ItemGrid", true);
        pack = grid.GetMethod("Pack", Flags);
        packedRows = grid.GetMethod("PackedRows", Flags);
        paginate = grid.GetMethod("Paginate", Flags);
        entryType = grid.GetNestedType("Entry", Flags);
        entryConstructor = entryType.GetConstructors(Flags).Single();
        fakeItem = RuntimeHelpers.GetUninitializedObject(typeof(GameItemElement));
        fakeInventory = RuntimeHelpers.GetUninitializedObject(typeof(GameGridInventory));
        CheckPack(); CheckPages(); CheckRows();
        Console.WriteLine("PASS item grid placement, pagination and bounded rows");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static long Key(int x, int y) => ((long)y << 32) | (uint)x;
    private static HashSet<long> Reserve(RectInt reserved)
    {
        var occupied = new HashSet<long>();
        for (int y = Math.Max(0, reserved.yMin); y < reserved.yMax; y++)
            for (int x = Math.Max(0, reserved.xMin); x < reserved.xMax; x++) occupied.Add(Key(x, y));
        return occupied;
    }
    private static bool SamePose(GridShape a, GridShape b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a.minX != b.minX || a.minY != b.minY || a.globalWidth != b.globalWidth || a.globalHeight != b.globalHeight ||
            a.orientation != b.orientation || a.flipped != b.flipped) return false;
        for (int y = 0; y < a.globalHeight; y++) for (int x = 0; x < a.globalWidth; x++)
            if (a.Get(a.minX + x, a.minY + y) != b.Get(b.minX + x, b.minY + y)) return false;
        return true;
    }
    private static GridShape NewShape(int kind, bool flip, int turn, int x, int y)
    {
        var builder = kind switch
        {
            0 => new GridShapeBuilder(1, 1).SetData("1", 1),
            1 => new GridShapeBuilder(2, 3).SetData("110010", 2),
            2 => new GridShapeBuilder(3, 2).SetData("101110", 3),
            _ => new GridShapeBuilder(3, 3).SetData("100010001", 3)
        };
        return builder.SetTransform(x, y, flip, turn).Build();
    }
    private static GridShape Place(GridShape shape, int columns, int maxRows, HashSet<long> occupied, ref int rows)
    {
        object[] args = { shape, columns, maxRows, occupied, rows };
        var actual = (GridShape)pack.Invoke(null, args); rows = (int)args[4];
        return actual;
    }
    private static void CheckPack()
    {
        var random = new System.Random(9381);
        for (int kind = 0; kind < 4; kind++) foreach (bool flip in new[] { false, true })
        for (int turn = 0; turn < 4; turn++) for (int test = 0; test < 80; test++)
        {
            GridShape shape = NewShape(kind, flip, turn, random.Next(-4, 8), random.Next(-4, 8));
            GridShape original = shape.Clone();
            int columns = random.Next(1, 9), maxRows = random.Next(1, 9), rows = 0;
            var occupied = new HashSet<long>();
            for (int y = 0; y < maxRows; y++) for (int x = 0; x < columns; x++)
                if (random.Next(5) == 0) occupied.Add(Key(x, y));
            var before = new HashSet<long>(occupied);
            GridShape actual = Place(shape, columns, maxRows, occupied, ref rows);
            Require(SamePose(shape, original), "Packing changed its source shape");
            if (actual == null) { Require(before.SetEquals(occupied) && rows == 0, "Failed placement changed occupancy"); continue; }
            Require(actual.minX >= 0 && actual.minY >= 0 && actual.minX + actual.globalWidth <= columns &&
                actual.minY + actual.globalHeight <= maxRows && actual.globalWidth == shape.globalWidth &&
                actual.globalHeight == shape.globalHeight && actual.orientation == shape.orientation && actual.flipped == shape.flipped,
                "Placed shape is outside the board or changed its orientation");
            var expected = new HashSet<long>(before);
            for (int y = 0; y < actual.globalHeight; y++) for (int x = 0; x < actual.globalWidth; x++)
            {
                Require(actual.Get(actual.minX + x, actual.minY + y) == shape.Get(shape.minX + x, shape.minY + y), "Placed shape changed its cells");
                if (actual.Get(actual.minX + x, actual.minY + y) != 1) continue;
                Require(expected.Add(Key(actual.minX + x, actual.minY + y)), "Placed item overlaps an occupied cell");
            }
            Require(expected.SetEquals(occupied) && rows == actual.minY + actual.globalHeight, "Occupancy or row count is incorrect");
        }
        var full = Enumerable.Range(0, 4).Select(index => Key(index % 2, index / 2)).ToHashSet();
        int used = 0;
        Require(Place(NewShape(0, false, 0, 0, 0), 2, 2, full, ref used) == null, "Full grid accepted an item");
    }
    private static IList Entries(IEnumerable<GridShape> shapes)
    {
        var entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
        foreach (var shape in shapes) entries.Add(entryConstructor.Invoke(new[] { fakeItem, fakeInventory, shape }));
        return entries;
    }
    private static void CheckPages()
    {
        var random = new System.Random(5587);
        for (int test = 0; test < 100; test++)
        {
            int columns = random.Next(1, 9), visibleRows = random.Next(1, 9);
            var reserved = new RectInt(random.Next(0, columns), random.Next(0, 5), random.Next(0, columns + 1), random.Next(0, 6));
            var shapes = Enumerable.Range(0, random.Next(0, 30)).Select(_ => NewShape(random.Next(4), random.Next(2) != 0,
                random.Next(4), random.Next(-4, 8), random.Next(-4, 8))).ToList();
            IList entries = Entries(shapes);
            var pages = ((IEnumerable)paginate.Invoke(null, new object[] { entries, columns, visibleRows, reserved }))
                .Cast<IEnumerable>().Select(page => page.Cast<object>().Select(entries.IndexOf).ToList()).ToList();
            Require(pages.Count > 0 && pages.SelectMany(page => page).OrderBy(i => i).SequenceEqual(Enumerable.Range(0, shapes.Count)),
                "Pagination lost, duplicated or invented an item");
            foreach (var page in pages)
            {
                var occupied = Reserve(reserved); int rows = 0;
                foreach (int index in page)
                {
                    GridShape shape = shapes[index];
                    if (shape.globalWidth > columns || shape.globalHeight > visibleRows)
                    { Require(page.Count == 1, "Oversized item shares a page"); continue; }
                    if (Place(shape, columns, visibleRows, occupied, ref rows) == null)
                        Require(page.Count == 1, "An unplaceable item must remain alone on its scrollable page");
                }
            }
        }
    }
    private static void CheckRows()
    {
        Require((int)packedRows.GetParameters()[3].DefaultValue == int.MaxValue, "The default row limit changed");
        var random = new System.Random(1946);
        for (int test = 0; test < 120; test++)
        {
            int columns = random.Next(1, 9);
            var reserved = new RectInt(random.Next(0, columns), random.Next(0, 5), random.Next(0, columns + 1), random.Next(0, 6));
            var shapes = Enumerable.Range(0, random.Next(0, 45)).Select(_ => NewShape(random.Next(4), random.Next(2) != 0,
                random.Next(4), random.Next(-4, 8), random.Next(-4, 8))).ToList();
            IList entries = Entries(shapes);
            int full = (int)packedRows.Invoke(null, new object[] { entries, columns, reserved, int.MaxValue });
            foreach (int limit in new[] { 0, 1, 3, 8, 24, int.MaxValue })
            {
                int actual = (int)packedRows.Invoke(null, new object[] { entries, columns, reserved, limit });
                Require(actual == Math.Min(full, limit), $"Rows exceed their cap: test={test}, limit={limit}");
            }
        }
        var many = Entries(Enumerable.Range(0, 1000).Select(_ => NewShape(0, false, 0, 0, 0)));
        Require((int)packedRows.Invoke(null, new object[] { many, 8, new RectInt(), 10 }) == 10,
            "A dense list did not stop at the requested row limit");
    }
}
