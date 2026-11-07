using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Verifies the real worksheet controls with two isolated native appraisal items.
public sealed class AppraisalWorksheetScenario
{
    private const string ModId = "starswhere.shopassist";
    private const string WindowId = "shopassist.f23.worksheet";
    private QaLoadSlot load;
    private GameItem first, second;
    private StoreClient client;
    private long cash;
    private int impatience;
    private int step, waitFrame;
    private readonly ArrangeCapture capture = new ArrangeCapture();
    private float started = Time.realtimeSinceStartup;

    public bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - started > 45f) throw new InvalidOperationException("F23 QA timed out at step " + step);
        switch (step)
        {
            case 0:
            {
                if (MainMenuUIController.Instance == null) return false;
                string[] context = File.ReadAllText(Path.Combine(Application.persistentDataPath, "qa-latest-run.txt")).Trim().Split('|');
                Require(context.Length == 4 && context[0] == "1" && Guid.TryParseExact(context[1], "N", out _) && int.TryParse(context[2], out _), "Invalid isolated QA context");
                load = new QaLoadSlot(int.Parse(context[2]), Guid.ParseExact(context[1], "N"));
                Next(1, "Loading isolated F23 appraisal save"); return false;
            }
            case 1:
            {
                if (!load.Tick() || StoreUIManager.Instance?.gameOptionUIManager == null || InspectionUIManager.Instance == null) return false;
                var calendar = AdvCalendarUIManager.Instance;
                if (calendar?.UIPanel != null && calendar.UIPanel.activeSelf) StoreUIManager.Instance.storeCalendar.OnClick();
                Require(PlayerStore.Instance.currentClientInstance == null, "QA save unexpectedly has a customer");
                client = StoreClientListAppraisal.CreateAppraisal1();
                PlayerStore.Instance.storeClientManager.AddNextClient(client);
                PlayerStore.Instance.GetNextClient();
                Require(ReferenceEquals(PlayerStore.Instance.currentClientInstance?.GetClientBlueprint(), client), "Native appraisal customer did not arrive");
                first = PreBuiltItemHelper.CreateHandRandom(); second = PreBuiltItemHelper.CreateHandRandom();
                AppHandHelper.InitDefault(first); AppHandHelper.InitPremium(second);
                Require(first.IsTag("APPRAISAL_ITEM") && second.IsTag("APPRAISAL_ITEM") &&
                    AppraisalHelper.GetCorrectBaseValue(first) != AppraisalHelper.GetCorrectBaseValue(second), "QA appraisal items do not differ");
                cash = PlayerStore.Instance.playerCash; impatience = client.clientImpatience;
                var options = Options();
                options.Get(ModId + ":f23.enabled").SetBool(true, true);
                options.Get(ModId + ":f23.calculate").SetBool(true, true);
                options.Get("apply").button.onClick.Invoke();
                StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                InspectionUIManager.Instance.OnInspectItem(first);
                InspectionUIManager.Instance.UpdateAppraisalPanel();
                Require(InspectionUIManager.Instance.appraisalPanel.activeInHierarchy, "Native appraisal panel did not open");
                waitFrame = Time.frameCount + 3;
                Next(2, "Prepared a native appraisal customer and two different items"); return false;
            }
            case 2:
            {
                if (Time.frameCount < waitFrame) return false;
                OpenWorksheet();
                var sheet = Worksheet();
                Require(sheet.Get("base").GetString() == "" && !sheet.Get("apply").button.interactable, "New F23 sheet leaked data or allowed an incomplete estimate");
                Fill(sheet, "100", "鉴定特征", "1234");
                sheet = Worksheet();
                Require(Total(sheet, "1334") && sheet.Get("apply").button.interactable, "F23 did not add the player's feature amount");
                sheet.Get("row_0").SetBool(false, true);
                Require(Total(sheet, "100"), "F23 did not remove an unchecked adjustment");
                sheet.Get("undo").button.onClick.Invoke();
                sheet = Worksheet();
                Require(sheet.Get("row_0").GetBool() && Total(sheet, "1334"), "F23 undo did not restore the player's choice");
                sheet.Get("feature").SetString("Unknown amount", true);
                sheet.Get("amount").SetString("", true);
                sheet.Get("add").button.onClick.Invoke();
                sheet = Worksheet();
                Require(!sheet.Get("apply").button.interactable && Total(sheet, "—"), "F23 silently estimated an unknown amount");
                sheet.Get("remove_1").button.onClick.Invoke();
                sheet = Worksheet();
                Require(sheet.Get("apply").button.interactable && Total(sheet, "1334"), "F23 did not recover after removing the unknown amount");
                Require(1334 != AppraisalHelper.GetCorrectBaseValue(first), "QA estimate was not intentionally wrong");
                waitFrame = Time.frameCount + 3;
                Next(20, "F23 selection, undo and unknown-amount controls passed; waiting for layout"); return false;
            }
            case 20:
            {
                if (Time.frameCount < waitFrame) return false;
                var sheet = Worksheet();
                Require(sheet.root.activeInHierarchy, "F23 worksheet is not rendered");
                Require(IsRaycastTarget(sheet.Get("apply").button, out string raycast), "F23 Apply is not reachable on the rendered UI: " + raycast);
                capture.Start("qa-appraisal-worksheet.png");
                Next(21, "Capturing the visible worksheet before applying its estimate"); return false;
            }
            case 21:
            {
                if (!capture.Ready()) return false;
                var sheet = Worksheet();
                sheet.Get("apply").button.onClick.Invoke();
                InspectionUIManager.Instance.UpdateAppraisalPanel();
                Require(first.GetTagReadonly("APPRAISAL_DEFAULT_VALUE").GetInt() == 1334 && InspectionUIManager.Instance.apprisalServiceInputField.text == "1334",
                    "F23 did not update the native input through its change action");
                Require(ReferenceEquals(InspectionUIManager.Instance.currentInspectingItem, first) &&
                    ReferenceEquals(PlayerStore.Instance.currentClientInstance.GetClientBlueprint(), client) &&
                    client.clientImpatience == impatience && PlayerStore.Instance.playerCash == cash,
                    "F23 applied an estimate by submitting or changing the transaction");
                InspectionUIManager.Instance.OnInspectItem(second);
                InspectionUIManager.Instance.UpdateAppraisalPanel();
                waitFrame = Time.frameCount + 3;
                Next(3, "F23 undo, unknown amount and explicitly applied incorrect estimate passed"); return false;
            }
            case 3:
            {
                if (Time.frameCount < waitFrame) return false;
                Require(CustomUIManager.Instance.GetWindow(WindowId) == null, "F23 did not close the prior item's sheet");
                OpenWorksheet();
                var sheet = Worksheet();
                Require(sheet.Get("base").GetString() == "" && Total(sheet, "—"), "F23 copied the first item's notes to another item");
                Fill(sheet, "100", "鉴定特征", "1234");
                sheet = Worksheet();
                Require(Total(sheet, "1334"), "Same player inputs produced different output for a different hidden item");
                sheet.Get("apply").button.onClick.Invoke();
                InspectionUIManager.Instance.UpdateAppraisalPanel();
                Require(second.GetTagReadonly("APPRAISAL_DEFAULT_VALUE").GetInt() == 1334 &&
                    first.GetTagReadonly("APPRAISAL_DEFAULT_VALUE").GetInt() == 1334 && PlayerStore.Instance.playerCash == cash && client.clientImpatience == impatience,
                    "F23 mixed items or submitted an appraisal");
                InspectionUIManager.Instance.OnInspectItem(first);
                InspectionUIManager.Instance.UpdateAppraisalPanel();
                waitFrame = Time.frameCount + 3;
                Next(4, "F23 kept sheets isolated across different hidden item states"); return false;
            }
            case 4:
            {
                if (Time.frameCount < waitFrame) return false;
                OpenWorksheet();
                var sheet = Worksheet();
                Require(sheet.Get("base").GetString() == "100" && sheet.Get("row_0").GetBool() && Total(sheet, "1334"),
                    "F23 lost the first item's notes on retry");
                var options = Options();
                options.Get(ModId + ":f23.calculate").SetBool(false, true);
                options.Get("apply").button.onClick.Invoke();
                StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                waitFrame = Time.frameCount + 3;
                Next(5, "Returned to the first item's worksheet and disabled calculation"); return false;
            }
            case 5:
            {
                if (Time.frameCount < waitFrame) return false;
                var sheet = Worksheet();
                Require(Total(sheet, "—") && !sheet.Get("apply").button.interactable && sheet.Get("row_0").GetBool(),
                    "F23 recording-only mode did not retain notes and disable calculation");
                InspectionUIManager.Instance.CloseUI();
                waitFrame = Time.frameCount + 3;
                Next(6, "F23 recording-only mode passed"); return false;
            }
            case 6:
                if (Time.frameCount < waitFrame) return false;
                Require(CustomUIManager.Instance.GetWindow(WindowId) == null, "F23 worksheet remained open after native inspection closed");
                result = "PASS F23 explicit notes, unknown amounts, selection/undo, player-only estimate, native apply without submission, hidden-state independence, item/retry isolation and record-only mode";
                return true;
            default: throw new InvalidOperationException("Unknown F23 QA step " + step);
        }
    }

    private static void Fill(CustomUIWindow sheet, string basis, string name, string amount)
    {
        sheet.Get("base").SetString(basis, true);
        sheet.Get("base").input.onEndEdit.Invoke(basis);
        sheet.Get("feature").SetString(name, true);
        sheet.Get("amount").SetString(amount, true);
        sheet.Get("add").button.onClick.Invoke();
    }
    private static bool Total(CustomUIWindow sheet, string value) => sheet.Get("total").GetText().Contains(value);
    private static CustomUIWindow Worksheet()
    {
        var sheet = CustomUIManager.Instance.GetWindow(WindowId);
        Require(sheet != null && sheet.IsVisible, "F23 worksheet unavailable"); return sheet;
    }
    private static CustomUIWindow Options()
    {
        StoreUIManager.Instance.gameOptionUIManager.OpenUI();
        var options = CustomUIManager.Instance.GetWindow("game_options");
        Require(options != null && options.IsVisible, "Native options unavailable"); return options;
    }
    private static void OpenWorksheet()
    {
        RoutingScenario.RunFeatureAction("f23.open");
        Worksheet();
    }
    private static bool IsRaycastTarget(Button button, out string details)
    {
        var rect = button.transform as RectTransform;
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        details = "point=" + point + " screen=" + Screen.width + "x" + Screen.height + " canvas=" + (canvas != null ? canvas.name : "<none>") +
            " hits=" + string.Join("; ", hits.Take(8).Select(hit => hit.gameObject == null ? "<null>" : PathOf(hit.gameObject.transform)));
        return hits.Count != 0 && hits[0].gameObject != null &&
            (hits[0].gameObject == button.gameObject || hits[0].gameObject.transform.IsChildOf(button.transform));
    }
    private static string PathOf(Transform node)
    {
        var names = new List<string>();
        for (; node != null; node = node.parent) names.Add(node.name);
        names.Reverse(); return string.Join("/", names);
    }
    private void Next(int value, string message) { Debug.Log("[PLAYER-QA] " + message); step = value; started = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
