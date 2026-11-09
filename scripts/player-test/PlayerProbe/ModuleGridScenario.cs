using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Runs in a fresh isolated native game, through the same pointer handlers used by the grid.
public sealed class ModuleGridScenario
{
    private const string Mod = "starswhere.shopassist";
    private const string MissingVersionBinding = "{\"location\":\"root:stock\"}";
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly QaNewRun start = new QaNewRun();
    private GameItem machine, other, fixedModule, installed, candidate, moving, remote, stored, bag;
    private GameGridInventory bay, otherBay, bagGrid;
    private GridShape fixedShape;
    private int step;
    private bool nativeDragTooltipShown;
    private readonly float deadline = Time.realtimeSinceStartup + 150;

    public bool Tick(out string result)
    {
        result = null;
        Check(Time.realtimeSinceStartup < deadline, "Module grid scenario timed out at step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null ||
                StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(true, "f10.enabled", "f12.enabled", "f40.enabled", "f41.enabled");
            machine = New("hydroponic"); other = New("hydroponic"); bag = New("fanny_pack");
            GeneralHelper.SetCustomName(other, "QA Other Machine"); GeneralHelper.SetCustomName(bag, "QA Module Storage");
            fixedModule = New("system_module_quality"); installed = New("system_module_efficiancy"); candidate = New("system_module_performance");
            moving = New("system_module_efficiancy"); remote = New("system_module_quality");
            stored = New("system_module_quality");
            foreach (GameItem item in new[] { machine, other, bag, fixedModule, installed, candidate, moving, remote, stored }) Put(EmporiumEntry.Instance.invElement, item);
            ItemMouseDoubleClickHandler.current.OpenContentAction(other, new Vector2(30, 80));
            otherBay = Bay(other); Put(otherBay, remote); other.contentWindow.Hide();
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(30, 80));
            bay = Bay(machine); bay.SetShape(7, 6);
            ModuleEffectHelper.AddEffectToModule(fixedModule, "MODULE_EFFECT_TOP_MOUNT");
            Put(bay, fixedModule); Put(bay, installed); ModuleHelper.SetModuleStuck(fixedModule); fixedShape = fixedModule.modifiedShape.Clone();
            bagGrid = bag.contentWindow.childElement as GameGridInventory;
            Check(bagGrid != null, "Module storage did not expose a real grid");
            bagGrid.SetShape(6, 5);
            Put(bagGrid, stored);
            ModHelper.SetModData(Mod, "module_storage", MissingVersionBinding);
            machine.contentWindow.ToFront();
            RoutingScenario.RunFeatureAction("f10.open");
            step++; return false;
        }
        if (step == 2)
        {
            CustomUIWindow browser = Browser();
            Check(browser.Get("storage_choose_footer") != null &&
                ModHelper.GetModData(Mod, "module_storage", null) == MissingVersionBinding,
                "A storage binding without a version was accepted or overwritten while reading");
            CheckWorkbench(browser, candidate, bay);
            CheckWindowShield(browser);
            CheckText(browser, "source_title"); CheckText(browser, "storage_status"); CheckText(browser, "selected_count");
            var stuck = FindBoardHit(browser, fixedModule, bay);
            Check(stuck != null && ((string)Field(Field(stuck, "entry"), "Reason")).Contains(ModHelper.GetLocalized(Mod, "f10.stuck")),
                "Stuck module lost its explanation");
            var marker = browser.root.GetComponentsInChildren<TMPro.TMP_Text>(true).FirstOrDefault(text =>
                text.name == "status_text" && text.text == ModHelper.GetLocalized(Mod, "grid.mark_stuck"));
            CheckVisible(marker, "stuck marker");
            Check(FindHit(browser, candidate) != null && FindHit(browser, moving) != null, "Idle modules lack real item cells");
            Check(browser.root.GetComponentsInChildren<Image>(true).Any(image => image.sprite == ((GameItemElement)candidate).background.image.sprite),
                "Idle card lacks the native item sprite");
            var unrelatedTooltip = OverlayHandler.current.GetTooltipWindow();
            OverlayHandler.current.ShowTooltip(Vector2.zero, ((GameItemElement)candidate).GetTooltip());
            var unhoveredCell = FindHit(browser, moving);
            unhoveredCell.gameObject.SetActive(false);
            Check(unrelatedTooltip.IsVisible(), "Disabling another grid cell hid the native tooltip");
            unhoveredCell.gameObject.SetActive(true);
            CheckGridLifetime(browser, unrelatedTooltip);
            OverlayHandler.current.HideTooltip();
            nativeDragTooltipShown = CheckDragTooltip(browser);
            Shot("qa-module-grid-idle.png"); step = 21; return false;
        }
        if (step == 21)
        {
            CustomUIWindow browser = Browser();
            browser.Get("storage_choose_footer").button.onClick.Invoke();
            var picker = CustomUIManager.Instance.GetWindow(Mod + ".choose");
            Check(picker != null, "Visual storage selector missing");
            MonoBehaviour storageHit = null;
            for (int page = 0; page < 20 && storageHit == null; page++)
            {
                storageHit = QaItemUi.Hit(picker.root, name: "QA Module Storage");
                if (storageHit != null) break;
                var next = picker.Get("next")?.button; if (next == null) break;
                next.onClick.Invoke(); picker = CustomUIManager.Instance.GetWindow(Mod + ".choose");
            }
            Check(storageHit != null, "Storage selector omitted the real container"); QaItemUi.Click(storageHit);
            step = 3; return false;
        }
        if (step == 3)
        {
            CustomUIWindow browser = Browser();
            if (BoardView(browser, bagGrid) == null) return false;
            Check(!string.IsNullOrEmpty(ModHelper.GetModData(Mod, "module_storage", null)), "Storage binding was not saved");
            Check(FindBoardHit(browser, stored, bagGrid) != null, "Storage grid omitted an existing occupant");
            Shot("qa-module-grid-storage.png"); step = 30; return false;
        }
        if (step == 30)
        {
            CustomUIWindow browser = Browser();
            browser.Get("installed_tab").button.onClick.Invoke(); browser = Browser();
            for (int page = 0; page < 20 && FindHit(browser, remote) == null; page++)
            {
                var next = browser.Get("next")?.button;
                if (next == null) break;
                string previous = browser.Get("module_page")?.GetText();
                next.onClick.Invoke(); browser = Browser();
                if (browser.Get("module_page")?.GetText() == previous) break;
            }
            bool hasRemote = FindHit(browser, remote) != null;
            bool hasSprite = browser.root.GetComponentsInChildren<Image>(true).Any(image =>
                image.sprite == ((GameItemElement)remote).background.image.sprite);
            bool hasOwner = browser.root.GetComponentsInChildren<TMPro.TMP_Text>(true).Any(text => text.text.Contains("QA Other Machine"));
            Check(hasRemote && hasSprite && hasOwner, "Other machine bay omitted its real owner/item sprite: " + DescribeOther(browser, hasRemote, hasSprite));
            Shot("qa-module-grid-other.png"); step = 31; return false;
        }
        if (step == 31)
        {
            CustomUIWindow browser = Browser();
            SlotMarker free = bay.TryFindOneValidInventorySlot(remote);
            Check(free?.itemGridShape != null, "Current bay has no room for the remote module");
            DragToBoard(FindBoardHit(browser, remote, otherBay), BoardView(browser, bay), free.itemGridShape);
            Check(remote.parentInventory == bay && Same(remote.modifiedShape, free.itemGridShape), "Remote-to-current drag did not use the real target");
            step = 32; return false;
        }
        if (step == 32)
        {
            CustomUIWindow browser = Browser();
            MonoBehaviour source = FindBoardHit(browser, remote, bay), target = BoardView(browser, otherBay);
            if (source == null || target == null) return false;
            SlotMarker free = otherBay.TryFindOneValidInventorySlot(remote);
            Check(free?.itemGridShape != null, "Other machine has no return slot");
            DragToBoard(source, target, free.itemGridShape);
            Check(remote.parentInventory == otherBay && Same(remote.modifiedShape, free.itemGridShape), "Current-to-remote drag did not use the real bay");
            step = 33; return false;
        }
        if (step == 33)
        {
            Browser().Get("idle_tab").button.onClick.Invoke(); step = 4; return false;
        }
        if (step == 4)
        {
            CustomUIWindow browser = Browser();
            CheckHeaderDoesNotReceiveItems(browser);
            MarqueeAll((MonoBehaviour)Field(FindHit(browser, candidate), "view"));
            Check(QaItemUi.Selected(browser, candidate) && QaItemUi.Selected(browser, moving) && QaItemUi.Selected(browser, stored),
                "Source marquee did not select the physical modules");
            Check(browser.Get("selected_count").text.text == ModHelper.GetLocalized(Mod, "grid.selected").Replace("{0}", "3"),
                "The selected count did not update immediately after marquee selection");
            CheckText(browser, "selected_count");
            Click(FindHit(browser, moving)); Click(FindHit(browser, stored));
            Check(browser.Get("selected_count").text.text == ModHelper.GetLocalized(Mod, "grid.selected").Replace("{0}", "1"),
                "The selected count did not update immediately after clicking");
            MarqueeAll(BoardView(browser, bay));
            Check((bool)Field(Field(FindBoardHit(browser, installed, bay), "entry"), "Selected"), "Current-bay marquee did not select its movable part");
            browser.Get("layout").button.onClick.Invoke();
            CustomUIWindow layout = Layout(); layout.Get("preview").button.onClick.Invoke(); layout = Layout();
            Check(layout.Get("apply")?.button != null, "F12 failed to create a real-grid preview");
            Check(PreviewHit(layout, candidate) != null && PreviewHit(layout, fixedModule) != null && BoardView(layout, bay) != null && candidate.parentInventory != bay &&
                fixedModule.parentInventory == bay && Same(fixedShape, fixedModule.modifiedShape), "Preview did not map real items without moving them");
            CheckPreviewFits(layout, bay, candidate);
            CheckText(layout, "source_title"); CheckText(layout, "storage_status");
            foreach (string key in new[] { "layout_performance", "layout_efficiency", "layout_quality" }) CheckText(layout, key);
            Shot("qa-module-grid-layout.png"); step = 41; return false;
        }
        if (step == 41)
        {
            CustomUIWindow layout = Layout();
            layout.Get("apply").button.onClick.Invoke();
            Check(candidate.parentInventory == bay && fixedModule.parentInventory == bay && Same(fixedShape, fixedModule.modifiedShape),
                "F12 failed to preserve the stuck item while applying the preview");
            Layout().Get("layout").button.onClick.Invoke(); step = 5; return false;
        }
        if (step == 5)
        {
            CustomUIWindow browser = Browser();
            var source = FindHit(browser, moving);
            var target = BoardView(browser, bay);
            if (source == null || target == null) return false;
            GridShape rotated = DragRotatedToBoard(source, target, moving, bay);
            Check(moving.parentInventory == bay && Same(moving.modifiedShape, rotated), "Rotated drag did not keep its grabbed cell at the exact current-bay position");
            step++; return false;
        }
        if (step == 6)
        {
            CustomUIWindow browser = Browser();
            browser.Get("storage_tab").button.onClick.Invoke(); browser = Browser();
            var source = FindBoardHit(browser, moving, bay); var target = BoardView(browser, bagGrid);
            if (source == null || target == null) return false;
            GridShape free = FreePose(bagGrid, moving);
            Check(free != null, "Storage has no free module slot in the dragged orientation");
            DragToBoard(source, target, free);
            Check(moving.parentInventory == bagGrid && Same(moving.modifiedShape, free), "Dragging to storage did not use the exact cell");
            step = 7; return false;
        }
        if (step == 7)
        {
            CustomUIWindow browser = Browser(); var source = FindBoardHit(browser, moving, bagGrid);
            var target = BoardView(browser, bay); if (source == null || target == null) return false;
            GridShape free = FreePose(bay, moving); Check(free != null, "Current bay has no return slot in the dragged orientation");
            DragToBoard(source, target, free);
            Check(moving.parentInventory == bay && Same(moving.modifiedShape, free), "Storage-to-current drag failed");
            step = 8; return false;
        }
        if (step == 8)
        {
            CustomUIWindow browser = Browser(); browser.Get("idle_tab").button.onClick.Invoke(); browser = Browser();
            var source = FindBoardHit(browser, moving, bay); var proxy = (MonoBehaviour)Field(FindHit(browser, stored), "view");
            Check(source != null && proxy != null, "Idle return target is unavailable");
            DragToProxy(source, proxy);
            Check(moving.parentInventory == bagGrid, "Idle aggregate did not return the real item to bound storage");
            step = 9; return false;
        }
        if (step == 9)
        {
            CustomUIWindow browser = Browser(); browser.Get("storage_tab").button.onClick.Invoke(); browser = Browser();
            var source = FindBoardHit(browser, moving, bagGrid); Check(source != null, "Storage item disappeared before native drop");
            machine.contentWindow.ToFront(); browser.SetPosition(new Vector2(-browser.Rect.rect.width * .65f, 0)); Canvas.ForceUpdateCanvases();
            Check(DragToNative(source, moving, bay), "Could not find an unobstructed native module bay cell");
            Check(moving.parentInventory == bay && candidate.parentInventory == bay && Same(fixedShape, fixedModule.modifiedShape),
                "Dragging out of storage did not install the original module");
            Check(!nativeDragTooltipShown, "A card tooltip appeared over an active native item drag");
            result = "PASS dense dual-grid workbench, source/current marquee, cross-machine drags, bound storage and proxy, anchored rotation, native bay drag, same-window F12 preview and stuck preservation";
            return true;
        }
        throw new InvalidOperationException("Unknown module scenario step");
    }

    private static GameItem New(string id)
    {
        GameItem item = DirectoryMaster.Item(id); Check(item != null, "Missing fixture " + id); GeneralHelper.SetItemOwned(item, true); return item;
    }
    private static void Put(GameInventory grid, GameItem item)
    {
        SlotMarker slot = grid.TryFindOneValidInventorySlot(item);
        Check(slot != null && slot.targetItem == null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid, "Cannot place " + item.identifier);
    }
    private static GameGridInventory Bay(GameItem item)
    {
        var panel = (item.contentWindow.childElement as MachineryPanelRow)?.GetPanel("module");
        var nodes = panel == null ? null : MachineryContainerUtils.Collect(panel.content);
        Check(nodes?.Count == 1 && nodes[0] is GameGridInventory, "Missing native module bay"); return (GameGridInventory)nodes[0];
    }
    private static CustomUIWindow Browser() => CustomUIManager.Instance.GetWindow(Mod + ".f10") ?? throw new InvalidOperationException("F10 closed");
    private static CustomUIWindow Layout() => Browser();
    private static bool Same(GridShape a, GridShape b) => a != null && b != null && a.minX == b.minX && a.minY == b.minY && a.orientation == b.orientation && a.flipped == b.flipped;
    private static void Shot(string name)
    {
        Canvas.ForceUpdateCanvases();
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, name));
    }
    private static void Check(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private static object Field(object value, string name) => value?.GetType().GetField(name, F)?.GetValue(value);
    private static object GridField(string name)
    {
        var mod = ModLoader.Mods.Find(value => value.Manifest?.ID == Mod)?.Instance?.GetType().Assembly;
        return mod?.GetType("ShopAssist.ItemGrid", true).GetField(name, F)?.GetValue(null);
    }
    private static int ActiveGridCount()
    {
        object roots = GridField("activeRoots");
        return roots == null ? -1 : (int)roots.GetType().GetProperty("Count").GetValue(roots);
    }
    private static void TickGrid()
    {
        var input = GridField("input") as MonoBehaviour;
        Check(input != null && input.isActiveAndEnabled, "An active grid has no input coordinator");
        input.GetType().GetMethod("Update", F).Invoke(input, null);
    }
    private static void CheckGridLifetime(CustomUIWindow browser, PixelWindow unrelatedTooltip)
    {
        int before = ActiveGridCount();
        Check(before > 0 && GridField("input") is MonoBehaviour, "Visible grids did not register their input coordinator");
        browser.root.SetActive(false);
        try
        {
            int remaining = ActiveGridCount();
            Check(remaining >= 0 && remaining < before, "Hidden grids remained registered");
            Check(remaining != 0 || GridField("input") == null, "The coordinator remained active without any grid");
            Check(unrelatedTooltip.IsVisible(), "Hiding a grid dismissed an unrelated native tooltip");
        }
        finally { browser.root.SetActive(true); }
        Check(ActiveGridCount() == before && GridField("input") is MonoBehaviour, "Reopened grids did not restore input coordination");
    }
    private string DescribeOther(CustomUIWindow browser, bool hit, bool sprite)
    {
        var panel = (other.contentWindow.childElement as MachineryPanelRow)?.GetPanel("module");
        var nodes = panel == null ? null : MachineryContainerUtils.Collect(panel.content);
        var branch = new List<string>();
        for (Transform cursor = otherBay.handler.transform; cursor != null; cursor = cursor.parent)
        {
            branch.Add(cursor.name + "[" + cursor.gameObject.activeSelf + "]");
            if (panel != null && ReferenceEquals(cursor, panel.handler.transform)) break;
        }
        var mod = ModLoader.Mods.Find(value => value.Manifest?.ID == Mod)?.Instance?.GetType().Assembly;
        var tools = mod?.GetType("ShopAssist.MachineTools");
        var locations = tools?.GetMethod("ModuleLocations", F)?.Invoke(null, null) as System.Collections.IEnumerable;
        var descriptions = new List<string>();
        if (locations != null) foreach (object location in locations)
        {
            var inventory = Field(location, "Inventory"); var owner = Field(location, "Owner") as GameItem;
            descriptions.Add((owner?.GetDisplayName(true) ?? "root") + ":" + Field(location, "Role") +
                (ReferenceEquals(inventory, otherBay) ? "[otherBay]" : ""));
        }
        return "hit=" + hit + " sprite=" + sprite + " ownerMachineType=" + other.IsGameItemType("MACHINE") +
            " target=" + other.GetDisplayName(true) + " owned=" + GeneralHelper.IsItemEffectivelyOwned(remote) +
            " parent=" + ReferenceEquals(remote.parentInventory, otherBay) + " visible=" + other.contentWindow.IsVisible() +
            " locked=" + other.contentWindow.isLocked + " panelHidden=" + panel?.isHidden + " nodes=" + nodes?.Count +
            " shape=" + remote.modifiedShape.minX + "," + remote.modifiedShape.minY + "/" + remote.modifiedShape.globalWidth + "x" + remote.modifiedShape.globalHeight +
            " branch=" + string.Join("/", branch) + " locations=" + string.Join(";", descriptions) +
            " text=" + string.Join("|", browser.root.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(text => text.text));
    }
    private static IEnumerable<MonoBehaviour> Hits(CustomUIWindow window) => window.root.GetComponentsInChildren<MonoBehaviour>(true)
        .Where(value => value != null && value.GetType().FullName == "ShopAssist.ItemGrid+Hit" && value.gameObject.activeInHierarchy);
    private static MonoBehaviour FindHit(CustomUIWindow window, GameItem item) => Hits(window)
        .FirstOrDefault(hit => hit.gameObject.name == "item_cell" && ReferenceEquals(Field(Field(hit, "entry"), "Item"), item));
    private static MonoBehaviour FindBoardHit(CustomUIWindow window, GameItem item, GameGridInventory inventory) => Hits(window)
        .FirstOrDefault(hit => hit.gameObject.name == "item_cell" && ReferenceEquals(Field(Field(hit, "entry"), "Item"), item) &&
            ReferenceEquals(Field(Field(Field(hit, "view"), "Options"), "Inventory"), inventory) &&
            !(bool)Field(Field(Field(hit, "view"), "Options"), "Proxy"));
    private static MonoBehaviour BoardView(CustomUIWindow window, GameGridInventory inventory) => window.root.GetComponentsInChildren<MonoBehaviour>(true)
        .FirstOrDefault(view => view != null && view.GetType().FullName == "ShopAssist.ItemGrid+View" && view.gameObject.activeInHierarchy &&
            ReferenceEquals(Field(Field(view, "Options"), "Inventory"), inventory) && !(bool)Field(Field(view, "Options"), "Proxy"));
    private static MonoBehaviour PreviewHit(CustomUIWindow window, GameItem item) => Hits(window).FirstOrDefault(hit =>
    {
        object entry = Field(hit, "entry"), options = Field(Field(hit, "view"), "Options");
        return ReferenceEquals(Field(entry, "Item"), item) && Field(options, "Board") != null && Field(options, "Inventory") == null;
    });
    private static void CheckWorkbench(CustomUIWindow window, GameItem sourceItem, GameGridInventory bay)
    {
        Canvas.ForceUpdateCanvases();
        var layer = CustomUIManager.Instance.GetLayerRoot(CustomUIManager.LAYER_META);
        Check(layer != null, "Workbench canvas is missing");
        float maxHeight = Mathf.Min(layer.rect.height * .8f, layer.rect.height - 32);
        Check(window.Rect.rect.width >= Mathf.Min(760, layer.rect.width * .8f) - 2 &&
            window.Rect.rect.width <= Mathf.Min(layer.rect.width * .8f, layer.rect.width - 32) + 2 &&
            window.Rect.rect.height >= layer.rect.height * .4f - 2 && window.Rect.rect.height <= maxHeight + 2 &&
            window.Rect.rect.height < maxHeight - 2, "Workbench did not adapt its height to the small inventory");
        Check(window.Get("attribute")?.dropdown != null, "Module attribute selector is missing");
        var source = (MonoBehaviour)Field(FindHit(window, sourceItem), "view"); var target = BoardView(window, bay);
        Check(source != null && target != null && !(bool)Field(Field(source, "Options"), "Cards") &&
            (float)Field(source, "Cell") >= 32, "Source is not a dense real-shape grid");
        var sourceRect = (RectTransform)Field(source, "Rect");
        var dock = window.root.GetComponentsInChildren<RectTransform>().Single(rect => rect.name == "ShopAssist_ModuleDock");
        var sourceScroll = sourceRect.GetComponentInParent<ScrollRect>();
        Check(sourceScroll != null && sourceRect.rect.width >= window.Rect.rect.width - 80 &&
            dock.rect.width < sourceRect.rect.width * .5f, "A full-height sidebar still takes space away from the module inventory");
        var reserved = (RectInt)Field(Field(source, "Options"), "Reserved");
        Check(reserved.width > 0 && reserved.height > 0, "Current machine bay has no protected display area");
        foreach (var hit in Hits(window).Where(hit => ReferenceEquals(Field(hit, "view"), source) && Field(hit, "entry") != null))
        {
            var shown = (GridShape)Field(hit, "shownShape");
            for (int y = shown.minY; y < shown.minY + shown.globalHeight; y++)
            for (int x = shown.minX; x < shown.minX + shown.globalWidth; x++)
                Check(shown.Get(x, y) != 1 || !reserved.Contains(new Vector2Int(x, y)), "An inventory item overlaps the current machine bay");
        }
    }
    private static void CheckText(CustomUIWindow window, string id) => CheckVisible(window.Get(id)?.text, id);
    private static void CheckWindowShield(CustomUIWindow window)
    {
        // Native inventory raycasts deliberately skip ordinary Unity UI. A real item behind
        // our window must stop being a tooltip/click target, then become reachable on hide.
        Vector2 original = window.Rect.anchoredPosition;
        GameItem behind = null; Vector2 point = default;
        window.Hide(); Canvas.ForceUpdateCanvases();
        foreach (GameItem item in EmporiumEntry.Instance.invElement.childItems)
        {
            var element = item as GameItemElement;
            if (element?.background?.rectTransform == null) continue;
            Vector2 candidatePoint = Center(element.background.rectTransform);
            if (candidatePoint.x <= 0 || candidatePoint.x >= Screen.width || candidatePoint.y <= 0 || candidatePoint.y >= Screen.height) continue;
            if (ReferenceEquals(RenderHandler.RaycastFindParentElement<GameItem>(candidatePoint), item))
            { behind = item; point = candidatePoint; break; }
        }
        Check(behind != null, "No actual background item was available for the input shielding regression");
        try
        {
            window.Show();
            RectTransform parent = (RectTransform)window.Rect.parent;
            Check(RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, point, CameraFor(parent), out Vector3 world),
                "Could not position the workbench over the native item");
            window.Rect.position += world - window.Rect.TransformPoint(window.Rect.rect.center);
            Canvas.ForceUpdateCanvases();
            Check(RenderHandler.RaycastFindParentElement<GameItem>(point) == null &&
                RenderHandler.RaycastElement<TooltipListener>(point) == null,
                "A native inventory item or tooltip remained reachable through the workbench");
            window.Hide(); Canvas.ForceUpdateCanvases();
            Check(ReferenceEquals(RenderHandler.RaycastFindParentElement<GameItem>(point), behind),
                "Hiding the workbench did not restore native inventory input");
        }
        finally { window.Show(); window.Rect.anchoredPosition = original; Canvas.ForceUpdateCanvases(); }
    }
    private static void CheckVisible(TMPro.TMP_Text text, string id)
    {
        Canvas.ForceUpdateCanvases();
        Check(text != null && text.gameObject.activeInHierarchy && text.font != null, "Missing active text " + id);
        text.ForceMeshUpdate();
        Check(text.textInfo.characterInfo.Take(text.textInfo.characterCount).Any(character => character.isVisible),
            "Text generated no visible glyphs: " + id + "=" + text.text + " rect=" + text.rectTransform.rect);
    }
    private static void CheckPreviewFits(CustomUIWindow window, GameGridInventory bay, GameItem candidate)
    {
        var current = (RectTransform)Field(BoardView(window, bay), "Rect");
        var preview = (RectTransform)Field(Field(PreviewHit(window, candidate), "view"), "Rect");
        var viewport = current.GetComponentInParent<ScrollRect>()?.viewport;
        Check(viewport != null, "Preview viewport missing");
        var viewCorners = new Vector3[4]; viewport.GetWorldCorners(viewCorners);
        foreach (var board in new[] { current, preview })
        {
            var corners = new Vector3[4]; board.GetWorldCorners(corners);
            Check(corners[0].y >= viewCorners[0].y - .5f && corners[1].y <= viewCorners[1].y + .5f,
                "Normal-sized current or preview bay is clipped vertically");
        }
    }
    private static GridShape FreePose(GameGridInventory inventory, GameItem item)
    {
        for (int y = 0; y < inventory.inventoryShape.height; y++)
        for (int x = 0; x < inventory.inventoryShape.width; x++)
        {
            GridShape pose = new GridShapeBuilder(item.modifiedShape).SetPosition(x, y).Build();
            SlotMarker slot = inventory.TryInventorySlot(item, item.unitCount, pose);
            if (slot != null && slot.targetItem == null && slot.numTransfer >= item.unitCount) return pose;
        }
        return null;
    }
    private static void MarqueeAll(MonoBehaviour view)
    {
        Check(view != null, "Selection view missing"); Canvas.ForceUpdateCanvases();
        var rect = (RectTransform)Field(view, "Rect");
        var tile = rect.GetComponentsInChildren<Image>(true).Where(image => image.gameObject.name.StartsWith("cell_") &&
            image.GetComponents<MonoBehaviour>().Any(component => component != null && ReferenceEquals(Field(component, "view"), view))).LastOrDefault();
        Check(tile != null, "No empty grid tile available for marquee selection");
        Vector2 start = Center((RectTransform)tile.transform);
        var ray = new PointerEventData(EventSystem.current) { position = start };
        var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(ray, hits);
        Check(hits.Count > 0 && ReferenceEquals(hits[0].gameObject, tile.gameObject), "Marquee did not start on an unobstructed empty tile");
        var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = start };
        ExecuteEvents.Execute(tile.gameObject, pointer, ExecuteEvents.beginDragHandler);
        Vector3 end = new Vector3(rect.rect.xMin + .5f, rect.rect.yMax - .5f);
        pointer.position = RectTransformUtility.WorldToScreenPoint(CameraFor(rect), rect.TransformPoint(end));
        ExecuteEvents.Execute(tile.gameObject, pointer, ExecuteEvents.dragHandler);
        ExecuteEvents.Execute(tile.gameObject, pointer, ExecuteEvents.endDragHandler);
    }
    private static Camera CameraFor(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>(); return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
    }
    private void CheckHeaderDoesNotReceiveItems(CustomUIWindow browser)
    {
        var source = FindHit(browser, candidate);
        GameInventory original = candidate.parentInventory; GridShape originalShape = candidate.modifiedShape.Clone();
        var pointer = Begin(source, out _);
        var button = browser.Get("layout").button;
        pointer.position = Center((RectTransform)button.transform);
        pointer.pointerCurrentRaycast = new RaycastResult { gameObject = button.gameObject };
        ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.dragHandler);
        ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.endDragHandler);
        Check(ReferenceEquals(candidate.parentInventory, original) && Same(candidate.modifiedShape, originalShape),
            "Dropping on the bay controls sent the item into storage");
    }
    private static Vector2 Center(RectTransform rect) => RectTransformUtility.WorldToScreenPoint(CameraFor(rect), rect.TransformPoint(rect.rect.center));
    private static void Click(MonoBehaviour hit)
    {
        Check(hit != null, "Item cell not present"); var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left,
            position = Center((RectTransform)hit.transform) };
        ExecuteEvents.Execute(hit.gameObject, pointer, ExecuteEvents.pointerClickHandler);
    }
    private static PointerEventData Begin(MonoBehaviour hit, out Vector2 grab)
    {
        Canvas.ForceUpdateCanvases();
        var view = Field(hit, "view"); var viewRect = (RectTransform)Field(view, "Rect"); float cell = (float)Field(view, "Cell");
        var pose = (GridShape)Field(hit, "shownShape"); Vector2 position = Center((RectTransform)hit.transform);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(viewRect, position, CameraFor(viewRect), out Vector2 local);
        grab = new Vector2(Mathf.Clamp(Mathf.FloorToInt((local.x - viewRect.rect.xMin) / cell) - pose.minX, 0, pose.globalWidth - 1),
            Mathf.Clamp(Mathf.FloorToInt((viewRect.rect.yMax - local.y) / cell) - pose.minY, 0, pose.globalHeight - 1));
        var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = position };
        ExecuteEvents.Execute(hit.gameObject, pointer, ExecuteEvents.beginDragHandler); return pointer;
    }
    private bool CheckDragTooltip(CustomUIWindow browser)
    {
        var hit = FindHit(browser, candidate); Check(hit != null, "Missing item cell for drag/hover test");
        var tooltip = OverlayHandler.current.GetTooltipWindow();
        var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left,
            position = Center((RectTransform)hit.transform) };
        ExecuteEvents.Execute(hit.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
        TickGrid();
        Check(tooltip.IsVisible(), "Hovering the actual item cell did not show its native tooltip");
        Vector2 mouse = Input.mousePosition;
        if (mouse.x > 2 && mouse.x < Screen.width - 2 && mouse.y > 2 && mouse.y < Screen.height - 2)
        {
            Vector3 original = browser.Rect.position;
            try
            {
                var parent = (RectTransform)browser.Rect.parent;
                Check(RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, mouse, CameraFor(parent), out Vector3 world),
                    "Could not align the grid with the native pointer for tooltip validation");
                var cell = (RectTransform)hit.transform;
                browser.Rect.position += world - cell.TransformPoint(cell.rect.center);
                Canvas.ForceUpdateCanvases();
                TickGrid();
                typeof(ItemTooltipHandler).GetMethod("OnEventUpdate", F).Invoke(ItemTooltipHandler.current, null);
                Check(tooltip.IsVisible(), "The native tooltip update erased the hovered module tooltip");
                Debug.Log("[PLAYER-QA] Native tooltip tick preserved the actual grid hover at " + mouse);
            }
            finally { browser.Rect.position = original; Canvas.ForceUpdateCanvases(); }
        }
        else Debug.Log("[PLAYER-QA] Native tooltip tick skipped because the real pointer is outside the rendered viewport; window shielding remains verified");
        Begin(hit, out _);
        Check(Field(hit, "ghost") is GameObject && !tooltip.IsVisible(), "Beginning a grid drag did not hide its owned tooltip");
        TickGrid();
        Check(!tooltip.IsVisible(), "An owned tooltip returned while that card was being dragged");
        ExecuteEvents.Execute(hit.gameObject, pointer, ExecuteEvents.pointerExitHandler);
        hit.gameObject.SetActive(false); hit.gameObject.SetActive(true);

        var handler = ItemMouseDragHandler.current;
        var item = moving as GameItemElement;
        Check(handler != null && item != null && !handler.IsDraggingItem, "Native drag handler is unavailable");
        var type = typeof(ItemMouseDragHandler); GameInventory home = item.parentInventory; GridShape shape = item.modifiedShape.Clone();
        type.GetField("currentItem", F).SetValue(handler, item);
        type.GetMethod("StartDrag", F).Invoke(handler, null);
        Check(handler.IsDraggingItem, "Could not start the actual native item drag");
        bool singleTooltip;
        try
        {
            ExecuteEvents.Execute(hit.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            TickGrid();
            singleTooltip = tooltip.IsVisible();
        }
        finally
        {
            ExecuteEvents.Execute(hit.gameObject, pointer, ExecuteEvents.pointerExitHandler);
            item.ToggleFakeBackground(false); item.Validate();
            type.GetMethod("AbortDrag", F).Invoke(handler, null);
            Check(item.parentInventory == home && Same(item.modifiedShape, shape), "Native hover test moved the source item");
        }
        var group = ItemMultiSelectHandler.current;
        var first = candidate as GameItemElement;
        Check(group != null && first != null && first.parentInventory == item.parentInventory, "Native group drag fixture is unavailable");
        GridShape firstShape = first.modifiedShape.Clone();
        var selected = (System.Collections.IList)typeof(ItemMultiSelectHandler).GetField("selectedItems", F).GetValue(group);
        var homes = (System.Collections.IDictionary)typeof(ItemMultiSelectHandler).GetField("selectedHomes", F).GetValue(group);
        selected.Clear(); homes.Clear(); selected.Add(first); selected.Add(item); homes.Add(first, home); homes.Add(item, home);
        var state = typeof(ItemMultiSelectHandler).GetField("state", F);
        state.SetValue(group, Enum.Parse(state.FieldType, "PendingGroupDrag"));
        ExecuteEvents.Execute(hit.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
        TickGrid();
        Check(tooltip.IsVisible(), "Pre-drag card tooltip was not shown");
        typeof(ItemMultiSelectHandler).GetMethod("StartGroupDrag", F).Invoke(group, null);
        Check(group.IsGroupDragging, "Could not start the actual native group drag");
        bool groupTooltip;
        try
        {
            Check(!tooltip.IsVisible(), "Starting a group drag did not hide the owned card tooltip");
            TickGrid();
            groupTooltip = tooltip.IsVisible();
        }
        finally
        {
            ExecuteEvents.Execute(hit.gameObject, pointer, ExecuteEvents.pointerExitHandler);
            group.Cancel();
        }
        Check(first.parentInventory == home && item.parentInventory == home && Same(first.modifiedShape, firstShape) && Same(item.modifiedShape, shape),
            "Native group hover test changed an item");
        return singleTooltip || groupTooltip;
    }
    private static void DragToBoard(MonoBehaviour source, MonoBehaviour target, GridShape destination)
    {
        var pointer = Begin(source, out Vector2 grab);
        var rect = (RectTransform)Field(target, "Rect"); float cell = (float)Field(target, "Cell");
        Vector3 local = new Vector3((destination.minX + grab.x + .5f) * cell, -(destination.minY + grab.y + .5f) * cell);
        pointer.position = RectTransformUtility.WorldToScreenPoint(CameraFor(rect), rect.TransformPoint(local));
        var tile = rect.GetComponentsInChildren<Image>().First(image => image.gameObject.name.StartsWith("cell_"));
        pointer.pointerCurrentRaycast = new RaycastResult { gameObject = tile.gameObject };
        ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.dragHandler);
        Check(rect.GetComponentsInChildren<Image>().Any(image => image.gameObject.name == "preview" && image.color.g > image.color.r),
            "A legal exact-cell drop did not show a green preview");
        ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.endDragHandler);
    }
    private static GridShape DragRotatedToBoard(MonoBehaviour source, MonoBehaviour target, GameItem item, GameGridInventory inventory)
    {
        Check(source != null && target != null, "Rotated drag views are missing");
        var pointer = Begin(source, out Vector2 oldGrab);
        GridShape old = (GridShape)Field(source, "dragShape"); Check(old != null, "Grid drag did not start");
        int oldWidth = old.globalWidth;
        source.GetType().GetMethod("RotateDrag", F).Invoke(source, new object[] { 1 });
        GridShape rotated = (GridShape)Field(source, "dragShape"); Vector2 grab = (Vector2)Field(source, "grab");
        Check(grab == new Vector2(oldGrab.y, oldWidth - 1 - oldGrab.x) &&
            old.Get(old.minX + (int)oldGrab.x, old.minY + (int)oldGrab.y) == rotated.Get(rotated.minX + (int)grab.x, rotated.minY + (int)grab.y),
            "Rotation did not follow the grabbed occupied cell");
        GridShape destination = null;
        for (int y = 0; y < inventory.inventoryShape.height && destination == null; y++)
        for (int x = 0; x < inventory.inventoryShape.width && destination == null; x++)
        {
            GridShape pose = new GridShapeBuilder(rotated).SetPosition(x, y).Build();
            SlotMarker slot = inventory.TryInventorySlot(item, item.unitCount, pose);
            if (slot != null && slot.targetItem == null && slot.numTransfer >= item.unitCount) destination = pose;
        }
        Check(destination != null, "No valid rotated destination in the current bay");
        var rect = (RectTransform)Field(target, "Rect"); float cell = (float)Field(target, "Cell");
        Vector3 local = new Vector3((destination.minX + grab.x + .5f) * cell, -(destination.minY + grab.y + .5f) * cell);
        pointer.position = RectTransformUtility.WorldToScreenPoint(CameraFor(rect), rect.TransformPoint(local));
        var tile = rect.GetComponentsInChildren<Image>().First(image => image.gameObject.name.StartsWith("cell_"));
        pointer.pointerCurrentRaycast = new RaycastResult { gameObject = tile.gameObject };
        ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.dragHandler);
        Check(rect.GetComponentsInChildren<Image>().Any(image => image.gameObject.name == "preview" && image.color.g > image.color.r),
            "A legal rotated drop did not show a green preview");
        ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.endDragHandler);
        return destination;
    }
    private static void DragToProxy(MonoBehaviour source, MonoBehaviour target)
    {
        var pointer = Begin(source, out _); var rect = (RectTransform)Field(target, "Rect");
        var tile = rect.GetComponentsInChildren<Image>().First(image => image.gameObject.name.StartsWith("cell_"));
        pointer.position = Center((RectTransform)tile.transform);
        pointer.pointerCurrentRaycast = new RaycastResult { gameObject = tile.gameObject };
        ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.dragHandler);
        ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.endDragHandler);
    }
    private static bool DragToNative(MonoBehaviour source, GameItem item, GameGridInventory target)
    {
        var rect = target.rectTransform; float cell = UISettings.current.gridSize;
        var pointer = Begin(source, out Vector2 grab);
        for (int y = 0; y < target.inventoryShape.height; y++) for (int x = 0; x < target.inventoryShape.width; x++)
        {
            GridShape shape = new GridShapeBuilder(item.modifiedShape).SetPosition(x, y).Build();
            var slot = target.TryInventorySlot(item, item.unitCount, shape);
            if (slot == null || slot.numTransfer < item.unitCount || slot.targetItem != null) continue;
            Vector3 local = new Vector3((x + grab.x + .5f) * cell, -(y + grab.y + .5f) * cell);
            Vector2 point = RectTransformUtility.WorldToScreenPoint(CameraFor(rect), rect.TransformPoint(local));
            var ray = new PointerEventData(EventSystem.current) { position = point };
            var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(ray, hits);
            if (hits.Count == 0 || hits[0].gameObject.GetComponent<TreeNodeRender>()?.FindParentElement<GameGridInventory>() != target) continue;
            pointer.position = point; pointer.pointerCurrentRaycast = hits[0];
            ExecuteEvents.Execute(source.gameObject, pointer, ExecuteEvents.endDragHandler); return true;
        }
        source.gameObject.SetActive(false); source.gameObject.SetActive(true);
        return false;
    }
}
