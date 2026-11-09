using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

// Diagnostic timings are inclusive and include Harmony/Stopwatch overhead.
// They must not be substituted for the separate, fixed comparison probe.
public class PerformanceFilterDiagnosticsScenario
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Counter
    {
        internal string Group, Name;
        internal long Ticks, Calls;
    }

    private static readonly Dictionary<MethodBase, Counter> Counters = new Dictionary<MethodBase, Counter>();
    private static bool capturing;
    private static int round;
    private static string output;
    private readonly PerformanceInventoryScenario scenario;
    private readonly string scale;
    private Harmony harmony;

    public PerformanceFilterDiagnosticsScenario() : this(true) { }
    protected PerformanceFilterDiagnosticsScenario(bool large)
    {
        scenario = large ? (PerformanceInventoryScenario)new PerformanceLargeFilterScenario() : new PerformanceSmallFilterScenario();
        scale = large ? "large" : "small";
    }

    public bool Tick(out string result)
    {
        if (harmony == null) Install();
        if (!scenario.Tick(out result)) return false;
        harmony.UnpatchAll(harmony.Id);
        result = "PASS module filter diagnostics; rounds=" + round + "; output=" + output;
        if (round != 3) throw new InvalidOperationException("Expected three complete diagnostic rounds");
        return true;
    }

    private void Install()
    {
        var mod = ModLoader.Mods.Find(value => value.Manifest?.ID == "starswhere.shopassist");
        if (mod?.Instance == null) throw new InvalidOperationException("ShopAssist instance unavailable for diagnostics");
        var entry = mod.Instance.GetType();
        var modules = entry.GetField("modules", Flags)?.GetValue(mod.Instance) as IEnumerable;
        if (modules == null) throw new InvalidOperationException("ShopAssist module list unavailable");
        harmony = new Harmony("qa.performance.filter-diagnostics");
        Counters.Clear(); round = 0; capturing = false;
        foreach (object module in modules)
        {
            var type = module.GetType();
            string name = (string)type.GetField("Name", Flags).GetValue(module);
            var tick = (Action)type.GetField("Tick", Flags).GetValue(module);
            if (tick != null) Instrument(tick.Method, "module", name, true);
        }
        var assembly = entry.Assembly;
        var machines = assembly.GetType("ShopAssist.MachineTools");
        var grid = assembly.GetType("ShopAssist.ItemGrid");
        foreach (string name in new[] { "RenderModules", "RenderModuleResults", "ModuleRows", "ModuleLocations" })
            Instrument(machines?.GetMethod(name, Flags), "stage", "MachineTools." + name, true);
        foreach (string name in new[] { "Paginate", "PackedRows", "Add" })
            Instrument(grid?.GetMethod(name, Flags), "stage", "ItemGrid." + name, true);

        // Native extern methods do not have a managed body Harmony can patch.
        Instrument(typeof(Canvas).GetMethod("ForceUpdateCanvases", Flags), "layout", "Canvas.ForceUpdateCanvases", false);
        Instrument(typeof(LayoutRebuilder).GetMethod("ForceRebuildLayoutImmediate", Flags), "layout", "LayoutRebuilder.ForceRebuildLayoutImmediate", false);
        var scenarioType = typeof(PerformanceInventoryScenario);
        harmony.Patch(scenarioType.GetMethod("BeginSample", Flags), prefix: Hook(nameof(BeginSample)));
        harmony.Patch(scenarioType.GetMethod("EndSample", Flags), prefix: Hook(nameof(EndSample)));
        output = Path.Combine(Application.persistentDataPath, "qa-performance-filter-" + scale + "-diagnostics.csv");
        File.WriteAllText(output, "round,group,method,calls,total_ms\n");
    }

    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(PerformanceFilterDiagnosticsScenario).GetMethod(name, Flags));

    private void Instrument(MethodInfo method, string group, string name, bool required)
    {
        if (method == null || method.GetMethodBody() == null)
        {
            if (required) throw new InvalidOperationException("Missing managed diagnostic method " + name);
            UnityEngine.Debug.Log("[PLAYER-QA] Diagnostic method unavailable (no managed body): " + name);
            return;
        }
        if (Counters.ContainsKey(method)) throw new InvalidOperationException("Duplicate diagnostic method " + name);
        Counters.Add(method, new Counter { Group = group, Name = name });
        harmony.Patch(method, prefix: Hook(nameof(TimedStart)), postfix: Hook(nameof(TimedEnd)));
    }

    private static void BeginSample()
    {
        foreach (Counter counter in Counters.Values) counter.Ticks = counter.Calls = 0;
        capturing = true;
    }

    private static void EndSample()
    {
        capturing = false;
        round++;
        double milliseconds = 1000d / Stopwatch.Frequency;
        foreach (Counter counter in Counters.Values.OrderBy(value => value.Group).ThenBy(value => value.Name))
            File.AppendAllText(output, round + "," + counter.Group + "," + counter.Name + "," + counter.Calls + "," +
                (counter.Ticks * milliseconds).ToString("F3", CultureInfo.InvariantCulture) + "\n");
    }

    private static void TimedStart(out long __state) => __state = capturing ? Stopwatch.GetTimestamp() : 0;
    private static void TimedEnd(MethodBase __originalMethod, long __state)
    {
        if (__state == 0) return;
        long elapsed = Stopwatch.GetTimestamp() - __state;
        if (Counters.TryGetValue(__originalMethod, out Counter counter)) { counter.Calls++; counter.Ticks += elapsed; }
    }
}

public sealed class PerformanceSmallFilterDiagnosticsScenario : PerformanceFilterDiagnosticsScenario
{
    public PerformanceSmallFilterDiagnosticsScenario() : base(false) { }
}
