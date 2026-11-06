using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Exercises both native panels and records their live hierarchy for layout QA.
public sealed class ScannerLayoutScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private GameItem sample;
    private int step;
    private float until;
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public bool Tick(out string result)
    {
        result = null;
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || WaterScannerUIManager.Instance == null || ChemScannerUIManager.Instance == null) return false;
            sample = DirectoryMaster.Item("wine_bottle"); Need(sample != null, "No scanner sample");
            GeneralHelper.SetItemOwned(sample);
            var inventory = EmporiumEntry.Instance.invElement;
            var slot = inventory.TryFindOneValidInventorySlot(sample);
            Need(slot != null && slot.TryAcceptOnce(sample.unitCount) == sample.unitCount, "Cannot place scanner sample");
            WaterHelper.AddLiquid(sample, "water", 730000);
            WaterHelper.AddLiquid(sample, "organic_waste", 12000);
            WaterHelper.AddLiquid(sample, "microbe", 23);
            var config = ModConfig.Find("starswhere.shopassist");
            Need(config?.Get("f19.enabled") is ConfigEntry<bool>, "Missing F19 setting");
            ((ConfigEntry<bool>)config.Get("f19.enabled")).Set(false);
            WaterScannerUIManager.Instance.Open(null, sample);
            until = Time.realtimeSinceStartup + 1; step++; return false;
        }
        if (step == 2)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Dump("water", WaterScannerUIManager.Instance.panel);
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-scanner-native-water.png"));
            until = Time.realtimeSinceStartup + 1; step++; return false;
        }
        if (step == 3)
        {
            if (Time.realtimeSinceStartup < until) return false;
            WaterScannerUIManager.Instance.Close();
            ChemScannerUIManager.Instance.Open(null, sample);
            until = Time.realtimeSinceStartup + 1; step++; return false;
        }
        if (step == 4)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Dump("chem", ChemScannerUIManager.Instance.panel);
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-scanner-native-chem.png"));
            until = Time.realtimeSinceStartup + 1; step++; return false;
        }
        if (Time.realtimeSinceStartup < until) return false;
        ChemScannerUIManager.Instance.Close();
        result = "PASS dumped native scanner panels and captured both screenshots"; return true;
    }
    private static void Dump(string name, GameObject panel)
    {
        var lines = panel.GetComponentsInChildren<RectTransform>(true).Select(r =>
        {
            string path = r.name; Transform p = r.parent;
            while (p != null && p != panel.transform.parent) { path = p.name + "/" + path; p = p.parent; }
            var t = r.GetComponent<TMP_Text>(); var b = r.GetComponent<Button>(); var image = r.GetComponent<Image>();
            return path + " active=" + r.gameObject.activeInHierarchy + " anchors=" + r.anchorMin + "/" + r.anchorMax +
                " pivot=" + r.pivot + " pos=" + r.anchoredPosition + " size=" + r.sizeDelta + " rect=" + r.rect +
                " scale=" + r.localScale + (t != null ? " TMP=" + t.text.Replace("\n", "|") + " font=" + t.fontSize : "") +
                (image != null ? " IMAGE=" + image.enabled + " sprite=" + (image.sprite != null ? image.sprite.name : "<none>") + " color=" + image.color : "") +
                (b != null ? " BUTTON" : "");
        });
        File.WriteAllLines(Path.Combine(Application.persistentDataPath, "qa-scanner-layout-" + name + ".txt"), lines);
    }
}
