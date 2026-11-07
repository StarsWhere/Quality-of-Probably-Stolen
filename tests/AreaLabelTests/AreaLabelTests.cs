using System;
using System.Collections.Generic;
using ShopAssist;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class AreaLabelTests
{
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void ResetGame()
    {
        WindowsHandler.current = new WindowsHandler();
        RenderHandler.current = new RenderHandler();
    }

    private static PixelWindow VisibleWindow()
    {
        var window = new PixelWindow();
        WindowsHandler.current.visibleWindows.Add(window);
        return window;
    }

    private static Transform ItemLabel(GameItemElement item) => item.middle.transform.Find("ShopAssist_AreaName");
    private static Transform Heading(PixelWindow window) => window.handler.transform.Find("ShopAssist_AreaHeading");
    private static TMPro.TMP_Text Text(Transform label) => label?.GetComponentInChildren<TMPro.TMP_Text>();

    private static void NamesAndLayout()
    {
        Check(AreaLabelPolicy.Name("自定义", "Storage", false) == "自定义", "custom name must take precedence");
        Check(AreaLabelPolicy.Name(" ", "Storage", false) == null, "blank custom names are not names");
        Check(AreaLabelPolicy.Name(null, "Storage", true) == "Storage", "vanilla fallback must be opt-in");
        Check(AreaLabelPolicy.Heading("工具", "工具") == "工具", "identical context must not repeat");
        Check(AreaLabelPolicy.Heading("工具", "备件") == "工具 · 备件", "distinct page must be shown");
        Check(!AreaLabelPolicy.ItemLayout(16, 16, true, true, 10, out _, out _, out _), "tiny counter needs unobstructed space");
        Check(AreaLabelPolicy.ItemLayout(64, 48, true, true, 10, out float width, out float height, out float y), "normal area should fit");
        Check(width <= 48 && y + height / 2 < -2 && y - height / 2 > -46, "label must stay inside the middle band");
        Check(AreaLabelPolicy.ItemLayout(64, 48, true, false, 14, out _, out _, out y) && y == -24,
            "the default centered placement must use the exact vertical middle");
        var rotated = new GameItemElement(new PixelWindow(), "Rotated");
        rotated.modifiedShape.width = 4; rotated.modifiedShape.height = 5; rotated.modifiedShape.orientation = 1;
        var actual = ItemOverlayLayout.Size(rotated);
        Check(actual.x == 80 && actual.y == 64, "overlay bounds must follow the rotated occupied footprint");
    }

    private static void VisibilityAndSettings()
    {
        ResetGame();
        var settings = new ShopSettings();
        using var labels = new AreaLabels(settings, _ => { });
        labels.Start();
        PixelWindow window = VisibleWindow();
        var named = new GameItemElement(window, "Parts");
        var unnamed = new GameItemElement(window, null);
        var ordinary = new GameItemElement(window, "Not an area") { contentWindow = null };
        labels.Tick();
        Check(Text(ItemLabel(named))?.text == "Parts", "visible named area should have a label");
        Check(Text(ItemLabel(named)).fontSizeMax == 14 && !ItemLabel(named).gameObject.GetComponent<UnityEngine.UI.Image>().enabled,
            "the default name must be readable and have no background");
        Check(ItemLabel(unnamed) == null && ItemLabel(ordinary) == null, "default and unrelated items stay unlabelled");
        var root = ItemLabel(named);
        Check(!root.gameObject.GetComponent<UnityEngine.UI.Image>().raycastTarget && !Text(root).raycastTarget,
            "decorative elements must not intercept input");
        settings.Set("f02.defaults", true);
        labels.Tick();
        Check(Text(ItemLabel(unnamed))?.text == "Storage", "enabling defaults updates visible views");
        settings.Set("f02.background", false);
        settings.Set("f02.font_size", 15);
        labels.Tick();
        Check(!ItemLabel(named).gameObject.GetComponent<UnityEngine.UI.Image>().enabled, "background follows the setting");
        Check(Text(ItemLabel(named)).fontSizeMax == 15, "font size follows the setting");
        settings.Set("f02.enabled", false);
        Check(ItemLabel(named) == null && ItemLabel(unnamed) == null, "disabling removes existing labels immediately");
        settings.Set("f02.enabled", true);
        labels.Tick();
        Check(Text(ItemLabel(named))?.text == "Parts", "re-enabling rediscovers visible items");
    }

    private static void RenameDragAndViews()
    {
        ResetGame();
        var settings = new ShopSettings();
        using var labels = new AreaLabels(settings, _ => { });
        labels.Start();
        PixelWindow window = VisibleWindow();
        var first = new GameItemElement(window, "Tools");
        var second = new GameItemElement(window, "Tools");
        labels.Tick();
        Check(ItemLabel(first) != ItemLabel(second), "similarly named instances have separate views");
        first.customName = "Spare parts";
        AreaLabels.ItemChanged(first);
        labels.Tick();
        Check(Text(ItemLabel(first))?.text == "Spare parts" && Text(ItemLabel(second))?.text == "Tools", "renaming updates the correct view");
        first.ToggleBackground(false);
        AreaLabels.ItemChanged(first);
        labels.Tick();
        Check(!ItemLabel(first).gameObject.activeSelf, "dragging the item away hides its label");
        first.ToggleBackground(true);
        AreaLabels.ItemChanged(first);
        labels.Tick();
        Check(ItemLabel(first).gameObject.activeSelf, "returning the item restores its label");
        window.Hide();
        AreaLabels.Invalidate();
        labels.Tick();
        Check(ItemLabel(first) == null && ItemLabel(second) == null, "closing a window releases its views");
        window.Show();
        AreaLabels.Invalidate();
        labels.Tick();
        Check(Text(ItemLabel(first))?.text == "Spare parts", "reopening creates a fresh label");
        first.Destroy();
        AreaLabels.ItemChanged(first);
        labels.Tick();
        Check(ItemLabel(first) == null, "destroyed item does not leave a label");
    }

    private static void NativePageAndHeader()
    {
        ResetGame();
        var settings = new ShopSettings();
        using var labels = new AreaLabels(settings, _ => { });
        labels.Start();
        PixelWindow inventory = VisibleWindow();
        var item = new GameItemElement(inventory, "Cabinet");
        PixelWindow content = item.contentWindow;
        content.Show();
        WindowsHandler.current.visibleWindows.Add(content);
        var row = new MachineryPanelRow();
        content.childElement = row;
        var panel = new MachineryPanel { role = "storage", handler = TreeNodeRender.Instantiate().SetParent(content.handler) };
        var tabs = new MachineryTabs { handler = TreeNodeRender.Instantiate().SetParent(panel.handler) };
        tabs.captions.AddRange(new[] { "Page 1", "Page 2" });
        panel.content = tabs;
        row.Panels.Add(panel);
        item.tabNames[0] = "Wires";
        item.tabNames[1] = "Bolts";
        labels.Tick();
        Check(Text(Heading(content))?.text == "Cabinet · Wires", "heading uses the current native storage-page name");
        tabs.Select(1);
        AreaLabels.Invalidate();
        labels.Tick();
        Check(Text(Heading(content))?.text == "Cabinet · Bolts", "changing page refreshes the heading");
        item.tabNames[1] = "Screws";
        tabs.SetCaption(1, "Screws");
        AreaLabels.Invalidate();
        labels.Tick();
        Check(Text(Heading(content))?.text == "Cabinet · Screws", "renaming a page refreshes the heading");
        content.hasTitle = true;
        AreaLabels.Invalidate();
        labels.Tick();
        Check(Heading(content) == null, "the original title is not covered or duplicated");
    }

    private static void Lifecycle()
    {
        ResetGame();
        var settings = new ShopSettings();
        var errors = new List<string>();
        using var labels = new AreaLabels(settings, errors.Add);
        labels.Start();
        PixelWindow window = VisibleWindow();
        var item = new GameItemElement(window, "Labeled");
        labels.Tick();
        Check(ItemLabel(item).gameObject.activeSelf, "the area name is visible");
        SceneManager.Change();
        Check(ItemLabel(item) == null, "changing scene clears owned objects");
        labels.Tick();
        Check(ItemLabel(item) != null, "the new scene can be rebuilt");
        labels.Dispose();
        Check(ItemLabel(item) == null, "dispose removes owned views");
        AreaLabels.Invalidate();
        labels.Tick();
        Check(ItemLabel(item) == null && errors.Count == 0, "disposed labels do not restart or fail");
    }

    private static void PublicLabelChanges()
    {
        ResetGame();
        var settings = new ShopSettings();
        var errors = new List<string>();
        using var labels = new ItemLabels(settings, errors.Add);
        labels.Start();
        PixelWindow window = VisibleWindow();
        var first = new GameItemElement(window, null);
        var second = new GameItemElement(window, null);
        var firstFeature = new ItemFeature { display = "First" };
        var secondFeature = new ItemFeature { display = "Second" };
        first.itemFeatures.Add(firstFeature); second.itemFeatures.Add(secondFeature);
        labels.Tick();
        TMPro.TMP_Text Text(GameItemElement item) => item.middle.transform.Find("ShopAssist_PublicLabels")?.GetComponentInChildren<TMPro.TMP_Text>();
        Check(Text(first)?.text == "First" && Text(second)?.text == "Second", "visible public labels are rendered");
        int before = secondFeature.reads;
        firstFeature.display = "Changed";
        VisibleInventoryViews.NotifyItemChanged(first);
        labels.Tick();
        Check(Text(first)?.text == "Changed" && secondFeature.reads == before,
            "one item change refreshes that label without reevaluating unrelated items");
        var custom = new GameItemElement(window, null) { tooltipRichTextFunc = new object() };
        CustomerIndicators.ShowTooltip(custom, "mech_label_tooltip_prefixObserved");
        labels.Tick();
        Check(Text(custom)?.text == "Observed", "an observed custom tooltip updates its own label");
        VisibleInventoryViews.NotifyItemChanged(custom);
        labels.Tick();
        Check(Text(custom) == null, "changing an item invalidates its observed custom label");
        first.Destroy();
        VisibleInventoryViews.NotifyItemChanged(first);
        labels.Tick();
        Check(Text(first) == null, "destroyed items lose their labels");
        window.Hide();
        VisibleInventoryViews.NotifyChanged();
        labels.Tick();
        Check(Text(second) == null && errors.Count == 0, "a window change still refreshes all visible labels");
    }

    private static void Main()
    {
        NamesAndLayout();
        VisibilityAndSettings();
        RenameDragAndViews();
        NativePageAndHeader();
        Lifecycle();
        PublicLabelChanges();
        Console.WriteLine($"Area labels: {assertions} assertions passed.");
    }
}
