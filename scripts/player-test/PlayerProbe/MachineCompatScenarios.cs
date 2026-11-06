using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

// Fixtures use the native inventory and options controls inside the isolated player.
public abstract class EquipmentFixture
{
    protected const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private bool entered;
    protected int step;
    protected bool Ready()
    {
        if (!entered) { entered = start.Tick(); return false; }
        if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;
        if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
        return true;
    }
    protected static GameItem Stock(string id)
    {
        var item = FeedScenario.New(id);
        FeedScenario.Put(EmporiumEntry.Instance.invElement, item);
        return item;
    }
    protected static T Panel<T>(GameItem host, string role) where T : GameInventory =>
        MachineryContainerUtils.Collect(((MachineryPanelRow)host.contentWindow.childElement).GetPanel(role).content).OfType<T>().Single();
    protected static void Open(GameItem item) => ItemMouseDoubleClickHandler.current.OpenContentAction(item, new Vector2(290, 190));
    protected static CustomUIWindow Ui(string id) => CustomUIManager.Instance.GetWindow(Mod + "." + id);
    protected static string Words(CustomUIWindow w) => string.Join(" | ", w.root.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text));
    protected static void Check(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    protected static string MaintenanceState()
    {
        var assembly = ModLoader.Mods.Single(m => m.Manifest.ID == Mod).Assembly;
        var type = assembly.GetType("ShopAssist.MachineTools");
        var active = type.GetField("current", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var pending = (System.Collections.IDictionary)type.GetField("pendingFilters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(active);
        bool free = (bool)assembly.GetType("ShopAssist.ShopInput").GetMethod("Ready", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        return "pending=" + pending.Count + " inputReady=" + free + " dialog=" + (DialogUIManager.Instance?.currentDialog != null) +
            "/" + (DialoguePhoneUIManager.Instance?.currentDialog != null) + "/" + StoreUIManager.Instance.isAboutToStartMainDialog;
    }
    protected static void Action(string key) => RoutingScenario.OpenOptionAction(key);
    protected static GameGridInventory Supply(GameItem host, int kind, params GameItem[] spares)
    {
        var bag = Stock("backpack_medium");
        GeneralHelper.SetCustomName(bag, "QA parts supply");
        var source = (GameGridInventory)bag.contentWindow.childElement;
        foreach (var spare in spares) FeedScenario.Put(source, spare);
        Open(host); Open(bag);
        Action("f" + kind + ".open");
        var settings = Ui("maintenance");
        QaItemUi.Choose(settings, "source", "QA parts supply");
        settings = Ui("maintenance");
        if (kind == 31) settings.Get("threshold").SetFloat(50, true);
        settings.Get("enable").button.onClick.Invoke();
        return source;
    }
}

public class EvaporatorLayoutScenario : EquipmentFixture
{
    private readonly string model;
    public EvaporatorLayoutScenario() : this("evaporator") { }
    protected EvaporatorLayoutScenario(string model) { this.model = model; }
    private GameItem machine, installed, addition;
    private GameGridInventory bay;
    private GridShape origin;
    public bool Tick(out string result)
    {
        result = null;
        if (!Ready()) return false;
        if (step++ == 0)
        {
            TradeScenario.SetOptions(true, "f10.enabled", "f12.enabled");
            machine = Stock(model); bay = Panel<GameGridInventory>(machine, "module");
            if (model == "evaporator") Check(bay.inventoryShape.width == 4 && bay.inventoryShape.height == 4, "Not the native 4x4 bay");
            installed = Stock("system_module_quality"); addition = Stock("system_module_performance");
            GeneralHelper.SetCustomName(addition, "QA selected module");
            FeedScenario.Put(bay, installed); origin = installed.modifiedShape.Clone();
            Open(machine); machine.contentWindow.ToFront(); Action("f10.open"); return false;
        }
        var browser = Ui("f10"); Check(browser.Get("compatible") != null && browser.Get("category") != null && browser.Get("search") != null, "Missing module filters");
        LayoutScenario.Select(browser, addition); browser.Get("layout").button.onClick.Invoke();
        Ui("f10").Get("preview").button.onClick.Invoke();
        var layout = Ui("f10");
        Check(layout.Get("apply") != null && addition.parentInventory != bay, "Missing native preview: " + Words(layout));
        layout.Get("apply").button.onClick.Invoke();
        Check(addition.parentInventory == bay && installed.parentInventory == bay &&
            machine.GetTagReadonly("TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT").GetInt() > 0, "Native module installation failed");
        if (model == "evaporator") Check(installed.modifiedShape.minX == origin.minX && installed.modifiedShape.minY == origin.minY &&
            installed.modifiedShape.orientation == origin.orientation && installed.modifiedShape.flipped == origin.flipped, "Moved occupied evaporator cells");
        result = "PASS native " + model + " module bay UI preview, apply, native totals and existing instance retained"; return true;
    }
}
public sealed class PrinterLayoutScenario : EvaporatorLayoutScenario { public PrinterLayoutScenario() : base("printer") { } }
public sealed class FurnaceLayoutScenario : EvaporatorLayoutScenario { public FurnaceLayoutScenario() : base("furnace") { } }

public sealed class ModulePagesScenario : EquipmentFixture
{
    private HashSet<GameItem> fixtures;
    private readonly HashSet<GameItem> seen = new HashSet<GameItem>();
    private GameItem last, wide;
    private GameGridInventory stock;
    private bool selected, wideChecked, usedBelowBay;
    private int pageCount, pagesBack, frame;
    public bool Tick(out string result)
    {
        result = null; if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f10.enabled", "f12.enabled");
            var machine = Stock("evaporator"); var box = Stock("backpack_large");
            stock = (GameGridInventory)box.contentWindow.childElement; stock.SetShape(36, 36);
            fixtures = new HashSet<GameItem>();
            for (int i = 0; i < 101; i++) { last = FeedScenario.New("system_module_performance"); FeedScenario.Put(stock, last); fixtures.Add(last); }
            GeneralHelper.SetCustomName(last, "QA module beyond 100");
            Open(machine); Action("f10.open");
            Canvas.ForceUpdateCanvases();
            var header = Ui("f10").Get("source_title").root.transform;
            var scroll = header.parent.parent.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
            Check(scroll?.viewport != null && scroll.viewport.rect.width > 0, "Cannot measure the source viewport");
            int wideColumns = Mathf.CeilToInt(scroll.viewport.rect.width / 34) + 3;
            stock.SetShape(Math.Max(36, wideColumns + 1), 36);
            wide = FeedScenario.New("system_module_performance");
            wide.SetShape(new GridShapeBuilder(wideColumns, 1).SetDataFill(1).Build());
            GeneralHelper.SetCustomName(wide, "QA wide module"); FeedScenario.Put(stock, wide); fixtures.Add(wide);
            wide.ValidateShapeState();
            Check(wide.modifiedShape.globalWidth == wideColumns && wide.modifiedShape.globalHeight == 1 &&
                ReferenceEquals(wide.parentInventory, stock), "Wide fixture did not retain its real shape after native placement and validation");
            Ui("f10").Get("storage_tab").button.onClick.Invoke(); Ui("f10").Get("idle_tab").button.onClick.Invoke();
            Canvas.ForceUpdateCanvases();
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-module-grid-many.png"));
            frame = Time.frameCount + 3; step = 1; return false;
        }
        if (Time.frameCount < frame) return false;
        if (step == 1)
        {
            var view = Ui("f10"); pageCount++;
            Check(pageCount <= 20, "Module pagination did not terminate");
            foreach (GameItem item in fixtures) if (QaItemUi.Hit(view.root, item) != null) seen.Add(item);
            usedBelowBay |= UsesSpaceBelowBay(view, fixtures);
            if (!selected && QaItemUi.Hit(view.root, last) != null) { LayoutScenario.Select(view, last); selected = true; }
            if (QaItemUi.Hit(view.root, wide) != null)
            {
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                    ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-module-grid-wide.png"));
                frame = Time.frameCount + 3; step = 4; return false;
            }
            step = 2;
        }
        if (step == 4)
        {
            CheckWideScroll(Ui("f10"), wide); wideChecked = true; step = 2;
        }
        if (step == 2)
        {
            var view = Ui("f10");
            var next = view.Get("next")?.button;
            bool isWidePage = QaItemUi.Hit(view.root, wide) != null;
            int unusedRows = isWidePage ? 0 : PageUnusedRows(view, fixtures);
            bool moreRegular = fixtures.Any(item => item != wide && !seen.Contains(item));
            string page = view.Get("module_page")?.GetText();
            if (next != null) next.onClick.Invoke();
            if (next != null && page != Ui("f10").Get("module_page")?.GetText())
            {
                Check(!moreRegular || unusedRows <= 1, "A nonfinal page left unused rows in its viewport: " + unusedRows);
                frame = Time.frameCount + 3; step = 1; return false;
            }
            Check(pageCount > 1 && seen.Count == 102 && selected && wideChecked, "Not every physical module, including the wide one, is reachable across pages");
            Check(usedBelowBay, "The inventory left the area below the compact machine bay unused");
            pagesBack = pageCount - 1; step = 3;
        }
        if (step == 3)
        {
            if (pagesBack > 0)
            {
                var view = Ui("f10"); string page = view.Get("module_page")?.GetText();
                view.Get("prev").button.onClick.Invoke();
                Check(ReferenceEquals(view, Ui("f10")) && page != view.Get("module_page")?.GetText(),
                    "Could not return to an earlier module page without replacing the window");
                pagesBack--; frame = Time.frameCount + 3; return false;
            }
            var firstPage = Ui("f10");
            foreach (GameItem item in fixtures.Where(item => item != last && QaItemUi.Hit(firstPage.root, item) != null).Take(5))
                LayoutScenario.Select(firstPage, item);
            firstPage.Get("layout").button.onClick.Invoke(); Ui("f10").Get("preview").button.onClick.Invoke();
            Check(Words(Ui("f10")).Contains(ModHelper.GetLocalized(Mod, "grid.too_many").Replace("{0}", "6")) &&
                fixtures.All(item => item.parentInventory == stock), "More than five candidates did not produce explicit feedback without moving items");
            Ui("f10").Get("installed_tab").button.onClick.Invoke(); Ui("f10").Get("idle_tab").button.onClick.Invoke();
            Check(last.parentInventory == stock, "Changing browser pages moved a module");
            result = "PASS 102 physical modules across dense pages, wide-item scrolling, five-candidate limit and retained original source"; return true;
        }
        throw new InvalidOperationException("Unknown module pages QA step " + step);
    }

    private static int PageUnusedRows(CustomUIWindow window, IEnumerable<GameItem> items)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object Field(object value, string name) => value?.GetType().GetField(name, flags)?.GetValue(value);
        var hit = items.Select(item => QaItemUi.Hit(window.root, item)).FirstOrDefault(value => value != null);
        var view = Field(hit, "view"); var rect = (RectTransform)Field(view, "Rect");
        var scroll = rect.GetComponentInParent<UnityEngine.UI.ScrollRect>();
        int rows = (int)Field(view, "Rows"); float cell = (float)Field(view, "Cell");
        int visibleRows = Mathf.FloorToInt(scroll.viewport.rect.height / cell);
        return visibleRows - rows;
    }

    private static bool UsesSpaceBelowBay(CustomUIWindow window, IEnumerable<GameItem> items)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object Field(object value, string name) => value?.GetType().GetField(name, flags)?.GetValue(value);
        foreach (var item in items)
        {
            var hit = QaItemUi.Hit(window.root, item); if (hit == null) continue;
            var shape = Field(hit, "shownShape") as GridShape;
            var options = Field(Field(hit, "view"), "Options");
            var reserved = (RectInt)Field(options, "Reserved");
            if (shape != null && reserved.width > 0 && shape.minY >= reserved.yMax &&
                shape.minX + shape.globalWidth > reserved.xMin) return true;
        }
        return false;
    }

    private static void CheckWideScroll(CustomUIWindow window, GameItem item)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object Field(object value, string name) => value?.GetType().GetField(name, flags)?.GetValue(value);
        var hit = QaItemUi.Hit(window.root, item); var view = Field(hit, "view");
        var rect = (RectTransform)Field(view, "Rect"); var scroll = rect.GetComponentInParent<UnityEngine.UI.ScrollRect>();
        Canvas.ForceUpdateCanvases();
        var shape = item.modifiedShape;
        string details = " shape=" + shape.globalWidth + "x" + shape.globalHeight + " orientation=" + shape.orientation +
            " columns=" + Field(view, "Columns") + " grid=" + rect.rect +
            " viewport=" + (scroll?.viewport == null ? "null" : scroll.viewport.rect.ToString()) +
            " content=" + (scroll?.content == null ? "null" : scroll.content.rect.ToString()) +
            " preferred=" + (scroll?.content == null ? -1 : UnityEngine.UI.LayoutUtility.GetPreferredWidth(scroll.content)) +
            " horizontal=" + (scroll != null && scroll.horizontal) +
            " scrollbar=" + (scroll?.horizontalScrollbar != null) +
            " anchors=" + (scroll?.content == null ? "null" : scroll.content.anchorMin + "/" + scroll.content.anchorMax);
        Debug.Log("[PLAYER-QA] Wide module geometry:" + details);
        Check(scroll != null && scroll.horizontal && scroll.viewport != null && scroll.content.rect.width > scroll.viewport.rect.width &&
            (int)Field(view, "Columns") >= item.modifiedShape.globalWidth, "Oversized module is not horizontally scrollable:" + details);
        var bar = scroll.horizontalScrollbar;
        Check(bar != null && bar.isActiveAndEnabled && bar.handleRect != null, "Wide module has no usable horizontal scrollbar");
        Canvas canvas = bar.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Vector2 start = RectTransformUtility.WorldToScreenPoint(camera, bar.handleRect.TransformPoint(bar.handleRect.rect.center));
        var pointer = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
        { position = start, pressPosition = start, button = UnityEngine.EventSystems.PointerEventData.InputButton.Left };
        var hits = new List<UnityEngine.EventSystems.RaycastResult>();
        UnityEngine.EventSystems.EventSystem.current.RaycastAll(pointer, hits);
        string hitDetails = " pointer=" + start + " track=" + ScreenBounds((RectTransform)bar.transform, camera) +
            " handle=" + ScreenBounds(bar.handleRect, camera) + " viewport=" + ScreenBounds(scroll.viewport, camera) +
            " sliding=" + ScreenBounds((RectTransform)bar.handleRect.parent, camera) + " barValue=" + bar.value + " barSize=" + bar.size +
            " handleActive=" + bar.handleRect.gameObject.activeInHierarchy + " handleRaycast=" + (bar.targetGraphic != null && bar.targetGraphic.raycastTarget) +
            " hits=" + string.Join(" | ", hits.Take(8).Select(value => value.gameObject.name + " parent=" +
                value.gameObject.transform.parent?.name + " via=" + value.module?.name + " depth=" + value.depth));
        Debug.Log("[PLAYER-QA] Wide module scrollbar:" + hitDetails);
        Check(hits.Count > 0 && (hits[0].gameObject == bar.gameObject || hits[0].gameObject.transform.IsChildOf(bar.transform)),
            "Horizontal scrollbar handle is not reachable by pointer:" + hitDetails);
        pointer.pointerPressRaycast = hits[0]; pointer.pointerCurrentRaycast = hits[0];
        UnityEngine.EventSystems.ExecuteEvents.Execute(bar.gameObject, pointer, UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
        UnityEngine.EventSystems.ExecuteEvents.Execute(bar.gameObject, pointer, UnityEngine.EventSystems.ExecuteEvents.beginDragHandler);
        var corners = new Vector3[4]; ((RectTransform)bar.transform).GetWorldCorners(corners);
        pointer.position = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        UnityEngine.EventSystems.ExecuteEvents.Execute(bar.gameObject, pointer, UnityEngine.EventSystems.ExecuteEvents.dragHandler);
        UnityEngine.EventSystems.ExecuteEvents.Execute(bar.gameObject, pointer, UnityEngine.EventSystems.ExecuteEvents.endDragHandler);
        UnityEngine.EventSystems.ExecuteEvents.Execute(bar.gameObject, pointer, UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
        Canvas.ForceUpdateCanvases();
        Check(scroll.horizontalNormalizedPosition > .95f, "Dragging the horizontal scrollbar did not scroll to the end");
        var cells = window.root.GetComponentsInChildren<MonoBehaviour>(true).Where(value => value != null &&
            value.gameObject.name == "item_cell" && ReferenceEquals(Field(Field(value, "entry"), "Item"), item)).ToList();
        Check(cells.Count > 0, "Wide module has no occupied cells");
        var viewport = new Vector3[4]; scroll.viewport.GetWorldCorners(viewport);
        Vector3 rightmost = cells.OrderBy(cell => cell.transform.position.x).Last().transform.position;
        Check(rightmost.x <= viewport[2].x + 1, "Right edge of the wide module remains outside the viewport after scrolling");
        scroll.horizontalNormalizedPosition = 0;
    }

    private static Rect ScreenBounds(RectTransform rect, Camera camera)
    {
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return Rect.MinMaxRect(low.x, low.y, high.x, high.y);
    }
}

public sealed class EquipmentOverviewScenario : EquipmentFixture
{
    public bool Tick(out string result)
    {
        result = null;
        if (!Ready()) return false;
        TradeScenario.SetOptions(true, "f30.enabled");
        Stock("evaporator"); Stock("printer"); Stock("furnace"); Action("f30.open");
        string view = Words(Ui("f30"));
        foreach (string key in new[] { "f30.water_container", "f30.chip", "f30.materials" })
            Check(view.Contains(ModHelper.GetLocalized(Mod, key)), "Missing state " + key + ": " + view);
        result = "PASS unopened evaporator, printer and furnace publish distinct native missing inputs"; return true;
    }
}

public sealed class PrinterMaintenanceScenario : EquipmentFixture
{
    public bool Tick(out string result)
    {
        result = null;
        if (!Ready()) return false;
        TradeScenario.SetOptions(true, "f31.enabled");
        var printer = Stock("printer"); var old = Stock("energy_credit"); var spare = Stock("energy_credit");
        var power = MachineHelper.GetBatterySlot(printer); FeedScenario.Put(power, old);
        PowerHelper.SetPowerSourceAt(old, 1); PowerHelper.SetPowerSourceAt(spare, 100);
        var source = Supply(printer, 31, spare);
        bool night = CurrentConverterHelper.isNightCycle;
        try
        {
            CurrentConverterHelper.isNightCycle = true; FeedScenario.Cycle(printer);
            Check(power.childItem == old && spare.parentInventory == source, "Printer without a job consumed a spare");
            var chip = Stock("printer_chip_pistol"); FeedScenario.Put(Panel<GameSlotInventory>(printer, "chip"), chip);
            var inputs = Panel<GameGridInventory>(printer, "input");
            for (int i = 0; i < 8 && chip.IsTag("REQUIRED_ITEM_ID_" + i); i++)
            {
                string id = chip.GetTagReadonly("REQUIRED_ITEM_ID_" + i).GetString();
                int count = chip.GetTagReadonly("REQUIRED_ITEM_COUNT_" + i).GetInt();
                for (int n = 0; n < count; n++) FeedScenario.Put(inputs, Stock(id));
            }
            Check(MachineProgressHelper.CanStart(chip, printer) && MachineProgressHelper.HasRequiredAdditionalItems(chip, inputs.childItems), "Unworkable QA printer recipe");
            CurrentConverterHelper.isNightCycle = false; FeedScenario.Cycle(printer);
            Check(power.childItem == old, "Accelerated daytime printer consumed a spare");
            CurrentConverterHelper.isNightCycle = true; FeedScenario.Cycle(printer);
            Check(power.childItem == spare && old.parentInventory == source &&
                old.GetTagReadonly("power_source_item_energy").GetInt() == 1 &&
                spare.GetTagReadonly("power_source_item_energy").GetInt() < 100, "Native night-cycle swap and energy use did not match");
        }
        finally { CurrentConverterHelper.isNightCycle = night; }
        result = "PASS printer: idle / day skipped, queued native recipe used one night battery, old battery back in registered supply"; return true;
    }
}

public sealed class FilterOwnerScenario : EquipmentFixture
{
    private GameItem pitcher, worn, spare;
    private GameSlotInventory slot;
    private GameGridInventory source;
    private ConfigEntry<bool> peer;
    private bool original;
    private float after;
    public bool Tick(out string result)
    {
        result = null;
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f35.enabled");
            pitcher = Stock("water_pitcher"); worn = Stock("kotton_fabric"); spare = Stock("kotton_fabric");
            slot = (GameSlotInventory)pitcher.contentWindow.childElement;
            FeedScenario.Put(slot, worn); source = Supply(pitcher, 35, spare);
            peer = ModConfig.Find("chiqingsan.AquaSense")?.Get("AutoReplaceCloth") as ConfigEntry<bool>;
            if (peer != null)
            {
                original = peer.Value; peer.Set(true);
                Action("f35.open"); Check(Words(Ui("maintenance")).Contains(ModHelper.GetLocalized(Mod, "f35.aqua")), "Missing AquaSense ownership notice");
                Ui("maintenance").Close(); peer.Set(false);
            }
            DurabilityHelper.SetCurrentDurability(worn, 1);
            var dirty = Stock("wine_bottle"); WaterHelper.AddLiquid(dirty, "plant_matter", 100000);
            WaterHelper.TransferLiquid(dirty, pitcher);
            Check(DurabilityHelper.GetRealDurability(worn) == 0 && slot.childItem == worn,
                "Plant matter failed to exhaust the installed cloth: durability " + DurabilityHelper.GetRealDurability(worn) + " seat " + slot.childItem?.identifier);
            after = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (slot.childItem != spare && Time.realtimeSinceStartup < after) return false;
        try { Check(slot.childItem == spare && worn.parentInventory == source, "Key disabled but registered pitcher filter was not replaced: " + MaintenanceState()); }
        finally { if (peer != null) peer.Set(original); }
        result = "PASS native same-stock kotton replacement and original retained" + (peer == null ? "" : " with AquaSense off per material"); return true;
    }
}

public sealed class RasMaintenanceScenario : EquipmentFixture
{
    private GameItem ras, exhausted, spare, remaining, advanced;
    private GameGridInventory source;
    private List<GameSlotInventory> seats;
    private float after;
    private string diagnostics;
    public bool Tick(out string result)
    {
        result = null;
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f35.enabled");
            ras = Stock("ras"); exhausted = Stock("hydroreed_filter"); spare = Stock("hydroreed_filter");
            remaining = Stock("water_filter"); advanced = Stock("water_filter_adv");
            var grid = MachineRAS.GetFilterGrid(ras);
            seats = Enumerable.Range(0, 4).Select(i => (GameSlotInventory)grid.GetElement(i, 0)).ToList();
            FeedScenario.Put(seats[1], exhausted); FeedScenario.Put(seats[2], remaining); FeedScenario.Put(seats[3], advanced);
            source = Supply(ras, 35, spare);
            var water = MachineRAS.GetWaterSlot(ras).childItem;
            if (water == null)
            {
                water = MachineRAS.CreateWaterTank(); GeneralHelper.SetItemOwned(water);
                FeedScenario.Put(MachineRAS.GetWaterSlot(ras), water);
            }
            Check(water != null, "RAS missing native tank");
            WaterHelper.AddLiquid(water, "water", 25000); WaterHelper.AddLiquid(water, "plant_matter", 6000);
            DurabilityHelper.SetCurrentDurability(exhausted, 1);
            AquacultureHelper.TickTank(ras, MachineRAS.GetTankGrid(ras), water, seats, MachineRAS.GetDispenserSlot(ras), true);
            Check(DurabilityHelper.GetRealDurability(exhausted) == 0, "Native RAS filtration did not use hydroreed");
            var own = ModLoader.Mods.Single(m => m.Manifest.ID == Mod).Assembly;
            var type = own.GetType("ShopAssist.MachineTools");
            var active = type.GetField("current", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var pending = (System.Collections.IDictionary)type.GetField("pendingFilters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(active);
            var actualSeats = (List<GameSlotInventory>)type.GetMethod("FilterSlots", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { ras });
            var swap = own.GetType("ShopAssist.MaintenanceSwap").GetMethod("Ready", BindingFlags.Static | BindingFlags.NonPublic);
            var args = new object[] { ras, seats[1], source, exhausted, spare };
            if (swap.GetParameters().Length == 6) args = args.Concat(new object[] { false }).ToArray();
            diagnostics = "pending=" + pending.Count + " native seats=" + actualSeats.Count + " ready=" + swap.Invoke(null, args) +
                " data=" + ModHelper.GetModData(Mod, "machineMaintenance", "") + " parent=" + seats[1].GetParentItem()?.identifier;
            after = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (seats[1].childItem != spare && Time.realtimeSinceStartup < after) return false;
        Check(seats[0].childItem == null && seats[1].childItem == spare && seats[2].childItem == remaining &&
            seats[3].childItem == advanced && exhausted.parentInventory == source, "RAS did not replace only the exhausted registered seat: " + diagnostics + "; later " + MaintenanceState());
        result = "PASS four RAS slots: one spent hydroreed replaced, healthy ordinary and advanced retained, empty seat unchanged"; return true;
    }
}

public sealed class FilterGradeScenario : EquipmentFixture
{
    private GameItem host, old, coarse;
    private GameGridInventory supply;
    private GameSlotInventory seat;
    private float after;
    private void Grade(int level)
    {
        Action("f35.open");
        Ui("maintenance").Get("grade").SetIndex(level, true);
        Ui("maintenance").Get("enable").button.onClick.Invoke();
    }
    private void Pour()
    {
        var bottle = Stock("wine_bottle"); WaterHelper.AddLiquid(bottle, "plant_matter", 8000);
        WaterHelper.AddLiquid(bottle, "water", 20000); WaterHelper.TransferLiquid(bottle, host);
        Check(DurabilityHelper.GetRealDurability(old) == 0, "The native filter did not exhaust");
    }
    public bool Tick(out string result)
    {
        result = null;
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f35.enabled"); host = Stock("water_pitcher");
            old = Stock("water_filter"); coarse = Stock("kotton_fabric");
            seat = (GameSlotInventory)host.contentWindow.childElement;
            FeedScenario.Put(seat, old); supply = Supply(host, 35, coarse);
            string run = Guid.Parse(PlayerStore.Instance.runID).ToString("N");
            string legacy = "{\"version\":1,\"run\":\"" + run + "\",\"slot\":" + PlayerStore.Instance.saveSlotId +
                ",\"rules\":[{\"kind\":35,\"host\":" + host.GetUniqueID() + ",\"source\":" + supply.GetParentItem().GetUniqueID() +
                ",\"page\":-1,\"threshold\":0,\"priority\":0,\"role\":\"contents\"}]}";
            ModHelper.SetModData(Mod, "machineMaintenance", legacy);
            Action("f35.open");
            var saved = Ui("maintenance");
            Check(saved.Get("allow_water_filter").GetBool() && !saved.Get("allow_kotton_fabric").GetBool() &&
                !saved.Get("allow_hydroreed_filter").GetBool() && saved.Get("grade").GetIndex() == 0 &&
                ModHelper.GetModData(Mod, "machineMaintenance", "") == legacy, "Opening older rules changed their material scope or persisted them prematurely");
            saved.Get("allow_kotton_fabric").SetBool(true, true); saved.Get("enable").button.onClick.Invoke();
            Grade(1); DurabilityHelper.SetCurrentDurability(old, 1); Pour();
            after = Time.realtimeSinceStartup + 1f; step++; return false;
        }
        if (step == 1)
        {
            if (Time.realtimeSinceStartup < after) return false;
            Check(seat.childItem == old && coarse.parentInventory == supply, "Changed to coarser material without authorization");
            Grade(2); Pour(); after = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (seat.childItem != coarse && Time.realtimeSinceStartup < after) return false;
        Check(seat.childItem == coarse && coarse.parentInventory == seat && old.parentInventory == supply &&
            DurabilityHelper.GetRealDurability(old) == 0 && DurabilityHelper.GetRealDurability(coarse) > 0 &&
            EmporiumEntry.Instance.swapBufferElement.childItem == null, "Explicit coarse replacement failed: " + MaintenanceState());
        result = "PASS no unapproved downgrade, explicit alternate material installed with independent shapes, old filter retained"; return true;
    }
}

public sealed class WorkshopKeysScenario : EquipmentFixture
{
    public bool Tick(out string result)
    {
        result = null; if (!Ready()) return false;
        var own = ModLoader.Mods.Single(m => m.Manifest.ID == Mod && m.IsEnabled);
        var lookup = own.Assembly.GetType("ShopAssist.ShopSettings").GetMethod("ShortcutOwner", BindingFlags.Static | BindingFlags.NonPublic);
        string Owner(KeyCode key) => (string)lookup.Invoke(null, new object[] { "f10.key", key });
        Check(Owner(KeyBindHelper.Get("sort_menu")) != null, "Native sort binding was not detected");
        StoreUIManager.Instance.gameOptionUIManager.OpenUI(); StoreUIManager.Instance.gameOptionUIManager.CloseUI();
        var shortcuts = GameOptionRegistry.FindSection("keybind").entries;
        var other = shortcuts.Single(e => e.id == Mod + ":f30.key");
        KeyCode original = other.getKey();
        try { other.setKey(KeyCode.F13); Check(Owner(KeyCode.F13) != null, "Restored duplicate assistant key was not detected"); }
        finally { other.setKey(original); }
        if (ModLoader.Mods.Any(m => m.Manifest.ID == "ShowItemNames" && m.IsEnabled))
        {
            Check(Owner(KeyCode.F10) != null && Owner(KeyCode.F6) != null && Owner(KeyCode.F5) != null, "Unregistered workshop hotkeys not detected");
        }
        result = "PASS native and restored duplicate shortcut ownership, and private workshop keys when loaded"; return true;
    }
}
