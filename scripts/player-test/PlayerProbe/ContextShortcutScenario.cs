using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
                    ItemMouseDoubleClickHandler.current == null || EventSystem.current == null) return false;
                if (StoreUIManager.Instance.blackscreen != null && StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy) return false;
                if (StoreUIManager.Instance.controlHintOverlay != null && StoreUIManager.Instance.controlHintOverlay.activeSelf)
                    StoreUIManager.Instance.ToggleControlHintOverlay();
                TradeScenario.SetOptions(true, "f10.enabled");
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
                Action tick = ModuleTick("Context buttons");
                tick();
                GameObject overlay = StoreUIManager.Instance.controlHintOverlay;
                Require(overlay != null && !overlay.activeSelf, "Hint overlay is unavailable for the interaction test");
                try
                {
                    overlay.SetActive(true); tick();
                    Require(button.isDisabled, "The shortcut remained enabled after an overlay opened in the same frame");
                    overlay.SetActive(false); tick();
                    Require(!button.isDisabled, "The shortcut remained disabled after an overlay closed in the same frame");
                }
                finally { overlay.SetActive(false); tick(); }
                RectTransform windowRect = first.contentWindow.handler.rectTransform;
                Vector2 original = windowRect.anchoredPosition;
                try
                {
                    tick(); Vector2 before = button.handler.rectTransform.anchoredPosition;
                    windowRect.anchoredPosition = original + new Vector2(Screen.width * 2, 0);
                    Canvas.ForceUpdateCanvases(); tick();
                    Require(button.handler.rectTransform.anchoredPosition != before,
                        "The shortcut did not follow a moved window in the same frame");
                }
                finally { windowRect.anchoredPosition = original; Canvas.ForceUpdateCanvases(); tick(); }
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
                result = "PASS native raycasts and actions for two machine buttons and their removal when disabled";
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
    internal static Action ModuleTick(string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var mod = ModLoader.Mods.FirstOrDefault(value => value.Manifest?.ID == Mod)?.Instance;
        var modules = mod?.GetType().GetField("modules", flags)?.GetValue(mod) as IEnumerable;
        if (modules != null)
            foreach (object module in modules)
            {
                Type type = module.GetType();
                if ((string)type.GetField("Name", flags)?.GetValue(module) == name &&
                    type.GetField("Tick", flags)?.GetValue(module) is Action tick) return tick;
            }
        throw new InvalidOperationException("Missing module tick: " + name);
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
    private void Advance(int value) { step = value; since = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
