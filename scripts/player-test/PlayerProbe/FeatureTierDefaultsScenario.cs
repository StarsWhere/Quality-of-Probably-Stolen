using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Verifies registered defaults and the native config loader's saved-value precedence.
public sealed class FeatureTierDefaultsScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly float deadline = Time.realtimeSinceStartup + 45f;
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Feature default configuration did not register");
        ModConfig config = ModConfig.Find(Mod);
        if (config?.Get("f42.enabled") == null || config.Get("f05.auto_insert") == null) return false;
        var ids = new[] { 1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 12, 14, 15, 16, 17, 18, 19, 20, 21, 23, 24, 25, 28, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42 };
        var enabled = new HashSet<int> { 2, 3, 4, 5, 38, 39, 40, 41 };
        foreach (int id in ids)
        {
            string key = "f" + id.ToString("00") + ".enabled";
            if (id == 25) key = "f25.return_mode";
            ConfigEntry entry = config.Get(key);
            Need(entry != null && (id == 25 ? entry.ValueType == typeof(int) && Equals(entry.BoxedDefault, 0) :
                entry.ValueType == typeof(bool) && Equals(entry.BoxedDefault, enabled.Contains(id))),
                "Wrong registered feature default: " + key + " = " + entry?.BoxedDefault);
        }
        foreach (string key in new[] { "f01.wholesale", "f05.auto_insert", "f04.unseen" })
            Need(Equals(config.Get(key)?.BoxedDefault, false), "Cross-tier option default is enabled: " + key);
        foreach (string key in new[] { "f04.stolen", "f04.contraband" })
            Need(Equals(config.Get(key)?.BoxedDefault, true), "Existing safety default changed: " + key);

        // Use a separate in-memory native config, leaving the current profile untouched.
        var scratch = (ModConfig)Activator.CreateInstance(typeof(ModConfig), Flags, null, new object[] { "qa.feature-defaults" }, null);
        var raw = (Dictionary<string, string>)typeof(ModConfig).GetField("rawValues", Flags).GetValue(scratch);
        raw["f09.enabled"] = "true"; raw["f03.enabled"] = "false";
        Type catalog = ModLoader.Mods.First(m => m.Manifest?.ID == Mod).Instance.GetType().Assembly.GetType("ShopAssist.FeatureCatalog", true);
        MethodInfo defaultFor = catalog.GetMethod("ToggleDefault", Flags);
        bool DefaultFor(string key) => (bool)defaultFor.Invoke(null, new object[] { key, false });
        ConfigEntry<bool> savedEnabled = scratch.Define("f09.enabled", DefaultFor("f09.enabled"), "QA", null, null);
        ConfigEntry<bool> savedDisabled = scratch.Define("f03.enabled", DefaultFor("f03.enabled"), "QA", null, null);
        Need(savedEnabled.Value && !savedDisabled.Value && Equals(savedEnabled.BoxedDefault, false) && Equals(savedDisabled.BoxedDefault, true),
            "Native config registration failed to preserve explicit saved values over the new defaults");
        Need(!config.Entries.Any(entry => entry.Key == "f06.enabled"), "Removed F06 switch is still registered");
        result = "PASS current registered feature defaults, cross-tier safety and native saved-value precedence";
        return true;
    }
}
