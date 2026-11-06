using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Exercises the shortcuts beside the native computer, coordinator and customer panels.
public sealed class PanelToolsScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step;
    private float since = Time.realtimeSinceStartup;
    private GameItem coordinator;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 90f) throw new InvalidOperationException("Panel tools QA timed out at step " + step);
        switch (step)
        {
            case 0: if (start.Tick()) step++; return false;
            case 1:
                if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || PCUIManager.Instance == null ||
                    InspectionUIManager.Instance == null || NegociationUIManager.Instance == null || EventSystem.current == null ||
                    StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                if (StoreUIManager.Instance.controlHintOverlay?.activeSelf == true) StoreUIManager.Instance.ToggleControlHintOverlay();
                TradeScenario.SetOptions(true, "f11.enabled", "f13.enabled", "f23.enabled", "f27.enabled", "f28.enabled", "f34.enabled");
                TradeScenario.SetOptions(false, "f15.enabled", "f17.enabled", "f21.enabled");
                coordinator = DirectoryMaster.Item("storage_coordinator");
                Need(coordinator != null, "Missing coordinator"); GeneralHelper.SetItemOwned(coordinator, true);
                Put(coordinator, EmporiumEntry.Instance.invElement);
                Need(ReferenceEquals(MachineStorageCoordinator.FindCoordinatorInReach(), coordinator), "Coordinator is not reachable");
                PCUIManager.Instance.OpenUI(); Advance(2); return false;
            case 2:
            {
                Button requests = Find("ShopAssist_RequestTools"), ledger = Find("ShopAssist_LedgerTools");
                if (requests == null || ledger == null) return false;
                Need(Hit(requests) && Hit(ledger), "Computer shortcuts are visually blocked");
                requests.onClick.Invoke();
                Need(CustomUIManager.Instance.GetWindow(Mod + ".requests")?.IsAlive == true, "Computer request shortcut did not open requests");
                CustomUIManager.Instance.GetWindow(Mod + ".requests").Close();
                ledger.onClick.Invoke();
                Need(CustomUIManager.Instance.GetWindow(Mod + ".ledger")?.IsAlive == true, "Computer ledger shortcut did not open history");
                CustomUIManager.Instance.GetWindow(Mod + ".ledger").Close();
                PCUIManager.Instance.HideUI(); MachineStorageCoordinator.OpenWindow(coordinator); Advance(3); return false;
            }
            case 3:
            {
                Button groups = Find("ShopAssist_CoordinatorTools");
                if (groups == null) return false;
                Need(Hit(groups), "Coordinator shortcut is visually blocked");
                groups.onClick.Invoke();
                Need(CustomUIManager.Instance.GetWindow("shopassist.f13.groups")?.IsAlive == true, "Coordinator shortcut did not open groups");
                CustomUIManager.Instance.GetWindow("shopassist.f13.groups").Close();
                CustomUIManager.Instance.GetWindow("storage_coordinator")?.Close();
                var buyer = StoreClientList.CreateSpacerClientFood(); buyer.SetBudget(9000); buyer.isIntroduced = true;
                PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer);
                PlayerStore.Instance.isClientArrived = true;
                GameItem offer = DirectoryMaster.Item("cup_noodle"); GeneralHelper.SetItemOwned(offer, true);
                Put(offer, EmporiumEntry.Instance.backInvinvElement);
                NegociationUIManager.Instance.OpenUI(); Advance(4); return false;
            }
            case 4:
            {
                Button basket = Find("ShopAssist_CustomerBasket");
                if (basket == null) return false;
                Need(Hit(basket), "Customer basket shortcut is visually blocked");
                basket.onClick.Invoke();
                Need(CustomUIManager.Instance.GetWindow(Mod + ".basket")?.IsAlive == true, "Customer shortcut did not open basket");
                CustomUIManager.Instance.GetWindow(Mod + ".basket").Close();
                NegociationUIManager.Instance.CloseUI();
                StoreClient appraisal = StoreClientListAppraisal.CreateAppraisal1();
                PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(appraisal);
                PlayerStore.Instance.isClientArrived = true;
                GameItem item = PreBuiltItemHelper.CreateHandRandom(); AppHandHelper.InitDefault(item);
                InspectionUIManager.Instance.OnInspectItem(item); InspectionUIManager.Instance.UpdateAppraisalPanel();
                Need(InspectionUIManager.Instance.appraisalPanel.activeInHierarchy, "Native appraisal did not open");
                Advance(5); return false;
            }
            case 5:
            {
                Button appraisal = Find("ShopAssist_AppraisalTools");
                if (appraisal == null) return false;
                Need(Hit(appraisal), "Appraisal shortcut is visually blocked");
                appraisal.onClick.Invoke();
                Need(CustomUIManager.Instance.GetWindow("shopassist.f23.worksheet")?.IsAlive == true,
                    "Appraisal shortcut did not open worksheet");
                result = "PASS native visible computer requests and ledger, coordinator groups, customer basket and appraisal worksheet shortcuts";
                return true;
            }
            default: throw new InvalidOperationException("Unknown panel tools step");
        }
    }

    private static void Put(GameItem item, GameInventory inventory)
    {
        SlotMarker marker = inventory.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory, "Fixture placement failed");
    }
    private static Button Find(string name) => Resources.FindObjectsOfTypeAll<Transform>()
        .FirstOrDefault(item => item.name == name && item.gameObject.scene.IsValid() && item.gameObject.activeInHierarchy)
        ?.GetComponentInChildren<Button>(true);
    private static bool Hit(Button button)
    {
        var rect = button.transform as RectTransform;
        var canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, (corners[0] + corners[2]) / 2f);
        var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        bool hit = hits.Count > 0 && (hits[0].gameObject == button.gameObject || hits[0].gameObject.transform.IsChildOf(button.transform));
        if (!hit) Debug.Log("[PLAYER-QA] Panel tool " + button.name + " at " + point + " hit=" +
            string.Join(" | ", hits.Take(8).Select(value => value.gameObject.name + " via " + value.module.name)));
        return hit;
    }
    private void Advance(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
