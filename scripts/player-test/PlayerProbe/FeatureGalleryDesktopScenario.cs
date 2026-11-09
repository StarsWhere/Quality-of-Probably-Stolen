using System;
using System.Linq;
using TMPro;
using UnityEngine;

// Opens the native bargaining panel and shutter data.
public sealed class FeatureGalleryDesktopScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private int phase;
    private float after;
    public bool Tick(out string result)
    {
        result = null;
        QaFeatureGallery.Need(Time.realtimeSinceStartup < deadline, "Desktop gallery timed out at " + phase);
        if (phase == 0) { if (!gallery.ChineseMainMenu()) return false; phase = 1; }
        if (phase == 1) { if (!start.Tick()) return false; phase = 2; }
        if (phase == 2)
        {
            if (!gallery.Screen1920() || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(true, "f14.enabled", "f17.enabled", "f21.enabled");
            phase = 3;
        }
        if (phase == 3)
        {
            var p = PlayerStore.Instance; p.isBargainIntroduced = true;
            var buyer = StoreClientList.CreateSpacerClientFood(); buyer.isIntroduced = true; buyer.isBargainAvailable = true;
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer); p.isClientArrived = true;
            var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
            var settings = CustomUIManager.Instance.GetWindow("game_options");
            settings.Get(Mod + ":f14.side").SetIndex(1, true); settings.Get("apply").button.onClick.Invoke(); options.CloseUI();
            NegociationUIManager.Instance.OpenUI();
            var tablet = NegociationUIManager.Instance.uiObject.GetComponent<NegotiationTabletFrame>();
            QaFeatureGallery.Need(tablet?.bargainButton != null && tablet.bargainButton.gameObject.activeInHierarchy,
                "Native customer has no bargaining button");
            tablet.bargainButton.onClick.Invoke();
            QaFeatureGallery.Need(BargainUIManager.Instance.IsOpen, "Native bargaining panel did not open");
            after = Time.realtimeSinceStartup + .6f; phase = 4; return false;
        }
        if (phase == 4)
        {
            if (Time.realtimeSinceStartup < after) return false;
            gallery.Capture("f14"); phase = 5; return false;
        }
        if (phase == 5)
        {
            if (!gallery.CaptureReady()) return false;
            BargainUIManager.Instance.CloseUI(); NegociationUIManager.Instance.CloseUI();
            PlayerStore.Instance.currentClientInstance = null; PlayerStore.Instance.isClientArrived = false;
            QaFeatureGallery.Action("f17.open");
            var todo = QaFeatureGallery.Window("todo");
            QaFeatureGallery.Need(todo?.Get("task_text") != null, "Checklist did not open");
            todo.Get("task_text").SetString("补充展示柜货物", true); todo.Get("task_save").button.onClick.Invoke();
            todo = QaFeatureGallery.Window("todo");
            todo.Get("task_text").SetString("检查净水壶滤芯", true); todo.Get("task_save").button.onClick.Invoke();
            gallery.Capture("f17-list"); phase = 6; return false;
        }
        if (phase == 6)
        {
            if (!gallery.CaptureReady()) return false;
            QaFeatureGallery.Window("todo").Close();
            TradeScenario.SetOptions(false, "f15.enabled", "f21.enabled");
            after = Time.realtimeSinceStartup + 1f; phase = 7; return false;
        }
        if (phase == 7)
        {
            if (Time.realtimeSinceStartup < after) return false;
            var task = Resources.FindObjectsOfTypeAll<TMP_Text>().FirstOrDefault(value => value.gameObject.scene.IsValid() &&
                value.gameObject.activeInHierarchy && value.text != null && value.text.Contains("补充展示柜货物"));
            if (task == null) return false;
            gallery.Capture("f17"); phase = 8; return false;
        }
        if (phase == 8)
        {
            if (!gallery.CaptureReady()) return false;
            TradeScenario.SetOptions(false, "f17.enabled"); TradeScenario.SetOptions(true, "f21.enabled");
            var p = PlayerStore.Instance;
            QaFeatureGallery.Need(p.IsPayingRent() && p.dayUntilRent > 0, "Native rent due state is unavailable");
            after = Time.realtimeSinceStartup + 1f; phase = 9; return false;
        }
        if (phase == 9)
        {
            if (Time.realtimeSinceStartup < after) return false;
            var row = Resources.FindObjectsOfTypeAll<TMP_Text>().FirstOrDefault(value => value.name == "deadline-row" &&
                value.gameObject.scene.IsValid() && value.gameObject.activeInHierarchy &&
                value.text.Contains(PlayerStore.Instance.dayUntilRent.ToString()));
            if (row == null) return false;
            gallery.Capture("f21"); phase = 10; return false;
        }
        if (!gallery.CaptureReady()) return false;
        gallery.RestoreSavedLocale(); result = "PASS gallery F14 bargaining, F17 checklist and F21 deadline"; return true;
    }
}
