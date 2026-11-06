using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

// Collects actual Unity objects after invoking the base game's inspection UI.
public sealed class InspectionTreeScenario
{
    private QaLoadSlot load;
    private int step;
    private float started = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - started > 75) throw new InvalidOperationException("Inspection tree probe timed out at step " + step);
        if (step == 0)
        {
            if (MainMenuUIController.Instance == null) return false;
            string[] context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-latest-run.txt")).Trim().Split('|');
            if (context.Length != 4 || context[0] != "1" || !Guid.TryParseExact(context[1], "N", out Guid run) || !int.TryParse(context[2], out int slot))
                throw new InvalidOperationException("Invalid isolated QA run context");
            load = new QaLoadSlot(slot, run); step = 1; return false;
        }
        if (step == 1)
        {
            if (!load.Tick() || InspectionUIManager.Instance == null || EmporiumEntry.Instance?.frontInvinvElement == null) return false;
            GameItem item = PreBuiltItemHelper.CounterfeitCigarette();
            if (item == null) throw new InvalidOperationException("Could not create native inspectable QA item");
            InspectionUIManager.Instance.OnInspectItem(item);
            if (InspectionUIManager.Instance.panel == null || !InspectionUIManager.Instance.panel.activeInHierarchy ||
                !ReferenceEquals(InspectionUIManager.Instance.currentInspectingItem, item)) throw new InvalidOperationException("Native inspection UI did not open");
            step = 2; return false;
        }
        var ui = InspectionUIManager.Instance;
        var output = new StringBuilder();
        output.AppendLine("Native inspection hierarchy; sample=CounterfeitCigarette; active=" + ui.panel.activeInHierarchy);
        var anchors = new Dictionary<string, GameObject>
        {
            ["panel"] = ui.panel, ["magnifyingGlass"] = ui.magnifyingGlass, ["blockerTray"] = ui.blockerTray,
            ["cigaretteUILarge"] = ui.cigaretteUILarge?.gameObject, ["fuelUILarge"] = ui.fuelUILarge?.gameObject,
            ["stampUILarge"] = ui.stampUILarge?.gameObject, ["injectorUILarge"] = ui.injectorUILarge?.gameObject,
            ["ribwichUILarge"] = ui.ribwichUILarge?.gameObject, ["appHandUILarge"] = ui.appHandUILarge?.gameObject,
            ["insBerryUILarge"] = ui.insBerryUILarge?.gameObject
        };
        foreach (var pair in anchors) output.AppendLine("ANCHOR " + pair.Key + " = " + PathOf(pair.Value == null ? null : pair.Value.transform));
        WriteTree(ui.panel.transform, output);
        string path = Path.Combine(Application.persistentDataPath, "qa-inspection-tree.txt");
        File.WriteAllText(path, output.ToString());
        ui.CloseUI();
        result = "PASS native inspection opened; object tree saved to " + path;
        return true;
    }

    private static string PathOf(Transform value)
    {
        if (value == null) return "<null>";
        var parts = new List<string>();
        for (Transform current = value; current != null; current = current.parent) parts.Add(current.name);
        parts.Reverse(); return string.Join("/", parts);
    }

    private static void WriteTree(Transform root, StringBuilder output)
    {
        var pending = new Stack<Transform>(); pending.Push(root);
        int count = 0;
        while (pending.Count != 0)
        {
            Transform node = pending.Pop();
            if (++count > 5000) throw new InvalidOperationException("Inspection hierarchy exceeded 5000 objects");
            output.Append("NODE ").Append(PathOf(node)).Append(" activeSelf=").Append(node.gameObject.activeSelf)
                .Append(" activeHierarchy=").Append(node.gameObject.activeInHierarchy).Append(" scale=").Append(node.localScale).AppendLine();
            if (node is RectTransform rect)
                output.Append("  RECT anchorMin=").Append(rect.anchorMin).Append(" anchorMax=").Append(rect.anchorMax)
                    .Append(" pivot=").Append(rect.pivot).Append(" position=").Append(rect.anchoredPosition)
                    .Append(" size=").Append(rect.sizeDelta).AppendLine();
            foreach (Component component in node.GetComponents<Component>())
            {
                if (component == null) { output.AppendLine("  COMPONENT <missing>"); continue; }
                output.Append("  COMPONENT ").Append(component.GetType().FullName);
                if (component is Behaviour behaviour) output.Append(" enabled=").Append(behaviour.enabled);
                if (component is Graphic graphic) output.Append(" raycastTarget=").Append(graphic.raycastTarget);
                if (component is CanvasGroup group) output.Append(" blocksRaycasts=").Append(group.blocksRaycasts).Append(" interactable=").Append(group.interactable);
                if (component is Canvas canvas) output.Append(" overrideSorting=").Append(canvas.overrideSorting).Append(" sortingOrder=").Append(canvas.sortingOrder);
                output.AppendLine();
            }
            for (int i = node.childCount - 1; i >= 0; i--) pending.Push(node.GetChild(i));
        }
    }
}
