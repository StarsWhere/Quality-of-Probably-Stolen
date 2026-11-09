using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShopAssist
{
    // A preview never opens a content window or asks the game to allocate an item ID.
    internal sealed class ContainerPeek : IDisposable
    {
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<int> delay;
        private readonly ConfigEntry<int> side;
        private readonly List<RaycastResult> hits = new List<RaycastResult>();
        private readonly List<PeekItem> items = new List<PeekItem>();
        private EventSystem eventSystem;
        private PointerEventData pointer;
        private GameItem hovered;
        private GameInventory parent;
        private GameGridInventory grid;
        private string run;
        private int slot;
        private int page;
        private float since;
        private float refreshAt;
        private RectTransform panel;
        private RectTransform layer;
        private Vector2 panelSize, availableSize;
        private bool started;

        private sealed class PeekItem
        {
            internal GameItemElement Item;
            internal Sprite Sprite;
            internal int X, Y, Width, Height, Orientation, Count, OriginX, OriginY;
            internal bool Flipped;
        }

        internal ContainerPeek(ShopSettings settings, Action<string> log)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (log == null) throw new ArgumentNullException(nameof(log));
            enabled = settings.Toggle("f20.enabled", "f20.tip");
            delay = settings.Integer("f20.delay", "f20.delay.tip", 500, 100, 2000);
            side = settings.Choice("f20.side", "f20.side.tip", new[] { "f20.auto", "f20.left", "f20.right" });
        }

        public void Start()
        {
            if (started) return;
            started = true;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        public void Tick()
        {
            if (!started) return;
            if (!enabled.Value || !ShopInventory.InStore || MapUIManager.Instance?.isOutside == true ||
                !ShopInput.Ready() || UIDragHandler.isDraggingAnyUI || ItemMouseDragHandler.current?.IsDraggingItem == true ||
                StoreUIManager.Instance.isAboutToStartMainDialog || DialogUIManager.Instance?.currentDialog != null ||
                DialoguePhoneUIManager.Instance?.currentDialog != null || CustomUIManager.Instance == null || EventSystem.current == null)
            { Reset(); return; }

            GameItem candidate = RaycastItem();
            if (candidate == null || !ShopInventory.SafeContainerForPeek(candidate) ||
                !TryGrid(candidate, out GameGridInventory current, out int currentPage))
            { Reset(); return; }
            string currentRun = PlayerStore.Instance.runID;
            int currentSlot = PlayerStore.Instance.saveSlotId;
            if (!ReferenceEquals(candidate, hovered) || !ReferenceEquals(candidate.parentInventory, parent) ||
                !ReferenceEquals(current, grid) || currentPage != page || currentRun != run || currentSlot != slot)
            {
                Reset();
                hovered = candidate; parent = candidate.parentInventory; grid = current; page = currentPage;
                run = currentRun; slot = currentSlot; since = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - since < Mathf.Clamp(delay.Value, 100, 2000) / 1000f) return;
            if (panel == null || Time.unscaledTime >= refreshAt)
            {
                refreshAt = Time.unscaledTime + 0.25f;
                var snapshot = Capture(current);
                if (panel == null || !Same(snapshot, items) ||
                    !VisibleBounds(out Rect visible) || visible.size != availableSize)
                {
                    items.Clear(); items.AddRange(snapshot);
                    Build(current);
                }
            }
            if (panel != null) Position();
        }

        private GameItem RaycastItem()
        {
            if (eventSystem != EventSystem.current)
            {
                eventSystem = EventSystem.current;
                pointer = new PointerEventData(eventSystem);
            }
            pointer.Reset(); pointer.position = Input.mousePosition;
            hits.Clear(); eventSystem.RaycastAll(pointer, hits);
            if (hits.Count == 0 || hits[0].gameObject == null) return null;
            // Do not look through a non-inventory modal or another UI's raycast target.
            TreeNodeRender node = hits[0].gameObject.GetComponent<TreeNodeRender>();
            return node != null ? node.FindParentElement() as GameItem : null;
        }

        private static bool TryGrid(GameItem item, out GameGridInventory result, out int page)
        {
            result = null; page = -1;
            if (item?.contentWindow?.childElement is GameGridInventory direct) result = direct;
            else if (item?.contentWindow?.childElement is MachineryPanelRow panels)
            {
                foreach (MachineryPanel panel in panels.Panels)
                {
                    if (panel == null || panel.isHidden || panel.role != "storage") continue;
                    GameGridInventory found = null;
                    int foundPage = -1;
                    if (panel.content is MachineryTabs tabs)
                    {
                        foundPage = tabs.selected;
                        found = tabs.GetTabInventory(foundPage) as GameGridInventory;
                    }
                    else found = panel.content as GameGridInventory;
                    if (found == null || result != null) return false;
                    result = found; page = foundPage;
                }
            }
            GridShape shape = result?.inventoryShape;
            return shape != null && shape.width > 0 && shape.height > 0 && shape.width <= 24 && shape.height <= 24 &&
                !result.IsRemoveLocked();
        }

        private static List<PeekItem> Capture(GameGridInventory inventory)
        {
            var result = new List<PeekItem>();
            foreach (GameItem child in inventory.childItems)
            {
                if (!(child is GameItemElement item) || item.IsDestroyed() || child.parentInventory != inventory ||
                    child.IsTag("ITEM_HIDDEN_TAG") || child.IsTag("ITEM_BEHIND_LOCK_TAG") || child.modifiedShape == null ||
                    item.background?.image == null || item.background.image.sprite == null) continue;
                GridShape shape = child.modifiedShape;
                if (shape.minX < 0 || shape.minY < 0 || shape.globalWidth <= 0 || shape.globalHeight <= 0 ||
                    shape.minX + shape.globalWidth > inventory.inventoryShape.width || shape.minY + shape.globalHeight > inventory.inventoryShape.height)
                    continue;
                result.Add(new PeekItem { Item = item, Sprite = item.background.image.sprite, X = shape.minX, Y = shape.minY,
                    Width = shape.width, Height = shape.height, Flipped = shape.flipped, Orientation = shape.orientation,
                    OriginX = child.modifiedXOrigin, OriginY = child.modifiedYOrigin, Count = child.unitCount });
            }
            return result;
        }

        private static bool Same(List<PeekItem> a, List<PeekItem> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                PeekItem x = a[i], y = b[i];
                if (x.Item != y.Item || x.Sprite != y.Sprite || x.X != y.X || x.Y != y.Y || x.Width != y.Width ||
                    x.Height != y.Height || x.Orientation != y.Orientation || x.Flipped != y.Flipped || x.Count != y.Count ||
                    x.OriginX != y.OriginX || x.OriginY != y.OriginY) return false;
            }
            return true;
        }

        private void Build(GameGridInventory inventory)
        {
            DestroyPanel();
            layer = CustomUIManager.Instance.GetLayerRoot("overlay");
            if (layer == null) return;
            int columns = inventory.inventoryShape.width, rows = inventory.inventoryShape.height;
            float cell = Mathf.Min(22f, 300f / columns, 300f / rows);
            if (!VisibleBounds(out Rect bounds)) { layer = null; return; }
            availableSize = bounds.size;
            cell = Mathf.Min(cell, (bounds.width - 16f) / columns, (bounds.height - 16f) / rows);
            if (cell <= 0f) return;
            panelSize = new Vector2(columns * cell + 16f, rows * cell + 16f);
            panel = NewRect("ShopAssist Container Preview", layer, Vector2.zero, panelSize);
            panel.anchorMin = panel.anchorMax = layer.pivot;
            panel.pivot = new Vector2(0f, 1f);
            var background = panel.gameObject.AddComponent<Image>();
            background.color = new Color(0.08f, 0.075f, 0.10f, 0.96f);
            background.raycastTarget = false;
            var group = panel.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false; group.interactable = false;

            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++)
                {
                    var cellRect = NewRect("cell", panel, new Vector2(8f + x * cell, -8f - y * cell), new Vector2(cell - 1f, cell - 1f));
                    var image = cellRect.gameObject.AddComponent<Image>();
                    image.color = inventory.inventoryShape.Get(x, y) == 0 ? new Color(0.32f, 0.30f, 0.33f, 0.85f) : new Color(0.12f, 0.11f, 0.13f, 0.85f);
                    image.raycastTarget = false;
                }

            float gameCell = UISettings.current.gridSize;
            if (gameCell <= 0f) { DestroyPanel(); return; }
            foreach (PeekItem item in items)
            {
                var itemRect = NewRect("item", panel, new Vector2(8f + item.X * cell, -8f - item.Y * cell),
                    new Vector2(item.Width * cell, item.Height * cell));
                var rotator = NewRect("rotation", itemRect, Vector2.zero, itemRect.sizeDelta);
                rotator.pivot = item.Item.rotator.rectTransform.pivot;
                rotator.localScale = new Vector3(item.Flipped ? -1f : 1f, 1f, 1f);
                rotator.localEulerAngles = new Vector3(0f, 0f, 90f * item.Orientation);
                var source = item.Item.background.rectTransform;
                var icon = NewRect("icon", rotator, source.anchoredPosition / gameCell * cell, source.sizeDelta / gameCell * cell);
                icon.anchorMin = source.anchorMin; icon.anchorMax = source.anchorMax; icon.pivot = source.pivot;
                var image = icon.gameObject.AddComponent<Image>();
                image.sprite = item.Sprite; image.color = item.Item.background.image.color; image.raycastTarget = false;
                if (item.Count > 1)
                {
                    var count = NewRect("count", itemRect, new Vector2(0f, -cell * (item.Orientation % 2 == 0 ? item.Height : item.Width) + 12f),
                        new Vector2(Mathf.Max(20f, item.Width * cell), 15f));
                    var text = count.gameObject.AddComponent<TextMeshProUGUI>();
                    text.text = item.Count.ToString(); text.fontSize = 12f; text.alignment = TextAlignmentOptions.BottomRight;
                    text.color = Color.white; text.raycastTarget = false;
                }
            }
        }

        private static RectTransform NewRect(string name, Transform owner, Vector2 position, Vector2 size)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(owner, false); rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f); rect.anchoredPosition = position; rect.sizeDelta = size;
            return rect;
        }

        private void Position()
        {
            Canvas canvas = layer.GetComponentInParent<Canvas>();
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (!VisibleBounds(out Rect bounds) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, Input.mousePosition, camera, out Vector2 cursor)) return;
            float leftEdge = cursor.x - 18f, rightEdge = cursor.x + 18f;
            var tooltip = OverlayHandler.current?.GetTooltipWindow();
            RectTransform tooltipRect = tooltip?.IsVisible() == true ? tooltip.handler?.rectTransform : null;
            if (tooltipRect != null)
            {
                Canvas tooltipCanvas = tooltipRect.GetComponentInParent<Canvas>();
                Camera tooltipCamera = tooltipCanvas == null || tooltipCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : tooltipCanvas.worldCamera;
                Vector2 first = RectTransformUtility.WorldToScreenPoint(tooltipCamera, tooltipRect.TransformPoint(Vector3.zero));
                Vector2 last = RectTransformUtility.WorldToScreenPoint(tooltipCamera,
                    tooltipRect.TransformPoint(new Vector3(tooltip.widthPixels, -tooltip.heightPixels, 0f)));
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, first, camera, out Vector2 low) &&
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, last, camera, out Vector2 high))
                {
                    leftEdge = Mathf.Min(leftEdge, Mathf.Min(low.x, high.x) - 8f);
                    rightEdge = Mathf.Max(rightEdge, Mathf.Max(low.x, high.x) + 20f);
                }
            }
            int mode = Mathf.Clamp(side.Value, 0, 2);
            bool leftFits = leftEdge - panelSize.x >= bounds.xMin;
            bool rightFits = rightEdge + panelSize.x <= bounds.xMax;
            bool left = mode == 1 ? leftFits || !rightFits : mode == 2 ? !rightFits && leftFits :
                !rightFits && (leftFits || leftEdge - bounds.xMin > bounds.xMax - rightEdge);
            float x = left ? leftEdge - panelSize.x : rightEdge;
            x = Mathf.Clamp(x, bounds.xMin, Mathf.Max(bounds.xMin, bounds.xMax - panelSize.x));
            float y = Mathf.Clamp(cursor.y, Mathf.Min(bounds.yMax, bounds.yMin + panelSize.y), bounds.yMax);
            panel.anchoredPosition = new Vector2(x, y);
        }

        private bool VisibleBounds(out Rect bounds)
        {
            bounds = default;
            if (layer == null || Screen.width <= 16 || Screen.height <= 16) return false;
            Canvas canvas = layer.GetComponentInParent<Canvas>();
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, new Vector2(8f, 8f), camera, out Vector2 first) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, new Vector2(Screen.width - 8f, Screen.height - 8f), camera, out Vector2 last)) return false;
            bounds = Rect.MinMaxRect(Mathf.Min(first.x, last.x), Mathf.Min(first.y, last.y),
                Mathf.Max(first.x, last.x), Mathf.Max(first.y, last.y));
            return bounds.width > 16f && bounds.height > 16f;
        }

        private void DestroyPanel()
        {
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            panel = null; layer = null;
        }
        private void Reset()
        {
            DestroyPanel(); hovered = null; parent = null; grid = null; run = null; slot = -1; page = -1;
            items.Clear(); refreshAt = 0f;
        }
        private void SceneChanged(Scene previous, Scene current) => Reset();
        public void Dispose()
        {
            if (!started) return;
            if (started) { ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Reset; SceneManager.activeSceneChanged -= SceneChanged; }
            Reset(); started = false;
        }
    }
}
