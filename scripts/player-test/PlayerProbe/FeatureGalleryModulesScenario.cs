using System;
using UnityEngine;

// F10 captures the idle module workbench; F12 separately captures its actual layout preview.
public sealed class FeatureGalleryModulesScenario
{
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120f;
    private GameItemElement machine, candidate;
    private GameGridInventory bay;
    private int phase, after;

    public bool Tick(out string result)
    {
        result = null;
        QaFeatureGallery.Need(Time.realtimeSinceStartup < deadline, "Module gallery timed out at " + phase);
        if (phase == 0) { if (!gallery.ChineseMainMenu()) return false; phase = 1; }
        if (phase == 1) { if (!start.Tick()) return false; phase = 2; }
        if (phase == 2)
        {
            if (!gallery.Screen1920() || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f10.enabled", "f12.enabled");
            var stock = EmporiumEntry.Instance.invElement;
            machine = QaFeatureGallery.Item("hydroponic", stock);
            candidate = QaFeatureGallery.Item("system_module_performance", stock);
            var spare = QaFeatureGallery.Item("system_module_efficiancy", stock);
            var installed = QaFeatureGallery.Item("system_module_quality", stock);
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(Screen.width * .17f, Screen.height * .65f));
            var panel = (machine.contentWindow.childElement as MachineryPanelRow)?.GetPanel("module");
            var nodes = panel == null ? null : MachineryContainerUtils.Collect(panel.content);
            QaFeatureGallery.Need(nodes?.Count == 1 && nodes[0] is GameGridInventory, "Native module bay unavailable");
            bay = (GameGridInventory)nodes[0]; QaFeatureGallery.Put(installed, bay);
            machine.contentWindow.ToFront(); after = Time.frameCount + 6; phase = 3; return false;
        }
        if (phase == 3)
        {
            if (Time.frameCount < after) return false;
            GridShape native = bay.inventoryShape;
            QaFeatureGallery.Need(native != null && native.width == 7 && native.height == 5 &&
                machine.GetTagReadonly("MODULE_UPGRADE_STAGE_INT").GetInt() == 0,
                "The new hydroponic machine no longer has its native unupgraded 7x5 module bay");
            const string mask = "10000010001000001110000010001000001";
            for (int y = 0; y < native.height; y++) for (int x = 0; x < native.width; x++)
                QaFeatureGallery.Need(native.Get(x, y) == mask[y * native.width + x] - '0',
                    "The new hydroponic module bay mask was changed before capture");
            QaFeatureGallery.Action("f10.open");
            var browser = QaFeatureGallery.Window("f10");
            QaFeatureGallery.Need(browser?.IsAlive == true && QaItemUi.Hit(browser.root, candidate) != null,
                "Module workbench did not show the idle module");
            gallery.Capture("f10"); phase = 4; return false;
        }
        if (phase == 4)
        {
            if (!gallery.CaptureReady()) return false;
            var browser = QaFeatureGallery.Window("f10");
            QaItemUi.Click(browser, candidate);
            browser.Get("layout").button.onClick.Invoke();
            browser = QaFeatureGallery.Window("f10");
            QaFeatureGallery.Need(browser.Get("preview")?.button != null, "Module layout controls unavailable");
            browser.Get("preview").button.onClick.Invoke(); browser = QaFeatureGallery.Window("f10");
            QaFeatureGallery.Need(browser.Get("apply")?.button != null && candidate.parentInventory != bay,
                "Module preview did not produce a safe layout before moving the candidate");
            gallery.Capture("f12"); phase = 5; return false;
        }
        if (!gallery.CaptureReady()) return false;
        gallery.RestoreSavedLocale(); result = "PASS gallery F10 idle module workbench and F12 non-mutating layout preview"; return true;
    }
}
