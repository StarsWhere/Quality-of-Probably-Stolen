using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Newtonsoft.Json
{
    [AttributeUsage(AttributeTargets.Field)] public sealed class JsonRequiredAttribute : Attribute { }
    public class JsonException : Exception { }
    public enum TypeNameHandling { None }
    public enum MissingMemberHandling { Ignore }
    public sealed class JsonSerializerSettings
    {
        public TypeNameHandling TypeNameHandling;
        public MissingMemberHandling MissingMemberHandling;
        public int? MaxDepth;
    }
    public static class JsonConvert
    {
        public static T DeserializeObject<T>(string raw, JsonSerializerSettings settings)
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<T>(raw, new JsonSerializerOptions { IncludeFields = true }); }
            catch (System.Text.Json.JsonException) { throw new JsonException(); }
        }
    }
}
public interface GridShape { int minX { get; } int minY { get; } GridShape Clone(); }
public sealed class Shape : GridShape { public int minX { get; set; } public int minY { get; set; } public GridShape Clone() => new Shape { minX = minX, minY = minY }; }
public class GameItem
{
    public string identifier, name, identifierName, shortDescription, longDescription, flavorText, customText, spritePath, spriteAtlasPath;
    public GameInventory parentInventory;
    public GridShape modifiedShape;
    public int unitCount = 1, uniqueId, bonusAccuracy, maxStack = 20;
    public long unitValue, lateUnitValue, unitBaseValue, backupUnitValue;
    public bool movable = true, visible = true, ordinaryTooltip = true, counterAllowed, reserved, destroyed;
    public bool forceDisableActivate, forceDisableUse, activateDefault, isCombatBackpack, useSpriteFromMod, triggerOverwatch, canUseOutsideCombat;
    public object contentWindow, recipeManager;
    public List<object> effects = new List<object>();
    public List<string> itemTypes = new List<string>();
    public string intrinsic = "default";
    public readonly HashSet<string> tags = new HashSet<string>();
    public List<string> types = new List<string>();
    public Delegate isItemStateValidFunc, mayInventoryAddItemFunc, maxSlotAmountItemFunc, maxSlotAddItemFunc, slotPriorityFunc;
    public Delegate mayInventoryRemoveItemFunc, minSlotAmountItemFunc, maxSlotRemoveItemFunc, onChangeAmountItemFunc, onSlotChangeAmountItemFunc, onSlotRemoveItemFunc, onSlotAddItemFunc;
    public Func<GameItem, GameItem, bool> mayStackItemFunc;
    public object parentItemShapeModifierForItemFunc, parentItemStateModifierForItemFunc;
    public int MaxNumRemove() => unitCount;
    private static int nextId;
    public int GetUniqueID() => uniqueId == 0 ? uniqueId = ++nextId : uniqueId;
    public string GetDisplayName(bool _) => name ?? identifier;
    public bool IsTag(string value) => tags.Contains(value);
    public void Destroy() { destroyed = true; }
    public SlotMarker GetStackSlot(GameItem incoming) => incoming != null && mayStackItemFunc?.Invoke(this, incoming) == true &&
        incoming.mayStackItemFunc?.Invoke(this, incoming) == true && parentInventory != null
        ? new SlotMarker { inventory = parentInventory, item = incoming, targetItem = this, numTransfer = maxStack - unitCount } : null;
}
public sealed class GameItemElement : GameItem { }
public class GameInventory
{
    public readonly List<GameItem> childItems = new List<GameItem>();
    public bool locked;
    public GameItem owner;
    public Delegate mayInventoryRemoveItemFunc, minSlotAmountItemFunc, maxSlotRemoveItemFunc, maxSlotAmountItemFunc, maxSlotAddItemFunc, onSlotChangeAmountItemFunc;
    public Delegate onSlotRemoveItemFunc, onSlotRemoveItemEndFunc, onSlotRemoveItemLateFunc, onSlotAddItemFunc, onSlotAddItemEndFunc, onSlotAddItemLateFunc;
    public bool IsInsertLocked() => locked;
    public GameItem GetParentItem() => owner;
    public virtual SlotMarker TryInventorySlot(GameItem item, int amount, GridShape shape) => null;
    public virtual SlotMarker TryFindOneValidInventorySlot(GameItem item) => null;
}
public sealed class GameGridInventory : GameInventory
{
    public int capacity;
    public Delegate isItemStateValidFunc, slotPriorityFunc;
    public object parentItemShapeModifierForItemFunc, parentItemStateModifierForItemFunc;
    public Func<GameItem, GameInventory, bool> mayInventoryAddItemFunc;
    public override SlotMarker TryInventorySlot(GameItem item, int amount, GridShape shape)
    {
        if (locked || shape == null || shape.minX < 0 || shape.minX >= capacity ||
            childItems.Any(other => !ReferenceEquals(other, item) && other.modifiedShape.minX == shape.minX)) return null;
        return new SlotMarker { inventory = this, item = item, itemGridShape = shape.Clone(), numTransfer = amount };
    }
    public override SlotMarker TryFindOneValidInventorySlot(GameItem item) => ShopAssist.GridPlacements.Empty(this, item, null);
}
public sealed class SlotMarker
{
    public static string Failure;
    public GameInventory inventory;
    public GameItem item, targetItem;
    public GridShape itemGridShape;
    public int numTransfer;
    public int TryAcceptOnce(int amount)
    {
        if (Failure == "before") throw new InvalidOperationException("before");
        if (targetItem != null)
        {
            targetItem.unitCount += amount;
            if (Failure == "after_target") throw new InvalidOperationException("after target");
            item.unitCount -= amount;
            if (item.unitCount == 0) { item.parentInventory.childItems.Remove(item); item.parentInventory = null; }
            return amount;
        }
        item.parentInventory.childItems.Remove(item);
        item.parentInventory = inventory;
        item.modifiedShape = itemGridShape.Clone();
        inventory.childItems.Add(item);
        if (Failure == "after_move") throw new InvalidOperationException("after move");
        return amount;
    }
}
public static class ContainerHelper { }
public static class GeneralHelper { public static bool MayPlayerInsertInto(GameInventory target) => true; }
public static class WineHelper { public const string WINE_YEAST_REUSABLE_TAG = "yeast"; }
public sealed class PlayerStore
{
    public static bool HasInstance = true;
    public static PlayerStore Instance = new PlayerStore();
    public string runID = "run";
    public int saveSlotId = 1;
    public object ModData() => this;
}
public static class ModHelper
{
    public static readonly Dictionary<string, string> Data = new Dictionary<string, string>();
    public static bool FailNextWrite;
    public static int WritesBeforeReadFailure = -1;
    public static bool FailNextRead;
    public static string FailWriteKey;
    public static Action<string, string> AfterWrite;
    public static string GetModData(string id, string key, string fallback)
    {
        if (FailNextRead) { FailNextRead = false; throw new InvalidOperationException("read failed"); }
        return Data.TryGetValue(key, out string value) ? value : fallback;
    }
    public static void SetModData(string id, string key, string value)
    {
        if (FailNextWrite) { FailNextWrite = false; throw new InvalidOperationException("write failed"); }
        if (FailWriteKey == key) { FailWriteKey = null; throw new InvalidOperationException("targeted write failed"); }
        Data[key] = value;
        if (WritesBeforeReadFailure > 0 && --WritesBeforeReadFailure == 0) { FailNextRead = true; WritesBeforeReadFailure = -1; }
        AfterWrite?.Invoke(key, value);
    }
}
public sealed class GenericConfirmationUIManager
{
    public static GenericConfirmationUIManager Instance = new GenericConfirmationUIManager();
    public bool Visible;
    public string Text, ConfirmText, CancelText;
    public Action Confirm, Cancel;
    public void OpenPanel(string text, string confirm, string cancel, Action accepted, Action cancelled)
    { Visible = true; Text = text; ConfirmText = confirm; CancelText = cancel; Confirm = accepted; Cancel = cancelled; }
    public void ClosePanel() { Visible = false; }
}
namespace UnityEngine { public static class Debug { public static void LogWarning(object value) { } } }

namespace ShopAssist
{
    internal sealed class CustomerIndicators
    {
        internal ICollection<string> KnownTypes(GameItemElement item) => item.types;
        internal static bool HasOrdinaryTooltip(GameItemElement item) => item.ordinaryTooltip;
    }
    internal static class VisibleInventoryViews { internal static bool Visible(GameItemElement item) => item.visible; }
    internal static class RequestBook { internal static bool Reserved(GameItem item) => item?.reserved == true; }
    internal static class ModEntry { internal const string Id = "test"; }
    internal static class ShopSettings
    {
        internal static string Text(string key) => key;
        internal static string Format(string key, params object[] values) => key + " " + string.Join(" | ", values);
    }
    internal static class ShopNotice { internal static string Last; internal static void Show(string value) { Last = value; } }
    internal sealed class AttentionItem
    {
        internal string Key, Feature, Title, Location, ReasonKey, ResolveLabelKey;
        internal int ItemId;
        internal bool NeedsInspection;
        internal Action Retry, Resolve;
    }
    internal static class AttentionBook
    {
        internal static readonly Dictionary<string, Func<IEnumerable<AttentionItem>>> Providers = new Dictionary<string, Func<IEnumerable<AttentionItem>>>();
        internal static void Register(string name, Func<IEnumerable<AttentionItem>> provider) => Providers.Add(name, provider);
        internal static void Unregister(string name) => Providers.Remove(name);
        internal static void Changed() { }
    }
    internal static class ShopData
    {
        private static readonly Dictionary<string, object> objects = new Dictionary<string, object>();
        internal static string Encode<T>(T value)
        {
            if (value is object[] array) return JsonSerializer.Serialize(array);
            string key = Guid.NewGuid().ToString("N"); objects[key] = value; return key;
        }
        internal static T Decode<T>(string raw, int max)
        {
            if (raw == "null") return default;
            if (raw == null || raw.Length > max || !objects.TryGetValue(raw, out object value)) throw new FormatException("Invalid data");
            return (T)value;
        }
    }
    internal static class SettledCounterEvidence
    {
        internal static bool Allows(GameItem item) => false;
    }
    internal static class InventoryAutoInsert
    {
        internal static bool KnownMutationPatches() => true;
        internal static bool SafeItem(GameItem item, bool allowStack, bool allowCounter = false) => item != null && item.onSlotRemoveItemFunc == null &&
            item.onSlotAddItemFunc == null && item.onChangeAmountItemFunc == null && item.onSlotChangeAmountItemFunc == null;
        internal static bool SafeTransferInventory(GameInventory inventory, GameItem source = null) => inventory != null &&
            inventory.onSlotRemoveItemFunc == null && inventory.onSlotRemoveItemEndFunc == null && inventory.onSlotRemoveItemLateFunc == null &&
            inventory.onSlotAddItemFunc == null && inventory.onSlotAddItemEndFunc == null && inventory.onSlotAddItemLateFunc == null &&
            inventory.onSlotChangeAmountItemFunc == null;
        internal static bool SafeTransferInventoryWithSnapshot(GameInventory inventory, GameItem source,
            IDictionary<GameInventory, bool> snapshot) => SafeTransferInventory(inventory, source);
    }
    internal static class InventoryArrangeTransaction
    {
        internal static string StateSignature(GameItem item) => item.identifier.Length + ":" + item.identifier + "|" + item.uniqueId + "|" + item.reserved + "|" + item.intrinsic;
    }
    internal static class InventorySortPlan
    {
        internal static bool SafeToCheck(GameGridInventory grid, GameItem item) => true;
        internal static bool SameShape(GridShape a, GridShape b) => a != null && b != null && a.minX == b.minX;
    }
    internal static class GridPlacements
    {
        internal static SlotMarker Exact(GameGridInventory grid, GameItem item, GridShape shape, int amount = -1)
        {
            var marker = grid?.TryInventorySlot(item, amount < 0 ? item.unitCount : amount, shape);
            return marker?.targetItem == null && marker?.numTransfer >= (amount < 0 ? item.unitCount : amount) ? marker : null;
        }
        internal static SlotMarker Empty(GameGridInventory grid, GameItem item, IReadOnlyList<GridShape> reserved)
        {
            var occupied = new HashSet<int>(grid.childItems.Select(child => child.modifiedShape.minX));
            foreach (GridShape shape in reserved) occupied.Add(shape.minX);
            for (int i = 0; i < grid.capacity; i++)
                if (!occupied.Contains(i)) return new SlotMarker { inventory = grid, item = item, itemGridShape = new Shape { minX = i }, numTransfer = item.unitCount };
            return null;
        }
    }
    internal static class ShopInventory
    {
        internal static bool InStore = true;
        internal sealed class Location
        {
            internal GameInventory Inventory;
            internal bool Ordinary = true, IsMachine = false, MainStock = true;
            internal string Name, id;
            internal string Key() => id;
        }
        internal static readonly List<Location> Locations = new List<Location>();
        internal static List<Location> OwnedLocations(bool onlyOpen) => Locations;
        internal static bool Owned(GameItem item) => item != null && item.parentInventory != null;
        internal static bool Movable(GameItem item) => item.movable;
        internal static bool CanRouteFromCounter(GameItem item, Location source) => source.id == "counter" && item.counterAllowed;
        internal static bool IsMainStockBranch(Location location) => location != null && location.MainStock;
        internal static bool WouldContainSelf(GameItem item, GameInventory target)
        {
            for (GameItem parent = target.GetParentItem(); parent != null; parent = parent.parentInventory?.GetParentItem())
                if (ReferenceEquals(item, parent)) return true;
            return false;
        }
    }
}
