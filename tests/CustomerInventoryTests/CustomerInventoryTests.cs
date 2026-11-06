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
        inventoryView.Open();
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
        Check(!ItemGrid.LastOptions.CanDrop(reservedEntry, stock, new GridShape()),
            "a reserved item cannot be moved from the customer list");
        var liveWindow = CustomUIManager.LastWindow;
        var scroll = liveWindow.root.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
        Check(scroll != null, "the list has a scroll view");
        scroll.verticalNormalizedPosition = .32f;
        int draws = ItemGrid.AddCount;
        Time.unscaledTime = .6f; inventoryView.Tick();
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
        VisibleInventoryViews.NotifyChanged(); Time.unscaledTime += 1; inventoryView.Tick();
        Check(UiLayout.Pages == 2 && UiLayout.Previous == null && UiLayout.Next != null && ReferenceEquals(reopened, CustomUIManager.LastWindow),
            "refresh updates the existing window and exposes multiple pages");
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
        Check(!indicators.Matches(custom) && !inventoryView.CanOpen(), "no customer leaves no demand");
        PlayerStore.Instance.currentClientInstance = new StoreClientInstance { client = client };
        indicators.Tick(); inventoryView.Open();
        Check(ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, custom)),
            "native matching works after reload without reusing tooltip observations");
        Check(errors.Count == 0, "customer query and locate should not log errors");
        RequestBook.Reservations.Clear();
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
        Console.WriteLine($"Customer inventory query / locate / native demand: {assertions} assertions passed.");
    }
}
