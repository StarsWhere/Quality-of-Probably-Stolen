using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Exercises categorized tools, stale availability and the settings jump in the guarded Player.
public sealed class SettingsToolsScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly Dictionary<string, bool> original = new Dictionary<string, bool>();
    private readonly float deadline = Time.realtimeSinceStartup + 110f;
    private GameOptionUIManager options;
    private CustomUIWindow openTools, openCard;
    private GameItemElement selection;
    private float checkAfter;
    private int step;
    private bool restored;
    private static CustomUIWindow Tools() => CustomUIManager.Instance.GetWindow(Mod + ".tools");
    private static CustomUIWindow Native() => CustomUIManager.Instance.GetWindow("game_options");
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch { try { Restore(); } catch (Exception error) { Debug.LogWarning("[PLAYER-QA] settings cleanup failed: " + error); } throw; }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Settings tools scenario timed out at step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            options = StoreUIManager.Instance?.gameOptionUIManager;
            if (options == null || CustomUIManager.Instance == null || EventSystem.current == null) return false;
            foreach (string key in new[] { "f05.enabled", "f07.enabled", "f08.enabled", "f13.enabled", "f22.enabled",
                "f24.enabled", "f25.enabled", "f26.enabled", "f29.enabled", "f30.enabled" })
                original[key] = ((ConfigEntry<bool>)ModConfig.Find(Mod).Get(key)).Value;
            TradeScenario.SetOptions(true, "f07.enabled", "f08.enabled", "f22.enabled");
            TradeScenario.SetOptions(false, "f05.enabled", "f30.enabled");
            options.OpenUI(); Native().Get(Mod + ":tools").button.onClick.Invoke();
            var tools = Tools(); Need(tools?.IsAlive == true, "Tools did not open");
            openTools = tools;
            Need(!ToolVisible("f05.open"), "Disabled features appeared by default");
            Need(tools.Get("show_disabled")?.toggle != null, "Show-disabled control is unavailable");
            tools.Get("show_disabled").SetBool(true, true);
            Need(ToolVisible("f05.open"), "Show-disabled did not expose the disabled feature");
            Need(tools.Get(Mod + ":f07.open")?.button?.interactable == true, "Enabled rule editor is unavailable");
            Need(tools.Get(Mod + ":f08.open")?.button?.interactable == false && Tooltip(tools, Mod + ":f08.open") == ModHelper.GetLocalized(Mod, "availability.selection"),
                "Routing without a selection did not explain why it is unavailable");
            Need(tools.Get(Mod + ":f22.open")?.button?.interactable == false && Tooltip(tools, Mod + ":f22.open") == ModHelper.GetLocalized(Mod, "availability.labeler"),
                "Missing labeler did not report its actual reason");
            ((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f07.enabled")).Set(false);
            tools.Get(Mod + ":f07.open").button.onClick.Invoke();
            Need(CustomUIManager.Instance.GetWindow(Mod + ".routes")?.IsAlive != true &&
                !((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f07.enabled")).Value,
                "A stale button ran or silently re-enabled the feature");
            var toggle = Native().Get(Mod + ":f07.enabled");
            toggle.SetBool(true, true); Native().Get("apply").button.onClick.Invoke();
            Need(ReferenceEquals(Tools(), openTools) && tools.Get(Mod + ":f07.open").button.interactable,
                "Apply did not refresh the enabled action in the existing tools window");
            toggle.SetBool(false, true); Native().Get("apply").button.onClick.Invoke();
            Need(ReferenceEquals(Tools(), openTools) && !tools.Get(Mod + ":f07.open").button.interactable &&
                Tooltip(tools, Mod + ":f07.open") == ModHelper.GetLocalized(Mod, "availability.disabled"),
                "Apply did not refresh the disabled action and tooltip");
            selection = DirectoryMaster.Item("wine_yeast") as GameItemElement;
            Need(selection != null && ItemMultiSelectHandler.current != null, "Missing selection fixture");
            GeneralHelper.SetItemOwned(selection, true);
            var slot = EmporiumEntry.Instance.invElement.TryFindOneValidInventorySlot(selection);
            Need(slot != null && slot.TryAcceptOnce(selection.unitCount) == selection.unitCount, "Could not place selection fixture");
            RoutingScenario.SetSelection(selection);
            checkAfter = Time.unscaledTime + 0.4f; step = 11; return false;
        }
        if (step == 11)
        {
            if (Time.unscaledTime < checkAfter) return false;
            Need(ReferenceEquals(Tools(), openTools) && Tools().Get(Mod + ":f08.open").button.interactable,
                "Selection did not refresh the existing routing action");
            RoutingScenario.SetSelection();
            checkAfter = Time.unscaledTime + 0.4f; step = 12; return false;
        }
        if (step == 12)
        {
            if (Time.unscaledTime < checkAfter) return false;
            Need(ReferenceEquals(Tools(), openTools) && !Tools().Get(Mod + ":f08.open").button.interactable &&
                Tooltip(Tools(), Mod + ":f08.open") == ModHelper.GetLocalized(Mod, "availability.selection"),
                "Clearing the selection did not refresh the routing reason");
            Tools().Get("category_1").button.onClick.Invoke();
            Need(ToolVisible("f24.choose") && ToolVisible("f29.open") && !ToolVisible("f25.open"),
                "Trade category has incorrect visible actions");
            Tools().Get("category_2").button.onClick.Invoke();
            Need(ToolVisible("f30.open") && ToolVisible("f31.open"), "Machine category has incorrect visible actions");
            Tools().Get("category_3").button.onClick.Invoke();
            Need(ToolVisible("f13.close"), "Calendar and workspace category lacks its action");
            Tools().Get("category_0").button.onClick.Invoke();
            Tools().Get("manage").button.onClick.Invoke();
            var control = Native().Get(Mod + ":f05.enabled");
            Need(control?.toggle != null && !((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f05.enabled")).Value,
                "Settings jump changed the saved switch");
            var scroll = control.Rect.GetComponentInParent<ScrollRect>();
            Need(scroll?.viewport != null && scroll.viewport.rect.Contains(scroll.viewport.InverseTransformPoint(control.Rect.TransformPoint(control.Rect.rect.center))),
                "Settings jump did not reveal the target control");
            Native().Get(Mod + ":tools").button.onClick.Invoke();
            Tools().Get("attention").button.onClick.Invoke();
            Need(CustomUIManager.Instance.GetWindow(Mod + ".attention")?.IsAlive == true &&
                !((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f30.enabled")).Value,
                "Attention entry depended on machine overview");
            CustomUIManager.Instance.GetWindow(Mod + ".attention").Close();
            options.CloseUI();
            TradeScenario.SetOptions(false, "f05.enabled", "f07.enabled", "f08.enabled", "f13.enabled", "f22.enabled",
                "f24.enabled", "f25.enabled", "f26.enabled", "f29.enabled");
            step = 2; return false;
        }
        if (step == 2)
        {
            var button = EmporiumEntry.Instance?.invWindow?.handler?.transform.Find("ShopAssist_WindowTools")
                ?.GetComponent<TreeNodeRender>()?.element as MachineryButton;
            if (button == null || button.isDisabled) return false;
            button.Select();
            var card = CustomUIManager.Instance.GetWindow(Mod + ".context");
            Need(card?.Get("manage")?.button != null, "Main warehouse tools lack a unified settings entry");
            card.Get("show_disabled").SetBool(true, true);
            Need(!card.Get("f07.open").button.interactable && Tooltip(card, "f07.open") == ModHelper.GetLocalized(Mod, "availability.disabled"),
                "Disabled warehouse action did not expose its reason");
            openCard = card;
            ((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f07.enabled")).Set(true);
            Need(((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f07.enabled")).Value, "Could not enable warehouse feature");
            checkAfter = Time.unscaledTime + 0.4f; step = 21; return false;
        }
        if (step == 21)
        {
            if (Time.unscaledTime < checkAfter) return false;
            var current = CustomUIManager.Instance.GetWindow(Mod + ".context");
            bool same = ReferenceEquals(current, openCard);
            var action = openCard.Get("f07.open");
            bool enabled = action?.button != null && action.button.interactable;
            if (same && !enabled && Time.unscaledTime < checkAfter + 2f) return false;
            Need(same && enabled, "Warehouse enable refresh: same=" + same + ", enabled=" + enabled);
            ((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f07.enabled")).Set(false);
            checkAfter = Time.unscaledTime + 0.4f; step = 22; return false;
        }
        if (step == 22)
        {
            if (Time.unscaledTime < checkAfter) return false;
            bool disabled = !openCard.Get("f07.open").button.interactable;
            string reason = Tooltip(openCard, "f07.open");
            if ((!disabled || reason != ModHelper.GetLocalized(Mod, "availability.disabled")) && Time.unscaledTime < checkAfter + 2f) return false;
            Need(disabled && reason == ModHelper.GetLocalized(Mod, "availability.disabled"),
                "Warehouse disable refresh lost its tooltip: " + reason);
            Need(openCard.Get("manage")?.button != null, "Warehouse card lost its unified settings entry");
            Restore();
            options.OpenUI();
            Native().Get(Mod + ":tools").button.onClick.Invoke();
            Need(Tools()?.Get("category_4")?.button != null, "Missing top-level playback entry");
            var sameTools = Tools();
            Tools().Get("category_4").button.onClick.Invoke();
            Need(ReferenceEquals(Tools(), sameTools) && Tools().IsAlive && !Tools().Get("category_4").button.interactable,
                "Playback category did not retain the current tools window");
            Tools().Get("manage").button.onClick.Invoke();
            Need(Tools()?.IsAlive != true && Native()?.Get(Mod + ":f42.enabled")?.root != null,
                "Playback management did not open its settings");
            var playbackRect = Native().Get(Mod + ":f42.enabled").Rect;
            var playbackScroll = playbackRect.GetComponentInParent<ScrollRect>();
            Need(playbackScroll?.viewport != null && playbackScroll.viewport.rect.Contains(
                playbackScroll.viewport.InverseTransformPoint(playbackRect.TransformPoint(playbackRect.rect.center))),
                "Playback entry did not focus its top-level setting");
            options.CloseUI();
            result = "PASS categorized tools, Apply and selection refresh, stale action recheck, settings jump and independent attention entries";
            return true;
        }
        return false;
    }
    private static string Text(CustomUIWindow window) => string.Join(" ", window.root.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text));
    private static string Tooltip(CustomUIWindow window, string id) => window.Get(id)?.root?.GetComponent<CustomUITooltipTrigger>()?.tooltipText;
    private static bool ToolVisible(string key) => Tools()?.Get(Mod + ":" + key)?.root?.activeInHierarchy == true;
    private void Restore()
    {
        if (restored || original.Count == 0 || options == null) return;
        if (ItemMultiSelectHandler.current != null) RoutingScenario.SetSelection();
        CustomUIManager.Instance.GetWindow(Mod + ".context")?.Close();
        CustomUIManager.Instance.GetWindow(Mod + ".attention")?.Close();
        Tools()?.Close();
        options.CloseUI(); options.OpenUI();
        foreach (var pair in original) Native().Get(Mod + ":" + pair.Key).SetBool(pair.Value, true);
        Native().Get("apply").button.onClick.Invoke(); options.CloseUI(); restored = true;
    }
}
