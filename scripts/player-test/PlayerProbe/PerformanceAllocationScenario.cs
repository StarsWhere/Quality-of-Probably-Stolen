using System;
using System.IO;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

// Checks whether this release Player exposes a trustworthy allocation counter.
public sealed class PerformanceAllocationScenario
{
    private static byte[] keep;
    private ProfilerRecorder frameBytes, allocationMarker;
    private bool wasEnabled;
    private int step, frame;
    private long beforeManaged, afterManaged, beforeFrame, afterFrame, beforeMarker, afterMarker;

    public bool Tick(out string result)
    {
        result = null;
        if (step == 0)
        {
            wasEnabled = Profiler.enabled;
            if (Profiler.supported) Profiler.enabled = true;
            var options = ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.CollectOnlyOnCurrentThread |
                ProfilerRecorderOptions.SumAllSamplesInFrame;
            frameBytes = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 4, options);
            allocationMarker = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 4, options);
            frame = Time.frameCount + 3; step = 1; return false;
        }
        if (step == 1)
        {
            if (Time.frameCount < frame) return false;
            beforeManaged = GC.GetAllocatedBytesForCurrentThread();
            beforeFrame = frameBytes.Valid ? frameBytes.CurrentValue : -1;
            beforeMarker = allocationMarker.Valid ? allocationMarker.CurrentValue : -1;
            keep = new byte[8 * 1024 * 1024]; keep[0] = 1;
            afterManaged = GC.GetAllocatedBytesForCurrentThread();
            afterFrame = frameBytes.Valid ? frameBytes.CurrentValue : -1;
            afterMarker = allocationMarker.Valid ? allocationMarker.CurrentValue : -1;
            frame = Time.frameCount + 1; step = 2; return false;
        }
        if (Time.frameCount < frame) return false;
        string value = "profiler_supported=" + Profiler.supported + "; profiler_enabled=" + Profiler.enabled +
            "; managed_delta=" + (afterManaged - beforeManaged) + "; frame_valid=" + frameBytes.Valid +
            "; frame_current_delta=" + (afterFrame - beforeFrame) + "; frame_last=" + (frameBytes.Valid ? frameBytes.LastValue : -1) +
            "; marker_valid=" + allocationMarker.Valid + "; marker_current_delta=" + (afterMarker - beforeMarker) +
            "; marker_last=" + (allocationMarker.Valid ? allocationMarker.LastValue : -1);
        File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-performance-allocation-calibration.txt"), value);
        if (frameBytes.Valid) frameBytes.Dispose();
        if (allocationMarker.Valid) allocationMarker.Dispose();
        if (Profiler.supported) Profiler.enabled = wasEnabled;
        result = "PASS release Player allocation counter calibration: " + value;
        return true;
    }
}
