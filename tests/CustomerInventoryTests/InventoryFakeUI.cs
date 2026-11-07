using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityEngine
{
    public sealed class CanvasGroup : Component { public float alpha = 1; public bool blocksRaycasts = true; }
}

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
    private readonly Dictionary<string, Action<string>> inputs = new Dictionary<string, Action<string>>();
    private readonly Dictionary<string, CustomUIElement> elements = new Dictionary<string, CustomUIElement>();
    public RectTransform ContentRoot => (RectTransform)root.transform;
    public RectTransform Rect => (RectTransform)root.transform;
    public void SetSize(float width, float height) => Rect.sizeDelta = new Vector2(width, height);
    public void SetTitle(string title) { }
    internal void AddAction(string id, Action action) { if (id != null) actions[id] = action; }
    internal void AddInput(string id, Action<string> action) { if (id != null) inputs[id] = action; }
    internal void Click(string id) => actions[id]();
    internal void SetInput(string id, string value) => inputs[id](value);
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
    public CustomUIBuilder AddInput(string label, string value, Action<string> changed, string id, Action<string> ended)
    { window.AddInput(id, changed); return this; }
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
    internal static class FakeGameExtensions
    {
        internal static T AddComponent<T>(this GameObject value) where T : Component, new() => value.Add(new T());
        internal static void GetWorldCorners(this RectTransform rect, Vector3[] corners)
        {
            float x = rect.position.x, y = rect.position.y, width = rect.sizeDelta.x, height = rect.sizeDelta.y;
            corners[0] = new Vector3(x - width / 2, y - height / 2);
            corners[1] = new Vector3(x - width / 2, y + height / 2);
            corners[2] = new Vector3(x + width / 2, y + height / 2);
            corners[3] = new Vector3(x + width / 2, y - height / 2);
        }
        internal static string Key(this ShopInventory.Location location, bool allocate = false) => location?.Role ?? "stock";
    }
    internal static class InventoryLockFake
    {
        internal static readonly HashSet<GameInventory> Locked = new HashSet<GameInventory>();
        internal static bool IsRemoveLocked(this GameInventory inventory) => Locked.Contains(inventory);
        internal static bool IsInsertLocked(this GameInventory inventory) => Locked.Contains(inventory);
    }
    // A controllable price source; native record reading is checked in Player QA.
    internal static class ShopLedger
    {
        internal static readonly Dictionary<GameItem, int> Prices = new Dictionary<GameItem, int>();
        internal static int? PurchasePrice(GameItem item) => Prices.TryGetValue(item, out int price) ? price : (int?)null;
    }
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
            internal bool Draggable = true;
            internal string Caption, SourceLabel, StatusLabel, QuoteLabel, PurchaseLabel, TooltipHint;
            internal Entry(GameItem item) { Item = item; }
        }
        internal sealed class Options
        {
            internal bool Cards, RequireSourceShape;
            internal int Columns;
            internal float CardWidth, CardHeight;
            internal Action<Entry> OnClick;
            internal Func<Entry, GameInventory, GameItem, GridShape, bool> CanDropTarget;
            internal Action<Entry, GameInventory, GameItem, GridShape> OnDropTarget, OnRejectTarget;
            internal Action OnDragStarted, OnDragEnded;
            internal Func<Entry, GameGridInventory, GridShape, bool> CanDrop => (entry, target, shape) => CanDropTarget(entry, target, null, shape);
            internal Action<Entry, GameGridInventory, GridShape> OnDrop => (entry, target, shape) => OnDropTarget(entry, target, null, shape);
            internal Action<Entry, GameGridInventory, GridShape> OnReject => (entry, target, shape) => OnRejectTarget(entry, target, null, shape);
        }
        internal static List<Entry> LastEntries = new List<Entry>();
        internal static Options LastOptions;
        internal static int AddCount;
        internal static bool DragActive = false;
        internal static void CancelDragWithin(GameObject root)
        { if (DragActive) { DragActive = false; LastOptions?.OnDragEnded?.Invoke(); } }
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
        internal static void Clear(Transform root, CustomUIWindow window = null)
        { if (root is RectTransform rect) CustomUIBuilder.ClearContainer(rect); }
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
        internal static bool Allowed = true, RestoreOnFailure = true;
        internal static int Attempts;
        internal static bool CanMove(GameItem item, GameGridInventory target, GridShape shape) => Allowed && shape != null;
        internal static bool TryMove(GameItem item, GameGridInventory target, GridShape shape, out bool restored)
        {
            Attempts++;
            restored = RestoreOnFailure;
            if (!Allowed) return false;
            item.parentInventory.childItems.Remove(item);
            item.parentInventory = target;
            target.childItems.Add(item);
            return true;
        }
    }

    // This fake exercises the view's source/target decisions; the actual journal
    // and interruption behavior are exercised by RoutingPlanTests.
    internal static class WholeStackTransfer
    {
        internal enum Result { Applied, Unchanged, Pending, Unavailable }
        internal sealed class Plan { internal GameItem Item, Stack; internal GameInventory Target; }
        internal static bool Ready = true;
        internal static readonly HashSet<GameItem> Unsafe = new HashSet<GameItem>();
        internal static void Acquire() { }
        internal static void Release() { }
        internal static bool MutationsSafe = true;
        internal static int MutationChecks, InventoryChecks;
        internal static bool SafeSource(GameItem item) { MutationChecks++; return item != null && MutationsSafe && !Unsafe.Contains(item); }
        internal sealed class SourceSafetySnapshot
        {
            private readonly bool mutationsSafe;
            private readonly HashSet<GameInventory> inventories = new HashSet<GameInventory>();
            internal SourceSafetySnapshot() { MutationChecks++; mutationsSafe = MutationsSafe; }
            internal bool Allows(GameItem item)
            {
                if (item == null || !mutationsSafe || Unsafe.Contains(item) || item.parentInventory == null) return false;
                if (inventories.Add(item.parentInventory)) InventoryChecks++;
                return true;
            }
        }
        internal static Plan ExactEmpty(GameItem item, GameGridInventory target, GridShape shape) =>
            SafeSource(item) && ItemGridMove.CanMove(item, target, shape) ? new Plan { Item = item, Target = target } : null;
        internal static Plan ExactStack(GameItem item, GameInventory target, GameItem resident) =>
            SafeSource(item) && ItemGridMove.Allowed && !ReferenceEquals(item, resident) && ReferenceEquals(resident?.parentInventory, target) &&
            !RequestBook.Reserved(resident) ? new Plan { Item = item, Target = target, Stack = resident } : null;
        internal static Plan EmptySlot(GameItem item, GameInventory target) => SafeSource(item) && ItemGridMove.Allowed && target.childItems.Count == 0
            ? new Plan { Item = item, Target = target } : null;
        internal static Result Apply(Plan plan, string action, string location, string sourceKey, string targetKey, Func<bool> scope)
        {
            if (!Ready || plan == null || !scope()) return Result.Unavailable;
            ItemGridMove.Attempts++;
            plan.Item.parentInventory.childItems.Remove(plan.Item);
            if (plan.Stack != null) { plan.Stack.unitCount += plan.Item.unitCount; plan.Item.unitCount = 0; plan.Item.parentInventory = null; }
            else { plan.Item.parentInventory = plan.Target; plan.Target.childItems.Add(plan.Item); }
            return Result.Applied;
        }
    }
}
