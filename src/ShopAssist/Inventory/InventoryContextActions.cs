using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    // Adds independent rows to the game's existing item and grid context menus.
    internal static class InventoryContextActions
    {
        private sealed class Entry
        {
            internal string Id;
            internal bool Grid;
            internal Func<GameItem, string> ItemLabel;
            internal Func<GameItem, ActionAvailability> ItemAvailable;
            internal Action<GameItem> ItemAction;
            internal Func<GameGridInventory, string> GridLabel;
            internal Func<GameGridInventory, ActionAvailability> GridAvailable;
            internal Action<GameGridInventory> GridAction;
            internal RichTextElement Button;
            internal PixelWindow Parent;
        }

        private static readonly List<Entry> entries = new List<Entry>();

        internal static void RegisterItem(string id, Func<GameItem, string> label, Func<GameItem, bool> canOpen, Action<GameItem> open)
        {
            if (canOpen == null) throw new ArgumentNullException(nameof(canOpen));
            RegisterItem(id, label, item => Legacy(id, () => canOpen(item)), open);
        }
        internal static void RegisterItem(string id, Func<GameItem, string> label, Func<GameItem, ActionAvailability> availability, Action<GameItem> open) =>
            Add(new Entry { Id = id, ItemLabel = label, ItemAvailable = availability, ItemAction = open });
        internal static void RegisterGrid(string id, Func<GameGridInventory, string> label,
            Func<GameGridInventory, bool> canOpen, Action<GameGridInventory> open)
        {
            if (canOpen == null) throw new ArgumentNullException(nameof(canOpen));
            RegisterGrid(id, label, grid => Legacy(id, () => canOpen(grid)), open);
        }
        internal static void RegisterGrid(string id, Func<GameGridInventory, string> label,
            Func<GameGridInventory, ActionAvailability> availability, Action<GameGridInventory> open) =>
            Add(new Entry { Id = id, Grid = true, GridLabel = label, GridAvailable = availability, GridAction = open });
        private static ActionAvailability Legacy(string id, Func<bool> available)
        {
            var feature = ShopSettings.ContextFeatureAvailability(id);
            if (!feature.Allowed) return feature;
            return available() ? ActionAvailability.Ready :
                ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "availability.unknown");
        }
        private static void Add(Entry entry)
        {
            if (string.IsNullOrEmpty(entry.Id) || entries.Exists(other => other.Id == entry.Id) ||
                (entry.Grid ? entry.GridLabel == null || entry.GridAvailable == null || entry.GridAction == null :
                    entry.ItemLabel == null || entry.ItemAvailable == null || entry.ItemAction == null))
                throw new ArgumentException("Invalid or duplicate inventory menu action.");
            entries.Add(entry);
        }
        internal static void Hide(string id)
        {
            Entry entry = entries.Find(candidate => candidate.Id == id);
            if (entry?.Button?.handler != null) entry.Button.handler.gameObject.SetActive(false);
        }
        internal static void Unregister(string id)
        {
            Entry entry = entries.Find(candidate => candidate.Id == id);
            if (entry == null) return;
            Destroy(entry);
            entries.Remove(entry);
        }

        private static void Destroy(Entry entry)
        {
            if (entry.Button?.handler != null) UnityEngine.Object.Destroy(entry.Button.handler.gameObject);
            entry.Button = null;
            entry.Parent = null;
        }
        private static string NodeName(string id) => id == "f05.open" ? "ShopAssist_SortMenu" :
            id == "f06.hold" ? "ShopAssist_HoldMenu" : id == "f07.open" ? "ShopAssist_RouteMenu" : "ShopAssist_" + id.Replace('.', '_');

        private static void Render(bool gridMenu, PixelWindow menu, GameItem item, GameGridInventory grid, bool opened)
        {
            foreach (Entry entry in entries)
                if (entry.Grid == gridMenu && entry.Button?.handler != null) entry.Button.handler.gameObject.SetActive(false);
            if (!opened || menu?.handler == null || !menu.IsVisible()) return;
            float y = -menu.heightPixels;
            float right = menu.widthPixels;
            foreach (Entry entry in entries.ToArray())
            {
                if (entry.Grid != gridMenu) continue;
                try
                {
                    if (!(gridMenu ? entry.GridAvailable(grid) : entry.ItemAvailable(item)).Allowed) continue;
                    if (entry.Button?.handler == null || !ReferenceEquals(entry.Parent, menu))
                    {
                        Destroy(entry);
                        entry.Parent = menu;
                        entry.Button = new RichTextElement();
                        entry.Button.handler.SetParent(menu.handler);
                        entry.Button.handler.gameObject.name = NodeName(entry.Id);
                    }
                    string label = gridMenu ? entry.GridLabel(grid) : entry.ItemLabel(item);
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    entry.Button.SetText(label, 10000, RenderHandler.ColorPalette.LightGray);
                    entry.Button.handler.rectTransform.anchoredPosition = new Vector2(2f, y);
                    entry.Button.handler.gameObject.SetActive(true);
                    foreach (Transform child in entry.Button.handler.GetComponentsInChildren<Transform>(true)) child.gameObject.tag = "ContextMenuNode";
                    right = Mathf.Max(right, 2f + entry.Button.widthPixels);
                    y -= entry.Button.heightPixels;
                }
                catch (Exception error) { Debug.LogError("[ShopAssist] Context action " + entry.Id + " failed: " + error); }
            }
            if (y < -menu.heightPixels) FitOnScreen(menu, -y, right);
        }

        private static void FitOnScreen(PixelWindow menu, float height, float width)
        {
            RectTransform rect = menu.handler.rectTransform;
            if (rect == null || rect.parent == null) return;
            Canvas canvas = menu.handler.GetComponentInParent<Canvas>(true);
            canvas = canvas != null ? canvas.rootCanvas : RenderHandler.current?.windowUICanvas;
            if (canvas == null) return;
            // Match PixelWindow.FixBounds, but include the independent rows below
            // the native menu. Do not resize its grid: peers also own its layout.
            Vector2 screenCorner = canvas.pixelRect.max;
            screenCorner.y = canvas.pixelRect.min.y;
            Vector2 bounds = rect.parent.InverseTransformPoint(screenCorner);
            bounds.y = -bounds.y;
            Vector2 position = rect.anchoredPosition;
            position.x = Mathf.Clamp(position.x, 0f, Mathf.Max(0f, bounds.x - width));
            position.y = -Mathf.Clamp(-position.y, 0f, Mathf.Max(0f, bounds.y - height));
            rect.anchoredPosition = position;
        }

        private static bool Trigger(ItemContextHandler handler, RichTextElement button, GameItem item, GameGridInventory grid)
        {
            if (button == null) return true;
            foreach (Entry entry in entries.ToArray())
            {
                if (!ReferenceEquals(entry.Button, button)) continue;
                try
                {
                    ActionAvailability availability = entry.Grid ? entry.GridAvailable(grid) : entry.ItemAvailable(item);
                    handler.Stop();
                    if (availability.Allowed)
                    {
                        if (entry.Grid) entry.GridAction(grid); else entry.ItemAction(item);
                    }
                    else ShopNotice.Show(availability.Message);
                }
                catch (Exception error) { Debug.LogError("[ShopAssist] Context action " + entry.Id + " failed: " + error); }
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ItemContextHandler), "TryOpenSortMenu")]
        private static class GridMenuPatch
        {
            private static void Postfix(bool __result, PixelWindow ___sortWindow, GameGridInventory ___currentInventory) =>
                Render(true, ___sortWindow, null, ___currentInventory, __result);
        }
        [HarmonyPatch(typeof(ItemContextHandler), "OnEventPress")]
        private static class ItemMenuPatch
        {
            private static void Postfix(PixelWindow ___contextWindow, GameItem ___currentItem) =>
                Render(false, ___contextWindow, ___currentItem, null, ___currentItem != null);
        }
        [HarmonyPatch(typeof(ItemContextHandler), "TriggerButton")]
        private static class ActionPatch
        {
            private static bool Prefix(ItemContextHandler __instance, RichTextElement button, GameGridInventory ___currentInventory, GameItem ___currentItem) =>
                Trigger(__instance, button, ___currentItem, ___currentInventory);
        }
    }
}
