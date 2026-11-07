using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

// Exercises recovery only in an isolated new run; the injected fault follows the actual write.
public sealed class AttentionRecoveryScenario
{
    [Serializable] private sealed class Marker { public string backupKey = ""; public bool completed = false; }
    private const string Mod = "starswhere.shopassist", Corrupt = "{unreadable attention fixture";
    private const string PatchId = "qa.attention-recovery-write";
    private const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private QaLoadSlot reload;
    private Type book;
    private int step, slot;
    private string run, backupKey;
    private static bool failWrite;
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Raw(string key) => ModHelper.GetModData(Mod, key, null);
    private static void AfterWrite(string __0, string __1)
    {
        if (!failWrite || __0 != Mod || __1 != "attention") return;
        failWrite = false;
        throw new InvalidOperationException("QA fault after attention write");
    }
    private object Call(string name, params object[] args) => book.GetMethod(name, F).Invoke(null, args);
    private bool Blocked(int id) => (bool)Call("HasInspection", "qa.action", id);
    private void Open()
    {
        Call("Open");
        Need(CustomUIManager.Instance.GetWindow(Mod + ".attention")?.IsAlive == true, "Attention window did not open");
    }
    private void Ask()
    {
        var button = RoutingScenario.Button(CustomUIManager.Instance.GetWindow(Mod + ".attention"), "attention.rebuild");
        Need(button != null && button.interactable, "Rebuild action is unavailable");
        button.onClick.Invoke();
        var prompt = GenericConfirmationUIManager.Instance;
        Need(prompt?.panel.activeInHierarchy == true && prompt.panel.GetComponentsInChildren<TMP_Text>(true)
            .Any(text => text.text == ModHelper.GetLocalized(Mod, "attention.rebuild_warning")), "Recovery warning was not displayed");
    }
    private void ReportPending()
    {
        Type operation = book.Assembly.GetType("ShopAssist.OperationResult", true);
        object result = Activator.CreateInstance(operation, F, null, new object[] { "qa.action" }, null);
        operation.GetMethod("SkipMissing", F).Invoke(result, new object[] { "QA pending issue", 1, "inv.inspect", "", 101, true });
        Call("Report", result, false);
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup < deadline, "Attention recovery timed out at step " + step);
        switch (step)
        {
            case 0:
                if (!start.Tick()) return false;
                book = ModLoader.Mods.Single(mod => mod.Manifest?.ID == Mod).Instance.GetType().Assembly.GetType("ShopAssist.AttentionBook", true);
                run = PlayerStore.Instance.runID; slot = PlayerStore.Instance.saveSlotId;
                ModHelper.SetModData(Mod, "attention", Corrupt); Call("Reset"); ReportPending();
                Need(Blocked(999), "Unreadable data did not block operations");
                Open(); Ask(); GenericConfirmationUIManager.Instance.CancelButtonPressed();
                Need(Raw("attention") == Corrupt && Raw("attention.recovery") == null, "Cancellation changed the unreadable record");
                Open(); Ask();
                var harmony = new Harmony(PatchId); failWrite = true;
                try
                {
                    harmony.Patch(AccessTools.Method(typeof(ModHelper), nameof(ModHelper.SetModData), new[] { typeof(string), typeof(string), typeof(string) }),
                        postfix: new HarmonyMethod(typeof(AttentionRecoveryScenario).GetMethod(nameof(AfterWrite), F)));
                    GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
                }
                finally { harmony.UnpatchAll(PatchId); failWrite = false; }
                Marker pending = JsonUtility.FromJson<Marker>(Raw("attention.recovery"));
                Need(pending != null && !pending.completed && !string.IsNullOrEmpty(pending.backupKey) && Raw(pending.backupKey) == Corrupt &&
                    Raw("attention") != Corrupt && Blocked(999), "Unverified write was not backed up and blocked");
                backupKey = pending.backupKey;
                PlayerStore.Instance.SaveGame(); StoreUIManager.Instance.escapeUIManager.OnMainMenu();
                reload = new QaLoadSlot(slot, Guid.Parse(run)); step = 1; return false;
            case 1:
                if (!reload.Tick()) return false;
                if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                    StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                Need(Blocked(999) && Raw(backupKey) == Corrupt, "Reload lifted an unverified recovery or lost its backup");
                Open(); Ask(); GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
                Marker complete = JsonUtility.FromJson<Marker>(Raw("attention.recovery"));
                Need(complete != null && complete.completed && Raw(backupKey) == Corrupt && Blocked(101) && !Blocked(999) &&
                    Raw("attention").Contains("QA pending issue"), "Confirmed recovery lost an issue or failed to unblock unrelated items");
                result = "PASS attention recovery confirmation, original backup, failed-write blocking and reload retry";
                return true;
            default: throw new InvalidOperationException("Unknown attention recovery step");
        }
    }
}
