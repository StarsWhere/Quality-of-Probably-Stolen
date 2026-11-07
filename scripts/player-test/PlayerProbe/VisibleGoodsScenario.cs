using System;
using System.Reflection;
using UnityEngine;

// Exercises owned counter goods through the real module, labeler and water windows.
public sealed class VisibleGoodsScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private GameItem machine, module, labeler, sample, candidate, other, source, destination, unknown, alcohol, fermenting;
    private int step, after;

    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window(string id) => CustomUIManager.Instance.GetWindow(Mod + "." + id);
    private static GameItem CurrentBlueTarget() => (GameItem)typeof(LabelerBlueUIManager)
        .GetField("curItem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(LabelerBlueUIManager.Instance);

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Visible goods scenario timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (entry?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null ||
                ItemSelectHandler.current == null || LabelerBlueUIManager.Instance == null) return false;
            TradeScenario.SetOptions(true, "f10.enabled", "f22.enabled", "f26.enabled");
            TradeScenario.SetOptions(false, "f22.overwrite", "f26.mix");
            machine = QaFeatureGallery.Item("hydroponic", entry.invElement);
            module = QaFeatureGallery.Item("system_module_performance", entry.backInvinvElement, "QA counter module");
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(Screen.width * .25f, Screen.height * .7f));
            machine.contentWindow.ToFront(); RoutingScenario.RunFeatureAction("f10.open");
            after = Time.frameCount + 3; step++; return false;
        }
        if (step == 2)
        {
            if (Time.frameCount < after) return false;
            var browser = Window("f10");
            Need(browser?.IsAlive == true && QaItemUi.Hit(browser.root, module) != null,
                "Owned counter module was missing from the module browser");
            browser.Close();
            labeler = QaFeatureGallery.Item("labeler_blue", entry.invElement);
            sample = QaFeatureGallery.Item("wine_yeast", entry.invElement);
            candidate = QaFeatureGallery.Item("wine_yeast", entry.backInvinvElement);
            other = QaFeatureGallery.Item("wine_yeast", entry.invElement, "QA untouched label");
            ItemSelectHandler.current.TryEquipItem(labeler);
            labeler.Target(sample);
            var blue = LabelerBlueUIManager.Instance;
            Need(blue.panel.activeSelf && ReferenceEquals(CurrentBlueTarget(), sample), "Native sample editor did not open");
            blue.inputField.SetTextWithoutNotify("QA sample label"); blue.OnEditEnd("QA sample label"); blue.CloseUI();
            RoutingScenario.RunFeatureAction("f22.open");
            var batch = Window("label");
            Need(batch?.IsAlive == true && QaItemUi.Hit(batch.root, candidate) != null,
                "Owned counter item was missing from batch labeling");
            QaItemUi.Click(batch, candidate);
            batch.Get("apply").button.onClick.Invoke();
            step++; return false;
        }
        if (step == 3)
        {
            var blue = LabelerBlueUIManager.Instance;
            if (!blue.panel.activeSelf || !ReferenceEquals(CurrentBlueTarget(), candidate)) return false;
            blue.OpenUI(other); after = Time.frameCount + 3; step++; return false;
        }
        if (step == 4)
        {
            if (Time.frameCount < after) return false;
            var blue = LabelerBlueUIManager.Instance;
            Need(blue.panel.activeSelf && ReferenceEquals(CurrentBlueTarget(), other) &&
                GeneralHelper.GetCustomName(other) == "QA untouched label" && !GeneralHelper.HasCustomName(candidate),
                "Interrupted batch changed or closed a different native editor");
            blue.CloseUI();
            source = QaFeatureGallery.Item("wine_bottle", entry.backInvinvElement, "QA scanned water bottle");
            destination = QaFeatureGallery.Item("wine_bottle", entry.backInvinvElement, "QA receiving bottle");
            unknown = QaFeatureGallery.Item("wine_bottle", entry.backInvinvElement, "QA unknown bottle");
            alcohol = QaFeatureGallery.Item("wine_bottle", entry.backInvinvElement, "QA alcohol bottle");
            fermenting = QaFeatureGallery.Item("wine_bottle", entry.backInvinvElement, "QA fermenting bottle");
            foreach (GameItem bottle in new[] { source, destination, unknown, alcohol, fermenting })
                WaterHelper.Remove(bottle, WaterHelper.GetCurrentCapacityUL(bottle));
            WaterHelper.AddWater(source, 0, 1800); WaterHelper.SetLastScanResult(source);
            WaterHelper.AddWater(unknown, 0, 1000);
            WaterHelper.AddLiquid(alcohol, "alcohol", 1000); WaterHelper.SetLastScanResult(alcohol);
            fermenting.ModifyTag(WineHelper.WINE_CONTAINER_STATE, state => state.SetInt(1));
            RoutingScenario.RunFeatureAction("f26.open");
            QaItemUi.Choose(Window("water"), "target", "QA receiving bottle");
            var water = Window("water");
            Need(QaItemUi.Hit(water.root, source) != null && QaItemUi.Hit(water.root, unknown) == null &&
                QaItemUi.Hit(water.root, alcohol) == null && QaItemUi.Hit(water.root, fermenting) == null,
                "Wine source visibility did not respect the public scan and fermentation state");
            QaItemUi.Click(water, source); Window("water").Get("preview").button.onClick.Invoke();
            Need(WaterHelper.GetCurrentCapacityUL(source) == 1800 && WaterHelper.GetCurrentCapacityUL(destination) == 0,
                "Water preview moved liquid");
            Window("water").Get("pour").button.onClick.Invoke();
            Need(WaterHelper.GetCurrentCapacityUL(source) == 0 && WaterHelper.GetCurrentCapacityUL(destination) == 1800 &&
                WineHelper.CanModifyLiquid(source) && WineHelper.CanModifyLiquid(destination) &&
                WaterHelper.GetCurrentCapacityUL(unknown) == 1000 && WaterHelper.GetCurrentCapacityUL(alcohol) == 1000,
                "Native wine-bottle pour changed the wrong contents or amount");
            result = "PASS owned counter modules and labels, editor takeover, public wine scans and safe native wine-bottle pour";
            return true;
        }
        return false;
    }
}
