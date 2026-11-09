using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using ShopAssist;
using UnityEngine;

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
        CheckNativeInputs();
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

    static void CheckNativeInputs()
    {
        var input = typeof(ModEntry).Assembly.GetType("ShopAssist.GroupRotationInput", true);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        MethodInfo press = input.GetMethod("Press", flags), scroll = input.GetMethod("Scroll", flags);
        int Press(bool swap, params KeyCode[] keys) => Convert.ToInt32(press.Invoke(null,
            new object[] { keys.ToHashSet(), KeyCode.F7, KeyCode.F8, KeyCode.Mouse1, swap }));
        int Scroll(float x, float y, bool swap = false, bool ignore = false) => Convert.ToInt32(scroll.Invoke(null,
            new object[] { new Vector2(x, y), swap, ignore }));
        Require(Press(false, KeyCode.F7) == 1 && Press(false, KeyCode.F8) == 2,
            "Remapped native clockwise and counterclockwise keys must drive group rotation");
        Require(Press(false, KeyCode.Q, KeyCode.E) == 0, "Default keys must not leak through after native remapping");
        Require(Press(false, KeyCode.Mouse1) == 0, "A native flip gesture must not become a group rotation");
        Require(Press(true, KeyCode.Mouse1) == 1, "Swapping native rotate/flip must make secondary click rotate the group");
        Require(Press(true, KeyCode.F8) == 2 && Press(true, KeyCode.F7, KeyCode.F8) == 3,
            "Explicit rotation bindings and simultaneous native presses must retain their meaning after swapping");
        Require(Press(true, KeyCode.F7, KeyCode.Mouse1) == 1, "Two clockwise bindings in one press dispatch must rotate only once");
        Require(Scroll(0, 1) == 2 && Scroll(0, -1) == 1, "Native wheel direction must match single-item rotation");
        Require(Scroll(1, 0) == 2 && Scroll(-1, 0) == 1, "Native horizontal wheel rotation must remain available");
        Require(Scroll(1, -1) == 0 && Scroll(0, .9f) == 0, "Wheel axes must cancel and fractional native ticks must truncate");
        Require(Scroll(0, 1, swap: true) == 0 && Scroll(0, -1, swap: true) == 0,
            "Swapped native wheel flipping must not rotate the group");
        Require(Scroll(0, 1, ignore: true) == 0, "The native ignore-scroll modifier must suppress group rotation");
        Console.WriteLine("PASS native remapped rotation bindings, swapped secondary click, simultaneous keys and wheel semantics");
    }
}
