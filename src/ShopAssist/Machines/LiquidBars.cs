using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    // The native scanner owns detection and its original text. This view is
    // built only after the rendered result matches the current sample.
    internal sealed class LiquidBars : IDisposable
    {
        private sealed class View
        {
            internal GameObject Panel, Root;
            internal GameItem Sample;
            internal TextMeshProUGUI NativeText;
            internal TextState Tested, Label, TotalText;
            internal bool Chemical;
            internal string Raw, Selected, MethodSignature;
            internal bool NativeEnabled;
            internal int Total;
            internal float Width, Height, PanelWidth, PanelHeight, Scroll = 1;
            internal readonly List<LiquidComposition.Row> Rows = new List<LiquidComposition.Row>();
            internal readonly List<string> Order = new List<string>();
            internal ScrollRect Scroller;
        }
        private sealed class TextState
        {
            internal TextMeshProUGUI Text;
            internal Vector2 AnchorMin, AnchorMax, Pivot, Position, Size, AppliedPosition, AppliedSize;
            internal float Font, FontMin, FontMax, AppliedFont, AppliedMin, AppliedMax;
            internal bool Auto, Enabled, AppliedEnabled, Applied;

            internal TextState(TextMeshProUGUI text)
            {
                Text = text;
                if (text == null) return;
                var rect = text.rectTransform;
                AnchorMin = rect.anchorMin; AnchorMax = rect.anchorMax; Pivot = rect.pivot;
                Position = rect.anchoredPosition; Size = rect.sizeDelta;
                Font = text.fontSize; FontMin = text.fontSizeMin; FontMax = text.fontSizeMax;
                Auto = text.enableAutoSizing; Enabled = text.enabled;
            }
            internal void Place(Vector2 position, Vector2 size, float font, bool show = true)
            {
                if (Text == null) return;
                Applied = true;
                var rect = Text.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
                rect.anchoredPosition = AppliedPosition = position;
                rect.sizeDelta = AppliedSize = size;
                Text.enableAutoSizing = true;
                Text.fontSizeMin = AppliedMin = 8;
                Text.fontSizeMax = AppliedMax = font;
                Text.fontSize = AppliedFont = font;
                Text.enabled = AppliedEnabled = show;
            }
            internal bool Owned()
            {
                if (Text == null || !Applied) return true;
                var rect = Text.rectTransform;
                return rect.anchorMin == new Vector2(.5f, .5f) && rect.anchorMax == new Vector2(.5f, .5f) &&
                    rect.pivot == new Vector2(.5f, .5f) && rect.anchoredPosition == AppliedPosition && rect.sizeDelta == AppliedSize &&
                    Text.enableAutoSizing && Mathf.Approximately(Text.fontSizeMin, AppliedMin) &&
                    Mathf.Approximately(Text.fontSizeMax, AppliedMax) && Text.enabled == AppliedEnabled;
            }
            internal void Restore()
            {
                if (Text == null || !Applied) return;
                var rect = Text.rectTransform;
                bool fontOwned = Text.enableAutoSizing && Mathf.Approximately(Text.fontSizeMin, AppliedMin) &&
                    Mathf.Approximately(Text.fontSizeMax, AppliedMax);
                // Restore only values still owned by this layout.
                if (rect.anchorMin == new Vector2(.5f, .5f)) rect.anchorMin = AnchorMin;
                if (rect.anchorMax == new Vector2(.5f, .5f)) rect.anchorMax = AnchorMax;
                if (rect.pivot == new Vector2(.5f, .5f)) rect.pivot = Pivot;
                if (rect.anchoredPosition == AppliedPosition) rect.anchoredPosition = Position;
                if (rect.sizeDelta == AppliedSize) rect.sizeDelta = Size;
                if (Text.enableAutoSizing) Text.enableAutoSizing = Auto;
                if (Mathf.Approximately(Text.fontSizeMin, AppliedMin)) Text.fontSizeMin = FontMin;
                if (Mathf.Approximately(Text.fontSizeMax, AppliedMax)) Text.fontSizeMax = FontMax;
                if (fontOwned) Text.fontSize = Font;
                if (Text.enabled == AppliedEnabled) Text.enabled = Enabled;
            }
        }
        private static LiquidBars active;
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<int> fontSize;
        private readonly ConfigEntry<string> pinned;
        private readonly Action<string> log;
        private readonly View water = new View(), chem = new View();
        private CustomUIWindow picker;
        private bool started;
        private static readonly Color PanelColor = new Color(.055f, .095f, .105f, .96f);
        private static readonly Color RowColor = new Color(.11f, .17f, .17f, .94f);
        private static readonly Color Accent = new Color(.34f, .78f, .69f, 1f);
        private static readonly Color TextColor = new Color(.94f, .96f, .89f, 1f);

        internal LiquidBars(ShopSettings settings, Action<string> log)
        {
            this.log = log;
            enabled = settings.Toggle("f19.enabled", "f19.tip");
            fontSize = settings.Integer("f19.size", "f19.size.tip", 14, 8, 28);
            pinned = ModHelper.GetConfig(ModEntry.Id).Define("f19.pinned", "", enabled.Section,
                () => ShopSettings.Text("f19.pinned"), tooltip: () => ShopSettings.Text("f19.pinned.tip"));
            settings.Action("f19.choose", OpenPicker, CanChoose);
        }
        internal void Start()
        {
            if (started) return;
            if (active != null && !ReferenceEquals(active, this)) throw new InvalidOperationException("Liquid scanner layout already active.");
            active = this; started = true;
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
            if (view.Root == null) return;
            // A later writer owns its new text. Do not draw stale results over it.
            if (!StillOpen(view) || view.NativeText.text != view.Raw || view.NativeText.enabled) { Clear(view); return; }
            if (view.Tested?.Owned() == false || view.Label?.Owned() == false || view.TotalText?.Owned() == false)
            { Clear(view); return; }
            Rect rect = ((RectTransform)view.Panel.transform).rect;
            if (Mathf.Abs(view.PanelWidth - rect.width) > .5f || Mathf.Abs(view.PanelHeight - rect.height) > .5f)
                Render(view);
        }
        internal void SettingsChanged()
        {
            if (!started) return;
            if (!enabled.Value) { Reset(); return; }
            ReapplySafely(water); ReapplySafely(chem);
        }
        private static bool StillOpen(View view) => view.Panel != null && view.Panel.activeInHierarchy && view.Sample != null && view.NativeText != null;
        private bool CanChoose() => enabled.Value && (StillOpen(chem) && chem.Rows.Count > 0 || StillOpen(water) && water.Rows.Count > 0);
        private void OpenPicker()
        {
            if (!CanChoose() || CustomUIManager.Instance == null) return;
            bool chooseChem = StillOpen(chem) && chem.Rows.Count > 0 &&
                (!StillOpen(water) || water.Rows.Count == 0 || PaperPanelFocusStamp.GetStamp(chem.Panel) >= PaperPanelFocusStamp.GetStamp(water.Panel));
            View source = chooseChem ? chem : water;
            picker?.Close();
            var builder = CustomUIManager.Window(ModEntry.Id + ".f19.pin", ShopSettings.Text("f19.choose"), CustomUIManager.LAYER_META)
                .SetSize(360, 340).SetDraggable(true).SetCloseOnEscape(true);
            picker = builder.GetWindow(); CustomUIWindow own = picker;
            builder.OnClose(() => { if (ReferenceEquals(picker, own)) picker = null; });
            builder.BeginScrollFlexible();
            builder.AddButton(ShopSettings.Text("f19.none"), () => SetPin(""));
            foreach (LiquidComposition.Row row in source.Rows)
            {
                string id = row.Liquid.identifier;
                builder.AddButton(row.Liquid.displayName, () => SetPin(id));
                if (builder.Last.text != null) builder.Last.text.richText = false;
            }
            builder.End().Show();
        }
        private void SetPin(string id)
        {
            pinned.Set(id); ClosePicker(); ReapplySafely(water); ReapplySafely(chem);
        }
        private void ClosePicker() { picker?.Close(); picker = null; }
        private void Capture(View view, GameObject panel, GameItem sample, TextMeshProUGUI text,
            TextMeshProUGUI tested, TextMeshProUGUI label, TextMeshProUGUI totalText, bool chemical)
        {
            if (!started || !enabled.Value || panel == null || !panel.activeInHierarchy || sample == null || text == null)
            { Clear(view); return; }
            try
            {
                string raw = text.text ?? "";
                if (!LiquidComposition.TryRead(sample, raw, out List<LiquidComposition.Row> rows, out int total))
                { Clear(view); return; }
                bool same = ReferenceEquals(view.Sample, sample) && view.NativeText == text && view.Panel == panel;
                bool changed = !same || view.Raw != raw;
                if (!same)
                {
                    Clear(view); view.Panel = panel; view.Sample = sample; view.NativeText = text; view.NativeEnabled = text.enabled;
                    view.Tested = new TextState(tested); view.Label = new TextState(label);
                    view.TotalText = totalText == null ? null : new TextState(totalText); view.Chemical = chemical;
                }
                view.Raw = raw; view.Total = total;
                if (changed || view.Root == null)
                {
                    bool hover = PointerOver(view.Root);
                    if (!hover) view.Order.Clear();
                    view.Order.RemoveAll(id => !rows.Any(row => row.Liquid.identifier == id));
                    if (!hover)
                    {
                        LiquidComposition.Row pin = rows.Find(row => row.Liquid.identifier == pinned.Value || row.Liquid.displayName == pinned.Value);
                        if (pin != null) view.Order.Add(pin.Liquid.identifier);
                    }
                    foreach (var row in rows) if (!view.Order.Contains(row.Liquid.identifier)) view.Order.Add(row.Liquid.identifier);
                    view.Rows.Clear();
                    foreach (string id in view.Order) view.Rows.Add(rows.Find(row => row.Liquid.identifier == id));
                    if (view.Selected != null && !view.Rows.Any(row => row.Liquid.identifier == view.Selected)) view.Selected = null;
                    Render(view);
                }
                else if (view.Selected != null)
                {
                    var selected = view.Rows.Find(row => row.Liquid.identifier == view.Selected);
                    if (selected != null)
                    {
                        List<LiquidKnowledge.Method> methods = LiquidKnowledge.For(selected.Liquid, sample);
                        if (MethodSignature(methods) != view.MethodSignature) Render(view, methods);
                    }
                }
            }
            catch (Exception error) { Clear(view); log("F19 could not display scanner output: " + error); }
        }
        private void Reapply(View view)
        {
            if (!StillOpen(view) || view.Raw == null || view.NativeText.text != view.Raw) return;
            view.Order.Clear();
            LiquidComposition.Row pin = view.Rows.Find(row => row.Liquid.identifier == pinned.Value || row.Liquid.displayName == pinned.Value);
            if (pin != null) view.Order.Add(pin.Liquid.identifier);
            foreach (var row in view.Rows) if (!view.Order.Contains(row.Liquid.identifier)) view.Order.Add(row.Liquid.identifier);
            var sorted = view.Order.Select(id => view.Rows.Find(row => row.Liquid.identifier == id)).ToArray();
            view.Rows.Clear(); view.Rows.AddRange(sorted);
            Render(view);
        }
        private void ReapplySafely(View view)
        {
            try { Reapply(view); }
            catch (Exception error) { Clear(view); log("F19 could not update scanner settings: " + error); }
        }
        private static bool PointerOver(GameObject go)
        {
            if (go == null || !go.activeInHierarchy || !(go.transform is RectTransform rect)) return false;
            Canvas canvas = go.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            return RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, camera);
        }

        private void Render(View view, List<LiquidKnowledge.Method> methods = null)
        {
            if (!StillOpen(view) || view.NativeText.text != view.Raw || view.NativeText.rectTransform.parent == null) return;
            if (view.Scroller != null) view.Scroll = view.Scroller.verticalNormalizedPosition;
            if (view.Root != null) { view.Root.SetActive(false); UnityEngine.Object.Destroy(view.Root); }
            RectTransform source = view.NativeText.rectTransform;
            view.Root = new GameObject("ShopAssist_LiquidBars", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var root = (RectTransform)view.Root.transform;
            root.SetParent(source.parent, false);
            var panel = view.Panel.transform as RectTransform;
            if (panel == null || source.parent != panel) { Clear(view); return; }
            Rect bounds = panel.rect; view.PanelWidth = bounds.width; view.PanelHeight = bounds.height;
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(.5f, .5f);
            root.anchoredPosition = new Vector2(0, bounds.height * (view.Chemical ? -.049f : -.111f));
            root.sizeDelta = new Vector2(bounds.width * .70f, bounds.height * (view.Chemical ? .228f : .325f));
            root.localScale = source.localScale; root.localRotation = source.localRotation;
            root.SetAsLastSibling();
            Image bg = view.Root.GetComponent<Image>(); bg.sprite = null; bg.color = PanelColor; bg.raycastTarget = true;
            view.Width = root.rect.width; view.Height = root.rect.height;
            view.NativeText.enabled = false;
            float scale = Mathf.Clamp(fontSize.Value / 14f, .72f, 1.75f);
            float width = bounds.width * .68f;
            view.Tested?.Place(new Vector2(0, bounds.height * .171f), new Vector2(width, bounds.height * .113f), Mathf.Min(22, 17 * scale));
            view.Label?.Place(new Vector2(0, bounds.height * .087f), new Vector2(width, bounds.height * .060f), Mathf.Min(18, 14 * scale));
            if (view.TotalText != null) view.TotalText.Place(view.TotalText.Position, view.TotalText.Size, view.TotalText.Font, false);
            float header = Mathf.Max(26f, 27f * scale);
            RectTransform top = Box(root, "Header", new Color(.09f, .22f, .21f, 1f), 0, 1, 1, 1, 0, -header, 0, 0);
            if (view.Selected == null)
            {
                Text(top, view, ShopSettings.Format("f19.total", WaterScannerUIManager.FormatAmount(view.Total)),
                    TextAlignmentOptions.MidlineLeft, Mathf.Max(11, 13 * scale), 4, 0, -4, 0);
            }
            else
            {
                LiquidComposition.Row selected = view.Rows.Find(row => row.Liquid.identifier == view.Selected);
                if (selected == null) { view.Selected = null; Render(view); return; }
                Text(top, view, ShopSettings.Format("f19.methods", selected.Liquid.displayName),
                    TextAlignmentOptions.MidlineLeft, Mathf.Max(10, 12 * scale), 4, 0, -56, 0);
                MakeButton(top, view, ShopSettings.Text("f19.back"), () => { view.Selected = null; view.Scroll = 1; Render(view); },
                    1, 0, 1, 1, -54, 2, -2, -2, 10 * scale);
            }
            float columns = view.Selected == null ? Mathf.Max(13f, 15f * scale) : 0;
            if (columns > 0)
            {
                RectTransform headings = Box(root, "Columns", PanelColor, 0, 1, 1, 1, 2, -header - columns, -2, -header);
                Text(headings, view, ShopSettings.Text("f19.component"), TextAlignmentOptions.MidlineLeft, 9 * scale, 4, 0, 0, 0, 0, .38f, fit: true);
                Text(headings, view, ShopSettings.Text("f19.volume"), TextAlignmentOptions.MidlineRight, 9 * scale, 0, 0, -3, 0, .38f, .63f, fit: true);
                Text(headings, view, ShopSettings.Text("f19.share"), TextAlignmentOptions.MidlineRight, 9 * scale, 0, 0, -3, 0, .63f, .79f, fit: true);
            }
            RectTransform viewport = Box(root, "Viewport", Color.clear, 0, 0, 1, 1, 2, 2, -2, -header - columns - 2);
            viewport.GetComponent<Image>().raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();
            RectTransform content = Box(viewport, "Content", Color.clear, 0, 1, 1, 1, 0, 0, 0, 0);
            content.pivot = new Vector2(.5f, 1f); content.anchoredPosition = Vector2.zero;
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = Mathf.Max(15, 22 * scale);
            view.Scroller = scroll;
            if (view.Selected == null) DrawRows(view, content, scale);
            else DrawMethods(view, content, scale, methods);
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = Mathf.Clamp01(view.Scroll);
        }
        private void DrawRows(View view, RectTransform content, float scale)
        {
            float rowHeight = Mathf.Max(28, 31 * scale), top = 0;
            foreach (LiquidComposition.Row row in view.Rows)
            {
                var entry = Box(content, "Component_" + row.Liquid.identifier, RowColor, 0, 1, 1, 1, 0, -top - rowHeight, 0, -top - 1);
                string id = row.Liquid.identifier;
                bool isPin = pinned.Value == id || pinned.Value == row.Liquid.displayName;
                Text(entry, view, row.Liquid.displayName, TextAlignmentOptions.MidlineLeft, 11.5f * scale, 4, 5, -4, -2, 0, .38f, wrap: true, fit: true);
                Text(entry, view, row.Volume, TextAlignmentOptions.MidlineRight, 10.5f * scale, 0, 5, -3, -2, .38f, .63f, fit: true);
                Text(entry, view, row.Percent, TextAlignmentOptions.MidlineRight, 10.5f * scale, 0, 5, -3, -2, .63f, .79f, fit: true);
                MakeButton(entry, view, ShopSettings.Text("f19.how"), () => { view.Selected = id; view.Scroll = 1; Render(view); },
                    .79f, 0, .92f, 1, 1, 5, -1, -3, 10 * scale);
                MakeButton(entry, view, isPin ? "★" : "☆", () => SetPin(isPin ? "" : id),
                    .92f, 0, 1, 1, 1, 5, -2, -3, 11 * scale);
                RectTransform track = Box(entry, "Track", new Color(.12f, .30f, .29f, 1), 0, 0, .79f, 0, 4, 1, -3, 4);
                float usable = Mathf.Max(0, view.Width * .79f - 8f);
                float fraction = usable <= 0 ? 0 : Mathf.Max(2f / usable, (float)row.Fraction);
                Box(track, "LiquidBar", Accent, 0, 0, Mathf.Clamp01(fraction), 1, 0, 0, 0, 0);
                top += rowHeight;
            }
            content.sizeDelta = new Vector2(0, top);
        }
        private void DrawMethods(View view, RectTransform content, float scale, List<LiquidKnowledge.Method> methods = null)
        {
            LiquidComposition.Row row = view.Rows.Find(value => value.Liquid.identifier == view.Selected);
            if (row == null) return;
            methods = methods ?? LiquidKnowledge.For(row.Liquid, view.Sample);
            view.MethodSignature = MethodSignature(methods);
            float top = 0;
            if (methods.Count == 0)
            {
                var entry = Box(content, "Unavailable", RowColor, 0, 1, 1, 1, 0, -48 * scale, 0, 0);
                Text(entry, view, ShopSettings.Text("f19.unknown"), TextAlignmentOptions.TopLeft, 11 * scale, 5, 3, -5, -4, wrap: true);
                top = 48 * scale;
            }
            foreach (LiquidKnowledge.Method method in methods)
            {
                float available = Mathf.Max(90f, view.Width - 42f);
                var probe = Text(content, view, method.Description, TextAlignmentOptions.TopLeft, 10.5f * scale, 0, 0, 0, 0, wrap: true);
                float descHeight = Mathf.Max(28, probe.GetPreferredValues(method.Description, available, 0).y + 4);
                UnityEngine.Object.Destroy(probe.gameObject);
                float height = Mathf.Max(55 * scale, descHeight + 23 * scale);
                var card = Box(content, "Method_" + method.Sprite, RowColor, 0, 1, 1, 1, 0, -top - height, 0, -top - 2);
                var icon = Box(card, "Icon", Color.clear, 0, 1, 0, 1, 4, -35 * scale, 34 * scale, -5);
                Image image = icon.GetComponent<Image>(); image.sprite = RenderHandler.LoadFromAtlas(method.Atlas, method.Sprite);
                image.color = Color.white; image.preserveAspect = true; image.raycastTarget = false;
                Text(card, view, method.Name, TextAlignmentOptions.TopLeft, 11.5f * scale, 38 * scale, -20 * scale, -4, -3, bold: true);
                Text(card, view, method.Description, TextAlignmentOptions.TopLeft, 10.5f * scale, 38 * scale, 3, -4, -22 * scale, wrap: true);
                top += height;
            }
            content.sizeDelta = new Vector2(0, top);
        }
        private static string MethodSignature(List<LiquidKnowledge.Method> methods) => string.Join("\n", methods.Select(method =>
            method.Name + "\u001f" + method.Description + "\u001f" + method.Atlas + "\u001f" + method.Sprite));
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
        private static TextMeshProUGUI Text(RectTransform parent, View view, string value, TextAlignmentOptions align, float size,
            float left, float bottom, float right, float top, float ax0 = 0, float ax1 = 1, bool wrap = false, bool bold = false, bool fit = false)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            var text = go.GetComponent<TextMeshProUGUI>(); var rect = text.rectTransform; rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(ax0, 0); rect.anchorMax = new Vector2(ax1, 1);
            rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(right, top);
            text.font = view.NativeText.font; text.fontSharedMaterial = view.NativeText.fontSharedMaterial;
            text.fontSize = size; text.color = TextColor; text.richText = false; text.raycastTarget = false;
            text.enableWordWrapping = wrap; text.overflowMode = wrap && !fit ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            if (fit) { text.enableAutoSizing = true; text.fontSizeMin = Mathf.Min(7, size); text.fontSizeMax = size; }
            text.alignment = align; if (bold) text.fontStyle = FontStyles.Bold; text.text = value ?? "";
            return text;
        }
        private void MakeButton(RectTransform parent, View view, string value, Action action,
            float ax0, float ay0, float ax1, float ay1, float left, float bottom, float right, float top, float size)
        {
            var rect = Box(parent, "Button_" + value, new Color(.17f, .35f, .32f, 1), ax0, ay0, ax1, ay1, left, bottom, right, top);
            Image image = rect.GetComponent<Image>(); image.raycastTarget = true;
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            button.onClick.AddListener(() =>
            {
                try { action(); }
                catch (Exception error) { Clear(view); log("F19 scanner action failed: " + error); }
            });
            Text(rect, view, value, TextAlignmentOptions.Center, size, 0, 0, 0, 0, bold: true, fit: true);
        }
        private static void Clear(View view)
        {
            if (view.NativeText != null && view.Root != null && !view.NativeText.enabled) view.NativeText.enabled = view.NativeEnabled;
            view.Tested?.Restore(); view.Label?.Restore(); view.TotalText?.Restore();
            if (view.Root != null) { view.Root.SetActive(false); UnityEngine.Object.Destroy(view.Root); }
            view.Root = null; view.Scroller = null; view.Panel = null; view.Sample = null; view.NativeText = null;
            view.Raw = null; view.Selected = null; view.MethodSignature = null; view.Total = 0; view.Scroll = 1; view.Rows.Clear(); view.Order.Clear();
            view.Tested = view.Label = view.TotalText = null;
        }
        internal void Reset() { ClosePicker(); Clear(water); Clear(chem); }
        public void Dispose() { Reset(); if (ReferenceEquals(active, this)) active = null; started = false; }
        [HarmonyPatch(typeof(WaterScannerUIManager), nameof(WaterScannerUIManager.RefreshUI))]
        private static class WaterRefresh
        {
            private static void Postfix(WaterScannerUIManager __instance) => active?.Capture(active.water, __instance.panel, __instance.currentWaterContainer,
                __instance.contaminantTMP, __instance.testedTMP, __instance.labelTMP, null, false);
        }
        [HarmonyPatch(typeof(ChemScannerUIManager), nameof(ChemScannerUIManager.RefreshUI))]
        private static class ChemRefresh
        {
            private static void Postfix(ChemScannerUIManager __instance) => active?.Capture(active.chem, __instance.panel, __instance.currentScannedItem,
                __instance.contaminantTMP, __instance.testedTMP, __instance.labelTMP, __instance.totalVolumeTMP, true);
        }
        [HarmonyPatch(typeof(WaterScannerUIManager), nameof(WaterScannerUIManager.Close))]
        private static class WaterClose
        {
            private static void Postfix() { if (active != null) Clear(active.water); }
        }
        [HarmonyPatch(typeof(ChemScannerUIManager), nameof(ChemScannerUIManager.Close))]
        private static class ChemClose
        {
            private static void Postfix() { if (active != null) Clear(active.chem); }
        }
    }
}
