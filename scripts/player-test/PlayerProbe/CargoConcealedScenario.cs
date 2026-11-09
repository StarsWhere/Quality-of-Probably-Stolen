using System;
using System.Linq;
using UnityEngine;

public sealed class CargoConcealedScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private int step;
    private float returnedAt;
    private GameItem equipped, foundBag, concealed;
    private GameGridInventory contents;
    private const string Secret = "QA concealed cargo identifier";

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static object ConcealedIssue() => QaTripCargo.Issues().FirstOrDefault(issue =>
        ((string)QaTripCargo.Field(issue, "Key"))?.StartsWith("f25:concealed:") == true);
    private static bool HasConcealedGain(GameItem item) => QaTripCargo.Gains().Any(gain => (int)QaTripCargo.Field(gain, "id") == item.uniqueId);
    private static void BeginConfirmation()
    {
        object issue = ConcealedIssue();
        Need(issue != null && QaTripCargo.Field(issue, "Resolve") is Action, "Concealed cargo cannot be acknowledged");
        ((Action)QaTripCargo.Field(issue, "Resolve"))();
        Need(GenericConfirmationUIManager.Instance.panel.activeInHierarchy, "Concealed cargo did not request confirmation");
    }

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Concealed cargo timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (entry?.storeInteractables?.activeInHierarchy != true) return false;
            QaTripCargo.SetMode(2);
            equipped = QaTripCargo.New("backpack_medium", entry.invElement);
            entry.selectedBackpack = equipped; PlayerStore.Instance.visitLeftTonight = 2;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND)); step++; return false;
        }
        if (step == 2)
        {
            if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !entry.afterhourWindow.IsVisible() ||
                entry.afterhourPocketSlotInvBackpack.childItem != equipped || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
            foundBag = QaTripCargo.New("backpack_small", entry.afterhourInventory);
            contents = (GameGridInventory)foundBag.contentWindow.childElement;
            concealed = DirectoryMaster.Item("wine_yeast"); Need(concealed != null, "Missing concealed fixture");
            GeneralHelper.SetItemOwned(concealed, true); GeneralHelper.SetCustomName(concealed, Secret);
            QaTripCargo.Put(contents, concealed, concealed.unitCount);
            Need(!foundBag.contentWindow.IsVisible(), "Concealed fixture was unexpectedly open");
            QaTripCargo.Put(entry.afterhourPocketSlotInvLeft, foundBag, foundBag.unitCount);
            Need(concealed.parentInventory == contents, "Native whole-bag move lost concealed cargo");
            MapUIManager.Instance.ReturnNow(); step++; return false;
        }
        if (MapUIManager.Instance.IsOutside || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
            equipped.parentInventory == entry.afterhourPocketSlotInvBackpack || entry.storeInteractables?.activeInHierarchy != true ||
            StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
        if (returnedAt == 0) returnedAt = Time.realtimeSinceStartup;
        if (Time.realtimeSinceStartup - returnedAt < 1) return false;
        Need(HasConcealedGain(concealed) && concealed.parentInventory == contents, "Unobserved cargo was unloaded or forgotten");
        object[] issues = QaTripCargo.Issues();
        Need(issues.All(issue => (int)QaTripCargo.Field(issue, "ItemId") != concealed.uniqueId &&
            !((string)QaTripCargo.Field(issue, "Title") ?? "").Contains(Secret) &&
            !((string)QaTripCargo.Field(issue, "Location") ?? "").Contains(Secret)), "Attention leaked concealed cargo details");
        if (!QaItemUi.OpenAttention()) return false;
        var attention = CustomUIManager.Instance.GetWindow(QaTripCargo.Mod + ".attention");
        Need(!QaTripCargo.Text(attention).Contains(Secret), "Rendered attention leaked concealed cargo");
        BeginConfirmation();
        int before = concealed.unitCount; concealed.unitCount = before + 1;
        GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
        Need(HasConcealedGain(concealed), "A changed concealed stack was acknowledged from a stale confirmation");
        concealed.unitCount = before;
        BeginConfirmation(); GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
        Need(!HasConcealedGain(concealed) && concealed.parentInventory == contents,
            "Confirmed manual handling did not clear only the concealed record");
        result = "PASS concealed cargo privacy and stale-confirmation protection";
        return true;
    }
}
