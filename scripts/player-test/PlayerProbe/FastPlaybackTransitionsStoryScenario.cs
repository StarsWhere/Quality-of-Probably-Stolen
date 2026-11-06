using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Exercises the native presentation coroutines in an isolated QA run. The test
// holds a line open to verify the game's real-time dialogue watchdogs separately
// from the speed of the surrounding presentation.
public sealed class FastPlaybackTransitionsStoryScenario
{
    private const string Mod = "starswhere.shopassist";
    private static FastPlaybackTransitionsStoryScenario active;
    private static FieldInfo woundState, bodyState;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90f;
    private QaFastPlayback playback;
    private Harmony observer;
    private string observerId;
    private Coroutine caseCoroutine;
    private StoreClientInstance previousCustomer, caseCustomer, shootingCustomer;
    private GameItem shownItem, interruptedItem, rewardItem;
    private bool previousArrived, previousAutoCall, previousCallNext, forceLine, collecting, cleaned, interruptedCleanup, fixtureCaptured;
    private bool awaitingFirstDismiss, shotAtShootout, handlingAtShootout, securityAtShootout;
    private int step, startedFrame, spinItems, rewardCount, rewardBeforeInterrupt, woundCount, woundFactoryCount, bodyCount, bodyFactoryCount, shootoutCount;
    private int spinAtInterrupt, firstDismissCount, bodyAtShootout;
    private float caseStarted, spinStarted, goldStarted, rewardAt, interruptedAt;
    private bool finalLine;

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch { Cleanup(); throw; }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Story playback timed out at step " + step);
        if (step == 0)
        {
            if (!start.Tick()) return false;
            Setup(); step = 1; return false;
        }
        if (step == 1)
        {
            if (Time.frameCount <= startedFrame) return false;
            playback.Press(); step = 2; return false;
        }
        if (step == 2)
        {
            if (playback.Rate < 15f) return false;
            playback.Hold();
            StartCase(true); step = 3; return false;
        }
        if (step == 3)
        {
            if (spinItems < 3) return false;
            Need(spinStarted - caseStarted >= 5.7f, "The 6-second opening dialogue timeout was accelerated");
            step = 4; return false;
        }
        if (step == 4)
        {
            Need(rewardCount <= 1, "Case reward was generated more than once");
            if (rewardCount == 0) return false;
            Need(goldStarted > 0f && rewardAt - goldStarted >= 1.8f,
                "The 2-second gold dialogue timeout was accelerated");
            Need(rewardItem != null && rewardItem.parentInventory != null,
                "The case reward was created but not placed in an inventory");
            step = 5; return false;
        }
        if (step == 5)
        {
            if (!finalLine) return false;
            Need(rewardCount == 1, "Case reward count changed after the final line");
            caseCoroutine = null;
            // Interrupting this line executes its native end action, which schedules
            // CurrentClientLeave(1). Let that delayed callback finish against the
            // original customer before replacing the fixture.
            forceLine = false;
            awaitingFirstDismiss = true;
            DialogUIManager.Instance.InterruptDialog();
            step = 51; return false;
        }
        if (step == 51)
        {
            Need(firstDismissCount <= 1, "The first case customer was dismissed more than once");
            if (ReferenceEquals(PlayerStore.Instance.currentClientInstance, caseCustomer)) return false;
            Need(PlayerStore.Instance.currentClientInstance == null && firstDismissCount == 1,
                "The first case did not finish its native customer dismissal");
            if (StoreUIManager.Instance.clientGameObject.IsMoving() || PlayerStore.Instance.lockNextBellFlag != 0) return false;
            awaitingFirstDismiss = false;
            rewardBeforeInterrupt = rewardCount;
            StartCase(false); step = 6; return false;
        }
        if (step == 6)
        {
            if (spinItems < 3) return false;
            interruptedItem = shownItem;
            spinAtInterrupt = spinItems;
            PlayerStore.Instance.currentClientInstance = null;
            interruptedAt = Time.time; step = 7; return false;
        }
        if (step == 7)
        {
            if (!interruptedCleanup || Time.time - interruptedAt < 0.5f) return false;
            Need(spinItems == spinAtInterrupt && rewardCount == rewardBeforeInterrupt,
                "An interrupted case continued spinning or produced a reward");
            caseCoroutine = null;
            shootingCustomer = NewCustomer();
            PlayerStore.Instance.currentClientInstance = shootingCustomer;
            PlayerStore.Instance.isClientArrived = true;
            DialogUIManager.Instance.InterruptDialog();
            StoreUIManager.Instance.EnterShootingMode();
            step = 8; return false;
        }
        if (step == 8)
        {
            var ui = StoreUIManager.Instance;
            if (!ui.isShootingMode || ui.shootingUI == null || !ui.shootingUI.activeInHierarchy) return false;
            Need(!ui.isShot && ui.shootingWoundSprites.Count > 0 && ui.shootingBodySprites.Count > 0,
                "Shooting fixture is not ready");
            ui.Shoot();
            Need(ui.isShot && ReferenceEquals(ui.shotClientInstance, shootingCustomer),
                "Native Shoot did not capture the expected customer: " + ShotState());
            step = 9; return false;
        }
        if (step == 9)
        {
            Need(woundCount <= 1 && bodyCount <= 1 && shootoutCount <= 1,
                "Shot result started a native continuation more than once: " + ShotState());
            if (shootoutCount == 0) return false;
            Need(woundCount == 1 && bodyCount == 1 && StoreUIManager.Instance.isShot && PlayerStore.Instance.isHandlingShootout,
                "Shot result did not complete the native wound, body-fall and security sequence: " + ShotState());
            startedFrame = Time.frameCount; step = 10; return false;
        }
        if (step == 10)
        {
            if (Time.frameCount < startedFrame + 5) return false;
            Need(woundCount == 1 && bodyCount == 1 && shootoutCount == 1,
                "Shot result repeated after completion: " + ShotState());
            Debug.Log("[PLAYER-QA] Shot result " + ShotState());
            Cleanup();
            result = "PASS case presentation, original 6s/2s watchdogs, one reward, customer interruption, and native shot-result continuation";
            return true;
        }
        throw new InvalidOperationException("Unknown story playback step " + step);
    }

    private void Setup()
    {
        Need(active == null, "Another story observer is active");
        previousCustomer = PlayerStore.Instance.currentClientInstance;
        previousArrived = PlayerStore.Instance.isClientArrived;
        previousCallNext = PlayerStore.Instance.callNextWhenPossible;
        previousAutoCall = EmporiumOption.Instance.autoCallNext;
        Need(previousCustomer == null, "Story fixture requires no active customer");
        fixtureCaptured = true;
        EmporiumOption.Instance.autoCallNext = false;
        StoreUIManager.Instance.gameOptionUIManager.OpenUI();
        StoreUIManager.Instance.gameOptionUIManager.CloseUI();
        playback = new QaFastPlayback(mode: 0, speed: 3);
        playback.Release();
        observerId = "qa.fastplay.story." + Guid.NewGuid().ToString("N");
        observer = new Harmony(observerId);
        active = this;
        var runtime = ModLoader.Mods.Find(item => item.Manifest != null && item.Manifest.ID == Mod).Assembly
            .GetType("ShopAssist.FastPlayback", true);
        Patch(AccessTools.Method(runtime, "AdvanceDialogue"), nameof(SuppressAutomaticAdvance), true);
        Patch(AccessTools.Method(typeof(DialogUIManager), "IsLineStillDisplaying"), nameof(HoldLine), true);
        Patch(AccessTools.Method(typeof(StoreClientUniqueList), "ShowSpinItem"), nameof(ObserveSpin), false);
        Patch(AccessTools.Method(typeof(StoreClientUniqueList), "DestroyShown"), nameof(ObserveDestroy), false);
        Patch(AccessTools.Method(typeof(StoreClientUniqueList), "CaseSceneLine"), nameof(ObserveCaseLine), false);
        Patch(AccessTools.Method(typeof(DirectoryMaster), "Item", new[] { typeof(string), typeof(bool) }), nameof(ObserveItem), false);
        var woundFactory = AccessTools.Method(typeof(StoreUIManager), "WoundAnimationRoutine");
        var bodyFactory = AccessTools.Method(typeof(StoreUIManager), "BodyFallRoutine");
        var woundMove = woundFactory == null ? null : AccessTools.EnumeratorMoveNext(woundFactory);
        var bodyMove = bodyFactory == null ? null : AccessTools.EnumeratorMoveNext(bodyFactory);
        woundState = woundMove == null ? null : AccessTools.Field(woundMove.DeclaringType, "<>1__state");
        bodyState = bodyMove == null ? null : AccessTools.Field(bodyMove.DeclaringType, "<>1__state");
        Need(woundState != null && bodyState != null, "Native shot-result iterator state fields are unavailable");
        Patch(woundMove, nameof(ObserveWound), true);
        Patch(bodyMove, nameof(ObserveBody), true);
        Patch(woundFactory, nameof(ObserveWoundFactory), false);
        Patch(bodyFactory, nameof(ObserveBodyFactory), false);
        Patch(AccessTools.Method(typeof(StoreUIManager), "StartShootoutSequence"), nameof(ObserveShootout), false);
        Patch(AccessTools.Method(typeof(PlayerStore), "DismissCurrentClient"), nameof(ObserveDismiss), true);
        collecting = true;
        startedFrame = Time.frameCount;
    }

    private void Patch(MethodBase target, string method, bool prefix)
    {
        var patch = AccessTools.Method(typeof(FastPlaybackTransitionsStoryScenario), method);
        Need(target != null && patch != null, "Story observer hook missing: " + method);
        if (prefix) observer.Patch(target, prefix: new HarmonyMethod(patch));
        else observer.Patch(target, postfix: new HarmonyMethod(patch));
    }

    private void StartCase(bool waitForTimeouts)
    {
        spinItems = 0; finalLine = false; spinStarted = goldStarted = rewardAt = 0f;
        forceLine = waitForTimeouts;
        caseCustomer = NewCustomer();
        PlayerStore.Instance.currentClientInstance = caseCustomer;
        PlayerStore.Instance.isClientArrived = true;
        StoreUIManager.Instance.clientGameObject.SetImmediatlyArrive();
        var opening = new Dialogue().SetText("QA", "Opening");
        var method = AccessTools.Method(typeof(StoreClientUniqueList), "CaseOpeningRoutine");
        Need(method != null, "Case iterator is unavailable");
        var iterator = method.Invoke(null, new object[] { caseCustomer, "QA", opening }) as IEnumerator;
        Need(iterator != null, "Case iterator was not created");
        caseStarted = Time.time;
        caseCoroutine = StoreUIManager.Instance.StartCoroutine(iterator);
    }

    private static StoreClientInstance NewCustomer() =>
        StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());

    private static bool SuppressAutomaticAdvance() => false;
    private static bool HoldLine(ref bool __result)
    {
        if (active == null || !active.forceLine) return true;
        __result = true; return false;
    }
    private static void ObserveSpin(GameItem __result)
    {
        if (active == null || !active.collecting) return;
        active.spinItems++; active.shownItem = __result;
        if (active.spinItems == 3) active.spinStarted = Time.time;
    }
    private static void ObserveDestroy(GameItem __0)
    {
        if (active != null && active.collecting && active.interruptedItem != null &&
            ReferenceEquals(active.interruptedItem, __0)) active.interruptedCleanup = true;
    }
    private static void ObserveCaseLine(string __1)
    {
        if (active == null || !active.collecting) return;
        if (__1 == "dialog_case_enjoyer_open_2") active.goldStarted = Time.time;
        if (__1 == "dialog_case_enjoyer_open_4") active.finalLine = true;
    }
    private static void ObserveItem(string __0, GameItem __result)
    {
        if (active == null || !active.collecting || __0 != "glass_shard_shiv" || __result == null) return;
        active.rewardCount++; active.rewardAt = Time.time; active.rewardItem = __result;
    }
    private static void ObserveWound(object __instance)
    {
        if (active != null && active.collecting && (int)woundState.GetValue(__instance) == 0) active.woundCount++;
    }
    private static void ObserveBody(object __instance)
    {
        if (active != null && active.collecting && (int)bodyState.GetValue(__instance) == 0) active.bodyCount++;
    }
    private static void ObserveWoundFactory()
    {
        if (active != null && active.collecting) active.woundFactoryCount++;
    }
    private static void ObserveBodyFactory()
    {
        if (active != null && active.collecting) active.bodyFactoryCount++;
    }
    private static void ObserveShootout(StoreUIManager __instance)
    {
        if (active == null || !active.collecting) return;
        active.shootoutCount++;
        active.bodyAtShootout = active.bodyCount;
        active.shotAtShootout = __instance.isShot;
        active.handlingAtShootout = PlayerStore.Instance.isHandlingShootout;
        active.securityAtShootout = __instance.secGameObject.gameObject.activeInHierarchy;
    }
    private static void ObserveDismiss(PlayerStore __instance)
    {
        if (active != null && active.collecting && active.awaitingFirstDismiss &&
            ReferenceEquals(__instance.currentClientInstance, active.caseCustomer)) active.firstDismissCount++;
    }

    private string ShotState()
    {
        var ui = StoreUIManager.Instance;
        var player = PlayerStore.Instance;
        return "wound=" + woundCount + ", woundFactory=" + woundFactoryCount +
            ", body=" + bodyCount + ", bodyFactory=" + bodyFactoryCount +
            ", shootout=" + shootoutCount + ", shot=" + ui.isShot +
            ", handling=" + player.isHandlingShootout + ", customerMatch=" +
            ReferenceEquals(player.currentClientInstance, shootingCustomer) + ", firstDismiss=" + firstDismissCount +
            ", atShootout=[body=" + bodyAtShootout + ", shot=" + shotAtShootout + ", handling=" +
            handlingAtShootout + ", security=" + securityAtShootout + "]";
    }

    private void Cleanup()
    {
        if (cleaned) return;
        cleaned = true; collecting = forceLine = false;
        try
        {
            if (caseCoroutine != null && StoreUIManager.Instance != null)
                StoreUIManager.Instance.StopCoroutine(caseCoroutine);
            if (fixtureCaptured && StoreUIManager.Instance != null) StoreUIManager.Instance.ClearArrestState();
            if (fixtureCaptured && DialogUIManager.Instance != null) DialogUIManager.Instance.InterruptDialog();
            if (fixtureCaptured && PlayerStore.HasInstance)
            {
                PlayerStore.Instance.currentClientInstance = previousCustomer;
                PlayerStore.Instance.isClientArrived = previousArrived;
                PlayerStore.Instance.callNextWhenPossible = previousCallNext;
            }
            if (fixtureCaptured && EmporiumOption.Instance != null) EmporiumOption.Instance.autoCallNext = previousAutoCall;
        }
        catch (Exception error) { Debug.LogWarning("[PLAYER-QA] Story cleanup failed: " + error); }
        active = null;
        if (observer != null) observer.UnpatchAll(observerId);
        playback?.Dispose(); playback = null;
    }

    private static void Need(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
