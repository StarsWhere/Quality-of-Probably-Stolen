using System;
using System.Globalization;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

internal static class DailyDesktopFixture
{
    internal const string Mod = "starswhere.shopassist";
    internal const string GroupsId = "shopassist.f13.groups";
    internal static string Context => Path.Combine(Application.persistentDataPath, "qa-f13-desktop.txt");

    internal static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    internal static GameItem New(string id)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing native fixture " + id);
        GeneralHelper.SetItemOwned(item, true); return item;
    }
    internal static void Put(GameInventory grid, GameItem item)
    {
        var slot = grid.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == grid && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid, "Could not place " + item.identifier);
    }
    internal static CustomUIWindow OpenGroups()
    {
        StoreUIManager.Instance.gameOptionUIManager.OpenUI();
        RoutingScenario.RunFeatureAction("f13.groups");
        StoreUIManager.Instance.gameOptionUIManager.CloseUI();
        var groups = CustomUIManager.Instance.GetWindow(GroupsId);
        Need(groups?.IsVisible == true, "F13 group control did not open"); return groups;
    }
    internal static void CloseCalendar()
    {
        if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
    }
    internal static bool Near(Vector2 a, Vector2 b) => (a - b).sqrMagnitude <= 0.04f;
}

// The paired scenarios run in separate QA Player processes.
public sealed class DailyDesktopSaveScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private int step;
    private float since = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        DailyDesktopFixture.Need(Time.realtimeSinceStartup - since < 60f, "F13 save QA timed out at step " + step);
        if (step == 0) { if (start.Tick()) { step++; since = Time.realtimeSinceStartup; } return false; }
        if (StoreUIManager.Instance?.gameOptionUIManager == null || ItemMouseDoubleClickHandler.current == null) return false;
        DailyDesktopFixture.CloseCalendar();
        TradeScenario.SetOptions(true, "f13.enabled");
        var inventory = EmporiumEntry.Instance.invElement;
        var coordinator = DailyDesktopFixture.New("storage_coordinator");
        var first = DailyDesktopFixture.New("fanny_pack"); var second = DailyDesktopFixture.New("satchel");
        DailyDesktopFixture.Put(inventory, coordinator); DailyDesktopFixture.Put(inventory, first); DailyDesktopFixture.Put(inventory, second);
        DailyDesktopFixture.Need(ReferenceEquals(MachineStorageCoordinator.FindCoordinatorInReach(), coordinator), "Native coordinator is not in reach");
        ItemMouseDoubleClickHandler.current.OpenContentAction(first, new Vector2(100, 100));
        ItemMouseDoubleClickHandler.current.OpenContentAction(second, new Vector2(320, 180));
        DailyDesktopFixture.Need(first.contentWindow.IsVisible() && second.contentWindow.IsVisible(), "Native fixture windows did not open");
        first.contentWindow.rectTransform.anchoredPosition = new Vector2(40, -40);
        second.contentWindow.rectTransform.anchoredPosition = new Vector2(280, -160);
        first.contentWindow.FixBounds(); second.contentWindow.FixBounds();
        Vector2 a = first.contentWindow.rectTransform.anchoredPosition, b = second.contentWindow.rectTransform.anchoredPosition;
        string name = "QA cold layout " + Guid.NewGuid().ToString("N").Substring(0, 8);
        var groups = DailyDesktopFixture.OpenGroups();
        groups.Get("name").SetString(name, true); groups.Get("save").button.onClick.Invoke();
        var preset = MachineStorageCoordinator.LoadPresets(coordinator).SingleOrDefault(value => value.name == name);
        DailyDesktopFixture.Need(preset != null && preset.itemIds.Contains(first.uniqueId) && preset.itemIds.Contains(second.uniqueId),
            "F13 did not save the native coordinator members");
        DailyDesktopFixture.Need(!string.IsNullOrEmpty(ModHelper.GetModData(DailyDesktopFixture.Mod, "f13.layouts", null)), "F13 layout data missing");
        var player = PlayerStore.Instance;
        DailyDesktopFixture.Need(Guid.TryParse(player.runID, out Guid run) && run != Guid.Empty, "Isolated run ID unavailable");
        player.SaveGame();
        string[] values = { run.ToString("N"), player.saveSlotId.ToString(CultureInfo.InvariantCulture),
            coordinator.uniqueId.ToString(CultureInfo.InvariantCulture), first.uniqueId.ToString(CultureInfo.InvariantCulture),
            second.uniqueId.ToString(CultureInfo.InvariantCulture), a.x.ToString("R", CultureInfo.InvariantCulture),
            a.y.ToString("R", CultureInfo.InvariantCulture), b.x.ToString("R", CultureInfo.InvariantCulture),
            b.y.ToString("R", CultureInfo.InvariantCulture), name };
        File.WriteAllText(DailyDesktopFixture.Context, string.Join("|", values));
        result = "PASS F13 native coordinator group and window positions saved for cold reload";
        return true;
    }
}

public sealed class DailyDesktopReloadScenario
{
    private QaLoadSlot load;
    private string[] data;
    private int step, frame;
    private float since = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        DailyDesktopFixture.Need(Time.realtimeSinceStartup - since < 60f, "F13 reload QA timed out at step " + step);
        if (step == 0)
        {
            data = File.ReadAllText(DailyDesktopFixture.Context).Trim().Split('|');
            DailyDesktopFixture.Need(data.Length == 10 && Guid.TryParseExact(data[0], "N", out Guid run) &&
                int.TryParse(data[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot), "Invalid F13 isolated save context");
            load = new QaLoadSlot(int.Parse(data[1], CultureInfo.InvariantCulture), Guid.ParseExact(data[0], "N"));
            step++; return false;
        }
        if (step == 1)
        {
            if (!load.Tick() || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                StoreUIManager.Instance?.gameOptionUIManager == null || ItemMouseDoubleClickHandler.current == null) return false;
            DailyDesktopFixture.CloseCalendar(); frame = Time.frameCount + 5; step++; return false;
        }
        if (Time.frameCount < frame) return false;
        int Id(int index) => int.Parse(data[index], CultureInfo.InvariantCulture);
        float Number(int index) => float.Parse(data[index], CultureInfo.InvariantCulture);
        var items = EmporiumEntry.Instance.invElement.childItems;
        var coordinator = items.SingleOrDefault(item => item.uniqueId == Id(2));
        var first = items.SingleOrDefault(item => item.uniqueId == Id(3));
        var second = items.SingleOrDefault(item => item.uniqueId == Id(4));
        DailyDesktopFixture.Need(coordinator != null && first?.contentWindow != null && second?.contentWindow != null &&
            ReferenceEquals(MachineStorageCoordinator.FindCoordinatorInReach(), coordinator), "Cold reload lost a coordinator or group member");
        var preset = MachineStorageCoordinator.LoadPresets(coordinator).SingleOrDefault(value => value.name == data[9]);
        DailyDesktopFixture.Need(preset != null && preset.itemIds.Contains(first.uniqueId) && preset.itemIds.Contains(second.uniqueId),
            "Cold reload lost the native coordinator group");
        if (first.contentWindow.IsVisible()) WindowsHandler.CloseWindow(first.contentWindow);
        if (second.contentWindow.IsVisible()) WindowsHandler.CloseWindow(second.contentWindow);
        var groups = DailyDesktopFixture.OpenGroups();
        string restoreLabel = ModHelper.GetLocalized(DailyDesktopFixture.Mod, "f13.restore");
        var restore = groups.root.GetComponentsInChildren<Button>(true).Where(button => button.gameObject.activeInHierarchy &&
            button.GetComponentInChildren<TMP_Text>(true)?.text == restoreLabel).ToArray();
        DailyDesktopFixture.Need(restore.Length == 1, "Cannot identify the saved group's restore control");
        restore[0].onClick.Invoke();
        DailyDesktopFixture.Need(first.contentWindow.IsVisible() && second.contentWindow.IsVisible() &&
            DailyDesktopFixture.Near(first.contentWindow.rectTransform.anchoredPosition, new Vector2(Number(5), Number(6))) &&
            DailyDesktopFixture.Near(second.contentWindow.rectTransform.anchoredPosition, new Vector2(Number(7), Number(8))),
            "Cold reload did not restore both grouped windows to their saved positions");
        result = "PASS F13 native coordinator identities and layout restored after cold reload";
        return true;
    }
}
