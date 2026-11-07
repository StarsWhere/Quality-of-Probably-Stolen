using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Opens the real machine overview and inspects its item cards and changing sources/statuses.
public sealed class MachineOverviewScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem first, second, seed;
    private GameInventory secondSource;
    private string secondPath;
    private int step;
    private float started = Time.realtimeSinceStartup, until;
    private static void Need(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private static string Text(CustomUIWindow window) => string.Join(" | ",
        window.root.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text));
    private static GameItem Item(string id, string name)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing " + id);
        GeneralHelper.SetItemOwned(item, true); if (name != null) GeneralHelper.SetCustomName(item, name); return item;
    }
    private static bool TryPut(GameInventory inventory, GameItem item)
    {
        var slot = inventory?.TryFindOneValidInventorySlot(item);
        return slot != null && slot.targetItem == null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory;
    }
    private static void Put(GameInventory inventory, GameItem item) => Need(TryPut(inventory, item), "Cannot place " + item.identifier);
    private static CustomUIWindow Overview() => CustomUIManager.Instance.GetWindow(Mod + ".f30");
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup - started < 90, "Machine overview QA timed out at " + step);
        if (step == 0) { if (start.Tick()) { until = Time.realtimeSinceStartup + 4; step++; } return false; }
        if (step == 1)
        {
            if (Time.realtimeSinceStartup < until || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(false, "f15.enabled", "f17.enabled", "f21.enabled");
            var e = EmporiumEntry.Instance; first = Item("hydroponic", "日常水培机"); second = Item("hydroponic", "备用水培机");
            Put(e.invElement, first);
            var destinations = new[] { (inventory: (GameInventory)e.backInvinvElement, key: "inv.counter"),
                (inventory: (GameInventory)e.showcaseElement, key: "inv.showcase"),
                (inventory: (GameInventory)e.frontInvinvElement, key: "inv.front") };
            foreach (var destination in destinations)
                if (TryPut(destination.inventory, second)) { secondSource = destination.inventory; secondPath = destination.key; break; }
            Need(secondSource != null, "No second native location accepted the QA machine");
            TradeScenario.SetOptions(true, "f30.enabled");
            RoutingScenario.RunFeatureAction("f30.open");
            var window = Overview(); Need(window?.IsAlive == true, "Machine overview failed to open");
            Need(window.root.GetComponentsInChildren<Transform>(true).Any(t => t.name == "ShopAssist_ItemCards") &&
                window.root.GetComponentsInChildren<Image>(true).Any(image => image.sprite == ((GameItemElement)first).background.image.sprite),
                "Machine overview lacks actual item cards / native sprites");
            Need(Text(window).Contains("日常水培机") && Text(window).Contains("备用水培机") &&
                Text(window).Contains(ModHelper.GetLocalized(Mod, "f30.seed")), "Machine names or missing-seed status absent");
            LayoutScenario.Select(window, first); window = Overview();
            string source = ModHelper.GetLocalized(Mod, "vm.location") + " · " + ModHelper.GetLocalized(Mod, "inv.stock");
            Need(Text(window).Contains(source), "Selected machine does not show its full source: " + Text(window));
            Need(window.Get("locate")?.button != null, "Visible machine has no locate action");
            var overlay = StoreUIManager.Instance.controlHintOverlay;
            if (overlay != null && overlay.activeSelf) StoreUIManager.Instance.ToggleControlHintOverlay();
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-machine-overview.png"));
            until = Time.realtimeSinceStartup + 1; step++; return false;
        }
        if (step == 2)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var window = Overview(); LayoutScenario.Select(window, second); window = Overview();
            string source = ModHelper.GetLocalized(Mod, "vm.location") + " · " + ModHelper.GetLocalized(Mod, secondPath);
            Need(Text(window).Contains(source), "Selected second machine has wrong source: " + Text(window));
            LayoutScenario.Select(window, first); seed = Item("nutrifruit_seed", null);
            Put(EmporiumEntry.Instance.invElement, seed); Put(MachineHydroponic.GetInputSlot(first), seed);
            step++; return false;
        }
        if (step == 3)
        {
            var window = Overview();
            if (window == null || !Text(window).Contains(ModHelper.GetLocalized(Mod, "f30.water_container"))) return false;
            string source = ModHelper.GetLocalized(Mod, "vm.location") + " · " + ModHelper.GetLocalized(Mod, "inv.stock");
            Need(Text(window).Contains(source), "Selection/source lost after native inventory update");
            Need(first.parentInventory == EmporiumEntry.Instance.invElement && second.parentInventory == secondSource &&
                MachineHydroponic.GetInputSlot(first).childItem == seed, "Read-only overview moved a fixture");
            window.Close(); result = "PASS native machine cards / sprites, selected source and locate, live state update and unchanged inventory"; return true;
        }
        throw new InvalidOperationException("Unknown overview QA step");
    }
}
