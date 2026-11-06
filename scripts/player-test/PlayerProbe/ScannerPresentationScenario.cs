using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Real scanner lifecycle and visible component/method-card regression.
public sealed class ScannerPresentationScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem sample, empty, nonReactive;
    private string raw;
    private int step;
    private float since = Time.realtimeSinceStartup, until;
    private static void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
    private static Transform Root(GameObject panel) => panel.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(t => t.name == "ShopAssist_LiquidBars" && t.gameObject.activeInHierarchy);
    private static string AllText(Transform root) => string.Join(" | ", root.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text));
    private static Button ButtonNamed(Transform root, string name) => root?.GetComponentsInChildren<Button>(true)
        .FirstOrDefault(b => b.gameObject.name == "Button_" + name && b.gameObject.activeInHierarchy);
    private static Transform Row(Transform root, string id) => root?.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(t => t.name == "Component_" + id && t.gameObject.activeInHierarchy);
    private static void Shot(string name) => ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-scanner-" + name + ".png"));

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - since < 100, "Scanner presentation timed out at step " + step);
        if (step == 0) { if (start.Tick()) { until = Time.realtimeSinceStartup + 4; step++; } return false; }
        if (step == 1)
        {
            if (Time.realtimeSinceStartup < until) return false;
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || WaterScannerUIManager.Instance == null || ChemScannerUIManager.Instance == null) return false;
            var overlay = StoreUIManager.Instance.controlHintOverlay;
            if (overlay != null && overlay.activeSelf) StoreUIManager.Instance.ToggleControlHintOverlay();
            sample = DirectoryMaster.Item("bottled_water"); empty = DirectoryMaster.Item("wine_bottle");
            nonReactive = DirectoryMaster.Item("wine_bottle");
            Need(sample != null && empty != null && nonReactive != null, "Cannot create samples");
            GeneralHelper.SetItemOwned(sample); GeneralHelper.SetItemOwned(empty); GeneralHelper.SetItemOwned(nonReactive);
            var inventory = EmporiumEntry.Instance.invElement;
            foreach (var item in new[] { sample, empty, nonReactive })
            {
                var slot = inventory.TryFindOneValidInventorySlot(item);
                Need(slot != null && slot.TryAcceptOnce(item.unitCount) == item.unitCount, "Cannot place scanner sample");
                WaterHelper.Remove(item, WaterHelper.GetCurrentCapacityUL(item));
            }
            WaterHelper.AddLiquid(sample, "water", 730000); WaterHelper.AddLiquid(sample, "organic_waste", 12000);
            WaterHelper.AddLiquid(sample, "microbe", 23);
            WaterHelper.AddLiquid(nonReactive, "water", 730000); WaterHelper.AddLiquid(nonReactive, "organic_waste", 12000);
            WaterHelper.AddLiquid(nonReactive, "microbe", 23);
            Need(sample.onOneSecondItemFunc != null && nonReactive.onOneSecondItemFunc == null,
                "Fixture does not distinguish reactive and nonreactive liquid containers");
            ((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f19.enabled")).Set(true);
            ((ConfigEntry<int>)ModConfig.Find(Mod).Get("f19.size")).Set(14);
            WaterScannerUIManager.Instance.Open(null, sample);
            raw = WaterScannerUIManager.Instance.contaminantTMP.text;
            step++; return false;
        }
        if (step == 2)
        {
            var ui = WaterScannerUIManager.Instance; var root = Root(ui.panel);
            Need(root != null && !ui.contaminantTMP.enabled && ui.contaminantTMP.text == raw, "Water presentation missing or native text replaced");
            Need(Row(root, "water") != null && Row(root, "organic_waste") != null && Row(root, "microbe") != null &&
                AllText(root).Contains("<0.01%"), "Current sample or trace component missing: " + AllText(root));
            Need(root.GetComponentsInChildren<ScrollRect>(true).Any(), "Composition is not scrollable");
            Need(ui.testedTMP.fontSizeMax > 12 && ui.testedTMP.text.Contains("98"), "Native quality header was not retained and enlarged");
            Shot("water-layout");
            until = Time.realtimeSinceStartup + 1; step = 21; return false;
        }
        if (step == 21)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var options = StoreUIManager.Instance.gameOptionUIManager;
            options.OpenUI(); var settings = CustomUIManager.Instance.GetWindow("game_options");
            Need(settings?.Get(Mod + ":f19.size")?.slider != null, "Scanner font setting missing");
            settings.Get(Mod + ":f19.size").SetFloat(28, true); settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
            var largeRoot = Root(WaterScannerUIManager.Instance.panel);
            Need(largeRoot != null && AllText(largeRoot).Contains("<0.01%"), "Large font lost trace percentage");
            foreach (var text in Row(largeRoot, "microbe").GetComponentsInChildren<TMP_Text>(true))
            {
                text.ForceMeshUpdate();
                Need(!text.isTextTruncated, "Large font clipped a component field: " + text.text);
            }
            options.OpenUI(); settings = CustomUIManager.Instance.GetWindow("game_options");
            settings.Get(Mod + ":f19.size").SetFloat(14, true); settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
            var root = Root(WaterScannerUIManager.Instance.panel);
            var how = ButtonNamed(Row(root, "organic_waste"), ModHelper.GetLocalized(Mod, "f19.how"));
            Need(how != null, "Missing component method button"); how.onClick.Invoke();
            step = 3; return false;
        }
        if (step == 3)
        {
            var root = Root(WaterScannerUIManager.Instance.panel);
            Need(root != null && root.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_water_filter") &&
                root.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_purifier"), "Verified filter/purifier cards unavailable: " + AllText(root));
            Need(root.GetComponentsInChildren<Image>(true).Any(i => i.name == "Icon" && i.sprite != null), "Native method sprite missing");
            Shot("method-cards");
            until = Time.realtimeSinceStartup + 1; step = 31; return false;
        }
        if (step == 31)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var root = Root(WaterScannerUIManager.Instance.panel);
            var back = ButtonNamed(root, ModHelper.GetLocalized(Mod, "f19.back")); Need(back != null, "Missing card back button"); back.onClick.Invoke();
            step = 4; return false;
        }
        if (step == 4)
        {
            var ui = WaterScannerUIManager.Instance; var root = Root(ui.panel);
            var pin = ButtonNamed(Row(root, "organic_waste"), "☆") ?? ButtonNamed(Row(root, "organic_waste"), "★");
            Need(pin != null, "Missing pin button");
            // Ensure this component is pinned even if a saved choice was present.
            if (pin.name.EndsWith("☆", StringComparison.Ordinal)) pin.onClick.Invoke();
            root = Root(ui.panel);
            Transform organic = Row(root, "organic_waste"), water = Row(root, "water");
            Need(organic.GetSiblingIndex() < water.GetSiblingIndex(), "Pinned row did not move first");
            var microbeHow = ButtonNamed(Row(root, "microbe"), ModHelper.GetLocalized(Mod, "f19.how"));
            Need(microbeHow != null, "Missing microbe method button"); microbeHow.onClick.Invoke();
            step = 41; return false;
        }
        if (step == 41)
        {
            var ui = WaterScannerUIManager.Instance; var root = Root(ui.panel);
            Need(root.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_uv") &&
                root.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_water_tablet"),
                "Native UV or chlorine reaction method unavailable: " + AllText(root));
            ui.Close();
            Need(ui.contaminantTMP.enabled && ui.contaminantTMP.text == raw, "Closing did not restore original text and visibility");
            ui.Open(null, nonReactive);
            root = Root(ui.panel);
            var how = ButtonNamed(Row(root, "microbe"), ModHelper.GetLocalized(Mod, "f19.how"));
            Need(how != null, "Nonreactive sample lacks microbe row"); how.onClick.Invoke();
            step = 42; return false;
        }
        if (step == 42)
        {
            var ui = WaterScannerUIManager.Instance; var root = Root(ui.panel);
            Need(root != null && !root.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_water_tablet") &&
                root.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Method_uv"),
                "A container without the reaction callback advertised a chlorine reaction");
            ui.Close();
            ChemScannerUIManager.Instance.Open(null, sample); step = 5; return false;
        }
        if (step == 5)
        {
            var ui = ChemScannerUIManager.Instance; var root = Root(ui.panel);
            Need(root != null && Row(root, "microbe") != null && ui.productNameTMP.enabled && !ui.totalVolumeTMP.enabled,
                "Chemical scanner layout missing or native product name lost");
            Shot("chem-layout");
            until = Time.realtimeSinceStartup + 1; step = 51; return false;
        }
        if (step == 51)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var ui = ChemScannerUIManager.Instance;
            ui.contaminantTMP.text = "external scanner writer";
            step = 6; return false;
        }
        if (step == 6)
        {
            var ui = ChemScannerUIManager.Instance;
            if (Root(ui.panel) != null) return false;
            Need(ui.contaminantTMP.enabled && ui.contaminantTMP.text == "external scanner writer", "A later scanner writer was overwritten");
            ui.RefreshUI(); Need(Root(ui.panel) != null, "Native refresh did not rebuild the current sample");
            ui.Close();
            WaterScannerUIManager.Instance.Open(null, empty);
            Need(Root(WaterScannerUIManager.Instance.panel) == null && WaterScannerUIManager.Instance.contaminantTMP.enabled,
                "Empty sample retained stale measurements");
            WaterScannerUIManager.Instance.Close(); until = Time.realtimeSinceStartup + 1; step++; return false;
        }
        if (Time.realtimeSinceStartup < until) return false;
        result = "PASS both scanner layouts, trace component, method sprites, pin, external writer, empty sample and close restoration"; return true;
    }
}
