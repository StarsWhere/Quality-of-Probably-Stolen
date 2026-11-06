using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

// Runs only in the isolated QA Player. Transitions use the real game coroutine,
// real settings, and the same synthetic key input as the other Player scenarios.
public sealed class FastPlaybackTransitionsScenario
{
    private const string Mod = "starswhere.shopassist";
    private const string PatchOwner = Mod + ".fastplay";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90f;
    private readonly List<string> callbacks = new List<string>();
    private QaFastPlayback playback;
    private bool restored, sawOpaque;
    private int day, step, startedFrame, baselineFrames;
    private float originalScale, startedAt, baselineTime;

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch { Restore(); throw; }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Transition playback timed out at step " + step);
        if (step == 0)
        {
            if (!start.Tick()) return false;
            Configure(); step = 1; return false;
        }
        if (step == 1)
        {
            if (Time.frameCount <= startedFrame || StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy) return false;
            BeginBlack(); step = 2; return false;
        }
        if (step == 2)
        {
            if (callbacks.Count < 2) return false;
            baselineTime = Time.realtimeSinceStartup - startedAt;
            CheckBlack();
            BeginBlack(); step = 3; return false;
        }
        if (step == 3)
        {
            if (Time.realtimeSinceStartup - startedAt < 0.22f) return false;
            playback.Press();
            step = 4; return false;
        }
        if (step == 4)
        {
            if (playback.Rate < 15f) return false;
            playback.Hold();
            if (callbacks.Count < 2) return false;
            float elapsed = Time.realtimeSinceStartup - startedAt;
            CheckBlack();
            Need(elapsed < baselineTime * 0.8f, "Holding the key during an active blackscreen did not accelerate it");
            playback.Release();
            startedFrame = Time.frameCount; step = 5; return false;
        }
        if (step == 5)
        {
            if (playback.Rate != 1f || Time.frameCount <= startedFrame) return false;
            startedFrame = Time.frameCount;
            StoreUIManager.Instance.FinishBlackscreenSequence(true);
            step = 6; return false;
        }
        if (step == 6)
        {
            if (StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy) return false;
            baselineFrames = Time.frameCount - startedFrame;
            Need(baselineFrames >= 90, "The 1x starting fade did not preserve its frame progression");
            playback.Press();
            step = 7; return false;
        }
        if (step == 7)
        {
            if (playback.Rate < 15f) return false;
            playback.Hold();
            startedFrame = Time.frameCount;
            StoreUIManager.Instance.FinishBlackscreenSequence(true);
            step = 8; return false;
        }
        if (step == 8)
        {
            if (StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy) return false;
            int fastFrames = Time.frameCount - startedFrame;
            Need(fastFrames < baselineFrames / 3, "The 100-step starting fade remained limited to one step per frame");
            Need(StoreStation.GetDayCounter() == day && Time.timeScale == originalScale,
                "A visual transition changed the game day or global timescale");
            Restore();
            result = "PASS scoped blackscreen callbacks, mid-transition held key, unchanged game time, and accelerated starting fade";
            return true;
        }
        throw new InvalidOperationException("Unknown transition playback step " + step);
    }

    private void Configure()
    {
        CheckHooks();
        StoreUIManager.Instance.gameOptionUIManager.OpenUI();
        StoreUIManager.Instance.gameOptionUIManager.CloseUI();
        playback = new QaFastPlayback(mode: 0, speed: 3);
        playback.Release();
        originalScale = Time.timeScale; day = StoreStation.GetDayCounter();
        startedFrame = Time.frameCount;
    }

    private static void CheckHooks()
    {
        foreach (var target in new[] {
            Tuple.Create(typeof(StoreUIManager), "BlackScreenSequence"),
            Tuple.Create(typeof(MainMenuUIController), "BlackScreenSequence"),
            Tuple.Create(typeof(StoreUIManager), "PlayerFinishBlackScreenSequence"),
            Tuple.Create(typeof(StoreClientUniqueList), "CaseOpeningRoutine"),
            Tuple.Create(typeof(StoreUIManager), "WoundAnimationRoutine"),
            Tuple.Create(typeof(StoreUIManager), "BodyFallRoutine") })
        {
            var factory = AccessTools.Method(target.Item1, target.Item2);
            var iterator = factory == null ? null : AccessTools.EnumeratorMoveNext(factory);
            var factoryPatches = factory == null ? null : Harmony.GetPatchInfo(factory);
            var iteratorPatches = iterator == null ? null : Harmony.GetPatchInfo(iterator);
            Need(factoryPatches != null && factoryPatches.Postfixes.Any(patch => patch.owner == PatchOwner) &&
                iteratorPatches != null && iteratorPatches.Transpilers.Any(patch => patch.owner == PatchOwner),
                "Transition hook did not install: " + target.Item1.Name + "." + target.Item2);
        }
    }

    private void BeginBlack()
    {
        callbacks.Clear(); sawOpaque = false; startedAt = Time.realtimeSinceStartup;
        StoreUIManager.Instance.StartBlackscreenSequence(true,
            () => { sawOpaque = StoreUIManager.Instance.blackscreen.color.a >= 0.99f; callbacks.Add("during"); },
            () => callbacks.Add("after"), 0.5f, 0.5f);
    }

    private void CheckBlack()
    {
        Need(callbacks.Count == 2 && callbacks[0] == "during" && callbacks[1] == "after" && sawOpaque,
            "Blackscreen callbacks or opaque transition were not preserved");
        Need(!StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy,
            "Blackscreen remained active after its completion callback");
        Need(StoreStation.GetDayCounter() == day && Time.timeScale == originalScale,
            "A visual transition changed the game day or global timescale");
    }

    private void Restore()
    {
        if (restored) return;
        restored = true;
        playback?.Dispose(); playback = null;
    }

    private static void Need(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
