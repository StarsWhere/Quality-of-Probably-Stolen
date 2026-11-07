using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

// Exercises the native item entry and paged batch controls in a guarded QA save.
public sealed class LabelBatchPagesScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private readonly List<GameItem> goods = new List<GameItem>();
    private GameItem tool, sample;
    private QaSyntheticInput input;
    private int step, count;
    private float resultDeadline;
    private static int nextCell;
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window(string name) => CustomUIManager.Instance.GetWindow(Mod + "." + name);

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch { input?.Dispose(); input = null; throw; }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Paged label batch timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            var entry = EmporiumEntry.Instance;
            if (entry?.storeInteractables?.activeInHierarchy != true || ItemSelectHandler.current == null ||
                ItemContextHandler.current == null || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f22.enabled");
            TradeScenario.SetOptions(false, "f22.overwrite");
            entry.invElement.SetShape(24, 24);
            tool = New("labeler_blue"); sample = New("wine_yeast");
            Need(Put(entry.invElement, tool) && Put(entry.invElement, sample), "Could not place labeler fixtures");
            ItemSelectHandler.current.TryEquipItem(tool);
            Need(ItemSelectHandler.current.GetEquippedItem() == tool, "Could not equip the labeler");
            tool.Target(sample);
            var blue = LabelerBlueUIManager.Instance;
            Need(blue?.panel?.activeSelf == true, "Native label editor did not open");
            blue.inputField.SetTextWithoutNotify("QA batch pages"); blue.OnEditEnd("QA batch pages"); blue.CloseUI();

            var grids = new List<GameGridInventory> { entry.invElement };
            Debug.Log("[PLAYER-QA] Preparing batch-label candidates");
            for (int i = 0; i < 130; i++)
            {
                GameItem item = New("wine_yeast");
                if (!grids.Any(grid => Put(grid, item))) { item.Destroy(); throw new InvalidOperationException("Not enough visible storage for 130 distinct goods"); }
                goods.Add(item);
                if ((i + 1) % 25 == 0) Debug.Log("[PLAYER-QA] Batch-label candidates: " + (i + 1));
            }
            Debug.Log("[PLAYER-QA] Batch-label candidates ready");
            RoutingScenario.SetSelection((GameItemElement)goods[0], (GameItemElement)goods[1]);
            RoutingScenario.RunFeatureAction("f22.open");
            var assembly = ModLoader.Mods.First(value => value.Manifest?.ID == Mod).Instance.GetType().Assembly;
            var owner = assembly.GetType("ShopAssist.BatchLabeler", true).GetField("active", Any).GetValue(null);
            var candidates = (ICollection)owner.GetType().GetField("candidates", Any).GetValue(owner);
            var picked = ((IEnumerable)owner.GetType().GetField("picked", Any).GetValue(owner)).Cast<GameItem>().ToArray();
            Need(picked.Length == 2 && picked.Contains(goods[0]) && picked.Contains(goods[1]) &&
                candidates.Cast<GameItem>().Contains(goods[2]), "Batch action did not preselect the selection while retaining other visible candidates");
            Window("label").Close();

            OpenItemAction(goods[2], "ShopAssist_f22_open");
            picked = ((IEnumerable)owner.GetType().GetField("picked", Any).GetValue(owner)).Cast<GameItem>().ToArray();
            Need(picked.Length == 1 && picked[0] == goods[2], "Direct item action did not preselect its target");
            Window("label").Close();

            RoutingScenario.SetSelection();
            OpenItemAction(sample, "ShopAssist_f22_open");
            var label = Window("label");
            Need(label?.IsAlive == true && label.Get("select_all")?.button != null, "Direct item action did not open batch labeling");
            candidates = (ICollection)owner.GetType().GetField("candidates", Any).GetValue(owner);
            count = candidates.Count;
            Need(count >= 130, "Batch labeling truncated candidates at " + count);
            GameItem first = goods.First(item => QaItemUi.Hit(label.root, item) != null);
            QaItemUi.Click(label, first);
            label.Get("next").button.onClick.Invoke(); label = Window("label");
            GameItem second = goods.First(item => QaItemUi.Hit(label.root, item) != null);
            Need(!ReferenceEquals(first, second), "The second page did not change items");
            QaItemUi.Click(label, second);
            label.Get("previous").button.onClick.Invoke(); label = Window("label");
            Need(QaItemUi.Selected(label, first), "Returning to the first page lost its selection");
            label.Get("next").button.onClick.Invoke(); label = Window("label");
            Need(QaItemUi.Selected(label, second), "Returning to the second page lost its selection");
            label.Get("select_all").button.onClick.Invoke(); label = Window("label");
            Need(label.Get("selected").GetText() == ModHelper.GetLocalized(Mod, "f22.selected", count, count), "Select all did not cover every page");
            label.Get("clear").button.onClick.Invoke(); label = Window("label");
            Need(label.Get("selected").GetText() == ModHelper.GetLocalized(Mod, "f22.selected", 0, count) && !QaItemUi.Selected(label, second),
                "Clear did not remove selections across pages");
            label.Get("previous").button.onClick.Invoke(); label = Window("label");
            Need(!QaItemUi.Selected(label, first), "Clear left an earlier-page item selected");
            label.Get("select_all").button.onClick.Invoke(); label = Window("label");
            input = new QaSyntheticInput(keyboard: true) { Down = KeyCode.Escape };
            label.Get("apply").button.onClick.Invoke();
            step++; return false;
        }
        if (step == 2)
        {
            input.Down = KeyCode.None; input.Dispose(); input = null;
            var escape = StoreUIManager.Instance?.escapeUIManager;
            if (escape?.menuPanel?.activeInHierarchy == true) escape.CloseEscapeMenu();
            var assembly = ModLoader.Mods.First(value => value.Manifest?.ID == Mod).Instance.GetType().Assembly;
            var notice = assembly.GetType("ShopAssist.ShopNotice", true);
            object batch = notice.GetField("lastResult", Any).GetValue(null);
            Need(batch != null, "Cancelling the batch did not retain an operation result");
            var rows = ((IEnumerable)batch.GetType().GetField("Items", Any).GetValue(batch)).Cast<object>().ToArray();
            Need(rows.Length == count && rows.All(row => (string)row.GetType().GetField("Reason", Any).GetValue(row) == "operation.not_attempted"),
                "Cancellation did not report all unprocessed items");
            resultDeadline = Time.realtimeSinceStartup + 5f; step++; return false;
        }
        if (step == 3)
        {
            var action = QaItemUi.WindowAction(EmporiumEntry.Instance.invWindow, "result");
            if (action == null || action.isDisabled)
            {
                Need(Time.realtimeSinceStartup < resultDeadline, "Recent result action unavailable: " +
                    (action == null ? "missing" : "disabled") + ", typing=" + StoreUIManager.IsAnyTyping() +
                    ", escape=" + (StoreUIManager.Instance?.escapeUIManager?.menuPanel?.activeInHierarchy == true));
                return false;
            }
            action.Select();
            var details = Window("operation");
            Need(details?.IsAlive == true && details.root.GetComponentsInChildren<TMP_Text>(true)
                .Any(text => text.text.Contains(ModHelper.GetLocalized(Mod, "operation.not_attempted"))),
                "The direct result action did not show unprocessed items");
            result = "PASS selected-item preselection, native batch entries, over 128 candidates, cross-page selection, select all, clear and cancelled-item details";
            return true;
        }
        return false;
    }

    private static GameItem New(string id)
    {
        GameItem item = DirectoryMaster.Item(id);
        Need(item != null, "Missing fixture " + id);
        GeneralHelper.SetItemOwned(item, true); return item;
    }

    private static bool Put(GameGridInventory grid, GameItem item)
    {
        var shape = new GridShapeBuilder(item.modifiedShape);
        int width = grid.inventoryShape.width;
        for (int cell = nextCell; cell < width * grid.inventoryShape.height; cell++)
        {
            int x = cell % width, y = cell / width;
            if (x + shape.globalWidth > width || y + shape.globalHeight > grid.inventoryShape.height) continue;
            shape.SetPosition(x, y);
            var marker = grid.TryInventorySlot(item, item.unitCount, shape);
            if (marker?.inventory == grid && marker.targetItem == null && marker.numTransfer >= item.unitCount &&
                marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid)
            { nextCell = cell + 1; return true; }
        }
        return false;
    }

    private static void OpenItemAction(GameItem item, string nodeName)
    {
        var handler = ItemContextHandler.current;
        var type = typeof(ItemContextHandler);
        var menu = (PixelWindow)type.GetField("contextWindow", Any).GetValue(handler);
        type.GetField("currentInventory", Any).SetValue(handler, null);
        type.GetField("currentItem", Any).SetValue(handler, item);
        type.GetMethod("UpdateConditions", Any).Invoke(handler, null);
        type.GetMethod("EqualizeButtonWidths", Any).Invoke(handler, null);
        menu.Validate(); menu.Show(new Vector2(Screen.width / 2, Screen.height / 2), Vector2.zero);
        type.GetMethod("OnEventPress", Any).Invoke(handler, new object[] { new HashSet<KeyCode>() });
        var node = QaItemUi.Descendant(menu.handler.transform, nodeName);
        var action = node?.GetComponent<TreeNodeRender>()?.element as RichTextElement;
        Need(action != null, "Native item menu has no batch-label action");
        type.GetMethod("TriggerButton", Any).Invoke(handler, new object[] { action });
    }
}
