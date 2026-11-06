using System;
using System.Collections.Generic;
using ShopAssist;

internal static class DesktopTests
{
    private static void Main()
    {
        Guid run = Guid.Parse("e4a55bd1-9123-4bb2-bf10-a0b0c0d0e0f0");
        Check(DesktopLayoutRules.MembersKey(new[] { 27, 5 }) == "5,27", "member identity is order independent");
        DesktopLayoutData data = Data(run);
        Check(DesktopLayoutRules.IsValid(data, run), "valid layout");
        Check(!DesktopLayoutRules.IsValid(data, Guid.NewGuid()), "other run rejected");
        data.schemaVersion = 2;
        Check(!DesktopLayoutRules.IsValid(data, run), "future version rejected");
        data = Data(run);
        data.layouts[0].positions[1].id = 5;
        Check(!DesktopLayoutRules.IsValid(data, run), "duplicate item id rejected");
        data = Data(run);
        data.layouts[0].positions[1].order = 0;
        Check(!DesktopLayoutRules.IsValid(data, run), "duplicate layer rejected");
        data = Data(run);
        data.layouts[0].positions[0].x = float.NaN;
        Check(!DesktopLayoutRules.IsValid(data, run), "invalid coordinate rejected");
        data = Data(run);
        data.layouts[0].members = "5,28";
        Check(!DesktopLayoutRules.IsValid(data, run), "membership mismatch rejected");
        data = Data(run);
        data.layouts.Add(data.layouts[0]);
        Check(!DesktopLayoutRules.IsValid(data, run), "ambiguous same-coordinator group rejected");
        var viewport = new DesktopRect(0, 0, 1000, 800);
        var left = new DesktopRect(100, 100, 300, 500);
        var right = new DesktopRect(700, 100, 900, 500);
        Check(DesktopBargainRules.PreferLeft(left, right, viewport, new DesktopRect(700, 200, 900, 400)),
            "automatic docking avoids the viewed item");
        Check(!DesktopBargainRules.PreferLeft(left, right, viewport, new DesktopRect(100, 200, 300, 400)),
            "automatic docking can select the right side");
        Check(DesktopBargainRules.PreferLeft(left, new DesktopRect(900, 100, 1100, 500), viewport, null),
            "automatic docking prefers the side inside the viewport");
        string bargain = new DesktopBargainPosition(run, 2, 1, -500.25f, 75).Encode();
        Check(DesktopBargainPosition.TryDecode(bargain, run, 2, out var position) && position.Mode == 1 && position.X == -500.25f,
            "bargain position round trip");
        Check(!DesktopBargainPosition.TryDecode(bargain, run, 3, out _), "bargain position rejects another slot");
        Check(!DesktopBargainPosition.TryDecode("2|" + run.ToString("N") + "|2|1|0|0", run, 2, out _),
            "unknown bargain position version rejected");
        string lens = new DesktopInspectionPosition(run, 2, -20, 75).Encode();
        Check(DesktopInspectionPosition.TryDecode(lens, run, 2, out var savedLens) && savedLens.X == -20 && savedLens.Y == 75,
            "inspection position round trip");
        Check(!DesktopInspectionPosition.TryDecode(lens, run, 3, out _), "inspection position rejects another slot");
        float oldOffset = -120, oldZoom = 1.5f, newZoom = 2.5f;
        float newOffset = DesktopInspectionRules.Recenter(oldOffset, oldZoom, newZoom);
        Check(Math.Abs(oldOffset / oldZoom - newOffset / newZoom) < 0.001f, "zoom preserves the inspected point");
        Check(Math.Abs(DesktopInspectionRules.CounterMove(30, 20, 1.5f, 2) - 0) < 0.001f,
            "restored lens position uses the scaled native movement mapping");
        Console.WriteLine("Desktop layout tests passed.");
    }

    private static DesktopLayoutData Data(Guid run) => new DesktopLayoutData
    {
        schemaVersion = 1,
        runId = run.ToString("N"),
        layouts = new List<DesktopLayout>
        {
            new DesktopLayout
            {
                coordinatorId = 17,
                members = "5,27",
                positions = new List<DesktopPosition>
                {
                    new DesktopPosition { id = 5, x = -123, y = 300, order = 0 },
                    new DesktopPosition { id = 27, x = 500, y = -20, order = 1 }
                }
            }
        }
    };

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
