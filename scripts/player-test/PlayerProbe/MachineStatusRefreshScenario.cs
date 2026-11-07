using System;
using System.Reflection;
using UnityEngine;

// The status window must follow the native item state without a manual refresh.
public sealed class MachineStatusRefreshScenario : EquipmentFixture
{
    private GameItem hydro, water, furnace;
    private int beforeFurnaceEnergy;
    private float expires;
    private static string Status(CustomUIWindow window, GameItem item)
    {
        var hit = QaItemUi.Hit(window.root, item);
        var entry = hit?.GetType().GetField("entry", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hit);
        return entry?.GetType().GetField("StatusLabel", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(entry) as string;
    }

    public bool Tick(out string result)
    {
        result = null;
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f30.enabled");
            EmporiumEntry.Instance.invElement.SetShape(24, 20);
            hydro = Stock("hydroponic"); furnace = Stock("furnace");
            var seed = Stock("hydroreed_seed"); water = Stock("wine_bottle");
            var hydroPower = Stock("energy_credit"); var furnacePower = Stock("energy_credit");
            PowerHelper.SetPowerSourceAt(hydroPower, 100); PowerHelper.SetPowerSourceAt(furnacePower, 100);
            FeedScenario.Put(MachineHydroponic.GetInputSlot(hydro), seed);
            WaterHelper.Remove(water, WaterHelper.GetCurrentCapacityUL(water));
            FeedScenario.Put(MachineHydroponic.GetWaterSlot(hydro), water);
            FeedScenario.Put(MachineHelper.GetBatterySlot(hydro), hydroPower);
            FeedScenario.Put(MachineHelper.GetBatterySlot(furnace), furnacePower);
            FeedScenario.Put(Panel<GameGridInventory>(furnace, "module"), Stock("furnace_module_junk"));
            var input = Panel<GameGridInventory>(furnace, "input");
            for (int i = 0; i < 6; i++) FeedScenario.Put(input, Stock("junk"));
            Check(input.childItems.Count == 6, "Native furnace fixture did not contain six separate junk items");
            beforeFurnaceEnergy = furnacePower.GetTagReadonly("power_source_item_energy").GetInt();
            Action("f30.open");
            var window = Ui("f30");
            Check(window?.IsAlive == true && Status(window, hydro) == ModHelper.GetLocalized(Mod, "f30.water"),
                "Empty hydroponic water was not shown as insufficient");
            Check(Status(window, furnace) == ModHelper.GetLocalized(Mod, "f30.verify_output") &&
                furnacePower.GetTagReadonly("power_source_item_energy").GetInt() == beforeFurnaceEnergy && input.childItems.Count == 6,
                "Junk module was reported as missing materials or the status view changed the furnace");
            WaterHelper.AddWater(water, 2, 750000);
            expires = Time.realtimeSinceStartup + 8f; step++; return false;
        }
        var overview = Ui("f30");
        string status = Status(overview, hydro);
        if (status != ModHelper.GetLocalized(Mod, "f30.ready") && Time.realtimeSinceStartup < expires) return false;
        Check(status == ModHelper.GetLocalized(Mod, "f30.ready"), "Hydroponic status did not update after adding water: " + status);
        result = "PASS read-only junk furnace status and automatic hydroponic water refresh";
        return true;
    }
}
