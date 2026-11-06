using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Exercises F20 on original game inventories without moving the desktop cursor.
public sealed class PeekScenario
{
    private const string ModId = "starswhere.shopassist";
    private QaLoadSlot load;
    private GameItem bay, contained;
    private GameGridInventory originalGrid;
    private Type peekType, inventoryType;
    private MethodInfo safe, tryGrid, capture;
    private object peek;
    private PeekCaptureDriver screenshot;
    private int step;
    private float started = Time.realtimeSinceStartup;
    private int waitFrame;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - started > 45f) throw new InvalidOperationException("F20 QA timed out at step " + step);
        switch (step)
        {
            case 0:
            {
                if (MainMenuUIController.Instance == null) return false;
                string[] context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-latest-run.txt")).Trim().Split('|');
                Require(context.Length == 4 && context[0] == "1" && Guid.TryParseExact(context[1], "N", out _) && int.TryParse(context[2], out _), "Invalid isolated QA context");
                load = new QaLoadSlot(int.Parse(context[2]), Guid.ParseExact(context[1], "N"));
                Next(1, "Loading isolated F20 save"); return false;
            }
            case 1:
            {
                if (!load.Tick() || EmporiumEntry.Instance?.invWindow == null || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
                var calendar = AdvCalendarUIManager.Instance;
                if (calendar?.UIPanel != null && calendar.UIPanel.activeSelf) StoreUIManager.Instance.storeCalendar.OnClick();
                var mod = ModLoader.Mods.Find(m => m.Manifest != null && m.Manifest.ID == ModId);
                Require(mod?.Instance != null, "Shop Assist is not loaded");
                Assembly assembly = mod.Instance.GetType().Assembly;
                peekType = assembly.GetType("ShopAssist.ContainerPeek", true);
                inventoryType = assembly.GetType("ShopAssist.ShopInventory", true);
                safe = inventoryType.GetMethod("SafeContainerForPeek", BindingFlags.Static | BindingFlags.NonPublic);
                tryGrid = peekType.GetMethod("TryGrid", BindingFlags.Static | BindingFlags.NonPublic);
                capture = peekType.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic);
                Require(safe != null && tryGrid != null && capture != null, "F20 entry points unavailable");
                var modules = mod.Instance.GetType().GetField("modules", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mod.Instance) as IEnumerable;
                foreach (object module in modules ?? throw new InvalidOperationException("Missing mod module registry"))
                {
                    Type type = module.GetType();
                    if ((string)type.GetField("Name", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(module) != "F20") continue;
                    peek = ((Action)type.GetField("Tick", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(module)).Target;
                }
                Require(peek != null && peek.GetType() == peekType, "F20 was not registered in the real mod loop");
                StoreUIManager.Instance.gameOptionUIManager.OpenUI();
                var options = CustomUIManager.Instance.GetWindow("game_options");
                options.Get(ModId + ":f20.enabled").SetBool(true, true);
                options.Get(ModId + ":f20.delay").SetFloat(100, true);
                options.Get("apply").button.onClick.Invoke();
                Require((ModConfig.Find(ModId).Get("f20.enabled") as ConfigEntry<bool>)?.Value == true, "F20 native option did not apply");
                StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                bay = DirectoryMaster.Item("storage_bay");
                GraphUtils.EmporiumTryAdd(EmporiumEntry.Instance.invElement, bay);
                Require(bay.parentInventory != null && bay.contentWindow != null && !bay.contentWindow.IsVisible(), "QA ordinary container unavailable or unexpectedly opened");
                Require(IsSafe(bay), "Native owned storage bay was not accepted for preview");
                Require(Grid(bay, out originalGrid, out int page) && page == -1, "Could not read the ordinary bay's native grid");
                Require(Contents(originalGrid).Count == 0, "New QA bay was not empty");
                contained = DirectoryMaster.Item("scrap_metal");
                Require(GraphUtils.TryAcceptAll(originalGrid, contained) == 0 && contained.parentInventory == originalGrid, "Could not place native QA contents");
                waitFrame = Time.frameCount + 4;
                Next(2, "Enabled F20 and populated a closed ordinary native container"); return false;
            }
            case 2:
            {
                if (Time.frameCount < waitFrame) return false;
                var element = bay as GameItemElement;
                Require(element != null && element.background.gameObject.activeInHierarchy, "QA bay is not visible");
                Vector2 point;
                Require(FindNativeHit(element, out point), "Native EventSystem cannot hit the ordinary QA container");
                int id = bay.uniqueId, childId = contained.uniqueId;
                bool openedBefore = bay.IsTag("OPENED_TAG"), trackedBefore = bay.IsTag("TRACK_OPENED_TAG");
                IList snapshot = Contents(originalGrid);
                Require(snapshot.Count == 1, "F20 did not capture the one visible shallow item");
                var rows = peekType.GetField("items", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(peek) as IList;
                rows.Clear(); foreach (object item in snapshot) rows.Add(item);
                peekType.GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(peek, new object[] { originalGrid });
                var panel = peekType.GetField("panel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(peek) as RectTransform;
                Require(panel != null && panel.GetComponentsInChildren<Image>(true).Any(image => image.sprite == ((GameItemElement)contained).background.image.sprite && image.color.a > 0f),
                    "F20 did not render the actual item sprite into the preview");
                Require(panel.GetComponentsInChildren<Graphic>(true).All(graphic => !graphic.raycastTarget) &&
                    !panel.GetComponent<CanvasGroup>().blocksRaycasts, "F20 preview intercepts native pointer input");
                Require(!bay.contentWindow.IsVisible() && bay.uniqueId == id && contained.uniqueId == childId &&
                    bay.IsTag("OPENED_TAG") == openedBefore && bay.IsTag("TRACK_OPENED_TAG") == trackedBefore && contained.parentInventory == originalGrid,
                    "F20 snapshot or preview changed ownership, identity, open state or placement");
                bay.EnableTag("TRACK_OPENED_TAG"); bay.DisableTag("OPENED_TAG");
                Require(!IsSafe(bay) && bay.IsTag("TRACK_OPENED_TAG") && !bay.IsTag("OPENED_TAG"), "F20 accepted a tracked unopened container or changed its tags");
                bay.DisableTag("TRACK_OPENED_TAG");
                var locked = DirectoryMaster.Item("sec_box");
                GraphUtils.EmporiumTryAdd(EmporiumEntry.Instance.invElement, locked);
                Require(locked.parentInventory != null && LockHelper.IsCurrentlyLocked(locked) && !IsSafe(locked), "F20 accepted a locked native container");
                var kit = DirectoryMaster.Item("storage_bay_expansion_kit");
                Require(StorageExpansionHelper.CanExpand(bay), "Native QA bay cannot be expanded");
                StorageExpansionHelper.Expand(bay, kit);
                var tabs = (bay.contentWindow.childElement as MachineryPanelRow)?.GetPanel("storage")?.content as MachineryTabs;
                Require(tabs != null && tabs.count > 1, "Native expansion did not create storage pages");
                Require(Grid(bay, out GameGridInventory firstPage, out int firstNumber) && firstNumber == tabs.selected &&
                    firstPage == originalGrid && Contents(firstPage).Count == 1, "F20 did not use the selected native page");
                tabs.Select(1, true);
                Require(Grid(bay, out GameGridInventory nextPage, out int nextNumber) && nextNumber == 1 && nextPage != firstPage &&
                    Contents(nextPage).Count == 0, "F20 did not follow the new native page");
                Require(!bay.contentWindow.IsVisible() && contained.parentInventory == originalGrid && !bay.IsTag("OPENED_TAG"),
                    "F20 changed open state or item placement during page inspection");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    var host = new GameObject("QA peek capture");
                    screenshot = host.AddComponent<PeekCaptureDriver>();
                    screenshot.Configure(peek, originalGrid, snapshot);
                }
                waitFrame = Time.frameCount + 5;
                Next(3, "Validated the F20 snapshot and requested a controlled rendered preview capture"); return false;
            }
            case 3:
            {
                if (Time.frameCount < waitFrame) return false;
                if (screenshot != null)
                {
                    Require(screenshot.Complete && screenshot.Error == null, "Controlled F20 screenshot failed: " + screenshot.Error);
                    UnityEngine.Object.Destroy(screenshot.gameObject);
                }
                result = "PASS F20 native option, real EventSystem container raycast, empty/shallow preview sprite, input passthrough, locked/unopened rejection and selected-page isolation; pointer dwell was not simulated";
                return true;
            }
            default: throw new InvalidOperationException("Unknown F20 QA step " + step);
        }
    }

    private bool IsSafe(GameItem item) => (bool)safe.Invoke(null, new object[] { item });
    private bool Grid(GameItem item, out GameGridInventory value, out int page)
    {
        object[] args = { item, null, -1 };
        bool success = (bool)tryGrid.Invoke(null, args);
        value = args[1] as GameGridInventory; page = (int)args[2]; return success;
    }
    private IList Contents(GameGridInventory value) => (IList)capture.Invoke(null, new object[] { value });
    private static bool FindNativeHit(GameItemElement item, out Vector2 point)
    {
        var rect = item.background.rectTransform;
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var results = new List<RaycastResult>();
        for (int x = 1; x < 10; x++) for (int y = 1; y < 10; y++)
        {
            Vector3 local = new Vector3(Mathf.Lerp(rect.rect.xMin, rect.rect.xMax, x / 10f), Mathf.Lerp(rect.rect.yMin, rect.rect.yMax, y / 10f), 0f);
            point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(local));
            var pointer = new PointerEventData(EventSystem.current) { position = point };
            results.Clear(); EventSystem.current.RaycastAll(pointer, results);
            if (results.Count == 0 || results[0].gameObject == null) continue;
            var node = results[0].gameObject.GetComponent<TreeNodeRender>();
            if (node != null && ReferenceEquals(node.FindParentElement(), item)) return true;
        }
        point = default; return false;
    }
    private void Next(int value, string message) { Debug.Log("[PLAYER-QA] " + message); step = value; started = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

// Keeps the actual preview renderer alive through a single rendered frame; it does
// not claim to simulate pointer movement or the production dwell timer.
public sealed class PeekCaptureDriver : MonoBehaviour
{
    private object target;
    private GameGridInventory grid;
    private IList snapshot;
    public bool Complete { get; private set; }
    public string Error { get; private set; }
    public void Configure(object instance, GameGridInventory inventory, IList entries)
    { target = instance; grid = inventory; snapshot = entries; }
    private void LateUpdate()
    {
        if (Complete || target == null) return;
        try
        {
            Type type = target.GetType();
            var rows = (IList)type.GetField("items", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
            rows.Clear(); foreach (object entry in snapshot) rows.Add(entry);
            type.GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, new object[] { grid });
            var panel = (RectTransform)type.GetField("panel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
            if (panel == null || !panel.gameObject.activeInHierarchy) throw new InvalidOperationException("Preview panel is not active");
            panel.anchoredPosition = Vector2.zero;
            Canvas.ForceUpdateCanvases();
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-peek-rendered.png"));
        }
        catch (Exception error) { Error = error.ToString(); }
        Complete = true;
    }
}
