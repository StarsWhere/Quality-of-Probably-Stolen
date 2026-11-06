using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Exercises the normal EventSystem pointer path; reflection is used only to set up feature entry points.
public sealed class SharedUxScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step;
    private float since = Time.realtimeSinceStartup, wait;
    private GameItem first, second;
    private CustomUIWindow modules, tools;
    private Vector2 toolPosition, toolScreenPosition;
    private string toolsBeforeScreenshot, toolsAfterScreenshot;
    private int screenshotFrame;
    private QaSyntheticInput input;
    private EventSystem focusSystem;
    private bool originalFocus;
    private PointerSpy pointer;
    private Assembly assembly;
    private object settings;
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private sealed class PointerSpy : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        internal int Down, Up, Click;
        public void OnPointerDown(PointerEventData eventData) { Down++; }
        public void OnPointerUp(PointerEventData eventData) { Up++; }
        public void OnPointerClick(PointerEventData eventData) { Click++; }
    }

    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - since < 100, "Shared UX scenario timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || EventSystem.current == null) return false;
            TradeScenario.SetOptions(true, "f10.enabled", "f16.enabled", "f16.money", "f16.rep");
            TradeScenario.SetOptions(false, "f16.sound", "f15.enabled", "f17.enabled", "f21.enabled");
            var mod = ModLoader.Mods.Single(m => m.Manifest?.ID == Mod);
            assembly = mod.Instance.GetType().Assembly;
            settings = mod.Instance.GetType().GetField("settings", Any).GetValue(mod.Instance);
            first = Put("hydroponic", "QA bound machine A"); second = Put("hydroponic", "QA other machine B");
            ItemMouseDoubleClickHandler.current.OpenContentAction(first, new Vector2(160, 340));
            ItemMouseDoubleClickHandler.current.OpenContentAction(second, new Vector2(Screen.width - 160, 340));
            first.contentWindow.ToFront();
            assembly.GetType("ShopAssist.MachineTools").GetMethod("OpenModulesFor", Any).Invoke(null, new object[] { first });
            modules = CustomUIManager.Instance.GetWindow(Mod + ".f10");
            Need(modules?.IsAlive == true, "Module browser did not open");
            second.contentWindow.ToFront(); wait = Time.unscaledTime + .3f; step++; return false;
        }
        if (step == 2)
        {
            if (Time.unscaledTime < wait) return false;
            Need(CustomUIManager.Instance.GetWindow(Mod + ".f10") == modules && modules.IsAlive &&
                modules.root.GetComponentsInChildren<TMP_Text>().Any(t => t.text.Contains("QA bound machine A")),
                "Looking at another machine changed the bound module target");
            Need(modules.useMachineryStyle, "Mod browser did not use the native machinery frame");
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-ux-machines.png"));
            wait = Time.unscaledTime + .2f; step = 20; return false;
        }
        if (step == 20)
        {
            if (Time.unscaledTime < wait) return false;
            modules.Close();
            settings.GetType().GetMethod("OpenTools", Any).Invoke(settings, new object[] { 0 });
            tools = CustomUIManager.Instance.GetWindow(Mod + ".tools");
            Need(tools?.IsAlive == true, "Tools window did not open");
            tools.Rect.anchoredPosition += new Vector2(12, -8); toolPosition = tools.Rect.anchoredPosition;
            input = new QaSyntheticInput(true, true);
            focusSystem = EventSystem.current; originalFocus = focusSystem.isFocused;
            Debug.Log("[PLAYER-QA] category before focus " + InputState());
            if (!originalFocus)
                typeof(EventSystem).GetMethod("OnApplicationFocus", Any).Invoke(focusSystem, new object[] { true });
            string screenshotId = Guid.NewGuid().ToString("N").Substring(0, 8);
            toolsBeforeScreenshot = Path.Combine(Application.persistentDataPath, "qa-ux-tools-before-" + screenshotId + ".png");
            toolsAfterScreenshot = Path.Combine(Application.persistentDataPath, "qa-ux-tools-after-" + screenshotId + ".png");
            ScreenCapture.CaptureScreenshot(toolsBeforeScreenshot);
            wait = Time.unscaledTime + .3f; step = 3; return false;
        }
        if (step == 3)
        {
            if (Time.unscaledTime < wait || !ScreenshotReady(toolsBeforeScreenshot)) return false;
            toolPosition = tools.Rect.anchoredPosition;
            toolScreenPosition = Position(tools.Rect);
            var button = tools.Get("category_2")?.button;
            Need(button != null && button.interactable, "Machine category tab is unavailable");
            pointer = button.gameObject.AddComponent<PointerSpy>();
            input.Mouse = Position(button);
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = input.Mouse }, hits);
            Need(hits.Count > 0 && hits[0].gameObject.GetComponentInParent<Button>() == button, "Category tab is covered in the actual UI raycast");
            Debug.Log("[PLAYER-QA] category raycast " + InputState());
            step++; return false;
        }
        if (step == 4) { input.MouseDown = input.MouseHeld = 0; Debug.Log("[PLAYER-QA] category press " + InputState()); step++; return false; }
        if (step == 5) { Debug.Log("[PLAYER-QA] category after press " + InputState()); input.MouseDown = input.MouseHeld = -1; input.MouseUp = 0; step++; return false; }
        if (step == 6)
        {
            input.MouseUp = -1;
            step = 60; return false;
        }
        if (step == 60)
        {
            Debug.Log("[PLAYER-QA] category after release " + InputState());
            ScreenCapture.CaptureScreenshot(toolsAfterScreenshot);
            Debug.Log("[PLAYER-QA] category diagnostic captures " + toolsBeforeScreenshot + " and " + toolsAfterScreenshot);
            screenshotFrame = Time.frameCount + 4; step = 61; return false;
        }
        if (step == 61)
        {
            if (Time.frameCount < screenshotFrame || !ScreenshotReady(toolsAfterScreenshot)) return false;
            Need(CustomUIManager.Instance.GetWindow(Mod + ".tools") == tools && tools.IsAlive &&
                Vector2.Distance(Position(tools.Rect), toolScreenPosition) <= .5f && tools.Get("category_2")?.button.interactable == false &&
                tools.Get("category_0")?.button.interactable == true && pointer.Down > 0 && pointer.Up > 0 && pointer.Click > 0,
                "Pointer category switch failed: " + InputState());
            wait = Time.unscaledTime + .2f; step = 21; return false;
        }
        if (step == 21)
        {
            if (Time.unscaledTime < wait) return false;
            input.Dispose(); input = null;
            if (focusSystem != null && !originalFocus)
                typeof(EventSystem).GetMethod("OnApplicationFocus", Any).Invoke(focusSystem, new object[] { false });
            if (pointer != null) UnityEngine.Object.Destroy(pointer);
            tools.Close();
            var player = PlayerStore.Instance;
            player.playerCash = 200000;
            var client = StoreClientList.CreateJunkerSellOnly(); client.clientFaction = "FACTION_UPPER_LEVEL";
            player.currentClientInstance = StoreClientInstance.CreateClientInstance(client); player.isClientArrived = true;
            foreach (int n in new[] { 1, 2 })
            {
                var item = DirectoryMaster.Item("wine_yeast"); GeneralHelper.SetItemOwned(item, false);
                var slot = EmporiumEntry.Instance.backInvinvElement.TryFindOneValidInventorySlot(item);
                Need(slot != null && slot.TryAcceptOnce() > 0, "Could not put receipt fixture on the counter");
                player.BuyItem(item);
            }
            wait = Time.unscaledTime + .3f; step = 7; return false;
        }
        if (step == 7)
        {
            if (Time.unscaledTime < wait) return false;
            var receipts = UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>()
                .Where(t => t.gameObject.activeInHierarchy && t.name.StartsWith(Mod + ".receipt.", StringComparison.Ordinal)).ToArray();
            Need(receipts.Length == 2 && receipts[0].text == receipts[1].text,
                "Two same-value native transactions did not produce two independent receipts");
            Need(receipts.All(t => t.enabled && t.color.a > .5f && !t.raycastTarget && t.fontSize >= 14), "Receipt is invisible, unreadable or intercepts input");
            Need(receipts.All(t => t.GetComponent<TreeNodeRender>().image?.enabled != true), "Receipt drew an opaque backing block");
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-ux-receipts.png"));
            wait = Time.unscaledTime + .2f; step++; return false;
        }
        if (step == 8)
        {
            if (Time.unscaledTime < wait) return false;
            result = "PASS bound machine context, native machinery frame, real EventSystem category clicks with stable window, distinct same-value receipts and readable nonblocking floating text";
            return true;
        }
        return false;
    }

    private static GameItem Put(string id, string name)
    {
        var item = DirectoryMaster.Item(id); GeneralHelper.SetItemOwned(item); GeneralHelper.SetCustomName(item, name);
        var slot = EmporiumEntry.Instance.invElement.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.TryAcceptOnce() > 0, "Could not place " + name); return item;
    }

    private static Vector2 Position(Button button)
    {
        return Position((RectTransform)button.transform);
    }

    private static Vector2 Position(RectTransform rect)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>()?.rootCanvas;
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
    }

    private static bool ScreenshotReady(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private string InputState()
    {
        EventSystem events = EventSystem.current;
        var active = CustomUIManager.Instance.GetWindow(Mod + ".tools");
        return "sameWindow=" + ReferenceEquals(active, tools) + " alive=" + (tools?.IsAlive == true) +
            " position=" + tools?.Rect.anchoredPosition + " expected=" + toolPosition +
            " screenDelta=" + (tools?.IsAlive == true ? Vector2.Distance(Position(tools.Rect), toolScreenPosition) : -1) +
            " category2=" + tools?.Get("category_2")?.button?.interactable + " category0=" + tools?.Get("category_0")?.button?.interactable +
            " module=" + (events?.currentInputModule != null ? events.currentInputModule.GetType().FullName : "null") +
            " eventFocused=" + (events?.isFocused == true) + " appFocused=" + Application.isFocused +
            " mousePresent=" + Input.mousePresent + " down=" + Input.GetMouseButtonDown(0) + " up=" + Input.GetMouseButtonUp(0) +
            " spy=" + (pointer == null ? "none" : pointer.Down + "/" + pointer.Up + "/" + pointer.Click);
    }
}
