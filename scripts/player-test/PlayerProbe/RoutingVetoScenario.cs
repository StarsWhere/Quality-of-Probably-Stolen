using System;
using System.Reflection;
using UnityEngine;

// A native grid test double refuses Expel to exercise the game's SlotMarker failure path.
public sealed class RoutingVetoScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private float started = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - started > 75) throw new InvalidOperationException("Routing veto QA timed out");
        if (!start.Tick()) return false;
        var mod = ModLoader.Mods.Find(value => value.Manifest != null && value.Manifest.ID == "starswhere.shopassist");
        Require(mod?.Instance != null, "Mod is unavailable");
        var type = mod.Instance.GetType().Assembly.GetType("ShopAssist.ShopInventory", true);
        var locationType = type.GetNestedType("Location", BindingFlags.NonPublic);
        var move = type.GetMethod("TryMoveIntact", BindingFlags.Static | BindingFlags.NonPublic);
        Require(locationType != null && move != null, "Shared inventory transfer interface changed");
        var from = new RefusingGrid(6, 6);
        var target = new GameGridInventory(6, 6);
        var item = DirectoryMaster.Item("processed_meat");
        Require(item != null, "Native fixture is unavailable");
        GeneralHelper.SetItemOwned(item, true);
        var pose = new GridShapeBuilder(item.modifiedShape).SetPosition(3, 3);
        var placement = from.TryInventorySlot(item, item.unitCount, pose);
        Require(placement != null && placement.numTransfer == item.unitCount && placement.TryAcceptOnce(item.unitCount) == item.unitCount,
            "Native source grid did not accept the fixture");
        GridShape original = item.modifiedShape.Clone();
        int count = item.unitCount;
        object sourceLocation = Activator.CreateInstance(locationType, true);
        object targetLocation = Activator.CreateInstance(locationType, true);
        Set(locationType, sourceLocation, "Inventory", from); Set(locationType, sourceLocation, "Ordinary", true);
        Set(locationType, targetLocation, "Inventory", target); Set(locationType, targetLocation, "Ordinary", true);
        from.refuse = true;
        object[] arguments = { item, sourceLocation, targetLocation, null, null };
        bool moved = (bool)move.Invoke(null, arguments);
        Require(!moved && from.refusals == 1 && ReferenceEquals(item.parentInventory, from) && from.childItems.Contains(item) &&
            !target.childItems.Contains(item) && item.unitCount == count && SamePose(item.modifiedShape, original),
            "Rejected Expel did not preserve the original item and pose; reason=" + arguments[3]);
        result = "PASS native rejected Expel retains the same item count, source and original pose";
        return true;
    }

    private static void Set(Type type, object instance, string field, object value)
    {
        var info = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        Require(info != null, "Missing location field " + field);
        info.SetValue(instance, value);
    }
    private static bool SamePose(GridShape a, GridShape b) => a != null && b != null && a.minX == b.minX && a.minY == b.minY &&
        a.orientation == b.orientation && a.flipped == b.flipped;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

public sealed class RefusingGrid : GameGridInventory
{
    public bool refuse;
    public int refusals;
    public RefusingGrid(int width, int height) : base(width, height) { }
    public override bool Expel(GameItem item)
    {
        if (!refuse) return base.Expel(item);
        refusals++;
        return false;
    }
}
