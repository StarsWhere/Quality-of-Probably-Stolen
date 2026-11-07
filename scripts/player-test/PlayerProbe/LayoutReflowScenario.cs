using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// A controlled fragmented board in the real hydroponic machine. All target checks, moves and attributes use the game.
public class LayoutReflowScenario
{
    private readonly string machineType;
    public LayoutReflowScenario() : this("hydroponic") { }
    protected LayoutReflowScenario(string machineType) { this.machineType = machineType; }
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step, baseline;
    private GameItem machine, installed, extra;
    private GameGridInventory bay;
    private GridShape original;
    private static void Check(bool ok, string details) { if (!ok) throw new InvalidOperationException(details); }
    private static List<GridShape> Poses(GameGridInventory board, GameItem item)
    {
        var result = new List<GridShape>();
        var pose = new GridShapeBuilder(item.modifiedShape);
        for (int t = 0; t < 4; t++) for (int f = 0; f < 2; f++)
        for (int y = 0; y < board.inventoryShape.height; y++) for (int x = 0; x < board.inventoryShape.width; x++)
        {
            pose.SetTransform(x, y, f == 1, t);
            var m = board.TryInventorySlot(item, 1, pose);
            if (m != null && m.inventory == board && m.targetItem == null && m.numTransfer > 0) result.Add(pose.Clone());
        }
        return result;
    }
    private static GameItem Put(string id)
    {
        var item = DirectoryMaster.Item(id); GeneralHelper.SetItemOwned(item);
        var grid = EmporiumEntry.Instance.invElement; var marker = grid.TryFindOneValidInventorySlot(item);
        Check(marker != null && marker.inventory == grid && marker.targetItem == null && marker.TryAcceptOnce() > 0, "Cannot place native QA item " + id);
        return item;
    }
    public bool Tick(out string result)
    {
        result = null;
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f10.enabled", "f12.enabled");
            machine = Put(machineType); installed = Put("system_module_quality"); extra = Put("system_module_performance");
            GeneralHelper.SetCustomName(extra, "QA reach target");
            var panel = ((MachineryPanelRow)machine.contentWindow.childElement).GetPanel("module");
            bay = MachineryContainerUtils.Collect(panel.content).OfType<GameGridInventory>().Single();
            for (int h = 1; h <= 5 && original == null; h++) for (int w = 2; w <= 7 && original == null; w++)
            {
                bay.SetShape(w, h);
                var a = Poses(bay, installed); var b = Poses(bay, extra);
                if (b.Count == 0 || !a.Any(p => b.Any(q => !p.Intersects(q)))) continue;
                original = a.FirstOrDefault(p => b.All(q => p.Intersects(q)));
            }
            Check(original != null, "Native module geometry did not produce a fragmented fixture");
            var home = bay.TryInventorySlot(installed, 1, original);
            Check(home != null && home.TryAcceptOnce() == 1 && bay.TryFindOneValidInventorySlot(extra) == null,
                "Candidate already had an empty placement in the fixture");
            baseline = machine.GetTagReadonly("TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT").GetInt();
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(300, 180));
            machine.contentWindow.ToFront();
            RoutingScenario.RunFeatureAction("f10.open");
            step++; return false;
        }
        var browser = CustomUIManager.Instance.GetWindow(Mod + ".f10");
        LayoutScenario.Select(browser, extra); browser.Get("layout").button.onClick.Invoke();
        var layout = CustomUIManager.Instance.GetWindow(Mod + ".f10"); layout.Get("preview").button.onClick.Invoke();
        layout = CustomUIManager.Instance.GetWindow(Mod + ".f10");
        Check(extra.parentInventory == EmporiumEntry.Instance.invElement && installed.parentInventory == bay &&
            installed.modifiedShape.minX == original.minX && installed.modifiedShape.minY == original.minY, "Reflow preview moved goods");
        var apply = layout.Get("apply")?.button;
        Check(apply != null, "Solver did not find the available reflow: " + string.Join(" | ", layout.root.GetComponentsInChildren<TMPro.TMP_Text>().Select(t => t.text)));
        apply.onClick.Invoke();
        Check(extra.parentInventory == bay && installed.parentInventory == bay && bay.childItems.Count == 2 &&
            (installed.modifiedShape.minX != original.minX || installed.modifiedShape.minY != original.minY ||
             installed.modifiedShape.orientation != original.orientation || installed.modifiedShape.flipped != original.flipped) &&
            machine.GetTagReadonly("TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT").GetInt() > baseline,
            "Applying reflow failed to retain and reposition the previously installed module");
        result = "PASS forced " + machineType + " reflow: extra module cannot fit until an installed module moves; same instance kept and native performance increased";
        return true;
    }
}

public sealed class MoistureLayoutScenario : LayoutReflowScenario
{
    public MoistureLayoutScenario() : base("moisture_farm") { }
}
