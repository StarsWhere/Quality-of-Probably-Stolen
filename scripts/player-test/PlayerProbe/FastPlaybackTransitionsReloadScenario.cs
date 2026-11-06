using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Reload the isolated native save exactly as the body-fall coroutine starts.
public sealed class FastPlaybackTransitionsReloadScenario
{
    private const string Mod = "starswhere.shopassist";
    private static FastPlaybackTransitionsReloadScenario active;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90f;
    private QaFastPlayback playback;
    private Harmony observer;
    private string observerId, originalSaveFile, expectedRun, reloadError;
    private PropertyInfo epoch;
    private FieldInfo bodyState;
    private int originalSaveSlot, slot, step, frame, beforeEpoch, afterEpoch, bodyStarts, bodyAfterReload, woundAfterReload, shootouts;
    private float waitUntil;
    private bool reloadTriggered, fixtureCaptured, cleaned;

    public bool Tick(out string result)
    {
        try { return Run(out result); }
        catch { Cleanup(); throw; }
    }

    private bool Run(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Shot reload QA timed out at step " + step + State());
        if (step == 0)
        {
            if (!start.Tick()) return false;
            Setup(); step = 1; return false;
        }
        if (step == 1)
        {
            if (Time.frameCount <= frame) return false;
            playback.Press(); step = 2; return false;
        }
        if (step == 2)
        {
            if (playback.Rate < 15f) return false;
            playback.Hold();
            PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
            PlayerStore.Instance.isClientArrived = true;
            StoreUIManager.Instance.EnterShootingMode(); step = 3; return false;
        }
        if (step == 3)
        {
            var ui = StoreUIManager.Instance;
            if (!ui.isShootingMode || ui.shootingUI == null || !ui.shootingUI.activeInHierarchy) return false;
            Need(!ui.isShot, "Reload fixture entered shooting mode after a shot");
            ui.Shoot(); step = 4; return false;
        }
        if (step == 4)
        {
            if (!reloadTriggered) return false;
            Need(reloadError == null && afterEpoch > beforeEpoch && PlayerStore.Instance.saveSlotId == slot &&
                PlayerStore.Instance.runID == expectedRun,
                "Native save reload failed or did not invalidate playback: " + reloadError + State());
            Need(bodyStarts == 1 && shootouts == 0, "Reload did not occur at the first body-fall frame" + State());
            playback.Release(); frame = Time.frameCount; step = 5; return false;
        }
        if (step == 5)
        {
            if (Time.frameCount <= frame) return false;
            playback.Press(); step = 6; return false;
        }
        if (step == 6)
        {
            if (playback.Rate < 15f) return false;
            playback.Hold();
            waitUntil = Time.realtimeSinceStartup + 2f; step = 7; return false;
        }
        if (step == 7)
        {
            Need(bodyAfterReload == 0 && woundAfterReload == 0 && shootouts == 0,
                "A stale native shot-result coroutine continued after reloading" + State());
            if (Time.realtimeSinceStartup < waitUntil) return false;
            Need(bodyStarts == 1 && bodyAfterReload == 0 && woundAfterReload == 0 && shootouts == 0,
                "Reloaded game received an old shot-result callback" + State());
            Cleanup();
            result = "PASS native save reload invalidates both active shot-result coroutines and prevents the old shootout callback";
            return true;
        }
        throw new InvalidOperationException("Unknown shot reload step " + step);
    }

    private void Setup()
    {
        Need(active == null && PlayerStore.Instance.currentClientInstance == null,
            "Shot reload fixture requires an idle store");
        slot = PlayerStore.Instance.saveSlotId; expectedRun = PlayerStore.Instance.runID;
        Need(slot >= 0 && !string.IsNullOrEmpty(expectedRun) && File.Exists(SaveFiles.AbsolutePath(SaveFiles.Slot(slot))),
            "The isolated native save is unavailable");
        originalSaveSlot = NewGameData.Instance.saveSlot; originalSaveFile = NewGameData.Instance.saveFile;
        fixtureCaptured = true;
        NewGameData.Instance.saveSlot = slot; NewGameData.Instance.saveFile = null;
        StoreUIManager.Instance.gameOptionUIManager.OpenUI();
        StoreUIManager.Instance.gameOptionUIManager.CloseUI();
        playback = new QaFastPlayback(mode: 0, speed: 3);
        playback.Release();
        observerId = "qa.fastplay.reload." + Guid.NewGuid().ToString("N");
        observer = new Harmony(observerId); active = this;
        var runtime = ModLoader.Mods.Find(item => item.Manifest != null && item.Manifest.ID == Mod).Assembly
            .GetType("ShopAssist.FastPlayback", true);
        epoch = runtime.GetProperty("Epoch", BindingFlags.Static | BindingFlags.NonPublic);
        Need(epoch != null, "Playback epoch is unavailable");
        Patch(AccessTools.Method(runtime, "AdvanceDialogue"), nameof(SuppressAutomaticAdvance), true);
        var body = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(StoreUIManager), "BodyFallRoutine"));
        var wound = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(StoreUIManager), "WoundAnimationRoutine"));
        bodyState = body == null ? null : AccessTools.Field(body.DeclaringType, "<>1__state");
        Need(bodyState != null && wound != null, "Shot-result iterators are unavailable");
        Patch(body, nameof(BodyFrame), true);
        Patch(wound, nameof(WoundFrame), true);
        Patch(AccessTools.Method(typeof(StoreUIManager), "StartShootoutSequence"), nameof(Shootout), false);
        frame = Time.frameCount;
    }

    private void Patch(MethodBase target, string name, bool prefix)
    {
        var patch = AccessTools.Method(typeof(FastPlaybackTransitionsReloadScenario), name);
        Need(target != null && patch != null, "Shot reload observer unavailable: " + name);
        if (prefix) observer.Patch(target, prefix: new HarmonyMethod(patch));
        else observer.Patch(target, postfix: new HarmonyMethod(patch));
    }

    private static bool SuppressAutomaticAdvance() => false;
    private static void WoundFrame()
    {
        if (active != null && active.reloadTriggered) active.woundAfterReload++;
    }
    private static void BodyFrame(object __instance)
    {
        var scenario = active;
        if (scenario == null) return;
        if (scenario.reloadTriggered) { scenario.bodyAfterReload++; return; }
        if ((int)scenario.bodyState.GetValue(__instance) != 0) return;
        scenario.bodyStarts++;
        scenario.beforeEpoch = (int)scenario.epoch.GetValue(null);
        try { PlayerStore.Instance.LoadGame(); }
        catch (Exception error) { scenario.reloadError = error.ToString(); }
        scenario.afterEpoch = (int)scenario.epoch.GetValue(null);
        scenario.reloadTriggered = true;
    }
    private static void Shootout() { if (active != null) active.shootouts++; }

    private string State() => "; bodyStarts=" + bodyStarts + ", bodyAfter=" + bodyAfterReload +
        ", woundAfter=" + woundAfterReload + ", shootouts=" + shootouts + ", epoch=" + beforeEpoch + "->" + afterEpoch;

    private void Cleanup()
    {
        if (cleaned) return;
        cleaned = true; active = null;
        if (observer != null) observer.UnpatchAll(observerId);
        playback?.Dispose(); playback = null;
        if (fixtureCaptured && NewGameData.Instance != null)
        {
            NewGameData.Instance.saveSlot = originalSaveSlot;
            NewGameData.Instance.saveFile = originalSaveFile;
        }
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
