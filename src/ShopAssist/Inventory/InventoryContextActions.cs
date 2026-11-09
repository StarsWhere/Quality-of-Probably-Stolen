using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    // The original menu grid remains the window's child: other mods can own that object.
    internal static class InventoryContextActions
    {
        private sealed class Entry
        {
            internal string Id;
            internal bool Grid;
            internal Func<GameItem, string> ItemLabel;
            internal Func<GameItem, bool> ItemApplies;
            internal Func<GameItem, ActionAvailability> ItemAvailable;
            internal Action<GameItem> ItemAction;
            internal Func<GameGridInventory, string> GridLabel;
            internal Func<GameGridInventory, bool> GridApplies;
            internal Func<GameGridInventory, ActionAvailability> GridAvailable;
            internal Action<GameGridInventory> GridAction;
            internal RichTextElement Button;
            internal PixelWindow Parent;
            internal string DisplayedLabel;
        }

        private sealed class Layout
        {
            internal PixelWindow Window;
            internal GridPixelElement Rows;
        }

        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly Dictionary<PixelWindow, Layout> layouts = new Dictionary<PixelWindow, Layout>();
        private static readonly FieldInfo Background = AccessTools.Field(typeof(PixelWindow), "background");
        private static readonly FieldInfo Border = AccessTools.Field(typeof(PixelWindow), "outsideBorder");
        private static readonly MethodInfo ClearItemHover = AccessTools.Method(typeof(ItemContextHandler), "UpdateSelectButtonColor");
        private static readonly MethodInfo ClearGridHover = AccessTools.Method(typeof(ItemContextHandler), "UpdateSortSelectButtonColor");
        private static readonly MethodInfo OpenGridMenu = AccessTools.Method(typeof(ItemContextHandler), "TryOpenSortMenu");
        private static Func<bool> keepOpen;
        private static Func<GameGridInventory, bool> validSortTarget;
        private static PixelWindow persistentWindow;
        private static GameGridInventory persistentGrid;
        private static GameItem persistentItem;
        private static RichTextElement hovered;
        private static int consumedFrame = -1;
        private static int escapeFrame = -1;

        internal static void ConfigureSortMenu(Func<bool> keep, Func<GameGridInventory, bool> valid)
        { keepOpen = keep; validSortTarget = valid; }

        internal static void ClearSortMenu()
        { ForgetSession(); keepOpen = null; validSortTarget = null; }

        internal static void DismissSortMenu()
        {
            if (persistentWindow == null) return;
            ItemContextHandler.current?.Stop();
            ForgetSession();
        }

        internal static void RegisterItem(string id, Func<GameItem, string> label, Func<GameItem, bool> canOpen, Action<GameItem> open)
        {
            if (canOpen == null) throw new ArgumentNullException(nameof(canOpen));
            RegisterItem(id, label, canOpen, item => ContextAvailability(id, () => canOpen(item)), open);
        }
        internal static void RegisterItem(string id, Func<GameItem, string> label, Func<GameItem, bool> applies,
            Func<GameItem, ActionAvailability> availability, Action<GameItem> open) =>
            Add(new Entry { Id = id, ItemLabel = label, ItemApplies = applies, ItemAvailable = availability, ItemAction = open });
        internal static void RegisterGrid(string id, Func<GameGridInventory, string> label,
            Func<GameGridInventory, bool> canOpen, Action<GameGridInventory> open)
        {
            if (canOpen == null) throw new ArgumentNullException(nameof(canOpen));
            RegisterGrid(id, label, canOpen, grid => ContextAvailability(id, () => canOpen(grid)), open);
        }
        internal static void RegisterGrid(string id, Func<GameGridInventory, string> label,
            Func<GameGridInventory, bool> applies, Func<GameGridInventory, ActionAvailability> availability, Action<GameGridInventory> open) =>
            Add(new Entry { Id = id, Grid = true, GridLabel = label, GridApplies = applies, GridAvailable = availability, GridAction = open });
        private static ActionAvailability ContextAvailability(string id, Func<bool> available)
        {
            var feature = ShopSettings.ContextFeatureAvailability(id);
            if (!feature.Allowed) return feature;
            return available() ? ActionAvailability.Ready :
                ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "availability.unknown");
        }
        private static void Add(Entry entry)
        {
            if (string.IsNullOrEmpty(entry.Id) || entries.Exists(other => other.Id == entry.Id) ||
                (entry.Grid ? entry.GridLabel == null || entry.GridApplies == null || entry.GridAvailable == null || entry.GridAction == null :
                    entry.ItemLabel == null || entry.ItemApplies == null || entry.ItemAvailable == null || entry.ItemAction == null))
                throw new ArgumentException("Invalid or duplicate inventory menu action.");
            entries.Add(entry);
        }
        internal static ActionAvailability EvaluateGrid(string id, GameGridInventory grid)
        {
            Entry entry = entries.Find(candidate => candidate.Grid && candidate.Id == id);
            if (entry == null) return ActionAvailability.Blocked(ActionUnavailableReason.Unsupported, "availability.unsupported");
            try
            {
                var feature = ShopSettings.ContextFeatureAvailability(id);
                if (!feature.Allowed) return feature;
                if (!entry.GridApplies(grid)) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.object");
                return entry.GridAvailable(grid);
            }
            catch (Exception error)
            {
                Debug.LogError("[ShopAssist] Context action " + id + " availability failed: " + error);
                return ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "availability.unknown");
            }
        }
        private static bool HasGridAction(GameGridInventory grid)
        {
            if (grid == null) return false;
            foreach (Entry entry in entries.ToArray())
            {
                if (!entry.Grid) continue;
                try
                {
                    if (ShopSettings.ContextFeatureAvailability(entry.Id).Allowed && entry.GridApplies(grid)) return true;
                }
                catch (Exception error)
                { Debug.LogError("[ShopAssist] Context action " + entry.Id + " availability failed: " + error); }
            }
            return false;
        }
        internal static void RunGrid(string id, GameGridInventory grid)
        {
            Entry entry = entries.Find(candidate => candidate.Grid && candidate.Id == id);
            ActionAvailability availability = EvaluateGrid(id, grid);
            if (!availability.Allowed) { ShopNotice.Show(availability.Message); return; }
            try { entry.GridAction(grid); }
            catch (Exception error)
            { Debug.LogError("[ShopAssist] Context action " + id + " failed: " + error); ShopNotice.Show(ShopSettings.Text("availability.unknown")); }
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
            if (entries.Count == 0)
            {
                foreach (Layout layout in layouts.Values.ToArray()) DestroyLayout(layout);
                layouts.Clear();
                ForgetSession();
            }
        }
        private static void Destroy(Entry entry)
        {
            if (entry.Button?.handler != null) UnityEngine.Object.Destroy(entry.Button.handler.gameObject);
            entry.Button = null;
            entry.Parent = null;
        }
        private static void DestroyLayout(Layout layout)
        {
            if (layout?.Rows?.handler != null)
            {
                layout.Rows.handler.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(layout.Rows.handler.gameObject);
            }
            foreach (Entry entry in entries)
                if (ReferenceEquals(entry.Parent, layout?.Window)) { entry.Button = null; entry.Parent = null; }
        }
        private static string NodeName(string id) => id == "f05.open" ? "ShopAssist_SortMenu" :
            id == "f07.open" ? "ShopAssist_RouteMenu" : "ShopAssist_" + id.Replace('.', '_');

        private static void Render(bool gridMenu, PixelWindow menu, GameItem item, GameGridInventory grid, bool opened)
        {
            if (menu != null && layouts.TryGetValue(menu, out Layout previous))
            { DestroyLayout(previous); layouts.Remove(menu); }
            if (!opened || menu?.handler == null || !menu.IsVisible()) { ForgetSession(); return; }
            if (!(menu.childElement is GridPixelElement original)) return;

            var visible = new List<(Entry entry, string label)>();
            foreach (Entry entry in entries.ToArray())
            {
                if (entry.Grid != gridMenu) continue;
                try
                {
                    if (!ShopSettings.ContextFeatureAvailability(entry.Id).Allowed ||
                        !(gridMenu ? entry.GridApplies(grid) : entry.ItemApplies(item))) continue;
                    ActionAvailability availability = gridMenu ? entry.GridAvailable(grid) : entry.ItemAvailable(item);
                    string label = gridMenu ? entry.GridLabel(grid) : entry.ItemLabel(item);
                    if (!availability.Allowed && !string.IsNullOrWhiteSpace(label)) label += " · " + availability.Message;
                    if (!string.IsNullOrWhiteSpace(label)) visible.Add((entry, label));
                }
                catch (Exception error) { Debug.LogError("[ShopAssist] Context action " + entry.Id + " failed: " + error); }
            }
            if (visible.Count == 0) { ForgetSession(); return; }

            var rows = new GridPixelElement(1, visible.Count).SetSpacing(0, menu.yPadding);
            var layout = new Layout { Window = menu, Rows = rows };
            layouts[menu] = layout;
            int width = 0;
            foreach (var row in visible)
            {
                row.entry.Parent = menu;
                row.entry.DisplayedLabel = row.label;
                row.entry.Button = new RichTextElement();
                row.entry.Button.handler.gameObject.name = NodeName(row.entry.Id);
                row.entry.Button.SetText(row.label, 10000, RenderHandler.ColorPalette.LightGray);
                width = Mathf.Max(width, row.entry.Button.widthPixels);
            }
            for (int y = 0; y < original.gridHeight; y++)
                for (int x = 0; x < original.gridWidth; x++)
                    if (original.GetElement(x, y) is RichTextElement native) width = Mathf.Max(width, native.widthPixels);
            for (int y = 0; y < original.gridHeight; y++)
                for (int x = 0; x < original.gridWidth; x++)
                    if (original.GetElement(x, y) is RichTextElement native) native.SetFixedSize(width, -1);
            original.Validate();
            menu.Validate();
            for (int i = 0; i < visible.Count; i++)
            {
                Entry entry = visible[i].entry;
                entry.Button.SetFixedSize(width, -1);
                if (!rows.AttachPos(entry.Button, 0, i)) throw new InvalidOperationException("Could not attach a context menu row.");
                foreach (Transform child in entry.Button.handler.GetComponentsInChildren<Transform>(true)) child.gameObject.tag = "ContextMenuNode";
            }
            rows.handler.SetParent(menu.handler);
            rows.handler.rectTransform.anchoredPosition = new Vector2(menu.xPadding, -menu.heightPixels);
            rows.Validate();
            int fullWidth = Mathf.Max(menu.widthPixels, width + 2 * menu.xPadding);
            int fullHeight = menu.heightPixels + rows.heightPixels + menu.yPadding;
            ResizeChrome(menu, fullWidth, fullHeight);
            FitOnScreen(menu, fullHeight, fullWidth);
            if (visible.Any(row => row.entry.Id.StartsWith("f05.", StringComparison.Ordinal)))
            {
                persistentWindow = menu;
                persistentGrid = gridMenu ? grid : item?.parentInventory as GameGridInventory;
                persistentItem = gridMenu ? null : item;
            }
            else ForgetSession();
        }

        private static void ResizeChrome(PixelWindow menu, int width, int height)
        {
            if (!(Background?.GetValue(menu) is TreeNodeRender background) || !(Border?.GetValue(menu) is TreeNodeRender border))
                throw new InvalidOperationException("Native context menu chrome changed.");
            background.rectTransform.sizeDelta = new Vector2(width, height);
            border.rectTransform.sizeDelta = new Vector2(width, height);
        }
        private static void FitOnScreen(PixelWindow menu, float height, float width)
        {
            RectTransform rect = menu.handler.rectTransform;
            if (rect == null || rect.parent == null) return;
            Canvas canvas = menu.handler.GetComponentInParent<Canvas>(true);
            canvas = canvas != null ? canvas.rootCanvas : RenderHandler.current?.windowUICanvas;
            if (canvas == null) return;
            Vector2 corner = canvas.pixelRect.max;
            corner.y = canvas.pixelRect.min.y;
            Vector2 bounds = rect.parent.InverseTransformPoint(corner);
            bounds.y = -bounds.y;
            Vector2 position = rect.anchoredPosition;
            position.x = Mathf.Clamp(position.x, 0f, Mathf.Max(0f, bounds.x - width));
            position.y = -Mathf.Clamp(-position.y, 0f, Mathf.Max(0f, bounds.y - height));
            rect.anchoredPosition = position;
        }

        private static bool Persistent(ItemContextHandler handler)
        {
            if (keepOpen?.Invoke() != true || persistentWindow?.handler == null || !persistentWindow.IsVisible() ||
                persistentGrid == null || validSortTarget?.Invoke(persistentGrid) != true ||
                (persistentItem != null && persistentItem.parentInventory != persistentGrid)) return false;
            return ReferenceEquals(handler, ItemContextHandler.current);
        }
        private static bool Inside(ItemContextHandler handler)
        {
            if (!(Background?.GetValue(persistentWindow) is TreeNodeRender background)) return false;
            Canvas canvas = handler.overlayCanvas;
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(background.rectTransform, Input.mousePosition, camera);
        }
        private static void ForgetSession()
        { persistentWindow = null; persistentGrid = null; persistentItem = null; hovered = null; }
        private static void SetHover(RichTextElement button)
        {
            if (ReferenceEquals(hovered, button)) return;
            foreach (Entry entry in entries)
            {
                if (entry.Button?.handler == null || !ReferenceEquals(entry.Parent, persistentWindow) || !entry.Button.handler.gameObject.activeSelf) continue;
                entry.Button.SetText(entry.DisplayedLabel, 10000, ReferenceEquals(entry.Button, button) ? RenderHandler.ColorPalette.VeryLightGray : RenderHandler.ColorPalette.LightGray);
            }
            hovered = button;
        }

        private static bool Trigger(ItemContextHandler handler, RichTextElement button, GameItem item, GameGridInventory grid)
        {
            if (button == null) return true;
            foreach (Entry entry in entries.ToArray())
            {
                if (!ReferenceEquals(entry.Button, button)) continue;
                try
                {
                    ActionAvailability availability = ShopSettings.ContextFeatureAvailability(entry.Id);
                    if (availability.Allowed && !(entry.Grid ? entry.GridApplies(grid) : entry.ItemApplies(item)))
                        availability = ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.object");
                    if (availability.Allowed) availability = entry.Grid ? entry.GridAvailable(grid) : entry.ItemAvailable(item);
                    var selection = ShopSelection.SaveContext();
                    handler.Stop(); ForgetSession();
                    ShopSelection.RestoreContext(selection);
                    if (availability.Allowed)
                    {
                        if (entry.Grid) entry.GridAction(grid); else entry.ItemAction(item);
                    }
                    else ShopNotice.Show(availability.Message);
                }
                catch (Exception error)
                { Debug.LogError("[ShopAssist] Context action " + entry.Id + " failed: " + error); ShopNotice.Show(ShopSettings.Text("availability.unknown")); }
                finally { ShopSelection.ClearContext(); }
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
            private static bool Prefix(ItemContextHandler __instance, ISet<KeyCode> keysPressed)
            {
                if (Persistent(__instance) && (keysPressed.Contains(KeyCode.Escape) || (!Inside(__instance) &&
                    (keysPressed.Contains(__instance.key) || keysPressed.Contains(__instance.triggerKey)))))
                {
                    if (keysPressed.Contains(KeyCode.Escape)) escapeFrame = Time.frameCount;
                    __instance.Stop(); ForgetSession(); consumedFrame = Time.frameCount;
                    return false;
                }
                if (keysPressed.Contains(__instance.triggerKey) || keysPressed.Contains(__instance.sortMenuKey))
                    ShopSelection.CaptureContext();
                if (keysPressed.Contains(__instance.triggerKey) && EmporiumOption.SortMenuDisabled &&
                    ItemSelectHandler.current != null && !ItemSelectHandler.current.IsEquipped && !ItemPinHelper.BlocksClicks &&
                    ItemMouseDragHandler.current != null && !ItemMouseDragHandler.current.IsDraggingItem &&
                    RenderHandler.RaycastElement<GameItem>(Input.mousePosition) == null)
                {
                    var grid = RenderHandler.RaycastElement<GameGridInventory>(Input.mousePosition);
                    if (HasGridAction(grid)) OpenGridMenu?.Invoke(__instance, new object[] { (Vector2)Input.mousePosition });
                }
                return true;
            }
            private static void Postfix(PixelWindow ___contextWindow, PixelWindow ___sortWindow, GameItem ___currentItem)
            {
                if (___currentItem != null) Render(false, ___contextWindow, ___currentItem, null, true);
                else if (___sortWindow?.IsVisible() != true) ShopSelection.ClearContext();
            }
        }
        [HarmonyPatch(typeof(ItemContextHandler), "get_keyListeners")]
        private static class KeyPatch
        {
            private static void Postfix(ItemContextHandler __instance, ref List<KeyCode> __result)
            { if (Persistent(__instance) && !__result.Contains(KeyCode.Escape)) __result.Add(KeyCode.Escape); }
        }
        [HarmonyPatch(typeof(ItemContextHandler), "OnEventUpdate")]
        private static class UpdatePatch
        {
            private static bool Prefix(ItemContextHandler __instance, ref bool __result)
            {
                if (consumedFrame == Time.frameCount) { __result = true; return false; }
                if (persistentWindow != null && keepOpen?.Invoke() == true && !Persistent(__instance))
                { __instance.Stop(); ForgetSession(); __result = false; return false; }
                if (!Persistent(__instance)) return true;
                if (Inside(__instance)) { SetHover(__instance.FindRichTextNode(Input.mousePosition, __instance.overlayCanvas)); return true; }
                ClearItemHover?.Invoke(__instance, new object[] { null });
                ClearGridHover?.Invoke(__instance, new object[] { null });
                SetHover(null);
                __result = false;
                return false;
            }
        }
        [HarmonyPatch(typeof(ItemContextHandler), "OnEventReset")]
        private static class ResetPatch
        {
            private static void Postfix() { ForgetSession(); ShopSelection.ClearContext(); }
        }
        [HarmonyPatch(typeof(StoreUIManager), nameof(StoreUIManager.ResolveEscape))]
        private static class EscapePatch
        {
            private static bool Prefix(ref bool __result)
            {
                if (escapeFrame == Time.frameCount) { __result = true; return false; }
                ItemContextHandler handler = ItemContextHandler.current;
                if (handler == null || !Persistent(handler) || !ShopInput.Ready()) return true;
                handler.Stop(); ForgetSession();
                consumedFrame = escapeFrame = Time.frameCount;
                __result = true;
                return false;
            }
        }
        [HarmonyPatch(typeof(ItemContextHandler), "TriggerButton")]
        private static class ActionPatch
        {
            private static bool Prefix(ItemContextHandler __instance, RichTextElement button, GameGridInventory ___currentInventory, GameItem ___currentItem) =>
                Trigger(__instance, button, ___currentItem, ___currentInventory);
        }
    }
}
