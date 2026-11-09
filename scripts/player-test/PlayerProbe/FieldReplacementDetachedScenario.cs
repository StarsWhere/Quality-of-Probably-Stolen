using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TMPro;
using UnityEngine;

internal static class QaFieldReplacementDetached
{
    private const string Owner = "qa.field-replacement-detached";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo NativeAcceptOnce = AccessTools.Method(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce));
    private static readonly MethodInfo ArmMethod = AccessTools.Method(typeof(QaFieldReplacementDetached), nameof(Arm));
    private static Harmony harmony;
    private static MethodInfo accept;
    private static GameItem expected;
    private static GameInventory source, target;
    private static bool active, armed, mutate;
    internal static int ApplyCalls, MatchedPlans, ArmCalls, Attempts, AcceptCalls, Refusals, TranspilerMatches;
    internal static bool DetachedAtRefusal, PendingAtRefusal, SafeBeforeRefusal;
    internal static readonly List<string> Plans = new List<string>();

    internal static void Need(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    internal static bool Same(GridShape first, GridShape second) => first != null && second != null &&
        first.minX == second.minX && first.minY == second.minY && first.width == second.width && first.height == second.height &&
        first.orientation == second.orientation && first.flipped == second.flipped;

    internal static bool NativeSafety() => (bool)QaFieldReplacementRecovery.ModType("InventoryAutoInsert")
        .GetMethod("KnownMutationPatches", Static).Invoke(null, null);

    internal static void CheckLeaveGuards()
    {
        foreach (var guard in new[] { (method: "ReturnNow", type: "ReturnGuard"), (method: "Travel", type: "TravelGuard") })
        {
            var method = AccessTools.Method(typeof(MapUIManager), guard.method);
            var patches = method == null ? null : Harmony.GetPatchInfo(method);
            Need(patches != null && patches.Prefixes.Any(patch => patch.PatchMethod?.DeclaringType?.FullName ==
                "ShopAssist.FieldPacking+" + guard.type), "Missing installed field departure guard " + guard.method);
        }
    }

    internal static void Start(GameItem item, GameInventory from, GameInventory to, bool changeState)
    {
        expected = item; source = from; target = to; mutate = changeState;
        active = armed = DetachedAtRefusal = PendingAtRefusal = SafeBeforeRefusal = false;
        ApplyCalls = MatchedPlans = ArmCalls = Attempts = AcceptCalls = Refusals = TranspilerMatches = 0;
        Plans.Clear();
        accept = AccessTools.Method(to.GetType(), nameof(GameInventory.UncheckedAccept), new[] { typeof(GameItem) });
        MethodInfo apply = QaFieldReplacementRecovery.ModType("WholeStackTransfer").GetMethod("ApplyCore", Static);
        Need(accept != null && apply != null && NativeAcceptOnce != null && ArmMethod != null && NativeSafety(),
            "Native replacement fault injection is unavailable");
        harmony = new Harmony(Owner);
        harmony.Patch(apply, prefix: Hook(nameof(BeforeApply)), postfix: Hook(nameof(AfterApply)),
            transpiler: Hook(nameof(InjectBeforeAccept)), finalizer: Hook(nameof(AfterException)));
    }

    private static HarmonyMethod Hook(string method) => new HarmonyMethod(AccessTools.Method(typeof(QaFieldReplacementDetached), method));

    private static void BeforeApply(object __0)
    {
        object item = __0?.GetType().GetField("Item", Fields)?.GetValue(__0);
        object from = __0?.GetType().GetField("From", Fields)?.GetValue(__0);
        object to = __0?.GetType().GetField("To", Fields)?.GetValue(__0);
        ApplyCalls++;
        active = Refusals == 0 && ReferenceEquals(item, expected) && ReferenceEquals(from, source) && ReferenceEquals(to, target);
        if (active) MatchedPlans++;
        if (Plans.Count < 12)
        {
            var gameItem = item as GameItem;
            Plans.Add("item=" + (gameItem?.GetDisplayName(true) ?? "null") + "/" + (gameItem?.uniqueId ?? 0) +
                " expected=" + ReferenceEquals(item, expected) + " from=" + ReferenceEquals(from, source) +
                " to=" + ReferenceEquals(to, target) + " active=" + active);
        }
    }

    private static IEnumerable<CodeInstruction> InjectBeforeAccept(IEnumerable<CodeInstruction> instructions)
    {
        int matches = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(NativeAcceptOnce))
            {
                matches++;
                yield return new CodeInstruction(OpCodes.Call, ArmMethod);
            }
            yield return instruction;
        }
        TranspilerMatches = matches;
        if (matches != 1) throw new InvalidOperationException("Expected one native acceptance call, found " + matches);
    }

    private static void Arm()
    {
        ArmCalls++;
        if (!active || armed) return;
        Attempts++;
        SafeBeforeRefusal = NativeSafety();
        var prefix = Hook(nameof(Refuse)); prefix.priority = Priority.First;
        harmony.Patch(accept, prefix: prefix);
        armed = true;
    }

    private static bool Refuse(GameInventory __instance, GameItem __0, ref bool __result)
    {
        AcceptCalls++;
        if (!active || !ReferenceEquals(__instance, target) || !ReferenceEquals(__0, expected)) return true;
        DetachedAtRefusal = expected.parentInventory == null && !source.childItems.Contains(expected) && !target.childItems.Contains(expected);
        string raw = QaFieldReplacementRecovery.Raw();
        PendingAtRefusal = raw != null && raw.Contains("\"state\":\"pending\"");
        Refusals++;
        if (mutate) expected.unitValue += 1;
        __result = false;
        return false;
    }

    private static void AfterApply() => Disarm();
    private static Exception AfterException(Exception __exception) { Disarm(); return __exception; }
    private static void Disarm()
    {
        if (armed) harmony.Unpatch(accept, HarmonyPatchType.Prefix, Owner);
        active = armed = false;
    }

    internal static void Stop()
    {
        try { harmony?.UnpatchAll(Owner); }
        finally { harmony = null; accept = null; expected = null; source = target = null; active = armed = false; }
    }
}

public abstract class FieldReplacementDetachedFixture
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 180f;
    private GameItem bag, resident, blocker, incoming;
    private GameGridInventory contents, ground;
    private GameSlotInventory left;
    private GridShape residentShape, incomingShape;
    private int step, residentId, incomingId, residentCount, incomingCount;
    private int screenshotFrame;
    private long residentValue, incomingValue;
    protected abstract bool IncomingFault { get; }
    protected abstract bool ChangeState { get; }

    private static void Need(bool value, string message) => QaFieldReplacementDetached.Need(value, message);
    private static bool Same(GridShape first, GridShape second) => QaFieldReplacementDetached.Same(first, second);

    private string Diagnostics()
    {
        string Inspect(Type type, string name, params object[] args)
        {
            try { return type.GetMethod(name, QaFieldReplacementRecovery.Static)?.Invoke(null, args)?.ToString() ?? "unavailable"; }
            catch (Exception error) { return "error:" + error.GetBaseException().Message; }
        }
        string Value(GameItem item)
        {
            try { return item.GetCurrentValue(false, true, true, false, 0) + "/base:" + item.GetBaseValue(); }
            catch (Exception error) { return "error:" + error.GetBaseException().Message; }
        }
        Type packing = QaFieldReplacementRecovery.ModType("FieldPacking");
        Type transfer = QaFieldReplacementRecovery.ModType("WholeStackTransfer");
        var notice = QaFieldReplacementRecovery.ModType("ShopNotice");
        object last = notice.GetField("lastResult", QaFieldReplacementRecovery.Static)?.GetValue(null);
        var rows = last == null ? null : QaTripCargo.Field(last, "Items") as IEnumerable;
        string results = rows == null ? "none" : string.Join("; ", rows.Cast<object>().Select(row =>
            "name=" + QaTripCargo.Field(row, "Name") + " id=" + QaTripCargo.Field(row, "ItemId") +
            " status=" + QaTripCargo.Field(row, "Status") + " reason=" + QaTripCargo.Field(row, "Reason")));
        string Inventory(GameItem item) => ReferenceEquals(item.parentInventory, ground) ? "ground" :
            ReferenceEquals(item.parentInventory, left) ? "left" :
            ReferenceEquals(item.parentInventory, EmporiumEntry.Instance.afterhourPocketSlotInvRight) ? "right" :
            ReferenceEquals(item.parentInventory, contents) ? "bag" : item.parentInventory == null ? "null" : item.parentInventory.GetType().Name;
        var replaceSetting = ModHelper.GetConfig(QaFieldReplacementRecovery.Mod).Get("f36.replace") as ConfigEntry<bool>;
        var valueSetting = ModHelper.GetConfig(QaFieldReplacementRecovery.Mod).Get("f36.valuation") as ConfigEntry<int>;
        var reserveSetting = ModHelper.GetConfig(QaFieldReplacementRecovery.Mod).Get("f36.space") as ConfigEntry<int>;
        return "incomingFault=" + IncomingFault + " changedState=" + ChangeState + " replace=" + replaceSetting?.Value +
            " valuation=" + valueSetting?.Value + " reserve=" + reserveSetting?.Value +
            " bagShape=" + contents.inventoryShape.width + "x" + contents.inventoryShape.height +
            " values=" + Value(incoming) + "/" + Value(resident) +
            " nativeValuesSafe=" + Inspect(packing, "NativeValuesSafe") +
            " incomingSafe=" + Inspect(packing, "SafeItem", incoming) + " residentSafe=" + Inspect(packing, "SafeItem", resident) +
            " groundSafe=" + Inspect(transfer, "SafeTarget", ground) + " leftSafe=" + Inspect(transfer, "SafeTarget", left) +
            " leftRemoveLocked=" + left.IsRemoveLocked() + " leftInsertLocked=" + left.IsInsertLocked() +
            " applyCalls=" + QaFieldReplacementDetached.ApplyCalls + " matchedPlans=" + QaFieldReplacementDetached.MatchedPlans +
            " transpilerMatches=" + QaFieldReplacementDetached.TranspilerMatches + " armCalls=" + QaFieldReplacementDetached.ArmCalls +
            " attempts=" + QaFieldReplacementDetached.Attempts + " acceptCalls=" + QaFieldReplacementDetached.AcceptCalls +
            " refusals=" + QaFieldReplacementDetached.Refusals + " safetyBefore=" + QaFieldReplacementDetached.SafeBeforeRefusal +
            " detached=" + QaFieldReplacementDetached.DetachedAtRefusal + " pendingAtRefusal=" + QaFieldReplacementDetached.PendingAtRefusal +
            " safetyAfter=" + QaFieldReplacementDetached.NativeSafety() +
            " resident=" + residentId + ":" + Inventory(resident) + " incoming=" + incomingId + ":" + Inventory(incoming) +
            " blocker=" + Inventory(blocker) + " bagChildren=" + contents.childItems.Count +
            " ready=" + QaFieldReplacementRecovery.Ready() + " journal=" + (QaFieldReplacementRecovery.Raw() ?? "none") +
            " plans=[" + string.Join("; ", QaFieldReplacementDetached.Plans) + "] result=[" + results + "]";
    }

    private void RunFault()
    {
        GameItem item = IncomingFault ? incoming : resident;
        GameInventory from = IncomingFault ? (GameInventory)ground : left;
        GameInventory to = IncomingFault ? (GameInventory)left : ground;
        QaFieldReplacementDetached.Start(item, from, to, ChangeState);
        try { RoutingScenario.RunFeatureAction("f36.open"); }
        finally { QaFieldReplacementDetached.Stop(); }
        bool valid = QaFieldReplacementDetached.Attempts == 1 && QaFieldReplacementDetached.Refusals == 1 &&
            QaFieldReplacementDetached.SafeBeforeRefusal && QaFieldReplacementDetached.DetachedAtRefusal &&
            QaFieldReplacementDetached.PendingAtRefusal && QaFieldReplacementDetached.NativeSafety();
        if (!valid)
        {
            string detail = Diagnostics();
            Debug.Log("[PLAYER-QA] detached replacement diagnostics: " + detail);
            Need(false, "Native acceptance fault was not observed: " + detail);
        }
    }

    private void CheckJournal(string state)
    {
        var journal = QaWholeStackRestart.ReadJournal();
        Need(journal.action == "f36.open" && journal.state == state && journal.source == residentId && journal.target == incomingId &&
            journal.amount == residentCount && journal.targetBefore == incomingCount &&
            QaFieldReplacementRecovery.Raw().Contains("\"version\":3"),
            "The replacement record lost an item identity, quantity, or expected state");
    }

    private void CheckResident()
    {
        Need(ReferenceEquals(resident.parentInventory, left) && left.childItems.Count(item => ReferenceEquals(item, resident)) == 1 &&
            !ground.childItems.Contains(resident) && resident.uniqueId == residentId && resident.unitCount == residentCount &&
            resident.unitValue == residentValue && Same(resident.modifiedShape, residentShape),
            "The original resident was not restored intact to its original hand");
    }

    private void CheckIncoming()
    {
        Need(ReferenceEquals(incoming.parentInventory, ground) && ground.childItems.Count(item => ReferenceEquals(item, incoming)) == 1 &&
            !left.childItems.Contains(incoming) && incoming.uniqueId == incomingId && incoming.unitCount == incomingCount &&
            incoming.unitValue == incomingValue && Same(incoming.modifiedShape, incomingShape),
            "The incoming item was not restored intact to its original ground location");
    }

    private static void CheckWarning()
    {
        var prompt = GenericConfirmationUIManager.Instance;
        Need(prompt?.panel.activeInHierarchy == true, "Detached goods did not require confirmation before leaving");
        var labels = prompt.panel.GetComponentsInChildren<TMP_Text>(true).Select(value => value.text).ToArray();
        Need(labels.Contains(ModHelper.GetLocalized(QaFieldReplacementRecovery.Mod, "f36.orphan_warning")) &&
            labels.Contains(ModHelper.GetLocalized(QaFieldReplacementRecovery.Mod, "f36.orphan_leave")) &&
            labels.Contains(ModHelper.GetLocalized(QaFieldReplacementRecovery.Mod, "f36.orphan_cancel")),
            "The rendered warning omitted the risk, confirmation, or cancellation action");
    }

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Detached replacement scenario timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        var player = PlayerStore.Instance;
        if (step == 1)
        {
            if (entry?.storeInteractables?.activeInHierarchy != true) return false;
            TradeScenario.SetOptions(true, "f36.enabled", "f36.replace");
            QaFieldReplacementDetached.CheckLeaveGuards();
            var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
            var settings = CustomUIManager.Instance.GetWindow("game_options");
            settings.Get(QaFieldReplacementRecovery.Mod + ":f36.space").SetFloat(32, true);
            settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
            bag = QaTripCargo.New("backpack_medium", entry.invElement);
            contents = (GameGridInventory)bag.contentWindow.childElement;
            resident = PackingRecoveryFixture.Put("wine_yeast_red", "QA detached resident", contents);
            blocker = PackingRecoveryFixture.Put("wine_berry", "QA detached blocker", contents);
            resident.unitValue = 1; blocker.unitValue = 20000;
            entry.selectedBackpack = bag; player.visitLeftTonight = 2;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND)); step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !entry.afterhourWindow.IsVisible() ||
                entry.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            ground = entry.afterhourInventory; left = entry.afterhourPocketSlotInvLeft;
            foreach (GameItem existing in ground.childItems.ToArray()) existing.Destroy();
            QaFieldReplacementRecovery.Move(resident, left);
            QaFieldReplacementRecovery.Move(blocker, entry.afterhourPocketSlotInvRight);
            contents.SetShape(2, 2);
            var reserve = ModHelper.GetConfig(QaFieldReplacementRecovery.Mod).Get("f36.space") as ConfigEntry<int>;
            Need(contents.childItems.Count == 0 && contents.inventoryShape.width == 2 && contents.inventoryShape.height == 2 &&
                reserve?.Value == 32 && left.childItem == resident && entry.afterhourPocketSlotInvRight.childItem == blocker,
                "The fixture must occupy both hands and reserve every backpack cell");
            incoming = PackingRecoveryFixture.Put("processed_meat", "QA detached incoming", ground); incoming.unitValue = 10000;
            Need(incoming.identifier != resident.identifier && incoming.identifier != blocker.identifier,
                "The incoming item must not stack with either hand item");
            residentId = resident.GetUniqueID(); incomingId = incoming.GetUniqueID();
            residentCount = resident.unitCount; incomingCount = incoming.unitCount;
            residentShape = resident.modifiedShape.Clone(); incomingShape = incoming.modifiedShape.Clone();
            residentValue = resident.unitValue; incomingValue = incoming.unitValue; step++; return false;
        }
        if (step == 3)
        {
            if (!QaFieldReplacementRecovery.GroundVisible(incoming)) return false;
            RunFault(); step = 30; return false;
        }
        if (step == 30)
        {
            var action = QaItemUi.WindowAction(entry.afterhourWindow, "result");
            if (action == null || action.isDisabled) return false;
            action.Select();
            var details = CustomUIManager.Instance.GetWindow(QaFieldReplacementRecovery.Mod + ".operation");
            Need(details?.IsAlive == true, "The field replacement result did not open");
            string detailsText = string.Join(" | ", details.root.GetComponentsInChildren<TMP_Text>(true).Select(value => value.text));
            Need(detailsText.Contains("QA detached resident") && detailsText.Contains("QA detached incoming"),
                "The rendered result omitted an item involved in the failed replacement");
            screenshotFrame = Time.frameCount + 3; step = 31; return false;
        }
        if (step == 31)
        {
            if (Time.frameCount < screenshotFrame) return false;
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-field-detached-" +
                    (IncomingFault ? "incoming-" : "resident-") + (ChangeState ? "uncertain" : "restored") + ".png"));
            screenshotFrame = Time.frameCount + 2; step = 32; return false;
        }
        if (step == 32)
        {
            if (Time.frameCount < screenshotFrame) return false;
            CustomUIManager.Instance.GetWindow(QaFieldReplacementRecovery.Mod + ".operation")?.Close();
            if (!ChangeState)
            {
                CheckResident(); CheckIncoming(); CheckJournal("done");
                Need(QaFieldReplacementRecovery.Ready() && QaFieldReplacementRecovery.CanLeave(),
                    "An exactly recovered replacement remained blocked");
                result = IncomingFault ? "PASS native incoming acceptance failure restores both original items" :
                    "PASS native resident acceptance failure restores both original items";
                return true;
            }
            CheckJournal("pending");
            Need(!QaFieldReplacementRecovery.Ready(), "Changed detached state was treated as an exact recovery");
            if (IncomingFault)
            {
                CheckResident();
                Need(incoming.parentInventory == null && incoming.unitCount == incomingCount && incoming.unitValue == incomingValue + 1 &&
                    !QaFieldReplacementRecovery.CanLeave(), "Uncertain incoming item or return guard is incorrect");
            }
            else
            {
                CheckIncoming();
                Need(resident.parentInventory == null && resident.unitCount == residentCount && resident.unitValue == residentValue + 1 &&
                    !QaFieldReplacementRecovery.CanLeave(), "Uncertain detached resident was not protected from return");
            }
            MapUIManager.Instance.ReturnNow();
            CheckWarning();
            screenshotFrame = Time.frameCount + 3; step = 40; return false;
        }
        if (step == 40)
        {
            if (Time.frameCount < screenshotFrame) return false;
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-field-detached-" +
                    (IncomingFault ? "incoming" : "resident") + "-warning.png"));
            screenshotFrame = Time.frameCount + 2; step = 41; return false;
        }
        if (step == 41)
        {
            if (Time.frameCount < screenshotFrame) return false;
            GenericConfirmationUIManager.Instance.CancelButtonPressed();
            Need(!GenericConfirmationUIManager.Instance.panel.activeInHierarchy &&
                MapUIManager.Instance.Current?.Id == AfterhourLocation.DUMPING_GROUND && !QaFieldReplacementRecovery.Ready(),
                "Canceling the return warning left the location or released the pending record");
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND));
            CheckWarning(); GenericConfirmationUIManager.Instance.CancelButtonPressed();
            Need(!GenericConfirmationUIManager.Instance.panel.activeInHierarchy &&
                MapUIManager.Instance.Current?.Id == AfterhourLocation.DUMPING_GROUND && !QaFieldReplacementRecovery.Ready(),
                "Canceling the travel warning left the location or released the pending record");
            if (!IncomingFault)
            {
                resident.unitValue = residentValue;
                QaFieldReplacementRecovery.Move(resident, left);
                CheckResident(); CheckIncoming();
                Need(QaFieldReplacementRecovery.CanLeave() && QaFieldReplacementRecovery.Ready(),
                    "Preserving both items did not complete the verified replacement recovery");
                CheckJournal("done");
                MapUIManager.Instance.ReturnNow(); step = 6; return false;
            }
            entry.afterhourWindow.Hide();
            Need(!entry.afterhourWindow.IsVisible(), "The ground window did not close before the final return attempt");
            MapUIManager.Instance.ReturnNow(); CheckWarning();
            GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
            Need(!GenericConfirmationUIManager.Instance.panel.activeInHierarchy && !QaFieldReplacementRecovery.Ready(),
                "Confirming return with the ground window closed did not retain the pending replacement");
            step = 5; return false;
        }
        if (step == 7)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !entry.afterhourWindow.IsVisible() ||
                StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            Need(!QaFieldReplacementRecovery.Ready(), "The pending record disappeared during the next trip");
            MapUIManager.Instance.ReturnNow();
            Need(GenericConfirmationUIManager.Instance?.panel.activeInHierarchy != true,
                "A stale detached item blocked return from a later trip");
            step = 8; return false;
        }
        if (MapUIManager.Instance.IsOutside || player.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
            entry.storeInteractables?.activeInHierarchy != true || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
        Need(player.FindAllItem().Count(item => item.uniqueId == residentId) == 1,
            "Return lost the resident that was safely carried");
        if (step == 6)
        {
            Need(QaFieldReplacementRecovery.Ready(), "Verified manual recovery became pending after return");
            result = "PASS changed detached resident warns on return and travel, and can be preserved";
            return true;
        }
        Need(!QaFieldReplacementRecovery.Ready(), "Confirmed return lost the uncertain incoming record");
        if (step == 5)
        {
            var site = AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND);
            player.visitLeftTonight = 2; entry.selectedBackpack = bag;
            Need(site != null && site.IsAvailable(), "The next trip is unavailable in the QA fixture");
            MapUIManager.Instance.Travel(site);
            Need(GenericConfirmationUIManager.Instance?.panel.activeInHierarchy != true,
                "A stale detached item blocked a later trip from the store");
            step = 7; return false;
        }
        if (!QaItemUi.OpenAttention()) return false;
        var window = CustomUIManager.Instance.GetWindow(QaFieldReplacementRecovery.Mod + ".attention");
        string text = string.Join(" | ", window.root.GetComponentsInChildren<TMP_Text>(true).Select(value => value.text));
        Need(text.Contains("QA detached resident") && text.Contains("QA detached incoming"),
            "The pending record does not expose both item identities for inspection");
        var resolve = RoutingScenario.Button(window, "attention.resolve");
        Need(resolve != null && resolve.interactable, "The uncertain replacement has no inspection action");
        resolve.onClick.Invoke();
        Need(QaFieldReplacementRecovery.Ready(), "Inspecting the uncertain replacement did not release the journal");
        window.Close();
        result = "PASS changed detached incoming item requires confirmation, preserves inspection and permits later travel";
        return true;
    }
}

public sealed class FieldReplacementOldDetachedScenario : FieldReplacementDetachedFixture
{ protected override bool IncomingFault => false; protected override bool ChangeState => false; }

public sealed class FieldReplacementNewDetachedScenario : FieldReplacementDetachedFixture
{ protected override bool IncomingFault => true; protected override bool ChangeState => false; }

public sealed class FieldReplacementOldUncertainScenario : FieldReplacementDetachedFixture
{ protected override bool IncomingFault => false; protected override bool ChangeState => true; }

public sealed class FieldReplacementNewUncertainScenario : FieldReplacementDetachedFixture
{ protected override bool IncomingFault => true; protected override bool ChangeState => true; }
