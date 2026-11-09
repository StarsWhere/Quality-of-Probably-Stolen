using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;

public abstract class FieldLoadedPackingFixture
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 180f;
    private int step, attempts;
    private float returnedAt;
    private GameItem equipped, loaded, fresh, old, blocker, competitor;
    private GameGridInventory equippedGrid, loadedGrid, ground;
    protected abstract bool Mixed { get; }

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Move(GameItem item, GameInventory target)
    {
        SlotMarker marker = target.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == target && marker.targetItem == null && marker.numTransfer >= item.unitCount &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == target,
            "Could not move " + item.GetDisplayName(true));
    }
    private static void SetReserve()
    {
        var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
        var settings = CustomUIManager.Instance.GetWindow("game_options");
        settings.Get(QaTripCargo.Mod + ":f36.space").SetFloat(32, true);
        settings.Get(QaTripCargo.Mod + ":f36.replace").SetBool(false, true);
        settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
    }
    private static bool GroundVisible(GameItem item)
    {
        var element = item as GameItemElement;
        PixelWindow window = element?.handler?.FindParentWindow();
        return element != null && !element.IsDestroyed() && element.handler != null && element.handler.gameObject.activeInHierarchy &&
            window != null && window.IsVisible() && WindowsHandler.current?.visibleWindows?.Contains(window) == true;
    }
    private static bool HasUnsafeResult(GameItem item)
    {
        var mod = ModLoader.Mods.First(value => value.Manifest?.ID == QaTripCargo.Mod && value.Instance != null);
        var notice = mod.Instance.GetType().Assembly.GetType("ShopAssist.ShopNotice", true);
        object result = notice.GetField("lastResult", QaTripCargo.Static).GetValue(null);
        var rows = QaTripCargo.Field(result, "Items") as IEnumerable;
        return rows != null && rows.Cast<object>().Any(row => (int)QaTripCargo.Field(row, "ItemId") == item.uniqueId &&
            (string)QaTripCargo.Field(row, "Reason") == "f36.leave_unsafe");
    }
    private static bool RunPacking()
    {
        var action = QaItemUi.WindowAction(EmporiumEntry.Instance.afterhourWindow, "f36.open");
        if (action == null || action.isDisabled) return false;
        action.Select(); return true;
    }

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Loaded backpack scenario timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (entry?.storeInteractables?.activeInHierarchy != true) return false;
            TradeScenario.SetOptions(true, "f36.enabled"); QaTripCargo.SetMode(2); SetReserve();
            equipped = QaTripCargo.New("backpack_medium", entry.invElement);
            equippedGrid = (GameGridInventory)equipped.contentWindow.childElement;
            equippedGrid.SetShape(5, 5);
            old = QaTripCargo.New("wine_yeast_red", equippedGrid);
            blocker = QaTripCargo.New("wine_berry", equippedGrid);
            entry.selectedBackpack = equipped; PlayerStore.Instance.visitLeftTonight = 2;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND)); step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !entry.afterhourWindow.IsVisible() ||
                entry.afterhourPocketSlotInvBackpack.childItem != equipped || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            ground = entry.afterhourInventory;
            foreach (GameItem existing in ground.childItems.ToArray()) existing.Destroy();
            Move(blocker, entry.afterhourPocketSlotInvRight);
            loaded = QaTripCargo.New("backpack_small", ground); loaded.unitValue = 10;
            loadedGrid = loaded.contentWindow.childElement as GameGridInventory;
            Need(loadedGrid != null, "Loaded backpack has no native grid");
            fresh = DirectoryMaster.Item("wine_yeast"); Need(fresh != null, "Missing preloaded item");
            GeneralHelper.SetItemOwned(fresh, true); fresh.unitValue = 10000; Move(fresh, loadedGrid);
            if (Mixed) Move(old, loadedGrid);
            attempts = PlayerStore.Instance.scavengingAttempts; step++; return false;
        }
        if (step == 3)
        {
            if (!GroundVisible(loaded) || !RunPacking()) return false;
            Need(loaded.parentInventory == ground && fresh.parentInventory == loadedGrid && HasUnsafeResult(loaded),
                "Closed backpack contents were used without being visible");
            ItemMouseDoubleClickHandler.current.OpenContentAction(loaded, new Vector2(200, 160));
            loaded.contentWindow.ToFront(); step++; return false;
        }
        if (step == 4)
        {
            if (!loaded.contentWindow.IsVisible() || !GroundVisible(fresh) || Mixed && !GroundVisible(old)) return false;
            competitor = QaTripCargo.New("processed_meat", ground); competitor.unitValue = 5000;
            step++; return false;
        }
        if (step == 5)
        {
            if (!GroundVisible(competitor) || !RunPacking()) return false;
            Need(loaded.parentInventory == entry.afterhourPocketSlotInvLeft && fresh.parentInventory == loadedGrid &&
                competitor.parentInventory == ground && blocker.parentInventory == entry.afterhourPocketSlotInvRight &&
                (!Mixed || old.parentInventory == loadedGrid) && PlayerStore.Instance.scavengingAttempts == attempts,
                "Visible loaded backpack was not ranked as a complete object or existing contents changed");
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Application.persistentDataPath,
                    Mixed ? "qa-field-loaded-mixed.png" : "qa-field-loaded.png"));
            loaded.contentWindow.Hide(); MapUIManager.Instance.ReturnNow(); step++; return false;
        }
        if (MapUIManager.Instance.IsOutside || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
            equipped.parentInventory == entry.afterhourPocketSlotInvBackpack || entry.storeInteractables?.activeInHierarchy != true ||
            StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
        if (returnedAt == 0) returnedAt = Time.realtimeSinceStartup;
        object trip = QaTripCargo.Trip();
        Need(trip != null && !(bool)QaTripCargo.Field(trip, "uncertain"), "A whole-container transfer lost outing provenance");
        if (Mixed)
        {
            if (fresh.parentInventory != entry.invElement && Time.realtimeSinceStartup - returnedAt < 8) return false;
            Need(fresh.parentInventory == entry.invElement && old.parentInventory == loadedGrid && loadedGrid.childItems.Contains(old) &&
                loaded.parentInventory != null && QaTripCargo.Gains().Any(gain => (int)QaTripCargo.Field(gain, "id") == loaded.uniqueId),
                "New-only return moved old contents or failed to unload the verified new child");
        }
        else
        {
            if (QaTripCargo.Gains().Length != 0 && Time.realtimeSinceStartup - returnedAt < 8) return false;
            Need(QaTripCargo.Gains().Length == 0 && loaded.parentInventory == entry.invElement && fresh.parentInventory == loadedGrid &&
                old.parentInventory == equippedGrid && blocker.parentInventory != null,
                "An entirely new backpack did not return intact while retaining prior equipment: " +
                ModHelper.GetModData(QaTripCargo.Mod, "tripCargo", ""));
        }
        result = Mixed ? "PASS visible loaded backpack and mixed old/new return provenance" :
            "PASS closed-container privacy, loaded backpack ranking and intact automatic return";
        return true;
    }
}

public sealed class FieldLoadedPackingScenario : FieldLoadedPackingFixture { protected override bool Mixed => false; }
public sealed class FieldMixedPackingScenario : FieldLoadedPackingFixture { protected override bool Mixed => true; }
