using System;
using System.Collections.Generic;
using System.Linq;

namespace Newtonsoft.Json { [AttributeUsage(AttributeTargets.Field)] public sealed class JsonRequiredAttribute : Attribute { } }
public interface GridShape { int minX { get; } GridShape Clone(); }
public sealed class Shape : GridShape { public int minX { get; set; } public GridShape Clone() => new Shape { minX = minX }; }
public class GameItem
{
    public string identifier;
    public GameInventory parentInventory;
    public GridShape modifiedShape;
    public int unitCount = 1;
    public bool movable = true, visible = true, ordinaryTooltip = true, counterAllowed;
    public List<string> types = new List<string>();
    public object isItemStateValidFunc, mayInventoryAddItemFunc, maxSlotAmountItemFunc, maxSlotAddItemFunc, slotPriorityFunc;
    public object parentItemShapeModifierForItemFunc, parentItemStateModifierForItemFunc;
    public int MaxNumRemove() => unitCount;
}
public sealed class GameItemElement : GameItem { }
public class GameInventory
{
    public readonly List<GameItem> childItems = new List<GameItem>();
    public bool locked;
    public GameItem owner;
    public bool IsInsertLocked() => locked;
    public GameItem GetParentItem() => owner;
}
public sealed class GameGridInventory : GameInventory
{
    public int capacity;
    public object isItemStateValidFunc, maxSlotAmountItemFunc, maxSlotAddItemFunc, slotPriorityFunc;
    public object parentItemShapeModifierForItemFunc, parentItemStateModifierForItemFunc;
    public Func<GameItem, GameInventory, bool> mayInventoryAddItemFunc;
}
public sealed class SlotMarker { public GameItem targetItem; public GridShape itemGridShape; public int numTransfer; }
public static class ContainerHelper { }
public static class GeneralHelper { public static bool MayPlayerInsertInto(GameInventory target) => true; }

namespace ShopAssist
{
    internal sealed class CustomerIndicators
    {
        internal ICollection<string> KnownTypes(GameItemElement item) => item.types;
        internal static bool HasOrdinaryTooltip(GameItemElement item) => item.ordinaryTooltip;
    }
    internal static class VisibleInventoryViews { internal static bool Visible(GameItemElement item) => item.visible; }
    internal static class RequestBook { internal static bool Reserved(GameItem item) => false; }
    internal static class InventorySortPlan
    {
        internal static bool SafeToCheck(GameGridInventory grid, GameItem item) => true;
        internal static bool SameShape(GridShape a, GridShape b) => a.minX == b.minX;
    }
    internal static class GridPlacements
    {
        internal static SlotMarker Empty(GameGridInventory grid, GameItem item, IReadOnlyList<GridShape> reserved)
        {
            var occupied = new HashSet<int>(grid.childItems.Select(child => child.modifiedShape.minX));
            foreach (GridShape shape in reserved) occupied.Add(shape.minX);
            for (int i = 0; i < grid.capacity; i++)
                if (!occupied.Contains(i)) return new SlotMarker { itemGridShape = new Shape { minX = i }, numTransfer = item.unitCount };
            return null;
        }
    }
    internal static class ShopInventory
    {
        internal sealed class Location
        {
            internal GameInventory Inventory;
            internal bool Ordinary = true, IsMachine = false, MainStock = true;
            internal string Name, id;
            internal string Key() => id;
        }
        internal static readonly List<Location> Locations = new List<Location>();
        internal static List<Location> OwnedLocations(bool onlyOpen) => Locations;
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
