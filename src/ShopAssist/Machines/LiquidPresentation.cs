using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace ShopAssist
{
    // Liquid.Liquids is initialized once in the game and keeps its original locale.
    // Read fresh names without replacing that registry or touching liquid state.
    internal sealed class LiquidDisplayNames
    {
        private static readonly MethodInfo Initializer = AccessTools.Method(typeof(Liquid), nameof(Liquid.InitLiquid));
        private readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Action<string> log;
        private Locale locale;
        private AsyncOperationHandle<LocalizationSettings> initializationHandle;
        private AsyncOperationHandle<StringTable> table;
        private bool requested, settled;

        internal LiquidDisplayNames(Action<string> log) { this.log = log; }

        internal bool Ready()
        {
            var selected = LocalizationSettings.SelectedLocaleAsync;
            if (!selected.IsValid() || !selected.IsDone || selected.Status != AsyncOperationStatus.Succeeded || selected.Result == null) return false;
            Locale current = selected.Result;
            if (!ReferenceEquals(locale, current))
            {
                locale = current; initializationHandle = default; table = default; requested = settled = false; names.Clear();
            }
            var initialization = LocalizationSettings.InitializationOperation;
            if (!initialization.IsValid()) return false;
            if (!initializationHandle.Equals(initialization))
            {
                initializationHandle = initialization; table = default; requested = settled = false; names.Clear();
            }
            if (!initialization.IsDone) return false;
            if (initialization.Status != AsyncOperationStatus.Succeeded)
            { if (!settled) Fail("native localization initialization failed", initialization.OperationException); return true; }
            if (settled) return true;
            // A foreign patch can make the initializer have side effects; use native names instead.
            if (!MaintenanceSwap.NoForeignPatches(Initializer)) { settled = true; return true; }
            try
            {
                if (!requested)
                {
                    // This is the database's shared handle. Dropping our reference must not
                    // release it: the game and other mods also use the same cached table.
                    table = LocalizationSettings.StringDatabase.GetTableAsync("Mechanic", current);
                    requested = true;
                }
                if (!table.IsValid()) { Fail("native liquid name table became invalid", null); return true; }
                if (!table.IsDone) return false;
                if (table.Status != AsyncOperationStatus.Succeeded || table.Result == null)
                { Fail("native liquid name table could not load", table.OperationException); return true; }
                var refreshed = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (Liquid value in Liquid.InitLiquid())
                    if (value != null && !string.IsNullOrEmpty(value.identifier) && !string.IsNullOrEmpty(value.displayName) &&
                        !refreshed.ContainsKey(value.identifier)) refreshed.Add(value.identifier, value.displayName);
                var latest = LocalizationSettings.SelectedLocaleAsync;
                if (!latest.IsValid() || !latest.IsDone || latest.Status != AsyncOperationStatus.Succeeded || !ReferenceEquals(latest.Result, current))
                { locale = null; table = default; requested = false; return false; }
                names.Clear(); foreach (var entry in refreshed) names.Add(entry.Key, entry.Value);
                settled = true;
                return true;
            }
            catch (Exception error) { Fail("could not refresh localized liquid names", error); return true; }
        }

        private void Fail(string message, Exception error)
        {
            names.Clear(); settled = true;
            log?.Invoke("F19 " + message + (error == null ? "." : ": " + error));
        }

        internal string Get(Liquid liquid)
        {
            if (liquid == null) return "";
            return liquid.identifier != null && names.TryGetValue(liquid.identifier, out string name) ? name : liquid.displayName ?? "";
        }

        internal void Reset() { locale = null; initializationHandle = default; table = default; requested = settled = false; names.Clear(); }
    }

    // The native locale fonts do not all contain star glyphs. Draw the same
    // small filled/outlined mark without depending on any font atlas.
    internal sealed class LiquidPinIcon : MaskableGraphic
    {
        private bool pinned;
        internal bool Pinned
        {
            get => pinned;
            set { if (pinned != value) { pinned = value; SetVerticesDirty(); } }
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = GetPixelAdjustedRect();
            float radius = Mathf.Min(rect.width, rect.height) * .39f;
            if (radius <= 0) return;
            Vector2 center = rect.center;
            Color32 tint = color;
            if (pinned) mesh.AddVert(center, tint, Vector2.zero);
            for (int i = 0; i < 10; i++)
            {
                float angle = Mathf.PI * .5f + i * Mathf.PI / 5;
                float length = radius * (i % 2 == 0 ? 1 : .43f);
                Vector2 point = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * length;
                mesh.AddVert(point, tint, Vector2.zero);
                if (!pinned) mesh.AddVert(center + (point - center) * .68f, tint, Vector2.zero);
            }
            for (int i = 0; i < 10; i++)
            {
                int next = (i + 1) % 10;
                if (pinned) mesh.AddTriangle(0, 1 + i, 1 + next);
                else
                {
                    int a = i * 2, b = next * 2;
                    mesh.AddTriangle(a, a + 1, b); mesh.AddTriangle(b, a + 1, b + 1);
                }
            }
        }
    }
}
