using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Only the guarded QA Player uses this virtual input; game callbacks and raycasts stay intact.
internal sealed class QaSyntheticInput : IDisposable
{
    private static QaSyntheticInput current;
    private readonly Harmony harmony;
    private readonly string owner;
    private readonly bool keyboard;
    internal Vector3 Mouse;
    internal KeyCode Held = KeyCode.None;
    internal KeyCode HeldModifier = KeyCode.None;
    internal KeyCode Down = KeyCode.None;

    internal QaSyntheticInput(bool keyboard = false)
    {
        if (current != null) throw new InvalidOperationException("Synthetic QA input already active");
        this.keyboard = keyboard;
        owner = "qa.synthetic-input." + Guid.NewGuid().ToString("N");
        harmony = new Harmony(owner);
        current = this;
        try
        {
            Patch(AccessTools.PropertyGetter(typeof(Input), nameof(Input.mousePosition)), nameof(MousePrefix));
            if (keyboard)
            {
                Patch(AccessTools.Method(typeof(Input), nameof(Input.GetKey), new[] { typeof(KeyCode) }), nameof(KeyPrefix));
                Patch(AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) }), nameof(DownPrefix));
            }
        }
        catch { Dispose(); throw; }
    }

    private void Patch(MethodBase original, string name)
    {
        MethodInfo prefix = typeof(QaSyntheticInput).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        if (original == null || prefix == null) throw new InvalidOperationException("Missing synthetic input method " + name);
        harmony.Patch(original, prefix: new HarmonyMethod(prefix));
    }

    private static bool MousePrefix(ref Vector3 __result)
    {
        if (current == null) return true;
        __result = current.Mouse; return false;
    }

    private static bool KeyPrefix(KeyCode __0, ref bool __result)
    {
        if (current == null || !current.keyboard) return true;
        __result = __0 != KeyCode.None && (__0 == current.Held || __0 == current.HeldModifier); return false;
    }

    private static bool DownPrefix(KeyCode __0, ref bool __result)
    {
        if (current == null || !current.keyboard) return true;
        __result = __0 != KeyCode.None && __0 == current.Down; return false;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(current, this)) return;
        current = null;
        harmony.UnpatchAll(owner);
    }
}
