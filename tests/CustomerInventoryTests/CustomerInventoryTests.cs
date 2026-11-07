using System;
using System.Collections.Generic;
using System.Linq;
using ShopAssist;
using UnityEngine;

internal static class CustomerInventoryTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static GameGridInventory Grid(PixelWindow window)
    {
        var grid = new GameGridInventory();
        grid.handler = TreeNodeRender.Instantiate().SetParent(window.handler);
        return grid;
    }

    private static void NativeQueryAndLocate()
    {
        WindowsHandler.current = new WindowsHandler();
        RenderHandler.current = new RenderHandler();
        PlayerStore.Instance = new PlayerStore();
        NegociationUIManager.Instance = new NegociationUIManager();
        OverlayHandler.current = new OverlayHandler();
        ShopInventory.InStore = true;
        ShopInventory.SearchableLock = InventoryLockFake.Locked.Contains;
        ShopInventory.Locations.Clear(); RequestBook.Reservations.Clear();
        Time.unscaledTime = 0; Time.frameCount++;
        var window = new PixelWindow(); WindowsHandler.current.visibleWindows.Add(window);
        var stock = Grid(window);
        var location = new ShopInventory.Location { Inventory = stock, Ordinary = true, Name = "Stock" };
        ShopInventory.Locations.Add(location);
        // Duplicate traversal paths must not duplicate an item in the result.
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = stock, Ordinary = true, Name = "Duplicate" });
        GameItemElement Add(GameGridInventory inventory, string name, string type, bool custom = false)
        {
            var item = new GameItemElement(window, null) { parentInventory = inventory, name = name };
            item.types.Add(type); if (custom) item.tooltipRichTextFunc = new object();
            inventory.childItems.Add(item); return item;
        }
        var ordinary = Add(stock, "ordinary", "FOOD");
        var hiddenAcceptance = Add(stock, "hidden acceptance", "FOOD"); hiddenAcceptance.hiddenAcceptance = true;
        var reserved = Add(stock, "reserved", "FOOD"); RequestBook.Reservations.Add(reserved);
        var custom = Add(stock, "custom", "FOOD", custom: true);
        custom.unitCount = 3; custom.removeLimit = 1;
        var other = Add(stock, "water", "WATER");
        var machineGrid = Grid(window);
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = machineGrid, IsMachine = true, Name = "Machine" });
        var machineItem = Add(machineGrid, "machine seed", "FOOD");
        var settings = new ShopSettings(); var errors = new List<string>();
        using var indicators = new CustomerIndicators(settings, errors.Add);
        using var inventoryView = new CustomerInventory(settings, indicators, errors.Add);
        string quote = "last quote 37";
        inventoryView.QuoteLabel = item => ReferenceEquals(item, ordinary) ? quote : null;
        indicators.Start(); inventoryView.Start(); settings.Set("f11.enabled", true);
        StoreClient client = PlayerStore.Instance.currentClientInstance.client;
        client.clientBuyingTagList.Add("FOOD"); indicators.Tick();
        inventoryView.OpenDemand();
        var entries = ItemGrid.LastEntries;
        Check(entries.Count == 4 && entries.Count(entry => ReferenceEquals(entry.Item, ordinary)) == 1,
            "query deduplicates real instances and includes known matching goods");
        Check(!entries.Any(entry => ReferenceEquals(entry.Item, hiddenAcceptance) || ReferenceEquals(entry.Item, other)) &&
            entries.Any(entry => ReferenceEquals(entry.Item, custom)),
            "native refusal and acceptance decide the list independently of tooltip evidence");
        var reservedEntry = entries.Single(entry => ReferenceEquals(entry.Item, reserved));
        var machineEntry = entries.Single(entry => ReferenceEquals(entry.Item, machineItem));
        Check(reservedEntry.StatusLabel == "f11.reserved" && machineEntry.StatusLabel == "f11.in_use" &&
            reservedEntry.SourceLabel == "Stock" && reservedEntry.QuoteLabel == "f11.unquoted",
            "query retains reserved and machine-source status");
        Check(entries.Single(entry => ReferenceEquals(entry.Item, ordinary)).QuoteLabel == "last quote 37",
            "source, status and last quote are separate card values");
        Check(entries.Single(entry => ReferenceEquals(entry.Item, ordinary)).TooltipHint == "f11.drag_tip" &&
            reservedEntry.TooltipHint == "f11.locate_tip" && machineEntry.TooltipHint == "f11.locate_tip" &&
            !reservedEntry.Draggable && !machineEntry.Draggable &&
            entries.Single(entry => ReferenceEquals(entry.Item, custom)).StatusLabel == ShopSettings.Format("f11.partial", 1) &&
            !entries.Single(entry => ReferenceEquals(entry.Item, custom)).Draggable,
            "full stacks advertise dragging, while reserved, machine and partial stacks remain individually locatable");
        foreach (var value in new[] { reserved, custom, machineItem })
        {
            ItemGrid.LastOptions.OnClick(ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, value)));
            Check(value.rotator.transform.Find("ShopAssist_LocateOutline") != null,
                "a result that cannot be dragged can still locate its real item");
            CustomUIManager.LastWindow.Click("return");
        }
        entries = ItemGrid.LastEntries;
        Check(entries.All(entry => entry.PurchaseLabel == null), "purchase comparison is off by default");
        ShopLedger.Prices[ordinary] = 23;
        settings.Set("f11.purchase", true);
        Time.unscaledTime += .6f; inventoryView.Tick();
        Check(ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, ordinary)).PurchaseLabel == ShopSettings.Format("f11.purchase_record", 23) &&
            ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, custom)).PurchaseLabel == "f11.purchase_unknown",
            "enabling comparison shows known records and keeps missing records unknown");
        ShopLedger.Prices[ordinary] = 0;
        Time.unscaledTime += .6f; inventoryView.Tick();
        Check(ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, ordinary)).PurchaseLabel == ShopSettings.Format("f11.purchase_record", 0),
            "a genuine zero purchase record is distinct from unknown and refreshes in place");
        settings.Set("f11.purchase", false);
        Time.unscaledTime += .6f; inventoryView.Tick();
        Check(ItemGrid.LastEntries.All(entry => entry.PurchaseLabel == null), "disabling comparison removes the purchase row");
        Check(!ItemGrid.LastOptions.CanDrop(ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, reserved)), stock, new GridShape()),
            "a reserved item cannot be moved from the customer list");
        var liveWindow = CustomUIManager.LastWindow;
        var scroll = liveWindow.root.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
        Check(scroll != null, "the list has a scroll view");
        scroll.verticalNormalizedPosition = .32f;
        int draws = ItemGrid.AddCount;
        Time.unscaledTime += .6f; inventoryView.Tick();
        Check(ItemGrid.AddCount == draws, "polling unchanged results does not rebuild item cards");
        ordinary.hiddenAcceptance = true;
        Time.unscaledTime += .6f; inventoryView.Tick();
        scroll = liveWindow.root.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
        Check(!ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, ordinary)) &&
            Math.Abs(scroll.verticalNormalizedPosition - .32f) < .001f,
            "a native refusal refreshes without an invalidation event and keeps scroll");
        ordinary.hiddenAcceptance = false;
        Input.MouseHeld[0] = true; Time.unscaledTime += 1; inventoryView.Tick();
        Check(!ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, ordinary)), "refresh waits during a pointer press");
        Input.MouseHeld[0] = false; Input.MouseUp[0] = true; inventoryView.Tick();
        Check(!ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, ordinary)), "refresh waits through pointer release");
        Input.MouseUp[0] = false; inventoryView.Tick();
        Check(ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, ordinary)), "the deferred result appears after release");
        quote = "last quote 44"; Time.unscaledTime += .6f; inventoryView.Tick();
        Check(ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, ordinary)).QuoteLabel == quote,
            "quote changes independently trigger a new snapshot");
        UIDragHandler.isDraggingAnyUI = true; ordinary.name = "renamed"; Time.unscaledTime += .6f; inventoryView.Tick();
        Check(ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, ordinary)).Caption == "ordinary",
            "refresh waits while a UI drag is active");
        UIDragHandler.isDraggingAnyUI = false; inventoryView.Tick();
        Check(ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, ordinary)).Caption == "renamed",
            "a renamed item updates after the drag");

        var observed = new RichText { nakedText = "Shown [Food]" };
        CustomerIndicators.TooltipBuilt(custom, observed); CustomerIndicators.TooltipDisplayed(observed);
        indicators.Tick(); Time.unscaledTime += 1; inventoryView.Tick();
        Check(ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, custom)),
            "observing a tooltip does not change native matching");

        ordinary.hiddenAcceptance = true; CustomerIndicators.DemandDisplayed(client);
        Time.unscaledTime += 1; inventoryView.Tick();
        Check(ReferenceEquals(liveWindow, CustomUIManager.LastWindow) &&
            !ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, ordinary)),
            "a changed native refusal updates the list without closing its window");
        ordinary.hiddenAcceptance = false; CustomerIndicators.DemandDisplayed(client);
        Time.unscaledTime += 1; inventoryView.Tick();

        var stale = ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, ordinary));
        var actions = ItemGrid.LastOptions;
        stock.childItems.Remove(ordinary); ShopNotice.Last = null; actions.OnClick(stale);
        Check(ShopNotice.Last == "f11.changed" && CustomUIManager.LastWindow.IsAlive,
            "locate revalidates a stale item and leaves the view open");
        stock.childItems.Add(ordinary); actions.OnClick(stale);
        Check(CustomUIManager.LastWindow.IsAlive && ordinary.rotator.transform.Find("ShopAssist_LocateOutline") != null,
            "locating marks the real item while keeping the same list open");
        Check(liveWindow.Rect.sizeDelta.x == 240 && liveWindow.Rect.sizeDelta.y == 94,
            "locating folds the list into a compact return control");
        liveWindow.Click("return");
        scroll = liveWindow.root.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
        Check(liveWindow.Rect.sizeDelta.x == 650 && liveWindow.Rect.sizeDelta.y == 480 &&
            Math.Abs(scroll.verticalNormalizedPosition - .32f) < .001f,
            "return restores the same list window, size and scroll");
        Time.unscaledTime += 5; ItemLocator.Tick();
        Check(ordinary.rotator.transform.Find("ShopAssist_LocateOutline") == null, "the shared locate marker expires");

        var reopened = CustomUIManager.LastWindow;
        for (int i = 0; i < 25; i++) Add(stock, "page " + i, "FOOD");
        int safetyChecks = WholeStackTransfer.MutationChecks;
        VisibleInventoryViews.NotifyChanged(); Time.unscaledTime += 1; inventoryView.Tick();
        Check(UiLayout.Pages == 2 && UiLayout.Previous == null && UiLayout.Next != null && ReferenceEquals(reopened, CustomUIManager.LastWindow),
            "refresh updates the existing window and exposes multiple pages");
        Check(WholeStackTransfer.MutationChecks == safetyChecks + 1,
            "a multi-item refresh checks shared mutation hooks once instead of once per result");
        UiLayout.Next();
        Check(UiLayout.Page == 1 && UiLayout.Previous != null && UiLayout.Next == null && ReferenceEquals(reopened, CustomUIManager.LastWindow),
            "the next page updates in place and disables the next boundary");
        Check(Math.Abs(reopened.root.GetComponentInChildren<UnityEngine.UI.ScrollRect>().verticalNormalizedPosition - 1f) < .001f,
            "an explicit page change begins at the top");
        UiLayout.Previous();
        Check(UiLayout.Page == 0 && UiLayout.Previous == null && UiLayout.Next != null, "the previous button returns to the first boundary");
        PlayerStore.Instance.currentClientInstance = new StoreClientInstance();
        PlayerStore.Instance.currentClientInstance.client.clientBuyingTagList.Add("WATER");
        indicators.Tick(); Time.unscaledTime += 1; inventoryView.Tick();
        Check(ReferenceEquals(reopened, CustomUIManager.LastWindow) && reopened.IsAlive && UiLayout.Pages == 1 &&
            UiLayout.Previous == null && UiLayout.Next == null && ItemGrid.LastEntries.Count == 1 && ReferenceEquals(ItemGrid.LastEntries[0].Item, other),
            "switching customer updates the existing list, clamps pagination and removes stale goods");
        PlayerStore.Instance.runID = "another run"; inventoryView.Tick();
        Check(!reopened.IsAlive, "a changed save identity closes an existing customer view");
        ModHook.LoadInit();
        PlayerStore.Instance.currentClientInstance = null;
        Check(!indicators.Matches(custom) && inventoryView.CanOpen() && !inventoryView.CanOpenDemand(),
            "stock search stays available without a customer");
        var hidden = Add(stock, "hidden", "FOOD"); hidden.tags.Add("ITEM_HIDDEN_TAG");
        var unowned = Add(stock, "unowned", "FOOD"); unowned.isOwned = false;
        var front = Grid(window);
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = front, Role = "front", Name = "Front" });
        var frontItem = Add(front, "front watch", "FOOD");
        var special = Grid(window);
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = special, Role = "special", Name = "Special" });
        var specialItem = Add(special, "special item", "FOOD");
        var manual = Add(stock, "manual transfer", "FOOD");
        stock.childItems.Remove(manual); stock.childItems.Insert(0, manual);
        inventoryView.Open();
        Check(ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, ordinary)) &&
            ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, other)) &&
            !ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, machineItem) || ReferenceEquals(entry.Item, hidden) ||
                ReferenceEquals(entry.Item, unowned) || ReferenceEquals(entry.Item, specialItem)),
            "stock search includes ordinary owned goods while excluding machine, hidden and unowned goods");
        var stockActions = ItemGrid.LastOptions;
        var stockEntry = ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, ordinary));
        var manualEntry = ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, manual));
        var pose = new GridShape();
        Check(stockActions.RequireSourceShape && stockActions.CanDrop(stockEntry, front, pose) && stockActions.OnDrop != null &&
            ReferenceEquals(ordinary.parentInventory, stock) && ItemGridMove.Attempts == 0,
            "stock search enables manual drag to an open ordinary root without moving anything during query");
        Check(stockEntry.TooltipHint == "f11.drag_tip" &&
            ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, reserved)).TooltipHint == "f11.locate_tip",
            "stock search distinguishes locating from dragging");
        WholeStackTransfer.MutationsSafe = false;
        Check(!stockActions.CanDrop(stockEntry, front, pose),
            "a mutation hook introduced after the result snapshot blocks the actual transfer");
        WholeStackTransfer.MutationsSafe = true;
        ordinary.unitCount++;
        Check(!stockActions.CanDrop(stockEntry, front, pose), "a changed stack quantity invalidates the rendered result");
        ordinary.unitCount--;
        ordinary.tags.Add("ITEM_HIDDEN_TAG");
        Check(!stockActions.CanDrop(stockEntry, front, pose), "a newly hidden source cannot be moved");
        ordinary.tags.Remove("ITEM_HIDDEN_TAG"); ordinary.isOwned = false;
        Check(!stockActions.CanDrop(stockEntry, front, pose), "a source that lost ownership cannot be moved");
        ordinary.isOwned = true; RequestBook.Reservations.Add(ordinary);
        Check(!stockActions.CanDrop(stockEntry, front, pose), "a newly reserved source cannot be moved");
        RequestBook.Reservations.Remove(ordinary);
        stock.childItems.Remove(ordinary);
        Check(!stockActions.CanDrop(stockEntry, front, pose), "the item must still belong to its original source list");
        stock.childItems.Insert(0, ordinary);
        InventoryLockFake.Locked.Add(stock);
        Check(!stockActions.CanDrop(stockEntry, front, pose), "the source lock is rechecked before a drop");
        InventoryLockFake.Locked.Remove(stock);
        location.Path = new GameItem[] { frontItem };
        Check(!stockActions.CanDrop(stockEntry, front, pose), "a changed source path invalidates the snapshot");
        location.Path = Array.Empty<GameItem>();
        ShopInventory.Locations.Remove(location);
        Check(!stockActions.CanDrop(stockEntry, front, pose), "a removed source location cannot be moved");
        ShopInventory.Locations.Insert(0, location);
        Check(!stockActions.CanDrop(stockEntry, machineGrid, pose) && !stockActions.CanDrop(stockEntry, special, pose),
            "machine and special inventories cannot be used as transfer targets");
        var closedWindow = new PixelWindow(false); var closedGrid = Grid(closedWindow);
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = closedGrid, Ordinary = true });
        Check(!stockActions.CanDrop(stockEntry, closedGrid, pose), "a closed target inventory is not accessible");
        closedWindow.Show(); ItemGridMove.Allowed = false;
        Check(!stockActions.CanDrop(stockEntry, closedGrid, pose), "native placement refusal prevents a transfer");
        ShopNotice.Last = null; stockActions.OnReject(stockEntry, closedGrid, pose);
        Check(ShopNotice.Last == "f11.move_manual" && ItemGridMove.Attempts == 0,
            "a rejected destination gets a location-specific explanation without moving the item");
        ShopNotice.Last = null; stockActions.OnDrop(stockEntry, closedGrid, pose);
        Check(ShopNotice.Last == "f11.move_manual" && ItemGridMove.Attempts == 0,
            "a destination that becomes invalid before the drop gets the same explanation");
        ordinary.tags.Add("ITEM_HIDDEN_TAG"); ShopNotice.Last = null;
        stockActions.OnReject(stockEntry, closedGrid, pose);
        Check(ShopNotice.Last == "inv.changed", "a hidden source takes precedence over target refusal");
        ordinary.tags.Remove("ITEM_HIDDEN_TAG"); RequestBook.Reservations.Add(ordinary); ShopNotice.Last = null;
        stockActions.OnReject(stockEntry, closedGrid, pose);
        Check(ShopNotice.Last == "inv.changed", "a newly reserved source is reported as changed");
        RequestBook.Reservations.Remove(ordinary); ShopNotice.Last = null;
        stockActions.OnReject(stockEntry, null, null);
        Check(ShopNotice.Last == "f11.move_manual", "an explicitly rejected non-inventory target offers manual location");
        ItemGridMove.Allowed = true;
        Check(stockActions.CanDropTarget(stockEntry, front, frontItem, pose), "the pointed resident is passed as the exact merge target");
        RequestBook.Reservations.Add(frontItem);
        Check(!stockActions.CanDropTarget(stockEntry, front, frontItem, pose), "a reserved pointed target is rejected");
        stockActions.OnRejectTarget(stockEntry, front, frontItem, pose);
        Check(ShopNotice.Last == "f11.target_reserved", "a reserved destination gets its own explanation");
        RequestBook.Reservations.Remove(frontItem);
        frontItem.stackRoom = 0;
        stockActions.OnRejectTarget(stockEntry, front, frontItem, pose);
        Check(ShopNotice.Last == "f11.stack_full", "an insufficient pointed stack is reported as full");
        frontItem.stackRoom = int.MaxValue; frontItem.stackAllowed = false;
        stockActions.OnRejectTarget(stockEntry, front, frontItem, pose);
        Check(ShopNotice.Last == "f11.stack_mismatch", "an incompatible pointed stack is reported separately");
        frontItem.stackAllowed = true;
        var machineSlot = new GameSlotInventory { handler = TreeNodeRender.Instantiate().SetParent(window.handler) };
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = machineSlot, IsMachine = true, Name = "Machine slot" });
        Check(stockActions.CanDropTarget(stockEntry, machineSlot, null, pose), "a validated open empty machine slot is eligible");
        machineSlot.childItems.Add(frontItem);
        Check(!stockActions.CanDropTarget(stockEntry, machineSlot, null, pose), "an occupied machine slot cannot be swapped");
        machineSlot.childItems.Clear();
        machineSlot.handler.gameObject.SetActive(false);
        Check(!stockActions.CanDropTarget(stockEntry, machineSlot, null, pose), "a hidden machine slot cannot receive a drop");
        machineSlot.handler.gameObject.SetActive(true);
        WholeStackTransfer.Ready = false; ShopNotice.Last = null;
        stockActions.OnReject(stockEntry, front, pose);
        Check(ShopNotice.Last == "f08.pending" && !stockActions.CanDrop(stockEntry, front, pose),
            "an unresolved journal blocks new drops and explains the required inspection");
        WholeStackTransfer.Ready = true;
        ordinary.unitCount++; ShopNotice.Last = null;
        stockActions.OnDrop(stockEntry, front, pose);
        Check(ItemGridMove.Attempts == 0 && ShopNotice.Last == "inv.changed" && ReferenceEquals(ordinary.parentInventory, stock),
            "drop revalidates a stale stack and reports the failure");
        ordinary.unitCount--;
        stockActions.OnDrop(manualEntry, front, pose);
        Check(ItemGridMove.Attempts == 1 && ReferenceEquals(manual.parentInventory, front) &&
            !stock.childItems.Contains(manual) && front.childItems.Contains(manual),
            "the explicit drop moves the original stack once to the chosen inventory");
        stockActions.OnDrop(manualEntry, front, pose);
        Check(ItemGridMove.Attempts == 1 && front.childItems.Count(item => ReferenceEquals(item, manual)) == 1,
            "repeating a stale drop cannot duplicate the moved item");
        ItemGridMove.Attempts = 0;
        var search = CustomUIManager.LastWindow;
        search.SetInput("query", "special item");
        Check(ItemGrid.LastEntries.Count == 0, "stock search excludes special areas");
        search.SetInput("query", "front watch");
        Check(ItemGrid.LastEntries.Count == 1 && ReferenceEquals(ItemGrid.LastEntries[0].Item, frontItem), "stock search includes native front root");
        ItemGrid.LastOptions.OnClick(ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, frontItem)));
        Check(frontItem.rotator.transform.Find("ShopAssist_LocateOutline") != null, "native root stock search results can be located");
        search.Click("return");
        search.SetInput("query", "water");
        Check(ItemGrid.LastEntries.Count == 1 && ReferenceEquals(ItemGrid.LastEntries[0].Item, other),
            "stock search filters by item name without a customer");
        search.SetInput("query", "");
        InventoryLockFake.Locked.Add(stock);
        ShopNotice.Last = null;
        ItemGrid.LastOptions.OnClick(ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, ordinary)));
        Check(ShopNotice.Last == "f11.changed", "locating rechecks that the stock is still unlocked");
        VisibleInventoryViews.NotifyChanged(); Time.unscaledTime += 1; inventoryView.Tick();
        Check(!ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, ordinary)),
            "stock search excludes a locked inventory without hiding other accessible roots");
        search.SetInput("query", "front watch");
        Check(ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, frontItem)), "other accessible roots remain searchable");
        InventoryLockFake.Locked.Clear();
        PlayerStore.Instance.currentClientInstance = new StoreClientInstance { client = client };
        indicators.Tick(); inventoryView.OpenDemand();
        Check(ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, custom)),
            "native matching works after reload without reusing tooltip observations");
        var dragWindow = CustomUIManager.LastWindow;
        dragWindow.SetInput("query", "custom");
        var dragScroll = dragWindow.root.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
        dragScroll.verticalNormalizedPosition = .37f;
        var group = dragWindow.root.Add(new CanvasGroup { alpha = .75f, blocksRaycasts = true });
        ItemGrid.DragActive = true;
        ItemGrid.LastOptions.OnDragStarted();
        Check(dragWindow.root.activeSelf && dragWindow.IsAlive && Math.Abs(group.alpha - .2f) < .001f && !group.blocksRaycasts,
            "drag fades the live window and lets raycasts pass without disabling its controls");
        Time.unscaledTime += 1; inventoryView.Tick();
        Check(ItemGrid.LastEntries.Count == 1 && ReferenceEquals(ItemGrid.LastEntries[0].Item, custom) &&
            Math.Abs(dragScroll.verticalNormalizedPosition - .37f) < .001f, "search and scroll remain stable during a drag");
        ItemGrid.DragActive = false; ItemGrid.LastOptions.OnDragEnded();
        Check(Math.Abs(group.alpha - .75f) < .001f && group.blocksRaycasts, "ending or cancelling a drag restores the original canvas state");
        ItemGrid.DragActive = true; ItemGrid.LastOptions.OnDragStarted();
        dragWindow.Close();
        Check(!ItemGrid.DragActive && Math.Abs(group.alpha - .75f) < .001f && group.blocksRaycasts,
            "closing the window cancels its drag and restores raycasts and opacity");
        inventoryView.OpenDemand();
        Check(CustomUIManager.LastWindow.IsAlive && ItemGrid.LastEntries.Count > 0, "reopening after close builds a fresh live view");
        Check(errors.Count == 0, "customer query and locate should not log errors");
        RequestBook.Reservations.Clear();
        ShopInventory.SearchableLock = null;
    }

    private static void LargeStockSnapshot()
    {
        WindowsHandler.current = new WindowsHandler();
        RenderHandler.current = new RenderHandler();
        PlayerStore.Instance = new PlayerStore { currentClientInstance = null };
        NegociationUIManager.Instance = new NegociationUIManager();
        ShopInventory.InStore = true;
        ShopInventory.Locations.Clear(); RequestBook.Reservations.Clear();
        var window = new PixelWindow(); WindowsHandler.current.visibleWindows.Add(window);
        var stock = Grid(window);
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = stock, Ordinary = true, Name = "Stock" });
        GameItemElement last = null;
        for (int i = 0; i < 500; i++)
        {
            last = new GameItemElement(window, null) { parentInventory = stock, name = "bulk item " + i.ToString("D3") };
            stock.childItems.Add(last);
        }
        var settings = new ShopSettings(); var errors = new List<string>();
        using var indicators = new CustomerIndicators(settings, errors.Add);
        using var inventoryView = new CustomerInventory(settings, indicators, errors.Add);
        indicators.Start(); inventoryView.Start(); settings.Set("f11.enabled", true);
        int safetyChecks = WholeStackTransfer.MutationChecks, inventoryChecks = WholeStackTransfer.InventoryChecks;
        inventoryView.Open();
        Check(UiLayout.Pages == 21 && ItemGrid.LastEntries.Count == 24 && WholeStackTransfer.MutationChecks == safetyChecks + 1 &&
            WholeStackTransfer.InventoryChecks == inventoryChecks + 1,
            "500 matching stacks are paginated and share mutation and inventory-path checks");
        int draws = ItemGrid.AddCount;
        Time.unscaledTime += .6f; inventoryView.Tick();
        Check(ItemGrid.AddCount == draws && WholeStackTransfer.MutationChecks == safetyChecks + 2 &&
            WholeStackTransfer.InventoryChecks == inventoryChecks + 2,
            "an unchanged large result refresh does not rebuild the cards");
        CustomUIManager.LastWindow.SetInput("query", "bulk item 499");
        Check(UiLayout.Pages == 1 && ItemGrid.LastEntries.Count == 1 && ReferenceEquals(ItemGrid.LastEntries[0].Item, last) && errors.Count == 0,
            "a search across the large snapshot finds the last item without losing its identity");
    }

    private static void Main()
    {
        ShopInventory.Locations.Clear();
        var rootWindow = new PixelWindow();
        var rootGrid = Grid(rootWindow);
        var root = new ShopInventory.Location { Inventory = rootGrid };
        ShopInventory.Locations.Add(root);
        ItemMouseDoubleClickHandler.current = null;
        Check(ItemLocator.RevealPath(root), "visible root location does not require an open handler");
        rootWindow.Hide();
        Check(!ItemLocator.RevealPath(root), "hidden root must be opened by the player first");
        rootWindow.Show();

        var host = new GameItemElement(rootWindow, null) { parentInventory = rootGrid };
        rootGrid.childItems.Add(host);
        var inner = Grid(host.contentWindow);
        var nested = new ShopInventory.Location { Inventory = inner, Owner = host, Path = new GameItem[] { host } };
        ShopInventory.Locations.Add(nested);
        Check(!ItemLocator.RevealPath(nested), "opening a closed container requires the normal handler");
        var opener = ItemMouseDoubleClickHandler.current = new ItemMouseDoubleClickHandler();
        host.safeToOpen = false;
        Check(!ItemLocator.RevealPath(nested) && opener.openCount == 0, "unsafe closed containers are not opened");
        host.safeToOpen = true;
        Check(ItemLocator.RevealPath(nested) && opener.openCount == 1, "safe container is opened by the normal handler");
        ItemMouseDoubleClickHandler.current = null;
        Check(ItemLocator.RevealPath(nested) && opener.openCount == 1, "already open container does not require the handler");

        host.contentWindow.Hide();
        ItemMouseDoubleClickHandler.current = opener;
        opener.beforeOpen = _ => ShopInventory.Locations.Remove(nested);
        Check(!ItemLocator.RevealPath(nested), "location is revalidated after the open callback");
        opener.beforeOpen = null;
        ShopInventory.Locations.Add(nested);

        var panels = new MachineryPanelRow();
        var tabs = new MachineryTabs();
        tabs.captions.Add("A"); tabs.captions.Add("B");
        panels.Panels.Add(new MachineryPanel { role = "storage", content = tabs });
        host.contentWindow.childElement = panels;
        nested.Role = "storage";
        nested.Page = 1;
        Check(ItemLocator.RevealPath(nested) && tabs.selected == 1 && tabs.selectCount == 1,
            "the requested storage page is selected");
        Check(ItemLocator.RevealPath(nested) && tabs.selectCount == 1,
            "locating an already selected page does not invoke selection again");
        NativeQueryAndLocate();
        LargeStockSnapshot();
        Console.WriteLine($"Customer inventory query / locate / native demand: {assertions} assertions passed.");
    }
}
