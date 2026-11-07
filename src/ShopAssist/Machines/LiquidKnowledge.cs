using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ShopAssist
{
    // Recommendations are only enabled for the inspected game assembly and
    // independent unmodified processing paths. A foreign patch degrades the
    // affected recommendation instead of changing the measurement display.
    internal static class LiquidKnowledge
    {
        internal sealed class Method
        {
            internal string Name, Summary, Description, Atlas, Sprite;
        }
        private static readonly Guid PreviousBuild = new Guid("83045710-adce-4e78-8ff1-b43a9399ec0d");
        private static readonly Guid CurrentBuild = new Guid("efd8b252-19aa-489b-96d3-e172c92c0b75");
        private static readonly Dictionary<string, (int particle, double floor, int wear, int removed)> FilterData =
            new Dictionary<string, (int, double, int, int)>(StringComparer.Ordinal)
            {
                ["physical_contaminant"] = (100, .0016, 2, 1500),
                ["organic_waste"] = (40, .0016, 1, 1000),
                ["microplastic"] = (25, .002, 2, 1500),
                ["mineral"] = (30, .0016, 1, 2000),
                ["dream_cap_mush"] = (200, 0, 2, 1000),
                ["plant_matter"] = (200, 0, 2, 1000),
                ["coffee_mush"] = (200, 0, 2, 1000),
                ["nightmare_cap_mush"] = (200, 0, 2, 1000),
                ["debris"] = (200, 0, 2, 1000)
            };
        private static readonly HashSet<string> BuiltIn = new HashSet<string>(StringComparer.Ordinal)
        {
            "water", "heavy_metal", "organic_waste", "microplastic", "microbe", "chemical_contaminant", "physical_contaminant", "mineral",
            "dream_cap_mush", "dream_cap_extract", "plant_matter", "alcohol", "caffeine", "protein", "fat", "starch", "sugar",
            "coffee_mush", "contraceptive", "zyanide", "nightmare_extract", "nightmare_cap_mush", "chlorine", "synaprest", "fanta",
            "methylol", "reducing_agent", "acid", "base", "stabilizing_agent", "debris", "lipobond_agent", "livestock_steroid",
            "livestock_immunity", "wine_extract", "nomore"
        };

        internal static bool VerifiedGameBuild(Guid module) => module == PreviousBuild || module == CurrentBuild;
        private static bool Supported => VerifiedGameBuild(typeof(GameItem).Module.ModuleVersionId);
        private const BindingFlags Methods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static readonly MethodInfo[] WaterMethods = typeof(WaterHelper).GetMethods(Methods);
        private static readonly MethodInfo[] LiquidMethods = typeof(Liquid).GetMethods(Methods);
        private static readonly MethodInfo[] DurabilityMethods = typeof(DurabilityHelper).GetMethods(Methods);
        private static readonly MethodInfo[] ItemMethods = typeof(AmenitiesItemDirectory).GetMethods(Methods);
        private static readonly MethodInfo[] ChemicalMethods = typeof(ChemicalHelper).GetMethods(Methods);
        private static readonly MethodInfo[] PurifierMethods = typeof(MachinePurifier).GetMethods(Methods);
        private static readonly MethodInfo[] EvaporatorMethods = typeof(MachineEvaporator).GetMethods(Methods);
        private static readonly MethodInfo[] MachineryMethods = typeof(MachineryHelper).GetMethods(Methods);
        private static readonly MethodInfo[] PowerMethods = typeof(PowerHelper).GetMethods(Methods);
        private static bool NoForeignPatches(MethodBase method)
        {
            if (method == null) return false;
            var patches = Harmony.GetPatchInfo(method);
            if (patches == null) return true;
            return patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers).Concat(patches.Finalizers)
                .All(patch => patch.PatchMethod?.DeclaringType?.Assembly == typeof(LiquidKnowledge).Assembly);
        }
        private static bool Safe(MethodInfo[] candidates, params string[] names)
        {
            if (!Supported) return false;
            foreach (string name in names)
            {
                var methods = candidates.Where(m => m.Name == name).ToArray();
                if (methods.Length == 0 || methods.Any(m => !NoForeignPatches(m))) return false;
            }
            return true;
        }
        private static bool SafeNested(Type type, ref List<MethodBase> patched)
        {
            if (!Supported) return false;
            // Harmony already exposes the set of patched methods; inspecting
            // that set avoids reflecting over arbitrary dynamically chosen types.
            if (patched == null) patched = Harmony.GetAllPatchedMethods().ToList();
            foreach (MethodBase method in patched)
                if (method.DeclaringType?.DeclaringType == type && !NoForeignPatches(method)) return false;
            return true;
        }
        // Factory and reaction lambdas contain part of the native processing;
        // checking only their enclosing methods would miss patches to those bodies.
        private static bool SafeGenerated(Type type, ref List<MethodBase> patched, params string[] names)
        {
            if (!Supported) return false;
            if (patched == null) patched = Harmony.GetAllPatchedMethods().ToList();
            foreach (MethodBase method in patched)
            {
                Type owner = method.DeclaringType;
                while (owner?.DeclaringType != null && owner.DeclaringType != type) owner = owner.DeclaringType;
                if (owner?.DeclaringType == type && names.Any(name => method.Name.StartsWith("<" + name + ">", StringComparison.Ordinal)) &&
                    !NoForeignPatches(method)) return false;
            }
            return true;
        }
        private static Method Card(string nameKey, string textKey, string atlas, string sprite, params object[] args) =>
            new Method { Name = LocHelper.GetLocalizedItem(nameKey), Summary = ShopSettings.Text(textKey + ".summary"),
                Description = args.Length == 0 ? ShopSettings.Text(textKey) : ShopSettings.Format(textKey, args), Atlas = atlas, Sprite = sprite };

        internal static List<Method> For(Liquid liquid, GameItem sample)
        {
            var result = new List<Method>();
            if (liquid == null || !BuiltIn.Contains(liquid.identifier) || !Supported || sample == null ||
                !sample.IsTag("LIQUID_CONTAINER_TAG")) return result;
            string id = liquid.identifier;
            List<MethodBase> patched = null;
            bool filter = Safe(WaterMethods, "HandleFilter", "TransferLiquid", "GetFilterItem", "GetLiquidParts", "SplitProportionally") &&
                SafeGenerated(typeof(WaterHelper), ref patched, "HandleFilter", "TransferLiquid") &&
                Safe(LiquidMethods, "InitLiquid") &&
                Safe(DurabilityMethods, "GetRealDurability", "ChangeDurability");
            if (filter && FilterData.TryGetValue(id, out var expected) && liquid.particleSize == expected.particle &&
                Math.Abs(liquid.filterMinimumPurity - expected.floor) < .0000001 && liquid.filterDurabilityUsage == expected.wear &&
                liquid.filterVolumeRemoved == expected.removed)
            {
                if (Safe(ItemMethods, "WaterFilter") && SafeGenerated(typeof(AmenitiesItemDirectory), ref patched, "WaterFilter"))
                    result.Add(Card("item_water_filter_name", "f19.filter", "Items/items_amenities", "water_filter",
                        22, expected.particle, expected.floor * 100, expected.removed / 1000.0, expected.wear));
                if (Safe(ItemMethods, "WaterFilterAdvanced") && SafeGenerated(typeof(AmenitiesItemDirectory), ref patched, "WaterFilterAdvanced"))
                    result.Add(Card("item_water_filter_adv_name", "f19.filter", "Items/items_amenities", "water_filter_adv",
                        22, expected.particle, expected.floor * 100, expected.removed / 1000.0, expected.wear));
                if (Safe(ItemMethods, "HydroreedFilter") && SafeGenerated(typeof(AmenitiesItemDirectory), ref patched, "HydroreedFilter"))
                    result.Add(Card("item_hydroreed_filter_name", "f19.filter", "Items/items_hydroponic_plant", "hydroreed_filter",
                        22, expected.particle, expected.floor * 100, expected.removed / 1000.0, expected.wear));
                if (expected.particle >= 150 && Safe(ItemMethods, "KottonFabric") && SafeGenerated(typeof(AmenitiesItemDirectory), ref patched, "KottonFabric"))
                    result.Add(Card("item_kotton_fabric_name", "f19.filter", "Items/items_armor", "kotton_fabric",
                        150, expected.particle, expected.floor * 100, expected.removed / 1000.0, expected.wear));
            }
            if (id == "microbe" && Safe(ItemMethods, "UVFilter") && SafeGenerated(typeof(AmenitiesItemDirectory), ref patched, "UVFilter"))
            {
                if (Safe(WaterMethods, "OnUVUsed", "IsEmpty") && SafeGenerated(typeof(WaterHelper), ref patched, "OnUVUsed"))
                    result.Add(Card("item_uv_filter_name", "f19.uv_direct", "Items/items_amenities", "uv"));
            }
            var tick = sample.onOneSecondItemFunc;
            bool nativeTick = tick != null && tick.GetInvocationList().Length == 1 &&
                tick.Method.DeclaringType?.DeclaringType == typeof(ChemicalHelper) && tick.Method.Name == "<.cctor>b__62_0" &&
                tick.Method.Module == typeof(GameItem).Module && NoForeignPatches(tick.Method);
            if ((id == "microbe" || id == "chemical_contaminant") && nativeTick &&
                Safe(ChemicalMethods, "InitContainerTempFunction", "HandleTemperatureReaction", "HandleChlorine", "BlendInto", "DissolveInto", "InitChemItem", "SetChemContentML") &&
                SafeGenerated(typeof(ChemicalHelper), ref patched, "InitChemItem", "SetChemContentML", "DissolveInto", "HandleChlorine") &&
                Safe(WaterMethods, "IsEmpty", "GetFreeCapacity", "GetTotalVolume", "Remove") &&
                Safe(ItemMethods, "WaterPurificationTablet"))
                result.Add(Card("item_water_tablet_name", "f19.tablet", "Items/items_amenities", "water_tablet"));
            if (id != "water" && liquid.canBeRemovedByPurifier && sample.GetTagReadonly(Liquid.GetCurrentPartFromId("water")).GetInt() > 0 &&
                Safe(PurifierMethods, "WaterPurifier", "PurifyContainer", "RemoveLargestFirst", "GetMaxPurityCap", "GetPurityMult", "GetDirtBonus") &&
                Safe(WaterMethods, "IsEmpty", "GetTotalVolume", "GetWaterPurity") && Safe(LiquidMethods, "InitLiquid") &&
                Safe(MachineryMethods, "GetMachinePowerUsage", "GetCurrentPerformanceBonus", "GetCurrentQualityBonus") &&
                Safe(PowerMethods, "CanDrawPowerSource", "DrawPowerSource") &&
                SafeNested(typeof(MachinePurifier), ref patched))
                result.Add(Card("item_water_purifier_name", "f19.purifier", "Items/items_tool2", "purifier"));
            bool volatilePresent = Liquid.Liquids.Any(part => part != null && part.isEvaporative &&
                (part.identifier == "water" || part.identifier == "alcohol" || part.identifier == "lipobond_agent") &&
                sample.GetTagReadonly(part.currentPart).GetInt() > 0);
            if (volatilePresent && Safe(EvaporatorMethods, "Evaporator", "CanEvaporateContainer") && SafeNested(typeof(MachineEvaporator), ref patched) &&
                Safe(LiquidMethods, "InitLiquid") && Safe(WaterMethods, "AddLiquid", "IsEmpty") &&
                SafeGenerated(typeof(WaterHelper), ref patched, "AddLiquid") &&
                Safe(MachineryMethods, "GetMachinePowerUsage", "GetCurrentPerformanceBonus", "GetCurrentQualityBonus") &&
                Safe(PowerMethods, "DrawPowerSource") &&
                liquid.isEvaporative == (id == "water" || id == "alcohol" || id == "lipobond_agent"))
                result.Add(Card("item_evaporator_name", liquid.isEvaporative ? "f19.evaporator_water" : "f19.evaporator_residue",
                    "Items/items_tool2", "evaporator"));
            return result;
        }
    }
}
