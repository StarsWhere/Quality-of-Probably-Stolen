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
            TradeScenario.SetOptions(true, "f25.enabled");
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
            MapUIManager.Instance.ReturnNow(); step++; return false;
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
            RoutingScenario.RunFeatureAction("f25.open");
            var window = QaTripCargo.Window();
            QaCargoRestart.Need(window?.Get("unload")?.button != null && QaCargoRestart.Selected(loot),
                "The confirmed pickup was not selected before saving");
            string record = QaCargoRestart.Raw("tripCargo");
            QaCargoRestart.Need(!string.IsNullOrEmpty(record) && QaWholeStackRestart.Ready(QaWholeStackRestart.Transfer()),
                "The producer has no cargo record or has an unexpected pending transfer");
            window.Close();
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
    private int step, markerFrame;
    private float markerDeadline;
    private GameItem bag, item, old;
    private GameGridInventory contents;
    private string journalBefore, attentionBefore, legacyRaw, malformedRaw, pendingRaw;

    private void CheckRestarted(string previous)
    {
        object restarted = QaTripCargo.Trip();
        QaCargoRestart.Need(restarted != null && (int)QaTripCargo.Field(restarted, "version") == 3 &&
            (bool)QaTripCargo.Field(restarted, "started") && (bool)QaTripCargo.Field(restarted, "returned") &&
            (bool)QaTripCargo.Field(restarted, "uncertain") && QaTripCargo.Gains().Length == 0 &&
            QaTripCargo.Text(QaTripCargo.Window()).Contains(ModHelper.GetLocalized(QaCargoRestart.Mod, "f25.uncertain")) &&
            QaCargoRestart.Raw("tripCargo") != previous && QaCargoRestart.Raw("wholeStackTransfer") == pendingRaw &&
            QaCargoRestart.Raw("attention") == attentionBefore && !QaWholeStackRestart.Ready(QaWholeStackRestart.Transfer()),
            "Recovery inferred a completed move or altered a separate pending issue");
        QaCargoRestart.SameWorld(bag, item, old, contents);
    }

    public bool Tick(out string result)
    {
        result = null;
        QaCargoRestart.Need(Time.realtimeSinceStartup < deadline, "Cargo restart consumer timed out at " + step);
        if (context == null)
        {
            QaCargoRestart.Need(File.Exists(QaCargoRestart.ContextPath), "Run CargoRestartScenario first in the same isolated profile");
            context = JsonUtility.FromJson<QaCargoRestart.Context>(File.ReadAllText(QaCargoRestart.ContextPath));
            QaCargoRestart.Need(context != null && context.version == 1 && Guid.TryParse(context.run, out Guid _) &&
                context.slot >= 0 && context.bag > 0 && context.item > 0 && context.old > 0 && context.quantity == 1 &&
                !string.IsNullOrEmpty(context.record) && context.token?.Length == 8 && context.process?.Length == 32 &&
                context.process != QaCargoRestart.ProcessStamp, "The restart context is invalid or still in the original process");
            load = new QaLoadSlot(context.slot, Guid.Parse(context.run));
        }
        if (step == 0)
        {
            if (!load.Tick() || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                EmporiumEntry.Instance.invWindow?.IsVisible() != true || ItemMouseDoubleClickHandler.current == null ||
                StoreUIManager.Instance?.blackscreen?.gameObject.activeInHierarchy == true) return false;
            var e = EmporiumEntry.Instance;
            bag = e.invElement.childItems.SingleOrDefault(value => value.uniqueId == context.bag);
            contents = bag?.contentWindow?.childElement as GameGridInventory;
            item = contents?.childItems.SingleOrDefault(value => value.uniqueId == context.item);
            old = contents?.childItems.SingleOrDefault(value => value.uniqueId == context.old);
            QaCargoRestart.SameWorld(bag, item, old, contents);
            QaCargoRestart.Need(GeneralHelper.GetCustomName(item) == "QA cargo restart pickup " + context.token &&
                QaCargoRestart.Raw("tripCargo") == context.record && QaWholeStackRestart.Ready(QaWholeStackRestart.Transfer()),
                "Native reload lost the saved identity, record or transfer state");
            journalBefore = QaCargoRestart.Raw("wholeStackTransfer");
            RoutingScenario.RunFeatureAction("f25.open");
            var window = QaTripCargo.Window();
            object[] gains = QaTripCargo.Gains();
            string text = QaTripCargo.Text(window);
            QaCargoRestart.Need(gains.Length == 1 && (int)QaTripCargo.Field(gains[0], "id") == item.uniqueId &&
                (bool)QaTripCargo.Field(gains[0], "manual") && (bool)QaTripCargo.Field(QaTripCargo.Trip(), "uncertain") &&
                text.Contains(item.GetDisplayName(true)) && text.Contains(ModHelper.GetLocalized(QaCargoRestart.Mod, "f25.check")) &&
                text.Contains(ModHelper.GetLocalized(QaCargoRestart.Mod, "f25.uncertain")) &&
                QaItemUi.Hit(window.root, item) == null && QaTripCargo.Button(window, "f25.locate") != null &&
                QaTripCargo.Button(window, "f25.handled") != null,
                "Reloaded cargo did not enter the visible manual-review state: " + text);
            var selection = QaTripCargo.Field(QaCargoRestart.Cargo(), "selected") as IEnumerable;
            QaCargoRestart.Need(selection != null && !selection.Cast<object>().Any(value => (int)value == item.uniqueId),
                "Reloaded uncertain cargo remained selected");
            QaCargoRestart.Need(window.Get("unload")?.button != null, "Unload control is missing");
            window.Get("unload").button.onClick.Invoke();
            window.Get("unload").button.onClick.Invoke();
            QaCargoRestart.SameWorld(bag, item, old, contents);
            QaCargoRestart.Need(QaCargoRestart.Raw("wholeStackTransfer") == journalBefore &&
                QaCargoRestart.Raw("tripCargo") == context.record && QaTripCargo.Gains().Length == 1,
                "Repeated unload attempts moved or re-recorded the uncertain pickup");
            Button locate = QaTripCargo.Button(window, "f25.locate");
            locate.onClick.Invoke();
            Type locator = QaCargoRestart.CargoType().Assembly.GetType("ShopAssist.ItemLocator", true);
            object marked = locator.GetField("marked", QaCargoRestart.Static)?.GetValue(null);
            QaCargoRestart.Need(ReferenceEquals(marked, item) && bag.contentWindow.IsVisible(),
                "The cargo Locate action did not open its container and mark the same item");
            markerFrame = Time.frameCount; markerDeadline = Time.unscaledTime + 2f;
            step++; return false;
        }
        if (step == 1)
        {
            if (Time.frameCount <= markerFrame) return false;
            Canvas.ForceUpdateCanvases();
            var outline = (item as GameItemElement)?.rotator?.transform.Find("ShopAssist_LocateOutline");
            bool visible = outline != null && outline.gameObject.activeInHierarchy && outline.GetComponentsInChildren<Image>(true)
                .Any(image => image.isActiveAndEnabled && image.color.a > 0 && image.rectTransform.rect.width > 0 && image.rectTransform.rect.height > 0);
            if (!visible && Time.unscaledTime < markerDeadline) return false;
            QaCargoRestart.Need(visible, "The located cargo did not show its visible outline");
            QaCargoRestart.SameWorld(bag, item, old, contents);
            QaCargoRestart.Need(QaCargoRestart.Raw("tripCargo") == context.record, "Locating changed the cargo record");

            var p = PlayerStore.Instance;
            var pending = new QaCargoRestart.Journal { run = p.runID, slot = p.saveSlotId, source = item.uniqueId,
                amount = item.unitCount, sourceName = item.GetDisplayName(true), location = "QA pending transfer",
                sourceKey = "item:" + bag.uniqueId + ":contents:-1" };
            pendingRaw = JsonUtility.ToJson(pending);
            ModHelper.SetModData(QaCargoRestart.Mod, "wholeStackTransfer", pendingRaw);
            QaCargoRestart.Need(!QaWholeStackRestart.Ready(QaWholeStackRestart.Transfer()), "Pending transfer fixture is invalid");
            attentionBefore = QaCargoRestart.Raw("attention");
            legacyRaw = "{\"version\":1,\"bag\":" + bag.uniqueId + ",\"returned\":true,\"outward\":[],\"gains\":[{\"id\":" +
                item.uniqueId + ",\"qty\":1,\"type\":\"wine_yeast\",\"manual\":false}],\"dest\":\"root:stock\"}";
            malformedRaw = "{not-a-readable-trip-record";
            ModHelper.SetModData(QaCargoRestart.Mod, "tripCargo", legacyRaw);
            QaCargoRestart.CargoType().GetMethod("Reset", QaTripCargo.Instance).Invoke(QaCargoRestart.Cargo(), null);
            RoutingScenario.RunFeatureAction("f25.open");
            var recovery = QaTripCargo.Window();
            QaCargoRestart.Need(recovery?.Get("restart")?.button?.interactable == true &&
                QaTripCargo.Text(recovery).Contains(ModHelper.GetLocalized(QaCargoRestart.Mod, "f25.unreadable")) &&
                QaCargoRestart.Raw("tripCargo") == legacyRaw && QaCargoRestart.Raw("wholeStackTransfer") == pendingRaw,
                "The legacy record was parsed or changed before explicit recovery");
            Button stale = recovery.Get("restart").button;
            ModHelper.SetModData(QaCargoRestart.Mod, "tripCargo", malformedRaw);
            stale.onClick.Invoke();
            QaCargoRestart.Need(QaCargoRestart.Raw("tripCargo") == malformedRaw && QaCargoRestart.Raw("wholeStackTransfer") == pendingRaw,
                "A stale recovery button overwrote a changed record or pending transfer");
            ModHelper.SetModData(QaCargoRestart.Mod, "tripCargo", legacyRaw);
            RoutingScenario.RunFeatureAction("f25.open");
            recovery = QaTripCargo.Window();
            QaCargoRestart.Need(recovery?.Get("restart")?.button?.interactable == true &&
                QaTripCargo.Text(recovery).Contains(ModHelper.GetLocalized(QaCargoRestart.Mod, "f25.unreadable")),
                "The legacy recovery entry was lost after the stale action");
            recovery.Get("restart").button.onClick.Invoke();
            CheckRestarted(legacyRaw);
            ModHelper.SetModData(QaCargoRestart.Mod, "tripCargo", malformedRaw);
            QaCargoRestart.CargoType().GetMethod("Reset", QaTripCargo.Instance).Invoke(QaCargoRestart.Cargo(), null);
            RoutingScenario.RunFeatureAction("f25.open");
            recovery = QaTripCargo.Window();
            QaCargoRestart.Need(recovery?.Get("restart")?.button?.interactable == true &&
                QaTripCargo.Text(recovery).Contains(ModHelper.GetLocalized(QaCargoRestart.Mod, "f25.unreadable")),
                "Malformed data did not use the same recovery entry");
            recovery.Get("restart").button.onClick.Invoke();
            CheckRestarted(malformedRaw);
            File.Delete(QaCargoRestart.ContextPath);
            result = "PASS cold reload requires review and locates cargo; legacy and malformed recovery preserve pending transfers";
            return true;
        }
        throw new InvalidOperationException("Unknown cargo restart consumer step");
    }
}
