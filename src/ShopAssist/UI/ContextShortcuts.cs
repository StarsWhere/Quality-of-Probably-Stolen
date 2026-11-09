using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShopAssist
{
    // Direct actions belong to the object or native panel the player is using.
    internal sealed class ContextShortcuts : IDisposable
    {
        private static readonly System.Reflection.FieldInfo NativeWindowBackground = AccessTools.Field(typeof(PixelWindow), "background");
        private static readonly System.Reflection.FieldInfo NativeButtonPlate = AccessTools.Field(typeof(MachineryButton), "plate");
        private static readonly Vector3[] corners = new Vector3[4];
        private static readonly string[] MachineActions = { "f10.open", "f31.open", "f32.open", "f33.open", "f35.open" };

        private sealed class WindowAction
        {
            internal PixelWindow Window;
            internal GameItem Owner;
            internal string Key, Label;
            internal MachineryButton Button;
            internal bool? Disabled;
        }

        private sealed class PanelShortcut
        {
            internal GameObject Root;
            internal RectTransform Rect, Parent, Anchor;
            internal Button Button;
            internal TabletButton TabletButton;
            internal string Command;
            internal int Index;
            internal bool Inspection, TabletHeader;
            internal bool? Interactable;
            internal Rect ParentBounds, AnchorBounds, TargetBounds, Viewport;
            internal Matrix4x4 ParentMatrix, CameraView, CameraProjection, AnchorCameraView, AnchorCameraProjection;
            internal Camera Camera, AnchorCamera;
            internal int ScreenWidth, ScreenHeight, PositionedIndex;
            internal float PositionRefreshAt;
            internal bool PositionReady, PositionedInspection, PositionedTabletHeader;
        }

        private sealed class WindowPosition
        {
            internal Rect Bounds;
            internal Matrix4x4 ParentMatrix, CameraView, CameraProjection;
            internal Camera Camera;
            internal int ScreenWidth, ScreenHeight;
            internal float RefreshAt;
            internal bool Ready;
        }

        private struct PanelState : IEquatable<PanelState>
        {
            internal RectTransform Inspection, InspectionAnchor, Pc, Negotiation, Water, Chemical;
            internal bool Appraisal;

            public bool Equals(PanelState other) => Inspection == other.Inspection && InspectionAnchor == other.InspectionAnchor &&
                Pc == other.Pc && Negotiation == other.Negotiation && Water == other.Water && Chemical == other.Chemical &&
                Appraisal == other.Appraisal;
        }

        private readonly ShopSettings settings;
        private readonly Action<string> log;
        private readonly Dictionary<PixelWindow, Dictionary<string, WindowAction>> windowActions = new Dictionary<PixelWindow, Dictionary<string, WindowAction>>();
        private readonly Dictionary<PixelWindow, WindowPosition> windowPositions = new Dictionary<PixelWindow, WindowPosition>();
        private readonly Dictionary<string, PanelShortcut> panelButtons = new Dictionary<string, PanelShortcut>();
        private PanelState panels;
        private bool started, dirty = true, positionDirty = true;
        private float refreshAt;
        private PixelWindow measuredStock;
        private Rect stockOffsets;
        private Vector2 measuredStockSize;
        private float measureStockAt;
        private string lastError;

        internal ContextShortcuts(ShopSettings settings, Action<string> log)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        internal void Start()
        {
            if (started) return;
            started = true;
            settings.Changed += Invalidate;
            VisibleInventoryViews.Changed += Invalidate;
            VisibleInventoryViews.ItemChanged += ItemChanged;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        private void Invalidate() { dirty = true; refreshAt = 0; measureStockAt = 0; positionDirty = true; }
        private void ItemChanged(GameItemElement item)
        {
            if (item == null || !ReferenceEquals(item.parentInventory, EmporiumEntry.Instance?.invElement)) return;
            measureStockAt = 0; positionDirty = true;
        }
        private void SceneChanged(Scene before, Scene after) => Reset();

        internal void Tick()
        {
            if (!started) return;
            try
            {
                PanelState currentPanels = ReadPanelState();
                bool panelsChanged = !panels.Equals(currentPanels);
                panels = currentPanels;
                if (dirty || Time.unscaledTime >= refreshAt)
                {
                    dirty = false;
                    refreshAt = Time.unscaledTime + .25f;
                    var visible = WindowsHandler.current?.visibleWindows?.ToList() ?? new List<PixelWindow>();
                    PixelWindow stock = EmporiumEntry.Instance?.invWindow;
                    PixelWindow ground = EmporiumEntry.Instance?.afterhourWindow;
                    if (VisibleInventoryViews.Visible(stock) && !visible.Contains(stock)) visible.Add(stock);
                    if (VisibleInventoryViews.Visible(ground) && !visible.Contains(ground)) visible.Add(ground);
                    RefreshWindowActions(visible);
                    RefreshPanelButtons(currentPanels);
                    lastError = null;
                }
                else if (panelsChanged) RefreshPanelButtons(currentPanels);

                if (windowActions.Count == 0 && panelButtons.Count == 0) return;
                bool canInteract = CanInteract();
                bool? canInteractGround = null;
                foreach (var pair in windowActions)
                {
                    bool ground = ReferenceEquals(pair.Key, EmporiumEntry.Instance?.afterhourWindow);
                    bool allowed = canInteract;
                    if (ground)
                    {
                        if (!canInteractGround.HasValue) canInteractGround = CanInteract(true);
                        allowed = canInteractGround.Value;
                    }
                    foreach (WindowAction entry in pair.Value.Values)
                    {
                        if (entry.Button?.handler == null || entry.Disabled == !allowed) continue;
                        entry.Button.SetDisabled(!allowed); entry.Disabled = !allowed;
                    }
                    if (WindowPositionChanged(pair.Key, out Rect avoid, out Camera camera))
                        PositionWindowActions(pair.Value.Values.ToList(), avoid, camera);
                }
                positionDirty = false;
                foreach (PanelShortcut button in panelButtons.Values)
                    if (button.Root != null && button.Button != null)
                    {
                        // Keep an enabled feature clickable so a changed prerequisite can explain itself.
                        if (button.Interactable != canInteract)
                        {
                            if (button.TabletButton != null) button.TabletButton.SetInteractable(canInteract);
                            else button.Button.interactable = canInteract;
                            button.Interactable = canInteract;
                        }
                        PositionPanelIfChanged(button);
                    }
            }
            catch (Exception error)
            {
                string message = error.ToString();
                if (message != lastError) log("Context shortcuts failed: " + message);
                lastError = message;
                ResetControls();
            }
        }

        private void RefreshWindowActions(IEnumerable<PixelWindow> windows)
        {
            var seen = new HashSet<PixelWindow>();
            foreach (PixelWindow window in windows.Distinct())
            {
                if (!VisibleInventoryViews.Visible(window) || window.handler == null) continue;
                bool stock = ShopInventory.InStore && ReferenceEquals(window, EmporiumEntry.Instance?.invWindow);
                bool ground = PlayerStore.HasInstance && MapUIManager.Instance?.Current?.Id == AfterhourLocation.DUMPING_GROUND &&
                    ReferenceEquals(window, EmporiumEntry.Instance?.afterhourWindow);
                GameItem owner = null;
                if (!stock && !ground && ShopInventory.InStore && window.parentItems != null)
                    foreach (GameItem item in window.parentItems)
                    {
                        if (item == null || !ReferenceEquals(item.contentWindow, window) || !ShopInventory.Owned(item)) continue;
                        if (owner != null && !ReferenceEquals(owner, item)) { owner = null; break; }
                        owner = item;
                    }

                var wanted = new List<(string key, string label)>();
                if (stock)
                {
                    AddIfEnabled(wanted, "f11.open", "f11.context");
                    AddIfEnabled(wanted, "f30.open", "f30.context");
                    if (AttentionBook.Count > 0) wanted.Add(("attention", "attention"));
                    if (ShopNotice.HasResult) wanted.Add(("result", "operation.last"));
                }
                else if (ground)
                {
                    AddIfEnabled(wanted, "f36.open", "f36.context");
                    if (ShopNotice.HasResult) wanted.Add(("result", "operation.last"));
                }
                else if (owner != null)
                {
                    foreach (string key in MachineActions)
                        if (settings.FeatureAvailability(key).Allowed && (key == "f10.open" ?
                            MachineTools.ModuleContextApplies(owner, window) : MachineTools.SupportsContextAction(key, owner)))
                            wanted.Add((key, key.Substring(0, 3) + ".context"));
                }

                if (wanted.Count == 0) continue;
                seen.Add(window);
                if (!windowActions.TryGetValue(window, out var group))
                    windowActions.Add(window, group = new Dictionary<string, WindowAction>());
                var keys = new HashSet<string>(wanted.Select(item => item.key));
                foreach (string key in group.Keys.ToArray())
                    if (!keys.Contains(key) || !ReferenceEquals(group[key].Owner, owner)) RemoveWindowAction(group, key);
                foreach (var item in wanted)
                {
                    string label = item.key == "attention" ? ShopSettings.Format("visual.attention_count", AttentionBook.Count) : ShopSettings.Text(item.label);
                    if (group.TryGetValue(item.key, out WindowAction existing) && existing.Button?.handler != null)
                    {
                        if (existing.Button.text != label) { existing.Button.SetText(label); positionDirty = true; }
                        continue;
                    }
                    var entry = new WindowAction { Window = window, Owner = owner, Key = item.key, Label = item.label };
                    entry.Button = new MachineryButton(label, () => RunWindowAction(entry));
                    entry.Button.handler.gameObject.name = "ShopAssist_Action_" + item.key.Replace('.', '_');
                    entry.Button.handler.SetParent(window.handler);
                    group[item.key] = entry;
                    positionDirty = true;
                }
            }
            foreach (PixelWindow window in windowActions.Keys.ToArray())
                if (!seen.Contains(window)) RemoveWindowGroup(window);
        }

        private void AddIfEnabled(List<(string key, string label)> wanted, string key, string label)
        {
            if (settings.FeatureAvailability(key).Allowed) wanted.Add((key, label));
        }

        private void RunWindowAction(WindowAction entry)
        {
            if (!CanInteract(ReferenceEquals(entry.Window, EmporiumEntry.Instance?.afterhourWindow)) || !VisibleInventoryViews.Visible(entry.Window)) return;
            if (entry.Key == "attention") { AttentionBook.Open(); return; }
            if (entry.Key == "result") { ShopNotice.OpenLastResult(); return; }
            if (entry.Owner == null) { settings.Run(entry.Key); return; }
            GameItem owner = entry.Owner;
            if (!ShopInventory.Owned(owner) || !ReferenceEquals(owner.contentWindow, entry.Window) ||
                !(entry.Key == "f10.open" ? MachineTools.ModuleContextApplies(owner, entry.Window) : MachineTools.SupportsContextAction(entry.Key, owner)))
            { ShopNotice.Show(ShopSettings.Text("availability.object")); return; }
            ActionAvailability availability = settings.FeatureAvailability(entry.Key);
            if (availability.Allowed)
                availability = entry.Key == "f10.open"
                    ? MachineTools.ModuleContextAvailability(owner, entry.Window)
                    : MachineTools.Availability(entry.Key, owner);
            if (!availability.Allowed) { ShopNotice.Show(availability.Message); return; }
            entry.Window.ToFront();
            switch (entry.Key)
            {
                case "f10.open": MachineTools.OpenModulesFor(owner); break;
                case "f31.open": MachineTools.OpenMaintenanceFor(owner, 31); break;
                case "f32.open": MachineTools.OpenReplantFor(owner); break;
                case "f33.open": MachineTools.OpenFeedFor(owner); break;
                case "f35.open": MachineTools.OpenMaintenanceFor(owner, 35); break;
            }
        }

        private static bool ScreenRect(RectTransform rect, Camera camera, out Rect bounds)
        {
            bounds = default;
            if (rect == null) return false;
            rect.GetWorldCorners(corners);
            Vector2 first = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            float minX = first.x, maxX = first.x, minY = first.y, maxY = first.y;
            for (int i = 1; i < corners.Length; i++)
            {
                Vector2 p = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return bounds.width > 0 && bounds.height > 0;
        }

        private static Rect Union(Rect a, Rect b) => Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
            Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));

        // Place the actions as one group outside the object's frame. Measuring the
        // rendered plate accounts for the game's pixel scale and translated labels.
        private bool WindowPositionChanged(PixelWindow window, out Rect bounds, out Camera camera)
        {
            bounds = default; camera = null;
            RectTransform parent = window?.handler?.rectTransform;
            if (parent == null) return false;
            camera = CanvasCamera(parent);
            var background = NativeWindowBackground?.GetValue(window) as TreeNodeRender;
            if (!ScreenRect(background?.rectTransform, camera, out bounds) && !ScreenRect(parent, camera, out bounds)) return false;
            if (!windowPositions.TryGetValue(window, out WindowPosition state))
                windowPositions.Add(window, state = new WindowPosition());
            Matrix4x4 parentMatrix = parent.localToWorldMatrix;
            Matrix4x4 view = camera != null ? camera.worldToCameraMatrix : Matrix4x4.identity;
            Matrix4x4 projection = camera != null ? camera.projectionMatrix : Matrix4x4.identity;
            bool changed = positionDirty || !state.Ready || state.Bounds != bounds || state.ParentMatrix != parentMatrix ||
                state.Camera != camera || state.CameraView != view || state.CameraProjection != projection ||
                state.ScreenWidth != Screen.width || state.ScreenHeight != Screen.height || Time.unscaledTime >= state.RefreshAt;
            if (!changed) return false;
            state.Bounds = bounds; state.ParentMatrix = parentMatrix; state.Camera = camera; state.CameraView = view;
            state.CameraProjection = projection; state.ScreenWidth = Screen.width; state.ScreenHeight = Screen.height;
            state.RefreshAt = Time.unscaledTime + .25f; state.Ready = true;
            return true;
        }

        private void PositionWindowActions(List<WindowAction> entries, Rect avoid, Camera camera)
        {
            if (entries.Count == 0) return;
            PixelWindow window = entries[0].Window;
            RectTransform parent = window?.handler?.rectTransform;
            if (parent == null) return;
            if (ReferenceEquals(window, EmporiumEntry.Instance?.invWindow))
            {
                Rect frame = avoid;
                Vector2 frameSize = frame.size;
                if (!ReferenceEquals(measuredStock, window) || measuredStockSize != frameSize || Time.unscaledTime >= measureStockAt)
                {
                    GameGridInventory grid = EmporiumEntry.Instance.invElement;
                    if (ScreenRect(grid?.inner?.rectTransform, camera, out Rect gridBounds)) avoid = Union(avoid, gridBounds);
                    if (grid?.childItems != null)
                        foreach (GameItem item in grid.childItems)
                            if (item is GameItemElement element && element.handler != null && element.handler.gameObject.activeInHierarchy &&
                                ScreenRect(element.background?.rectTransform, camera, out Rect itemBounds)) avoid = Union(avoid, itemBounds);
                    measuredStock = window; measuredStockSize = frameSize; measureStockAt = Time.unscaledTime + .25f;
                    stockOffsets = Rect.MinMaxRect(avoid.xMin - frame.xMin, avoid.yMin - frame.yMin,
                        avoid.xMax - frame.xMin, avoid.yMax - frame.yMin);
                }
                else avoid = Rect.MinMaxRect(frame.xMin + stockOffsets.xMin, frame.yMin + stockOffsets.yMin,
                    frame.xMin + stockOffsets.xMax, frame.yMin + stockOffsets.yMax);
            }
            var measured = new List<(WindowAction entry, Rect bounds)>();
            float width = 0, height = 0;
            foreach (WindowAction entry in entries)
            {
                if (entry.Button?.handler == null) continue;
                entry.Button.handler.rectTransform.anchoredPosition = new Vector2(0, entry.Button.heightPixels + 3f);
                var plate = NativeButtonPlate?.GetValue(entry.Button) as TreeNodeRender;
                if (!ScreenRect(plate?.rectTransform, camera, out Rect bounds)) continue;
                measured.Add((entry, bounds));
                width = Mathf.Max(width, bounds.width); height = Mathf.Max(height, bounds.height);
            }
            if (measured.Count == 0 || width <= 0 || height <= 0) return;
            const float gap = 3f, margin = 2f;
            int columns = Mathf.Max(1, Mathf.Min(measured.Count, Mathf.FloorToInt((Screen.width - 2 * margin + gap) / (width + gap))));
            int rows = (measured.Count + columns - 1) / columns;
            float groupWidth = columns * width + (columns - 1) * gap;
            float groupHeight = rows * height + (rows - 1) * gap;
            float x = Mathf.Clamp(avoid.xMax - groupWidth, margin, Mathf.Max(margin, Screen.width - margin - groupWidth));
            float y;
            if (avoid.yMax + gap + groupHeight <= Screen.height - margin) y = avoid.yMax + gap;
            else if (avoid.yMin - gap - groupHeight >= margin) y = avoid.yMin - gap - groupHeight;
            else
            {
                columns = 1; rows = measured.Count; groupWidth = width; groupHeight = rows * height + (rows - 1) * gap;
                if (avoid.xMax + gap + groupWidth <= Screen.width - margin) x = avoid.xMax + gap;
                else if (avoid.xMin - gap - groupWidth >= margin) x = avoid.xMin - gap - groupWidth;
                else x = Mathf.Clamp(avoid.xMax - groupWidth, margin, Mathf.Max(margin, Screen.width - margin - groupWidth));
                y = Mathf.Clamp(avoid.yMax - groupHeight, margin, Mathf.Max(margin, Screen.height - margin - groupHeight));
            }
            for (int i = 0; i < measured.Count; i++)
            {
                var item = measured[i];
                int column = i % columns, row = i / columns;
                Vector2 wanted = new Vector2(x + column * (width + gap), y + (rows - 1 - row) * (height + gap));
                Vector2 delta = wanted - item.bounds.min;
                RectTransform rect = item.entry.Button.handler.rectTransform;
                Vector2 origin = RectTransformUtility.WorldToScreenPoint(camera, rect.position);
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, origin, camera, out Vector2 before) &&
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, origin + delta, camera, out Vector2 after))
                    rect.anchoredPosition += after - before;
            }
        }

        private void RemoveWindowAction(Dictionary<string, WindowAction> group, string key)
        {
            if (!group.TryGetValue(key, out WindowAction entry)) return;
            group.Remove(key);
            positionDirty = true;
            if (entry.Button?.handler != null)
            {
                entry.Button.handler.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(entry.Button.handler.gameObject);
            }
        }

        private void RemoveWindowGroup(PixelWindow window)
        {
            if (!windowActions.TryGetValue(window, out var group)) return;
            foreach (string key in group.Keys.ToArray()) RemoveWindowAction(group, key);
            windowActions.Remove(window);
            windowPositions.Remove(window);
        }

        private static RectTransform VisiblePanel(GameObject panel) =>
            panel != null && panel.activeInHierarchy ? panel.transform as RectTransform : null;

        private static PanelState ReadPanelState()
        {
            var state = new PanelState();
            InspectionUIManager inspect = InspectionUIManager.Instance;
            state.Inspection = VisiblePanel(inspect?.panel);
            if (state.Inspection != null)
            {
                state.Appraisal = inspect.appraisalPanel != null && inspect.appraisalPanel.activeInHierarchy;
                state.InspectionAnchor = state.Appraisal ? inspect.appraisalPanel.transform as RectTransform :
                    inspect.magnifyingGlass?.transform as RectTransform;
            }
            state.Pc = VisiblePanel(PCUIManager.Instance?.panel);
            NegociationUIManager negotiation = NegociationUIManager.Instance;
            NegotiationTabletFrame frame = negotiation?.IsOpen == true && negotiation.uiObject != null ?
                negotiation.uiObject.GetComponent<NegotiationTabletFrame>() : null;
            state.Negotiation = frame?.panel != null && frame.panel.gameObject.activeInHierarchy ? frame.panel : null;
            state.Water = VisiblePanel(WaterScannerUIManager.Instance?.panel);
            state.Chemical = VisiblePanel(ChemScannerUIManager.Instance?.panel);
            return state;
        }

        private void RefreshPanelButtons(PanelState state)
        {
            var desired = new HashSet<string>();
            if (state.Inspection != null)
            {
                int index = 0;
                if (state.Appraisal && settings.FeatureAvailability("f23.open").Allowed)
                    EnsurePanelShortcut(desired, "ShopAssist_AppraisalWorksheet", state.Inspection, "f23.open", index++, state.InspectionAnchor, true);
                if (settings.FeatureAvailability("f18.reset").Allowed)
                    EnsurePanelShortcut(desired, "ShopAssist_AppraisalReset", state.Inspection, "f18.reset", index, state.InspectionAnchor, true);
            }
            if (state.Pc != null)
            {
                int index = 0;
                if (settings.FeatureAvailability("f28.open").Allowed) EnsurePanelShortcut(desired, "ShopAssist_LedgerTools", state.Pc, "f28.open", index);
            }
            if (state.Negotiation != null)
            {
                int index = 0;
                if (settings.FeatureAvailability("f34.open").Allowed) EnsurePanelShortcut(desired, "ShopAssist_CustomerBasket", state.Negotiation, "f34.open", index++, tabletHeader: true);
                if (settings.FeatureAvailability("f14.reset").Allowed) EnsurePanelShortcut(desired, "ShopAssist_BargainReset", state.Negotiation, "f14.reset", index, tabletHeader: true);
            }
            if (settings.FeatureAvailability("f19.choose").Allowed)
            {
                if (state.Water != null) EnsurePanelShortcut(desired, "ShopAssist_WaterPins", state.Water, "f19.choose", 0);
                if (state.Chemical != null) EnsurePanelShortcut(desired, "ShopAssist_ChemPins", state.Chemical, "f19.choose", 0);
            }
            foreach (string id in panelButtons.Keys.ToArray()) if (!desired.Contains(id)) RemovePanelShortcut(id);
        }

        private void EnsurePanelShortcut(HashSet<string> desired, string name, RectTransform parent, string command, int index,
            RectTransform anchor = null, bool inspection = false, bool tabletHeader = false)
        {
            if (parent == null || !parent.gameObject.activeInHierarchy) return;
            desired.Add(name);
            if (panelButtons.TryGetValue(name, out PanelShortcut old) && old.Parent == parent && old.Root != null)
            {
                old.Index = index;
                old.Anchor = anchor != null ? anchor : parent;
                old.Inspection = inspection;
                old.TabletHeader = tabletHeader;
                TMP_Text existing = old.Root.GetComponentInChildren<TMP_Text>(true);
                if (existing != null) existing.text = ShopSettings.Text(tabletHeader ? command.Substring(0, 3) + ".button" : command);
                return;
            }
            RemovePanelShortcut(name);
            if (tabletHeader)
            {
                var frame = parent.GetComponentInParent<NegotiationTabletFrame>();
                if (frame == null) return;
                TabletButton native = TabletUI.SmallButton(frame, parent, 40f + index * 150f, 8f, 142f, 32f,
                    ShopSettings.Text(command.Substring(0, 3) + ".button"),
                    () => { if (CanInteract() && parent != null && parent.gameObject.activeInHierarchy) settings.Run(command); });
                if (native == null || native.button == null) return;
                GameObject nativeRoot = native.gameObject;
                nativeRoot.name = name;
                TabletUI.AddTooltip(nativeRoot, () => ShopSettings.Text(command));
                nativeRoot.transform.SetAsLastSibling();
                panelButtons[name] = new PanelShortcut { Root = nativeRoot, Rect = nativeRoot.transform as RectTransform, Parent = parent,
                    Anchor = parent, Button = native.button, TabletButton = native, Command = command, Index = index, TabletHeader = true };
                return;
            }
            GameObject prefab = CustomUIManager.Instance?.GetPrefab(CustomUIManager.ElementKind.Button, true);
            if (prefab == null) return;
            GameObject root = UnityEngine.Object.Instantiate(prefab, parent, false);
            root.name = name;
            RectTransform rect = root.transform as RectTransform;
            Button control = root.GetComponentInChildren<Button>(true);
            TMP_Text text = root.GetComponentInChildren<TMP_Text>(true);
            if (rect == null || control == null || text == null) { UnityEngine.Object.Destroy(root); return; }
            LayoutElement layout = root.GetComponent<LayoutElement>();
            if (layout != null) layout.ignoreLayout = true;
            rect.sizeDelta = new Vector2(112, 28);
            text.text = ShopSettings.Text(command); text.richText = false; text.raycastTarget = false;
            text.enableAutoSizing = true; text.fontSizeMin = 11; text.fontSizeMax = 18;
            control.onClick = new Button.ButtonClickedEvent();
            control.onClick.AddListener(() => { if (CanInteract() && parent != null && parent.gameObject.activeInHierarchy) settings.Run(command); });
            root.SetActive(true); rect.SetAsLastSibling();
            panelButtons[name] = new PanelShortcut { Root = root, Rect = rect, Parent = parent, Anchor = anchor != null ? anchor : parent,
                Button = control, Command = command, Index = index, Inspection = inspection };
            PositionPanelShortcut(panelButtons[name]);
        }

        private static void PositionPanelShortcut(PanelShortcut entry)
        {
            if (entry.Rect == null || entry.Parent == null) return;
            if (entry.Inspection) PositionInspectionShortcut(entry);
            else if (entry.TabletHeader) entry.Rect.anchoredPosition = new Vector2(40f + entry.Index * 150f, -8f);
            else PositionOutsidePanel(entry.Rect, entry.Parent, entry.Index);
        }

        private static void PositionPanelIfChanged(PanelShortcut entry)
        {
            RectTransform target = entry.Inspection ? entry.Button?.transform as RectTransform : entry.Rect;
            if (entry.Parent == null || target == null) return;
            Camera camera = entry.Inspection ? PanelCamera(target) : CanvasCamera(target);
            Camera anchorCamera = entry.Inspection ? PanelCamera(entry.Anchor) : CanvasCamera(entry.Parent);
            Rect anchor = default;
            if (!ScreenRect(entry.Parent, anchorCamera, out Rect parent) || !ScreenRect(target, camera, out Rect targetBounds) ||
                entry.Inspection && !ScreenRect(entry.Anchor, anchorCamera, out anchor))
            { entry.PositionReady = false; PositionPanelShortcut(entry); return; }
            Canvas canvas = entry.Inspection ? target.GetComponentInParent<Canvas>()?.rootCanvas : null;
            Rect viewport = canvas != null ? canvas.pixelRect : default;
            Matrix4x4 parentMatrix = entry.Parent.localToWorldMatrix;
            Matrix4x4 view = camera != null ? camera.worldToCameraMatrix : Matrix4x4.identity;
            Matrix4x4 projection = camera != null ? camera.projectionMatrix : Matrix4x4.identity;
            Matrix4x4 anchorView = anchorCamera != null ? anchorCamera.worldToCameraMatrix : Matrix4x4.identity;
            Matrix4x4 anchorProjection = anchorCamera != null ? anchorCamera.projectionMatrix : Matrix4x4.identity;
            bool changed = !entry.PositionReady || entry.ParentBounds != parent || entry.AnchorBounds != anchor ||
                entry.TargetBounds != targetBounds || entry.Viewport != viewport || entry.ParentMatrix != parentMatrix ||
                entry.Camera != camera || entry.CameraView != view || entry.CameraProjection != projection ||
                entry.AnchorCamera != anchorCamera || entry.AnchorCameraView != anchorView || entry.AnchorCameraProjection != anchorProjection ||
                entry.ScreenWidth != Screen.width || entry.ScreenHeight != Screen.height || entry.PositionedIndex != entry.Index ||
                entry.PositionedInspection != entry.Inspection || entry.PositionedTabletHeader != entry.TabletHeader ||
                Time.unscaledTime >= entry.PositionRefreshAt;
            if (!changed) return;
            PositionPanelShortcut(entry);
            if (!ScreenRect(target, camera, out targetBounds)) { entry.PositionReady = false; return; }
            entry.ParentBounds = parent; entry.AnchorBounds = anchor; entry.TargetBounds = targetBounds; entry.Viewport = viewport;
            entry.ParentMatrix = parentMatrix; entry.Camera = camera; entry.CameraView = view; entry.CameraProjection = projection;
            entry.AnchorCamera = anchorCamera; entry.AnchorCameraView = anchorView; entry.AnchorCameraProjection = anchorProjection;
            entry.ScreenWidth = Screen.width; entry.ScreenHeight = Screen.height; entry.PositionedIndex = entry.Index;
            entry.PositionedInspection = entry.Inspection; entry.PositionedTabletHeader = entry.TabletHeader;
            entry.PositionRefreshAt = Time.unscaledTime + .25f; entry.PositionReady = true;
        }

        private static Camera PanelCamera(RectTransform rect)
        {
            Canvas canvas = rect != null ? rect.GetComponentInParent<Canvas>() : null;
            if (canvas == null) return null;
            GraphicRaycaster raycaster = canvas.GetComponent<GraphicRaycaster>();
            return raycaster != null ? raycaster.eventCamera :
                canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        private static void PositionInspectionShortcut(PanelShortcut entry)
        {
            RectTransform actual = entry.Button?.transform as RectTransform;
            if (actual == null || entry.Anchor == null || Screen.width <= 0 || Screen.height <= 0) return;
            Camera camera = PanelCamera(actual);
            if (!ScreenRect(actual, camera, out Rect button) || !ScreenRect(entry.Anchor, PanelCamera(entry.Anchor), out Rect anchor)) return;
            Rect screen = new Rect(0, 0, Screen.width, Screen.height);
            Canvas canvas = actual.GetComponentInParent<Canvas>()?.rootCanvas;
            if (canvas != null)
            {
                Rect viewport = canvas.pixelRect;
                float left = Mathf.Max(screen.xMin, viewport.xMin), right = Mathf.Min(screen.xMax, viewport.xMax);
                float bottom = Mathf.Max(screen.yMin, viewport.yMin), top = Mathf.Min(screen.yMax, viewport.yMax);
                if (right > left && top > bottom) screen = Rect.MinMaxRect(left, bottom, right, top);
            }
            const float gap = 4f, margin = 2f;
            float xMin = screen.xMin + margin, xMax = Mathf.Max(xMin, screen.xMax - margin - button.width);
            float yMin = screen.yMin + margin, yMax = Mathf.Max(yMin, screen.yMax - margin - button.height);
            float rightX = anchor.xMax + gap, leftX = anchor.xMin - gap - button.width;
            float topY = anchor.yMax + gap, bottomY = anchor.yMin - gap - button.height;
            float x, y;
            if (rightX <= xMax || leftX >= xMin)
            {
                x = rightX <= xMax ? rightX : leftX;
                y = anchor.yMax - button.height - entry.Index * (button.height + gap);
            }
            else if (topY <= yMax || bottomY >= yMin)
            {
                x = anchor.xMax - button.width - entry.Index * (button.width + gap);
                y = topY <= yMax ? topY : bottomY;
            }
            else
            {
                x = screen.xMax - margin - button.width;
                y = screen.yMax - margin - button.height - entry.Index * (button.height + gap);
            }
            Vector2 target = new Vector2(Mathf.Clamp(x, xMin, xMax), Mathf.Clamp(y, yMin, yMax));
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(entry.Parent, button.min, camera, out Vector2 before) &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(entry.Parent, target, camera, out Vector2 after))
                entry.Rect.anchoredPosition += after - before;
        }

        private void RemovePanelShortcut(string id)
        {
            if (!panelButtons.TryGetValue(id, out PanelShortcut entry)) return;
            panelButtons.Remove(id);
            if (entry.Root != null) { entry.Root.SetActive(false); UnityEngine.Object.Destroy(entry.Root); }
        }

        private static void PositionOutsidePanel(RectTransform rect, RectTransform parent, int index)
        {
            Camera camera = CanvasCamera(parent);
            if (!ScreenRect(parent, camera, out Rect bounds)) return;
            Vector2 origin = RectTransformUtility.WorldToScreenPoint(camera, parent.TransformPoint(Vector3.zero));
            Vector2 horizontal = RectTransformUtility.WorldToScreenPoint(camera, parent.TransformPoint(new Vector3(rect.rect.width + 6, 0, 0)));
            Vector2 vertical = RectTransformUtility.WorldToScreenPoint(camera, parent.TransformPoint(new Vector3(0, rect.rect.height + 4, 0)));
            float width = Mathf.Abs(horizontal.x - origin.x), height = Mathf.Abs(vertical.y - origin.y);
            bool fitsBelowTop = bounds.yMax - (index + 1) * height >= 2;
            bool right = Screen.width - bounds.xMax >= width + 4;
            bool left = bounds.xMin >= width + 4;
            if (fitsBelowTop && (right || left))
            {
                rect.anchorMin = rect.anchorMax = new Vector2(right ? 1 : 0, 1);
                rect.pivot = new Vector2(right ? 0 : 1, 1);
                rect.anchoredPosition = new Vector2(right ? 4 : -4, -4 - index * (rect.rect.height + 4));
            }
            else if (Screen.height - bounds.yMax >= height + 4 || bounds.yMin >= height + 4)
            {
                bool above = Screen.height - bounds.yMax >= height + 4;
                rect.anchorMin = rect.anchorMax = new Vector2(1, above ? 1 : 0);
                rect.pivot = new Vector2(1, above ? 0 : 1);
                rect.anchoredPosition = new Vector2(-index * (rect.rect.width + 6), above ? 4 : -4);
            }
            else
            {
                bool useRight = Screen.width - bounds.xMax >= bounds.xMin;
                rect.anchorMin = rect.anchorMax = new Vector2(useRight ? 1 : 0, 1);
                rect.pivot = new Vector2(useRight ? 0 : 1, 1);
                rect.anchoredPosition = new Vector2(useRight ? 4 : -4, -4 - index * (rect.rect.height + 4));
            }
            ClampToScreen(rect, parent);
        }

        private static Camera CanvasCamera(RectTransform rect)
        {
            GraphicRaycaster raycaster = rect.GetComponentInParent<GraphicRaycaster>();
            if (raycaster != null) return raycaster.eventCamera;
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        private static void ClampToScreen(RectTransform rect, RectTransform parent)
        {
            if (rect == null || parent == null || Screen.width <= 0 || Screen.height <= 0) return;
            Camera camera = CanvasCamera(rect);
            if (!ScreenRect(rect, camera, out Rect bounds)) return;
            float dx = bounds.xMin < 2 ? 2 - bounds.xMin : bounds.xMax > Screen.width - 2 ? Screen.width - 2 - bounds.xMax : 0;
            float dy = bounds.yMin < 2 ? 2 - bounds.yMin : bounds.yMax > Screen.height - 2 ? Screen.height - 2 - bounds.yMax : 0;
            if (dx == 0 && dy == 0) return;
            Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, rect.position);
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, point, camera, out Vector2 before) &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, point + new Vector2(dx, dy), camera, out Vector2 after))
                rect.anchoredPosition += after - before;
        }

        private static bool CanInteract(bool ground = false)
        {
            StoreUIManager ui = StoreUIManager.Instance;
            bool inScope = ShopInventory.InStore || ground && PlayerStore.HasInstance &&
                MapUIManager.Instance?.Current?.Id == AfterhourLocation.DUMPING_GROUND;
            return inScope && ui != null && !StoreUIManager.IsAnyTyping() && !KeyBindHelper.IsCapturing &&
                !UIDragHandler.isDraggingAnyUI && ItemMouseDragHandler.current?.IsDraggingItem != true &&
                (ui.gameOptionUIManager == null || !ui.gameOptionUIManager.IsOpen) &&
                (ui.escapeUIManager == null || !ui.escapeUIManager.menuPanel.activeInHierarchy) &&
                (ui.blackscreen == null || !ui.blackscreen.gameObject.activeInHierarchy) &&
                (ui.controlHintOverlay == null || !ui.controlHintOverlay.activeInHierarchy) &&
                (ui.closeShutterConfirmationPanel == null || !ui.closeShutterConfirmationPanel.activeInHierarchy) &&
                (GenericConfirmationUIManager.Instance == null || !GenericConfirmationUIManager.Instance.panel.activeInHierarchy) &&
                (ui.confirmPanelUIManager == null || !ui.confirmPanelUIManager.panel.activeInHierarchy);
        }

        private void ResetControls()
        {
            foreach (PixelWindow window in windowActions.Keys.ToArray()) RemoveWindowGroup(window);
            foreach (string id in panelButtons.Keys.ToArray()) RemovePanelShortcut(id);
            measuredStock = null; measureStockAt = 0; panels = default; dirty = true; positionDirty = true;
        }
        private void Reset() { ResetControls(); lastError = null; }
        public void Dispose()
        {
            if (!started) return;
            started = false;
            settings.Changed -= Invalidate;
            VisibleInventoryViews.Changed -= Invalidate;
            VisibleInventoryViews.ItemChanged -= ItemChanged;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
        }
    }
}
