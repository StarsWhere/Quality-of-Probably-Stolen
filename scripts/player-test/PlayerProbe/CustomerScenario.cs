using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Uses a fresh isolated run, native item factories, inventory slots, options and customer UI.
public sealed class CustomerScenario
{
    private const string Mod = "starswhere.shopassist";
    private const string ListWindow = Mod + ".customer_inventory";
    private readonly QaNewRun start = new QaNewRun();
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 8);
    private GameItemElement ordinary, hiddenRejection, customTooltip, bag, nested, stolen, unseenBag;
    private int step;
    private float since = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 60)
            throw new InvalidOperationException("Customer QA timed out at step " + step);
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                Advance(1); return false;
            case 1:
                if (StoreUIManager.Instance?.gameOptionUIManager == null || EmporiumEntry.Instance?.invElement == null ||
                    NegociationUIManager.Instance == null || ItemMouseDoubleClickHandler.current == null) return false;
                Prepare();
                Advance(2); return false;
            case 2:
                if (!Outlined(ordinary) || !Outlined(hiddenRejection)) return false;
                Require(!Outlined(bag) && !Outlined(customTooltip),
                    "F03 highlighted an unrelated item or a replacement tooltip before display");
                var shown = customTooltip.GetTooltip();
                Require(shown != null && shown.nakedText.Contains("[" + TypeHelper.GetTypeDisplayName("FOOD") + "]"),
                    "Fixture did not produce the public custom tooltip");
                OverlayHandler.current.ShowTooltip(Vector2.zero, Vector2.zero, shown, 60);
                Advance(3); return false;
            case 3:
                if (!Outlined(customTooltip)) return false;
                OverlayHandler.current.HideTooltip();
                TradeScenario.SetOptions(false, "f03.enabled");
                Advance(4); return false;
            case 4:
                if (Outlined(ordinary) || Outlined(hiddenRejection) || Outlined(customTooltip)) return false;
                OpenList();
                var first = List();
                Require(QaItemUi.Hit(first.root, ordinary) != null && QaItemUi.Hit(first.root, hiddenRejection) != null && QaItemUi.Hit(first.root, nested) != null,
                    "F11 omitted public-category goods with F03 disabled, including the safe closed container");
                Require(!bag.contentWindow.IsVisible(), "F11 search opened the container as a side effect");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                    ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-customer-grid.png"));
                Advance(5); return false;
            case 5:
                var view = List();
                foreach (GameItem item in new[] { ordinary, hiddenRejection, customTooltip, nested }) RequireCaptionVisible(view, item);
                QaItemUi.Click(view, nested);
                Advance(6); return false;
            case 6:
                if (!bag.contentWindow.IsVisible() || !Outlined(nested)) return false;
                Require(ListOrNull() == null, "F11 did not close after locating the existing instance");
                Require(ReferenceEquals(nested.parentInventory, bag.contentWindow.childElement),
                    "F11 moved the located item");
                NegociationUIManager.Instance.CloseUI();
                PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateInspectionClient());
                PlayerStore.Instance.isClientArrived = true;
                Advance(7); return false;
            case 7:
                if (!Outlined(stolen) || !Outlined(bag)) return false;
                bag.contentWindow.Hide();
                Advance(8); return false;
            case 8:
                if (!Outlined(bag)) return false;
                Require(!Outlined(ordinary) && !Outlined(hiddenRejection), "Buyer demand survived the customer change");
                ItemMouseDoubleClickHandler.current.OpenContentAction(bag, Vector2.zero);
                Require(bag.contentWindow.IsVisible(), "Could not reopen the observed container");
                Put(EmporiumEntry.Instance.invElement, stolen);
                bag.contentWindow.Hide();
                Advance(9); return false;
            case 9:
                if (Outlined(bag) || !Outlined(stolen)) return false;
                // The optional tier-II lookup is deliberately separate from the defaults.
                if (ModConfig.Find(Mod).Get("f04.unseen") != null)
                {
                    TradeScenario.SetOptions(false, "f04.unseen");
                    unseenBag = NewItem("fanny_pack", "QA Never Opened " + token);
                    Put(EmporiumEntry.Instance.invElement, unseenBag);
                    var insideRisk = NewItem("wine_yeast", "QA Unseen " + token);
                    StolenHelper.SetHeat(insideRisk, 42);
                    Put((GameGridInventory)unseenBag.contentWindow.childElement, insideRisk);
                    unseenBag.contentWindow.Hide(); Advance(11); return false;
                }
                PlayerStore.Instance.currentClientInstance = null;
                PlayerStore.Instance.isClientArrived = false;
                Advance(10); return false;
            case 10:
                if (Outlined(stolen)) return false;
                result = "PASS F03 public buyer categories and tooltip evidence, F11 independent search and native nested locate, F04 observed risk and invalidation";
                return true;
            case 11:
                Require(!Outlined(unseenBag), "Strict mode discovered the contents of a never-opened bag");
                TradeScenario.SetOptions(true, "f04.unseen"); Advance(12); return false;
            case 12:
                if (!Outlined(unseenBag)) return false;
                Require(!unseenBag.contentWindow.IsVisible(), "Deep inspection opened the container");
                TradeScenario.SetOptions(false, "f04.unseen"); Advance(13); return false;
            case 13:
                if (Outlined(unseenBag)) return false;
                PlayerStore.Instance.currentClientInstance = null; PlayerStore.Instance.isClientArrived = false;
                Advance(10); return false;
            default: throw new InvalidOperationException("Unknown customer QA step");
        }
    }

    private void Prepare()
    {
        TradeScenario.SetOptions(true, "f03.enabled", "f04.enabled", "f04.stolen", "f04.contraband", "f11.enabled");
        var root = EmporiumEntry.Instance.invElement;
        ordinary = NewItem("processed_meat", "QA Ordinary " + token);
        hiddenRejection = NewItem("processed_juice", "QA Hidden " + token);
        customTooltip = NewItem("processed_milk", "QA Tooltip " + token);
        customTooltip.tooltipRichTextFunc = (item, inv, slot, shape, state) =>
            new RichTextBuilder(40).AddLine("[" + TypeHelper.GetTypeDisplayName("FOOD") + "]");
        bag = NewItem("backpack_medium", "QA Bag " + token);
        nested = NewItem("cup_noodle", "QA Nested " + token);
        stolen = NewItem("wine_yeast", "QA Stolen " + token);
        StolenHelper.SetHeat(stolen, 42);
        Require(bag.contentWindow?.childElement is GameGridInventory && bag.contentWindow.openCallback == null &&
            !bag.contentWindow.isLocked && !bag.IsTag("TRACK_OPENED_TAG"), "QA container is not an ordinary safe backpack");
        Put(root, ordinary); Put(root, hiddenRejection); Put(root, customTooltip); Put(root, bag);
        var inside = (GameGridInventory)bag.contentWindow.childElement;
        Put(inside, nested); Put(inside, stolen);
        bag.contentWindow.Hide();
        Require(ordinary.GetGameItemType().Contains("FOOD") && hiddenRejection.GetGameItemType().Contains("FOOD") &&
            nested.GetGameItemType().Contains("FOOD"), "The selected fixtures do not expose the food category");
        Require(ordinary.tooltipRichTextFunc == null && hiddenRejection.tooltipRichTextFunc == null &&
            stolen.tooltipRichTextFunc == null && ordinary.HasTooltip() && stolen.HasTooltip(), "Fixture tooltip is not ordinary");
        var client = StoreClientList.CreateSpacerClientFood();
        client.clientBlackIdList.Add(hiddenRejection.identifier);
        PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(client);
        PlayerStore.Instance.isClientArrived = true;
        NegociationUIManager.Instance.OpenUI();
        Require(NegociationUIManager.Instance.IsOpen, "Native buyer information did not open");
    }

    private static GameItemElement NewItem(string id, string name)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement;
        Require(item != null, "Missing native item " + id);
        GeneralHelper.SetItemOwned(item, true);
        GeneralHelper.SetCustomName(item, name);
        return item;
    }

    private static void Put(GameInventory inventory, GameItem item)
    {
        int count = item.unitCount;
        var marker = inventory.TryFindOneValidInventorySlot(item);
        Require(marker != null && marker.inventory == inventory && marker.targetItem == null && marker.numTransfer >= count &&
            marker.TryAcceptOnce(count) == count && ReferenceEquals(item.parentInventory, inventory),
            "Native slot did not place QA fixture " + item.identifier);
    }

    private static void OpenList()
    {
        RoutingScenario.OpenOptionAction("f11.open");
        Require(ListOrNull() != null, "F11 list did not open");
    }

    private static CustomUIWindow ListOrNull() => CustomUIManager.Instance.GetWindow(ListWindow);
    private static CustomUIWindow List() => ListOrNull() ?? throw new InvalidOperationException("F11 list is closed");
    internal static void RequireCaptionVisible(CustomUIWindow window, GameItem item)
    {
        var hit = QaItemUi.Hit(window.root, item);
        var caption = hit?.transform.parent?.parent?.Find("caption")?.GetComponent<TMP_Text>();
        Require(caption != null && caption.isActiveAndEnabled && caption.text == item.GetDisplayName(true), "Missing card caption");
        Canvas.ForceUpdateCanvases(); caption.ForceMeshUpdate(true, true);
        Require(caption.textInfo.characterInfo.Take(caption.textInfo.characterCount).Any(character => character.isVisible),
            "Card caption generated no visible glyphs: " + caption.text + " rect=" + caption.rectTransform.rect + " font=" + caption.font?.name);
    }
    private static bool Outlined(GameItemElement item)
    {
        var outline = item?.rotator?.transform.Find("ShopAssist_Outline");
        return outline != null && outline.gameObject.activeInHierarchy && outline.GetComponentsInChildren<Image>(true)
            .Any(image => image.isActiveAndEnabled && image.color.a > 0 && image.rectTransform.rect.width > 0 && image.rectTransform.rect.height > 0);
    }
    private void Advance(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
