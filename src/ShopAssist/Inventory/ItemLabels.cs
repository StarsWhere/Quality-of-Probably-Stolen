using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    // Labels come from the same public fields as the ordinary tooltip. A custom
    // tooltip is authoritative only after it has actually appeared on screen.
    internal sealed class ItemLabels : IDisposable
    {
        private sealed class Label
        {
            internal TreeNodeRender Root, Text;
            internal string Full;
        }

        private readonly ShopSettings settings;
        private readonly Action<string> log;
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<int> size;
        private readonly Dictionary<GameItemElement, Label> labels = new Dictionary<GameItemElement, Label>();
        private readonly Dictionary<GameItemElement, List<string>> observed = new Dictionary<GameItemElement, List<string>>();
        private readonly HashSet<GameItemElement> visible = new HashSet<GameItemElement>();
        private readonly HashSet<GameItemElement> dirtyItems = new HashSet<GameItemElement>();
        private readonly List<TreeNodeRender> nodes = new List<TreeNodeRender>();
        private bool started, dirty = true, showingTooltip;
        private string lastError, lastItemError;

        internal ItemLabels(ShopSettings settings, Action<string> log)
        {
            this.settings = settings; this.log = log;
            enabled = settings.Toggle("f38.enabled", "f38.tip", true);
            size = settings.Integer("f38.size", "f38.size.tip", 9, 6, 16);
        }

        internal void Start()
        {
            if (started) return;
            started = true;
            settings.Changed += Invalidate;
            VisibleInventoryViews.Changed += Invalidate;
            VisibleInventoryViews.ItemChanged += ItemChanged;
            CustomerIndicators.PublicTooltipShown += Observe;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        private void Invalidate() => dirty = true;
        private void ItemChanged(GameItemElement item)
        {
            if (item == null) return;
            observed.Remove(item);
            dirtyItems.Add(item);
        }

        private void Observe(GameItemElement item, string tooltip)
        {
            if (item == null || CustomerIndicators.HasOrdinaryTooltip(item)) return;
            string prefix = LocHelper.GetLocalizedMechanic("mech_label_tooltip_prefix");
            if (string.IsNullOrEmpty(prefix)) return;
            var values = new List<string>();
            foreach (string raw in (tooltip ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string value = line.Substring(prefix.Length).Trim();
                if (value.Length > 0) values.Add(value);
            }
            if (observed.TryGetValue(item, out List<string> previous) && previous.Count == values.Count)
            {
                bool same = true;
                for (int i = 0; i < values.Count; i++)
                    if (previous[i] != values[i]) { same = false; break; }
                if (same) return;
            }
            observed[item] = values;
            dirtyItems.Add(item);
        }

        internal void Tick()
        {
            if (!started) return;
            if (!enabled.Value) { Clear(); return; }
            if (WindowsHandler.current?.visibleWindows == null || RenderHandler.current == null) return;
            try
            {
                if (dirty)
                {
                    dirty = false;
                    dirtyItems.Clear();
                    visible.Clear();
                    foreach (PixelWindow window in WindowsHandler.current.visibleWindows)
                        VisibleInventoryViews.Collect(window, nodes, visible);
                    string itemError = null;
                    foreach (GameItemElement item in visible)
                    {
                        try { Refresh(item); }
                        catch (Exception error) { Remove(item); itemError = error.ToString(); }
                    }
                    if (itemError != null && itemError != lastItemError) log("F38 could not display one item's labels: " + itemError);
                    lastItemError = itemError;
                    foreach (GameItemElement item in new List<GameItemElement>(labels.Keys))
                        if (!visible.Contains(item)) Remove(item);
                }
                else if (dirtyItems.Count != 0)
                {
                    var pending = new List<GameItemElement>(dirtyItems);
                    dirtyItems.Clear();
                    string itemError = null;
                    foreach (GameItemElement item in pending)
                    {
                        try
                        {
                            if (VisibleInventoryViews.Visible(item)) Refresh(item);
                            else Remove(item);
                        }
                        catch (Exception error) { Remove(item); itemError = error.ToString(); }
                    }
                    if (itemError != null && itemError != lastItemError) log("F38 could not display one item's labels: " + itemError);
                    lastItemError = itemError;
                }
                foreach (var pair in labels)
                    if (pair.Value.Root != null)
                        pair.Value.Root.gameObject.SetActive(VisibleInventoryViews.Visible(pair.Key) &&
                            pair.Key.background?.image != null && pair.Key.background.image.enabled);
                UpdateHover();
                lastError = null;
            }
            catch (Exception error)
            {
                Clear(); dirty = false; dirtyItems.Clear();
                string message = error.ToString();
                if (message != lastError) log("F38 label display failed: " + message);
                lastError = message;
            }
        }

        private List<string> Values(GameItemElement item)
        {
            if (!CustomerIndicators.HasOrdinaryTooltip(item))
                return observed.TryGetValue(item, out List<string> found) ? found : null;
            var result = new List<string>();
            if (item.itemFeatures == null) return result;
            foreach (ItemFeature feature in item.itemFeatures)
            {
                if (feature == null) continue;
                // The native tooltip deliberately stops here, including later features.
                if (item.IsTag("LIQUID_CONTAINER_TAG") && WaterHelper.IsEmpty(item) && feature.category == "CATEGORY_WATER_PURITY") break;
                if (!feature.IsExposed() && feature.GetPublicHidden()) continue;
                string display = feature.GetBaseCurrentDisplay();
                if (!string.IsNullOrWhiteSpace(display)) result.Add(display.Trim());
            }
            return result;
        }

        private void Refresh(GameItemElement item)
        {
            List<string> values = Values(item);
            Vector2 extent = ItemOverlayLayout.Size(item);
            if (values == null || values.Count == 0 || item.middle == null || extent.x < 12 || extent.y < 12)
            { Remove(item); return; }
            if (!labels.TryGetValue(item, out Label label) || label.Root == null || label.Text == null)
            {
                Remove(item);
                TreeNodeRender root = TreeNodeRender.Instantiate();
                TreeNodeRender text = TreeNodeRender.InstantiateText();
                if (root == null || text == null || root.image == null || text.text == null)
                {
                    if (root != null) UnityEngine.Object.Destroy(root.gameObject);
                    if (text != null) UnityEngine.Object.Destroy(text.gameObject);
                    throw new InvalidOperationException("Unable to create a label view.");
                }
                root.SetParent(item.middle); text.SetParent(root);
                root.gameObject.name = "ShopAssist_PublicLabels";
                root.image.sprite = null; root.image.color = new Color(0, 0, 0, 0.77f); root.image.raycastTarget = false;
                root.image.enabled = true;
                text.text.color = Color.white; text.text.richText = false; text.text.raycastTarget = false; text.text.enabled = true;
                text.text.enableWordWrapping = false; text.text.overflowMode = TextOverflowModes.Ellipsis;
                text.text.alignment = TextAlignmentOptions.Center; text.text.enableAutoSizing = false;
                text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
                text.rectTransform.offsetMin = new Vector2(1, 0); text.rectTransform.offsetMax = new Vector2(-1, 0);
                label = new Label { Root = root, Text = text }; labels[item] = label;
            }
            int maxRows = extent.y >= 42 ? 2 : 1;
            int shown = Math.Min(maxRows, values.Count);
            label.Full = string.Join("\n", values);
            float lineHeight = Mathf.Min(size.Value + 1f, Mathf.Max(5f, extent.y * 0.22f));
            RectTransform rect = label.Root.rectTransform;
            Transform area = item.middle.transform.Find("ShopAssist_AreaName");
            Transform marker = item.middle.transform.Find("ShopAssist_KnownStolen");
            bool reserveCorners = item.unitCount > 1 || !string.IsNullOrEmpty(item.customText) ||
                area != null && area.gameObject.activeInHierarchy || marker != null && marker.gameObject.activeInHierarchy;
            // Keep enough room for other visible overlays but use the full
            // item width when there is no competing text or badge.
            float width = Mathf.Max(8f, extent.x - Mathf.Min(reserveCorners ? 16f : 4f, extent.x * .5f));
            bool areaBelow = area != null && area.gameObject.activeInHierarchy && area is RectTransform areaRect &&
                areaRect.anchoredPosition.y < -extent.y * .5f;
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0.5f, areaBelow ? 1 : 0);
            rect.anchoredPosition = new Vector2(extent.x * 0.5f, areaBelow ? -2f : -extent.y + 2f);
            rect.sizeDelta = new Vector2(width, lineHeight * shown);
            float maximumFont = Mathf.Min(size.Value, lineHeight - 1f);
            label.Text.text.enableAutoSizing = false;
            label.Text.text.fontSize = maximumFont;
            var lines = new List<string>();
            for (int i = 0; i < shown; i++)
            {
                string suffix = i == shown - 1 && values.Count > shown ? " +" + (values.Count - shown) : "";
                lines.Add(Fit(label.Text.text, values[i], suffix, width - 2f));
            }
            label.Text.text.text = string.Join("\n", lines);
            label.Text.text.fontSizeMin = 3f; label.Text.text.fontSizeMax = maximumFont;
            label.Text.text.enableAutoSizing = true;
            rect.SetAsLastSibling();
        }

        private static string Fit(TMP_Text text, string value, string suffix, float width)
        {
            // Let TMP shrink a complete label before falling back to ellipsis.
            if (suffix.Length == 0) return value;
            if (text.GetPreferredValues(value + suffix).x <= width) return value + suffix;
            string tail = "…" + suffix;
            if (suffix.Length != 0 && text.GetPreferredValues(tail).x > width) return suffix.TrimStart();
            int length = value.Length;
            while (length > 0 && text.GetPreferredValues(value.Substring(0, length) + tail).x > width) length--;
            return value.Substring(0, length) + tail;
        }

        private void UpdateHover()
        {
            Label under = null;
            foreach (var pair in labels)
            {
                Label label = pair.Value;
                if (label.Root == null || !label.Root.gameObject.activeInHierarchy) continue;
                RectTransform rect = label.Root.rectTransform;
                Canvas canvas = label.Text.text.canvas;
                Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                if (RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, camera) &&
                    IsTopmost(pair.Key)) under = label;
            }
            if (under != null && OverlayHandler.current != null)
            {
                OverlayHandler.current.ShowTooltip(Input.mousePosition, new Vector2(24, 0), new RichTextBuilder().Add(under.Full).Build());
                showingTooltip = true;
            }
            else if (showingTooltip && OverlayHandler.current != null)
            {
                OverlayHandler.current.HideTooltip(); showingTooltip = false;
            }
        }

        internal static bool IsTopmost(GameItemElement item)
        {
            return item?.handler != null && UnityEngine.EventSystems.EventSystem.current != null &&
                ReferenceEquals(RenderHandler.RaycastElement<GameItemElement>(Input.mousePosition), item);
        }

        private void Remove(GameItemElement item)
        {
            if (item != null && labels.TryGetValue(item, out Label label))
            {
                if (label.Root != null) { label.Root.gameObject.SetActive(false); UnityEngine.Object.Destroy(label.Root.gameObject); }
                labels.Remove(item);
            }
        }
        private void Clear()
        {
            foreach (Label label in labels.Values)
                if (label.Root != null) { label.Root.gameObject.SetActive(false); UnityEngine.Object.Destroy(label.Root.gameObject); }
            labels.Clear();
            if (showingTooltip && OverlayHandler.current != null) OverlayHandler.current.HideTooltip();
            showingTooltip = false;
        }
        private void Reset() { Clear(); observed.Clear(); dirtyItems.Clear(); dirty = true; }
        private void SceneChanged(Scene oldScene, Scene newScene) => Reset();
        public void Dispose()
        {
            if (!started) return;
            started = false; settings.Changed -= Invalidate; VisibleInventoryViews.Changed -= Invalidate;
            VisibleInventoryViews.ItemChanged -= ItemChanged; CustomerIndicators.PublicTooltipShown -= Observe;
            ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Reset; SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
        }
    }
}
