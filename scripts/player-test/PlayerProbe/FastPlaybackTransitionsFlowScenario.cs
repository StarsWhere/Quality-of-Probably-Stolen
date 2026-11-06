using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Native outing and sleep paths, run against a fresh isolated QA save.
public sealed class FastPlaybackTransitionsFlowScenario
{
    private static FastPlaybackTransitionsFlowScenario active;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private readonly List<string> settlement = new List<string>();
    private QaFastPlayback playback;
    private Harmony observer;
    private string observerId;
    private GameItem bag, kept, acquired, pocket;
    private GameGridInventory bagContents;
    private bool observing, restored;
    private int step, frame, initialDay, initialVisits, mapOpen, travel, returned, nightDuring, nightAfter;
    private float initialScale, stableAfter;
    private string savePath;
    private DateTime initialSaveWrite;

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch { Cleanup(); throw; }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Transition flow timed out at step " + step + State());
        if (step == 0)
        {
            if (!start.Tick()) return false;
            Setup(); step = 1; return false;
        }
        var player = PlayerStore.Instance;
        var ui = StoreUIManager.Instance;
        var entry = EmporiumEntry.Instance;
        if (step <= 9) Need(StoreStation.GetDayCounter() == initialDay, "The outing or shutter advanced the day");
        if (step == 1)
        {
            if (Time.frameCount <= frame) return false;
            playback.Press(); step = 2; return false;
        }
        if (step == 2)
        {
            playback.Release();
            if (playback.Rate != 16f) return false;
            Need(player.storeState == PlayerStore.StoreState.MORNING, "Fresh save is not in the morning");
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) ui.storeCalendar.OnClick();
            if (!player.CanStartDay() || player.lockShutterButtonFlag > 0) return false;
            StoreShutterButton.Instance.OnClick();
            Need(player.storeState == PlayerStore.StoreState.OPEN, "Native shutter did not open");
            step = 3; return false;
        }
        if (step == 3)
        {
            if (player.lockShutterButtonFlag > 0 || !player.CanEndDay()) return false;
            StoreShutterButton.Instance.OnClick(); step = 4; return false;
        }
        if (step == 4)
        {
            if (player.storeState == PlayerStore.StoreState.OPEN)
            {
                if (ui.closeShutterConfirmationPanel?.activeInHierarchy == true) StoreShutterButton.Instance.EndAndClose();
                return false;
            }
            Need(player.storeState == PlayerStore.StoreState.CLOSED, "Native shutter did not close");
            stableAfter = Time.realtimeSinceStartup + 0.5f; step = 5; return false;
        }
        if (step == 5)
        {
            if (Time.realtimeSinceStartup < stableAfter || ui.blackscreen.gameObject.activeInHierarchy) return false;
            Need(playback.Rate == 16f, "Toggle playback did not resume after shutter confirmation");
            bag = New("backpack_medium", entry.invElement);
            bagContents = bag.contentWindow?.childElement as GameGridInventory;
            Need(bagContents != null, "Native backpack inventory unavailable");
            kept = New("wine_yeast_red", bagContents);
            entry.selectedBackpack = bag;
            player.visitLeftTonight = 2;
            var destination = AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND);
            Need(destination != null && destination.IsAvailable(), "Native dumping ground unavailable");
            initialVisits = player.GetAfterhourState(destination.Id).visits;
            MapUIManager.Instance.OpenUI(); step = 6; return false;
        }
        if (step == 6)
        {
            if (!TransitUI.IsOpen || ui.blackscreen.gameObject.activeInHierarchy) return false;
            Need(mapOpen == 1 && travel == 0 && returned == 0, "Map opening callback did not run exactly once");
            Need(playback.Rate == 16f, "Outing playback stopped before travel");
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND));
            step = 7; return false;
        }
        if (step == 7)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND ||
                !entry.afterhourWindow.IsVisible() || entry.afterhourPocketSlotInvBackpack.childItem != bag ||
                ui.blackscreen.gameObject.activeInHierarchy) return false;
            var visits = player.GetAfterhourState(AfterhourLocation.DUMPING_GROUND);
            Need(mapOpen == 1 && travel == 1 && returned == 0 && visits.visits == initialVisits + 1 &&
                visits.lastVisitDay == initialDay, "Travel callback or visit accounting changed");
            Need(ReferenceEquals(kept.parentInventory, bagContents), "Original backpack contents were lost on departure");
            acquired = New("wine_berry", bagContents);
            pocket = New("wine_yeast", entry.afterhourPocketSlotInvLeft);
            Need(playback.Rate == 16f, "Outing playback stopped before return");
            MapUIManager.Instance.ReturnNow(); step = 8; return false;
        }
        if (step == 8)
        {
            if (MapUIManager.Instance.IsOutside || player.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
                !entry.storeInteractables.activeInHierarchy || ui.blackscreen.gameObject.activeInHierarchy) return false;
            Need(mapOpen == 1 && travel == 1 && returned == 1, "Outing callbacks were skipped or repeated");
            Need(Stored(entry, bag) && ReferenceEquals(kept.parentInventory, bagContents) &&
                ReferenceEquals(acquired.parentInventory, bagContents) && Stored(entry, pocket),
                "Native return did not conserve the backpack, its contents and pocket item");
            Need(player.GetAfterhourState(AfterhourLocation.DUMPING_GROUND).visits == initialVisits + 1 &&
                Time.timeScale == initialScale, "Return changed visit count or global time");
            stableAfter = Time.realtimeSinceStartup + 0.5f; step = 9; return false;
        }
        if (step == 9)
        {
            if (Time.realtimeSinceStartup < stableAfter) return false;
            var bed = UnityEngine.Object.FindObjectOfType<StoreBedroll>();
            if (bed == null || EndOfDayUIManager.Instance == null) return false;
            bed.OnClick();
            Need(EndOfDayUIManager.Instance.IsOpen, "Native end-of-day screen did not open");
            Need(playback.Rate == 16f, "Toggle playback stopped before end-of-day presentation");
            savePath = SaveFiles.AbsolutePath(SaveFiles.Slot(player.saveSlotId));
            Need(File.Exists(savePath), "The isolated native save is unavailable");
            initialSaveWrite = File.GetLastWriteTimeUtc(savePath);
            observing = true;
            EndOfDayUIManager.Instance.OnEndDayButtonClick(); step = 10; return false;
        }
        if (step == 10)
        {
            if (nightAfter == 0 || ui.blackscreen.gameObject.activeInHierarchy) return false;
            Need(StoreStation.GetDayCounter() == initialDay + 1 && player.storeState == PlayerStore.StoreState.MORNING,
                "Ending the day did not advance exactly one day");
            Need(nightDuring == 1 && nightAfter == 1 && settlement.SequenceEqual(new[] { "EndNight", "OnDayEnd", "BeginDay", "SaveGame" }),
                "Night callback/settlement sequence changed: during=" + nightDuring + ", after=" + nightAfter +
                ", calls=" + string.Join(",", settlement));
            Need(File.Exists(savePath) && File.GetLastWriteTimeUtc(savePath) > initialSaveWrite,
                "Night settlement did not update the native save file");
            Need(Stored(entry, bag) && ReferenceEquals(kept.parentInventory, bagContents) &&
                ReferenceEquals(acquired.parentInventory, bagContents) && Stored(entry, pocket) &&
                Time.timeScale == initialScale, "Night settlement changed the outing items or global time");
            stableAfter = Time.realtimeSinceStartup + 0.5f; step = 11; return false;
        }
        if (step == 11)
        {
            if (Time.realtimeSinceStartup < stableAfter) return false;
            Need(StoreStation.GetDayCounter() == initialDay + 1 && nightDuring == 1 && nightAfter == 1 && settlement.Count == 4,
                "A delayed transition repeated night settlement or advanced another day");
            Cleanup();
            result = "PASS 16x native shutter, map outing and conserved return, one visit, one day and one ordered settlement/save";
            return true;
        }
        throw new InvalidOperationException("Unknown transition flow step " + step);
    }

    private void Setup()
    {
        Need(active == null, "Another transition flow observer is active");
        Need(StoreUIManager.Instance != null && StoreShutterButton.Instance != null && MapUIManager.Instance != null,
            "Native store controls are unavailable");
        initialDay = StoreStation.GetDayCounter(); initialScale = Time.timeScale;
        observerId = "qa.fastplay.flow." + Guid.NewGuid().ToString("N");
        observer = new Harmony(observerId); active = this;
        Observe(AccessTools.Method(typeof(MapUIManager), "<OpenUI>b__38_0"), nameof(MapOpened));
        Observe(Nested(typeof(MapUIManager), "<Travel>b__0"), nameof(Traveled));
        Observe(Nested(typeof(MapUIManager), "<ReturnNow>b__0"), nameof(Returned));
        Observe(Nested(typeof(EndOfDayUIManager), "<OnEndDayButtonClick>b__0"), nameof(NightDuring));
        Observe(Nested(typeof(EndOfDayUIManager), "<OnEndDayButtonClick>b__1"), nameof(NightAfter));
        Observe(AccessTools.Method(typeof(PlayerStore), "EndNight"), nameof(EndNight));
        Observe(AccessTools.Method(typeof(StoreEventManager), "OnDayEnd"), nameof(OnDayEnd));
        Observe(AccessTools.Method(typeof(PlayerStore), "BeginDay"), nameof(BeginDay));
        Observe(AccessTools.Method(typeof(PlayerStore), "SaveGame"), nameof(SaveGame));
        StoreUIManager.Instance.gameOptionUIManager.OpenUI();
        StoreUIManager.Instance.gameOptionUIManager.CloseUI();
        playback = new QaFastPlayback(mode: 1, speed: 3);
        playback.Release(); frame = Time.frameCount;
    }

    private static MethodInfo Nested(Type type, string name)
    {
        var matches = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(nested => nested.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(method => method.Name == name).ToArray();
        Need(matches.Length == 1, "Native transition callback unavailable: " + type.Name + "." + name);
        return matches[0];
    }

    private void Observe(MethodBase target, string name)
    {
        var patch = AccessTools.Method(typeof(FastPlaybackTransitionsFlowScenario), name);
        Need(target != null && patch != null, "Transition flow observer unavailable: " + name);
        observer.Patch(target, postfix: new HarmonyMethod(patch));
    }

    private static GameItem New(string id, GameInventory destination)
    {
        var item = DirectoryMaster.Item(id);
        Need(item != null, "Missing QA item " + id);
        GeneralHelper.SetItemOwned(item);
        var marker = destination.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == destination && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, destination),
            "Cannot place QA item " + id);
        return item;
    }

    private static bool Stored(EmporiumEntry entry, GameItem item)
    {
        var inventories = new[] { entry.backInvinvElement, entry.invElement, entry.showcaseElement };
        return item != null && inventories.Count(inventory => inventory != null &&
            ReferenceEquals(item.parentInventory, inventory) &&
            inventory.childItems.Count(child => ReferenceEquals(child, item)) == 1) == 1;
    }

    private static void MapOpened() { if (active != null) active.mapOpen++; }
    private static void Traveled() { if (active != null) active.travel++; }
    private static void Returned() { if (active != null) active.returned++; }
    private static void NightDuring() { if (active != null && active.observing) active.nightDuring++; }
    private static void NightAfter() { if (active != null && active.observing) active.nightAfter++; }
    private static void EndNight() { if (active != null && active.observing) active.settlement.Add("EndNight"); }
    private static void OnDayEnd() { if (active != null && active.observing) active.settlement.Add("OnDayEnd"); }
    private static void BeginDay() { if (active != null && active.observing) active.settlement.Add("BeginDay"); }
    private static void SaveGame() { if (active != null && active.observing) active.settlement.Add("SaveGame"); }

    private string State()
    {
        if (!PlayerStore.HasInstance) return "; no player";
        var player = PlayerStore.Instance;
        return "; day=" + StoreStation.GetDayCounter() + ", store=" + player.storeState +
            ", shutterLock=" + player.lockShutterButtonFlag + ", canEnd=" + player.CanEndDay() +
            ", map=" + (MapUIManager.Instance?.Current?.Id ?? "none") + ", callbacks=" + mapOpen + "/" + travel +
            "/" + returned + ", night=" + nightDuring + "/" + nightAfter + ", calls=" + string.Join(",", settlement);
    }

    private void Cleanup()
    {
        if (restored) return;
        restored = true; observing = false; active = null;
        if (observer != null) observer.UnpatchAll(observerId);
        playback?.Dispose(); playback = null;
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
