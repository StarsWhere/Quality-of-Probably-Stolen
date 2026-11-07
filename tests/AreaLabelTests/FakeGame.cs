using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public static void Destroy(Object value)
        {
            if (value is GameObject obj) obj.Destroy();
        }
    }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public void GetComponentsInChildren<T>(bool includeInactive, List<T> result) where T : Component =>
            gameObject.Collect(includeInactive, result);
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component
        {
            var result = new List<T>();
            gameObject.Collect(includeInactive, result);
            return result.ToArray();
        }
        public T GetComponentInParent<T>() where T : Component
        {
            for (Transform node = transform; node != null; node = node.parent)
            {
                T found = node.gameObject.GetComponent<T>();
                if (found != null) return found;
            }
            return null;
        }
    }

    public class Behaviour : Component
    {
        public bool enabled = true;
        public bool isActiveAndEnabled => enabled && gameObject.activeInHierarchy;
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public static Vector2 one => new Vector2(1, 1);
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z = 0) { this.x = x; this.y = y; this.z = z; }
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1, 1);
    }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static implicit operator Color(Color32 value) => new Color(value.r / 255f, value.g / 255f, value.b / 255f, value.a / 255f);
    }

    public static class Mathf
    {
        public static float Min(float a, float b) => Math.Min(a, b);
        public static float Max(float a, float b) => Math.Max(a, b);
    }

    public enum RenderMode { ScreenSpaceOverlay, ScreenSpaceCamera }
    public class Camera { }
    public class Canvas : Component { public RenderMode renderMode; public Camera worldCamera; public float scaleFactor = 1; }
    public static class Screen { public static int width = 1280, height = 720; }
    public static class RectTransformUtility
    {
        public static bool RectangleContainsScreenPoint(RectTransform rect, Vector2 position, Camera camera) => false;
        public static Vector2 WorldToScreenPoint(Camera camera, Vector3 position) => new Vector2(position.x, position.y);
    }

    public static class Time { public static int frameCount; public static float unscaledTime; }

    public enum KeyCode { None }
    public static class Input
    {
        public static Vector2 mousePosition;
        public static readonly bool[] MouseHeld = new bool[3], MouseUp = new bool[3];
        public static bool GetMouseButton(int button) => MouseHeld[button];
        public static bool GetMouseButtonUp(int button) => MouseUp[button];
        public static bool GetKeyDown(KeyCode key) => false;
    }

    public static class Debug
    {
        public static void LogError(object message) => throw new Exception(message.ToString());
    }

    public class Transform : Component
    {
        internal readonly List<Transform> children = new List<Transform>();
        public Vector3 position;
        private Transform parentValue;
        public Transform parent => parentValue;
        public void SetParent(Transform value)
        {
            parentValue?.children.Remove(this);
            parentValue = value;
            parentValue?.children.Add(this);
        }
        internal Transform Parent => parentValue;
        public int childCount => children.Count;
        public Transform GetChild(int index) => children[index];
        public Transform Find(string name) => children.Find(t => t.gameObject.name == name);
        public void SetAsLastSibling() { }
        public T GetComponentInChildren<T>() where T : Component => gameObject.GetComponentInChildren<T>();
    }

    public sealed class RectTransform : Transform
    {
        public Vector2 anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta, offsetMin, offsetMax;
    }

    public sealed class GameObject : Object
    {
        private readonly List<Component> components = new List<Component>();
        public string name;
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf && (transform.Parent == null || transform.Parent.gameObject.activeInHierarchy);
        public RectTransform transform { get; }

        public GameObject(string name = "")
        {
            this.name = name;
            transform = Add(new RectTransform());
        }

        public T Add<T>(T component) where T : Component
        {
            component.gameObject = this;
            components.Add(component);
            return component;
        }

        public void SetActive(bool value) => activeSelf = value;
        public T GetComponent<T>() where T : Component => components.Find(c => c is T) as T;
        public T GetComponentInParent<T>() where T : Component => transform.GetComponentInParent<T>();
        public void GetComponents<T>(List<T> result) where T : Component
        {
            result.Clear();
            foreach (Component component in components) if (component is T found) result.Add(found);
        }
        public T GetComponentInChildren<T>() where T : Component
        {
            var result = new List<T>();
            Collect(false, result);
            return result.Count == 0 ? null : result[0];
        }
        internal void Collect<T>(bool includeInactive, List<T> result) where T : Component
        {
            if (!includeInactive && !activeInHierarchy) return;
            foreach (Component component in components) if (component is T found) result.Add(found);
            foreach (Transform child in transform.children.ToArray()) child.gameObject.Collect(includeInactive, result);
        }
        internal void Destroy()
        {
            SetActive(false);
            transform.SetParent(null);
        }
    }
}

namespace UnityEngine.UI
{
    public class Graphic : UnityEngine.Behaviour
    {
        public bool raycastTarget;
        public UnityEngine.Color color;
    }
    public sealed class Image : Graphic { public object sprite; }
    public sealed class ScrollRect : UnityEngine.Behaviour { public float verticalNormalizedPosition = 1; }
}

public static class UIDragHandler { public static bool isDraggingAnyUI; }

namespace TMPro
{
    public enum TextOverflowModes { Ellipsis }
    public enum TextAlignmentOptions { Center }
    public class TMP_Text : UnityEngine.Behaviour
    {
        public string text;
        public bool raycastTarget, richText, enableWordWrapping, enableAutoSizing;
        public float fontSizeMin, fontSizeMax, fontSize, outlineWidth;
        public UnityEngine.Color outlineColor;
        public TextOverflowModes overflowMode;
        public TextAlignmentOptions alignment;
        public UnityEngine.Color color;
        public UnityEngine.Canvas canvas;
        public UnityEngine.Vector2 GetPreferredValues(string value) => new UnityEngine.Vector2(value.Length, 1);
    }
    public sealed class TextMeshProUGUI : TMP_Text { }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene { }
    public static class SceneManager
    {
        public static event Action<Scene, Scene> activeSceneChanged;
        public static void Change() => activeSceneChanged?.Invoke(default, default);
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string name) { }
        public HarmonyPatch(Type type, string name, Type[] parameters) { }
    }
}

public sealed class ConfigEntry<T>
{
    public T Value { get; set; }
    public void Set(T value) => Value = value;
}

namespace UnityEngine
{
    public static class PlayerPrefs
    {
        private static readonly Dictionary<string, int> values = new Dictionary<string, int>();
        public static int GetInt(string key, int fallback = 0) => values.TryGetValue(key, out int value) ? value : fallback;
        public static void SetInt(string key, int value) => values[key] = value;
        public static void Save() { }
    }
}

namespace ShopAssist
{
    internal static class ModEntry { internal const string Id = "starswhere.shopassist"; }
    internal sealed class ShopSettings
    {
        private readonly Dictionary<string, object> entries = new Dictionary<string, object>();
        internal event Action Changed;
        internal ConfigEntry<bool> Toggle(string key, string tip, bool initial = false)
        {
            var entry = new ConfigEntry<bool> { Value = initial };
            entries.Add(key, entry);
            return entry;
        }
        internal ConfigEntry<int> Integer(string key, string tip, int initial, int min, int max)
        {
            var entry = new ConfigEntry<int> { Value = initial };
            entries.Add(key, entry);
            return entry;
        }
        internal Func<UnityEngine.KeyCode> Key(string key, string tip) => () => UnityEngine.KeyCode.None;
        internal void Action(string key, Action action, Func<bool> condition) { }
        internal static string Text(string key) => key;
        internal static string Format(string key, params object[] values) => key;
        internal void Set<T>(string key, T value)
        {
            ((ConfigEntry<T>)entries[key]).Value = value;
            Changed?.Invoke();
        }
    }
}

public static class ModHook
{
    public static event Action OnGameLoadedInit;
    public static event Action OnGameLoadedLate;
    public static event Action<string> OnNewGameStarted;
    public static event Action OnLeavingStoreEarly;
    public static void LoadInit() => OnGameLoadedInit?.Invoke();
    public static void LoadLate() => OnGameLoadedLate?.Invoke();
    public static void NewGame() => OnNewGameStarted?.Invoke("test");
    public static void Leave() => OnLeavingStoreEarly?.Invoke();
}

public sealed class RenderHandler
{
    public static RenderHandler current = new RenderHandler();
    public static T RaycastElement<T>(UnityEngine.Vector2 position) where T : class => null;
}

public sealed class WindowsHandler
{
    public static WindowsHandler current = new WindowsHandler();
    public readonly List<PixelWindow> visibleWindows = new List<PixelWindow>();
}

public class PixelElement { public TreeNodeRender handler; }
public class GameInventory : PixelElement { public readonly List<GameItem> childItems = new List<GameItem>(); }
public sealed class GameGridInventory : GameInventory
{
    public bool UncheckedAccept(GameItem item) => true;
    public bool Expel(GameItem item) => true;
}
public sealed class GameSlotInventory : GameInventory
{
    public bool UncheckedAccept(GameItem item) => true;
    public bool Expel(GameItem item) => true;
}
public sealed class SlotMarker
{
    public GameInventory inventory;
    public GameItem targetItem;
    public int numTransfer;
}
public static class MachineHelper { public const string STANDARD_MACHINE_TAG = "STANDARD_MACHINE_TAG"; }

public class GameItem
{
    public PixelWindow contentWindow;
    public GameInventory parentInventory = new GameGridInventory();
    public bool isDebugMenu;
    public string customText, customName, name = "Storage", identifier = "item";
    public int unitCount = 1;
    public int removeLimit = int.MaxValue;
    public int stackRoom = int.MaxValue;
    public bool stackAllowed = true;
    public bool machineTag;
    public bool safeToOpen = true;
    public bool hiddenAcceptance;
    public bool isOwned = true;
    public object tooltipRichTextFunc;
    public readonly HashSet<string> tags = new HashSet<string>();
    public readonly List<string> types = new List<string>();
    public readonly Dictionary<string, string> tagValues = new Dictionary<string, string>();
    public readonly Dictionary<int, string> tabNames = new Dictionary<int, string>();
    public readonly List<ItemFeature> itemFeatures = new List<ItemFeature>();
    public bool IsTag(string tag) => tag == MachineHelper.STANDARD_MACHINE_TAG ? machineTag : tags.Contains(tag);
    public string GetDisplayName() => name;
    public string GetDisplayName(bool includeCustom) => name;
    public int MaxNumRemove() => Math.Min(unitCount, removeLimit);
    public SlotMarker GetStackSlot(GameItem item) => !stackAllowed || item == null ? null :
        new SlotMarker { inventory = parentInventory, targetItem = this, numTransfer = stackRoom };
    public List<string> GetGameItemType() => types;
    public TagState GetTagReadonly(string tag) => new TagState(tagValues.TryGetValue(tag, out string v) ? v : "");
    public void ValidateShapeState() { }
}

public sealed class TagState
{
    private readonly string value;
    public TagState(string value) { this.value = value; }
    public string GetString() => value;
}

public static class GeneralHelper
{
    public static bool HasCustomName(GameItem item) => item.customName != null;
    public static string GetCustomName(GameItem item) => item.customName;
    public static bool IsItemEffectivelyOwned(GameItem item) => item.isOwned;
    public static bool IsItemOwned(GameItem item) => item.isOwned;
}

public static class StorageExpansionHelper
{
    public static string GetTabName(GameItem item, int index) => item.tabNames.TryGetValue(index, out string value) ? value : null;
}

public sealed class GameItemElement : GameItem
{
    public TreeNodeRender handler, middle, background, rotator;
    public int widthPixels = 64, heightPixels = 64;
    public GridShape modifiedShape = new GridShape();
    public bool tooltipEnabled = true;
    private bool destroyed;
    public GameItemElement(PixelWindow parent, string customName)
    {
        this.customName = customName;
        contentWindow = new PixelWindow(false) { childElement = new GameGridInventory() };
        contentWindow.parentItems.Add(this);
        handler = TreeNodeRender.Instantiate().SetParent(parent.handler);
        handler.element = this;
        middle = TreeNodeRender.Instantiate().SetParent(handler);
        rotator = TreeNodeRender.Instantiate().SetParent(middle);
        background = TreeNodeRender.Instantiate().SetParent(rotator);
    }
    public bool IsDestroyed() => destroyed;
    public void ValidateSelect(bool updateBackground = true) { }
    public void ToggleBackground(bool active, float opacity = 1, float lightness = 1) => background.image.enabled = active;
    public void Destroy() { destroyed = true; UnityEngine.Object.Destroy(handler.gameObject); }
    public bool HasTooltip() => tooltipEnabled;
    public RichText GetTooltip() => null;
    public RichText GetTooltipAdvanced() => null;
}

public sealed class GridShape
{
    public int width = 2, height = 2;
    public int orientation;
    public int globalWidth => orientation % 2 == 0 ? width : height;
    public int globalHeight => orientation % 2 == 0 ? height : width;
    public byte GetLocal(int x, int y) => 1;
}

public sealed class UISettings
{
    public static UISettings current = new UISettings();
    public int gridSize = 16;
}

public sealed class RichText { public string nakedText; }
public sealed class RichTextBuilder
{
    private string value;
    public RichTextBuilder Add(string text) { value = text; return this; }
    public RichText Build() => new RichText { nakedText = value };
}
public sealed class ItemFeature
{
    public string category, display;
    public bool exposed = true, hidden;
    public int reads;
    public bool IsExposed() => exposed;
    public bool GetPublicHidden() => hidden;
    public string GetBaseCurrentDisplay() { reads++; return display; }
}
public static class WaterHelper { public static bool IsEmpty(GameItem item) => false; }
public static class StolenHelper
{
    public const string STOLEN_TAG = "STOLEN_TAG";
    public static bool IsStolenItem(GameItem item) => item.IsTag(STOLEN_TAG);
}
public static class ContrabandHelper
{
    public const string CONTRABAND_ITEM_TAG = "CONTRABAND_ITEM_TAG", CONTRABAND_LEVEL = "CONTRABAND_LEVEL";
    public const string CONTRABAND_LEVEL_LOW = "LOW", CONTRABAND_LEVEL_MID = "MID", CONTRABAND_LEVEL_HIGH = "HIGH", CONTRABAND_LEVEL_CRITICAL = "CRITICAL";
    public static bool IsContraband(GameItem item) => item.IsTag(CONTRABAND_ITEM_TAG);
}
public static class TypeHelper
{
    public static readonly List<string> ALL_TYPES_LIST = new List<string> { "FOOD", "WATER", "CONTRABAND", "NOT_FOR_RESALE" };
    public static string GetTypeDisplayName(string value) => value == "FOOD" ? "Food" : value;
}
public static class LocHelper
{
    public static string GetLocalizedMechanic(string key, params object[] args) =>
        key == "mech_stolen_tooltip" ? "Stolen: " + args[0] : key;
}
public sealed class StoreClient
{
    public enum ClientIntent { BUY, SELL, SELLNBUY, INSPECTION, BARTER }
    public ClientIntent clientIntent = ClientIntent.BUY;
    public bool isBusinessComplete, noAcceptingContraband;
    public List<string> clientBuyingTagList = new List<string>();
    public List<string> clientBuyingIdList = new List<string>();
    public List<string> clientBlackTagList = new List<string>();
    public BarterOffer barterOffer;
    public bool IsClientBuying() => clientIntent == ClientIntent.BUY || clientIntent == ClientIntent.SELLNBUY;
    public bool IsClientSelling() => clientIntent == ClientIntent.SELL || clientIntent == ClientIntent.SELLNBUY;
    public bool IsClientBuyingThisItem(GameItem item)
    {
        if (item.hiddenAcceptance || item.IsTag("NOT_FOR_RESALE") ||
            (noAcceptingContraband && ContrabandHelper.IsContraband(item)) || item.types.Exists(clientBlackTagList.Contains)) return false;
        return clientBuyingTagList.Contains("ALL_TYPE") ||
            (clientBuyingTagList.Contains("ALL_TYPE_EXCEPT_CONTRABAND") && !ContrabandHelper.IsContraband(item)) ||
            clientBuyingIdList.Contains(item.identifier) || item.types.Exists(clientBuyingTagList.Contains);
    }
}
public sealed class BarterOffer { public List<BarterCondition> conditions = new List<BarterCondition>(); }
public sealed class BarterCondition
{
    public string requiredItemId;
    public bool IsItemValid(GameItem item) => item.identifier == requiredItemId && !item.hiddenAcceptance;
}
public sealed class StoreClientInstance
{
    public StoreClient client = new StoreClient();
    public StoreClient GetClientBlueprint() => client;
}
public sealed class PlayerStore
{
    public static PlayerStore Instance = new PlayerStore();
    public bool isClientArrived = true;
    public string runID = "run";
    public int saveSlotId = 1;
    public StoreClientInstance currentClientInstance = new StoreClientInstance();
    public void SellItem(GameItem item) { }
}
public sealed class NegociationUIManager
{
    public static NegociationUIManager Instance = new NegociationUIManager();
    public bool IsOpen = true;
}
public sealed class OverlayHandler
{
    public static OverlayHandler current = new OverlayHandler();
    public PixelWindow tooltip = new PixelWindow();
    public PixelWindow GetTooltipWindow() => tooltip;
    public void ShowTooltip(UnityEngine.Vector2 a, UnityEngine.Vector2 b, RichText text, int width = 0) { }
    public void HideTooltip() { }
}

namespace UnityEngine.EventSystems
{
    public sealed class EventSystem { public static EventSystem current; }
}

namespace ShopAssist
{
    internal static class ShopInventory
    {
        internal sealed class Location
        {
            internal GameInventory Inventory { get; set; }
            internal GameItem Owner { get; set; }
            internal GameItem[] Path { get; set; } = Array.Empty<GameItem>();
            internal string Name { get; set; } = "Location";
            internal string Role { get; set; }
            internal int Page { get; set; } = -1;
            internal bool IsMachine { get; set; }
            internal bool Ordinary { get; set; }
        }
        internal static bool InStore = true;
        internal static readonly List<Location> Locations = new List<Location>();
        internal static List<Location> OwnedLocations(bool onlyOpen) => Locations;
        internal static List<Location> CustomerLocations() => Locations;
        internal static Func<GameInventory, bool> SearchableLock = null;
        internal static bool SearchableGoods(Location location) => location?.Inventory != null && !location.IsMachine &&
            SearchableLock?.Invoke(location.Inventory) != true && (location.Ordinary || location.Owner == null &&
                (location.Role == "counter" || location.Role == "showcase" || location.Role == "front"));
        internal static bool Owned(GameItem item) => item != null && item.isOwned &&
            !item.IsTag("ITEM_HIDDEN_TAG") && !item.IsTag("ITEM_BEHIND_LOCK_TAG");
        internal static bool Movable(GameItem item) => Owned(item);
        internal static bool SafeContainerForPeek(GameItem item) => item != null && item.safeToOpen;
        internal static bool TryRefresh(Location previous, out Location refreshed)
        {
            refreshed = Locations.Find(x => ReferenceEquals(x, previous));
            return refreshed != null;
        }
        internal static bool TryRefreshCustomer(Location previous, out Location refreshed) => TryRefresh(previous, out refreshed);
    }
}

public sealed class PixelWindow
{
    public TreeNodeRender handler;
    public PixelElement childElement;
    public readonly List<GameItem> parentItems = new List<GameItem>();
    public bool canMove = true, forceRaycast, hasTitle;
    public int widthPixels = 160;
    public PixelWindow(bool visible = true)
    {
        handler = TreeNodeRender.Instantiate();
        handler.window = this;
        handler.gameObject.SetActive(visible);
    }
    public bool IsVisible() => handler.gameObject.activeSelf;
    public PixelWindow Show() { handler.gameObject.SetActive(true); return this; }
    public PixelWindow Show(UnityEngine.Vector2 a, UnityEngine.Vector2 b, bool c) => Show();
    public PixelWindow Hide() { handler.gameObject.SetActive(false); return this; }
    public void ToFront() { }
    public void Validate() { }
}

public sealed class MachineryPanelRow : PixelElement
{
    public readonly List<MachineryPanel> Panels = new List<MachineryPanel>();
    public MachineryPanel GetPanel(string role) => Panels.Find(panel => panel.role == role);
}

public sealed class MachineryPanel
{
    public bool isHidden;
    public string role;
    public TreeNodeRender handler;
    public PixelElement content;
    public void Validate() { }
}

public sealed class MachineryTabs : PixelElement
{
    public int selected;
    public int count => captions.Count;
    public int selectCount;
    public readonly List<string> captions = new List<string>();
    public string GetCaption(int index) => index >= 0 && index < captions.Count ? captions[index] : null;
    public MachineryTabs Select(int index, bool notify = false) { selected = index; selectCount++; return this; }
    public MachineryTabs SetCaption(int index, string value) { captions[index] = value; return this; }
}

public sealed class TreeNodeRender : UnityEngine.Component
{
    public PixelWindow window;
    public object element;
    public TreeNodeRender parent;
    public UnityEngine.RectTransform rectTransform => gameObject.transform;
    public UnityEngine.UI.Image image;
    public TMPro.TextMeshProUGUI text;
    public static TreeNodeRender Instantiate()
    {
        var gameObject = new UnityEngine.GameObject();
        var node = gameObject.Add(new TreeNodeRender());
        node.image = gameObject.Add(new UnityEngine.UI.Image());
        return node;
    }
    public static TreeNodeRender InstantiateText()
    {
        var node = Instantiate();
        node.text = node.gameObject.Add(new TMPro.TextMeshProUGUI());
        return node;
    }
    public TreeNodeRender SetParent(TreeNodeRender value)
    {
        parent = value;
        transform.SetParent(value.transform);
        return this;
    }
    public PixelWindow FindParentWindow() => window ?? parent?.FindParentWindow();
}
