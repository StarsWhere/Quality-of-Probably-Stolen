using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Exercises title actions and changing views in a guarded, newly created Player save.
public sealed class ContextWindowStabilityScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem bagA, bagB, machineA, machineB, itemA, itemB;
    private GameGridInventory gridA, gridB;
    private CustomUIWindow overview, maintenance;
    private Vector2 overviewPosition, overviewScreenPosition, maintenancePosition;
    private float scrollPosition, maintenanceScroll, wait;
    private int step, captureFrame;
    private string maintenanceCapture, pickerCapture, overviewCapture;
    private readonly float deadline = Time.realtimeSinceStartup + 120;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Context/window scenario timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                StoreUIManager.Instance?.gameOptionUIManager == null || ItemMouseDoubleClickHandler.current == null || EventSystem.current == null) return false;
            TradeScenario.SetOptions(true, "f05.enabled", "f07.enabled", "f30.enabled", "f31.enabled");
            TradeScenario.SetOptions(false, "f05.rotate", "f15.enabled", "f17.enabled", "f21.enabled");
            bagA = New("backpack_medium", "QA context container A"); bagB = New("backpack_medium", "QA context container B");
            Put(EmporiumEntry.Instance.invElement, bagA); Put(EmporiumEntry.Instance.invElement, bagB);
            gridA = bagA.contentWindow.childElement as GameGridInventory; gridB = bagB.contentWindow.childElement as GameGridInventory;
            Need(gridA != null && gridB != null, "Container grids are unavailable");
            itemA = New("processed_meat", "QA context goods A"); itemB = New("processed_meat", "QA context goods B");
            PutFar(gridA, itemA); PutFar(gridB, itemB);
            machineA = New("hydroponic", "QA maintenance machine A"); machineB = New("hydroponic", "QA maintenance machine B");
            Put(EmporiumEntry.Instance.invElement, machineA); Put(EmporiumEntry.Instance.invElement, machineB);
            var destinations = new GameInventory[] { EmporiumEntry.Instance.invElement, EmporiumEntry.Instance.backInvinvElement,
                EmporiumEntry.Instance.showcaseElement, EmporiumEntry.Instance.frontInvinvElement };
            for (int i = 0; i < 8; i++)
            {
                GameItem extra = New("hydroponic", "QA overview " + i);
                if (!destinations.Any(destination => TryPut(destination, extra))) extra.Destroy();
            }
            ItemMouseDoubleClickHandler.current.OpenContentAction(bagA, new Vector2(150, 250));
            ItemMouseDoubleClickHandler.current.OpenContentAction(bagB, new Vector2(Screen.width - 180, 250));
            ItemMouseDoubleClickHandler.current.OpenContentAction(machineA, new Vector2(180, 400));
            ItemMouseDoubleClickHandler.current.OpenContentAction(machineB, new Vector2(Screen.width - 180, 400));
            step++; return false;
        }
        if (step == 2)
        {
            if (Tool(bagA.contentWindow) == null || Tool(bagB.contentWindow) == null || Tool(machineA.contentWindow) == null) return false;
            OpenFrom(bagA.contentWindow, "f07.open");
            var rules = Window("routes");
            Need(Text(rules).Contains(bagA.GetDisplayName(true)), "F07 did not select container A");
            rules.Get("receive").SetBool(true, true);
            OpenFrom(bagB.contentWindow, "f07.open");
            rules = Window("routes");
            Need(Text(rules).Contains(bagB.GetDisplayName(true)), "F07 did not switch to container B");
            OpenFrom(bagA.contentWindow, "f07.open");
            rules = Window("routes");
            Need(Text(rules).Contains(bagA.GetDisplayName(true)) && rules.Get("receive").toggle.isOn,
                "Switching containers lost the unsaved A draft");
            OpenFrom(bagB.contentWindow, "f07.open");
            rules = Window("routes");
            rules.Get("receive").SetBool(true, true);
            rules.Get("save").button.onClick.Invoke();
            string saved = ModHelper.GetModData(Mod, "routing", "");
            Need(bagB.uniqueId > 0 && saved.Contains("item:" + bagB.uniqueId + ":contents:-1") &&
                (bagA.uniqueId <= 0 || !saved.Contains("item:" + bagA.uniqueId + ":contents:-1")),
                "F07 saved a different container");
            Window("routes").Close();
            GridShape beforeA = itemA.modifiedShape.Clone(), beforeB = itemB.modifiedShape.Clone();
            OpenFrom(bagB.contentWindow, "f05.open");
            Need(Same(beforeA, itemA.modifiedShape) && !Same(beforeB, itemB.modifiedShape) &&
                itemA.parentInventory == gridA && itemB.parentInventory == gridB,
                "F05 did not arrange only the clicked container");
            machineB.contentWindow.ToFront();
            OpenFrom(machineA.contentWindow, "f31.open");
            maintenance = Window("maintenance");
            maintenanceCapture = Path.Combine(Application.persistentDataPath, "qa-maintenance-host-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            ScreenCapture.CaptureScreenshot(maintenanceCapture);
            Debug.Log("[PLAYER-QA] maintenance diagnostic capture " + maintenanceCapture);
            captureFrame = Time.frameCount + 4; step = 20; return false;
        }
        if (step == 20)
        {
            if (Time.frameCount < captureFrame || !File.Exists(maintenanceCapture)) return false;
            Need(Text(maintenance).Contains(machineA.GetDisplayName(true)), "Maintenance targeted another machine");
            maintenance.Rect.anchoredPosition += new Vector2(19, -13); maintenancePosition = maintenance.Rect.anchoredPosition;
            Canvas.ForceUpdateCanvases();
            ScrollRect maintenanceList = maintenance.root.GetComponentsInChildren<ScrollRect>(true).FirstOrDefault();
            Need(maintenanceList != null, "Maintenance has no scrollable content");
            maintenanceList.verticalNormalizedPosition = .4f;
            maintenanceScroll = maintenanceList.verticalNormalizedPosition;
            var hostSlot = maintenance.Get("host_slot")?.root;
            string hostDetails = QaItemUi.Describe(hostSlot, machineA, machineB);
            Debug.Log("[PLAYER-QA] maintenance host selector: " + hostDetails);
            Need(hostSlot != null && QaItemUi.Hit(hostSlot, machineA) != null, "Maintenance host selector is missing its item cell: " + hostDetails);
            var picker = QaItemUi.OpenPicker(maintenance, "host");
            pickerCapture = Path.Combine(Application.persistentDataPath, "qa-maintenance-picker-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            ScreenCapture.CaptureScreenshot(pickerCapture);
            Debug.Log("[PLAYER-QA] maintenance picker diagnostic capture " + pickerCapture);
            captureFrame = Time.frameCount + 4; step = 21; return false;
        }
        if (step == 21)
        {
            if (Time.frameCount < captureFrame || !File.Exists(pickerCapture)) return false;
            var picker = Window("choose");
            string pickerDetails = QaItemUi.Describe(picker.root, machineA, machineB);
            Debug.Log("[PLAYER-QA] maintenance picker: " + pickerDetails);
            Need(QaItemUi.Hit(picker.root, machineB) != null, "Other maintenance machine has no item cell: " + pickerDetails);
            QaItemUi.Click(picker, machineB);
            var maintenanceList = maintenance.root.GetComponentsInChildren<ScrollRect>(true).FirstOrDefault(s => s.gameObject.activeInHierarchy);
            Need(Window("maintenance") == maintenance && maintenance.IsAlive && maintenance.Rect.anchoredPosition == maintenancePosition &&
                maintenanceList != null && Mathf.Abs(maintenanceList.verticalNormalizedPosition - maintenanceScroll) < .02f &&
                Text(maintenance).Contains(machineB.GetDisplayName(true)), "Selecting a machine moved, scrolled or recreated maintenance");
            maintenance.Close();
            RoutingScenario.OpenOptionAction("f30.open");
            overview = Window("f30");
            overview.Rect.anchoredPosition += new Vector2(21, -11); overviewPosition = overview.Rect.anchoredPosition;
            Canvas.ForceUpdateCanvases();
            overviewScreenPosition = ScreenPosition(overview.Rect);
            ScrollRect scroll = overview.root.GetComponentsInChildren<ScrollRect>(true).FirstOrDefault();
            Need(scroll != null, "Overview has no scrollable list");
            scroll.verticalNormalizedPosition = .35f; scrollPosition = scroll.verticalNormalizedPosition;
            LayoutScenario.Select(overview, machineB);
            scroll = overview.root.GetComponentsInChildren<ScrollRect>(true).FirstOrDefault(s => s.gameObject.activeInHierarchy);
            Debug.Log("[PLAYER-QA] overview after select " + OverviewState(scroll));
            Need(Window("f30") == overview && overview.IsAlive, "Selecting an overview card recreated or closed the window");
            Need(Vector2.Distance(ScreenPosition(overview.Rect), overviewScreenPosition) <= .5f,
                "Selecting an overview card moved the window: " + OverviewState(scroll));
            Need(scroll != null && Mathf.Abs(scroll.verticalNormalizedPosition - scrollPosition) < .02f,
                "Selecting an overview card reset the scroll: " + OverviewState(scroll));
            Put(gridA, New("wine_yeast", "QA overview invalidation"));
            wait = Time.realtimeSinceStartup + .5f; step = 3; return false;
        }
        if (step == 3)
        {
            if (Time.realtimeSinceStartup < wait) return false;
            overviewCapture = Path.Combine(Application.persistentDataPath, "qa-overview-refresh-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            ScreenCapture.CaptureScreenshot(overviewCapture);
            Debug.Log("[PLAYER-QA] overview diagnostic capture " + overviewCapture);
            captureFrame = Time.frameCount + 4; step = 30; return false;
        }
        if (step == 30)
        {
            if (Time.frameCount < captureFrame || !File.Exists(overviewCapture)) return false;
            var scroll = overview.root.GetComponentsInChildren<ScrollRect>(true).FirstOrDefault(s => s.gameObject.activeInHierarchy);
            Debug.Log("[PLAYER-QA] overview after inventory refresh " + OverviewState(scroll));
            Need(Window("f30") == overview && overview.IsAlive, "Inventory refresh recreated or closed the overview");
            Need(Vector2.Distance(ScreenPosition(overview.Rect), overviewScreenPosition) <= .5f,
                "Inventory refresh moved the overview: " + OverviewState(scroll));
            Need(scroll != null && Mathf.Abs(scroll.verticalNormalizedPosition - scrollPosition) < .02f,
                "Inventory refresh reset the overview scroll: " + OverviewState(scroll));
            overview.Close();
            foreach (PixelWindow window in WindowsHandler.current.visibleWindows.ToArray())
                if (window?.parentItems != null && window.parentItems.Any(i => i != null && i.IsGameItemType("MACHINE"))) window.Hide();
            RoutingScenario.OpenOptionAction("f31.open");
            maintenance = Window("maintenance");
            Need(maintenance.IsAlive && QaItemUi.Hit(maintenance.root, machineA) != null, "No-host maintenance chooser did not open");
            maintenance.Rect.anchoredPosition += new Vector2(-16, 9); maintenancePosition = maintenance.Rect.anchoredPosition;
            QaItemUi.Click(maintenance, machineA);
            Need(Window("maintenance") == maintenance && maintenance.IsAlive && maintenance.Rect.anchoredPosition == maintenancePosition &&
                Text(maintenance).Contains(machineA.GetDisplayName(true)), "No-host selection did not keep its maintenance window");
            maintenance.Close();
            machineA.contentWindow.Hide();
            RoutingScenario.OpenOptionAction("f31.open");
            maintenance = Window("maintenance");
            Need(maintenance.IsAlive && QaItemUi.Hit(maintenance.root, machineA) != null, "No-host chooser did not reopen");
            TradeScenario.SetOptions(false, "f31.enabled");
            Need(!maintenance.IsAlive, "No-host maintenance chooser survived disabling the feature");
            result = "PASS explicit F05/F07 container targets and unsaved drafts, bound maintenance machine, stable machine windows and feature shutdown";
            return true;
        }
        throw new InvalidOperationException("Unknown context/window scenario step");
    }

    private static CustomUIWindow Window(string id) => CustomUIManager.Instance.GetWindow(Mod + "." + id)
        ?? throw new InvalidOperationException("Missing window " + id);
    private static string Text(CustomUIWindow window) => string.Join(" ", window.root.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text));
    private string OverviewState(ScrollRect scroll) => "same=" + ReferenceEquals(CustomUIManager.Instance.GetWindow(Mod + ".f30"), overview) + " alive=" + (overview?.IsAlive == true) +
        " anchored=" + (overview?.IsAlive == true ? overview.Rect.anchoredPosition.ToString() : "closed") + " expected=" + overviewPosition +
        " screenDelta=" + (overview?.IsAlive == true ? Vector2.Distance(ScreenPosition(overview.Rect), overviewScreenPosition) : -1) +
        " scroll=" + (scroll != null ? scroll.verticalNormalizedPosition.ToString() : "null") + " expectedScroll=" + scrollPosition +
        " content=" + (scroll?.content != null ? scroll.content.rect.height.ToString() : "null") +
        " viewport=" + (scroll?.viewport != null ? scroll.viewport.rect.height.ToString() : "null");
    private static Vector2 ScreenPosition(RectTransform rect)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
    }
    private static MachineryButton Tool(PixelWindow window) => window?.handler?.transform.Find("ShopAssist_WindowTools")
        ?.GetComponent<TreeNodeRender>()?.element as MachineryButton;
    private static void OpenFrom(PixelWindow source, string action)
    {
        MachineryButton button = Tool(source); Need(button != null && !button.isDisabled, "Unavailable source title button");
        button.Select();
        var card = Window("context");
        var operation = card.Get(action)?.button;
        Need(operation != null && operation.interactable, "Unavailable source action " + action);
        operation.onClick.Invoke();
    }
    private static GameItem New(string id, string name)
    {
        GameItem item = DirectoryMaster.Item(id); Need(item != null, "Missing " + id);
        GeneralHelper.SetItemOwned(item, true); GeneralHelper.SetCustomName(item, name); return item;
    }
    private static bool TryPut(GameInventory inventory, GameItem item)
    {
        var marker = inventory?.TryFindOneValidInventorySlot(item);
        return marker != null && marker.targetItem == null && marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory;
    }
    private static void Put(GameInventory inventory, GameItem item) => Need(TryPut(inventory, item), "Cannot place " + item.identifier);
    private static void PutFar(GameGridInventory inventory, GameItem item)
    {
        var shape = new GridShapeBuilder(item.modifiedShape);
        for (int y = inventory.inventoryShape.height - shape.globalHeight; y >= 0; y--)
        for (int x = inventory.inventoryShape.width - shape.globalWidth; x >= 0; x--)
        {
            shape.SetPosition(x, y); var marker = inventory.TryInventorySlot(item, item.unitCount, shape);
            if (marker?.inventory == inventory && marker.targetItem == null && marker.numTransfer >= item.unitCount &&
                marker.TryAcceptOnce(item.unitCount) == item.unitCount) return;
        }
        throw new InvalidOperationException("Cannot place distant item " + item.identifier);
    }
    private static bool Same(GridShape a, GridShape b) => a.minX == b.minX && a.minY == b.minY &&
        a.orientation == b.orientation && a.flipped == b.flipped;
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
