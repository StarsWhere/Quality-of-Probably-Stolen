using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Uses the original scene's fixed main storage, without calling PixelWindow.Show on it.
public sealed class StorageAnchorScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 120;
    private PixelWindow stock;
    private Transform originalParent;
    private Vector2 originalPosition;
    private GameItem machine, bag;
    private int step, frame;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Storage anchor QA timed out at step " + step);
        if (step == 0) { if (start.Tick()) step = 1; return false; }
        if (step == 1)
        {
            var entry = EmporiumEntry.Instance;
            if (entry?.invWindow == null || entry.invElement == null || entry.storeInteractables?.activeInHierarchy != true ||
                StoreUIManager.Instance?.gameOptionUIManager == null || ItemMouseDoubleClickHandler.current == null ||
                EventSystem.current == null || RenderHandler.current?.windowUICanvasObject == null) return false;
            stock = entry.invWindow;
            Need(stock.IsVisible() && !stock.canMove && StoreUIManager.Instance.IsInnateWindow(stock), "Main storage is not the native fixed scene inventory");
            originalParent = stock.rectTransform.parent; originalPosition = stock.rectTransform.anchoredPosition;
            entry.Validate();
            CheckAnchor("the native scene layout validation");
            TradeScenario.SetOptions(true, "f07.enabled", "f10.enabled");
            CheckAnchor("changing options");
            machine = FeedScenario.New("hydroponic"); bag = FeedScenario.New("backpack_medium");
            FeedScenario.Put(entry.invElement, machine); FeedScenario.Put(entry.invElement, bag);
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(180, Screen.height - 180));
            Need(machine.contentWindow.IsVisible(), "Machine window did not open");
            CheckAnchor("opening a machine");
            frame = Time.frameCount + 3; step = 2; return false;
        }
        if (Time.frameCount < frame) return false;
        if (step == 2)
        {
            var button = Button(machine.contentWindow, "ShopAssist_MachineModules");
            if (button == null || button.isDisabled) return false;
            button.Select();
            var browser = Ui("f10"); Need(browser?.IsAlive == true, "Machine shortcut did not open the module workbench");
            CheckAnchor("opening the module workbench");
            foreach (string tab in new[] { "installed_tab", "storage_tab", "idle_tab" })
            {
                Ui("f10").Get(tab).button.onClick.Invoke();
                CheckAnchor("switching module tab " + tab);
            }
            Canvas.ForceUpdateCanvases();
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-storage-anchor.png"));
            frame = Time.frameCount + 3; step = 3; return false;
        }
        if (step == 3)
        {
            CheckAnchor("after rendering the workbench");
            Ui("f10")?.Close();
            if (machine.contentWindow.IsVisible()) machine.contentWindow.Hide();
            var button = Button(stock, "ShopAssist_WindowTools");
            if (button == null || button.isDisabled) return false;
            Need(HitNativeButton(button), "Native main storage tools button is not reachable");
            button.Select();
            var card = Ui("context"); Need(card?.IsAlive == true && card.Get("f07.open")?.button != null, "Main storage tools did not open");
            CheckAnchor("opening storage tools");
            card.Get("f07.open").button.onClick.Invoke();
            var routes = Ui("routes"); Need(routes?.IsAlive == true, "Storage tools did not open the routing window");
            CheckAnchor("opening a storage tool");
            routes.Close();
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width * .7f, Screen.height * .75f));
            Need(bag.contentWindow != null && bag.contentWindow.IsVisible() && bag.contentWindow.canMove,
                "Ordinary container did not open as a movable window");
            CheckAnchor("opening an ordinary container");
            frame = Time.frameCount + 3; step = 4; return false;
        }
        if (step == 4)
        {
            DragNativeContainer(bag.contentWindow);
            CheckAnchor("dragging an ordinary container");
            bag.contentWindow.Hide(); machine.contentWindow.Hide();
            result = "PASS native main-storage anchor across module tabs and storage tools; ordinary container remains draggable";
            return true;
        }
        throw new InvalidOperationException("Unknown storage anchor QA step " + step);
    }

    private void CheckAnchor(string action)
    {
        Need(stock != null && stock.IsVisible() && ReferenceEquals(stock.rectTransform.parent, originalParent) &&
            Vector2.Distance(stock.rectTransform.anchoredPosition, originalPosition) < .1f,
            "Main storage moved while " + action + ": parent=" + stock?.rectTransform.parent?.name +
            " position=" + stock?.rectTransform.anchoredPosition + " expected=" + originalPosition);
    }

    private static MachineryButton Button(PixelWindow window, string name) =>
        window?.handler?.transform.Find(name)?.GetComponent<TreeNodeRender>()?.element as MachineryButton;

    private static CustomUIWindow Ui(string id) => CustomUIManager.Instance.GetWindow(Mod + "." + id);

    private static bool HitNativeButton(MachineryButton button)
    {
        var plate = button.handler.GetComponentsInChildren<TreeNodeRender>(true).FirstOrDefault(node => node != button.handler &&
            node.image != null && node.image.isActiveAndEnabled && node.image.raycastTarget && node.rectTransform.rect.width > 1);
        if (plate == null) return false;
        Canvas canvas = plate.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var corners = new Vector3[4]; plate.rectTransform.GetWorldCorners(corners);
        Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, (corners[0] + corners[2]) / 2);
        return ReferenceEquals(RenderHandler.RaycastElement<SelectableElement>(point), button);
    }

    private static void DragNativeContainer(PixelWindow window)
    {
        var background = (TreeNodeRender)typeof(PixelWindow).GetField("background", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
        Need(background?.rectTransform != null, "Native container drag surface is missing");
        Canvas canvas = background.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, background.rectTransform.TransformPoint(background.rectTransform.rect.center));
        var pointer = new PointerEventData(EventSystem.current) { position = point, pressPosition = point, button = PointerEventData.InputButton.Left };
        Vector2 before = window.rectTransform.anchoredPosition;
        ExecuteEvents.Execute(background.gameObject, pointer, ExecuteEvents.beginDragHandler);
        pointer.position += new Vector2(-36, -24);
        ExecuteEvents.Execute(background.gameObject, pointer, ExecuteEvents.dragHandler);
        ExecuteEvents.Execute(background.gameObject, pointer, ExecuteEvents.endDragHandler);
        Need(Vector2.Distance(before, window.rectTransform.anchoredPosition) > 1, "Ordinary container did not respond to native drag events");
    }

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
