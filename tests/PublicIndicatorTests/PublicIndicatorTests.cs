using System;
using System.Collections.Generic;
using ShopAssist;
using UnityEngine;

internal static class PublicIndicatorTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static PixelWindow Window()
    {
        var window = new PixelWindow();
        WindowsHandler.current.visibleWindows.Add(window);
        return window;
    }

    private static Transform Outline(GameItemElement item) => item.rotator.transform.Find("ShopAssist_Outline");

    private static void Reset()
    {
        WindowsHandler.current = new WindowsHandler();
        RenderHandler.current = new RenderHandler();
        PlayerStore.Instance = new PlayerStore();
        NegociationUIManager.Instance = new NegociationUIManager();
        OverlayHandler.current = new OverlayHandler();
        ShopInventory.InStore = true;
        ShopInventory.Locations.Clear();
        Time.frameCount++;
    }

    private static void NativeDemandAndCustomTooltip()
    {
        Reset();
        var settings = new ShopSettings();
        var errors = new List<string>();
        using var indicator = new CustomerIndicators(settings, errors.Add);
        indicator.Start();
        PixelWindow window = Window();
        var inventory = new GameGridInventory();
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = inventory, Ordinary = true });
        var a = new GameItemElement(window, null) { parentInventory = inventory, hiddenAcceptance = false };
        var b = new GameItemElement(window, null) { parentInventory = inventory, hiddenAcceptance = true };
        var custom = new GameItemElement(window, null) { parentInventory = inventory, tooltipRichTextFunc = new object() };
        a.types.Add("FOOD"); b.types.Add("FOOD"); custom.types.Add("FOOD");
        StoreClient client = PlayerStore.Instance.currentClientInstance.client;
        client.clientBuyingTagList.Add("FOOD");
        indicator.Tick();
        Check(Outline(a) != null && Outline(b) == null && Outline(custom) != null,
            "the native buyer result controls ordinary, refused and custom-tooltip items");
        Check(indicator.Matches(a) && !indicator.Matches(b), "native refusal overrides a matching category");
        var text = new RichText { nakedText = "Bread [Food]" };
        CustomerIndicators.TooltipBuilt(custom, text);
        indicator.Tick();
        Check(Outline(custom) != null, "native demand does not depend on viewing the tooltip");
        CustomerIndicators.TooltipDisplayed(text);
        indicator.Tick();
        Check(Outline(custom) != null && indicator.KnownTypes(custom).Contains("FOOD"), "tooltip evidence remains available to other features");
        string observed = null;
        void Observe(GameItemElement item, string value) { if (ReferenceEquals(item, custom)) observed = value; }
        CustomerIndicators.PublicTooltipShown += Observe;
        var native = new RichText { nakedText = "Bread [Food]" };
        var composed = new RichText { nakedText = "Bread [Food]\nMOD label" };
        CustomerIndicators.TooltipBuilt(custom, native);
        CustomerIndicators.TooltipComposed(custom, native, composed);
        CustomerIndicators.TooltipDisplayed(composed);
        CustomerIndicators.PublicTooltipShown -= Observe;
        Check(observed == native.nakedText, "a displayed card must expose only the native tooltip as evidence");
        CustomerIndicators.ItemStateChanging(custom);
        indicator.Tick();
        Check(Outline(custom) != null && indicator.KnownTypes(custom) == null, "item changes invalidate observations without suppressing native demand");
        PlayerStore.Instance.currentClientInstance = new StoreClientInstance();
        indicator.Tick();
        Check(Outline(a) == null && Outline(b) == null, "changing customer immediately clears old demand");
        var next = PlayerStore.Instance.currentClientInstance.client;
        next.clientBuyingIdList.Add(custom.identifier);
        indicator.Tick(); Time.unscaledTime += 1; indicator.Tick();
        Check(Outline(custom) != null, "a specific native item ID matches without a disclosed category");
        next.clientBuyingIdList.Clear(); next.clientBuyingTagList.Add("ALL_TYPE");
        Time.unscaledTime += 1; indicator.Tick();
        Check(Outline(a) == null, "the all-goods merchant is hidden by default");
        settings.Set("f03.ignore_merchant", false); indicator.Tick();
        Check(Outline(a) != null, "the all-goods merchant can be enabled");
        next.clientIntent = StoreClient.ClientIntent.BARTER;
        next.barterOffer = new BarterOffer();
        next.barterOffer.conditions.Add(new BarterCondition { requiredItemId = "barter" });
        a.identifier = "barter"; custom.identifier = "other";
        Time.unscaledTime += 1; indicator.Tick();
        Check(Outline(a) != null && Outline(custom) == null, "barter uses its native per-item condition");
        Check(errors.Count == 0, "public demand flow should not log errors");
    }

    private static void ObservedRiskAndMutation()
    {
        Reset();
        var settings = new ShopSettings();
        using var indicator = new CustomerIndicators(settings, message => throw new Exception(message));
        indicator.Start();
        PlayerStore.Instance.currentClientInstance.client.clientIntent = StoreClient.ClientIntent.INSPECTION;
        PixelWindow outerWindow = Window();
        var root = new GameGridInventory();
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = root, Ordinary = true });
        var box = new GameItemElement(outerWindow, "Box") { parentInventory = root };
        PixelWindow innerWindow = box.contentWindow;
        innerWindow.Show();
        WindowsHandler.current.visibleWindows.Add(innerWindow);
        var inner = new GameGridInventory();
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = inner, Owner = box, Ordinary = true });
        var stolen = new GameItemElement(innerWindow, null) { parentInventory = inner };
        stolen.tags.Add(StolenHelper.STOLEN_TAG);
        indicator.Tick();
        Check(Outline(stolen) != null, "visible recognizable stolen item is highlighted for an inspector");
        Check(Outline(box) != null, "an observed ordinary container receives a contained-risk mark");
        innerWindow.Hide();
        WindowsHandler.current.visibleWindows.Remove(innerWindow);
        VisibleInventoryViews.NotifyChanged();
        indicator.Tick();
        Check(Outline(box) != null && Outline(stolen) == null, "closing retains the observed path without drawing hidden items");
        CustomerIndicators.InventoryChanging(inner, stolen);
        indicator.Tick();
        Check(Outline(box) == null, "a content change invalidates the remembered path");
    }

    private static void VisibleRestrictions()
    {
        Reset();
        var settings = new ShopSettings();
        using var indicator = new CustomerIndicators(settings, message => throw new Exception(message));
        indicator.Start();
        var item = new GameItemElement(Window(), null);
        item.types.Add("FOOD");
        item.tags.Add(ContrabandHelper.CONTRABAND_ITEM_TAG);
        item.tagValues[ContrabandHelper.CONTRABAND_LEVEL] = ContrabandHelper.CONTRABAND_LEVEL_LOW;
        StoreClient client = PlayerStore.Instance.currentClientInstance.client;
        client.clientBuyingTagList.Add("FOOD");
        Check(indicator.Matches(item), "the native buyer accepts the matching item");
        client.noAcceptingContraband = true;
        Check(!indicator.Matches(item), "the native no-contraband restriction rejects the item");
    }

    private static void ColorsContainersAndMachineOutput()
    {
        Reset();
        var settings = new ShopSettings();
        using var indicator = new CustomerIndicators(settings, message => throw new Exception(message));
        indicator.Start();
        var window = Window();
        var stock = new GameGridInventory();
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = stock, Ordinary = true });
        GameItemElement Goods(GameGridInventory inventory)
        {
            var value = new GameItemElement(window, null) { parentInventory = inventory };
            value.types.Add("FOOD"); inventory.childItems.Add(value); return value;
        }
        var normal = Goods(stock);
        var stolen = Goods(stock); stolen.tags.Add(StolenHelper.STOLEN_TAG);
        var contraband = Goods(stock); contraband.tags.Add(ContrabandHelper.CONTRABAND_ITEM_TAG);
        var hidden = Goods(stock); hidden.tags.Add(StolenHelper.STOLEN_TAG); hidden.tags.Add("ITEM_HIDDEN_TAG");
        var machineInput = new GameGridInventory();
        var machineOutput = new GameGridInventory();
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = machineInput, IsMachine = true, Role = "input" });
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = machineOutput, IsMachine = true, Role = "output" });
        var input = Goods(machineInput); var output = Goods(machineOutput);
        var bag = new GameItemElement(window, null) { parentInventory = stock };
        stock.childItems.Add(bag);
        var bagContents = new GameGridInventory();
        ShopInventory.Locations.Add(new ShopInventory.Location { Inventory = bagContents, Owner = bag, Path = new GameItem[] { bag }, Ordinary = true });
        var nested = new GameItemElement(bag.contentWindow, null) { parentInventory = bagContents };
        nested.types.Add("FOOD"); bagContents.childItems.Add(nested);
        PlayerStore.Instance.currentClientInstance.client.clientBuyingTagList.Add("FOOD");
        indicator.Tick();
        Color ColorOf(GameItemElement item) => Outline(item).children[0].gameObject.GetComponent<UnityEngine.UI.Image>().color;
        Check(Outline(normal) != null && ColorOf(normal).g > ColorOf(normal).r, "ordinary accepted goods use green");
        Check(Outline(stolen) != null && ColorOf(stolen).r > ColorOf(stolen).g && ColorOf(stolen).g > ColorOf(stolen).b,
            "accepted stolen goods use amber");
        Check(Outline(contraband) != null && ColorOf(contraband).r > ColorOf(contraband).g, "accepted contraband uses red");
        Check(Outline(input) == null && Outline(output) != null, "machine work slots are excluded while output is highlighted");
        Check(Outline(bag) != null && Outline(bag).childCount > Outline(normal).childCount,
            "a visible outer container receives a distinct dashed hint for matching contents");
        var seller = PlayerStore.Instance.currentClientInstance.client;
        seller.clientIntent = StoreClient.ClientIntent.SELL;
        stolen.isOwned = false;
        settings.Set("f03.client_goods", true); Time.unscaledTime += 1; indicator.Tick();
        Check(Outline(stolen) != null && Outline(normal) == null && Outline(hidden) == null,
            "seller-owned stolen goods can be highlighted without exposing hidden owned goods");
    }

    private static void Main()
    {
        NativeDemandAndCustomTooltip();
        ObservedRiskAndMutation();
        VisibleRestrictions();
        ColorsContainersAndMachineOutput();
        Console.WriteLine($"Public indicators: {assertions} assertions passed.");
    }
}
