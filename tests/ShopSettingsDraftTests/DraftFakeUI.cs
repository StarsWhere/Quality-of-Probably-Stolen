using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class HarmonyPatch : Attribute { internal HarmonyPatch(Type type, string method) { } }
    internal static class AccessTools
    {
        internal static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        internal static MethodInfo Method(Type type, string name) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }
}

namespace UnityEngine
{
    internal enum KeyCode { None, A, B, F5 }
    internal class Object
    {
        internal bool Destroyed;
        public static bool operator ==(Object a, Object b) =>
            (ReferenceEquals(a, null) || a.Destroyed) && (ReferenceEquals(b, null) || b.Destroyed) || ReferenceEquals(a, b);
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => base.GetHashCode();
    }
    internal class MonoBehaviour : Object { }
    internal sealed class GameObject
    {
        internal object Component;
        internal T GetComponent<T>() where T : class => Component as T;
        internal T AddComponent<T>() where T : class => (T)(Component = Activator.CreateInstance(typeof(T), true));
    }
    internal sealed class RectTransform { internal GameObject gameObject = new GameObject(); }
    internal struct Vector2 { internal float x, y; internal Vector2(float x, float y) { this.x = x; this.y = y; } }
    internal static class Debug
    {
        internal static readonly List<string> Errors = new List<string>();
        internal static void LogError(object value) => Errors.Add(value.ToString());
    }
}

internal sealed class GameOptionUIManager : MonoBehaviour
{
    internal static GameOptionUIManager Instance;
    private readonly Dictionary<string, KeyCode> pendingKeys = new Dictionary<string, KeyCode>();
    internal void SetKey(string id, KeyCode value) => pendingKeys[id] = value;
    internal void OpenUI() { }
    private void RefreshKeyBindButton(string id) { }
    private void RefreshRules() { }
}

internal static class GameOptionRegistry
{
    internal enum Kind { KeyBind, Toggle, Slider }
    internal sealed class Entry { internal string id; internal Kind kind; }
    internal sealed class Section { internal List<Entry> entries = new List<Entry>(); }
    internal static readonly List<Section> Sections = new List<Section>();
}

internal sealed class CustomUIElement
{
    internal bool IsAlive = true;
    internal object toggle, dropdown, slider, input;
    internal bool Bool;
    internal int Index;
    internal float Float;
    internal string String;
    internal bool GetBool() => Bool;
    internal int GetIndex() => Index;
    internal float GetFloat() => Float;
    internal string GetString() => String;
    internal void SetBool(bool value, bool notify) => Bool = value;
    internal void SetIndex(int value, bool notify) => Index = value;
    internal void SetFloat(float value, bool notify) => Float = value;
    internal void SetString(string value, bool notify) => String = value;
}

internal sealed class CustomUIWindow
{
    internal bool IsAlive = true;
    internal string id = "game_options";
    internal readonly RectTransform Rect = new RectTransform();
    internal readonly Dictionary<string, CustomUIElement> Controls = new Dictionary<string, CustomUIElement>();
    internal CustomUIElement Get(string id) => Controls.TryGetValue(id, out var control) ? control : null;
    internal void Show() { }
    internal void BringToFront() { }
    internal void Close() => IsAlive = false;
}

internal sealed class CustomUIManager
{
    internal static CustomUIManager Instance;
    internal const string LAYER_META = "meta";
    internal CustomUIWindow GetWindow(string id) => null;
    internal static Builder Window(string id, string title, string layer) => new Builder();
    internal sealed class Builder
    {
        internal Builder SetSize(float x, float y) => this;
        internal Builder SetDraggable(bool value) => this;
        internal Builder SetCloseOnEscape(bool value) => this;
        internal CustomUIWindow GetWindow() => new CustomUIWindow();
        internal Builder OnClose(Action action) => this;
        internal Builder AddLabel(string value) => this;
        internal Builder BeginRow() => this;
        internal Builder AddButton(string label, Action action, string id) => this;
        internal Builder End() => this;
        internal void Show() { }
    }
}

internal sealed class ConfigEntry { internal string Key; }
internal sealed class ModConfig
{
    internal string ModId;
    internal readonly List<ConfigEntry> Entries = new List<ConfigEntry>();
}
internal sealed class ModConfigWindow { }
internal sealed class StoreUIManager { internal static StoreUIManager Instance; }

namespace ShopAssist
{
    internal static class ModEntry { internal const string Id = "test"; }
    internal static class UiLayout { internal static Vector2 Fit(float x, float y) => new Vector2(x, y); }
    internal sealed partial class ShopSettings
    {
        private static ShopSettings attached;
        private readonly ModConfig config = new ModConfig();
        private static string Text(string key) => key;
        private void CloseFinder() { }
    }
}
