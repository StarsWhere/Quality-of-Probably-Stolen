using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public sealed class BasketQuoteStagingScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step, money;
    private GameItem offered, unseen;
    private float since = Time.realtimeSinceStartup;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow W(string id) => CustomUIManager.Instance.GetWindow(Mod + "." + id);
    private static void Action(string key) => RoutingScenario.RunFeatureAction(key);
    private static GameItem Item(string id, string label, GameInventory grid)
    {
        var i = DirectoryMaster.Item(id); Need(i != null, "Missing native fixture " + id);
        GeneralHelper.SetItemOwned(i); GeneralHelper.SetCustomName(i, label); Put(i, grid); return i;
    }
    private static void Put(GameItem i, GameInventory grid)
    {
        var m = grid.TryFindOneValidInventorySlot(i);
        Need(m != null && m.inventory == grid && m.targetItem == null && m.TryAcceptOnce() > 0 && i.parentInventory == grid,
            "Native fixture placement failed: " + i.identifier);
    }
    private static string Labels(CustomUIWindow w) => string.Join(" | ", w.root.GetComponentsInChildren<TMPro.TMP_Text>().Select(t => t.text));
    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 55) throw new TimeoutException("Basket step " + step);
        if (step == 0) { if (start.Tick()) { step++; since = Time.realtimeSinceStartup; } return false; }
        var p = PlayerStore.Instance; var e = EmporiumEntry.Instance;
        switch (step)
        {
            case 1:
                if (e.storeInteractables?.activeInHierarchy != true || NegociationUIManager.Instance == null) return false;
                TradeScenario.SetOptions(true, "f34.enabled", "f34.move");
                var buyer = StoreClientList.CreateSpacerClientFood(); buyer.SetBudget(9000); buyer.isIntroduced = true;
                p.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer); p.isClientArrived = true;
                offered = Item("cup_noodle", "QA quoted noodles", e.backInvinvElement);
                unseen = Item("processed_meat", "QA unquoted meat", e.invElement);
                NegociationUIManager.Instance.OpenUI(); money = p.playerCash;
                step++; return false;
            case 2:
                if (!NegociationUIManager.Instance.IsOpen) return false;
                Action("f34.open");
                var lines = W("basket");
                Need(lines.Get("compare") != null && Labels(lines).Contains("QA unquoted meat"), "Public buyer candidates missing: " + Labels(lines));
                Need(QaItemUi.Hit(lines.root, offered) != null, "No native counter quote was observed: " + Labels(lines));
                lines.Close(); Put(offered, e.invElement); NegociationUIManager.Instance.RefreshUI();
                step++; return false;
            case 3:
                Action("f34.open"); W("basket").Get("compare").button.onClick.Invoke();
                var basket = W("basket");
                var place = basket.root.GetComponentsInChildren<Button>().FirstOrDefault(b => b.GetComponentsInChildren<TMPro.TMP_Text>().Any(t =>
                    t.text == ModHelper.GetLocalized(Mod, "f34.place")));
                Need(place != null && offered.parentInventory == e.invElement && p.playerCash == money, "Basket preview changed stock/cash or no offer: " + Labels(basket));
                place.onClick.Invoke(); step++; return false;
            case 4:
                if (offered.parentInventory != e.backInvinvElement || unseen.parentInventory != e.backInvinvElement) return false;
                Need(p.playerCash == money && GeneralHelper.IsItemOwned(offered) && GeneralHelper.IsItemOwned(unseen),
                    "Staging calculated and observed quotes sold goods or changed cash");
                result = "PASS observed and calculated customer quotes staged without sale";
                return true;
        }
        return false;
    }
}
