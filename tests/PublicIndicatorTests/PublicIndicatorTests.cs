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

    private static void PublicDemandAndCustomTooltip()
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
        CustomerIndicators.DemandDisplayed(client);
        indicator.Tick();
        Check(Outline(a) != null && Outline(b) != null, "ordinary public type is highlighted without hovering");
        Check(Outline(custom) == null, "custom tooltip contents are unknown before display");
        var peer = new GameObject("CDH_Border");
        peer.transform.SetParent(a.middle.transform);
        var peerGraphic = peer.Add(new UnityEngine.UI.Graphic { color = Color.white });
        indicator.Tick();
        Check(Outline(a).gameObject.activeSelf, "the peer's unconfigured delayed border does not hide the helper");
        peerGraphic.color = new Color(0.3f, 0.9f, 0.35f, 0.95f);
        indicator.Tick();
        Check(!Outline(a).gameObject.activeSelf, "a visible peer demand border suppresses the duplicate");
        peer.SetActive(false);
        indicator.Tick();
        Check(Outline(a).gameObject.activeSelf, "the demand border returns when the peer is toggled off");
        Check(indicator.Match(a) == indicator.Match(b), "hidden acceptance state cannot change public matching");
        var text = new RichText { nakedText = "Bread [Food]" };
        CustomerIndicators.TooltipBuilt(custom, text);
        indicator.Tick();
        Check(Outline(custom) == null, "building a tooltip alone is not evidence");
        CustomerIndicators.TooltipDisplayed(text);
        indicator.Tick();
        Check(Outline(custom) != null, "an actually displayed custom tooltip provides public evidence");
        CustomerIndicators.ItemStateChanging(custom);
        indicator.Tick();
        Check(Outline(custom) == null, "changing the item invalidates custom tooltip evidence");
        PlayerStore.Instance.currentClientInstance = new StoreClientInstance();
        indicator.Tick();
        Check(Outline(a) == null && Outline(b) == null, "changing customer immediately clears old demand");
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
        float originalY = ((RectTransform)Outline(box).children[0]).anchoredPosition.y;
        var containerPeer = new GameObject("CDH_Border");
        containerPeer.transform.SetParent(box.middle.transform);
        containerPeer.Add(new UnityEngine.UI.Graphic { color = new Color(1f, 0.7f, 0.1f, 0.95f) });
        indicator.Tick();
        Check(Outline(box).gameObject.activeSelf && ((RectTransform)Outline(box).children[0]).anchoredPosition.y < originalY,
            "the distinct contained-risk dashes move inside the peer's solid border");
        containerPeer.SetActive(false);
        indicator.Tick();
        Check(((RectTransform)Outline(box).children[0]).anchoredPosition.y == originalY,
            "contained-risk dashes return to the edge when the peer is switched off");
        var peer = new GameObject("CDH_Border");
        peer.transform.SetParent(stolen.rotator.transform);
        var peerGraphic = peer.Add(new UnityEngine.UI.Graphic { color = new Color(1f, 0.7f, 0.1f, 0.95f) });
        indicator.Tick();
        Check(!Outline(stolen).gameObject.activeSelf, "the same direct risk from the peer does not get two borders");
        peerGraphic.color = new Color(0.95f, 0.22f, 0.2f, 0.95f);
        indicator.Tick();
        Check(Outline(stolen).gameObject.activeSelf, "a different peer risk does not hide the known stolen state");
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
        CustomerIndicators.DemandDisplayed(client);
        Check(indicator.Match(item) == PublicMatch.Match,
            "a visible interest can match an ordinary item's publicly shown type");
        client.noAcceptingContraband = true;
        CustomerIndicators.DemandDisplayed(client);
        Check(indicator.Match(item) == PublicMatch.Unknown,
            "the separately displayed no-contraband restriction applies to an observed risk tag");
    }

    private static void Main()
    {
        PublicDemandAndCustomTooltip();
        ObservedRiskAndMutation();
        VisibleRestrictions();
        Console.WriteLine($"Public indicators: {assertions} assertions passed.");
    }
}
