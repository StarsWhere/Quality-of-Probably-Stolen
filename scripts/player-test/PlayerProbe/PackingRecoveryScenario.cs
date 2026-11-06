using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Uses F36's actual preview/apply controls and injects failures only at native placement.
public abstract class PackingRecoveryFixture
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private QaNewRun secondRun;
    private QaLoadSlot reload;
    private readonly float deadline = Time.realtimeSinceStartup + 180f;
    private GameItem bag, first, second;
    private GameGridInventory bagGrid, ground, thirdGrid;
    private GridShape firstHome, secondHome;
    private int firstCount, secondCount, attempts, savedSlot, step;
    private string savedRun, firstName, secondName, savedAttention;
    private float storeReadyAt;

    private static GameItem faultItem, laterItem;
    private static GameInventory faultGround, faultTarget;
    private static GameGridInventory faultThird;
    private static GridShape thirdPose;
    private static int mode, targetCalls, rollbackCalls, laterCalls, acceptedByNative;
    private static string planOrder, placementOrder;
    protected abstract bool UsePockets { get; }
    protected virtual bool ThirdPartyCase => false;

    private static void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
    private static CustomUIWindow Window(string id) => CustomUIManager.Instance.GetWindow(Mod + "." + id);
    private static Type ModType(string name) => ModLoader.Mods.First(m => m.Manifest?.ID == Mod).Instance.GetType().Assembly.GetType("ShopAssist." + name, true);
    private static bool HasResult() => (bool)ModType("ShopNotice").GetProperty("HasResult", F).GetValue(null);
    private static string Text(CustomUIWindow window) => string.Join(" | ", window.root.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text));
    private static Toggle PriorityToggle()
    {
        string label = ModHelper.GetLocalized(Mod, "f36.priority_edit");
        return Window("packing").root.GetComponentsInChildren<Toggle>(true).FirstOrDefault(toggle => toggle.gameObject.activeInHierarchy &&
            toggle.transform.parent.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == label));
    }
    private static bool Same(GridShape a, GridShape b) => a != null && b != null && a.minX == b.minX && a.minY == b.minY &&
        a.width == b.width && a.height == b.height && a.orientation == b.orientation && a.flipped == b.flipped;

    internal static GameItem Put(string identifier, string name, GameInventory inventory)
    {
        GameItem item = DirectoryMaster.Item(identifier);
        Need(item != null, "Missing native fixture " + identifier);
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

    private static bool BeforePlacement(SlotMarker __instance, ref int __result, out bool __state)
    {
        __state = false;
        if (mode == 0 || __instance == null) return true;
        if ((ReferenceEquals(__instance.item, faultItem) || ReferenceEquals(__instance.item, laterItem)) &&
            !ReferenceEquals(__instance.inventory, faultGround))
            placementOrder += (placementOrder.Length == 0 ? "" : " > ") + (ReferenceEquals(__instance.item, faultItem) ? "first" : "second");
        if (ReferenceEquals(__instance.item, laterItem) && !ReferenceEquals(__instance.inventory, faultGround)) laterCalls++;
        if (!ReferenceEquals(__instance.item, faultItem)) return true;
        if (ReferenceEquals(__instance.inventory, faultTarget))
        {
            targetCalls++;
            __state = true;
        }
        else if (ReferenceEquals(__instance.inventory, faultGround) && !ReferenceEquals(faultItem.parentInventory, faultGround))
        {
            rollbackCalls++;
            if (mode == 3) { __result = 0; return false; }
        }
        return true;
    }

    private static void BeforeCommit(IEnumerable __0)
    {
        planOrder = string.Join(" > ", __0.Cast<object>().Select(pick =>
        {
            var value = (GameItem)pick.GetType().GetField("Item", F).GetValue(pick);
            return ReferenceEquals(value, faultItem) ? "first" : ReferenceEquals(value, laterItem) ? "second" : value?.identifier;
        }));
    }

    private static void AfterPlacement(bool __state, ref int __result)
    {
        if (!__state) return;
        acceptedByNative = __result;
        if (mode == 1) throw new InvalidOperationException("QA injected placement callback failure");
        if (mode == 4)
        {
            var third = faultThird.TryFindOneValidInventorySlot(faultItem);
            Need(third != null && third.inventory == faultThird && third.targetItem == null &&
                third.TryAcceptOnce(faultItem.unitCount) == faultItem.unitCount && ReferenceEquals(faultItem.parentInventory, faultThird),
                "The injected callback could not move the original item into the third-party container");
            thirdPose = faultItem.modifiedShape.Clone();
        }
        __result = 0;
    }

    private void ApplyFailure(int failureMode)
    {
        Window("packing").Get("preview").button.onClick.Invoke();
        var preview = Window("packing");
        Need(preview?.Get("apply")?.button != null && QaItemUi.Selected(preview, first) && QaItemUi.Selected(preview, second),
            "The real F36 preview did not include both recovery fixtures");
        faultItem = first; laterItem = second; faultGround = ground;
        GameInventory expectedTarget = bagGrid;
        if (UsePockets)
        {
            expectedTarget = new[] { EmporiumEntry.Instance.afterhourPocketSlotInvLeft, EmporiumEntry.Instance.afterhourPocketSlotInvRight }
                .Where(pocket => pocket != null && pocket.childItem == null && !pocket.IsInsertLocked())
                .Select(pocket => pocket.TryFindOneValidInventorySlot(first))
                .Where(marker => marker != null && marker.targetItem == null && marker.numTransfer >= firstCount)
                .Select(marker => marker.inventory).FirstOrDefault();
            Need(expectedTarget != null, "The native pockets cannot accept the recovery fixture");
        }
        faultTarget = expectedTarget; faultThird = thirdGrid; thirdPose = null;
        mode = failureMode; targetCalls = rollbackCalls = laterCalls = acceptedByNative = 0;
        planOrder = placementOrder = "";
        const string patchId = "qa.field-packing-recovery";
        var harmony = new Harmony(patchId);
        try
        {
            harmony.Patch(AccessTools.Method(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce), new[] { typeof(int) }),
                prefix: new HarmonyMethod(typeof(PackingRecoveryFixture).GetMethod(nameof(BeforePlacement), F)),
                postfix: new HarmonyMethod(typeof(PackingRecoveryFixture).GetMethod(nameof(AfterPlacement), F)));
            harmony.Patch(ModType("FieldPacking").GetMethod("Commit", F),
                prefix: new HarmonyMethod(typeof(PackingRecoveryFixture).GetMethod(nameof(BeforeCommit), F)));
            preview.Get("apply").button.onClick.Invoke();
        }
        finally
        {
            harmony.UnpatchAll(patchId); mode = 0;
            faultItem = laterItem = null; faultGround = faultTarget = null; faultThird = null;
        }
        string diagnostics = "mode=" + failureMode + ", plan=" + planOrder + ", placements=" + placementOrder +
            ", targetCalls=" + targetCalls + ", laterCalls=" + laterCalls + ", rollbackCalls=" + rollbackCalls +
            ", accepted=" + acceptedByNative;
        UnityEngine.Debug.Log("[PLAYER-QA] Packing recovery: " + diagnostics);
        Need(planOrder == "first > second", "The recovery fixture was not planned in priority order: " + diagnostics);
        Need(targetCalls == 1 && laterCalls == 0, "Failure did not stop before attempting the next item: " + diagnostics);
        Need(ReferenceEquals(second.parentInventory, ground) && ground.childItems.Contains(second) && second.unitCount == secondCount &&
            Same(second.modifiedShape, secondHome), "A later item changed after the failure");
        Need(PlayerStore.Instance.scavengingAttempts == attempts, "Packing failure consumed a scavenging attempt");
        if (failureMode == 1)
            Need(acceptedByNative == firstCount && rollbackCalls == 1 && ReferenceEquals(first.parentInventory, ground) && ground.childItems.Contains(first) &&
                first.unitCount == firstCount && Same(first.modifiedShape, firstHome), "A callback exception after native movement did not restore the original item and pose");
        else if (failureMode == 2)
            Need(acceptedByNative == firstCount && rollbackCalls == 1 && ReferenceEquals(first.parentInventory, ground) &&
                ground.childItems.Contains(first) && first.unitCount == firstCount && Same(first.modifiedShape, firstHome),
                "A rejected placement after a real move did not restore the original instance and pose");
        else if (failureMode == 3)
            Need(acceptedByNative == firstCount && rollbackCalls == 1 && !ReferenceEquals(first.parentInventory, ground) &&
                first.unitCount == firstCount && ReferenceEquals(first.parentInventory, expectedTarget),
                "The failed restoration fixture did not leave the actual item at its destination");
        else
            Need(acceptedByNative == firstCount && rollbackCalls == 0 && ReferenceEquals(first.parentInventory, thirdGrid) &&
                thirdGrid.childItems.Contains(first) && !expectedTarget.childItems.Contains(first) && first.unitCount == firstCount &&
                Same(first.modifiedShape, thirdPose), "Recovery took an item back from the third-party inventory");

        var notice = Window("operation");
        Need(HasResult() && notice?.IsAlive == true && Text(notice).Contains(firstName) && Text(notice).Contains(secondName) &&
            Text(notice).Contains(ModHelper.GetLocalized(Mod, "operation.not_attempted")), "The visible result omitted the failure or unattempted item");
        var rows = (IEnumerable)ModType("ShopNotice").GetField("lastResult", F).GetValue(null).GetType().GetField("Items", F)
            .GetValue(ModType("ShopNotice").GetField("lastResult", F).GetValue(null));
        object[] items = rows.Cast<object>().ToArray();
        object failed = items.Single(row => ((string)row.GetType().GetField("Name", F).GetValue(row)).Contains(firstName));
        object pending = items.Single(row => ((string)row.GetType().GetField("Name", F).GetValue(row)).Contains(secondName));
        string failedStatus = failed.GetType().GetField("Status", F).GetValue(failed).ToString();
        string pendingReason = (string)pending.GetType().GetField("Reason", F).GetValue(pending);
        Need(failedStatus == (failureMode >= 3 ? "NeedsInspection" : "Skipped") && pendingReason == "operation.not_attempted",
            "The result classified the failed or unattempted item incorrectly");
        if (failureMode < 3) notice.Close();
    }

    private static void OpenAttention()
    {
        var options = StoreUIManager.Instance.gameOptionUIManager;
        options.OpenUI();
        CustomUIManager.Instance.GetWindow("game_options").Get(Mod + ":tools").button.onClick.Invoke();
        Window("tools").Get("attention").button.onClick.Invoke();
        Need(Window("attention")?.IsAlive == true, "The visible attention control did not open the list");
    }

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Packing recovery timed out at step " + step);
        var e = EmporiumEntry.Instance;
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                TradeScenario.SetOptions(true, "f36.enabled");
                var options = StoreUIManager.Instance.gameOptionUIManager;
                options.OpenUI();
                var settings = CustomUIManager.Instance.GetWindow("game_options");
                settings.Get(Mod + ":f36.space").SetFloat(UsePockets ? 32 : 2, true);
                settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
                bag = Put("backpack_medium", "QA recovery backpack", e.invElement);
                bagGrid = bag.contentWindow.childElement as GameGridInventory;
                Need(bagGrid != null, "The recovery backpack has no native grid");
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
                firstName = "QA recovery first " + token; secondName = "QA recovery next " + token;
                if (ThirdPartyCase)
                {
                    GameItem thirdBag = Put("fanny_pack", "QA third-party container " + token, ground);
                    thirdGrid = thirdBag.contentWindow?.childElement as GameGridInventory;
                    Need(thirdGrid != null, "The third-party container has no native grid");
                }
                first = Put("wine_yeast", firstName, ground); second = Put("wine_yeast_red", secondName, ground);
                if (Cells(first.modifiedShape) > Cells(second.modifiedShape))
                { (first, second) = (second, first); (firstName, secondName) = (secondName, firstName); }
                first.GetUniqueID(); second.GetUniqueID();
                firstHome = first.modifiedShape.Clone(); secondHome = second.modifiedShape.Clone();
                firstCount = first.unitCount; secondCount = second.unitCount;
                step = 10; return false;
            case 10:
                if (!GroundVisible(first) || !GroundVisible(second)) return false;
                RoutingScenario.OpenOptionAction("f36.open");
                foreach (GameItem item in ground.childItems.ToArray())
                {
                    if (ReferenceEquals(item, first) || ReferenceEquals(item, second)) continue;
                    var packing = Window("packing");
                    if (QaItemUi.Hit(packing.root, item) != null && QaItemUi.Selected(packing, item)) QaItemUi.Click(packing, item);
                }
                var priority = PriorityToggle();
                Need(priority != null && !priority.isOn, "The recovery priority control is unavailable");
                priority.isOn = true;
                Need(!QaItemUi.Selected(Window("packing"), first) && !QaItemUi.Selected(Window("packing"), second),
                    "The recovery fixture unexpectedly had priority");
                QaItemUi.Click(Window("packing"), first);
                Need(QaItemUi.Selected(Window("packing"), first) && !QaItemUi.Selected(Window("packing"), second),
                    "The recovery priority control did not select only the failed item");
                priority = PriorityToggle();
                Need(priority != null && priority.isOn, "The recovery priority control was lost");
                priority.isOn = false;
                Need(QaItemUi.Selected(Window("packing"), first) && QaItemUi.Selected(Window("packing"), second),
                    "The recovery priority choice changed the selected fixtures");
                ApplyFailure(1); step = 20; return false;
            case 20:
                if (!GroundVisible(first) || !GroundVisible(second)) return false;
                ApplyFailure(2); step = 21; return false;
            case 21:
                if (!GroundVisible(first) || !GroundVisible(second)) return false;
                ApplyFailure(ThirdPartyCase ? 4 : 3);
                string raw = ModHelper.GetModData(Mod, "attention", null);
                Need(raw != null && raw.Contains(firstName) && raw.Contains(PlayerStore.Instance.runID) &&
                    raw.Contains("\"slot\":" + PlayerStore.Instance.saveSlotId) && raw.Contains("\"item\":" + first.uniqueId),
                    "Unrecoverable F36 item was not saved with run, slot, and item identity");
                Need(Window("operation")?.IsAlive == true, "The field result is not open before returning");
                if (!ThirdPartyCase)
                {
                    // A player can manually return the item, but that does not acknowledge its inspection issue.
                    SlotMarker manual = ground.TryInventorySlot(first, firstCount, firstHome);
                    Need(manual != null && manual.targetItem == null && manual.numTransfer >= firstCount &&
                        manual.TryAcceptOnce(firstCount) == firstCount && ReferenceEquals(first.parentInventory, ground) &&
                        Same(first.modifiedShape, firstHome), "Could not manually return the inspection fixture to its ground position");
                    savedAttention = raw; step = 11; return false;
                }
                Window("packing")?.Close(); MapUIManager.Instance.ReturnNow(); step = 2; return false;
            case 11:
                if (!GroundVisible(first) || !GroundVisible(second)) return false;
                Window("packing").Get("preview").button.onClick.Invoke();
                var nextPreview = Window("packing");
                Need(QaItemUi.Hit(nextPreview.root, first) == null && QaItemUi.Selected(nextPreview, second) &&
                    ModHelper.GetModData(Mod, "attention", null) == savedAttention,
                    "A visibly accessible unresolved inspection item was offered for automatic packing again");
                Need(Window("operation")?.IsAlive == true, "The field result unexpectedly closed before returning");
                Window("packing")?.Close(); MapUIManager.Instance.ReturnNow(); step = 2; return false;
            case 2:
                if (MapUIManager.Instance.IsOutside || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
                    bag.parentInventory == e.afterhourPocketSlotInvBackpack || e.storeInteractables?.activeInHierarchy != true ||
                    StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                if (storeReadyAt == 0) { storeReadyAt = Time.unscaledTime + .1f; return false; }
                if (Time.unscaledTime < storeReadyAt) return false;
                Need(!HasResult() && Window("operation")?.IsAlive != true, "The field result leaked into the store");
                OpenAttention(); Need(Text(Window("attention")).Contains(firstName), "The field inspection item was not available in the store");
                Window("attention").Close(); StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                savedRun = PlayerStore.Instance.runID; savedSlot = PlayerStore.Instance.saveSlotId;
                PlayerStore.Instance.SaveGame(); StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                secondRun = new QaNewRun(); step = 3; return false;
            case 3:
                if (!secondRun.Tick()) return false;
                Need(PlayerStore.Instance.runID != savedRun && PlayerStore.Instance.saveSlotId != savedSlot && !HasResult() &&
                    string.IsNullOrEmpty(ModHelper.GetModData(Mod, "attention", null)), "A different save inherited the field result or attention item");
                PlayerStore.Instance.SaveGame(); StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                reload = new QaLoadSlot(savedSlot, Guid.Parse(savedRun)); step = 4; return false;
            case 4:
                if (!reload.Tick()) return false;
                Need(!HasResult() && Window("operation")?.IsAlive != true &&
                    (ModHelper.GetModData(Mod, "attention", "").Contains(firstName)), "Reload lost the issue or resurrected the field result");
                OpenAttention(); Need(Text(Window("attention")).Contains(firstName), "Reloaded inspection item is not visible");
                result = "PASS F36 " + (ThirdPartyCase ? "third-party container" : UsePockets ? "pocket" : "backpack") +
                    " native placement exception, restored move, failed recovery, stopped batch and run/slot isolation";
                return true;
            default: throw new InvalidOperationException("Unknown packing recovery step");
        }
    }

    private static int Cells(GridShape shape)
    {
        int result = 0;
        for (int y = shape.minY; y <= shape.maxY; y++) for (int x = shape.minX; x <= shape.maxX; x++)
            if (shape.Intersects(x, y)) result++;
        return result;
    }

    private bool GroundVisible(GameItem value)
    {
        var element = value as GameItemElement;
        PixelWindow parent = element?.handler?.FindParentWindow();
        return value != null && ReferenceEquals(value.parentInventory, ground) && element != null && !element.IsDestroyed() &&
            element.handler != null && element.handler.gameObject.activeInHierarchy && parent != null && parent.IsVisible() &&
            WindowsHandler.current?.visibleWindows?.Contains(parent) == true;
    }
}

public sealed class PackingRecoveryScenario : PackingRecoveryFixture { protected override bool UsePockets => false; }
public sealed class PackingPocketRecoveryScenario : PackingRecoveryFixture { protected override bool UsePockets => true; }
public sealed class PackingThirdPartyRecoveryScenario : PackingRecoveryFixture
{
    protected override bool UsePockets => false;
    protected override bool ThirdPartyCase => true;
}
