using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// All fixtures live in a fresh, isolated QA save and use native item/inventory APIs.
public sealed class MachinesScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem machine, other, idle, installed, sample, seed;
    private GameGridInventory otherBay;
    private string raw, chosen, chosenId, chemRaw;
    private int step;
    private float deadline = Time.realtimeSinceStartup + 90;
    private float waitUntil;

    private static void Require(bool value, string detail)
    {
        if (!value) throw new InvalidOperationException(detail);
    }

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup > deadline) throw new InvalidOperationException("Machines QA timeout at step " + step);
        if (step == 0)
        {
            if (!start.Tick()) return false;
            step++; return false;
        }
        if (step == 1)
        {
            if (EmporiumEntry.Instance == null || StoreUIManager.Instance?.gameOptionUIManager == null ||
                EmporiumEntry.Instance.storeInteractables == null || !EmporiumEntry.Instance.storeInteractables.activeInHierarchy ||
                ItemMouseDoubleClickHandler.current == null || WaterScannerUIManager.Instance == null) return false;
            var e = EmporiumEntry.Instance;
            machine = New("hydroponic"); machine.name = "QA Hydro A";
            other = New("hydroponic"); other.name = "QA Hydro B";
            idle = New("system_module_performance");
            installed = New("system_module_quality");
            sample = New("wine_bottle");
            Put(e.invElement, machine); Put(e.invElement, other); Put(e.invElement, idle);
            Put(e.invElement, installed); Put(e.invElement, sample);
            ItemMouseDoubleClickHandler.current.OpenContentAction(other, new Vector2(150, 140));
            otherBay = Bay(other);
            Put(otherBay, installed);
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(300, 160));
            Require(machine.contentWindow.IsVisible() && other.contentWindow.IsVisible(), "Fixture machine windows did not open");
            other.contentWindow.Hide();
            Require(!other.contentWindow.IsVisible(), "Could not close the other machine before listing its modules");
            machine.contentWindow.ToFront();
            var options = Options();
            foreach (string key in new[] { "f10.enabled", "f19.enabled", "f30.enabled" })
            {
                var toggle = options.Get(Mod + ":" + key);
                Require(toggle != null, "Missing option " + key);
                toggle.SetBool(true, true);
            }
            options.Get(Mod + ":f15.enabled").SetBool(false, true);
            options.Get("apply").button.onClick.Invoke();
            foreach (string key in new[] { "f10.enabled", "f19.enabled", "f30.enabled" })
                Require(((ConfigEntry<bool>)ModConfig.Find(Mod).Get(key)).Value, "Option did not apply: " + key);
            StoreUIManager.Instance.gameOptionUIManager.CloseUI();
            step++; return false;
        }
        if (step == 2)
        {
            RoutingScenario.OpenOptionAction("f10.open");
            var browser = CustomUIManager.Instance.GetWindow(Mod + ".f10");
            Require(browser?.IsAlive == true, "Module browser did not open");
            bool hasIdle = QaItemUi.Hit(browser.root, idle) != null &&
                browser.root.GetComponentsInChildren<Image>(true).Any(image => image.sprite == ((GameItemElement)idle).background.image.sprite);
            browser.Get("installed_tab").button.onClick.Invoke();
            browser = CustomUIManager.Instance.GetWindow(Mod + ".f10");
            for (int page = 0; page < 20 && QaItemUi.Hit(browser.root, installed) == null; page++)
            {
                var next = browser.Get("next")?.button;
                if (next == null) break;
                string previous = browser.Get("module_page")?.GetText();
                next.onClick.Invoke(); browser = CustomUIManager.Instance.GetWindow(Mod + ".f10");
                if (browser.Get("module_page")?.GetText() == previous) break;
            }
            string all = Text(browser);
            Require(hasIdle && all.Contains("QA Hydro B") &&
                browser.root.GetComponentsInChildren<Image>(true).Any(image => image.sprite == ((GameItemElement)installed).background.image.sprite),
                "Module browser omitted idle item or installed item/source: " + all);
            Require(ReferenceEquals(installed.parentInventory, otherBay) && ReferenceEquals(idle.parentInventory, EmporiumEntry.Instance.invElement),
                "Opening the browser moved a module");
            browser.Close();
            RoutingScenario.OpenOptionAction("f30.open");
            var overview = CustomUIManager.Instance.GetWindow(Mod + ".f30");
            Require(overview?.IsAlive == true, "Machine overview did not open");
            string overviewText = Text(overview);
            string noSeed = ModHelper.GetLocalized(Mod, "f30.seed");
            Require(overviewText.Contains("QA Hydro A") && overviewText.Contains("QA Hydro B") && overviewText.Contains(noSeed),
                "Machine overview omitted fixture or known missing seed: " + overviewText);
            Require(otherBay.childItems.Contains(installed) && machine.parentInventory != null, "Overview changed machine inventory");
            seed = New("nutrifruit_seed");
            Put(EmporiumEntry.Instance.invElement, seed);
            Put(MachineHydroponic.GetInputSlot(machine), seed);
            step = 20; return false;
        }
        if (step == 20)
        {
            var overview = CustomUIManager.Instance.GetWindow(Mod + ".f30");
            Require(overview?.IsAlive == true, "Machine overview disappeared after inventory change");
            string changedText = Text(overview);
            Require(changedText.Contains(ModHelper.GetLocalized(Mod, "f30.water_container")),
                "Machine overview did not refresh the newly missing water container: " + changedText);
            overview.Close(); StoreUIManager.Instance.gameOptionUIManager.CloseUI();
            WaterHelper.AddWater(sample, 2, 750000);
            WaterScannerUIManager.Instance.Open(null, sample);
            raw = WaterScannerUIManager.Instance.contaminantTMP.text;
            Require(!string.IsNullOrWhiteSpace(raw), "Native scanner did not display liquid composition");
            step = 3; return false;
        }
        if (step == 3)
        {
            var scanner = WaterScannerUIManager.Instance;
            Transform bars = ScannerRoot(scanner.panel);
            Require(bars != null, "F19 did not create bars for real scanner output: " + raw);
            var images = bars.GetComponentsInChildren<Image>(true).Where(image => image.name == "LiquidBar").ToList();
            Require(images.Count > 0 && images.All(image => !image.raycastTarget) &&
                images.Any(image => image.gameObject.activeInHierarchy && image.rectTransform.rect.width > 0 && image.rectTransform.rect.height > 0) &&
                bars.GetComponentsInChildren<Button>(true).Any(button => button.gameObject.activeInHierarchy && button.interactable),
                "F19 bars or the interactive component actions are missing");
            Require(!scanner.contaminantTMP.enabled && scanner.contaminantTMP.text == raw, "F19 changed the native source text");
            string[] lines = scanner.contaminantTMP.text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Require(lines.Length > 1 && lines.All(line => raw.Contains(line)), "Scanner fixture lacks several unchanged native rows: " + raw);
            var rows = bars.GetComponentsInChildren<Transform>(true).Where(row => row.gameObject.activeInHierarchy && row.name.StartsWith("Component_", StringComparison.Ordinal)).ToList();
            Require(rows.Count > 1, "F19 did not display multiple component rows");
            chosenId = rows[1].name.Substring("Component_".Length);
            chosen = rows[1].GetComponentsInChildren<TMP_Text>(true).First().text;
            RoutingScenario.OpenOptionAction("f19.choose");
            var picker = CustomUIManager.Instance.GetWindow(Mod + ".f19.pin");
            Require(picker?.IsAlive == true, "Liquid picker did not open");
            Button button = picker.root.GetComponentsInChildren<Button>(true).FirstOrDefault(b =>
                b.GetComponentInChildren<TMP_Text>(true)?.text == chosen);
            Require(button != null, "Liquid picker omitted an observed component: " + chosen);
            button.onClick.Invoke();
            bars = ScannerRoot(scanner.panel);
            var pinnedRow = bars.GetComponentsInChildren<Transform>(true).FirstOrDefault(row => row.name == "Component_" + chosenId && row.gameObject.activeInHierarchy);
            var visibleRows = bars.GetComponentsInChildren<Transform>(true).Where(row => row.gameObject.activeInHierarchy && row.name.StartsWith("Component_", StringComparison.Ordinal)).ToList();
            Require(pinnedRow != null && visibleRows.All(row => row == pinnedRow || pinnedRow.GetSiblingIndex() < row.GetSiblingIndex()) &&
                scanner.contaminantTMP.text == raw, "Pinned component did not move to the first row or native text changed");
            // Let the new-game transition finish before capturing the actual scanner.
            waitUntil = Time.realtimeSinceStartup + 3;
            step++; return false;
        }
        if (step == 4)
        {
            if (Time.realtimeSinceStartup < waitUntil) return false;
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                var overlay = StoreUIManager.Instance.controlHintOverlay;
                if (overlay != null && overlay.activeSelf) StoreUIManager.Instance.ToggleControlHintOverlay();
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-machines.png"));
                waitUntil = Time.realtimeSinceStartup + 1;
            }
            step++; return false;
        }
        if (step == 5)
        {
            if (Time.realtimeSinceStartup < waitUntil) return false;
            var scanner = WaterScannerUIManager.Instance;
            var options = Options();
            options.Get(Mod + ":f19.enabled").SetBool(false, true);
            options.Get("apply").button.onClick.Invoke();
            StoreUIManager.Instance.gameOptionUIManager.CloseUI();
            step++; return false;
        }
        if (step == 6)
        {
            var scanner = WaterScannerUIManager.Instance;
            Require(ScannerRoot(scanner.panel) == null && scanner.contaminantTMP.enabled && scanner.contaminantTMP.text == raw,
                "F19 bars survived disabling the option");
            string restored = scanner.contaminantTMP.text;
            scanner.RefreshUI();
            Require(restored == scanner.contaminantTMP.text,
                "F19 did not restore the native scanner composition; restored=" + restored + " native=" + scanner.contaminantTMP.text);
            scanner.Close();
            var options = Options();
            options.Get(Mod + ":f19.enabled").SetBool(true, true);
            options.Get("apply").button.onClick.Invoke();
            StoreUIManager.Instance.gameOptionUIManager.CloseUI();
            Require(ChemScannerUIManager.Instance != null, "Native chemical scanner unavailable");
            ChemScannerUIManager.Instance.Open(null, sample);
            step++; return false;
        }
        if (step == 7)
        {
            var scanner = ChemScannerUIManager.Instance;
            var bars = ScannerRoot(scanner.panel);
            Require(bars != null && !scanner.contaminantTMP.enabled && bars.GetComponentsInChildren<Image>(true).Any(image =>
                image.name == "LiquidBar" && image.gameObject.activeInHierarchy && !image.raycastTarget && image.rectTransform.rect.width > 0) &&
                bars.GetComponentsInChildren<Button>(true).Any(button => button.gameObject.activeInHierarchy && button.interactable),
                "F19 did not render chemical scanner bars and component actions");
            chemRaw = scanner.contaminantTMP.text;
            TradeScenario.SetOptions(false, "f19.enabled");
            step++; return false;
        }
        if (step == 8)
        {
            var scanner = ChemScannerUIManager.Instance;
            Require(ScannerRoot(scanner.panel) == null && scanner.contaminantTMP.enabled && scanner.contaminantTMP.text == chemRaw,
                "Disabling F19 did not restore the native chemical scanner");
            scanner.RefreshUI();
            Require(scanner.contaminantTMP.text == chemRaw, "Chemical scanner native composition changed after disable");
            scanner.Close(); TradeScenario.SetOptions(true, "f19.enabled");
            Require(ReferenceEquals(installed.parentInventory, otherBay) && ReferenceEquals(idle.parentInventory, EmporiumEntry.Instance.invElement),
                "Read-only machine views moved a module");
            result = "PASS F10 idle/other-machine modules, F30 changing hydroponic state, F19 water/chemical scanner bars/pin/disable and read-only inventory";
            return true;
        }
        throw new InvalidOperationException("Unknown machines QA step");
    }

    private static GameItem New(string id)
    {
        var item = DirectoryMaster.Item(id);
        Require(item != null, "Missing native item " + id);
        GeneralHelper.SetItemOwned(item);
        return item;
    }

    private static void Put(GameInventory inventory, GameItem item)
    {
        var slot = inventory.TryFindOneValidInventorySlot(item);
        Require(slot != null && slot.inventory == inventory && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, inventory),
            "Could not place native QA fixture " + item.identifier);
    }

    private static GameGridInventory Bay(GameItem item)
    {
        var row = item.contentWindow.childElement as MachineryPanelRow;
        var panel = row?.Panels.SingleOrDefault(p => p.role == "module");
        var nodes = panel == null ? null : MachineryContainerUtils.Collect(panel.content);
        Require(nodes?.Count == 1 && nodes[0] is GameGridInventory, "Native module bay not found");
        return (GameGridInventory)nodes[0];
    }

    private static CustomUIWindow Options()
    {
        StoreUIManager.Instance.gameOptionUIManager.OpenUI();
        var window = CustomUIManager.Instance.GetWindow("game_options");
        Require(window != null, "Native options window missing");
        return window;
    }

    private static Transform ScannerRoot(GameObject panel) => panel?.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(node => node.name == "ShopAssist_LiquidBars" && node.gameObject.activeInHierarchy);

    private static string Text(CustomUIWindow window) => string.Join(" | ",
        window.root.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text).Where(s => !string.IsNullOrEmpty(s)));
}
