using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

// Exercises the actual routing preview and native stack transfers in a fresh run.
public sealed class RoutingPartialScenario
{
    private const string Mod = "starswhere.shopassist", Preview = Mod + ".route_preview";
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly float deadline = Time.realtimeSinceStartup + 100f;
    private bool directory;
    private int step;
    private GameItemElement item, a, b;
    private GameItem bagA, bagB;
    private GameGridInventory gridA, gridB, firstGrid;
    private object locationA, locationB;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Partial routing QA timed out at step " + step);
        if (!directory)
        {
            if (!QaWholeStackRestart.PrepareNativeDirectory()) return false;
            directory = true;
        }
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.invWindow?.IsVisible() != true || ItemMouseDoubleClickHandler.current == null ||
                ItemMultiSelectHandler.current == null || StoreUIManager.Instance?.gameOptionUIManager == null ||
                StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            Prepare();
            RoutingScenario.CreateCategoryRule(bagA, "MATERIAL");
            RoutingScenario.CreateCategoryRule(bagB, "MATERIAL");
            OpenPreview();
            CheckPreview();
            b.SetStack(97);
            RoutingScenario.Button(RoutingScenario.Window(Preview), "f08.apply").onClick.Invoke();
            Need(ReferenceEquals(item.parentInventory, EmporiumEntry.Instance.invElement) && item.unitCount == 7 &&
                a.unitCount == 97 && b.unitCount == 97, "A stale multi-destination preview changed goods");
            b.SetStack(96);
            OpenPreview();
            step++; return false;
        }
        if (step == 2)
        {
            CheckPreview();
            RoutingScenario.Button(RoutingScenario.Window(Preview), "f08.apply").onClick.Invoke();
            Need(a.parentInventory == gridA && b.parentInventory == gridB && a.unitCount == 99 && b.unitCount == 99 &&
                ReferenceEquals(item.parentInventory, firstGrid) && item.unitCount == 2 && firstGrid.childItems.Contains(item) &&
                gridA.childItems.Concat(gridB.childItems).Where(value => value.identifier == QaWholeStackRestart.ItemId)
                    .Sum(value => value.unitCount) == 200 && QaWholeStackRestart.Ready(QaWholeStackRestart.Transfer()),
                "Confirmed routing did not conserve the two partial merges and the original remaining stack");
            result = "PASS F08 native partial merges across two storage areas, stale preview rejection and conserved remainder";
            return true;
        }
        throw new InvalidOperationException("Unknown partial routing QA step");
    }

    private void Prepare()
    {
        TradeScenario.SetOptions(true, "f07.enabled", "f08.enabled", "f09.enabled");
        var options = StoreUIManager.Instance.gameOptionUIManager;
        options.OpenUI();
        var mode = CustomUIManager.Instance.GetWindow("game_options").Get(Mod + ":f09.mode");
        Need(mode?.dropdown != null, "Preview mode control missing");
        mode.SetIndex(0, true);
        CustomUIManager.Instance.GetWindow("game_options").Get("apply").button.onClick.Invoke();
        options.CloseUI();

        bagA = QaWholeStackRestart.New("backpack_medium"); bagB = QaWholeStackRestart.New("backpack_medium");
        GeneralHelper.SetCustomName(bagA, "QA partial A " + token);
        GeneralHelper.SetCustomName(bagB, "QA partial B " + token);
        var stock = EmporiumEntry.Instance.invElement;
        QaWholeStackRestart.Put(bagA, stock); QaWholeStackRestart.Put(bagB, stock);
        gridA = bagA.contentWindow?.childElement as GameGridInventory;
        gridB = bagB.contentWindow?.childElement as GameGridInventory;
        Need(gridA != null && gridB != null, "Partial routing fixture grids unavailable");
        item = NewStack(7); a = NewStack(97); b = NewStack(96);
        QaWholeStackRestart.Put(item, stock); QaWholeStackRestart.Put(a, gridA); QaWholeStackRestart.Put(b, gridB);
        Need(a.GetStackSlot(item)?.numTransfer == 2 && b.GetStackSlot(item)?.numTransfer == 3,
            "Native stack capacities differ from the scenario");
        Assembly assembly = QaWholeStackRestart.Transfer().Assembly;
        locationA = QaWholeStackRestart.Location(assembly, gridA); locationB = QaWholeStackRestart.Location(assembly, gridB);
        firstGrid = string.CompareOrdinal(QaWholeStackRestart.Key(locationA), QaWholeStackRestart.Key(locationB)) < 0 ? gridA : gridB;
    }

    private static GameItemElement NewStack(int count)
    {
        var item = QaWholeStackRestart.New(QaWholeStackRestart.ItemId, count) as GameItemElement;
        Need(item != null, "Native stack fixture is not an ordinary item");
        item.itemTypes.Add("MATERIAL");
        return item;
    }

    private void OpenPreview()
    {
        RoutingScenario.SetSelection(item);
        RoutingScenario.RunFeatureAction("f08.open");
        Need(CustomUIManager.Instance.GetWindow(Preview) != null, "Partial routing preview did not open");
    }

    private void CheckPreview()
    {
        var window = RoutingScenario.Window(Preview);
        var labels = window.root.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text).ToArray();
        string nameA = (string)QaWholeStackRestart.Field(locationA, "Name");
        string nameB = (string)QaWholeStackRestart.Field(locationB, "Name");
        int amountA = 2 + (firstGrid == gridA ? 2 : 0), amountB = 3 + (firstGrid == gridB ? 2 : 0);
        Need(labels.Contains(ModHelper.GetLocalized(Mod, "f09.target", nameA, amountA)) &&
            labels.Contains(ModHelper.GetLocalized(Mod, "f09.target", nameB, amountB)) &&
            RoutingScenario.Button(window, "f08.apply")?.interactable == true && item.unitCount == 7 && a.unitCount == 97 && b.unitCount == 96,
            "Preview did not show both exact destination quantities without changing goods");
        Type routes = QaWholeStackRestart.Transfer().Assembly.GetType("ShopAssist.InventoryRoutes", true);
        object owner = routes.GetField("current", Flags).GetValue(null);
        object plan = routes.GetField("plan", Flags).GetValue(owner);
        var moves = ((IEnumerable)QaWholeStackRestart.Field(plan, "Moves")).Cast<object>().ToArray();
        Need(moves.Length == 3, "Preview did not produce two merges and one whole remainder move");
        object[] transfers = moves.Select(move => QaWholeStackRestart.Field(move, "Transfer")).ToArray();
        int[] amounts = transfers.Select(move => (int)QaWholeStackRestart.Field(move, "Amount")).ToArray();
        int[] sourceCounts = transfers.Select(move => (int)QaWholeStackRestart.Field(move, "SourceBefore")).ToArray();
        Need(amounts[0] == (firstGrid == gridA ? 2 : 3) && amounts[1] == (firstGrid == gridA ? 3 : 2) && amounts[2] == 2 &&
            sourceCounts.SequenceEqual(new[] { 7, 7 - amounts[0], 2 }) &&
            QaWholeStackRestart.Field(transfers[0], "Stack") != null && QaWholeStackRestart.Field(transfers[1], "Stack") != null &&
            QaWholeStackRestart.Field(transfers[2], "Stack") == null &&
            ReferenceEquals(QaWholeStackRestart.Field(transfers[2], "To"), firstGrid),
            "Previewed native moves have incorrect source counts or destinations");
    }

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
