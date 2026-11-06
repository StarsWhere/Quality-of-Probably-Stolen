using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

// Shared by F42 scenarios; only the isolated QA Player receives the focus override.
internal sealed class QaFastPlayback : IDisposable
{
    private const string Mod = "starswhere.shopassist";
    private readonly ConfigEntry<bool> enabled;
    private readonly ConfigEntry<int> modeEntry, speedEntry;
    private readonly bool oldEnabled;
    private readonly int oldMode, oldSpeed;
    private readonly GameOptionRegistry.Entry keyEntry;
    private readonly KeyCode oldKey;
    private readonly MethodInfo focusChanged;
    private readonly PropertyInfo rate;
    private readonly Harmony focusPatch;
    private readonly string owner;
    private bool disposed;

    internal QaSyntheticInput Input { get; private set; }
    internal KeyCode Key { get; }
    internal float Rate => (float)rate.GetValue(null);

    internal QaFastPlayback(int mode = 0, int speed = 3)
    {
        var config = ModConfig.Find(Mod) ?? throw new InvalidOperationException("F42 mod config unavailable");
        enabled = config.Get("f42.enabled") as ConfigEntry<bool>;
        modeEntry = config.Get("f42.mode") as ConfigEntry<int>;
        speedEntry = config.Get("f42.speed") as ConfigEntry<int>;
        if (enabled == null || modeEntry == null || speedEntry == null)
            throw new InvalidOperationException("F42 configuration unavailable");
        oldEnabled = enabled.Value; oldMode = modeEntry.Value; oldSpeed = speedEntry.Value;
        keyEntry = GameOptionRegistry.Sections.SelectMany(section => section.entries)
            .SingleOrDefault(entry => entry.id == Mod + ":f42.key");
        if (keyEntry?.getKey == null || keyEntry.setKey == null)
            throw new InvalidOperationException("F42 key binding unavailable");
        oldKey = keyEntry.getKey();

        var loaded = ModLoader.Mods.Find(mod => mod.Manifest != null && mod.Manifest.ID == Mod);
        var assembly = loaded?.Instance?.GetType().Assembly ?? throw new InvalidOperationException("F42 mod unavailable");
        var playback = assembly.GetType("ShopAssist.FastPlayback", true);
        var settings = assembly.GetType("ShopAssist.ShopSettings", true);
        rate = playback.GetProperty("Rate", BindingFlags.Static | BindingFlags.NonPublic);
        focusChanged = playback.GetMethod("FocusChanged", BindingFlags.Static | BindingFlags.NonPublic);
        var refresh = playback.GetMethod("RefreshInput", BindingFlags.Instance | BindingFlags.NonPublic);
        var conflict = settings.GetMethod("ShortcutOwner", BindingFlags.Static | BindingFlags.NonPublic);
        if (rate == null || focusChanged == null || refresh == null || conflict == null)
            throw new InvalidOperationException("F42 input inspection unavailable");
        var candidates = new[] { KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F11, KeyCode.F12,
            KeyCode.PageUp, KeyCode.PageDown, KeyCode.Home, KeyCode.End };
        Key = candidates.FirstOrDefault(candidate => conflict.Invoke(null, new object[] { "f42.key", candidate }) == null);
        if (Key == KeyCode.None) throw new InvalidOperationException("No unused F42 QA key is available");

        owner = "qa.fastplay.focus." + Guid.NewGuid().ToString("N");
        focusPatch = new Harmony(owner);
        try
        {
            focusPatch.Patch(refresh, transpiler: new HarmonyMethod(typeof(QaFastPlayback), nameof(AssumeFocusedInFeature)));
            Input = new QaSyntheticInput(true);
            keyEntry.setKey(Key);
            Configure(mode, speed);
            SetFocus(true);
            Release();
        }
        catch { Dispose(); throw; }
    }

    internal void Configure(int mode, int speed)
    {
        if (mode < 0 || mode > 1 || speed < 0 || speed > 3) throw new ArgumentOutOfRangeException();
        modeEntry.Set(mode); speedEntry.Set(speed); enabled.Set(true);
    }

    internal void Press() { Input.Held = Key; Input.Down = Key; }
    internal void Hold() { Input.Held = Key; Input.Down = KeyCode.None; }
    internal void Release() { Input.Held = KeyCode.None; Input.Down = KeyCode.None; }
    internal void SetFocus(bool focused) { focusChanged.Invoke(null, new object[] { focused }); }

    private static bool AssumeFocused() => true;
    private static IEnumerable<CodeInstruction> AssumeFocusedInFeature(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var getter = AccessTools.PropertyGetter(typeof(Application), nameof(Application.isFocused));
        var matches = code.Where(instruction => instruction.Calls(getter)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("F42 focus test site changed");
        matches[0].opcode = OpCodes.Call;
        matches[0].operand = AccessTools.DeclaredMethod(typeof(QaFastPlayback), nameof(AssumeFocused));
        return code;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Input?.Dispose(); Input = null;
        keyEntry.setKey(oldKey);
        modeEntry.Set(oldMode); speedEntry.Set(oldSpeed); enabled.Set(oldEnabled);
        focusChanged.Invoke(null, new object[] { Application.isFocused });
        focusPatch.UnpatchAll(owner);
    }
}
