using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// The guarded player verifies the actual native title buttons and item menu.
public sealed class ContextToolsScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItem first, second, bag;
    private int step;
    private float since = Time.realtimeSinceStartup;
    private float after;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 90) throw new InvalidOperationException("Context tools QA timed out at step " + step);
        switch (step)
        {
            case 0: if (start.Tick()) step++; return false;
            case 1:
                if (StoreUIManager.Instance?.gameOptionUIManager == null || EmporiumEntry.Instance?.invElement == null ||
                    ItemMouseDoubleClickHandler.current == null || EventSystem.current == null ||
                    StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                if (StoreUIManager.Instance.controlHintOverlay?.activeSelf == true) StoreUIManager.Instance.ToggleControlHintOverlay();
                TradeScenario.SetOptions(true, "f05.enabled", "f10.enabled", "f24.enabled", "f31.enabled", "f32.enabled");
                TradeScenario.SetOptions(false, "f15.enabled", "f17.enabled", "f21.enabled");
                first = NewItem("hydroponic", "QA Context A"); second = NewItem("hydroponic", "QA Context B");
                bag = NewItem("backpack_medium", "QA Receipt Box");
                Put(first); Put(second); Put(bag);
                ItemMouseDoubleClickHandler.current.OpenContentAction(first, new Vector2(190, 300));
                ItemMouseDoubleClickHandler.current.OpenContentAction(second, new Vector2(Screen.width - 190, 300));
                Advance(2); return false;
            case 2:
            {
                MachineryButton firstButton = ToolButton(first.contentWindow);
                if (firstButton == null || ToolButton(second.contentWindow) == null || ToolButton(EmporiumEntry.Instance.invWindow) == null) return false;
                first.contentWindow.ToFront();
                Capture("qa-context-precheck.png"); after = Time.realtimeSinceStartup + 0.3f; Advance(19); return false;
            }
            case 19:
            {
                if (Time.realtimeSinceStartup < after) return false;
                MachineryButton firstButton = ToolButton(first.contentWindow);
                Need(firstButton != null, "Machine title tools button disappeared");
                Need(HitMachine(firstButton), "Machine title tools button is not reachable");
                firstButton.Select();
                var card = Card();
                Need(card.Get("f32.open")?.button != null && card.Get("f31.open")?.button != null,
                    "Hydroponic card lacks its actual supported controls");
                Capture("qa-context-machine.png"); after = Time.realtimeSinceStartup + 0.5f; Advance(20); return false;
            }
            case 20:
            {
                if (Time.realtimeSinceStartup < after) return false;
                Card().Get("f32.open").button.onClick.Invoke();
                var replant = CustomUIManager.Instance.GetWindow(Mod + ".f32");
                Need(replant?.IsAlive == true && Text(replant).Contains(first.GetDisplayName(true)),
                    "Machine card opened replant for a different machine");
                replant.Close();
                second.contentWindow.ToFront(); Advance(3); return false;
            }
            case 3:
            {
                MachineryButton secondButton = ToolButton(second.contentWindow);
                Need(secondButton != null && HitMachine(secondButton), "Second machine title tools button is not reachable");
                secondButton.Select();
                Card().Get("f31.open").button.onClick.Invoke();
                var maintenance = CustomUIManager.Instance.GetWindow(Mod + ".maintenance");
                Need(maintenance?.IsAlive == true && Text(maintenance).Contains(second.GetDisplayName(true)),
                    "Machine card opened maintenance for a different machine");
                maintenance.Close();
                EmporiumEntry.Instance.invWindow.ToFront(); Advance(4); return false;
            }
            case 4:
            {
                MachineryButton stock = ToolButton(EmporiumEntry.Instance.invWindow);
                Need(stock != null && HitMachine(stock), "Warehouse title tools button is not reachable");
                stock.Select(); Need(Card().Get("f05.open")?.button != null, "Warehouse card lacks arrange action");
                Capture("qa-context-warehouse.png"); after = Time.realtimeSinceStartup + 0.5f; Advance(40); return false;
            }
            case 40:
            {
                if (Time.realtimeSinceStartup < after) return false;
                Card().Close();
                StoreUIManager.Instance.gameOptionUIManager.OpenUI(); Advance(5); return false;
            }
            case 5:
            {
                MachineryButton stock = ToolButton(EmporiumEntry.Instance.invWindow);
                if (stock == null || !stock.isDisabled) return false;
                StoreUIManager.Instance.gameOptionUIManager.CloseUI(); Advance(6); return false;
            }
            case 6:
            {
                var stock = ToolButton(EmporiumEntry.Instance.invWindow);
                if (stock == null || stock.isDisabled) return false;
                var menu = OpenItemMenu(bag);
                var node = QaItemUi.Descendant(menu.handler.transform, "ShopAssist_f24_choose");
                Need(node != null && node.gameObject.activeInHierarchy, "Owned container has no receipt destination context action");
                RichTextElement action = node.GetComponent<TreeNodeRender>()?.element as RichTextElement;
                Need(action != null, "Receipt context row is not a native selectable");
                typeof(ItemContextHandler).GetMethod("TriggerButton", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ItemContextHandler.current, new object[] { action });
                string raw = ModHelper.GetModData(Mod, "f24.target", "");
                Need(bag.uniqueId > 0 && raw.Contains("\"owner\":" + bag.uniqueId),
                    "Receipt context action did not bind the selected container");
                result = "PASS native warehouse and machine title raycasts, per-machine replant and battery targeting, modal blocking, and container receipt context action";
                return true;
            }
            default: throw new InvalidOperationException("Unknown context tools step");
        }
    }

    private static GameItem NewItem(string id, string name)
    {
        GameItem item = DirectoryMaster.Item(id); Need(item != null, "Missing fixture " + id);
        GeneralHelper.SetItemOwned(item, true); GeneralHelper.SetCustomName(item, name); return item;
    }
    private static void Put(GameItem item)
    {
        GameGridInventory inventory = EmporiumEntry.Instance.invElement;
        SlotMarker marker = inventory.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory,
            "Could not place context fixture");
    }
    private static MachineryButton ToolButton(PixelWindow window) =>
        window?.handler?.transform.Find("ShopAssist_WindowTools")?.GetComponent<TreeNodeRender>()?.element as MachineryButton;
    private static CustomUIWindow Card() => CustomUIManager.Instance.GetWindow(Mod + ".context") ?? throw new InvalidOperationException("Context card is closed");
    private static string Text(CustomUIWindow window) => string.Join(" ", window.root.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text));
    private static bool HitMachine(MachineryButton button)
    {
        var plate = button.handler.GetComponentsInChildren<TreeNodeRender>(true).FirstOrDefault(node => node != button.handler &&
            node.image != null && node.image.isActiveAndEnabled && node.image.raycastTarget && node.rectTransform.rect.width > 1);
        if (plate == null) return false;
        var canvas = plate.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var corners = new Vector3[4]; plate.rectTransform.GetWorldCorners(corners);
        Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, (corners[0] + corners[2]) / 2f);
        var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        var target = RenderHandler.RaycastElement<SelectableElement>(point);
        if (!ReferenceEquals(target, button)) Debug.Log("[PLAYER-QA] tools hit=" + (target?.handler?.name ?? "null") + " at " + point +
            " button=" + PathOf(button.handler.transform) + " hits=" + string.Join(" | ", hits.Take(6).Select(hit =>
                PathOf(hit.gameObject.transform) + ":" + hit.gameObject.GetComponent<TreeNodeRender>()?.FindParentElement()?.GetType().Name)));
        return ReferenceEquals(target, button);
    }
    private static string PathOf(Transform node)
    {
        string path = "";
        for (int i = 0; node != null && i < 8; i++, node = node.parent) path = node.name + "/" + path;
        return path;
    }
    private static PixelWindow OpenItemMenu(GameItem item)
    {
        var handler = ItemContextHandler.current;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(ItemContextHandler);
        var menu = (PixelWindow)type.GetField("contextWindow", flags).GetValue(handler);
        type.GetField("currentInventory", flags).SetValue(handler, null);
        type.GetField("currentItem", flags).SetValue(handler, item);
        type.GetMethod("UpdateConditions", flags).Invoke(handler, null);
        type.GetMethod("EqualizeButtonWidths", flags).Invoke(handler, null);
        menu.Validate(); menu.Show(new Vector2(Screen.width / 2, Screen.height / 2), Vector2.zero);
        type.GetMethod("OnEventPress", flags).Invoke(handler, new object[] { new HashSet<KeyCode>() });
        return menu;
    }
    private static void Capture(string filename)
    {
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, filename));
    }
    private void Advance(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
