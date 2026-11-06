using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public sealed class OrderScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem bag, goods;
    private StoreRequest order;
    private int step, cash;
    private string tag = "QA delivery " + Guid.NewGuid().ToString("N").Substring(0, 6);
    private static void Need(bool ok, string what) { if (!ok) throw new InvalidOperationException(what); }
    private static CustomUIWindow Book() => CustomUIManager.Instance.GetWindow(Mod + ".requests");

    public bool Tick(out string result)
    {
        result = null;
        if (step == 0) { if (start.Tick()) step++; return false; }
        var e = EmporiumEntry.Instance;
        var p = PlayerStore.Instance;
        if (step == 1)
        {
            if (e.storeInteractables == null || !e.storeInteractables.activeInHierarchy || PCUIManager.Instance == null) return false;
            TradeScenario.SetOptions(true, "f27.enabled");
            bag = DirectoryMaster.Item("backpack_medium"); goods = DirectoryMaster.Item("wine_yeast_red");
            GeneralHelper.SetItemOwned(bag); GeneralHelper.SetItemOwned(goods); GeneralHelper.SetCustomName(bag, tag);
            Place(bag); Place(goods);
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width / 2, Screen.height / 2));
            order = new StoreRequest { title = tag, id = tag, description = tag, requiredItem = goods.identifier, requiredItemCount = 1,
                requesterName = "QA", sourceEventId = tag, duration = 2, creditReward = 155 };
            p.storeRequests.Add(order);
            cash = p.playerCash;
            PCUIManager.Instance.OpenUI();
            step++; return false;
        }
        if (step == 2)
        {
            var row = UnityEngine.Object.FindObjectsOfType<StoreRequestElement>().FirstOrDefault(r => r.storeRequest == order);
            Need(row != null, "Native market row missing");
            var button = row.acceptButton.transform.parent.Find(Mod + ".track")?.GetComponent<Button>();
            Need(button != null && button.interactable, "Independent track button missing");
            button.onClick.Invoke();
            Need(p.storeRequests.Contains(order) && p.playerCash == cash && goods.parentInventory == e.invElement, "Track button accidentally delivered order");
            Book().Get("track").button.onClick.Invoke();
            QaItemUi.Choose(Book(), "destination", tag);
            var b = Book();
            QaItemUi.Click(b, goods);
            b.Get("prepare").button.onClick.Invoke();
            Need(goods.parentInventory == bag.contentWindow.childElement && p.storeRequests.Contains(order) && p.playerCash == cash,
                "Preparing order did not move the exact item, or redeemed the reward");
            var raw = ModHelper.GetModData(Mod, "requests", null);
            Need(raw != null && raw.Contains(tag) && raw.Contains(goods.uniqueId.ToString()), "Reservation was not stored");
            Book().Get("prepare").button.onClick.Invoke();
            Need(((GameInventory)bag.contentWindow.childElement).childItems.Count(i2 => i2 == goods) == 1 && p.playerCash == cash,
                "Second click duplicated an item or credited money");
            result = "PASS native public request row, independent track action, selection of real destination, exact physical reservation and idempotent prepare without delivery";
            return true;
        }
        return false;
    }
    private static void Place(GameItem item)
    {
        var root = EmporiumEntry.Instance.invElement;
        var marker = root.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == root && marker.TryAcceptOnce() > 0 && item.parentInventory == root, "Could not place request fixture");
    }
}
