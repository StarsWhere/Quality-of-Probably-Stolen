using System;
using System.Collections.Generic;
using System.Reflection;
using ShopAssist;
using UnityEngine;

static class ShopSettingsDraftTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void ManagerTeardown(bool destroyedObject)
    {
        GameOptionRegistry.Sections.Clear();
        var section = new GameOptionRegistry.Section();
        section.entries.Add(new GameOptionRegistry.Entry { id = "key", kind = GameOptionRegistry.Kind.KeyBind });
        section.entries.Add(new GameOptionRegistry.Entry { id = "toggle", kind = GameOptionRegistry.Kind.Toggle });
        section.entries.Add(new GameOptionRegistry.Entry { id = "slider", kind = GameOptionRegistry.Kind.Slider });
        GameOptionRegistry.Sections.Add(section);
        var manager = new GameOptionUIManager();
        manager.SetKey("key", KeyCode.A); GameOptionUIManager.Instance = manager;
        var window = new CustomUIWindow();
        window.Controls["key"] = new CustomUIElement();
        window.Controls["toggle"] = new CustomUIElement { toggle = new object() };
        window.Controls["slider"] = new CustomUIElement { slider = new object(), Float = 1 };
        var settings = new ShopSettings();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ShopSettings).GetMethod("WatchDraft", flags).Invoke(settings, new object[] { window, true, false });
        object observer = window.Rect.gameObject.Component;
        Check(observer != null, "A native draft must install its observer.");
        manager.SetKey("key", KeyCode.F5);
        observer.GetType().GetMethod("LateUpdate", flags).Invoke(observer, null);
        window.Controls["toggle"].Bool = true;
        window.Controls["slider"].Float = 3;
        if (destroyedObject) { manager.SetKey("key", KeyCode.B); manager.Destroyed = true; }
        else GameOptionUIManager.Instance = null;
        Debug.Errors.Clear();
        observer.GetType().GetMethod("OnDisable", flags).Invoke(observer, null);
        observer.GetType().GetMethod("OnDestroy", flags).Invoke(observer, null);
        var recovered = (Dictionary<bool, Dictionary<string, object>>)typeof(ShopSettings).GetField("recovered", flags).GetValue(settings);
        Check(Debug.Errors.Count == 0 && recovered.TryGetValue(true, out var saved) &&
            Equals(saved["key"], KeyCode.F5) && Equals(saved["toggle"], true) && Equals(saved["slider"], 3f),
            "A missing or destroyed key manager must retain the last key and capture remaining live controls without errors.");
        GameOptionUIManager.Instance = null;
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
        ManagerTeardown(false);
        ManagerTeardown(true);
        Console.WriteLine("Settings draft state tests passed.");
    }
}
