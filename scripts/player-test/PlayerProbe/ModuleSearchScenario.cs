using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

public sealed class ModuleSearchScenario : EquipmentFixture
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private GameItem machine, wanted, other;
    private GameGridInventory bay;
    private CustomUIWindow originalWindow;
    private TMP_InputField originalInput;
    private Transform originalGrid, previousGrid;
    private Harmony reservationPatch;
    private static GameItem reservedItem;
    private static bool simulateReservation;
    private readonly float deadline = Time.realtimeSinceStartup + 180f;
    private RectTransform layer;
    private Vector3 originalScale;
    private bool scaleChanged;
    private int frame, typed;
    private Vector2 originalPoint;
    private readonly string[] queries = { "Q", "QA", "QA typed", "QA typed module" };

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch { Cleanup(); throw; }
    }

    private bool TickCore(out string result)
    {
        result = null; Check(Time.realtimeSinceStartup < deadline, "Module search timed out at step " + step);
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f10.enabled", "f12.enabled");
            machine = Stock("hydroponic"); bay = Panel<GameGridInventory>(machine, "module");
            wanted = Stock("system_module_performance"); other = Stock("system_module_quality");
            GeneralHelper.SetCustomName(wanted, queries[queries.Length - 1]);
            GeneralHelper.SetCustomName(other, "Unrelated module");
            Open(machine); Action("f10.open");
            originalWindow = Ui("f10");
            var input = Input(); originalInput = input; originalPoint = Point(input);
            originalGrid = Grid(wanted);
            Check(originalGrid != null, "The original module source grid is missing");
            input.Select(); input.ActivateInputField();
            frame = Time.frameCount + 3; step = 1; return false;
        }
        if (Time.frameCount < frame) return false;
        if (step > 1) return CheckChangedState(out result);
        var search = Input();
        Check(ReferenceEquals(originalWindow, Ui("f10")) && ReferenceEquals(originalInput, search),
            "Refreshing module results replaced the search window or input");
        Check(search.isFocused && EventSystem.current.currentSelectedGameObject == search.gameObject,
            "Refreshing the module search lost keyboard focus");
        Check(Vector2.Distance(originalPoint, Point(search)) < 2, "Typing moved the search field");
        if (typed > 0)
        {
            Check(search.text == queries[typed - 1] && search.selectionFocusPosition == search.text.Length && search.selectionAnchorPosition == search.text.Length,
                "Refreshing the module search lost text or caret position");
            Check(QaItemUi.Hit(Ui("f10").root, wanted) != null && QaItemUi.Hit(Ui("f10").root, other) == null,
                "The actual module grid did not follow the search text");
            if (typed == 1) Check(!ReferenceEquals(originalGrid, Grid(wanted)), "A changed search result reused the stale source grid");
            if (typed == 3) previousGrid = Grid(wanted);
            if (typed == 4) Check(ReferenceEquals(previousGrid, Grid(wanted)),
                "Equivalent search results rebuilt the source grid");
        }
        if (typed == queries.Length)
        {
            previousGrid = Grid(wanted);
            FeedScenario.Put(bay, other);
            search.text = "QA TYPED MODULE"; frame = Time.frameCount + 3; step = 2; return false;
        }
        search.text = queries[typed++]; search.caretPosition = search.text.Length;
        frame = Time.frameCount + 3; return false;
    }

    private bool CheckChangedState(out string result)
    {
        result = null;
        if (step == 2)
        {
            Check(!ReferenceEquals(previousGrid, Grid(wanted)) && ReferenceEquals(other.parentInventory, bay) && QaItemUi.Hit(Ui("f10").root, other) != null,
                "Changing the installed bay did not refresh the search view");
            var assembly = ModLoader.Mods.Find(value => value.Manifest?.ID == Mod).Instance.GetType().Assembly;
            reservationPatch = new Harmony("qa.module-search-reservation"); reservedItem = wanted; simulateReservation = true;
            reservationPatch.Patch(assembly.GetType("ShopAssist.RequestBook", true).GetMethod("Reserved", Flags),
                postfix: new HarmonyMethod(typeof(ModuleSearchScenario).GetMethod(nameof(Reservation), Flags)));
            previousGrid = Grid(wanted); Input().text = "qA typed module"; frame = Time.frameCount + 3; step = 3; return false;
        }
        if (step == 3)
        {
            var hit = QaItemUi.Hit(Ui("f10").root, wanted);
            Check(!ReferenceEquals(previousGrid, Grid(wanted)) && !(bool)Entry(hit, "Draggable") &&
                (string)Entry(hit, "Marker") == ModHelper.GetLocalized(Mod, "grid.mark_reserved"),
                "A newly reserved item retained stale search controls");
            simulateReservation = false; previousGrid = Grid(wanted);
            Input().text = "qa TYPED MODULE"; frame = Time.frameCount + 3; step = 4; return false;
        }
        if (step == 4)
        {
            var hit = QaItemUi.Hit(Ui("f10").root, wanted);
            Check(!ReferenceEquals(previousGrid, Grid(wanted)) && (bool)Entry(hit, "Draggable"),
                "Releasing a reservation retained stale search controls");
            reservationPatch.UnpatchAll("qa.module-search-reservation"); reservationPatch = null; reservedItem = null;
            QaItemUi.Click(Ui("f10"), wanted);
            Check(QaItemUi.Selected(Ui("f10"), wanted), "Module selection did not update");
            Input().text = "Qa Typed Module"; frame = Time.frameCount + 3; step = 5; return false;
        }
        if (step == 5)
        {
            Check(QaItemUi.Selected(Ui("f10"), wanted), "Search lost the active module selection");
            layer = CustomUIManager.Instance.GetLayerRoot(CustomUIManager.LAYER_META);
            Check(layer != null, "The module canvas layer is missing");
            originalScale = layer.localScale; layer.localScale = originalScale * 1.08f; scaleChanged = true;
            previousGrid = Grid(wanted); Input().text = "qA Typed Module"; frame = Time.frameCount + 3; step = 6; return false;
        }
        if (step == 6)
        {
            Check(!ReferenceEquals(previousGrid, Grid(wanted)), "A changed canvas scale retained the stale search grid");
            layer.localScale = originalScale; scaleChanged = false;
            previousGrid = Grid(wanted); Input().text = "QA Typed Module"; frame = Time.frameCount + 3; step = 7; return false;
        }
        if (step == 7)
        {
            Check(!ReferenceEquals(previousGrid, Grid(wanted)), "Restoring the canvas scale retained the stale search grid");
            frame = Time.frameCount + 3; step = 11; return false;
        }
        if (step == 11)
        {
            Check(Ui("f10").Get("idle_tab").text.text == ModHelper.GetLocalized(Mod, "grid.tab_idle"),
                "Module labels do not match the language chosen before entering the store");
            ModuleEffectHelper.ComputeAllModuleTempStat(bay.childItems);
            ModuleHelper.ComputeModuleEffect(bay, null, machine);
            ModuleHelper.ApplyBasicModuleEffect(bay, null, machine);
            MachineryHelper.UpdatePowerUsageProcessTypeMachine(machine);
            Ui("f10").Get("layout").button.onClick.Invoke();
            Ui("f10").Get("preview").button.onClick.Invoke();
            Check(Ui("f10").Get("apply") != null, "The layout preview did not open for the selected module");
            previousGrid = Grid(wanted); Input().text = "qA tYPED mODULE"; frame = Time.frameCount + 3; step = 12; return false;
        }
        if (step == 12)
        {
            Check(Ui("f10").Get("apply") != null && !ReferenceEquals(previousGrid, Grid(wanted)) && QaItemUi.Selected(Ui("f10"), wanted),
                "Search failed to refresh the active layout preview");
            Ui("f10").Get("layout").button.onClick.Invoke();
            var hit = QaItemUi.Hit(Ui("f10").root, wanted);
            object target = typeof(ModuleGridScenario).GetMethod("BoardView", Flags).Invoke(null, new object[] { Ui("f10"), bay });
            SlotMarker slot = bay.TryFindOneValidInventorySlot(wanted);
            Check(hit != null && target != null && slot?.itemGridShape != null && slot.targetItem == null,
                "The refreshed search grid has no valid drag target");
            typeof(ModuleGridScenario).GetMethod("DragToBoard", Flags).Invoke(null, new[] { (object)hit, target, slot.itemGridShape });
            Check(ReferenceEquals(wanted.parentInventory, bay), "Dragging after search refresh did not install the actual module");
            result = "PASS stable equivalent module search, changed results and bay, reservation state, scale, layout preview and real drag";
            return true;
        }
        throw new InvalidOperationException("Unknown module search state " + step);
    }

    private static void Reservation(GameItem __0, ref bool __result)
    { if (simulateReservation && ReferenceEquals(__0, reservedItem)) __result = true; }

    private void Cleanup()
    {
        reservationPatch?.UnpatchAll("qa.module-search-reservation"); reservationPatch = null; reservedItem = null; simulateReservation = false;
        if (scaleChanged && layer != null) layer.localScale = originalScale;
        scaleChanged = false;
    }

    private static object Entry(MonoBehaviour hit, string name)
    {
        object entry = hit?.GetType().GetField("entry", Flags)?.GetValue(hit);
        return entry?.GetType().GetField(name, Flags)?.GetValue(entry);
    }

    private static Transform Grid(GameItem item) => QaItemUi.Hit(Ui("f10").root, item)?.transform.parent;

    private TMP_InputField Input() => Ui("f10").Get("search").root.GetComponentInChildren<TMP_InputField>(true);
    private static Vector2 Point(TMP_InputField input)
    {
        var rect = (RectTransform)input.transform; var canvas = rect.GetComponentInParent<Canvas>();
        var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
    }
}
