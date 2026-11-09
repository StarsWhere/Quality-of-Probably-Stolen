using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Uses a new isolated save. Fixtures are native directory items and are placed by native grid operations.
public sealed class TradeScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step, credit, purchasePrice, salePrice;
    private GameItem purchased;
    private float since = Time.realtimeSinceStartup;
    private float after;

    [Serializable] private sealed class Ledger { public Row[] entries = Array.Empty<Row>(); }
    [Serializable] private sealed class Row { public int kind = -1, day = -1, events = -1; public long income = -1, expense = -1; }
    private static void Check(bool value, string detail)
    {
        if (!value) throw new InvalidOperationException(detail);
    }

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 70) throw new InvalidOperationException("Trade QA timed out at step " + step);
        if (step == 0)
        {
            if (!start.Tick()) return false;
            step++; return false;
        }
        var p = PlayerStore.Instance;
        var e = EmporiumEntry.Instance;
        if (step == 1)
        {
            if (e.storeInteractables == null || !e.storeInteractables.activeInHierarchy || StoreUIManager.Instance.gameOptionUIManager == null) return false;
            SetOptions(true, "f01.enabled", "f16.enabled", "f21.enabled", "f28.enabled", "f37.enabled");
            step++; return false;
        }
        if (step == 2)
        {
            CheckYeast();
            Debug.Log("[ShopAssist QA] PASS yeast exact rotated home, occupied-home fallback, unavailable destinations, ordinary yeast and missing ingredients");
            step++; return false;
        }
        if (step == 3)
        {
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
            purchased = NewItem("wine_yeast", false);
            Put(e.backInvinvElement, purchased);
            credit = p.playerCash;
            purchasePrice = checked((int)purchased.GetNegociatedValue());
            p.BuyItem(purchased);
            Check(purchasePrice > 0 && p.playerCash == credit - purchasePrice && GeneralHelper.IsItemOwned(purchased) &&
                purchased.parentInventory == e.backInvinvElement, "F01 changed purchase ownership/cash, or moved the purchased instance");
            Check(Receipt() != null, "F16 did not show the purchase receipt");
            step++; return false;
        }
        if (step == 4)
        {
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateSpacerClientFood());
            int before = p.playerCash;
            p.SellItem(purchased);
            salePrice = p.playerCash - before;
            Check(salePrice > 0 && !GeneralHelper.IsItemOwned(purchased), "Original sale did not settle");
            VerifyLedger(1, 0, purchasePrice);
            VerifyLedger(2, salePrice, 0);
            SetOptions(false, "f01.enabled", "f16.enabled");
            p.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
            var control = NewItem("wine_yeast_red", false);
            Put(e.backInvinvElement, control);
            p.BuyItem(control);
            Check(control.parentInventory != e.backInvinvElement && GeneralHelper.IsItemOwned(control), "F01 remained active after disabling in Options");
            step++; return false;
        }
        if (step == 5)
        {
            RoutingScenario.RunFeatureAction("f28.open");
            var ledger = CustomUIManager.Instance.GetWindow(Mod + ".ledger");
            Check(ledger != null && ledger.IsAlive, "Ledger failed to open");
            ledger.Close();
            p.dayUntilRent = 1;
            Check(p.IsPayingRent(), "Test store does not rent property");
            if (StoreUIManager.Instance.controlHintOverlay?.activeSelf == true) StoreUIManager.Instance.ToggleControlHintOverlay();
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            after = Time.realtimeSinceStartup + 1f;
            step++; return false;
        }
        if (step == 6)
        {
            if (Time.realtimeSinceStartup < after) return false;
            var row = Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>().FirstOrDefault(text => text.gameObject.scene.IsValid() &&
                text.gameObject.activeInHierarchy && text.name == "deadline-row" && text.text.Contains("1") &&
                text.text.Contains(ModHelper.GetLocalized(Mod, "door.rent")));
            Check(row != null, "Public rent deadline was not shown on the shutter");
            p.currentClientInstance = null; p.isClientArrived = false;
            StoreShutterButton.Instance.OpenShutter();
            Check(p.storeState == PlayerStore.StoreState.OPEN, "Native shutter did not open");
            after = Time.realtimeSinceStartup + 3.2f;
            step++; return false;
        }
        if (step == 7)
        {
            if (Time.realtimeSinceStartup < after) return false;
            var prompt = GenericConfirmationUIManager.Instance;
            Check(prompt != null && !prompt.panel.activeInHierarchy, "Unexpected extra confirmation before closing");
            StoreShutterButton.Instance.ShowConfirmCloseUI();
            Check(StoreUIManager.Instance.closeShutterConfirmationPanel.activeInHierarchy, "Native close confirmation did not open");
            StoreShutterButton.Instance.EndAndClose();
            Check(p.storeState == PlayerStore.StoreState.CLOSED && !prompt.panel.activeInHierarchy &&
                !StoreUIManager.Instance.closeShutterConfirmationPanel.activeSelf,
                "F21 interrupted the native close confirmation or prevented closing");
            result = "PASS native grid and brew returns, purchase counter switch and receipts, balanced buy/sell ledger, shutter rent deadline and native close confirmation";
            return true;
        }
        return false;
    }

    private static TMPro.TMP_Text Receipt()
    {
        var panel = QaTradeReceipt.Latest();
        return panel == null ? null : panel.GetComponentsInChildren<TMPro.TMP_Text>().FirstOrDefault(t => !string.IsNullOrEmpty(t.text));
    }

    private static void VerifyLedger(int kind, int income, int expense)
    {
        var raw = ModHelper.GetModData(Mod, "ledger", null);
        var book = raw == null ? null : JsonUtility.FromJson<Ledger>(raw);
        Check(book?.entries != null && book.entries.Any(r => r.day == StoreStation.GetDayCounter() && r.kind == kind &&
            r.events == 1 && r.income == income && r.expense == expense), "Ledger differs from cash delta: " + raw);
    }

    internal static void SetOptions(bool value, params string[] keys)
    {
        var options = StoreUIManager.Instance.gameOptionUIManager;
        options.OpenUI();
        var w = CustomUIManager.Instance.GetWindow("game_options");
        foreach (var key in keys) w.Get(Mod + ":" + key).SetBool(value, true);
        w.Get("apply").button.onClick.Invoke();
        foreach (var key in keys) Check(((ConfigEntry<bool>)ModConfig.Find(Mod).Get(key)).Value == value, "Apply did not set " + key);
        options.CloseUI();
    }

    private static GameItem NewItem(string id, bool own = true)
    {
        var obj = DirectoryMaster.Item(id);
        Check(obj != null, "Missing native item " + id);
        GeneralHelper.SetItemOwned(obj, own);
        return obj;
    }

    private static void Put(GameInventory grid, GameItem item, GridShape shape = null)
    {
        var marker = shape == null ? grid.TryFindOneValidInventorySlot(item) : grid.TryInventorySlot(item, item.unitCount, shape);
        Check(marker != null && marker.inventory == grid && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == grid, "Cannot place fixture " + item.identifier);
    }

    private static void CheckYeast()
    {
        var e = EmporiumEntry.Instance;
        var bag = NewItem("backpack_medium");
        Put(e.invElement, bag);
        var home = (GameGridInventory)bag.contentWindow.childElement;
        var first = NewItem("wine_yeast_infinite");
        var shape = new GridShapeBuilder(first.modifiedShape).SetTransform(1, 2, false, 1);
        Put(home, first, shape);
        Brew(first, true, (b, grid) =>
        {
            Check(WineHelper.TryStartFermentation(b) && b.GetTagReadonly(WineHelper.WINE_CONTAINER_STATE).GetInt() == 1, "Native recipe did not begin");
            Check(first.parentInventory == home && first.modifiedShape.minX == 1 && first.modifiedShape.minY == 2 &&
                first.modifiedShape.orientation == shape.orientation && first.modifiedShape.flipped == shape.flipped, "Yeast did not return to its exact original slot/orientation");
            Check(b.GetTagReadonly(WineHelper.WINE_YEAST_POWER_INT).GetInt() == 2 && WaterHelper.GetCurrentCapacityML(b) == 0,
                "Recipe lost yeast strength or failed to consume water");
        });
        Brew(first, true, (b, grid) =>
        {
            var blocker = NewItem("wine_yeast_infinite");
            Put(home, blocker, shape);
            var roots = new GameInventory[] { e.invElement, e.backInvinvElement, e.showcaseElement, e.frontInvinvElement };
            bool[] locks = roots.Select(r => r.overrideLockInsert).ToArray();
            try
            {
                foreach (var g in roots) g.overrideLockInsert = true;
                Check(!WineHelper.TryStartFermentation(b) && first.parentInventory == grid && b.GetTagReadonly(WineHelper.WINE_CONTAINER_STATE).GetInt() == 0 &&
                    WaterHelper.GetCurrentCapacityML(b) >= 749 && grid.childItems.Any(i => i.IsTag(WineHelper.WINE_INGREDIENT_TAG)),
                    "Unavailable return storage started brewing or consumed ingredients");
                var prompt = GenericConfirmationUIManager.Instance;
                Check(prompt.panel.activeSelf, "Missing free-space prompt");
                prompt.CancelButtonPressed();
            }
            finally { for (int i = 0; i < roots.Length; i++) roots[i].overrideLockInsert = locks[i]; }
            Check(WineHelper.TryStartFermentation(b) && roots.Contains(first.parentInventory) && blocker.parentInventory == home,
                "Occupied origin did not preserve both yeast instances");
        });
        var ordinary = NewItem("wine_yeast");
        Brew(ordinary, false, (b, grid) =>
        {
            Check(!WineHelper.TryStartFermentation(b) && ordinary.parentInventory == grid, "Incomplete native recipe consumed ordinary yeast");
            GenericConfirmationUIManager.Instance.ClosePanel();
            Put(grid, NewItem("wine_berry"));
            Check(WineHelper.TryStartFermentation(b) && ordinary.parentInventory == null && b.GetTagReadonly(WineHelper.WINE_YEAST_POWER_INT).GetInt() == 2,
                "Ordinary yeast was incorrectly returned or not used");
        });
    }

    private static void Brew(GameItem yeast, bool addBerry, Action<GameItem, GameInventory> test)
    {
        var bottle = NewItem("wine_bottle");
        Put(EmporiumEntry.Instance.invElement, bottle);
        WaterHelper.AddWater(bottle, 2, 750000);
        var panels = (MachineryPanelRow)bottle.contentWindow.childElement;
        var grid = MachineryContainerUtils.Collect(panels.GetPanel("ingredients").content).OfType<GameGridInventory>().Single();
        if (addBerry) Put(grid, NewItem("wine_berry"));
        Put(grid, yeast);
        test(bottle, grid);
    }
}
