using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class FeedbackScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private int step;
    private float until;
    private GameItem item;
    private Type notice, book;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string NoticeText() => GameObject.Find(Mod + ".notice")?.GetComponentInChildren<TMP_Text>()?.text ?? "";
    private static object Call(Type type, string name, params object[] args) => type.GetMethod(name, Static).Invoke(null, args);

    public bool Tick(out string result)
    {
        result = null;
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            var e = EmporiumEntry.Instance;
            if (e?.storeInteractables?.activeInHierarchy != true) return false;
            TradeScenario.SetOptions(false, "f30.enabled");
            Assembly assembly = ModLoader.Mods.Single(mod => mod.Manifest?.ID == Mod).Instance.GetType().Assembly;
            notice = assembly.GetType("ShopAssist.ShopNotice", true); book = assembly.GetType("ShopAssist.AttentionBook", true);
            Type operation = assembly.GetType("ShopAssist.OperationResult", true);
            item = DirectoryMaster.Item("wine_yeast"); GeneralHelper.SetItemOwned(item); GeneralHelper.SetCustomName(item, "QA feedback item");
            var marker = e.invElement.TryFindOneValidInventorySlot(item);
            Need(marker != null && marker.TryAcceptOnce() > 0, "Could not place feedback fixture");
            item.GetUniqueID();
            object batch = Activator.CreateInstance(operation, Instance, null, new object[] { "f25.open" }, null);
            operation.GetMethod("Skip", Instance).Invoke(batch, new object[] { item, 1, "inv.changed", "QA source → QA destination", false });
            Call(notice, "Report", batch);
            Need((int)book.GetProperty("Count", Static).GetValue(null) == 0, "Ordinary failure incorrectly created an inspection item");
            step = 10; return false;
        }
        if (step == 10)
        {
            var resultButton = QaItemUi.WindowAction(EmporiumEntry.Instance.invWindow, "result");
            if (resultButton == null || resultButton.isDisabled) return false;
            Need(QaItemUi.WindowAction(EmporiumEntry.Instance.invWindow, "attention") == null,
                "Ordinary failure exposed a spurious attention action");
            resultButton.Select();
            var details = CustomUIManager.Instance.GetWindow(Mod + ".operation");
            Need(details?.IsAlive == true && details.root.GetComponentsInChildren<TMP_Text>(true).Any(t => t.text.Contains("QA feedback item")),
                "The direct result action did not open an ordinary failure");
            details.Close();
            Call(notice, "ResetSession");
            Type operation = notice.Assembly.GetType("ShopAssist.OperationResult", true);
            object batch = Activator.CreateInstance(operation, Instance, null, new object[] { "f25.open" }, null);
            operation.GetMethod("Skip", Instance).Invoke(batch, new object[] { item, 1, "inv.inspect", "QA source → QA destination", true });
            Call(notice, "Report", batch); Call(notice, "Report", batch);
            Need((int)book.GetProperty("Count", Static).GetValue(null) == 1, "Repeated reports duplicated the unresolved item");
            string raw = ModHelper.GetModData(Mod, "attention", null);
            Need(raw != null && raw.Contains(item.uniqueId.ToString()) && raw.Contains(PlayerStore.Instance.runID), "Unresolved issue did not persist with its save identity");
            until = Time.realtimeSinceStartup + 7.3f; step = 11; return false;
        }
        if (step == 11)
        {
            var attentionButton = QaItemUi.WindowAction(EmporiumEntry.Instance.invWindow, "attention");
            if (attentionButton == null || attentionButton.isDisabled) return false;
            attentionButton.Select();
            var window = CustomUIManager.Instance.GetWindow(Mod + ".attention");
            Need(window?.IsAlive == true && window.root.GetComponentsInChildren<TMP_Text>(true).Any(t => t.text.Contains("QA feedback item")),
                "The direct attention action did not show the affected item with F30 disabled");
            step = 2; return false;
        }
        if (step == 2)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(NoticeText() == "", "A single failed operation produced an additional duplicate alert");
            Call(notice, "ResetSession");
            Call(notice, "Show", "QA first notice", 1f);
            Call(notice, "Show", "QA second notice", 1f);
            Call(notice, "Show", "QA second notice", 1f);
            Need(NoticeText() == "QA first notice", "A new notice replaced the currently visible notice");
            until = Time.realtimeSinceStartup + 1.15f; step++; return false;
        }
        if (step == 3)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(NoticeText() == "QA second notice", "Queued notice did not appear");
            until = Time.realtimeSinceStartup + 1.15f; step++; return false;
        }
        if (step == 4)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(NoticeText() == "QA second notice", "Independent events with equal text were incorrectly merged");
            until = Time.realtimeSinceStartup + 1.15f; step++; return false;
        }
        if (step == 5)
        {
            if (Time.realtimeSinceStartup < until) return false;
            Need(NoticeText() == "", "Completed notice did not expire");
            Call(book, "Open");
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-feedback.png"));
            var p = PlayerStore.Instance;
            File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-feedback-run.txt"), string.Join("|", p.runID, p.saveSlotId, item.uniqueId));
            p.SaveGame();
            result = "PASS event-identity deduplication, independent equal-text events, persistent unresolved operation and attention access";
            return true;
        }
        return false;
    }
}

public sealed class FeedbackReloadScenario
{
    private const string Mod = "starswhere.shopassist";
    private QaLoadSlot load;
    private string[] context;
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public bool Tick(out string result)
    {
        result = null;
        if (load == null)
        {
            context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-feedback-run.txt")).Split('|');
            Need(context.Length == 3 && Guid.TryParse(context[0], out _), "Invalid feedback reload context");
            load = new QaLoadSlot(int.Parse(context[1]), Guid.Parse(context[0])); return false;
        }
        if (!load.Tick()) return false;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var book = ModLoader.Mods.Single(mod => mod.Manifest?.ID == Mod).Instance.GetType().Assembly.GetType("ShopAssist.AttentionBook", true);
        Need((int)book.GetProperty("Count", flags).GetValue(null) == 1, "Unresolved item did not survive cold reload");
        StoreUIManager.Instance.gameOptionUIManager.OpenUI();
        book.GetMethod("Open", flags).Invoke(null, null);
        var window = CustomUIManager.Instance.GetWindow(Mod + ".attention");
        var resolve = window.root.GetComponentsInChildren<Button>(true).Single(button =>
            button.GetComponentInChildren<TMP_Text>()?.text == ModHelper.GetLocalized(Mod, "attention.resolve"));
        resolve.onClick.Invoke();
        Need((int)book.GetProperty("Count", flags).GetValue(null) == 0 && !ModHelper.GetModData(Mod, "attention", "").Contains("QA feedback item"),
            "Confirmed inspection did not clear the persistent issue");
        StoreUIManager.Instance.gameOptionUIManager.CloseUI();
        result = "PASS unresolved attention survives cold reload and explicit checked action clears the saved issue";
        return true;
    }
}
