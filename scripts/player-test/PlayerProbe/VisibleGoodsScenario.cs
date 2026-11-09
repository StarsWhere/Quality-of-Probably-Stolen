using System;
using UnityEngine;

// Exercises owned counter goods through the real module window.
public sealed class VisibleGoodsScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private GameItem machine, module;
    private int step, after;

    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static CustomUIWindow Window(string id) => CustomUIManager.Instance.GetWindow(Mod + "." + id);
    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Visible goods scenario timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var entry = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (entry?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f10.enabled");
            machine = QaFeatureGallery.Item("hydroponic", entry.invElement);
            module = QaFeatureGallery.Item("system_module_performance", entry.backInvinvElement, "QA counter module");
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(Screen.width * .25f, Screen.height * .7f));
            machine.contentWindow.ToFront(); RoutingScenario.RunFeatureAction("f10.open");
            after = Time.frameCount + 3; step++; return false;
        }
        if (step == 2)
        {
            if (Time.frameCount < after) return false;
            var browser = Window("f10");
            Need(browser?.IsAlive == true && QaItemUi.Hit(browser.root, module) != null,
                "Owned counter module was missing from the module browser");
            browser.Close();
            result = "PASS owned counter module appears in the native module browser";
            return true;
        }
        return false;
    }
}
