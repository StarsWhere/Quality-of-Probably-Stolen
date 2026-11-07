using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Exercises F35 against a reservation made and released through the actual F27 UI.
public sealed class MaintenanceReservationScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly string label = "QA filter reservation " + Guid.NewGuid().ToString("N").Substring(0, 6);
    private GameItem pitcher, oldFilter, spare, box;
    private GameSlotInventory filterSlot;
    private GameGridInventory source;
    private StoreRequest order;
    private int step;
    private float deadline = Time.realtimeSinceStartup + 120f, until;
    private static void Need(bool ok, string what) { if (!ok) throw new InvalidOperationException(what); }

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "F35 reservation QA timed out at step " + step);
        if (step == 0) { if (start.Tick()) step = 1; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables == null || !EmporiumEntry.Instance.storeInteractables.activeInHierarchy ||
                PCUIManager.Instance == null || ItemMouseDoubleClickHandler.current == null) return false;
            var calendar = AdvCalendarUIManager.Instance;
            if (calendar?.UIPanel != null && calendar.UIPanel.activeSelf) StoreUIManager.Instance.storeCalendar.OnClick();
            pitcher = New("water_pitcher"); oldFilter = New("water_filter"); spare = New("water_filter"); box = New("backpack_medium");
            GeneralHelper.SetCustomName(box, label);
            Put(EmporiumEntry.Instance.invElement, pitcher); Put(EmporiumEntry.Instance.invElement, oldFilter);
            Put(EmporiumEntry.Instance.invElement, spare); Put(EmporiumEntry.Instance.invElement, box);
            filterSlot = pitcher.contentWindow?.childElement as GameSlotInventory;
            source = box.contentWindow?.childElement as GameGridInventory;
            Need(filterSlot != null && source != null, "Native QA filter/source slots unavailable");
            Put(filterSlot, oldFilter); Put(source, spare);
            ItemMouseDoubleClickHandler.current.OpenContentAction(pitcher, new Vector2(250, 140));
            ItemMouseDoubleClickHandler.current.OpenContentAction(box, new Vector2(500, 140));
            var opts = StoreUIManager.Instance.gameOptionUIManager;
            opts.OpenUI(); var options = CustomUIManager.Instance.GetWindow("game_options");
            options.Get(Mod + ":f15.enabled").SetBool(false, true);
            options.Get(Mod + ":f27.enabled").SetBool(true, true);
            options.Get(Mod + ":f35.enabled").SetBool(true, true);
            options.Get("apply").button.onClick.Invoke();
            opts.CloseUI();
            RoutingScenario.RunFeatureAction("f35.open");
            var chooser = CustomUIManager.Instance.GetWindow(Mod + ".maintenance");
            Need(chooser != null, "F35 source selector unavailable");
            QaItemUi.Choose(chooser, "source", label);
            chooser = CustomUIManager.Instance.GetWindow(Mod + ".maintenance");
            Need(chooser?.Get("enable")?.button != null, "F35 source selection was not retained");
            chooser.Get("enable").button.onClick.Invoke();
            order = new StoreRequest { title = label, id = label, description = label, requiredItem = "water_filter",
                requiredItemCount = 1, requesterName = "QA", sourceEventId = label, duration = 2, creditReward = 155 };
            PlayerStore.Instance.storeRequests.Add(order);
            PCUIManager.Instance.OpenUI(); step = 2; return false;
        }
        if (step == 2)
        {
            var row = UnityEngine.Object.FindObjectsOfType<StoreRequestElement>().FirstOrDefault(r => r.storeRequest == order);
            if (row == null) return false;
            var track = row.acceptButton.transform.parent.Find(Mod + ".track")?.GetComponent<Button>();
            Need(track != null && track.interactable, "Native F27 tracking action unavailable"); track.onClick.Invoke();
            var book = Book(); Need(book?.Get("track")?.button != null, "F27 tracking window unavailable");
            book.Get("track").button.onClick.Invoke(); book = Book();
            QaItemUi.Choose(book, "destination", label); book = Book();
            QaItemUi.Click(book, spare);
            book.Get("prepare").button.onClick.Invoke();
            string raw = ModHelper.GetModData(Mod, "requests", null);
            Need(spare.uniqueId > 0 && raw != null && raw.Contains(spare.uniqueId.ToString()) && spare.parentInventory == source,
                "Native F27 UI did not reserve the actual spare");
            Book().Close(); PCUIManager.Instance.HideUI();
            DurabilityHelper.SetCurrentDurability(oldFilter, 0);
            int before = WaterHelper.GetCurrentCapacityML(pitcher);
            WaterHelper.AddWater(pitcher, 5, 100000, true, 0, 1);
            Need(WaterHelper.GetCurrentCapacityML(pitcher) > before, "Native processing did not add water");
            until = Time.realtimeSinceStartup + 1f; step = 3; return false;
        }
        if (step == 3)
        {
            Need(filterSlot.childItem == oldFilter && spare.parentInventory == source, "F35 consumed a filter reserved by F27");
            if (Time.realtimeSinceStartup < until) return false;
            RoutingScenario.RunFeatureAction("f27.open");
            var book = Book();
            var orderButton = book.root.GetComponentsInChildren<Button>(true).FirstOrDefault(b =>
                b.GetComponentInChildren<TMP_Text>(true)?.text == ModHelper.GetLocalized(Mod, "f27.prepare") &&
                b.transform.parent.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == label));
            Need(orderButton != null, "Tracked QA request not offered for release"); orderButton.onClick.Invoke(); book = Book();
            string release = ModHelper.GetLocalized(Mod, "f27.release");
            var releaseButton = book.root.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.GetComponentInChildren<TMP_Text>(true)?.text == release);
            Need(releaseButton != null, "Native F27 release action unavailable"); releaseButton.onClick.Invoke();
            Book()?.Close();
            int before = WaterHelper.GetCurrentCapacityML(pitcher);
            WaterHelper.AddWater(pitcher, 5, 100000, true, 0, 1);
            Need(WaterHelper.GetCurrentCapacityML(pitcher) > before, "Native processing did not retry after release");
            step = 4; return false;
        }
        if (step == 4)
        {
            if (filterSlot.childItem == oldFilter) return false;
            Need(filterSlot.childItem == spare && oldFilter.parentInventory == source && DurabilityHelper.GetRealDurability(oldFilter) == 0 &&
                EmporiumEntry.Instance.swapBufferElement.childItem == null, "F35 did not exchange conserved filters after the reservation was released");
            result = "PASS F35 honored and released a real F27 reservation before exchanging the same filter instances";
            return true;
        }
        throw new InvalidOperationException("Unknown F35 reservation QA step");
    }

    private static CustomUIWindow Book() => CustomUIManager.Instance.GetWindow(Mod + ".requests");
    private static GameItem New(string id) { var item = DirectoryMaster.Item(id); Need(item != null, "Missing " + id); GeneralHelper.SetItemOwned(item); return item; }
    private static void Put(GameInventory target, GameItem item)
    {
        var slot = target?.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == target && slot.targetItem == null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == target,
            "Cannot place QA " + item.identifier);
    }
}
