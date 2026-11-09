using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ShopAssist
{
    // The original scanner remains the authority for measurement and actions.
    internal sealed class LiquidBars : IDisposable
    {
        private sealed class RowView
        {
            internal RectTransform Rect, Track, Fill, Mark, PinRect;
            internal TextMeshProUGUI Name, Volume, Percent;
            internal LiquidPinIcon Pin;
        }

        private sealed class View
        {
            internal GameObject Panel, Root;
            internal GameItem Sample;
            internal TextMeshProUGUI NativeText, Title, Total, TestedCopy, LabelCopy, TestedHeading, LabelHeading;
            internal TextMeshProUGUI NameHeading, VolumeHeading;
            internal TextState Tested, Label, TotalText, Product;
            internal Button Match, Confront, Sort, Gear;
            internal TextMeshProUGUI SortText;
            internal RectTransform Content, Viewport, Summary, LayoutRoot, Header, ListHeader, SortRect, HeaderRule, SummaryRule;
            internal RectTransform ScreenCenter, ScreenLeft, ScreenRight;
            internal ScrollRect Scroller;
            internal Scrollbar Scrollbar;
            internal bool Chemical, NativeEnabled, Hovered, MatchActive, ConfrontActive, ModeReady, Stacked, HasEffects, Pending;
            internal string Raw, Selected, MethodSignature, ExpandedMethod, TestedRaw, LabelRaw, PendingDetail;
            internal string Locale;
            internal float NextCheck;
            internal TMP_FontAsset Font;
            internal int TotalAmount;
            internal float PanelWidth, PanelHeight, RowHeight, ModeWidth, ModeFont;
            internal readonly List<LiquidComposition.Row> NativeRows = new List<LiquidComposition.Row>();
            internal readonly List<LiquidComposition.Row> Rows = new List<LiquidComposition.Row>();
            internal readonly Dictionary<string, Liquid> Seen = new Dictionary<string, Liquid>(StringComparer.Ordinal);
            internal readonly Dictionary<string, RowView> RowViews = new Dictionary<string, RowView>(StringComparer.Ordinal);
            internal readonly LiquidDisplayRules Ordering = new LiquidDisplayRules();
            internal CustomUIWindow Detail, DisplaySettings;
            internal RectTransform DetailBody;
            internal CustomUIElement DetailSummary;
        }

        private sealed class TextState
        {
            internal readonly TextMeshProUGUI Text;
            private readonly bool enabled;
            private bool applied;

            internal TextState(TextMeshProUGUI text)
            {
                Text = text;
                if (text != null) enabled = text.enabled;
            }

            internal void Hide()
            {
                if (Text == null) return;
                applied = true; Text.enabled = false;
            }

            internal bool Owned() => Text == null || !applied || !Text.enabled;

            internal void Restore()
            {
                if (Text == null || !applied) return;
                if (!Text.enabled) Text.enabled = enabled;
            }
        }

        private static LiquidBars active;
        private static readonly Vector3[] windowCorners = new Vector3[4];
        private readonly ConfigEntry<bool> enabled, showBars, showColors, sortByShare;
        private readonly ConfigEntry<int> fontSize;
        private readonly ConfigEntry<string> pinSetting;
        private readonly ModConfig config;
        private readonly Action<string> log;
        private readonly LiquidDisplayNames displayNames;
        private readonly View water = new View(), chem = new View();
        private readonly List<string> pins = new List<string>();
        private CustomUIWindow picker;
        private string lastPinValue;
        private bool started, pinDataValid;

        internal LiquidBars(ShopSettings settings, Action<string> log)
        {
            this.log = log;
            displayNames = new LiquidDisplayNames(log);
            enabled = settings.Toggle("f19.enabled", "f19.tip");
            fontSize = settings.Integer("f19.size", "f19.size.tip", 14, 8, 28);
            showBars = settings.Toggle("f19.bars", "f19.bars.tip", true);
            showColors = settings.Toggle("f19.colors", "f19.colors.tip");
            sortByShare = settings.Toggle("f19.sort", "f19.sort.tip", true);
            config = ModHelper.GetConfig(ModEntry.Id);
            pinSetting = config.Define("f19.pinned", LiquidPins.Write(Array.Empty<string>()), enabled.Section,
                () => ShopSettings.Text("f19.pinned"), tooltip: () => ShopSettings.Text("f19.pinned.tip"));
            settings.Action("f19.choose", OpenPicker, CanChoose);
        }

        internal void Start()
        {
            if (started) return;
            if (active != null && !ReferenceEquals(active, this)) throw new InvalidOperationException("Liquid scanner layout already active.");
            active = this; started = true;
            LoadPins();
        }

        private void LoadPins()
        {
            pins.Clear(); lastPinValue = pinSetting.Value ?? "";
            pinDataValid = LiquidPins.TryRead(lastPinValue, out List<string> loaded);
            if (pinDataValid) pins.AddRange(loaded);
            else log("F19 retained invalid pinned-component preferences; pin changes are disabled.");
        }

        private bool SavePins()
        {
            try
            {
                string encoded = LiquidPins.Write(pins);
                if (!LiquidPins.TryRead(encoded, out _)) throw new InvalidOperationException("Invalid liquid pin preferences.");
                string original = pinSetting.Value ?? "";
                try
                {
                    pinSetting.Set(encoded);
                    config.Save();
                    if (pinSetting.Value != encoded) throw new InvalidOperationException("Pin preferences were not retained.");
                    lastPinValue = encoded;
                }
                catch
                {
                    try { pinSetting.Set(original); config.Save(); lastPinValue = original; }
                    catch (Exception rollback) { log("F19 could not restore the previous pin setting: " + rollback); }
                    throw;
                }
                return true;
            }
            catch (Exception error)
            {
                log("F19 could not save pinned components: " + error);
                ShopNotice.Show(ShopSettings.Text("f19.pin_failed"));
                return false;
            }
        }

        private bool CommitPins(List<string> next)
        {
            var before = pins.ToArray();
            pins.Clear(); pins.AddRange(next);
            if (SavePins()) return true;
            pins.Clear(); pins.AddRange(before);
            return false;
        }

        internal void Tick()
        {
            if (!started) return;
            if (!enabled.Value) { Reset(); return; }
            CheckSafely(water); CheckSafely(chem);
            if (picker != null && (!picker.IsAlive || !CanChoose())) ClosePicker();
        }

        private void CheckSafely(View view)
        {
            try { Check(view); }
            catch (Exception error) { Clear(view); log("F19 could not update scanner layout: " + error); }
        }

        private void Check(View view)
        {
            if (view.Panel == null) return;
            if (!StillOpen(view)) { Clear(view); return; }
            if (view.Root != null)
            {
                if (view.NativeText.text != view.Raw || view.NativeText.enabled ||
                    view.Tested?.Owned() == false || view.Label?.Owned() == false || view.TotalText?.Owned() == false || view.Product?.Owned() == false)
                { Clear(view); return; }
                Rect bounds = ((RectTransform)view.Panel.transform).rect;
                bool layoutChanged = Mathf.Abs(view.PanelWidth - bounds.width) > .5f || Mathf.Abs(view.PanelHeight - bounds.height) > .5f ||
                    view.HasEffects != HasChemicalEffects(view) || (view.Match?.gameObject.activeInHierarchy == true) != view.MatchActive ||
                    (view.Confront?.gameObject.activeInHierarchy == true) != view.ConfrontActive;
                bool hovered = PointerOver(view.Root);
                bool left = view.Hovered && !hovered;
                view.Hovered = hovered;
                if (layoutChanged || left)
                {
                    UpdateRows(view);
                    if (view.Root == null) return;
                }
                if (view.Detail != null && !view.Detail.IsAlive) { view.Detail = null; view.Selected = null; view.DetailBody = null; }
                else if (view.Detail?.IsAlive == true) PositionWindow(view, view.Detail, view.Detail.Rect.rect.size);
                if (view.DisplaySettings?.IsAlive == true) PositionWindow(view, view.DisplaySettings, view.DisplaySettings.Rect.rect.size);
            }
            if (Time.unscaledTime < view.NextCheck && !view.Pending && !view.Hovered) return;
            view.NextCheck = Time.unscaledTime + .05f;
            if (!displayNames.Ready()) { view.Pending = true; return; }
            if (view.Root == null)
            {
                if (!view.NativeText.enabled || view.NativeText.text != view.Raw) { Clear(view); return; }
                if (!Create(view)) { Clear(view); return; }
                view.Pending = false; UpdateRows(view); UpdateDetail(view);
                return;
            }
            if (view.Pending || view.TestedRaw != (view.Tested?.Text?.text ?? "") || view.LabelRaw != (view.Label?.Text?.text ?? "") ||
                (view.Title?.text ?? "") != SampleTitle(view) || view.Locale != (LocHelper.GetCurrentLocaleCode() ?? "") ||
                view.Font != view.NativeText.font)
            { view.Pending = false; UpdateRows(view); UpdateDetail(view); }
            string pendingDetail = view.PendingDetail;
            if (pendingDetail != null) { view.PendingDetail = null; OpenDetail(view, pendingDetail); }
        }

        internal void SettingsChanged()
        {
            if (!started) return;
            if ((pinSetting.Value ?? "") != lastPinValue) LoadPins();
            if (!enabled.Value) { Reset(); return; }
            ReapplySafely(water); ReapplySafely(chem);
        }

        private void ReapplySafely(View view)
        {
            try { Reapply(view); }
            catch (Exception error) { Clear(view); log("F19 could not update scanner settings: " + error); }
        }

        private void Reapply(View view)
        {
            if (view.Root == null || !StillOpen(view) || view.NativeText.text != view.Raw) return;
            if (!displayNames.Ready()) { view.Pending = true; return; }
            if (!Layout(view)) { Clear(view); return; }
            UpdateRows(view);
            UpdateDetail(view);
        }

        private static bool StillOpen(View view)
        {
            if (view.Panel == null || !view.Panel.activeInHierarchy || view.Sample == null || view.NativeText == null) return false;
            return view.Chemical ? ReferenceEquals(ChemScannerUIManager.Instance?.currentScannedItem, view.Sample) :
                ReferenceEquals(WaterScannerUIManager.Instance?.currentWaterContainer, view.Sample);
        }

        private bool CanChoose() => enabled.Value && (!pinDataValid ||
            (StillOpen(chem) && chem.Rows.Count > 0 || StillOpen(water) && water.Rows.Count > 0));

        private void OpenPicker()
        {
            if (!CanChoose() || CustomUIManager.Instance == null) return;
            bool chooseChem = StillOpen(chem) && chem.Rows.Count > 0 &&
                (!StillOpen(water) || water.Rows.Count == 0 || PaperPanelFocusStamp.GetStamp(chem.Panel) >= PaperPanelFocusStamp.GetStamp(water.Panel));
            View source = chooseChem ? chem : water;
            ClosePicker();
            Vector2 size = UiLayout.Fit(360, 340);
            var builder = CustomUIManager.Window(ModEntry.Id + ".f19.pin", ShopSettings.Text("f19.choose"), CustomUIManager.LAYER_META)
                .SetSize(size.x, size.y).SetDraggable(true).SetCloseOnEscape(true);
            picker = builder.GetWindow(); CustomUIWindow own = picker;
            builder.OnClose(() => { if (ReferenceEquals(picker, own)) picker = null; });
            builder.BeginScrollFlexible();
            if (!pinDataValid) builder.AddWarning(ShopSettings.Text("f19.pin_invalid"));
            builder.AddButton(ShopSettings.Text(pinDataValid ? "f19.none" : "f19.pin_reset"), () => { ClearPins(); OpenPicker(); });
            foreach (LiquidComposition.Row row in pinDataValid ? source.Rows.ToArray() : Array.Empty<LiquidComposition.Row>())
            {
                string id = row.Liquid.identifier;
                string caption = ShopSettings.Format(pins.Contains(id) ? "f19.picker_pinned" : "f19.picker_pin", displayNames.Get(row.Liquid));
                builder.AddButton(caption, () => { TogglePin(id); OpenPicker(); });
                if (builder.Last.text != null) { builder.Last.text.richText = false; SetReadableFont(builder.Last.text, source, builder.Last.text.fontSize, caption); }
            }
            builder.End().Show();
        }

        private void ClearPins()
        {
            if (CommitPins(new List<string>())) { pinDataValid = true; ReapplySafely(water); ReapplySafely(chem); }
        }

        private void TogglePin(string id)
        {
            if (!pinDataValid || string.IsNullOrWhiteSpace(id) || id.Length > 128)
            { ShopNotice.Show(ShopSettings.Text("f19.pin_invalid")); return; }
            var changed = pins.ToList();
            if (!changed.Remove(id))
            {
                if (changed.Count >= 128) { ShopNotice.Show(ShopSettings.Text("f19.pin_limit")); return; }
                changed.Add(id);
            }
            if (CommitPins(changed)) { ReapplySafely(water); ReapplySafely(chem); }
        }

        private void ToggleSort()
        {
            bool before = sortByShare.Value;
            try { sortByShare.Set(!before); config.Save(); }
            catch (Exception error)
            {
                try { sortByShare.Set(before); config.Save(); }
                catch (Exception rollback) { log("F19 could not restore the previous sort setting: " + rollback); }
                log("F19 could not save scan sorting: " + error);
                ShopNotice.Show(ShopSettings.Text("f19.sort_failed"));
                return;
            }
            ReapplySafely(water); ReapplySafely(chem);
        }

        private void ClosePicker() { CustomUIWindow previous = picker; picker = null; previous?.Close(); }

        private void Capture(View view, GameObject panel, GameItem sample, TextMeshProUGUI text,
            TextMeshProUGUI tested, TextMeshProUGUI label, TextMeshProUGUI totalText, TextMeshProUGUI product,
            Button match, Button confront, bool chemical)
        {
            if (!started || !enabled.Value || panel == null || !panel.activeInHierarchy || sample == null || text == null)
            { Clear(view); return; }
            try
            {
                string raw = text.text ?? "";
                bool same = ReferenceEquals(view.Sample, sample) && view.NativeText == text && view.Panel == panel;
                if (!LiquidComposition.TryRead(sample, raw, out List<LiquidComposition.Row> rows, out int total) ||
                    !same && (!text.enabled || rows.Count == 0))
                { Clear(view); return; }
                if (!same)
                {
                    Clear(view); view.Panel = panel; view.Sample = sample; view.NativeText = text; view.NativeEnabled = text.enabled;
                    view.Tested = new TextState(tested); view.Label = new TextState(label);
                    view.TotalText = totalText == null ? null : new TextState(totalText);
                    view.Product = product == null ? null : new TextState(product);
                    view.Match = match; view.Confront = confront; view.Chemical = chemical;
                }
                view.Raw = raw; view.TotalAmount = total;
                view.NativeRows.Clear(); view.NativeRows.AddRange(rows);
                foreach (LiquidComposition.Row row in rows) view.Seen[row.Liquid.identifier] = row.Liquid;
                if (!displayNames.Ready()) { view.Pending = true; return; }
                if (view.Root == null && !Create(view)) { Clear(view); return; }
                if ((view.Match?.gameObject.activeInHierarchy == true) != view.MatchActive ||
                    (view.Confront?.gameObject.activeInHierarchy == true) != view.ConfrontActive ||
                    view.HasEffects != HasChemicalEffects(view))
                    if (!Layout(view)) { Clear(view); return; }
                UpdateRows(view);
                UpdateDetail(view);
            }
            catch (Exception error) { Clear(view); log("F19 could not display scanner output: " + error); }
        }

        private bool Create(View view)
        {
            RectTransform source = view.NativeText.rectTransform;
            if (!(view.Panel.transform is RectTransform panel) || source.parent != panel) return false;
            view.Root = new GameObject("ShopAssist_LiquidBars", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform root = (RectTransform)view.Root.transform;
            root.SetParent(panel, false); root.anchorMin = root.anchorMax = root.pivot = new Vector2(.5f, .5f);
            root.localScale = Vector3.one; root.localRotation = Quaternion.identity; root.SetAsLastSibling();
            Image rootImage = view.Root.GetComponent<Image>(); rootImage.color = Color.clear; rootImage.raycastTarget = false;
            Color screen = new Color32(41, 34, 52, 255);
            view.ScreenCenter = Box(root, "ScreenCenter", screen, 0, 0, 1, 1, 12, 0, -12, 0);
            view.ScreenLeft = Box(root, "ScreenLeft", screen, 0, 0, 0, 1, 0, 12, 12, -12);
            view.ScreenRight = Box(root, "ScreenRight", screen, 1, 0, 1, 1, -12, 12, 0, -12);
            view.LayoutRoot = Box(root, "InteriorContent", Color.clear, 0, 0, 1, 1, 12, 10, -12, -10);
            view.Header = Box(view.LayoutRoot, "Header", Color.clear, 0, 1, 1, 1, 0, -43, 0, 0);
            view.Title = Text(view.Header, view, "", TextAlignmentOptions.MidlineLeft, 0, 21, -30, 0);
            view.Title.gameObject.name = "SampleTitle";
            view.Title.raycastTarget = true; view.Title.gameObject.AddComponent<CustomUITooltipTrigger>();
            view.Total = Text(view.Header, view, "", TextAlignmentOptions.MidlineLeft, 0, 1, -30, -21);
            view.Total.gameObject.name = "Total";
            view.HeaderRule = Box(view.Header, "HeaderRule", Color.white, 0, 0, 1, 0, 0, 0, 0, 1);
            RectTransform gear = Box(view.Header, "DisplaySettings", Color.clear, 1, .5f, 1, .5f, -26, -13, 0, 13);
            Image gearImage = gear.GetComponent<Image>(); gearImage.raycastTarget = true;
            gearImage.sprite = CustomUIManager.Instance.spriteSetting; gearImage.preserveAspect = true;
            if (gearImage.sprite != null) gearImage.color = Color.white;
            view.Gear = gear.gameObject.AddComponent<Button>(); view.Gear.targetGraphic = gearImage;
            view.Gear.onClick.AddListener(() => OpenDisplaySettings(view));
            gear.gameObject.AddComponent<CustomUITooltipTrigger>().tooltipText = ShopSettings.Text("f19.display");
            if (gearImage.sprite == null)
            {
                TextMeshProUGUI gearText = Text(gear, view, ShopSettings.Text("f19.display_short"), TextAlignmentOptions.Center, 0, 0, 0, 0);
                SetFont(gearText, view, 11);
            }
            view.Summary = Box(view.LayoutRoot, "NativeSummary", Color.clear, 0, 1, 1, 1, 0, -100, 0, -47);
            view.TestedHeading = Text(view.Summary, view, "", TextAlignmentOptions.TopLeft, 0, 0, -5, 0, 0, .5f);
            view.LabelHeading = Text(view.Summary, view, "", TextAlignmentOptions.TopLeft, 5, 0, 0, 0, .5f, 1);
            view.TestedCopy = Text(view.Summary, view, "", TextAlignmentOptions.TopLeft, 0, 0, -5, -17, 0, .5f);
            view.TestedCopy.gameObject.name = "Tested";
            view.LabelCopy = Text(view.Summary, view, "", TextAlignmentOptions.TopLeft, 5, 0, 0, -17, .5f, 1);
            view.LabelCopy.gameObject.name = "Labeled";
            view.SummaryRule = Box(view.Summary, "SummaryRule", Color.white, 0, 0, 1, 0, 0, 0, 0, 1);
            view.ListHeader = Box(view.LayoutRoot, "ListHeader", Color.clear, 0, 1, 1, 1, 0, -122, 0, -104);
            view.NameHeading = Text(view.ListHeader, view, ShopSettings.Text("f19.component"), TextAlignmentOptions.MidlineLeft, 20, 0, -100, 0);
            view.VolumeHeading = Text(view.ListHeader, view, ShopSettings.Text("f19.volume"), TextAlignmentOptions.MidlineRight, 0, 0, -50, 0);
            RectTransform sort = Box(view.ListHeader, "Sort", Color.clear, 1, 0, 1, 1, -60, 0, 0, 0);
            view.SortRect = sort;
            sort.GetComponent<Image>().raycastTarget = true;
            view.Sort = sort.gameObject.AddComponent<Button>();
            view.Sort.targetGraphic = sort.GetComponent<Image>();
            view.Sort.onClick.AddListener(ToggleSort);
            sort.gameObject.AddComponent<CustomUITooltipTrigger>();
            view.SortText = Text(sort, view, "", TextAlignmentOptions.MidlineRight, 0, 0, 0, 0);
            RectTransform viewport = Box(view.LayoutRoot, "Viewport", Color.clear, 0, 0, 1, 1, 0, 0, -7, -122);
            viewport.GetComponent<Image>().raycastTarget = true; viewport.gameObject.AddComponent<RectMask2D>();
            view.Viewport = viewport;
            view.Content = Box(viewport, "Content", Color.clear, 0, 1, 1, 1, 0, 0, 0, 0);
            view.Content.pivot = new Vector2(.5f, 1); view.Content.anchoredPosition = Vector2.zero;
            view.Scroller = viewport.gameObject.AddComponent<ScrollRect>();
            view.Scroller.viewport = viewport; view.Scroller.content = view.Content; view.Scroller.horizontal = false;
            view.Scroller.vertical = true; view.Scroller.movementType = ScrollRect.MovementType.Clamped;
            view.Scroller.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            RectTransform track = Box(view.LayoutRoot, "Scrollbar", new Color(1, 1, 1, .10f), 1, 0, 1, 1, -4, 0, 0, -122);
            track.GetComponent<Image>().raycastTarget = true;
            RectTransform handle = Box(track, "Handle", new Color(1, 1, 1, .55f), 0, 0, 1, 1, 0, 0, 0, 0);
            view.Scrollbar = track.gameObject.AddComponent<Scrollbar>();
            view.Scrollbar.handleRect = handle; view.Scrollbar.targetGraphic = handle.GetComponent<Image>();
            view.Scrollbar.direction = Scrollbar.Direction.BottomToTop;
            view.Scroller.verticalScrollbar = view.Scrollbar;
            view.NativeText.enabled = false;
            view.Tested?.Hide(); view.Label?.Hide(); view.TotalText?.Hide(); view.Product?.Hide();
            return Layout(view);
        }

        private static bool HasChemicalEffects(View view)
        {
            if (!view.Chemical || view.Panel == null) return false;
            Transform group = view.Panel.transform.Find("chemPropertyGroup");
            if (group == null) return true;
            for (int i = 0; i < group.childCount; i++)
                if (group.GetChild(i).gameObject.activeInHierarchy) return true;
            return false;
        }

        private static string SampleTitle(View view) => view.Product?.Text != null
            ? view.Product.Text.text ?? "" : view.Sample?.GetDisplayName(true) ?? "";

        private bool Layout(View view)
        {
            if (!StillOpen(view) || view.Root == null) return false;
            RectTransform panel = (RectTransform)view.Panel.transform;
            Rect bounds = panel.rect; view.PanelWidth = bounds.width; view.PanelHeight = bounds.height;
            Image nativePanel = view.Panel.GetComponent<Image>();
            string expectedSprite = view.Chemical ? "chem_scan" : "aqua_scan";
            if (nativePanel == null || nativePanel.sprite == null || nativePanel.sprite.name != expectedSprite ||
                bounds.width <= 0 || bounds.height <= 0) return false;
            view.MatchActive = view.Match?.gameObject.activeInHierarchy == true;
            view.ConfrontActive = view.Confront?.gameObject.activeInHierarchy == true;
            view.HasEffects = HasChemicalEffects(view);
            // The native scanner sprites contain their dividers. Replace their inner screens while
            // retaining the device frame and any active chemical-effect controls.
            float sx = bounds.width / 422f, sy = bounds.height / 614f;
            float left = -144f * sx, right = 144f * sx;
            float top = (view.Chemical ? 191f : 129f) * sy;
            float bottom = (view.Chemical ? view.HasEffects ? -95f : -183f : -152f) * sy;
            foreach (Button button in new[] { view.Match, view.Confront })
            {
                if (button == null || !button.gameObject.activeInHierarchy || !(button.transform is RectTransform control)) continue;
                Vector3[] corners = new Vector3[4]; control.GetWorldCorners(corners);
                float controlTop = corners.Max(point => panel.InverseTransformPoint(point).y) - bounds.center.y;
                float controlBottom = corners.Min(point => panel.InverseTransformPoint(point).y) - bounds.center.y;
                float controlLeft = corners.Min(point => panel.InverseTransformPoint(point).x) - bounds.center.x;
                float controlRight = corners.Max(point => panel.InverseTransformPoint(point).x) - bounds.center.x;
                if (controlLeft < right && controlRight > left &&
                    controlBottom < top && controlTop > bottom) bottom = Mathf.Max(bottom, controlTop + 4);
            }
            RectTransform root = (RectTransform)view.Root.transform;
            root.anchoredPosition = new Vector2((left + right) * .5f, (top + bottom) * .5f);
            root.sizeDelta = new Vector2(right - left, top - bottom);
            float cornerX = 12 * sx, cornerY = 12 * sy;
            view.ScreenCenter.offsetMin = new Vector2(cornerX, 0); view.ScreenCenter.offsetMax = new Vector2(-cornerX, 0);
            view.ScreenLeft.offsetMin = new Vector2(0, cornerY); view.ScreenLeft.offsetMax = new Vector2(cornerX, -cornerY);
            view.ScreenRight.offsetMin = new Vector2(-cornerX, cornerY); view.ScreenRight.offsetMax = new Vector2(0, -cornerY);
            float padX = 12 * sx, padY = 10 * sy;
            view.LayoutRoot.offsetMin = new Vector2(padX, padY); view.LayoutRoot.offsetMax = new Vector2(-padX, -padY);
            view.Locale = LocHelper.GetCurrentLocaleCode() ?? ""; view.Font = view.NativeText.font;
            view.Title.text = SampleTitle(view);
            SetReadableFont(view.Title, view, 16, view.Title.text); SetFont(view.Total, view, 13);
            SetFont(view.NameHeading, view, 11); SetFont(view.VolumeHeading, view, 11); SetFont(view.SortText, view, 11);
            view.Title.GetComponent<CustomUITooltipTrigger>().tooltipText = view.Title.text;
            view.Total.text = ShopSettings.Format("f19.total", WaterScannerUIManager.FormatAmount(view.TotalAmount));
            char arrow = sortByShare.Value ? '↓' : '↕';
            string indicator = view.SortText.font != null && view.SortText.font.HasCharacter(arrow, true, false)
                ? arrow.ToString() : sortByShare.Value ? "v" : "-";
            view.SortText.text = ShopSettings.Text("f19.share") + " " + indicator;
            view.SortRect.GetComponent<CustomUITooltipTrigger>().tooltipText = ShopSettings.Text(sortByShare.Value ? "f19.sort_share" : "f19.sort_native");
            float contentWidth = Mathf.Max(1, root.rect.width - 2 * padX);
            float summaryHeight = UpdateSummary(view, contentWidth);
            float titleLine = Mathf.Max(16 * 1.35f, view.Title.GetPreferredValues("Mg", 10000, 0).y) + 4;
            float totalLine = Mathf.Max(13 * 1.35f, view.Total.GetPreferredValues("Mg", 10000, 0).y) + 4;
            float header = titleLine + totalLine + 4;
            const float headerGap = 6, listHeader = 20;
            float listTop = header + headerGap + summaryHeight + 4;
            float viewportTop = listTop + listHeader;
            if (top - bottom - 2 * padY < viewportTop + 18) return false;
            view.Header.offsetMin = new Vector2(0, -header); view.Header.offsetMax = Vector2.zero;
            PlaceText(view.Title, 0, 1, 1, 1, 0, -titleLine, -30, 0);
            PlaceText(view.Total, 0, 1, 1, 1, 0, -titleLine - totalLine - 1, -30, -titleLine - 1);
            view.Summary.offsetMin = new Vector2(0, -(header + headerGap + summaryHeight));
            view.Summary.offsetMax = new Vector2(0, -(header + headerGap));
            view.ListHeader.offsetMin = new Vector2(0, -viewportTop); view.ListHeader.offsetMax = new Vector2(-7, -listTop);
            view.Viewport.offsetMax = new Vector2(-7, -viewportTop);
            ((RectTransform)view.Scrollbar.transform).offsetMax = new Vector2(0, -viewportTop);
            Color rule = view.Tested?.Text != null ? view.Tested.Text.color : view.NativeText.color;
            rule.a *= .38f; view.HeaderRule.GetComponent<Image>().color = rule; view.SummaryRule.GetComponent<Image>().color = rule;
            view.Scroller.scrollSensitivity = Mathf.Max(16, view.RowHeight);
            return true;
        }

        private void UpdateRows(View view)
        {
            if (view.Root == null || view.Content == null) return;
            if (!displayNames.Ready()) { view.Pending = true; return; }
            if (!Layout(view)) { Clear(view); return; }
            bool hover = PointerOver(view.Root);
            view.Hovered = hover;
            var measured = view.NativeRows.Select(row => new LiquidDisplayRules.Part
            {
                Id = row.Liquid.identifier, Amount = row.Amount, NativeIndex = Liquid.Liquids.IndexOf(row.Liquid)
            }).ToList();
            List<LiquidDisplayRules.Part> ordered = view.Ordering.Update(view.Sample, measured, pins, sortByShare.Value, hover);
            var activeIds = new HashSet<string>(ordered.Select(part => part.Id), StringComparer.Ordinal);
            foreach (string id in view.RowViews.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            {
                RowView old = view.RowViews[id]; view.RowViews.Remove(id);
                old.Rect.gameObject.SetActive(false); old.Rect.SetParent(null, false); Object.Destroy(old.Rect.gameObject);
            }
            float scroll = view.Content.anchoredPosition.y;
            float font = Mathf.Clamp(fontSize.Value, 8, 28);
            float available = Mathf.Max(1, view.Viewport.rect.width);
            SetFont(view.Total, view, font);
            float volumeWidth = 0, percentWidth = 0;
            foreach (LiquidDisplayRules.Part part in ordered)
            {
                if (!view.Seen.TryGetValue(part.Id, out Liquid liquid)) continue;
                var measuredRow = new LiquidComposition.Row { Liquid = liquid, Amount = part.Amount,
                    Fraction = view.TotalAmount > 0 ? (double)part.Amount / view.TotalAmount : 0 };
                volumeWidth = Mathf.Max(volumeWidth, view.Total.GetPreferredValues(measuredRow.Volume).x);
                percentWidth = Mathf.Max(percentWidth, view.Total.GetPreferredValues(measuredRow.Percent).x);
            }
            float lineHeight = Mathf.Max(font * 1.25f, view.Total.GetPreferredValues("Mg", 10000, 0).y) + 3;
            SetFont(view.Total, view, 13);
            const float pinWidth = 20;
            volumeWidth = Mathf.Max(42, volumeWidth + 7);
            percentWidth = Mathf.Max(42, percentWidth + 7);
            bool stacked = pinWidth + Mathf.Max(42, font * 3) + volumeWidth + percentWidth > available;
            if (hover && view.ModeReady && Mathf.Abs(view.ModeWidth - available) < .5f && Mathf.Approximately(view.ModeFont, font))
                stacked = view.Stacked;
            else { view.ModeReady = true; view.Stacked = stacked; view.ModeWidth = available; view.ModeFont = font; }
            bool separateNumbers = stacked && volumeWidth + percentWidth > available - pinWidth;
            int lines = separateNumbers ? 3 : stacked ? 2 : 1;
            float barSpace = showBars.Value ? 5 : 0;
            view.RowHeight = lineHeight * lines + barSpace + 2;
            view.Scroller.scrollSensitivity = Mathf.Max(16, view.RowHeight);
            float headingShare = Mathf.Clamp(percentWidth, 48, Mathf.Max(48, available * .3f));
            float headingVolume = Mathf.Clamp(volumeWidth, 56, Mathf.Max(56, available * .36f));
            PlaceText(view.NameHeading, 0, 0, 1, 1, pinWidth, 0, -(headingVolume + headingShare), 0);
            PlaceText(view.VolumeHeading, 1, 0, 1, 1, -(headingVolume + headingShare), 0, -headingShare, 0);
            view.SortRect.anchorMin = new Vector2(1, 0); view.SortRect.anchorMax = Vector2.one;
            view.SortRect.offsetMin = new Vector2(-headingShare, 0); view.SortRect.offsetMax = Vector2.zero;
            view.Rows.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                LiquidDisplayRules.Part part = ordered[i];
                if (!view.Seen.TryGetValue(part.Id, out Liquid liquid)) continue;
                var row = new LiquidComposition.Row { Liquid = liquid, Amount = part.Amount,
                    Fraction = view.TotalAmount > 0 ? (double)part.Amount / view.TotalAmount : 0 };
                view.Rows.Add(row);
                if (!view.RowViews.TryGetValue(part.Id, out RowView visual))
                { visual = CreateRow(view, part.Id); view.RowViews.Add(part.Id, visual); }
                visual.Rect.SetSiblingIndex(i);
                visual.Rect.offsetMin = new Vector2(0, -(i + 1) * view.RowHeight);
                visual.Rect.offsetMax = new Vector2(0, -i * view.RowHeight);
                string displayName = displayNames.Get(liquid);
                SetReadableFont(visual.Name, view, font, displayName); SetFont(visual.Volume, view, font);
                SetFont(visual.Percent, view, font);
                visual.Name.text = displayName; visual.Volume.text = row.Volume; visual.Percent.text = row.Percent;
                visual.Rect.GetComponent<CustomUITooltipTrigger>().tooltipText = displayName;
                visual.Pin.Pinned = pins.Contains(part.Id); visual.Pin.color = view.NativeText.color;
                visual.PinRect.gameObject.name = visual.Pin.Pinned ? "Button_★" : "Button_☆";
                RectTransform pinRect = visual.PinRect;
                pinRect.anchorMin = new Vector2(0, 1); pinRect.anchorMax = new Vector2(0, 1);
                pinRect.offsetMin = new Vector2(0, -lineHeight); pinRect.offsetMax = new Vector2(pinWidth, 0);
                visual.Track.anchorMin = new Vector2(0, 0); visual.Track.anchorMax = new Vector2(1, 0);
                visual.Track.offsetMin = new Vector2(pinWidth, 1); visual.Track.offsetMax = new Vector2(0, 4);
                if (stacked)
                {
                    float topLine = (barSpace + lineHeight * (lines - 1)) / view.RowHeight;
                    float numberTop = (barSpace + lineHeight * (lines - 1)) / view.RowHeight;
                    float numberBottom = (barSpace + lineHeight * (lines - 2)) / view.RowHeight;
                    PlaceText(visual.Name, 0, topLine, 1, 1, pinWidth + (showColors.Value ? 6 : 0), 0, 0, 0);
                    PlaceText(visual.Volume, 0, numberBottom, 1, numberTop, pinWidth, 0, separateNumbers ? 0 : -percentWidth, 0);
                    PlaceText(visual.Percent, 1, separateNumbers ? barSpace / view.RowHeight : numberBottom,
                        1, separateNumbers ? (barSpace + lineHeight) / view.RowHeight : numberTop,
                        -percentWidth, 0, 0, 0);
                    visual.Volume.alignment = TextAlignmentOptions.MidlineRight;
                }
                else
                {
                    PlaceText(visual.Name, 0, 0, 1, 1, pinWidth + (showColors.Value ? 6 : 0), barSpace, -(volumeWidth + percentWidth), 0);
                    PlaceText(visual.Volume, 1, 0, 1, 1, -(volumeWidth + percentWidth), barSpace, -percentWidth, 0);
                    PlaceText(visual.Percent, 1, 0, 1, 1, -percentWidth, barSpace, 0, 0);
                    visual.Volume.alignment = TextAlignmentOptions.MidlineRight;
                }
                visual.Track.gameObject.SetActive(showBars.Value);
                visual.Mark.gameObject.SetActive(showColors.Value);
                visual.Mark.anchorMin = new Vector2(0, 1); visual.Mark.anchorMax = new Vector2(0, 1);
                visual.Mark.offsetMin = new Vector2(pinWidth, -lineHeight + 5); visual.Mark.offsetMax = new Vector2(pinWidth + 3, -4);
                Color tint = ComponentColor(liquid);
                tint.a = .78f; visual.Fill.GetComponent<Image>().color = tint;
                visual.Mark.GetComponent<Image>().color = tint;
                float usable = Mathf.Max(1, view.Viewport.rect.width - 4);
                float fraction = row.Fraction <= 0 ? 0 : Mathf.Max(2 / usable, (float)row.Fraction);
                visual.Fill.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1);
            }
            view.Content.sizeDelta = new Vector2(0, ordered.Count * view.RowHeight);
            float maxScroll = Mathf.Max(0, view.Content.rect.height - view.Viewport.rect.height);
            view.Content.anchoredPosition = new Vector2(0, Mathf.Clamp(scroll, 0, maxScroll));
            if (view.Selected != null && !activeIds.Contains(view.Selected)) CloseDetail(view);
        }

        private static void PlaceText(TextMeshProUGUI text, float x0, float y0, float x1, float y1,
            float left, float bottom, float right, float top)
        {
            RectTransform rect = text.rectTransform;
            rect.anchorMin = new Vector2(x0, y0); rect.anchorMax = new Vector2(x1, y1);
            rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(right, top);
        }

        private static float UpdateSummary(View view, float width)
        {
            view.TestedRaw = view.Tested?.Text?.text ?? "";
            view.LabelRaw = view.Label?.Text?.text ?? "";
            SummaryText(view.TestedRaw, out string testedHeading, out string testedValue);
            SummaryText(view.LabelRaw, out string labeledHeading, out string labeledValue);
            CopySummary(view.TestedHeading, view.Tested?.Text, testedHeading, 11, false);
            CopySummary(view.LabelHeading, view.Label?.Text, labeledHeading, 11, false);
            CopySummary(view.TestedCopy, view.Tested?.Text, testedValue, 14, true);
            CopySummary(view.LabelCopy, view.Label?.Text, labeledValue, 14, true);
            float columnWidth = Mathf.Max(1, width * .5f - 6);
            float tested = view.TestedCopy.GetPreferredValues(view.TestedCopy.text, columnWidth, 0).y;
            float labeled = view.LabelCopy.GetPreferredValues(view.LabelCopy.text, columnWidth, 0).y;
            return 17 + Mathf.Clamp(Mathf.Max(tested, labeled), 17, 36) + 8;
        }

        private static void SummaryText(string raw, out string heading, out string value)
        {
            string[] lines = raw.Replace("\r", "").Split('\n');
            heading = lines.Length > 0 ? lines[0] : "";
            value = lines.Length > 1 ? string.Join(" ", lines.Skip(1)) : "";
        }

        private static void CopySummary(TextMeshProUGUI target, TextMeshProUGUI source, string value, float font, bool wrap)
        {
            target.text = value;
            if (source == null) return;
            target.font = source.font; target.fontSharedMaterial = source.fontSharedMaterial;
            target.color = source.color; target.richText = source.richText; target.fontStyle = source.fontStyle;
            target.alignment = TextAlignmentOptions.TopLeft; target.lineSpacing = source.lineSpacing;
            target.fontSize = font; target.enableAutoSizing = false; target.enableWordWrapping = wrap;
            target.overflowMode = TextOverflowModes.Ellipsis;
        }

        private RowView CreateRow(View view, string id)
        {
            RectTransform entry = Box(view.Content, "Component_" + id, Color.clear, 0, 1, 1, 1, 0, 0, 0, 0);
            entry.GetComponent<Image>().raycastTarget = true;
            entry.gameObject.AddComponent<CustomUITooltipTrigger>();
            Button select = entry.gameObject.AddComponent<Button>(); select.targetGraphic = entry.GetComponent<Image>();
            select.onClick.AddListener(() =>
            {
                try { OpenDetail(view, id); }
                catch (Exception error) { CloseDetail(view); log("F19 could not open component details: " + error); ShopNotice.Show(ShopSettings.Text("f19.detail_failed")); }
            });
            var row = new RowView { Rect = entry };
            row.Mark = Box(entry, "ColorMark", Color.clear, 0, 0, 0, 1, 1, 6, 5, -3);
            row.Name = Text(entry, view, "", TextAlignmentOptions.MidlineLeft, 2, 5, -3, 0, 0, .43f);
            row.Name.gameObject.name = "Name";
            row.Volume = Text(entry, view, "", TextAlignmentOptions.MidlineRight, 0, 5, -3, 0, .43f, .70f);
            row.Volume.gameObject.name = "Volume";
            row.Percent = Text(entry, view, "", TextAlignmentOptions.MidlineRight, 0, 5, -3, 0, .70f, .91f);
            row.Percent.gameObject.name = "Share";
            RectTransform pin = Box(entry, "Button_☆", Color.clear, 0, 0, 0, 1, 0, 0, 20, 0);
            pin.GetComponent<Image>().raycastTarget = true;
            Button button = pin.gameObject.AddComponent<Button>(); button.targetGraphic = pin.GetComponent<Image>();
            button.onClick.AddListener(() => TogglePin(id));
            row.PinRect = pin;
            var pinIcon = new GameObject("PinIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(LiquidPinIcon));
            RectTransform pinIconRect = (RectTransform)pinIcon.transform; pinIconRect.SetParent(pin, false);
            pinIconRect.anchorMin = Vector2.zero; pinIconRect.anchorMax = Vector2.one;
            pinIconRect.offsetMin = pinIconRect.offsetMax = Vector2.zero;
            row.Pin = pinIcon.GetComponent<LiquidPinIcon>(); row.Pin.raycastTarget = false; row.Pin.color = view.NativeText.color;
            row.Track = Box(entry, "Track", new Color(1, 1, 1, .16f), 0, 0, .91f, 0, 2, 1, -3, 4);
            row.Fill = Box(row.Track, "LiquidBar", Color.white, 0, 0, 0, 1, 0, 0, 0, 0);
            return row;
        }

        private static Color ComponentColor(Liquid liquid)
        {
            string value = liquid?.color;
            if (!string.IsNullOrWhiteSpace(value) && ColorUtility.TryParseHtmlString(value.StartsWith("#", StringComparison.Ordinal) ? value : "#" + value, out Color native))
                return Color.Lerp(native, new Color(.88f, .88f, .84f), .3f);
            return new Color(.48f, .78f, .75f);
        }

        private void OpenDisplaySettings(View view)
        {
            if (!StillOpen(view) || CustomUIManager.Instance == null) return;
            if (view.DisplaySettings?.IsAlive == true) { CloseDisplaySettings(view); return; }
            CloseDetail(view);
            int draftSize = fontSize.Value; bool draftBars = showBars.Value, draftColors = showColors.Value;
            Vector2 size = UiLayout.Fit(320, 270);
            var builder = CustomUIManager.Instance.CreateWindow(ModEntry.Id + (view.Chemical ? ".f19.display.chem" : ".f19.display.water"),
                ShopSettings.Text("f19.display"), CustomUIManager.LAYER_META, null, CustomUIManager.Style.Machinery)
                .SetSize(size.x, size.y).SetDraggable(false).SetCloseOnEscape(true);
            view.DisplaySettings = builder.GetWindow(); CustomUIWindow own = view.DisplaySettings;
            builder.OnClose(() => { if (ReferenceEquals(view.DisplaySettings, own)) view.DisplaySettings = null; });
            builder.AddLabel(ShopSettings.Text("f19.size") + "  " + draftSize, "size_label");
            builder.AddSlider(8, 28, draftSize, value =>
            {
                draftSize = Mathf.RoundToInt(value);
                own.Get("size_label")?.SetText(ShopSettings.Text("f19.size") + "  " + draftSize);
            }, wholeNumbers: true, id: "size");
            builder.AddToggle(ShopSettings.Text("f19.bars"), draftBars, value => draftBars = value, "bars");
            builder.AddToggle(ShopSettings.Text("f19.colors"), draftColors, value => draftColors = value, "colors");
            builder.AddButton(ShopSettings.Text("f19.display_apply"), () =>
            {
                if (!StillOpen(view) || !ReferenceEquals(view.DisplaySettings, own)) return;
                int oldSize = fontSize.Value; bool oldBars = showBars.Value, oldColors = showColors.Value;
                try
                {
                    fontSize.Set(draftSize); showBars.Set(draftBars); showColors.Set(draftColors); config.Save();
                    ReapplySafely(water); ReapplySafely(chem); CloseDisplaySettings(view);
                }
                catch (Exception error)
                {
                    try { fontSize.Set(oldSize); showBars.Set(oldBars); showColors.Set(oldColors); config.Save(); }
                    catch (Exception rollback) { log("F19 could not restore display settings: " + rollback); }
                    log("F19 could not save display settings: " + error);
                    ShopNotice.Show(ShopSettings.Text("f19.display_failed"));
                }
            }, "apply").Show();
            PositionWindow(view, own, size);
        }

        private void CloseDisplaySettings(View view)
        {
            CustomUIWindow previous = view.DisplaySettings; view.DisplaySettings = null; previous?.Close();
        }

        private void OpenDetail(View view, string id)
        {
            LiquidComposition.Row row = view.Rows.Find(part => part.Liquid.identifier == id);
            if (row == null || !StillOpen(view) || CustomUIManager.Instance == null) return;
            if (!displayNames.Ready()) { view.Pending = true; view.PendingDetail = id; return; }
            CloseDisplaySettings(view);
            CloseDetail(view); view.Selected = id;
            Vector2 size = UiLayout.Fit(380, 420);
            var builder = CustomUIManager.Instance.CreateWindow(ModEntry.Id + (view.Chemical ? ".f19.detail.chem" : ".f19.detail.water"),
                ShopSettings.Format("f19.methods", displayNames.Get(row.Liquid)), CustomUIManager.LAYER_META, null, CustomUIManager.Style.Machinery)
                .SetSize(size.x, size.y).SetDraggable(false).SetCloseOnEscape(true);
            view.Detail = builder.GetWindow(); CustomUIWindow own = view.Detail;
            builder.OnClose(() => { if (ReferenceEquals(view.Detail, own)) { view.Detail = null; view.DetailBody = null; view.DetailSummary = null; view.Selected = null; } });
            builder.AddLabel("", "summary"); view.DetailSummary = builder.Last;
            builder.BeginScrollFlexible(120); view.DetailBody = builder.CurrentContainer; builder.End().Show();
            PositionWindow(view, view.Detail, size);
            view.MethodSignature = null;
            UpdateDetail(view);
        }

        private void UpdateDetail(View view)
        {
            if (view.Detail?.IsAlive != true || view.Selected == null || view.DetailBody == null) return;
            if (!displayNames.Ready()) { view.Pending = true; return; }
            LiquidComposition.Row row = view.Rows.Find(part => part.Liquid.identifier == view.Selected);
            if (row == null || !StillOpen(view)) { CloseDetail(view); return; }
            view.Detail.SetTitle(ShopSettings.Format("f19.methods", displayNames.Get(row.Liquid)));
            view.DetailSummary?.SetText(row.Volume + " · " + row.Percent);
            List<LiquidKnowledge.Method> methods = LiquidKnowledge.For(row.Liquid, view.Sample);
            string signature = string.Join("\n", methods.Select(method => method.Name + "\u001f" + method.Summary + "\u001f" + method.Description + "\u001f" + method.Atlas + "\u001f" + method.Sprite));
            if (signature == view.MethodSignature) return;
            bool firstDisplay = view.MethodSignature == null;
            view.MethodSignature = signature;
            ScrollRect scroll = view.DetailBody.GetComponentInParent<ScrollRect>();
            float position = !firstDisplay && scroll != null ? scroll.verticalNormalizedPosition : 1;
            UiLayout.Clear(view.DetailBody, view.Detail);
            var builder = new CustomUIBuilder(CustomUIManager.Instance, view.Detail, view.DetailBody);
            if (methods.Count == 0) builder.AddLabel(ShopSettings.Text("f19.unknown"));
            foreach (LiquidKnowledge.Method method in methods)
            {
                builder.BeginPanel(0, 0, spacing: 4, padding: 8); builder.CurrentContainer.name = "Method_" + method.Sprite;
                Sprite icon = RenderHandler.LoadFromAtlas(method.Atlas, method.Sprite);
                builder.BeginRow(6); if (icon != null) builder.AddImage(icon, 28, 28);
                builder.AddLabel(method.Name); builder.Last.SetFlexible(1, 0);
                builder.AddLabel(ShopSettings.Text("f19.applies")); builder.Last.SetFixedWidth(76); builder.End();
                DetailParagraph(builder, method.Summary, view.Detail.Rect.rect.width - 66);
                bool expanded = view.ExpandedMethod == method.Sprite;
                builder.BeginRow().AddFlexibleSpace();
                builder.AddButton(ShopSettings.Text(expanded ? "f19.less" : "f19.more"), () =>
                {
                    if (view.Detail?.IsAlive != true) return;
                    view.ExpandedMethod = expanded ? null : method.Sprite;
                    view.MethodSignature = "";
                    UpdateDetail(view);
                }, "method_more_" + method.Sprite);
                builder.Last.SetFixedWidth(110); builder.End();
                if (expanded) DetailParagraph(builder, method.Description, view.Detail.Rect.rect.width - 66);
                builder.End();
            }
            CustomUIBuilder.RebuildLayout(view.DetailBody);
            UiLayout.RestoreScroll(view.DetailBody, position);
        }

        private static void DetailParagraph(CustomUIBuilder builder, string value, float width)
        {
            builder.AddLabel(value);
            if (builder.Last.text == null) return;
            builder.Last.text.richText = false; builder.Last.text.enableWordWrapping = true;
            float height = builder.Last.text.GetPreferredValues(value, Mathf.Max(100, width), 0).y + 8;
            builder.Last.SetPreferredSize(-1, Mathf.Max(28, height));
        }

        private static void PositionWindow(View view, CustomUIWindow window, Vector2 size)
        {
            if (!(window?.root?.transform is RectTransform rect) || !(rect.parent is RectTransform parent) ||
                !(view.Panel.transform is RectTransform scanner)) return;
            Canvas canvas = parent.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Canvas sourceCanvas = scanner.GetComponentInParent<Canvas>();
            Camera sourceCamera = sourceCanvas != null && sourceCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? sourceCanvas.worldCamera : null;
            scanner.GetWorldCorners(windowCorners);
            Vector2 left = RectTransformUtility.WorldToScreenPoint(sourceCamera, windowCorners[1]);
            Vector2 right = RectTransformUtility.WorldToScreenPoint(sourceCamera, windowCorners[2]);
            float scale = canvas != null && canvas.scaleFactor > 0 ? canvas.scaleFactor : 1;
            float width = size.x * scale;
            float areaLeft = LetterboxManager.GameAreaWidth > 0 ? LetterboxManager.GameAreaX : 0;
            float areaBottom = LetterboxManager.GameAreaHeight > 0 ? LetterboxManager.GameAreaY : 0;
            float areaRight = LetterboxManager.GameAreaWidth > 0 ? areaLeft + LetterboxManager.GameAreaWidth : Screen.width;
            float areaTop = LetterboxManager.GameAreaHeight > 0 ? areaBottom + LetterboxManager.GameAreaHeight : Screen.height;
            bool onRight = areaRight - right.x >= width + 6 || left.x - areaLeft < width + 6 && areaRight - right.x >= left.x - areaLeft;
            Vector2 point = onRight ? right + new Vector2(4, 0) : left + new Vector2(-width - 4, 0);
            point.x = Mathf.Clamp(point.x, areaLeft + 2, Mathf.Max(areaLeft + 2, areaRight - width - 2));
            point.y = Mathf.Clamp(point.y, Mathf.Min(areaTop - 2, areaBottom + size.y * scale + 2), areaTop - 2);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, point, camera, out Vector2 local)) return;
            if (rect.anchorMin != parent.pivot) rect.anchorMin = parent.pivot;
            if (rect.anchorMax != parent.pivot) rect.anchorMax = parent.pivot;
            Vector2 pivot = new Vector2(0, 1);
            if (rect.pivot != pivot) rect.pivot = pivot;
            if (rect.anchoredPosition != local) rect.anchoredPosition = local;
        }

        private static RectTransform Box(RectTransform parent, string name, Color color, float ax0, float ay0, float ax1, float ay1,
            float left, float bottom, float right, float top)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform; rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(ax0, ay0); rect.anchorMax = new Vector2(ax1, ay1);
            rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(right, top);
            Image image = go.GetComponent<Image>(); image.sprite = null; image.color = color; image.raycastTarget = false;
            return rect;
        }

        private static TextMeshProUGUI Text(RectTransform parent, View view, string value, TextAlignmentOptions alignment,
            float left, float bottom, float right, float top, float ax0 = 0, float ax1 = 1)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            var text = go.GetComponent<TextMeshProUGUI>(); RectTransform rect = text.rectTransform; rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(ax0, 0); rect.anchorMax = new Vector2(ax1, 1);
            rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(right, top);
            text.text = value; text.richText = false; text.raycastTarget = false; text.alignment = alignment;
            text.enableAutoSizing = false; text.enableWordWrapping = false; text.overflowMode = TextOverflowModes.Ellipsis;
            SetFont(text, view, 14);
            return text;
        }

        private static void SetFont(TextMeshProUGUI text, View view, float size)
        {
            if (text == null || view.NativeText == null) return;
            text.font = view.NativeText.font; text.fontSharedMaterial = view.NativeText.fontSharedMaterial;
            text.color = view.NativeText.color; text.enableAutoSizing = false; text.fontSize = size;
        }

        private static void SetReadableFont(TextMeshProUGUI text, View view, float size, string value)
        {
            SetFont(text, view, size);
            if (text == null || text.font == null || string.IsNullOrEmpty(value) ||
                text.font.HasCharacters(value, out _, true, true)) return;
            FontLoader fonts = FontLoader.Instance;
            if (fonts == null) return;
            foreach (TMP_FontAsset candidate in new[] { fonts.simplifiedChineseFont, fonts.japaneseFont, fonts.koreanFont })
                if (candidate != null && candidate.HasCharacters(value, out _, true, true))
                { text.font = candidate; text.fontSharedMaterial = candidate.material; return; }
        }

        private static bool PointerOver(GameObject go)
        {
            if (go == null || !go.activeInHierarchy || !(go.transform is RectTransform rect)) return false;
            Canvas canvas = go.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            return RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, camera);
        }

        private void CloseDetail(View view)
        {
            CustomUIWindow previous = view.Detail; view.Detail = null; view.DetailBody = null; view.DetailSummary = null;
            view.Selected = null; view.MethodSignature = null; view.ExpandedMethod = null; previous?.Close();
        }

        private void Clear(View view)
        {
            CloseDetail(view);
            CloseDisplaySettings(view);
            if (view.NativeText != null && view.Root != null && !view.NativeText.enabled) view.NativeText.enabled = view.NativeEnabled;
            view.Tested?.Restore(); view.Label?.Restore(); view.TotalText?.Restore(); view.Product?.Restore();
            if (view.Root != null) { view.Root.SetActive(false); Object.Destroy(view.Root); }
            view.Root = view.Panel = null; view.Sample = null; view.NativeText = view.Title = view.Total = view.SortText = null;
            view.TestedCopy = view.LabelCopy = view.TestedHeading = view.LabelHeading = view.NameHeading = view.VolumeHeading = null;
            view.Scroller = null; view.Scrollbar = null;
            view.Summary = view.Content = view.Viewport = view.LayoutRoot = view.Header = view.ListHeader = view.SortRect = view.HeaderRule = view.SummaryRule = null;
            view.ScreenCenter = view.ScreenLeft = view.ScreenRight = null;
            view.Match = view.Confront = view.Sort = view.Gear = null; view.Raw = view.TestedRaw = view.LabelRaw = null;
            view.Locale = view.PendingDetail = null; view.Font = null; view.Pending = false; view.NextCheck = 0f;
            view.TotalAmount = 0; view.Hovered = view.ModeReady = false;
            view.Rows.Clear(); view.NativeRows.Clear(); view.RowViews.Clear(); view.Seen.Clear(); view.Ordering.Reset();
            view.Tested = view.Label = view.TotalText = view.Product = null;
        }

        internal void Reset() { ClosePicker(); Clear(water); Clear(chem); }
        public void Dispose() { Reset(); displayNames.Reset(); if (ReferenceEquals(active, this)) active = null; started = false; }

        [HarmonyPatch(typeof(WaterScannerUIManager), nameof(WaterScannerUIManager.RefreshUI))]
        private static class WaterRefresh
        {
            private static void Postfix(WaterScannerUIManager __instance) => active?.Capture(active.water, __instance.panel, __instance.currentWaterContainer,
                __instance.contaminantTMP, __instance.testedTMP, __instance.labelTMP, null, null, __instance.matchButton, __instance.confrontButton, false);
        }
        [HarmonyPatch(typeof(ChemScannerUIManager), nameof(ChemScannerUIManager.RefreshUI))]
        private static class ChemRefresh
        {
            private static void Postfix(ChemScannerUIManager __instance) => active?.Capture(active.chem, __instance.panel, __instance.currentScannedItem,
                __instance.contaminantTMP, __instance.testedTMP, __instance.labelTMP, __instance.totalVolumeTMP, __instance.productNameTMP, __instance.matchButton, __instance.confrontButton, true);
        }
        [HarmonyPatch(typeof(WaterScannerUIManager), nameof(WaterScannerUIManager.Close))]
        private static class WaterClose { private static void Postfix() { if (active != null) active.Clear(active.water); } }
        [HarmonyPatch(typeof(ChemScannerUIManager), nameof(ChemScannerUIManager.Close))]
        private static class ChemClose { private static void Postfix() { if (active != null) active.Clear(active.chem); } }
    }
}
