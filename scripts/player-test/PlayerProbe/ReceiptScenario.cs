using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

// Exercises real appraisal payment and receipt placement in the guarded QA save.
public sealed class ReceiptScenario
{
    private const string ModId = "starswhere.shopassist";
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly List<GameItem> frontFillers = new List<GameItem>();
    private readonly List<GameItem> targetFillers = new List<GameItem>();
    private QaLoadSlot load;
    private GameItem other, appraisal;
    private GameGridInventory front, trash, decoyTrash, showcase, stock;
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
                trash = EmporiumEntry.Instance.trashcan?.contentWindow?.childElement as GameGridInventory;
                showcase = EmporiumEntry.Instance.showcaseElement;
                stock = EmporiumEntry.Instance.invElement;
                Require(front != null && trash != null && showcase != null && stock != null &&
                    ReferenceEquals(trash.GetParentItem(), EmporiumEntry.Instance.trashcan) && Areas().Distinct().Count() == 4,
                    "The four native receipt destinations are unavailable or ambiguous: front=" + (front != null) +
                    ", trash=" + (trash != null) + ", showcase=" + (showcase != null) + ", stock=" + (stock != null) +
                    ", trashcan=" + (EmporiumEntry.Instance.trashcan?.identifier ?? "null") +
                    ", trashParent=" + (trash?.GetParentItem()?.identifier ?? "null") +
                    ", distinct=" + Areas().Distinct().Count());
                GameItem decoyTrashcan = DirectoryMaster.Item("trashcan");
                decoyTrash = decoyTrashcan?.contentWindow?.childElement as GameGridInventory;
                Require(decoyTrash != null && ReferenceEquals(EmporiumEntry.Instance.trashcanInvElement, decoyTrash) &&
                    !ReferenceEquals(decoyTrash, trash),
                    "Could not reproduce the overwritten native trashcan inventory reference");
                other = PreBuiltItemHelper.CreatePaperWithText("existing paper " + token, "QA existing paper");
                PlayerStore.Instance.AddDirectSellingItemToTable(other, isOwend: true);
                Require(other.parentInventory == front, "Could not place the unrelated paper on the front counter");
                Configure(2);
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
                var before = Snapshot();
                SetEstimate(1);
                InspectionUIManager.Instance.OnAppraisalServiceEstimated();
                Require(PlayerStore.Instance.playerCash == cashBefore && client.clientImpatience > 0 &&
                    !((GameItemElement)appraisal).IsDestroyed() && Created(before).Length == 0 && other.parentInventory == front,
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
                var before = Snapshot();
                InspectionUIManager.Instance.OnInspectItem(appraisal);
                InspectionUIManager.Instance.UpdateAppraisalPanel();
                SetEstimate(estimate);
                int expectedFee = Fee(estimate);
                cashBefore = PlayerStore.Instance.playerCash;
                InspectionUIManager.Instance.OnAppraisalServiceEstimated();
                Require(PlayerStore.Instance.playerCash - cashBefore == expectedFee, "Native accepted appraisal did not pay exactly once");
                Require(((GameItemElement)appraisal).IsDestroyed(), "Native accepted appraisal item was not consumed");
                RequireDelivery(showcase, before, "accepted native appraisal");
                Next(3, "Native appraisal accepted, paid once and delivered its new receipt to the showcase"); return false;
            }
            case 3:
            {
                if (DialogUIManager.Instance?.currentDialog != null)
                {
                    DialogUIManager.Instance.OnAdvancePressed(); return false;
                }
                Fill(front, frontFillers);
                Require(NoRoom(front), "QA front counter did not fill");
                var before = Snapshot();
                int beforeCash = PlayerStore.Instance.playerCash;
                AppraisalServiceHelper.GiveAppraisalFee(201, 1, 201);
                Require(PlayerStore.Instance.playerCash - beforeCash == Fee(201), "F24 changed or repeated the fee with a full front counter");
                RequireDelivery(showcase, before, "full front counter");
                Clear(frontFillers);
                Fill(showcase, targetFillers);
                Require(NoRoom(showcase), "QA showcase did not fill");
                before = Snapshot();
                beforeCash = PlayerStore.Instance.playerCash;
                AppraisalServiceHelper.GiveAppraisalFee(202, 1, 202);
                Require(PlayerStore.Instance.playerCash - beforeCash == Fee(202), "F24 changed or repeated the fee for a full showcase");
                RequireDelivery(front, before, "full showcase fallback");
                Fill(front, frontFillers);
                Require(NoRoom(front), "QA front counter did not fill for the double-full fallback");
                before = Snapshot();
                beforeCash = PlayerStore.Instance.playerCash;
                AppraisalServiceHelper.GiveAppraisalFee(204, 1, 204);
                Require(PlayerStore.Instance.playerCash - beforeCash == Fee(204) && other.parentInventory == front &&
                    Created(before).Length == 0, "Double-full fallback duplicated payment, added a receipt, or changed unrelated paper");
                Clear(frontFillers); Clear(targetFillers);
                Next(4, "Full front, full showcase and double-full fallback retained the native payment semantics"); return false;
            }
            case 4:
            {
                CheckDelivery(0, front, 211);
                Require(ReferenceEquals(EmporiumEntry.Instance.trashcanInvElement, decoyTrash),
                    "QA trashcan inventory reference changed before the regression check");
                CheckDelivery(1, trash, 212);
                Require(decoyTrash.childItems.Count == 0, "Receipt was delivered to the other trashcan");
                CheckDelivery(2, showcase, 213);
                CheckDelivery(3, stock, 214);
                Configure(2);
                var harmony = new Harmony("qa.receipt-destination");
                var method = AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.AddedItemToDisplayCase));
                Require(method != null, "Showcase callback unavailable");
                harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(ReceiptScenario), nameof(Noop))));
                try
                {
                    var before = Snapshot();
                    int beforeCash = PlayerStore.Instance.playerCash;
                    AppraisalServiceHelper.GiveAppraisalFee(215, 1, 215);
                    Require(PlayerStore.Instance.playerCash - beforeCash == Fee(215), "Unknown target patch changed the appraisal fee");
                    RequireDelivery(front, before, "unknown showcase callback fallback");
                }
                finally { harmony.UnpatchAll(harmony.Id); }
                result = "PASS native appraisal rejection and acceptance, four configured receipt destinations, full and unknown-target fallbacks, and unrelated paper preservation";
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
    private GameGridInventory[] Areas() => new[] { front, trash, showcase, stock };
    private Dictionary<GameGridInventory, HashSet<GameItem>> Snapshot() =>
        Areas().ToDictionary(area => area, area => new HashSet<GameItem>(area.childItems));
    private GameItem[] Created(Dictionary<GameGridInventory, HashSet<GameItem>> previous) =>
        Areas().SelectMany(area => area.childItems.Where(item => !previous[area].Contains(item) && item.identifier == "slip"))
            .Distinct().ToArray();
    private void RequireDelivery(GameGridInventory target, Dictionary<GameGridInventory, HashSet<GameItem>> previous, string stage)
    {
        GameItem[] slips = Created(previous);
        Require(slips.Length == 1 && ReferenceEquals(slips[0].parentInventory, target) && GeneralHelper.IsItemOwned(slips[0]) &&
            other.parentInventory == front, "Receipt location, ownership or unrelated paper changed during " + stage);
    }
    private void CheckDelivery(int mode, GameGridInventory target, int value)
    {
        Configure(mode);
        var before = Snapshot();
        int beforeCash = PlayerStore.Instance.playerCash;
        AppraisalServiceHelper.GiveAppraisalFee(value, 1, value);
        Require(PlayerStore.Instance.playerCash - beforeCash == Fee(value), "Receipt destination changed or repeated the appraisal fee");
        RequireDelivery(target, before, "destination " + mode);
    }
    private static void Configure(int mode)
    {
        var options = Options();
        var destination = options.Get(ModId + ":f24.destination_v2");
        string[] labels = { "f24.front", "f24.trash", "f24.showcase", "f24.stock" };
        Require(destination?.dropdown != null && destination.dropdown.options.Count == labels.Length &&
            labels.Select((label, index) => destination.dropdown.options[index].text == ModHelper.GetLocalized(ModId, label)).All(value => value),
            "Receipt destination settings do not show the four native targets");
        options.Get(ModId + ":f24.enabled").SetBool(true, true);
        destination.SetIndex(mode, true);
        options.Get("apply").button.onClick.Invoke();
        var setting = ModHelper.GetConfig(ModId).Get("f24.destination_v2") as ConfigEntry<int>;
        Require(setting != null && setting.Value == mode, "Receipt destination setting did not apply");
        StoreUIManager.Instance.gameOptionUIManager.CloseUI();
    }
    private static void Noop() { }
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
    private static void Clear(List<GameItem> items)
    {
        foreach (GameItem item in items) if (item.parentInventory != null) item.Destroy();
        items.Clear();
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
