using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ShopAssist;

static class FieldPackingTests
{
    static int assertions;
    static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    static (FieldPacking feature, ShopSettings settings, EmporiumEntry entry) Setup(int width = 2, int height = 2)
    {
        EmporiumEntry.Instance = new EmporiumEntry();
        var e = EmporiumEntry.Instance;
        var equipped = new GameItem { Cargo = new GameGridInventory(width, height), parentInventory = e.afterhourPocketSlotInvBackpack };
        equipped.Cargo.Owner = equipped; e.afterhourPocketSlotInvBackpack.childItems.Add(equipped);
        var settings = new ShopSettings();
        var feature = new FieldPacking(settings);
        settings.Set("f36.enabled", true); settings.Set("f36.space", 0);
        WholeStackTransfer.Applied.Clear(); WholeStackTransfer.Ready = true; WholeStackTransfer.FailNext = false;
        WholeStackTransfer.FailAt = WholeStackTransfer.Calls = 0; WholeStackTransfer.AfterApply = null;
        WholeStackTransfer.Replacement = null; WholeStackTransfer.FailBegin = false; WholeStackTransfer.DetachAt = 0;
        WholeStackTransfer.BlockRecovery = false; WholeStackTransfer.AfterDetach = null;
        ShopNotice.Last = null; GameItem.ValueReads = 0; GenericConfirmationUIManager.Instance = null;
        return (feature, settings, e);
    }
    static GameItem Add(GameInventory inventory, string name, int width, int height, long value, int x = 0, int y = 0, long? basis = null)
    {
        var item = new GameItemElement { Name = name, Value = value, Base = basis ?? value,
            modifiedShape = new GridShape(width, height, x, y), parentInventory = inventory };
        inventory.childItems.Add(item); return item;
    }
    static GameItem AddBag(GameInventory inventory, string name, long value, int x = 0)
    {
        var bag = Add(inventory, name, 1, 1, value, x);
        bag.Backpack = true; bag.Cargo = new GameGridInventory(3, 3) { Owner = bag };
        bag.contentWindow = new ContentWindow { childElement = bag.Cargo };
        return bag;
    }
    static void Main()
    {
        var (feature, settings, e) = Setup();
        var a = Add(e.afterhourInventory, "bulky", 2, 2, 100);
        var b = Add(e.afterhourInventory, "small", 1, 1, 60, 3);
        var c = Add(e.afterhourInventory, "small", 1, 1, 60, 4);
        var d = Add(e.afterhourInventory, "small", 1, 1, 60, 5);
        settings.Run();
        Check(new[] { a, b, c, d }.All(i => i.parentInventory != e.afterhourInventory), "Joint backpack and hands planning must avoid wasting a hand on a small good");
        Check(ReferenceEquals(a.parentInventory, e.afterhourPocketSlotInvLeft) || ReferenceEquals(a.parentInventory, e.afterhourPocketSlotInvRight), "The bulky good belongs in a hand in the best candidate plan");
        Check(ShopNotice.Last.Detail == "f36.approximate", "Bounded search must disclose its approximation");

        (feature, settings, e) = Setup(1, 1);
        e.afterhourPocketSlotInvLeft.Locked = e.afterhourPocketSlotInvRight.Locked = true;
        a = Add(e.afterhourInventory, "estimate", 1, 1, 100, basis: 1);
        b = Add(e.afterhourInventory, "base", 1, 1, 50, 2, basis: 200);
        settings.Set("f36.valuation", 1); settings.Run();
        Check(b.parentInventory == e.afterhourPocketSlotInvBackpack.childItem.Cargo && a.parentInventory == e.afterhourInventory, "Base valuation must change the selected good");

        (feature, settings, e) = Setup(1, 1);
        var bag = e.afterhourPocketSlotInvBackpack.childItem.Cargo;
        e.afterhourPocketSlotInvLeft.Locked = e.afterhourPocketSlotInvRight.Locked = true;
        a = Add(bag, "carried", 1, 1, 5);
        b = Add(e.afterhourInventory, "better", 1, 1, 50, 2);
        settings.Run();
        Check(a.parentInventory == bag && b.parentInventory == e.afterhourInventory, "Replacement must be off by default");
        settings.Set("f36.replace", true); settings.Run();
        Check(a.parentInventory == e.afterhourInventory && b.parentInventory == bag, "Opted in bag replacement must improve value");
        Check(WholeStackTransfer.Applied.Count == 2 && WholeStackTransfer.Applied[0] == a && WholeStackTransfer.Applied[1] == b,
            "Replacement must put the old good safely on the ground before taking the new one");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        a = Add(e.afterhourPocketSlotInvLeft, "carried", 1, 1, 5);
        b = Add(e.afterhourInventory, "better", 4, 4, 50, 2);
        settings.Set("f36.replace", true); settings.Run();
        Check(a.parentInventory == e.afterhourInventory && b.parentInventory == e.afterhourPocketSlotInvLeft, "Hand replacement must not impose backpack footprint limits");

        (feature, settings, e) = Setup(1, 1);
        e.afterhourPocketSlotInvLeft.Locked = e.afterhourPocketSlotInvRight.Locked = true;
        a = Add(e.afterhourInventory, "container", 1, 1, 100); a.contentWindow = new object();
        b = Add(e.afterhourInventory, "unknown callback", 1, 1, 50, 2); b.Unsafe = true;
        settings.Run();
        Check(GameItem.ValueReads == 0 && WholeStackTransfer.Applied.Count == 0, "Containers and unknown callbacks must be rejected before value reads");
        Check(ShopNotice.Last.Items.All(row => row.Reason == "f36.leave_unsafe"), "Unsafe skips must remain visible in the result");

        (feature, settings, e) = Setup();
        a = Add(e.afterhourInventory, "custom tooltip", 1, 1, 100); a.CustomTooltip = true;
        settings.Run();
        Check(GameItem.ValueReads == 0 && a.parentInventory == e.afterhourInventory, "A custom tooltip must not be replaced with an assumed ordinary value");

        (feature, settings, e) = Setup(1, 1);
        e.afterhourPocketSlotInvLeft.Locked = e.afterhourPocketSlotInvRight.Locked = true;
        a = Add(e.afterhourInventory, "reserve", 1, 1, 100);
        settings.Set("f36.space", 1); settings.Run();
        Check(a.parentInventory == e.afterhourInventory, "Reserved backpack cells must remain free");

        (feature, settings, e) = Setup();
        a = Add(e.afterhourInventory, "first", 1, 1, 100); b = Add(e.afterhourInventory, "later", 1, 1, 50, 2);
        WholeStackTransfer.FailNext = true; settings.Run();
        Check(WholeStackTransfer.Applied.Count == 0 && a.parentInventory == e.afterhourInventory && b.parentInventory == e.afterhourInventory,
            "A failed checked movement must stop the rest of the batch");
        Check(ShopNotice.Last.Items.Any(row => row.Reason == "operation.not_attempted"), "Unattempted goods must be reported after failure");
        Check(ShopNotice.Last.Detail == "f36.stopped", "A stopped batch must not report an approximate success");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        var foundBag = AddBag(e.afterhourInventory, "loaded", 10);
        var contents = Add(foundBag.Cargo, "stack", 1, 1, 20); contents.unitCount = 5;
        a = Add(e.afterhourInventory, "ordinary", 1, 1, 25, 2);
        settings.Run();
        Check(foundBag.parentInventory == e.afterhourPocketSlotInvLeft && contents.parentInventory == foundBag.Cargo &&
            contents.unitCount == 5 && a.parentInventory == e.afterhourInventory,
            "Visible contents must contribute their native unit value without moving them separately");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        foundBag = AddBag(e.afterhourInventory, "native stacked contents", 10);
        contents = Add(foundBag.Cargo, "stack", 1, 1, 20); contents.unitCount = 5;
        a = Add(e.afterhourInventory, "higher native quote", 1, 1, 90, 2);
        settings.Run();
        Check(a.parentInventory == e.afterhourPocketSlotInvLeft && foundBag.parentInventory == e.afterhourInventory,
            "A child's stack quantity must not be multiplied into the native whole-container quote");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        foundBag = AddBag(e.afterhourInventory, "ignore children", 10); foundBag.IgnoreChildren = true;
        contents = Add(foundBag.Cargo, "ignored stack", 1, 1, 100); contents.unitCount = 5;
        a = Add(e.afterhourInventory, "ordinary", 1, 1, 90, 2);
        settings.Run();
        Check(a.parentInventory == e.afterhourPocketSlotInvLeft && foundBag.parentInventory == e.afterhourInventory && contents.Reads == 0,
            "The native ignore-child-value tag must exclude child prices from ranking");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        foundBag = AddBag(e.afterhourInventory, "hidden", 10);
        contents = Add(foundBag.Cargo, "unseen", 1, 1, 100); contents.Visible = false;
        settings.Run();
        Check(foundBag.parentInventory == e.afterhourInventory && foundBag.Reads == 0 && contents.Reads == 0 &&
            ShopNotice.Last.Items.Any(row => row.ItemId == foundBag.uniqueId && row.Reason == "f36.leave_unsafe"),
            "No part of a backpack may be valued before every child is known to be visible");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        foundBag = AddBag(e.afterhourInventory, "outer", 10);
        var nestedBag = AddBag(foundBag.Cargo, "inner", 10); nestedBag.IgnoreChildren = true;
        contents = Add(nestedBag.Cargo, "nested contents", 1, 1, 100); contents.unitCount = 5;
        a = Add(e.afterhourInventory, "ordinary", 1, 1, 90, 2);
        settings.Run();
        Check(foundBag.parentInventory == e.afterhourPocketSlotInvLeft && a.parentInventory == e.afterhourInventory && contents.Reads > 0,
            "Only the root ignore-child-value tag suppresses the flattened descendants in the native value");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        foundBag = AddBag(e.afterhourPocketSlotInvLeft, "carried loaded", 10);
        contents = Add(foundBag.Cargo, "carried contents", 1, 1, 120); contents.unitCount = 3;
        a = Add(e.afterhourInventory, "less valuable", 1, 1, 100, 2);
        settings.Set("f36.replace", true); settings.Run();
        Check(foundBag.parentInventory == e.afterhourPocketSlotInvLeft && a.parentInventory == e.afterhourInventory,
            "Replacement must compare the value of the loaded carried backpack, not just its shell");
        a.Value = a.Base = 200; settings.Run();
        Check(foundBag.parentInventory == e.afterhourInventory && contents.parentInventory == foundBag.Cargo &&
            a.parentInventory == e.afterhourPocketSlotInvLeft, "A better item may replace the complete loaded backpack");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        foundBag = AddBag(e.afterhourPocketSlotInvLeft, "recover loaded", 10);
        contents = Add(foundBag.Cargo, "retained", 1, 1, 5);
        a = Add(e.afterhourInventory, "incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true); WholeStackTransfer.FailAt = 2; settings.Run();
        Check(foundBag.parentInventory == e.afterhourPocketSlotInvLeft && contents.parentInventory == foundBag.Cargo &&
            a.parentInventory == e.afterhourInventory && ShopNotice.Last.Detail == "f36.stopped",
            "An unchanged second transfer must restore the original whole backpack and stop the batch");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        foundBag = AddBag(e.afterhourPocketSlotInvLeft, "stale loaded", 10);
        contents = Add(foundBag.Cargo, "retained", 1, 1, 5);
        a = Add(e.afterhourInventory, "incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true); WholeStackTransfer.AfterApply = item => { if (item == foundBag) a.Value = 1; };
        settings.Run();
        Check(foundBag.parentInventory == e.afterhourPocketSlotInvLeft && contents.parentInventory == foundBag.Cargo &&
            a.parentInventory == e.afterhourInventory, "A price change between the two legs must restore the outgoing item");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        var resident = Add(e.afterhourPocketSlotInvLeft, "confirmation resident", 1, 1, 5);
        var incoming = Add(e.afterhourInventory, "confirmation incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true);
        WholeStackTransfer.AfterApply = item => { if (item == resident) GenericConfirmationUIManager.Instance = new GenericConfirmationUIManager { panel = new Panel { activeInHierarchy = true } }; };
        settings.Run();
        Check(resident.parentInventory == e.afterhourPocketSlotInvLeft && incoming.parentInventory == e.afterhourInventory &&
            WholeStackTransfer.Ready && FieldPacking.CanLeave(), "A confirmation between replacement legs must restore the original item before stopping");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        resident = Add(e.afterhourPocketSlotInvLeft, "exception resident", 1, 1, 5);
        incoming = Add(e.afterhourInventory, "exception incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true);
        WholeStackTransfer.AfterApply = item => { if (item == resident) incoming.ThrowOnRead = true; };
        settings.Run();
        Check(resident.parentInventory == e.afterhourPocketSlotInvLeft && incoming.parentInventory == e.afterhourInventory &&
            WholeStackTransfer.Ready && FieldPacking.CanLeave(), "An exception between legs must restore the original item and close the replacement record");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        resident = Add(e.afterhourPocketSlotInvLeft, "blocked resident", 1, 1, 5);
        incoming = Add(e.afterhourInventory, "blocked incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true);
        WholeStackTransfer.AfterApply = item => { if (item == resident) { incoming.Value = 1; e.afterhourPocketSlotInvLeft.Locked = true; } };
        settings.Run();
        Check(resident.parentInventory == e.afterhourInventory && !WholeStackTransfer.Ready && !FieldPacking.CanLeave(),
            "A failed restore must retain the replacement record and prevent leaving the old item on the ground");
        var mergeGuard = typeof(FieldPacking).GetNestedType("RecoveryMergeGuard", BindingFlags.NonPublic)
            .GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
        object[] mergeArguments = { new SlotMarker { item = resident, targetItem = incoming }, 4 };
        Check(!(bool)mergeGuard.Invoke(null, mergeArguments) && (int)mergeArguments[1] == 0,
            "The protected item cannot be merged away before it is carried");
        e.afterhourPocketSlotInvLeft.Locked = false;
        e.afterhourInventory.childItems.Remove(resident); e.afterhourPocketSlotInvLeft.childItems.Add(resident);
        resident.parentInventory = e.afterhourPocketSlotInvLeft;
        Check(FieldPacking.CanLeave() && !WholeStackTransfer.Ready,
            "Manually carrying the old item permits return while keeping the record for later inspection");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        resident = Add(e.afterhourPocketSlotInvLeft, "nested resident", 1, 1, 5);
        incoming = Add(e.afterhourInventory, "nested incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true);
        WholeStackTransfer.AfterApply = item => { if (item == resident) { incoming.Value = 1; e.afterhourPocketSlotInvLeft.Locked = true; } };
        settings.Run();
        var groundBag = AddBag(e.afterhourInventory, "ground bag", 1, 4);
        e.afterhourInventory.childItems.Remove(resident); groundBag.Cargo.childItems.Add(resident); resident.parentInventory = groundBag.Cargo;
        Check(!FieldPacking.CanLeave(), "An item in a ground container is still at risk during return");
        e.afterhourPocketSlotInvLeft.Locked = false;
        e.afterhourInventory.childItems.Remove(groundBag); e.afterhourPocketSlotInvLeft.childItems.Add(groundBag);
        groundBag.parentInventory = e.afterhourPocketSlotInvLeft;
        Check(FieldPacking.CanLeave(), "A protected item nested in a carried container may return safely");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        resident = Add(e.afterhourPocketSlotInvLeft, "destroyed resident", 1, 1, 5);
        incoming = Add(e.afterhourInventory, "destroyed incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true);
        WholeStackTransfer.AfterApply = item => { if (item == resident) { incoming.Value = 1; e.afterhourPocketSlotInvLeft.Locked = true; } };
        settings.Run();
        ((GameItemElement)resident).Destroyed = true;
        Check(FieldPacking.CanLeave() && !WholeStackTransfer.Ready,
            "An already destroyed resident does not trap the player and its record still requires inspection");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        resident = Add(e.afterhourPocketSlotInvLeft, "prewrite resident", 1, 1, 5);
        incoming = Add(e.afterhourInventory, "prewrite incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true); WholeStackTransfer.FailBegin = true; settings.Run();
        Check(resident.parentInventory == e.afterhourPocketSlotInvLeft && incoming.parentInventory == e.afterhourInventory &&
            WholeStackTransfer.Applied.Count == 0, "A failed replacement prewrite must not move the outgoing item");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        resident = AddBag(e.afterhourPocketSlotInvLeft, "detached loaded resident", 5);
        contents = Add(resident.Cargo, "resident contents", 1, 1, 1);
        incoming = Add(e.afterhourInventory, "first-leg incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true); WholeStackTransfer.DetachAt = 1; settings.Run();
        Check(resident.parentInventory == e.afterhourPocketSlotInvLeft && contents.parentInventory == resident.Cargo &&
            incoming.parentInventory == e.afterhourInventory && WholeStackTransfer.Ready && FieldPacking.CanLeave(),
            "A detached first-leg resident must return intact to its original slot");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        resident = Add(e.afterhourPocketSlotInvLeft, "second-leg resident", 1, 1, 5);
        incoming = AddBag(e.afterhourInventory, "detached loaded incoming", 100, 2);
        contents = Add(incoming.Cargo, "incoming contents", 1, 1, 1);
        settings.Set("f36.replace", true); WholeStackTransfer.DetachAt = 2; settings.Run();
        Check(resident.parentInventory == e.afterhourPocketSlotInvLeft && incoming.parentInventory == e.afterhourInventory &&
            contents.parentInventory == incoming.Cargo && WholeStackTransfer.Ready,
            "A detached second-leg incoming item must return intact before the resident is restored");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        resident = Add(e.afterhourPocketSlotInvLeft, "retry resident", 1, 1, 5);
        incoming = Add(e.afterhourInventory, "retry incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true); WholeStackTransfer.DetachAt = 2; WholeStackTransfer.BlockRecovery = true; settings.Run();
        Check(resident.parentInventory == e.afterhourPocketSlotInvLeft && incoming.parentInventory == null &&
            !WholeStackTransfer.Ready && !FieldPacking.CanLeave(),
            "An unplaced incoming item must retain the pending record even when the resident is restored");
        WholeStackTransfer.BlockRecovery = false;
        Check(FieldPacking.CanLeave() && incoming.parentInventory == e.afterhourInventory && WholeStackTransfer.Ready,
            "A later return attempt retries and verifies both original positions");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        resident = Add(e.afterhourPocketSlotInvLeft, "unconfirmed resident", 1, 1, 5);
        incoming = Add(e.afterhourInventory, "unconfirmed incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true); WholeStackTransfer.DetachAt = 2;
        WholeStackTransfer.AfterDetach = item => item.Value = 1;
        settings.Run();
        var guardLeave = typeof(FieldPacking).GetMethod("GuardLeave", BindingFlags.NonPublic | BindingFlags.Static);
        bool departed = false;
        Action retryLeave = () => departed = (bool)guardLeave.Invoke(null, new object[] { null });
        GenericConfirmationUIManager.Instance = new GenericConfirmationUIManager();
        Check(!(bool)guardLeave.Invoke(null, new object[] { retryLeave }) && GenericConfirmationUIManager.Instance.panel.activeInHierarchy &&
            incoming.parentInventory == null && resident.parentInventory == e.afterhourPocketSlotInvLeft && !WholeStackTransfer.Ready,
            "An unsafe detached incoming item must require confirmation and retain its record");
        GenericConfirmationUIManager.Instance.Cancel();
        Check(!departed && !WholeStackTransfer.Ready && !FieldPacking.CanLeave(), "Cancelling must leave the recovery guard active");
        e.afterhourWindow.Visible = false;
        guardLeave.Invoke(null, new object[] { retryLeave });
        GenericConfirmationUIManager.Instance.Confirm();
        Check(departed && !WholeStackTransfer.Ready && !FieldPacking.CanLeave(),
            "Confirmation still works with the ground window closed and permits only that departure without clearing the record");
        e.afterhourWindow.Visible = true;
        incoming.Value = 100;
        Check(FieldPacking.CanLeave() && WholeStackTransfer.Ready && incoming.parentInventory == e.afterhourInventory,
            "A restored verifiable state can still be recovered after a cancelled transition");

        (feature, settings, e) = Setup(0, 0);
        e.afterhourPocketSlotInvRight.Locked = true;
        resident = Add(e.afterhourPocketSlotInvLeft, "departed resident", 1, 1, 5);
        incoming = Add(e.afterhourInventory, "departed incoming", 1, 1, 100, 2);
        settings.Set("f36.replace", true); WholeStackTransfer.DetachAt = 2; WholeStackTransfer.BlockRecovery = true; settings.Run();
        MapUIManager.Instance.Current.Id = "store";
        Check(FieldPacking.CanLeave() && !WholeStackTransfer.Ready, "Leaving the scene clears only the session guard and retains inspection");
        MapUIManager.Instance.Current.Id = "dumping_ground";

        Check(FieldPacking.TotalValue(long.MaxValue, int.MaxValue) == (decimal)long.MaxValue * int.MaxValue,
            "Large stack totals must not wrap into a low or negative ranking");
        Console.WriteLine($"PASS {assertions} field packing safety, objective, settings and replacement assertions");
    }
}

namespace UnityEngine
{
    public enum KeyCode { None }
    public static class Input { public static bool GetKeyDown(KeyCode key) => false; }
    public static class Debug { public static void LogWarning(object message) => Console.WriteLine(message); }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
}
namespace ShopAssist
{
    class ConfigEntry<T> { internal T Value; public ConfigEntry(T value) { Value = value; } }
    class ShopSettings
    {
        readonly Dictionary<string, object> entries = new(); Action run;
        internal ConfigEntry<bool> Toggle(string key, string tip, bool initial = false) { var e = new ConfigEntry<bool>(initial); entries[key] = e; return e; }
        internal ConfigEntry<int> Integer(string key, string tip, int initial, int min, int max) { var e = new ConfigEntry<int>(initial); entries[key] = e; return e; }
        internal ConfigEntry<int> Choice(string key, string tip, string[] labels) => Integer(key, tip, 0, 0, labels.Length - 1);
        internal Func<UnityEngine.KeyCode> Key(string key, string tip) => () => UnityEngine.KeyCode.None;
        internal void Action(string key, Action callback, Func<bool> available) { run = () => { if (available()) callback(); }; }
        internal void Run() => run();
        internal void Set<T>(string key, T value) => ((ConfigEntry<T>)entries[key]).Value = value;
        internal static string Text(string key) => key;
    }
    class GridShape
    {
        internal int width, height, minX, minY, orientation; internal bool flipped;
        internal int maxX => minX + width - 1; internal int maxY => minY + height - 1;
        internal GridShape(int w, int h, int x = 0, int y = 0) { width = w; height = h; minX = x; minY = y; }
        internal bool Intersects(int x, int y) => x >= minX && x <= maxX && y >= minY && y <= maxY;
        internal bool Intersects(GridShape shape) => minX <= shape.maxX && maxX >= shape.minX && minY <= shape.maxY && maxY >= shape.minY;
        internal int Get(int x, int y) => 0;
        internal GridShape Clone() => (GridShape)MemberwiseClone();
    }
    class GridShapeBuilder
    {
        readonly GridShape original; int x, y, orientation; bool flipped;
        internal GridShapeBuilder(GridShape shape) { original = shape; }
        internal void SetOrientation(int turn) { orientation = turn; }
        internal void SetFlipped(bool flip) { flipped = flip; }
        internal void SetPosition(int a, int b) { x = a; y = b; }
        internal GridShapeBuilder SetTransform(int a, int b, bool flip, int turn) { x = a; y = b; flipped = flip; orientation = turn; return this; }
        internal GridShape Build() => new(orientation % 2 == 0 ? original.width : original.height,
            orientation % 2 == 0 ? original.height : original.width, x, y) { orientation = orientation, flipped = flipped };
    }
    class ItemFeature { }
    class ItemCondition { }
    class WaterHelper { internal static int GetContainerPrice(GameItem item) => 0; }
    class PowerHelper { }
    class StoreEventManager { }
    class StoreStation { }
    class DebugPanel { }
    class GraphUtils { }
    class TagSystem { }
    class TagState { }
    class GameItem
    {
        static int sequence; internal static int ValueReads;
        internal int uniqueId = ++sequence, unitCount = 1; internal long Value, Base, unitValue, lateUnitValue; internal bool Unsafe, Backpack, IgnoreChildren, Visible = true;
        internal int Reads; internal bool ThrowOnRead;
        internal string Name; internal object contentWindow; internal GameGridInventory Cargo; internal bool CustomTooltip;
        internal GameInventory parentInventory; internal GridShape modifiedShape = new(1, 1);
        internal TagSystem modifiedState = new(); internal List<ItemFeature> itemFeatures = new();
        internal long GetCurrentValue(bool a, bool withChild, bool c, bool d, long extra)
        {
            if (ThrowOnRead) throw new InvalidOperationException("injected value failure");
            ValueReads++; Reads++;
            long value = Value;
            if (withChild && !IgnoreChildren) foreach (var child in Descendants()) value += child.GetCurrentValue(a, false, c, d, extra);
            return value;
        }
        internal long GetBaseValue()
        {
            ValueReads++; Reads++;
            long value = Base;
            if (!IgnoreChildren) foreach (var child in Descendants()) value += child.GetCurrentValue(false, false, false, false, 0);
            return value;
        }
        IEnumerable<GameItem> Descendants()
        {
            var pending = new Queue<GameItem>(Cargo?.childItems ?? new List<GameItem>());
            while (pending.Count > 0)
            {
                var item = pending.Dequeue(); yield return item;
                if (item.Cargo != null) foreach (var child in item.Cargo.childItems) pending.Enqueue(child);
            }
        }
        internal int GetUniqueID() => uniqueId;
        internal bool IsTag(string tag) => tag == "BACKPACK_TAG" && Backpack || tag == "IGNORE_CHILD_VALUE" && IgnoreChildren;
        internal bool CheckIsStateValid(GameInventory inventory, SlotMarker marker, GridShape shape, TagSystem tags) => true;
        internal int MaxNumAdd(SlotMarker marker) => unitCount;
    }
    class GameItemElement : GameItem { internal bool Destroyed; internal bool IsDestroyed() => Destroyed; }
    class ContentWindow { public GameInventory childElement; }
    class GameInventory
    {
        internal readonly List<GameItem> childItems = new(); internal bool Locked;
        internal GameItem Owner;
        internal GameItem childItem => childItems.FirstOrDefault();
        internal GameItem GetParentItem() => Owner;
        internal bool IsInsertLocked() => Locked; internal bool IsRemoveLocked() => Locked;
        internal bool MayHaveValidInventorySlot(GameItem item) => !Locked;
        internal bool CheckAcceptParentShapeStateModifier(GameItem item, SlotMarker marker, GridShape shape, TagSystem tags) => true;
    }
    class GameGridInventory : GameInventory
    {
        internal GridShape inventoryShape;
        internal GameGridInventory(int w, int h) { inventoryShape = new(w, h); }
        internal bool CheckShapePlacement(GameItem item, GridShape shape, List<GameItem> ignore) =>
            shape.minX >= 0 && shape.minY >= 0 && shape.maxX < inventoryShape.width && shape.maxY < inventoryShape.height &&
            childItems.All(other => ignore.Contains(other) || !other.modifiedShape.Intersects(shape));
    }
    class GameSlotInventory : GameInventory { internal int defaultOrientation; }
    class SlotMarker { internal GameItem item, targetItem; internal GameInventory inventory; internal GridShape itemGridShape; internal int TryAcceptOnce(int amount) => 0; }
    class EmporiumEntry
    {
        internal static EmporiumEntry Instance = new();
        internal GameGridInventory afterhourInventory = new(100, 100);
        internal GameSlotInventory afterhourPocketSlotInvBackpack = new(), afterhourPocketSlotInvLeft = new(), afterhourPocketSlotInvRight = new();
        internal Window afterhourWindow = new();
    }
    class Window { internal bool Visible = true; internal bool IsVisible() => Visible; }
    class PlayerStore { internal static PlayerStore Instance = new(); internal static bool HasInstance => true; internal string runID = "test"; internal int saveSlotId; }
    class MapUIManager { internal static MapUIManager Instance = new(); internal Map Current = new(); internal void ReturnNow() { } internal void Travel(AfterhourLocation location) { } }
    class AfterhourLocation { }
    class Map { internal string Id = "dumping_ground"; }
    class GenericConfirmationUIManager
    {
        internal static GenericConfirmationUIManager Instance; internal Panel panel = new(); internal Action Confirm, Cancel;
        internal void OpenPanel(string text, string accept, string cancel, Action confirm, Action reject)
        { panel.activeInHierarchy = true; Confirm = confirm; Cancel = reject; }
        internal void ClosePanel() => panel.activeInHierarchy = false;
    }
    class Panel { internal bool activeInHierarchy; }
    static class VisibleInventoryViews { internal static bool Visible(GameItemElement item) => item.Visible; }
    static class CustomerIndicators { internal static bool HasOrdinaryTooltip(GameItemElement item) => !item.CustomTooltip; }
    static class ShopInput { internal static bool Ready() => true; }
    static class AttentionBook { internal static bool HasInspection(string key, int id) => false; }
    static class ShopInventory { internal static bool Movable(GameItem item) => !item.Unsafe; internal static GameGridInventory OutingBag(GameItem item) => item?.Cargo; }
    static class InventoryAutoInsert { internal static bool VerifiedGameBuild(Guid module) => true; internal static bool KnownPatches(MethodBase method) => true; }
    class OperationResult
    {
        internal class Row { internal int ItemId; internal string Reason; }
        internal List<Row> Items = new(); internal string Detail;
        internal OperationResult(string key) { }
        internal static int Capture(GameItem item) => item.uniqueId;
        internal void CompleteCaptured(int id, int count, string path) => Items.Add(new Row { ItemId = id });
        internal void SkipCaptured(int id, int count, string reason, string path) => Items.Add(new Row { ItemId = id, Reason = reason });
        internal void Skip(GameItem item, int count, string reason, string path = null) => SkipCaptured(item.uniqueId, count, reason, path ?? "");
    }
    static class ShopNotice { internal static OperationResult Last; internal static void Show(string text, float seconds = 4) { } internal static void ReportField(OperationResult result, GameInventory ground) { Last = result; } }
    static class WholeStackTransfer
    {
        internal static bool AllowFieldContainerCargo;
        internal enum Result { Applied, Unchanged, Pending }
        internal class Plan { internal GameItem Item, Stack; internal GameInventory From, To; internal GridShape Before, After; internal int Amount; }
        internal class FieldReplacement { internal Plan Leave, IncomingOriginal; internal GameItem Incoming; internal long Value, Base; internal int Count; }
        internal class Reservations { internal Dictionary<GameInventory, List<GridShape>> Shapes = new(); }
        internal static bool Ready = true, FailNext, FailBegin, BlockRecovery; internal static int FailAt, Calls, DetachAt;
        internal static Action<GameItem> AfterApply, AfterDetach;
        internal static FieldReplacement Replacement;
        internal static List<GameItem> Applied = new();
        internal static void Acquire() { } internal static void Release() { }
        internal static bool TryFieldContainerItems(GameItem item, out List<GameItem> items)
        {
            items = new List<GameItem>(); var pending = new Queue<GameItem>(); var seen = new HashSet<GameItem>(); pending.Enqueue(item);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (!seen.Add(current) || current.Unsafe) return false;
                items.Add(current);
                if (current.contentWindow == null) continue;
                if (!current.Backpack || current.Cargo == null || current.unitCount != 1) return false;
                foreach (var child in current.Cargo.childItems)
                {
                    if (child.parentInventory != current.Cargo) return false;
                    pending.Enqueue(child);
                }
            }
            return item.contentWindow != null;
        }
        internal static bool SafeSource(GameItem item) => !item.Unsafe && (item.contentWindow == null ||
            AllowFieldContainerCargo && TryFieldContainerItems(item, out _));
        internal static bool SafeTarget(GameInventory inventory) => !inventory.Locked;
        internal static Plan Stack(GameItem item, GameInventory target, Reservations reserved) => null;
        static Plan Create(GameItem item, GameInventory target, GridShape shape) => new() { Item = item, From = item.parentInventory, To = target, Before = item.modifiedShape.Clone(), After = shape.Clone(), Amount = item.unitCount };
        internal static Plan Empty(GameItem item, GameGridInventory target, Reservations reserved)
        {
            if (!SafeSource(item) || target.Locked) return null;
            if (!reserved.Shapes.TryGetValue(target, out var shapes)) reserved.Shapes[target] = shapes = new();
            for (int turn = 0; turn < 2; turn++) for (int y = 0; y < target.inventoryShape.height; y++) for (int x = 0; x < target.inventoryShape.width; x++)
            {
                var shape = new GridShapeBuilder(item.modifiedShape).SetTransform(x, y, false, turn).Build();
                if (!target.CheckShapePlacement(item, shape, new()) || shapes.Any(shape.Intersects)) continue;
                shapes.Add(shape); return Create(item, target, shape);
            }
            return null;
        }
        internal static Plan EmptySlot(GameItem item, GameInventory target) => !target.Locked && target.childItems.Count == 0 ? Create(item, target, item.modifiedShape) : null;
        internal static Plan ExactEmpty(GameItem item, GameGridInventory target, GridShape shape) => target.CheckShapePlacement(item, shape, new()) ? Create(item, target, shape) : null;
        internal static FieldReplacement BeginFieldReplacement(Plan leave, GameItem incoming, string path, string from)
        {
            if (!Ready || FailBegin) return null;
            Ready = false; return Replacement = new FieldReplacement { Leave = leave, Incoming = incoming,
                IncomingOriginal = Create(incoming, leave.From, incoming.modifiedShape), Value = incoming.Value, Base = incoming.Base, Count = incoming.unitCount };
        }
        internal static Result ApplyFieldReplacement(FieldReplacement replacement, Plan move, Func<bool> scope) => Apply(move, "f36.open", "", "", "", scope);
        internal static bool FieldReplacementOriginal(FieldReplacement replacement, GameInventory inventory)
        {
            if (replacement != Replacement || replacement.Leave.Item.parentInventory != inventory || !inventory.childItems.Contains(replacement.Leave.Item)) return false;
            var expected = ReferenceEquals(inventory, replacement.Leave.From) ? replacement.Leave.Before : replacement.Leave.After;
            var actual = replacement.Leave.Item.modifiedShape;
            return actual.minX == expected.minX && actual.minY == expected.minY && actual.width == expected.width && actual.height == expected.height &&
                actual.orientation == expected.orientation && actual.flipped == expected.flipped;
        }
        internal static bool FieldReplacementIncomingOriginal(FieldReplacement replacement) => replacement == Replacement &&
            replacement.Incoming.parentInventory == replacement.IncomingOriginal.From &&
            replacement.IncomingOriginal.From.childItems.Contains(replacement.Incoming) && replacement.Incoming.unitCount == replacement.Count &&
            replacement.Incoming.Value == replacement.Value && replacement.Incoming.Base == replacement.Base &&
            replacement.Incoming.modifiedShape.minX == replacement.IncomingOriginal.Before.minX &&
            replacement.Incoming.modifiedShape.minY == replacement.IncomingOriginal.Before.minY;
        internal static bool RecoverDetachedFieldItem(FieldReplacement replacement, bool incoming, Func<bool> scope)
        {
            if (replacement != Replacement || BlockRecovery || !scope()) return false;
            var plan = incoming ? replacement.IncomingOriginal : replacement.Leave;
            var item = plan.Item;
            if (item.parentInventory != null || item.Unsafe || incoming && (item.Value != replacement.Value || item.Base != replacement.Base || item.unitCount != replacement.Count) ||
                plan.From.Locked || plan.From is GameGridInventory grid && !grid.CheckShapePlacement(item, plan.Before, new()) ||
                plan.From is GameSlotInventory && plan.From.childItems.Count != 0) return false;
            item.modifiedShape = plan.Before.Clone(); item.parentInventory = plan.From; plan.From.childItems.Add(item); return true;
        }
        internal static bool FinishFieldReplacement(FieldReplacement replacement)
        { if (replacement != Replacement) return false; Ready = true; Replacement = null; return true; }
        internal static string FieldPending() => Replacement?.Leave.Item.Name;
        internal static Result Apply(Plan move, string action, string path, string from, string to, Func<bool> scope)
        {
            Calls++;
            if (FailNext || FailAt == Calls) { FailNext = false; return Result.Unchanged; }
            if (!scope() || move.Item.parentInventory != move.From) return Result.Unchanged;
            if (DetachAt == Calls)
            {
                move.From.childItems.Remove(move.Item); move.Item.parentInventory = null; move.Item.modifiedShape = move.After.Clone();
                AfterDetach?.Invoke(move.Item); return Result.Pending;
            }
            move.From.childItems.Remove(move.Item); move.To.childItems.Add(move.Item); move.Item.parentInventory = move.To;
            move.Item.modifiedShape = move.After.Clone(); Applied.Add(move.Item); AfterApply?.Invoke(move.Item); return Result.Applied;
        }
    }
}
