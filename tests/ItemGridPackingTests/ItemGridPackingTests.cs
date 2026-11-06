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
        CheckPack();
        CheckPages();
        CheckRows();
        Console.WriteLine("PASS actual ItemGrid packing matches native legacy placement and capped row counts");
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
    private static GridShape LegacyPack(GridShape shape, int columns, int maxRows, HashSet<long> occupied, ref int rows)
    {
        if (shape == null || shape.globalWidth > columns || shape.globalHeight > maxRows) return null;
        for (int y = 0; y + shape.globalHeight <= maxRows; y++)
        for (int x = 0; x + shape.globalWidth <= columns; x++)
        {
            GridShape pose = new GridShapeBuilder(shape).SetPosition(x, y).Build();
            bool clear = true;
            for (int py = 0; py < pose.globalHeight && clear; py++) for (int px = 0; px < pose.globalWidth; px++)
                if (pose.Get(x + px, y + py) == 1 && occupied.Contains(Key(x + px, y + py))) { clear = false; break; }
            if (!clear) continue;
            for (int py = 0; py < pose.globalHeight; py++) for (int px = 0; px < pose.globalWidth; px++)
                if (pose.Get(x + px, y + py) == 1) occupied.Add(Key(x + px, y + py));
            rows = Math.Max(rows, y + pose.globalHeight); return pose;
        }
        return null;
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
    private static void CheckPack()
    {
        var random = new System.Random(9381);
        for (int kind = 0; kind < 4; kind++) foreach (bool flip in new[] { false, true })
        for (int turn = 0; turn < 4; turn++)
        for (int test = 0; test < 80; test++)
        {
            GridShape shape = NewShape(kind, flip, turn, random.Next(-4, 8), random.Next(-4, 8));
            GridShape original = shape.Clone();
            int columns = random.Next(1, 9), maxRows = random.Next(1, 9), oldRows = 0, newRows = 0;
            var oldOccupied = new HashSet<long>();
            for (int y = 0; y < maxRows; y++) for (int x = 0; x < columns; x++)
                if (random.Next(5) == 0) oldOccupied.Add(Key(x, y));
            var newOccupied = new HashSet<long>(oldOccupied);
            GridShape expected = LegacyPack(shape, columns, maxRows, oldOccupied, ref oldRows);
            object[] args = { shape, columns, maxRows, newOccupied, newRows };
            var actual = (GridShape)pack.Invoke(null, args); newRows = (int)args[4];
            Require(SamePose(actual, expected) && oldRows == newRows && oldOccupied.SetEquals(newOccupied) && SamePose(shape, original),
                $"Packing mismatch: kind={kind}, flip={flip}, turn={turn}, test={test}");
        }
    }
    private static int LegacyRows(IEnumerable<GridShape> shapes, int columns, RectInt reserved)
    {
        int rows = Math.Max(0, reserved.yMax); var occupied = Reserve(reserved);
        foreach (var shape in shapes)
        {
            if (shape == null || shape.globalWidth < 1 || shape.globalHeight < 1 || shape.globalWidth > columns) continue;
            LegacyPack(shape, columns, rows + shape.globalHeight, occupied, ref rows);
        }
        return rows;
    }
    private static IList Entries(IEnumerable<GridShape> shapes)
    {
        var entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
        foreach (var shape in shapes) entries.Add(entryConstructor.Invoke(new[] { fakeItem, fakeInventory, shape }));
        return entries;
    }
    private static List<List<int>> LegacyPages(IList<GridShape> shapes, int columns, int visibleRows, RectInt reserved)
    {
        var remaining = Enumerable.Range(0, shapes.Count).ToList();
        var pages = new List<List<int>>();
        while (remaining.Count > 0)
        {
            var page = new List<int>();
            GridShape first = shapes[remaining[0]];
            if (first.globalWidth > columns || first.globalHeight > visibleRows)
            { page.Add(remaining[0]); remaining.RemoveAt(0); pages.Add(page); continue; }
            var occupied = Reserve(reserved); int rows = 0;
            for (int i = 0; i < remaining.Count;)
            {
                int index = remaining[i];
                if (LegacyPack(shapes[index], columns, visibleRows, occupied, ref rows) == null) { i++; continue; }
                page.Add(index); remaining.RemoveAt(i);
            }
            if (page.Count == 0) { page.Add(remaining[0]); remaining.RemoveAt(0); }
            pages.Add(page);
        }
        if (pages.Count == 0) pages.Add(new List<int>());
        return pages;
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
            var actual = (IEnumerable)paginate.Invoke(null, new object[] { entries, columns, visibleRows, reserved });
            var actualPages = actual.Cast<IEnumerable>().Select(page => page.Cast<object>().Select(entries.IndexOf).ToList()).ToList();
            var expected = LegacyPages(shapes, columns, visibleRows, reserved);
            Require(actualPages.Count == expected.Count && actualPages.Zip(expected).All(pair => pair.First.SequenceEqual(pair.Second)),
                $"Page order or membership changed: test={test}");
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
            int expected = LegacyRows(shapes, columns, reserved);
            IList entries = Entries(shapes);
            foreach (int limit in new[] { 0, 1, 3, 8, 24, int.MaxValue })
            {
                int actual = (int)packedRows.Invoke(null, new object[] { entries, columns, reserved, limit });
                Require(actual == Math.Min(expected, limit), $"Rows mismatch: test={test}, limit={limit}, expected={expected}, actual={actual}");
            }
        }
        var many = Entries(Enumerable.Range(0, 1000).Select(_ => NewShape(0, false, 0, 0, 0)));
        Require((int)packedRows.Invoke(null, new object[] { many, 8, new RectInt(), 10 }) == 10,
            "A dense list did not stop at the requested row limit");
    }
}
