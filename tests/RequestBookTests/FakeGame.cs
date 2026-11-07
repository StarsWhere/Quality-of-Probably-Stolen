using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Newtonsoft.Json
{
    [AttributeUsage(AttributeTargets.Field)] public sealed class JsonRequiredAttribute : Attribute { }
    public enum Required { AllowNull }
    [AttributeUsage(AttributeTargets.Field)] public sealed class JsonPropertyAttribute : Attribute { public Required Required { get; set; } }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string name) { } }
}
namespace UnityEngine
{
    public class Object
    {
        public static T Instantiate<T>(T value, Transform parent, bool world) where T : Object => value;
        public static void Destroy(Object value) { }
        public static T[] FindObjectsOfType<T>() => Array.Empty<T>();
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
    }
    public struct Rect { public float width; }
    public class Component : Object
    {
        public GameObject gameObject = new GameObject();
        public Transform transform => gameObject.transform;
        public T GetComponentInChildren<T>(bool inactive) where T : class, new() => new T();
    }
    public class Transform : Object
    {
        public Transform parent;
        public T GetComponent<T>() where T : class => null;
        public Transform Find(string name) => null;
    }
    public sealed class RectTransform : Transform { public Vector2 anchoredPosition; public Rect rect; }
    public sealed class GameObject : Object
    {
        public string name;
        public bool activeInHierarchy = true;
        public RectTransform transform = new RectTransform();
        public void SetActive(bool value) => activeInHierarchy = value;
    }
    public static class Time { public static float unscaledTime; }
    public static class Input
    {
        public static bool Held;
        public static bool GetMouseButton(int button) => Held;
        public static bool GetMouseButtonUp(int button) => false;
    }
}
namespace UnityEngine.UI
{
    public sealed class HorizontalLayoutGroup { }
    public sealed class Button : UnityEngine.Component
    {
        public sealed class ButtonClickedEvent { public void AddListener(Action action) { } }
        public ButtonClickedEvent onClick = new ButtonClickedEvent();
        public bool interactable;
        public string name;
    }
}
namespace TMPro { public sealed class TMP_Text { public bool richText; public void SetText(string value) { } } }

public sealed class ConfigEntry<T> { public T Value { get; set; } }
public static class KeyBindHelper { public static bool IsCapturing; }
public static class UIDragHandler { public static bool isDraggingAnyUI; }
public static class StoreUIManager { public static bool IsAnyTyping() => false; }
public static class StoreStation { public static int GetDayCounter() => 1; }
public static class ModHook { public static event Action OnGameLoadedInit, OnLeavingStoreEarly; }

public sealed class PlayerStore
{
    public static PlayerStore Instance = new PlayerStore();
    public string runID = "test-run";
    public int saveSlotId = 1;
    public List<StoreRequest> storeRequests = new List<StoreRequest>();
    public object ModData() => this;
}
public sealed class StoreRequest
{
    public string id, sourceEventId, requiredItem, requesterName, title;
    public int requiredItemCount, duration;
    public bool CanThisItemFillRequest(GameItem item) => item != null && item.identifier == requiredItem;
}
public sealed class StoreRequestElement : UnityEngine.Component
{
    public UnityEngine.UI.Button acceptButton;
    public StoreRequest storeRequest;
    public void Init() { }
}
public sealed class GridShape { public GridShape Clone() => new GridShape(); }
public sealed class GameInventory
{
    public List<GameItem> childItems = new List<GameItem>();
    public bool locked;
    public bool IsInsertLocked() => locked;
}
public class GameItem
{
    public string identifier, name;
    public int uniqueId, unitCount = 1;
    public GameInventory parentInventory;
    public GridShape modifiedShape = new GridShape();
    public int GetUniqueID() => uniqueId;
    public string GetDisplayName(bool full) => name;
}
public sealed class GameItemElement : GameItem { }

public static class ModHelper
{
    private static readonly Dictionary<string, string> data = new Dictionary<string, string>();
    public static bool FailWrite, FailReadAfterWrite;
    internal static string FailReadAfterKeyWrite;
    private static bool failRead;
    internal static void FailNextRead() => failRead = true;
    private static string Key(string key) => PlayerStore.Instance.runID + ":" + PlayerStore.Instance.saveSlotId + ":" + key;
    public static string GetModData(string mod, string key, string fallback)
    {
        if (failRead) { failRead = false; throw new InvalidOperationException("injected readback failure"); }
        return data.TryGetValue(Key(key), out var value) ? value : fallback;
    }
    public static void SetModData(string mod, string key, string value)
    {
        if (FailWrite) { FailWrite = false; throw new InvalidOperationException("injected write failure"); }
        data[Key(key)] = value;
        if (FailReadAfterWrite || FailReadAfterKeyWrite == key)
        { FailReadAfterWrite = false; FailReadAfterKeyWrite = null; failRead = true; }
    }
}

public sealed class CustomUIElement
{
    public TMPro.TMP_Text text = new TMPro.TMP_Text();
    public void SetInteractable(bool value) { }
}
public sealed class CustomUIWindow
{
    public bool IsAlive = true;
    public UnityEngine.GameObject root = new UnityEngine.GameObject();
    internal readonly Dictionary<string, Action> buttons = new Dictionary<string, Action>();
    internal Action closed;
    public void Close() { if (!IsAlive) return; IsAlive = false; closed?.Invoke(); }
    public void Click(string id) => buttons[id]();
}
public sealed class CustomUIBuilder
{
    private readonly CustomUIWindow window;
    public CustomUIElement Last = new CustomUIElement();
    public CustomUIBuilder(CustomUIWindow window) { this.window = window; }
    public CustomUIBuilder OnClose(Action action) { window.closed = action; return this; }
    public CustomUIBuilder BeginScrollFlexible(int height) => this;
    public CustomUIBuilder BeginGrid(int columns, int width, int height, int spacing) => this;
    public CustomUIBuilder BeginPanel(int width, int height, int padding) => this;
    public CustomUIBuilder BeginRow() => this;
    public CustomUIBuilder End() => this;
    public CustomUIBuilder AddLabel(string text) { Last = new CustomUIElement(); return this; }
    public CustomUIBuilder AddButton(string text, Action action, string id = null)
    { Last = new CustomUIElement(); if (id != null) window.buttons[id] = action; return this; }
    public void Show() { }
}
public sealed class CustomUIManager { public static CustomUIManager Instance = new CustomUIManager(); }

namespace ShopAssist
{
    internal static class ModEntry { internal const string Id = "starswhere.shopassist"; }
    internal static class ShopData
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { IncludeFields = true };
        internal static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);
        internal static T Decode<T>(string value, int max) => JsonSerializer.Deserialize<T>(value, Options);
    }
    internal sealed class ShopSettings
    {
        private readonly Dictionary<string, object> options = new Dictionary<string, object>();
        internal event Action Changed;
        internal ConfigEntry<bool> Toggle(string key, string tip) { var option = new ConfigEntry<bool>(); options[key] = option; return option; }
        internal ConfigEntry<int> Integer(string key, string tip, int value, int min, int max) => new ConfigEntry<int> { Value = value };
        internal void Action(string key, Action action, Func<bool> available) { }
        internal void Set(string key, bool value) { ((ConfigEntry<bool>)options[key]).Value = value; Changed?.Invoke(); }
        internal static string Text(string key) => key;
        internal static string Format(string key, params object[] values) => key + ":" + string.Join(",", values);
    }
    internal static class VisibleInventoryViews
    {
        internal static event Action Changed;
        internal static event Action<GameItemElement> ItemChanged;
        internal static void NotifyChanged() => Changed?.Invoke();
    }
    internal static class AttentionBook
    {
        internal static readonly HashSet<int> Inspections = new HashSet<int>();
        internal static readonly Dictionary<string, Func<IEnumerable<AttentionItem>>> Providers = new Dictionary<string, Func<IEnumerable<AttentionItem>>>();
        internal static bool Opened;
        internal static void Register(string id, Func<IEnumerable<AttentionItem>> provider) { Providers.Add(id, provider); }
        internal static void Unregister(string id) { Providers.Remove(id); }
        internal static void Changed() { }
        internal static bool HasInspection(string action, int id) => Inspections.Contains(id);
        internal static void Open() => Opened = true;
    }
    internal static class ShopNotice
    {
        internal static string Last;
        internal static OperationResult Result;
        internal static void Show(string value) => Last = value;
        internal static void Report(OperationResult value) => Result = value;
    }
    internal static class ShopInventory
    {
        internal sealed class Location
        {
            internal GameInventory Inventory;
            internal GameItem Owner;
            internal bool Ordinary = true, Open = true;
            internal string Name, Id;
            internal string Key(bool allocate = false) => Id;
        }
        internal static readonly List<Location> Locations = new List<Location>();
        internal static string NextMoveFailure;
        internal static bool InStore = true;
        internal static bool Owned(GameItem item) => item != null && item.parentInventory != null;
        internal static bool Movable(GameItem item) => Owned(item) && item.unitCount > 0;
        internal static List<Location> OwnedLocations(bool onlyOpen) => Locations.Where(location => !onlyOpen || location.Open).ToList();
        internal static bool TryRefresh(Location expected, List<Location> list, out Location live)
        { live = list.FirstOrDefault(location => location.Inventory == expected?.Inventory); return live != null; }
        internal static bool TryMoveIntact(GameItem item, Location source, Location target, out string reason)
        {
            reason = NextMoveFailure;
            if (reason != null) { NextMoveFailure = null; return false; }
            source.Inventory.childItems.Remove(item); target.Inventory.childItems.Add(item); item.parentInventory = target.Inventory;
            return true;
        }
    }
    internal static class InventorySortPlan { internal static bool SameShape(GridShape a, GridShape b) => a != null && b != null; }
    internal static class UiLayout
    {
        internal static CustomUIWindow Window;
        internal static int RebuildCount;
        internal static CustomUIBuilder Rebuild(ref CustomUIWindow window, string id, string title, int width, int height)
        {
            if (window?.IsAlive != true) window = new CustomUIWindow();
            window.buttons.Clear(); Window = window; RebuildCount++; ItemGrid.Views.Clear();
            return new CustomUIBuilder(window);
        }
        internal static void Pager(CustomUIBuilder b, int page, int count, int size, Action<int> next) { }
    }
    internal static class VisualChoices
    {
        internal static Func<IList<ShopInventory.Location>> Choices;
        internal static Action<ShopInventory.Location> Change;
        internal static void Location(CustomUIBuilder b, string title, ShopInventory.Location selected,
            Func<IList<ShopInventory.Location>> choices, Action<ShopInventory.Location> changed, string id)
        { Choices = choices; Change = changed; }
    }
    internal static class ItemGrid
    {
        internal sealed class Entry
        {
            internal GameItem Item;
            internal string Badge, Reason;
            internal bool Selectable = true, Selected;
            internal Entry(GameItem item) { Item = item; }
        }
        internal sealed class Options
        {
            internal bool Cards;
            internal int Columns, CardWidth, CardHeight;
            internal Action<Entry> OnClick;
        }
        internal static bool DragActive;
        internal static readonly List<(List<Entry> Entries, Options Options)> Views = new List<(List<Entry>, Options)>();
        internal static void Add(CustomUIBuilder b, IEnumerable<Entry> entries, Options options) => Views.Add((entries.ToList(), options));
    }
}
