using System;
using System.Reflection;
using TMPro;
using UnityEngine;

public sealed class ModuleFilterScenario : EquipmentFixture
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private GameItem machine, high, low, quality;
    private CustomUIWindow original;
    private TMP_Dropdown attribute;
    private int frame;

    public bool Tick(out string result)
    {
        result = null;
        Check(Time.realtimeSinceStartup < deadline, "Module attribute filter timed out at step " + step);
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f10.enabled");
            machine = Stock("hydroponic");
            high = Stock("system_module_performance"); low = Stock("system_module_performance");
            quality = Stock("system_module_quality");
            SetPerformance(high, 40); SetPerformance(low, 10);
            GeneralHelper.SetCustomName(high, "QA high performance");
            GeneralHelper.SetCustomName(low, "QA low performance");
            Open(machine); Action("f10.open");
            original = Ui("f10");
            attribute = original?.Get("attribute")?.dropdown;
            Check(attribute != null && attribute.options.Count == 4 && original.Get("search") == null && original.Get("category") == null,
                "Module browser did not expose the current attribute control");
            Check(QaItemUi.Hit(original.root, high) != null && QaItemUi.Hit(original.root, low) != null && QaItemUi.Hit(original.root, quality) != null,
                "Unfiltered module grid omitted a fixture");
            attribute.value = 1; frame = Time.frameCount + 3; step = 1; return false;
        }
        if (Time.frameCount < frame) return false;
        if (step == 1)
        {
            var window = Ui("f10");
            Check(ReferenceEquals(window, original) && ReferenceEquals(attribute, window.Get("attribute")?.dropdown),
                "Filtering replaced the window or attribute control");
            MonoBehaviour highHit = QaItemUi.Hit(window.root, high), lowHit = QaItemUi.Hit(window.root, low);
            Check(highHit != null && lowHit != null && QaItemUi.Hit(window.root, quality) == null,
                "Performance filter did not retain only matching modules");
            GridShape highShape = (GridShape)Field(highHit, "shownShape"), lowShape = (GridShape)Field(lowHit, "shownShape");
            Check(highShape.minY < lowShape.minY || highShape.minY == lowShape.minY && highShape.minX < lowShape.minX,
                "Performance modules were not ordered by descending value");
            QaItemUi.Click(window, high);
            Check(QaItemUi.Selected(window, high), "Module selection failed after filtering");
            ItemPinHelper.SetPinned(low, true);
            window.Get("refresh").button.onClick.Invoke();
            frame = Time.frameCount + 3; step = 2; return false;
        }
        if (step == 2)
        {
            var window = Ui("f10");
            MonoBehaviour lowHit = QaItemUi.Hit(window.root, low);
            Check(lowHit != null && !(bool)Field(Field(lowHit, "entry"), "Draggable") &&
                (string)Field(Field(lowHit, "entry"), "Marker") == ModHelper.GetLocalized(Mod, "grid.mark_fixed") &&
                QaItemUi.Selected(window, high), "Pinning or refreshing lost the current module state");
            ItemPinHelper.SetPinned(low, false);
            attribute.value = 0; frame = Time.frameCount + 3; step = 3; return false;
        }
        var restored = Ui("f10");
        Check(QaItemUi.Hit(restored.root, quality) != null && QaItemUi.Selected(restored, high) &&
            (bool)Field(Field(QaItemUi.Hit(restored.root, low), "entry"), "Draggable"),
            "Clearing the attribute filter did not restore the unpinned module grid");
        result = "PASS module attribute filtering, descending order, fixed state and retained selection";
        return true;
    }

    private static object Field(object owner, string name) => owner?.GetType().GetField(name, Flags)?.GetValue(owner);

    private static void SetPerformance(GameItem item, int value)
    {
        const string tag = "BONUS_PERCENTAGE_PERFORMANCE_INT";
        item.state.GetTag(tag).SetInt(value);
        item.modifiedState.GetTag(tag).SetInt(value);
    }
}
