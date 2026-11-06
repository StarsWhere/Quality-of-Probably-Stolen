using System;
using System.IO;
using System.Linq;
using UnityEngine;

public sealed class WholesaleScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private GameItem first, second;
    private int step, paidBalance;
    private static void Need(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }
    public bool Tick(out string result)
    {
        result = null;
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        switch (step)
        {
            case 1:
                if (e.storeInteractables?.activeInHierarchy != true || NegociationUIManager.Instance == null) return false;
                TradeScenario.SetOptions(true, "f01.wholesale");
                var client = StoreClientList.CreateJunkerSellOnly();
                p.currentClientInstance = StoreClientInstance.CreateClientInstance(client); p.isClientArrived = true;
                client.isOfferWholesale = true; client.wholesaleDiscount = 20;
                foreach (var it in e.GetAllNonOwnedItem()) it.Destroy();
                first = Add("wine_yeast", e.frontInvinvElement); second = Add("wine_superyeast", e.backInvinvElement);
                int before = p.playerCash;
                bool locked = e.backInvinvElement.overrideLockInsert;
                e.backInvinvElement.overrideLockInsert = true;
                try
                {
                    NegociationUIManager.Instance.OnWholesaleClick();
                    Need(GenericConfirmationUIManager.Instance.panel.activeSelf && p.playerCash == before && !GeneralHelper.IsItemOwned(first),
                        "Blocked wholesale space failed to guard native settlement");
                    GenericConfirmationUIManager.Instance.CancelButtonPressed();
                }
                finally { e.backInvinvElement.overrideLockInsert = locked; }
                int price = p.GetWholesalePrice();
                NegociationUIManager.Instance.OnWholesaleClick();
                Need(price > 0 && p.playerCash == before - price && GeneralHelper.IsItemOwned(first) && GeneralHelper.IsItemOwned(second) &&
                    first.parentInventory == e.backInvinvElement && second.parentInventory == e.backInvinvElement, "Wholesale settlement lost cash or stock");
                paidBalance = p.playerCash;
                p.currentClientInstance = null; p.isClientArrived = false;
                MapUIManager.Instance.Travel(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND));
                step++; return false;
            case 2:
                if (!MapUIManager.Instance.IsOutside) return false;
                MapUIManager.Instance.ReturnNow(); step++; return false;
            case 3:
                if (MapUIManager.Instance.IsOutside || p.playerLocation != PlayerStore.PlayerLocation.IN_STORE || !e.storeInteractables.activeInHierarchy) return false;
                Need(first.parentInventory == e.backInvinvElement && second.parentInventory == e.backInvinvElement && p.playerCash == paidBalance,
                    "Purchased wholesale goods did not survive leaving and returning");
                string data = string.Join("|", p.runID, p.saveSlotId, first.GetUniqueID(), second.GetUniqueID(), paidBalance);
                File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-wholesale.txt"), data);
                p.SaveGame(); result = "PASS native wholesale space cancellation, single charge, retained purchased goods after outing, saved isolated store"; return true;
        }
        return false;
    }
    private static GameItem Add(string id, GameInventory grid)
    {
        var item = DirectoryMaster.Item(id); GeneralHelper.SetItemOwned(item, false);
        var m = grid.TryFindOneValidInventorySlot(item);
        Need(m != null && m.inventory == grid && m.TryAcceptOnce() == item.unitCount, "Probe could not place wholesale item");
        return item;
    }
}

public sealed class WholesaleReloadScenario
{
    private QaLoadSlot load;
    private string[] data;
    public bool Tick(out string result)
    {
        result = null;
        if (load == null)
        {
            data = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-wholesale.txt")).Trim().Split('|');
            load = new QaLoadSlot(int.Parse(data[1]), Guid.Parse(data[0]));
        }
        if (!load.Tick()) return false;
        var items = EmporiumEntry.Instance.backInvinvElement.childItems;
        foreach (int id in new[] { int.Parse(data[2]), int.Parse(data[3]) })
            if (items.Count(i => i.uniqueId == id && GeneralHelper.IsItemOwned(i)) != 1)
                throw new InvalidOperationException("Cold save lost or duplicated a paid counter item: " + id);
        if (PlayerStore.Instance.playerCash != int.Parse(data[4])) throw new InvalidOperationException("Cold wholesale balance changed");
        result = "PASS paid counter instances, ownership and cash preserved on genuine cold reload"; return true;
    }
}
