using System;
using System.Linq;
using UnityEngine;

// Compares the real ingredient grid before brewing with the same yeast returned to its source.
public sealed class FeatureGalleryYeastScenario
{
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100f;
    private GameItemElement bag, bottle, yeast;
    private GameGridInventory home, ingredients;
    private int phase;

    public bool Tick(out string result)
    {
        result = null;
        QaFeatureGallery.Need(Time.realtimeSinceStartup < deadline, "Yeast gallery timed out at " + phase);
        if (phase == 0) { if (!gallery.ChineseMainMenu()) return false; phase = 1; }
        if (phase == 1) { if (!start.Tick()) return false; phase = 2; }
        if (phase == 2)
        {
            if (!gallery.Screen1920() || ItemMouseDoubleClickHandler.current == null || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(true, "f37.enabled");
            TradeScenario.SetOptions(false, "f15.enabled", "f21.enabled");
            var stock = EmporiumEntry.Instance.invElement;
            bag = QaFeatureGallery.Item("backpack_medium", stock, "酿酒原料包");
            home = bag.contentWindow.childElement as GameGridInventory;
            QaFeatureGallery.Need(home != null, "Yeast source container unavailable");
            yeast = QaFeatureGallery.Item("wine_yeast_infinite", home);
            bottle = QaFeatureGallery.Item("wine_bottle", stock);
            var panel = (bottle.contentWindow.childElement as MachineryPanelRow)?.GetPanel("ingredients");
            ingredients = panel == null ? null : MachineryContainerUtils.Collect(panel.content).OfType<GameGridInventory>().SingleOrDefault();
            QaFeatureGallery.Need(ingredients != null, "Native wine ingredient grid unavailable");
            QaFeatureGallery.Item("wine_berry", ingredients);
            WaterHelper.AddWater(bottle, 2, 750000); QaFeatureGallery.Put(yeast, ingredients);
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width * .18f, Screen.height * .65f));
            ItemMouseDoubleClickHandler.current.OpenContentAction(bottle, new Vector2(Screen.width * .46f, Screen.height * .65f));
            gallery.Capture("f37-before"); phase = 3; return false;
        }
        if (phase == 3)
        {
            if (!gallery.CaptureReady()) return false;
            QaFeatureGallery.Need(WineHelper.TryStartFermentation(bottle) && ReferenceEquals(yeast.parentInventory, home),
                "Native brewing did not return the original reusable yeast to its source");
            gallery.Capture("f37"); phase = 4; return false;
        }
        if (!gallery.CaptureReady()) return false;
        gallery.RestoreSavedLocale(); result = "PASS gallery F37 native brewing and same yeast returned to its source"; return true;
    }
}
