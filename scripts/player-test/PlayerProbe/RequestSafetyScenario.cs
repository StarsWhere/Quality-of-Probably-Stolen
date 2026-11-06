using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Makes a destination inaccessible during the save callback and between native moves.
public sealed class RequestSafetyScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90;
    private static GameItem destination;
    private static bool lockAfterWrite, watchMoves;
    private static int moves;
    private static void Need(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".requests");
    private static void AfterWrite(string __0, string __1)
    {
        if (!lockAfterWrite || __0 != Mod || __1 != "requests") return;
        lockAfterWrite = false; destination.contentWindow.isLocked = true;
    }
    private static void AfterMove(bool __result)
    {
        if (!watchMoves) return;
        moves++;
        if (__result && moves == 1) destination.contentWindow.isLocked = true;
    }
    private static GameItem Item(string id, string name)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing request fixture " + id);
        GeneralHelper.SetItemOwned(item, true); GeneralHelper.SetCustomName(item, name);
        Put(item, EmporiumEntry.Instance.invElement); return item;
    }
    private static void Put(GameItem item, GameInventory inventory)
    {
        var marker = inventory.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == inventory && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory, "Cannot place request fixture");
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Request boundary test timed out");
        if (!start.Tick()) return false;
        var e = EmporiumEntry.Instance; var player = PlayerStore.Instance;
        if (e?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;
        TradeScenario.SetOptions(true, "f27.enabled");
        destination = Item("backpack_medium", "QA request boundary destination");
        var first = Item("wine_yeast_red", "QA request boundary first");
        var second = Item("wine_yeast_red", "QA request boundary second");
        ItemMouseDoubleClickHandler.current.OpenContentAction(destination, Vector2.zero);
        var request = new StoreRequest { title = "QA request boundary", id = Guid.NewGuid().ToString("N"),
            sourceEventId = "QA request boundary", requiredItem = first.identifier, requiredItemCount = 2, requesterName = "QA", duration = 2 };
        player.storeRequests.Add(request);
        var mod = ModLoader.Mods.First(m => m.Manifest?.ID == Mod).Instance;
        Type bookType = mod.GetType().Assembly.GetType("ShopAssist.RequestBook", true);
        object book = bookType.GetField("active", Flags).GetValue(null);
        var draw = bookType.GetMethod("Draw", Flags);
        draw.Invoke(book, new object[] { request }); Window().Get("track").button.onClick.Invoke();
        QaItemUi.Choose(Window(), "destination", "QA request boundary destination"); QaItemUi.Click(Window(), first);
        const string writePatch = "qa.request-destination-during-save";
        var harmony = new Harmony(writePatch); lockAfterWrite = true;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ModHelper), nameof(ModHelper.SetModData), new[] { typeof(string), typeof(string), typeof(string) }),
                postfix: new HarmonyMethod(typeof(RequestSafetyScenario).GetMethod(nameof(AfterWrite), Flags)));
            Window().Get("prepare").button.onClick.Invoke();
        }
        finally { harmony.UnpatchAll(writePatch); lockAfterWrite = false; }
        Need(first.parentInventory == e.invElement && RequestColdReloadScenario.Reserved(first),
            "A destination locked during reservation save was used or the reservation was lost");
        destination.contentWindow.isLocked = false;
        if (!destination.contentWindow.IsVisible()) ItemMouseDoubleClickHandler.current.OpenContentAction(destination, Vector2.zero);
        CustomUIManager.Instance.GetWindow(Mod + ".operation")?.Close();
        draw.Invoke(book, new object[] { request }); QaItemUi.Click(Window(), second); Window().Get("prepare").button.onClick.Invoke();
        var inside = destination.contentWindow.childElement as GameGridInventory;
        Need(inside != null && second.parentInventory == inside && RequestColdReloadScenario.Reserved(second),
            "Could not establish both saved reservations");
        Put(second, e.invElement);
        Window().Get("migrate").button.onClick.Invoke();
        Type inventoryType = mod.GetType().Assembly.GetType("ShopAssist.ShopInventory", true);
        const string movePatch = "qa.request-destination-between-moves";
        harmony = new Harmony(movePatch); moves = 0; watchMoves = true;
        try
        {
            harmony.Patch(inventoryType.GetMethod("TryMoveIntact", Flags),
                postfix: new HarmonyMethod(typeof(RequestSafetyScenario).GetMethod(nameof(AfterMove), Flags)));
            Window().Get("migrate_apply").button.onClick.Invoke();
        }
        finally { harmony.UnpatchAll(movePatch); watchMoves = false; destination.contentWindow.isLocked = false; }
        Need(moves == 1 && new[] { first, second }.Count(item => item.parentInventory == inside) == 1 &&
            new[] { first, second }.Count(item => item.parentInventory == e.invElement) == 1 &&
            RequestColdReloadScenario.Reserved(first) && RequestColdReloadScenario.Reserved(second),
            "Migration continued after the destination changed or discarded a saved reservation");
        result = "PASS F27 saved reservation survives a changed destination and migration revalidates before each move";
        return true;
    }
}
