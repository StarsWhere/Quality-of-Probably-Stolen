using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Covers a real 300 µL transfer, including liquid completely caught by a filter.
public sealed class FilterMicroScenario : EquipmentFixture
{
    private GameItem pitcher, worn, spare, source;
    private GameSlotInventory seat;
    private GameGridInventory supply;
    private float expires;
    private string diagnostic;

    public bool Tick(out string result)
    {
        result = null;
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f35.enabled");
            EmporiumEntry.Instance.invElement.SetShape(24, 20);
            pitcher = Stock("water_pitcher"); worn = Stock("kotton_fabric"); spare = Stock("kotton_fabric");
            source = Stock("wine_bottle"); seat = (GameSlotInventory)pitcher.contentWindow.childElement;
            WaterHelper.Remove(pitcher, WaterHelper.GetCurrentCapacityUL(pitcher));
            WaterHelper.AddWater(pitcher, 0, WaterHelper.GetTotalCapacity(pitcher) - 300);
            FeedScenario.Put(seat, worn); supply = Supply(pitcher, 35, spare);
            WaterHelper.Remove(source, WaterHelper.GetCurrentCapacityUL(source));
            WaterHelper.AddLiquid(source, "plant_matter", 1800);
            DurabilityHelper.SetCurrentDurability(worn, 1);
            int before = WaterHelper.GetCurrentCapacityUL(pitcher);
            WaterHelper.TransferLiquid(source, pitcher);
            Check(WaterHelper.GetCurrentCapacityUL(source) == 1500 && WaterHelper.GetCurrentCapacityUL(pitcher) == before &&
                DurabilityHelper.GetRealDurability(worn) == 0 && ReferenceEquals(seat.childItem, worn),
                "The native 300 µL fully filtered transfer did not reach the expected state");
            var assembly = ModLoader.Mods.Single(mod => mod.Manifest.ID == Mod).Assembly;
            var type = assembly.GetType("ShopAssist.MachineTools", true);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;
            var active = type.GetField("current", flags).GetValue(null);
            var pending = (IDictionary)type.GetField("pendingFilters", flags).GetValue(active);
            var filterPath = type.GetNestedType("FilterPath", BindingFlags.NonPublic);
            var capability = type.GetMethod("FilterCapability", flags).Invoke(active, new[] { pitcher, Enum.Parse(filterPath, "Transfer") });
            var capabilityType = capability.GetType();
            var swap = assembly.GetType("ShopAssist.MaintenanceSwap", true).GetMethod("Ready", flags);
            var attention = assembly.GetType("ShopAssist.AttentionBook", true).GetMethod("HasInspection", flags);
            diagnostic = "immediatePending=" + pending.Count + " capability=" + capabilityType.GetProperty("Allowed", flags).GetValue(capability) +
                " reason=" + capabilityType.GetProperty("ReasonKey", flags).GetValue(capability) +
                " swapReady=" + swap.Invoke(null, new object[] { pitcher, seat, supply, worn, spare, true, false }) +
                " inspection=" + attention.Invoke(null, new object[] { "f35.open", pitcher.uniqueId }) +
                " rules=" + ModHelper.GetModData(Mod, "machineMaintenance", "");
            Debug.Log("[PLAYER-QA] F35 micro " + diagnostic);
            expires = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        if (seat.childItem != spare && Time.realtimeSinceStartup < expires) return false;
        Check(ReferenceEquals(seat.childItem, spare) && ReferenceEquals(worn.parentInventory, supply),
            "The exhausted filter was not replaced after a fully filtered micro-transfer: " + diagnostic + " / " + MaintenanceState());
        result = "PASS 1800 to 1500 µL transfer with zero target growth triggers one conserved filter swap";
        return true;
    }
}
