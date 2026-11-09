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
    private CustomUIWindow retainedList;
    private Vector2 listPosition, listSize;
    private float listScroll;
    private int step, captureFrame;
    private string gridCapture;
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
                if (!Outlined(ordinary) || !Outlined(customTooltip)) return false;
                Require(!Outlined(hiddenRejection), "F03 highlighted a native buyer rejection");
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
                var query = List().Get("query");
                Require(query?.input != null, "F11 search input is unavailable");
                query.SetString(token, true); query.input.onEndEdit.Invoke(token);
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    gridCapture = Path.Combine(Application.persistentDataPath, "qa-customer-grid-" + token + ".png");
                    ScreenCapture.CaptureScreenshot(gridCapture);
                    Debug.Log("[PLAYER-QA] customer diagnostic capture " + gridCapture);
                }
                captureFrame = Time.frameCount + 4; Advance(40); return false;
            case 40:
                if (Time.frameCount < captureFrame || gridCapture != null && !File.Exists(gridCapture)) return false;
                var first = List();
                var buyer = PlayerStore.Instance.currentClientInstance.GetClientBlueprint();
                Debug.Log("[PLAYER-QA] customer fixture: " + QaItemUi.Describe(first.root, ordinary, customTooltip, hiddenRejection, nested) +
                    " | native acceptance=" + buyer.IsClientBuyingThisItem(ordinary) + "/" + buyer.IsClientBuyingThisItem(customTooltip) +
                    "/" + buyer.IsClientBuyingThisItem(hiddenRejection) + "/" + buyer.IsClientBuyingThisItem(nested));
                Require(first.Get("query")?.input?.text == token && QaItemUi.Hit(first.root, ordinary) != null &&
                    QaItemUi.Hit(first.root, customTooltip) != null && QaItemUi.Hit(first.root, hiddenRejection) != null &&
                    QaItemUi.Hit(first.root, nested) != null,
                    "F11 did not find all matching owned stock independently of buyer acceptance: " +
                    QaItemUi.Describe(first.root, ordinary, customTooltip, hiddenRejection, nested));
                Require(!bag.contentWindow.IsVisible(), "F11 search opened the container as a side effect");
                Require(!CardTexts(ordinary).Contains(ModHelper.GetLocalized(Mod, "f11.purchase_record", 14)),
                    "Purchase comparison remained visible while disabled");
                var options = StoreUIManager.Instance.gameOptionUIManager;
                options.OpenUI();
                var settings = CustomUIManager.Instance.GetWindow("game_options");
                settings.Get(Mod + ":f11.purchase").SetBool(true, true);
                options.CloseUI();
                var discard = CustomUIManager.Instance.GetWindow(Mod + ".settings.discard");
                Require(discard?.Get("discard")?.button != null, "Unapplied purchase setting did not ask before closing");
                discard.Get("discard").button.onClick.Invoke();
                Require(!((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f11.purchase")).Value,
                    "Cancelling native options enabled purchase comparison");
                TradeScenario.SetOptions(true, "f11.purchase");
                Advance(41); return false;
            case 41:
                if (Time.realtimeSinceStartup - since < .7f) return false;
                Require(CardTexts(ordinary).Contains(ModHelper.GetLocalized(Mod, "f11.purchase_record", 14)) &&
                    CardTexts(customTooltip).Contains(ModHelper.GetLocalized(Mod, "f11.purchase_unknown")) &&
                    CardTexts(nested).Contains(ModHelper.GetLocalized(Mod, "f11.purchase_record", 0)),
                    "Purchase cards did not distinguish recorded prices, missing records and real zero");
                Require(ordinary.GetTagReadonly("BOUGHT_PRICE_TAG").GetInt() == 14 && !customTooltip.IsTag("BOUGHT_PRICE_TAG") &&
                    ordinary.parentInventory == EmporiumEntry.Instance.invElement && nested.parentInventory == bag.contentWindow.childElement,
                    "Showing purchase records mutated items");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    gridCapture = Path.Combine(Application.persistentDataPath, "qa-customer-prices-" + token + ".png");
                    ScreenCapture.CaptureScreenshot(gridCapture);
                }
                captureFrame = Time.frameCount + 4; Advance(42); return false;
            case 42:
                if (Time.frameCount < captureFrame || gridCapture != null && !File.Exists(gridCapture)) return false;
                TradeScenario.SetOptions(false, "f11.purchase");
                Advance(43); return false;
            case 43:
                if (Time.realtimeSinceStartup - since < .7f) return false;
                Require(!CardTexts(ordinary).Contains(ModHelper.GetLocalized(Mod, "f11.purchase_record", 14)),
                    "Disabling purchase comparison did not refresh the existing list");
                Advance(5); return false;
            case 5:
                var view = List();
                foreach (GameItem item in new[] { ordinary, hiddenRejection, customTooltip, nested }) RequireCaptionVisible(view, item);
                retainedList = view;
                Require(!CardTexts(ordinary).Contains(ModHelper.GetLocalized(Mod, "f11.pinned")),
                    "Ordinary fixture was already pinned");
                PlayerStore.Instance.currentClientInstance.GetClientBlueprint().clientBlackIdList.Add(ordinary.identifier);
                ItemPinHelper.SetPinned(ordinary, true);
                Advance(50); return false;
            case 50:
                if (Time.realtimeSinceStartup - since < .7f) return false;
                Require(!PlayerStore.Instance.currentClientInstance.GetClientBlueprint().IsClientBuyingThisItem(ordinary) &&
                    List() == retainedList && List().Get("query")?.input?.text == token &&
                    QaItemUi.Hit(List().root, ordinary) != null && QaItemUi.Hit(List().root, hiddenRejection) != null &&
                    CardTexts(ordinary).Contains(ModHelper.GetLocalized(Mod, "f11.pinned")),
                    "F11 lost rejected stock or failed to refresh its pinned status");
                PlayerStore.Instance.currentClientInstance.GetClientBlueprint().clientBlackIdList.Remove(ordinary.identifier);
                ItemPinHelper.SetPinned(ordinary, false);
                Advance(51); return false;
            case 51:
                if (Time.realtimeSinceStartup - since < .7f) return false;
                view = List();
                Require(PlayerStore.Instance.currentClientInstance.GetClientBlueprint().IsClientBuyingThisItem(ordinary) &&
                    view == retainedList && view.Get("query")?.input?.text == token && QaItemUi.Hit(view.root, ordinary) != null &&
                    !CardTexts(ordinary).Contains(ModHelper.GetLocalized(Mod, "f11.pinned")),
                    "F11 did not refresh its unpinned status after buyer acceptance changed");
                listPosition = view.Rect.anchoredPosition; listSize = view.Rect.sizeDelta;
                var scroll = view.root.GetComponentsInChildren<ScrollRect>(true).FirstOrDefault(s => s.gameObject.activeInHierarchy);
                Require(scroll != null, "F11 lacks a scrollable list");
                scroll.verticalNormalizedPosition = .4f; listScroll = scroll.verticalNormalizedPosition;
                QaItemUi.Click(view, nested);
                Advance(6); return false;
            case 6:
                if (!bag.contentWindow.IsVisible() || !Outlined(nested)) return false;
                view = List();
                Require(view == retainedList && view.Get("return")?.button != null &&
                    view.Rect.sizeDelta.x <= 240 && view.Rect.sizeDelta.y <= 94,
                    "F11 did not fold its existing window after locating");
                Canvas targetCanvas = nested.handler.GetComponentInParent<Canvas>();
                Camera targetCamera = targetCanvas == null || targetCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : targetCanvas.worldCamera;
                Vector2 targetPoint = RectTransformUtility.WorldToScreenPoint(targetCamera, nested.handler.rectTransform.position);
                Canvas listCanvas = view.root.GetComponentInParent<Canvas>();
                Camera listCamera = listCanvas == null || listCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : listCanvas.worldCamera;
                Require(!RectTransformUtility.RectangleContainsScreenPoint(view.Rect, targetPoint, listCamera),
                    "The folded F11 window still covers the located item");
                view.Get("return").button.onClick.Invoke();
                scroll = view.root.GetComponentsInChildren<ScrollRect>(true).FirstOrDefault(s => s.gameObject.activeInHierarchy);
                Require(List() == retainedList && view.Get("query")?.input?.text == token &&
                    view.Rect.anchoredPosition == listPosition && view.Rect.sizeDelta == listSize &&
                    scroll != null && Mathf.Abs(scroll.verticalNormalizedPosition - listScroll) < .02f,
                    "Returning from locate lost the window position, size or scroll");
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
                result = "PASS F03 native buyer acceptance, F11 independent search and native nested locate, F04 observed risk and invalidation";
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
        TradeScenario.SetOptions(false, "f11.purchase", "f34.enabled");
        var root = EmporiumEntry.Instance.invElement;
        ordinary = NewItem("processed_meat", "QA Ordinary " + token);
        hiddenRejection = NewItem("processed_juice", "QA Hidden " + token);
        customTooltip = NewItem("processed_milk", "QA Tooltip " + token);
        customTooltip.tooltipRichTextFunc = (item, inv, slot, shape, state) =>
            new RichTextBuilder(40).AddLine("[" + TypeHelper.GetTypeDisplayName("FOOD") + "]");
        bag = NewItem("backpack_medium", "QA Bag " + token);
        nested = NewItem("cup_noodle", "QA Nested " + token);
        GeneralHelper.TrackBuyPrice(ordinary, 14);
        GeneralHelper.TrackBuyPrice(nested, 0);
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
        Require(client.IsClientBuyingThisItem(ordinary) && client.IsClientBuyingThisItem(customTooltip) &&
            client.IsClientBuyingThisItem(nested) && !client.IsClientBuyingThisItem(hiddenRejection),
            "Customer fixture does not distinguish native acceptance from rejection");
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
        RoutingScenario.RunFeatureAction("f11.open");
        var list = ListOrNull();
        Require(list?.IsAlive == true, "F11 list did not open");
    }

    private static CustomUIWindow ListOrNull() => CustomUIManager.Instance.GetWindow(ListWindow);
    private static CustomUIWindow List() => ListOrNull() ?? throw new InvalidOperationException("F11 list is closed");
    private static string[] CardTexts(GameItem item) => QaItemUi.Hit(List().root, item).transform.parent.parent
        .GetComponentsInChildren<TMP_Text>(true).Where(t => t.isActiveAndEnabled).Select(t => t.text).ToArray();
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
        var outline = item?.rotator?.transform.Find("ShopAssist_Outline") ?? item?.rotator?.transform.Find("ShopAssist_LocateOutline");
        return outline != null && outline.gameObject.activeInHierarchy && outline.GetComponentsInChildren<Image>(true)
            .Any(image => image.isActiveAndEnabled && image.color.a > 0 && image.rectTransform.rect.width > 0 && image.rectTransform.rect.height > 0);
    }
    private void Advance(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
