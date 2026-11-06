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
    private static PixelWindow watchedStock;
    private static int stockShows;

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
    private static void CountStockShow(PixelWindow __instance) { if (ReferenceEquals(__instance, watchedStock)) stockShows++; }
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

    private static void InvokeWithoutOpeningStock(Button locate, PixelWindow stock)
    {
        watchedStock = stock; stockShows = 0;
        const string patchId = "qa.attention-stock-show";
        var harmony = new Harmony(patchId);
        try
        {
            var prefix = new HarmonyMethod(typeof(AttentionLocationScenario).GetMethod(nameof(CountStockShow), F));
            foreach (MethodInfo method in typeof(PixelWindow).GetMethods(F).Where(method => method.Name == "Show" && !method.IsStatic))
                harmony.Patch(method, prefix: prefix);
            locate.onClick.Invoke();
        }
        finally { harmony.UnpatchAll(patchId); watchedStock = null; }
        Need(stockShows == 0 && ReferenceEquals(stock, EmporiumEntry.Instance.invWindow) && stock.IsVisible() &&
            !stock.canMove && StoreUIManager.Instance.IsInnateWindow(stock), "Locate reopened or replaced the fixed stock window");
    }

    private void LocateThroughUi()
    {
        Button locate = Locate(); Need(locate != null && locate.interactable, "Locate action is unavailable");
        stock.ToFront(); long previousFocus = bag.contentWindow.focusStamp;
        InvokeWithoutOpeningStock(locate, stock);
        Need(bag.contentWindow.IsVisible() && bag.contentWindow.focusStamp > previousFocus &&
            bag.contentWindow.focusStamp > stock.focusStamp, "Locate did not bring the existing item container forward");
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
                OpenAttention();
                Need(Text(Attention()).Contains(label) && Locate() == null, "A closed container was presented as directly locatable");
                ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(220, 200));
                Need(bag.contentWindow.IsVisible(), "Native container did not open");
                WaitForRefresh(); step = 2; return false;
            case 2:
                if (!RefreshWaitComplete()) return false;
                bool alive = Attention()?.IsAlive == true;
                bool hasText = alive && Text(Attention()).Contains(label);
                bool hasLocate = alive && Locate() != null;
                string raw = ModHelper.GetModData(Mod, "attention", null);
                string diagnostics = "windowAlive=" + alive + ", text=" + hasText + ", locate=" + hasLocate +
                    ", rawMatches=" + (raw == saved) + ", raw=" + raw;
                Need(alive, "Attention window closed after opening the container: " + diagnostics);
                Need(hasText, "Attention item disappeared after opening the container: " + diagnostics);
                Need(hasLocate, "Opening only the container did not refresh Locate: " + diagnostics);
                Need(raw == saved, "Opening the container changed the saved attention data: " + diagnostics);
                LocateThroughUi(); bag.contentWindow.Hide();
                WaitForRefresh(); step = 3; return false;
            case 3:
                if (!RefreshWaitComplete()) return false;
                Need(Attention()?.IsAlive == true && Text(Attention()).Contains(label) && Locate() == null &&
                    ModHelper.GetModData(Mod, "attention", null) == saved && ReferenceEquals(stock, EmporiumEntry.Instance.invWindow) && stock.IsVisible(),
                    "Closing only the container did not remove Locate while preserving the issue and stock window");
                Attention().Close();
                saved = Issue(run, slot, stockItemId, stockLabel);
                ModHelper.SetModData(Mod, "attention", saved);
                PlayerStore.Instance.SaveGame(); StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                reload = new QaLoadSlot(slot, Guid.Parse(run)); step = 4; return false;
            case 4:
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
                result = "PASS attention Locate tracks native container visibility, focuses the existing window and leaves fixed stock untouched";
                return true;
            default: throw new InvalidOperationException("Unknown attention location step");
        }
    }
}
