using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Exercises both independent build gates against the real game and foreign patches.
public sealed class GameBuildGuardScenario
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private int step;

    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Noop() { }
    private static void UnknownAdd(GameItem item, GameInventory inventory, SlotMarker slot) { }
    private static bool UnknownPassthrough(GameItem item, GameInventory inventory, SlotMarker slot, GameItem resident) => false;
    private static Type ModType(string name) => ModLoader.Mods.First(mod => mod.Manifest?.ID == "starswhere.shopassist")
        .Instance.GetType().Assembly.GetType("ShopAssist." + name, true);
    private static bool Gate(Type type, string name, params object[] args) => (bool)type.GetMethod(name, Flags).Invoke(null, args);

    private static string[] Methods(Type type, Liquid liquid, GameItem sample)
    {
        var methods = (IEnumerable)type.GetMethod("For", Flags).Invoke(null, new object[] { liquid, sample });
        return methods.Cast<object>().Select(value => (string)value.GetType().GetField("Sprite", Flags).GetValue(value)).ToArray();
    }

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Game build guard test timed out");
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (EmporiumEntry.Instance == null || PlayerStore.Instance == null) return false;
        Type inventory = ModType("InventoryAutoInsert"), liquid = ModType("LiquidKnowledge");
        var current = typeof(GameItem).Module.ModuleVersionId;
        var unknown = Guid.NewGuid();
        foreach (Type type in new[] { inventory, liquid })
        {
            Need(Gate(type, "VerifiedGameBuild", current), type.Name + " rejects the installed game build");
            Need(!Gate(type, "VerifiedGameBuild", unknown), type.Name + " accepts an unknown game build");
        }
        Need(Gate(inventory, "KnownMutationPatches"), "Installed native inventory methods are not safe");
        Need(Gate(inventory, "SafeGrid", EmporiumEntry.Instance.invElement), "Native stock grid was rejected");
        Need(Gate(inventory, "SafeGrid", EmporiumEntry.Instance.afterhourInventory), "Native outing grid was rejected");
        var pocket = EmporiumEntry.Instance.afterhourPocketSlotInvLeft;
        Need(Gate(inventory, "SafeTransferInventory", pocket, null), "Native empty outing pocket was rejected");

        Func<GameItem, GameItem, bool> foreignCallback = (first, second) => true;
        Func<Delegate, bool> allow = _ => true;
        Need(!Gate(inventory, "KnownCallbacks", foreignCallback, allow), "Unknown callback assembly was accepted");
        var stock = EmporiumEntry.Instance.invElement;
        var oldAdd = stock.onSlotAddItemFunc;
        try
        {
            stock.onSlotAddItemFunc += UnknownAdd;
            Need(!Gate(inventory, "SafeGrid", stock), "Unknown inventory notification was accepted");
        }
        finally { stock.onSlotAddItemFunc = oldAdd; }
        var oldPass = pocket.slotSearchPassthroughForItemFunc;
        try
        {
            pocket.slotSearchPassthroughForItemFunc = UnknownPassthrough;
            Need(!Gate(inventory, "SafeTransferInventory", pocket, null), "Unknown slot-search callback was accepted");
        }
        finally { pocket.slotSearchPassthroughForItemFunc = oldPass; }

        GameItem sample = MachineFeatureQa.Stock("bottled_water");
        MachineFeatureQa.Empty(sample);
        WaterHelper.AddLiquid(sample, "water", 400000);
        WaterHelper.AddLiquid(sample, "organic_waste", 10000);
        WaterHelper.AddLiquid(sample, "microbe", 10000);
        Liquid organic = Liquid.Liquids.Single(part => part.identifier == "organic_waste");
        Liquid microbe = Liquid.Liquids.Single(part => part.identifier == "microbe");
        Need(Methods(liquid, organic, sample).Contains("water_filter"), "Native filter recommendation unavailable");
        Need(Methods(liquid, organic, sample).Contains("purifier"), "Native purifier recommendation unavailable");
        Need(Methods(liquid, organic, sample).Contains("evaporator"), "Native evaporator recommendation unavailable");
        Need(Methods(liquid, microbe, sample).Contains("uv"), "Native UV recommendation unavailable");
        Need(Methods(liquid, microbe, sample).Contains("water_tablet"), "Native tablet recommendation unavailable");

        var prefix = new HarmonyMethod(typeof(GameBuildGuardScenario).GetMethod(nameof(Noop), Flags));
        var mutations = (MethodBase[])inventory.GetField("NativeMutations", Flags).GetValue(null);
        Need(mutations.All(method => method != null), "A guarded inventory method did not resolve");
        var probe = new Harmony("qa.game-build-guard.inventory");
        try
        {
            probe.Patch(AccessTools.Method(typeof(GameItem), nameof(GameItem.GetStackSlot)), prefix: prefix);
            Need(!Gate(inventory, "KnownMutationPatches"), "Foreign inventory patch was accepted");
            Need(Methods(liquid, organic, sample).Contains("water_filter"), "Inventory patch disabled the liquid guard");
        }
        finally { probe.UnpatchAll(probe.Id); }
        Need(Gate(inventory, "KnownMutationPatches"), "Inventory guard did not recover after unpatch");

        MethodInfo uvCallback = typeof(AmenitiesItemDirectory).GetNestedTypes(Flags).SelectMany(type => type.GetMethods(Flags))
            .Single(method => method.Name.StartsWith("<UVFilter>", StringComparison.Ordinal) &&
                method.ReturnType == typeof(void) && method.GetParameters().Length == 2);
        // The foreign owner deliberately resembles the mod's own Harmony owner.
        probe = new Harmony("mod.starswhere.shopassist.fake-foreign");
        try
        {
            probe.Patch(uvCallback, prefix: prefix);
            Need(!Methods(liquid, microbe, sample).Contains("uv"), "Foreign UV callback patch was accepted");
            Need(Methods(liquid, microbe, sample).Contains("water_tablet"), "UV patch disabled the independent tablet path");
            Need(Methods(liquid, organic, sample).Contains("water_filter"), "UV patch disabled the independent filter path");
            Need(Gate(inventory, "KnownMutationPatches"), "Liquid patch disabled the inventory guard");
        }
        finally { probe.UnpatchAll(probe.Id); }

        probe = new Harmony("qa.game-build-guard.chlorine");
        try
        {
            probe.Patch(AccessTools.Method(typeof(ChemicalHelper), nameof(ChemicalHelper.HandleChlorine)), prefix: prefix);
            Need(!Methods(liquid, microbe, sample).Contains("water_tablet"), "Foreign chlorine patch was accepted");
            Need(Methods(liquid, microbe, sample).Contains("uv"), "Chlorine patch disabled the independent UV path");
        }
        finally { probe.UnpatchAll(probe.Id); }
        Need(Methods(liquid, microbe, sample).Contains("water_tablet"), "Tablet guard did not recover after unpatch");
        result = "PASS verified builds, unknown callbacks and foreign patches for independent inventory and liquid guards";
        return true;
    }
}
