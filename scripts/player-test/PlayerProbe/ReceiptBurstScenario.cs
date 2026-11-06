using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

// Native purchases exercise equal-value events across the visible receipt limit.
public sealed class ReceiptBurstScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly HashSet<string> seen = new HashSet<string>();
    private readonly HashSet<string> amounts = new HashSet<string>();
    private readonly float deadline = Time.realtimeSinceStartup + 70;
    private ConfigEntry<int> duration;
    private int originalDuration, step;
    private float finish;
    private bool captured;

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch { Restore(); throw; }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Receipt burst timed out");
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true) return false;
            TradeScenario.SetOptions(true, "f16.enabled", "f16.money", "f16.rep");
            TradeScenario.SetOptions(false, "f16.sound");
            duration = (ConfigEntry<int>)ModConfig.Find(Mod).Get("f16.seconds");
            originalDuration = duration.Value; duration.Set(1);
            var player = PlayerStore.Instance;
            player.playerCash = 200000;
            var client = StoreClientList.CreateJunkerSellOnly(); client.clientFaction = "FACTION_UPPER_LEVEL";
            player.currentClientInstance = StoreClientInstance.CreateClientInstance(client); player.isClientArrived = true;
            for (int index = 0; index < 7; index++)
            {
                var item = DirectoryMaster.Item("wine_yeast"); GeneralHelper.SetItemOwned(item, false);
                var marker = EmporiumEntry.Instance.backInvinvElement.TryFindOneValidInventorySlot(item);
                Need(marker != null && marker.TryAcceptOnce() > 0, "Could not prepare native purchase " + index);
                player.BuyItem(item);
            }
            var notice = ModLoader.Mods.Single(mod => mod.Manifest?.ID == Mod).Instance.GetType().Assembly.GetType("ShopAssist.ShopNotice");
            var kind = notice.GetNestedType("NoticeKind", BindingFlags.Public | BindingFlags.NonPublic);
            notice.GetMethod("ShowEvent", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                new object[] { Guid.NewGuid(), Enum.Parse(kind, "Error"), "QA", "QA priority error", 1f });
            Need(GameObject.Find(Mod + ".notice")?.GetComponentInChildren<TMP_Text>()?.text == "QA priority error",
                "Error waited behind transaction receipts");
            finish = Time.unscaledTime + 3.5f; step++; return false;
        }
        var receipts = UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>()
            .Where(text => text.gameObject.activeInHierarchy && text.name.StartsWith(Mod + ".receipt.", StringComparison.Ordinal)).ToArray();
        Need(receipts.Length <= 4, "Receipt burst exceeded its visible limit");
        foreach (var text in receipts) { seen.Add(text.name); amounts.Add(text.text); Need(!text.raycastTarget, "Receipt intercepted input"); }
        if (!captured && receipts.Length == 4 && receipts.All(text => text.color.a >= .9f))
        {
            captured = true;
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-receipt-burst.png"));
        }
        if (Time.unscaledTime < finish) return false;
        Need(seen.Count == 7 && amounts.Count == 1 && receipts.Length == 0,
            "Receipt burst lost or duplicated equal-value events: seen=" + seen.Count + " amounts=" + amounts.Count + " visible=" + receipts.Length);
        Restore();
        result = "PASS seven native equal-value purchases display once each across the four-receipt limit; error displays independently and receipts expire";
        return true;
    }

    private void Restore() { if (duration != null) { duration.Set(originalDuration); duration = null; } }
    private static void Need(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
