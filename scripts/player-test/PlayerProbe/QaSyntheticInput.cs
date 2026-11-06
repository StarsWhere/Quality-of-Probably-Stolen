using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

// Only the guarded QA Player uses this virtual input; game callbacks and raycasts stay intact.
internal sealed class QaSyntheticInput : IDisposable
{
    private static QaSyntheticInput current;
    // Unity's mouse-button methods are extern. Removing their last Harmony
    // transpiler makes Harmony rebuild an invalid empty body, so keep this QA-only
    // replacement for the lifetime of the isolated test process.
    private static readonly Harmony mouseHarmony = new Harmony("qa.synthetic-input.mouse-buttons");
    private static bool mousePatched;
    private readonly Harmony harmony;
    private readonly string owner;
    private readonly bool keyboard, mouseButtons;
    internal Vector3 Mouse;
    internal KeyCode Held = KeyCode.None;
    internal KeyCode HeldModifier = KeyCode.None;
    internal KeyCode Down = KeyCode.None;
    internal KeyCode Up = KeyCode.None;
    internal int MouseHeld = -1, MouseDown = -1, MouseUp = -1;

    internal QaSyntheticInput(bool keyboard = false, bool mouseButtons = false)
    {
        if (current != null) throw new InvalidOperationException("Synthetic QA input already active");
        this.keyboard = keyboard;
        this.mouseButtons = mouseButtons;
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
                Patch(AccessTools.Method(typeof(Input), nameof(Input.GetKeyUp), new[] { typeof(KeyCode) }), nameof(UpPrefix));
            }
            if (mouseButtons)
            {
                EnsureMouseButtons();
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
    private static void EnsureMouseButtons()
    {
        if (mousePatched) return;
        PatchTranspiler(AccessTools.Method(typeof(Input), nameof(Input.GetMouseButton), new[] { typeof(int) }), nameof(MouseHeldTranspiler));
        PatchTranspiler(AccessTools.Method(typeof(Input), nameof(Input.GetMouseButtonDown), new[] { typeof(int) }), nameof(MouseDownTranspiler));
        PatchTranspiler(AccessTools.Method(typeof(Input), nameof(Input.GetMouseButtonUp), new[] { typeof(int) }), nameof(MouseUpTranspiler));
        mousePatched = true;
    }
    private static void PatchTranspiler(MethodBase original, string name)
    {
        MethodInfo transpiler = typeof(QaSyntheticInput).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        if (original == null || transpiler == null) throw new InvalidOperationException("Missing synthetic input method " + name);
        mouseHarmony.Patch(original, transpiler: new HarmonyMethod(transpiler));
    }

    private static bool MousePrefix(ref Vector3 __result)
    {
        if (current == null) return true;
        __result = current.Mouse; return false;
    }

    private static bool KeyPrefix(KeyCode __0, ref bool __result)
    {
        if (current == null || !current.keyboard) return true;
        __result = __0 != KeyCode.None && (__0 == current.Held || __0 == current.HeldModifier ||
            current.mouseButtons && MouseKey(__0) == current.MouseHeld && current.MouseHeld >= 0); return false;
    }

    private static bool DownPrefix(KeyCode __0, ref bool __result)
    {
        if (current == null || !current.keyboard) return true;
        __result = __0 != KeyCode.None && (__0 == current.Down ||
            current.mouseButtons && MouseKey(__0) == current.MouseDown && current.MouseDown >= 0); return false;
    }
    private static bool UpPrefix(KeyCode __0, ref bool __result)
    {
        if (current == null || !current.keyboard) return true;
        __result = __0 != KeyCode.None && (__0 == current.Up ||
            current.mouseButtons && MouseKey(__0) == current.MouseUp && current.MouseUp >= 0); return false;
    }
    private static int MouseKey(KeyCode key) => key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6 ? (int)key - (int)KeyCode.Mouse0 : -1;
    private static IEnumerable<CodeInstruction> MouseHeldTranspiler(IEnumerable<CodeInstruction> _) => MouseBody(nameof(MouseHeldValue));
    private static IEnumerable<CodeInstruction> MouseDownTranspiler(IEnumerable<CodeInstruction> _) => MouseBody(nameof(MouseDownValue));
    private static IEnumerable<CodeInstruction> MouseUpTranspiler(IEnumerable<CodeInstruction> _) => MouseBody(nameof(MouseUpValue));
    private static IEnumerable<CodeInstruction> MouseBody(string method)
    {
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(QaSyntheticInput), method));
        yield return new CodeInstruction(OpCodes.Ret);
    }
    private static bool MouseHeldValue(int button) => button >= 0 && button <= 6 &&
        (current?.mouseButtons == true ? button == current.MouseHeld : Input.GetKey((KeyCode)((int)KeyCode.Mouse0 + button)));
    private static bool MouseDownValue(int button) => button >= 0 && button <= 6 &&
        (current?.mouseButtons == true ? button == current.MouseDown : Input.GetKeyDown((KeyCode)((int)KeyCode.Mouse0 + button)));
    private static bool MouseUpValue(int button) => button >= 0 && button <= 6 &&
        (current?.mouseButtons == true ? button == current.MouseUp : Input.GetKeyUp((KeyCode)((int)KeyCode.Mouse0 + button)));

    public void Dispose()
    {
        if (!ReferenceEquals(current, this)) return;
        current = null;
        harmony.UnpatchAll(owner);
    }
}
