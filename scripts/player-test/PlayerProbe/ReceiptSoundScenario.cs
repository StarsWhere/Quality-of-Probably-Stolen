using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// Observes actual AudioSource playback requests without replacing the audio behavior.
public sealed class ReceiptSoundScenario : EquipmentFixture
{
    private static AudioSource register;
    private static readonly List<float> played = new List<float>();

    public bool Tick(out string result)
    {
        result = null; if (!Ready()) return false;
        register = EmporiumAudioPlayer.Instance?.audioRegister;
        Check(register != null && register.clip != null, "Native register clip is unavailable");
        var player = PlayerStore.Instance;
        player.playerCash = 200000;
        player.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateJunkerSellOnly());
        player.isClientArrived = true;
        string owner = "qa.receipt-audio." + Guid.NewGuid().ToString("N");
        var harmony = new Harmony(owner);
        try
        {
            harmony.Patch(AccessTools.Method(typeof(AudioSource), nameof(AudioSource.PlayOneShot), new[] { typeof(AudioClip), typeof(float) }),
                prefix: new HarmonyMethod(typeof(ReceiptSoundScenario), nameof(Observe)));
            Exercise(true, true, 37, .37f);
            Exercise(true, true, 0, null);
            Exercise(true, false, 80, null);
            Exercise(false, true, 80, null);
            Exercise(true, true, 100, 1f);
            result = "PASS F16 native register clip at 37% and 100%, zero volume, sound off and receipt off";
            return true;
        }
        finally { harmony.UnpatchAll(owner); register = null; played.Clear(); }
    }

    private static void Observe(AudioSource __instance, AudioClip clip, float volumeScale)
    {
        if (__instance == register && clip == register?.clip) played.Add(volumeScale);
    }

    private static void Exercise(bool receipt, bool sound, int volume, float? expected)
    {
        var options = StoreUIManager.Instance.gameOptionUIManager; options.OpenUI();
        var window = CustomUIManager.Instance.GetWindow("game_options");
        window.Get(Mod + ":f16.enabled").SetBool(receipt, true);
        window.Get(Mod + ":f16.money").SetBool(true, true);
        window.Get(Mod + ":f16.rep").SetBool(false, true);
        window.Get(Mod + ":f16.sound").SetBool(sound, true);
        window.Get(Mod + ":f16.volume").SetFloat(volume, true);
        window.Get("apply").button.onClick.Invoke(); options.CloseUI();
        var item = DirectoryMaster.Item("wine_yeast"); GeneralHelper.SetItemOwned(item, false);
        var counter = EmporiumEntry.Instance.backInvinvElement;
        var slot = counter.TryFindOneValidInventorySlot(item);
        Check(slot != null && slot.targetItem == null && slot.TryAcceptOnce(item.unitCount) == item.unitCount,
            "Cannot prepare native receipt audio trade");
        int before = PlayerStore.Instance.playerCash;
        played.Clear(); PlayerStore.Instance.BuyItem(item);
        Check(PlayerStore.Instance.playerCash < before && GeneralHelper.IsItemOwned(item), "Audio fixture did not complete a real purchase");
        Check(expected.HasValue ? played.Count == 1 && Mathf.Abs(played[0] - expected.Value) < .0001f : played.Count == 0,
            "Receipt sound did not match its enabled/volume setting: " + string.Join(",", played));
    }
}
