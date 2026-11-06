using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Exercises real appraisal payment and receipt placement in the guarded QA save.
public sealed class ReceiptScenario
{
    private const string ModId = "starswhere.shopassist";
    private const string WindowId = ModId + ".f24.target";
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly List<GameItem> frontFillers = new List<GameItem>();
    private QaLoadSlot load;
    private GameItem box, other, appraisal;
    private GameGridInventory target, front;
    private StoreClient client;
    private int estimate, cashBefore;
    private int step;
    private float started = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - started > 50f) throw new InvalidOperationException("F24 QA timed out at step " + step);
        switch (step)
        {
            case 0:
            {
                if (MainMenuUIController.Instance == null) return false;
                string[] context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-latest-run.txt")).Trim().Split('|');
                Require(context.Length == 4 && context[0] == "1" && Guid.TryParseExact(context[1], "N", out _) && int.TryParse(context[2], out _), "Invalid isolated QA context");
                load = new QaLoadSlot(int.Parse(context[2]), Guid.ParseExact(context[1], "N"));
                Next(1, "Loading isolated F24 appraisal save"); return false;
            }
            case 1:
            {
                if (!load.Tick() || StoreUIManager.Instance?.gameOptionUIManager == null || EmporiumEntry.Instance?.invWindow == null) return false;
                var calendar = AdvCalendarUIManager.Instance;
                if (calendar?.UIPanel != null && calendar.UIPanel.activeSelf) StoreUIManager.Instance.storeCalendar.OnClick();
                Require(PlayerStore.Instance.currentClientInstance == null, "QA save unexpectedly has a customer");
                front = EmporiumEntry.Instance.frontInvinvElement;
                box = DirectoryMaster.Item("fanny_pack");
                GeneralHelper.SetCustomName(box, "QA receipts " + token);
                GraphUtils.EmporiumTryAdd(EmporiumEntry.Instance.invElement, box);
                target = box.contentWindow?.childElement as GameGridInventory;
                Require(box.parentInventory != null && target != null, "Cannot place the isolated QA receipt container");
                other = PreBuiltItemHelper.CreatePaperWithText("existing paper " + token, "QA existing paper");
                PlayerStore.Instance.AddDirectSellingItemToTable(other, isOwend: true);
                Require(other.parentInventory == front, "Could not place the unrelated paper on the front counter");
                var options = Options();
                options.Get(ModId + ":f24.enabled").SetBool(true, true);
                options.Get(ModId + ":f24.destination").SetIndex(1, true);
                options.Get("apply").button.onClick.Invoke();
                StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                RoutingScenario.OpenOptionAction("f24.choose");
                var picker = CustomUIManager.Instance.GetWindow(ModId + ".choose");
                Require(picker != null && picker.IsVisible, "F24 target picker unavailable");
                QaItemUi.Click(picker, box);
                string binding = ModHelper.GetModData(ModId, "f24.target", null);
                Require(binding != null && binding.Contains(box.uniqueId.ToString()), "F24 did not bind the selected container");
                client = StoreClientListAppraisal.CreateAppraisal1();
                PlayerStore.Instance.storeClientManager.AddNextClient(client);
                PlayerStore.Instance.GetNextClient();
                Require(ReferenceEquals(PlayerStore.Instance.currentClientInstance?.GetClientBlueprint(), client), "Native appraisal customer unavailable");
                appraisal = PreBuiltItemHelper.CreateHandRandom();
                PlayerStore.Instance.AddDirectSellingItemToTable(appraisal);
                Require(appraisal.parentInventory == front && !GeneralHelper.IsItemOwned(appraisal), "Could not place the customer's appraisal item");
                estimate = AppraisalHelper.GetCorrectBaseValue(appraisal); // QA oracle only; not used by the feature.
                cashBefore = PlayerStore.Instance.playerCash;
                InspectionUIManager.Instance.OnInspectItem(appraisal);
                InspectionUIManager.Instance.UpdateAppraisalPanel();
                Require(InspectionUIManager.Instance.appraisalPanel.activeInHierarchy, "Native appraisal input is unavailable");
                SetEstimate(1);
                InspectionUIManager.Instance.OnAppraisalServiceEstimated();
                Require(PlayerStore.Instance.playerCash == cashBefore && client.clientImpatience > 0 &&
                    !((GameItemElement)appraisal).IsDestroyed() && target.childItems.Count == 0 && other.parentInventory == front,
                    "Rejected estimate unexpectedly paid, routed a receipt or changed unrelated paper");
                Next(2, "Native appraisal rejection left cash, item and receipts unchanged"); return false;
            }
            case 2:
            {
                if (DialogUIManager.Instance?.currentDialog != null)
                {
                    DialogUIManager.Instance.OnAdvancePressed(); return false;
                }
                Require(ReferenceEquals(PlayerStore.Instance.currentClientInstance?.GetClientBlueprint(), client), "QA customer left after the first rejection");
                var before = new HashSet<GameItem>(target.childItems);
                InspectionUIManager.Instance.OnInspectItem(appraisal);
                InspectionUIManager.Instance.UpdateAppraisalPanel();
                SetEstimate(estimate);
                int expectedFee = Fee(estimate);
                cashBefore = PlayerStore.Instance.playerCash;
                InspectionUIManager.Instance.OnAppraisalServiceEstimated();
                Require(PlayerStore.Instance.playerCash - cashBefore == expectedFee, "Native accepted appraisal did not pay exactly once");
                Require(((GameItemElement)appraisal).IsDestroyed(), "Native accepted appraisal item was not consumed");
                GameItem slip = NewSlip(target, before);
                Require(slip != null && GeneralHelper.IsItemOwned(slip) && other.parentInventory == front, "F24 did not route exactly the new native receipt to the selected box");
                Next(3, "Native appraisal accepted, paid once and delivered its new receipt to the selected container"); return false;
            }
            case 3:
            {
                Fill(front, frontFillers);
                Require(NoRoom(front), "QA front counter did not fill");
                var before = new HashSet<GameItem>(target.childItems);
                int beforeCash = PlayerStore.Instance.playerCash;
                AppraisalServiceHelper.GiveAppraisalFee(201, 1, 201);
                Require(PlayerStore.Instance.playerCash - beforeCash == Fee(201), "F24 changed or repeated the fee with a full front counter");
                Require(NewSlip(target, before) != null && other.parentInventory == front, "F24 could not route to an available box when the front counter was full");
                foreach (GameItem filler in frontFillers) if (filler.parentInventory == front) filler.Destroy();
                frontFillers.Clear();
                Fill(target, null);
                Require(NoRoom(target), "QA receipt box did not fill");
                before = new HashSet<GameItem>(front.childItems);
                beforeCash = PlayerStore.Instance.playerCash;
                AppraisalServiceHelper.GiveAppraisalFee(202, 1, 202);
                Require(PlayerStore.Instance.playerCash - beforeCash == Fee(202), "F24 changed or repeated the fee for a full box");
                Require(NewSlip(front, before) != null && other.parentInventory == front, "A full box did not fall back to the original counter");
                Next(4, "Available box works with a full front; a full box falls back to the front"); return false;
            }
            case 4:
            {
                box.Destroy();
                var before = new HashSet<GameItem>(front.childItems);
                int beforeCash = PlayerStore.Instance.playerCash;
                AppraisalServiceHelper.GiveAppraisalFee(203, 1, 203);
                Require(PlayerStore.Instance.playerCash - beforeCash == Fee(203) && NewSlip(front, before) != null && other.parentInventory == front,
                    "A missing selected container did not retain the original counter delivery and fee");
                Fill(front, frontFillers);
                Require(NoRoom(front), "QA front counter did not fill for the double-full fallback");
                var occupied = new HashSet<GameItem>(front.childItems);
                beforeCash = PlayerStore.Instance.playerCash;
                AppraisalServiceHelper.GiveAppraisalFee(204, 1, 204);
                Require(PlayerStore.Instance.playerCash - beforeCash == Fee(204) && other.parentInventory == front &&
                    front.childItems.All(occupied.Contains), "Double-full fallback duplicated payment, added a receipt, or changed unrelated paper");
                result = "PASS F24 real native appraisal rejection/acceptance, one fee, selected box and full front, full/missing box fallback, double-full native failure, and unrelated paper preservation";
                return true;
            }
            default: throw new InvalidOperationException("Unknown F24 QA step " + step);
        }
    }

    private void SetEstimate(int value)
    {
        string text = value.ToString();
        InspectionUIManager.Instance.apprisalServiceInputField.SetTextWithoutNotify(text);
        InspectionUIManager.Instance.OnValueChanged(text);
    }
    private static int Fee(int value) => RNG.CalculatePercentage(value, AppraisalRankHelper.GetFeePercent(1)) + AppraisalRankHelper.GetBaseFee();
    private static GameItem NewSlip(GameGridInventory grid, HashSet<GameItem> previous)
    {
        var items = grid.childItems.Where(item => !previous.Contains(item) && item.identifier == "slip").ToArray();
        return items.Length == 1 ? items[0] : null;
    }
    private static bool NoRoom(GameGridInventory inventory)
    {
        GameItem probe = PreBuiltItemHelper.CreatePaperWithText("QA capacity probe " + Guid.NewGuid().ToString("N"), "QA capacity probe");
        SlotMarker slot = inventory.TryFindOneValidInventorySlot(probe);
        bool result = slot == null || slot.numTransfer < 1;
        probe.Destroy(); return result;
    }
    private static void Fill(GameGridInventory inventory, List<GameItem> keep)
    {
        for (int i = 0; i < 200 && !NoRoom(inventory); i++)
        {
            GameItem item = PreBuiltItemHelper.CreatePaperWithText("QA fill " + Guid.NewGuid().ToString("N"), "QA filler");
            int countBefore = inventory.childItems.Count;
            int remaining = GraphUtils.TryAcceptAll(inventory, item);
            Require(remaining == 0 && item.parentInventory == inventory && inventory.childItems.Count == countBefore + 1,
                "Could not fill QA inventory with distinct native papers");
            keep?.Add(item);
        }
    }
    private static CustomUIWindow Options()
    {
        StoreUIManager.Instance.gameOptionUIManager.OpenUI();
        var options = CustomUIManager.Instance.GetWindow("game_options");
        Require(options != null && options.IsVisible, "Native options unavailable"); return options;
    }
    private void Next(int value, string message) { Debug.Log("[PLAYER-QA] " + message); step = value; started = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
