using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

// Runs only inside the isolated player-test profile.
public sealed class ReceiptDestinationsScenario
{
    private const string ModId = "starswhere.shopassist";
    private QaLoadSlot load;
    private int step;
    private float started = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - started > 60f) throw new InvalidOperationException("Receipt destination QA timed out");
        switch (step)
        {
            case 0:
                if (MainMenuUIController.Instance == null) return false;
                string[] context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-latest-run.txt")).Trim().Split('|');
                Require(context.Length == 4 && context[0] == "1" && Guid.TryParseExact(context[1], "N", out _) &&
                    int.TryParse(context[2], out _), "Invalid isolated QA context");
                load = new QaLoadSlot(int.Parse(context[2]), Guid.ParseExact(context[1], "N"));
                step = 1; return false;
            case 1:
                if (!load.Tick() || StoreUIManager.Instance?.gameOptionUIManager == null || EmporiumEntry.Instance?.invWindow == null) return false;
                if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
                Require(PlayerStore.Instance.currentClientInstance == null, "Unexpected customer in isolated QA save");
                Configure(2);
                CheckDelivery(EmporiumEntry.Instance.showcaseElement, 241);
                Configure(3);
                CheckDelivery(EmporiumEntry.Instance.invElement, 242);
                Configure(0);
                CheckDelivery(EmporiumEntry.Instance.frontInvinvElement, 243);
                step = 2; return false;
            case 2:
                Configure(2);
                var harmony = new Harmony("qa.receipt-destination");
                var method = AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.AddedItemToDisplayCase));
                harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(ReceiptDestinationsScenario), nameof(Noop))));
                try { CheckDelivery(EmporiumEntry.Instance.frontInvinvElement, 244); }
                finally { harmony.UnpatchAll(harmony.Id); }
                result = "PASS native receipt delivery to showcase, stock and front; unknown target patch falls back to front";
                return true;
            default: throw new InvalidOperationException("Unknown receipt destination QA step");
        }
    }

    private static void Configure(int mode)
    {
        StoreUIManager.Instance.gameOptionUIManager.OpenUI();
        CustomUIWindow options = CustomUIManager.Instance.GetWindow("game_options");
        Require(options != null && options.IsVisible, "Native options unavailable");
        options.Get(ModId + ":f24.enabled").SetBool(true, true);
        options.Get(ModId + ":f24.destination_v2").SetIndex(mode, true);
        options.Get("apply").button.onClick.Invoke();
        StoreUIManager.Instance.gameOptionUIManager.CloseUI();
    }

    private static void CheckDelivery(GameGridInventory target, int value)
    {
        Require(target != null, "Receipt target unavailable");
        var before = new HashSet<GameItem>(target.childItems);
        int cash = PlayerStore.Instance.playerCash;
        AppraisalServiceHelper.GiveAppraisalFee(value, 1, value);
        int fee = RNG.CalculatePercentage(value, AppraisalRankHelper.GetFeePercent(1)) + AppraisalRankHelper.GetBaseFee();
        var slips = target.childItems.Where(item => !before.Contains(item) && item.identifier == "slip").ToArray();
        Require(PlayerStore.Instance.playerCash - cash == fee && slips.Length == 1 && GeneralHelper.IsItemOwned(slips[0]),
            "Receipt target or appraisal payment did not match the selected native destination");
    }

    private static void Noop() { }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
