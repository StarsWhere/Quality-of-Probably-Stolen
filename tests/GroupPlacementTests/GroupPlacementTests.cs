using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using ShopAssist;

static class GroupPlacementTests
{
    static void Main()
    {
        string game = Environment.GetEnvironmentVariable("PROBABLY_STOLEN_GAME_DIR") ?? @"D:\SteamLibrary\steamapps\common\Probably Stolen";
        string managed = Path.Combine(game, "Probably Stolen_Data", "Managed");
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string file = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
        Check();
    }

    static HashSet<(int, int)> Cells(GridShape shape)
    {
        var points = new HashSet<(int, int)>();
        for (int y = shape.minY; y <= shape.maxY; y++) for (int x = shape.minX; x <= shape.maxX; x++)
            if (shape.Get(x, y) == 1) points.Add((x, y));
        return points;
    }

    static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    static void Check()
    {
        foreach (bool flipped in new[] { false, true }) foreach (int orientation in Enumerable.Range(0, 4))
        {
            var first = new GridShapeBuilder(2, 3).SetData("110010", 2).SetTransform(4, 5, flipped, orientation).Build();
            var second = new GridShapeBuilder(3, 2).SetData("101110", 3).SetTransform(9, 2, !flipped, (orientation + 1) % 4).Build();
            var originals = new List<GridShape> { first, second };
            foreach (bool ccw in new[] { false, true })
            {
                GridShape turned = new GridShapeBuilder(first).SetOrientation((first.orientation + (ccw ? 1 : 3)) % 4).Build();
                foreach (var point in Cells(first))
                {
                    int x = point.Item1 - first.minX, y = point.Item2 - first.minY;
                    var local = first.InverseTransform(point.Item1, point.Item2);
                    var native = turned.ForwardTransform(local.Item1, local.Item2);
                    var anchor = GroupGeometry.RotateGrab(first, x, y, ccw);
                    Require(anchor.x == native.Item1 - turned.minX && anchor.y == native.Item2 - turned.minY,
                        $"Grabbed occupied cell changed: flipped={flipped}, orientation={orientation}, ccw={ccw}");
                    var restored = GroupGeometry.RotateGrab(turned, anchor.x, anchor.y, !ccw);
                    Require(restored.x == x && restored.y == y, "Reverse rotation changed the grab anchor");
                }
                var rotated = GroupGeometry.Rotate(originals, first.minX, first.minY, ccw);
                int rawX = ccw ? first.minY : -first.minY - first.globalHeight + 1;
                int rawY = ccw ? -first.minX - first.globalWidth + 1 : first.minX;
                int sx = first.minX - rawX, sy = first.minY - rawY;
                for (int i = 0; i < originals.Count; i++)
                {
                    var expected = Cells(originals[i]).Select(p => ccw ? (p.Item2 + sx, -p.Item1 + sy) : (-p.Item2 + sx, p.Item1 + sy)).ToHashSet();
                    Require(expected.SetEquals(Cells(rotated[i])), $"Incorrect rotated mask: flipped={flipped}, orientation={orientation}, ccw={ccw}, item={i}");
                }
                var back = GroupGeometry.Rotate(rotated, first.minX, first.minY, !ccw);
                for (int i = 0; i < originals.Count; i++) Require(Cells(originals[i]).SetEquals(Cells(back[i])), "Reverse rotation changed the formation");
            }
            var four = originals;
            for (int i = 0; i < 4; i++) four = GroupGeometry.Rotate(four, first.minX, first.minY, true);
            for (int i = 0; i < originals.Count; i++) Require(Cells(originals[i]).SetEquals(Cells(four[i])), "Four turns changed the formation");
        }
        Console.WriteLine("PASS group rotation preserves irregular masks, flipped poses, relative offsets and occupied grab anchors");
    }
}
