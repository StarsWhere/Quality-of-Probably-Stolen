using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Tests explicit rule editing and routing only in a newly created isolated run.
public sealed class RoutingScenario
{
    private const string Mod = "starswhere.shopassist";
    private const string Rules = Mod + ".routes", Preview = Mod + ".route_preview";
    private readonly QaNewRun start = new QaNewRun();
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 8);
    private GameItemElement target, first, second, blocker;
    private GameGridInventory inside;
    private QaLoadSlot load;
    private string run, savedRule;
    private int slot, day, step;
    private float since = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 75) throw new InvalidOperationException("Routing QA timed out at step " + step);
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                Advance(1); return false;
            case 1:
                if (StoreUIManager.Instance?.gameOptionUIManager == null || EmporiumEntry.Instance?.invElement == null ||
                    ItemMouseDoubleClickHandler.current == null || ItemMultiSelectHandler.current == null) return false;
                Prepare();
                Advance(2); return false;
            case 2:
                CreateFoodRule(target);
                GeneralHelper.SetCustomName(target, "QA Renamed " + token);
                savedRule = ModHelper.GetModData(Mod, "routing", null);
                Require(!string.IsNullOrEmpty(savedRule) && target.uniqueId > 0, "F07 did not save an instance-bound rule");
                OpenPreview();
                Advance(3); return false;
            case 3:
                VerifyPreview();
                Put(inside, blocker);
                Button(Window(Preview), "f08.apply").onClick.Invoke();
                Require(ReferenceEquals(first.parentInventory, EmporiumEntry.Instance.invElement) &&
                    ReferenceEquals(second.parentInventory, EmporiumEntry.Instance.invElement),
                    "F09 committed a stale preview after a destination changed");
                Put(EmporiumEntry.Instance.invElement, blocker);
                OpenPreview();
                Advance(4); return false;
            case 4:
                VerifyPreview();
                Button(Window(Preview), "f08.apply").onClick.Invoke();
                Require(ReferenceEquals(first.parentInventory, inside) && ReferenceEquals(second.parentInventory, inside) &&
                    inside.childItems.Contains(first) && inside.childItems.Contains(second) && first.unitCount == 1 && second.unitCount == 1,
                    "F08 did not move exactly the selected items to the tagged area");
                Require(ModHelper.GetModData(Mod, "routing", null) == savedRule, "F08 mutated F07 rules");
                slot = PlayerStore.Instance.saveSlotId; run = PlayerStore.Instance.runID; day = StoreStation.GetDayCounter();
                PlayerStore.Instance.SaveGame();
                StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                load = new QaLoadSlot(slot, Guid.Parse(run));
                Advance(5); return false;
            case 5:
                if (!load.Tick()) return false;
                Require(StoreStation.GetDayCounter() == day && ModHelper.GetModData(Mod, "routing", null) == savedRule,
                    "F07 rule was not preserved in the same save on reload");
                var bag = EmporiumEntry.Instance.invElement.childItems.SingleOrDefault(item => item.GetDisplayName(true) == "QA Renamed " + token);
                Require(bag != null && bag.uniqueId > 0 && bag.contentWindow?.childElement is GameGridInventory,
                    "Renamed target or its instance ID did not survive reload");
                var content = (GameGridInventory)bag.contentWindow.childElement;
                Require(content.childItems.Any(item => item.GetDisplayName(true) == "QA Route A " + token) &&
                    content.childItems.Any(item => item.GetDisplayName(true) == "QA Route B " + token),
                    "Moved goods did not survive native save/load");
                result = "PASS F07 instance rule and rename/save, F09 side-effect-free preview and stale rejection, F08 selected intact goods";
                return true;
            default: throw new InvalidOperationException("Unknown routing QA step");
        }
    }

    private void Prepare()
    {
        TradeScenario.SetOptions(true, "f07.enabled", "f08.enabled", "f09.enabled");
        target = NewItem("backpack_medium", "QA Target " + token);
        first = NewItem("processed_meat", "QA Route A " + token);
        second = NewItem("processed_juice", "QA Route B " + token);
        blocker = NewItem("wine_yeast", "QA Blocker " + token);
        var stock = EmporiumEntry.Instance.invElement;
        Put(stock, target); Put(stock, first); Put(stock, second); Put(stock, blocker);
        inside = target.contentWindow?.childElement as GameGridInventory;
        Require(inside != null, "QA target does not have an ordinary grid");
        ItemMouseDoubleClickHandler.current.OpenContentAction(target, Vector2.zero);
    }

    internal static void CreateFoodRule(GameItem target) => CreateCategoryRule(target, "FOOD");

    internal static void CreateCategoryRule(GameItem target, string type)
    {
        RunFeatureAction("f07.open");
        var window = Window(Rules);
        QaItemUi.Choose(window, "area", target.GetDisplayName(true));
        window = Window(Rules);
        Require(window.Get("receive")?.toggle != null && window.Get("receive").toggle.interactable, "Ordinary target cannot receive");
        window.Get("receive").SetBool(true, true);
        var category = window.Get("category")?.dropdown;
        Require(category != null, "F07 category chooser missing");
        int typeIndex = category.options.FindIndex(option => option.text == TypeHelper.GetTypeDisplayName(type));
        Require(typeIndex >= 0, "F07 category is unavailable: " + type);
        category.SetValueWithoutNotify(typeIndex); category.onValueChanged.Invoke(typeIndex);
        window.Get("add_category").button.onClick.Invoke();
        Window(Rules).Get("save").button.onClick.Invoke();
        Window(Rules).Close();
    }

    private void OpenPreview()
    {
        SetSelection(first, second);
        RunFeatureAction("f08.open");
        Require(CustomUIManager.Instance.GetWindow(Preview) != null, "F09 preview did not open");
    }

    private void VerifyPreview()
    {
        var preview = Window(Preview);
        if (!preview.root.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text.Contains(target.GetDisplayName(true))))
            Debug.Log("[PLAYER-QA] routing guard " + GuardDiagnostics(first, second, EmporiumEntry.Instance.invElement, inside, target) + " preview=" +
                string.Join(" | ", preview.root.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text)));
        Require(preview.root.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text.Contains(target.GetDisplayName(true))),
            "F09 did not show the renamed destination");
        Button apply = Button(preview, "f08.apply");
        Require(apply != null && apply.interactable, "F09 has no confirmed movable goods");
        Require(ReferenceEquals(first.parentInventory, EmporiumEntry.Instance.invElement) &&
            ReferenceEquals(second.parentInventory, EmporiumEntry.Instance.invElement) && inside.childItems.Count == 0,
            "F09 preview changed the inventory");
    }

    internal static string GuardDiagnostics(GameItem first, GameItem second, GameInventory source, GameGridInventory inside, GameItem target)
    {
        var mod = ModLoader.Mods.FirstOrDefault(value => value.Manifest?.ID == Mod && value.Instance != null);
        var assembly = mod?.Instance.GetType().Assembly;
        var safety = assembly?.GetType("ShopAssist.InventoryAutoInsert");
        var transfer = assembly?.GetType("ShopAssist.WholeStackTransfer");
        var cargo = assembly?.GetType("ShopAssist.TripCargo");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        string Inspect(Type type, string method, params object[] args)
        {
            try { return method + "=" + type?.GetMethod(method, flags)?.Invoke(null, args); }
            catch (Exception error) { return method + "=" + error.GetBaseException().Message; }
        }
        string Callbacks(object obj)
        {
            string[] names = { "mayInventoryAddItemFunc", "mayInventoryRemoveItemFunc", "mayStackItemFunc", "maxSlotAddItemFunc",
                "maxSlotRemoveItemFunc", "maxSlotAmountItemFunc", "minSlotAmountItemFunc", "isItemStateValidFunc", "onSlotAddItemFunc",
                "onSlotAddItemEndFunc", "onSlotAddItemLateFunc", "onSlotRemoveItemFunc", "onSlotRemoveItemEndFunc",
                "onSlotRemoveItemLateFunc", "onSlotChangeAmountItemFunc", "onChangeAmountItemFunc", "parentItemShapeModifierForItemFunc",
                "parentItemStateModifierForItemFunc", "parentItemSpriteModifierForItemFunc", "getItemSpriteModifierForItemFunc", "slotPriorityFunc" };
            return string.Join("/", names.Select(name => (name, callback: obj.GetType().GetProperty(name)?.GetValue(obj) as Delegate))
                .Where(pair => pair.callback != null).SelectMany(pair => pair.callback.GetInvocationList().Select(callback =>
                    pair.name + ":" + callback.Method.DeclaringType?.FullName + "." + callback.Method.Name + "@" + callback.Method.Module.Assembly.GetName().Name)));
        }
        string Item(GameItem item) => item.identifier + "[" + Callbacks(item) + "] features=" +
            (item.itemFeatures == null ? "null" : string.Join("/", item.itemFeatures.Select(feature => feature == null ? "null" :
                feature.featureType + ":" + feature.category))) + " " + Inspect(safety, "SafeItem", item, true, true) +
            " " + Inspect(transfer, "SafeSource", item) + " back=" + (item.GetParentWithIdentifier("backInv") != null) +
            " " + Inspect(cargo, "IsReturnedCargoInCounter", item);
        var methods = safety?.GetField("NativeMutations", flags)?.GetValue(null) as MethodBase[];
        string badPatches = methods == null ? "unavailable" : string.Join("/", methods.Where(method => method == null ||
            !(bool)safety.GetMethod("KnownPatches", flags).Invoke(null, new object[] { method })).Select(method => method?.ToString() ?? "missing"));
        string UnsafeContents(GameInventory inventory) => string.Join("/", inventory.childItems.Where(item =>
            !(bool)safety.GetMethod("SafeVisualItem", flags).Invoke(null, new object[] { item }))
            .Select(item => item.identifier + "[" + Callbacks(item) + "]"));
        return Inspect(safety, "KnownMutationPatches") + " bad=" + badPatches + " source=" + Inspect(safety, "SafeGrid", source) +
            " " + Inspect(safety, "SafeTransferInventory", source, first) + "[" + Callbacks(source) + "] unsafe children=" + UnsafeContents(source) +
            " target=" + Inspect(safety, "SafeGrid", inside) + " " + Inspect(safety, "SafeTransferInventory", inside, null) +
            "[" + Callbacks(inside) + "] unsafe children=" + UnsafeContents(inside) + " parent=" + Inspect(safety, "SafeVisualItem", target) +
            "[" + Callbacks(target) + "] parent inventory=" + target.parentInventory?.identifier +
            " unsafe ancestor children=" + (target.parentInventory == null ? "none" : UnsafeContents(target.parentInventory)) +
            " " + Item(first) + " " + Item(second);
    }

    internal static void SetSelection(params GameItemElement[] items)
    {
        // This drives the game's actual selection snapshot without synthesizing a desktop drag.
        var handler = ItemMultiSelectHandler.current;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(ItemMultiSelectHandler);
        var selectedField = type.GetField("selectedItems", flags);
        var homesField = type.GetField("selectedHomes", flags);
        var stateField = type.GetField("state", flags);
        var selected = selectedField?.GetValue(handler) as IList;
        var homes = homesField?.GetValue(handler) as IDictionary;
        Require(selected != null && homes != null && stateField != null, "Native multi-selection interface changed");
        selected.Clear(); homes.Clear();
        foreach (var item in items) { selected.Add(item); homes.Add(item, item.parentInventory); }
        stateField.SetValue(handler, Enum.Parse(stateField.FieldType, "Selected"));
    }

    private static GameItemElement NewItem(string id, string name)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement;
        Require(item != null, "Missing native fixture " + id);
        GeneralHelper.SetItemOwned(item, true);
        GeneralHelper.SetCustomName(item, name);
        return item;
    }
    private static void Put(GameInventory inventory, GameItem item)
    {
        var slot = inventory.TryFindOneValidInventorySlot(item);
        Require(slot != null && slot.inventory == inventory && slot.targetItem == null &&
            slot.numTransfer >= item.unitCount && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory,
            "Could not place test fixture " + item.identifier);
    }
    internal static void RunFeatureAction(string id)
    {
        var options = StoreUIManager.Instance.gameOptionUIManager;
        if (options != null && options.IsOpen)
        {
            options.CloseUI();
            Require(!options.IsOpen, "Close the options draft before invoking " + id);
        }
        QaFeatureGallery.Action(id);
    }
    internal static CustomUIWindow Window(string id) => CustomUIManager.Instance.GetWindow(id) ??
        throw new InvalidOperationException("Missing window " + id);
    internal static Button Button(CustomUIWindow window, string key)
    {
        string label = ModHelper.GetLocalized(Mod, key);
        return window.root.GetComponentsInChildren<Button>(true).FirstOrDefault(button => button.gameObject.activeInHierarchy &&
            button.GetComponentInChildren<TMP_Text>(true)?.text == label);
    }
    private void Advance(int value) { step = value; since = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
