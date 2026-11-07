using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Exercises the actual attention buttons and native confirmation on an isolated save.
public sealed class MachineRuleRecoveryScenario : EquipmentFixture
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string MaintenanceRaw = "{\"version\":2,\"legacy\":true}", FeedRaw = "{\"version\":1,\"staged\":[123,456]}";
    private readonly float deadline = Time.realtimeSinceStartup + 90f;
    private Assembly assembly;
    private Type machineType, bookType;
    private object machineTools;
    private GameItem furnace;
    private HashSet<string> unsaved;
    private string unsavedKey, attentionRaw;

    public bool Tick(out string result)
    {
        result = null;
        Check(Time.realtimeSinceStartup < deadline, "Machine rule recovery timed out at " + step);
        if (!Ready()) return false;
        if (step == 0)
        {
            TradeScenario.SetOptions(true, "f31.enabled", "f35.enabled", "f33.enabled");
            EmporiumEntry.Instance.invElement.SetShape(24, 20);
            furnace = Stock("furnace");
            int id = furnace.GetUniqueID();
            assembly = ModLoader.Mods.Single(mod => mod.Manifest.ID == Mod).Assembly;
            machineType = assembly.GetType("ShopAssist.MachineTools", true);
            bookType = assembly.GetType("ShopAssist.AttentionBook", true);
            machineTools = machineType.GetField("current", Flags).GetValue(null);
            unsaved = (HashSet<string>)machineType.GetField("feedUnsaved", Flags).GetValue(machineTools);
            unsavedKey = PlayerStore.Instance.runID + ":" + PlayerStore.Instance.saveSlotId + ":" + id;
            unsaved.Add(unsavedKey);

            Type opType = assembly.GetType("ShopAssist.OperationResult", true);
            object operation = Activator.CreateInstance(opType, Flags, null, new object[] { "f33.open" }, null);
            opType.GetMethod("Skip", Flags).Invoke(operation, new object[] { furnace, 1, "f33.inspect", "QA inspection", true });
            bookType.GetMethod("Report", Flags).Invoke(null, new object[] { operation, false });
            attentionRaw = ModHelper.GetModData(Mod, "attention", null);
            Check(!string.IsNullOrEmpty(attentionRaw), "The independent inspection fixture was not saved");
            ModHelper.SetModData(Mod, "machineMaintenance", MaintenanceRaw);
            ModHelper.SetModData(Mod, "f33.rules", FeedRaw);
            OpenAttention();
            Check(HasIssue("machines:maintenance:invalid") && HasIssue("machines:f33:invalid") && HasIssue("machines:f33:" + id),
                "Unreadable rules or the separate unsaved furnace inspection are missing");
            Click("maintenance.reset");
            Check(GenericConfirmationUIManager.Instance.panel.activeSelf && Read("machineMaintenance") == MaintenanceRaw,
                "Maintenance reset did not require confirmation or changed the original early");
            GenericConfirmationUIManager.Instance.CancelButtonPressed();
            Check(Read("machineMaintenance") == MaintenanceRaw, "Cancelling confirmation changed the maintenance record");
            OpenAttention(); Click("maintenance.reset");
            const string changed = "{\"changed\":true}";
            ModHelper.SetModData(Mod, "machineMaintenance", changed);
            GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
            Check(Read("machineMaintenance") == changed, "A stale confirmation overwrote a newer record");
            ModHelper.SetModData(Mod, "machineMaintenance", MaintenanceRaw);
            OpenAttention(); Click("maintenance.reset");
            GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
            Check(Readable("ReadMaintenance") && Read("machineMaintenance") != MaintenanceRaw &&
                Read(BackupKey("machineMaintenance", MaintenanceRaw)) == MaintenanceRaw,
                "Maintenance rules were not rebuilt with their original preserved");
            Check(Read("attention") == attentionRaw && unsaved.Contains(unsavedKey), "Maintenance reset changed an independent inspection");
            step++; return false;
        }
        OpenAttention(); Click("f33.reset");
        var confirmation = GenericConfirmationUIManager.Instance;
        string warning = ModHelper.GetLocalized(Mod, "f33.reset_warning");
        Check(confirmation.panel.activeSelf && confirmation.panel.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == warning) &&
            Read("f33.rules") == FeedRaw, "F33 must warn about the furnace and preserve its record before confirmation");
        confirmation.ConfirmButtonPressed();
        Check(Readable("ReadFeedRules") && Read("f33.rules") != FeedRaw && Read(BackupKey("f33.rules", FeedRaw)) == FeedRaw,
            "F33 rules were not rebuilt with their original preserved");
        Check(Read("attention") == attentionRaw && unsaved.Contains(unsavedKey) && HasIssue("machines:f33:" + furnace.uniqueId),
            "F33 reset cleared an unrelated inspection or unsaved furnace state");
        result = "PASS maintenance and F33 resets require confirmation, reject stale data, retain originals and preserve inspections";
        return true;
    }

    private void OpenAttention()
    {
        bookType.GetMethod("Changed", Flags).Invoke(null, null);
        bookType.GetMethod("Open", Flags).Invoke(null, null);
        Check(CustomUIManager.Instance.GetWindow(Mod + ".attention")?.IsAlive == true, "Attention list did not open");
    }

    private bool HasIssue(string key)
    {
        var items = (IEnumerable)bookType.GetMethod("Snapshot", Flags).Invoke(null, null);
        foreach (object item in items)
            if ((string)item.GetType().GetField("Key", Flags).GetValue(item) == key) return true;
        return false;
    }

    private void Click(string key)
    {
        CustomUIWindow window = CustomUIManager.Instance.GetWindow(Mod + ".attention");
        string title = ModHelper.GetLocalized(Mod, key);
        var matches = window.root.GetComponentsInChildren<Button>(true)
            .Where(button => button.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == title)).ToArray();
        Check(matches.Length == 1, "Expected one attention button for " + key + ", found " + matches.Length);
        matches[0].onClick.Invoke();
    }

    private bool Readable(string method)
    {
        var arguments = new object[] { null };
        return (bool)machineType.GetMethod(method, Flags).Invoke(machineTools, arguments);
    }
    private static string Read(string key) => ModHelper.GetModData(Mod, key, null);
    private static string BackupKey(string key, string raw)
    {
        using (SHA256 hash = SHA256.Create())
            return key + ".unreadable." + BitConverter.ToString(hash.ComputeHash(Encoding.Unicode.GetBytes(raw))).Replace("-", "").ToLowerInvariant();
    }
}
