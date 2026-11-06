using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

// Native fixtures and controls in an isolated QA new game.
public sealed class LayoutScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem machine, fixedItem, first, second, bag;
    private GameGridInventory bay, bagGrid;
    private GridShape fixedShape;
    private int step;
    private bool resetContext;
    private float deadline = Time.realtimeSinceStartup + 90;

    public bool Tick(out string result)
    {
        result = null;
        Require(Time.realtimeSinceStartup < deadline, "F12 scenario timed out at step " + step);
        if (step == 0)
        {
            if (!resetContext) { File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-layout-run.txt"), "pending"); resetContext = true; }
            if (!start.Tick()) return false;
            step++; return false;
        }
        if (step == 1)
        {
            if (EmporiumEntry.Instance == null || StoreUIManager.Instance?.gameOptionUIManager == null ||
                EmporiumEntry.Instance.storeInteractables == null || !EmporiumEntry.Instance.storeInteractables.activeInHierarchy ||
                ItemMouseDoubleClickHandler.current == null) return false;
            var root = EmporiumEntry.Instance.invElement;
            machine = New("hydroponic"); fixedItem = New("system_module_quality");
            first = New("system_module_performance"); second = New("system_module_efficiancy"); bag = New("fanny_pack");
            Put(root, machine); Put(root, fixedItem); Put(root, first); Put(root, second); Put(root, bag);
            bagGrid = bag.contentWindow.childElement as GameGridInventory;
            Require(bagGrid != null, "Native source container is unavailable");
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(300, 160));
            bay = Bay(machine);
            ModuleEffectHelper.AddEffectToModule(fixedItem, "MODULE_EFFECT_TOP_MOUNT");
            Put(bay, fixedItem);
            ModuleHelper.SetModuleStuck(fixedItem);
            fixedShape = fixedItem.modifiedShape.Clone();
            machine.contentWindow.ToFront();
            var options = Options();
            options.Get(Mod + ":f10.enabled").SetBool(true, true);
            options.Get(Mod + ":f12.enabled").SetBool(true, true);
            options.Get(Mod + ":f15.enabled").SetBool(false, true);
            options.Get("apply").button.onClick.Invoke();
            StoreUIManager.Instance.gameOptionUIManager.CloseUI();
            RoutingScenario.OpenOptionAction("f10.open");
            step++; return false;
        }
        if (step == 2)
        {
            var browser = Browser();
            Select(browser, first);
            browser.Get("layout").button.onClick.Invoke();
            var layout = Layout();
            layout.Get("preview").button.onClick.Invoke();
            layout = Layout();
            Require(layout.Get("apply")?.button != null, "F12 did not produce a preview: " + Text(layout));
            Require(ReferenceEquals(first.parentInventory, EmporiumEntry.Instance.invElement) &&
                ReferenceEquals(fixedItem.parentInventory, bay) && Same(fixedShape, fixedItem.modifiedShape),
                "F12 preview changed an actual item");
            layout.Get("apply").button.onClick.Invoke();
            Require(ReferenceEquals(first.parentInventory, bay) && ReferenceEquals(fixedItem.parentInventory, bay) &&
                Same(fixedShape, fixedItem.modifiedShape), "F12 did not install selected item while preserving stuck item");
            layout = Layout();
            Require(Text(layout).Contains(ModHelper.GetLocalized(Mod, "f12.applied")), "F12 did not confirm successful native totals: " + Text(layout));
            step++; return false;
        }
        if (step == 3)
        {
            var browser = Browser();
            Select(browser, second);
            var layout = Layout();
            // Efficiency is the second objective.
            layout.Get("objective").SetIndex(1, true);
            layout = Layout();
            layout.Get("preview").button.onClick.Invoke();
            layout = Layout();
            Require(layout.Get("apply")?.button != null, "F12 did not preview the second module: " + Text(layout));
            Put(bagGrid, second);
            layout.Get("apply").button.onClick.Invoke();
            layout = Layout();
            Require(Text(layout).Contains(ModHelper.GetLocalized(Mod, "f12.changed")) && ReferenceEquals(second.parentInventory, bagGrid) &&
                ReferenceEquals(first.parentInventory, bay) && Same(fixedShape, fixedItem.modifiedShape),
                "F12 accepted a stale preview or altered an existing module");
            int machineId = machine.GetUniqueID(), fixedId = fixedItem.GetUniqueID(), firstId = first.GetUniqueID();
            var p = PlayerStore.Instance;
            string context = string.Join("|", "1", p.runID, p.saveSlotId, machineId, fixedId, firstId,
                fixedShape.minX, fixedShape.minY, fixedShape.orientation, fixedShape.flipped ? 1 : 0,
                first.modifiedShape.minX, first.modifiedShape.minY, first.modifiedShape.orientation, first.modifiedShape.flipped ? 1 : 0,
                machine.GetTagReadonly("TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT").GetInt(),
                machine.GetTagReadonly("TOTAL_PERCENTAGE_POWER_BONUS_INT").GetInt(),
                machine.GetTagReadonly("TOTAL_PERCENTAGE_QUALITY_BONUS_INT").GetInt());
            File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-layout-run.txt"), context);
            p.SaveGame();
            result = "PASS F12 native preview and apply on hydroponic bay; stuck item retained; stale source rejected; saved QA run";
            return true;
        }
        throw new InvalidOperationException("Unknown F12 step");
    }

    private static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private static GameItem New(string id)
    {
        GameItem item = DirectoryMaster.Item(id);
        Require(item != null, "Missing QA item " + id); GeneralHelper.SetItemOwned(item); return item;
    }
    private static void Put(GameInventory target, GameItem item)
    {
        SlotMarker slot = target.TryFindOneValidInventorySlot(item);
        Require(slot != null && slot.targetItem == null && ReferenceEquals(slot.inventory, target) &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, target), "Could not place " + item.identifier);
    }
    private static GameGridInventory Bay(GameItem item)
    {
        var row = item.contentWindow.childElement as MachineryPanelRow;
        var panel = row?.Panels.SingleOrDefault(p => p.role == "module");
        var nodes = panel == null ? null : MachineryContainerUtils.Collect(panel.content);
        Require(nodes?.Count == 1 && nodes[0] is GameGridInventory, "Native module bay missing");
        return (GameGridInventory)nodes[0];
    }
    private static CustomUIWindow Options() { StoreUIManager.Instance.gameOptionUIManager.OpenUI(); return CustomUIManager.Instance.GetWindow("game_options"); }
    private static CustomUIWindow Browser() { var w = CustomUIManager.Instance.GetWindow(Mod + ".f10"); Require(w?.IsAlive == true, "F10 browser missing"); return w; }
    private static CustomUIWindow Layout() { var w = CustomUIManager.Instance.GetWindow(Mod + ".f10"); Require(w?.IsAlive == true, "Module workbench missing"); return w; }
    private static string Text(CustomUIWindow w) => string.Join(" | ", w.root.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text));
    internal static void Select(CustomUIWindow browser, GameItem item)
    {
        foreach (MonoBehaviour hit in browser.root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (hit == null || !hit.gameObject.activeInHierarchy || hit.GetType().FullName != "ShopAssist.ItemGrid+Hit") continue;
            object entry = hit.GetType().GetField("entry", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hit);
            if (!ReferenceEquals(entry?.GetType().GetField("Item", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(entry), item)) continue;
            var rect = hit.transform as RectTransform;
            var canvas = rect?.GetComponentInParent<Canvas>();
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left,
                position = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center)) };
            ExecuteEvents.Execute(hit.gameObject, pointer, ExecuteEvents.pointerClickHandler); return;
        }
        throw new InvalidOperationException("Selected QA module not present: " + item.GetDisplayName(true));
    }
    private static bool Same(GridShape a, GridShape b) => a.minX == b.minX && a.minY == b.minY && a.orientation == b.orientation && a.flipped == b.flipped;
}

public sealed class LayoutReloadScenario
{
    private QaLoadSlot load;
    private string[] context;
    private int step;
    public bool Tick(out string result)
    {
        result = null;
        if (step == 0)
        {
            context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-layout-run.txt")).Split('|');
            if (context.Length != 17 || context[0] != "1" || !Guid.TryParse(context[1], out Guid run) ||
                !int.TryParse(context[2], out int slot)) throw new InvalidOperationException("Invalid F12 QA run context");
            load = new QaLoadSlot(slot, run); step++; return false;
        }
        if (!load.Tick()) return false;
        // The native traversal stops at the first item by default; descend through the machine too.
        var all = GraphUtils.FindChildrenType<GameItem>(EmporiumEntry.Instance.invElement, passthrough: _ => true);
        GameItem machine = all.SingleOrDefault(i => i.uniqueId == int.Parse(context[3]));
        GameItem fixedItem = all.SingleOrDefault(i => i.uniqueId == int.Parse(context[4]));
        GameItem added = all.SingleOrDefault(i => i.uniqueId == int.Parse(context[5]));
        if (machine == null || fixedItem == null || added == null)
            throw new InvalidOperationException("F12 QA item missing after reload; expected=" + string.Join(",", context.Skip(3).Take(3)) +
                " found=" + string.Join(",", all.Where(i => i.uniqueId > 0).Select(i => i.identifier + ":" + i.uniqueId)));
        if (!ReferenceEquals(fixedItem.parentInventory, added.parentInventory) ||
            fixedItem.modifiedShape.minX != int.Parse(context[6]) || fixedItem.modifiedShape.minY != int.Parse(context[7]) ||
            fixedItem.modifiedShape.orientation != int.Parse(context[8]) || fixedItem.modifiedShape.flipped != (context[9] == "1") ||
            added.modifiedShape.minX != int.Parse(context[10]) || added.modifiedShape.minY != int.Parse(context[11]) ||
            added.modifiedShape.orientation != int.Parse(context[12]) || added.modifiedShape.flipped != (context[13] == "1") ||
            machine.GetTagReadonly("TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT").GetInt() != int.Parse(context[14]) ||
            machine.GetTagReadonly("TOTAL_PERCENTAGE_POWER_BONUS_INT").GetInt() != int.Parse(context[15]) ||
            machine.GetTagReadonly("TOTAL_PERCENTAGE_QUALITY_BONUS_INT").GetInt() != int.Parse(context[16]))
            throw new InvalidOperationException("F12 layout or native totals changed after cold reload");
        result = "PASS F12 actual module instances, poses and native totals survived cold reload";
        return true;
    }
}
