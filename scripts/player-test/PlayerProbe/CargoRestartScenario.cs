using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

internal static class QaCargoRestart
{
    internal const string Mod = QaTripCargo.Mod;
    internal const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static readonly string ProcessStamp = Guid.NewGuid().ToString("N");
    internal static string ContextPath => Path.Combine(Application.persistentDataPath, "qa-cargo-restart.json");

    [Serializable] internal sealed class Context
    {
        public int version = 1, slot, bag, item, old, quantity;
        public string run, token, process, record;
    }

    [Serializable] internal sealed class Journal
    {
        public int version = 1, slot, source, amount;
        public int target = 0, targetBefore = 0, sourceX = 0, sourceY = 0, targetX = 0, targetY = 0;
        public string run, action = "f25.open", state = "pending", sourceName, targetName = "", location,
            sourceKey, targetKey = "root:stock";
    }

    internal static Type CargoType()
    {
        var mod = ModLoader.Mods.Single(value => value.Manifest?.ID == Mod && value.Instance != null);
        return mod.Instance.GetType().Assembly.GetType("ShopAssist.TripCargo", true);
    }

    internal static object Cargo() => CargoType().GetField("active", Static)?.GetValue(null);
    internal static string Raw(string key) => ModHelper.GetModData(Mod, key, null);
    internal static void Need(bool condition, string message) => QaTripCargo.Need(condition, message);
    internal static bool Selected(GameItem item)
    {
        object entry = QaTripCargo.Field(QaItemUi.Hit(QaTripCargo.Window().root, item), "entry");
        object selected = QaTripCargo.Field(entry, "Selected");
        Need(selected is bool, "Cargo card has no selection state");
        return (bool)selected;
    }
    internal static void SameWorld(GameItem bag, GameItem item, GameItem old, GameGridInventory contents)
    {
        var e = EmporiumEntry.Instance;
        Need(bag != null && item != null && old != null && contents != null &&
            ReferenceEquals(bag.parentInventory, e.invElement) && e.invElement.childItems.Contains(bag) &&
            ReferenceEquals(item.parentInventory, contents) && contents.childItems.Contains(item) && item.unitCount == 1 &&
            ReferenceEquals(old.parentInventory, contents) && contents.childItems.Contains(old) && old.unitCount == 1,
            "The confirmed cargo or original item moved or changed");
    }
}

public sealed class CargoRestartScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 8);
    private int step;
    private GameItem bag, old, loot;
    private GameGridInventory contents;

    public bool Tick(out string result)
    {
        result = null;
        QaCargoRestart.Need(Time.realtimeSinceStartup < deadline, "Cargo restart producer timed out at " + step);
        if (step == 0)
        {
            if (File.Exists(QaCargoRestart.ContextPath)) File.Delete(QaCargoRestart.ContextPath);
            if (start.Tick()) step++;
            return false;
        }
        var e = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (e?.storeInteractables?.activeInHierarchy != true) return false;
            QaTripCargo.SetMode(2);
            bag = QaTripCargo.New("backpack_medium", e.invElement);
            GeneralHelper.SetCustomName(bag, "QA cargo restart bag " + token);
            contents = bag.contentWindow?.childElement as GameGridInventory;
            QaCargoRestart.Need(contents != null, "Cargo restart bag has no inventory");
            old = QaTripCargo.New("wine_berry", contents);
            e.selectedBackpack = bag;
            PlayerStore.Instance.visitLeftTonight = 2;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND));
            step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !e.afterhourWindow.IsVisible() ||
                e.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            loot = QaTripCargo.New("wine_yeast", e.afterhourInventory);
            GeneralHelper.SetCustomName(loot, "QA cargo restart pickup " + token);
            QaTripCargo.Put(contents, loot, 1);
            QaCargoRestart.Need(ReferenceEquals(loot.parentInventory, contents), "Ground pickup did not enter the outing bag");
            MapUIManager.Instance.ReturnNow();
            QaCargoRestart.CargoType().GetField("autoUnloadPending", QaTripCargo.Instance).SetValue(QaCargoRestart.Cargo(), false);
            step++; return false;
        }
        if (step == 3)
        {
            if (MapUIManager.Instance.IsOutside || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
                bag.parentInventory == e.afterhourPocketSlotInvBackpack) return false;
            if (!ReferenceEquals(bag.parentInventory, e.invElement)) QaTripCargo.Put(e.invElement, bag, 1);
            QaCargoRestart.SameWorld(bag, loot, old, contents);
            object[] gains = QaTripCargo.Gains();
            QaCargoRestart.Need(gains.Length == 1 && (int)QaTripCargo.Field(gains[0], "id") == loot.uniqueId &&
                !(bool)QaTripCargo.Field(gains[0], "manual") && (int)QaTripCargo.Field(gains[0], "qty") == 1,
                "The new pickup was not confirmed before saving");
            string record = QaCargoRestart.Raw("tripCargo");
            QaCargoRestart.Need(!string.IsNullOrEmpty(record) && QaWholeStackRestart.Ready(QaWholeStackRestart.Transfer()),
                "The producer has no cargo record or has an unexpected pending transfer");
            var p = PlayerStore.Instance;
            var context = new QaCargoRestart.Context { run = p.runID, slot = p.saveSlotId, bag = bag.GetUniqueID(),
                item = loot.GetUniqueID(), old = old.GetUniqueID(), quantity = loot.unitCount, token = token,
                process = QaCargoRestart.ProcessStamp, record = record };
            p.SaveGame();
            File.WriteAllText(QaCargoRestart.ContextPath, JsonUtility.ToJson(context));
            QaCargoRestart.SameWorld(bag, loot, old, contents);
            result = "PASS confirmed, unprocessed trip cargo saved with the native save operation";
            return true;
        }
        return false;
    }
}

public sealed class CargoRestartReloadScenario
{
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private QaCargoRestart.Context context;
    private QaLoadSlot load;

    public bool Tick(out string result)
    {
        result = null;
        QaCargoRestart.Need(Time.realtimeSinceStartup < deadline, "Cargo restart consumer timed out");
        if (context == null)
        {
            QaCargoRestart.Need(File.Exists(QaCargoRestart.ContextPath), "Run CargoRestartScenario first in the same isolated profile");
            context = JsonUtility.FromJson<QaCargoRestart.Context>(File.ReadAllText(QaCargoRestart.ContextPath));
            QaCargoRestart.Need(context != null && context.version == 1 && context.process != QaCargoRestart.ProcessStamp &&
                context.slot >= 0 && Guid.TryParse(context.run, out Guid _) && context.item > 0 && context.bag > 0,
                "A separate process and valid cargo context are required");
            load = new QaLoadSlot(context.slot, Guid.Parse(context.run));
        }
        if (!load.Tick() || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
            EmporiumEntry.Instance.invWindow?.IsVisible() != true || StoreUIManager.Instance?.blackscreen?.gameObject.activeInHierarchy == true) return false;
        var e = EmporiumEntry.Instance;
        var bag = e.invElement.childItems.SingleOrDefault(value => value.uniqueId == context.bag);
        var contents = bag?.contentWindow?.childElement as GameGridInventory;
        var item = contents?.childItems.SingleOrDefault(value => value.uniqueId == context.item);
        var old = contents?.childItems.SingleOrDefault(value => value.uniqueId == context.old);
        QaCargoRestart.SameWorld(bag, item, old, contents);
        string journalBefore = QaCargoRestart.Raw("wholeStackTransfer");
        var issues = QaTripCargo.Issues();
        object[] gains = QaTripCargo.Gains();
        QaCargoRestart.Need(gains.Length == 1 && (bool)QaTripCargo.Field(gains[0], "manual") &&
            (bool)QaTripCargo.Field(QaTripCargo.Trip(), "uncertain") && issues.Any(issue =>
                (int)QaTripCargo.Field(issue, "ItemId") == item.uniqueId && (bool)QaTripCargo.Field(issue, "NeedsInspection") &&
                QaTripCargo.Field(issue, "Retry") == null && QaTripCargo.Field(issue, "Resolve") is Action),
            "Cold cargo was not retained as an actionable inspection issue");
        object cargo = QaCargoRestart.Cargo();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            cargo.GetType().GetMethod("RequestRetry", QaTripCargo.Instance).Invoke(cargo, null);
            cargo.GetType().GetMethod("Tick", QaTripCargo.Instance).Invoke(cargo, null);
        }
        QaCargoRestart.SameWorld(bag, item, old, contents);
        QaCargoRestart.Need(QaCargoRestart.Raw("tripCargo") == context.record && QaCargoRestart.Raw("wholeStackTransfer") == journalBefore &&
            QaTripCargo.Window()?.IsAlive != true, "Retrying uncertain cargo changed records, moved goods or opened an unload window");
        var pending = new QaCargoRestart.Journal { run = PlayerStore.Instance.runID, slot = PlayerStore.Instance.saveSlotId,
            source = item.uniqueId, amount = item.unitCount, sourceName = item.GetDisplayName(true), location = "QA pending cargo",
            sourceKey = "item:" + bag.uniqueId + ":contents:-1" };
        string pendingRaw = JsonUtility.ToJson(pending);
        const string unreadable = "{not-a-readable-trip-record";
        ModHelper.SetModData(QaCargoRestart.Mod, "wholeStackTransfer", pendingRaw);
        ModHelper.SetModData(QaCargoRestart.Mod, "tripCargo", unreadable);
        cargo.GetType().GetMethod("Reset", QaTripCargo.Instance).Invoke(cargo, null);
        object recordIssue = QaTripCargo.Issues().Single(issue => (string)QaTripCargo.Field(issue, "Key") == "f25:record");
        ((Action)QaTripCargo.Field(recordIssue, "Resolve"))();
        QaCargoRestart.Need(QaCargoRestart.Raw("tripCargo") == unreadable && QaCargoRestart.Raw("wholeStackTransfer") == pendingRaw,
            "A pending transfer must block resetting an unreadable cargo record");
        ModHelper.SetModData(QaCargoRestart.Mod, "tripCargo", context.record);
        ModHelper.SetModData(QaCargoRestart.Mod, "wholeStackTransfer", journalBefore);
        cargo.GetType().GetMethod("Reset", QaTripCargo.Instance).Invoke(cargo, null);
        File.Delete(QaCargoRestart.ContextPath);
        result = "PASS cold cargo remains manual, retries do not move it, unreadable recovery preserves pending journals";
        return true;
    }
}
