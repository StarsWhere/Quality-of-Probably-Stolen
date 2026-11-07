using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Newtonsoft.Json
{
    [AttributeUsage(AttributeTargets.Field)] public sealed class JsonRequiredAttribute : Attribute { }
}

namespace UnityEngine
{
    public struct Vector2 { public float x, y; }
    public class RectTransform { public Vector2 anchoredPosition; public void SetAsLastSibling() { } }
    public sealed class GameObject { public RectTransform transform = new RectTransform(); }
    public enum KeyCode { None }
    public static class Input
    {
        public static bool GetKeyDown(KeyCode key) => false;
        public static bool GetMouseButton(int button) => false;
        public static bool GetMouseButtonUp(int button) => false;
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene { }
    public static class SceneManager { public static event Action<Scene, Scene> activeSceneChanged; }
}

public static class UIDragHandler { public static bool isDraggingAnyUI; }
public static class StoreUIManager { public static bool IsAnyTyping() => false; }

public sealed class ConfigEntry<T> { public T Value { get; set; } }

public sealed class PlayerStore
{
    public enum PlayerLocation { IN_STORE }
    public enum StoreState { MORNING, CLOSED }
    public static PlayerStore Instance = new PlayerStore();
    public static bool HasInstance => Instance != null;
    public string runID = "test-run";
    public int saveSlotId = 1;
    public PlayerLocation playerLocation = PlayerLocation.IN_STORE;
    public StoreState storeState = StoreState.MORNING;
}

public static class StoreStation { public static int Day = 1; public static int GetDayCounter() => Day; }
public static class ModHook
{
    public static event Action OnGameLoadedInit, OnLeavingStoreEarly;
}

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

public sealed class FakeInput { public int characterLimit; }
public sealed class FakeText { public bool richText; }
public sealed class CustomUIElement
{
    internal string value = "";
    internal Action<string> changed;
    internal Action<int> selected;
    internal Action<bool> toggled;
    public FakeInput input = new FakeInput();
    public FakeText text = new FakeText();
    public void SetFlexible(int x, int y) { }
    public string GetString() => value;
    public void SetString(string next) { value = next; changed?.Invoke(next); }
    public void SetIndex(int next) => selected?.Invoke(next);
    public void SetBool(bool value, bool notify = true) { if (notify) toggled?.Invoke(value); }
    public void SetText(string next) => value = next;
}

public sealed class CustomUIWindow
{
    public bool IsAlive = true;
    public UnityEngine.GameObject root = new UnityEngine.GameObject();
    internal readonly Dictionary<string, CustomUIElement> elements = new Dictionary<string, CustomUIElement>();
    internal readonly Dictionary<string, Action> buttons = new Dictionary<string, Action>();
    internal readonly List<string> taskOrder = new List<string>();
    internal Action onClose;
    public CustomUIElement Get(string id) => elements[id];
    public void Close() { if (!IsAlive) return; IsAlive = false; onClose?.Invoke(); }
    internal void Click(string id) => buttons[id]();
}

public sealed class CustomUIBuilder
{
    private readonly CustomUIWindow window = new CustomUIWindow();
    public CustomUIElement Last = new CustomUIElement();
    public CustomUIBuilder SetSize(float x, float y) => this;
    public CustomUIBuilder SetDraggable(bool value) => this;
    public CustomUIBuilder SetCloseOnEscape(bool value) => this;
    public CustomUIWindow GetWindow() => window;
    public CustomUIBuilder OnClose(Action action) { window.onClose = action; return this; }
    public CustomUIBuilder AddLabel(string text, string id = null) { Last = new CustomUIElement { value = text }; if (id != null) window.elements[id] = Last; return this; }
    public CustomUIBuilder BeginRow() => this;
    public CustomUIBuilder End() => this;
    public CustomUIBuilder AddFlexibleSpace() => this;
    public CustomUIBuilder BeginScrollFlexible(int height) => this;
    public CustomUIBuilder AddInput(string label, string value, Action<string> changed, string id, Action<string> ended)
    { Last = new CustomUIElement { value = value, changed = changed }; window.elements[id] = Last; return this; }
    public CustomUIBuilder AddDropdown(string[] labels, int value, Action<int> selected, string id)
    { Last = new CustomUIElement { selected = selected }; window.elements[id] = Last; return this; }
    public CustomUIBuilder AddButton(string label, Action action, string id = null)
    { Last = new CustomUIElement(); if (id != null) window.buttons[id] = action; return this; }
    public CustomUIBuilder AddToggle(string label, bool value, Action<bool> changed, string id = null)
    {
        Last = new CustomUIElement { toggled = changed };
        if (id != null) { window.elements[id] = Last; if (id.StartsWith("done_")) window.taskOrder.Add(label); }
        return this;
    }
    public void Show() { }
}

public sealed class CustomUIManager
{
    public const int LAYER_META = 1;
    public static CustomUIManager Instance = new CustomUIManager();
    public static CustomUIWindow LastWindow;
    public static CustomUIBuilder Window(string id, string title, int layer)
    { var builder = new CustomUIBuilder(); LastWindow = builder.GetWindow(); return builder; }
}

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
        internal readonly Dictionary<string, Action> actions = new Dictionary<string, Action>();
        internal event Action Changed;
        internal ConfigEntry<bool> Toggle(string key, string tip)
        { var entry = new ConfigEntry<bool>(); options[key] = entry; return entry; }
        internal Func<UnityEngine.KeyCode> Key(string key, string tip) => () => UnityEngine.KeyCode.None;
        internal void Action(string key, Action callback, Func<bool> available) => actions[key] = callback;
        internal void Set(string key, bool value) { ((ConfigEntry<bool>)options[key]).Value = value; Changed?.Invoke(); }
        internal static string Text(string key) => key;
        internal static string Format(string key, params object[] values) => key + ":" + string.Join(",", values);
    }
    internal static class ShopInput { internal static bool Ready() => true; }
    internal static class ShopNotice { internal static string Last; internal static void Show(string text) { Last = text; } }
}
