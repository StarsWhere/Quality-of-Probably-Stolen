using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public sealed class GameObject { public bool activeInHierarchy; }
    public static class Time { public static float unscaledTime; }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { }
    public static class SceneManager { public static event Action<Scene, Scene> activeSceneChanged; }
}

public static class ModHook { public static event Action OnGameLoadedInit, OnLeavingStoreEarly; }
public sealed class PlayerStore
{
    public static bool HasInstance = true;
    public static PlayerStore Instance = new PlayerStore();
    public string runID = "test-run";
    public int saveSlotId = 1;
    public object ModData() => this;
}
public static class ModHelper
{
    public static readonly Dictionary<string, string> Data = new Dictionary<string, string>();
    public static Func<string, bool> FailRead, FailBeforeWrite, FailAfterWrite;
    public static Action<string, string> OnWrite;
    private static string Key(string key) => PlayerStore.Instance.runID + ":" + PlayerStore.Instance.saveSlotId + ":" + key;
    public static string GetModData(string mod, string key, string fallback)
    {
        if (FailRead?.Invoke(key) == true) throw new InvalidOperationException("Injected read failure");
        return Data.TryGetValue(Key(key), out string value) ? value : fallback;
    }
    public static void SetModData(string mod, string key, string value)
    {
        if (FailBeforeWrite?.Invoke(key) == true) throw new InvalidOperationException("Injected write failure");
        Data[Key(key)] = value;
        OnWrite?.Invoke(key, value);
        if (FailAfterWrite?.Invoke(key) == true) throw new InvalidOperationException("Injected post-write failure");
    }
}
public sealed class GameInventory { public readonly List<GameItem> childItems = new List<GameItem>(); }
public class GameItem
{
    public int uniqueId;
    public int GetUniqueID() => uniqueId;
    public string GetDisplayName(bool full) => "item";
}
public sealed class GameItemElement : GameItem { }
public static class KeyBindHelper { public static bool IsCapturing; }
public static class UIDragHandler { public static bool isDraggingAnyUI; }
public sealed class ItemMouseDragHandler { public static ItemMouseDragHandler current; public bool IsDraggingItem; }
public sealed class UIOverlay { public UnityEngine.GameObject gameObject = new UnityEngine.GameObject(); }
public sealed class StoreUIManager
{
    public static StoreUIManager Instance = new StoreUIManager();
    public UIOverlay blackscreen;
    public GenericConfirmationUIManager confirmPanelUIManager;
}
public sealed class GenericConfirmationUIManager
{
    public static GenericConfirmationUIManager Instance = new GenericConfirmationUIManager();
    public UnityEngine.GameObject panel = new UnityEngine.GameObject();
    public Action Confirm, Cancel;
    public string Message;
    public void OpenPanel(string message, string accept, string cancel, Action confirm, Action onCancel)
    { Message = message; Confirm = confirm; Cancel = onCancel; panel.activeInHierarchy = true; }
    public void ClosePanel() { panel.activeInHierarchy = false; }
}
public sealed class FakeText { public bool richText; }
public sealed class CustomUIElement { public FakeText text = new FakeText(); }
public sealed class CustomUIWindow
{
    public bool IsAlive = true;
    public Action Closed;
    public void Close() { IsAlive = false; Closed?.Invoke(); }
}
public sealed class CustomUIBuilder
{
    private readonly CustomUIWindow window;
    public CustomUIElement Last = new CustomUIElement();
    public CustomUIBuilder(CustomUIWindow window) { this.window = window; }
    public CustomUIBuilder OnClose(Action action) { window.Closed = action; return this; }
    public CustomUIBuilder BeginScrollFlexible(int height) => this;
    public CustomUIBuilder AddLabel(string label) => this;
    public CustomUIBuilder BeginRow() => this;
    public CustomUIBuilder AddButton(string label, Action action, string id = null) => this;
    public CustomUIBuilder End() => this;
    public void Show() { }
}
public sealed class CustomUIManager { public static CustomUIManager Instance = new CustomUIManager(); }

namespace ShopAssist
{
    internal static class ModEntry { internal const string Id = "test"; }
    internal sealed class ShopSettings
    {
        internal static string Text(string key) => key;
        internal static string Format(string key, params object[] args) => key;
    }
    internal static class MachineTools { internal static void OverviewAttentionChanged() { } }
    internal static class ShopInventory
    {
        internal static bool InStore = true;
        internal static IEnumerable<(GameInventory Inventory, string Name)> MachineLocations() => Array.Empty<(GameInventory, string)>();
        internal static bool Owned(GameItem item) => true;
    }
    internal static class ItemLocator { internal static bool TryLocate(GameItem item, out string reason) { reason = null; return true; } }
    internal static class ShopNotice
    {
        internal static string Last;
        internal static bool HasResult => false;
        internal static void Show(string message, float duration = 0) { Last = message; }
        internal static void OpenLastResult() { }
        internal static void ResetSession() { }
    }
    internal static class UiLayout
    {
        internal static CustomUIBuilder Rebuild(ref CustomUIWindow window, string id, string title, int width, int height)
        { window = new CustomUIWindow(); return new CustomUIBuilder(window); }
    }
}
