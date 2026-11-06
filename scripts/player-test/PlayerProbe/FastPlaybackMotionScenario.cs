using System;
using System.Linq;
using System.Reflection;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;

// Exercises real presentation paths in the guarded Player, including a competing tween writer.
public sealed class FastPlaybackMotionScenario
{
    private static readonly BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo Duration = typeof(StoreShutterButton).GetField("duration", F);
    private static readonly FieldInfo Elapsed = typeof(StoreShutterButton).GetField("elapsedTime", F);
    private static readonly FieldInfo Closed = typeof(StoreShutterButton).GetField("isClosed", F);
    private static readonly FieldInfo ClientPath = typeof(StoreClientMono).GetField("dOTweenPath", F);
    private static readonly FieldInfo ClientWalkingIn = typeof(StoreClientMono).GetField("walkingIn", F);
    private static readonly FieldInfo ClientIgnoreLeft = typeof(StoreClientMono).GetField("ignoreOnLeftFlag", F);
    private static readonly FieldInfo GuardPath = typeof(GenericClientMono).GetField("dOTweenPath", F);
    private static readonly FieldInfo GuardWalkingIn = typeof(GenericClientMono).GetField("walkingIn", F);
    private static readonly FieldInfo GuardIgnoreLeft = typeof(GenericClientMono).GetField("ignoreOnLeftFlag", F);
    private static int footsteps, arrivals, departures, guardArrivals, guardDepartures, guardDispatches;
    private static int fastGuardArrivals, fastGuardDepartures;
    private static int unlocked, frontHidden, closeClient, closeSound, dismissCalls;
    private static float lastFootstep, lastGuardDispatch, unlockedAt, closedAt;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float began = Time.realtimeSinceStartup;
    private QaFastPlayback playback;
    private Harmony observer;
    private string observerId;
    private Tween customerTween, controlTween;
    private float unrelated, initialTimeScale, timer, startedAt;
    private int step, speedIndex, previousArrivals, previousDepartures, lockBefore;
    private StoreClientInstance replacementClient;
    private static readonly float[] Rates = { 2f, 4f, 8f, 16f };

    public bool Tick(out string result)
    {
        result = null;
        try
        {
            if (Time.realtimeSinceStartup - began > 120f) throw new InvalidOperationException("Motion QA timed out at step " + step);
            return Run(out result);
        }
        catch { Cleanup(); throw; }
    }

    private bool Run(out string result)
    {
        result = null;
        if (step == 0) { if (start.Tick()) step++; return false; }
        var ui = StoreUIManager.Instance;
        var player = PlayerStore.Instance;
        if (step == 1)
        {
            if (StoreShutterButton.Instance == null || ui?.clientGameObject == null || ui.secGameObject == null ||
                ui.secGameObject2 == null || EmporiumAudioPlayer.Instance == null) return false;
            Need(Duration != null && Elapsed != null && Closed != null && ClientPath != null && ClientWalkingIn != null &&
                ClientIgnoreLeft != null && GuardPath != null && GuardWalkingIn != null && GuardIgnoreLeft != null, "Native motion fields unavailable");
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) ui.storeCalendar.OnClick();
            CloseDialogue();
            footsteps = arrivals = departures = guardArrivals = guardDepartures = guardDispatches = 0;
            observerId = "qa.fastplay.motion." + Guid.NewGuid().ToString("N");
            observer = new Harmony(observerId);
            Observe(AccessTools.Method(typeof(EmporiumAudioPlayer), "<PlayWalking>b__39_0"), nameof(Footstep));
            Observe(AccessTools.Method(typeof(StoreClientMono), nameof(StoreClientMono.OnArrived)), nameof(CustomerArrived));
            Observe(AccessTools.Method(typeof(StoreClientMono), nameof(StoreClientMono.OnLeft)), nameof(CustomerLeft));
            Observe(AccessTools.Method(typeof(GenericClientMono), nameof(GenericClientMono.OnArrived)), nameof(GuardArrived));
            Observe(AccessTools.Method(typeof(GenericClientMono), nameof(GenericClientMono.OnLeft)), nameof(GuardLeft));
            Observe(AccessTools.Method(typeof(GenericClientMono), nameof(GenericClientMono.StartArrive)), nameof(GuardDispatched));
            Observe(AccessTools.Method(typeof(StoreShutterButton), "<OpenShutter>b__26_0"), nameof(OpeningUnlocked));
            Observe(Closure("<EndAndClose>b__29_0"), nameof(FrontHidden));
            Observe(Closure("<EndAndClose>b__1"), nameof(CloseClient));
            Observe(Closure("<EndAndClose>b__2"), nameof(CloseSound));
            Observe(AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.DismissCurrentClient)), nameof(Dismissed));
            initialTimeScale = Time.timeScale;
            controlTween = DOTween.To(() => unrelated, value => unrelated = value, 1f, 1000f).SetEase(Ease.Linear);
            controlTween.timeScale = .7f;
            playback = new QaFastPlayback(mode: 0, speed: 3);
            playback.Release(); step++; return false;
        }
        if (step == 2) { playback.Press(); step++; return false; }
        if (step == 3)
        {
            playback.Hold(); Need(playback.Rate == 16f, "Playback did not enter held mode");
            speedIndex = 0; playback.Configure(0, speedIndex); step++; return false;
        }
        if (step == 4)
        {
            playback.Hold();
            Need(playback.Rate == Rates[speedIndex], "Wrong speed for shutter sample " + speedIndex);
            SampleShutter(StoreShutterButton.Instance, Rates[speedIndex], false);
            SampleShutter(StoreShutterButton.Instance, Rates[speedIndex], true);
            speedIndex++;
            if (speedIndex < Rates.Length) { playback.Configure(0, speedIndex); return false; }
            Need(Time.timeScale == initialTimeScale && Near(controlTween.timeScale, .7f), "Playback changed a global or unrelated tween clock");
            footsteps = 0; startedAt = Time.time; EmporiumAudioPlayer.Instance.PlayWalking();
            timer = Time.realtimeSinceStartup + 5f; step++; return false;
        }
        if (step == 5)
        {
            playback.Hold();
            Need(Time.realtimeSinceStartup < timer, "Walking sound callbacks did not finish");
            if (footsteps < 4) return false;
            Need(footsteps == 4 && lastFootstep - startedAt < 1.5f,
                "Four walking callbacks were duplicated or did not use the presentation clock");
            timer = Time.realtimeSinceStartup + .35f; step++; return false;
        }
        if (step == 6)
        {
            playback.Hold(); if (Time.realtimeSinceStartup < timer) return false;
            Need(footsteps == 4 && Time.timeScale == initialTimeScale && Near(controlTween.timeScale, .7f),
                "Walking playback duplicated callbacks or changed an unrelated clock");
            player.currentClientInstance = QuietClient();
            player.callNextWhenPossible = false;
            ui.DisplayClient(); step++; return false;
        }
        if (step == 7)
        {
            playback.Hold();
            var path = (DOTweenPath)ClientPath.GetValue(ui.clientGameObject);
            if (path?.tween == null || !path.tween.IsActive()) return false;
            customerTween = path.tween;
            customerTween.timeScale = .02f;
            previousArrivals = arrivals;
            ui.QueueClientArriveAnimation();
            Need(customerTween.IsPlaying() && Near(customerTween.timeScale, .32f), "Customer arrival path was not scoped to 16x");
            customerTween.timeScale = .04f; step++; return false;
        }
        if (step == 8)
        {
            playback.Hold();
            Need(Near(customerTween.timeScale, .64f), "External tween baseline was not adopted during playback");
            customerTween.timeScale = .005f;
            playback.Release(); step++; return false;
        }
        if (step == 9)
        {
            Need(playback.Rate == 1f && Near(customerTween.timeScale, .005f),
                "Releasing playback overwrote a competing tween writer");
            playback.Press(); step++; return false;
        }
        if (step == 10)
        {
            playback.Hold();
            Need(playback.Rate == 16f && Near(customerTween.timeScale, .08f), "Resuming did not use the new tween baseline");
            customerTween.timeScale = .25f;
            timer = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (step == 11)
        {
            playback.Hold(); Need(Time.realtimeSinceStartup < timer, "Customer did not naturally arrive");
            if (arrivals == previousArrivals) return false;
            Need(arrivals == previousArrivals + 1 && player.isClientArrived, "Customer arrival callback did not complete exactly once");
            step++; return false;
        }
        if (step == 12)
        {
            playback.Hold();
            Need(Near(customerTween.timeScale, .25f), "Completed customer tween did not restore the external baseline");
            CloseDialogue(); step++; return false;
        }
        if (step == 13)
        {
            playback.Hold(); if (playback.Rate != 16f) return false;
            previousDepartures = departures;
            ui.OnClientStartLeaving(false);
            timer = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (step == 14)
        {
            playback.Hold(); Need(Time.realtimeSinceStartup < timer, "Customer did not naturally leave on the backward path");
            if (departures == previousDepartures) return false;
            Need(departures == previousDepartures + 1 && !ui.clientGameObject.gameObject.activeInHierarchy,
                "Backward customer path did not complete once");
            CloseDialogue();
            player.currentClientInstance = QuietClient();
            ui.DisplayClient(); step++; return false;
        }
        if (step == 15)
        {
            playback.Hold(); if (playback.Rate != 16f) return false;
            previousArrivals = arrivals;
            ui.QueueClientArriveAnimation();
            timer = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (step == 16)
        {
            playback.Hold(); Need(Time.realtimeSinceStartup < timer, "Customer did not return for right-path test");
            if (arrivals == previousArrivals) return false;
            Need(arrivals == previousArrivals + 1, "Second arrival callback repeated");
            CloseDialogue(); step++; return false;
        }
        if (step == 17)
        {
            playback.Hold(); if (playback.Rate != 16f) return false;
            var path = ui.clientGameObject.pathRight;
            Need(path != null && path.tween != null && path.tween.IsActive(), "Native right-hand customer path unavailable");
            previousDepartures = departures;
            ui.OnClientStartLeaving(true);
            Need(path.tween.IsPlaying() && path.tween.timeScale > 1f, "Right-hand customer path was not accelerated");
            timer = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (step == 18)
        {
            playback.Hold(); Need(Time.realtimeSinceStartup < timer, "Customer did not naturally leave on the right path");
            if (departures == previousDepartures) return false;
            Need(departures == previousDepartures + 1 && !ui.clientGameObject.gameObject.activeInHierarchy,
                "Right-hand customer path did not complete once");
            CloseDialogue();
            player.currentClientInstance = QuietClient();
            player.currentClientInstance.GetClientBlueprint().customOnArrestDialogue = new Dialogue().SetText("QA", "Guard");
            step++; return false;
        }
        if (step == 19)
        {
            playback.Hold(); if (playback.Rate != 16f) return false;
            guardDispatches = guardArrivals = guardDepartures = fastGuardArrivals = fastGuardDepartures = 0;
            startedAt = Time.time;
            ui.StartArrestSequence(2.5f);
            Need(guardDispatches == 0, "Delayed arrest dispatched guards synchronously");
            timer = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (step == 20)
        {
            playback.Hold(); Need(Time.realtimeSinceStartup < timer, "Arrest did not dispatch both guards");
            if (guardDispatches < 2) return false;
            Need(guardDispatches == 2 && lastGuardDispatch - startedAt < 1.5f, "Guard dispatch did not use the presentation clock");
            timer = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (step == 21)
        {
            playback.Hold(); Need(Time.realtimeSinceStartup < timer, "Guards did not naturally arrive");
            if (guardArrivals < 2) return false;
            Need(guardArrivals == 2 && fastGuardArrivals == 2, "Guard arrival callbacks repeated or a path was not accelerated");
            CloseDialogue(); step++; return false;
        }
        if (step == 22)
        {
            playback.Hold(); if (playback.Rate != 16f) return false;
            ui.StartLeaveSec();
            timer = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (step == 23)
        {
            playback.Hold(); Need(Time.realtimeSinceStartup < timer, "Guards did not naturally leave");
            if (guardDepartures < 2) return false;
            Need(guardDepartures == 2 && fastGuardDepartures == 2 && Time.timeScale == initialTimeScale && Near(controlTween.timeScale, .7f),
                "Guard callbacks repeated or an unrelated clock changed");
            CloseDialogue();
            player.currentClientInstance = null;
            step++; return false;
        }
        if (step == 24)
        {
            playback.Hold(); if (playback.Rate != 16f) return false;
            unlocked = 0; lockBefore = player.lockShutterButtonFlag;
            startedAt = Time.time;
            StoreShutterButton.Instance.OpenShutter();
            Need(player.storeState == PlayerStore.StoreState.OPEN && player.lockShutterButtonFlag == lockBefore + 1,
                "Native opening did not apply its state and shutter lock");
            timer = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (step == 25)
        {
            playback.Hold(); Need(Time.realtimeSinceStartup < timer, "Opening shutter did not unlock");
            if (unlocked == 0) return false;
            float duration = (float)Duration.GetValue(StoreShutterButton.Instance);
            Need(unlocked == 1 && player.lockShutterButtonFlag == lockBefore && unlockedAt - startedAt < duration / 4f + .6f &&
                Vector3.Distance(StoreShutterButton.Instance.shutterGameObject.transform.position, StoreShutterButton.Instance.endPosition) < .001f,
                "Native opening callback, timing or endpoint changed");
            CloseDialogue(); step++; return false;
        }
        if (step == 26)
        {
            playback.Hold(); if (playback.Rate != 16f) return false;
            var oldClient = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
            replacementClient = StoreClientInstance.CreateClientInstance(StoreClientList.CreateSpacerClientFood());
            player.currentClientInstance = oldClient;
            frontHidden = closeClient = closeSound = dismissCalls = 0;
            startedAt = Time.time;
            StoreShutterButton.Instance.EndAndClose();
            Need(player.storeState == PlayerStore.StoreState.CLOSED && ReferenceEquals(player.currentClientInstance, oldClient),
                "Native close did not retain the originally captured customer");
            player.currentClientInstance = replacementClient;
            dismissCalls = 0;
            CloseDialogue();
            timer = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (step == 27)
        {
            playback.Hold(); Need(Time.realtimeSinceStartup < timer, "Closing shutter callbacks did not finish");
            if (frontHidden == 0 || closeClient == 0 || closeSound == 0) return false;
            float duration = (float)Duration.GetValue(StoreShutterButton.Instance);
            Need(frontHidden == 1 && closeClient == 1 && closeSound == 1 && dismissCalls == 0 &&
                ReferenceEquals(player.currentClientInstance, replacementClient) && closedAt - startedAt < (duration + 1.25f) / 4f + .6f,
                "Native close callbacks repeated, were not accelerated, or dismissed a replacement customer");
            timer = Time.realtimeSinceStartup + .25f; step++; return false;
        }
        if (step == 28)
        {
            playback.Hold(); if (Time.realtimeSinceStartup < timer) return false;
            Need(unlocked == 1 && frontHidden == 1 && closeClient == 1 && closeSound == 1 && dismissCalls == 0,
                "Shutter callbacks repeated after reaching the endpoint");
            Cleanup();
            result = "PASS F42 shutter motion and native callbacks, stale-customer guard, walking timers, customer forward/back/right paths, external tween baseline and guard endpoints";
            return true;
        }
        throw new InvalidOperationException("Unknown motion QA step " + step);
    }

    private static void SampleShutter(StoreShutterButton door, float rate, bool closing)
    {
        float duration = (float)Duration.GetValue(door), elapsed = (float)Elapsed.GetValue(door);
        bool closed = (bool)Closed.GetValue(door);
        Vector3 shutter = door.shutterGameObject.transform.position, blocker = door.blockerGameObject.transform.localPosition;
        try
        {
            Duration.SetValue(door, 40f); Elapsed.SetValue(door, 0f); Closed.SetValue(door, closing);
            door.shutterGameObject.transform.position = closing ? door.endPosition : door.startPosition;
            door.blockerGameObject.transform.localPosition = closing ? door.blockerEndPosition : door.blockerStartPosition;
            door.LerpShutter(door.shutterGameObject);
            float actual = (float)Elapsed.GetValue(door);
            Need(Near(actual, Time.deltaTime * rate), "Shutter " + (closing ? "closing" : "opening") + " clock differs at " + rate + "x");
        }
        finally
        {
            Duration.SetValue(door, duration); Elapsed.SetValue(door, elapsed); Closed.SetValue(door, closed);
            door.shutterGameObject.transform.position = shutter; door.blockerGameObject.transform.localPosition = blocker;
        }
    }

    private void Observe(MethodInfo original, string handler)
    {
        Need(original != null, "Missing native observation target " + handler);
        observer.Patch(original, prefix: new HarmonyMethod(typeof(FastPlaybackMotionScenario), handler));
    }
    private static MethodInfo Closure(string name) => typeof(StoreShutterButton).GetNestedTypes(BindingFlags.NonPublic)
        .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        .SingleOrDefault(method => method.Name == name);
    private static StoreClientInstance QuietClient()
    {
        StoreClient client = StoreClientList.CreateJunkerSellOnly();
        client.mainDialogue = null;
        return StoreClientInstance.CreateClientInstance(client);
    }
    private static void Footstep() { footsteps++; lastFootstep = Time.time; }
    private static void CustomerArrived(StoreClientMono __instance) { if ((bool)ClientWalkingIn.GetValue(__instance)) arrivals++; }
    private static void CustomerLeft(StoreClientMono __instance) { if (!(bool)ClientIgnoreLeft.GetValue(__instance)) departures++; }
    private static void GuardDispatched() { guardDispatches++; lastGuardDispatch = Time.time; }
    private static void GuardArrived(GenericClientMono __instance)
    {
        if (!(bool)GuardWalkingIn.GetValue(__instance)) return;
        guardArrivals++;
        if (((DOTweenPath)GuardPath.GetValue(__instance))?.tween?.timeScale > 1f) fastGuardArrivals++;
    }
    private static void GuardLeft(GenericClientMono __instance)
    {
        if ((bool)GuardIgnoreLeft.GetValue(__instance)) return;
        guardDepartures++;
        if (((DOTweenPath)GuardPath.GetValue(__instance))?.tween?.timeScale > 1f) fastGuardDepartures++;
    }
    private static void OpeningUnlocked() { unlocked++; unlockedAt = Time.time; }
    private static void FrontHidden() { frontHidden++; closedAt = Time.time; }
    private static void CloseClient() { closeClient++; closedAt = Time.time; }
    private static void CloseSound() { closeSound++; closedAt = Time.time; }
    private static void Dismissed() { dismissCalls++; }
    private static void CloseDialogue()
    {
        var dialog = DialogUIManager.Instance;
        if (dialog == null) return;
        if (dialog.currentDialog != null) dialog.InterruptDialog();
        dialog.HideChoiceBubble();
    }
    private void Cleanup()
    {
        playback?.Dispose(); playback = null;
        if (observer != null) { observer.UnpatchAll(observerId); observer = null; }
        if (controlTween != null && controlTween.IsActive()) controlTween.Kill();
        controlTween = null;
    }
    private static bool Near(float value, float expected) => Mathf.Abs(value - expected) < .001f;
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
