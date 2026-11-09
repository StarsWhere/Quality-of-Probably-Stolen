using System;
using System.Linq;
using UnityEngine;

// Configures the real filter and furnace rules, then captures each native outcome.
public sealed class FeatureGalleryMachinesScenario
{
    private readonly QaFeatureGallery gallery = new QaFeatureGallery();
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private int phase;
    private float afterFilterNotice;
    private GameItemElement furnace, source, destination, pitcher, oldFilter, spareFilter;
    private GameGridInventory supplies, output;
    private GameSlotInventory filterSeat;

    public bool Tick(out string result)
    {
        result = null;
        QaFeatureGallery.Need(Time.realtimeSinceStartup < deadline, "Machine gallery timed out at " + phase);
        if (phase == 0) { if (!gallery.ChineseMainMenu()) return false; phase = 1; }
        if (phase == 1) { if (!start.Tick()) return false; phase = 2; }
        if (phase == 2)
        {
            if (!gallery.Screen1920() || ItemMouseDoubleClickHandler.current == null || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            TradeScenario.SetOptions(true, "f33.enabled", "f35.enabled");
            var stock = EmporiumEntry.Instance.invElement;
            furnace = QaFeatureGallery.Item("furnace", stock);
            source = QaFeatureGallery.Item("backpack_medium", stock, "备件与原料");
            destination = QaFeatureGallery.Item("satchel", stock, "成品收纳包");
            pitcher = QaFeatureGallery.Item("water_pitcher", stock);
            supplies = (GameGridInventory)source.contentWindow.childElement;
            output = (GameGridInventory)destination.contentWindow.childElement;
            oldFilter = QaFeatureGallery.Item("water_filter", stock);
            spareFilter = QaFeatureGallery.Item("water_filter", supplies);
            filterSeat = pitcher.contentWindow?.childElement as GameSlotInventory;
            QaFeatureGallery.Need(filterSeat != null, "Pitcher filter seat unavailable");
            QaFeatureGallery.Put(oldFilter, filterSeat); DurabilityHelper.SetCurrentDurability(oldFilter, 1);
            for (int i = 0; i < 4; i++) QaFeatureGallery.Item("scrap_metal", supplies);
            var battery = QaFeatureGallery.Item("energy_credit", stock);
            PowerHelper.SetPowerSourceAt(battery, 100); QaFeatureGallery.Put(battery, MachineHelper.GetBatterySlot(furnace));
            ItemMouseDoubleClickHandler.current.OpenContentAction(source, new Vector2(Screen.width * .13f, Screen.height * .64f));
            ItemMouseDoubleClickHandler.current.OpenContentAction(destination, new Vector2(Screen.width * .35f, Screen.height * .64f));
            ItemMouseDoubleClickHandler.current.OpenContentAction(pitcher, new Vector2(Screen.width * .57f, Screen.height * .64f));
            pitcher.contentWindow.ToFront();
            QaFeatureGallery.Action("f35.open");
            var maintenance = QaFeatureGallery.Window("maintenance");
            QaFeatureGallery.Need(maintenance?.IsAlive == true && maintenance.Get("source_slot") != null,
                "Filter maintenance view unavailable");
            QaItemUi.Choose(maintenance, "source", source.GetDisplayName(true));
            maintenance = QaFeatureGallery.Window("maintenance");
            QaFeatureGallery.Need(maintenance.Get("enable")?.button?.interactable == true, "Filter rule is not ready to save");
            gallery.Capture("f35"); phase = 3; return false;
        }
        if (phase == 3)
        {
            if (!gallery.CaptureReady()) return false;
            QaFeatureGallery.Window("maintenance").Get("enable").button.onClick.Invoke();
            int before = WaterHelper.GetCurrentCapacityML(pitcher);
            WaterHelper.AddWater(pitcher, 5, 250000, true, 0, 1);
            QaFeatureGallery.Need(WaterHelper.GetCurrentCapacityML(pitcher) > before && DurabilityHelper.GetRealDurability(oldFilter) == 0,
                "Native water processing did not exhaust the installed filter");
            phase = 4; return false;
        }
        if (phase == 4)
        {
            if (!ReferenceEquals(filterSeat.childItem, spareFilter)) return false;
            QaFeatureGallery.Need(ReferenceEquals(oldFilter.parentInventory, supplies), "Filter exchange did not retain the old filter");
            gallery.Capture("f35-result"); afterFilterNotice = Time.realtimeSinceStartup + 6.2f; phase = 5; return false;
        }
        if (phase == 5)
        {
            if (!gallery.CaptureReady() || Time.realtimeSinceStartup < afterFilterNotice || QaFeatureGallery.NoticeVisible()) return false;
            ItemMouseDoubleClickHandler.current.OpenContentAction(furnace, new Vector2(Screen.width * .57f, Screen.height * .64f));
            furnace.contentWindow.ToFront(); QaFeatureGallery.Action("f33.open");
            var feed = QaFeatureGallery.Window("f33");
            QaFeatureGallery.Need(feed?.IsAlive == true, "Furnace rule window unavailable");
            QaItemUi.Choose(feed, "source", source.GetDisplayName(true)); feed = QaFeatureGallery.Window("f33");
            QaItemUi.Choose(feed, "destination", destination.GetDisplayName(true)); feed = QaFeatureGallery.Window("f33");
            GameItem material = supplies.childItems.First(item => item.identifier == "scrap_metal");
            QaItemUi.Choose(feed, "material", material.GetDisplayName(true)); feed = QaFeatureGallery.Window("f33");
            feed.Get("limit").SetFloat(2, true);
            gallery.Capture("f33"); phase = 6; return false;
        }
        if (phase == 6)
        {
            if (!gallery.CaptureReady()) return false;
            QaFeatureGallery.Window("f33").Get("bind").button.onClick.Invoke();
            QaFeatureGallery.Need(!string.IsNullOrEmpty(ModHelper.GetModData("starswhere.shopassist", "f33.rules", null)),
                "Furnace rule was not saved");
            FeedScenario.Cycle(furnace);
            QaFeatureGallery.Need(supplies.childItems.Count(item => item.identifier == "scrap_metal") == 2 &&
                output.childItems.Count(item => item.identifier == "metal_ingot") == 1,
                "Native furnace cycle did not consume a batch and return its output");
            gallery.Capture("f33-result"); phase = 7; return false;
        }
        if (!gallery.CaptureReady()) return false;
        gallery.RestoreSavedLocale(); result = "PASS gallery F35 filter rule and exchange; F33 furnace rule and native batch"; return true;
    }
}
