using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Uses the native day cycle and raycasts to check the two ledger entry points.
public sealed class LedgerSurfaceScenario
{
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 190f;
    private QaSyntheticInput input;
    private int step, frame, day, firstSale, secondSale;
    private float after;
    private Button target;
    private string targetStatus;

    public bool Tick(out string result)
    {
        result = null;
        LedgerProbe.Need(Time.realtimeSinceStartup < deadline, "Ledger surface timed out at " + step + ": " + targetStatus);
        if (step == 0) { if (start.Tick()) step++; return false; }
        var player = PlayerStore.Instance;
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                StoreUIManager.Instance?.gameOptionUIManager == null || EventSystem.current == null || StoreShutterButton.Instance == null) return false;
            LedgerProbe.CloseCalendarAndHints();
            TradeScenario.SetOptions(false, "f28.enabled", "f16.enabled", "f15.enabled", "f17.enabled", "f21.enabled");
            player.playerCash = 200000;
            TradeScenario.SetOptions(true, "f28.enabled");
            day = StoreStation.GetDayCounter();
            LedgerProbe.Need(day == 1, "Surface fixture did not start on day one");
            firstSale = LedgerProbe.Sell("wine_yeast");
            ClearCustomer();
            EmporiumOption.Instance.autoCallNext = false; player.callNextWhenPossible = false;
            input = new QaSyntheticInput { Mouse = new Vector3(-100, -100, 0) };
            step++; return false;
        }
        if (step == 2)
        {
            LedgerProbe.CloseCalendarAndHints();
            if (!player.CanStartDay() || player.lockShutterButtonFlag > 0) return false;
            StoreShutterButton.Instance.OnClick();
            LedgerProbe.Need(player.storeState == PlayerStore.StoreState.OPEN, "Native shutter did not start opening");
            step++; return false;
        }
        if (step == 3)
        {
            if (!SettledOpen()) return false;
            if (!PrepareTarget("ShopAssist_WallLedger", day, false)) return false;
            step++; return false;
        }
        if (step == 4)
        {
            if (Time.frameCount < frame) return false;
            ClickTarget();
            CheckOpenedDay(day, firstSale);
            LedgerProbe.Window().Close();
            input.Mouse = new Vector3(-100, -100, 0);
            step++; return false;
        }
        if (step == 5)
        {
            if (!player.CanEndDay()) ClearCustomer();
            if (!player.CanEndDay() || player.lockShutterButtonFlag > 0) return false;
            StoreShutterButton.Instance.OnClick(); step++; return false;
        }
        if (step == 6)
        {
            if (player.storeState == PlayerStore.StoreState.OPEN)
            {
                if (StoreUIManager.Instance.closeShutterConfirmationPanel?.activeInHierarchy == true) StoreShutterButton.Instance.EndAndClose();
                return false;
            }
            LedgerProbe.Need(player.storeState == PlayerStore.StoreState.CLOSED, "Native shutter failed to close");
            after = Time.realtimeSinceStartup + 3f; step++; return false;
        }
        if (step == 7)
        {
            if (Time.realtimeSinceStartup < after) return false;
            var bed = UnityEngine.Object.FindObjectOfType<StoreBedroll>();
            if (bed == null || EndOfDayUIManager.Instance == null) return false;
            bed.OnClick();
            LedgerProbe.Need(EndOfDayUIManager.Instance.IsOpen, "Native sleep screen did not open");
            EndOfDayUIManager.Instance.OnEndDayButtonClick(); step++; return false;
        }
        if (step == 8)
        {
            if (StoreStation.GetDayCounter() != day + 1 || player.storeState != PlayerStore.StoreState.MORNING) return false;
            LedgerProbe.CloseCalendarAndHints();
            if (!PrepareTarget("ShopAssist_DoorBoard", day, true)) return false;
            step++; return false;
        }
        if (step == 9)
        {
            if (Time.frameCount < frame) return false;
            ClickTarget();
            CheckOpenedDay(day, firstSale);
            LedgerProbe.Window().Close();
            TradeScenario.SetOptions(false, "f28.enabled");
            input.Mouse = new Vector3(-100, -100, 0); frame = Time.frameCount + 5; step = 90; return false;
        }
        if (step == 90)
        {
            if (Time.frameCount < frame) return false;
            LedgerProbe.Need(Find("ShopAssist_LedgerSummary") == null && Find("ShopAssist_DoorBoard") == null,
                "Disabling F28 left its shutter summary visible");
            TradeScenario.SetOptions(true, "f28.enabled");
            after = Time.realtimeSinceStartup + 5f; frame = Time.frameCount + 5; step = 91; return false;
        }
        if (step == 91)
        {
            if (Time.frameCount < frame) return false;
            if (!PrepareTarget("ShopAssist_DoorBoard", day, true))
            {
                if (Time.realtimeSinceStartup >= after)
                {
                    var inactive = Resources.FindObjectsOfTypeAll<GameObject>()
                        .FirstOrDefault(value => value.name == "ShopAssist_DoorBoard" && value.scene.IsValid());
                    LedgerProbe.Need(false, "Re-enabled F28 did not restore its shutter summary: " + targetStatus +
                        "; existing=" + (inactive != null) + " activeSelf=" + (inactive != null && inactive.activeSelf) +
                        " parentActive=" + (inactive != null && inactive.transform.parent != null && inactive.transform.parent.gameObject.activeInHierarchy));
                }
                return false;
            }
            step = 92; return false;
        }
        if (step == 92)
        {
            if (Time.frameCount < frame) return false;
            ClickTarget();
            CheckOpenedDay(day, firstSale);
            LedgerProbe.Window().Close();
            input.Mouse = new Vector3(-100, -100, 0); step = 10; return false;
        }
        if (step == 10)
        {
            LedgerProbe.CloseCalendarAndHints();
            if (!player.CanStartDay() || player.lockShutterButtonFlag > 0) return false;
            StoreShutterButton.Instance.OnClick();
            LedgerProbe.Need(player.storeState == PlayerStore.StoreState.OPEN, "Second shutter did not start opening");
            step++; return false;
        }
        if (step == 11)
        {
            if (!SettledOpen()) return false;
            secondSale = LedgerProbe.Sell("wine_superyeast");
            LedgerProbe.Need(firstSale != secondSale, "Surface fixture requires different sale amounts");
            ClearCustomer(); frame = Time.frameCount + 5; step++; return false;
        }
        if (step == 12)
        {
            if (Time.frameCount < frame || !PrepareTarget("ShopAssist_WallLedger", day + 1, false)) return false;
            step++; return false;
        }
        if (step == 13)
        {
            if (Time.frameCount < frame) return false;
            ClickTarget();
            CheckOpenedDay(day + 1, secondSale);
            LedgerProbe.Need(LedgerProbe.RowCount(LedgerProbe.Window(), ModHelper.GetLocalized(LedgerProbe.Mod, "f28.sale"), firstSale,
                ModHelper.GetLocalized(LedgerProbe.Mod, "f28.unknown")) == 0, "Wall click opened the previous day's transactions");
            TradeScenario.SetOptions(false, "f28.enabled");
            input.Mouse = new Vector3(-100, -100, 0); frame = Time.frameCount + 5; step++; return false;
        }
        if (Time.frameCount < frame) return false;
        LedgerProbe.Need(LedgerProbe.Window()?.IsAlive != true && Find("ShopAssist_LedgerSummary") == null &&
            Find("ShopAssist_WallLedger") == null, "Disabling F28 left its ledger or surface entry visible");
        input.Dispose(); input = null;
        result = "PASS F28 previous-day shutter and current-day wall summaries, reachable clicks and disable cleanup";
        return true;
    }

    private static bool SettledOpen() => StoreShutterButton.Instance != null &&
        Vector3.Distance(StoreShutterButton.Instance.shutterGameObject.transform.position, StoreShutterButton.Instance.endPosition) < 0.001f;

    private static void ClearCustomer()
    {
        var player = PlayerStore.Instance;
        var client = player.currentClientInstance?.GetClientBlueprint();
        if (client != null && !client.dismissable) player.DismissCurrentClient(callNextWhenPossible: false);
        player.currentClientInstance = null; player.isClientArrived = false;
        player.storeClientManager?.clientStack?.RemoveAll(value => value != null && !value.dismissable);
    }

    private bool PrepareTarget(string rootName, int expectedDay, bool yesterday)
    {
        var root = Find(rootName);
        if (root == null)
        {
            targetStatus = rootName + " unavailable";
            return false;
        }
        var button = root.GetComponentsInChildren<Button>(true).FirstOrDefault(value => value.name == "ShopAssist_LedgerSummary" && value.gameObject.activeInHierarchy);
        var cash = root.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(value => value.name == "ledger-cash" && value.gameObject.activeInHierarchy);
        var profit = root.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(value => value.name == "ledger-profit" && value.gameObject.activeInHierarchy);
        if (button == null || cash == null || profit == null)
        {
            targetStatus = rootName + " children: button=" + (button != null) + " cash=" + (cash != null) + " profit=" + (profit != null);
            return false;
        }
        var entries = LedgerProbe.History().entries.Where(row => row.day == expectedDay).ToArray();
        long net = entries.Sum(row => row.income - row.expense), margin = entries.Sum(row => row.knownMargin);
        int unknown = entries.Sum(row => row.unknownCost);
        string title = ModHelper.GetLocalized(LedgerProbe.Mod, yesterday ? "f28.yesterday" : "f28.today");
        string expectedCash = ModHelper.GetLocalized(LedgerProbe.Mod, "f28.door_cash", title, net);
        string expectedProfit = ModHelper.GetLocalized(LedgerProbe.Mod, "f28.door_profit", margin, unknown);
        if (cash.text != expectedCash || profit.text != expectedProfit)
        {
            targetStatus = rootName + " cash=" + cash.text + " expected=" + expectedCash +
                " profit=" + profit.text + " expected=" + expectedProfit;
            return false;
        }
        targetStatus = null;
        target = button;
        var rect = (RectTransform)button.transform;
        var canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        input.Mouse = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        LedgerProbe.Need(input.Mouse.x >= 0 && input.Mouse.x < Screen.width && input.Mouse.y >= 0 && input.Mouse.y < Screen.height,
            "Ledger summary is outside the screen");
        frame = Time.frameCount + 3;
        return true;
    }

    private void ClickTarget()
    {
        LedgerProbe.Need(target != null && target.gameObject.activeInHierarchy && target.IsInteractable(), "Ledger summary is not interactable");
        var pointer = new PointerEventData(EventSystem.current) { position = input.Mouse, button = PointerEventData.InputButton.Left, pointerId = -1 };
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointer, hits);
        LedgerProbe.Need(hits.Count > 0 && (hits[0].gameObject == target.gameObject || hits[0].gameObject.transform.IsChildOf(target.transform)),
            "Ledger summary is blocked from the native raycast");
        ExecuteEvents.Execute(target.gameObject, pointer, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.Execute(target.gameObject, pointer, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.Execute(target.gameObject, pointer, ExecuteEvents.pointerClickHandler);
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-ledger-surface-" + step + ".png"));
    }

    private static void CheckOpenedDay(int expectedDay, int sale)
    {
        var window = LedgerProbe.Window();
        LedgerProbe.Need(LedgerProbe.Day(window) == expectedDay &&
            LedgerProbe.RowCount(window, ModHelper.GetLocalized(LedgerProbe.Mod, "f28.sale"), sale,
                ModHelper.GetLocalized(LedgerProbe.Mod, "f28.unknown")) == 1,
            "Ledger surface did not open its corresponding day's transaction");
    }

    private static GameObject Find(string name) => Resources.FindObjectsOfTypeAll<GameObject>()
        .FirstOrDefault(value => value.name == name && value.scene.IsValid() && value.activeInHierarchy);
}
