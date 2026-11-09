using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

// Native F36 moves remain unpatched; only the journal completion write is faulted.
public abstract class PackingRecoveryFixture
{
    private const string Mod = "starswhere.shopassist", JournalKey = "wholeStackTransfer";
    private const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 175f;
    private QaNewRun secondRun;
    private QaLoadSlot reload;
    private GameItem bag, first, second;
    private GameGridInventory bagGrid, ground;
    private GridShape secondHome;
    private int secondCount, attempts, firstId, secondId, savedSlot, step;
    private string firstName, secondName, savedRun;
    private float readyAt;

    private static GameItem observedFirst;
    private static GameInventory observedGround;
    private static bool failRestore, completedWriteFailed, observedNativeMove;
    private static int doneFailures, restoreFailures;
    protected abstract bool UsePockets { get; }

    private static void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
    private static Type ModType(string name) => ModLoader.Mods.First(mod => mod.Manifest?.ID == Mod).Instance.GetType().Assembly
        .GetType("ShopAssist." + name, true);
    private static CustomUIWindow Window(string id) => CustomUIManager.Instance.GetWindow(Mod + "." + id);
    private static string Text(CustomUIWindow window) => string.Join(" | ", window.root.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text));
    private static bool Ready() => (bool)ModType("WholeStackTransfer").GetProperty("Ready", F).GetValue(null);
    private static bool HasResult() => (bool)ModType("ShopNotice").GetProperty("HasResult", F).GetValue(null);
    private static string Raw() => ModHelper.GetModData(Mod, JournalKey, null);
    private static bool State(string raw, string state) => raw != null && raw.Contains("\"state\":\"" + state + "\"");
    private static bool Same(GridShape a, GridShape b) => a != null && b != null && a.minX == b.minX && a.minY == b.minY &&
        a.width == b.width && a.height == b.height && a.orientation == b.orientation && a.flipped == b.flipped;

    // Also used by AttentionLocationScenario; this places a real native item.
    internal static GameItem Put(string identifier, string name, GameInventory inventory)
    {
        GameItem item = DirectoryMaster.Item(identifier);
        Need(item != null && item.identifier == identifier, "Missing native fixture " + identifier);
        GeneralHelper.SetItemOwned(item, true); GeneralHelper.SetCustomName(item, name);
        SlotMarker marker = inventory.TryFindOneValidInventorySlot(item);
        if ((marker == null || marker.inventory != inventory || marker.targetItem != null || marker.numTransfer < item.unitCount) &&
            inventory is GameGridInventory grid && item.modifiedShape != null)
        {
            marker = null;
            for (int y = 0; y < grid.inventoryShape.height && marker == null; y++)
                for (int x = 0; x < grid.inventoryShape.width; x++)
                {
                    GridShape pose = new GridShapeBuilder(item.modifiedShape).SetPosition(x, y).Build();
                    SlotMarker candidate = grid.TryInventorySlot(item, item.unitCount, pose);
                    if (candidate == null || candidate.inventory != grid || candidate.targetItem != null || candidate.numTransfer < item.unitCount) continue;
                    marker = candidate; break;
                }
        }
        Need(marker != null && marker.targetItem == null && marker.TryAcceptOnce(item.unitCount) == item.unitCount &&
            ReferenceEquals(item.parentInventory, inventory), "Could not place " + name);
        return item;
    }

    private static void FailAfterCompletionWrite(string __0, string __1)
    {
        if (__0 != JournalKey || !State(__1, "done")) return;
        doneFailures++;
        var entry = EmporiumEntry.Instance;
        observedNativeMove = observedFirst != null && !ReferenceEquals(observedFirst.parentInventory, observedGround) &&
            (ReferenceEquals(observedFirst.parentInventory, entry.afterhourPocketSlotInvBackpack.childItem?.contentWindow?.childElement) ||
             ReferenceEquals(observedFirst.parentInventory, entry.afterhourPocketSlotInvLeft) ||
             ReferenceEquals(observedFirst.parentInventory, entry.afterhourPocketSlotInvRight));
        completedWriteFailed = true;
        throw new InvalidOperationException("QA completion readback failure after native transfer");
    }

    private static bool FailPendingRestore(string __0, string __1, string __2)
    {
        if (!failRestore || !completedWriteFailed || __0 != Mod || __1 != JournalKey || !State(__2, "pending")) return true;
        restoreFailures++;
        throw new InvalidOperationException("QA pending journal restoration failure");
    }

    private static bool GroundVisible(GameItem item, GameInventory expected)
    {
        var element = item as GameItemElement;
        PixelWindow parent = element?.handler?.FindParentWindow();
        return item != null && ReferenceEquals(item.parentInventory, expected) && element != null && !element.IsDestroyed() &&
            element.handler != null && element.handler.gameObject.activeInHierarchy && parent != null && parent.IsVisible() &&
            WindowsHandler.current?.visibleWindows?.Contains(parent) == true;
    }

    private static bool NativeGate() => (bool)ModType("InventoryAutoInsert").GetMethod("KnownMutationPatches", F).Invoke(null, null);

    private static bool SafeTransfer(GameInventory inventory) => (bool)ModType("InventoryAutoInsert")
        .GetMethod("SafeTransferInventory", F).Invoke(null, new object[] { inventory, null });

    private static void NoopOwnershipPrefix() { }

    private void CheckNativePocketGate()
    {
        var e = EmporiumEntry.Instance;
        var left = e.afterhourPocketSlotInvLeft; var right = e.afterhourPocketSlotInvRight;
        foreach (GameSlotInventory pocket in new[] { left, right })
        {
            var callbacks = pocket.mayInventoryAddItemFunc?.GetInvocationList();
            Need(callbacks?.Length == 1 && callbacks[0].Method.DeclaringType?.FullName == "ContainerHelper+<>c" &&
                callbacks[0].Method.Name == "<AllowOnlyOwnedItems>b__13_0" &&
                callbacks[0].Method.Module.ModuleVersionId == typeof(GameItem).Module.ModuleVersionId && SafeTransfer(pocket),
                "The actual native owned-item pocket predicate was not accepted: " +
                string.Join(", ", (callbacks ?? Array.Empty<Delegate>()).Select(value => value.Method.DeclaringType?.FullName + "." + value.Method.Name)));
        }
        var original = left.mayInventoryAddItemFunc;
        Func<GameItem, GameInventory, bool> unknown = (item, inventory) => true;
        try
        {
            left.mayInventoryAddItemFunc = original + unknown;
            Need(!SafeTransfer(left), "The pocket gate accepted an additional unknown callback");
        }
        finally { left.mayInventoryAddItemFunc = original; }

        var unrelated = e.afterhourPocketSlotInvBackpack;
        var previous = unrelated.mayInventoryAddItemFunc;
        try
        {
            unrelated.mayInventoryAddItemFunc = original;
            Need(!SafeTransfer(unrelated), "The owned-item predicate was allowed on an unrelated inventory");
        }
        finally { unrelated.mayInventoryAddItemFunc = previous; }

        MethodBase[] dependencies = {
            AccessTools.Method(typeof(GeneralHelper), nameof(GeneralHelper.IsItemOwned), new[] { typeof(GameItem) }),
            AccessTools.Method(typeof(GameItem), nameof(GameItem.IsTag), new[] { typeof(string) }),
            AccessTools.Method(typeof(TagSystem), nameof(TagSystem.IsTag), new[] { typeof(string) }),
            AccessTools.PropertyGetter(typeof(GameItem), nameof(GameItem.modifiedState)),
            AccessTools.PropertyGetter(typeof(TagSystem), nameof(TagSystem.dict)),
            AccessTools.Method(typeof(TagState), nameof(TagState.IsEnabled))
        };
        var prefix = new HarmonyMethod(typeof(PackingRecoveryFixture).GetMethod(nameof(NoopOwnershipPrefix), F));
        foreach (MethodBase dependency in dependencies)
        {
            Need(dependency != null, "An owned-item predicate dependency is unavailable");
            const string owner = "qa.f36-pocket-dependency";
            var harmony = new Harmony(owner);
            try
            {
                harmony.Patch(dependency, prefix: prefix);
                Need(!NativeGate() && !(bool)ModType("WholeStackTransfer").GetMethod("SafeSource", F).Invoke(null, new object[] { first }),
                    "The transfer gate accepted an unknown patch on " + dependency.Name);
            }
            finally { harmony.UnpatchAll(owner); }
            Need(NativeGate(), "The native gate remained blocked after removing the QA-only patch");
        }
        Need(SafeTransfer(left) && SafeTransfer(right), "Pocket safety was not restored after the gate checks");
    }

    private void BeginFaultedTransfer()
    {
        observedFirst = first; observedGround = ground; failRestore = UsePockets;
        completedWriteFailed = observedNativeMove = false; doneFailures = restoreFailures = 0;
        const string owner = "qa.f36-completion-write";
        var harmony = new Harmony(owner);
        try
        {
            Need(NativeGate(), "Native transfer safety gate was not ready before fault injection");
            MethodInfo checkedWrite = ModType("CheckedModData").GetMethod("Write", F);
            Need(checkedWrite != null, "Checked journal write is unavailable");
            harmony.Patch(checkedWrite, postfix: new HarmonyMethod(typeof(PackingRecoveryFixture).GetMethod(nameof(FailAfterCompletionWrite), F)));
            if (failRestore)
                harmony.Patch(AccessTools.Method(typeof(ModHelper), nameof(ModHelper.SetModData), new[] { typeof(string), typeof(string), typeof(string) }),
                    prefix: new HarmonyMethod(typeof(PackingRecoveryFixture).GetMethod(nameof(FailPendingRestore), F)));
            Need(NativeGate(), "Journal-only fault injection changed the native transfer safety gate");
            RoutingScenario.RunFeatureAction("f36.open");
        }
        finally { harmony.UnpatchAll(owner); observedFirst = null; observedGround = null; }

        var entry = EmporiumEntry.Instance;
        bool correctDestination = UsePockets ? ReferenceEquals(first.parentInventory, entry.afterhourPocketSlotInvLeft) ||
            ReferenceEquals(first.parentInventory, entry.afterhourPocketSlotInvRight) : ReferenceEquals(first.parentInventory, bagGrid);
        Need(doneFailures == 1 && completedWriteFailed && observedNativeMove && correctDestination &&
            restoreFailures == (UsePockets ? 1 : 0), "The journal fault did not occur after exactly one native move");
        Need(ReferenceEquals(second.parentInventory, ground) && ground.childItems.Contains(second) && second.unitCount == secondCount &&
            Same(second.modifiedShape, secondHome) && PlayerStore.Instance.scavengingAttempts == attempts,
            "The later item moved or changed after the first transfer became uncertain");
        Need(!Ready() && State(Raw(), UsePockets ? "done" : "pending"),
            "The uncertain completion did not keep the transfer blocked with its expected saved state");
        string beforeRepeat = Raw(); GameInventory firstDestination = first.parentInventory;
        RoutingScenario.RunFeatureAction("f36.open");
        Need(!Ready() && Raw() == beforeRepeat && ReferenceEquals(first.parentInventory, firstDestination) &&
            ReferenceEquals(second.parentInventory, ground) && second.unitCount == secondCount,
            "A repeated confirmation moved goods or changed the pending record");

        object result = ModType("ShopNotice").GetField("lastResult", F).GetValue(null);
        var rows = result?.GetType().GetField("Items", F)?.GetValue(result) as IEnumerable;
        Need(HasResult() && rows != null, "The interrupted batch did not retain operation details");
        object[] items = rows.Cast<object>().ToArray();
        object failed = items.SingleOrDefault(row => ((string)row.GetType().GetField("Name", F).GetValue(row)).Contains(firstName));
        object later = items.SingleOrDefault(row => ((string)row.GetType().GetField("Name", F).GetValue(row)).Contains(secondName));
        Need(failed != null && later != null && (string)failed.GetType().GetField("Reason", F).GetValue(failed) == "f08.pending" &&
            (string)later.GetType().GetField("Reason", F).GetValue(later) == "operation.not_attempted",
            "Batch details did not distinguish the pending item from the unattempted item");
    }

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Packing recovery timed out at step " + step);
        var e = EmporiumEntry.Instance;
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                TradeScenario.SetOptions(true, "f36.enabled");
                var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
                var settings = CustomUIManager.Instance.GetWindow("game_options");
                settings.Get(Mod + ":f36.space").SetFloat(UsePockets ? 32 : 2, true);
                settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
                bag = Put("backpack_medium", "QA recovery backpack", e.invElement);
                bagGrid = bag.contentWindow.childElement as GameGridInventory;
                Need(bagGrid != null, "Recovery backpack has no native grid");
                if (UsePockets) bagGrid.SetShape(2, 2);
                e.selectedBackpack = bag; PlayerStore.Instance.visitLeftTonight = 2;
                var site = AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND);
                Need(site != null && site.IsAvailable(), "Dumping ground unavailable");
                MapUIManager.Instance.Travel(site); step = 1; return false;
            case 1:
                if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !e.afterhourWindow.IsVisible() ||
                    e.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                ground = e.afterhourInventory; attempts = PlayerStore.Instance.scavengingAttempts;
                string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                firstName = "QA pending item " + token; secondName = "QA unattempted item " + token;
                foreach (GameItem existing in ground.childItems.ToArray()) existing.Destroy();
                first = Put("wine_yeast", firstName, ground); second = Put("wine_yeast_red", secondName, ground);
                first.unitValue = 10000; second.unitValue = 5000;
                secondHome = second.modifiedShape.Clone(); secondCount = second.unitCount;
                firstId = first.GetUniqueID(); secondId = second.GetUniqueID(); step = 2; return false;
            case 2:
                if (!GroundVisible(first, ground) || !GroundVisible(second, ground)) return false;
                if (UsePockets) CheckNativePocketGate();
                BeginFaultedTransfer(); step = 3; return false;
            case 3:
            {
                var direct = QaItemUi.WindowAction(e.afterhourWindow, "result");
                if (direct == null || direct.isDisabled) return false;
                direct.Select();
                var details = Window("operation");
                Need(details?.IsAlive == true && Text(details).Contains(firstName) && Text(details).Contains(secondName) &&
                    Text(details).Contains(ModHelper.GetLocalized(Mod, "operation.not_attempted")),
                    "Direct field result omitted the pending or unattempted item");
                details.Close();
                GameSlotInventory free = e.afterhourPocketSlotInvLeft.childItem == null ? e.afterhourPocketSlotInvLeft : e.afterhourPocketSlotInvRight;
                SlotMarker carry = free.TryFindOneValidInventorySlot(second);
                Need(carry != null && carry.inventory == free && carry.targetItem == null &&
                    carry.TryAcceptOnce(second.unitCount) == second.unitCount, "Could not preserve the unattempted item for retry");
                MapUIManager.Instance.ReturnNow(); step = 4; return false;
            }
            case 4:
                if (MapUIManager.Instance.IsOutside || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
                    bag.parentInventory == e.afterhourPocketSlotInvBackpack || e.storeInteractables?.activeInHierarchy != true ||
                    StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                if (readyAt == 0) { readyAt = Time.unscaledTime + .5f; return false; }
                if (Time.unscaledTime < readyAt || HasResult() || Window("operation")?.IsAlive == true) return false;
                Need(!Ready() && State(Raw(), UsePockets ? "done" : "pending"), "In-session uncertainty was lost after returning to the store");
                if (!QaItemUi.OpenAttention()) return false;
                Need(Text(Window("attention")).Contains(firstName), "Pending field transfer was not shown in store attention");
                Window("attention").Close();
                SlotMarker preserve = bagGrid.TryFindOneValidInventorySlot(second);
                Need(preserve != null && preserve.inventory == bagGrid && preserve.targetItem == null &&
                    preserve.TryAcceptOnce(second.unitCount) == second.unitCount, "Could not retain the remaining item for a later trip");
                savedRun = PlayerStore.Instance.runID; savedSlot = PlayerStore.Instance.saveSlotId;
                PlayerStore.Instance.SaveGame(); StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                secondRun = new QaNewRun(); step = 5; return false;
            case 5:
                if (!secondRun.Tick()) return false;
                Need(PlayerStore.Instance.runID != savedRun && PlayerStore.Instance.saveSlotId != savedSlot && Ready() &&
                    string.IsNullOrEmpty(Raw()) && !HasResult(), "A different save inherited the transfer or its pending state");
                PlayerStore.Instance.SaveGame(); StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                reload = new QaLoadSlot(savedSlot, Guid.Parse(savedRun)); step = 6; return false;
            case 6:
                if (!reload.Tick() || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                    StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                Need(!HasResult() && PlayerStore.Instance.FindAllItem().Any(item => item.uniqueId == firstId),
                    "Reload lost the physically moved item or resurrected the field result");
                Need(!Ready() && State(Raw(), UsePockets ? "done" : "pending"), "Returning to the original save lost its in-session pending latch");
                if (!QaItemUi.OpenAttention()) return false;
                Need(Text(Window("attention")).Contains(firstName), "The original save's pending item is not visible after switching saves");
                var confirm = RoutingScenario.Button(Window("attention"), "attention.resolve");
                Need(confirm != null && confirm.interactable, "The original save has no explicit inspection confirmation");
                confirm.onClick.Invoke();
                Need(Ready() && State(Raw(), "resolved") && PlayerStore.Instance.FindAllItem().Any(item => item.uniqueId == firstId),
                    "Confirming inspection did not resolve the latch while retaining the moved item");
                Window("attention")?.Close();
                bag = PlayerStore.Instance.FindAllItem().First(item => item.GetDisplayName(true) == "QA recovery backpack");
                bagGrid = bag.contentWindow.childElement as GameGridInventory;
                second = PlayerStore.Instance.FindAllItem().First(item => item.uniqueId == secondId);
                Need(bagGrid != null && second.parentInventory == bagGrid, "The unattempted item was not retained across saves");
                e = EmporiumEntry.Instance; e.selectedBackpack = bag; PlayerStore.Instance.visitLeftTonight = 2;
                MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND)); step = 7; return false;
            case 7:
                if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !e.afterhourWindow.IsVisible() ||
                    e.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                ground = e.afterhourInventory;
                foreach (GameItem existing in ground.childItems.ToArray()) existing.Destroy();
                SlotMarker discard = ground.TryFindOneValidInventorySlot(second);
                Need(discard != null && discard.inventory == ground && discard.targetItem == null &&
                    discard.TryAcceptOnce(second.unitCount) == second.unitCount, "Could not prepare the retry item");
                attempts = PlayerStore.Instance.scavengingAttempts; step = 8; return false;
            case 8:
                if (!GroundVisible(second, ground)) return false;
                RoutingScenario.RunFeatureAction("f36.open");
                Need(Ready() && second.parentInventory != ground && PlayerStore.Instance.scavengingAttempts == attempts &&
                    PlayerStore.Instance.FindAllItem().Any(item => item.uniqueId == firstId),
                    "The post-inspection retry did not retain the first item and move the remaining one");
                result = "PASS F36 completion-write failure, stopped batch, explicit inspection, retry and save isolation";
                return true;
            default: throw new InvalidOperationException("Unknown packing recovery step");
        }
    }
}

public sealed class PackingRecoveryScenario : PackingRecoveryFixture { protected override bool UsePockets => false; }
public sealed class PackingPocketRecoveryScenario : PackingRecoveryFixture { protected override bool UsePockets => true; }
