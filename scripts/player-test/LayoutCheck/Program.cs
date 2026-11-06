using System;
using System.Collections.Generic;
using ShopAssist;

static class Program
{
    static GridShape Shape(int x, int y, byte layer = 1) => new GridShapeBuilder(1, 1).SetDataFill(layer).SetPosition(x, y);
    static void Check(bool value, string error) { if (!value) throw new Exception(error); }
    static void Main()
    {
        var module = new ModuleLayout.Part { Module = true, ModuleType = true, Performance = 20, Efficiency = -5,
            Effect1 = "MODULE_EFFECT_RECEIVER", Effect2 = "MODULE_EFFECT_TOP_MOUNT" };
        var node = new ModuleLayout.Part { NodeType = true, NodeTag = true, Effect1 = "NODE_EFFECT_SUPPORT" };
        var parts = new List<ModuleLayout.Part> { module, node };
        var poses = new List<GridShape> { Shape(0, 0), Shape(1, 0) };
        Check(ModuleLayout.Evaluate(parts, poses, out var value) && value.Performance == 30 && value.Efficiency == -7,
            "Native receiver + top mount + support-node arithmetic disagreed");
        poses[1] = Shape(1, 0, 2);
        Check(ModuleLayout.Evaluate(parts, poses, out value) && value.Performance == 24 && value.Efficiency == -6,
            "Different grid layers incorrectly counted as adjacent");
        module.Effect1 = "MODULE_EFFECT_NEGATIVE_FEEDBACK"; module.Effect2 = null;
        var negative = new ModuleLayout.Part { Module = true, ModuleType = true, Performance = -11 };
        parts = new List<ModuleLayout.Part> { module, negative }; poses = new List<GridShape> { Shape(0, 0), Shape(1, 0) };
        Check(ModuleLayout.Evaluate(parts, poses, out value) && value.Performance == 7,
            "Negative feedback did not truncate toward zero");
        module.Effect1 = "THIRD_PARTY_EFFECT";
        Check(!ModuleLayout.Evaluate(parts, poses, out value), "Unknown effects must not be predicted");
        Console.WriteLine("PASS module layer adjacency, effects, negative rounding and unknown-effect refusal");
    }
}
