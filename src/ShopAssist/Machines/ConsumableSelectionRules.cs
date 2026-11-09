using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopAssist
{
    internal enum BatteryReplacementCondition { BestEnergy, BestPercentage, CannotRun }
    internal enum FilterReplacementCondition { Exhausted, BestRemaining }

    internal static class ConsumableSelectionRules
    {
        internal static IEnumerable<T> OrderBatteryMachines<T>(IEnumerable<T> machines, Func<T, int> energy,
            Func<T, int> required, Random random)
        {
            var shuffled = machines.ToArray();
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                T item = shuffled[i]; shuffled[i] = shuffled[j]; shuffled[j] = item;
            }
            return shuffled.OrderByDescending(machine => (long)energy(machine) < 2L * required(machine))
                .ThenBy(energy).ToArray();
        }

        internal static bool BatteryStateValid(int energy, int capacity, int required) =>
            required > 0 && energy >= 0 && capacity > 0 && energy <= capacity;

        internal static bool BatteryImproves(int condition, int installedEnergy, int installedCapacity,
            int candidateEnergy, int candidateCapacity, int required)
        {
            if (condition < 0 || condition > 2 || !BatteryStateValid(installedEnergy, installedCapacity, required) ||
                candidateCapacity <= 0 || candidateEnergy < required || candidateEnergy > candidateCapacity) return false;
            if (condition == (int)BatteryReplacementCondition.CannotRun)
                return installedEnergy < required;
            return condition == (int)BatteryReplacementCondition.BestEnergy ? candidateEnergy > installedEnergy :
                (long)candidateEnergy * installedCapacity > (long)installedEnergy * candidateCapacity;
        }

        internal static double BatteryGain(int condition, int installedEnergy, int installedCapacity, int energy, int capacity) =>
            condition == (int)BatteryReplacementCondition.BestPercentage ?
                installedCapacity > 0 && capacity > 0 ? (double)energy / capacity - (double)installedEnergy / installedCapacity : 0 :
                (double)energy - installedEnergy;

        internal static bool FilterDue(int condition, int durability) => durability >= 0 &&
            (condition == (int)FilterReplacementCondition.Exhausted ? durability == 0 :
                condition == (int)FilterReplacementCondition.BestRemaining);

        internal static bool FilterImproves(int installedDurability, int candidateDurability) =>
            installedDurability >= 0 && candidateDurability > installedDurability;

        internal static bool FilterNeedsWork(int condition, int durability, bool sourceAvailable, bool hasBetter) =>
            FilterDue(condition, durability) && (condition == (int)FilterReplacementCondition.Exhausted ||
                durability == 0 || !sourceAvailable || hasBetter);

        internal static IOrderedEnumerable<T> OrderFilters<T>(IEnumerable<T> filters, Func<T, int> durability,
            Func<T, bool> sameType) => filters.OrderByDescending(durability).ThenByDescending(sameType);
    }
}
