using System;
using System.Linq;
using UnityEngine;

public sealed class CargoHiddenOldScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private const string Secret = "QA never opened old cargo";
    private GameItem equipped, nested, secret, visibleOld;
    private GameGridInventory equippedGrid, nestedGrid;
    private int step;
    private float waitUntil;
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool HasGain(GameItem item) => QaTripCargo.Gains().Any(value => (int)QaTripCargo.Field(value, "id") == item.uniqueId);

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Hidden old cargo timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (entry?.storeInteractables?.activeInHierarchy != true) return false;
            QaTripCargo.SetMode(1);
            equipped = QaTripCargo.New("backpack_large", entry.invElement);
            equippedGrid = (GameGridInventory)equipped.contentWindow.childElement;
            nested = DirectoryMaster.Item("fanny_pack"); Need(nested != null, "Missing native nested backpack");
            GeneralHelper.SetItemOwned(nested, true); nested.EnableTag("NESTABLE_DEVICE_TAG");
            Need(ContainerHelper.IsNestingAllowed(nested, equipped), "Native nested backpack exception unavailable");
            QaTripCargo.Put(equippedGrid, nested, nested.unitCount);
            nestedGrid = (GameGridInventory)nested.contentWindow.childElement;
            secret = QaTripCargo.New("wine_yeast", nestedGrid); GeneralHelper.SetCustomName(secret, Secret);
            visibleOld = QaTripCargo.New("wine_yeast_red", equippedGrid);
            Need(!nested.contentWindow.IsVisible(), "Nested fixture was unexpectedly opened");
            entry.selectedBackpack = equipped; PlayerStore.Instance.visitLeftTonight = 2;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND)); step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !entry.afterhourWindow.IsVisible() ||
                entry.afterhourPocketSlotInvBackpack.childItem != equipped || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            Need(!nested.contentWindow.IsVisible() && secret.parentInventory == nestedGrid, "Nested contents unexpectedly became visible");
            MapUIManager.Instance.ReturnNow(); step++; return false;
        }
        if (MapUIManager.Instance.IsOutside || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
            equipped.parentInventory == entry.afterhourPocketSlotInvBackpack || entry.storeInteractables?.activeInHierarchy != true ||
            StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
        if (step == 3)
        {
            if (visibleOld.parentInventory != entry.invElement) return false;
            Need(secret.parentInventory == nestedGrid && nested.parentInventory == equippedGrid && HasGain(secret) && HasGain(nested),
                "Unopened nested old cargo was moved or forgotten");
            Need(QaTripCargo.Issues().All(issue => (int)QaTripCargo.Field(issue, "ItemId") != secret.uniqueId &&
                !((string)QaTripCargo.Field(issue, "Title") ?? "").Contains(Secret) &&
                !((string)QaTripCargo.Field(issue, "Location") ?? "").Contains(Secret)), "Attention exposed unopened old cargo");
            if (!QaItemUi.OpenAttention()) return false;
            var window = CustomUIManager.Instance.GetWindow(QaTripCargo.Mod + ".attention");
            Need(!QaTripCargo.Text(window).Contains(Secret), "Rendered attention exposed unopened old cargo");
            window.Close();
            ItemMouseDoubleClickHandler.current.OpenContentAction(equipped, new Vector2(140, 120)); equipped.contentWindow.ToFront();
            step++; return false;
        }
        if (step == 4)
        {
            if (!equipped.contentWindow.IsVisible()) return false;
            ItemMouseDoubleClickHandler.current.OpenContentAction(nested, new Vector2(200, 160)); nested.contentWindow.ToFront();
            step++; return false;
        }
        if (step == 5)
        {
            var element = secret as GameItemElement; PixelWindow view = element?.handler?.FindParentWindow();
            if (!nested.contentWindow.IsVisible() || element?.handler?.gameObject.activeInHierarchy != true || view?.IsVisible() != true ||
                WindowsHandler.current?.visibleWindows?.Contains(view) != true) return false;
            QaTripCargo.SetMode(1); waitUntil = Time.realtimeSinceStartup + 4; step++; return false;
        }
        if (HasGain(secret) && Time.realtimeSinceStartup < waitUntil) return false;
        Need(!HasGain(secret) && secret.parentInventory != null, "Opening the nested container did not permit the confirmed old item to unload");
        result = "PASS old direct cargo unloads while unopened nested old contents remain private";
        return true;
    }
}
