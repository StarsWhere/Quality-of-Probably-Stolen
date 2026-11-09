using System;
using ShopAssist;

internal static class DesktopTests
{
    private static void Main()
    {
        Guid run = Guid.Parse("e4a55bd1-9123-4bb2-bf10-a0b0c0d0e0f0");
        Check(DesktopLayoutRules.Finite(1000000f) && !DesktopLayoutRules.Finite(float.NaN) &&
            !DesktopLayoutRules.Finite(float.PositiveInfinity) && !DesktopLayoutRules.Finite(1000001f),
            "desktop coordinates must be finite and within bounds");
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

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
