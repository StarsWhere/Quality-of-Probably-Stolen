using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    // Draw only on views the game has already made visible. Nothing here opens
    // an inventory, assigns an item identity, or changes a name.
    internal sealed class AreaLabels : IDisposable
    {
        private static AreaLabels current;

        private sealed class Label
        {
            internal TreeNodeRender Root;
            internal TreeNodeRender Text;
        }

        private readonly ShopSettings settings;
        private readonly Action<string> log;
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<bool> showDefaults;
        private readonly ConfigEntry<bool> background;
        private readonly ConfigEntry<bool> upper;
        private readonly ConfigEntry<int> fontSize;
        private readonly Dictionary<GameItemElement, Label> items = new Dictionary<GameItemElement, Label>();
        private readonly Dictionary<PixelWindow, Label> windows = new Dictionary<PixelWindow, Label>();
        private readonly HashSet<GameItemElement> dirtyItems = new HashSet<GameItemElement>();
        private readonly HashSet<GameItemElement> seenItems = new HashSet<GameItemElement>();
        private readonly HashSet<PixelWindow> seenWindows = new HashSet<PixelWindow>();
        private readonly List<TreeNodeRender> nodes = new List<TreeNodeRender>();
        private readonly List<GameItemElement> itemCleanup = new List<GameItemElement>();
        private readonly List<PixelWindow> windowCleanup = new List<PixelWindow>();
        private bool started;
        private bool refreshAll = true;
        private string lastError;

        internal AreaLabels(ShopSettings settings, Action<string> log)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            enabled = settings.Toggle("f02.enabled", "f02.tip", true);
            showDefaults = settings.Toggle("f02.defaults", "f02.defaults.tip");
            background = settings.Toggle("f02.background", "f02.background.tip", false);
            upper = settings.Toggle("f02.upper", "f02.upper.tip", false);
            fontSize = settings.Integer("f02.font_size", "f02.font_size.tip", 14, 6, 24);
        }

        public void Start()
        {
            if (started) return;
            if (current != null && !ReferenceEquals(current, this))
                throw new InvalidOperationException("Area labels are already active.");
            current = this;
            started = true;
            refreshAll = true;
            settings.Changed += SettingsChanged;
            ModHook.OnGameLoadedInit += ResetViews;
            ModHook.OnGameLoadedLate += Invalidate;
            ModHook.OnNewGameStarted += NewGameStarted;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        public void Tick()
        {
            if (!started) return;
            if (!enabled.Value)
            {
                if (items.Count != 0 || windows.Count != 0) ClearViews();
                return;
            }
            if (WindowsHandler.current == null || WindowsHandler.current.visibleWindows == null ||
                RenderHandler.current == null) return;

            try
            {
                if (refreshAll)
                {
                    RefreshVisibleWindows();
                    refreshAll = false;
                    dirtyItems.Clear();
                }
                else if (dirtyItems.Count != 0)
                {
                    foreach (GameItemElement item in dirtyItems) RefreshItem(item);
                    dirtyItems.Clear();
                }
                SynchronizeVisibility();
                lastError = null;
            }
            catch (Exception error)
            {
                // A later real view/settings event can retry; do not leave a
                // partially updated overlay or throw into the game's loop.
                ClearViews();
                refreshAll = false;
                dirtyItems.Clear();
                string message = error.ToString();
                if (message != lastError) log("F02 display failed: " + message);
                lastError = message;
            }
        }

        private void SettingsChanged()
        {
            if (!started) return;
            if (!enabled.Value) ClearViews();
            refreshAll = true;
        }

        private void NewGameStarted(string scenario) => Invalidate();
        private void SceneChanged(Scene oldScene, Scene newScene) => ResetViews();

        private void ResetViews()
        {
            ClearViews();
            dirtyItems.Clear();
            refreshAll = true;
        }

        internal static void Invalidate()
        {
            VisibleInventoryViews.NotifyChanged();
            if (current != null && current.started) current.refreshAll = true;
        }

        internal static void ItemChanged(GameItemElement item)
        {
            VisibleInventoryViews.NotifyItemChanged(item);
            AreaLabels owner = current;
            if (owner == null || !owner.started || !owner.enabled.Value || item == null) return;
            owner.dirtyItems.Add(item);
            PixelWindow content = item.contentWindow;
            if (content != null && content.handler != null && content.handler.gameObject.activeInHierarchy)
                owner.refreshAll = true;
        }

        private void RefreshVisibleWindows()
        {
            seenItems.Clear();
            seenWindows.Clear();
            // These are the game's currently visible views, not an inventory walk.
            foreach (PixelWindow window in WindowsHandler.current.visibleWindows)
            {
                if (!VisibleInventoryViews.Visible(window)) continue;
                seenWindows.Add(window);
                RefreshWindow(window);
                VisibleInventoryViews.Collect(window, nodes, seenItems);
            }
            foreach (GameItemElement item in seenItems) RefreshItem(item);
            itemCleanup.Clear();
            itemCleanup.AddRange(items.Keys);
            foreach (GameItemElement item in itemCleanup)
                if (!seenItems.Contains(item)) RemoveItem(item);
            windowCleanup.Clear();
            windowCleanup.AddRange(windows.Keys);
            foreach (PixelWindow window in windowCleanup)
                if (!seenWindows.Contains(window)) RemoveWindow(window);
        }

        private static bool IsArea(GameItem item)
        {
            PixelElement child = item?.contentWindow?.childElement;
            return child is GameGridInventory || child is MachineryPanelRow ||
                (child != null && item.IsTag(MachineHelper.STANDARD_MACHINE_TAG));
        }

        private string ItemName(GameItem item)
        {
            string custom = GeneralHelper.HasCustomName(item) ? GeneralHelper.GetCustomName(item) : null;
            return AreaLabelPolicy.Name(custom, showDefaults.Value ? item.GetDisplayName() : null, showDefaults.Value);
        }

        private void RefreshItem(GameItemElement item)
        {
            if (!VisibleInventoryViews.Visible(item) || !IsArea(item))
            {
                RemoveItem(item);
                return;
            }

            string name = ItemName(item);
            bool counter = item.unitCount > 1 || !string.IsNullOrEmpty(item.customText);
            Vector2 bounds = ItemOverlayLayout.Size(item);
            if (name == null || !AreaLabelPolicy.ItemLayout(bounds.x, bounds.y, counter,
                upper.Value, fontSize.Value, out float width, out float height, out float y))
            {
                RemoveItem(item);
                return;
            }

            if (!items.TryGetValue(item, out Label label) || label.Root == null || label.Text == null)
            {
                RemoveItem(item);
                label = Create(item.middle, "ShopAssist_AreaName");
                items[item] = label;
            }
            RectTransform rect = label.Root.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(bounds.x * 0.5f, y);
            rect.sizeDelta = new Vector2(width, height);
            Apply(label, name, height);
            SyncItem(item, label, true);
        }

        private void RefreshWindow(PixelWindow window)
        {
            // A header is drawn inside the existing draggable header, never over
            // a close button or over an existing title.
            if ((!window.canMove && !window.forceRaycast) || window.hasTitle || window.widthPixels < 32)
            {
                RemoveWindow(window);
                return;
            }
            GameItem owner = null;
            foreach (GameItem parent in window.parentItems)
            {
                if (parent == null || !ReferenceEquals(parent.contentWindow, window)) continue;
                if (owner != null && !ReferenceEquals(owner, parent))
                {
                    RemoveWindow(window);
                    return;
                }
                owner = parent;
            }
            if (owner == null || !IsArea(owner))
            {
                RemoveWindow(window);
                return;
            }

            string heading = AreaLabelPolicy.Heading(ItemName(owner), SelectedPage(owner, window));
            if (heading == null)
            {
                RemoveWindow(window);
                return;
            }
            if (!windows.TryGetValue(window, out Label label) || label.Root == null || label.Text == null)
            {
                RemoveWindow(window);
                label = Create(window.handler, "ShopAssist_AreaHeading");
                windows[window] = label;
            }
            float height = Mathf.Min(16f, Mathf.Max(8f, fontSize.Value + 2f));
            RectTransform rect = label.Root.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(4f, -2f);
            rect.sizeDelta = new Vector2(Mathf.Max(0f, window.widthPixels - 26f), height);
            Apply(label, heading, height);
            label.Root.gameObject.SetActive(true);
        }

        private string SelectedPage(GameItem owner, PixelWindow window)
        {
            if (!(window.childElement is MachineryPanelRow row)) return null;
            foreach (MachineryPanel panel in row.Panels)
            {
                if (panel == null || panel.isHidden || panel.handler == null ||
                    !panel.handler.gameObject.activeInHierarchy || !(panel.content is MachineryTabs tabs) ||
                    tabs.handler == null || !tabs.handler.gameObject.activeInHierarchy || tabs.selected < 0)
                    continue;
                string custom = panel.role == "storage" ? StorageExpansionHelper.GetTabName(owner, tabs.selected) : null;
                string name = AreaLabelPolicy.Name(custom,
                    showDefaults.Value ? tabs.GetCaption(tabs.selected) : null, showDefaults.Value);
                if (name != null) return name;
            }
            return null;
        }

        private static Label Create(TreeNodeRender parent, string name)
        {
            TreeNodeRender root = TreeNodeRender.Instantiate();
            if (root == null || root.image == null || root.rectTransform == null)
                throw new InvalidOperationException("The game did not create a label background.");
            root.SetParent(parent);
            root.gameObject.name = name;
            root.image.sprite = null;
            root.image.raycastTarget = false;
            TreeNodeRender text = TreeNodeRender.InstantiateText();
            if (text == null || text.text == null || text.rectTransform == null)
            {
                UnityEngine.Object.Destroy(root.gameObject);
                throw new InvalidOperationException("The game did not create label text.");
            }
            text.SetParent(root);
            text.gameObject.name = name + "Text";
            text.text.raycastTarget = false;
            text.text.richText = false;
            text.text.enableWordWrapping = false;
            text.text.overflowMode = TextOverflowModes.Ellipsis;
            text.text.alignment = TextAlignmentOptions.Center;
            text.text.color = Color.white;
            text.text.outlineColor = new Color32(35, 26, 42, 220);
            text.text.outlineWidth = 0.15f;
            text.text.enableAutoSizing = true;
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(1f, 0f);
            text.rectTransform.offsetMax = new Vector2(-1f, 0f);
            return new Label { Root = root, Text = text };
        }

        private void Apply(Label label, string value, float height)
        {
            label.Root.image.color = new Color(0f, 0f, 0f, 0.72f);
            label.Root.image.enabled = background.Value;
            label.Root.image.raycastTarget = false;
            label.Text.text.text = value;
            label.Text.text.fontSizeMin = 3f;
            label.Text.text.fontSizeMax = Mathf.Max(3f, Mathf.Min(fontSize.Value, height - 1f));
            label.Text.text.fontSize = label.Text.text.fontSizeMax;
            label.Text.text.raycastTarget = false;
            label.Text.text.enabled = true;
            label.Root.rectTransform.SetAsLastSibling();
        }

        private void SyncItem(GameItemElement item, Label label, bool itemVisible)
        {
            if (label.Root == null || label.Text == null) return;
            bool visible = itemVisible && item.background != null && item.background.image != null &&
                item.background.image.enabled;
            if (label.Root.gameObject.activeSelf != visible) label.Root.gameObject.SetActive(visible);
            if (visible) label.Text.text.enabled = true;
        }

        private void SynchronizeVisibility()
        {
            itemCleanup.Clear();
            itemCleanup.AddRange(items.Keys);
            foreach (GameItemElement item in itemCleanup)
            {
                if (!VisibleInventoryViews.Visible(item)) RemoveItem(item);
                else SyncItem(item, items[item], true);
            }
            windowCleanup.Clear();
            windowCleanup.AddRange(windows.Keys);
            foreach (PixelWindow window in windowCleanup)
                if (!VisibleInventoryViews.Visible(window) || !WindowsHandler.current.visibleWindows.Contains(window)) RemoveWindow(window);
        }

        private void RemoveItem(GameItemElement item)
        {
            if (item != null && items.TryGetValue(item, out Label label))
            {
                Destroy(label);
                items.Remove(item);
            }
        }

        private void RemoveWindow(PixelWindow window)
        {
            if (window != null && windows.TryGetValue(window, out Label label))
            {
                Destroy(label);
                windows.Remove(window);
            }
        }

        private static void Destroy(Label label)
        {
            if (label?.Root != null) UnityEngine.Object.Destroy(label.Root.gameObject);
        }

        private void ClearViews()
        {
            foreach (Label label in items.Values) Destroy(label);
            foreach (Label label in windows.Values) Destroy(label);
            items.Clear();
            windows.Clear();
        }

        public void Dispose()
        {
            if (!started) return;
            started = false;
            if (ReferenceEquals(current, this)) current = null;
            settings.Changed -= SettingsChanged;
            ModHook.OnGameLoadedInit -= ResetViews;
            ModHook.OnGameLoadedLate -= Invalidate;
            ModHook.OnNewGameStarted -= NewGameStarted;
            SceneManager.activeSceneChanged -= SceneChanged;
            ResetViews();
        }
    }

    [HarmonyPatch(typeof(GameItemElement), nameof(GameItemElement.ValidateSelect))]
    internal static class AreaItemValidatePatch
    {
        private static void Postfix(GameItemElement __instance) => AreaLabels.ItemChanged(__instance);
    }

    [HarmonyPatch(typeof(GameItemElement), nameof(GameItemElement.ToggleBackground))]
    internal static class AreaItemVisibilityPatch
    {
        private static void Postfix(GameItemElement __instance) => AreaLabels.ItemChanged(__instance);
    }

    [HarmonyPatch(typeof(GameItemElement), nameof(GameItemElement.Destroy))]
    internal static class AreaItemDestroyPatch
    {
        private static void Postfix(GameItemElement __instance) => AreaLabels.ItemChanged(__instance);
    }

    [HarmonyPatch(typeof(PixelWindow), nameof(PixelWindow.Show), new Type[] { })]
    internal static class AreaWindowShowPatch
    {
        private static void Prefix(PixelWindow __instance, out bool __state) => __state = __instance.IsVisible();
        private static void Postfix(PixelWindow __instance, bool __state)
        {
            if (__state != __instance.IsVisible()) AreaLabels.Invalidate();
        }
    }

    [HarmonyPatch(typeof(PixelWindow), nameof(PixelWindow.Show), new Type[] { typeof(Vector2), typeof(Vector2), typeof(bool) })]
    internal static class AreaWindowShowAtPatch
    {
        private static void Prefix(PixelWindow __instance, out bool __state) => __state = __instance.IsVisible();
        private static void Postfix(PixelWindow __instance, bool __state)
        {
            if (__state != __instance.IsVisible()) AreaLabels.Invalidate();
        }
    }

    [HarmonyPatch(typeof(PixelWindow), nameof(PixelWindow.Hide))]
    internal static class AreaWindowHidePatch
    {
        private static void Prefix(PixelWindow __instance, out bool __state) => __state = __instance.IsVisible();
        private static void Postfix(PixelWindow __instance, bool __state)
        {
            if (__state != __instance.IsVisible()) AreaLabels.Invalidate();
        }
    }

    [HarmonyPatch(typeof(PixelWindow), nameof(PixelWindow.Validate))]
    internal static class AreaWindowValidatePatch
    {
        private static void Postfix() => AreaLabels.Invalidate();
    }

    [HarmonyPatch(typeof(MachineryTabs), nameof(MachineryTabs.Select))]
    internal static class AreaTabSelectPatch
    {
        private static void Postfix() => AreaLabels.Invalidate();
    }

    [HarmonyPatch(typeof(MachineryTabs), nameof(MachineryTabs.SetCaption))]
    internal static class AreaTabRenamePatch
    {
        private static void Postfix() => AreaLabels.Invalidate();
    }

    [HarmonyPatch(typeof(MachineryPanel), nameof(MachineryPanel.Validate))]
    internal static class AreaPanelValidatePatch
    {
        private static void Postfix() => AreaLabels.Invalidate();
    }
}
