using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Runs native dialog and telephone flows inside the guarded, newly created QA save.
public sealed class FastPlaybackDialogueScenario
{
    private static FastPlaybackDialogueScenario active;
    private readonly QaNewRun start = new QaNewRun();
    private QaFastPlayback playback;
    private Harmony spy;
    private string spyOwner;
    private StoreClientInstance oldClient;
    private bool oldAuto, oldTypewriter;
    private int step, flowStore, flowPhone, firstEnd, lastEnd, choiceCount, manualEnd;
    private int starts, handles, phoneDialogs, phoneFinishes, phoneEnds, phoneChoices, phoneEnd, shopAfterPhone;
    private int shopAdvances, phoneAdvances, advanceBefore;
    private int startBefore, handleBefore, dialogBefore, endBefore, dismissals, day;
    private bool watchDismiss;
    private float since = Time.realtimeSinceStartup, until, timeScale;
    private Dialogue first, question, manual, manualNext, phoneQuestion, naturalPhone;
    private StoreClientInstance leavingClient, replacementClient, observedDismissal;

    public bool Tick(out string result)
    {
        try
        {
            bool done = Advance(out result);
            if (done) Cleanup();
            return done;
        }
        catch { Cleanup(); throw; }
    }

    private bool Advance(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - since < 15f, "F42 dialogue QA timed out at step " + step);
        if (step == 0)
        {
            if (!start.Tick()) { since = Time.realtimeSinceStartup; return false; }
            Next(1); return false;
        }
        var shop = DialogUIManager.Instance;
        var phone = PhoneUIManager.Instance;
        var phoneDialog = DialoguePhoneUIManager.Instance;
        if (shop == null || phone == null || phoneDialog == null || EmporiumOption.Instance == null) return false;

        if (step == 1)
        {
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            oldClient = PlayerStore.Instance.currentClientInstance;
            PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
            oldAuto = EmporiumOption.Instance.autoNextDialog;
            oldTypewriter = EmporiumOption.Instance.enableTypewriterEffect;
            EmporiumOption.Instance.autoNextDialog = false;
            EmporiumOption.Instance.enableTypewriterEffect = true;
            shop.InterruptDialog(); phone.StopCall();
            day = StoreStation.GetDayCounter(); timeScale = Time.timeScale;
            playback = new QaFastPlayback();
            spyOwner = "qa.fastplay.dialogue." + Guid.NewGuid().ToString("N");
            spy = new Harmony(spyOwner); active = this;
            Spy(typeof(PhoneUIManager), "StartCalling", nameof(CountStart));
            Spy(typeof(PhoneUIManager), "HandleCall", nameof(CountHandle));
            Spy(typeof(PhoneUIManager), "StartPhoneDialog", nameof(CountPhoneDialog));
            Spy(typeof(DialoguePhoneUIManager), "FinishConversationNow", nameof(CountPhoneFinish));
            Spy(typeof(DialoguePhoneUIManager), "EndConversation", nameof(CountPhoneEnd));
            Spy(typeof(DialogUIManager), "OnAdvancePressed", nameof(CountShopAdvance));
            Spy(typeof(DialoguePhoneUIManager), "OnAdvancePressed", nameof(CountPhoneAdvance));
            Next(2); return false;
        }
        if (step == 2)
        {
            playback.Press();
            Next(3); return false;
        }
        if (step == 3)
        {
            playback.Hold();
            Need(playback.Rate == 16f, "F42 hold mode did not activate");
            Schedule(shop, 2f, () => flowStore++);
            Schedule(phoneDialog, 2f, () => flowPhone++);
            until = Time.realtimeSinceStartup + 0.75f;
            Next(4); return false;
        }
        if (step == 4)
        {
            if (flowStore == 0 || flowPhone == 0)
            {
                Need(Time.realtimeSinceStartup < until, "Native dialog flow delays were not accelerated");
                return false;
            }
            Need(flowStore == 1 && flowPhone == 1, "Native flow callback repeated");
            Schedule(shop, 4f, () => flowStore++);
            Schedule(phoneDialog, 4f, () => flowPhone++);
            playback.Release();
            until = Time.realtimeSinceStartup + 0.4f;
            Next(31); return false;
        }
        if (step == 31)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(playback.Rate == 1f && flowStore == 1 && flowPhone == 1,
                "Releasing the key did not slow an in-flight dialog delay");
            playback.Press();
            until = Time.realtimeSinceStartup + 0.8f;
            Next(32); return false;
        }
        if (step == 32)
        {
            playback.Hold();
            if (flowStore == 1 || flowPhone == 1)
            {
                Need(Time.realtimeSinceStartup < until, "Reactivating F42 did not resume the pending flow delays");
                return false;
            }
            Need(flowStore == 2 && flowPhone == 2, "Resumed dialog flow callback repeated");
            Schedule(shop, 2f, () => flowStore++);
            Schedule(phoneDialog, 2f, () => flowPhone++);
            shop.InterruptDialog(); phoneDialog.InterruptDialog();
            until = Time.realtimeSinceStartup + 0.45f;
            Next(5); return false;
        }
        if (step == 5)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(flowStore == 2 && flowPhone == 2, "Interrupted dialog ran an obsolete scheduled callback");
            var last = Line("A final QA line.").SetEndAction(() => lastEnd++);
            var selected = Line("Continue").SetNextDialogue(last);
            question = Line("Choose how to continue.");
            question.choices.Add((null, selected, () => choiceCount++));
            first = Line("The first QA line is deliberately long enough to exercise the native typewriter.")
                .SetEndAction(() => firstEnd++).SetNextDialogue(question);
            playback.Release();
            Need(shop.StartDialogue(first, true), "Could not start the native shop dialog");
            until = Time.realtimeSinceStartup + 0.35f;
            Next(6); return false;
        }
        if (step == 6)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(ReferenceEquals(shop.currentDialog, first), "Dialog advanced without the F42 key");
            playback.Press(); Next(7); return false;
        }
        if (step == 7)
        {
            playback.Hold();
            AllowAdvanceNow(); advanceBefore = shopAdvances;
            shop.OnAdvancePressed();
            Need(shopAdvances == advanceBefore + 1, "Native shop advance was not invoked");
            Next(27); return false;
        }
        if (step == 27)
        {
            Need(shopAdvances == advanceBefore + 1, "Manual and F42 shop advance ran in the same frame");
            Next(28); return false;
        }
        if (step == 28)
        {
            if (!ChoiceVisible(shop.dialogChoiceBubbles)) return false;
            Need(ReferenceEquals(shop.currentDialog, question), "Shop choice appeared for the wrong line");
            until = Time.realtimeSinceStartup + 0.35f;
            Next(8); return false;
        }
        if (step == 8)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(playback.Rate == 1f && choiceCount == 0 && ReferenceEquals(shop.currentDialog, question) &&
                ChoiceVisible(shop.dialogChoiceBubbles), "F42 did not pause at the native shop choice");
            shop.SelectChoice(0);
            Next(9); return false;
        }
        if (step == 9)
        {
            if (shop.currentDialog != null || lastEnd == 0) return false;
            until = Time.realtimeSinceStartup + 1.3f;
            Next(10); return false;
        }
        if (step == 10)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(firstEnd == 1 && lastEnd == 1 && choiceCount == 1, "Shop dialog callbacks were lost or repeated");
            playback.Release();
            manualNext = Line("Manual second line.");
            manual = Line("Manual first line.").SetEndAction(() => manualEnd++).SetNextDialogue(manualNext);
            Need(shop.StartDialogue(manual, true), "Could not start manual control dialog");
            until = Time.realtimeSinceStartup + 0.35f;
            Next(11); return false;
        }
        if (step == 11)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(ReferenceEquals(shop.currentDialog, manual) && playback.Rate == 1f,
                "Released hold continued advancing dialog");
            shop.OnAdvancePressed();
            until = Time.realtimeSinceStartup + 0.12f;
            Next(12); return false;
        }
        if (step == 12)
        {
            if (!ReferenceEquals(shop.currentDialog, manualNext))
            {
                if (Time.realtimeSinceStartup >= until) { shop.OnAdvancePressed(); until = Time.realtimeSinceStartup + 0.12f; }
                return false;
            }
            until = Time.realtimeSinceStartup + 1.3f;
            Next(13); return false;
        }
        if (step == 13)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(ReferenceEquals(shop.currentDialog, manualNext) && manualEnd == 1,
                "Manual advancement or its end action changed when F42 was released");
            shop.InterruptDialog();
            phone.OpenUI();
            playback.Press(); Next(14); return false;
        }
        if (step == 14)
        {
            playback.Hold();
            Need(playback.Rate == 16f, "F42 did not resume for phone testing");
            phone.StopCall(); startBefore = starts; handleBefore = handles;
            phone.PressNumber(1);
            until = Time.realtimeSinceStartup + 0.3f;
            Next(15); return false;
        }
        if (step == 15)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(starts == startBefore && PhoneFloat(phone, "clickTimer") > 1.5f,
                "F42 shortened the manual digit-entry window");
            phone.StopCall();
            phone.AutoCall(999999999L);
            until = Time.realtimeSinceStartup + 1.2f;
            Next(16); return false;
        }
        if (step == 16)
        {
            if (handles == handleBefore)
            {
                Need(Time.realtimeSinceStartup < until, "Quick-dial fallback or ringing did not accelerate");
                return false;
            }
            Need(starts == startBefore + 1 && handles == handleBefore + 1 && !phone.phoneCalling.isPlaying,
                "Quick dial did not call exactly once or stop ringing");
            startBefore = starts; handleBefore = handles;
            phone.AutoCall(999999999L);
            phone.PressNumber(4);
            until = Time.realtimeSinceStartup + 0.3f;
            Next(17); return false;
        }
        if (step == 17)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(starts == startBefore && PhoneFloat(phone, "clickTimer") > 1.5f,
                "Manual input did not cancel the quick-dial timing origin");
            phone.StopCall();
            until = Time.realtimeSinceStartup + 0.4f;
            Next(18); return false;
        }
        if (step == 18)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(starts == startBefore && handles == handleBefore, "Cancelled quick dial still called");
            phone.StartCalling(); phone.StopCall();
            dialogBefore = phoneDialogs;
            phone.StartPhoneDialogSequence(); phone.StopCall();
            until = Time.realtimeSinceStartup + 0.4f;
            Next(19); return false;
        }
        if (step == 19)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(handles == handleBefore && phoneDialogs == dialogBefore && !phone.phoneCalling.isPlaying &&
                !phone.phoneClientPickup.isPlaying, "Cancelled ringing or pickup still continued");
            (AccessTools.Field(typeof(PhoneUIManager), "currentNumber") ?? throw new InvalidOperationException("Missing phone number field"))
                .SetValue(phone, 999999999L);
            phone.StartPhoneDialogSequence();
            until = Time.realtimeSinceStartup + 0.7f;
            Next(20); return false;
        }
        if (step == 20)
        {
            if (phoneDialogs == dialogBefore)
            {
                Need(Time.realtimeSinceStartup < until, "Phone pickup delay did not accelerate");
                return false;
            }
            Need(phoneDialogs == dialogBefore + 1, "Phone pickup started dialog more than once");
            phone.StopCall();
            var last = Line("Phone final line.").SetEndAction(() => phoneEnd++);
            var selected = Line("Phone choice").SetNextDialogue(last);
            phoneQuestion = Line("Choose on the phone.");
            phoneQuestion.choices.Add((null, selected, () => phoneChoices++));
            phoneDialog.StartDialogue(phoneQuestion);
            AllowAdvanceNow(); advanceBefore = phoneAdvances;
            phoneDialog.OnAdvancePressed();
            Need(phoneAdvances == advanceBefore + 1, "Native phone advance was not invoked");
            Next(33); return false;
        }
        if (step == 33)
        {
            Need(phoneAdvances == advanceBefore + 1, "Manual and F42 phone advance ran in the same frame");
            Next(21); return false;
        }
        if (step == 21)
        {
            if (!ChoiceVisible(phoneDialog.dialogChoiceBubbles)) return false;
            Need(ReferenceEquals(phoneDialog.currentDialog, phoneQuestion), "Phone choice appeared for the wrong line");
            until = Time.realtimeSinceStartup + 0.35f;
            Next(22); return false;
        }
        if (step == 22)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(playback.Rate == 1f && phoneChoices == 0 && ReferenceEquals(phoneDialog.currentDialog, phoneQuestion) &&
                ChoiceVisible(phoneDialog.dialogChoiceBubbles), "F42 did not pause at the native phone choice");
            phoneDialog.SelectChoice(0);
            Next(23); return false;
        }
        if (step == 23)
        {
            if (phoneEnd == 0) return false;
            until = Time.realtimeSinceStartup + 1.4f;
            Next(24); return false;
        }
        if (step == 24)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(phoneChoices == 1 && phoneEnd == 1 && phoneFinishes <= 1,
                "Phone choice, end action or terminal advance repeated");
            playback.Release();
            phone.StopCall();
            EmporiumOption.Instance.enableTypewriterEffect = false;
            naturalPhone = Line("A phone line ended by its native scheduled callback.");
            endBefore = phoneEnds;
            phoneDialog.StartDialogue(naturalPhone);
            Next(25); return false;
        }
        if (step == 25)
        {
            if (phoneEnds == endBefore) return false;
            Need(ReferenceEquals(phoneDialog.currentDialog, naturalPhone) && phoneDialog.playerBubble != null &&
                phoneDialog.playerBubble.gameObject.activeInHierarchy,
                "Native scheduled phone ending did not leave the expected terminal bubble");
            EmporiumOption.Instance.enableTypewriterEffect = true;
            var next = Line("Shop proceeds after phone").SetStartAction(() => shopAfterPhone++);
            var line = Line("Shop dialogue should proceed despite an ended phone bubble.").SetNextDialogue(next);
            Need(shop.StartDialogue(line, true), "Could not start shop dialog after phone completion");
            playback.Press(); Next(26); return false;
        }
        if (step == 26)
        {
            playback.Hold();
            if (shopAfterPhone == 0) return false;
            Need(shopAfterPhone == 1, "Shop dialog following a phone ending advanced more than once");
            Need(StoreStation.GetDayCounter() == day && Time.timeScale == timeScale,
                "F42 changed the in-game day or global time scale");
            shop.InterruptDialog(); phone.StopCall();
            leavingClient = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
            replacementClient = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
            PlayerStore.Instance.currentClientInstance = leavingClient;
            Spy(typeof(PlayerStore), "DismissCurrentClient", nameof(ObserveDismiss));
            watchDismiss = true;
            shop.CurrentClientLeave(2f);
            phoneDialog.CurrentClientLeave(2f);
            until = Time.realtimeSinceStartup + 0.8f;
            Next(34); return false;
        }
        if (step == 34)
        {
            if (dismissals < 2)
            {
                Need(Time.realtimeSinceStartup < until, "Shop or phone client-leave wait was not accelerated");
                return false;
            }
            Need(dismissals == 2 && ReferenceEquals(observedDismissal, leavingClient),
                "Accelerated leave callbacks did not target the original client once each");
            shop.CurrentClientLeave(0f);
            Need(dismissals == 3 && ReferenceEquals(observedDismissal, leavingClient),
                "The native immediate leave branch changed");
            shop.CurrentClientLeave(2f);
            phoneDialog.CurrentClientLeave(2f);
            PlayerStore.Instance.currentClientInstance = replacementClient;
            until = Time.realtimeSinceStartup + 0.6f;
            Next(35); return false;
        }
        if (step == 35)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(dismissals == 3 && ReferenceEquals(PlayerStore.Instance.currentClientInstance, replacementClient),
                "A previous client's delayed leave dismissed its replacement");
            watchDismiss = false;
            result = "PASS F42 native dialog flow, shop and phone choices, manual advance, once-only end actions, " +
                "quick/manual dial timing, ringing and pickup cancellation, natural phone completion followed by " +
                "shop dialog, accelerated client leave and replacement-client protection, unchanged day and time scale";
            return true;
        }
        throw new InvalidOperationException("Unknown F42 dialogue step " + step);
    }

    private static Dialogue Line(string text) => new Dialogue().SetText("PLAYER", text);
    private static bool ChoiceVisible(System.Collections.Generic.List<DialogChoiceBubble> choices) =>
        choices != null && choices.Any(choice => choice != null && choice.gameObject.activeInHierarchy);
    private static float PhoneFloat(PhoneUIManager phone, string name) =>
        (float)(AccessTools.Field(typeof(PhoneUIManager), name) ?? throw new InvalidOperationException("Missing phone " + name)).GetValue(phone);
    private static void Schedule(object manager, float seconds, Action callback)
    {
        var method = AccessTools.DeclaredMethod(manager.GetType(), "ScheduleFlow", new[] { typeof(float), typeof(Action) });
        if (method == null) throw new InvalidOperationException("Native dialog scheduler unavailable");
        method.Invoke(manager, new object[] { seconds, callback });
    }
    private static void AllowAdvanceNow()
    {
        var mod = ModLoader.Mods.Find(item => item.Manifest != null && item.Manifest.ID == "starswhere.shopassist");
        var type = mod?.Instance?.GetType().Assembly.GetType("ShopAssist.FastPlayback", true);
        var instance = type?.GetField("active", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        var next = type?.GetField("nextAdvance", BindingFlags.Instance | BindingFlags.NonPublic);
        if (instance == null || next == null) throw new InvalidOperationException("F42 dialogue pulse unavailable");
        next.SetValue(instance, 0f);
    }
    private void Spy(Type type, string method, string prefix)
    {
        var original = AccessTools.DeclaredMethod(type, method);
        if (original == null) throw new InvalidOperationException("Missing phone spy target " + method);
        spy.Patch(original, prefix: new HarmonyMethod(typeof(FastPlaybackDialogueScenario), prefix));
    }
    private static void CountStart() { if (active != null) active.starts++; }
    private static void CountHandle() { if (active != null) active.handles++; }
    private static void CountPhoneDialog() { if (active != null) active.phoneDialogs++; }
    private static void CountPhoneFinish() { if (active != null) active.phoneFinishes++; }
    private static void CountPhoneEnd() { if (active != null) active.phoneEnds++; }
    private static void CountShopAdvance() { if (active != null) active.shopAdvances++; }
    private static void CountPhoneAdvance() { if (active != null) active.phoneAdvances++; }
    private static bool ObserveDismiss(PlayerStore __instance)
    {
        if (active == null || !active.watchDismiss) return true;
        active.dismissals++;
        active.observedDismissal = __instance.currentClientInstance;
        return false;
    }
    private void Next(int value) { step = value; since = Time.realtimeSinceStartup; }
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private void Cleanup()
    {
        active = null;
        if (spy != null) { spy.UnpatchAll(spyOwner); spy = null; }
        playback?.Dispose(); playback = null;
        if (PhoneUIManager.Instance != null) PhoneUIManager.Instance.StopCall();
        if (DialogUIManager.Instance != null) DialogUIManager.Instance.InterruptDialog();
        if (PlayerStore.HasInstance) PlayerStore.Instance.currentClientInstance = oldClient;
        if (EmporiumOption.Instance != null)
        {
            EmporiumOption.Instance.autoNextDialog = oldAuto;
            EmporiumOption.Instance.enableTypewriterEffect = oldTypewriter;
        }
    }
}
