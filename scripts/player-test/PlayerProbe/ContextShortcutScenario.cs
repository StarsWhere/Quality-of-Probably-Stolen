using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Exercises the visible targets using Unity's real GraphicRaycaster in an isolated run.
public sealed class ContextShortcutScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem first, second;
    private int step;
    private float since = Time.realtimeSinceStartup;
    private static string notice;
    private static void RecordNotice(string message) { notice = message; }

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 75) throw new InvalidOperationException("Context shortcut QA timed out at step " + step);
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                Advance(1); return false;
            case 1:
                if (StoreUIManager.Instance?.gameOptionUIManager == null || EmporiumEntry.Instance?.invElement == null ||
                    ItemMouseDoubleClickHandler.current == null || NegociationUIManager.Instance == null || EventSystem.current == null) return false;
                if (StoreUIManager.Instance.blackscreen != null && StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy) return false;
                if (StoreUIManager.Instance.controlHintOverlay != null && StoreUIManager.Instance.controlHintOverlay.activeSelf)
                    StoreUIManager.Instance.ToggleControlHintOverlay();
                TradeScenario.SetOptions(true, "f10.enabled", "f11.enabled");
                first = NewMachine("QA Shortcut A"); second = NewMachine("QA Shortcut B");
                Put(EmporiumEntry.Instance.invElement, first); Put(EmporiumEntry.Instance.invElement, second);
                ItemMouseDoubleClickHandler.current.OpenContentAction(first, new Vector2(180, 300));
                ItemMouseDoubleClickHandler.current.OpenContentAction(second, new Vector2(Screen.width - 180, 300));
                Require(first.contentWindow.IsVisible() && second.contentWindow.IsVisible(), "Machine windows did not open");
                first.contentWindow.ToFront();
                Advance(2); return false;
            case 2:
            {
                MachineryButton button = MachineButton(first);
                if (button == null) return false;
                Require(MachineButton(second) != null, "The second open machine has no independent shortcut");
                Require(HitMachine(button), "The first machine shortcut is not the top native raycast target");
                button.Select();
                Require(BrowserText().Contains(first.GetDisplayName(true)), "First machine button opened the wrong browser");
                CustomUIManager.Instance.GetWindow(Mod + ".f10").Close();
                second.contentWindow.ToFront();
                Advance(3); return false;
            }
            case 3:
            {
                MachineryButton button = MachineButton(second);
                if (button == null) return false;
                Require(HitMachine(button), "The second machine shortcut is not the top native raycast target");
                button.Select();
                Require(BrowserText().Contains(second.GetDisplayName(true)), "Second machine button opened the wrong browser");
                CustomUIManager.Instance.GetWindow(Mod + ".f10").Close();
                TradeScenario.SetOptions(false, "f10.enabled");
                Advance(4); return false;
            }
            case 4:
                if (MachineButton(first) != null || MachineButton(second) != null) return false;
                PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateSpacerClientFood());
                PlayerStore.Instance.isClientArrived = true;
                NegociationUIManager.Instance.OpenUI();
                Advance(5); return false;
            case 5:
            {
                Button button = CustomerButton();
                if (button == null || !button.gameObject.activeInHierarchy) return false;
                Require(button.interactable, "Customer shortcut is disabled when the buyer is active");
                Require(InViewport((RectTransform)button.transform), "Customer shortcut is outside the viewport");
                Require(HitUI(button.gameObject), "Customer shortcut is blocked in the raycast stack");
                StoreUIManager.Instance.StartTyping();
                Advance(6); return false;
            }
            case 6:
            {
                Button button = CustomerButton();
                if (button == null || button.interactable) return false;
                StoreUIManager.Instance.StopTyping();
                Advance(7); return false;
            }
            case 7:
            {
                Button button = CustomerButton();
                if (button == null || !button.interactable) return false;
                Require(HitUI(button.gameObject), "Customer shortcut became obstructed after typing");
                button.onClick.Invoke();
                var list = CustomUIManager.Instance.GetWindow(Mod + ".customer_inventory");
                Require(list?.IsAlive == true, "Customer shortcut did not open the demand list");
                list.Close();
                var tablet = NegociationUIManager.Instance.uiObject.GetComponent<RectTransform>();
                tablet.anchoredPosition += new Vector2(Screen.width * 0.4f, 0f);
                Advance(8); return false;
            }
            case 8:
            {
                Button button = CustomerButton();
                if (button == null) return false;
                Require(InViewport((RectTransform)button.transform), "Customer shortcut left the viewport when the tablet moved");
                PlayerStore.Instance.currentClientInstance = null;
                PlayerStore.Instance.isClientArrived = false;
                Advance(9); return false;
            }
            case 9:
            {
                Button button = CustomerButton();
                if (button == null || !button.interactable) return false;
                const string patchId = "qa.customer-no-demand";
                var harmony = new Harmony(patchId); notice = null;
                try
                {
                    var show = AccessTools.TypeByName("ShopAssist.ShopNotice")?.GetMethod("Show", BindingFlags.Static | BindingFlags.NonPublic);
                    Require(show != null, "Notice handler is unavailable");
                    harmony.Patch(show, prefix: new HarmonyMethod(typeof(ContextShortcutScenario).GetMethod(nameof(RecordNotice), BindingFlags.Static | BindingFlags.NonPublic)));
                    button.onClick.Invoke();
                }
                finally { harmony.UnpatchAll(patchId); }
                Require(notice == ModHelper.GetLocalized(Mod, "f11.no_demand"), "Customer shortcut did not explain the missing demand");
                PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateSpacerClientFood());
                PlayerStore.Instance.isClientArrived = true;
                NegociationUIManager.Instance.OpenUI();
                Advance(10); return false;
            }
            case 10:
                if (CustomerButton() == null) return false;
                TradeScenario.SetOptions(false, "f11.enabled");
                Advance(11); return false;
            case 11:
                if (CustomerButton() != null) return false;
                result = "PASS native raycasts and actions for two machine buttons and customer demand; typing, viewport, customer change and disable";
                return true;
            default: throw new InvalidOperationException("Unknown context shortcut QA step");
        }
    }

    private static GameItem NewMachine(string name)
    {
        GameItem item = DirectoryMaster.Item("hydroponic");
        Require(item != null, "Native hydroponic machine unavailable");
        GeneralHelper.SetItemOwned(item, true);
        GeneralHelper.SetCustomName(item, name);
        return item;
    }
    private static void Put(GameInventory inventory, GameItem item)
    {
        SlotMarker slot = inventory.TryFindOneValidInventorySlot(item);
        Require(slot != null && slot.inventory == inventory && slot.targetItem == null && slot.numTransfer >= item.unitCount &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory, "Could not place test machine");
    }
    private static MachineryButton MachineButton(GameItem item)
    {
        var node = item?.contentWindow?.handler?.transform.Find("ShopAssist_Action_f10_open");
        if (node == null || !node.gameObject.activeInHierarchy) return null;
        return node.GetComponent<TreeNodeRender>()?.element as MachineryButton;
    }
    private static string BrowserText()
    {
        var browser = CustomUIManager.Instance.GetWindow(Mod + ".f10");
        Require(browser?.IsAlive == true, "Module browser did not open");
        return string.Join(" ", browser.root.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text));
    }
    private static Button CustomerButton()
    {
        var root = NegociationUIManager.Instance?.uiObject;
        if (root == null) return null;
        var node = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(child => child.name == "ShopAssist_CustomerList" && child.gameObject.activeInHierarchy);
        return node?.GetComponentInChildren<Button>(true);
    }
    private static Camera CameraFor(RectTransform rect)
    {
        var raycaster = rect.GetComponentInParent<GraphicRaycaster>();
        if (raycaster != null) return raycaster.eventCamera;
        var canvas = rect.GetComponentInParent<Canvas>();
        return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
    }
    private static Vector2 Center(RectTransform rect)
    {
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        return RectTransformUtility.WorldToScreenPoint(CameraFor(rect), (corners[0] + corners[2]) / 2f);
    }
    private static bool HitMachine(MachineryButton button)
    {
        var plate = button.handler.GetComponentsInChildren<TreeNodeRender>(true).FirstOrDefault(node =>
            node != button.handler && node.image != null && node.image.isActiveAndEnabled && node.image.raycastTarget &&
            node.rectTransform.rect.width > 1f && node.rectTransform.rect.height > 1f);
        if (plate == null) { Debug.Log("[PLAYER-QA] Machine shortcut has no interactive plate"); return false; }
        Vector2 point = Center(plate.rectTransform);
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        var actual = RenderHandler.RaycastElement<SelectableElement>(point);
        if (!ReferenceEquals(actual, button))
            Debug.Log("[PLAYER-QA] machine hit point=" + point + " plate=" + plate.rectTransform.rect +
                " root=" + button.rectTransform.rect + " screen=" + Screen.width + "x" + Screen.height +
                " target=" + (actual == null ? "<null>" : actual.handler.gameObject.name) +
                " hits=" + string.Join(" | ", hits.Take(6).Select(hit => hit.gameObject.name + " via " + hit.module.name)));
        return ReferenceEquals(actual, button);
    }
    private static bool HitUI(GameObject button)
    {
        var rect = button.transform as RectTransform;
        var events = new PointerEventData(EventSystem.current) { position = Center(rect) };
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(events, hits);
        bool found = hits.Count != 0 && (hits[0].gameObject == button || hits[0].gameObject.transform.IsChildOf(button.transform));
        if (!found)
        {
            var parents = new List<string>();
            for (Transform node = button.transform; node != null && parents.Count < 12; node = node.parent)
                parents.Add(node.name + (node.GetComponent<RectMask2D>() != null ? "[mask]" : ""));
            Debug.Log("[PLAYER-QA] customer hit point=" + events.position + " rect=" + rect.rect +
                " screen=" + Screen.width + "x" + Screen.height + " parents=" + string.Join("/", parents) +
                " hits=" + string.Join(" | ", hits.Take(8).Select(hit => hit.gameObject.name + " via " + hit.module.name)));
        }
        return found;
    }
    private static bool InViewport(RectTransform rect)
    {
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        return corners.All(corner =>
        {
            Vector2 point = RectTransformUtility.WorldToScreenPoint(CameraFor(rect), corner);
            return point.x >= 0 && point.y >= 0 && point.x <= Screen.width && point.y <= Screen.height;
        });
    }
    private void Advance(int value) { step = value; since = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
