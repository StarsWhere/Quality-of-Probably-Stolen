using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Uses the guarded Player's virtual mouse, actual event system and native grid raycasts.
public sealed class StockSearchDragInputScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 110f;
    private readonly string query = "QA search drag " + Guid.NewGuid().ToString("N").Substring(0, 8);
    private QaSyntheticInput input;
    private GameItem sourceBag, targetBag, item;
    private GameGridInventory source, target;
    private CustomUIWindow search;
    private MonoBehaviour hit;
    private Vector2 press, drop;
    private GridShape destination, original;
    private int count, step, readyFrame;
    private float waitUntil;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Stock-search drag timed out at step " + step);
        if (Time.frameCount < readyFrame) return false;
        switch (step)
        {
            case 0:
                if (start.Tick()) Next(1);
                return false;
            case 1:
            {
                var e = EmporiumEntry.Instance;
                if (e?.invElement == null || e.invWindow?.IsVisible() != true ||
                    ItemMouseDoubleClickHandler.current == null || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
                if (StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
                if (NegociationUIManager.Instance?.IsOpen == true) NegociationUIManager.Instance.CloseUI();
                CustomUIManager.Instance.GetWindow(Mod + ".todo")?.Close();
                PlayerStore.Instance.currentClientInstance = null; PlayerStore.Instance.isClientArrived = false;
                TradeScenario.SetOptions(true, "f11.enabled");
                TradeScenario.SetOptions(false, "f03.enabled", "f04.enabled", "f11.purchase", "f34.enabled");
                sourceBag = New("backpack_medium"); targetBag = New("backpack_medium"); item = New("wine_yeast");
                GeneralHelper.SetCustomName(item, query);
                Put(e.invElement, sourceBag); Put(e.invElement, targetBag);
                source = sourceBag.contentWindow?.childElement as GameGridInventory;
                target = targetBag.contentWindow?.childElement as GameGridInventory;
                Need(source != null && target != null, "Ordinary source or destination grid is missing");
                Put(source, item); original = item.modifiedShape.Clone(); count = item.unitCount;
                sourceBag.contentWindow.Hide();
                ItemMouseDoubleClickHandler.current.OpenContentAction(targetBag, new Vector2(Screen.width - 220, 180));
                input = new QaSyntheticInput(keyboard: true, mouseButtons: true) { Mouse = new Vector3(-100, -100) };
                Next(2, 5); return false;
            }
            case 2:
            {
                Need(targetBag.contentWindow.IsVisible() && !sourceBag.contentWindow.IsVisible(), "Inventory fixtures are not in the expected visibility state");
                var action = QaItemUi.WindowAction(EmporiumEntry.Instance.invWindow, "f11.open");
                if (action == null || action.isDisabled) return false;
                action.Select();
                search = CustomUIManager.Instance.GetWindow(Mod + ".customer_inventory");
                Need(search?.IsAlive == true, "Stock search did not open");
                search.Rect.anchoredPosition = new Vector2(-180, 0);
                var field = search.Get("query"); Need(field?.input != null, "Search field is missing");
                field.SetString(query, true); field.input.onEndEdit.Invoke(query);
                Next(3, 5); return false;
            }
            case 3:
                PreparePress(); CheckDragHint(); Press(); Next(4); return false;
            case 4:
                input.MouseDown = -1; input.Mouse = press + new Vector2(12, 12); Next(5, 3); return false;
            case 5:
                Need(Field(hit, "ghost") is GameObject && ReferenceEquals(item.parentInventory, source), "The virtual mouse did not start a card drag");
                GeneralHelper.SetCustomName(item, query + " changed");
                drop = FindFreeCell(); input.Mouse = drop; waitUntil = Time.unscaledTime + .8f; Next(6); return false;
            case 6:
                if (Time.unscaledTime < waitUntil) return false;
                Need(QaItemUi.Hit(search.root, item) == hit && Field(hit, "ghost") is GameObject &&
                    ReferenceEquals(item.parentInventory, source), "Polling rebuilt or moved the active drag");
                Need(Preview(true), "A legal native cell did not show the green placement preview");
                item.EnableTag("ITEM_HIDDEN_TAG"); waitUntil = Time.unscaledTime + .4f; Next(7); return false;
            case 7:
                if (Time.unscaledTime < waitUntil) return false;
                Need(Preview(false), "A source hidden during dragging did not turn the native preview red");
                Release(); Next(8, 3); return false;
            case 8:
                ClearInput();
                Need(ReferenceEquals(item.parentInventory, source) && item.unitCount == count && Same(original, item.modifiedShape) &&
                    source.childItems.Count(value => ReferenceEquals(value, item)) == 1 && !target.childItems.Contains(item),
                    "A stale or hidden source was transferred");
                Need(HasChangedNotice(), "Rejected native drop did not show its failure feedback");
                Need(!PreviewExists(), "Native placement preview remained after the rejected drop");
                item.DisableTag("ITEM_HIDDEN_TAG"); waitUntil = Time.unscaledTime + .8f; Next(9); return false;
            case 9:
                if (Time.unscaledTime < waitUntil) return false;
                PreparePress(); Press(); Next(10); return false;
            case 10:
                input.MouseDown = -1; input.Mouse = press + new Vector2(12, 12); Next(11, 3); return false;
            case 11:
                Need(Field(hit, "ghost") is GameObject, "The refreshed result could not be dragged again");
                drop = FindFreeCell(); input.Mouse = drop; waitUntil = Time.unscaledTime + .4f; Next(12); return false;
            case 12:
                if (Time.unscaledTime < waitUntil) return false;
                Need(Preview(true), "The refreshed source was not accepted at a native empty cell");
                Release(); Next(13, 3); return false;
            case 13:
                ClearInput();
                Need(ReferenceEquals(item.parentInventory, target) && item.unitCount == count && Same(destination, item.modifiedShape) &&
                    !source.childItems.Contains(item) && target.childItems.Count(value => ReferenceEquals(value, item)) == 1,
                    "The drag did not place the same whole item exactly at the chosen native cell");
                Need(!sourceBag.contentWindow.IsVisible() && !PreviewExists(), "Dragging opened the source or left a preview behind");
                input.Dispose(); input = null;
                result = "PASS F11 virtual mouse drag to native exact cell, source revalidation, previews, rejection feedback and refresh safety";
                return true;
            default: throw new InvalidOperationException("Unknown stock-search drag step");
        }
    }

    private void PreparePress()
    {
        Canvas.ForceUpdateCanvases();
        hit = QaItemUi.Hit(search.root, item); Need(hit != null, "Search result is missing");
        var rect = (RectTransform)hit.transform;
        press = RectTransformUtility.WorldToScreenPoint(CameraFor(rect), rect.TransformPoint(rect.rect.center));
        Need(ReferenceEquals(TopHit(press), hit.gameObject), "Search card is not the actual topmost pointer target");
    }

    private void CheckDragHint()
    {
        var grid = hit.GetType().DeclaringType;
        var tooltip = grid?.GetMethod("Tooltip", Fields);
        var text = tooltip?.Invoke(null, new[] { Field(hit, "entry"), item as GameItemElement }) as RichText;
        string hint = ModHelper.GetLocalized(Mod, "f11.drag_tip");
        Need(!string.IsNullOrEmpty(hint) && text?.nakedText?.Contains(hint) == true,
            "The movable search result's native tooltip is missing its drag hint");
    }

    private Vector2 FindFreeCell()
    {
        Canvas.ForceUpdateCanvases();
        Vector2 grab = (Vector2)Field(hit, "grab");
        GridShape dragged = (GridShape)Field(hit, "dragShape");
        Need(dragged != null && target.rectTransform != null, "The native drop geometry is missing");
        var rect = target.rectTransform; float cell = UISettings.current.gridSize;
        for (int y = 0; y < target.inventoryShape.height; y++) for (int x = 0; x < target.inventoryShape.width; x++)
        {
            var pose = new GridShapeBuilder(dragged).SetPosition(x, y).Build();
            var slot = target.TryInventorySlot(item, count, pose);
            if (slot == null || slot.targetItem != null || slot.numTransfer < count || !Same(slot.itemGridShape, pose)) continue;
            var local = new Vector3(rect.rect.xMin + (x + grab.x + .5f) * cell, rect.rect.yMax - (y + grab.y + .5f) * cell);
            Vector2 point = RectTransformUtility.WorldToScreenPoint(CameraFor(rect), rect.TransformPoint(local));
            if (point.x <= 0 || point.y <= 0 || point.x >= Screen.width || point.y >= Screen.height) continue;
            if (TopHit(point)?.GetComponent<TreeNodeRender>()?.FindParentElement<GameGridInventory>() != target) continue;
            destination = pose; return point;
        }
        throw new InvalidOperationException("No unobstructed, native-raycastable empty target cell");
    }

    private bool PreviewExists() => target.rectTransform.Find("ShopAssist_NativeGridPreview")?.gameObject.activeInHierarchy == true;
    private bool Preview(bool allowed)
    {
        Transform preview = target.rectTransform.Find("ShopAssist_NativeGridPreview");
        return preview != null && preview.gameObject.activeInHierarchy && preview.GetComponentsInChildren<Image>(true)
            .Any(image => image.isActiveAndEnabled && !image.raycastTarget && (allowed ? image.color.g > image.color.r : image.color.r > image.color.g));
    }
    private static bool HasChangedNotice()
    {
        var assembly = ModLoader.Mods.First(value => value.Manifest?.ID == Mod).Instance.GetType().Assembly;
        var notice = assembly.GetType("ShopAssist.ShopNotice", true);
        string expected = ModHelper.GetLocalized(Mod, "inv.changed");
        object active = notice.GetField("activeNotice", Fields)?.GetValue(null);
        if ((string)Field(active, "Message") == expected) return true;
        var pending = notice.GetField("pending", Fields)?.GetValue(null) as IEnumerable;
        return pending != null && pending.Cast<object>().Any(value => (string)Field(value, "Message") == expected);
    }
    private static object Field(object value, string name) => value?.GetType().GetField(name, Fields)?.GetValue(value);
    private static GameObject TopHit(Vector2 point)
    {
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        return hits.FirstOrDefault(value => value.gameObject != null).gameObject;
    }
    private static Camera CameraFor(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>();
        return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
    }
    private static bool Same(GridShape a, GridShape b) => a != null && b != null && a.minX == b.minX && a.minY == b.minY &&
        a.orientation == b.orientation && a.flipped == b.flipped && a.globalWidth == b.globalWidth && a.globalHeight == b.globalHeight;
    private static GameItem New(string id)
    {
        GameItem value = DirectoryMaster.Item(id); Need(value != null, "Missing fixture " + id);
        GeneralHelper.SetItemOwned(value, true); return value;
    }
    private static void Put(GameInventory inventory, GameItem value)
    {
        var marker = inventory.TryFindOneValidInventorySlot(value);
        Need(marker != null && marker.targetItem == null && marker.numTransfer >= value.unitCount &&
            marker.TryAcceptOnce(value.unitCount) == value.unitCount && ReferenceEquals(value.parentInventory, inventory), "Fixture placement failed");
    }
    private void Press() { input.Mouse = press; input.MouseDown = input.MouseHeld = 0; }
    private void Release() { input.MouseDown = input.MouseHeld = -1; input.MouseUp = 0; }
    private void ClearInput() { input.MouseDown = input.MouseHeld = input.MouseUp = -1; }
    private void Next(int value, int frames = 1) { step = value; readyFrame = Time.frameCount + frames; }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
