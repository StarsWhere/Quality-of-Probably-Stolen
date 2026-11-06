using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

// Native feature selectors, including a delayed click and an item moving while search is open.
public sealed class PickerLifecycleScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90;
    private GameItem furnace, firstBag, secondBag, ore, jug;
    private GameGridInventory firstGrid, secondGrid;
    private CustomUIWindow picker, parent;
    private int step, frame;
    private string capture;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Picker lifecycle timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                StoreUIManager.Instance?.gameOptionUIManager == null || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f26.enabled", "f33.enabled");
            TradeScenario.SetOptions(false, "f15.enabled", "f17.enabled", "f21.enabled");
            var stock = EmporiumEntry.Instance.invElement;
            furnace = Item("furnace", "QA picker furnace");
            firstBag = Item("backpack_medium", "QA picker source A");
            secondBag = Item("backpack_medium", "QA picker source B");
            ore = Item("scrap_metal", "QA moved picker material");
            jug = Item("water_jug", "QA picker destination jug");
            WaterHelper.Remove(jug, WaterHelper.GetCurrentCapacityUL(jug));
            Put(stock, furnace); Put(stock, firstBag); Put(stock, secondBag); Put(stock, jug);
            firstGrid = firstBag.contentWindow.childElement as GameGridInventory;
            secondGrid = secondBag.contentWindow.childElement as GameGridInventory;
            Need(firstGrid != null && secondGrid != null, "Picker source grids unavailable");
            Put(firstGrid, ore);
            ItemMouseDoubleClickHandler.current.OpenContentAction(furnace, new Vector2(250, 200));
            furnace.contentWindow.ToFront();
            RoutingScenario.OpenOptionAction("f33.open");
            parent = Window("f33");
            picker = QaItemUi.OpenPicker(parent, "source");
            Capture("feed-source"); step = 11; return false;
        }
        if (step == 11)
        {
            if (!CaptureReady()) return false;
            string sourceDetails = QaItemUi.Describe(picker.root, firstBag, secondBag);
            Debug.Log("[PLAYER-QA] feed source picker: " + sourceDetails);
            Need(QaItemUi.Hit(picker.root, firstBag) != null, "Original source bag has no selectable item cell: " + sourceDetails);
            QaItemUi.Click(picker, firstBag);
            parent = Window("f33");
            picker = QaItemUi.OpenPicker(parent, "material");
            Capture("feed-material"); step = 12; return false;
        }
        if (step == 12)
        {
            if (!CaptureReady()) return false;
            string materialDetails = QaItemUi.Describe(picker.root, ore);
            Debug.Log("[PLAYER-QA] feed material picker: " + materialDetails);
            Need(QaItemUi.Hit(picker.root, ore) != null, "Original source material is absent from picker: " + materialDetails);
            Put(secondGrid, ore);
            Need(ore.parentInventory == secondGrid, "Could not move picker material to the other source");
            TMP_InputField search = picker.Get("query")?.root?.GetComponentInChildren<TMP_InputField>(true);
            Need(search != null, "Picker search is unavailable");
            search.text = "QA moved picker material";
            Need(QaItemUi.Hit(picker.root, ore) == null, "Searching rebound the moved item to its new source");
            Need(picker.Get("changed")?.root?.activeInHierarchy == true, "Moved source was not reported to the player");
            Need(QaItemUi.Hit(parent.root, ore) == null && string.IsNullOrEmpty(ModHelper.GetModData(Mod, "f33.rules", "")),
                "A moved material was selected or persisted against the old source");
            picker.Close(); parent.Close();
            RoutingScenario.OpenOptionAction("f26.open");
            parent = Window("water"); picker = QaItemUi.OpenPicker(parent, "target");
            Capture("water-target"); step = 13; return false;
        }
        if (step == 13)
        {
            if (!CaptureReady()) return false;
            string targetDetails = QaItemUi.Describe(picker.root, jug);
            Debug.Log("[PLAYER-QA] water target picker: " + targetDetails);
            var delayed = QaItemUi.Hit(picker.root, jug);
            Need(delayed != null, "Water destination is absent from picker: " + targetDetails);
            parent.Close();
            // A queued click can arrive before the next feature Tick closes the selector.
            QaItemUi.Click(delayed);
            Need(!parent.IsAlive && !picker.IsAlive && CustomUIManager.Instance.GetWindow(Mod + ".water")?.IsAlive != true,
                "A delayed picker click reopened its closed parent");
            RoutingScenario.OpenOptionAction("f26.open");
            parent = Window("water"); picker = QaItemUi.OpenPicker(parent, "target");
            Need(QaItemUi.Hit(picker.root, jug) != null, "Water selector did not reopen");
            parent.Close(); frame = Time.frameCount + 3; step = 2; return false;
        }
        if (step == 2)
        {
            if (Time.frameCount < frame) return false;
            Need(!picker.IsAlive && CustomUIManager.Instance.GetWindow(Mod + ".water")?.IsAlive != true,
                "Closing a parent left its picker active");
            result = "PASS moved material stays bound to its original source across picker search; closed parents cancel delayed clicks and selectors";
            return true;
        }
        throw new InvalidOperationException("Unknown picker lifecycle step");
    }

    private static CustomUIWindow Window(string name)
    {
        var window = CustomUIManager.Instance.GetWindow(Mod + "." + name);
        Need(window?.IsAlive == true, "Missing window " + name); return window;
    }
    private static GameItem Item(string id, string label)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing fixture " + id);
        GeneralHelper.SetItemOwned(item, true); GeneralHelper.SetCustomName(item, label); return item;
    }
    private static void Put(GameInventory inventory, GameItem item)
    {
        var marker = inventory.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.targetItem == null && marker.TryAcceptOnce(item.unitCount) == item.unitCount &&
            item.parentInventory == inventory, "Could not move " + item.identifier);
    }
    private void Capture(string name)
    {
        capture = Path.Combine(Application.persistentDataPath, "qa-picker-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
        ScreenCapture.CaptureScreenshot(capture);
        Debug.Log("[PLAYER-QA] picker diagnostic capture " + capture);
        frame = Time.frameCount + 4;
    }
    private bool CaptureReady() => Time.frameCount >= frame && File.Exists(capture) && new FileInfo(capture).Length > 0;
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
