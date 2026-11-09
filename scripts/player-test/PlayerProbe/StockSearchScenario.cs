using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Exercises the stock-window entry in a fresh, guarded Player with no customer.
public class StockSearchScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100f;
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 8);
    private GameItem firstBag, secondBag, lockedBag, first, second, counter, hidden, locked, unowned;
    private GameItem[] fixtures;
    private int[] originalIds;
    private int uniqueBefore, step, frame;
    private float waitUntil;
    private string query, firstSource, secondSource, screenshot;

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CustomUIWindow Search() => CustomUIManager.Instance.GetWindow(Mod + ".customer_inventory");
    private static object Field(object value, string name) => value?.GetType().GetField(name, Fields)?.GetValue(value);
    private static GameItem New(string id, string name = null)
    {
        GameItem item = DirectoryMaster.Item(id);
        Need(item != null && item.identifier == id, "Missing native stock-search fixture " + id);
        GeneralHelper.SetItemOwned(item, true);
        if (name != null) GeneralHelper.SetCustomName(item, name);
        return item;
    }
    private static void Put(GameInventory inventory, GameItem item)
    {
        SlotMarker marker = inventory.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == inventory && marker.targetItem == null && marker.numTransfer >= item.unitCount &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory,
            "Could not place stock-search fixture " + item.identifier);
    }
    private static GameGridInventory Contents(GameItem bag)
    {
        var grid = bag?.contentWindow?.childElement as GameGridInventory;
        Need(grid != null && bag.contentWindow.openCallback == null && !bag.IsTag("TRACK_OPENED_TAG"),
            "Stock-search fixture is not a safe ordinary container");
        return grid;
    }
    private static bool Outlined(GameItem item)
    {
        var outline = (item as GameItemElement)?.rotator?.transform.Find("ShopAssist_LocateOutline");
        return outline != null && outline.gameObject.activeInHierarchy && outline.GetComponentsInChildren<Image>(true)
            .Any(image => image.isActiveAndEnabled && image.color.a > 0 && image.rectTransform.rect.width > 0 && image.rectTransform.rect.height > 0);
    }
    private static string SourceLabel(CustomUIWindow window, GameItem item)
    {
        var hit = QaItemUi.Hit(window.root, item);
        var entry = Field(hit, "entry");
        string source = Field(entry, "SourceLabel") as string;
        Need(hit != null && !string.IsNullOrEmpty(source) && hit.transform.parent?.parent != null &&
            hit.transform.parent.parent.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == source),
            "Stock-search card has no visible source label");
        return source;
    }
    private static void CheckVisibleNumber(CustomUIWindow window, GameItem item, string source)
    {
        var hit = QaItemUi.Hit(window.root, item);
        var text = hit.transform.parent.parent.GetComponentsInChildren<TMP_Text>(true).First(t => t.text == source);
        text.ForceMeshUpdate();
        string visible = new string(text.textInfo.characterInfo.Take(text.textInfo.characterCount)
            .Where(character => character.isVisible).Select(character => character.character).ToArray());
        string number = source.Split(' ')[0];
        Need(number.StartsWith("#") && visible.StartsWith(number, StringComparison.Ordinal),
            "The container identifier was clipped from its search card: " + source + " / " + visible);
    }
    private static void SetQuery(CustomUIWindow window, string value)
    {
        var input = window?.Get("query");
        Need(input?.input != null, "Stock-search input is unavailable");
        input.SetString(value, true); input.input.onEndEdit.Invoke(value);
    }
    private static void ReturnToList()
    {
        var button = Search()?.Get("return")?.button;
        Need(button != null && button.interactable, "Locate did not offer a return to stock search");
        button.onClick.Invoke();
        Need(Search()?.Get("query")?.input != null, "Returning from locate did not restore the search");
    }
    private void CheckUnchanged()
    {
        Need(ReferenceEquals(first.parentInventory, firstBag.contentWindow.childElement) &&
            ReferenceEquals(second.parentInventory, secondBag.contentWindow.childElement) &&
            ReferenceEquals(counter.parentInventory, EmporiumEntry.Instance.backInvinvElement) &&
            ReferenceEquals(hidden.parentInventory, firstBag.contentWindow.childElement) &&
            ReferenceEquals(unowned.parentInventory, secondBag.contentWindow.childElement) &&
            ReferenceEquals(locked.parentInventory, lockedBag.contentWindow.childElement),
            "Browsing or locating moved a stock-search fixture");
    }

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Stock search timed out at step " + step);
        if (step == 0) { if (start.Tick()) step = 1; return false; }
        var entry = EmporiumEntry.Instance; var player = PlayerStore.Instance;
        switch (step)
        {
            case 1:
            {
                if (entry?.storeInteractables?.activeInHierarchy != true || entry.invWindow?.IsVisible() != true ||
                    entry.backInvinvElement == null || ItemMouseDoubleClickHandler.current == null ||
                    StoreUIManager.Instance?.gameOptionUIManager == null) return false;
                if (StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
                if (NegociationUIManager.Instance?.IsOpen == true) NegociationUIManager.Instance.CloseUI();
                CustomUIManager.Instance.GetWindow(Mod + ".todo")?.Close();
                player.currentClientInstance = null; player.isClientArrived = false;
                TradeScenario.SetOptions(true, "f11.enabled");
                TradeScenario.SetOptions(false, "f03.enabled", "f04.enabled", "f11.purchase", "f34.enabled");
                string sameBagName = "QA same container " + token;
                firstBag = New("backpack_medium", sameBagName); secondBag = New("backpack_medium", sameBagName);
                lockedBag = New("fanny_pack");
                Put(entry.invElement, firstBag); Put(entry.invElement, secondBag); Put(entry.invElement, lockedBag);
                query = "QA stock find " + token;
                first = New("wine_yeast", query + " A"); second = New("wine_yeast", query + " B");
                counter = New("wine_yeast", query + " counter"); hidden = New("wine_yeast", query + " hidden");
                locked = New("wine_yeast", query + " locked"); unowned = New("wine_yeast", query + " unowned");
                Put(Contents(firstBag), first); Put(Contents(firstBag), hidden);
                Put(Contents(secondBag), second); Put(Contents(secondBag), unowned);
                Put(Contents(lockedBag), locked); Put(entry.backInvinvElement, counter);
                hidden.EnableTag("ITEM_HIDDEN_TAG"); GeneralHelper.SetItemOwned(unowned, false);
                lockedBag.contentWindow.Hide(); lockedBag.contentWindow.isLocked = true;
                firstBag.contentWindow.Hide(); secondBag.contentWindow.Hide();
                // Native fixture creation assigns IDs immediately. Restore two fixtures to
                // the unassigned state so browsing must prove it does not allocate one.
                firstBag.uniqueId = 0; first.uniqueId = 0;
                Need(!firstBag.contentWindow.IsVisible() && !secondBag.contentWindow.IsVisible() &&
                    !lockedBag.contentWindow.IsVisible() && hidden.IsTag("ITEM_HIDDEN_TAG") &&
                    !GeneralHelper.IsItemEffectivelyOwned(unowned), "Search exclusion fixtures are not isolated");
                fixtures = new[] { firstBag, secondBag, lockedBag, first, second, counter, hidden, locked, unowned };
                CheckUnchanged(); step = 2; return false;
            }
            case 2:
            {
                var action = QaItemUi.WindowAction(entry.invWindow, "f11.open");
                if (action == null || action.isDisabled) return false;
                uniqueBefore = player.currentUniqueId; originalIds = fixtures.Select(item => item.uniqueId).ToArray();
                Need((firstBag.uniqueId <= 0 || secondBag.uniqueId <= 0) && (first.uniqueId <= 0 || second.uniqueId <= 0),
                    "Stock-search fixture already has persistent IDs before browsing: " + string.Join(",", originalIds));
                action.Select();
                var window = Search();
                Need(window?.IsAlive == true &&
                    player.currentClientInstance == null && !player.isClientArrived, "Direct warehouse search did not open without a customer");
                SetQuery(window, query.ToUpperInvariant());
                waitUntil = Time.unscaledTime + .7f; step = 3; return false;
            }
            case 3:
            {
                if (Time.unscaledTime < waitUntil) return false;
                var window = Search(); Need(window?.IsAlive == true, "Stock search closed while filtering");
                Need(QaItemUi.Hit(window.root, first) != null && QaItemUi.Hit(window.root, second) != null &&
                    QaItemUi.Hit(window.root, counter) != null, "Name search omitted a closed owned container or the native back counter");
                Need(QaItemUi.Hit(window.root, hidden) == null && QaItemUi.Hit(window.root, locked) == null &&
                    QaItemUi.Hit(window.root, unowned) == null, "Name search exposed hidden, locked or unowned goods");
                firstSource = SourceLabel(window, first); secondSource = SourceLabel(window, second);
                Need(firstSource != secondSource && firstSource.Contains("#") && secondSource.Contains("#") &&
                    firstSource.Contains("QA same container " + token) && secondSource.Contains("QA same container " + token),
                    "Equal-named containers were not distinguishable in search results");
                CheckVisibleNumber(window, first, firstSource); CheckVisibleNumber(window, second, secondSource);
                Need(SourceLabel(window, counter) == ModHelper.GetLocalized(Mod, "inv.counter"), "Back-counter item has the wrong source");
                Need(player.currentUniqueId == uniqueBefore && fixtures.Select(item => item.uniqueId).SequenceEqual(originalIds) &&
                    !firstBag.contentWindow.IsVisible() && !secondBag.contentWindow.IsVisible() && !lockedBag.contentWindow.IsVisible(),
                    "Browsing assigned persistent IDs or opened an inventory");
                CheckUnchanged(); QaItemUi.Click(window, first); frame = Time.frameCount; step = 4; return false;
            }
            case 4:
                if (Time.frameCount <= frame || !Outlined(first)) return false;
                Need(firstBag.contentWindow.IsVisible() && !secondBag.contentWindow.IsVisible() && !lockedBag.contentWindow.IsVisible(),
                    "Locating the first result opened the wrong container");
                CheckUnchanged(); ReturnToList(); firstBag.contentWindow.Hide(); step = 5; return false;
            case 5:
            {
                var window = Search();
                Need(SourceLabel(window, first) == firstSource && SourceLabel(window, second) == secondSource,
                    "Returning from locate changed the identities of the equal-named containers");
                QaItemUi.Click(window, second); frame = Time.frameCount; step = 6; return false;
            }
            case 6:
                if (Time.frameCount <= frame || !Outlined(second)) return false;
                Need(secondBag.contentWindow.IsVisible() && !firstBag.contentWindow.IsVisible() && !lockedBag.contentWindow.IsVisible(),
                    "Locating the second result opened the wrong container");
                CheckUnchanged(); ReturnToList(); secondBag.contentWindow.Hide();
                entry.backInvWindow.Show(); frame = Time.frameCount; step = 7; return false;
            case 7:
                if (Time.frameCount <= frame || !entry.backInvWindow.IsVisible()) return false;
                QaItemUi.Click(Search(), counter); frame = Time.frameCount; step = 8; return false;
            case 8:
                if (Time.frameCount <= frame || !Outlined(counter)) return false;
                CheckUnchanged(); ReturnToList();
                foreach (GameItem item in fixtures) GeneralHelper.SetCustomName(item, null);
                string nativeName = first.GetDisplayName(true);
                Need(!string.IsNullOrWhiteSpace(nativeName), "Native localized item name is unavailable for the gallery");
                SetQuery(Search(), nativeName); waitUntil = Time.unscaledTime + .7f; step = 9; return false;
            case 9:
                if (Time.unscaledTime < waitUntil) return false;
                CheckUnchanged();
                Need(QaItemUi.Hit(Search().root, first) != null && QaItemUi.Hit(Search().root, second) != null &&
                    QaItemUi.Hit(Search().root, counter) != null, "Localized gallery search omitted the verified fixtures");
                Need(!Search().root.GetComponentsInChildren<TMP_Text>(true).Any(text => text.gameObject.activeInHierarchy &&
                    (text.text ?? "").Contains("QA ")), "Gallery still contains QA fixture names");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    screenshot = Path.Combine(Application.persistentDataPath,
                        GetType() == typeof(StockSearchEnglishScenario) ? "gallery-f11-en.png" : "gallery-f11.png");
                    if (File.Exists(screenshot)) File.Delete(screenshot);
                    Canvas.ForceUpdateCanvases(); ScreenCapture.CaptureScreenshot(screenshot);
                    frame = Time.frameCount + 5;
                }
                else frame = Time.frameCount;
                step = 10; return false;
            case 10:
                if (Time.frameCount < frame || screenshot != null && (!File.Exists(screenshot) || new FileInfo(screenshot).Length == 0)) return false;
                result = "PASS F11 direct stock search without customer, closed same-name containers, back counter, exclusions, ID-free browsing and native locate";
                return true;
            default: throw new InvalidOperationException("Unknown stock-search step");
        }
    }
}

public sealed class StockSearchEnglishScenario : StockSearchScenario { }
