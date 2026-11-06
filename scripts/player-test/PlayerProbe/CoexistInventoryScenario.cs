using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Exercises the installed inventory peers in the isolated player-test save.
public sealed class CoexistInventoryScenario
{
    private const string Mod = "starswhere.shopassist";
    private const string ListId = Mod + ".customer_inventory";
    private readonly QaNewRun start = new QaNewRun();
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 6);
    private float since = Time.realtimeSinceStartup;
    private int step;
    private GameItemElement bag, nested, locked, secret, machine, seed, food, water, stolen, reserved;
    private StoreRequest order;
    private string peerSettings;
    private float transitionAt;
    private PixelWindow menuView;
    private RichTextElement scanButton, holdButton;
    private RichTextElement helperSort, peerSort, routeButton;
    private GridShape beforePeerNested, beforePeerStolen;
    private int beforePeerNestedCount, beforePeerStolenCount;
    private int menuFrame, nativeMenuHeight, nativeMenuWidth;
    private float nameVisibleAt;
    private Vector2 fittedMenuPosition;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - since < 100, "Inventory coexistence timed out at step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                ItemContextHandler.current == null || NegociationUIManager.Instance == null || WaterScannerUIManager.Instance == null || PCUIManager.Instance == null) return false;
            foreach (string id in new[] { "hollow.clientdemandhighlight", "ShowItemNames", "smart_inventory_arrange", "item_details" }) Peer(id);
            peerSettings = PeerSettings();
            Prepare();
            step++; return false;
        }
        if (step == 2)
        {
            Transform name = bag.handler.transform.Find("SND_Label");
            Transform peerBorder = PeerBorder(food);
            if (name == null || !name.gameObject.activeInHierarchy || peerBorder == null || !peerBorder.gameObject.activeInHierarchy) return false;
            var drawnNames = name.GetComponentsInChildren<TMP_Text>(true).Where(text => text.isActiveAndEnabled &&
                text.color.a > 0 && !string.IsNullOrWhiteSpace(text.text)).ToArray();
            if (drawnNames.Length == 0) return false;
            if (nameVisibleAt == 0) nameVisibleAt = Time.realtimeSinceStartup;
            var helperName = bag.middle.transform.Find("ShopAssist_AreaName");
            if ((helperName == null || helperName.gameObject.activeInHierarchy) && Time.realtimeSinceStartup - nameVisibleAt < 0.6f) return false;
            Need(helperName != null && !helperName.gameObject.activeInHierarchy, "The same area name was drawn twice after render synchronization: own=" +
                (helperName == null ? "missing" : string.Join(" | ", helperName.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text))) +
                "; peer=" + string.Join(" | ", drawnNames.Select(text => text.text)));
            var helperBorder = OwnBorder(food);
            if (!PeerDrawn(peerBorder))
            {
                if (helperBorder != null) Need(Rendered(helperBorder),
                    "A not-yet-drawn peer border hid the helper during the delay");
                return false;
            }
            if (transitionAt == 0) transitionAt = Time.realtimeSinceStartup;
            if (helperBorder != null && helperBorder.gameObject.activeInHierarchy && Time.realtimeSinceStartup - transitionAt < 0.5f) return false;
            Need(helperBorder != null && !helperBorder.gameObject.activeInHierarchy, "The same demand was outlined twice");
            ToggleDemandPeer();
            transitionAt = Time.realtimeSinceStartup;
            step = 20; return false;
        }
        if (step == 20)
        {
            if (PeerBorder(food)?.gameObject.activeInHierarchy == true || !Rendered(OwnBorder(food)))
            {
                Need(Time.realtimeSinceStartup - transitionAt < 3f, "The helper demand did not return after the peer was switched off");
                return false;
            }
            ToggleDemandPeer();
            transitionAt = Time.realtimeSinceStartup;
            step = 21; return false;
        }
        if (step == 21)
        {
            var peerBorder = PeerBorder(food);
            var helperBorder = OwnBorder(food);
            if (peerBorder == null || !peerBorder.gameObject.activeInHierarchy || !PeerDrawn(peerBorder) ||
                helperBorder == null || helperBorder.gameObject.activeInHierarchy)
            {
                Need(Time.realtimeSinceStartup - transitionAt < 5f, "Demand borders did not settle after the peer was switched on");
                return false;
            }
            Need(bag.contentWindow.IsVisible() && !machine.contentWindow.IsVisible(), "Fixture visibility changed unexpectedly");
            RoutingScenario.OpenOptionAction("f11.open");
            var list = List();
            Need(QaItemUi.Hit(list.root, nested) != null && QaItemUi.Hit(list.root, seed) != null && QaItemUi.Hit(list.root, secret) == null,
                "F11 missed the ordinary bag or closed machine slot, or revealed locked contents");
            Need(!machine.contentWindow.IsVisible() && !locked.contentWindow.IsVisible(), "F11 opened a machine or locked container while searching");
            Need(CellBadge(list, seed) == ModHelper.GetLocalized(Mod, "f11.in_use"), "F11 did not identify the machine item as in use");
            List().Close();
            var machineSlot = MachineHydroponic.GetInputSlot(machine);
            Need(machineSlot?.handler != null && machineSlot.handler.gameObject.activeSelf, "Machine slot fixture is not locally visible");
            machineSlot.handler.gameObject.SetActive(false);
            RoutingScenario.OpenOptionAction("f11.open");
            Need(QaItemUi.Hit(List().root, seed) == null, "F11 revealed an explicitly hidden machine slot");
            List().Close();
            machineSlot.handler.gameObject.SetActive(true);
            step = 3; return false;
        }
        if (step == 3)
        {
            // Item Details caches scanners for two seconds. Rebuild the real native item menu.
            if (Time.realtimeSinceStartup - since < 4f) return false;
            menuView = OpenItemMenu(water, MousePosition());
            var scan = menuView.handler.GetComponentsInChildren<TreeNodeRender>(true)
                .FirstOrDefault(node => node.gameObject.name == "nodeButtonItemDetailsScan" && node.gameObject.activeInHierarchy);
            var hold = menuView.handler.transform.Find("ShopAssist_HoldMenu");
            Need(scan != null && hold != null && hold.gameObject.activeInHierarchy, "The scan and temporary-hold menu entries did not coexist");
            scanButton = scan.element as RichTextElement;
            holdButton = hold.GetComponent<TreeNodeRender>()?.element as RichTextElement;
            Need(scanButton != null && holdButton != null && scanButton != holdButton, "The menu entries shared a native action");
            menuFrame = Time.frameCount;
            transitionAt = Time.realtimeSinceStartup;
            step = 30; return false;
        }
        if (step == 30)
        {
            if (Time.frameCount <= menuFrame) return false;
            var scanBackground = ButtonBackground(scanButton);
            var holdBackground = ButtonBackground(holdButton);
            Need(menuView?.IsVisible() == true && scanBackground != null && holdBackground != null,
                "The native menu closed before its first rendered frame");
            if (scanBackground.depth < 0 || holdBackground.depth < 0)
            {
                Need(Time.realtimeSinceStartup - transitionAt < 4f, "The menu was never rendered");
                KeepMenuForNextFrame();
                return false;
            }
            Need(Separate(scanBackground.rectTransform, holdBackground.rectTransform), "The extra item menu entries overlap");
            Need(Raycast(scanButton), "The native peer scan entry is not reachable by the native menu raycast");
            Need(Raycast(holdButton), "The temporary-hold entry is not reachable by the native menu raycast");
            menuView = OpenItemMenu(water, new Vector2(Screen.width / 2f, 1f));
            var hold = menuView.handler.transform.Find("ShopAssist_HoldMenu");
            holdButton = hold?.GetComponent<TreeNodeRender>()?.element as RichTextElement;
            var scan = menuView.handler.GetComponentsInChildren<TreeNodeRender>(true)
                .FirstOrDefault(node => node.gameObject.name == "nodeButtonItemDetailsScan" && node.gameObject.activeInHierarchy);
            scanButton = scan?.element as RichTextElement;
            Need(holdButton != null && scanButton != null, "The independent entries were absent when the menu was reopened");
            menuFrame = Time.frameCount;
            transitionAt = Time.realtimeSinceStartup;
            step = 31; return false;
        }
        if (step == 31)
        {
            if (Time.frameCount <= menuFrame) return false;
            var holdBackground = ButtonBackground(holdButton);
            Need(menuView?.IsVisible() == true && holdBackground != null, "The bottom-edge menu closed before rendering");
            if (holdBackground.depth < 0)
            {
                Need(Time.realtimeSinceStartup - transitionAt < 4f, "The bottom-edge menu was never rendered");
                KeepMenuForNextFrame();
                return false;
            }
            Need(OnScreen(holdBackground.rectTransform), "The temporary-hold entry extends outside the screen");
            Need(Raycast(scanButton), "The peer scan entry is not reachable near the bottom of the screen");
            Need(Raycast(holdButton), "The temporary-hold entry is not reachable near the bottom of the screen");
            nativeMenuHeight = menuView.heightPixels;
            nativeMenuWidth = menuView.widthPixels;
            fittedMenuPosition = menuView.handler.rectTransform.anchoredPosition;
            RefreshItemMenu();
            Need(menuView.heightPixels == nativeMenuHeight && menuView.widthPixels == nativeMenuWidth &&
                Vector2.Distance(menuView.handler.rectTransform.anchoredPosition, fittedMenuPosition) < 0.01f,
                "Refreshing the menu repeatedly changed its native size or position");
            menuFrame = Time.frameCount;
            transitionAt = Time.realtimeSinceStartup;
            step = 32; return false;
        }
        if (step == 32)
        {
            if (Time.frameCount <= menuFrame) return false;
            var holdBackground = ButtonBackground(holdButton);
            Need(menuView?.IsVisible() == true && holdBackground != null, "The refreshed menu closed before rendering");
            if (holdBackground.depth < 0)
            {
                Need(Time.realtimeSinceStartup - transitionAt < 4f, "The refreshed menu was never rendered");
                KeepMenuForNextFrame();
                return false;
            }
            Need(menuView.heightPixels == nativeMenuHeight && menuView.widthPixels == nativeMenuWidth &&
                Vector2.Distance(menuView.handler.rectTransform.anchoredPosition, fittedMenuPosition) < 0.01f &&
                OnScreen(holdBackground.rectTransform) && Raycast(scanButton) && Raycast(holdButton),
                "Refresh changed the menu's bounds or made an action unreachable");
            Trigger(holdButton);
            step = 4; return false;
        }
        if (step == 4)
        {
            var hold = water.rotator.transform.Find("ShopAssist_HoldOutline");
            if (!Rendered(hold)) return false;
            Need(!ItemPinHelper.IsPinned(water) && ReferenceEquals(water.parentInventory, EmporiumEntry.Instance.invElement),
                "Temporary hold changed native pin or inventory");
            var menu = OpenItemMenu(water);
            var scan = menu.handler.GetComponentsInChildren<TreeNodeRender>(true)
                .First(node => node.gameObject.name == "nodeButtonItemDetailsScan" && node.gameObject.activeInHierarchy);
            Trigger((RichTextElement)scan.element);
            Need(WaterScannerUIManager.Instance.panel.activeSelf && ReferenceEquals(WaterScannerUIManager.Instance.currentWaterContainer, water),
                "The Item Details scan action did not open the intended item");
            Need(Rendered(hold) && !ItemPinHelper.IsPinned(water), "Scanning also triggered the hold action");
            WaterScannerUIManager.Instance.Close();
            NegociationUIManager.Instance.CloseUI();
            var grid = (GameGridInventory)bag.contentWindow.childElement;
            MoveToFarthest(grid, nested);
            beforePeerNested = nested.modifiedShape.Clone(); beforePeerStolen = stolen.modifiedShape.Clone();
            beforePeerNestedCount = nested.unitCount; beforePeerStolenCount = stolen.unitCount;
            StartGridMenu();
            step = 34; return false;
        }
        if (step == 34)
        {
            if (Time.frameCount <= menuFrame) return false;
            var peer = ButtonBackground(peerSort);
            var route = ButtonBackground(routeButton);
            Need(menuView?.IsVisible() == true && peer != null && route != null, "The shared sort menu closed before rendering");
            if (peer.depth < 0 || route.depth < 0)
            {
                Need(Time.realtimeSinceStartup - transitionAt < 4f, "The shared sort menu was never rendered");
                KeepMenuForNextFrame(); return false;
            }
            Need((helperSort == null || !helperSort.handler.gameObject.activeInHierarchy) &&
                OnScreen(route.rectTransform) && OnScreen(peer.rectTransform) && Separate(route.rectTransform, peer.rectTransform) &&
                Raycast(routeButton) && Raycast(peerSort), "The peer sort and distinct route entry overlap, are missing, or duplicate sorting");
            ItemContextHandler.current.Stop();
            menuFrame = Time.frameCount;
            step = 37; return false;
        }
        if (step == 37)
        {
            if (Time.frameCount <= menuFrame) return false;
            StartGridMenu();
            step = 35; return false;
        }
        if (step == 35)
        {
            if (Time.frameCount <= menuFrame) return false;
            var peer = ButtonBackground(peerSort);
            Need(menuView?.IsVisible() == true && peer != null, "The reopened peer sort entry disappeared");
            if (peer.depth < 0)
            {
                Need(Time.realtimeSinceStartup - transitionAt < 4f, "The reopened peer sort menu was never rendered");
                KeepMenuForNextFrame(); return false;
            }
            Need(Raycast(peerSort), "The peer sort entry cannot be clicked after reopening");
            Trigger(peerSort);
            Need(CustomUIManager.Instance.GetWindow(Mod + ".inventory_organizer") == null,
                "Clicking the peer sort action also opened the helper organizer");
            transitionAt = Time.realtimeSinceStartup;
            step = 36; return false;
        }
        if (step == 36)
        {
            if (PeerSortBusy())
            {
                Need(Time.realtimeSinceStartup - transitionAt < 20f, "Peer sorting did not finish");
                return false;
            }
            if (SamePose(beforePeerNested, nested.modifiedShape) && SamePose(beforePeerStolen, stolen.modifiedShape) &&
                Time.realtimeSinceStartup - transitionAt < 2f) return false;
            Need(ReferenceEquals(nested.parentInventory, bag.contentWindow.childElement) &&
                ReferenceEquals(stolen.parentInventory, bag.contentWindow.childElement) && nested.unitCount == beforePeerNestedCount &&
                stolen.unitCount == beforePeerStolenCount, "Peer sorting changed a fixture's owning inventory or quantity");
            Need(!SamePose(beforePeerNested, nested.modifiedShape) || !SamePose(beforePeerStolen, stolen.modifiedShape),
                "The peer sort entry did not perform the native rearrangement");
            order = new StoreRequest { title = "AA order " + token, id = "AA order " + token,
                description = "QA", requiredItem = reserved.identifier, requiredItemCount = 1,
                requesterName = "QA", sourceEventId = token, duration = 2, creditReward = 1 };
            PlayerStore.Instance.storeRequests.Add(order);
            PCUIManager.Instance.OpenUI();
            step = 40; return false;
        }
        if (step == 40)
        {
            var row = UnityEngine.Object.FindObjectsOfType<StoreRequestElement>().FirstOrDefault(entry => entry.storeRequest == order);
            if (row == null) return false;
            var track = row.acceptButton.transform.parent.Find(Mod + ".track")?.GetComponent<Button>();
            Need(track != null && track.interactable, "Reservation track action is unavailable");
            track.onClick.Invoke();
            var book = CustomUIManager.Instance.GetWindow(Mod + ".requests");
            Need(book?.Get("track")?.button != null, "Reservation form is unavailable");
            book.Get("track").button.onClick.Invoke();
            book = CustomUIManager.Instance.GetWindow(Mod + ".requests");
            QaItemUi.Choose(book, "destination", bag.GetDisplayName(true));
            book = CustomUIManager.Instance.GetWindow(Mod + ".requests");
            QaItemUi.Click(book, reserved);
            book.Get("prepare").button.onClick.Invoke();
            Need(ReferenceEquals(reserved.parentInventory, bag.contentWindow.childElement), "Reservation did not move the intended instance");
            PCUIManager.Instance.HideUI();
            RoutingScenario.OpenOptionAction("f11.open");
            var list = List();
            Need(QaItemUi.Hit(list.root, reserved) != null, "Reserved goods disappeared from F11");
            Need(CellBadge(list, reserved) == ModHelper.GetLocalized(Mod, "f11.reserved"), "F11 did not show the reservation");
            book = CustomUIManager.Instance.GetWindow(Mod + ".requests");
            var release = Button(book, "f27.release");
            Need(release != null, "Reservation release action is unavailable");
            release.onClick.Invoke();
            step = 41; return false;
        }
        if (step == 41)
        {
            var list = List();
            if (QaItemUi.Hit(list.root, reserved) == null) return false;
            if (CellBadge(list, reserved) == ModHelper.GetLocalized(Mod, "f11.reserved")) return false;
            Need(QaItemUi.Hit(list.root, reserved) != null && ReferenceEquals(reserved.parentInventory, bag.contentWindow.childElement) && reserved.unitCount > 0,
                "F11 did not refresh the released item without changing the reserved stock");
            var oldHit = QaItemUi.Hit(list.root, nested);
            Need(oldHit != null, "The nested item card disappeared before the customer changed");
            PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateInspectionClient());
            PlayerStore.Instance.isClientArrived = true;
            QaItemUi.Click(oldHit);
            Need(ReferenceEquals(nested.parentInventory, bag.contentWindow.childElement), "An old customer row moved the item");
            step = 5; return false;
        }
        if (step == 5)
        {
            if (CustomUIManager.Instance.GetWindow(ListId) != null) return false;
            if (!VisibleBorder(stolen)) return false;
            bag.contentWindow.Hide();
            transitionAt = 0;
            step++; return false;
        }
        if (step == 6)
        {
            var contained = OwnBorder(bag);
            if (!Rendered(contained)) return false;
            Need(contained.GetComponentsInChildren<Transform>(true).Length > 4,
                "The helper's distinct contained-risk indicator disappeared with the peer enabled");
            var peer = PeerBorder(bag);
            if (peer == null || !peer.gameObject.activeInHierarchy || !PeerDrawn(peer)) return false;
            if (transitionAt == 0) transitionAt = Time.realtimeSinceStartup;
            var edge = contained.GetChild(0) as RectTransform;
            if (edge == null || edge.anchoredPosition.y > -2f)
            {
                Need(Time.realtimeSinceStartup - transitionAt < 4f, "Contained-risk dashes did not separate from the peer border");
                return false;
            }
            ToggleDemandPeer();
            transitionAt = Time.realtimeSinceStartup;
            step = 60; return false;
        }
        if (step == 60)
        {
            var contained = OwnBorder(bag);
            var edge = contained?.GetChild(0) as RectTransform;
            if (PeerBorder(bag)?.gameObject.activeInHierarchy == true || edge == null || Math.Abs(edge.anchoredPosition.y) > 0.01f)
            {
                Need(Time.realtimeSinceStartup - transitionAt < 3f, "Contained-risk dashes did not return to the edge after F5");
                return false;
            }
            ToggleDemandPeer();
            transitionAt = Time.realtimeSinceStartup;
            step = 61; return false;
        }
        if (step == 61)
        {
            var peer = PeerBorder(bag);
            var edge = OwnBorder(bag)?.GetChild(0) as RectTransform;
            if (peer == null || !peer.gameObject.activeInHierarchy || !PeerDrawn(peer) || edge == null || edge.anchoredPosition.y > -2f)
            {
                Need(Time.realtimeSinceStartup - transitionAt < 5f, "Contained-risk dashes did not separate again after F5");
                return false;
            }
            Put(EmporiumEntry.Instance.invElement, stolen);
            step = 7; return false;
        }
        if (step == 7)
        {
            if (Rendered(OwnBorder(bag))) return false;
            Need(peerSettings == PeerSettings(), "An inventory peer's settings were changed");
            result = "PASS four inventory peers: independent item-menu actions, name and demand deduplication, F11 machine/locked/reservation boundaries, stale customer, and unique contained-risk mark";
            return true;
        }
        throw new InvalidOperationException("Unknown inventory coexistence step");
    }

    private void Prepare()
    {
        TradeScenario.SetOptions(true, "f02.enabled", "f03.enabled", "f04.enabled", "f04.stolen", "f04.contraband", "f05.enabled", "f06.enabled", "f07.enabled", "f11.enabled", "f27.enabled");
        TradeScenario.SetOptions(false, "f04.unseen", "f15.enabled");
        var root = EmporiumEntry.Instance.invElement;
        bag = New("backpack_medium", "AA Bag " + token); Put(root, bag);
        nested = New("cup_noodle", "AA Nested " + token); Put((GameInventory)bag.contentWindow.childElement, nested);
        stolen = New("wine_yeast", "AA Stolen " + token); StolenHelper.SetHeat(stolen, 42); Put((GameInventory)bag.contentWindow.childElement, stolen);
        locked = New("backpack_medium", "AA Locked " + token); Put(root, locked);
        secret = New("processed_juice", "AA Secret " + token); Put((GameInventory)locked.contentWindow.childElement, secret);
        locked.contentWindow.isLocked = true;
        machine = New("hydroponic", "AA Machine " + token); Put(root, machine);
        seed = New("nutrifruit_seed", "AA Machine Seed " + token); seed.SetGameItemType("FOOD");
        Put(MachineHydroponic.GetInputSlot(machine), seed);
        machine.contentWindow.Hide();
        food = New("processed_meat", "AA Buyer Food " + token); Put(root, food);
        reserved = New("processed_milk", "AA Reserved " + token); Put(root, reserved);
        Put(root, New("aquascan", "AA Scanner " + token));
        water = New("bottled_water", "AA Water " + token); Put(root, water);
        ItemMouseDoubleClickHandler.current.OpenContentAction(bag, Vector2.zero);
        Need(bag.contentWindow.IsVisible(), "Could not open the ordinary fixture bag");
        var client = StoreClientList.CreateSpacerClientFood();
        Need(client.IsClientBuyingThisItem(food), "Buyer does not accept the demand-border fixture");
        PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(client);
        PlayerStore.Instance.isClientArrived = true;
        NegociationUIManager.Instance.OpenUI();
    }

    private static GameItemElement New(string id, string name)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement;
        Need(item != null, "Missing native item " + id);
        GeneralHelper.SetItemOwned(item, true); GeneralHelper.SetCustomName(item, name); return item;
    }
    private static void Put(GameInventory destination, GameItem item)
    {
        Need(destination != null, "Missing native destination");
        var slot = destination.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.inventory == destination && slot.targetItem == null && slot.numTransfer >= item.unitCount &&
            slot.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, destination),
            "Could not place " + item.identifier);
    }
    private static void Peer(string id)
    {
        var mod = ModLoader.Mods.Find(m => m.Manifest != null && m.Manifest.ID == id);
        Need(mod != null && mod.IsEnabled && mod.Instance != null && string.IsNullOrEmpty(mod.LoadError), "Peer not running: " + id);
    }
    private static string PeerSettings()
    {
        return string.Join("|", new[] { "hollow.clientdemandhighlight", "smart_inventory_arrange", "item_details" }.SelectMany(id =>
            (ModConfig.Find(id)?.Entries ?? throw new InvalidOperationException("Peer config unavailable: " + id))
                .Select(entry => id + ":" + entry.Key + "=" + entry.BoxedValue))) +
            "|ShowItemNames:hotkey=" + PlayerPrefs.GetInt("snd.hotkey", (int)KeyCode.F10);
    }
    private static Transform PeerBorder(GameItemElement item) => item.middle.transform.Find("CDH_Border") ?? item.rotator.transform.Find("CDH_Border");
    private static bool PeerDrawn(Transform node)
    {
        var graphic = node?.GetComponent<Graphic>();
        if (graphic == null || !graphic.isActiveAndEnabled) return false;
        var color = graphic.color;
        return color.a > 0 && ((Math.Abs(color.r - 0.3f) < 0.02f && Math.Abs(color.g - 0.9f) < 0.02f && Math.Abs(color.b - 0.35f) < 0.02f) ||
            (Math.Abs(color.r - 1f) < 0.02f && Math.Abs(color.g - 0.7f) < 0.02f && Math.Abs(color.b - 0.1f) < 0.02f) ||
            (Math.Abs(color.r - 0.95f) < 0.02f && Math.Abs(color.g - 0.22f) < 0.02f && Math.Abs(color.b - 0.2f) < 0.02f));
    }
    private static void ToggleDemandPeer()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("ClientDemandHighlight.Core", false)).FirstOrDefault(value => value != null);
        var method = type?.GetMethod("Toggle", BindingFlags.Static | BindingFlags.NonPublic);
        Need(method != null, "The peer's non-persistent F5 action is unavailable");
        method.Invoke(null, null);
    }
    private static Vector2 MousePosition()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("UnityEngine.Input", false)).FirstOrDefault(value => value != null);
        var property = type?.GetProperty("mousePosition", BindingFlags.Static | BindingFlags.Public);
        Need(property?.GetValue(null) is Vector3, "Native pointer position is unavailable");
        return (Vector3)property.GetValue(null);
    }
    private static Transform OwnBorder(GameItemElement item) => item.rotator.transform.Find("ShopAssist_Outline");
    private static bool Rendered(Transform node) => node != null && node.gameObject.activeInHierarchy &&
        node.GetComponentsInChildren<Image>(true).Any(image => image.isActiveAndEnabled && image.color.a > 0 &&
            image.rectTransform.rect.width > 0 && image.rectTransform.rect.height > 0);
    private static bool VisibleBorder(GameItemElement item) => Rendered(OwnBorder(item)) || PeerDrawn(PeerBorder(item));
    private static CustomUIWindow List() => CustomUIManager.Instance.GetWindow(ListId) ?? throw new InvalidOperationException("F11 window is closed");
    private static string CellBadge(CustomUIWindow window, GameItem item)
    {
        var hit = QaItemUi.Hit(window.root, item);
        object entry = hit?.GetType().GetField("entry", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hit);
        return entry?.GetType().GetField("Badge", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(entry) as string;
    }
    private static Button Button(CustomUIWindow window, string key) => window.root.GetComponentsInChildren<Button>(true)
        .FirstOrDefault(button => button.gameObject.activeInHierarchy && button.GetComponentInChildren<TMP_Text>(true)?.text == ModHelper.GetLocalized(Mod, key));
    private static PixelWindow OpenItemMenu(GameItem item, Vector2? screenPosition = null)
    {
        var handler = ItemContextHandler.current;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(ItemContextHandler);
        var menu = (PixelWindow)type.GetField("contextWindow", flags).GetValue(handler);
        type.GetField("currentInventory", flags).SetValue(handler, null);
        type.GetField("currentItem", flags).SetValue(handler, item);
        type.GetMethod("UpdateConditions", flags).Invoke(handler, null);
        type.GetMethod("EqualizeButtonWidths", flags).Invoke(handler, null);
        menu.Validate(); menu.Show(screenPosition ?? new Vector2(Screen.width / 2f, Screen.height / 2f), Vector2.zero);
        type.GetMethod("OnEventPress", flags).Invoke(handler, new object[] { new HashSet<KeyCode>() });
        KeepMenuForNextFrame();
        return menu;
    }
    private static void KeepMenuForNextFrame() => typeof(ItemContextHandler).GetField("delayFrame", BindingFlags.Instance | BindingFlags.NonPublic)
        .SetValue(ItemContextHandler.current, true);
    private static void RefreshItemMenu()
    {
        typeof(ItemContextHandler).GetMethod("OnEventPress", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(ItemContextHandler.current, new object[] { new HashSet<KeyCode>() });
        KeepMenuForNextFrame();
    }
    private void StartGridMenu()
    {
        ItemContextHandler.current.Stop();
        bag.contentWindow.ToFront();
        var grid = bag.contentWindow.childElement as GameGridInventory;
        Need(grid?.handler != null && grid.inventoryShape != null, "Open backpack grid is unavailable");
        Canvas.ForceUpdateCanvases();
        var canvas = grid.handler.GetComponentInParent<Canvas>(true);
        Need(canvas != null, "The backpack grid has no canvas");
        Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Vector2 point = default;
        bool found = false;
        for (int y = 0; y < grid.inventoryShape.height && !found; y++)
        for (int x = 0; x < grid.inventoryShape.width; x++)
        {
            if (grid.inventoryShape.Get(x, y) != 0 || grid.childItems.Any(item => item.modifiedShape.Intersects(x, y))) continue;
            Vector3 local = new Vector3((x + 0.5f) * UISettings.current.gridSize, -(y + 0.5f) * UISettings.current.gridSize, 0);
            point = RectTransformUtility.WorldToScreenPoint(camera, grid.handler.rectTransform.TransformPoint(local));
            if (ReferenceEquals(RenderHandler.RaycastElement<GameGridInventory>(point), grid)) { found = true; break; }
        }
        Need(found, "No empty backpack cell is reachable by the native inventory raycast");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(ItemContextHandler);
        bool opened = (bool)type.GetMethod("TryOpenSortMenu", flags).Invoke(ItemContextHandler.current, new object[] { point });
        Need(opened, "Native sort menu did not open on the backpack grid");
        menuView = (PixelWindow)type.GetField("sortWindow", flags).GetValue(ItemContextHandler.current);
        helperSort = menuView.handler.transform.Find("ShopAssist_SortMenu")?.GetComponent<TreeNodeRender>()?.element as RichTextElement;
        routeButton = menuView.handler.transform.Find("ShopAssist_RouteMenu")?.GetComponent<TreeNodeRender>()?.element as RichTextElement;
        var peerType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("SmartInventoryArrange.NativeSmartMenu", false)).FirstOrDefault(value => value != null);
        peerSort = peerType?.GetField("Button", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as RichTextElement;
        Need((helperSort == null || !helperSort.handler.gameObject.activeInHierarchy) && peerSort != null && routeButton != null &&
            routeButton.handler.gameObject.activeInHierarchy, "Shared menu did not retain the peer sort and independent routing without duplicate sort");
        KeepMenuForNextFrame();
        menuFrame = Time.frameCount;
        transitionAt = Time.realtimeSinceStartup;
    }
    private static bool PeerSortBusy()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("SmartInventoryArrange.SmartArrangeMod", false)).FirstOrDefault(value => value != null);
        var property = type?.GetProperty("Busy", BindingFlags.Static | BindingFlags.NonPublic);
        Need(property != null, "Peer sorting state is unavailable");
        return (bool)property.GetValue(null);
    }
    private static void MoveToFarthest(GameGridInventory grid, GameItem item)
    {
        GridShape before = item.modifiedShape.Clone();
        var shape = new GridShapeBuilder(before);
        for (int y = grid.inventoryShape.height - shape.globalHeight; y >= 0; y--)
        for (int x = grid.inventoryShape.width - shape.globalWidth; x >= 0; x--)
        {
            shape.SetPosition(x, y);
            if (SamePose(before, shape)) continue;
            SlotMarker slot = grid.TryInventorySlot(item, item.unitCount, shape);
            if (slot == null || slot.inventory != grid || slot.targetItem != null || slot.numTransfer < item.unitCount) continue;
            int count = item.unitCount;
            if (slot.TryAcceptOnce(count) == count && item.parentInventory == grid && item.unitCount == count) return;
        }
        throw new InvalidOperationException("Could not arrange a displaced fixture for the peer sort");
    }
    private static bool SamePose(GridShape first, GridShape second) => first != null && second != null &&
        first.minX == second.minX && first.minY == second.minY && first.orientation == second.orientation && first.flipped == second.flipped;
    private static void Trigger(RichTextElement button) => typeof(ItemContextHandler).GetMethod("TriggerButton", BindingFlags.Instance | BindingFlags.NonPublic)
        .Invoke(ItemContextHandler.current, new object[] { button });
    private static bool Separate(RectTransform left, RectTransform right)
    {
        var a = new Vector3[4]; var b = new Vector3[4];
        left.GetWorldCorners(a); right.GetWorldCorners(b);
        return a[2].x <= b[0].x || b[2].x <= a[0].x || a[2].y <= b[0].y || b[2].y <= a[0].y;
    }
    private static bool OnScreen(RectTransform rect)
    {
        var canvas = ItemContextHandler.current.overlayCanvas;
        var camera = canvas.GetComponent<GraphicRaycaster>().eventCamera;
        Rect bounds = canvas.rootCanvas.pixelRect;
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        return corners.All(corner =>
        {
            Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, corner);
            return point.x >= bounds.xMin - 0.5f && point.x <= bounds.xMax + 0.5f &&
                point.y >= bounds.yMin - 0.5f && point.y <= bounds.yMax + 0.5f;
        });
    }
    private static bool Raycast(RichTextElement button)
    {
        var handler = ItemContextHandler.current;
        var background = ButtonBackground(button);
        var raycaster = handler.overlayCanvas?.GetComponent<GraphicRaycaster>();
        if (background == null || raycaster == null || EventSystem.current == null) return false;
        Vector2 point = RectTransformUtility.WorldToScreenPoint(raycaster.eventCamera,
            background.rectTransform.TransformPoint(background.rectTransform.rect.center));
        Rect screen = handler.overlayCanvas.rootCanvas.pixelRect;
        var actual = handler.FindRichTextNode(point, handler.overlayCanvas);
        if (screen.Contains(point) && ReferenceEquals(actual, button)) return true;
        var hits = new List<RaycastResult>();
        raycaster.Raycast(new PointerEventData(EventSystem.current) { position = point }, hits);
        var filters = new List<string>();
        for (Transform current = background.transform; current != null; current = current.parent)
        {
            var rectMask = current.GetComponent<RectMask2D>();
            var mask = current.GetComponent<Mask>();
            var group = current.GetComponent<CanvasGroup>();
            if (rectMask != null && rectMask.enabled) filters.Add(current.name + ":RectMask=" + rectMask.rectTransform.rect);
            if (mask != null && mask.enabled) filters.Add(current.name + ":Mask");
            if (group != null) filters.Add(current.name + ":CanvasGroup=" + group.blocksRaycasts + "/" + group.interactable);
        }
        var graphics = GraphicRegistry.GetGraphicsForCanvas(handler.overlayCanvas);
        var raycastable = GraphicRegistry.GetRaycastableGraphicsForCanvas(handler.overlayCanvas);
        Debug.Log("[PLAYER-QA] context raycast point=" + point + " screen=" + Screen.width + "x" + Screen.height +
            " background=" + background.name + " rect=" + background.rectTransform.rect +
            " root=" + button.handler.rectTransform.rect + " targetCanvas=" + background.canvas?.name +
            " overlayCanvas=" + handler.overlayCanvas.name + " sameCanvas=" + ReferenceEquals(background.canvas, handler.overlayCanvas) +
            " activeCanvas=" + handler.overlayCanvas.isActiveAndEnabled + " camera=" + raycaster.eventCamera?.name +
            " depth=" + background.depth + " cull=" + background.canvasRenderer.cull +
            " contains=" + RectTransformUtility.RectangleContainsScreenPoint(background.rectTransform, point, raycaster.eventCamera) +
            " passesFilters=" + background.Raycast(point, raycaster.eventCamera) +
            " registered=" + graphics.Contains(background) + "/" + graphics.Count +
            " raycastable=" + raycastable.Contains(background) + "/" + raycastable.Count + " filters=" + string.Join(",", filters) +
            " hits=" + string.Join(", ", hits.Select(hit =>
            {
                var node = hit.gameObject.GetComponent<TreeNodeRender>();
                return hit.gameObject.name + ":" + (node?.parent?.element?.GetType().Name ?? "no-parent-element");
            })));
        return false;
    }
    private static Image ButtonBackground(RichTextElement button) => button?.handler?.GetComponentsInChildren<Image>(true)
        .FirstOrDefault(image => image.isActiveAndEnabled && image.raycastTarget &&
            ReferenceEquals(image.GetComponent<TreeNodeRender>()?.parent?.element, button));
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
