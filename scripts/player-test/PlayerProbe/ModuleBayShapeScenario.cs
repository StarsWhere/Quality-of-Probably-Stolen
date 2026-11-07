using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Compare the unmodified native hydroponic bay with its workbench and drop target.
public sealed class ModuleBayShapeScenario : EquipmentFixture
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private GameItem machine, candidate;
    private GameGridInventory bay;
    private int readyFrame;

    public bool Tick(out string result)
    {
        result = null;
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f10.enabled", "f12.enabled");
            machine = Stock("hydroponic");
            candidate = Stock("system_module_performance");
            bay = Panel<GameGridInventory>(machine, "module");
            Check(bay?.inventoryShape != null, "Native hydroponic bay is missing");
            ModHelper.SetModData(Mod, "module_storage", "");
            Open(machine); machine.contentWindow.ToFront(); Action("f10.open");
            readyFrame = Time.frameCount + 3; step = 1; return false;
        }
        if (Time.frameCount < readyFrame) return false;
        Canvas.ForceUpdateCanvases();
        CustomUIWindow browser = Ui("f10");
        Check(browser != null, "Module workbench did not open");
        int expectedStage = step - 1;
        string expectedMask = expectedStage == 0 ? "10000010001000001110000010001000001" :
            expectedStage == 1 ? "10000010000000000000000000001000001" : "00000000000000000000000000000000000";
        int expectedUsable = expectedStage == 0 ? 26 : expectedStage == 1 ? 31 : 35;
        var shape = bay.inventoryShape;
        Check(machine.GetTagReadonly("MODULE_UPGRADE_STAGE_INT").GetInt() == expectedStage &&
            shape.width == 7 && shape.height == 5, "Native upgrade changed the stage or bay dimensions unexpectedly");
        int usable = 0;
        for (int y = 0; y < shape.height; y++) for (int x = 0; x < shape.width; x++)
        {
            int cell = shape.Get(x, y);
            Check(cell == expectedMask[y * shape.width + x] - '0', "Unexpected native bay mask at " + x + "," + y);
            if (cell == 0) usable++;
        }
        Check(usable == expectedUsable, "Native upgrade did not unlock the expected number of cells");
        var view = browser.root.GetComponentsInChildren<MonoBehaviour>(true).SingleOrDefault(value =>
            value != null && value.gameObject.activeInHierarchy && value.GetType().FullName == "ShopAssist.ItemGrid+View" &&
            ReferenceEquals(Field(Field(value, "Options"), "Inventory"), bay));
        Check(view != null, "Current bay view is missing");
        var options = Field(view, "Options");
        int width = shape.width, height = shape.height;
        Check(ReferenceEquals(Field(options, "Board"), shape) && (int)Field(view, "Columns") == width &&
            (int)Field(view, "Rows") == height, "Current bay dimensions differ from its native GridShape");
        var rect = (RectTransform)Field(view, "Rect");
        var tiles = rect.Cast<Transform>().Where(child => child.name.StartsWith("cell_", StringComparison.Ordinal)).ToArray();
        Check(tiles.Length == width * height, "Current bay has extra or missing cells");
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            var tile = tiles.SingleOrDefault(child => child.name == "cell_" + x + "_" + y);
            Check(tile != null && tile.GetComponent<Image>()?.raycastTarget == true,
                "Native bay cell is missing or cannot receive input at " + x + "," + y);
            bool blocked = tile.Find("blocked_mark") != null;
            Check(blocked == (shape.Get(x, y) != 0), "Current bay mask differs at " + x + "," + y);
        }
        var source = QaItemUi.Hit(browser.root, candidate);
        Check(source != null, "Candidate module is missing from the workbench");
        var outside = new GridShapeBuilder(candidate.modifiedShape).SetPosition(0, height).Build();
        var canDrop = (Delegate)Field(options, "CanDrop");
        Check(!(bool)canDrop.DynamicInvoke(Field(source, "entry"), bay, outside),
            "The current bay accepted a module beyond its native bottom edge");
        if (expectedStage == 0)
        {
            GridShape destination = BottomRowSlot();
            Check(destination != null, "Native bay has no valid placement reaching its last row");
            DragToBoard(source, view, destination);
            Check(ReferenceEquals(candidate.parentInventory, bay) && SamePose(candidate.modifiedShape, destination),
                "The visible last row did not place the module at the native position");
        }
        if (expectedStage < 2)
        {
            browser.Close();
            var kit = Stock("module_bay_expansion_kit");
            Check(kit.canItemTargetItemFunc != null && kit.canItemTargetItemFunc(kit, machine) && kit.onItemTargetItemFunc != null,
                "Native module bay expansion kit cannot target this owned machine");
            Debug.Log("[PLAYER-QA] Native expansion kit: " + kit.GetDisplayName(true));
            kit.onItemTargetItemFunc(kit, machine);
            machine.contentWindow.ToFront(); Action("f10.open");
            readyFrame = Time.frameCount + 3; step++; return false;
        }
        Check(!MachineHelper.CanExpand(machine), "Fully upgraded native bay still accepts another upgrade");
        result = "PASS native hydroponic 7x5 stages 0/1/2, 26/31/35 usable cells, matching workbench, outside rejection and last-row drag";
        return true;
    }

    private GridShape BottomRowSlot()
    {
        for (int y = 0; y < bay.inventoryShape.height; y++) for (int x = 0; x < bay.inventoryShape.width; x++)
        {
            var pose = new GridShapeBuilder(candidate.modifiedShape).SetPosition(x, y).Build();
            bool touchesBottom = false;
            for (int column = 0; column < bay.inventoryShape.width; column++)
                touchesBottom |= pose.Get(column, bay.inventoryShape.height - 1) == 1;
            if (!touchesBottom) continue;
            var slot = bay.TryInventorySlot(candidate, candidate.unitCount, pose);
            if (slot?.inventory == bay && slot.targetItem == null && slot.numTransfer >= candidate.unitCount &&
                SamePose(slot.itemGridShape, pose)) return pose;
        }
        return null;
    }

    private static void DragToBoard(MonoBehaviour source, MonoBehaviour target, GridShape destination)
    {
        var sourceView = Field(source, "view"); var sourceRect = (RectTransform)Field(sourceView, "Rect");
        float sourceCell = (float)Field(sourceView, "Cell");
        var shown = (GridShape)Field(source, "shownShape");
        var sourceTile = (RectTransform)source.transform;
        Vector2 start = RectTransformUtility.WorldToScreenPoint(CameraFor(sourceRect), sourceTile.TransformPoint(sourceTile.rect.center));
        Check(RectTransformUtility.ScreenPointToLocalPointInRectangle(sourceRect, start, CameraFor(sourceRect), out Vector2 local),
            "Could not locate the source module cell");
        var grab = new Vector2(Mathf.Clamp(Mathf.FloorToInt((local.x - sourceRect.rect.xMin) / sourceCell) - shown.minX, 0, shown.globalWidth - 1),
            Mathf.Clamp(Mathf.FloorToInt((sourceRect.rect.yMax - local.y) / sourceCell) - shown.minY, 0, shown.globalHeight - 1));
        var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = start };
        ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.beginDragHandler);
        var rect = (RectTransform)Field(target, "Rect"); float cell = (float)Field(target, "Cell");
        int x = destination.minX + (int)grab.x, y = destination.minY + (int)grab.y;
        var tile = rect.Find("cell_" + x + "_" + y);
        Check(tile != null, "No visible target cell for the native placement");
        Vector3 point = new Vector3((x + .5f) * cell, -(y + .5f) * cell);
        pointer.position = RectTransformUtility.WorldToScreenPoint(CameraFor(rect), rect.TransformPoint(point));
        pointer.pointerCurrentRaycast = new RaycastResult { gameObject = tile.gameObject };
        ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.dragHandler);
        Check(rect.GetComponentsInChildren<Image>().Any(image => image.name == "preview" && image.color.g > image.color.r),
            "A native last-row placement did not show an allowed preview");
        ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.endDragHandler);
    }

    private static object Field(object value, string name) => value?.GetType().GetField(name, Flags)?.GetValue(value);
    private static Camera CameraFor(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>();
        return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
    }
    private static bool SamePose(GridShape a, GridShape b) => a != null && b != null &&
        a.minX == b.minX && a.minY == b.minY && a.orientation == b.orientation && a.flipped == b.flipped;
}
