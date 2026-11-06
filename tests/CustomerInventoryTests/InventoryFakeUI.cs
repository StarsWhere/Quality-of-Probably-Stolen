using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public sealed class ItemMouseDoubleClickHandler
{
    public static ItemMouseDoubleClickHandler current;
    public int openCount;
    public Action<GameItem> beforeOpen;
    public void OpenContentAction(GameItem host, Vector2 position)
    {
        openCount++;
        beforeOpen?.Invoke(host);
        host.contentWindow.Show();
    }
}

public sealed class CustomUIWindow
{
    public bool IsAlive = true;
    public GameObject root = new GameObject();
    internal Action Closed;
    private readonly Dictionary<string, Action> actions = new Dictionary<string, Action>();
    private readonly Dictionary<string, CustomUIElement> elements = new Dictionary<string, CustomUIElement>();
    public RectTransform ContentRoot => (RectTransform)root.transform;
    public RectTransform Rect => (RectTransform)root.transform;
    public void SetSize(float width, float height) => Rect.sizeDelta = new Vector2(width, height);
    internal void AddAction(string id, Action action) { if (id != null) actions[id] = action; }
    internal void Click(string id) => actions[id]();
    public void BringToFront() { }
    public void Close() { if (!IsAlive) return; IsAlive = false; Closed?.Invoke(); }
}

public sealed class CustomUIBuilder
{
    private readonly CustomUIWindow window;
    public RectTransform CurrentContainer;
    public CustomUIBuilder() { window = new CustomUIWindow(); CurrentContainer = window.ContentRoot; }
    public CustomUIBuilder(CustomUIManager manager, CustomUIWindow window, RectTransform parent) { this.window = window; CurrentContainer = parent; }
    public CustomUIElement Last = new CustomUIElement();
    public static void ClearContainer(RectTransform parent)
    {
        while (parent.childCount != 0) UnityEngine.Object.Destroy(parent.GetChild(0).gameObject);
    }
    public static void RebuildLayout(RectTransform parent) { }
    public CustomUIBuilder SetSize(float x, float y) { window.SetSize(x, y); return this; }
    public CustomUIBuilder SetDraggable(bool value) => this;
    public CustomUIBuilder SetCloseOnEscape(bool value) => this;
    public CustomUIWindow GetWindow() => window;
    public CustomUIBuilder OnClose(Action action) { window.Closed = action; return this; }
    public CustomUIBuilder BeginRow() => this;
    public CustomUIBuilder AddLabel(string value) => this;
    public CustomUIBuilder AddFlexibleSpace() => this;
    public CustomUIBuilder AddButton(string value, Action action, string id = null) { window.AddAction(id, action); return this; }
    public CustomUIBuilder End() { CurrentContainer = window.ContentRoot; return this; }
    public CustomUIBuilder BeginScrollFlexible(int value)
    {
        var viewport = new GameObject("scroll"); viewport.transform.SetParent(CurrentContainer);
        viewport.Add(new UnityEngine.UI.ScrollRect());
        var body = new GameObject("scroll content"); body.transform.SetParent(viewport.transform);
        CurrentContainer = body.transform; return this;
    }
    public void Show() { }
}

public sealed class CustomUIElement
{
    public TMPro.TMP_Text text;
    public void SetFlexible(int width, int height) { }
}

public sealed class CustomUIManager
{
    public const int LAYER_META = 0;
    public static CustomUIManager Instance = new CustomUIManager();
    public static CustomUIWindow LastWindow;
    public static CustomUIBuilder Window(string id, string title, int layer)
    {
        var builder = new CustomUIBuilder(); LastWindow = builder.GetWindow(); return builder;
    }
}

public static class StoreUIManager { public static bool IsAnyTyping() => false; }

namespace ShopAssist
{
    internal static class ShopInput { internal static bool Ready() => true; }
    internal static class ShopNotice
    {
        internal static string Last;
        internal static void Show(string value) { Last = value; }
    }
    internal static class RequestBook
    {
        internal static readonly HashSet<GameItem> Reservations = new HashSet<GameItem>();
        internal static HashSet<GameItem> ReservedItems(IEnumerable<ShopInventory.Location> locations) => new HashSet<GameItem>(Reservations);
        internal static bool Reserved(GameItem item) => Reservations.Contains(item);
    }
    // Only captures the production view's inputs and click callback. Real card
    // layout and drag behavior are covered by the native-player scenarios.
    internal static class ItemGrid
    {
        internal sealed class Entry
        {
            internal readonly GameItem Item;
            internal string Caption, SourceLabel, StatusLabel, QuoteLabel;
            internal Entry(GameItem item) { Item = item; }
        }
        internal sealed class Options
        {
            internal bool Cards;
            internal int Columns;
            internal float CardWidth, CardHeight;
            internal Action<Entry> OnClick;
            internal Func<Entry, GameGridInventory, GridShape, bool> CanDrop;
            internal Action<Entry, GameGridInventory, GridShape> OnDrop;
        }
        internal static List<Entry> LastEntries = new List<Entry>();
        internal static Options LastOptions;
        internal static int AddCount;
        internal static bool DragActive = false;
        internal static void Add(CustomUIBuilder builder, IEnumerable<Entry> entries, Options options)
        {
            LastEntries = entries.ToList(); LastOptions = options; AddCount++;
        }
    }
    internal static class UiLayout
    {
        internal static int Page, Pages;
        internal static Action Previous, Next;
        internal static Vector2 Fit(float width, float height) => new Vector2(width, height);
        internal static void RestoreScroll(Transform content, float position)
        {
            var scroll = content?.GetComponentInParent<UnityEngine.UI.ScrollRect>();
            if (scroll != null) scroll.verticalNormalizedPosition = Math.Max(0, Math.Min(1, position));
        }
        internal static void Pager(CustomUIBuilder builder, int page, int count, int size, Action<int> change)
        {
            Pages = Math.Max(1, (count + size - 1) / size);
            Page = Math.Max(0, Math.Min(page, Pages - 1));
            int current = Page;
            Previous = Pages > 1 && current > 0 ? () => change(current - 1) : null;
            Next = Pages > 1 && current + 1 < Pages ? () => change(current + 1) : null;
        }
    }
    internal static class ItemGridMove
    {
        internal static bool CanMove(GameItem item, GameGridInventory target, GridShape shape) =>
            throw new NotSupportedException("Native inventory movement is outside these path tests.");
        internal static bool TryMove(GameItem item, GameGridInventory target, GridShape shape, out bool restored)
        {
            restored = false;
            throw new NotSupportedException("Native inventory movement is outside these path tests.");
        }
    }
}
