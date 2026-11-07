using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

// An isolated outing: the actual UI event system, not a direct tooltip call,
// receives a pointer over one rendered card. The other item's value is unobserved.
public sealed class FieldValueScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static FieldValueScenario active;
    private readonly QaNewRun start = new QaNewRun();
    private float since = Time.realtimeSinceStartup;
    private int step, readyFrame, tooltipFrame = -1, hiddenTooltips, hiddenValueReads;
    private GameItemElement known, hidden, concealed;
    private GameItem bag, closedContainer;
    private GameGridInventory ground;
    private MonoBehaviour firstHit;
    private QaSyntheticInput input;
    private Harmony observer;
    private Assembly modAssembly;
    private string builtTooltip, shownTooltip, observedBadge;
    private bool watching;

    private static void Need(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private void Next(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static CustomUIWindow Window() => CustomUIManager.Instance.GetWindow(Mod + ".packing");
    private static object Field(object instance, string name) => instance?.GetType().GetField(name, Fields)?.GetValue(instance);
    private static object Entry(CustomUIWindow window, GameItem item) => Field(QaItemUi.Hit(window.root, item), "entry");
    private static string Badge(CustomUIWindow window, GameItem item) => Field(Entry(window, item), "Badge") as string;
    private static bool Flag(CustomUIWindow window, GameItem item, string name) => Field(Entry(window, item), name) is bool value && value;
    private static bool DisplaysBadge(CustomUIWindow window, GameItem item, string badge)
    {
        Transform card = QaItemUi.Hit(window.root, item)?.transform;
        while (card != null && card.name != "card") card = card.parent;
        return card != null && card.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == badge && text.gameObject.activeInHierarchy);
    }
    private static GameItemElement New(string identifier, string name, GameInventory inventory)
    {
        var item = DirectoryMaster.Item(identifier) as GameItemElement;
        Need(item != null, "Missing native fixture " + identifier);
        GeneralHelper.SetItemOwned(item, true); GeneralHelper.SetCustomName(item, name);
        var marker = inventory.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == inventory && marker.targetItem == null &&
            marker.numTransfer >= item.unitCount && marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory,
            "Could not place native fixture " + identifier);
        return item;
    }

    private void BeginObservation()
    {
        var mod = ModLoader.Mods.FirstOrDefault(value => value.Manifest?.ID == Mod && value.Instance != null);
        modAssembly = mod?.Instance.GetType().Assembly;
        Need(modAssembly != null && active == null, "Could not observe the active mod");
        active = this;
        observer = new Harmony("qa.field-value." + Guid.NewGuid().ToString("N"));
        Patch(AccessTools.Method(typeof(GameItemElement), nameof(GameItemElement.GetTooltip), Type.EmptyTypes), null, nameof(TooltipBuilt));
        Patch(AccessTools.Method(typeof(OverlayHandler), nameof(OverlayHandler.ShowTooltip),
            new[] { typeof(Vector2), typeof(Vector2), typeof(RichText), typeof(int) }), null, nameof(TooltipShown));
        Patch(AccessTools.Method(typeof(GameItem), nameof(GameItem.GetCurrentValue)), nameof(ValueRead), null);
        Patch(AccessTools.Method(typeof(GameItem), nameof(GameItem.GetValue)), nameof(ValueRead), null);
        Patch(AccessTools.Method(typeof(GameItem), nameof(GameItem.GetBaseValue)), nameof(ValueRead), null);
        foreach (string name in new[] { "unitValue", "lateUnitValue", "unitBaseValue", "backupUnitValue" })
            Patch(AccessTools.PropertyGetter(typeof(GameItem), name), nameof(ValueRead), null);
        input = new QaSyntheticInput { Mouse = new Vector3(-1000, -1000) };
        watching = true;
    }

    private void Patch(MethodBase original, string prefix, string postfix)
    {
        Need(original != null, "Missing native method to observe");
        HarmonyMethod Hook(string name) => name == null ? null : new HarmonyMethod(typeof(FieldValueScenario).GetMethod(name,
            BindingFlags.Static | BindingFlags.NonPublic));
        observer.Patch(original, prefix: Hook(prefix), postfix: Hook(postfix));
    }
    private static void ValueRead(GameItem __instance)
    {
        var scenario = active;
        if (scenario?.watching != true || !(ReferenceEquals(__instance, scenario.hidden) || ReferenceEquals(__instance, scenario.concealed))) return;
        // The rest of the game may update independently. Only count a value
        // read reached through this mod while the unhovered item is displayed.
        if (new StackTrace().GetFrames()?.Any(frame => frame.GetMethod()?.DeclaringType?.Assembly == scenario.modAssembly) == true)
            scenario.hiddenValueReads++;
    }
    private static void TooltipBuilt(GameItemElement __instance, RichText __result)
    {
        var scenario = active;
        if (scenario?.watching != true) return;
        if (ReferenceEquals(__instance, scenario.hidden) || ReferenceEquals(__instance, scenario.concealed)) scenario.hiddenTooltips++;
        if (!ReferenceEquals(__instance, scenario.known) || __result == null) return;
        scenario.builtTooltip = __result.nakedText;
        scenario.tooltipFrame = Time.frameCount;
    }
    private static void TooltipShown(RichText richText)
    {
        var scenario = active;
        if (scenario?.watching != true || scenario.tooltipFrame != Time.frameCount || richText?.nakedText == null ||
            string.IsNullOrEmpty(scenario.builtTooltip) || !richText.nakedText.Contains(scenario.builtTooltip) ||
            OverlayHandler.current?.GetTooltipWindow()?.IsVisible() != true || EventSystem.current == null ||
            !PointerHits(Input.mousePosition, scenario.known)) return;
        scenario.shownTooltip = scenario.builtTooltip;
    }

    private static Vector2 Center(MonoBehaviour hit)
    {
        var rect = (RectTransform)hit.transform;
        var canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
    }
    private static bool PointerHits(Vector2 point, GameItem item)
    {
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        GameObject first = hits.FirstOrDefault(hit => hit.gameObject != null).gameObject;
        var hit = first?.GetComponentsInParent<MonoBehaviour>(true).FirstOrDefault(component => component != null &&
            component.GetType().FullName == "ShopAssist.ItemGrid+Hit");
        return ReferenceEquals(Field(Field(hit, "entry"), "Item"), item);
    }
    private static bool GroupHasReason(CustomUIWindow window, GameItem item, string reason)
    {
        Transform grid = QaItemUi.Hit(window.root, item)?.transform;
        while (grid != null && grid.name != "ShopAssist_ItemGrid") grid = grid.parent;
        if (grid?.parent == null) return false;
        for (int i = grid.GetSiblingIndex() - 1; i >= 0; i--)
        {
            var previous = grid.parent.GetChild(i);
            if (previous.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == reason)) return true;
            if (previous.name == "ShopAssist_ItemGrid") break;
        }
        return false;
    }
    private void AssertHidden()
    {
        Need(hiddenTooltips == 0 && hiddenValueReads == 0,
            "Unhovered item was queried: tooltips=" + hiddenTooltips + " mod value reads=" + hiddenValueReads);
    }
    private void Cleanup()
    {
        watching = false;
        input?.Dispose(); input = null;
        observer?.UnpatchAll(observer.Id); observer = null;
        if (ReferenceEquals(active, this)) active = null;
    }

    public bool Tick(out string result)
    {
        result = null;
        AudioListener.volume = 0f;
        try
        {
            if (Time.realtimeSinceStartup - since > 60f) throw new TimeoutException("Field value QA step " + step);
            if (step == 0)
            {
                if (start.Tick()) Next(1);
                return false;
            }
            var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
            switch (step)
            {
                case 1:
                    if (e?.storeInteractables == null || !e.storeInteractables.activeInHierarchy || e.invElement == null) return false;
                    TradeScenario.SetOptions(true, "f36.enabled");
                    bag = New("backpack_medium", "QA value backpack", e.invElement);
                    e.selectedBackpack = bag; p.visitLeftTonight = 2;
                    var site = AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND);
                    Need(site != null && site.IsAvailable(), "Native dumping ground is unavailable");
                    MapUIManager.Instance.Travel(site); Next(2); return false;
                case 2:
                    if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !e.afterhourWindow.IsVisible() ||
                        e.afterhourPocketSlotInvBackpack.childItem != bag || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true)
                        return false;
                    ground = e.afterhourInventory;
                    closedContainer = New("backpack_medium", "QA unopened container", ground);
                    var inside = closedContainer.contentWindow?.childElement as GameGridInventory;
                    Need(inside != null, "The native closed container has no inventory");
                    concealed = New("processed_milk", "QA concealed content", inside);
                    closedContainer.contentWindow.Hide();
                    Need(!closedContainer.contentWindow.IsVisible(), "The fixture container must remain closed");
                    known = New("processed_meat", "QA hovered value", ground);
                    hidden = New("processed_juice", "QA unhovered value", ground);
                    BeginObservation();
                    RoutingScenario.RunFeatureAction("f36.open");
                    var chooser = Window();
                    Need(chooser?.Get("mode")?.dropdown != null, "Packing mode selector is unavailable");
                    var mode = chooser.Get("mode").dropdown;
                    mode.SetValueWithoutNotify(1); mode.onValueChanged.Invoke(1);
                    readyFrame = Time.frameCount + 3; Next(3); return false;
                case 3:
                    if (Time.frameCount < readyFrame) return false;
                    var list = Window();
                    Need(list?.IsAlive == true && QaItemUi.Hit(list.root, known) != null && QaItemUi.Hit(list.root, hidden) != null,
                        "Value-mode item cards are missing");
                    string unknown = ModHelper.GetLocalized(Mod, "f36.noPrice");
                    Need(Badge(list, known) == unknown && Badge(list, hidden) == unknown && !Flag(list, known, "Enabled") &&
                        !Flag(list, hidden, "Enabled") && DisplaysBadge(list, hidden, unknown),
                        "Value mode did not initially show both unobserved estimates as unknown");
                    AssertHidden();
                    var scroll = list.root.GetComponentsInChildren<ScrollRect>(true).FirstOrDefault();
                    if (scroll != null) scroll.verticalNormalizedPosition = 0;
                    readyFrame = Time.frameCount + 3; Next(4); return false;
                case 4:
                    if (Time.frameCount < readyFrame) return false;
                    Canvas.ForceUpdateCanvases();
                    firstHit = QaItemUi.Hit(Window().root, known);
                    Need(firstHit != null && EventSystem.current != null, "The known-value item card is unavailable");
                    Vector2 point = Center(firstHit);
                    Need(point.x >= 0 && point.y >= 0 && point.x < Screen.width && point.y < Screen.height && PointerHits(point, known),
                        "The real UI raycast did not hit the intended item card at " + point);
                    input.Mouse = point; Next(5); return false;
                case 5:
                    var updated = Window();
                    var refreshed = QaItemUi.Hit(updated.root, known);
                    string badge = Badge(updated, known);
                    if (refreshed == null || ReferenceEquals(firstHit, refreshed) || string.IsNullOrEmpty(shownTooltip) ||
                        !long.TryParse(badge, NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount) || amount <= 0 ||
                        !Flag(updated, known, "Enabled"))
                    {
                        AssertHidden();
                        Need(Time.realtimeSinceStartup - since < 8f,
                            "Native hover did not display an estimate and redraw the value card; badge=" + badge +
                            " shown=" + !string.IsNullOrEmpty(shownTooltip) + " replaced=" + !ReferenceEquals(firstHit, refreshed));
                        return false;
                    }
                    observedBadge = badge;
                    string nativeEstimate = Regex.Replace(LocHelper.GetLocalizedMechanic("mech_tooltip_est_value", badge, 202029), "<[^>]*>", "");
                    Need(nativeEstimate.Contains("202029") && Regex.IsMatch(shownTooltip,
                            Regex.Escape(nativeEstimate).Replace("202029", "[0-9]+")) && DisplaysBadge(updated, known, badge),
                        "The shown native tooltip and the redrawn card disagree on the displayed estimate");
                    Need(Badge(updated, hidden) == ModHelper.GetLocalized(Mod, "f36.noPrice") && !Flag(updated, hidden, "Enabled"),
                        "The unhovered item gained an estimate after another tooltip was shown");
                    AssertHidden();
                    input.Mouse = new Vector3(-1000, -1000);
                    readyFrame = Time.frameCount + 2; Next(6); return false;
                case 6:
                    if (Time.frameCount < readyFrame) return false;
                    var redraw = Window();
                    var choice = redraw.Get("mode").dropdown;
                    choice.SetValueWithoutNotify(0); choice.onValueChanged.Invoke(0);
                    choice = Window().Get("mode").dropdown;
                    choice.SetValueWithoutNotify(1); choice.onValueChanged.Invoke(1);
                    var after = Window();
                    Need(Badge(after, known) == observedBadge && Flag(after, known, "Enabled") &&
                        Badge(after, hidden) == ModHelper.GetLocalized(Mod, "f36.noPrice") && !Flag(after, hidden, "Enabled"),
                        "A mode redraw lost the public estimate or invented an unobserved one");
                    AssertHidden();
                    after.Get("preview").button.onClick.Invoke();
                    var preview = Window();
                    Need(QaItemUi.Hit(preview.root, known) != null && QaItemUi.Hit(preview.root, hidden) != null &&
                        Flag(preview, known, "Selected") && !Flag(preview, hidden, "Selected") &&
                        GroupHasReason(preview, hidden, ModHelper.GetLocalized(Mod, "f36.leave_unknown")),
                        "Preview did not select the observed item and explain the unobserved item's exclusion");
                    Need(known.parentInventory == ground && hidden.parentInventory == ground,
                        "Value-mode preview moved an item");
                    Need(!closedContainer.contentWindow.IsVisible() && concealed.parentInventory == closedContainer.contentWindow.childElement,
                        "Value-mode preview opened or changed the closed container");
                    AssertHidden();
                    Cleanup();
                    result = "PASS F36 native pointer hover, publicly displayed estimate, redraw, unknown-value exclusion and closed-container privacy";
                    return true;
            }
            throw new InvalidOperationException("Unknown field value QA step " + step);
        }
        catch { Cleanup(); throw; }
    }
}
