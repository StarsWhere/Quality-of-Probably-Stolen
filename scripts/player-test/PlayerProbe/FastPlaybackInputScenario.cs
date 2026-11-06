using System;
using System.IO;
using System.Linq;
using UnityEngine;

public sealed class FastPlaybackInputScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 150f;
    private QaFastPlayback playback;
    private QaLoadSlot reload;
    private int step, speed, day;
    private float scale;
    private ConfigEntry<bool> enabled;
    private GameOptionRegistry.Entry binding;
    private KeyCode testKey;
    private bool finished;

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch { Cleanup(); throw; }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Playback input timed out at " + step);
        if (step == 0)
        {
            if (!start.Tick()) return false;
            StoreUIManager.Instance.gameOptionUIManager.OpenUI();
            StoreUIManager.Instance.gameOptionUIManager.CloseUI();
            playback = new QaFastPlayback(0, 2);
            enabled = (ConfigEntry<bool>)ModConfig.Find("starswhere.shopassist").Get("f42.enabled");
            binding = GameOptionRegistry.Sections.SelectMany(s => s.entries).Single(e => e.id == "starswhere.shopassist:f42.key");
            testKey = playback.Key; scale = Time.timeScale; day = StoreStation.GetDayCounter();
            step++; return false;
        }
        switch (step)
        {
            case 1: Need(playback.Rate == 1, "Playback started without a key"); playback.Press(); break;
            case 2: Need(playback.Rate == 8, "Hold did not activate 8x"); playback.Hold(); break;
            case 3:
                Need(playback.Rate == 8, "Hold did not persist");
                var status = CustomUIManager.Instance.GetLayerRoot(CustomUIManager.LAYER_META).Find("starswhere.shopassist.fastplay.status");
                Need(status != null && status.gameObject.activeInHierarchy && !status.GetComponent<CanvasGroup>().blocksRaycasts,
                    "Playback status is absent or blocks input");
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-fastplay-status.png"));
                playback.Release(); break;
            case 4: Need(playback.Rate == 1, "Release did not restore native speed"); playback.Configure(0, 0); playback.Press(); break;
            case 5:
                Need(playback.Rate == new[] { 2, 4, 8, 16 }[speed], "Speed selection did not update the active playback");
                playback.Hold();
                if (++speed < 4) { playback.Configure(0, speed); return false; }
                playback.Release(); playback.Configure(1, 2); break;
            case 6: Need(playback.Rate == 1, "Changing mode kept playback active"); playback.Press(); break;
            case 7: Need(playback.Rate == 8, "Toggle did not start"); playback.Release(); break;
            case 8:
                Need(playback.Rate == 8, "Toggle stopped on release");
                GenericConfirmationUIManager.Instance.panel.SetActive(true); break;
            case 9:
                Need(playback.Rate == 1, "Confirmation did not suspend playback");
                GenericConfirmationUIManager.Instance.panel.SetActive(false); break;
            case 10:
                Need(playback.Rate == 8, "Confirmation completion did not resume toggle playback");
                StoreUIManager.Instance.StartTyping(); break;
            case 11:
                Need(playback.Rate == 1, "Typing did not suspend playback");
                StoreUIManager.Instance.StopTyping(); break;
            case 12: Need(playback.Rate == 8, "Typing completion did not resume"); playback.Press(); break;
            case 13: Need(playback.Rate == 1, "Second toggle did not stop"); playback.Release(); break;
            case 14: playback.Press(); break;
            case 15: Need(playback.Rate == 8, "Toggle failed to restart"); playback.Hold(); playback.SetFocus(false); break;
            case 16: Need(playback.Rate == 1, "Focus loss did not stop playback"); playback.SetFocus(true); break;
            case 17: Need(playback.Rate == 1, "Focus return reactivated a held key"); playback.Release(); break;
            case 18: playback.Press(); break;
            case 19: Need(playback.Rate == 8, "Playback failed after focus recovery"); enabled.Set(false); playback.Hold(); break;
            case 20: Need(playback.Rate == 1, "Disabling left playback active"); enabled.Set(true); break;
            case 21: Need(playback.Rate == 1, "Enabling reused an old held key"); playback.Release(); break;
            case 22:
                binding.setKey(KeyBindHelper.Get("advance_dialogue"));
                Need(binding.getKey() != KeyCode.None, "Native dialogue key unavailable for conflict test");
                break;
            case 23: playback.Input.Down = playback.Input.Held = binding.getKey(); break;
            case 24:
                Need(playback.Rate == 1, "Conflicting native shortcut activated playback");
                playback.Release(); binding.setKey(testKey); break;
            case 25: playback.Release(); break;
            case 26: playback.Press(); break;
            case 27:
                Need(playback.Rate == 8, "Playback did not recover after rebinding");
                playback.Release();
                PlayerStore.Instance.SaveGame();
                reload = new QaLoadSlot(PlayerStore.Instance.saveSlotId, Guid.Parse(PlayerStore.Instance.runID));
                StoreUIManager.Instance.escapeUIManager.OnMainMenu(); break;
            case 28:
                Need(playback.Rate == 1, "Returning to the menu retained toggle playback");
                if (!reload.Tick()) return false;
                break;
            case 29:
                Need(playback.Rate == 1 && StoreStation.GetDayCounter() == day && Time.timeScale == scale,
                    "Load retained playback or changed game time");
                Cleanup(); finished = true;
                result = "PASS F42 hold/toggle, all speeds, modal and typing resume, focus reset, conflict rejection, disable and real save reload";
                return true;
            default: throw new InvalidOperationException("Unknown playback input stage");
        }
        step++; return false;
    }

    private void Cleanup()
    {
        if (finished) return;
        if (StoreUIManager.Instance != null) StoreUIManager.Instance.StopTyping();
        if (GenericConfirmationUIManager.Instance?.panel != null) GenericConfirmationUIManager.Instance.panel.SetActive(false);
        playback?.Dispose(); playback = null;
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
