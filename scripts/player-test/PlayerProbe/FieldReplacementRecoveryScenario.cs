using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

internal static class QaFieldReplacementRecovery
{
    internal const string Mod = "starswhere.shopassist";
    internal const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static string ContextPath => Path.Combine(Application.persistentDataPath, "qa-field-replacement-recovery.txt");
    internal static GameItem Resident;
    internal static bool Interrupted;

    internal static void Need(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    internal static Type ModType(string name) => ModLoader.Mods.First(mod => mod.Manifest?.ID == Mod && mod.Instance != null)
        .Instance.GetType().Assembly.GetType("ShopAssist." + name, true);

    internal static bool Ready() => (bool)ModType("WholeStackTransfer").GetProperty("Ready", Static).GetValue(null);
    internal static bool CanLeave() => (bool)ModType("FieldPacking").GetMethod("CanLeave", Static).Invoke(null, null);
    internal static string Raw() => ModHelper.GetModData(Mod, "wholeStackTransfer", null);

    internal static void InterruptAfterMove(object __0)
    {
        int id = (int)__0.GetType().GetField("ItemId", Instance).GetValue(__0);
        if (Interrupted || Resident == null || id != Resident.uniqueId ||
            Resident.parentInventory != EmporiumEntry.Instance.afterhourInventory) return;
        Interrupted = true;
        throw new InvalidOperationException("QA interruption after first replacement leg");
    }

    internal static bool RefuseRestore(GameItem __0) => !Interrupted || !ReferenceEquals(__0, Resident) ||
        Resident.parentInventory != EmporiumEntry.Instance.afterhourInventory;

    internal static void Move(GameItem item, GameInventory target)
    {
        SlotMarker marker = target.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == target && marker.targetItem == null && marker.numTransfer >= item.unitCount &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == target,
            "Could not move " + item.GetDisplayName(true));
    }

    internal static bool GroundVisible(GameItem item)
    {
        var element = item as GameItemElement;
        PixelWindow window = element?.handler?.FindParentWindow();
        return element != null && !element.IsDestroyed() && element.handler != null && element.handler.gameObject.activeInHierarchy &&
            window != null && window.IsVisible() && WindowsHandler.current?.visibleWindows?.Contains(window) == true;
    }
}

public sealed class FieldReplacementRecoveryScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 180f;
    private GameItem bag, resident, blocker, incoming;
    private GameGridInventory contents, ground;
    private int step, residentId, incomingId, incomingCount;
    private long changedValue;
    private float verifyAt;

    public bool Tick(out string result)
    {
        result = null;
        QaFieldReplacementRecovery.Need(Time.realtimeSinceStartup < deadline, "Field replacement recovery timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        var player = PlayerStore.Instance;
        if (step == 1)
        {
            if (entry?.storeInteractables?.activeInHierarchy != true) return false;
            TradeScenario.SetOptions(true, "f36.enabled", "f36.replace");
            var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
            var settings = CustomUIManager.Instance.GetWindow("game_options");
            settings.Get(QaFieldReplacementRecovery.Mod + ":f36.space").SetFloat(32, true);
            settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
            bag = QaTripCargo.New("backpack_medium", entry.invElement);
            contents = (GameGridInventory)bag.contentWindow.childElement;
            resident = PackingRecoveryFixture.Put("wine_yeast_red", "QA replacement resident", contents);
            blocker = PackingRecoveryFixture.Put("wine_berry", "QA replacement blocker", contents);
            resident.unitValue = 1; blocker.unitValue = 20000;
            entry.selectedBackpack = bag; player.visitLeftTonight = 2;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND)); step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !entry.afterhourWindow.IsVisible() ||
                entry.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true)
                return false;
            ground = entry.afterhourInventory;
            foreach (GameItem existing in ground.childItems.ToArray()) existing.Destroy();
            QaFieldReplacementRecovery.Move(resident, entry.afterhourPocketSlotInvLeft);
            QaFieldReplacementRecovery.Move(blocker, entry.afterhourPocketSlotInvRight);
            contents.SetShape(2, 2);
            var reserve = ModHelper.GetConfig(QaFieldReplacementRecovery.Mod).Get("f36.space") as ConfigEntry<int>;
            QaFieldReplacementRecovery.Need(contents.childItems.Count == 0 && contents.inventoryShape.width == 2 &&
                contents.inventoryShape.height == 2 && reserve?.Value == 32 &&
                entry.afterhourPocketSlotInvLeft.childItem == resident && entry.afterhourPocketSlotInvRight.childItem == blocker,
                "The fixture must occupy both hands and reserve every backpack cell");
            incoming = PackingRecoveryFixture.Put("processed_meat", "QA replacement incoming", ground); incoming.unitValue = 10000;
            QaFieldReplacementRecovery.Need(incoming.identifier != resident.identifier && incoming.identifier != blocker.identifier,
                "The incoming item must not stack with either hand item");
            residentId = resident.GetUniqueID(); incomingId = incoming.GetUniqueID(); incomingCount = incoming.unitCount; step++; return false;
        }
        if (step == 3)
        {
            if (!QaFieldReplacementRecovery.GroundVisible(incoming)) return false;
            QaFieldReplacementRecovery.Resident = resident; QaFieldReplacementRecovery.Interrupted = false;
            var harmony = new Harmony("qa.field-replacement-recovery");
            try
            {
                var resultType = QaFieldReplacementRecovery.ModType("OperationResult");
                var transferType = QaFieldReplacementRecovery.ModType("WholeStackTransfer");
                harmony.Patch(resultType.GetMethod("CompleteCaptured", QaFieldReplacementRecovery.Instance),
                    prefix: new HarmonyMethod(typeof(QaFieldReplacementRecovery).GetMethod("InterruptAfterMove", QaFieldReplacementRecovery.Static)));
                harmony.Patch(transferType.GetMethod("EmptySlot", QaFieldReplacementRecovery.Static),
                    prefix: new HarmonyMethod(typeof(QaFieldReplacementRecovery).GetMethod("RefuseRestore", QaFieldReplacementRecovery.Static)));
                RoutingScenario.RunFeatureAction("f36.open");
            }
            finally { harmony.UnpatchAll("qa.field-replacement-recovery"); QaFieldReplacementRecovery.Resident = null; }
            var journal = QaWholeStackRestart.ReadJournal();
            QaFieldReplacementRecovery.Need(QaFieldReplacementRecovery.Interrupted && resident.parentInventory == ground &&
                incoming.parentInventory == ground && !QaFieldReplacementRecovery.Ready() &&
                journal.action == "f36.open" && journal.state == "pending" && journal.source == residentId && journal.target == incomingId &&
                QaFieldReplacementRecovery.Raw().Contains("\"version\":3") && !QaFieldReplacementRecovery.CanLeave(),
                "Interrupted replacement did not retain both goods and its pending record");
            MapUIManager.Instance.ReturnNow();
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND));
            verifyAt = Time.realtimeSinceStartup + 1; step++; return false;
        }
        if (step == 4)
        {
            if (Time.realtimeSinceStartup < verifyAt) return false;
            QaFieldReplacementRecovery.Need(MapUIManager.Instance.Current?.Id == AfterhourLocation.DUMPING_GROUND &&
                resident.parentInventory == ground, "Return or travel abandoned the protected resident");
            changedValue = incoming.unitValue + 1; incoming.unitValue = changedValue;
            QaFieldReplacementRecovery.Move(resident, entry.afterhourPocketSlotInvLeft);
            QaFieldReplacementRecovery.Need(QaFieldReplacementRecovery.CanLeave() && !QaFieldReplacementRecovery.Ready() &&
                resident.parentInventory == entry.afterhourPocketSlotInvLeft && incoming.parentInventory == ground &&
                incoming.unitValue == changedValue && incoming.unitCount == incomingCount &&
                QaWholeStackRestart.ReadJournal().state == "pending",
                "A changed incoming item was incorrectly treated as an exact replacement recovery");
            contents.SetShape(5, 5);
            QaFieldReplacementRecovery.Move(incoming, contents);
            QaFieldReplacementRecovery.Need(!QaFieldReplacementRecovery.Ready() && incoming.unitValue == changedValue &&
                incoming.unitCount == incomingCount, "Preserving the changed incoming item cleared its inspection record");
            MapUIManager.Instance.ReturnNow(); step++; return false;
        }
        if (MapUIManager.Instance.IsOutside || player.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
            entry.storeInteractables?.activeInHierarchy != true || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
        var returned = player.FindAllItem();
        QaFieldReplacementRecovery.Need(returned.Count(item => item.uniqueId == residentId) == 1 &&
            returned.Count(item => item.uniqueId == incomingId && item.unitValue == changedValue && item.unitCount == incomingCount) == 1 &&
            !QaFieldReplacementRecovery.Ready(), "Return lost either item, the changed state, or the pending record");
        player.SaveGame();
        File.WriteAllText(QaFieldReplacementRecovery.ContextPath, string.Join("|", "2", player.runID, player.saveSlotId,
            residentId, incomingId, changedValue, incomingCount));
        result = "PASS interrupted replacement preserves both items and saves the changed incoming item with a pending inspection";
        return true;
    }
}

public sealed class FieldReplacementRecoveryReloadScenario
{
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private QaLoadSlot load;
    private int step, residentId, incomingId, incomingCount;
    private long changedValue;

    public bool Tick(out string result)
    {
        result = null;
        QaFieldReplacementRecovery.Need(Time.realtimeSinceStartup < deadline, "Field replacement reload timed out at " + step);
        if (step == 0)
        {
            if (MainMenuUIController.Instance == null || CustomUIManager.Instance == null) return false;
            QaFieldReplacementRecovery.Need(File.Exists(QaFieldReplacementRecovery.ContextPath), "Run the field replacement recovery scenario first");
            string[] context = File.ReadAllText(QaFieldReplacementRecovery.ContextPath).Split('|');
            QaFieldReplacementRecovery.Need(context.Length == 7 && context[0] == "2" && Guid.TryParse(context[1], out Guid run) &&
                int.TryParse(context[2], out int slot) && int.TryParse(context[3], out residentId) && int.TryParse(context[4], out incomingId) &&
                long.TryParse(context[5], out changedValue) && int.TryParse(context[6], out incomingCount) && incomingCount > 0,
                "Invalid field replacement QA context");
            load = new QaLoadSlot(int.Parse(context[2]), Guid.Parse(context[1])); step++; return false;
        }
        if (step == 1)
        {
            if (!load.Tick() || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            var journal = QaWholeStackRestart.ReadJournal();
            QaFieldReplacementRecovery.Need(!QaFieldReplacementRecovery.Ready() && journal.state == "pending" &&
                journal.source == residentId && journal.target == incomingId &&
                PlayerStore.Instance.FindAllItem().Count(item => item.uniqueId == residentId) == 1 &&
                PlayerStore.Instance.FindAllItem().Count(item => item.uniqueId == incomingId && item.unitValue == changedValue &&
                    item.unitCount == incomingCount) == 1,
                "Cold reload lost an item, the changed incoming state, or the replacement record");
            if (!QaItemUi.OpenAttention()) return false;
            var window = CustomUIManager.Instance.GetWindow(QaFieldReplacementRecovery.Mod + ".attention");
            string text = string.Join(" | ", window.root.GetComponentsInChildren<TMP_Text>(true).Select(value => value.text));
            QaFieldReplacementRecovery.Need(text.Contains("QA replacement resident") && text.Contains("QA replacement incoming"),
                "The pending replacement does not identify both goods in attention");
            var resolve = RoutingScenario.Button(window, "attention.resolve");
            QaFieldReplacementRecovery.Need(resolve != null && resolve.interactable, "The pending replacement cannot be inspected");
            resolve.onClick.Invoke();
            QaFieldReplacementRecovery.Need(QaFieldReplacementRecovery.Ready() && QaWholeStackRestart.ReadJournal().state == "resolved" &&
                PlayerStore.Instance.FindAllItem().Count(item => item.uniqueId == residentId) == 1 &&
                PlayerStore.Instance.FindAllItem().Count(item => item.uniqueId == incomingId && item.unitValue == changedValue &&
                    item.unitCount == incomingCount) == 1,
                "Inspection did not resolve the pending replacement while retaining both items");
            window.Close(); PlayerStore.Instance.SaveGame(); step++; result = "PASS cold reload and explicit inspection of an interrupted field replacement";
            return true;
        }
        throw new InvalidOperationException("Unknown field replacement reload step");
    }
}
