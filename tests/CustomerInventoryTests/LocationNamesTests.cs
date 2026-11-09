using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

internal static class LocationNamesTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static Type locationType;
    private static MethodInfo disambiguate;

    private static object Location(GameInventory grid, GameItem owner, string name, int page = -1)
    {
        object location = Activator.CreateInstance(locationType, true);
        locationType.GetField("Inventory", Flags).SetValue(location, grid);
        locationType.GetField("Owner", Flags).SetValue(location, owner);
        locationType.GetField("Name", Flags).SetValue(location, name);
        locationType.GetField("Role", Flags).SetValue(location, "contents");
        locationType.GetField("Path", Flags).SetValue(location, Array.Empty<GameItem>());
        locationType.GetField("Page", Flags).SetValue(location, page);
        return location;
    }

    private static string Name(object location) => (string)locationType.GetField("Name", Flags).GetValue(location);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Apply(string run, params object[] locations)
    {
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(locationType));
        foreach (object location in locations) list.Add(location);
        disambiguate.Invoke(null, new object[] { list, run, 1 });
    }

    private static void Main()
    {
        string game = Environment.GetEnvironmentVariable("PROBABLY_STOLEN_GAME_DIR") ?? @"D:\SteamLibrary\steamapps\common\Probably Stolen";
        string managed = Path.Combine(game, "Probably Stolen_Data", "Managed");
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string file = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
        Run();
    }

    private static void Run()
    {
        var shop = typeof(ShopAssist.ModEntry).Assembly.GetType("ShopAssist.ShopInventory", true);
        locationType = shop.GetNestedType("Location", Flags);
        disambiguate = shop.GetMethod("Disambiguate", Flags, null, new[]
        { typeof(List<>).MakeGenericType(locationType), typeof(string), typeof(int) }, null);
        var firstGrid = (GameGridInventory)RuntimeHelpers.GetUninitializedObject(typeof(GameGridInventory));
        var secondGrid = (GameGridInventory)RuntimeHelpers.GetUninitializedObject(typeof(GameGridInventory));
        var thirdGrid = (GameGridInventory)RuntimeHelpers.GetUninitializedObject(typeof(GameGridInventory));
        var firstOwner = (GameItemElement)RuntimeHelpers.GetUninitializedObject(typeof(GameItemElement));
        var secondOwner = (GameItemElement)RuntimeHelpers.GetUninitializedObject(typeof(GameItemElement));
        var thirdOwner = (GameItemElement)RuntimeHelpers.GetUninitializedObject(typeof(GameItemElement));
        int firstId = firstOwner.uniqueId, secondId = secondOwner.uniqueId;

        var first = Location(firstGrid, firstOwner, "Stock › Box", 0);
        var second = Location(secondGrid, secondOwner, "Stock › Box", 1);
        Apply("run-a", first, second);
        Check(Name(first) == "#1 Stock › Box" && Name(second) == "#2 Stock › Box", "same-name inventories must be distinct");
        Check(firstOwner.uniqueId == firstId && secondOwner.uniqueId == secondId, "browsing must not allocate item IDs");
        var secondAlone = Location(secondGrid, secondOwner, "Stock › Box", 1);
        Apply("run-a", secondAlone);
        Check(Name(secondAlone) == "#2 Stock › Box", "a temporarily hidden sibling must not renumber the location");
        var third = Location(thirdGrid, thirdOwner, "Stock › Box");
        var firstAgain = Location(firstGrid, firstOwner, "Stock › Box");
        var secondAgain = Location(secondGrid, secondOwner, "Stock › Box");
        Apply("run-a", third, secondAgain, firstAgain);
        Check(Name(firstAgain) == "#1 Stock › Box" && Name(secondAgain) == "#2 Stock › Box" && Name(third) == "#3 Stock › Box",
            "reordering and newly found containers must preserve prior labels");
        var otherRunFirst = Location(firstGrid, firstOwner, "Stock › Box");
        var otherRunSecond = Location(secondGrid, secondOwner, "Stock › Box");
        Apply("run-b", otherRunFirst, otherRunSecond);
        Check(Name(otherRunFirst) == "#1 Stock › Box" && Name(otherRunSecond) == "#2 Stock › Box", "a new run starts a fresh naming scope");

        var picker = typeof(ShopAssist.ModEntry).Assembly.GetType("ShopAssist.VisualChoices", true);
        MethodInfo offered = picker.GetMethod("StillOffered", Flags);
        IList current = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(locationType));
        current.Add(otherRunFirst);
        Type listType = typeof(IList<>).MakeGenericType(locationType);
        Type supplierType = typeof(Func<>).MakeGenericType(listType);
        Delegate supplier = Expression.Lambda(supplierType, Expression.Convert(Expression.Constant(current), listType)).Compile();
        Check((bool)offered.Invoke(null, new[] { supplier, otherRunFirst }), "a live candidate remains selectable");
        current.Clear();
        Check(!(bool)offered.Invoke(null, new[] { supplier, otherRunFirst }), "a removed candidate cannot be selected");
        var movedPath = Location(firstGrid, firstOwner, "Stock › Box");
        locationType.GetField("Path", Flags).SetValue(movedPath, new GameItem[] { thirdOwner });
        current.Add(movedPath);
        Check(!(bool)offered.Invoke(null, new[] { supplier, otherRunFirst }), "a changed containment path invalidates a stale selection");

        var safety = typeof(ShopAssist.ModEntry).Assembly.GetType("ShopAssist.InventoryAutoInsert", true);
        var liquid = typeof(ShopAssist.ModEntry).Assembly.GetType("ShopAssist.LiquidKnowledge", true);
        foreach (Type guarded in new[] { safety, liquid })
        {
            MethodInfo verified = guarded.GetMethod("VerifiedGameBuild", Flags);
            foreach (string build in new[] { "83045710-adce-4e78-8ff1-b43a9399ec0d", "efd8b252-19aa-489b-96d3-e172c92c0b75",
                "911508a6-e0eb-4cc0-91e8-e14b16ea28d7" })
                Check((bool)verified.Invoke(null, new object[] { new Guid(build) }), guarded.Name + " must accept the verified build " + build);
            Check(!(bool)verified.Invoke(null, new object[] { Guid.Empty }), guarded.Name + " must reject unknown builds");
        }
        var basket = typeof(ShopAssist.ModEntry).Assembly.GetType("ShopAssist.CustomerBasket", true);
        MethodInfo verifiedCounter = basket.GetMethod("VerifiedCounterGameBuild", Flags);
        foreach (string build in new[] { "efd8b252-19aa-489b-96d3-e172c92c0b75", "911508a6-e0eb-4cc0-91e8-e14b16ea28d7" })
            Check((bool)verifiedCounter.Invoke(null, new object[] { new Guid(build) }), "counter staging must accept verified build " + build);
        foreach (Guid build in new[] { Guid.Empty, new Guid("83045710-adce-4e78-8ff1-b43a9399ec0d") })
            Check(!(bool)verifiedCounter.Invoke(null, new object[] { build }), "counter staging must reject unaudited builds");
        var counterMethods = (MethodBase[])basket.GetField("CounterMethods", Flags).GetValue(null);
        Check(Array.TrueForAll(counterMethods, method => method != null), "every guarded native counter method must resolve");
        var counterOwner = typeof(GameItem).Assembly.GetType("EmporiumEntry+<>c", true);
        foreach (string method in new[] { "<Start>b__184_7", "<Start>b__184_16", "<Start>b__184_17", "<Start>b__184_18" })
            Check(counterOwner.GetMethod(method, Flags) != null, "the native counter callback must exist: " + method);
        foreach (string method in new[] { "<CompleteClientCreation>b__143_0", "<CompleteClientCreation>b__143_1", "<CompleteClientCreation>b__143_2" })
            Check(typeof(StoreClient).GetMethod(method, Flags) != null, "the native customer callback must exist: " + method);
        var mutations = (MethodBase[])safety.GetField("NativeMutations", Flags).GetValue(null);
        Check(Array.TrueForAll(mutations, method => method != null), "every guarded native transfer method must resolve");
        Check((bool)safety.GetMethod("KnownMutationPatches", Flags).Invoke(null, null),
            "the installed verified game is accepted when native transfer methods have no unknown patches");
        MethodInfo checkCallbacks = safety.GetMethod("KnownCallbacks", Flags);
        var owner = typeof(GameItem).Assembly.GetType("UnusedDirectory+<>c", true);
        var nativeMethod = owner.GetMethod("<Coin>b__96_0", Flags);
        var nativeTarget = RuntimeHelpers.GetUninitializedObject(owner);
        var nativeCallback = Delegate.CreateDelegate(typeof(Func<GameItem, GameItem, bool>), nativeTarget, nativeMethod);
        Func<Delegate, bool> allow = _ => true;
        Check((bool)checkCallbacks.Invoke(null, new object[] { nativeCallback, allow }), "verified native callbacks are recognized");
        Func<GameItem, GameItem, bool> injected = (_, _) => true;
        Check(!(bool)checkCallbacks.Invoke(null, new object[] { injected, allow }), "unknown callback assemblies are rejected");

        var outingOwner = typeof(GameItem).Assembly.GetType("EmporiumEntry+<>c", true);
        var outingTarget = RuntimeHelpers.GetUninitializedObject(outingOwner);
        string outingVersion = typeof(GameItem).Module.ModuleVersionId == new Guid("83045710-adce-4e78-8ff1-b43a9399ec0d") ? "280" : "284";
        MethodInfo outingCheck = safety.GetMethod("OutingCallback", Flags);
        for (int ordinal = 0; ordinal < 2; ordinal++)
        {
            var method = outingOwner.GetMethod("<SetupAfterhourInv>b__" + outingVersion + "_" + ordinal, Flags);
            Check(method != null, "the expected native outing callback must exist");
            Type callbackType = ordinal == 0 ? typeof(Func<GameItem, GameInventory, bool>) : typeof(Func<GameItem, GameInventory, SlotMarker, int>);
            var callback = Delegate.CreateDelegate(callbackType, outingTarget, method);
            Check((bool)outingCheck.Invoke(null, new object[] { callback, ordinal }), "the native outing callback must be recognized");
            Check(!(bool)outingCheck.Invoke(null, new object[] { callback, 1 - ordinal }), "the wrong outing callback must be rejected");
        }

        var gunOwner = typeof(GameItem).Assembly.GetType("GunModDirectory+<>c", true);
        var gunTarget = RuntimeHelpers.GetUninitializedObject(gunOwner);
        var visualItem = (GameItemElement)RuntimeHelpers.GetUninitializedObject(typeof(GameItemElement));
        MethodInfo visualSafe = safety.GetMethod("SafeVisualItem", Flags);
        foreach (var example in new[] {
            ("parentItemShapeModifierForItemFunc", "<CreateSilencer>b__13_0"),
            ("parentItemShapeModifierForItemFunc", "<CreateSilencerMakeshift>b__19_0"),
            ("parentItemSpriteModifierForItemFunc", "<CreateRDS>b__9_0"),
            ("parentItemSpriteModifierForItemFunc", "<CreateSilencer>b__13_1"),
            ("parentItemSpriteModifierForItemFunc", "<CreateSilencerMakeshift>b__19_1"),
            ("parentItemSpriteModifierForItemFunc", "<CreateLaser>b__29_0"),
            ("parentItemSpriteModifierForItemFunc", "<CreateLaserToy>b__33_0"),
            ("parentItemSpriteModifierForItemFunc", "<Create22Mag>b__57_0"),
            ("parentItemSpriteModifierForItemFunc", "<Create22MagHigh>b__59_0") })
        {
            var property = typeof(GameItemElement).GetProperty(example.Item1, Flags);
            var callback = Delegate.CreateDelegate(property.PropertyType, gunTarget, gunOwner.GetMethod(example.Item2, Flags));
            property.SetValue(visualItem, callback);
            Check((bool)visualSafe.Invoke(null, new object[] { visualItem }), "verified native gun modifier must not block other inventory items: " + example.Item2);
            property.SetValue(visualItem, null);
        }
        Func<GameItem, GameInventory, SlotMarker, GridShape, TagSystem, GameItem, List<ItemSpriteModifier>> injectedVisual =
            (_, _, _, _, _, _) => null;
        visualItem.parentItemSpriteModifierForItemFunc = injectedVisual;
        Check(!(bool)visualSafe.Invoke(null, new object[] { visualItem }), "unknown visual callbacks still block transfer");
        Console.WriteLine("PASS location names and native transfer method guards");
    }
}
