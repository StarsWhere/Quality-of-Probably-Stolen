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
    internal sealed class ContextShortcuts : IDisposable
    {
        private static ContextShortcuts current;
        private static readonly Vector3[] tooltipCorners = new Vector3[4];
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
            internal string Key;
            internal Func<ActionAvailability> Evaluate;
            internal CustomUIElement Button;
        }

        private readonly ShopSettings settings;
        private readonly CustomerInventory listings;
        private readonly Action<string> log;
        private readonly Dictionary<PixelWindow, MachineEntry> machines = new Dictionary<PixelWindow, MachineEntry>();
        private readonly Dictionary<PixelWindow, ToolEntry> toolButtons = new Dictionary<PixelWindow, ToolEntry>();
        private readonly Dictionary<string, PanelShortcut> panelButtons = new Dictionary<string, PanelShortcut>();
        private readonly List<CardAction> cardActions = new List<CardAction>();
        private CustomUIWindow contextCard;
        private CustomUIElement contextEmpty, contextShowDisabled;
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
            current = this;
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
                    bool storage = ReferenceEquals(window, EmporiumEntry.Instance.invWindow) || owner != null && ShopInventory.SafeContainerForPeek(owner);
                    bool machine = !storage && owner != null && owner.contentWindow?.childElement is MachineryPanelRow;
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
            OverlayHandler.current?.HideTooltip();
            entry.Window.ToFront();
            CloseContextCard();
            var actions = new List<(string key, Func<ActionAvailability> availability, Action run)>();
            if (entry.Machine)
            {
                if (MachineTools.SupportsContextAction("f10.open", entry.Owner))
                    actions.Add(("f10.open", () => MachineAvailability("f10.open", entry.Owner, entry.Window), () => MachineTools.OpenModulesFor(entry.Owner)));
                if (MachineTools.SupportsContextAction("f31.open", entry.Owner))
                    actions.Add(("f31.open", () => MachineAvailability("f31.open", entry.Owner, entry.Window), () => MachineTools.OpenMaintenanceFor(entry.Owner, 31)));
                if (MachineTools.SupportsContextAction("f32.open", entry.Owner))
                    actions.Add(("f32.open", () => MachineAvailability("f32.open", entry.Owner, entry.Window), () => MachineTools.OpenReplantFor(entry.Owner)));
                if (MachineTools.SupportsContextAction("f33.open", entry.Owner))
                    actions.Add(("f33.open", () => MachineAvailability("f33.open", entry.Owner, entry.Window), () => MachineTools.OpenFeedFor(entry.Owner)));
                if (MachineTools.SupportsContextAction("f35.open", entry.Owner))
                    actions.Add(("f35.open", () => MachineAvailability("f35.open", entry.Owner, entry.Window), () => MachineTools.OpenMaintenanceFor(entry.Owner, 35)));
            }
            else
                foreach (string key in new[] { "f05.open", "f07.open" })
                {
                    string action = key;
                    GameGridInventory target = ContextGrid(entry);
                    actions.Add((action, () => target != null && ReferenceEquals(ContextGrid(entry), target)
                            ? InventoryContextActions.EvaluateGrid(action, target)
                            : ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.object"),
                        () => InventoryContextActions.RunGrid(action, target)));
                }
            string objectName = entry.Owner != null ? entry.Owner.GetDisplayName(true) : ShopSettings.Text("inv.stock");
            Vector2 size = UiLayout.Fit(390, Mathf.Min(430, 185 + actions.Count * 44));
            var builder = CustomUIManager.Window(ModEntry.Id + ".context", ShopSettings.Text("visual.object_tools") + " · " + objectName, CustomUIManager.LAYER_META)
                .SetSize(size.x, size.y).SetDraggable(true).SetCloseOnEscape(true);
            contextCard = builder.GetWindow(); contextOwner = entry.Window;
            CustomUIWindow own = contextCard;
            if (own.frame?.titleText != null) { own.frame.titleText.richText = false; own.frame.titleText.fontSize = 19; own.frame.titleText.enableAutoSizing = false; }
            builder.OnClose(() =>
            {
                if (ReferenceEquals(contextCard, own)) { contextCard = null; contextOwner = null; contextEmpty = contextShowDisabled = null; cardActions.Clear(); }
            });
            builder.AddToggle(ShopSettings.Text("visual.show_disabled"), settings.ShowDisabledTools, value =>
            {
                settings.ShowDisabledToolsChanged(value); RefreshContextCard();
            }, "show_disabled");
            contextShowDisabled = builder.Last;
            builder.Last.SetPreferredSize(-1, 38);
            if (builder.Last.text != null) { builder.Last.text.fontSize = 19; builder.Last.text.enableAutoSizing = false; }
            builder.BeginScrollFlexible(80);
            foreach (var action in actions.OrderBy(value => FeatureCatalog.For(value.key).Category).ThenBy(value => FeatureCatalog.Order(value.key)))
            {
                Action run = action.run;
                Func<ActionAvailability> evaluate = action.availability;
                var cardAction = new CardAction { Key = action.key, Evaluate = evaluate };
                builder.AddButton(ShopSettings.Text(action.key), () =>
                {
                    if (!CanInteract() || !VisibleInventoryViews.Visible(entry.Window)) { CloseContextCard(); return; }
                    if (entry.Machine && (entry.Owner == null || !ShopInventory.Owned(entry.Owner) || entry.Owner.contentWindow != entry.Window)) { CloseContextCard(); return; }
                    var current = evaluate();
                    if (!current.Allowed) { CloseContextCard(); ShopNotice.Show(current.Message); return; }
                    entry.Window.ToFront(); CloseContextCard(); run();
                }, action.key);
                cardAction.Button = builder.Last;
                ShopSettings.StyleToolRow(builder.Last);
                cardActions.Add(cardAction);
            }
            builder.AddLabel(ShopSettings.Text("visual.no_object_actions"), "empty"); contextEmpty = builder.Last;
            builder.End().AddButton(ShopSettings.Text("visual.manage"), () =>
            {
                CloseContextCard(); settings.OpenToolSettings(entry.Machine ? 2 : 0);
            }, "manage");
            ShopSettings.StyleToolRow(builder.Last);
            builder.Show();
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

        private static GameGridInventory ContextGrid(ToolEntry entry)
        {
            if (entry?.Window == null || entry.Window.isLocked || !VisibleInventoryViews.Visible(entry.Window)) return null;
            if (entry.Owner != null)
            {
                if (!ShopInventory.Owned(entry.Owner) || !ReferenceEquals(entry.Owner.contentWindow, entry.Window)) return null;
                if (entry.Window.childElement is GameGridInventory grid) return grid;
                var visible = ShopInventory.OwnedLocations(true)
                    .Where(location => ReferenceEquals(location.Owner, entry.Owner) && location.Ordinary && !location.IsMachine)
                    .Select(location => location.Inventory as GameGridInventory).Where(inventory => inventory != null).Distinct().Take(2).ToArray();
                return visible.Length == 1 ? visible[0] : null;
            }
            return ReferenceEquals(entry.Window, EmporiumEntry.Instance?.invWindow) ? EmporiumEntry.Instance.invElement : null;
        }

        private void CloseContextCard()
        {
            CustomUIWindow previous = contextCard; contextCard = null; contextOwner = null;
            contextEmpty = contextShowDisabled = null;
            cardActions.Clear();
            if (previous != null && previous.IsAlive) previous.Close();
        }

        private void RefreshContextCard()
        {
            if (contextCard?.IsAlive != true || contextOwner == null || !VisibleInventoryViews.Visible(contextOwner)) return;
            if (contextShowDisabled?.toggle != null && contextShowDisabled.GetBool() != settings.ShowDisabledTools)
                contextShowDisabled.SetBool(settings.ShowDisabledTools, false);
            int shown = 0;
            foreach (var action in cardActions)
            {
                bool visible = settings.ShowDisabledTools || settings.FeatureAvailability(action.Key).Allowed;
                if (action.Button?.root != null && action.Button.root.activeSelf != visible) action.Button.SetVisible(visible);
                if (!visible) continue;
                shown++;
                ActionAvailability availability = action.Evaluate();
                ShopSettings.UpdateActionButton(action.Button, availability);
            }
            contextEmpty?.SetVisible(shown == 0);
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

        private static bool OverToolWindow(CustomUIWindow window, Vector2 point)
        {
            if (window?.IsAlive != true || !window.root.activeInHierarchy || window.Rect == null) return false;
            window.Rect.GetWorldCorners(tooltipCorners);
            Camera camera = CanvasCamera(window.Rect);
            Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, tooltipCorners[0]);
            Vector2 high = RectTransformUtility.WorldToScreenPoint(camera, tooltipCorners[2]);
            const float margin = 20;
            return point.x >= low.x - margin && point.x <= high.x + margin &&
                point.y >= low.y - margin && point.y <= high.y + margin;
        }

        [HarmonyPatch(typeof(ItemTooltipHandler), "OnEventUpdate")]
        private static class BlockToolBackgroundTooltip
        {
            private static bool Prefix(ref bool __result)
            {
                Vector2 point = Input.mousePosition;
                if (!OverToolWindow(current?.contextCard, point) && !OverToolWindow(ShopSettings.OpenToolsWindow, point)) return true;
                OverlayHandler.current?.HideTooltip();
                __result = false;
                return false;
            }
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
            if (ReferenceEquals(current, this)) current = null;
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
