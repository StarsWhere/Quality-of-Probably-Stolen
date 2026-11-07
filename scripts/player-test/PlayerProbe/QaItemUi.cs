using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

// Exercises the rendered item controls; never calls the feature's private mutation methods.
internal static class QaItemUi
{
    internal static Transform Descendant(Transform root, string name) => root?.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(node => node.name == name && node.gameObject.activeInHierarchy);
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Field(object value, string name) => value?.GetType().GetField(name, Flags)?.GetValue(value);
    internal static MonoBehaviour Hit(GameObject root, GameItem item = null, string name = null) => root.GetComponentsInChildren<MonoBehaviour>(true)
        .FirstOrDefault(hit => hit != null && hit.gameObject.activeInHierarchy && hit.gameObject.name == "item_cell" &&
            hit.GetType().FullName == "ShopAssist.ItemGrid+Hit" && Field(hit, "entry") is object entry &&
            (item == null || ReferenceEquals(Field(entry, "Item"), item)) &&
            (name == null || ((GameItem)Field(entry, "Item")).GetDisplayName(true).Contains(name)));
    internal static void Click(MonoBehaviour hit)
    {
        if (hit == null) throw new InvalidOperationException("No matching rendered item cell");
        Canvas.ForceUpdateCanvases();
        var rect = (RectTransform)hit.transform; var canvas = rect.GetComponentInParent<Canvas>();
        var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left,
            position = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center)) };
        ExecuteEvents.Execute(hit.gameObject, pointer, ExecuteEvents.pointerClickHandler);
    }
    internal static void Click(CustomUIWindow window, GameItem item) => Click(Hit(window.root, item));
    internal static MachineryButton WindowAction(PixelWindow window, string key) =>
        window?.handler?.transform.Find("ShopAssist_Action_" + key.Replace('.', '_'))
            ?.GetComponent<TreeNodeRender>()?.element as MachineryButton;

    internal static bool OpenAttention()
    {
        var button = WindowAction(EmporiumEntry.Instance?.invWindow, "attention");
        if (button == null || button.isDisabled) return false;
        button.Select();
        var attention = CustomUIManager.Instance.GetWindow("starswhere.shopassist.attention");
        if (attention?.IsAlive != true) throw new InvalidOperationException("Direct attention action did not open its list");
        return true;
    }

    internal static CustomUIWindow OpenOperationDetails()
    {
        const string mod = "starswhere.shopassist";
        var assembly = ModLoader.Mods.First(value => value.Manifest?.ID == mod).Instance.GetType().Assembly;
        assembly.GetType("ShopAssist.ShopNotice", true).GetMethod("OpenLastResult", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, null);
        var result = CustomUIManager.Instance.GetWindow(mod + ".operation");
        if (result?.IsAlive != true || !result.root.activeInHierarchy) throw new InvalidOperationException("Operation details did not remain visible");
        return result;
    }
    internal static bool Selected(CustomUIWindow window, GameItem item) =>
        Field(Field(Hit(window.root, item), "entry"), "Selected") is bool value && value;
    internal static string Describe(GameObject root, params GameItem[] targets)
    {
        if (root == null) return "root=null";
        var hits = root.GetComponentsInChildren<MonoBehaviour>(true)
            .Where(hit => hit != null && hit.GetType().FullName == "ShopAssist.ItemGrid+Hit")
            .Select(hit => new { Hit = hit, Entry = Field(hit, "entry") })
            .Select(row => new { row.Hit, Item = Field(row.Entry, "Item") as GameItem, Shape = Field(row.Entry, "Shape") as GridShape })
            .Where(row => row.Item != null).ToArray();
        string Details(GameItem item)
        {
            var matching = hits.Where(row => ReferenceEquals(row.Item, item)).ToArray();
            var chain = new System.Collections.Generic.List<string>();
            for (Transform node = matching.FirstOrDefault()?.Hit?.transform; node != null && chain.Count < 18; node = node.parent)
                chain.Add(node.name + "(self=" + node.gameObject.activeSelf + ",hierarchy=" + node.gameObject.activeInHierarchy + ")");
            return (item?.GetDisplayName(true) ?? "null") + " parent=" + item?.parentInventory?.GetType().Name +
                " hits=" + string.Join(",", matching.Select(row => row.Hit.gameObject.name + ":active=" + row.Hit.gameObject.activeInHierarchy +
                    ":shape=" + (row.Shape == null ? "null" : row.Shape.globalWidth + "x" + row.Shape.globalHeight))) +
                " strict=" + (Hit(root, item) != null) + " ancestors=" + string.Join(" <- ", chain);
        }
        string entries = string.Join(", ", hits.Where(row => row.Hit.gameObject.name == "card")
            .Select(row => row.Item.GetDisplayName(true) + ":active=" + row.Hit.gameObject.activeInHierarchy));
        return "root=" + root.name + "(self=" + root.activeSelf + ",hierarchy=" + root.activeInHierarchy + ") | " +
            string.Join(" | ", targets.Select(Details)) + " | card entries: " + entries;
    }
    private static void ClickSelector(CustomUIWindow window, string id)
    {
        var slot = window.Get(id + "_slot")?.root;
        if (slot == null) throw new InvalidOperationException("Missing visual selector: " + id);
        var hit = Hit(slot);
        if (hit != null) Click(hit);
        else
        {
            var button = window.Get(id)?.button;
            if (button == null || !button.interactable) throw new InvalidOperationException("Visual selector unavailable: " + id);
            button.onClick.Invoke();
        }
    }
    internal static CustomUIWindow OpenPicker(CustomUIWindow window, string id)
    {
        ClickSelector(window, id);
        var picker = CustomUIManager.Instance.GetWindow("starswhere.shopassist.choose");
        if (picker == null) throw new InvalidOperationException("Visual picker did not open: " + id);
        return picker;
    }
    internal static void Choose(CustomUIWindow window, string id, string itemName)
    {
        string windowId = window.id;
        ClickSelector(window, id);
        var picker = CustomUIManager.Instance.GetWindow("starswhere.shopassist.choose");
        if (picker == null || !picker.IsAlive)
        {
            var updated = CustomUIManager.Instance.GetWindow(windowId);
            var slot = updated?.Get(id + "_slot")?.root;
            var hit = slot == null ? null : Hit(slot, name: itemName);
            bool selected = hit != null && Field(Field(hit, "entry"), "Selected") is bool value && value;
            string rootName = updated?.Get(id)?.GetText();
            if (updated?.IsAlive == true && updated.root.activeInHierarchy &&
                (selected || rootName != null && rootName.Contains(itemName))) return;
            throw new InvalidOperationException("Visual selector neither opened nor selected " + itemName);
        }
        for (int page = 0; page < 20; page++)
        {
            var hit = Hit(picker.root, name: itemName);
            if (hit != null) { Click(hit); return; }
            var next = picker.Get("next")?.button;
            if (next == null) break;
            next.onClick.Invoke(); picker = CustomUIManager.Instance.GetWindow("starswhere.shopassist.choose");
        }
        throw new InvalidOperationException("No eligible item named " + itemName);
    }
}
