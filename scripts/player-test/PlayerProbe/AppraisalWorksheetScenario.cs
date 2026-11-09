using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Runs only in the independently identified QA player and starts a new run.
public sealed class AppraisalWorksheetScenario
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly QaNewRun start = new QaNewRun();
    private readonly ArrangeCapture capture = new ArrangeCapture();
    private GameItem first, second, incompleteFuel;
    private StoreClient customer;
    private object feature;
    private long cash;
    private int impatience, step, ready;
    private float since = Time.realtimeSinceStartup;
    private static void Need(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static object Field(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    private CustomUIWindow Sheet() => Field(feature, "window") as CustomUIWindow;
    private void Next(int value) { step = value; ready = Time.frameCount + 5; since = Time.realtimeSinceStartup; }
    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup - since < 70, "Appraisal worksheet timed out at step " + step);
        if (step == 0) { if (start.Tick()) Next(1); return false; }
        if (Time.frameCount < ready) return false;
        var ui = InspectionUIManager.Instance;
        if (step == 1)
        {
            if (ui == null || StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            if (AdvCalendarUIManager.Instance?.UIPanel?.activeSelf == true) StoreUIManager.Instance.storeCalendar.OnClick();
            Need(PlayerStore.Instance.currentClientInstance == null, "A new run unexpectedly has a customer");
            customer = StoreClientListAppraisal.CreateAppraisal1();
            PlayerStore.Instance.storeClientManager.AddNextClient(customer); PlayerStore.Instance.GetNextClient();
            first = PreBuiltItemHelper.CreateHandRandom(); second = PreBuiltItemHelper.CreateHandRandom();
            AppHandHelper.InitDefault(first); AppHandHelper.InitPremium(second);
            cash = PlayerStore.Instance.playerCash; impatience = customer.clientImpatience;
            TradeScenario.SetOptions(true, "f23.enabled", "f23.calculate");
            var mod = ModLoader.Mods.Find(m => m.Manifest?.ID == "starswhere.shopassist")?.Instance;
            var modules = Field(mod, "modules") as IEnumerable;
            feature = modules.Cast<object>().Select(m => Field(m, "Tick") as Action).Select(t => t?.Target)
                .FirstOrDefault(target => target?.GetType().FullName == "ShopAssist.AppraisalWorksheet");
            Need(feature != null, "Worksheet module is not loaded");
            ui.OnInspectItem(first); ui.UpdateAppraisalPanel(); Next(2); return false;
        }
        if (step == 2)
        {
            var sheet = Sheet();
            Need(sheet?.root != null && sheet.root.activeInHierarchy, "Worksheet was not integrated into the native appraisal view");
            Need(sheet.root.transform.parent == ui.panel.transform && CustomUIManager.Instance.GetWindow("shopassist.f23.worksheet") == null,
                "Worksheet opened as a separate overlay window");
            for (int i = 0; i < 5; i++) Need(sheet.Get("choice_" + i).GetIndex() == 0, "New sheet guessed an observation");
            Need(!sheet.Get("apply").button.interactable && sheet.Get("total").GetText().Contains("—"), "Unknown observations allowed an estimate");
            Fill(sheet); Need(sheet.Get("total").GetText().Contains("100") && sheet.Get("apply").button.interactable,
                "Explicit standard observations did not calculate the handbook value");
            sheet.Get("choice_0").SetIndex(3, true); Need(sheet.Get("total").GetText().Contains("300"), "Material choice did not update the sheet");
            sheet.Get("undo").button.onClick.Invoke(); sheet = Sheet();
            Need(sheet.Get("choice_0").GetIndex() == 1 && sheet.Get("total").GetText().Contains("100"), "Undo did not restore the observation");
            Next(22); return false;
        }
        if (step == 22)
        {
            var sheet = Sheet();
            Need(Reachable(sheet.Get("apply").button), "Apply is not reachable in the rendered integrated panel");
            Need(sheet.Get("guide").button.interactable && HealthData.CanRead(), "Original reading conditions unexpectedly reject the guide");
            sheet.Get("guide").button.onClick.Invoke();
            Need(MultiPageUIManager.Instance.panel.activeInHierarchy &&
                (int)typeof(MultiPageUIManager).GetField("currentBookIndex", Flags).GetValue(MultiPageUIManager.Instance) == 6,
                "The original hand appraisal guide did not open");
            Next(20); return false;
        }
        if (step == 20)
        {
            Need(Sheet()?.root != null && !Sheet().root.activeInHierarchy, "Worksheet covered the original appraisal guide");
            // Public handbook text is recorded to validate the selectable catalog,
            // without reading an item's hidden tags or correct appraisal value.
            foreach (int book in new[] { 0, 4, 6 })
            {
                MultiPageUIManager.Instance.OpenUI(book);
                foreach (var text in MultiPageUIManager.Instance.books[book].GetComponentsInChildren<TMPro.TMP_Text>(true))
                    Debug.Log("[APPRAISAL-PUBLIC-GUIDE] " + book + " " + text.text);
            }
            MultiPageUIManager.Instance.CloseUI();
            Next(21); return false;
        }
        if (step == 21)
        {
            Need(Sheet()?.root?.activeInHierarchy == true, "Worksheet did not return after closing the guide");
            capture.Start("qa-appraisal-integrated.png"); Next(3); return false;
        }
        if (step == 3)
        {
            if (!capture.Ready()) return false;
            Sheet().Get("apply").button.onClick.Invoke(); ui.UpdateAppraisalPanel();
            Need(ui.apprisalServiceInputField.text == "100" && PlayerStore.Instance.playerCash == cash && customer.clientImpatience == impatience,
                "Applying the selected estimate submitted or changed the transaction");
            ui.OnInspectItem(second); ui.UpdateAppraisalPanel(); Next(4); return false;
        }
        if (step == 4)
        {
            var sheet = Sheet(); Need(sheet?.root?.activeInHierarchy == true, "Next item's embedded worksheet is absent");
            Need(sheet.Get("choice_0").GetIndex() == 0 && !sheet.Get("apply").button.interactable, "Observations leaked from the prior item");
            Fill(sheet); Need(sheet.Get("total").GetText().Contains("100"), "Identical observations changed with the hidden item configuration");
            sheet.Get("apply").button.onClick.Invoke(); ui.UpdateAppraisalPanel();
            Need(ui.apprisalServiceInputField.text == "100" && PlayerStore.Instance.playerCash == cash && customer.clientImpatience == impatience,
                "The second selected estimate read an answer or submitted an appraisal");
            ui.OnInspectItem(first); ui.UpdateAppraisalPanel(); Next(5); return false;
        }
        if (step == 5)
        {
            Need(Sheet().Get("choice_0").GetIndex() == 1, "The first item's observations did not survive switching items");
            incompleteFuel = DirectoryMaster.Item("plasma_fuel");
            Need(incompleteFuel != null && !incompleteFuel.IsTag("PLASMA_CELL_TAG"),
                "Incomplete plasma fixture unexpectedly activates the native fuel view");
            PlasmaHelper.InitDefaultPlasma(incompleteFuel);
            ui.OnInspectItem(incompleteFuel); ui.UpdateAppraisalPanel(); Next(29); return false;
        }
        if (step == 29)
        {
            Need(ui.fuelUILarge?.gameObject.activeInHierarchy == false && Sheet() == null &&
                ReferenceEquals(Field(feature, "unsupported"), incompleteFuel),
                "Worksheet did not reject the uninitialized native fuel view");
            var fuel = PreBuiltItemHelper.CreatePlasmaFull(100);
            Need(fuel != null && fuel.identifier == "plasma_fuel" && fuel.IsTag("PLASMA_CELL_TAG") && fuel.IsTag("APPRAISAL_ITEM"),
                "Native plasma fixture is not inspectable as an appraisal item");
            ui.OnInspectItem(fuel); ui.UpdateAppraisalPanel(); Next(30); return false;
        }
        if (step == 30)
        {
            var sheet = Sheet();
            Need(ui.fuelUILarge?.gameObject.activeInHierarchy == true,
                "Native plasma inspection view did not initialize");
            Need(sheet?.root?.activeInHierarchy == true, "Fuel observation sheet was not embedded: item=" +
                ui.currentInspectingItem?.identifier + ", inspection=" + ui.panel?.activeInHierarchy +
                ", appraisal=" + ui.appraisalPanel?.activeInHierarchy + ", fuel=" + ui.fuelUILarge?.gameObject.activeInHierarchy +
                ", unsupported=" + ((GameItem)Field(feature, "unsupported"))?.identifier +
                ", available=" + feature.GetType().GetMethod("Available", Flags).Invoke(feature, null) +
                ", supported=" + feature.GetType().GetMethod("SupportedView", Flags | BindingFlags.Static).Invoke(null, new object[] { ui }));
            for (int i = 0; i < 6; i++) sheet.Get("choice_" + i).SetIndex(1, true);
            Need(!sheet.Get("apply").button.interactable && !sheet.Get("total").GetText().Contains("—"),
                "Fuel handbook mismatch did not visibly disable automatic calculation");
            sheet.Get("guide").button.onClick.Invoke();
            Need((int)typeof(MultiPageUIManager).GetField("currentBookIndex", Flags).GetValue(MultiPageUIManager.Instance) == 0,
                "Fuel observation sheet did not open its original handbook");
            MultiPageUIManager.Instance.CloseUI(); Next(31); return false;
        }
        if (step == 31)
        {
            capture.Start("qa-appraisal-fuel-observations.png"); Next(32); return false;
        }
        if (step == 32)
        {
            if (!capture.Ready()) return false;
            ui.OnInspectItem(first); ui.UpdateAppraisalPanel(); Next(33); return false;
        }
        if (step == 33)
        {
            TradeScenario.SetOptions(false, "f23.calculate"); Next(6); return false;
        }
        if (step == 6)
        {
            Need(!Sheet().Get("apply").button.interactable && Sheet().Get("total").GetText().Contains("—"), "Recording mode still allowed calculations");
            ui.CloseUI(); Next(7); return false;
        }
        Need(Sheet() == null && ui.panel.transform.Find("ShopAssist_AppraisalChoices") == null, "Embedded worksheet remained after inspection closed");
        result = "PASS integrated F23, explicit unknown choices, undo, native guide, native apply without submission, hidden-state independence, item isolation and cleanup";
        return true;
    }
    private static void Fill(CustomUIWindow sheet) { for (int i = 0; i < 5; i++) sheet.Get("choice_" + i).SetIndex(1, true); }
    private static bool Reachable(Button button)
    {
        var rect = button.transform as RectTransform; var canvas = rect.GetComponentInParent<Canvas>();
        var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        return hits.Any(hit => hit.gameObject != null && hit.gameObject.transform.IsChildOf(button.transform));
    }
}
