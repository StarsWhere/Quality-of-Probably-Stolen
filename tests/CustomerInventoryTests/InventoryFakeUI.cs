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
    public void BringToFront() { }
    public void Close() { if (!IsAlive) return; IsAlive = false; Closed?.Invoke(); }
}

public sealed class CustomUIBuilder
{
    private readonly CustomUIWindow window = new CustomUIWindow();
    public FakeElement Last = new FakeElement();
    public CustomUIBuilder SetSize(int x, int y) => this;
    public CustomUIBuilder SetDraggable(bool value) => this;
    public CustomUIBuilder SetCloseOnEscape(bool value) => this;
    public CustomUIWindow GetWindow() => window;
    public CustomUIBuilder OnClose(Action action) { window.Closed = action; return this; }
    public CustomUIBuilder BeginRow() => this;
    public CustomUIBuilder AddLabel(string value) => this;
    public CustomUIBuilder AddFlexibleSpace() => this;
    public CustomUIBuilder AddButton(string value, Action action, string id = null) => this;
    public CustomUIBuilder End() => this;
    public CustomUIBuilder BeginScrollFlexible(int value) => this;
    public void Show() { }
}

public sealed class FakeElement
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
            internal string Caption, Badge, Reason;
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
        internal static void Add(CustomUIBuilder builder, IEnumerable<Entry> entries, Options options)
        {
            LastEntries = entries.ToList(); LastOptions = options;
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
