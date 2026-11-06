using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ShopAssist
{
    // A view of existing objects: no GameItem is created, reparented, or mutated by this class.
    internal static class ItemGrid
    {
        internal sealed class Entry
        {
            internal readonly GameItem Item;
            internal readonly GameInventory Source;
            internal readonly GridShape Shape;
            internal bool Selected;
            internal bool Enabled = true;
            internal bool Selectable = true;
            internal string Reason;
            internal string Caption;
            internal string Badge;
            internal string Marker;
            internal string SourceLabel;
            internal string StatusLabel;
            internal string QuoteLabel;

            internal Entry(GameItem item, GameInventory source = null, GridShape shape = null)
            {
                Item = item;
                Source = source ?? item?.parentInventory;
                Shape = shape ?? item?.modifiedShape;
            }
        }

        internal sealed class Options
        {
            // Board and Inventory are set together for a real, precisely addressable destination.
            // Without a board, entries are packed into a compact candidate grid.
            internal GridShape Board;
            internal GameGridInventory Inventory;
            // Occluded occupants are shown as blocked cells without revealing their contents.
            internal IReadOnlyList<GridShape> Concealed;
            // Aggregate views may wrap items around a real machine bay. This is display space only.
            internal RectInt Reserved;
            // A compact aggregate may direct a drop to a real inventory's first valid empty pose.
            internal bool Proxy;
            internal int Columns = 12;
            internal float CellSize = 24f;
            internal bool HighContrast;
            internal bool Cards;
            internal float CardWidth = 120f;
            internal float CardHeight = 105f;
            internal Action<Entry> OnClick;
            internal Action<IReadOnlyList<Entry>> OnMarquee;
            internal Func<Entry, GameGridInventory, GridShape, bool> CanDrop;
            internal Action<Entry, GameGridInventory, GridShape> OnDrop;
            internal Action<Entry, GameGridInventory, GridShape> OnReject;
        }

        private sealed class Placed
        {
            internal Entry Entry;
            internal GridShape Shape;
        }

        internal static RectTransform Add(CustomUIBuilder builder, IEnumerable<Entry> entries, Options options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            options = options ?? new Options();
            if (options.Cards && options.Board == null) return AddCards(builder, entries, options);
            var list = (entries ?? Enumerable.Empty<Entry>()).Where(entry => entry?.Item != null && entry.Shape != null).ToList();
            float cell = Mathf.Clamp(options.CellSize, 12f, 64f);
            // A particularly wide item must remain reachable even when the viewport is narrow.
            // The surrounding scroll view can expose the extra width.
            int widest = list.Count == 0 ? 1 : list.Max(entry => entry.Shape.globalWidth);
            int columns = options.Board == null ? Math.Max(Math.Max(1, options.Columns), widest) : options.Board.width;
            int rows = options.Board == null ? 0 : options.Board.height;
            if (columns < 1 || rows < 0) throw new ArgumentOutOfRangeException(nameof(options));
            var placed = Place(list, options.Board, columns, ref rows, options.Reserved);
            rows = Math.Max(1, rows);
            var rect = NewRect("ShopAssist_ItemGrid", builder.CurrentContainer, Vector2.zero, new Vector2(columns * cell, rows * cell));
            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minWidth = layout.preferredWidth = columns * cell;
            layout.minHeight = layout.preferredHeight = rows * cell;
            layout.flexibleWidth = layout.flexibleHeight = 0;
            var view = rect.gameObject.AddComponent<View>();
            view.Initialize(rect, options, cell, columns, rows);
            rect.gameObject.AddComponent<GridRoot>();
            for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++)
            {
                if (options.Board == null && options.Reserved.Contains(new Vector2Int(x, y))) continue;
                RectTransform tile = NewRect("cell_" + x + "_" + y, rect, new Vector2(x * cell, -y * cell), new Vector2(cell - 1, cell - 1));
                Image bg = tile.gameObject.AddComponent<Image>();
                bool concealed = options.Concealed != null && options.Concealed.Any(shape => shape != null && shape.Get(x, y) == 1);
                bool usable = options.Board == null || options.Board.Get(x, y) == 0;
                Color fill = concealed ? new Color(.16f, .15f, .18f, .95f) : usable ?
                    (options.HighContrast ? new Color(.29f, .31f, .33f, .95f) : new Color(.25f, .24f, .27f, .85f)) :
                    new Color(.08f, .07f, .09f, .95f);
                bg.color = options.HighContrast ? new Color(.56f, .58f, .60f, .95f) : fill;
                bg.raycastTarget = true;
                if (bg.raycastTarget) tile.gameObject.AddComponent<Hit>().Initialize(view, null, null);
                if (options.HighContrast)
                {
                    RectTransform inside = NewRect("fill", tile, new Vector2(1, -1), new Vector2(cell - 3, cell - 3));
                    Image image = inside.gameObject.AddComponent<Image>(); image.color = fill; image.raycastTarget = false;
                    if (!usable || concealed)
                    {
                        for (int turn = -1; turn <= 1; turn += 2)
                        {
                            RectTransform slash = NewRect("blocked_mark", tile, new Vector2(cell * .5f, -cell * .5f), new Vector2(cell * .58f, 2));
                            slash.pivot = new Vector2(.5f, .5f); slash.localRotation = Quaternion.Euler(0, 0, 45 * turn);
                            Image mark = slash.gameObject.AddComponent<Image>(); mark.color = new Color(.64f, .38f, .35f, .9f); mark.raycastTarget = false;
                        }
                    }
                }
            }
            foreach (Placed part in placed) DrawPart(view, part);
            return rect;
        }

        internal static void RefreshEntry(RectTransform grid, Entry entry) => grid?.GetComponent<View>()?.RefreshEntry(entry);

        private static RectTransform AddCards(CustomUIBuilder builder, IEnumerable<Entry> entries, Options options)
        {
            var list = (entries ?? Enumerable.Empty<Entry>()).Where(e => e?.Item != null && e.Shape != null).ToList();
            int columns = Mathf.Clamp(options.Columns, 1, 12);
            float width = Mathf.Clamp(options.CardWidth, 64f, 300f), height = Mathf.Clamp(options.CardHeight, 64f, 300f);
            bool compact = options.Columns == 1 && options.CardHeight < 90;
            if (!compact)
            {
                float available = Mathf.Max(160, (UiLayout.Window(builder)?.Rect.rect.width ?? columns * width) - 48);
                columns = Mathf.Clamp(Mathf.FloorToInt(available / 180), 1, Math.Max(1, Math.Min(columns, list.Count)));
                width = Mathf.Min(240, available / columns);
            }
            int rows = (list.Count + columns - 1) / columns;
            height = Mathf.Max(height, list.Count == 0 ? 0 : list.Max(entry => (compact ? 74f : 54f) + CardDetails(entry).Count * 18f));
            RectTransform root = NewRect("ShopAssist_ItemCards", builder.CurrentContainer, Vector2.zero, new Vector2(columns * width, rows * height));
            var rootImage = root.gameObject.AddComponent<Image>(); rootImage.color = new Color(0, 0, 0, .01f);
            var rootView = root.gameObject.AddComponent<View>();
            rootView.Initialize(root, options, options.CellSize, columns, rows);
            root.gameObject.AddComponent<GridRoot>();
            root.gameObject.AddComponent<Hit>().Initialize(rootView, null, null);
            LayoutElement layout = root.gameObject.AddComponent<LayoutElement>();
            layout.minWidth = layout.preferredWidth = columns * width; layout.flexibleWidth = 0;
            layout.minHeight = layout.preferredHeight = rows * height; layout.flexibleHeight = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Entry entry = list[i]; GridShape shape = entry.Shape;
                RectTransform card = NewRect("card", root, new Vector2((i % columns) * width, -(i / columns) * height), new Vector2(width - 7, height - 7));
                var background = card.gameObject.AddComponent<Image>();
                background.color = CardColor(entry.Selected);
                background.raycastTarget = true;
                string caption = entry.Caption ?? entry.Item.GetDisplayName(true);
                List<string> details = CardDetails(entry);
                float badgeHeight = details.Count * 18f;
                const float captionHeight = 30f;
                float imageHeight = compact ? Mathf.Max(8, height - captionHeight - badgeHeight - 9) : Mathf.Min(76, height - 20);
                float imageWidth = compact ? width - 12 : 52;
                float cell = Mathf.Clamp(Mathf.Min(options.CellSize, imageWidth / shape.globalWidth, imageHeight / shape.globalHeight), 4, 64);
                float gridWidth = shape.globalWidth * cell, gridHeight = shape.globalHeight * cell;
                RectTransform grid = NewRect("grid", card,
                    compact ? new Vector2((width - gridWidth) / 2, -6) : new Vector2(9 + (52 - gridWidth) / 2, -(height - gridHeight) / 2),
                    new Vector2(gridWidth, gridHeight));
                var view = grid.gameObject.AddComponent<View>();
                view.Initialize(grid, options, cell, shape.globalWidth, shape.globalHeight);
                view.CardBackground = background;
                rootView.RegisterCard(entry, view);
                GridShape pose = new GridShapeBuilder(shape).SetPosition(0, 0).Build();
                card.gameObject.AddComponent<Hit>().Initialize(view, entry, pose);
                DrawPart(view, new Placed { Entry = entry, Shape = pose });
                Vector2 titlePosition = compact ? new Vector2(6, -height + badgeHeight + captionHeight + 3) : new Vector2(70, -8);
                Vector2 titleSize = new Vector2(compact ? width - 18 : width - 85, captionHeight);
                var title = Label(card, "caption", caption, titlePosition, titleSize, compact ? 12 : 14,
                    compact ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft, true);
                title.color = new Color(.91f, .88f, .79f); title.fontStyle = FontStyles.Bold;
                for (int detail = 0; detail < details.Count; detail++)
                {
                    var text = Label(card, "detail_" + detail, details[detail],
                        compact ? new Vector2(6, -height + badgeHeight + 3 - detail * 18) : new Vector2(70, -40 - detail * 18),
                        new Vector2(compact ? width - 18 : width - 85, 19), 11,
                        compact ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft, false);
                    text.color = detail == 0 ? new Color(.70f, .75f, .72f) : new Color(.64f, .78f, .76f);
                }
            }
            return root;
        }

        private static Color CardColor(bool selected) => selected ? new Color(.18f, .34f, .34f, 1f) : new Color(.19f, .17f, .23f, .98f);

        private static List<string> CardDetails(Entry entry)
        {
            var details = new List<string>(3);
            foreach (string value in new[] { entry.SourceLabel, entry.StatusLabel, entry.QuoteLabel })
                if (!string.IsNullOrWhiteSpace(value) && !details.Contains(value)) details.Add(value);
            if (details.Count == 0)
                foreach (string value in new[] { entry.Badge, entry.Reason })
                    if (!string.IsNullOrWhiteSpace(value) && !details.Contains(value)) details.Add(value);
            return details;
        }

        private static TextMeshProUGUI Label(Transform parent, string name, string value, Vector2 position, Vector2 size, float fontSize, TextAlignmentOptions align, bool wrap)
        {
            RectTransform rect = NewRect(name, parent, position, size);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.text = value; text.fontSize = fontSize; text.alignment = align; text.richText = false;
            text.enableWordWrapping = wrap; text.enableAutoSizing = false;
            text.overflowMode = TextOverflowModes.Ellipsis; text.raycastTarget = false;
            ApplyLocaleFont(rect.gameObject);
            return text;
        }

        private static void ApplyLocaleFont(GameObject target)
        {
            // Configure before OnEnable so the native localizer changes only the font, not the supplied item text.
            bool active = target.activeSelf;
            target.SetActive(false);
            var localizer = target.AddComponent<TMPLocalizer>();
            localizer.ChangeFontOnly = true; localizer.fontRole = FontLoader.FontRole.Noto_Sans;
            target.SetActive(active);
        }

        private static List<Placed> Place(IEnumerable<Entry> source, GridShape board, int columns, ref int rows, RectInt reserved)
        {
            var result = new List<Placed>();
            var occupied = Reserve(board == null ? reserved : default);
            if (board == null) rows = Math.Max(rows, reserved.yMax);
            foreach (Entry entry in source ?? Enumerable.Empty<Entry>())
            {
                GridShape shape = entry?.Shape;
                if (entry?.Item == null || shape == null || shape.globalWidth < 1 || shape.globalHeight < 1) continue;
                if (board != null) { result.Add(new Placed { Entry = entry, Shape = shape }); continue; }
                GridShape pose = Pack(shape, columns, rows + shape.globalHeight, occupied, ref rows);
                if (pose != null) result.Add(new Placed { Entry = entry, Shape = pose });
            }
            return result;
        }

        // Fill each visible page using the same irregular-mask packing as the rendered grid.
        // An item larger than the viewport gets its own scrollable page.
        internal static List<List<Entry>> Paginate(IEnumerable<Entry> source, int columns, int visibleRows, RectInt reserved = default)
        {
            columns = Math.Max(1, columns); visibleRows = Math.Max(1, visibleRows);
            var remaining = (source ?? Enumerable.Empty<Entry>()).Where(entry => entry?.Item != null && entry.Shape != null &&
                entry.Shape.globalWidth > 0 && entry.Shape.globalHeight > 0).ToList();
            var pages = new List<List<Entry>>();
            while (remaining.Count > 0)
            {
                var page = new List<Entry>();
                GridShape first = remaining[0].Shape;
                if (first.globalWidth > columns || first.globalHeight > visibleRows)
                {
                    page.Add(remaining[0]); remaining.RemoveAt(0); pages.Add(page); continue;
                }
                var occupied = Reserve(reserved); int usedRows = 0;
                for (int i = 0; i < remaining.Count;)
                {
                    Entry entry = remaining[i];
                    if (Pack(entry.Shape, columns, visibleRows, occupied, ref usedRows) == null) { i++; continue; }
                    page.Add(entry); remaining.RemoveAt(i);
                }
                // A bay taller than the viewport must not prevent progress. The item remains
                // reachable below it using the same vertical scroll as the rest of the grid.
                if (page.Count == 0) { page.Add(remaining[0]); remaining.RemoveAt(0); }
                pages.Add(page);
            }
            if (pages.Count == 0) pages.Add(new List<Entry>());
            return pages;
        }

        internal static int PackedRows(IEnumerable<Entry> source, int columns, RectInt reserved = default, int rowLimit = int.MaxValue)
        {
            rowLimit = Math.Max(0, rowLimit);
            int rows = Math.Max(0, reserved.yMax);
            if (rows >= rowLimit) return rowLimit;
            var occupied = Reserve(reserved);
            foreach (Entry entry in source ?? Enumerable.Empty<Entry>())
            {
                GridShape shape = entry?.Shape;
                if (entry?.Item == null || shape == null || shape.globalWidth < 1 || shape.globalHeight < 1 || shape.globalWidth > columns) continue;
                int maxRows = shape.globalHeight > rowLimit - rows ? rowLimit : rows + shape.globalHeight;
                if (Pack(shape, columns, maxRows, occupied, ref rows) == null)
                {
                    if (rowLimit != int.MaxValue) return rowLimit;
                    continue;
                }
                if (rows >= rowLimit) return rowLimit;
            }
            return rows;
        }

        private static HashSet<long> Reserve(RectInt area)
        {
            var occupied = new HashSet<long>();
            for (int y = Math.Max(0, area.yMin); y < area.yMax; y++)
            for (int x = Math.Max(0, area.xMin); x < area.xMax; x++) occupied.Add(Key(x, y));
            return occupied;
        }

        private static GridShape Pack(GridShape shape, int columns, int maxRows, HashSet<long> occupied, ref int rows)
        {
            if (shape == null || shape.globalWidth > columns || shape.globalHeight > maxRows) return null;
            var cells = new List<(int x, int y)>();
            for (int py = 0; py < shape.globalHeight; py++) for (int px = 0; px < shape.globalWidth; px++)
                if (shape.Get(shape.minX + px, shape.minY + py) == 1) cells.Add((px, py));
            for (int y = 0; y + shape.globalHeight <= maxRows; y++)
            for (int x = 0; x + shape.globalWidth <= columns; x++)
            {
                bool clear = true;
                foreach (var cell in cells)
                    if (occupied.Contains(Key(x + cell.x, y + cell.y))) { clear = false; break; }
                if (!clear) continue;
                GridShape pose = new GridShapeBuilder(shape).SetPosition(x, y).Build();
                foreach (var cell in cells) occupied.Add(Key(x + cell.x, y + cell.y));
                rows = Math.Max(rows, y + pose.globalHeight); return pose;
            }
            return null;
        }

        private static long Key(int x, int y) => ((long)y << 32) | (uint)x;

        private static void DrawPart(View view, Placed part)
        {
            Entry entry = part.Entry;
            GridShape pose = part.Shape;
            float cell = view.Cell;
            var element = entry.Item as GameItemElement;
            for (int y = 0; y < pose.globalHeight; y++) for (int x = 0; x < pose.globalWidth; x++)
            {
                int px = pose.minX + x, py = pose.minY + y;
                if (pose.Get(px, py) != 1 || px < 0 || py < 0 || px >= view.Columns || py >= view.Rows) continue;
                RectTransform hitRect = NewRect("item_cell", view.Rect, new Vector2(px * cell, -py * cell), new Vector2(cell - 1, cell - 1));
                Image hitImage = hitRect.gameObject.AddComponent<Image>();
                hitImage.color = entry.Selected ? new Color(.24f, .62f, .78f, .85f) :
                    !entry.Enabled ? new Color(.4f, .18f, .18f, .55f) : new Color(.44f, .43f, .46f, .7f);
                view.Register(entry, hitImage, pose);
                hitRect.gameObject.AddComponent<Hit>().Initialize(view, entry, pose);
            }
            if (element?.background?.image?.sprite != null && element.rotator != null && UISettings.current != null && UISettings.current.gridSize > 0)
            {
                RectTransform item = NewRect("item_art", view.Rect, new Vector2(pose.minX * cell, -pose.minY * cell),
                    new Vector2(pose.width * cell, pose.height * cell));
                RectTransform rotator = NewRect("rotation", item, Vector2.zero, item.sizeDelta);
                Vector2 pivot = element.rotator.rectTransform.pivot;
                GridShape originalShape = element.modifiedShape;
                if (originalShape != null)
                {
                    if (originalShape.flipped != pose.flipped) pivot.x = 1f - pivot.x;
                    int turns = (pose.orientation - originalShape.orientation + 4) % 4;
                    for (int i = 0; i < turns; i++)
                        pivot = pose.flipped ? new Vector2(1f - pivot.y, pivot.x) : new Vector2(pivot.y, 1f - pivot.x);
                }
                rotator.pivot = pivot;
                rotator.localScale = new Vector3(pose.flipped ? -1 : 1, 1, 1);
                rotator.localEulerAngles = new Vector3(0, 0, 90f * pose.orientation);
                RectTransform original = element.background.rectTransform;
                RectTransform icon = NewRect("icon", rotator, original.anchoredPosition / UISettings.current.gridSize * cell,
                    original.sizeDelta / UISettings.current.gridSize * cell);
                icon.anchorMin = original.anchorMin; icon.anchorMax = original.anchorMax; icon.pivot = original.pivot;
                Image image = icon.gameObject.AddComponent<Image>();
                image.sprite = element.background.image.sprite; image.color = element.background.image.color;
                view.RegisterArt(entry, image, image.color);
                if (!entry.Enabled) image.color = new Color(image.color.r, image.color.g, image.color.b, image.color.a * .55f);
                image.raycastTarget = false;
            }
            if (entry.Item.unitCount > 1)
            {
                RectTransform count = NewRect("count", view.Rect,
                    new Vector2(pose.minX * cell, -(pose.minY + pose.globalHeight) * cell + 14),
                    new Vector2(Math.Max(20, pose.globalWidth * cell), 14));
                var text = count.gameObject.AddComponent<TextMeshProUGUI>();
                text.text = entry.Item.unitCount.ToString(); text.fontSize = 12; text.alignment = TextAlignmentOptions.BottomRight;
                text.color = Color.white; text.raycastTarget = false;
                ApplyLocaleFont(count.gameObject);
            }
            if (!string.IsNullOrEmpty(entry.Marker))
            {
                float width = Mathf.Min(pose.globalWidth * cell, Mathf.Max(22, entry.Marker.Length * 9));
                RectTransform marker = NewRect("status_marker", view.Rect,
                    new Vector2((pose.minX + pose.globalWidth) * cell - width, -pose.minY * cell), new Vector2(width, 20));
                Image background = marker.gameObject.AddComponent<Image>();
                background.color = new Color(.26f, .12f, .10f, .96f); background.raycastTarget = false;
                Label(marker, "status_text", entry.Marker, Vector2.zero, new Vector2(width, 20), 13, TextAlignmentOptions.Center, false);
            }
        }

        private static RectTransform NewRect(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = position; rect.sizeDelta = size;
            return rect;
        }

        private sealed class View : MonoBehaviour
        {
            internal RectTransform Rect;
            internal Options Options;
            internal float Cell;
            internal int Columns, Rows;
            internal Image CardBackground;
            private readonly Dictionary<Entry, List<Image>> itemCells = new Dictionary<Entry, List<Image>>();
            private readonly Dictionary<Entry, GridShape> itemShapes = new Dictionary<Entry, GridShape>();
            private Dictionary<Entry, (Image Image, Color Color)> itemArt;
            private Dictionary<Entry, View> cards;
            private readonly List<GameObject> preview = new List<GameObject>();
            private GridShape previousPreview;
            private bool previousAllowed, previewVisible;
            internal void Initialize(RectTransform rect, Options options, float cell, int columns, int rows)
            { Rect = rect; Options = options; Cell = cell; Columns = columns; Rows = rows; }
            internal void Register(Entry entry, Image image, GridShape shape)
            {
                if (!itemCells.TryGetValue(entry, out List<Image> cells)) itemCells[entry] = cells = new List<Image>();
                cells.Add(image);
                itemShapes[entry] = shape;
            }
            internal void RegisterCard(Entry entry, View view)
            {
                if (cards == null) cards = new Dictionary<Entry, View>();
                cards[entry] = view;
            }
            internal void RegisterArt(Entry entry, Image image, Color color)
            {
                if (itemArt == null) itemArt = new Dictionary<Entry, (Image, Color)>();
                itemArt[entry] = (image, color);
            }
            internal List<Entry> EntriesIn(Rect area)
            {
                var result = new List<Entry>();
                foreach (var pair in itemShapes)
                {
                    Entry entry = pair.Key; GridShape shape = pair.Value;
                    if (!entry.Enabled || !entry.Selectable) continue;
                    bool intersects = false;
                    for (int y = 0; y < shape.globalHeight && !intersects; y++)
                    for (int x = 0; x < shape.globalWidth && !intersects; x++)
                    {
                        int px = shape.minX + x, py = shape.minY + y;
                        if (shape.Get(px, py) != 1) continue;
                        var cell = new Rect(Rect.rect.xMin + px * Cell, Rect.rect.yMax - (py + 1) * Cell, Cell, Cell);
                        intersects = cell.Overlaps(area, true);
                    }
                    if (intersects) result.Add(entry);
                }
                return result;
            }
            internal void RefreshEntry(Entry entry)
            {
                if (entry == null) return;
                if (cards != null && cards.TryGetValue(entry, out View card))
                { if (card != null) card.RefreshEntry(entry); return; }
                if (itemCells.TryGetValue(entry, out List<Image> cells))
                    foreach (Image image in cells) if (image != null) image.color = entry.Selected ? new Color(.24f, .62f, .78f, .85f) :
                        !entry.Enabled ? new Color(.4f, .18f, .18f, .55f) : new Color(.44f, .43f, .46f, .7f);
                if (CardBackground != null) CardBackground.color = CardColor(entry.Selected);
                if (itemArt != null && itemArt.TryGetValue(entry, out var art) && art.Image != null)
                {
                    Color color = art.Color;
                    if (!entry.Enabled) color.a *= .55f;
                    art.Image.color = color;
                }
            }
            internal void ClearPreview()
            {
                foreach (GameObject cell in preview) if (cell != null) Destroy(cell);
                preview.Clear();
                previousPreview = null; previewVisible = false;
            }
            internal void ShowPreview(GridShape shape, bool allowed)
            {
                if (previewVisible && previousAllowed == allowed &&
                    (Options.Proxy || ItemGridMove.SameShape(previousPreview, shape))) return;
                ClearPreview();
                if (shape == null && !Options.Proxy) return;
                previousAllowed = allowed; previousPreview = shape?.Clone(); previewVisible = true;
                if (Options.Proxy)
                {
                    RectTransform overlay = NewRect("preview", Rect, Vector2.zero, Rect.rect.size);
                    Image image = overlay.gameObject.AddComponent<Image>();
                    image.color = allowed ? new Color(.15f, .85f, .25f, .3f) : new Color(.9f, .12f, .12f, .3f);
                    image.raycastTarget = false; preview.Add(overlay.gameObject); return;
                }
                for (int y = 0; y < shape.globalHeight; y++) for (int x = 0; x < shape.globalWidth; x++)
                {
                    int px = shape.minX + x, py = shape.minY + y;
                    if (px < 0 || py < 0 || px >= Columns || py >= Rows || shape.Get(px, py) != 1) continue;
                    RectTransform tile = NewRect("preview", Rect, new Vector2(px * Cell, -py * Cell), new Vector2(Cell - 1, Cell - 1));
                    Image image = tile.gameObject.AddComponent<Image>();
                    image.color = allowed ? new Color(.15f, .85f, .25f, .65f) : new Color(.9f, .12f, .12f, .65f);
                    image.raycastTarget = false; preview.Add(tile.gameObject);
                }
            }
        }

        private static Hit activeDrag;
        internal static bool DragActive => activeDrag != null || NativeDragActive();

        private static readonly HashSet<GridRoot> activeRoots = new HashSet<GridRoot>();
        private static readonly List<Hit> hoveredHits = new List<Hit>();
        private static GridInput input;

        private sealed class GridRoot : MonoBehaviour
        {
            private void OnEnable()
            {
                if (!activeRoots.Add(this) || input != null) return;
                var host = new GameObject("ShopAssist_ItemGridInput");
                DontDestroyOnLoad(host);
                input = host.AddComponent<GridInput>();
            }

            private void OnDisable()
            {
                if (!activeRoots.Remove(this) || activeRoots.Count != 0) return;
                var previous = input; input = null;
                if (previous != null) { previous.gameObject.SetActive(false); Destroy(previous.gameObject); }
                hoveredHits.Clear();
                if (nativePreview != null) nativePreview.ClearPreview();
                nativePreview = null;
                tooltipRaycast.Clear(); nativeGridRaycast.Clear(); viewRaycast.Clear();
            }
        }

        private sealed class GridInput : MonoBehaviour
        {
            private void Update()
            {
                Hit hovered = null;
                for (int i = hoveredHits.Count - 1; i >= 0; i--)
                {
                    Hit candidate = hoveredHits[i];
                    if (candidate == null || !candidate.isActiveAndEnabled || !candidate.hovered)
                    { hoveredHits.RemoveAt(i); continue; }
                    if (hovered == null || candidate.transform.IsChildOf(hovered.transform)) hovered = candidate;
                }
                hovered?.TickInput();
                if (activeDrag != null && !ReferenceEquals(activeDrag, hovered)) activeDrag.TickInput();
            }
        }

        private sealed class Hit : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler,
            IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            private View view;
            private Entry entry;
            internal bool hovered;
            private Vector2 grab;
            private GridShape shownShape;
            private GridShape dragShape;
            private GameObject ghost;
            private View previewTarget;
            private GameInventory original;
            private int originalCount;
            private GridShape originalShape;
            private RectTransform marquee;
            private Vector2 marqueeStart;
            private float tooltipRefresh;
            private RichText tooltip;

            internal void Initialize(View owner, Entry value, GridShape pose) { view = owner; entry = value; shownShape = pose; }
            internal static View At(GameObject target) => target?.GetComponentInParent<Hit>()?.view;
            internal bool HasTooltipHover => hovered && ghost == null && entry?.Item is GameItemElement item && !item.IsDestroyed();
            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Left || entry?.Enabled != true || !entry.Selectable || !Valid()) return;
                view.Options.OnClick?.Invoke(entry);
                if (view != null && view.gameObject != null) view.RefreshEntry(entry);
            }
            public void OnPointerEnter(PointerEventData eventData)
            {
                hovered = true;
                if (entry != null && !hoveredHits.Contains(this)) hoveredHits.Add(this);
            }
            public void OnPointerExit(PointerEventData eventData)
            {
                hovered = false; hoveredHits.Remove(this); HideOwnedTooltip(this);
            }
            internal void TickInput()
            {
                bool dragging = DragActive;
                if (dragging) HideOwnedTooltip(this);
                if (!dragging && hovered && ghost == null && entry?.Item is GameItemElement item && !item.IsDestroyed() && OverlayHandler.current != null)
                {
                    if (item.HasTooltip())
                    {
                        if (tooltip == null || Time.unscaledTime >= tooltipRefresh)
                        { tooltip = Tooltip(entry, item); tooltipRefresh = Time.unscaledTime + .25f; }
                        ShowOwnedTooltip(this, tooltip);
                    }
                }
                if (ghost == null) return;
                var handler = ItemMouseDragHandler.current;
                bool cw = handler != null && Input.GetKeyDown(handler.rotateCWKey);
                bool ccw = handler != null && Input.GetKeyDown(handler.rotateCCWKey);
                float scroll = Input.mouseScrollDelta.y;
                if (cw || ccw || scroll != 0)
                {
                    int turn = cw || scroll < 0 ? 3 : 1;
                    RotateDrag(turn);
                }
                var rect = ghost.transform as RectTransform;
                if (rect?.parent is RectTransform parent && RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, Input.mousePosition,
                    parent.GetComponentInParent<Canvas>()?.worldCamera, out Vector2 point)) rect.anchoredPosition = point;
            }
            public void OnBeginDrag(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Left && entry == null && view.Options.OnMarquee != null &&
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(view.Rect, eventData.position, eventData.pressEventCamera, out marqueeStart))
                {
                    marquee = NewRect("selection", view.Rect, Vector2.zero, Vector2.zero);
                    var image = marquee.gameObject.AddComponent<Image>();
                    image.color = new Color(.25f, .7f, .95f, .3f); image.raycastTarget = false;
                    activeDrag = this; return;
                }
                if (eventData.button != PointerEventData.InputButton.Left || entry?.Enabled != true || !Valid() ||
                    view.Options.CanDrop == null || view.Options.OnDrop == null || entry.Shape == null) return;
                original = entry.Item.parentInventory; originalCount = entry.Item.unitCount;
                originalShape = entry.Item.modifiedShape?.Clone();
                dragShape = entry.Shape.Clone();
                RectTransformUtility.ScreenPointToLocalPointInRectangle(view.Rect, eventData.position, eventData.pressEventCamera, out Vector2 local);
                int x = Mathf.FloorToInt((local.x - view.Rect.rect.xMin) / view.Cell);
                int y = Mathf.FloorToInt((view.Rect.rect.yMax - local.y) / view.Cell);
                grab = new Vector2(Mathf.Clamp(x - shownShape.minX, 0, shownShape.globalWidth - 1),
                    Mathf.Clamp(y - shownShape.minY, 0, shownShape.globalHeight - 1));
                RectTransform layer = CustomUIManager.Instance?.GetLayerRoot("overlay");
                if (layer == null) return;
                RectTransform root = NewRect("ShopAssist_ItemGridDrag", layer, Vector2.zero, Vector2.one);
                root.anchorMin = root.anchorMax = layer.pivot;
                root.gameObject.AddComponent<CanvasGroup>().blocksRaycasts = false;
                ghost = root.gameObject; activeDrag = this; UpdateGhost();
                HideOwnedTooltip(this);
            }
            public void OnDrag(PointerEventData eventData)
            {
                if (marquee != null) { UpdateMarquee(eventData); return; }
                if (ghost == null) return;
                View target = At(eventData.pointerCurrentRaycast.gameObject);
                if (previewTarget != null && previewTarget != target) previewTarget.ClearPreview();
                previewTarget = target;
                if (target?.Options.Inventory == null) return;
                GridShape pose = TargetPose(target, entry.Item, eventData.position, eventData.pressEventCamera, dragShape, grab);
                bool allowed = pose != null && Valid() && entry.Item.parentInventory == original && entry.Item.unitCount == originalCount &&
                    ItemGridMove.SameShape(entry.Item.modifiedShape, originalShape) &&
                    view.Options.CanDrop(entry, target.Options.Inventory, pose);
                target.ShowPreview(pose, allowed);
            }
            public void OnEndDrag(PointerEventData eventData)
            {
                if (marquee != null)
                {
                    try
                    {
                        Rect area = UpdateMarquee(eventData);
                        List<Entry> found = view.EntriesIn(area);
                        view.Options.OnMarquee?.Invoke(found);
                        foreach (Entry item in found) view.RefreshEntry(item);
                    }
                    finally { EndGhost(); }
                    return;
                }
                if (ghost == null) return;
                try
                {
                    if (!Valid() || entry.Item.parentInventory != original || entry.Item.unitCount != originalCount ||
                        !ItemGridMove.SameShape(entry.Item.modifiedShape, originalShape)) return;
                    View target = At(eventData.pointerCurrentRaycast.gameObject);
                    GameGridInventory inventory = target?.Options.Inventory;
                    GridShape pose = null;
                    if (target != null && inventory != null)
                        pose = TargetPose(target, entry.Item, eventData.position, eventData.pressEventCamera, dragShape, grab);
                    else
                    {
                        inventory = NativeGridAtPointer(eventData.position);
                        if (inventory != null)
                            pose = NativePoseAt(inventory, eventData.position, dragShape, grab);
                    }
                    if (inventory != null)
                    {
                        if (pose != null && view.Options.CanDrop(entry, inventory, pose)) view.Options.OnDrop(entry, inventory, pose);
                        else view.Options.OnReject?.Invoke(entry, inventory, pose);
                    }
                }
                finally { EndGhost(); }
            }
            private bool Valid() => entry?.Item != null && entry.Item.parentInventory == entry.Source && entry.Item.unitCount > 0 &&
                (!(entry.Item is GameItemElement item) || !item.IsDestroyed());
            private Rect UpdateMarquee(PointerEventData eventData)
            {
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(view.Rect, eventData.position, eventData.pressEventCamera, out Vector2 end))
                    end = marqueeStart;
                float x = Mathf.Min(marqueeStart.x, end.x), y = Mathf.Min(marqueeStart.y, end.y);
                float width = Mathf.Abs(marqueeStart.x - end.x), height = Mathf.Abs(marqueeStart.y - end.y);
                marquee.anchoredPosition = new Vector2(x - view.Rect.rect.xMin, y + height - view.Rect.rect.yMax);
                marquee.sizeDelta = new Vector2(width, height);
                return new Rect(x, y, width, height);
            }
            private void RotateDrag(int turn)
            {
                if (ghost == null || dragShape == null) return;
                (int x, int y) = GroupGeometry.RotateGrab(dragShape, (int)grab.x, (int)grab.y, turn == 1);
                grab = new Vector2(x, y);
                dragShape = new GridShapeBuilder(dragShape).SetOrientation((dragShape.orientation + turn) % 4).Build();
                UpdateGhost();
            }
            private void UpdateGhost()
            {
                if (ghost == null) return;
                for (int i = ghost.transform.childCount - 1; i >= 0; i--) Destroy(ghost.transform.GetChild(i).gameObject);
                Canvas sourceCanvas = view.Rect.GetComponentInParent<Canvas>();
                Canvas ghostCanvas = ghost.GetComponentInParent<Canvas>();
                float scale = ghostCanvas != null && ghostCanvas.scaleFactor > 0 && sourceCanvas != null
                    ? sourceCanvas.scaleFactor / ghostCanvas.scaleFactor : 1f;
                float cell = view.Cell * scale;
                RectTransform rect = NewRect("ghost_grid", ghost.transform,
                    new Vector2(-(grab.x + .5f) * cell, (grab.y + .5f) * cell),
                    new Vector2(dragShape.globalWidth * cell, dragShape.globalHeight * cell));
                var ghostView = rect.gameObject.AddComponent<View>();
                ghostView.Initialize(rect, view.Options, cell, dragShape.globalWidth, dragShape.globalHeight);
                DrawPart(ghostView, new Placed { Entry = entry, Shape = new GridShapeBuilder(dragShape).SetPosition(0, 0).Build() });
            }
            private void EndGhost()
            {
                if (previewTarget != null) previewTarget.ClearPreview(); previewTarget = null;
                if (ghost != null) Destroy(ghost); ghost = null; dragShape = null; originalShape = null;
                if (marquee != null) Destroy(marquee.gameObject); marquee = null;
                if (activeDrag == this) activeDrag = null;
            }
            private void OnDisable()
            {
                EndGhost(); hovered = false; hoveredHits.Remove(this); HideOwnedTooltip(this);
            }
        }

        private static Hit tooltipOwner;
        private static bool showingOwnedTooltip;
        private sealed class RaycastCache
        {
            private EventSystem system;
            private PointerEventData pointer;
            private readonly List<RaycastResult> hits = new List<RaycastResult>();

            internal List<RaycastResult> At(EventSystem current, Vector2 position)
            {
                if (system != current) { system = current; pointer = new PointerEventData(current); }
                pointer.Reset(); pointer.position = position;
                hits.Clear(); current.RaycastAll(pointer, hits);
                return hits;
            }

            internal void Clear() { hits.Clear(); pointer = null; system = null; }
        }
        private static readonly RaycastCache tooltipRaycast = new RaycastCache();
        private static readonly RaycastCache nativeGridRaycast = new RaycastCache();
        private static readonly RaycastCache viewRaycast = new RaycastCache();
        private static bool NativeDragActive() => ItemMouseDragHandler.current?.IsDraggingItem == true ||
            ItemMultiSelectHandler.current?.IsGroupDragging == true;

        private static void ShowOwnedTooltip(Hit owner, RichText tooltip)
        {
            var overlay = OverlayHandler.current;
            if (owner == null || tooltip == null || overlay == null || !overlay.isActiveAndEnabled || NativeDragActive()) return;
            try
            {
                showingOwnedTooltip = true;
                overlay.ShowTooltip(Input.mousePosition, new Vector2(24, 0), tooltip);
                tooltipOwner = owner;
            }
            finally { showingOwnedTooltip = false; }
        }

        private static void HideOwnedTooltip(Hit owner)
        {
            if (!ReferenceEquals(tooltipOwner, owner)) return;
            tooltipOwner = null;
            var overlay = OverlayHandler.current;
            var window = overlay != null ? overlay.GetTooltipWindow() : null;
            // On scene unload the singleton can outlive its rendered window and window handler.
            if (overlay == null || !overlay.isActiveAndEnabled || window?.handler == null ||
                window.handler.gameObject == null || WindowsHandler.current == null) return;
            overlay.HideTooltip();
        }

        private static void HideTooltipForDrag()
        {
            Hit owner = tooltipOwner;
            if (!ReferenceEquals(owner, null)) HideOwnedTooltip(owner);
        }

        [HarmonyPatch(typeof(ItemTooltipHandler), "OnEventUpdate")]
        private static class PreserveOwnedTooltipPatch
        {
            private static bool Prefix(ref bool __result)
            {
                Hit owner = tooltipOwner;
                if (owner == null || !owner.isActiveAndEnabled || !owner.HasTooltipHover ||
                    DragActive || UIDragHandler.isDraggingAnyUI || EventSystem.current == null) return true;
                var hits = tooltipRaycast.At(EventSystem.current, Input.mousePosition);
                if (hits.Count == 0 || hits[0].gameObject == null) return true;
                GameObject first = hits[0].gameObject;
                if (first.GetComponentInParent<WindowInput>() == null || first.GetComponentInParent<Hit>() != owner) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(ItemMouseDragHandler), "StartDrag")]
        private static class NativeDragTooltipPatch
        {
            private static void Postfix(ItemMouseDragHandler __instance) { if (__instance.IsDraggingItem) HideTooltipForDrag(); }
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "StartGroupDrag")]
        private static class GroupDragTooltipPatch
        {
            private static void Postfix(ItemMultiSelectHandler __instance) { if (__instance.IsGroupDragging) HideTooltipForDrag(); }
        }

        // Native and other-mod tooltip updates take ownership away from the grid.
        [HarmonyPatch(typeof(OverlayHandler), nameof(OverlayHandler.ShowTooltip), new Type[] { typeof(Vector2), typeof(Vector2), typeof(RichText), typeof(int) })]
        private static class NativeTooltipPatch
        {
            private static void Prefix() { if (!showingOwnedTooltip) tooltipOwner = null; }
        }

        [HarmonyPatch(typeof(OverlayHandler), nameof(OverlayHandler.ShowTooltip2))]
        private static class AlternateTooltipPatch
        {
            private static void Prefix() { if (!showingOwnedTooltip) tooltipOwner = null; }
        }

        [HarmonyPatch(typeof(OverlayHandler), nameof(OverlayHandler.HideTooltip))]
        private static class TooltipHiddenPatch
        {
            private static void Prefix() { tooltipOwner = null; }
        }

        private static RichText Tooltip(Entry entry, GameItemElement item)
        {
            RichText native = item.GetTooltip();
            if (native == null) return null;
            var extra = new RichTextBuilder(60);
            if (!string.IsNullOrWhiteSpace(entry.Caption) && entry.Caption != item.GetDisplayName(true)) extra.AddLine(Clean(entry.Caption));
            foreach (string detail in CardDetails(entry)) extra.AddLine(Clean(detail));
            RichText suffix = extra.Build();
            if (string.IsNullOrWhiteSpace(suffix.nakedText)) return native;
            return new RichText(native.nakedText + "\n" + suffix.nakedText,
                native.richTextStrikethrough + "\n" + suffix.richTextStrikethrough,
                native.richTextUnderline + "\n" + suffix.richTextUnderline,
                native.richTextForeground + "\n" + suffix.richTextForeground,
                native.richTextBackground + "\n" + suffix.richTextBackground,
                Math.Max(native.recommendedWidth, suffix.recommendedWidth), native.recommendedHeight + suffix.recommendedHeight,
                native.isTruncated || suffix.isTruncated);
        }

        private static string Clean(string value) => value.Replace("<", "‹").Replace(">", "›");

        private static GridShape PoseAt(View view, Vector2 point, Camera camera, GridShape shape, Vector2 grab)
        {
            if (view == null || shape == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(view.Rect, point, camera, out Vector2 local)) return null;
            int x = Mathf.FloorToInt((local.x - view.Rect.rect.xMin) / view.Cell - grab.x);
            int y = Mathf.FloorToInt((view.Rect.rect.yMax - local.y) / view.Cell - grab.y);
            return new GridShapeBuilder(shape).SetPosition(x, y).Build();
        }

        private static GridShape NativePoseAt(GameGridInventory inventory, Vector2 point, GridShape shape, Vector2 grab)
        {
            RectTransform rect = inventory?.rectTransform;
            if (rect == null || shape == null || UISettings.current == null || UISettings.current.gridSize <= 0) return null;
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, point, camera, out Vector2 local)) return null;
            float cell = UISettings.current.gridSize;
            int x = Mathf.FloorToInt((local.x - rect.rect.xMin) / cell - grab.x);
            int y = Mathf.FloorToInt((rect.rect.yMax - local.y) / cell - grab.y);
            return new GridShapeBuilder(shape).SetPosition(x, y).Build();
        }

        private static GridShape TargetPose(View view, GameItem item, Vector2 point, Camera camera, GridShape shape, Vector2 grab)
        {
            if (view?.Options.Proxy == true) return GridPlacements.Empty(view.Options.Inventory, item)?.itemGridShape;
            return PoseAt(view, point, camera, shape, grab);
        }

        private static GameGridInventory NativeGridAtPointer(Vector2 point)
        {
            if (EventSystem.current == null) return null;
            var hits = nativeGridRaycast.At(EventSystem.current, point);
            foreach (RaycastResult hit in hits)
            {
                if (hit.gameObject == null) continue;
                TreeNodeRender node = hit.gameObject.GetComponent<TreeNodeRender>();
                return node == null ? null : node.FindParentElement<GameGridInventory>();
            }
            return null;
        }

        private static View ViewAtPointer(GameItemElement dragging)
        {
            if (EventSystem.current == null) return null;
            var hits = viewRaycast.At(EventSystem.current, Input.mousePosition);
            foreach (RaycastResult hit in hits)
            {
                if (hit.gameObject == null) continue;
                if (dragging?.fakeMiddle != null && hit.gameObject.transform.IsChildOf(dragging.fakeMiddle.transform)) continue;
                View view = Hit.At(hit.gameObject);
                // The first actual UI hit owns the pointer; do not look through another window.
                return view;
            }
            return null;
        }

        private static View nativePreview;
        private static float nextNativePreview;

        [HarmonyPatch(typeof(ItemMouseDragHandler), "OnEventUpdate")]
        private static class NativePreviewPatch
        {
            private static void Postfix(ItemMouseDragHandler __instance, GameItemElement ___currentItem, int ___currentRemove)
            {
                if (activeRoots.Count == 0) return;
                View target = __instance.IsDraggingItem ? ViewAtPointer(___currentItem) : null;
                if (nativePreview != null && nativePreview != target) nativePreview.ClearPreview();
                if (nativePreview == target && Time.unscaledTime < nextNativePreview) return;
                nativePreview = target;
                nextNativePreview = Time.unscaledTime + .08f;
                if (target == null || target.Options.Inventory == null || ___currentItem == null || target.Options.CanDrop == null) return;
                try
                {
                    Canvas canvas = target.Rect.GetComponentInParent<Canvas>();
                    Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    var entry = new Entry(___currentItem, ___currentItem.parentInventory, ___currentItem.tempShape);
                    GridShape shape = TargetPose(target, ___currentItem, Input.mousePosition, camera, ___currentItem.tempShape,
                        new Vector2(___currentItem.tempShape.globalWidth / 2f, ___currentItem.tempShape.globalHeight / 2f));
                    bool allowed = ___currentRemove == ___currentItem.unitCount && shape != null &&
                        target.Options.CanDrop(entry, target.Options.Inventory, shape);
                    target.ShowPreview(shape, allowed);
                }
                catch (Exception error) { Debug.LogWarning("[ShopAssist] Grid preview failed: " + error); target.ClearPreview(); }
            }
        }

        [HarmonyPatch(typeof(ItemMouseDragHandler), "EndDrag")]
        private static class NativeDropPatch
        {
            private static bool Prefix(GameItemElement ___currentItem, int ___currentRemove, ref bool ___isDragging,
                List<TreeNodeRender> ___highlightedMayNodes, HighlightManager ___highlightManager)
            {
                if (activeRoots.Count == 0) return true;
                View target = ViewAtPointer(___currentItem);
                if (target == null || ___currentItem == null) return true;
                try
                {
                    GameItemElement item = ___currentItem;
                    var entry = new Entry(item, item.parentInventory, item.tempShape);
                    Canvas canvas = target.Rect.GetComponentInParent<Canvas>();
                    Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    GridShape pose = TargetPose(target, item, Input.mousePosition, camera, item.tempShape,
                        new Vector2(item.tempShape.globalWidth / 2f, item.tempShape.globalHeight / 2f));
                    if (target.Options.Inventory != null && target.Options.CanDrop != null && target.Options.OnDrop != null)
                    {
                        if (___currentRemove == item.unitCount && pose != null && target.Options.CanDrop(entry, target.Options.Inventory, pose))
                            target.Options.OnDrop(entry, target.Options.Inventory, pose);
                        else target.Options.OnReject?.Invoke(entry, target.Options.Inventory, pose);
                    }
                }
                catch (Exception error) { Debug.LogError("[ShopAssist] Grid drop failed: " + error); }
                finally
                {
                    target.ClearPreview(); nativePreview = null; MachineryTabs.ClearDragHover();
                    foreach (TreeNodeRender node in ___highlightedMayNodes) if (node?.image != null) node.image.enabled = false;
                    ___highlightManager?.Clear();
                    ___currentItem.ToggleFakeBackground(false); ___currentItem.Validate();
                    OverlayHandler.current?.HideTooltip(); ___isDragging = false;
                }
                return false;
            }
        }
    }
}
