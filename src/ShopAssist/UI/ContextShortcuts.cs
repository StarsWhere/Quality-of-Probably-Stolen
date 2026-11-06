using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed class ContextShortcuts : IDisposable
    {
        private sealed class MachineEntry
        {
            internal PixelWindow Window;
            internal GameItem Machine;
            internal MachineryButton Button;
        }

        private sealed class ToolEntry
        {
            internal PixelWindow Window;
            internal GameItem Owner;
            internal bool Machine;
            internal MachineryButton Button;
        }

        private sealed class PanelShortcut
        {
            internal GameObject Root;
            internal RectTransform Rect, Parent, Anchor;
            internal Button Button;
            internal string Command;
            internal int Index;
        }

        private sealed class CardAction
        {
            internal Func<ActionAvailability> Evaluate;
            internal CustomUIElement Button, Reason, SettingButton;
            internal GameObject DetailRow;
        }

        private readonly ShopSettings settings;
        private readonly CustomerInventory listings;
        private readonly Action<string> log;
        private readonly Dictionary<PixelWindow, MachineEntry> machines = new Dictionary<PixelWindow, MachineEntry>();
        private readonly Dictionary<PixelWindow, ToolEntry> toolButtons = new Dictionary<PixelWindow, ToolEntry>();
        private readonly Dictionary<string, PanelShortcut> panelButtons = new Dictionary<string, PanelShortcut>();
        private readonly List<CardAction> cardActions = new List<CardAction>();
        private CustomUIWindow contextCard;
        private PixelWindow contextOwner;
        private GameObject customerButton;
        private Button customerControl;
        private RectTransform customerRect, customerPanel;
        private NegotiationTabletFrame customerFrame;
        private bool started, dirty = true;
        private float refreshAt;
        private string lastError;

        internal ContextShortcuts(ShopSettings settings, CustomerInventory listings, Action<string> log)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.listings = listings ?? throw new ArgumentNullException(nameof(listings));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        internal void Start()
        {
            if (started) return;
            started = true;
            InventoryContextActions.RegisterItem("f22.open", _ => ShopSettings.Text("f22.open"), BatchLabeler.CanOpenFor, BatchLabeler.OpenFor);
            InventoryContextActions.RegisterItem("f24.choose", _ => ShopSettings.Text("f24.choose"), AppraisalReceipt.CanBind, AppraisalReceipt.OpenBind);
            InventoryContextActions.RegisterItem("f25.open", _ => ShopSettings.Text("f25.open"), TripCargo.CanOpenFor, TripCargo.OpenFor);
            InventoryContextActions.RegisterItem("f29.open", _ => ShopSettings.Text("f29.open"), DisplayRestock.CanOpenFor, DisplayRestock.OpenFor);
            settings.Changed += Invalidate;
            VisibleInventoryViews.Changed += Invalidate;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        private void Invalidate() { dirty = true; refreshAt = 0; }
        private void SceneChanged(Scene before, Scene after) => Reset();

        internal void Tick()
        {
            if (!started) return;
            try
            {
                if (dirty || Time.unscaledTime >= refreshAt)
                {
                    dirty = false;
                    refreshAt = Time.unscaledTime + 0.25f;
                    PixelWindow[] visible = ShopInventory.InStore && WindowsHandler.current?.visibleWindows != null
                        ? WindowsHandler.current.visibleWindows.ToArray() : Array.Empty<PixelWindow>();
                    RefreshMachines(visible);
                    RefreshTools(visible);
                    RefreshCustomer();
                    RefreshPanelButtons();
                    settings.RefreshCommands();
                    RefreshContextCard();
                    lastError = null;
                }
                bool available = CanInteract();
                foreach (MachineEntry entry in machines.Values)
                {
                    bool enabled = available && MachineTools.ModulesAvailableFor(entry.Machine, entry.Window);
                    entry.Button.SetDisabled(!enabled);
                    if (entry.Window.style != WindowStyle.Machinery) PositionClassic(entry);
                }
                foreach (ToolEntry entry in toolButtons.Values)
                {
                    entry.Button.SetDisabled(!available);
                    if (entry.Window.style != WindowStyle.Machinery) PositionToolClassic(entry);
                }
                if (contextCard != null && (!contextCard.IsAlive || contextOwner == null || !VisibleInventoryViews.Visible(contextOwner)))
                    CloseContextCard();
                foreach (PanelShortcut button in panelButtons.Values)
                    if (button.Root != null && button.Button != null)
                    {
                        button.Button.interactable = available && settings.CanRun(button.Command);
                        PositionPanelShortcut(button);
                    }
                if (customerControl != null)
                {
                    customerControl.interactable = available && listings.CanOpen() &&
                        (customerFrame?.confirmRoot == null || !customerFrame.confirmRoot.activeInHierarchy);
                    PositionCustomer();
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

        private void RefreshTools(PixelWindow[] windows)
        {
            var visible = new HashSet<PixelWindow>();
            foreach (PixelWindow window in windows)
                {
                    if (!VisibleInventoryViews.Visible(window) || window.isLocked || window.handler == null) continue;
                    GameItem owner = null;
                    if (window.parentItems != null)
                        foreach (GameItem item in window.parentItems)
                        {
                            if (item == null || !ReferenceEquals(item.contentWindow, window) || !ShopInventory.Owned(item)) continue;
                            if (owner != null && !ReferenceEquals(owner, item)) { owner = null; break; }
                            owner = item;
                        }
                    bool machine = owner != null && owner.contentWindow?.childElement is MachineryPanelRow;
                    bool storage = ReferenceEquals(window, EmporiumEntry.Instance.invWindow) || owner != null && ShopInventory.SafeContainerForPeek(owner);
                    if (!machine && !storage) continue;
                    visible.Add(window);
                    if (toolButtons.TryGetValue(window, out ToolEntry existing) && existing.Owner == owner && existing.Machine == machine && existing.Button?.handler != null)
                    {
                        string title = ShopSettings.Text("visual.tools");
                        if (existing.Button.text != title) existing.Button.SetText(title);
                        continue;
                    }
                    if (existing != null) RemoveTool(existing);
                    var entry = new ToolEntry { Window = window, Owner = owner, Machine = machine };
                    entry.Button = new MachineryButton(ShopSettings.Text("visual.tools"), () => OpenContextCard(entry));
                    entry.Button.handler.gameObject.name = "ShopAssist_WindowTools";
                    toolButtons[window] = entry;
                    if (window.style == WindowStyle.Machinery) window.AddHeaderButton(entry.Button);
                    else { entry.Button.handler.SetParent(window.handler); PositionToolClassic(entry); }
                }
            foreach (PixelWindow window in toolButtons.Keys.ToArray())
                if (!visible.Contains(window)) RemoveTool(toolButtons[window]);
        }

        private ActionAvailability MachineAvailability(string key, GameItem item, PixelWindow window)
        {
            var feature = settings.FeatureAvailability(key);
            if (!feature.Allowed) return feature;
            if (key != "f10.open") return MachineTools.Availability(key, item);
            return MachineTools.ModulesAvailableFor(item, window) ? ActionAvailability.Ready :
                ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.object");
        }

        private static void PositionToolClassic(ToolEntry entry)
        {
            if (entry.Window?.handler == null || entry.Button?.handler == null) return;
            var rect = entry.Button.handler.rectTransform;
            rect.anchoredPosition = new Vector2(Mathf.Max(0, entry.Window.widthPixels - entry.Button.widthPixels - 18), -2f);
            ClampToScreen(rect, entry.Window.handler.rectTransform);
        }

        private void RemoveTool(ToolEntry entry)
        {
            toolButtons.Remove(entry.Window);
            if (entry.Button?.handler == null) return;
            if (entry.Window?.handler != null && entry.Window.style == WindowStyle.Machinery)
                entry.Window.RemoveHeaderButton(entry.Button);
            UnityEngine.Object.Destroy(entry.Button.handler.gameObject);
        }

        private void OpenContextCard(ToolEntry entry)
        {
            if (!CanInteract() || entry?.Window == null || !VisibleInventoryViews.Visible(entry.Window) ||
                entry.Machine && (entry.Owner == null || !ReferenceEquals(entry.Owner.contentWindow, entry.Window) || !ShopInventory.Owned(entry.Owner))) return;
            entry.Window.ToFront();
            CloseContextCard();
            var actions = new List<(string key, Func<ActionAvailability> availability, Action run)>();
            if (entry.Machine)
            {
                actions.Add(("f10.open", () => MachineAvailability("f10.open", entry.Owner, entry.Window), () => MachineTools.OpenModulesFor(entry.Owner)));
                actions.Add(("f31.open", () => MachineAvailability("f31.open", entry.Owner, entry.Window), () => MachineTools.OpenMaintenanceFor(entry.Owner, 31)));
                actions.Add(("f32.open", () => MachineAvailability("f32.open", entry.Owner, entry.Window), () => settings.Run("f32.open")));
                actions.Add(("f33.open", () => MachineAvailability("f33.open", entry.Owner, entry.Window), () => settings.Run("f33.open")));
                actions.Add(("f35.open", () => MachineAvailability("f35.open", entry.Owner, entry.Window), () => MachineTools.OpenMaintenanceFor(entry.Owner, 35)));
            }
            else
                foreach (string key in new[] { "f05.open", "f07.open", "f08.open", "f22.open", "f24.choose", "f25.open", "f26.open", "f29.open", "f13.groups" })
                { string action = key; actions.Add((action, () => settings.Evaluate(action), () => settings.Run(action))); }
            var builder = CustomUIManager.Window(ModEntry.Id + ".context", ShopSettings.Text("visual.tools"), CustomUIManager.LAYER_META)
                .SetSize(340, Mathf.Min(480, 55 + actions.Count * 68)).SetDraggable(true).SetCloseOnEscape(true);
            contextCard = builder.GetWindow(); contextOwner = entry.Window;
            CustomUIWindow own = contextCard;
            builder.OnClose(() =>
            {
                if (ReferenceEquals(contextCard, own)) { contextCard = null; contextOwner = null; cardActions.Clear(); }
            });
            builder.AddButton(ShopSettings.Text("visual.attention"), () =>
            {
                if (!CanInteract() || !VisibleInventoryViews.Visible(entry.Window)) { CloseContextCard(); return; }
                CloseContextCard(); AttentionBook.Open();
            }, "attention");
            builder.BeginScrollFlexible(120);
            FeatureCategory? previousCategory = null;
            foreach (var action in actions.OrderBy(value => FeatureCatalog.For(value.key).Category).ThenBy(value => FeatureCatalog.Order(value.key)))
            {
                FeatureCategory category = FeatureCatalog.For(action.key).Category;
                if (!entry.Machine && category != previousCategory)
                {
                    builder.AddLabel(ShopSettings.Text(FeatureCatalog.CategoryKey(category)));
                    previousCategory = category;
                }
                Action run = action.run;
                Func<ActionAvailability> evaluate = action.availability;
                var cardAction = new CardAction { Evaluate = evaluate };
                builder.AddButton(ShopSettings.Text(action.key), () =>
                {
                    if (!CanInteract() || !VisibleInventoryViews.Visible(entry.Window)) { CloseContextCard(); return; }
                    if (entry.Machine && (entry.Owner == null || !ShopInventory.Owned(entry.Owner) || entry.Owner.contentWindow != entry.Window)) { CloseContextCard(); return; }
                    var current = evaluate();
                    if (!current.Allowed) { CloseContextCard(); ShopNotice.Show(current.Message); return; }
                    entry.Window.ToFront(); CloseContextCard(); run();
                }, action.key);
                cardAction.Button = builder.Last;
                builder.BeginRow();
                cardAction.DetailRow = builder.CurrentContainer.gameObject;
                builder.AddLabel("", "reason_" + action.key);
                cardAction.Reason = builder.Last;
                builder.AddButton(ShopSettings.Text("settings.open"), () =>
                {
                    var current = evaluate();
                    if (!current.Allowed && current.SettingKey != null) { CloseContextCard(); settings.OpenSetting(current.SettingKey); }
                    else RefreshContextCard();
                }, "setting_" + action.key);
                cardAction.SettingButton = builder.Last;
                builder.End();
                cardActions.Add(cardAction);
            }
            builder.End().Show();
            RefreshContextCard();
            var rect = contextCard.root.transform as RectTransform;
            if (rect?.parent is RectTransform parent && RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,
                (Vector2)Input.mousePosition + new Vector2(10, -10), CanvasCamera(parent), out Vector2 local))
            {
                rect.anchorMin = rect.anchorMax = parent.pivot;
                rect.pivot = new Vector2(0, 1); rect.anchoredPosition = local;
                ClampToScreen(rect, parent);
            }
        }

        private void CloseContextCard()
        {
            CustomUIWindow previous = contextCard; contextCard = null; contextOwner = null;
            cardActions.Clear();
            if (previous != null && previous.IsAlive) previous.Close();
        }

        private void RefreshContextCard()
        {
            if (contextCard?.IsAlive != true || contextOwner == null || !VisibleInventoryViews.Visible(contextOwner)) return;
            foreach (var action in cardActions)
            {
                ActionAvailability availability = action.Evaluate();
                ShopSettings.UpdateAvailabilityControls(action.Button, action.Reason, action.SettingButton, availability);
                if (action.DetailRow != null && action.DetailRow.activeSelf == availability.Allowed)
                    action.DetailRow.SetActive(!availability.Allowed);
            }
        }

        private void RefreshPanelButtons()
        {
            var desired = new HashSet<string>();
            InspectionUIManager inspect = InspectionUIManager.Instance;
            if (inspect?.appraisalPanel != null && inspect.appraisalPanel.activeInHierarchy && settings.CanRun("f23.open"))
                EnsurePanelShortcut(desired, "ShopAssist_AppraisalTools", inspect.panel.transform as RectTransform, "f23.open", 0,
                    inspect.appraisalPanel.transform as RectTransform);

            PCUIManager pc = PCUIManager.Instance;
            if (pc?.panel != null && pc.panel.activeInHierarchy)
            {
                int index = 0;
                if (settings.CanRun("f27.open")) EnsurePanelShortcut(desired, "ShopAssist_RequestTools", pc.panel.transform as RectTransform, "f27.open", index++);
                if (settings.CanRun("f28.open")) EnsurePanelShortcut(desired, "ShopAssist_LedgerTools", pc.panel.transform as RectTransform, "f28.open", index);
            }

            CustomUIWindow coordinator = CustomUIManager.Instance?.GetWindow("storage_coordinator");
            if (coordinator?.IsVisible == true && settings.CanRun("f13.groups"))
                EnsurePanelShortcut(desired, "ShopAssist_CoordinatorTools", coordinator.Rect, "f13.groups", 0);

            NegociationUIManager negotiation = NegociationUIManager.Instance;
            NegotiationTabletFrame frame = negotiation?.uiObject != null ? negotiation.uiObject.GetComponent<NegotiationTabletFrame>() : null;
            if (negotiation != null && negotiation.IsOpen && frame?.panel != null && frame.panel.gameObject.activeInHierarchy && settings.CanRun("f34.open"))
                EnsurePanelShortcut(desired, "ShopAssist_CustomerBasket", frame.panel, "f34.open", customerButton != null ? 1 : 0);

            foreach (string id in panelButtons.Keys.ToArray())
                if (!desired.Contains(id)) RemovePanelShortcut(id);
        }

        private void EnsurePanelShortcut(HashSet<string> desired, string name, RectTransform parent, string command, int index, RectTransform anchor = null)
        {
            if (parent == null || !parent.gameObject.activeInHierarchy) return;
            anchor = anchor != null ? anchor : parent;
            desired.Add(name);
            if (panelButtons.TryGetValue(name, out PanelShortcut old) && old.Parent == parent && old.Anchor == anchor && old.Root != null)
            {
                old.Index = index;
                TMP_Text existing = old.Root.GetComponentInChildren<TMP_Text>(true);
                if (existing != null) existing.text = ShopSettings.Text(command);
                return;
            }
            RemovePanelShortcut(name);
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
            control.onClick = new Button.ButtonClickedEvent();
            control.onClick.AddListener(() =>
            {
                if (CanInteract() && parent != null && parent.gameObject.activeInHierarchy && settings.CanRun(command)) settings.Run(command);
            });
            root.SetActive(true); rect.SetAsLastSibling();
            panelButtons[name] = new PanelShortcut { Root = root, Rect = rect, Parent = parent, Anchor = anchor, Button = control, Command = command, Index = index };
            PositionPanelShortcut(panelButtons[name]);
        }

        private static void PositionPanelShortcut(PanelShortcut entry)
        {
            if (entry.Rect == null || entry.Parent == null) return;
            if (entry.Anchor != null && entry.Anchor != entry.Parent)
            {
                Camera anchorCamera = CanvasCamera(entry.Anchor), parentCamera = CanvasCamera(entry.Parent);
                Vector3[] anchorCorners = new Vector3[4]; entry.Anchor.GetWorldCorners(anchorCorners);
                Vector2 leftTop = RectTransformUtility.WorldToScreenPoint(anchorCamera, anchorCorners[1]);
                Vector2 rightTop = RectTransformUtility.WorldToScreenPoint(anchorCamera, anchorCorners[2]);
                Vector2 parentOrigin = RectTransformUtility.WorldToScreenPoint(parentCamera, entry.Parent.TransformPoint(Vector3.zero));
                Vector2 parentWidth = RectTransformUtility.WorldToScreenPoint(parentCamera, entry.Parent.TransformPoint(new Vector3(entry.Rect.rect.width + 6, 0, 0)));
                float screenWidth = Mathf.Abs(parentWidth.x - parentOrigin.x);
                bool placeRight = Screen.width - rightTop.x >= screenWidth + 4 ||
                    (leftTop.x < screenWidth + 4 && Screen.width - rightTop.x >= leftTop.x);
                Vector2 screen = placeRight ? rightTop + new Vector2(4, -4) : leftTop + new Vector2(-screenWidth - 4, -4);
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(entry.Parent, screen, parentCamera, out Vector2 local))
                {
                    entry.Rect.anchorMin = entry.Rect.anchorMax = entry.Parent.pivot;
                    entry.Rect.pivot = new Vector2(0, 1); entry.Rect.anchoredPosition = local;
                    ClampToScreen(entry.Rect, entry.Parent);
                }
                return;
            }
            Camera camera = CanvasCamera(entry.Parent);
            Vector3[] corners = new Vector3[4]; entry.Parent.GetWorldCorners(corners);
            float left = RectTransformUtility.WorldToScreenPoint(camera, corners[0]).x;
            float right = RectTransformUtility.WorldToScreenPoint(camera, corners[2]).x;
            Vector2 origin = RectTransformUtility.WorldToScreenPoint(camera, entry.Parent.TransformPoint(Vector3.zero));
            Vector2 end = RectTransformUtility.WorldToScreenPoint(camera, entry.Parent.TransformPoint(new Vector3(entry.Rect.rect.width + 6, 0, 0)));
            float width = Mathf.Abs(end.x - origin.x);
            bool useRight = Screen.width - right >= width + 4 || (left < width + 4 && Screen.width - right >= left);
            entry.Rect.anchorMin = entry.Rect.anchorMax = new Vector2(useRight ? 1 : 0, 1);
            entry.Rect.pivot = new Vector2(useRight ? 0 : 1, 1);
            entry.Rect.anchoredPosition = new Vector2(useRight ? 4 : -4, -4 - entry.Index * 32);
            ClampToScreen(entry.Rect, entry.Parent);
        }

        private void RemovePanelShortcut(string id)
        {
            if (!panelButtons.TryGetValue(id, out PanelShortcut entry)) return;
            panelButtons.Remove(id);
            if (entry.Root != null) UnityEngine.Object.Destroy(entry.Root);
        }

        private void RefreshMachines(PixelWindow[] windows)
        {
            var visible = new HashSet<PixelWindow>();
            foreach (PixelWindow window in windows)
                {
                    if (!VisibleInventoryViews.Visible(window) || window.isLocked || window.parentItems == null) continue;
                    GameItem owner = null;
                    foreach (GameItem item in window.parentItems)
                    {
                        if (item == null || !ReferenceEquals(item.contentWindow, window) || !MachineTools.ModulesAvailableFor(item, window)) continue;
                        if (owner != null && !ReferenceEquals(owner, item)) { owner = null; break; }
                        owner = item;
                    }
                    if (owner == null) continue;
                    visible.Add(window);
                    if (machines.TryGetValue(window, out MachineEntry existing) && ReferenceEquals(existing.Machine, owner) && existing.Button?.handler != null)
                    {
                        string label = ShopSettings.Text("f10.context");
                        if (existing.Button.text != label) existing.Button.SetText(label);
                        continue;
                    }
                    if (existing != null) RemoveMachine(existing);
                    GameItem target = owner;
                    var button = new MachineryButton(ShopSettings.Text("f10.context"), () =>
                    {
                        if (CanInteract() && MachineTools.ModulesAvailableFor(target, window)) MachineTools.OpenModulesFor(target);
                    });
                    button.handler.gameObject.name = "ShopAssist_MachineModules";
                    var entry = new MachineEntry { Window = window, Machine = target, Button = button };
                    machines[window] = entry;
                    if (window.style == WindowStyle.Machinery) window.AddHeaderButton(button);
                    else { button.handler.SetParent(window.handler); PositionClassic(entry); }
            }
            foreach (PixelWindow window in machines.Keys.ToArray())
                if (!visible.Contains(window)) RemoveMachine(machines[window]);
        }

        private void RemoveMachine(MachineEntry entry)
        {
            machines.Remove(entry.Window);
            if (entry.Button?.handler == null) return;
            if (entry.Window?.handler != null && entry.Window.style == WindowStyle.Machinery)
                entry.Window.RemoveHeaderButton(entry.Button);
            UnityEngine.Object.Destroy(entry.Button.handler.gameObject);
        }

        private static void PositionClassic(MachineEntry entry)
        {
            if (entry.Window?.handler == null || entry.Button?.handler == null) return;
            var rect = entry.Button.handler.rectTransform;
            // Classic windows do not participate in the native header-button layout.
            rect.anchoredPosition = new Vector2(entry.Window.widthPixels + 3f, -2f);
            ClampToScreen(rect, entry.Window.handler.rectTransform);
        }

        private void RefreshCustomer()
        {
            NegociationUIManager negotiation = NegociationUIManager.Instance;
            NegotiationTabletFrame frame = negotiation?.uiObject != null ? negotiation.uiObject.GetComponent<NegotiationTabletFrame>() : null;
            if (!listings.CanOpen() || negotiation == null || !negotiation.IsOpen || frame?.panel == null ||
                !frame.panel.gameObject.activeInHierarchy)
            {
                RemoveCustomer();
                return;
            }
            if (customerButton != null && ReferenceEquals(customerFrame, frame))
            {
                TMP_Text label = customerButton.GetComponentInChildren<TMP_Text>(true);
                if (label != null) label.text = ShopSettings.Text("f11.context");
                return;
            }
            RemoveCustomer();
            GameObject prefab = CustomUIManager.Instance?.GetPrefab(CustomUIManager.ElementKind.Button, true);
            if (prefab == null) return;
            customerButton = UnityEngine.Object.Instantiate(prefab, frame.panel, false);
            customerButton.name = "ShopAssist_CustomerList";
            customerRect = customerButton.transform as RectTransform;
            customerControl = customerButton.GetComponentInChildren<Button>(true);
            TMP_Text text = customerButton.GetComponentInChildren<TMP_Text>(true);
            if (customerRect == null || customerControl == null || text == null) { RemoveCustomer(); return; }
            customerPanel = frame.panel;
            customerFrame = frame;
            LayoutElement layout = customerButton.GetComponent<LayoutElement>();
            if (layout != null) layout.ignoreLayout = true;
            customerRect.sizeDelta = new Vector2(104f, 28f);
            text.text = ShopSettings.Text("f11.context");
            text.richText = false;
            text.raycastTarget = false;
            customerControl.onClick = new Button.ButtonClickedEvent();
            customerControl.onClick.AddListener(() =>
            {
                if (CanInteract() && listings.CanOpen() && NegociationUIManager.Instance != null && NegociationUIManager.Instance.IsOpen &&
                    (customerFrame?.confirmRoot == null || !customerFrame.confirmRoot.activeInHierarchy)) listings.Open();
            });
            customerButton.SetActive(true);
            customerRect.SetAsLastSibling();
            PositionCustomer();
        }

        private void PositionCustomer()
        {
            if (customerRect == null || customerPanel == null) return;
            Camera camera = CanvasCamera(customerPanel);
            Vector3[] corners = new Vector3[4];
            customerPanel.GetWorldCorners(corners);
            float left = RectTransformUtility.WorldToScreenPoint(camera, corners[0]).x;
            float right = RectTransformUtility.WorldToScreenPoint(camera, corners[2]).x;
            Vector2 origin = RectTransformUtility.WorldToScreenPoint(camera, customerPanel.TransformPoint(Vector3.zero));
            Vector2 widthPoint = RectTransformUtility.WorldToScreenPoint(camera, customerPanel.TransformPoint(new Vector3(customerRect.rect.width + 6f, 0f, 0f)));
            float width = Mathf.Abs(widthPoint.x - origin.x);
            bool useRight = Screen.width - right >= width + 4f || (left < width + 4f && Screen.width - right >= left);
            customerRect.anchorMin = customerRect.anchorMax = new Vector2(useRight ? 1f : 0f, 1f);
            customerRect.pivot = new Vector2(useRight ? 0f : 1f, 1f);
            customerRect.anchoredPosition = new Vector2(useRight ? 4f : -4f, -4f);
            ClampToScreen(customerRect, customerPanel);
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
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            foreach (Vector3 corner in corners)
            {
                Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                minX = Mathf.Min(minX, point.x); maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y); maxY = Mathf.Max(maxY, point.y);
            }
            float dx = minX < 2f ? 2f - minX : maxX > Screen.width - 2f ? Screen.width - 2f - maxX : 0f;
            float dy = minY < 2f ? 2f - minY : maxY > Screen.height - 2f ? Screen.height - 2f - maxY : 0f;
            if (dx == 0f && dy == 0f) return;
            Vector2 pointOnScreen = RectTransformUtility.WorldToScreenPoint(camera, rect.position);
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, pointOnScreen, camera, out Vector2 before) &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, pointOnScreen + new Vector2(dx, dy), camera, out Vector2 after))
                rect.anchoredPosition += after - before;
        }

        private static bool CanInteract()
        {
            StoreUIManager ui = StoreUIManager.Instance;
            return ShopInventory.InStore && ui != null && !StoreUIManager.IsAnyTyping() && !KeyBindHelper.IsCapturing &&
                !UIDragHandler.isDraggingAnyUI && ItemMouseDragHandler.current?.IsDraggingItem != true &&
                (ui.gameOptionUIManager == null || !ui.gameOptionUIManager.IsOpen) &&
                (ui.escapeUIManager == null || !ui.escapeUIManager.menuPanel.activeInHierarchy) &&
                (ui.blackscreen == null || !ui.blackscreen.gameObject.activeInHierarchy) &&
                (ui.controlHintOverlay == null || !ui.controlHintOverlay.activeInHierarchy) &&
                (ui.closeShutterConfirmationPanel == null || !ui.closeShutterConfirmationPanel.activeInHierarchy) &&
                (GenericConfirmationUIManager.Instance == null || !GenericConfirmationUIManager.Instance.panel.activeInHierarchy) &&
                (ui.confirmPanelUIManager == null || !ui.confirmPanelUIManager.panel.activeInHierarchy);
        }

        private void RemoveCustomer()
        {
            if (customerButton != null) UnityEngine.Object.Destroy(customerButton);
            customerButton = null; customerRect = null; customerControl = null; customerPanel = null; customerFrame = null;
        }
        private void ResetControls()
        {
            foreach (MachineEntry entry in machines.Values.ToArray()) RemoveMachine(entry);
            foreach (ToolEntry entry in toolButtons.Values.ToArray()) RemoveTool(entry);
            foreach (string id in panelButtons.Keys.ToArray()) RemovePanelShortcut(id);
            CloseContextCard();
            RemoveCustomer();
            dirty = true;
        }
        private void Reset() { ResetControls(); lastError = null; }
        public void Dispose()
        {
            if (!started) return;
            started = false;
            InventoryContextActions.Unregister("f22.open");
            InventoryContextActions.Unregister("f24.choose");
            InventoryContextActions.Unregister("f25.open");
            InventoryContextActions.Unregister("f29.open");
            settings.Changed -= Invalidate;
            VisibleInventoryViews.Changed -= Invalidate;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
        }
    }
}
