using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The two scenarios run in separate guarded Players and share only their QA context.
internal static class QaWholeStackRestart
{
    internal const string Mod = "starswhere.shopassist";
    internal const string ItemId = "exotic_fuel";
    internal const int Amount = 2, TargetBefore = 3;
    internal const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static string ContextPath => Path.Combine(Application.persistentDataPath, "qa-whole-stack-restart.txt");

    [Serializable]
    internal sealed class Journal
    {
        public string action = "", state = "", sourceName = "", location = "", sourceKey = "", targetKey = "";
        public int source = 0, target = 0, amount = 0, targetBefore = 0, sourceX = 0, sourceY = 0, targetX = 0, targetY = 0;
    }

    internal static void Need(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    // This is the game's own stackable item factory. Register it only in the
    // isolated Player if its native directory is not already active.
    internal static bool PrepareNativeDirectory()
    {
        if (MainMenuUIController.Instance == null || CustomUIManager.Instance == null) return false;
        var property = typeof(DirectoryMaster).GetProperty("directories", Static);
        var directories = property?.GetValue(null) as IDictionary;
        Need(directories != null, "Native item directories unavailable");
        var registered = directories.Contains(typeof(GameItem)) ? directories[typeof(GameItem)] as IEnumerable : null;
        if (registered == null || !registered.Cast<object>().Any(value => value is UnusedDirectory))
        {
            var host = new GameObject("QA native stackable directory");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<UnusedDirectory>();
        }
        return true;
    }

    internal static Type Transfer()
    {
        var mod = ModLoader.Mods.Single(value => value.Manifest?.ID == Mod);
        return mod.Instance.GetType().Assembly.GetType("ShopAssist.WholeStackTransfer", true);
    }

    internal static bool Ready(Type transfer) => (bool)transfer.GetProperty("Ready", Static).GetValue(null);

    internal static GameItem New(string id, int count = 1)
    {
        GameItem item = DirectoryMaster.Item(id);
        Need(item != null && item.identifier == id, "Native fixture unavailable: " + id);
        GeneralHelper.SetItemOwned(item, true);
        if (count != 1) item.SetStack(count);
        return item;
    }

    internal static void Put(GameItem item, GameGridInventory inventory)
    {
        SlotMarker marker = inventory.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == inventory && marker.targetItem == null && marker.numTransfer >= item.unitCount &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory,
            "Could not place the native whole-stack fixture: " + item.identifier);
    }

    internal static object Plan(Type transfer, GameItem source, GameGridInventory target)
    {
        Type reservations = transfer.GetNestedType("Reservations", BindingFlags.NonPublic);
        Need(reservations != null, "Whole-stack reservations type unavailable");
        var planner = transfer.GetMethod("Stack", Static);
        Need(planner != null, "Whole-stack planning method unavailable");
        object plan = planner.Invoke(null, new[] { (object)source, target, Activator.CreateInstance(reservations, true) });
        Need(plan != null, "Native stack was not accepted by the shared planner");
        return plan;
    }

    internal static object Field(object value, string name) => value?.GetType().GetField(name, Instance)?.GetValue(value);

    internal static string Key(object location) => (string)location.GetType().GetMethod("Key", Instance).Invoke(location, new object[] { true });

    internal static object Location(Assembly assembly, GameInventory inventory)
    {
        Type inventoryType = assembly.GetType("ShopAssist.ShopInventory", true);
        var values = (IEnumerable)inventoryType.GetMethod("OwnedLocations", Static).Invoke(null, new object[] { false });
        object location = values.Cast<object>().SingleOrDefault(value => ReferenceEquals(Field(value, "Inventory"), inventory));
        Need(location != null && (bool)Field(location, "Ordinary"), "Native storage location unavailable");
        return location;
    }

    internal static Journal ReadJournal()
    {
        string raw = ModHelper.GetModData(Mod, "wholeStackTransfer", null);
        Need(!string.IsNullOrEmpty(raw), "Whole-stack journal is missing");
        Journal journal = JsonUtility.FromJson<Journal>(raw);
        Need(journal != null, "Whole-stack journal is unreadable");
        return journal;
    }
}

public sealed class WholeStackRestartScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private bool directoryPrepared;
    private int step;

    public bool Tick(out string result)
    {
        result = null;
        QaWholeStackRestart.Need(Time.realtimeSinceStartup < deadline, "Whole-stack producer timed out at " + step);
        if (!directoryPrepared)
        {
            if (!QaWholeStackRestart.PrepareNativeDirectory()) return false;
            directoryPrepared = true;
            if (File.Exists(QaWholeStackRestart.ContextPath)) File.Delete(QaWholeStackRestart.ContextPath);
        }
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        if (entry?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;

        string token = Guid.NewGuid().ToString("N").Substring(0, 8);
        GameItem sourceBag = QaWholeStackRestart.New("backpack_medium"), targetBag = QaWholeStackRestart.New("backpack_medium");
        GeneralHelper.SetCustomName(sourceBag, "QA cold source " + token);
        GeneralHelper.SetCustomName(targetBag, "QA cold target " + token);
        QaWholeStackRestart.Put(sourceBag, entry.invElement); QaWholeStackRestart.Put(targetBag, entry.invElement);
        var sourceGrid = sourceBag.contentWindow?.childElement as GameGridInventory;
        var targetGrid = targetBag.contentWindow?.childElement as GameGridInventory;
        QaWholeStackRestart.Need(sourceGrid != null && targetGrid != null, "Native storage grids unavailable");
        GameItem source = QaWholeStackRestart.New(QaWholeStackRestart.ItemId, QaWholeStackRestart.Amount);
        GameItem target = QaWholeStackRestart.New(QaWholeStackRestart.ItemId, QaWholeStackRestart.TargetBefore);
        QaWholeStackRestart.Put(source, sourceGrid); QaWholeStackRestart.Put(target, targetGrid);
        int sourceBagId = sourceBag.GetUniqueID(), targetBagId = targetBag.GetUniqueID();
        int sourceId = source.GetUniqueID(), targetId = target.GetUniqueID();
        Type transfer = QaWholeStackRestart.Transfer();
        QaWholeStackRestart.Need(QaWholeStackRestart.Ready(transfer), "A transfer is already pending in the fresh QA save");
        object plan = QaWholeStackRestart.Plan(transfer, source, targetGrid);
        QaWholeStackRestart.Need(ReferenceEquals(QaWholeStackRestart.Field(plan, "Stack"), target) &&
            (int)QaWholeStackRestart.Field(plan, "Amount") == QaWholeStackRestart.Amount &&
            (int)QaWholeStackRestart.Field(plan, "StackBefore") == QaWholeStackRestart.TargetBefore,
            "Shared planner did not select the complete native stack merge");
        object from = QaWholeStackRestart.Location(transfer.Assembly, sourceGrid);
        object to = QaWholeStackRestart.Location(transfer.Assembly, targetGrid);
        string path = (string)QaWholeStackRestart.Field(from, "Name") + " → " + (string)QaWholeStackRestart.Field(to, "Name");
        var begin = transfer.GetMethod("Begin", QaWholeStackRestart.Static);
        QaWholeStackRestart.Need(begin != null && (bool)begin.Invoke(null, new[] { plan, "f08.open", path,
            QaWholeStackRestart.Key(from), QaWholeStackRestart.Key(to) }), "Could not prewrite the pending transfer");
        SlotMarker marker = target.GetStackSlot(source);
        QaWholeStackRestart.Need(marker != null && marker.inventory == targetGrid && marker.targetItem == target &&
            marker.item == source && marker.numTransfer >= QaWholeStackRestart.Amount &&
            marker.TryAcceptOnce(QaWholeStackRestart.Amount) == QaWholeStackRestart.Amount,
            "Native stack merge did not accept the planned quantity");
        QaWholeStackRestart.Need(source.parentInventory == null && source.unitCount == 0 && !sourceGrid.childItems.Contains(source) &&
            target.parentInventory == targetGrid && target.unitCount == QaWholeStackRestart.Amount + QaWholeStackRestart.TargetBefore &&
            !QaWholeStackRestart.Ready(transfer) && QaWholeStackRestart.ReadJournal().state == "pending",
            "Merged goods or pending journal did not match before the save");
        var player = PlayerStore.Instance;
        player.SaveGame();
        File.WriteAllText(QaWholeStackRestart.ContextPath, string.Join("|", "1", player.runID, player.saveSlotId,
            sourceBagId, targetBagId, sourceId, targetId, token));
        result = "PASS native whole-stack merge saved with a pending journal for a separate Player reload";
        return true;
    }
}

public sealed class WholeStackRestartReloadScenario
{
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private bool directoryPrepared;
    private int step, sourceBagId, targetBagId, sourceId, targetId;
    private string token;
    private QaLoadSlot load;
    private Type transfer;
    private GameGridInventory sourceGrid, targetGrid;
    private GameItem candidate, target;
    private object plan;
    private QaWholeStackRestart.Journal journal;

    public bool Tick(out string result)
    {
        result = null;
        QaWholeStackRestart.Need(Time.realtimeSinceStartup < deadline, "Whole-stack cold reload timed out at " + step);
        if (!directoryPrepared)
        {
            if (!QaWholeStackRestart.PrepareNativeDirectory()) return false;
            directoryPrepared = true;
            QaWholeStackRestart.Need(File.Exists(QaWholeStackRestart.ContextPath), "Run WholeStackRestartScenario first");
            string[] context = File.ReadAllText(QaWholeStackRestart.ContextPath).Split('|');
            QaWholeStackRestart.Need(context.Length == 8 && context[0] == "1" && Guid.TryParse(context[1], out Guid _) &&
                int.TryParse(context[2], out int _) && int.TryParse(context[3], out sourceBagId) &&
                int.TryParse(context[4], out targetBagId) && int.TryParse(context[5], out sourceId) &&
                int.TryParse(context[6], out targetId) && context[7].Length == 8, "Invalid whole-stack QA context");
            token = context[7];
            load = new QaLoadSlot(int.Parse(context[2]), Guid.Parse(context[1]));
        }
        if (step == 0)
        {
            if (!load.Tick() || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                StoreUIManager.Instance?.blackscreen?.gameObject.activeInHierarchy == true) return false;
            var calendar = AdvCalendarUIManager.Instance;
            if (calendar?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            var root = EmporiumEntry.Instance.invElement;
            GameItem sourceBag = root.childItems.SingleOrDefault(item => item.uniqueId == sourceBagId);
            GameItem targetBag = root.childItems.SingleOrDefault(item => item.uniqueId == targetBagId);
            sourceGrid = sourceBag?.contentWindow?.childElement as GameGridInventory;
            targetGrid = targetBag?.contentWindow?.childElement as GameGridInventory;
            QaWholeStackRestart.Need(sourceGrid != null && targetGrid != null, "Cold reload lost the native storage containers");
            target = targetGrid.childItems.SingleOrDefault(item => item.uniqueId == targetId);
            QaWholeStackRestart.Need(target?.identifier == QaWholeStackRestart.ItemId &&
                target.unitCount == QaWholeStackRestart.TargetBefore + QaWholeStackRestart.Amount &&
                sourceGrid.childItems.All(item => item.uniqueId != sourceId) &&
                sourceGrid.childItems.Concat(targetGrid.childItems).Where(item => item.identifier == QaWholeStackRestart.ItemId)
                    .Sum(item => item.unitCount) == QaWholeStackRestart.TargetBefore + QaWholeStackRestart.Amount,
                "Cold reload did not conserve the merged whole-stack quantity");
            transfer = QaWholeStackRestart.Transfer();
            journal = QaWholeStackRestart.ReadJournal();
            QaWholeStackRestart.Need(journal.state == "pending" && journal.action == "f08.open" && journal.source == sourceId &&
                journal.target == targetId && journal.amount == QaWholeStackRestart.Amount &&
                journal.targetBefore == QaWholeStackRestart.TargetBefore && !QaWholeStackRestart.Ready(transfer),
                "Cold reload lost the pending transfer or its quantities");
            candidate = QaWholeStackRestart.New(QaWholeStackRestart.ItemId, QaWholeStackRestart.Amount);
            QaWholeStackRestart.Put(candidate, sourceGrid);
            plan = QaWholeStackRestart.Plan(transfer, candidate, targetGrid);
            QaWholeStackRestart.Need(ReferenceEquals(QaWholeStackRestart.Field(plan, "Stack"), target), "Retry fixture is not a native stack merge");
            string before = ModHelper.GetModData(QaWholeStackRestart.Mod, "wholeStackTransfer", null);
            object outcome = transfer.GetMethod("Apply", QaWholeStackRestart.Static).Invoke(null, new object[]
                { plan, "f08.open", journal.location, journal.sourceKey, journal.targetKey, (Func<bool>)(() => true), null });
            QaWholeStackRestart.Need(outcome?.ToString() == "Unavailable" && candidate.parentInventory == sourceGrid &&
                candidate.unitCount == QaWholeStackRestart.Amount && target.unitCount == QaWholeStackRestart.TargetBefore + QaWholeStackRestart.Amount &&
                ModHelper.GetModData(QaWholeStackRestart.Mod, "wholeStackTransfer", null) == before,
                "Pending journal did not block a repeated whole-stack transfer");
            step = 1; return false;
        }
        if (step == 1)
        {
            if (!QaItemUi.OpenAttention()) return false;
            var window = CustomUIManager.Instance.GetWindow(QaWholeStackRestart.Mod + ".attention");
            var text = window.root.GetComponentsInChildren<TMP_Text>(true).Select(value => value.text ?? "").ToArray();
            string detail = ModHelper.GetLocalized(QaWholeStackRestart.Mod, "f08.journal_detail", journal.location,
                journal.sourceX + 1, journal.sourceY + 1, journal.targetX + 1, journal.targetY + 1, journal.amount, journal.targetBefore);
            QaWholeStackRestart.Need(text.Any(value => value.Contains(journal.sourceName)) &&
                text.Any(value => value.Contains(ModHelper.GetLocalized(QaWholeStackRestart.Mod, "f08.pending"))) &&
                text.Any(value => value.Contains(detail) && value.Contains(token)),
                "Pending transfer, locations or original quantities were not visible in attention");
            string resolved = ModHelper.GetLocalized(QaWholeStackRestart.Mod, "attention.resolve");
            Button button = window.root.GetComponentsInChildren<Button>(true).FirstOrDefault(value =>
                value.gameObject.activeInHierarchy && value.interactable && value.GetComponentInChildren<TMP_Text>(true)?.text == resolved);
            QaWholeStackRestart.Need(button != null, "Pending transfer cannot be marked as checked");
            button.onClick.Invoke();
            QaWholeStackRestart.Need(QaWholeStackRestart.Ready(transfer) && QaWholeStackRestart.ReadJournal().state == "resolved",
                "Marking the pending transfer checked did not unlock it");
            window.Close();
            object outcome = transfer.GetMethod("Apply", QaWholeStackRestart.Static).Invoke(null, new object[]
                { plan, "f08.open", journal.location, journal.sourceKey, journal.targetKey, (Func<bool>)(() => true), null });
            QaWholeStackRestart.Need(outcome?.ToString() == "Applied" && target.unitCount ==
                QaWholeStackRestart.TargetBefore + QaWholeStackRestart.Amount * 2 && !sourceGrid.childItems.Contains(candidate) &&
                QaWholeStackRestart.Ready(transfer) && QaWholeStackRestart.ReadJournal().state == "done",
                "A fresh confirmed transfer did not run after pending resolution");
            File.Delete(QaWholeStackRestart.ContextPath);
            result = "PASS cold reload preserves native merged quantity, displays and blocks the pending transfer, and unlocks after checking";
            return true;
        }
        throw new InvalidOperationException("Unknown whole-stack cold reload step");
    }
}
