using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Exercises each visible overlay's native setting without changing the other two.
public sealed class DailyMarkerSwitchScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly string areaName = "QA area " + Guid.NewGuid().ToString("N").Substring(0, 8);
    private GameItemElement bag, stolen;
    private string publicLabel;
    private int step, frame;
    private float since = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - since < 45f, "Overlay switch QA timed out at step " + step +
            ": name=" + AreaVisible() + " label=" + LabelVisible() + " stolen=" + MarkerVisible());
        if (step == 0) { if (start.Tick()) Next(1); return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.invElement == null || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            Need(EmporiumEntry.Instance.invWindow.IsVisible() && !EmporiumEntry.Instance.invWindow.canMove &&
                StoreUIManager.Instance.IsInnateWindow(EmporiumEntry.Instance.invWindow), "Native main stock is not naturally ready");
            TradeScenario.SetOptions(true, "f02.enabled", "f38.enabled", "f39.enabled");
            TradeScenario.SetOptions(false, "f04.enabled");
            bag = New("backpack_medium"); stolen = New("wine_yeast");
            GeneralHelper.SetCustomName(bag, areaName);
            var shown = ItemConditionList.CreateCounterfeitCigarette();
            var actual = ItemConditionList.CreateGenuineCigarette();
            shown.onActivateActionId = null;
            publicLabel = shown.display;
            var feature = new ItemFeature(ItemFeature.FeatureType.Special) { identifier = "qa_switch_public", category = shown.category };
            feature.SetConditions(shown, actual); bag.AddItemFeature(feature);
            StolenHelper.SetHeat(stolen, 42);
            Need(stolen.IsTag(StolenHelper.STOLEN_TAG) && stolen.HasTooltip() && stolen.tooltipRichTextFunc == null,
                "Native visible stolen fixture is unavailable");
            Put(EmporiumEntry.Instance.invElement, bag); Put(EmporiumEntry.Instance.invElement, stolen);
            Next(2); return false;
        }
        if (step == 2)
        {
            if (!AreaVisible() || !LabelVisible() || !MarkerVisible()) return false;
            CheckContent();
            TradeScenario.SetOptions(false, "f38.enabled"); Wait(3); return false;
        }
        if (Time.frameCount < frame) return false;
        if (step == 3)
        {
            Need(!LabelVisible() && AreaVisible() && MarkerVisible(), "F38 off did not remove only the public label");
            TradeScenario.SetOptions(true, "f38.enabled"); Wait(4); return false;
        }
        if (step == 4)
        {
            Need(LabelVisible() && AreaVisible() && MarkerVisible(), "F38 on did not restore only the public label");
            CheckContent(); TradeScenario.SetOptions(false, "f39.enabled"); Wait(5); return false;
        }
        if (step == 5)
        {
            Need(!MarkerVisible() && AreaVisible() && LabelVisible(), "F39 off did not remove only the stolen marker");
            TradeScenario.SetOptions(true, "f39.enabled"); Wait(6); return false;
        }
        if (step == 6)
        {
            Need(MarkerVisible() && AreaVisible() && LabelVisible(), "F39 on did not restore only the stolen marker");
            CheckContent(); TradeScenario.SetOptions(false, "f02.enabled"); Wait(7); return false;
        }
        if (step == 7)
        {
            Need(!AreaVisible() && LabelVisible() && MarkerVisible(), "F02 off did not remove only the area name");
            TradeScenario.SetOptions(true, "f02.enabled"); Wait(8); return false;
        }
        Need(AreaVisible() && LabelVisible() && MarkerVisible(), "F02 on did not restore the area name independently");
        CheckContent();
        Need(ReferenceEquals(bag.parentInventory, EmporiumEntry.Instance.invElement) &&
            ReferenceEquals(stolen.parentInventory, EmporiumEntry.Instance.invElement) && stolen.IsTag(StolenHelper.STOLEN_TAG),
            "Overlay switches changed the actual items");
        result = "PASS F02/F38/F39 independent disable/re-enable on visible native items, with F04 disabled";
        return true;
    }

    private void CheckContent()
    {
        Need(Text(Area()) == areaName, "Visible area name does not match the native container name");
        Need(!string.IsNullOrEmpty(publicLabel) && Text(Label()).Contains(publicLabel), "Visible label does not match the public native condition");
        Need(Text(Marker()).Contains("!") && Marker().GetComponent<Image>()?.enabled == true,
            "Known stolen item did not show its direct marker");
    }
    private Transform Area() => bag?.middle?.transform.Find("ShopAssist_AreaName");
    private Transform Label() => bag?.middle?.transform.Find("ShopAssist_PublicLabels");
    private Transform Marker() => stolen?.middle?.transform.Find("ShopAssist_KnownStolen");
    private bool AreaVisible() => Visible(Area());
    private bool LabelVisible() => Visible(Label());
    private bool MarkerVisible() => Visible(Marker());
    private static bool Visible(Transform node) => node != null && node.gameObject.activeInHierarchy;
    private static string Text(Transform node) => node == null ? "" : string.Join("|", node.GetComponentsInChildren<TMP_Text>(true).Select(value => value.text));
    private static GameItemElement New(string id)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement;
        Need(item != null, "Missing native fixture " + id); GeneralHelper.SetItemOwned(item, true); return item;
    }
    private static void Put(GameInventory inventory, GameItem item)
    {
        var slot = inventory.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == inventory && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory, "Could not place " + item.identifier);
    }
    private void Wait(int next) { frame = Time.frameCount + 8; Next(next); }
    private void Next(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
