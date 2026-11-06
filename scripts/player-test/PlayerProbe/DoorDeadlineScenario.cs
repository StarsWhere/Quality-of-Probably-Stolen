using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// A native tracked request must remain visible on F21 even with F27 later disabled.
public sealed class DoorDeadlineScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly string title = "QA deadline " + Guid.NewGuid().ToString("N").Substring(0, 6);
    private StoreRequest request;
    private int step, frame;
    private float since = Time.realtimeSinceStartup, after;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 75f) throw new InvalidOperationException("Door deadline QA timed out at step " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || PCUIManager.Instance == null ||
                StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            if (StoreUIManager.Instance.controlHintOverlay?.activeSelf == true) StoreUIManager.Instance.ToggleControlHintOverlay();
            TradeScenario.SetOptions(true, "f21.enabled", "f27.enabled");
            TradeScenario.SetOptions(false, "f15.enabled", "f17.enabled");
            request = new StoreRequest { title = title, id = title, description = title, requiredItem = "wine_yeast_red",
                requiredItemCount = 1, requesterName = "QA", sourceEventId = title, duration = 2, creditReward = 1 };
            PlayerStore.Instance.storeRequests.Add(request);
            PCUIManager.Instance.OpenUI(); frame = Time.frameCount + 2; step++; return false;
        }
        if (Time.frameCount < frame) return false;
        if (step == 2)
        {
            var row = UnityEngine.Object.FindObjectsOfType<StoreRequestElement>().FirstOrDefault(item => item.storeRequest == request);
            Need(row != null, "Native request row is missing");
            var action = row.acceptButton.transform.parent.Find(Mod + ".track")?.GetComponent<Button>();
            Need(action != null && action.interactable, "Native request tracking entry is missing");
            action.onClick.Invoke();
            var book = CustomUIManager.Instance.GetWindow(Mod + ".requests");
            Need(book?.Get("track")?.button != null, "Request tracking control is missing");
            book.Get("track").button.onClick.Invoke();
            Need(ModHelper.GetModData(Mod, "requests", "").Contains(title), "Request was not tracked");
            CustomUIManager.Instance.GetWindow(Mod + ".requests")?.Close(); PCUIManager.Instance.HideUI();
            frame = Time.frameCount + 4; step++; return false;
        }
        if (step == 3)
        {
            if (!DeadlinePresent()) return false;
            TradeScenario.SetOptions(false, "f27.enabled"); frame = Time.frameCount + 4; step++; return false;
        }
        if (step == 4)
        {
            Need(DeadlinePresent(), "Tracked deadline disappeared with only F27 disabled");
            request.duration = 1; after = Time.realtimeSinceStartup + 0.7f; step++; return false;
        }
        if (Time.realtimeSinceStartup < after) return false;
        Need(!DeadlinePresent(), "Changed/expired request retained a stale tracked deadline");
        result = "PASS F21 tracked request uses duration - 1, survives F27 disable and rejects stale request identity";
        return true;
    }

    private bool DeadlinePresent()
    {
        string expected = ModHelper.GetLocalized(Mod, "door.deadline", title, 1);
        return Resources.FindObjectsOfTypeAll<TMP_Text>().Any(text => text.gameObject.scene.IsValid() && text.gameObject.activeInHierarchy &&
            text.name == "deadline-row" && text.text == expected);
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
