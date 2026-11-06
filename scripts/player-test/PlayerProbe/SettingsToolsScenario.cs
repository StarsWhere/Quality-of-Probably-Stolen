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
            TradeScenario.SetOptions(false, "f30.enabled");
            options.OpenUI(); Native().Get(Mod + ":tools").button.onClick.Invoke();
            var tools = Tools(); Need(tools?.IsAlive == true, "Tools did not open");
            openTools = tools;
            Need(tools.Get(Mod + ":f07.open")?.button?.interactable == true, "Enabled rule editor is unavailable");
            Need(tools.Get(Mod + ":f08.open")?.button?.interactable == false && Text(tools).Contains(ModHelper.GetLocalized(Mod, "availability.selection")),
                "Routing without a selection did not explain why it is unavailable");
            Need(tools.Get(Mod + ":f22.open")?.button?.interactable == false && Text(tools).Contains(ModHelper.GetLocalized(Mod, "availability.labeler")),
                "Missing labeler did not report its actual reason");
            ((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f07.enabled")).Set(false);
            tools.Get(Mod + ":f07.open").button.onClick.Invoke();
            Need(CustomUIManager.Instance.GetWindow(Mod + ".routes")?.IsAlive != true &&
                !((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f07.enabled")).Value,
                "A stale button ran or silently re-enabled the feature");
            var toggle = Native().Get(Mod + ":f07.enabled");
            toggle.SetBool(true, true); Native().Get("apply").button.onClick.Invoke();
            Need(ReferenceEquals(Tools(), openTools) && tools.Get(Mod + ":f07.open").button.interactable &&
                !tools.Get("reason_f07.open").root.activeSelf && !tools.Get("setting_f07.open").root.activeSelf,
                "Apply did not refresh the enabled action in the existing tools window");
            toggle.SetBool(false, true); Native().Get("apply").button.onClick.Invoke();
            Need(ReferenceEquals(Tools(), openTools) && !tools.Get(Mod + ":f07.open").button.interactable &&
                tools.Get("reason_f07.open").root.activeSelf && tools.Get("setting_f07.open").root.activeSelf &&
                tools.Get("reason_f07.open").GetText() == ModHelper.GetLocalized(Mod, "availability.disabled"),
                "Apply did not refresh the disabled action, reason and settings jump");
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
            Need(ReferenceEquals(Tools(), openTools) && Tools().Get(Mod + ":f08.open").button.interactable &&
                !Tools().Get("reason_f08.open").root.activeSelf,
                "Selection did not refresh the existing routing action");
            RoutingScenario.SetSelection();
            checkAfter = Time.unscaledTime + 0.4f; step = 12; return false;
        }
        if (step == 12)
        {
            if (Time.unscaledTime < checkAfter) return false;
            Need(ReferenceEquals(Tools(), openTools) && !Tools().Get(Mod + ":f08.open").button.interactable &&
                Tools().Get("reason_f08.open").GetText() == ModHelper.GetLocalized(Mod, "availability.selection"),
                "Clearing the selection did not refresh the routing reason");
            Tools().Get("category_1").button.onClick.Invoke();
            Need(Tools().Get(Mod + ":f24.choose") != null && Tools().Get(Mod + ":f29.open") != null &&
                Tools().Get(Mod + ":f25.open") == null, "Trade category has incorrect actions");
            Tools().Get("category_2").button.onClick.Invoke();
            Need(Tools().Get(Mod + ":f30.open") != null && Tools().Get(Mod + ":f31.open") != null, "Machine category has incorrect actions");
            Tools().Get("category_3").button.onClick.Invoke();
            Need(Tools().Get(Mod + ":f13.close") != null, "Calendar and workspace category lacks its action");
            Tools().Get("category_0").button.onClick.Invoke();
            Tools().Get("setting_f07.open").button.onClick.Invoke();
            var control = Native().Get(Mod + ":f07.enabled");
            Need(control?.toggle != null && !((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f07.enabled")).Value,
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
            Need(card?.Get("attention")?.button != null, "Main warehouse tools lack an attention entry");
            Need(!card.Get("f07.open").button.interactable && card.Get("setting_f07.open").root.activeSelf,
                "Disabled warehouse action did not expose its settings link");
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
            var reason = openCard.Get("reason_f07.open");
            var setting = openCard.Get("setting_f07.open");
            bool enabled = action?.button != null && action.button.interactable;
            bool reasonVisible = reason?.root != null && reason.root.activeInHierarchy;
            bool settingVisible = setting?.root != null && setting.root.activeInHierarchy;
            if (same && (!enabled || reasonVisible || settingVisible) && Time.unscaledTime < checkAfter + 2f) return false;
            Need(same && enabled && !reasonVisible && !settingVisible,
                "Warehouse enable refresh: same=" + same + ", enabled=" + enabled + ", reasonVisible=" + reasonVisible +
                ", settingVisible=" + settingVisible + ", saved=" + ((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f07.enabled")).Value +
                ", reason=" + reason?.GetText());
            ((ConfigEntry<bool>)ModConfig.Find(Mod).Get("f07.enabled")).Set(false);
            checkAfter = Time.unscaledTime + 0.4f; step = 22; return false;
        }
        if (step == 22)
        {
            if (Time.unscaledTime < checkAfter) return false;
            bool disabled = !openCard.Get("f07.open").button.interactable;
            bool reasonVisible = openCard.Get("reason_f07.open").root.activeInHierarchy;
            bool settingVisible = openCard.Get("setting_f07.open").root.activeInHierarchy;
            if ((!disabled || !reasonVisible || !settingVisible) && Time.unscaledTime < checkAfter + 2f) return false;
            Need(disabled && reasonVisible && settingVisible,
                "Warehouse disable refresh: disabled=" + disabled + ", reasonVisible=" + reasonVisible + ", settingVisible=" + settingVisible);
            openCard.Get("attention").button.onClick.Invoke();
            Need(CustomUIManager.Instance.GetWindow(Mod + ".attention")?.IsAlive == true,
                "Main warehouse attention entry did not open");
            Restore();
            result = "PASS categorized tools, Apply and selection refresh, stale action recheck, settings jump and independent attention entries";
            return true;
        }
        return false;
    }
    private static string Text(CustomUIWindow window) => string.Join(" ", window.root.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text));
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
