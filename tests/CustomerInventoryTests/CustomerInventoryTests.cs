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

    private static void PublicQueryAndLocate()
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
        indicators.Start(); inventoryView.Start(); settings.Set("f11.enabled", true);
        StoreClient client = PlayerStore.Instance.currentClientInstance.client;
        client.clientBuyingTagList.Add("FOOD"); CustomerIndicators.DemandDisplayed(client); indicators.Tick();
        inventoryView.Open();
        var entries = ItemGrid.LastEntries;
        Check(entries.Count == 4 && entries.Count(entry => ReferenceEquals(entry.Item, ordinary)) == 1,
            "query deduplicates real instances and includes known matching goods");
        Check(entries.Any(entry => ReferenceEquals(entry.Item, hiddenAcceptance)) &&
            !entries.Any(entry => ReferenceEquals(entry.Item, other) || ReferenceEquals(entry.Item, custom)),
            "unpublished acceptance and unobserved custom tooltip do not change public matching");
        var reservedEntry = entries.Single(entry => ReferenceEquals(entry.Item, reserved));
        var machineEntry = entries.Single(entry => ReferenceEquals(entry.Item, machineItem));
        Check(reservedEntry.Badge == "f11.reserved" && machineEntry.Badge == "f11.in_use",
            "query retains reserved and machine-source status");
        Check(!ItemGrid.LastOptions.CanDrop(reservedEntry, stock, new GridShape()),
            "a reserved item cannot be moved from the customer list");

        var observed = new RichText { nakedText = "Shown [Food]" };
        CustomerIndicators.TooltipBuilt(custom, observed); CustomerIndicators.TooltipDisplayed(observed);
        indicators.Tick(); Time.unscaledTime = 1; inventoryView.Tick();
        Check(ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, custom)),
            "an actually displayed custom tooltip can supply a public category");

        var stale = ItemGrid.LastEntries.Single(entry => ReferenceEquals(entry.Item, ordinary));
        var actions = ItemGrid.LastOptions;
        stock.childItems.Remove(ordinary); ShopNotice.Last = null; actions.OnClick(stale);
        Check(ShopNotice.Last == "f11.changed" && CustomUIManager.LastWindow.IsAlive,
            "locate revalidates a stale item and leaves the view open");
        stock.childItems.Add(ordinary); actions.OnClick(stale);
        Check(!CustomUIManager.LastWindow.IsAlive && ordinary.rotator.transform.Find("ShopAssist_Outline") != null,
            "locating a still-visible public match opens its actual location and marks it");

        inventoryView.Open(); var reopened = CustomUIManager.LastWindow;
        PlayerStore.Instance.runID = "another run"; inventoryView.Tick();
        Check(!reopened.IsAlive, "a changed save identity closes an existing customer view");
        ModHook.LoadInit();
        Check(indicators.Match(custom) == PublicMatch.Unknown && !inventoryView.CanOpen(),
            "loading clears the previous public demand and observed custom-tooltip evidence");
        CustomerIndicators.DemandDisplayed(client); indicators.Tick(); inventoryView.Open();
        Check(!ItemGrid.LastEntries.Any(entry => ReferenceEquals(entry.Item, custom)),
            "a new session cannot reuse a prior tooltip observation");
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
        Check(CustomerInventory.RevealPath(root), "visible root location does not require an open handler");
        rootWindow.Hide();
        Check(!CustomerInventory.RevealPath(root), "hidden root must be opened by the player first");
        rootWindow.Show();

        var host = new GameItemElement(rootWindow, null) { parentInventory = rootGrid };
        rootGrid.childItems.Add(host);
        var inner = Grid(host.contentWindow);
        var nested = new ShopInventory.Location { Inventory = inner, Owner = host, Path = new GameItem[] { host } };
        ShopInventory.Locations.Add(nested);
        Check(!CustomerInventory.RevealPath(nested), "opening a closed container requires the normal handler");
        var opener = ItemMouseDoubleClickHandler.current = new ItemMouseDoubleClickHandler();
        host.safeToOpen = false;
        Check(!CustomerInventory.RevealPath(nested) && opener.openCount == 0, "unsafe closed containers are not opened");
        host.safeToOpen = true;
        Check(CustomerInventory.RevealPath(nested) && opener.openCount == 1, "safe container is opened by the normal handler");
        ItemMouseDoubleClickHandler.current = null;
        Check(CustomerInventory.RevealPath(nested) && opener.openCount == 1, "already open container does not require the handler");

        host.contentWindow.Hide();
        ItemMouseDoubleClickHandler.current = opener;
        opener.beforeOpen = _ => ShopInventory.Locations.Remove(nested);
        Check(!CustomerInventory.RevealPath(nested), "location is revalidated after the open callback");
        opener.beforeOpen = null;
        ShopInventory.Locations.Add(nested);

        var panels = new MachineryPanelRow();
        var tabs = new MachineryTabs();
        tabs.captions.Add("A"); tabs.captions.Add("B");
        panels.Panels.Add(new MachineryPanel { role = "storage", content = tabs });
        host.contentWindow.childElement = panels;
        nested.Role = "storage";
        nested.Page = 1;
        Check(CustomerInventory.RevealPath(nested) && tabs.selected == 1 && tabs.selectCount == 1,
            "the requested storage page is selected");
        Check(CustomerInventory.RevealPath(nested) && tabs.selectCount == 1,
            "locating an already selected page does not invoke selection again");
        PublicQueryAndLocate();
        Console.WriteLine($"Customer inventory query / locate / public evidence: {assertions} assertions passed.");
    }
}
