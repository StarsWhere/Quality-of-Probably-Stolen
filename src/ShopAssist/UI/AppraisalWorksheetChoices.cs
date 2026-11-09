using System;
using System.Collections.Generic;

namespace ShopAssist
{
    // Public handbook choices, never an item state reader. Index zero in every
    // group means the player has not identified that observation yet.
    internal static class AppraisalWorksheetChoices
    {
        internal sealed class Group
        {
            internal readonly string Key;
            internal readonly string[] Options;
            internal Group(string key, params string[] options) { Key = key; Options = options; }
        }
        internal sealed class Catalog
        {
            internal readonly string Id;
            internal readonly int Book, Basis;
            internal readonly Group[] Groups;
            internal Catalog(string id, int book, int basis, params Group[] groups)
            { Id = id; Book = book; Basis = basis; Groups = groups; }
        }
        private static Group G(string key, params string[] choices) => new Group("f23." + key, choices);
        internal static readonly Catalog Hand = new Catalog("hand", 6, 100,
            G("material", "aluminum", "carbon", "gold"), G("interface", "emg", "emg_plus", "neural", "unspecified"),
            G("condition", "undamaged", "wear", "damage"), G("armored", "absent", "present"), G("bladed", "absent", "present"));
        internal static readonly Catalog Ribwich = new Catalog("ribwich", 4, 150,
            G("package", "sealed_genuine", "opened", "counterfeit"), G("meat", "normal", "double"),
            G("edition", "normal", "deluxe"), G("production_edge", "no", "yes"),
            G("first_run", "no", "yes"), G("facility", "normal", "high", "low"));
        internal static readonly Catalog Plasma = new Catalog("plasma_fuel", 0, 100,
            G("wear", "none", "wear", "heavy_wear"), G("crack", "absent", "present"),
            G("uncovered", "absent", "present"), G("reinforced", "absent", "present"),
            G("fuel", "standard", "poor", "high"), G("seal", "none", "ll", "zhonghua", "republic", "ignition"));
        internal static Catalog For(string identifier) => identifier == Hand.Id ? Hand : identifier == Ribwich.Id ? Ribwich :
            identifier == Plasma.Id ? Plasma : null;
        internal static bool TryTotal(Catalog catalog, IReadOnlyList<int> selected, out int total)
        {
            total = 0;
            // The shipped fuel handbook and current valuation rules disagree.
            // Keep observations and the native guide available without turning
            // unpublished valuation changes into a supposedly known estimate.
            if (ReferenceEquals(catalog, Plasma)) return false;
            if ((!ReferenceEquals(catalog, Hand) && !ReferenceEquals(catalog, Ribwich) && !ReferenceEquals(catalog, Plasma)) ||
                selected == null || selected.Count != catalog.Groups.Length) return false;
            if (ReferenceEquals(catalog, Ribwich) && (selected[0] == 2 || selected[0] == 3))
            { total = 40; return true; }
            for (int i = 0; i < selected.Count; i++)
                if (selected[i] <= 0 || selected[i] > catalog.Groups[i].Options.Length) return false;
            int a = selected[0], b = selected[1], c = selected[2], d = selected[3], e = selected[4];
            if (ReferenceEquals(catalog, Hand))
            {
                total = catalog.Basis + (a == 2 ? 50 : a == 3 ? 200 : 0) +
                    (b == 2 ? 45 : b == 3 ? 120 : b == 4 ? -60 : 0) + (c == 2 ? -35 : c == 3 ? -85 : 0) +
                    (d == 2 ? 50 : 0) + (e == 2 ? 80 : 0);
                if (a == 2 && (b == 2 || b == 3) && c == 1 && d == 2 && e == 2) total += 100;
                if (a == 3 && b == 3 && c == 1) total += 200;
                total = Math.Max(50, total); return true;
            }
            if (ReferenceEquals(catalog, Ribwich))
            {
                if (a != 1) { total = 40; return true; }
                int facility = selected[5];
                total = Math.Max(40, catalog.Basis + (b == 2 ? 800 : 0) + (c == 2 ? 300 : 0) +
                    (d == 2 ? 50 : 0) + (e == 2 ? 80 : 0) + (facility == 2 ? 50 : facility == 3 ? -50 : 0));
                return true;
            }
            return false;
        }
    }
}
