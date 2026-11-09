using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Exercises the shortcuts beside the native computer, negotiation and appraisal panels.
public sealed class PanelToolsScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step;
    private float since = Time.realtimeSinceStartup;

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
                TradeScenario.SetOptions(true, "f23.enabled", "f28.enabled", "f34.enabled");
                TradeScenario.SetOptions(false, "f15.enabled", "f17.enabled", "f21.enabled");
                PCUIManager.Instance.OpenUI(); Advance(2); return false;
            case 2:
            {
                Button ledger = Find("ShopAssist_LedgerTools");
                if (ledger == null) return false;
                Need(Find("ShopAssist_RequestTools") == null, "Removed request shortcut appeared on the computer");
                Need(Hit(ledger), "Computer ledger shortcut is visually blocked");
                ledger.onClick.Invoke();
                Need(CustomUIManager.Instance.GetWindow(Mod + ".ledger")?.IsAlive == true, "Computer ledger shortcut did not open history");
                CustomUIManager.Instance.GetWindow(Mod + ".ledger").Close();
                PCUIManager.Instance.HideUI();
                var buyer = StoreClientList.CreateSpacerClientFood(); buyer.SetBudget(9000); buyer.isIntroduced = true;
                PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer);
                PlayerStore.Instance.isClientArrived = true;
                GameItem offer = DirectoryMaster.Item("cup_noodle"); GeneralHelper.SetItemOwned(offer, true);
                Put(offer, EmporiumEntry.Instance.backInvinvElement);
                NegociationUIManager.Instance.OpenUI(); Advance(3); return false;
            }
            case 3:
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
                Advance(4); return false;
            }
            case 4:
            {
                Button appraisal = Find("ShopAssist_AppraisalWorksheet");
                if (appraisal == null) return false;
                Need(Hit(appraisal), "Appraisal shortcut is visually blocked");
                appraisal.onClick.Invoke();
                Transform worksheet = InspectionUIManager.Instance.panel.transform.Find("ShopAssist_AppraisalChoices");
                Need(worksheet != null && worksheet.gameObject.activeInHierarchy,
                    "Appraisal shortcut did not show its embedded worksheet");
                result = "PASS native visible computer ledger, customer basket and appraisal worksheet shortcuts";
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
        var raycaster = canvas != null ? canvas.GetComponent<GraphicRaycaster>() : null;
        Camera camera = raycaster != null ? raycaster.eventCamera :
            canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, (corners[0] + corners[2]) / 2f);
        var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        bool inScreen = point.x >= 0 && point.y >= 0 && point.x <= Screen.width && point.y <= Screen.height;
        bool hit = inScreen && hits.Count > 0 && (hits[0].gameObject == button.gameObject || hits[0].gameObject.transform.IsChildOf(button.transform));
        if (!hit) Debug.Log("[PLAYER-QA] Panel tool " + button.name + " at " + point + " hit=" +
            string.Join(" | ", hits.Take(8).Select(value => value.gameObject.name + " via " + value.module.name)) +
            " screen=" + Screen.width + "x" + Screen.height + " button=" + Geometry(rect) +
            " parent=" + Geometry(rect.parent as RectTransform) +
            " inspection=" + Geometry(InspectionUIManager.Instance?.panel?.transform as RectTransform) +
            " appraisal=" + Geometry(InspectionUIManager.Instance?.appraisalPanel?.transform as RectTransform));
        return hit;
    }
    private static string Geometry(RectTransform rect)
    {
        if (rect == null) return "<none>";
        var canvas = rect.GetComponentInParent<Canvas>();
        var raycaster = canvas != null ? canvas.GetComponent<GraphicRaycaster>() : null;
        Camera camera = raycaster != null ? raycaster.eventCamera :
            canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return rect.name + " screen=" + low + ".." + high + " local=" + rect.rect + " anchored=" + rect.anchoredPosition +
            " anchors=" + rect.anchorMin + ".." + rect.anchorMax + " scale=" + rect.lossyScale +
            " canvas=" + (canvas == null ? "<none>" : canvas.name + "/" + canvas.renderMode + "/" + canvas.pixelRect +
                "/root=" + canvas.rootCanvas.name + "/worldCamera=" + (canvas.worldCamera != null ? canvas.worldCamera.name : "<null>")) +
            " eventCamera=" + (camera != null ? camera.name : "<null>") +
            " raycaster=" + (raycaster != null ? raycaster.name : "<none>");
    }
    private void Advance(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
