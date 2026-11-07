using System;
using System.Linq;
using UnityEngine;

// Creates and runs a real category rule, capturing the editor, preview and moved goods.
public sealed class FeatureGalleryRoutingScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private int phase;
    private GameItemElement bag, first, second;
    private GameGridInventory target;

    public bool Tick(out string result)
    {
        result = null;
        QaFeatureGallery.Need(Time.realtimeSinceStartup < deadline, "Routing gallery timed out at " + phase);
        if (phase == 0) { if (!gallery.ChineseMainMenu()) return false; phase = 1; }
        if (phase == 1) { if (!start.Tick()) return false; phase = 2; }
        if (phase == 2)
        {
            if (!gallery.Screen1920() || StoreUIManager.Instance?.gameOptionUIManager == null || ItemMultiSelectHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f07.enabled", "f08.enabled", "f09.enabled");
            TradeScenario.SetOptions(false, "f15.enabled");
            var options = StoreUIManager.Instance.gameOptionUIManager;
            options.OpenUI(); var settings = CustomUIManager.Instance.GetWindow("game_options");
            settings.Get(Mod + ":f09.mode").SetIndex(0, true); settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
            var stock = EmporiumEntry.Instance.invElement;
            bag = QaFeatureGallery.Item("backpack_medium", stock, "食品收纳包");
            first = QaFeatureGallery.Item("processed_meat", stock, "加工肉");
            second = QaFeatureGallery.Item("processed_juice", stock, "果汁");
            target = bag.contentWindow?.childElement as GameGridInventory;
            QaFeatureGallery.Need(target != null, "Routing target has no native inventory");
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width * .74f, Screen.height * .65f));
            QaFeatureGallery.Action("f07.open");
            var rules = QaFeatureGallery.Window("routes");
            QaFeatureGallery.Need(rules?.IsAlive == true, "Rule editor did not open");
            QaItemUi.Choose(rules, "area", bag.GetDisplayName(true)); rules = QaFeatureGallery.Window("routes");
            rules.Get("receive").SetBool(true, true);
            var category = rules.Get("category").dropdown;
            int food = category.options.FindIndex(option => option.text == TypeHelper.GetTypeDisplayName("FOOD"));
            QaFeatureGallery.Need(food >= 0, "Native food category unavailable");
            category.SetValueWithoutNotify(food); category.onValueChanged.Invoke(food);
            rules.Get("add_category").button.onClick.Invoke();
            rules = QaFeatureGallery.Window("routes");
            QaItemUi.Choose(rules, "sample", first.GetDisplayName(true));
            gallery.Capture("f07"); phase = 3; return false;
        }
        if (phase == 3)
        {
            if (!gallery.CaptureReady()) return false;
            var rules = QaFeatureGallery.Window("routes");
            rules.Get("save").button.onClick.Invoke(); QaFeatureGallery.Window("routes").Close();
            RoutingScenario.SetSelection(first, second);
            QaFeatureGallery.Action("f08.open");
            var preview = QaFeatureGallery.Window("route_preview");
            QaFeatureGallery.Need(preview?.IsAlive == true && RoutingScenario.Button(preview, "f08.apply")?.interactable == true,
                "Native route preview unavailable");
            gallery.Capture("f09"); phase = 4; return false;
        }
        if (phase == 4)
        {
            if (!gallery.CaptureReady()) return false;
            gallery.Capture("f08-before"); phase = 5; return false;
        }
        if (phase == 5)
        {
            if (!gallery.CaptureReady()) return false;
            RoutingScenario.Button(QaFeatureGallery.Window("route_preview"), "f08.apply").onClick.Invoke();
            QaFeatureGallery.Need(first.parentInventory == target && second.parentInventory == target,
                "Rule execution did not move both selected native items");
            if (!bag.contentWindow.IsVisible()) ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width * .74f, Screen.height * .65f));
            gallery.Capture("f08"); phase = 6; return false;
        }
        if (!gallery.CaptureReady()) return false;
        gallery.RestoreSavedLocale(); result = "PASS gallery F07 rule, F09 native preview, F08 actual routed goods"; return true;
    }
}
