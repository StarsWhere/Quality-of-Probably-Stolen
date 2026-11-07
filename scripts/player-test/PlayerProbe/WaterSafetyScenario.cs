using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Exercises the real confirmation and native pour with state changes from another mod.
public sealed class WaterSafetyScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90;
    private static GameItem a, b, destination, changed;
    private static int mode, calls, changedBefore;
    private static void Need(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static CustomUIWindow Window(string name = "water") => CustomUIManager.Instance.GetWindow(Mod + "." + name);
    private static Type ModType(string name) => ModLoader.Mods.First(m => m.Manifest?.ID == Mod).Instance.GetType().Assembly.GetType("ShopAssist." + name, true);
    private static int Grade(GameItem item) => (int)ModType("WaterGather").GetMethod("Grade", Flags).Invoke(null, new object[] { item });
    private static bool Inspect(GameItem item) => (bool)ModType("AttentionBook").GetMethod("HasInspection", Flags).Invoke(null, new object[] { "f26.open", item.uniqueId });
    private static bool Selected(GameItem item)
    {
        var mod = ModLoader.Mods.First(m => m.Manifest?.ID == Mod).Instance;
        var modules = (IEnumerable)mod.GetType().GetField("modules", Flags).GetValue(mod);
        foreach (object module in modules)
        {
            if ((string)module.GetType().GetField("Name", Flags).GetValue(module) != "F26") continue;
            var action = (Delegate)module.GetType().GetField("Start", Flags).GetValue(module);
            return ((System.Collections.Generic.HashSet<GameItem>)action.Target.GetType().GetField("chosen", Flags).GetValue(action.Target)).Contains(item);
        }
        throw new InvalidOperationException("Water module unavailable");
    }
    private static GameItem Item(string id, string name, int quality, int volume)
    {
        var item = DirectoryMaster.Item(id); Need(item != null, "Missing water fixture " + id);
        GeneralHelper.SetItemOwned(item, true); GeneralHelper.SetCustomName(item, name);
        var root = EmporiumEntry.Instance.invElement; var marker = root.TryFindOneValidInventorySlot(item);
        Need(marker != null && marker.inventory == root && marker.targetItem == null &&
            marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == root, "Cannot place " + name);
        WaterHelper.Remove(item, WaterHelper.GetCurrentCapacityUL(item));
        if (volume > 0) { WaterHelper.AddWater(item, quality, volume); WaterFeatureHelper.HandleWaterCheckTestingStrip(item); }
        return item;
    }
    private static void AfterTarget(GameItem __instance, GameItem __0)
    {
        if (mode == 0 || !ReferenceEquals(__0, destination) || (!ReferenceEquals(__instance, a) && !ReferenceEquals(__instance, b))) return;
        calls++;
        if (mode == 2) throw new InvalidOperationException("QA injected liquid callback failure");
        changed = ReferenceEquals(__instance, a) ? b : a;
        changedBefore = WaterHelper.GetCurrentCapacityUL(changed);
        int grade = Grade(changed);
        WaterHelper.AddWater(changed, ReferenceEquals(changed, a) ? 0 : 5, 400);
        WaterFeatureHelper.HandleWaterCheckTestingStrip(changed);
        Need(Grade(changed) == grade, "Injected liquid change unexpectedly changed the public grade");
        mode = 0;
    }
    private static void ApplyWithFault(int fault)
    {
        const string id = "qa.water-state-boundary";
        var harmony = new Harmony(id); calls = 0; mode = fault;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(GameItem), nameof(GameItem.Target), new[] { typeof(GameItem) }),
                postfix: new HarmonyMethod(typeof(WaterSafetyScenario).GetMethod(nameof(AfterTarget), Flags)));
            Window().Get("pour").button.onClick.Invoke();
        }
        finally { harmony.UnpatchAll(id); mode = 0; }
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Water boundary test timed out");
        if (!start.Tick()) return false;
        if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true) return false;
        TradeScenario.SetOptions(true, "f26.enabled", "f26.mix");
        a = Item("bottled_water", "QA water boundary A", 0, 10000);
        b = Item("small_bottled_water", "QA water boundary B", 5, 10000);
        destination = Item("water_jug", "QA water boundary target", 0, 0);
        var secondTarget = Item("water_jug", "QA water inspection target", 0, 0);
        Need(Grade(a) >= 0 && Grade(b) >= 0 && Grade(a) != Grade(b), "Water grades are not publicly distinguishable");
        RoutingScenario.RunFeatureAction("f26.open"); QaItemUi.Choose(Window(), "target", "QA water boundary target");
        QaItemUi.Click(Window(), a); QaItemUi.Click(Window(), b); Window().Get("preview").button.onClick.Invoke();
        var preview = Window(); TradeScenario.SetOptions(false, "f26.mix");
        preview.Get("pour").button.onClick.Invoke();
        Need(WaterHelper.GetCurrentCapacityUL(a) == 10000 && WaterHelper.GetCurrentCapacityUL(b) == 10000 &&
            WaterHelper.GetCurrentCapacityUL(destination) == 0 && Selected(a) && Selected(b), "A stale mixed plan was poured or its selection lost");
        TradeScenario.SetOptions(true, "f26.mix"); Window().Get("preview").button.onClick.Invoke();
        ApplyWithFault(1);
        Need(calls == 1 && changed != null && WaterHelper.GetCurrentCapacityUL(changed) == changedBefore + 400 && Selected(changed),
            "A source changed after the first pour was processed or discarded");
        Window("operation")?.Close();
        QaItemUi.Choose(Window(), "target", "QA water inspection target");
        destination = secondTarget;
        Window().Get("preview").button.onClick.Invoke(); ApplyWithFault(2);
        Need(calls == 1 && Inspect(changed) && Inspect(secondTarget) && Selected(changed),
            "An interrupted native pour did not preserve the source and mark both vessels for inspection");
        Window("operation")?.Close(); Window().Close();
        RoutingScenario.RunFeatureAction("f26.open");
        Need(Inspect(changed) && Inspect(secondTarget), "Closing and reopening cleared interrupted-pour inspection");
        result = "PASS F26 current mixing policy, per-source volume revalidation and interrupted-pour inspection";
        return true;
    }
}
