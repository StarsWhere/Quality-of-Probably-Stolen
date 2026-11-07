using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public sealed class MachineLifecycleScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem machine, sample;
    private IMod instance;
    private string scannerRaw;
    private int step;
    private float deadline = Time.realtimeSinceStartup + 75;
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

    public bool Tick(out string result)
    {
        result = null;
        Require(Time.realtimeSinceStartup < deadline, "Machine lifecycle timed out at step " + step);
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
            machine = New("hydroponic"); sample = New("wine_bottle");
            Put(EmporiumEntry.Instance.invElement, machine); Put(EmporiumEntry.Instance.invElement, sample);
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(260, 140));
            machine.contentWindow.ToFront();
            var config = ModConfig.Find(Mod);
            Require(config?.Get("f19.enabled") != null && config.Get("f19.pinned") != null &&
                config.Get("f19.enabled").Section == config.Get("f19.pinned").Section,
                "Pinned liquid setting is in a different gear section");
            var options = StoreUIManager.Instance.gameOptionUIManager;
            options.OpenUI(); var window = CustomUIManager.Instance.GetWindow("game_options");
            window.Get(Mod + ":f10.enabled").SetBool(true, true);
            window.Get(Mod + ":f19.enabled").SetBool(true, true);
            window.Get(Mod + ":f15.enabled").SetBool(false, true);
            window.Get("apply").button.onClick.Invoke(); options.CloseUI();
            WaterHelper.AddWater(sample, 2, 750000);
            WaterScannerUIManager.Instance.Open(null, sample);
            scannerRaw = WaterScannerUIManager.Instance.contaminantTMP.text;
            Require(VisibleBars(), "Scanner bars absent before disable");
            instance = ModLoader.Mods.Single(m => m.Manifest != null && m.Manifest.ID == Mod).Instance;
            Require(instance != null, "Loaded MOD instance unavailable");
            instance.OnDisable();
            step++; return false;
        }
        if (step == 2)
        {
            Require(ScannerRoot() == null && WaterScannerUIManager.Instance.contaminantTMP.enabled &&
                WaterScannerUIManager.Instance.contaminantTMP.text == scannerRaw,
                "Scanner bars survived OnDisable");
            instance.OnEnable();
            WaterScannerUIManager.Instance.RefreshUI();
            Require(VisibleBars(), "Scanner bars did not return after same-instance OnEnable");
            RoutingScenario.RunFeatureAction("f10.open");
            Require(CustomUIManager.Instance.GetWindow(Mod + ".f10")?.IsAlive == true, "Module browser failed after re-enable");
            result = "PASS F19 shared gear section and same-instance disable/re-enable restores F19 and F10";
            return true;
        }
        throw new InvalidOperationException("Unknown machine lifecycle step");
    }
    private static bool VisibleBars()
    {
        var root = ScannerRoot();
        return root != null && root.GetComponentsInChildren<Image>(true).Any(image => image.name == "LiquidBar" &&
            image.gameObject.activeInHierarchy && !image.raycastTarget && image.rectTransform.rect.width > 0);
    }
    private static Transform ScannerRoot() => WaterScannerUIManager.Instance?.panel?.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(node => node.name == "ShopAssist_LiquidBars" && node.gameObject.activeInHierarchy);
    private static GameItem New(string id) { var item = DirectoryMaster.Item(id); Require(item != null, "Missing " + id); GeneralHelper.SetItemOwned(item); return item; }
    private static void Put(GameInventory target, GameItem item)
    {
        var slot = target.TryFindOneValidInventorySlot(item);
        Require(slot != null && slot.targetItem == null && ReferenceEquals(slot.inventory, target) &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, target), "Could not place " + item.identifier);
    }
}
