using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

// A failed module-layout recovery must remain visible and block a repeated plan.
public sealed class ModuleRecoveryScenario : EquipmentFixture
{
    private GameItem machine, candidate;
    private GameGridInventory bay, source;
    private int after;

    public bool Tick(out string result)
    {
        result = null;
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f10.enabled", "f12.enabled");
            EmporiumEntry.Instance.invElement.SetShape(24, 20);
            machine = Stock("hydroponic"); candidate = Stock("system_module_performance");
            source = (GameGridInventory)candidate.parentInventory;
            bay = Panel<GameGridInventory>(machine, "module");
            Open(machine); machine.contentWindow.ToFront(); Action("f10.open");
            after = Time.frameCount + 3; step++; return false;
        }
        if (Time.frameCount < after) return false;
        var browser = Ui("f10");
        Check(browser?.IsAlive == true, "Module browser missing");
        LayoutScenario.Select(browser, candidate);
        browser.Get("layout").button.onClick.Invoke();
        browser = Ui("f10"); browser.Get("preview").button.onClick.Invoke(); browser = Ui("f10");
        Check(browser.Get("apply")?.button != null && ReferenceEquals(candidate.parentInventory, source), "No safe preview for recovery test");
        var priorAdd = source.mayInventoryAddItemFunc;
        Action<GameItem, GameInventory, SlotMarker> failAfterAdd = (item, inventory, marker) =>
        {
            if (ReferenceEquals(item, candidate)) throw new InvalidOperationException("QA module arrival interruption");
        };
        source.mayInventoryAddItemFunc = (item, inventory) => !ReferenceEquals(item, candidate) && (priorAdd?.Invoke(item, inventory) ?? true);
        bay.onSlotAddItemEndFunc += failAfterAdd;
        try { browser.Get("apply").button.onClick.Invoke(); }
        finally { bay.onSlotAddItemEndFunc -= failAfterAdd; source.mayInventoryAddItemFunc = priorAdd; }
        var assembly = ModLoader.Mods.Single(mod => mod.Manifest.ID == Mod).Assembly;
        var hasInspection = assembly.GetType("ShopAssist.AttentionBook", true).GetMethod("HasInspection", BindingFlags.Static | BindingFlags.NonPublic);
        bool machinePending = (bool)hasInspection.Invoke(null, new object[] { "f12.open", machine.uniqueId });
        bool itemPending = (bool)hasInspection.Invoke(null, new object[] { "f12.open", candidate.uniqueId });
        Check(machinePending && itemPending && ReferenceEquals(candidate.parentInventory, bay),
            "Failed recovery did not preserve both machine and item for inspection");
        browser = Ui("f10"); browser.Get("preview").button.onClick.Invoke(); browser = Ui("f10");
        Check(browser.Get("apply") == null && ReferenceEquals(candidate.parentInventory, bay),
            "An unreviewed module recovery allowed another automatic plan");
        result = "PASS interrupted module layout persists machine and source inspection and blocks repetition";
        return true;
    }
}
