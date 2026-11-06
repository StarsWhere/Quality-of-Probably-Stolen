using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

public sealed class WaterBasketScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step, money;
    private GameItem offered, unseen;
    private static GameItem faultSource, faultTarget;
    private float since = Time.realtimeSinceStartup;
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static CustomUIWindow W(string id) => CustomUIManager.Instance.GetWindow(Mod + "." + id);
    private static void Action(string key) => RoutingScenario.OpenOptionAction(key);
    private static GameItem Item(string id, string label, GameInventory grid)
    {
        var i = DirectoryMaster.Item(id); Need(i != null, "Missing native fixture " + id);
        GeneralHelper.SetItemOwned(i); GeneralHelper.SetCustomName(i, label); Put(i, grid); return i;
    }
    private static void Put(GameItem i, GameInventory grid)
    {
        var m = grid.TryFindOneValidInventorySlot(i);
        Need(m != null && m.inventory == grid && m.targetItem == null && m.TryAcceptOnce() > 0 && i.parentInventory == grid,
            "Native fixture placement failed: " + i.identifier);
    }
    private static string Labels(CustomUIWindow w) => string.Join(" | ", w.root.GetComponentsInChildren<TMPro.TMP_Text>().Select(t => t.text));
    private static void Water()
    {
        var root = EmporiumEntry.Instance.invElement;
        var src = Item("bottled_water", "QA measured source", root);
        var dest = Item("water_jug", "QA receiving jug", root);
        var unknown = Item("small_bottled_water", "QA untested", root);
        var farm = Item("moisture_farm", "QA collector", root);
        var socket = MachineryContainerUtils.Collect(((MachineryPanelRow)farm.contentWindow.childElement).GetPanel("container").content)
            .OfType<GameSlotInventory>().Single();
        Put(src, socket);
        foreach (var i in new[] { src, dest, unknown }) WaterHelper.Remove(i, WaterHelper.GetCurrentCapacityUL(i));
        WaterHelper.AddWater(src, 0, 1800); WaterHelper.AddWater(unknown, 0, 1250);
        WaterHelper.AddWater(dest, 0, WaterHelper.GetTotalCapacity(dest) - 300);
        foreach (var i in new[] { src, dest })
        {
            WaterFeatureHelper.UpdateWaterFeature(i, false); WaterFeatureHelper.GetWaterFeature(i).SetExposed(false);
            WaterFeatureHelper.HandleWaterCheckTestingStrip(i);
        }
        WaterFeatureHelper.GetWaterFeature(unknown).SetExposed(false);
        WaterFeatureHelper.GetWaterFeature(unknown).SetFeatureDiscovered(false);
        Action("f26.open");
        QaItemUi.Choose(W("water"), "target", "QA receiving jug");
        var w = W("water");
        Need(QaItemUi.Hit(w.root, src) != null && QaItemUi.Hit(w.root, unknown) == null, "Known/unknown water was not filtered correctly");
        QaItemUi.Click(w, src);
        W("water").Get("preview").button.onClick.Invoke();
        Need(WaterHelper.GetCurrentCapacityUL(src) == 1800 && WaterHelper.GetFreeCapacity(dest) == 300, "Water preview changed contents");
        W("water").Get("pour").button.onClick.Invoke();
        Need(WaterHelper.GetCurrentCapacityUL(src) == 1500 && WaterHelper.GetFreeCapacity(dest) == 0 && WaterHelper.GetCurrentCapacityUL(unknown) == 1250,
            "Native partial pour failed conservation at 300 µL");
        string amount = 0.3m.ToString("0.###", CultureInfo.CurrentCulture);
        string notice = GameObject.Find(Mod + ".notice")?.GetComponentInChildren<TMPro.TMP_Text>()?.text ?? "";
        Need((W("operation") == null || !W("operation").IsAlive) &&
            notice.Contains(ModHelper.GetLocalized(Mod, "f26.result", amount, amount)) &&
            notice.Contains(ModHelper.GetLocalized(Mod, "f08.full")),
            "A normal full target did not show its 0.3 mL flows and full status without opening failure details: " + notice);
        Need(QaItemUi.Selected(W("water"), src), "Partial source was not retained for the current session");
        W("water").Close();
        FilteredWater(root, src);
        InterruptedWater(src, dest);
    }
    private static void FilteredWater(GameInventory root, GameItem previous)
    {
        GameItem Pitcher(string label)
        {
            var pitcher = Item("water_pitcher", label, root);
            var socket = GraphUtils.FindAllChildrenType<GameSlotInventory>(pitcher).Single();
            Item("water_filter", label + " filter", socket);
            WaterHelper.Remove(pitcher, WaterHelper.GetCurrentCapacityUL(pitcher));
            return pitcher;
        }
        GameItem Mixture(string label)
        {
            var source = Item("bottled_water", label, root);
            WaterHelper.Remove(source, WaterHelper.GetCurrentCapacityUL(source));
            WaterHelper.AddLiquid(source, "water", 50000); WaterHelper.AddLiquid(source, "organic_waste", 2000);
            WaterFeatureHelper.HandleWaterCheckTestingStrip(source);
            return source;
        }
        var control = Pitcher("QA native pitcher"); var controlSource = Mixture("QA native liquid");
        var dest = Pitcher("QA filtered pitcher"); var source = Mixture("QA filtered source");
        controlSource.Target(control);
        Action("f26.open");
        QaItemUi.Choose(W("water"), "target", "QA filtered pitcher");
        Need(!QaItemUi.Selected(W("water"), previous), "A closed water session retained its old source");
        QaItemUi.Click(W("water"), source);
        Need(QaItemUi.Selected(W("water"), source), "The selected filtered source was disabled or not selected");
        W("water").Get("preview").button.onClick.Invoke();
        Need(QaItemUi.Hit(W("water").root, source) != null && QaItemUi.Hit(W("water").root, previous) == null,
            "The filtered preview contains a source from the closed water session");
        W("water").Get("pour").button.onClick.Invoke();
        int received = WaterHelper.GetCurrentCapacityUL(dest);
        Need(WaterHelper.GetCurrentCapacityUL(source) == 0 && received > 0 && received < 52000 &&
            WaterHelper.GetLiquidParts(dest).SequenceEqual(WaterHelper.GetLiquidParts(control)) &&
            DurabilityHelper.GetRealDurability(WaterHelper.GetFilterItem(dest)) == DurabilityHelper.GetRealDurability(WaterHelper.GetFilterItem(control)),
            "Native and assisted filter yields or wear differ; source=" + WaterHelper.GetCurrentCapacityUL(source) +
            ", received=" + received + ", native=" + WaterHelper.GetCurrentCapacityUL(control));
        W("water").Close();
    }
    private static void AfterFaultTarget(GameItem __instance, GameItem __0)
    {
        if (ReferenceEquals(__instance, faultSource) && ReferenceEquals(__0, faultTarget))
            throw new InvalidOperationException("QA injected liquid feedback failure");
    }
    private static bool NeedsInspection(GameItem item)
    {
        var assembly = ModLoader.Mods.First(m => m.Manifest?.ID == Mod).Instance.GetType().Assembly;
        var method = assembly.GetType("ShopAssist.AttentionBook", true).GetMethod("HasInspection", BindingFlags.Static | BindingFlags.NonPublic);
        return (bool)method.Invoke(null, new object[] { "f26.open", item.uniqueId });
    }
    private static void InterruptedWater(GameItem source, GameItem dest)
    {
        foreach (var item in new[] { source, dest }) WaterHelper.Remove(item, WaterHelper.GetCurrentCapacityUL(item));
        WaterHelper.AddWater(source, 0, 2500); WaterFeatureHelper.HandleWaterCheckTestingStrip(source);
        Action("f26.open"); QaItemUi.Choose(W("water"), "target", "QA receiving jug");
        QaItemUi.Click(W("water"), source); W("water").Get("preview").button.onClick.Invoke();
        const string patchId = "qa.water-feedback-interruption";
        var harmony = new Harmony(patchId); faultSource = source; faultTarget = dest;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(GameItem), nameof(GameItem.Target), new[] { typeof(GameItem) }),
                postfix: new HarmonyMethod(typeof(WaterBasketScenario).GetMethod(nameof(AfterFaultTarget), BindingFlags.Static | BindingFlags.NonPublic)));
            W("water").Get("pour").button.onClick.Invoke();
        }
        finally { harmony.UnpatchAll(patchId); faultSource = faultTarget = null; }
        var operation = W("operation");
        string confirmed = ModHelper.GetLocalized(Mod, "f26.confirmed", "0", "0");
        Need(WaterHelper.GetCurrentCapacityUL(source) == 0 && WaterHelper.GetCurrentCapacityUL(dest) == 2500 &&
            operation?.IsAlive == true && operation.Get("attention")?.button != null && Labels(operation).Contains(confirmed) &&
            NeedsInspection(source) && NeedsInspection(dest),
            "Interrupted liquid action did not report inspection with only confirmed flows");
        operation.Close(); W("water").Close();
    }
    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 55) throw new TimeoutException("Water/basket step " + step);
        if (step == 0) { if (start.Tick()) { step++; since = Time.realtimeSinceStartup; } return false; }
        var p = PlayerStore.Instance; var e = EmporiumEntry.Instance;
        switch (step)
        {
            case 1:
                if (e.storeInteractables?.activeInHierarchy != true || NegociationUIManager.Instance == null) return false;
                TradeScenario.SetOptions(true, "f26.enabled", "f34.enabled", "f34.move");
                Water();
                var buyer = StoreClientList.CreateSpacerClientFood(); buyer.SetBudget(9000); buyer.isIntroduced = true;
                p.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer); p.isClientArrived = true;
                offered = Item("cup_noodle", "QA quoted noodles", e.backInvinvElement);
                unseen = Item("processed_meat", "QA unquoted meat", e.invElement);
                NegociationUIManager.Instance.OpenUI(); money = p.playerCash;
                step++; return false;
            case 2:
                if (!NegociationUIManager.Instance.IsOpen) return false;
                Action("f34.open");
                var lines = W("basket");
                Need(lines.Get("compare") != null && Labels(lines).Contains("QA unquoted meat"), "Public buyer candidates missing: " + Labels(lines));
                Need(QaItemUi.Hit(lines.root, offered) != null, "No native counter quote was observed: " + Labels(lines));
                lines.Close(); Put(offered, e.invElement); NegociationUIManager.Instance.RefreshUI();
                step++; return false;
            case 3:
                Action("f34.open"); W("basket").Get("compare").button.onClick.Invoke();
                var basket = W("basket");
                var place = basket.root.GetComponentsInChildren<Button>().FirstOrDefault(b => b.GetComponentsInChildren<TMPro.TMP_Text>().Any(t =>
                    t.text == ModHelper.GetLocalized(Mod, "f34.place")));
                Need(place != null && offered.parentInventory == e.invElement && p.playerCash == money, "Basket preview changed stock/cash or no offer: " + Labels(basket));
                place.onClick.Invoke(); step++; return false;
            case 4:
                if (offered.parentInventory != e.backInvinvElement) return false;
                Need(unseen.parentInventory == e.invElement && p.playerCash == money && GeneralHelper.IsItemOwned(offered), "Staging sold goods or moved unpriced stock");
                result = "PASS known-grade water preview and 300 µL native pour, untested source excluded, real buyer quote, unpriced goods excluded and staged basket without sale";
                return true;
        }
        return false;
    }
}
