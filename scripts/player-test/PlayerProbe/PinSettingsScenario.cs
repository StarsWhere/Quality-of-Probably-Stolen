using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Uses the native gear settings window while pin preferences change independently.
public sealed class PinSettingsScenario
{
    private const string Mod = "starswhere.shopassist";
    [Serializable] private sealed class FileData { public FileEntry[] entries = null; }
    [Serializable] private sealed class FileEntry { public string key = null, value = null; }
    private readonly float deadline = Time.realtimeSinceStartup + 45;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Pin settings scenario timed out");
        var mod = ModLoader.Mods.Find(candidate => candidate.Manifest?.ID == Mod);
        ModConfig config = ModConfig.Find(Mod);
        if (CustomUIManager.Instance == null || mod?.Instance == null || config == null) return false;
        var pins = config.Get("f19.pinned") as ConfigEntry<string>;
        var other = config.Get("f15.enabled") as ConfigEntry<bool>;
        Need(pins != null && other != null, "Pin or ordinary setting is unavailable");
        var original = config.Entries.ToDictionary(entry => entry.Key, entry => entry.BoxedValue, StringComparer.Ordinal);
        CustomUIWindow gear = null;
        try
        {
            var write = mod.Instance.GetType().Assembly.GetType("ShopAssist.LiquidPins", true)
                .GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic);
            Need(write != null, "Native pin serialization is unavailable");
            string Encode(params string[] ids) => (string)write.Invoke(null, new object[] { ids.ToList() });
            string first = Encode("water"), second = Encode("water", "organic_waste"), third = Encode("organic_waste");
            pins.Set(first); config.Save();
            gear = ModConfigWindow.Open(mod);
            Need(gear?.IsAlive == true && gear.Get("f19.pinned")?.root?.activeInHierarchy != true,
                "Gear settings did not hide the pin storage widget");
            pins.Set(second); config.Save();
            bool changed = !other.Value;
            Need(gear.Get("f15.enabled")?.toggle != null, "Ordinary gear toggle is unavailable");
            gear.Get("f15.enabled").SetBool(changed, true);
            Click(gear, "apply");
            Need(pins.Value == second && other.Value == changed, "Gear Apply reverted a newer pin value or lost the ordinary change");
            AssertSaved(config, pins);
            pins.Set(third); config.Save();
            Click(gear, "reset");
            Need(pins.Value == third, "Gear Reset modified live pins before Apply");
            Click(gear, "apply");
            Need(pins.Value == third && Equals(other.Value, other.BoxedDefault), "Reset and Apply reverted a newer pin value");
            AssertSaved(config, pins);
            result = "PASS hidden pins survive gear Apply and Reset/Apply after independent changes, with ordinary settings and persisted config intact";
            return true;
        }
        finally
        {
            if (gear?.IsAlive == true) gear.Close();
            foreach (ConfigEntry entry in config.Entries)
            {
                object value = original[entry.Key];
                if (entry is ConfigEntry<bool> boolean) boolean.Set((bool)value);
                else if (entry is ConfigEntry<int> number) number.Set((int)value);
                else if (entry is ConfigEntry<string> text) text.Set((string)value);
                else throw new InvalidOperationException("Cannot restore config type: " + entry.Key);
            }
            config.Save();
        }
    }

    private static void Click(CustomUIWindow window, string id)
    {
        var button = window.Get(id)?.button;
        Need(button != null && button.interactable, "Missing gear action " + id);
        button.onClick.Invoke();
    }
    private static void AssertSaved(ModConfig config, ConfigEntry pin)
    {
        var file = JsonUtility.FromJson<FileData>(File.ReadAllText(config.FilePath));
        Need(file?.entries != null && file.entries.Count(entry => entry.key == pin.Key && entry.value == pin.Serialize()) == 1,
            "Pinned components were not preserved in the saved configuration");
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
