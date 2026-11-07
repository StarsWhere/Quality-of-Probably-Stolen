using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Native scanner data and controls remain authoritative while the view updates in place.
public sealed class ScannerUxScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 90;
    private GameItem sample, other;
    private int step;

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Transform Root() => WaterScannerUIManager.Instance.panel.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(node => node.name == "ShopAssist_LiquidBars" && node.gameObject.activeInHierarchy);
    private static Transform Row(Transform root, string id) => root?.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(node => node.name == "Component_" + id && node.gameObject.activeInHierarchy);
    private static string[] Order(Transform root) => root.GetComponentsInChildren<Transform>(true)
        .Where(node => node.gameObject.activeInHierarchy && node.name.StartsWith("Component_", StringComparison.Ordinal))
        .Select(node => node.name.Substring("Component_".Length)).ToArray();
    private static Button Pin(Transform row) => row.GetComponentsInChildren<Button>(true)
        .FirstOrDefault(button => button.gameObject.name == "Button_☆" || button.gameObject.name == "Button_★");
    private static string Text(Transform node) => string.Join(" | ", node.GetComponentsInChildren<TMP_Text>(true).Select(text => text.text));

    private static Vector2 Center(Transform target)
    {
        var rect = (RectTransform)target; var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        return RectTransformUtility.WorldToScreenPoint(camera, (corners[0] + corners[2]) * .5f);
    }

    private static GameItem NewSample()
    {
        GameItem item = DirectoryMaster.Item("wine_bottle"); Need(item != null, "Missing scanner sample");
        GeneralHelper.SetItemOwned(item);
        var slot = EmporiumEntry.Instance.invElement.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.TryAcceptOnce(item.unitCount) == item.unitCount, "Cannot place scanner sample");
        WaterHelper.Remove(item, WaterHelper.GetCurrentCapacityUL(item));
        return item;
    }

    private static void ResetPins()
    {
        RoutingScenario.RunFeatureAction("f19.choose");
        var picker = CustomUIManager.Instance.GetWindow(Mod + ".f19.pin");
        Button clear = picker?.root.GetComponentsInChildren<Button>(true).FirstOrDefault(button =>
            button.GetComponentInChildren<TMP_Text>(true)?.text == ModHelper.GetLocalized(Mod, "f19.none"));
        Need(clear != null, "Missing clear-pins action"); clear.onClick.Invoke();
        CustomUIManager.Instance.GetWindow(Mod + ".f19.pin")?.Close();
    }

    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Scanner UX scenario timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || WaterScannerUIManager.Instance == null) return false;
            TradeScenario.SetOptions(true, "f19.enabled", "f19.bars", "f19.sort");
            TradeScenario.SetOptions(false, "f19.colors");
            sample = NewSample(); other = NewSample();
            WaterHelper.AddLiquid(sample, "water", 500000); WaterHelper.AddLiquid(sample, "organic_waste", 100000);
            WaterHelper.AddLiquid(sample, "mineral", 20000); WaterHelper.AddLiquid(sample, "microbe", 2000);
            WaterHelper.AddLiquid(other, "water", 200000);
            WaterScannerUIManager.Instance.Open(null, sample);
            step = 11; return false;
        }
        if (step == 11)
        {
            if (Root() == null) return false;
            ResetPins(); step = 2; return false;
        }
        if (step == 2)
        {
            var scanner = WaterScannerUIManager.Instance; Transform root = Root();
            Image screen = root?.Find("ScreenCenter")?.GetComponent<Image>();
            Need(screen != null && screen.color == (Color)new Color32(41, 34, 52, 255) && !screen.raycastTarget && Row(root, "microbe") != null,
                "Scanner did not redraw the native screen and components");
            using (var input = new QaSyntheticInput())
            {
                input.Mouse = new Vector3(-1000, -1000);
                Pin(Row(root, "microbe")).onClick.Invoke(); Pin(Row(root, "organic_waste")).onClick.Invoke();
                Need(Order(root).SequenceEqual(new[] { "microbe", "organic_waste", "water", "mineral" }), "Pin order or default share order is incorrect");
            }
            Transform oldRoot = root, oldWater = Row(root, "water"), oldMicrobe = Row(root, "microbe");
            using (var input = new QaSyntheticInput())
            {
                input.Mouse = Center(root);
                WaterHelper.Remove(sample, WaterHelper.GetCurrentCapacityUL(sample));
                WaterHelper.AddLiquid(sample, "water", 10000); WaterHelper.AddLiquid(sample, "organic_waste", 100000);
                WaterHelper.AddLiquid(sample, "mineral", 400000);
                scanner.RefreshUI();
                Need(ReferenceEquals(root, Root()) && ReferenceEquals(oldWater, Row(root, "water")) && ReferenceEquals(oldMicrobe, Row(root, "microbe")),
                    "Live refresh recreated the scanner or row controls");
                Need(Order(root).SequenceEqual(new[] { "microbe", "organic_waste", "water", "mineral" }) &&
                    Text(oldMicrobe).Contains(WaterScannerUIManager.FormatAmount(0)) && Text(oldWater).Contains(WaterScannerUIManager.FormatAmount(10000)),
                    "Hover did not preserve row order while updating measurements and a depleted pin");
            }
            using (var input = new QaSyntheticInput())
            {
                input.Mouse = new Vector3(-1000, -1000); scanner.RefreshUI();
                Need(ReferenceEquals(oldRoot, Root()) && Order(root).SequenceEqual(new[] { "microbe", "organic_waste", "mineral", "water" }),
                    "The list did not sort after the pointer left");
            }
            scanner.Close(); scanner.Open(null, other);
            root = Root();
            Need(root != null && Row(root, "water") != null && Row(root, "microbe") == null && Row(root, "organic_waste") == null,
                "A different sample inherited zero rows from the previous sample");
            ResetPins();
            string native = scanner.contaminantTMP.text;
            TradeScenario.SetOptions(false, "f19.enabled");
            Need(Root() == null && scanner.contaminantTMP.enabled && scanner.contaminantTMP.text == native,
                "Disabling the enhancement did not restore the original scanner");
            scanner.Close();
            result = "PASS incremental scanner rows, ordered pins, hover stability, sample-scoped zeroes and native restoration";
            return true;
        }
        return false;
    }
}
