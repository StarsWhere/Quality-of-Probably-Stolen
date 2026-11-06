using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

public sealed class FieldScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly string sampleName = "QA label " + Guid.NewGuid().ToString("N").Substring(0, 6);
    private float since = Time.realtimeSinceStartup;
    private int step, attempts;
    private GameItem tool, sample, labelTarget, displayItem, bag, keep, loot, excludedLoot, unknownLoot;
    private GameGridInventory inside;
    private string plan;

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CustomUIWindow W(string id) => CustomUIManager.Instance.GetWindow(Mod + "." + id);
    private static void Action(string key) => RoutingScenario.OpenOptionAction(key);
    private static GameItem New(string id, GameInventory target)
    {
        var it = DirectoryMaster.Item(id); Need(it != null, "Missing fixture " + id); GeneralHelper.SetItemOwned(it); Put(target, it); return it;
    }
    private static void Put(GameInventory to, GameItem it)
    {
        var m = to.TryFindOneValidInventorySlot(it);
        Need(m != null && m.inventory == to && m.targetItem == null && m.TryAcceptOnce() > 0 && it.parentInventory == to, "Cannot place " + it.identifier);
    }
    private static object Field(object value, string name) => value?.GetType().GetField(name, Fields)?.GetValue(value);
    private static bool CellFlag(CustomUIWindow window, GameItem item, string field)
    {
        var hit = QaItemUi.Hit(window.root, item); var entry = Field(hit, "entry"); var flag = Field(entry, field);
        Need(flag is bool, "Missing item-grid state " + field + " for " + item.GetDisplayName(true)); return (bool)flag;
    }
    private static bool SpriteShown(CustomUIWindow window, GameItem item) => item is GameItemElement element &&
        element.background?.image?.sprite != null && window.root.GetComponentsInChildren<Image>(true).Any(image => image.sprite == element.background.image.sprite);
    private static void Shot(string name)
    {
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, name));
    }
    private void Next() { step++; since = Time.realtimeSinceStartup; }
    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 50) throw new TimeoutException("Field QA step " + step);
        if (step == 0) { if (start.Tick()) Next(); return false; }
        var e = EmporiumEntry.Instance; var p = PlayerStore.Instance;
        switch (step)
        {
            case 1:
                if (e.storeInteractables == null || !e.storeInteractables.activeInHierarchy || ItemSelectHandler.current == null) return false;
                TradeScenario.SetOptions(true, "f22.enabled", "f25.enabled", "f29.enabled", "f36.enabled");
                tool = New("labeler_blue", e.invElement); sample = New("wine_yeast", e.invElement); labelTarget = New("wine_yeast", e.invElement);
                ItemSelectHandler.current.TryEquipItem(tool); Need(ItemSelectHandler.current.GetEquippedItem() == tool, "Labeler did not equip");
                tool.Target(sample);
                var blue = LabelerBlueUIManager.Instance;
                Need(blue.panel.activeSelf, "Native blue labeler did not open");
                blue.inputField.SetTextWithoutNotify(sampleName); blue.OnEditEnd(sampleName); blue.CloseUI();
                Action("f22.open");
                var label = W("label"); Need(label?.IsAlive == true && label.Get("apply")?.button != null, "Batch label selection missing");
                Need(QaItemUi.Hit(label.root, labelTarget) != null && SpriteShown(label, labelTarget), "Batch label omitted the actual item card");
                QaItemUi.Click(label, labelTarget); Need(CellFlag(label, labelTarget, "Selected"), "Batch label card did not show selection");
                Shot("qa-field-label-grid.png"); Next(); return false;
            case 2:
                CustomerScenario.RequireCaptionVisible(W("label"), labelTarget);
                Need((ItemSelectHandler.current.GetEquippedItem() == null || ItemSelectHandler.current.GetEquippedItem() == tool) &&
                    CellFlag(W("label"), labelTarget, "Selected"),
                    "Label tool or target selection changed before apply: " + LabelState());
                W("label").Get("apply").button.onClick.Invoke();
                Need(W("label") == null || !W("label").IsAlive, "Batch label did not start: " + LabelState());
                Next(); return false;
            case 3:
                if (GeneralHelper.GetCustomName(labelTarget) != sampleName)
                {
                    Need(Time.realtimeSinceStartup - since < 5f, "Batch label did not finish: " + LabelState());
                    return false;
                }
                Need(GeneralHelper.GetCustomName(sample) == sampleName && labelTarget.parentInventory == e.invElement, "Label copy changed sample or target parent");
                ItemSelectHandler.current.EndSelect();
                displayItem = New("energy_drink", e.invElement); GeneralHelper.TrackBuyPrice(displayItem, 15);
                // Default rules retain two units at source.
                New("energy_drink", e.invElement); New("energy_drink", e.invElement);
                Action("f29.open");
                var list = W("restock"); Need(list?.IsAlive == true, "Restock window did not open");
                QaItemUi.Choose(list, "type", displayItem.GetDisplayName(true)); list = W("restock");
                list.Get("add").button.onClick.Invoke(); W("restock").Get("preview").button.onClick.Invoke();
                list = W("restock");
                Need(displayItem.parentInventory == e.invElement && QaItemUi.Hit(list.root, displayItem) != null && SpriteShown(list, displayItem),
                    "Restock preview moved the item or omitted its native image");
                Shot("qa-field-restock-grid.png"); Next(); return false;
            case 4:
                W("restock").Get("apply").button.onClick.Invoke();
                Need(displayItem.parentInventory == e.showcaseElement && e.invElement.childItems.Count(i => i.identifier == "energy_drink") >= 2,
                    "Restock ignored reserve or failed to place goods");
                W("restock").Close();
                bag = New("backpack_medium", e.invElement); inside = (GameGridInventory)bag.contentWindow.childElement;
                keep = New("wine_yeast_red", inside); e.selectedBackpack = bag; p.visitLeftTonight = 2;
                var site = AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND);
                Need(site != null && site.IsAvailable(), "Native dumping ground unavailable in QA fixture");
                MapUIManager.Instance.Travel(site); Next(); return false;
            case 5:
                if (MapUIManager.Instance.Current?.Id != AfterhourLocation.DUMPING_GROUND || !e.afterhourWindow.IsVisible() ||
                    e.afterhourPocketSlotInvBackpack.childItem != bag) return false;
                attempts = p.scavengingAttempts;
                loot = New("wine_berry", e.afterhourInventory); loot.RemoveGameItemType("WATER");
                GeneralHelper.SetCustomName(loot, "QA included loot");
                excludedLoot = New("processed_meat", e.afterhourInventory); excludedLoot.SetGameItemType("WATER");
                GeneralHelper.SetCustomName(excludedLoot, "QA excluded category");
                unknownLoot = New("processed_juice", e.afterhourInventory); unknownLoot.SetGameItemType("WATER");
                GeneralHelper.SetCustomName(unknownLoot, "QA unknown category");
                unknownLoot.tooltipRichTextFunc = (item, inventory, slot, shape, state) => new RichTextBuilder(40).AddLine("QA undisclosed category");
                Next(); return false;
            case 6:
                if (StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                Action("f36.open");
                var field = W("packing"); Need(field?.IsAlive == true, "Packing window did not open at native dumping ground");
                var categories = field.Get("category")?.dropdown;
                Need(categories != null, "Packing category chooser is missing");
                int water = categories.options.FindIndex(option => option.text == TypeHelper.GetTypeDisplayName("WATER"));
                Need(water >= 0, "A publicly visible category is missing from the chooser");
                categories.SetValueWithoutNotify(water); categories.onValueChanged.Invoke(water);
                field = W("packing"); field.Get("exclude_category").SetBool(true, true); field = W("packing");
                Need(CellFlag(field, loot, "Enabled") && !CellFlag(field, excludedLoot, "Enabled") && !CellFlag(field, unknownLoot, "Enabled"),
                    "The public exclusion did not conservatively retain unknown-category loot");
                field.Get("preview").button.onClick.Invoke(); field = W("packing");
                Need(QaItemUi.Hit(field.root, loot) != null && QaItemUi.Hit(field.root, excludedLoot) != null &&
                    QaItemUi.Hit(field.root, unknownLoot) != null && CellFlag(field, loot, "Selected") &&
                    !CellFlag(field, excludedLoot, "Selected") && !CellFlag(field, unknownLoot, "Selected") &&
                    SpriteShown(field, loot) && SpriteShown(field, excludedLoot) && SpriteShown(field, unknownLoot),
                    "Packing preview did not show the actual included, excluded, and unknown-category items");
                plan = "included=" + loot.GetDisplayName(true) + "; excluded=" + excludedLoot.GetDisplayName(true) + "; unknown=" + unknownLoot.GetDisplayName(true);
                Need(loot.parentInventory == e.afterhourInventory && p.scavengingAttempts == attempts, "Packing preview consumed or rolled loot");
                Shot("qa-field-packing-grid.png"); Next(); return false;
            case 7:
                loot.SetGameItemType("WATER"); W("packing").Get("apply").button.onClick.Invoke();
                Need(loot.parentInventory == e.afterhourInventory && keep.parentInventory == inside,
                    "A category change after preview was committed without revalidation");
                loot.RemoveGameItemType("WATER"); W("packing").Close(); Action("f36.open");
                W("packing").Get("preview").button.onClick.Invoke(); W("packing").Get("apply").button.onClick.Invoke();
                Need(loot.parentInventory == inside && keep.parentInventory == inside && excludedLoot.parentInventory == e.afterhourInventory &&
                    unknownLoot.parentInventory == e.afterhourInventory && p.scavengingAttempts == attempts,
                    "Packing state: plan=" + plan + " bag=" + (loot.parentInventory == inside) + " onGround=" + (loot.parentInventory == e.afterhourInventory) +
                    " pocket=" + (loot.parentInventory == e.afterhourPocketSlotInvLeft || loot.parentInventory == e.afterhourPocketSlotInvRight) +
                    " original=" + (keep.parentInventory == inside) + " rolls=" + (p.scavengingAttempts - attempts));
                W("packing").Close(); MapUIManager.Instance.ReturnNow(); Next(); return false;
            case 8:
                if (MapUIManager.Instance.IsOutside || p.playerLocation != PlayerStore.PlayerLocation.IN_STORE ||
                    bag.parentInventory == e.afterhourPocketSlotInvBackpack) return false;
                Action("f25.open");
                var cargo = W("cargo");
                Need(cargo?.Get("unload")?.button != null && QaItemUi.Hit(cargo.root, loot) != null && SpriteShown(cargo, loot) &&
                    QaItemUi.Hit(cargo.root, keep) == null && CellFlag(cargo, loot, "Selected"),
                    "Return cargo did not show only the newly acquired native item: " + ModHelper.GetModData(Mod, "tripCargo", ""));
                QaItemUi.Click(cargo, loot); Need(!CellFlag(cargo, loot, "Selected"), "Cargo item did not deselect");
                QaItemUi.Click(cargo, loot); Need(CellFlag(cargo, loot, "Selected"), "Cargo item did not reselect");
                Shot("qa-field-cargo-grid.png"); Next(); return false;
            case 9:
                W("cargo").Get("unload").button.onClick.Invoke();
                Need(loot.parentInventory == e.invElement && keep.parentInventory == inside, "Previous equipment unloaded or new loot not transferred");
                Action("f25.open"); Need(W("cargo").Get("unload") == null, "Trip gain could be unloaded twice");
                result = "PASS real labeler and restock cards, public exclusions and unknown-category packing, stale-preview rejection, original equipment and native return unloading";
                return true;
        }
        return false;
    }

    private string LabelState()
    {
        var assembly = ModLoader.Mods.FirstOrDefault(mod => mod.Manifest?.ID == Mod)?.Instance?.GetType().Assembly;
        var type = assembly?.GetType("ShopAssist.BatchLabeler");
        object active = type?.GetField("active", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        object queued = Field(active, "queue");
        int queue = queued is System.Collections.ICollection collection ? collection.Count : -1;
        return "phase=" + Field(active, "phase") + " done=" + Field(active, "done") + " skipped=" + Field(active, "skipped") +
            " queued=" + queue + " hasTool=" + type?.GetProperty("HasTool", Fields)?.GetValue(active) +
            " equipped=" + ReferenceEquals(ItemSelectHandler.current?.GetEquippedItem(), tool) +
            " tool=" + ReferenceEquals(Field(active, "tool"), tool) + " sample=" + ReferenceEquals(Field(active, "sample"), sample) +
            " editing=" + ReferenceEquals(Field(active, "editing"), labelTarget) + " sampleName=" + GeneralHelper.GetCustomName(sample) +
            " targetName=" + GeneralHelper.GetCustomName(labelTarget) + " source=" + ReferenceEquals(labelTarget?.parentInventory, EmporiumEntry.Instance.invElement) +
            " blueOpen=" + (LabelerBlueUIManager.Instance?.panel?.activeSelf == true);
    }
}
