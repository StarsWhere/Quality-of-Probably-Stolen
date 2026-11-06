using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

public static class PlayerProbe
{
    private static bool created;
    public static void Start()
    {
        try
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string[] identity = File.ReadAllLines(Path.Combine(root, "qa-identity.txt"));
            if (identity.Length != 3 || identity[0] != Application.companyName || identity[1] != Application.productName ||
                !string.Equals(Path.GetFullPath(identity[2]).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(Application.persistentDataPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected Unity persistent data identity or path");
            string es3Default = new ES3Settings((string)null, (ES3Settings)null).FullPath;
            string es3Index = new ES3Settings("saves_index.es3", (ES3Settings)null).FullPath;
            if (!string.Equals(Path.GetFullPath(es3Default), Path.Combine(identity[2], "SaveFile.es3"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(es3Index), Path.Combine(identity[2], "saves_index.es3"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected ES3 default or index path: " + es3Default + " / " + es3Index);
            string runId = File.ReadAllText(Path.Combine(root, "qa-run-id.txt")).Trim();
            if (!Guid.TryParseExact(runId, "N", out _)) throw new InvalidOperationException("Invalid QA run identity");
            File.WriteAllText(Path.Combine(identity[2], "guard-proof.txt"), Application.companyName + "\n" + Application.productName + "\n" + Application.persistentDataPath + "\n" + es3Default + "\n" + es3Index + "\n" + runId);
            string errorLog = Path.Combine(identity[2], "qa-unity-errors-" + runId + ".txt");
            File.WriteAllText(errorLog, "");
            Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.Full);
            Application.logMessageReceived += (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    File.AppendAllText(errorLog, "---- " + type + " ----\n" + message + "\n" + stack + "\n");
            };
        }
        catch (Exception error)
        {
            UnityEngine.Debug.LogError("[PLAYER-QA] guard failed: " + error);
            Environment.Exit(70);
        }
    }

    public static void Begin()
    {
        if (created) return;
        created = true;
        Application.runInBackground = true;
        Application.targetFrameRate = 60;
        AudioListener.volume = 0f;
        Debug.Log("[PLAYER-QA] batchMode=" + Application.isBatchMode + " graphics=" + SystemInfo.graphicsDeviceType);
        var go = new GameObject("Player QA driver");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<PlayerQaDriver>();
    }
}

public sealed class PlayerQaDriver : MonoBehaviour
{
    private const string ModId = "starswhere.shopassist";
    private string phasePath;
    private string resultPath;
    private string statusPath;
    private object calendar;
    private System.Reflection.MethodInfo calendarTick;
    private float readyAfter;
    private float deadline;
    private bool finished;

    private void Awake()
    {
        phasePath = Path.Combine(Application.persistentDataPath, "qa-phase.txt");
        resultPath = Path.Combine(Application.persistentDataPath, "qa-results.txt");
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string runId = File.ReadAllText(Path.Combine(root, "qa-run-id.txt")).Trim();
        if (!Guid.TryParseExact(runId, "N", out _)) throw new InvalidOperationException("Invalid QA run identity");
        statusPath = Path.Combine(Application.persistentDataPath, "qa-status-" + runId + ".txt");
        readyAfter = Time.realtimeSinceStartup + 3;
        deadline = Time.realtimeSinceStartup + 180;
    }

    private void Update()
    {
        if (finished || Time.realtimeSinceStartup < readyAfter) return;
        if (Time.realtimeSinceStartup >= deadline) { Fail("readiness timeout"); return; }
        try
        {
            string phase = File.Exists(phasePath) ? File.ReadAllText(phasePath).Trim() : "activate";
            if (phase == "coexist-reset" || phase == "coexist-approve" || phase == "coexist-verify")
            {
                if (MainMenuUIController.Instance == null) return;
                Log("PASS " + CoexistScenario.Run(phase));
                Stop();
                return;
            }
            bool customScenario = System.Text.RegularExpressions.Regex.IsMatch(phase,
                @"^scenario-[A-Za-z][A-Za-z0-9]{0,63}Scenario$");
            if (phase == "calendar" || phase == "calendar-reload" || phase == "batch-a" || phase == "batch-a-reload" || phase == "inspection-tree" || customScenario)
            {
                if (calendar == null)
                {
                    string scenarioName = phase == "calendar" ? "MorningScenario" :
                        phase == "calendar-reload" ? "CalendarReloadScenario" :
                        phase == "batch-a" ? "BatchAScenario" : phase == "batch-a-reload" ? "BatchAReloadScenario" :
                        phase == "inspection-tree" ? "InspectionTreeScenario" : phase.Substring("scenario-".Length);
                    var type = typeof(PlayerProbe).Assembly.GetType(scenarioName, true);
                    calendar = Activator.CreateInstance(type);
                    calendarTick = type.GetMethod("Tick", new[] { typeof(string).MakeByRefType() });
                    Require(calendarTick != null && calendarTick.ReturnType == typeof(bool), "Missing " + scenarioName + ".Tick(out string)");
                }
                object[] arguments = { null };
                if ((bool)calendarTick.Invoke(calendar, arguments))
                {
                    string detail = (string)arguments[0] ?? "scenario completed";
                    Log(detail.StartsWith("PASS ", StringComparison.Ordinal) ? detail : "PASS " + detail);
                    if (phase == "calendar")
                    {
                        Require(PlayerStore.HasInstance, "No isolated QA run remains loaded");
                        Require(Guid.TryParse(PlayerStore.Instance.runID, out Guid run) && run != Guid.Empty &&
                            PlayerStore.Instance.saveSlotId >= 0 && StoreStation.GetDayCounter() >= 1, "Cannot identify the isolated QA run");
                        File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-latest-run.txt"),
                            "1|" + run.ToString("N") + "|" + PlayerStore.Instance.saveSlotId + "|" + StoreStation.GetDayCounter());
                        File.WriteAllText(phasePath, "calendar-reload");
                    }
                    else if (phase == "calendar-reload") File.WriteAllText(phasePath, "calendar-complete");
                    else if (phase == "batch-a") File.WriteAllText(phasePath, "batch-a-reload");
                    else if (phase == "batch-a-reload") File.WriteAllText(phasePath, "batch-a-complete");
                    else if (phase == "inspection-tree") File.WriteAllText(phasePath, "inspection-tree-complete");
                    else File.WriteAllText(phasePath, phase + "-complete");
                    Stop();
                }
                return;
            }
            LoadedMod mod = ModLoader.Mods.Find(m => m.Manifest != null && m.Manifest.ID == ModId);
            if (mod == null || MainMenuUIController.Instance == null) return;
            if (phase == "activate")
            {
                if (ModLoader.IsModEnabled(ModId))
                {
                    ModLoader.SetModEnabled(ModId, false);
                    Require(!ModLoader.IsModEnabled(ModId), "Could not disable the previous QA activation");
                    Log("PASS previous QA activation disabled for a fresh native approval");
                    Stop();
                    return;
                }
                ModEnableCheck check = ModLoader.CheckEnable(mod);
                Require(check != null && check.Refusal == null && check.Scan != null && !check.Scan.Refused && check.Fingerprint != null,
                    "Native activation refused: " + (check == null ? "null" : check.Refusal));
                Require(check.Scan.HardFlags.Count == 0 && check.Scan.Capabilities.All(c =>
                    (c == "dynamic-code" || c == "reflection-external") && mod.Manifest.Capabilities.Contains(c)) &&
                    mod.Manifest.Capabilities.All(c => c == "dynamic-code" || c == "reflection-external"), "Unexpected native capability or hard flag");
                ModLoader.ApproveAndEnable(mod, check);
                Require(ModLoader.IsModEnabled(ModId), "Native activation did not persist enabled state");
                Log("PASS native activation: scanned methods=" + check.Scan.MethodsScanned + ", capabilities=" + check.Scan.Capabilities.Count);
                File.WriteAllText(phasePath, "settings");
                Stop();
                return;
            }
            if (phase == "settings")
            {
                Require(mod.IsEnabled && mod.Instance != null && mod.LoadError == null, "Mod failed to load: " + mod.LoadError);
                var config = ModConfig.Find(ModId);
                if (config == null || CustomUIManager.Instance == null || EmporiumOption.Instance == null) return;
                var entry = config.Get("f15.enabled") as ConfigEntry<bool>;
                Require(entry != null, "Missing F15 ConfigEntry");
                var options = Resources.FindObjectsOfTypeAll<GameOptionUIManager>().FirstOrDefault(x => x.gameObject.scene.IsValid());
                if (options == null) return;
                options.OpenUI();
                var window = CustomUIManager.Instance.GetWindow("game_options");
                Require(window != null, "Options window missing");
                var toggle = window.Get(ModId + ":f15.enabled");
                var apply = window.Get("apply");
                Require(toggle != null && toggle.toggle != null && apply != null && apply.button != null, "Options controls missing");
                for (int id = 1; id <= 42; id++)
                {
                    string key = "f" + id.ToString("D2") + ".enabled";
                    Require(config.Get(key) is ConfigEntry<bool> && window.Get(ModId + ":" + key)?.toggle != null,
                        "Release is missing an independent native switch: " + key);
                }
                foreach (var option in config.Entries)
                {
                    string label = option.Label?.Invoke(), tip = option.Tooltip?.Invoke();
                    Require(!string.IsNullOrWhiteSpace(label) && label != option.Key && !string.IsNullOrWhiteSpace(tip) &&
                        !System.Text.RegularExpressions.Regex.IsMatch(tip, @"^f\d{2}\.[\w.]+$"),
                        "Untranslated configuration control: " + option.Key);
                    foreach (string language in new[] { "zh", "en" })
                        Require(mod.Localization.TryGetValue(language, out var map) && map.TryGetValue(option.Key, out var title) &&
                            !string.IsNullOrWhiteSpace(title), "Missing setting label: " + language + " " + option.Key);
                    if (option.HasRange)
                        Require(Convert.ToDouble(option.BoxedValue) >= option.Min && Convert.ToDouble(option.BoxedValue) <= option.Max,
                            "Setting value outside native range: " + option.Key);
                }
                Log("PASS native configuration labels, descriptions, localization and ranges: " + config.Entries.Count + " entries");
                string actualLabel = window.Get("label_" + ModId + ":f15.enabled")?.GetText();
                string tooltip = entry.Tooltip?.Invoke();
                Require(!string.IsNullOrWhiteSpace(actualLabel) && actualLabel.Contains(entry.Label()) &&
                    !string.IsNullOrWhiteSpace(tooltip) && tooltip != "f15.tip", "F15 label or tooltip is missing");
                foreach (string language in new[] { "zh", "en" })
                {
                    Require(mod.Localization.TryGetValue(language, out var words) && words.TryGetValue("f15.enabled", out var label) &&
                        words.TryGetValue("f15.tip", out var tip) && !string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(tip),
                        "Missing F15 label or tooltip in locale " + language);
                }
                Log("PASS native option label=" + actualLabel + "; tooltip=" + tooltip + "; en/zh labels and tooltips loaded");
                bool original = entry.Value;
                toggle.SetBool(!original, true);
                Require(entry.Value == original, "Options draft wrote before Apply");
                apply.button.onClick.Invoke();
                Require(entry.Value != original, "Options Apply did not update config");
                options.CloseUI();

                var modWindow = ModConfigWindow.Open(mod);
                Require(modWindow != null, "Mods configuration window missing");
                var modToggle = modWindow.Get("f15.enabled");
                var modApply = modWindow.Get("apply");
                Require(modToggle != null && modToggle.toggle != null && modApply != null && modApply.button != null, "Mods controls missing");
                Require(modToggle.GetBool() == entry.Value, "Mods UI disagrees with Options Apply");
                modToggle.SetBool(false, true);
                modApply.button.onClick.Invoke();
                Require(!entry.Value, "Mods Apply did not update shared config");
                modWindow.Close();

                options.OpenUI();
                window = CustomUIManager.Instance.GetWindow("game_options");
                toggle = window.Get(ModId + ":f15.enabled");
                Require(toggle != null && !toggle.GetBool(), "Options UI disagrees with Mods Apply");
                toggle.SetBool(true, true);
                window.Get("apply").button.onClick.Invoke();
                Require(entry.Value && File.Exists(config.FilePath), "Options final Apply was not saved");
                options.CloseUI();
                Log("PASS Options and Mods panels share ConfigEntry and apply/save changes");
                File.WriteAllText(phasePath, "persist");
                Stop();
                return;
            }
            if (phase == "persist")
            {
                Require(mod.IsEnabled && mod.Instance != null && mod.LoadError == null, "Mod failed to reload");
                var config = ModConfig.Find(ModId);
                if (config == null) return;
                var entry = config.Get("f15.enabled") as ConfigEntry<bool>;
                Require(entry != null && entry.Value, "F15 setting did not persist across process restart");
                Log("PASS F15 setting persisted across process restart");
                File.WriteAllText(phasePath, "calendar");
                Stop();
                return;
            }
            Fail("Unknown phase " + phase);
        }
        catch (Exception ex) { Fail(ex.ToString()); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Log(string text) { Debug.Log("[PLAYER-QA] " + text); File.AppendAllText(resultPath, text + "\n"); }
    private void Fail(string text) { if (finished) return; finished = true; Log("FAIL " + text); File.WriteAllText(statusPath, "FAIL " + text); Application.Quit(1); }
    private void Stop()
    {
        finished = true;
        try
        {
            foreach (var paper in Resources.FindObjectsOfTypeAll<StoreUI>())
            {
                if (paper == null || !paper.isPaperUI || !paper.gameObject.scene.IsValid() || !paper.gameObject.activeInHierarchy) continue;
                if (StoreUIManager.Instance != null) StoreUIManager.Instance.CloseUI(paper.identifier);
                if (paper != null && paper.gameObject.activeInHierarchy) paper.gameObject.SetActive(false);
            }
            StartCoroutine(QuitOnNextFrame());
        }
        catch (Exception error)
        {
            finished = false;
            Fail("Native QA cleanup failed: " + error);
        }
    }

    private IEnumerator QuitOnNextFrame()
    {
        yield return null;
        try
        {
            File.WriteAllText(statusPath, "PASS");
            Application.Quit();
        }
        catch (Exception error)
        {
            finished = false;
            Fail("QA shutdown failed: " + error);
        }
    }
}
