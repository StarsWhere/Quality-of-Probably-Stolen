using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Runs only in the guarded Player, using actual pointer events and native raycasts.
public sealed class F11TargetDragScenario
{
    private const string Mod = "starswhere.shopassist";
    // The current game's ExoticFuel maxSlotAmountItemFunc returns 99.
    private const int NearFull = 98;
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 160f;
    private readonly string name = "QA F11 stack " + Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly Dictionary<GameItem, Vector2> nativeItemPoints = new Dictionary<GameItem, Vector2>();
    private bool directory;
    private int step, dragPhase, readyFrame, machineCount;
    private float waitUntil, savedAlpha, savedScroll;
    private bool savedBlocks, machineAllowed;
    private string savedQuery, machineResult;
    private QaSyntheticInput input;
    private CustomUIWindow search;
    private GameItem sourceBag, targetBag, first, full, roomy, sameSource, sameTarget, machine, machineSource;
    private GameGridInventory sourceGrid, targetGrid;
    private GameSlotInventory machineSlot;
    private GameInventory machineHome;
    private GridShape machineShape;
    private MonoBehaviour hit;
    private Vector2 press, drop;
    private string beforeFade;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "F11 pointer scenario timed out at " + step + "/" + dragPhase);
        if (Time.frameCount < readyFrame) return false;
        if (!directory)
        {
            if (!QaWholeStackRestart.PrepareNativeDirectory()) return false;
            directory = true;
        }
        switch (step)
        {
            case 0:
                if (start.Tick()) Next(1);
                return false;
            case 1:
                if (EmporiumEntry.Instance?.invElement == null || EmporiumEntry.Instance.invWindow?.IsVisible() != true ||
                    ItemMouseDoubleClickHandler.current == null || StoreUIManager.Instance?.gameOptionUIManager == null ||
                    StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                Setup(); Next(2, 5); return false;
            case 2:
                OpenSearch(); Next(3, 5); return false;
            case 3:
                Cover(full);
                Next(4, 3); return false;
            case 4:
                if (!Drag(first, full, null, false, true)) return false;
                Need(first.parentInventory == sourceGrid && first.unitCount == 2 && full.unitCount == NearFull && roomy.unitCount == 2 &&
                    sameSource.unitCount == 1 && sameTarget.unitCount == 2 && Ready(),
                    "An insufficient pointed stack was partially filled or redirected to another stack");
                Need(NoticeSeen("f11.stack_full"), "Insufficient target capacity did not show the specific f11.stack_full feedback");
                Need(string.IsNullOrEmpty(ModHelper.GetModData(Mod, "wholeStackTransfer", null)),
                    "The rejected full-stack target unexpectedly started a journal");
                Next(5, 5); return false;
            case 5:
                if (!Drag(first, roomy, null, true, false)) return false;
                Need(first.parentInventory == null && first.unitCount == 0 && !sourceGrid.childItems.Contains(first) &&
                    roomy.parentInventory == targetGrid && roomy.unitCount == 4 && full.unitCount == NearFull &&
                    sameSource.unitCount == 1 && sameTarget.unitCount == 2 && Ready(),
                    "Cross-inventory drag did not conserve units in the exact pointed stack");
                CheckJournal(first, roomy, 2, 2);
                Next(6, 5); return false;
            case 6:
                if (!Drag(sameSource, sameTarget, null, true, false)) return false;
                Need(sameSource.parentInventory == null && sameSource.unitCount == 0 && !targetGrid.childItems.Contains(sameSource) &&
                    sameTarget.parentInventory == targetGrid && sameTarget.unitCount == 3 && roomy.unitCount == 4 && full.unitCount == NearFull &&
                    targetGrid.childItems.Where(value => value.identifier == "exotic_fuel").Sum(value => value.unitCount) == NearFull + 7 && Ready(),
                    "Same-inventory merge changed the wrong stack or failed to conserve units");
                CheckJournal(sameSource, sameTarget, 1, 2);
                Next(7, 5); return false;
            case 7:
                SetupMachine(); Next(8, 6); return false;
            case 8:
                if (!Drag(machineSource, null, machineSlot, machineAllowed, false)) return false;
                if (machineAllowed)
                {
                    Need(machineSlot.childItem == machineSource && machineSource.parentInventory == machineSlot && Ready(),
                        "A verified safe, open empty machine slot did not receive the exact item");
                    machineResult = "verified safe machine slot accepted the whole item";
                }
                else
                {
                    Need(machineSlot.childItem == null && machineSource.parentInventory == machineHome &&
                        machineHome.childItems.Contains(machineSource) && machineSource.unitCount == machineCount &&
                        SameShape(machineSource.modifiedShape, machineShape) && Ready(),
                        "A rejected machine slot changed the source or installed an item");
                    machineResult = "machine slot refused safely (" + machineResult + ")";
                }
                input.Dispose(); input = null;
                result = "PASS F11 native pointer: covered target and raycast restoration, exact cross/same-inventory merge, " +
                    "insufficient capacity without redirection, and " + machineResult;
                return true;
            default: throw new InvalidOperationException("Unknown F11 pointer scenario step");
        }
    }

    private void Setup()
    {
        if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
        if (NegociationUIManager.Instance?.IsOpen == true) NegociationUIManager.Instance.CloseUI();
        CustomUIManager.Instance.GetWindow(Mod + ".todo")?.Close();
        PlayerStore.Instance.currentClientInstance = null; PlayerStore.Instance.isClientArrived = false;
        TradeScenario.SetOptions(true, "f11.enabled");
        TradeScenario.SetOptions(false, "f03.enabled", "f04.enabled", "f11.purchase", "f34.enabled");
        sourceBag = QaWholeStackRestart.New("backpack_medium"); targetBag = QaWholeStackRestart.New("backpack_medium");
        QaWholeStackRestart.Put(sourceBag, EmporiumEntry.Instance.invElement);
        QaWholeStackRestart.Put(targetBag, EmporiumEntry.Instance.invElement);
        sourceGrid = sourceBag.contentWindow?.childElement as GameGridInventory;
        targetGrid = targetBag.contentWindow?.childElement as GameGridInventory;
        Need(sourceGrid != null && targetGrid != null, "Ordinary source and destination inventories are unavailable");
        first = Stack(2, sourceGrid); full = Stack(NearFull, targetGrid); roomy = Stack(2, targetGrid);
        sameSource = Stack(1, targetGrid); sameTarget = Stack(2, targetGrid);
        CheckSafetySnapshot(first, full, roomy, sameSource, sameTarget);
        Need(full.GetStackSlot(first)?.numTransfer == 1 && roomy.GetStackSlot(first)?.numTransfer >= first.unitCount &&
            sameTarget.GetStackSlot(sameSource)?.numTransfer >= sameSource.unitCount,
            "Native stack fixtures do not provide the intended full and compatible capacities");
        Need(ExactStack(first, targetGrid, roomy) != null && ExactStack(sameSource, targetGrid, sameTarget) != null &&
            ExactStack(first, targetGrid, full) == null && Ready(), "Whole-stack safety checks rejected the native fixtures");
        sourceBag.contentWindow.Hide();
        ItemMouseDoubleClickHandler.current.OpenContentAction(targetBag, new Vector2(Screen.width / 2f, Screen.height / 2f));
        input = new QaSyntheticInput(keyboard: true, mouseButtons: true) { Mouse = new Vector3(-100, -100) };
    }

    private GameItem Stack(int count, GameGridInventory grid)
    {
        GameItem item = QaWholeStackRestart.New("exotic_fuel", count);
        GeneralHelper.SetCustomName(item, name);
        PutExact(grid, item);
        return item;
    }

    private static void PutExact(GameGridInventory grid, GameItem item)
    {
        for (int y = 0; y < grid.inventoryShape.height; y++) for (int x = 0; x < grid.inventoryShape.width; x++)
        {
            GridShape pose = new GridShapeBuilder(item.modifiedShape).SetPosition(x, y).Build();
            SlotMarker marker = grid.TryInventorySlot(item, item.unitCount, pose);
            if (marker?.inventory != grid || marker.item != item || marker.targetItem != null || marker.numTransfer < item.unitCount) continue;
            int amount = item.unitCount;
            if (marker.TryAcceptOnce(amount) == amount && item.parentInventory == grid && item.unitCount == amount) return;
            throw new InvalidOperationException("A native fixture failed during exact placement");
        }
        throw new InvalidOperationException("No native empty cell for " + item.identifier);
    }

    private void OpenSearch()
    {
        Need(targetBag.contentWindow.IsVisible() && !sourceBag.contentWindow.IsVisible(), "Target or source window visibility changed");
        Canvas.ForceUpdateCanvases();
        foreach (GameItem item in new[] { full, roomy, sameTarget }) nativeItemPoints[item] = FindNativeItemPoint(item);
        var action = QaItemUi.WindowAction(EmporiumEntry.Instance.invWindow, "f11.open");
        Need(action != null && !action.isDisabled, "F11 stock search action unavailable");
        action.Select(); search = CustomUIManager.Instance.GetWindow(Mod + ".customer_inventory");
        Need(search?.IsAlive == true && search.Get("query")?.input != null, "F11 stock search did not open");
        Search(name);
    }

    private void Search(string value)
    {
        var field = search.Get("query"); field.SetString(value, true); field.input.onEndEdit.Invoke(value);
    }

    private void Cover(GameItem item)
    {
        Canvas.ForceUpdateCanvases();
        Vector2 target = nativeItemPoints[item];
        RectTransform rect = search.Rect;
        Vector2 current = Center(rect);
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        float scale = canvas != null && canvas.scaleFactor > 0 ? canvas.scaleFactor : 1f;
        rect.anchoredPosition += (target - current) / scale;
        Canvas.ForceUpdateCanvases();
        var top = TopHit(target);
        Need(top != null && top.transform.IsChildOf(search.root.transform),
            "The F11 window did not cover the verified native target before dragging: " + Diagnostic(target, item, null));
    }

    // Each call advances one pointer phase. The caller checks the resulting native state.
    private bool Drag(GameItem item, GameItem resident, GameSlotInventory slot, bool allowed, bool covered)
    {
        if (dragPhase == 0)
        {
            Canvas.ForceUpdateCanvases();
            hit = QaItemUi.Hit(search.root, item);
            Need(hit != null, "The real F11 result card is missing for " + item.identifier);
            if (!(Field(Field(hit, "entry"), "Draggable") is bool draggable) || !draggable)
                throw new InvalidOperationException("The native result is not draggable: " + SourceDiagnostic(item));
            press = Center((RectTransform)hit.transform);
            Need(ReferenceEquals(TopHit(press), hit.gameObject), "The F11 source card is not the actual pointer target");
            RectTransform nativeSlot = resident == null ? SlotBackground(slot) : null;
            if (resident == null)
                Need(nativeSlot != null && nativeSlot.rect.width > 0 && nativeSlot.rect.height > 0,
                    "The native slot background has no usable bounds: " + Diagnostic(Center(slot.handler.rectTransform), null, slot));
            drop = resident != null ? nativeItemPoints[resident] : RectPoints(nativeSlot).FirstOrDefault(OnScreen);
            Need(drop.x > 0 && drop.y > 0 && drop.x < Screen.width && drop.y < Screen.height, "The native target is off screen");
            if (covered)
            {
                GameObject before = TopHit(drop);
                Need(before != null && before.transform.IsChildOf(search.root.transform),
                    "The verified native target is not occluded by F11: " + Diagnostic(drop, resident, slot));
            }
            beforeFade = Diagnostic(drop, resident, slot);
            var group = search.root.GetComponent<CanvasGroup>();
            savedAlpha = group != null ? group.alpha : 1f; savedBlocks = group == null || group.blocksRaycasts;
            savedQuery = search.Get("query").input.text;
            savedScroll = search.root.GetComponentInChildren<ScrollRect>().verticalNormalizedPosition;
            input.Mouse = press; input.MouseDown = input.MouseHeld = 0;
            dragPhase = 1; Next(step); return false;
        }
        if (dragPhase == 1)
        {
            input.MouseDown = -1; input.Mouse = press + new Vector2(12, 12);
            dragPhase = 2; Next(step, 3); return false;
        }
        if (dragPhase == 2)
        {
            var group = search.root.GetComponent<CanvasGroup>();
            Need(Field(hit, "ghost") is GameObject && search.IsAlive && search.root.activeInHierarchy && group != null &&
                group.alpha < savedAlpha && !group.blocksRaycasts, "Drag did not fade the active F11 window and release its raycasts");
            if (resident == null && !Matches(TopHit(drop), null, slot) && TryFindSlotPoint(slot, out Vector2 actualSlot)) drop = actualSlot;
            TreeNodeRender node = TopHit(drop)?.GetComponent<TreeNodeRender>();
            Need(resident != null ? ReferenceEquals(node?.FindParentElement<GameItem>(), resident) :
                ReferenceEquals(node?.FindParentElement<GameSlotInventory>(), slot),
                "After fade the exact native item or machine slot was not the topmost pointer hit. Before fade: " + beforeFade +
                " After fade: " + Diagnostic(drop, resident, slot));
            input.Mouse = drop; waitUntil = Time.unscaledTime + .5f;
            dragPhase = 3; Next(step); return false;
        }
        if (dragPhase == 3)
        {
            if (Time.unscaledTime < waitUntil) return false;
            if (resident != null) Need(Preview(targetGrid, allowed), "The pointed native stack showed the wrong placement preview");
            input.MouseDown = input.MouseHeld = -1; input.MouseUp = 0;
            dragPhase = 4; Next(step, 3); return false;
        }
        input.MouseDown = input.MouseHeld = input.MouseUp = -1;
        var restored = search.root.GetComponent<CanvasGroup>();
        Need(restored != null && Mathf.Abs(restored.alpha - savedAlpha) < .001f && restored.blocksRaycasts == savedBlocks &&
            search.IsAlive && search.root.activeInHierarchy && search.Get("query").input.text == savedQuery &&
            Mathf.Abs(search.root.GetComponentInChildren<ScrollRect>().verticalNormalizedPosition - savedScroll) < .01f,
            "F11 failed to restore its original window, search, scroll or raycast state after the drop");
        dragPhase = 0; return true;
    }

    private void SetupMachine()
    {
        targetBag.contentWindow.Hide();
        machine = QaWholeStackRestart.New("hydroponic");
        var shop = EmporiumEntry.Instance;
        Need(new[] { shop.frontInvinvElement, shop.showcaseElement, shop.backInvinvElement }
            .Any(inventory => inventory != null && MachineFeatureQa.TryPut(inventory, machine)),
            "Could not place the native machine outside the source inventory's root");
        Need(!ReferenceEquals(machine.parentInventory, shop.invElement), "Machine and source unexpectedly share the same root");
        ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(Screen.width / 2f, Screen.height / 2f));
        machine.contentWindow.ToFront();
        Need(machine.contentWindow.IsVisible(), "Hydroponic machine did not open");
        GameSlotInventory power = MachineHelper.GetBatterySlot(machine);
        GameSlotInventory seeds = MachineHydroponic.GetInputSlot(machine);
        var candidates = new[] { (slot: power, id: "energy_credit_dis"), (slot: seeds, id: "hydroreed_seed") };
        bool found = false;
        var candidateFailures = new List<string>();
        foreach (var candidate in candidates)
        {
            if (candidate.slot?.handler == null || !candidate.slot.handler.gameObject.activeInHierarchy || candidate.slot.childItem != null) continue;
            GameItem item = QaWholeStackRestart.New(candidate.id);
            GeneralHelper.SetCustomName(item, "QA F11 machine " + candidate.id);
            PutExact(sourceGrid, item);
            if (!CanDragSource(item))
            {
                candidateFailures.Add(candidate.id + ": " + SourceDiagnostic(item));
                DiscardCandidate(item);
                continue;
            }
            SlotMarker marker = candidate.slot.TryFindOneValidInventorySlot(item);
            if (marker?.inventory != candidate.slot || marker.targetItem != null || marker.numTransfer < item.unitCount)
            {
                candidateFailures.Add(candidate.id + ": native slot rejected the whole source");
                DiscardCandidate(item);
                continue;
            }
            machineSlot = candidate.slot; machineSource = item; found = true; break;
        }
        if (!found)
        {
            machineSlot = candidates.Select(value => value.slot).FirstOrDefault(value => value?.handler != null &&
                value.handler.gameObject.activeInHierarchy && value.childItem == null);
            Need(machineSlot != null, "No opened machine slot is visible for the real pointer test");
            // This same native item was the successfully merged target in the
            // previous step, so it is a verified real, whole owned stack.
            machineSource = sameTarget;
            Need(CanDragSource(machineSource), "The previously merged real stack is no longer draggable: " +
                SourceDiagnostic(machineSource) + " candidates=[" + string.Join(" | ", candidateFailures) + "]");
        }
        machineHome = machineSource.parentInventory;
        CheckSafetySnapshot(machineSource);
        machineCount = machineSource.unitCount;
        machineShape = machineSource.modifiedShape.Clone();
        bool safeTarget = SafeTarget(machineSlot), listed = ListedMachineSlot(machineSlot);
        object plan = Transfer().GetMethod("EmptySlot", Flags).Invoke(null, new object[] { machineSource, machineSlot });
        machineAllowed = found && safeTarget && listed && plan != null;
        machineResult = !found ? "no verified compatible draggable source" : !safeTarget ? "native slot callbacks are not verified" :
            !listed ? "native slot is not an addressable open location" : plan == null ? "native whole-item placement was not approved" : "";
        Search(machineSource.GetDisplayName(true));
    }

    private static bool ListedMachineSlot(GameSlotInventory slot)
    {
        Type inventory = Transfer().Assembly.GetType("ShopAssist.ShopInventory", true);
        var locations = (IEnumerable)inventory.GetMethod("OwnedLocations", Flags).Invoke(null, new object[] { true });
        return locations.Cast<object>().Any(value => ReferenceEquals(Field(value, "Inventory"), slot) && (bool)Field(value, "IsMachine"));
    }
    private static Type Transfer() => QaWholeStackRestart.Transfer();
    private static bool Ready() => QaWholeStackRestart.Ready(Transfer());
    private static bool SafeSource(GameItem item) => (bool)Transfer().GetMethod("SafeSource", Flags).Invoke(null, new object[] { item });
    private static void CheckSafetySnapshot(params GameItem[] items)
    {
        Type type = Transfer().GetNestedType("SourceSafetySnapshot", BindingFlags.NonPublic);
        object snapshot = Activator.CreateInstance(type, true);
        MethodInfo allows = type.GetMethod("Allows", Flags);
        foreach (GameItem item in items)
            Need((bool)allows.Invoke(snapshot, new object[] { item }) == SafeSource(item),
                "The search snapshot disagrees with the strict transfer safety check for " + item.identifier);
    }
    private static bool CanDragSource(GameItem item)
    {
        Type inventory = Transfer().Assembly.GetType("ShopAssist.ShopInventory", true);
        Type request = Transfer().Assembly.GetType("ShopAssist.RequestBook", true);
        return item != null && item.unitCount > 0 && SafeSource(item) && item.MaxNumRemove() >= item.unitCount &&
            (bool)inventory.GetMethod("Movable", Flags).Invoke(null, new object[] { item }) &&
            !(bool)request.GetMethod("Reserved", Flags).Invoke(null, new object[] { item });
    }

    private void DiscardCandidate(GameItem item)
    {
        Need(item != null && ReferenceEquals(item.parentInventory, sourceGrid) && sourceGrid.Expel(item) &&
            item.parentInventory == null && !sourceGrid.childItems.Contains(item),
            "Could not remove a rejected QA machine fixture from the source inventory");
        item.Destroy();
    }

    private static string SourceDiagnostic(GameItem item)
    {
        try
        {
            Type safety = Transfer().Assembly.GetType("ShopAssist.InventoryAutoInsert", true);
            MethodInfo safeItem = safety.GetMethod("SafeItem", Flags);
            MethodInfo safeVisual = safety.GetMethod("SafeVisualItem", Flags);
            MethodInfo safeInventory = safety.GetMethod("SafeTransferInventory", Flags);
            bool sourceItem = (bool)safeItem.Invoke(null, new object[] { item, true, true });
            bool inventory = (bool)safeInventory.Invoke(null, new object[] { item.parentInventory, item });
            bool mutations = (bool)safety.GetMethod("KnownMutationPatches", Flags).Invoke(null, null);
            var chain = new List<string>();
            var visited = new HashSet<GameInventory>();
            for (GameInventory current = item.parentInventory; current != null && visited.Add(current);)
            {
                string children = string.Join(",", current.childItems.Select(child => child == null ? "null" : child.identifier +
                    ":visual=" + safeVisual.Invoke(null, new object[] { child })));
                GameItem owner = current.GetParentItem();
                chain.Add(current.GetType().Name + " children=[" + children + "] owner=" + (owner?.identifier ?? "root"));
                current = owner?.parentInventory;
            }
            return "id=" + item.identifier + " count=" + item.unitCount + " removable=" + item.MaxNumRemove() +
                " mutations=" + mutations + " safeItem=" + sourceItem + " safeInventory=" + inventory + " path=" + string.Join(" -> ", chain);
        }
        catch (Exception error) { return "Could not inspect source safety: " + error; }
    }

    private static bool SameShape(GridShape first, GridShape second)
    {
        if (first == null || second == null || first.width != second.width || first.height != second.height ||
            first.minX != second.minX || first.minY != second.minY || first.orientation != second.orientation || first.flipped != second.flipped) return false;
        for (int y = 0; y < first.height; y++) for (int x = 0; x < first.width; x++)
            if (first.GetLocal(x, y) != second.GetLocal(x, y)) return false;
        return true;
    }
    private static bool SafeTarget(GameInventory inventory) => (bool)Transfer().GetMethod("SafeTarget", Flags).Invoke(null, new object[] { inventory });
    private static object ExactStack(GameItem item, GameInventory inventory, GameItem resident) =>
        Transfer().GetMethod("ExactStack", Flags).Invoke(null, new object[] { item, inventory, resident });
    private static void CheckJournal(GameItem source, GameItem target, int amount, int before)
    {
        var journal = QaWholeStackRestart.ReadJournal();
        Need(journal.action == "f11.open" && journal.state == "done" && journal.source == source.uniqueId &&
            journal.target == target.uniqueId && journal.amount == amount && journal.targetBefore == before,
            "The persisted F11 transaction does not name the exact source and target quantities");
    }
    private static bool Preview(GameGridInventory grid, bool allowed)
    {
        Transform preview = grid.rectTransform.Find("ShopAssist_NativeGridPreview");
        return preview != null && preview.gameObject.activeInHierarchy && preview.GetComponentsInChildren<Image>(true)
            .Any(image => image.isActiveAndEnabled && !image.raycastTarget && (allowed ? image.color.g > image.color.r : image.color.r > image.color.g));
    }
    private static bool NoticeSeen(string key)
    {
        Type notice = Transfer().Assembly.GetType("ShopAssist.ShopNotice", true);
        string expected = ModHelper.GetLocalized(Mod, key);
        object active = notice.GetField("activeNotice", Flags)?.GetValue(null);
        if ((string)Field(active, "Message") == expected) return true;
        var pending = notice.GetField("pending", Flags)?.GetValue(null) as IEnumerable;
        return pending != null && pending.Cast<object>().Any(value => (string)Field(value, "Message") == expected);
    }

    // Resolve the actual native top hit before opening F11, so an off-center
    // item handler cannot be mistaken for a blocked raycast after fading.
    private Vector2 FindNativeItemPoint(GameItem item)
    {
        var element = item as GameItemElement;
        Need(element?.background?.rectTransform != null && item.parentInventory is GameGridInventory,
            "Native stack has no visible background or grid");
        var grid = (GameGridInventory)item.parentInventory;
        var candidates = new List<Vector2>(RectPoints(element.background.rectTransform));
        GridShape shape = item.modifiedShape;
        float cell = UISettings.current.gridSize;
        if (shape != null && cell > 0)
            for (int y = 0; y < shape.globalHeight; y++) for (int x = 0; x < shape.globalWidth; x++)
            {
                int gx = shape.minX + x, gy = shape.minY + y;
                if (shape.Get(gx, gy) != 1) continue;
                foreach (float fy in new[] { .25f, .5f, .75f }) foreach (float fx in new[] { .25f, .5f, .75f })
                {
                    var local = new Vector3(grid.rectTransform.rect.xMin + (gx + fx) * cell,
                        grid.rectTransform.rect.yMax - (gy + fy) * cell);
                    candidates.Add(ScreenPoint(grid.rectTransform, local));
                }
            }
        foreach (Vector2 point in candidates)
            if (OnScreen(point) && Matches(TopHit(point), item, null)) return point;
        Vector2 center = Center(element.background.rectTransform);
        throw new InvalidOperationException("The native stack is not pointer-addressable even before opening F11: " + Diagnostic(center, item, null));
    }

    private static bool TryFindSlotPoint(GameSlotInventory slot, out Vector2 point)
    {
        // GameSlotInventory.handler is an empty anchor in some layouts. The
        // native slot's background and its raycastable child graphics carry the
        // real bounds; convert their own rectangles through the active canvas.
        RectTransform background = SlotBackground(slot);
        var rects = new List<RectTransform>();
        if (background != null) rects.Add(background);
        if (slot?.handler != null)
            foreach (Graphic graphic in slot.handler.GetComponentsInChildren<Graphic>(true))
                if (graphic != null && graphic.isActiveAndEnabled && graphic.raycastTarget && graphic.rectTransform != background)
                    rects.Add(graphic.rectTransform);
        foreach (RectTransform rect in rects)
        {
            if (rect == null || rect.rect.width <= 0 || rect.rect.height <= 0) continue;
            foreach (Vector2 candidate in RectPoints(rect))
                if (OnScreen(candidate) && Matches(TopHit(candidate), null, slot)) { point = candidate; return true; }
        }
        point = default; return false;
    }

    private static RectTransform SlotBackground(GameSlotInventory slot) => (Field(slot, "background") as TreeNodeRender)?.rectTransform;

    private static IEnumerable<Vector2> RectPoints(RectTransform rect)
    {
        foreach (float y in new[] { .5f, .25f, .75f, .1f, .9f }) foreach (float x in new[] { .5f, .25f, .75f, .1f, .9f })
            yield return ScreenPoint(rect, new Vector3(Mathf.Lerp(rect.rect.xMin, rect.rect.xMax, x),
                Mathf.Lerp(rect.rect.yMin, rect.rect.yMax, y)));
    }

    private static bool OnScreen(Vector2 point) => point.x > 0 && point.y > 0 && point.x < Screen.width && point.y < Screen.height;
    private static bool Matches(GameObject target, GameItem item, GameSlotInventory slot)
    {
        TreeNodeRender node = target?.GetComponent<TreeNodeRender>();
        return item != null ? ReferenceEquals(node?.FindParentElement<GameItem>(), item) :
            slot != null && ReferenceEquals(node?.FindParentElement<GameSlotInventory>(), slot);
    }

    private string Diagnostic(Vector2 point, GameItem item, GameSlotInventory slot)
    {
        var group = search?.root?.GetComponent<CanvasGroup>();
        var element = item as GameItemElement;
        TreeNodeRender owner = element?.handler ?? slot?.handler;
        PixelWindow nativeWindow = owner?.FindParentWindow();
        var hits = new List<RaycastResult>();
        if (EventSystem.current != null) EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        string raycasts = string.Join(" || ", hits.Where(value => value.gameObject != null).Take(8).Select(value =>
        {
            TreeNodeRender node = value.gameObject.GetComponent<TreeNodeRender>();
            return "path=" + Path(value.gameObject.transform) + " active=" + value.gameObject.activeInHierarchy +
                " node=" + NodePath(node, item, slot) + " sorting=" + value.sortingOrder + " depth=" + value.depth;
        }));
        return "frame=" + Time.frameCount + " point=" + point + " screen=" + Screen.width + "x" + Screen.height +
            " nativeWindowVisible=" + (nativeWindow?.IsVisible() == true) + " targetHandlerActive=" + (owner?.gameObject.activeInHierarchy == true) +
            " targetHandler=" + Bounds(owner?.rectTransform) + " targetBackground=" + Bounds(element?.background?.rectTransform ?? SlotBackground(slot)) +
            " targetGrid=" + Bounds((item?.parentInventory as GameGridInventory)?.rectTransform) +
            " searchAlive=" + (search?.IsAlive == true) + " searchActive=" + (search?.root?.activeInHierarchy == true) +
            " searchAlpha=" + (group == null ? "none" : group.alpha.ToString("F2")) +
            " searchBlocksRaycasts=" + (group == null ? "none" : group.blocksRaycasts.ToString()) +
            " searchRect=" + Bounds(search?.Rect) + " hits=[" + raycasts + "]";
    }

    private static string NodePath(TreeNodeRender node, GameItem item, GameSlotInventory slot)
    {
        if (node == null) return "none";
        var parts = new List<string>();
        for (TreeNodeRender cursor = node; cursor != null && parts.Count < 8; cursor = cursor.parent)
        {
            object element = cursor.element;
            string description = element == null ? "none" : element.GetType().Name;
            if (element is GameItem value) description += "(" + value.identifier + ",count=" + value.unitCount +
                ",expected=" + ReferenceEquals(value, item) + ")";
            if (element is GameSlotInventory seat) description += "(expected=" + ReferenceEquals(seat, slot) + ")";
            parts.Add(cursor.gameObject.name + ":" + description);
        }
        return string.Join(" <- ", parts);
    }

    private static string Path(Transform transform)
    {
        var names = new List<string>();
        for (Transform cursor = transform; cursor != null && names.Count < 10; cursor = cursor.parent) names.Add(cursor.name);
        return string.Join("/", names);
    }

    private static string Bounds(RectTransform rect)
    {
        if (rect == null) return "none";
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 a = ScreenPoint(rect, rect.InverseTransformPoint(corners[0]));
        Vector2 b = ScreenPoint(rect, rect.InverseTransformPoint(corners[2]));
        return "(" + a.x.ToString("F1") + "," + a.y.ToString("F1") + ")-(" + b.x.ToString("F1") + "," + b.y.ToString("F1") + ")";
    }

    private static object Field(object value, string field) => value?.GetType().GetField(field, Flags)?.GetValue(value);
    private static Vector2 ScreenPoint(RectTransform rect, Vector3 local)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(local));
    }
    private static Vector2 Center(RectTransform rect)
    {
        return ScreenPoint(rect, rect.rect.center);
    }
    private static GameObject TopHit(Vector2 point)
    {
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        return hits.FirstOrDefault(value => value.gameObject != null).gameObject;
    }
    private void Next(int value, int frames = 1) { step = value; readyFrame = Time.frameCount + frames; }
    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
}
