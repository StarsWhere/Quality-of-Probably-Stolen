using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Seeds one saved issue, then exercises the visible attention and native window controls.
public sealed class AttentionLocationScenario
{
    [Serializable] private sealed class SavedIssue { public string key, feature, title, location, reason; public int item; }
    [Serializable] private sealed class SavedIssues { public int version = 1; public string run; public int slot; public SavedIssue[] issues; }
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private QaLoadSlot reload;
    private GameItem bag, item;
    private PixelWindow stock;
    private int bagId, itemId, stockItemId, slot, step;
    private string run, label, stockLabel, saved;
    private float checkAt;
    private int eligibleFrame;
    private GameItem markTarget;
    private GameInventory markInventory;
    private int markFrame;
    private float markDeadline;
    private string initialMarkerState;
    private static PixelWindow watchedStock, watchedContainer;
    private static int stockShows, containerShows;
    private static string locateNotice;

    private static void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
    private static CustomUIWindow Attention() => CustomUIManager.Instance.GetWindow(Mod + ".attention");
    private static string Text(CustomUIWindow window) => string.Join(" | ", window.root.GetComponentsInChildren<TMP_Text>(true).Select(value => value.text));
    private static Button Locate() => Attention()?.IsAlive == true ? RoutingScenario.Button(Attention(), "attention.locate") : null;
    private void WaitForRefresh()
    {
        checkAt = Time.unscaledTime + .8f;
        eligibleFrame = -1;
    }
    private bool RefreshWaitComplete()
    {
        if (Time.unscaledTime < checkAt) return false;
        // The probe runs in Update; give the mod's LateUpdate one frame after the deadline,
        // even when a slow frame makes the deadline pass between two Updates.
        if (eligibleFrame < 0) { eligibleFrame = Time.frameCount; return false; }
        return Time.frameCount > eligibleFrame;
    }
    private static void CountWindowShow(PixelWindow __instance)
    {
        if (ReferenceEquals(__instance, watchedStock)) stockShows++;
        if (ReferenceEquals(__instance, watchedContainer)) containerShows++;
    }
    private static void RecordNotice(string message) { locateNotice = message; }
    private static GameItem Put(string identifier, string name, GameInventory inventory) => PackingRecoveryFixture.Put(identifier, name, inventory);

    private static void OpenAttention()
    {
        var options = StoreUIManager.Instance.gameOptionUIManager;
        options.OpenUI();
        CustomUIManager.Instance.GetWindow("game_options").Get(Mod + ":tools").button.onClick.Invoke();
        CustomUIManager.Instance.GetWindow(Mod + ".tools").Get("attention").button.onClick.Invoke();
        Need(Attention()?.IsAlive == true, "Attention control did not open its window");
        options.CloseUI();
    }

    private static void InvokeWithoutOpeningStock(Button locate, PixelWindow stock, PixelWindow container = null, bool keepContainerOpen = false)
    {
        watchedStock = stock; watchedContainer = container; stockShows = containerShows = 0; locateNotice = null;
        const string patchId = "qa.attention-stock-show";
        var harmony = new Harmony(patchId);
        try
        {
            var prefix = new HarmonyMethod(typeof(AttentionLocationScenario).GetMethod(nameof(CountWindowShow), F));
            foreach (MethodInfo method in typeof(PixelWindow).GetMethods(F).Where(method => method.Name == "Show" && !method.IsStatic))
                harmony.Patch(method, prefix: prefix);
            Type noticeType = AccessTools.TypeByName("ShopAssist.ShopNotice");
            MethodInfo notice = noticeType?.GetMethod("Show", F);
            if (notice != null)
                harmony.Patch(notice, prefix: new HarmonyMethod(typeof(AttentionLocationScenario).GetMethod(nameof(RecordNotice), F)));
            locate.onClick.Invoke();
        }
        finally { harmony.UnpatchAll(patchId); watchedStock = watchedContainer = null; }
        Need(stockShows == 0 && ReferenceEquals(stock, EmporiumEntry.Instance.invWindow) && stock.IsVisible() &&
            !stock.canMove && StoreUIManager.Instance.IsInnateWindow(stock), "Locate reopened or replaced the fixed stock window");
        if (keepContainerOpen) Need(containerShows == 0, "Locate reopened an existing or locked container window");
    }

    private static bool Marked(GameItem target)
    {
        var outline = (target as GameItemElement)?.rotator?.transform.Find("ShopAssist_LocateOutline");
        return outline != null && outline.gameObject.activeInHierarchy && outline.GetComponentsInChildren<Image>(true)
            .Any(image => image.isActiveAndEnabled && image.color.a > 0 && image.rectTransform.rect.width > 0 && image.rectTransform.rect.height > 0);
    }

    private static string MarkerState(GameItem target)
    {
        var element = target as GameItemElement;
        var outline = element?.rotator?.transform.Find("ShopAssist_LocateOutline");
        var images = outline == null ? "none" : string.Join(",", outline.GetComponentsInChildren<Image>(true).Select(image =>
            image.name + "[active=" + image.gameObject.activeInHierarchy + ",enabled=" + image.enabled +
            ",alpha=" + image.color.a + ",rect=" + image.rectTransform.rect.size + ",sizeDelta=" + image.rectTransform.sizeDelta + "]"));
        Type locator = AccessTools.TypeByName("ShopAssist.ItemLocator");
        var marked = locator?.GetField("marked", F)?.GetValue(null) as GameItemElement;
        bool sameItem = target?.parentInventory?.childItems.Any(value => ReferenceEquals(value, target)) == true;
        var window = element?.handler?.FindParentWindow();
        return "frame=" + Time.frameCount + ", item=" + target?.uniqueId + ", sameInventoryItem=" + sameItem +
            ", sameMarkedItem=" + ReferenceEquals(marked, target) + ", markedId=" + marked?.uniqueId +
            ", handlerActive=" + (element?.handler?.gameObject.activeInHierarchy == true) +
            ", windowVisible=" + (window?.IsVisible() == true) + ", backgroundEnabled=" +
            (element?.background?.image == null ? "missing" : element.background.image.enabled.ToString()) +
            ", rotatorActive=" + (element?.rotator?.gameObject.activeInHierarchy == true) +
            ", outline=" + (outline == null ? "missing" : "self=" + outline.gameObject.activeSelf + "/hierarchy=" + outline.gameObject.activeInHierarchy) +
            ", images=" + images + ", notice=" + (locateNotice ?? "none");
    }

    private void BeginMarkerCheck(GameItem target)
    {
        Need(target != null && target.parentInventory != null, "The located item is unavailable");
        markTarget = target; markInventory = target.parentInventory;
        markFrame = Time.frameCount; markDeadline = Time.unscaledTime + 2f;
        initialMarkerState = MarkerState(target);
    }

    private bool AwaitMarker()
    {
        if (Time.frameCount <= markFrame) return false;
        Canvas.ForceUpdateCanvases();
        Need(markTarget != null && ReferenceEquals(markTarget.parentInventory, markInventory) && markInventory.childItems.Any(value => ReferenceEquals(value, markTarget)),
            "The target object or inventory changed while checking the locate marker: " + MarkerState(markTarget));
        if (Marked(markTarget)) return true;
        Need(Time.unscaledTime < markDeadline,
            "Locate did not visibly mark the target; immediately: " + initialMarkerState + "; after layout: " + MarkerState(markTarget));
        return false;
    }

    private void LocateThroughUi(bool alreadyOpen)
    {
        Button locate = Locate(); Need(locate != null && locate.interactable, "Locate action is unavailable");
        Need(bag.contentWindow.IsVisible() == alreadyOpen, "Container visibility did not match the locate fixture");
        stock.ToFront(); long previousFocus = bag.contentWindow.focusStamp;
        InvokeWithoutOpeningStock(locate, stock, bag.contentWindow, alreadyOpen);
        Need(bag.contentWindow.IsVisible() && bag.contentWindow.focusStamp > previousFocus &&
            bag.contentWindow.focusStamp > stock.focusStamp, "Locate did not bring the existing item container forward: " + MarkerState(item));
        Need(Attention()?.IsAlive == true && Text(Attention()).Contains(label) && ModHelper.GetModData(Mod, "attention", null) == saved,
            "Locating changed or dismissed the saved attention issue");
        BeginMarkerCheck(item);
    }

    private static string Issue(string run, int slot, int id, string title) => JsonUtility.ToJson(new SavedIssues
    {
        run = run, slot = slot, issues = new[]
        {
            new SavedIssue { key = "qa.attention:" + id, feature = "f25.open", title = title, location = "", reason = "inv.inspect", item = id }
        }
    });

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Attention location timed out at step " + step);
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                bag = Put("backpack_medium", "QA attention container", EmporiumEntry.Instance.invElement);
                var inside = bag.contentWindow.childElement as GameGridInventory;
                Need(inside != null, "Fixture container has no native inventory");
                label = "QA attention target " + Guid.NewGuid().ToString("N").Substring(0, 8);
                item = Put("wine_yeast", label, inside);
                bagId = bag.GetUniqueID(); itemId = item.GetUniqueID();
                stockLabel = "QA attention stock " + Guid.NewGuid().ToString("N").Substring(0, 8);
                stockItemId = Put("processed_meat", stockLabel, EmporiumEntry.Instance.invElement).GetUniqueID();
                Need(bagId > 0 && itemId > 0 && stockItemId > 0, "Fixture IDs were not assigned");
                run = PlayerStore.Instance.runID; slot = PlayerStore.Instance.saveSlotId;
                saved = Issue(run, slot, itemId, label);
                ModHelper.SetModData(Mod, "attention", saved);
                PlayerStore.Instance.SaveGame(); StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                reload = new QaLoadSlot(slot, Guid.Parse(run)); step = 1; return false;
            case 1:
                if (!reload.Tick()) return false;
                stock = EmporiumEntry.Instance.invWindow;
                if (stock?.IsVisible() != true || EmporiumEntry.Instance.storeInteractables?.activeInHierarchy != true ||
                    StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                bag = EmporiumEntry.Instance.invElement.childItems.Find(value => value.uniqueId == bagId);
                item = (bag?.contentWindow?.childElement as GameGridInventory)?.childItems.Find(value => value.uniqueId == itemId);
                Need(bag != null && item != null && stock != null && stock.IsVisible() && !stock.canMove &&
                    StoreUIManager.Instance.IsInnateWindow(stock), "Reloaded native stock or item fixture is unavailable");
                Need(!bag.contentWindow.IsVisible() && ModHelper.GetModData(Mod, "attention", null) == saved,
                    "Fixture unexpectedly changed on reload");
                Need(bag.identifier == "backpack_medium" && bag.contentWindow.openCallback == null &&
                    !bag.contentWindow.isLocked && !bag.IsTag("LOCKED_TAG") && !bag.IsTag("TRACK_OPENED_TAG"),
                    "Fixture is not a closed, ordinary safe container");
                OpenAttention();
                Need(Text(Attention()).Contains(label) && Locate() != null, "A safe closed container has no Locate action");
                LocateThroughUi(false);
                step = 11; return false;
            case 11:
                if (!AwaitMarker()) return false;
                WaitForRefresh(); step = 2; return false;
            case 2:
                if (!RefreshWaitComplete()) return false;
                bool alive = Attention()?.IsAlive == true;
                bool hasText = alive && Text(Attention()).Contains(label);
                bool hasLocate = alive && Locate() != null;
                string raw = ModHelper.GetModData(Mod, "attention", null);
                string diagnostics = "windowAlive=" + alive + ", text=" + hasText + ", locate=" + hasLocate +
                    ", rawMatches=" + (raw == saved) + ", raw=" + raw;
                Need(alive, "Attention window closed after locating: " + diagnostics);
                Need(hasText, "Attention item disappeared after locating: " + diagnostics);
                Need(hasLocate, "The open container no longer has Locate: " + diagnostics);
                Need(raw == saved, "Locating changed the saved attention data: " + diagnostics);
                LocateThroughUi(true);
                step = 21; return false;
            case 21:
                if (!AwaitMarker()) return false;
                Button stale = Locate();
                bag.contentWindow.Hide(); bag.contentWindow.isLocked = true;
                InvokeWithoutOpeningStock(stale, stock, bag.contentWindow, true);
                Need(!bag.contentWindow.IsVisible() && ModHelper.GetModData(Mod, "attention", null) == saved,
                    "A stale Locate action opened a newly locked container or changed its issue");
                WaitForRefresh(); step = 3; return false;
            case 3:
                if (!RefreshWaitComplete()) return false;
                Need(Attention()?.IsAlive == true && Text(Attention()).Contains(label) && Locate() == null &&
                    !bag.contentWindow.IsVisible() && ModHelper.GetModData(Mod, "attention", null) == saved &&
                    ReferenceEquals(stock, EmporiumEntry.Instance.invWindow) && stock.IsVisible(),
                    "Locking the container did not remove Locate while preserving the issue and stock window");
                bag.contentWindow.isLocked = false;
                WaitForRefresh(); step = 4; return false;
            case 4:
                if (!RefreshWaitComplete()) return false;
                Need(Attention()?.IsAlive == true && Text(Attention()).Contains(label) && Locate() != null &&
                    !bag.contentWindow.IsVisible() && ModHelper.GetModData(Mod, "attention", null) == saved,
                    "Unlocking the closed safe container did not restore Locate and preserve the issue");
                LocateThroughUi(false);
                step = 41; return false;
            case 41:
                if (!AwaitMarker()) return false;
                Attention().Close();
                saved = Issue(run, slot, stockItemId, stockLabel);
                ModHelper.SetModData(Mod, "attention", saved);
                PlayerStore.Instance.SaveGame(); StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                reload = new QaLoadSlot(slot, Guid.Parse(run)); step = 5; return false;
            case 5:
                if (!reload.Tick()) return false;
                stock = EmporiumEntry.Instance.invWindow;
                if (stock?.IsVisible() != true || EmporiumEntry.Instance.storeInteractables?.activeInHierarchy != true ||
                    StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                bag = EmporiumEntry.Instance.invElement.childItems.Find(value => value.uniqueId == bagId);
                Need(bag != null && EmporiumEntry.Instance.invElement.childItems.Any(value => value.uniqueId == stockItemId),
                    "Reloaded stock fixture is unavailable");
                OpenAttention();
                Need(Text(Attention()).Contains(stockLabel) && Locate() != null, "Visible stock item has no Locate action");
                ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(220, 200));
                bag.contentWindow.ToFront();
                long stockBefore = stock.focusStamp;
                InvokeWithoutOpeningStock(Locate(), stock);
                Need(stock.focusStamp > stockBefore && stock.focusStamp > bag.contentWindow.focusStamp,
                    "Locate did not bring the existing fixed stock window forward");
                BeginMarkerCheck(EmporiumEntry.Instance.invElement.childItems.Find(value => value.uniqueId == stockItemId));
                step = 51; return false;
            case 51:
                if (!AwaitMarker()) return false;
                Need(ModHelper.GetModData(Mod, "attention", null) == saved, "Stock locate changed the saved issue");
                result = "PASS attention Locate safely opens closed containers, reuses open windows, rejects locked paths and preserves saved issues";
                return true;
            default: throw new InvalidOperationException("Unknown attention location step");
        }
    }
}
