using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

public sealed class TodoUnreadableScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90f;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup >= deadline) throw new InvalidOperationException("Unreadable checklist test timed out");
        if (!start.Tick() || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true) return false;
        TradeScenario.SetOptions(true, "f17.enabled");
        const string damaged = "QA unreadable checklist";
        ModHelper.SetModData(Mod, "todo", damaged);
        var mod = ModLoader.Mods.First(value => value.Manifest?.ID == Mod);
        Type type = mod.Instance.GetType().Assembly.GetType("ShopAssist.TodoBook", true);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        object book = type.GetField("active", flags).GetValue(null);
        type.GetMethod("Open", flags).Invoke(book, null);
        var window = CustomUIManager.Instance.GetWindow(Mod + ".todo");
        if (window?.Get("task_reset")?.button == null) throw new InvalidOperationException("Unreadable checklist did not offer recovery");
        window.Get("task_reset").button.onClick.Invoke();
        window = CustomUIManager.Instance.GetWindow(Mod + ".todo");
        if (window?.Get("task_reset_confirm")?.button == null || ModHelper.GetModData(Mod, "todo", null) != damaged)
            throw new InvalidOperationException("Checklist recovery did not require confirmation or changed the original data");
        window.Get("task_reset_confirm").button.onClick.Invoke();
        window = CustomUIManager.Instance.GetWindow(Mod + ".todo");
        if (window?.Get("task_save")?.button == null || ModHelper.GetModData(Mod, "todoUnreadableBackup", null) != damaged)
            throw new InvalidOperationException("Checklist recovery did not restore editing and back up the original data");
        result = "PASS unreadable checklist requires confirmation, backs up the original, and restores editing";
        return true;
    }
}
