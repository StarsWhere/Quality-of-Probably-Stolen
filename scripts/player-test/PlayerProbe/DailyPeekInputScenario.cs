using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Feeds only pointer coordinates to the ordinary mod loop in the isolated QA Player.
public sealed class DailyPeekInputScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private QaSyntheticInput input;
    private GameItemElement bay, locked, first, second;
    private GameGridInventory inside;
    private int step, frame;
    private float since = Time.realtimeSinceStartup, hoverAt;
    internal bool ReadyForFixtures => step == 1;

    public bool Tick(out string result)
    {
        try
        {
            bool complete = Advance(out result);
            if (complete) Cleanup();
            return complete;
        }
        catch { Cleanup(); throw; }
    }

    private bool Advance(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - since < 45f, "F20 pointer QA timed out at step " + step);
        if (step == 0)
        {
            if (!start.Tick()) return false;
            Next(1); return false;
        }
        if (step == 1)
        {
            if (StoreUIManager.Instance?.gameOptionUIManager == null || EventSystem.current == null ||
                EmporiumEntry.Instance?.invElement == null) return false;
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            Need(EmporiumEntry.Instance.invWindow.IsVisible() && !EmporiumEntry.Instance.invWindow.canMove &&
                StoreUIManager.Instance.IsInnateWindow(EmporiumEntry.Instance.invWindow), "Native main stock is not naturally ready");
            SetOptions(true);
            bay = New("storage_bay"); locked = New("sec_box");
            Put(EmporiumEntry.Instance.invElement, bay); Put(EmporiumEntry.Instance.invElement, locked);
            Need(LockHelper.IsCurrentlyLocked(locked), "Native locked-container fixture is not locked");
            inside = bay.contentWindow?.childElement as GameGridInventory;
            Need(inside != null && !bay.contentWindow.IsVisible(), "Ordinary container fixture is not closed");
            first = New("scrap_metal"); Put(inside, first);
            input = new QaSyntheticInput(); input.Mouse = new Vector3(-200, -200, 0);
            frame = Time.frameCount + 5; Next(2); return false;
        }
        if (step == 2)
        {
            if (Time.frameCount < frame) return false;
            Need(Preview() == null, "A preview remained after the pointer left");
            input.Mouse = Hit(bay);
            Need(Vector3.Distance(Input.mousePosition, input.Mouse) < 0.01f, "QA pointer injection is unavailable");
            hoverAt = Time.unscaledTime; Next(3); return false;
        }
        if (step == 3)
        {
            float elapsed = Time.unscaledTime - hoverAt;
            if (elapsed < 0.35f) return false;
            if (elapsed < 0.75f) { Need(Preview() == null, "F20 appeared before its configured delay"); return false; }
            if (elapsed < 1.35f) return false;
            GameObject panel = Preview();
            Need(panel != null && Shows(panel, first), "Pointer dwell did not show the closed container's actual contents");
            Need(panel.GetComponent<CanvasGroup>()?.blocksRaycasts == false &&
                panel.GetComponentsInChildren<Graphic>(true).All(graphic => !graphic.raycastTarget), "F20 preview intercepted input");
            Need(!bay.contentWindow.IsVisible() && first.parentInventory == inside, "F20 opened or moved container contents");
            second = New("processed_milk"); Put(inside, second);
            hoverAt = Time.unscaledTime; Next(4); return false;
        }
        if (step == 4)
        {
            if (Time.unscaledTime - hoverAt < 0.6f) return false;
            Need(Preview() != null && Shows(Preview(), first) && Shows(Preview(), second), "F20 did not refresh changed contents");
            var bounds = ScreenRect((RectTransform)Preview().transform);
            Need(bounds.xMin >= 2 && bounds.yMin >= 2 && bounds.xMax <= Screen.width - 2 && bounds.yMax <= Screen.height - 2,
                "F20 preview extends outside the visible screen: " + bounds);
            var tooltip = OverlayHandler.current?.GetTooltipWindow();
            if (tooltip?.IsVisible() == true && tooltip.handler?.rectTransform != null)
            {
                var graphics = tooltip.handler.GetComponentsInChildren<Graphic>(true)
                    .Where(graphic => graphic.isActiveAndEnabled && graphic.color.a > 0 &&
                        graphic.rectTransform.rect.width > 0 && graphic.rectTransform.rect.height > 0).ToArray();
                Need(graphics.Length > 0, "Native tooltip has no visible graphics");
                Rect tooltipBounds = ScreenRect(graphics[0].rectTransform);
                foreach (Graphic graphic in graphics.Skip(1))
                {
                    Rect part = ScreenRect(graphic.rectTransform);
                    tooltipBounds = Rect.MinMaxRect(Mathf.Min(tooltipBounds.xMin, part.xMin), Mathf.Min(tooltipBounds.yMin, part.yMin),
                        Mathf.Max(tooltipBounds.xMax, part.xMax), Mathf.Max(tooltipBounds.yMax, part.yMax));
                }
                Debug.Log("[PLAYER-QA] F20 preview=" + bounds + " tooltip=" + tooltipBounds);
                if (tooltipBounds.xMin >= bounds.width + 16 || Screen.width - tooltipBounds.xMax >= bounds.width + 16)
                    Need(!bounds.Overlaps(tooltipBounds), "F20 preview covers the native item tooltip");
            }
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-peek-pointer.png"));
            frame = Time.frameCount + 3; Next(40); return false;
        }
        if (step == 40)
        {
            if (Time.frameCount < frame) return false;
            input.Mouse = new Vector3(-200, -200, 0); frame = Time.frameCount + 5; Next(5); return false;
        }
        if (step == 5)
        {
            if (Time.frameCount < frame) return false;
            Need(Preview() == null, "F20 did not hide after leaving the container");
            input.Mouse = Hit(locked); hoverAt = Time.unscaledTime; Next(6); return false;
        }
        if (step == 6)
        {
            if (Time.unscaledTime - hoverAt < 1.35f) return false;
            Need(Preview() == null && !locked.contentWindow.IsVisible(), "F20 exposed a locked container");
            input.Mouse = new Vector3(-200, -200, 0); SetOptions(false);
            frame = Time.frameCount + 5; Next(7); return false;
        }
        if (step == 7)
        {
            if (Time.frameCount < frame) return false;
            input.Mouse = Hit(bay); hoverAt = Time.unscaledTime; Next(8); return false;
        }
        if (step == 8)
        {
            if (Time.unscaledTime - hoverAt < 1.35f) return false;
            Need(Preview() == null, "Disabled F20 still opened a preview");
            input.Mouse = new Vector3(-200, -200, 0); SetOptions(true);
            frame = Time.frameCount + 5; Next(9); return false;
        }
        if (step == 9)
        {
            if (Time.frameCount < frame) return false;
            input.Mouse = Hit(bay); hoverAt = Time.unscaledTime; Next(10); return false;
        }
        if (Time.unscaledTime - hoverAt < 1.35f) return false;
        Need(Preview() != null && Shows(Preview(), first) && Shows(Preview(), second), "F20 did not recover after re-enable");
        result = "PASS F20 real UI raycast and mod Tick: delayed hover, contents refresh, pointer leave, locked rejection and disable/re-enable";
        return true;
    }

    private static void SetOptions(bool enabled)
    {
        var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
        var window = CustomUIManager.Instance.GetWindow("game_options");
        Need(window != null, "Native options unavailable");
        window.Get(Mod + ":f20.enabled").SetBool(enabled, true);
        window.Get(Mod + ":f20.delay").SetFloat(1000, true);
        window.Get("apply").button.onClick.Invoke();
        Need(((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f20.enabled")).Value == enabled &&
            ((ConfigEntry<int>)ModConfig.Find(Mod).Get("f20.delay")).Value == 1000, "F20 options did not apply");
        options.CloseUI();
    }

    private static GameItemElement New(string id)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement;
        Need(item != null, "Missing native fixture " + id); GeneralHelper.SetItemOwned(item, true); return item;
    }

    private static void Put(GameInventory grid, GameItem item)
    {
        var slot = grid.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == grid && slot.targetItem == null &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid, "Could not place " + item.identifier);
    }

    private static Vector3 Hit(GameItemElement item)
    {
        Canvas.ForceUpdateCanvases();
        var rect = item.background.rectTransform;
        var canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var hits = new List<RaycastResult>();
        for (int x = 1; x < 10; x++) for (int y = 1; y < 10; y++)
        {
            var local = new Vector3(Mathf.Lerp(rect.rect.xMin, rect.rect.xMax, x / 10f),
                Mathf.Lerp(rect.rect.yMin, rect.rect.yMax, y / 10f));
            Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(local));
            hits.Clear(); EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            if (hits.Count == 0 || hits[0].gameObject == null) continue;
            var node = hits[0].gameObject.GetComponent<TreeNodeRender>();
            if (node != null && ReferenceEquals(node.FindParentElement(), item)) return point;
        }
        throw new InvalidOperationException("Native EventSystem cannot hit " + item.identifier);
    }

    private static GameObject Preview() => GameObject.Find("ShopAssist Container Preview");
    private static Rect ScreenRect(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }
    private static bool Shows(GameObject panel, GameItemElement item) => panel != null && item?.background?.image?.sprite != null &&
        panel.GetComponentsInChildren<Image>(true).Any(image => image.sprite == item.background.image.sprite && image.color.a > 0);
    private void Cleanup() { input?.Dispose(); input = null; }
    private void Next(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
