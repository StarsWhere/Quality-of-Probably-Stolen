using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShopAssist
{
    internal enum ViewOutlineKind { Demand, Stolen, Contraband, ContainsStolen, ContainsContraband, Locate, Hold }

    // Owned render nodes are children of the same rotating view as the item.
    // Each boundary is inset into an occupied cell, so irregular shapes do not
    // outline or intercept an adjacent item.
    internal sealed class ViewOutlines : IDisposable
    {
        private sealed class Outline
        {
            internal TreeNodeRender Root;
            internal ViewOutlineKind Kind;
            internal bool InsetForPeer;
            internal readonly List<TreeNodeRender> Parts = new List<TreeNodeRender>();
        }

        private readonly Dictionary<GameItemElement, Outline> outlines = new Dictionary<GameItemElement, Outline>();
        private readonly string rootName;

        internal ViewOutlines(string rootName = "ShopAssist_Outline") { this.rootName = rootName; }

        internal bool Show(GameItemElement item, ViewOutlineKind kind)
        {
            GridShape shape = item?.modifiedShape;
            if (shape == null || item.rotator == null || shape.width < 1 || shape.height < 1 ||
                shape.width > 64 || shape.height > 64 || UISettings.current == null || UISettings.current.gridSize <= 0)
            {
                Remove(item);
                return false;
            }
            var cells = new bool[shape.width * shape.height];
            for (int y = 0; y < shape.height; y++)
                for (int x = 0; x < shape.width; x++)
                    cells[y * shape.width + x] = shape.GetLocal(x, y) != 0;
            bool dashed = kind == ViewOutlineKind.ContainsStolen || kind == ViewOutlineKind.ContainsContraband;
            bool insetForPeer = InsetForPeer(item, kind);
            List<OutlineSegment> segments = OutlineGeometry.Build(cells, shape.width, shape.height,
                UISettings.current.gridSize, dashed, insetForPeer ? 2.5f : 0);
            if (segments.Count == 0)
            {
                Remove(item);
                return false;
            }
            if (!outlines.TryGetValue(item, out Outline outline) || outline.Root == null)
            {
                Remove(item);
                outline = new Outline { Root = NewNode(item.rotator, rootName) };
                outline.Root.image.enabled = false;
                outlines.Add(item, outline);
            }
            Color color = kind == ViewOutlineKind.Hold ? new Color(0.7f, 0.6f, 1f, 1f) :
                kind == ViewOutlineKind.Locate ? new Color(0.35f, 0.7f, 1f, 1f) :
                kind == ViewOutlineKind.Demand ? new Color(0.2f, 0.95f, 0.75f, 1f) :
                (kind == ViewOutlineKind.Stolen || kind == ViewOutlineKind.ContainsStolen
                    ? new Color(1f, 0.65f, 0.15f, 1f) : new Color(1f, 0.25f, 0.22f, 1f));
            outline.Kind = kind;
            outline.InsetForPeer = insetForPeer;
            int required = segments.Count + (dashed ? 4 : kind == ViewOutlineKind.Hold ? 2 : 0);
            while (outline.Parts.Count < required)
                outline.Parts.Add(NewNode(outline.Root, "ShopAssist_OutlinePart"));
            for (int i = 0; i < outline.Parts.Count; i++)
            {
                TreeNodeRender part = outline.Parts[i];
                bool active = i < required;
                part.gameObject.SetActive(active);
                if (!active) continue;
                OutlineSegment segment;
                if (i < segments.Count) segment = segments[i];
                else if (kind == ViewOutlineKind.Hold)
                    segment = i == segments.Count ? new OutlineSegment(2f, -2f, 7f, 3f) : new OutlineSegment(5f, -5f, 2f, 5f);
                else
                {
                    int side = i - segments.Count;
                    segment = side == 0 ? new OutlineSegment(2f, -2f, 6f, 1f) :
                        side == 1 ? new OutlineSegment(2f, -7f, 6f, 1f) :
                        side == 2 ? new OutlineSegment(2f, -2f, 1f, 6f) : new OutlineSegment(7f, -2f, 1f, 6f);
                }
                RectTransform rect = part.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(segment.X, segment.Y);
                rect.sizeDelta = new Vector2(segment.Width, segment.Height);
                part.image.color = color;
                part.image.enabled = true;
                part.image.raycastTarget = false;
            }
            outline.Root.rectTransform.SetAsLastSibling();
            SetVisibility(item, outline);
            return outline.Root.gameObject.activeSelf;
        }

        // The workshop painter may toggle on a later frame without a game view
        // event. Yield only the same visible indication, and restore ours when
        // its border is turned off. Contained-risk, locate and hold marks have
        // their own meaning and remain independent.
        internal void Sync()
        {
            var redraw = new List<KeyValuePair<GameItemElement, ViewOutlineKind>>();
            foreach (var pair in outlines)
                if (pair.Key != null && pair.Value.Root != null)
                {
                    if (pair.Value.InsetForPeer != InsetForPeer(pair.Key, pair.Value.Kind))
                        redraw.Add(new KeyValuePair<GameItemElement, ViewOutlineKind>(pair.Key, pair.Value.Kind));
                    else SetVisibility(pair.Key, pair.Value);
                }
            foreach (var pair in redraw) Show(pair.Key, pair.Value);
        }

        private static bool InsetForPeer(GameItemElement item, ViewOutlineKind kind) =>
            kind == ViewOutlineKind.ContainsStolen ? EquivalentWorkshopBorder(item, ViewOutlineKind.Stolen) :
            kind == ViewOutlineKind.ContainsContraband && EquivalentWorkshopBorder(item, ViewOutlineKind.Contraband);

        private static void SetVisibility(GameItemElement item, Outline outline)
        {
            bool show = item.background != null && item.background.image != null && item.background.image.enabled &&
                !EquivalentWorkshopBorder(item, outline.Kind);
            if (outline.Root.gameObject.activeSelf != show) outline.Root.gameObject.SetActive(show);
        }

        private static bool EquivalentWorkshopBorder(GameItemElement item, ViewOutlineKind kind)
        {
            if (kind != ViewOutlineKind.Demand && kind != ViewOutlineKind.Stolen && kind != ViewOutlineKind.Contraband)
                return false;
            return EquivalentWorkshopNode(item.middle?.transform.Find("CDH_Border"), kind) ||
                EquivalentWorkshopNode(item.rotator?.transform.Find("CDH_Border"), kind);
        }

        private static bool EquivalentWorkshopNode(Transform node, ViewOutlineKind kind)
        {
            if (node == null || !node.gameObject.activeInHierarchy) return false;
            var graphic = node.gameObject.GetComponent<UnityEngine.UI.Graphic>();
            if (graphic == null || !graphic.isActiveAndEnabled || graphic.color.a <= 0f) return false;
            Color color = graphic.color;
            bool amber = Math.Abs(color.r - 1f) < 0.02f && Math.Abs(color.g - 0.7f) < 0.02f && Math.Abs(color.b - 0.1f) < 0.02f;
            bool red = Math.Abs(color.r - 0.95f) < 0.02f && Math.Abs(color.g - 0.22f) < 0.02f && Math.Abs(color.b - 0.2f) < 0.02f;
            bool green = Math.Abs(color.r - 0.3f) < 0.02f && Math.Abs(color.g - 0.9f) < 0.02f && Math.Abs(color.b - 0.35f) < 0.02f;
            return kind == ViewOutlineKind.Demand ? green || amber || red : kind == ViewOutlineKind.Stolen ? amber : red;
        }

        private static TreeNodeRender NewNode(TreeNodeRender parent, string name)
        {
            TreeNodeRender node = TreeNodeRender.Instantiate();
            if (node == null || node.image == null || node.rectTransform == null)
                throw new InvalidOperationException("The game did not create an outline node.");
            node.SetParent(parent);
            node.gameObject.name = name;
            node.image.sprite = null;
            node.image.raycastTarget = false;
            return node;
        }

        internal void Remove(GameItemElement item)
        {
            if (item != null && outlines.TryGetValue(item, out Outline outline))
            {
                if (outline.Root != null) UnityEngine.Object.Destroy(outline.Root.gameObject);
                outlines.Remove(item);
            }
        }

        internal void KeepOnly(HashSet<GameItemElement> current)
        {
            foreach (GameItemElement item in new List<GameItemElement>(outlines.Keys))
                if (!current.Contains(item)) Remove(item);
        }

        public void Dispose()
        {
            foreach (Outline outline in outlines.Values)
                if (outline.Root != null) UnityEngine.Object.Destroy(outline.Root.gameObject);
            outlines.Clear();
        }
    }
}
