using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Measured in the guarded rendered Player: the same native full blackscreen sequence in both modes.
public sealed class FastPlaybackMotionPerformanceScenario
{
    private const int Frames = 60;
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private struct Stamp { internal bool Valid; internal long Ticks, Bytes; }
    private sealed class FrameSamples
    {
        internal readonly long[] Early = new long[Frames], Tick = new long[Frames], Bytes = new long[Frames];
        internal readonly double[] FrameMs = new double[Frames];
        internal int Count, Frame = -1;
        internal bool HasEarly, HasTick;
        internal long EarlyTicks, TickTicks, EarlyBytes, TickBytes;
        internal void Reset() { Count = 0; Frame = -1; HasEarly = HasTick = false; }
        internal void Add(bool early, long ticks, long bytes)
        {
            if (Count >= Frames) return;
            int frame = Time.frameCount;
            if (Frame != frame) { Frame = frame; HasEarly = HasTick = false; }
            if (early) { HasEarly = true; EarlyTicks = ticks; EarlyBytes = bytes; }
            else { HasTick = true; TickTicks = ticks; TickBytes = bytes; }
            if (!HasEarly || !HasTick) return;
            Early[Count] = EarlyTicks;
            Tick[Count] = TickTicks;
            Bytes[Count] = EarlyBytes < 0 || TickBytes < 0 ? -1 : EarlyBytes + TickBytes;
            FrameMs[Count] = Time.unscaledDeltaTime * 1000d;
            Count++;
            HasEarly = HasTick = false;
        }
    }

    private static FastPlaybackMotionPerformanceScenario active;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float began = Time.realtimeSinceStartup;
    private readonly FrameSamples idle = new FrameSamples(), fast = new FrameSamples();
    private readonly List<string> rows = new List<string>();
    private readonly List<double> slowDurations = new List<double>(), fastDurations = new List<double>();
    private QaFastPlayback playback;
    private Harmony timing;
    private string timingId, allocationSource;
    private byte[] calibrationBuffer;
    private int step, sampling, round, duringCount, afterCount, oldWidth, oldHeight, oldFrameRate, oldVSync;
    private int oldOptionFullscreen, oldOptionResolution, oldOptionBorderless, stableFrame;
    private FullScreenMode oldFullscreen;
    private bool settingsSaved, nativeOptionsSaved, oldMinimizedCap, oldEnableCap, allocationAvailable;
    private float deadline, stableAfter;
    private EmporiumOption nativeOptions;
    private long startedAt, finishedAt;

    public bool Tick(out string result)
    {
        result = null;
        try
        {
            if (Time.realtimeSinceStartup - began > 90f) throw new InvalidOperationException("F42 performance QA timed out at step " + step);
            return Run(out result);
        }
        catch { Cleanup(); throw; }
    }

    private bool Run(out string result)
    {
        result = null;
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (StoreUIManager.Instance?.blackscreen == null || StoreUIManager.Instance.gameOptionUIManager == null ||
                EmporiumOption.Instance == null || EmporiumEntry.Instance == null) return false;
            oldWidth = Screen.width; oldHeight = Screen.height; oldFullscreen = Screen.fullScreenMode;
            oldFrameRate = Application.targetFrameRate; oldVSync = QualitySettings.vSyncCount; settingsSaved = true;
            nativeOptions = EmporiumOption.Instance;
            oldOptionFullscreen = nativeOptions.fullscreenMode; oldOptionResolution = nativeOptions.resolution;
            oldOptionBorderless = nativeOptions.borderlessDivisor; oldMinimizedCap = nativeOptions.fpsCapWhileMinimized;
            oldEnableCap = nativeOptions.enableFpsCap; nativeOptionsSaved = true;

            // Mirror the established inventory performance fixture. The native selector
            // does not offer 1024x768, so finish its 1280x720 adjustment first.
            nativeOptions.fpsCapWhileMinimized = false; nativeOptions.enableFpsCap = false;
            nativeOptions.fullscreenMode = 2; nativeOptions.borderlessDivisor = 1;
            nativeOptions.resolution = EmporiumOption.GetResolutionValueFromDimensions(1280, 720);
            Need(nativeOptions.resolution >= 0, "Native 1280x720 option unavailable");
            nativeOptions.ApplySetting();
            deadline = Time.realtimeSinceStartup + 20f;
            stableAfter = Time.realtimeSinceStartup + .5f; stableFrame = Time.frameCount + 4;
            step = 90; return false;
        }
        if (step == 90)
        {
            Need(Time.realtimeSinceStartup < deadline, "Native display did not settle: " + DisplaySettings());
            if (Screen.width != 1280 || Screen.height != 720 || Time.realtimeSinceStartup < stableAfter || Time.frameCount < stableFrame) return false;
            Screen.SetResolution(1024, 768, false);
            stableAfter = Time.realtimeSinceStartup + .5f; stableFrame = Time.frameCount + 3;
            step = 91; return false;
        }
        if (step == 91)
        {
            Need(Time.realtimeSinceStartup < deadline, "Requested display did not settle: " + DisplaySettings());
            if (Screen.width != 1024 || Screen.height != 768 || Time.realtimeSinceStartup < stableAfter || Time.frameCount < stableFrame) return false;
            LetterboxManager.Refresh();
            EmporiumEntry.Instance.SetAspectRatio();
            EmporiumEntry.Instance.Validate(true);
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
            stableAfter = Time.realtimeSinceStartup + .5f; stableFrame = Time.frameCount + 3;
            step = 92; return false;
        }
        if (step == 92)
        {
            Need(Time.realtimeSinceStartup < deadline, "Performance display did not remain stable: " + DisplaySettings());
            if (Screen.width != 1024 || Screen.height != 768 || Screen.fullScreenMode != FullScreenMode.Windowed)
            {
                Screen.SetResolution(1024, 768, false);
                stableAfter = Time.realtimeSinceStartup + .5f; stableFrame = Time.frameCount + 3;
                step = 91; return false;
            }
            if (QualitySettings.vSyncCount != 0 || Application.targetFrameRate != 60)
            {
                QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
                stableAfter = Time.realtimeSinceStartup + .5f; stableFrame = Time.frameCount + 3;
                return false;
            }
            if (Time.realtimeSinceStartup < stableAfter || Time.frameCount < stableFrame) return false;
            CheckSettings();
            step = 2; return false;
        }
        if (step == 2)
        {
            CheckSettings();
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            var dialog = DialogUIManager.Instance;
            if (dialog?.currentDialog != null) dialog.InterruptDialog();
            dialog?.HideChoiceBubble();
            CalibrateAllocations();
            InstallTiming();
            playback = new QaFastPlayback(mode: 0, speed: 2);
            playback.Release(); round = 1; step++; return false;
        }
        if (step == 3)
        {
            playback.Release(); Need(playback.Rate == 1f, "Idle measurement did not use 1x");
            CheckSettings(); stableAfter = Time.realtimeSinceStartup + .5f; stableFrame = Time.frameCount + 12;
            step = 93; return false;
        }
        if (step == 93)
        {
            playback.Release(); CheckSettings(); Need(playback.Rate == 1f, "Idle rate changed during warmup");
            if (Time.realtimeSinceStartup < stableAfter || Time.frameCount < stableFrame) return false;
            idle.Reset(); sampling = 1; step = 4; return false;
        }
        if (step == 4)
        {
            playback.Release(); CheckSettings(); Need(playback.Rate == 1f, "Idle rate changed during sampling");
            if (idle.Count < Frames) return false;
            sampling = 0;
            rows.Add(FrameRow("idle_1x", round, idle));
            BeginTransition(); step++; return false;
        }
        if (step == 5)
        {
            playback.Release(); if (!TransitionFinished()) return false;
            double elapsed = TransitionMilliseconds(); slowDurations.Add(elapsed);
            rows.Add(TransitionRow("idle_1x", round, elapsed));
            playback.Press(); step++; return false;
        }
        if (step == 6)
        {
            playback.Hold(); Need(playback.Rate == 8f, "Fast measurement did not use 8x");
            CheckSettings(); stableAfter = Time.realtimeSinceStartup + .5f; stableFrame = Time.frameCount + 12;
            step = 94; return false;
        }
        if (step == 94)
        {
            playback.Hold(); CheckSettings(); Need(playback.Rate == 8f, "Fast rate changed during warmup");
            if (Time.realtimeSinceStartup < stableAfter || Time.frameCount < stableFrame) return false;
            fast.Reset(); sampling = 2; step = 7; return false;
        }
        if (step == 7)
        {
            playback.Hold(); CheckSettings(); Need(playback.Rate == 8f, "Fast rate changed during sampling");
            if (fast.Count < Frames) return false;
            sampling = 0;
            rows.Add(FrameRow("fast_8x", round, fast));
            BeginTransition(); step++; return false;
        }
        if (step == 8)
        {
            playback.Hold(); if (!TransitionFinished()) return false;
            double elapsed = TransitionMilliseconds(); fastDurations.Add(elapsed);
            rows.Add(TransitionRow("fast_8x", round, elapsed));
            playback.Release();
            if (++round <= 3) { step = 3; return false; }
            Need(fastDurations.Average() < slowDurations.Average() * .7, "8x transitions were not faster than the same 1x fixture");
            string path = Path.Combine(Application.persistentDataPath, "qa-fastplay-performance.csv");
            File.WriteAllLines(path, rows);
            Cleanup();
            result = "PASS F42 performance: three 1x/8x native blackscreen pairs and 60 frames per mode per round; " + path;
            return true;
        }
        throw new InvalidOperationException("Unknown F42 performance step " + step);
    }

    private void CalibrateAllocations()
    {
        try
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            calibrationBuffer = new byte[65536]; calibrationBuffer[0] = 1;
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(calibrationBuffer);
            allocationAvailable = delta >= 65536;
        }
        catch (NotSupportedException) { allocationAvailable = false; }
        catch (NotImplementedException) { allocationAvailable = false; }
        allocationSource = allocationAvailable ? "managed_thread_calibrated" : "unsupported";
    }

    private void InstallTiming()
    {
        var mod = ModLoader.Mods.Find(value => value.Manifest?.ID == Mod);
        var type = mod?.Instance?.GetType().Assembly.GetType("ShopAssist.FastPlayback");
        MethodInfo early = type?.GetMethod("EarlyTick", Static);
        MethodInfo tick = type?.GetMethod("Tick", Instance);
        Need(early != null && tick != null, "F42 core timing targets unavailable");
        timingId = "qa.fastplay.performance." + Guid.NewGuid().ToString("N");
        timing = new Harmony(timingId);
        active = this;
        timing.Patch(early, prefix: Hook(nameof(MeasureStart)), postfix: Hook(nameof(EarlyStop)));
        timing.Patch(tick, prefix: Hook(nameof(MeasureStart)), postfix: Hook(nameof(TickStop)));
        rows.Add("measurement,mode,round,frames,duration_ms,early_mean_us,early_p95_us,tick_mean_us,tick_p95_us,total_mean_us,total_p95_us,alloc_mean_bytes,alloc_p95_bytes,frame_mean_ms,frame_p95_ms,allocation_source,width,height,target_fps,vsync,graphics");
    }

    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(FastPlaybackMotionPerformanceScenario).GetMethod(name, Static));
    private static void MeasureStart(ref Stamp __state)
    {
        var current = active;
        if (current == null || current.sampling == 0) return;
        __state.Valid = true;
        __state.Bytes = current.allocationAvailable ? GC.GetAllocatedBytesForCurrentThread() : -1;
        __state.Ticks = Stopwatch.GetTimestamp();
    }
    private static void EarlyStop(Stamp __state) => MeasureStop(__state, true);
    private static void TickStop(Stamp __state) => MeasureStop(__state, false);
    private static void MeasureStop(Stamp stamp, bool early)
    {
        long end = Stopwatch.GetTimestamp();
        var current = active;
        if (!stamp.Valid || current == null || current.sampling == 0) return;
        long bytes = current.allocationAvailable ? GC.GetAllocatedBytesForCurrentThread() - stamp.Bytes : -1;
        if (bytes < 0) { bytes = -1; current.allocationAvailable = false; current.allocationSource = "unsupported"; }
        (current.sampling == 1 ? current.idle : current.fast).Add(early, end - stamp.Ticks, bytes);
    }

    private void BeginTransition()
    {
        Need(!StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy, "Another blackscreen is already active");
        duringCount = afterCount = 0; finishedAt = 0;
        startedAt = Stopwatch.GetTimestamp();
        StoreUIManager.Instance.StartBlackscreenSequence(true, () => duringCount++, () =>
        {
            afterCount++;
            finishedAt = Stopwatch.GetTimestamp();
        }, .25f, 1f);
        deadline = Time.realtimeSinceStartup + 8f;
    }

    private bool TransitionFinished()
    {
        CheckSettings();
        Need(Time.realtimeSinceStartup < deadline, "Native blackscreen completion timed out");
        if (afterCount == 0) return false;
        Need(duringCount == 1 && afterCount == 1 && finishedAt > startedAt &&
            !StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy, "Native blackscreen callbacks or endpoint changed");
        return true;
    }

    private double TransitionMilliseconds() => (finishedAt - startedAt) * 1000d / Stopwatch.Frequency;

    private string TransitionRow(string mode, int trial, double elapsed) =>
        "transition," + mode + "," + trial + ",," + Number(elapsed) + ",,,,,,,,,,," + Environment();

    private string FrameRow(string mode, int trial, FrameSamples sample)
    {
        Need(sample.Count == Frames, "Incomplete F42 frame samples");
        var early = sample.Early.Select(value => value * 1000000d / Stopwatch.Frequency).ToArray();
        var tick = sample.Tick.Select(value => value * 1000000d / Stopwatch.Frequency).ToArray();
        var total = early.Zip(tick, (a, b) => a + b).ToArray();
        bool allocationsValid = allocationAvailable && sample.Bytes.All(value => value >= 0);
        var allocated = allocationsValid ? sample.Bytes.Select(value => (double)value).ToArray() : null;
        return "frame," + mode + "," + trial + "," + Frames + ",," + Number(early.Average()) + "," + Number(P95(early)) + "," +
            Number(tick.Average()) + "," + Number(P95(tick)) + "," + Number(total.Average()) + "," + Number(P95(total)) + "," +
            Number(allocationsValid ? allocated.Average() : -1) + "," + Number(allocationsValid ? P95(allocated) : -1) + "," +
            Number(sample.FrameMs.Average()) + "," + Number(P95(sample.FrameMs)) + "," + Environment();
    }

    private string Environment() => allocationSource + "," + Screen.width + "," + Screen.height + "," +
        Application.targetFrameRate + "," + QualitySettings.vSyncCount + "," + SystemInfo.graphicsDeviceType;
    private static double P95(double[] values) { var sorted = values.OrderBy(value => value).ToArray(); return sorted[(int)Math.Ceiling(sorted.Length * .95) - 1]; }
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string DisplaySettings()
    {
        var options = EmporiumOption.Instance;
        return "screen=" + Screen.width + "x" + Screen.height + ", mode=" + Screen.fullScreenMode +
            ", targetFrameRate=" + Application.targetFrameRate + ", vSyncCount=" + QualitySettings.vSyncCount +
            ", focused=" + Application.isFocused + ", runInBackground=" + Application.runInBackground +
            ", nativeMode=" + (options == null ? "missing" : options.fullscreenMode.ToString()) +
            ", nativeResolution=" + (options == null ? "missing" : options.resolution.ToString()) +
            ", minimizedCap=" + (options == null ? "missing" : options.fpsCapWhileMinimized.ToString()) +
            ", nativeFpsCap=" + (options == null ? "missing" : options.enableFpsCap.ToString());
    }

    private static void CheckSettings()
    {
        var options = EmporiumOption.Instance;
        if (Screen.width == 1024 && Screen.height == 768 && Screen.fullScreenMode == FullScreenMode.Windowed &&
            Application.targetFrameRate == 60 && QualitySettings.vSyncCount == 0 && options != null &&
            options.fullscreenMode == 2 && options.resolution == EmporiumOption.GetResolutionValueFromDimensions(1280, 720) &&
            !options.fpsCapWhileMinimized && !options.enableFpsCap) return;
        throw new InvalidOperationException("Performance display settings changed during sampling: " + DisplaySettings());
    }

    private void Cleanup()
    {
        sampling = 0; active = null;
        if (timing != null) { timing.UnpatchAll(timingId); timing = null; }
        playback?.Dispose(); playback = null;
        try
        {
            if (nativeOptionsSaved && nativeOptions != null)
            {
                nativeOptions.fullscreenMode = oldOptionFullscreen; nativeOptions.resolution = oldOptionResolution;
                nativeOptions.borderlessDivisor = oldOptionBorderless; nativeOptions.fpsCapWhileMinimized = oldMinimizedCap;
                nativeOptions.enableFpsCap = oldEnableCap;
                nativeOptionsSaved = false;
                nativeOptions.ApplySetting(false);
            }
        }
        finally
        {
            if (settingsSaved)
            {
                Screen.SetResolution(oldWidth, oldHeight, oldFullscreen);
                QualitySettings.vSyncCount = oldVSync; Application.targetFrameRate = oldFrameRate;
                settingsSaved = false;
            }
        }
        calibrationBuffer = null;
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
