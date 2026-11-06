using System;
using System.Linq;
using UnityEngine;

// Tests old area bindings after their owning boxes are moved to trade/display roots.
public sealed class RoutingBoundaryScenario
{
    private const string Mod = "starswhere.shopassist", Rules = Mod + ".routes", Preview = Mod + ".route_preview";
    private readonly QaNewRun start = new QaNewRun();
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 8);
    private GameItemElement goodBag, movedBag, nested, stockItem;
    private GameGridInventory goodGrid, movedGrid;
    private string saved;
    private int step, branch;
    private float since = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 75) throw new InvalidOperationException("Routing boundary QA timed out at step " + step + "/" + branch);
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                Advance(1); return false;
            case 1:
                if (StoreUIManager.Instance?.gameOptionUIManager == null || EmporiumEntry.Instance?.invElement == null ||
                    ItemMouseDoubleClickHandler.current == null || ItemMultiSelectHandler.current == null) return false;
                Prepare();
                RoutingScenario.CreateCategoryRule(goodBag, "MATERIAL");
                RoutingScenario.CreateFoodRule(movedBag);
                saved = ModHelper.GetModData(Mod, "routing", null);
                Require(!string.IsNullOrEmpty(saved), "No persisted area rules");
                OpenRuleEditorForMovedBag();
                Move(EmporiumEntry.Instance.backInvinvElement, movedBag);
                RoutingScenario.Window(Rules).Get("save").button.onClick.Invoke();
                Require(ModHelper.GetModData(Mod, "routing", null) == saved,
                    "A stale F07 editor wrote rules after its area moved to the counter");
                RoutingScenario.Window(Rules).Close();
                Advance(2); return false;
            case 2:
                if (branch >= 3) { Advance(3); return false; }
                GameInventory root = branch == 0 ? EmporiumEntry.Instance.backInvinvElement :
                    branch == 1 ? EmporiumEntry.Instance.frontInvinvElement : EmporiumEntry.Instance.showcaseElement;
                if (movedBag.parentInventory != root) Move(root, movedBag);
                if (!movedBag.contentWindow.IsVisible()) ItemMouseDoubleClickHandler.current.OpenContentAction(movedBag, Vector2.zero);
                if (branch == 0)
                {
                    PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
                    PlayerStore.Instance.isClientArrived = true;
                }
                AssertRuleEditorHidesMovedBag();
                AssertNoMove(nested, "A nested " + branch + " item was admitted as a source");
                AssertNoMove(stockItem, "An old rule sent stock into a nested " + branch + " box");
                Require(ReferenceEquals(nested.parentInventory, movedGrid) && ReferenceEquals(stockItem.parentInventory, EmporiumEntry.Instance.invElement) &&
                    ModHelper.GetModData(Mod, "routing", null) == saved, "A blocked preview changed goods or erased the stored rules");
                PlayerStore.Instance.currentClientInstance = null;
                PlayerStore.Instance.isClientArrived = false;
                branch++;
                since = Time.realtimeSinceStartup;
                return false;
            case 3:
                Move(EmporiumEntry.Instance.invElement, movedBag);
                OpenPreview(stockItem);
                var apply = RoutingScenario.Button(RoutingScenario.Window(Preview), "f08.apply");
                Require(apply != null && apply.interactable, "The stored rule did not reactivate after its box returned to main stock");
                apply.onClick.Invoke();
                Require(ReferenceEquals(stockItem.parentInventory, movedGrid) && movedGrid.childItems.Contains(stockItem),
                    "The valid main-stock target did not receive the selected item");
                result = "PASS F07 stale editor and hidden off-stock areas; F08 rejects nested counter/front/showcase sources and targets while preserving valid main-stock rules";
                return true;
            default: throw new InvalidOperationException("Unknown routing boundary QA step");
        }
    }

    private void Prepare()
    {
        TradeScenario.SetOptions(true, "f07.enabled", "f08.enabled", "f09.enabled");
        var options = StoreUIManager.Instance.gameOptionUIManager;
        options.OpenUI();
        var mode = CustomUIManager.Instance.GetWindow("game_options").Get(Mod + ":f09.mode");
        Require(mode?.dropdown != null, "Preview mode control missing");
        mode.SetIndex(0, true);
        CustomUIManager.Instance.GetWindow("game_options").Get("apply").button.onClick.Invoke();
        options.CloseUI();
        goodBag = New("backpack_medium", "QA Stock Destination " + token);
        movedBag = New("backpack_medium", "QA Moving Area " + token);
        nested = New("scrap_metal", "QA Nested Material " + token);
        stockItem = New("processed_meat", "QA Stock Food " + token);
        var entry = EmporiumEntry.Instance;
        Move(entry.invElement, goodBag); Move(entry.invElement, movedBag); Move(entry.invElement, stockItem);
        goodGrid = goodBag.contentWindow?.childElement as GameGridInventory;
        movedGrid = movedBag.contentWindow?.childElement as GameGridInventory;
        Require(goodGrid != null && movedGrid != null, "Expected two ordinary backpack grids");
        Move(movedGrid, nested);
        entry.backInvWindow.Show(); entry.frontInvWindow.Show(); entry.showcaseWindow.Show();
        ItemMouseDoubleClickHandler.current.OpenContentAction(goodBag, new Vector2(300, 300));
        ItemMouseDoubleClickHandler.current.OpenContentAction(movedBag, new Vector2(650, 300));
    }

    private void OpenRuleEditorForMovedBag()
    {
        RoutingScenario.OpenOptionAction("f07.open");
        QaItemUi.Choose(RoutingScenario.Window(Rules), "area", movedBag.GetDisplayName(true));
    }

    private void AssertRuleEditorHidesMovedBag()
    {
        RoutingScenario.OpenOptionAction("f07.open");
        var picker = QaItemUi.OpenPicker(RoutingScenario.Window(Rules), "area");
        Require(QaItemUi.Hit(picker.root, movedBag) == null && QaItemUi.Hit(picker.root, goodBag) != null,
            "F07 displayed an area outside the main-stock branch");
        picker.Close();
        RoutingScenario.Window(Rules).Close();
    }

    private static void OpenPreview(GameItemElement item)
    {
        RoutingScenario.SetSelection(item);
        RoutingScenario.OpenOptionAction("f08.open");
    }
    private static void AssertNoMove(GameItemElement item, string message)
    {
        OpenPreview(item);
        var window = RoutingScenario.Window(Preview);
        var apply = RoutingScenario.Button(window, "f08.apply");
        Require(apply != null && !apply.interactable, message);
        window.Close();
    }
    private static GameItemElement New(string id, string name)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement;
        Require(item != null, "Missing native fixture " + id);
        GeneralHelper.SetItemOwned(item, true); GeneralHelper.SetCustomName(item, name);
        return item;
    }
    private static void Move(GameInventory inventory, GameItem item)
    {
        var marker = inventory.TryFindOneValidInventorySlot(item);
        Require(marker != null && marker.inventory == inventory && marker.targetItem == null && marker.numTransfer >= item.unitCount &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, inventory),
            "Could not place fixture " + item.identifier);
    }
    private void Advance(int value) { step = value; since = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
