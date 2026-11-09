using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed class AppraisalWorksheet : IDisposable
    {
        private const string PanelName = "ShopAssist_AppraisalChoices";
        private readonly ConfigEntry<bool> enabled, calculate;
        private readonly Func<KeyCode> key;
        private readonly Action<string> log;
        private readonly Dictionary<GameItem, Sheet> sheets = new Dictionary<GameItem, Sheet>();
        private CustomUIWindow window;
        private CustomUIElement totalLabel, applyButton, undoButton, guideButton;
        private RectTransform panel;
        private InspectionUIManager inspection;
        private StoreClientInstance client;
        private GameItem item, unsupported;
        private AppraisalWorksheetChoices.Catalog catalog;
        private string run;
        private int slot;
        private bool started;

        private sealed class Sheet
        {
            internal int[] Selected;
            internal readonly AppraisalWorksheetChoices.Catalog Catalog;
            internal readonly Stack<int[]> Undo = new Stack<int[]>();
            internal Sheet(AppraisalWorksheetChoices.Catalog catalog) { Catalog = catalog; Selected = new int[catalog.Groups.Length]; }
            internal void Select(int group, int value)
            {
                if (Selected[group] == value) return;
                if (Undo.Count >= 100)
                {
                    var recent = Undo.Take(99).Reverse().ToArray(); Undo.Clear();
                    foreach (var previous in recent) Undo.Push(previous);
                }
                Undo.Push((int[])Selected.Clone()); Selected[group] = value;
            }
        }
        internal AppraisalWorksheet(ShopSettings settings, Action<string> log)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            enabled = settings.Toggle("f23.enabled", "f23.tip");
            calculate = settings.Toggle("f23.calculate", "f23.calculate.tip");
            key = settings.Key("f23.key", "f23.key.tip");
            settings.Action("f23.open", Open, Available);
        }
        public void Start()
        {
            if (started) return;
            started = true; ModHook.OnGameLoadedInit += Reset; ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }
        public void Tick()
        {
            if (!started) return;
            if (!enabled.Value || !PlayerStore.HasInstance) { Reset(); return; }
            if (client != null && (PlayerStore.Instance.currentClientInstance != client || PlayerStore.Instance.runID != run ||
                PlayerStore.Instance.saveSlotId != slot)) Reset();
            if (!Available()) { Close(); return; }
            var current = InspectionUIManager.Instance.currentInspectingItem;
            if (!ReferenceEquals(unsupported, current) && (!ReferenceEquals(item, current) ||
                !ReferenceEquals(catalog, AppraisalWorksheetChoices.For(current.identifier)) || panel == null)) Open();
            if (panel == null) return;
            if (inspection != InspectionUIManager.Instance || !panel.IsChildOf(inspection.panel.transform)) { Close(); return; }
            if (MultiPageUIManager.Instance?.panel?.activeInHierarchy == true) { panel.gameObject.SetActive(false); return; }
            if (!PlacePanel()) { panel.gameObject.SetActive(false); return; }
            panel.gameObject.SetActive(true); RefreshSummary();
            KeyCode binding = key();
            if (binding != KeyCode.None && ShopInput.Ready() && Input.GetKeyDown(binding)) Open();
        }
        private bool Available()
        {
            var ui = InspectionUIManager.Instance;
            var current = PlayerStore.HasInstance ? PlayerStore.Instance.currentClientInstance : null;
            return enabled.Value && CustomUIManager.Instance != null && ui != null && ui.panel != null && ui.panel.activeInHierarchy &&
                ui.appraisalPanel != null && ui.appraisalPanel.activeInHierarchy && ui.currentInspectingItem != null &&
                AppraisalWorksheetChoices.For(ui.currentInspectingItem.identifier) != null &&
                current?.GetClientBlueprint()?.clientIntent == StoreClient.ClientIntent.APPRAISAL_SERVICE;
        }
        private static bool NativeMethodsSafe(Type type) => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).All(InventoryAutoInsert.KnownPatches);
        private static bool OriginalComponents(GameObject root) => root != null && root.GetComponentsInChildren<MonoBehaviour>(true).All(component =>
            component != null && (component.GetType().Assembly == typeof(GameItem).Assembly ||
                component.GetType().Assembly == typeof(AppraisalWorksheet).Assembly || component.GetType().Assembly == typeof(Button).Assembly ||
                component.GetType().Assembly == typeof(TMPro.TMP_Text).Assembly ||
                component.GetType().Assembly == typeof(UnityEngine.Localization.Components.LocalizeStringEvent).Assembly));
        private static bool SupportedView(InspectionUIManager ui)
        {
            if (!InventoryAutoInsert.VerifiedGameBuild(typeof(GameItem).Module.ModuleVersionId) || ui.GetType() != typeof(InspectionUIManager) ||
                !NativeMethodsSafe(typeof(InspectionUIManager)) || !NativeMethodsSafe(typeof(GameItem)) ||
                !(ui.panel.transform is RectTransform) || !(ui.magnifyingGlass?.transform is RectTransform) ||
                !(ui.appraisalPanel.transform is RectTransform) || !ui.appraisalPanel.transform.IsChildOf(ui.panel.transform) ||
                ui.magnifyingGlass.transform.parent != ui.panel.transform || ui.apprisalServiceInputField == null ||
                !ui.apprisalServiceInputField.transform.IsChildOf(ui.appraisalPanel.transform) ||
                ui.currentInspectingItem.GetType() != typeof(GameItemElement) || !OriginalComponents(ui.panel)) return false;
            Component view = ui.currentInspectingItem.identifier == "hand" ? (Component)ui.appHandUILarge :
                ui.currentInspectingItem.identifier == "ribwich" ? ui.ribwichUILarge : ui.fuelUILarge;
            return view != null && view.gameObject.activeInHierarchy && view.transform.IsChildOf(ui.magnifyingGlass.transform) &&
                view.GetType().Assembly == typeof(GameItem).Assembly;
        }
        private void Open()
        {
            if (!Available()) return;
            var ui = InspectionUIManager.Instance;
            try
            {
                if (!SupportedView(ui)) { unsupported = ui.currentInspectingItem; Close(); return; }
                if (client != null && (client != PlayerStore.Instance.currentClientInstance || run != PlayerStore.Instance.runID ||
                    slot != PlayerStore.Instance.saveSlotId)) Reset();
                if (ReferenceEquals(item, ui.currentInspectingItem) && ReferenceEquals(catalog, AppraisalWorksheetChoices.For(item.identifier)) && panel != null)
                { panel.gameObject.SetActive(true); panel.SetAsLastSibling(); return; }
                Close();
                client = PlayerStore.Instance.currentClientInstance; run = PlayerStore.Instance.runID; slot = PlayerStore.Instance.saveSlotId;
                inspection = ui; item = ui.currentInspectingItem; catalog = AppraisalWorksheetChoices.For(item.identifier);
                if (!sheets.TryGetValue(item, out var existing) || !ReferenceEquals(existing.Catalog, catalog)) sheets[item] = new Sheet(catalog);
                Build();
            }
            catch (Exception error) { unsupported = ui.currentInspectingItem; Close(); log("F23 left an unsupported appraisal view unchanged: " + error); }
        }
        private void Build()
        {
            if (item == null || !sheets.TryGetValue(item, out var sheet) || !Current(sheet)) return;
            var go = new GameObject(PanelName, typeof(RectTransform), typeof(Image)); panel = (RectTransform)go.transform;
            panel.SetParent(inspection.panel.transform, false); panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(.5f, .5f);
            panel.sizeDelta = new Vector2(310, 450);
            var image = go.GetComponent<Image>(); image.sprite = MachineryStyle.ButtonNormal; image.type = Image.Type.Sliced; image.color = MachineryStyle.PANEL;
            var builder = CustomUIManager.Instance.BuildInto(panel, true, PanelName).UseMachineryStyle(true);
            var layout = go.GetComponent<VerticalLayoutGroup>(); layout.padding = new RectOffset(10, 10, 10, 10); layout.spacing = 6;
            builder.AddButton(ShopSettings.Text("f23.guide"), OpenGuide, "guide"); guideButton = builder.Last;
            builder.AddLabel(ShopSettings.Format("f23.basis", catalog.Basis));
            builder.BeginScrollFlexible(120);
            for (int i = 0; i < catalog.Groups.Length; i++)
            {
                int index = i; var group = catalog.Groups[i];
                builder.AddLabel(ShopSettings.Text(group.Key));
                var labels = new List<string> { ShopSettings.Text("f23.unknown") };
                labels.AddRange(group.Options.Select(option => ShopSettings.Text("f23.option." + option)));
                builder.AddDropdown(labels, sheet.Selected[index], value =>
                {
                    if (!Current(sheet) || value < 0 || value > group.Options.Length) return;
                    sheet.Select(index, value); RefreshSummary();
                }, "choice_" + i);
            }
            builder.End().AddLabel("", "total"); totalLabel = builder.Last;
            builder.BeginRow().AddButton(ShopSettings.Text("f23.undo"), () =>
            {
                if (!Current(sheet) || sheet.Undo.Count == 0) return;
                sheet.Selected = sheet.Undo.Pop(); Rebuild();
            }, "undo"); undoButton = builder.Last;
            builder.AddButton(ShopSettings.Text("f23.apply"), Apply, "apply"); applyButton = builder.Last;
            builder.End(); window = builder.Show();
            if (!PlacePanel()) panel.gameObject.SetActive(false);
            RefreshSummary();
        }
        private static Rect ScreenRect(RectTransform rect)
        {
            var corners = new Vector3[4]; rect.GetWorldCorners(corners); var canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var points = corners.Select(point => RectTransformUtility.WorldToScreenPoint(camera, point)).ToArray();
            return Rect.MinMaxRect(points.Min(p => p.x), points.Min(p => p.y), points.Max(p => p.x), points.Max(p => p.y));
        }
        private bool PlacePanel()
        {
            if (panel == null || inspection?.panel == null || inspection.magnifyingGlass == null) return false;
            var bounds = new List<Rect> { ScreenRect((RectTransform)inspection.magnifyingGlass.transform),
                ScreenRect((RectTransform)inspection.appraisalPanel.transform) };
            foreach (var button in inspection.panel.GetComponentsInChildren<Button>())
                if (button != null && !button.transform.IsChildOf(panel) && button.transform is RectTransform rect) bounds.Add(ScreenRect(rect));
            Component small = item.identifier == "hand" ? (Component)inspection.appHandUISmall : item.identifier == "ribwich" ? inspection.ribwichUISmall : inspection.fuelUISmall;
            if (small != null && small.transform is RectTransform smallRect) bounds.Add(ScreenRect(smallRect));
            Rect current = ScreenRect(panel);
            if (current.height > Screen.height - 16 && current.height > 0)
            {
                panel.sizeDelta = new Vector2(panel.sizeDelta.x, panel.sizeDelta.y * (Screen.height - 16) / current.height);
                current = ScreenRect(panel);
            }
            float width = current.width, height = current.height;
            if (width <= 0 || height < 250) return false;
            var xs = new List<float> { Screen.width - width - 8, 8 };
            var ys = new List<float> { Screen.height - height - 8, 8 };
            xs.AddRange(bounds.Select(rect => rect.xMax + 8)); xs.AddRange(bounds.Select(rect => rect.xMin - width - 8));
            ys.AddRange(bounds.Select(rect => rect.yMin - height - 8)); ys.AddRange(bounds.Select(rect => rect.yMax + 8));
            foreach (float x in xs) foreach (float y in ys)
            {
                var candidate = new Rect(x, y, width, height);
                if (candidate.xMin < 8 || candidate.yMin < 8 || candidate.xMax > Screen.width - 8 || candidate.yMax > Screen.height - 8 ||
                    bounds.Any(rect => candidate.Overlaps(rect))) continue;
                var parent = (RectTransform)inspection.panel.transform;
                var canvas = parent.GetComponentInParent<Canvas>();
                Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, candidate.center, camera, out var point)) return false;
                panel.localPosition = new Vector3(point.x, point.y, 0); return true;
            }
            return false;
        }
        private bool Current(Sheet sheet) => Available() && item != null && ReferenceEquals(item, InspectionUIManager.Instance.currentInspectingItem) &&
            ReferenceEquals(catalog, AppraisalWorksheetChoices.For(item.identifier)) &&
            ReferenceEquals(client, PlayerStore.Instance.currentClientInstance) && run == PlayerStore.Instance.runID && slot == PlayerStore.Instance.saveSlotId &&
            sheets.TryGetValue(item, out var current) && ReferenceEquals(current, sheet);
        private void RefreshSummary()
        {
            if (item == null || !sheets.TryGetValue(item, out var sheet)) return;
            int value = 0; bool valid = calculate.Value && AppraisalWorksheetChoices.TryTotal(catalog, sheet.Selected, out value);
            string text = ReferenceEquals(catalog, AppraisalWorksheetChoices.Plasma) ? ShopSettings.Text("f23.guide_mismatch") :
                ShopSettings.Format("f23.total", valid ? value.ToString(CultureInfo.InvariantCulture) : "—");
            if (totalLabel?.IsAlive == true && totalLabel.GetText() != text) totalLabel.SetText(text);
            applyButton?.SetInteractable(valid && Current(sheet)); undoButton?.SetInteractable(sheet.Undo.Count != 0);
            guideButton?.SetInteractable(CanOpenGuide());
        }
        private bool CanOpenGuide()
        {
            var books = MultiPageUIManager.Instance;
            return Available() && catalog != null && books != null && books.GetType() == typeof(MultiPageUIManager) && books.panel != null &&
                books.books != null && catalog.Book >= 0 && catalog.Book < books.books.Count && books.books.All(book => book != null) &&
                books.books[catalog.Book].transform.childCount > 0 && books.buttonLeft != null && books.buttonRight != null && books.pageNumberText != null &&
                PaperUIManager.Instance?.audioSourcePickup != null &&
                books.books[catalog.Book].transform.IsChildOf(books.panel.transform) && OriginalComponents(books.panel) &&
                NativeMethodsSafe(typeof(MultiPageUIManager)) && NativeMethodsSafe(typeof(HealthData)) &&
                NativeMethodsSafe(typeof(StartingPerk)) && HealthData.CanRead();
        }
        private void OpenGuide()
        {
            if (item == null || !sheets.TryGetValue(item, out var sheet) || !Current(sheet) || !CanOpenGuide()) return;
            MultiPageUIManager.Instance.OpenUI(catalog.Book);
        }
        private void Apply()
        {
            if (item == null || !sheets.TryGetValue(item, out var sheet) || !Current(sheet) || !calculate.Value ||
                !AppraisalWorksheetChoices.TryTotal(catalog, sheet.Selected, out int value) || !SupportedView(inspection)) return;
            var ui = inspection;
            if (ui.isEditingAppraisalField || StoreUIManager.IsAnyTyping() || KeyBindHelper.IsCapturing || UIDragHandler.isDraggingAnyUI ||
                ItemMouseDragHandler.current?.IsDraggingItem == true || DialogUIManager.Instance?.currentDialog != null ||
                DialoguePhoneUIManager.Instance?.currentDialog != null) return;
            string text = value.ToString(CultureInfo.InvariantCulture);
            ui.apprisalServiceInputField.SetTextWithoutNotify(text); ui.OnValueChanged(text);
        }
        private void Rebuild() { window?.Close(); window = null; panel = null; Build(); }
        private void Close()
        {
            window?.Close(); window = null;
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            panel = null; totalLabel = applyButton = undoButton = guideButton = null; item = null; inspection = null; catalog = null;
        }
        private void Reset() { Close(); sheets.Clear(); client = null; run = null; slot = -1; unsupported = null; }
        private void SceneChanged(Scene oldScene, Scene newScene) => Reset();
        public void Dispose()
        {
            if (!started) return;
            ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Reset; SceneManager.activeSceneChanged -= SceneChanged;
            Reset(); started = false;
        }
    }
}
