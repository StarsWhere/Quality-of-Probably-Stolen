using System;
using System.Collections.Generic;

public interface GridShape
{
    int width { get; }
    int height { get; }
    int minX { get; }
    int minY { get; }
    int globalWidth { get; }
    int globalHeight { get; }
    int maxX { get; }
    int maxY { get; }
    int orientation { get; }
    bool flipped { get; }
    byte GetLocal(int x, int y);
    byte GetOutsideBounds();
    byte Get(int x, int y);
    GridShape Clone();
    bool Intersects(GridShape other);
}

public sealed class GridShapeBuilder : GridShape
{
    public static int intersectionChecks;
    private readonly byte[] data;
    public int width { get; }
    public int height { get; }
    public int minX { get; private set; }
    public int minY { get; private set; }
    public int orientation { get; private set; }
    public bool flipped { get; private set; }
    public int globalWidth => orientation % 2 == 0 ? width : height;
    public int globalHeight => orientation % 2 == 0 ? height : width;
    public int maxX => minX + globalWidth - 1;
    public int maxY => minY + globalHeight - 1;
    public GridShapeBuilder(int width, int height, params byte[] data)
    {
        this.width = width; this.height = height;
        this.data = data.Length == 0 ? new byte[width * height] : (byte[])data.Clone();
    }
    public GridShapeBuilder(GridShape original) : this(original.width, original.height)
    {
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) data[y * width + x] = original.GetLocal(x, y);
        minX = original.minX; minY = original.minY; orientation = original.orientation; flipped = original.flipped;
    }
    public GridShapeBuilder SetPosition(int x, int y) { minX = x; minY = y; return this; }
    public GridShapeBuilder SetOrientation(int value) { orientation = value % 4; return this; }
    public GridShapeBuilder SetFlipped(bool value) { flipped = value; return this; }
    public GridShapeBuilder SetTransform(GridShape value)
    { minX = value.minX; minY = value.minY; orientation = value.orientation; flipped = value.flipped; return this; }
    public GridShape Build() => Clone();
    public GridShape Clone() => new GridShapeBuilder(this);
    public byte GetOutsideBounds() => 0;
    public byte GetLocal(int x, int y) => x < 0 || y < 0 || x >= width || y >= height ? (byte)0 : data[y * width + x];
    public byte Get(int x, int y)
    {
        x -= minX; y -= minY;
        if (x < 0 || y < 0 || x >= globalWidth || y >= globalHeight) return 0;
        int localX, localY;
        switch (orientation)
        {
            case 1: localX = width - 1 - y; localY = x; break;
            case 2: localX = width - 1 - x; localY = height - 1 - y; break;
            case 3: localX = y; localY = height - 1 - x; break;
            default: localX = x; localY = y; break;
        }
        if (flipped) localX = width - 1 - localX;
        return GetLocal(localX, localY);
    }
    public bool Intersects(GridShape other)
    {
        intersectionChecks++;
        int right = Math.Min(minX + globalWidth, other.minX + other.globalWidth);
        int bottom = Math.Min(minY + globalHeight, other.minY + other.globalHeight);
        for (int y = Math.Max(minY, other.minY); y < bottom; y++)
            for (int x = Math.Max(minX, other.minX); x < right; x++)
                if (Get(x, y) != 0 && Get(x, y) == other.Get(x, y)) return true;
        return false;
    }
}

public class GameItem
{
    public string identifier;
    public int uniqueId;
    public TagSystem state = new TagSystem(), modifiedState = new TagSystem();
    public List<ItemFeature> itemFeatures = new List<ItemFeature>();
    public GridShape shape, modifiedShape;
    public GameGridInventory parentInventory;
    public int unitCount = 1;
    public int modifiedXOrigin, modifiedYOrigin;
    public bool movable = true;
    public bool reserved;
    public object contentWindow, examineWindow;
    public object isItemStateValidFunc, mayInventoryRemoveItemFunc, minSlotAmountItemFunc, maxSlotRemoveItemFunc;
    public object parentItemShapeModifierForItemFunc, parentItemStateModifierForItemFunc;
    public object parentItemSpriteModifierForItemFunc, getItemSpriteModifierForItemFunc;
    public int validations;
    public Action onValidate;
    public int MaxNumRemove() => movable ? unitCount : 0;
    public void Validate() { validations++; onValidate?.Invoke(); }
}
public sealed class GameItemElement : GameItem { }
public sealed class ItemFeature { public string identifier; public int value; public ItemCondition condition; }
public sealed class ItemCondition { public string identifier; public int value; }

public class GameInventory
{
    public virtual bool Expel(GameItem item) => false;
}

public sealed class GameGridInventory : GameInventory
{
    public string scope = "open";
    public GridShape inventoryShape;
    public readonly List<GameItem> childItems = new List<GameItem>();
    public bool insertLocked, removeLocked, denyPlacements;
    public object isItemStateValidFunc, mayInventoryRemoveItemFunc, minSlotAmountItemFunc, maxSlotRemoveItemFunc;
    public object parentItemShapeModifierForItemFunc, parentItemStateModifierForItemFunc, parentItemSpriteModifierForItemFunc;
    public int validations, placementChecks;
    public Action onValidate;
    public Action onPlacementCheck;
    public Action<GameItem> onAccept;
    public void Validate() { validations++; onValidate?.Invoke(); }
    public bool IsInsertLocked() => insertLocked;
    public bool IsRemoveLocked() => removeLocked;
    public override bool Expel(GameItem item)
    {
        if (item.parentInventory != this || !childItems.Remove(item)) return false;
        item.parentInventory = null;
        return true;
    }
    public bool CheckShapePlacement(GameItem item, GridShape shape, List<GameItem> ignored)
    {
        placementChecks++;
        onPlacementCheck?.Invoke();
        if (denyPlacements || shape.minX < 0 || shape.minY < 0 ||
            shape.minX + shape.globalWidth > inventoryShape.width || shape.minY + shape.globalHeight > inventoryShape.height) return false;
        foreach (GameItem other in childItems)
            if (item != other && !ignored.Contains(other) && shape.Intersects(other.modifiedShape)) return false;
        return true;
    }
}

public sealed class TagState
{
    public bool valueEnabled, valueBool;
    public int valueInt;
    public float valueFloat;
    public long valueLong;
    public double valueDouble;
    public string valueString;
}
public sealed class TagSystem { public Dictionary<string, TagState> dict = new Dictionary<string, TagState>(); }
public sealed class PlayerStore
{
    public static PlayerStore Instance = new PlayerStore();
    public string runID = "test";
    public int saveSlotId;
}
public sealed class SlotMarker
{
    public GameGridInventory inventory;
    public GameItem item;
    public GridShape shape;
    public int TryAcceptOnce(int amount)
    {
        if (item.parentInventory != null && !item.parentInventory.Expel(item)) return 0;
        item.modifiedShape = shape.Clone();
        item.parentInventory = inventory;
        inventory.childItems.Add(item);
        inventory.onAccept?.Invoke(item);
        return amount;
    }
}
public static class GeneralHelper { public static bool MayPlayerInsertInto(GameGridInventory grid) => true; }
namespace ShopAssist
{
    internal static class RequestBook { internal static bool Reserved(GameItem item) => item.reserved; }
    internal static class ShopInventory
    {
        internal static bool InStore => true;
        internal static bool Movable(GameItem item) => item.movable;
    }
    internal static class GridPlacements
    {
        internal static SlotMarker Exact(GameGridInventory grid, GameItem item, GridShape shape) =>
            grid.CheckShapePlacement(item, shape, new List<GameItem> { item }) ?
                new SlotMarker { inventory = grid, item = item, shape = shape.Clone() } : null;
    }
}
