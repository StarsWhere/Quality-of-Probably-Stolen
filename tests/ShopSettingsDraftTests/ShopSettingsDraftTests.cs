using System;
using System.Collections.Generic;
using ShopAssist;

static class ShopSettingsDraftTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Main()
    {
        var initial = new Dictionary<string, object> { ["f42.key"] = ConsoleKey.A, ["f42.enabled"] = false, ["other"] = 1 };
        var original = new SettingsDraftState(initial);
        original.Observe(new Dictionary<string, object> { ["f42.key"] = ConsoleKey.F5, ["f42.enabled"] = true });
        // During scene destruction the key manager can disappear before the remaining controls.
        original.Observe(new Dictionary<string, object> { ["other"] = 1 });
        var saved = original.Changes();
        Check(saved.Count == 2 && Equals(saved["f42.key"], ConsoleKey.F5), "Pending key and toggle must survive partial observation.");

        var next = new SettingsDraftState(new Dictionary<string, object> { ["f42.key"] = ConsoleKey.A, ["f42.enabled"] = false, ["other"] = 2 });
        saved["removed"] = "old control";
        var restorable = next.Applicable(saved);
        Check(restorable.Count == 2 && !restorable.ContainsKey("other"), "Restore only changes for controls that still exist.");
        next.Observe(restorable);
        Check(next.Changes().Count == 2 && Equals(next.Current["other"], 2), "Recovery must preserve newly applied unrelated settings.");

        // Discard the recovered changes while leaving the new window's baseline untouched.
        next.Observe(new Dictionary<string, object> { ["f42.key"] = next.Baseline["f42.key"], ["f42.enabled"] = next.Baseline["f42.enabled"] });
        Check(next.Changes().Count == 0 && Equals(next.Current["other"], 2), "Discard must restore the current baseline.");
        next.Observe(new Dictionary<string, object> { ["f42.key"] = ConsoleKey.F5 });
        next.Reset(next.Current);
        Check(next.Changes().Count == 0, "Applying a draft must reset the close warning.");
        next.Observe(new Dictionary<string, object> { ["f42.key"] = ConsoleKey.F5 });
        Check(next.Changes().Count == 0, "Reobserving an applied key must stay clean.");
        Console.WriteLine("Settings draft state tests passed.");
    }
}
