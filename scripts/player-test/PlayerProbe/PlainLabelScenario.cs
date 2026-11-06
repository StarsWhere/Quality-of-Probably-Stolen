using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// Repeats an actual native water-purity label on two other owned bottles.
public sealed class PlainLabelScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem tool, sample, first, second;
    private string expected, original;
    private int step;
    private float since;
    private static void Need(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }
    private static GameItem Put(string id)
    {
        var item = DirectoryMaster.Item(id); GeneralHelper.SetItemOwned(item);
        var root = EmporiumEntry.Instance.invElement; var slot = root.TryFindOneValidInventorySlot(item);
        Need(slot?.inventory == root && slot.TryAcceptOnce() == 1, "Cannot place " + id); return item;
    }
    public bool Tick(out string result)
    {
        result = null;
        var ui = LabelerUIManager.Instance;
        if (step == 0) { if (start.Tick()) { step++; since = Time.realtimeSinceStartup; } return false; }
        Need(Time.realtimeSinceStartup - since < 35, "Ordinary labeling timed out at " + step + " / " + expected +
            " : " + Label(first) + " / " + Label(second));
        if (step == 1)
        {
            if (ui?.panel == null || EmporiumEntry.Instance.storeInteractables?.activeInHierarchy != true) return false;
            TradeScenario.SetOptions(true, "f22.enabled", "f22.overwrite");
            var root = EmporiumEntry.Instance; tool = Put("labeler"); sample = Put("bottled_water"); first = Put("bottled_water"); second = Put("bottled_water");
            foreach (var bottle in new[] { sample, first, second })
            {
                WaterHelper.Remove(bottle, WaterHelper.GetCurrentCapacityUL(bottle)); WaterHelper.AddWater(bottle, 2, 750000);
                WaterFeatureHelper.InitWaterFeature(bottle);
            }
            original = Label(first);
            ItemSelectHandler.current.TryEquipItem(tool); tool.Target(sample);
            Need(ui.panel.activeSelf && ItemSelectHandler.current.GetEquippedItem() == tool, "Original labeler did not open on the sample");
            step++; return false;
        }
        if (step == 2)
        {
            var row = ui.verticalLayoutGroup.GetComponentsInChildren<LabelerElement>().Single(e => e.GetCategory() == "CATEGORY_WATER_PURITY");
            var menu = row.GetComponentInChildren<TMPro.TMP_Dropdown>();
            Need(menu != null && menu.options.Count > 1, "Native purity selector unavailable");
            menu.value = (menu.value + 1) % menu.options.Count;
            expected = row.GetSelectedConditionIdentifier(); ui.CloseUI();
            Need(!string.IsNullOrEmpty(expected) && expected != original && Label(sample) == expected, "Native template was not changed");
            step++; return false;
        }
        if (step == 3)
        {
            var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
            var native = CustomUIManager.Instance.GetWindow("game_options");
            options.CloseUI(); RoutingScenario.OpenOptionAction("f22.open");
            var preview = CustomUIManager.Instance.GetWindow(Mod + ".label");
            Need(preview?.Get("apply")?.button != null, "Ordinary batch control not available");
            QaItemUi.Click(preview, first); QaItemUi.Click(preview, second);
            Need(Label(first) == original, "Preview relabeled the actual bottle");
            preview.Get("apply").button.onClick.Invoke(); step++; return false;
        }
        if (ui.panel.activeSelf || Label(first) != expected || Label(second) != expected) return false;
        Need(first.parentInventory == EmporiumEntry.Instance.invElement && second.parentInventory == first.parentInventory &&
            !WaterFeatureHelper.GetWaterFeature(first).IsExposed() && Label(sample) == expected, "Native labels or ownership changed unexpectedly");
        result = "PASS native ordinary labeler repeated its real sample on two existing bottles; no items moved"; return true;
    }
    private static string Label(GameItem item) => item?.FindItemFeatureByCategory("CATEGORY_WATER_PURITY")?.fakeCondition?.identifier;
}
