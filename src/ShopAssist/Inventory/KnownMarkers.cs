using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    // Uses the public-risk observer's direct evidence and its session ledger;
    // it never opens or scans a closed container.
    internal sealed class KnownMarkers : IDisposable
    {
        private sealed class Marker
        {
            internal TreeNodeRender Root, Text;
            internal bool Direct, Inside;
            internal readonly List<TreeNodeRender> Edges = new List<TreeNodeRender>();
        }
        private readonly ShopSettings settings;
        private readonly CustomerIndicators clues;
        private readonly Action<string> log;
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<int> corner, size, style;
        private readonly Dictionary<GameItemElement, Marker> markers = new Dictionary<GameItemElement, Marker>();
        private readonly HashSet<GameItemElement> visible = new HashSet<GameItemElement>();
        private readonly List<TreeNodeRender> nodes = new List<TreeNodeRender>();
        private bool started, dirty = true, showingTooltip;
        private string lastError, lastItemError;

        internal KnownMarkers(ShopSettings settings, CustomerIndicators clues, Action<string> log)
        {
            this.settings = settings; this.clues = clues; this.log = log;
            enabled = settings.Toggle("f39.enabled", "f39.tip", true);
            corner = settings.Choice("f39.corner", "f39.corner.tip", new[] { "f39.top_left", "f39.top_right", "f39.bottom_left", "f39.bottom_right" });
            size = settings.Integer("f39.size", "f39.size.tip", 11, 6, 20);
            style = settings.Choice("f39.style", "f39.style.tip", new[] { "f39.filled", "f39.outline" });
        }
        internal void Start()
        {
            if (started) return;
            started = true; settings.Changed += Invalidate; clues.Changed += Invalidate;
            VisibleInventoryViews.Changed += Invalidate; VisibleInventoryViews.ItemChanged += ItemChanged;
            ModHook.OnGameLoadedInit += Reset; ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }
        private void Invalidate() => dirty = true;
        private void ItemChanged(GameItemElement item) => dirty = true;
        internal void Tick()
        {
            if (!started) return;
            if (!enabled.Value) { Clear(); return; }
            if (WindowsHandler.current?.visibleWindows == null || RenderHandler.current == null) return;
            try
            {
                if (dirty)
                {
                    visible.Clear();
                    foreach (PixelWindow window in WindowsHandler.current.visibleWindows)
                        VisibleInventoryViews.Collect(window, nodes, visible);
                    var containedRisks = clues.KnownRisksInside();
                    string itemError = null;
                    foreach (GameItemElement item in visible)
                    {
                        try { Refresh(item, containedRisks); }
                        catch (Exception error) { Remove(item); itemError = error.ToString(); }
                    }
                    if (itemError != null && itemError != lastItemError) log("F39 could not display one item's marker: " + itemError);
                    lastItemError = itemError;
                    foreach (GameItemElement item in new List<GameItemElement>(markers.Keys))
                        if (!visible.Contains(item)) Remove(item);
                    dirty = false;
                }
                foreach (var pair in markers)
                    if (pair.Value.Root != null)
                        pair.Value.Root.gameObject.SetActive(VisibleInventoryViews.Visible(pair.Key) &&
                            pair.Key.background?.image != null && pair.Key.background.image.enabled);
                UpdateHover();
                lastError = null;
            }
            catch (Exception error)
            {
                Clear(); dirty = false;
                string message = error.ToString();
                if (message != lastError) log("F39 marker display failed: " + message);
                lastError = message;
            }
        }
        private void Refresh(GameItemElement item, Dictionary<GameItem, PublicRisk> containedRisks)
        {
            bool direct = (clues.KnownRisk(item) & PublicRisk.Stolen) != 0;
            containedRisks.TryGetValue(item, out PublicRisk knownInside);
            bool inside = (knownInside & PublicRisk.Stolen) != 0;
            Vector2 extent = ItemOverlayLayout.Size(item);
            if ((!direct && !inside) || item.middle == null || extent.x < 6 || extent.y < 6)
            { Remove(item); return; }
            if (!markers.TryGetValue(item, out Marker marker) || marker.Root == null || marker.Text == null)
            {
                Remove(item);
                TreeNodeRender root = TreeNodeRender.Instantiate();
                TreeNodeRender text = TreeNodeRender.InstantiateText();
                if (root == null || text == null || root.image == null || text.text == null)
                {
                    if (root != null) UnityEngine.Object.Destroy(root.gameObject);
                    if (text != null) UnityEngine.Object.Destroy(text.gameObject);
                    throw new InvalidOperationException("Unable to create a stolen marker.");
                }
                root.SetParent(item.middle); text.SetParent(root); root.gameObject.name = "ShopAssist_KnownStolen";
                root.image.sprite = null; root.image.raycastTarget = false; root.image.enabled = true;
                text.text.raycastTarget = false; text.text.richText = false; text.text.enableWordWrapping = false; text.text.enabled = true;
                text.text.alignment = TextAlignmentOptions.Center; text.text.fontStyle = FontStyles.Bold;
                text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
                text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
                marker = new Marker { Root = root, Text = text }; markers[item] = marker;
                for (int i = 0; i < 4; i++)
                {
                    TreeNodeRender edge = TreeNodeRender.Instantiate();
                    if (edge == null || edge.image == null) throw new InvalidOperationException("Unable to create a marker border.");
                    edge.SetParent(root); edge.image.sprite = null; edge.image.raycastTarget = false; edge.image.enabled = true;
                    edge.image.color = new Color(1f, 0.19f, 0.14f, 1f); marker.Edges.Add(edge);
                }
                text.rectTransform.SetAsLastSibling();
            }
            float dimension = Mathf.Min(size.Value, Mathf.Min(extent.x * .45f, extent.y * .45f));
            marker.Direct = direct; marker.Inside = inside;
            int actualCorner = ChooseCorner(item, extent, Mathf.Clamp(corner.Value, 0, 3), ref dimension);
            bool right = actualCorner == 1 || actualCorner == 3, bottom = actualCorner >= 2;
            float x = right ? extent.x - 2f : 2f;
            float y = bottom ? -extent.y + 2f : -2f;
            var rect = marker.Root.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(right ? 1 : 0, bottom ? 0 : 1);
            rect.anchoredPosition = new Vector2(x, y); rect.sizeDelta = new Vector2(dimension, dimension);
            bool filled = style.Value == 0;
            marker.Root.image.color = filled ? new Color(0.85f, 0.08f, 0.06f, 0.95f) : new Color(0, 0, 0, 0.8f);
            marker.Text.text.color = Color.white; marker.Text.text.fontSize = dimension * (direct && inside ? 0.60f : 0.85f);
            marker.Text.text.text = direct ? (inside ? "!+" : "!") : "+";
            for (int i = 0; i < marker.Edges.Count; i++)
            {
                TreeNodeRender edge = marker.Edges[i]; edge.gameObject.SetActive(!filled);
                RectTransform e = edge.rectTransform;
                e.anchorMin = Vector2.zero; e.anchorMax = Vector2.one;
                e.offsetMin = i == 0 ? new Vector2(0, dimension - 1f) : i == 1 ? Vector2.zero : i == 2 ? Vector2.zero : new Vector2(dimension - 1f, 0);
                e.offsetMax = i == 0 ? Vector2.zero : i == 1 ? new Vector2(0, 1f - dimension) : i == 2 ? new Vector2(1f - dimension, 0) : Vector2.zero;
            }
            rect.SetAsLastSibling();
        }
        private struct Bounds
        {
            internal float Left, Right, Top, Bottom;
            internal Bounds(float left, float right, float top, float bottom)
            { Left = left; Right = right; Top = top; Bottom = bottom; }
        }
        private static bool TryBounds(Transform parent, string name, out Bounds bounds)
        {
            bounds = default;
            if (parent == null) return false;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform node = parent.GetChild(i);
                if (node.name != name || !node.gameObject.activeInHierarchy || !(node is RectTransform rect)) continue;
                Vector2 position = rect.anchoredPosition, size = rect.sizeDelta, pivot = rect.pivot;
                float left = position.x - pivot.x * size.x, top = position.y + (1 - pivot.y) * size.y;
                bounds = new Bounds(left, left + size.x, top, top - size.y);
                return true;
            }
            return false;
        }
        private static float Overlap(Bounds a, Bounds b) => Mathf.Max(0, Mathf.Min(a.Right, b.Right) - Mathf.Max(a.Left, b.Left)) *
            Mathf.Max(0, Mathf.Min(a.Top, b.Top) - Mathf.Max(a.Bottom, b.Bottom));
        private static int ChooseCorner(GameItemElement item, Vector2 extent, int preferred, ref float dimension)
        {
            var obstacles = new List<Bounds>();
            if (TryBounds(item.middle.transform, "ShopAssist_AreaName", out Bounds area)) obstacles.Add(area);
            if (TryBounds(item.middle.transform, "ShopAssist_PublicLabels", out Bounds labels)) obstacles.Add(labels);
            if (item.unitCount > 1 || !string.IsNullOrEmpty(item.customText))
                obstacles.Add(new Bounds(extent.x - 11, extent.x, -extent.y + 11, -extent.y));
            int[] order = { preferred, preferred ^ 2, preferred ^ 1, preferred ^ 3 };
            int chosen = preferred; float best = float.MaxValue, bestSize = dimension;
            float minimum = Mathf.Min(6f, dimension);
            for (float trial = dimension; trial >= minimum; trial = Mathf.Max(minimum, trial - 1f))
            {
                foreach (int candidate in order)
                {
                    bool right = candidate == 1 || candidate == 3, bottom = candidate >= 2;
                    float left = right ? extent.x - 2 - trial : 2;
                    float top = bottom ? -extent.y + 2 + trial : -2;
                    var box = new Bounds(left, left + trial, top, top - trial);
                    float overlap = 0;
                    foreach (Bounds obstacle in obstacles) overlap += Overlap(box, obstacle);
                    if (overlap < best) { best = overlap; chosen = candidate; bestSize = trial; }
                    if (overlap <= 0) { dimension = trial; return candidate; }
                }
                if (trial <= minimum) break;
            }
            dimension = bestSize;
            return chosen;
        }
        private void UpdateHover()
        {
            Marker under = null;
            foreach (var pair in markers)
            {
                Marker marker = pair.Value;
                if (marker.Root == null || !marker.Root.gameObject.activeInHierarchy) continue;
                Canvas canvas = marker.Text.text.canvas;
                Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                if (RectTransformUtility.RectangleContainsScreenPoint(marker.Root.rectTransform, Input.mousePosition, camera) &&
                    ItemLabels.IsTopmost(pair.Key)) under = marker;
            }
            if (under != null && OverlayHandler.current != null)
            {
                string title = ShopSettings.Text(under.Direct && under.Inside ? "f39.both" : under.Direct ? "f39.direct" : "f39.inside");
                OverlayHandler.current.ShowTooltip(Input.mousePosition, new Vector2(24, 0), new RichTextBuilder().Add(title).Build());
                showingTooltip = true;
            }
            else if (showingTooltip && OverlayHandler.current != null)
            {
                OverlayHandler.current.HideTooltip(); showingTooltip = false;
            }
        }
        private void Remove(GameItemElement item)
        {
            if (item != null && markers.TryGetValue(item, out Marker marker))
            {
                if (marker.Root != null) { marker.Root.gameObject.SetActive(false); UnityEngine.Object.Destroy(marker.Root.gameObject); }
                markers.Remove(item);
            }
        }
        private void Clear()
        {
            foreach (Marker marker in markers.Values)
                if (marker.Root != null) { marker.Root.gameObject.SetActive(false); UnityEngine.Object.Destroy(marker.Root.gameObject); }
            markers.Clear();
            if (showingTooltip && OverlayHandler.current != null) OverlayHandler.current.HideTooltip();
            showingTooltip = false;
        }
        private void Reset() { Clear(); dirty = true; }
        private void SceneChanged(Scene oldScene, Scene newScene) => Reset();
        public void Dispose()
        {
            if (!started) return;
            started = false; settings.Changed -= Invalidate; clues.Changed -= Invalidate;
            VisibleInventoryViews.Changed -= Invalidate; VisibleInventoryViews.ItemChanged -= ItemChanged;
            ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Reset; SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
        }
    }
}
