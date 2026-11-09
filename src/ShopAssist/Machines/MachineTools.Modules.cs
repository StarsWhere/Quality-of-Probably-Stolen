using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private sealed class ModuleRow
        {
            internal GameItem Item;
            internal GameItem Machine;
            internal string Path;
            internal int Compatibility;
            internal bool Installed;
            internal GameGridInventory Grid;
        }

        [Serializable]
        private sealed class ModuleStorageBinding
        {
            [Newtonsoft.Json.JsonRequired] public int version = 1;
            [Newtonsoft.Json.JsonRequired] public string location;
        }
        private const string ModuleStorageKey = "module_storage";

        private ConfigEntry<bool> modulesEnabled;
        private Func<KeyCode> modulesKey;
        private CustomUIWindow modulesWindow;
        private RectTransform moduleResults, moduleIdleGrid;
        private ScrollRect moduleSourceScroll, moduleBayScroll;
        private static readonly FieldInfo ModuleWindowElements = AccessTools.Field(typeof(CustomUIWindow), "elements");
        private GameItem moduleTarget;
        private GameInventory moduleBay;
        private string modulesRun;
        private int modulesSlot;
        private bool modulesDirty, moduleFilterDirty, compatibleOnly;
        private int modulePage;
        private int moduleAttribute, moduleTab, moduleColumns, moduleVisibleRows, moduleDockRows;
        private float moduleWindowWidth;
        private readonly HashSet<GameItem> selectedModules = new HashSet<GameItem>();
        private readonly HashSet<GameItem> selectedInstalledModules = new HashSet<GameItem>();

        // Selection is session-local and is always revalidated by a future consumer.
        internal IReadOnlyCollection<GameItem> SelectedModules => selectedModules;

        private void InitModuleBrowser()
        {
            modulesEnabled = settings.Toggle("f10.enabled", "f10.tip");
            modulesKey = settings.Key("f10.key", "f10.key.tip");
            settings.Action("f10.open", OpenModules, ModulesAvailable);
        }

        private bool ModulesAvailable() => modulesEnabled.Value && ShopInventory.InStore && FindTarget(out _, out _);

        private static bool FindTarget(out GameItem machine, out GameInventory bay) => FindTarget(null, out machine, out bay);

        private static bool FindTarget(GameItem requested, out GameItem machine, out GameInventory bay)
        {
            machine = null; bay = null;
            if (WindowsHandler.current?.visibleWindows == null) return false;
            if (requested != null)
                return FindWindowTarget(requested.contentWindow, requested, out machine, out bay);
            long newest = long.MinValue;
            foreach (PixelWindow window in WindowsHandler.current.visibleWindows)
            {
                if (!FindWindowTarget(window, null, out GameItem owner, out GameInventory found, true) || window.focusStamp < newest) continue;
                newest = window.focusStamp; machine = owner; bay = found;
            }
            return machine != null;
        }

        private static bool FindWindowTarget(PixelWindow window, GameItem requested, out GameItem machine, out GameInventory bay, bool inVisibleList = false)
        {
            machine = null; bay = null;
            if (window == null || WindowsHandler.current?.visibleWindows == null || !inVisibleList && !WindowsHandler.current.visibleWindows.Contains(window) ||
                !window.IsVisible() || window.isLocked || window.handler == null || !window.handler.gameObject.activeInHierarchy ||
                window.parentItems == null || !(window.childElement is MachineryPanelRow row)) return false;
            GameItem owner = null;
            foreach (GameItem candidate in window.parentItems)
            {
                if (candidate == null || !ReferenceEquals(candidate.contentWindow, window) || !ShopInventory.Owned(candidate)) continue;
                if (owner != null && !ReferenceEquals(owner, candidate)) return false;
                owner = candidate;
            }
            if (owner == null || requested != null && !ReferenceEquals(owner, requested)) return false;
            GameInventory found = null;
            foreach (MachineryPanel panel in row.Panels)
            {
                if (panel == null || panel.isHidden || panel.role != "module" || panel.content == null) continue;
                var nodes = MachineryContainerUtils.Collect(panel.content);
                if (nodes == null || nodes.Count != 1 || !(nodes[0] is GameGridInventory inventory) || found != null) return false;
                found = inventory;
            }
            if (found == null) return false;
            machine = owner; bay = found; return true;
        }

        private void OpenModules()
        {
            if (!modulesEnabled.Value || !ShopInventory.InStore || CustomUIManager.Instance == null ||
                !FindTarget(out GameItem target, out GameInventory bay)) return;
            OpenModulesResolved(target, bay);
        }

        internal static bool ModuleContextApplies(GameItem target, PixelWindow window)
        {
            if (target == null || !ShopInventory.Owned(target) || !ReferenceEquals(target.contentWindow, window) ||
                !(window?.childElement is MachineryPanelRow row)) return false;
            foreach (MachineryPanel panel in row.Panels)
            {
                if (panel == null || panel.role != "module" || panel.content == null) continue;
                var nodes = MachineryContainerUtils.Collect(panel.content);
                if (nodes != null && nodes.Any(node => node is GameGridInventory)) return true;
            }
            return false;
        }

        internal static ActionAvailability ModuleContextAvailability(GameItem target, PixelWindow window)
        {
            if (current?.started != true) return ActionAvailability.Blocked(ActionUnavailableReason.Unknown, "availability.unknown");
            if (!current.modulesEnabled.Value) return ActionAvailability.Blocked(ActionUnavailableReason.Disabled, "availability.disabled", "f10.enabled");
            if (!ModuleContextApplies(target, window) || !ShopInventory.InStore)
                return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.object");
            if (window.isLocked || LockHelper.IsCurrentlyLocked(target))
                return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.module_locked");
            if (!FindWindowTarget(window, target, out _, out GameInventory bay))
                return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.module_unavailable");
            if (bay.IsInsertLocked() || bay.IsRemoveLocked())
                return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.module_locked");
            if (CustomUIManager.Instance == null)
                return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.object");
            return ActionAvailability.Ready;
        }

        internal static void OpenModulesFor(GameItem target)
        {
            if (target == null || current?.started != true || !current.modulesEnabled.Value || !ShopInventory.InStore ||
                CustomUIManager.Instance == null || !FindWindowTarget(target.contentWindow, target, out GameItem owner, out GameInventory bay)) return;
            owner.contentWindow.ToFront();
            current.OpenModulesResolved(owner, bay);
        }

        private void OpenModulesResolved(GameItem target, GameInventory bay)
        {
            CloseModules();
            moduleTarget = target; moduleBay = bay; modulesRun = PlayerStore.Instance.runID; modulesSlot = PlayerStore.Instance.saveSlotId;
            RenderModules();
        }

        private void TickModuleBrowser()
        {
            if (!modulesEnabled.Value) { if (modulesWindow != null) CloseModules(); return; }
            if (modulesWindow != null)
            {
                if (!modulesWindow.IsAlive || !SameModuleSession()) { CloseModules(); return; }
                if ((modulesDirty || moduleFilterDirty) && !ItemGrid.DragActive) RenderModules(moduleFilterDirty && !modulesDirty);
            }
            KeyCode key = modulesKey();
            if (key != KeyCode.None && ShopInput.Ready() && Input.GetKeyDown(key)) OpenModules();
        }

        private bool SameModuleSession() => ShopInventory.InStore && PlayerStore.Instance.runID == modulesRun &&
            PlayerStore.Instance.saveSlotId == modulesSlot && moduleTarget != null && ShopInventory.Owned(moduleTarget) &&
            moduleTarget.contentWindow != null && moduleTarget.contentWindow.IsVisible() && !moduleTarget.contentWindow.isLocked && moduleBay != null;

        private void ModuleSettingsChanged() { if (!modulesEnabled.Value) CloseModules(); else modulesDirty = true; }

        private void RenderModules(bool filterOnly = false)
        {
            if (!SameModuleSession()) { CloseModules(); return; }
            if (ItemGrid.DragActive) { modulesDirty = true; return; }
            modulesDirty = moduleFilterDirty = false;
            var locations = ModuleLocations();
            List<ModuleRow> rows = ModuleRows(locations);
            List<ShopInventory.Location> choices = ModuleStorageChoices();
            ShopInventory.Location storage = BoundModuleStorage(choices);
            var available = new HashSet<GameItem>(rows.Where(row => !row.Installed).Select(row => row.Item));
            selectedModules.RemoveWhere(item => !available.Contains(item));
            selectedInstalledModules.RemoveWhere(item => item == null || !ShopInventory.Owned(item));
            var matching = rows.Where(r => r.Installed == (moduleTab == 1) && (!compatibleOnly || r.Compatibility > 0) &&
                (moduleAttribute == 0 || ModuleAttributeValue(r.Item, moduleAttribute) > 0)).ToList();
            var displayed = OrderModules(matching).ToList();
            bool filtered = compatibleOnly || moduleAttribute != 0;
            var bays = locations.Where(location => location.IsMachine && location.Role == "module" &&
                location.Inventory is GameGridInventory && !ReferenceEquals(location.Inventory, moduleBay) &&
                (!filtered || displayed.Any(row => ReferenceEquals(row.Grid, location.Inventory))))
                .OrderByDescending(location => moduleAttribute == 0 ? 0 : displayed.Where(row => ReferenceEquals(row.Grid, location.Inventory))
                    .Select(row => ModuleAttributeValue(row.Item, moduleAttribute)).DefaultIfEmpty(0).Max())
                .ThenBy(location => location.Name, StringComparer.CurrentCulture).ToList();
            var idleEntries = displayed.Select(ModuleEntry).ToList();
            int idleWidth = rows.Where(row => !row.Installed).Sum(row => row.Item.modifiedShape?.globalWidth ?? 0);
            int dockRows = moduleDockRows;
            float extraHeight = filterOnly ? FilterHeightHint(idleEntries, bays, storage, dockRows, idleWidth) : 0;
            float previewCell = 0;
            for (int reflows = 0; reflows <= 4; reflows++)
                if (!RenderModuleResults(rows, displayed, bays, idleEntries, idleWidth, storage,
                    ref extraHeight, ref previewCell, ref dockRows)) break;
            moduleDockRows = dockRows;
        }

        private float FilterHeightHint(List<ItemGrid.Entry> entries, List<ShopInventory.Location> bays,
            ShopInventory.Location storage, int dockRows, int idleWidth)
        {
            if (moduleTab != 0 || layoutOpen || modulesWindow?.IsAlive != true || moduleSourceScroll?.viewport == null ||
                moduleColumns < 1 || dockRows < 1) return 0;
            if (moduleSourceScroll.horizontalScrollbar != null || entries.Any(entry => (entry.Shape?.globalWidth ?? 0) > moduleColumns)) return 0;
            Vector2 size = ModuleWindowSize(out Vector2 available, entries, bays, storage, dockRows, idleWidth);
            if (Mathf.Abs(modulesWindow.Rect.rect.width - size.x) > .5f) return 0;
            float maxHeight = Mathf.Max(1, Mathf.Min(available.y * .8f, available.y - 32));
            float remaining = maxHeight - size.y;
            if (remaining <= .5f) return 0;
            // Reuse only the measured window chrome; the current contents are packed again.
            float chrome = modulesWindow.Rect.rect.height - moduleSourceScroll.viewport.rect.height;
            if (chrome < 0 || chrome >= modulesWindow.Rect.rect.height) return 0;
            float viewport = size.y - chrome;
            if (viewport <= 0) return 0;
            float rightWidth = ModuleDockWidth(Mathf.Max(80, size.x - 32));
            int rowLimit = Mathf.Max(1, Mathf.CeilToInt((viewport + remaining) / 34));
            float needed = ItemGrid.PackedRows(entries, moduleColumns,
                ModuleDockArea(moduleColumns, rightWidth, dockRows), rowLimit) * 34 + 8;
            float deficit = needed - viewport;
            return deficit > .5f ? Mathf.Min(deficit + 2, remaining) : 0;
        }

        private bool RenderModuleResults(List<ModuleRow> rows, List<ModuleRow> displayed, List<ShopInventory.Location> bays,
            List<ItemGrid.Entry> idleEntries, int idleWidth, ShopInventory.Location storage,
            ref float extraHeight, ref float previewCell, ref int dockRows)
        {
            if (!SameModuleSession()) { CloseModules(); return false; }
            if (ItemGrid.DragActive) { modulesDirty = true; return false; }
            Vector2 size = ModuleWindowSize(out Vector2 availableSize, idleEntries, bays, storage, dockRows, idleWidth);
            float maxHeight = Mathf.Max(1, Mathf.Min(availableSize.y * .8f, availableSize.y - 32));
            size.y = Mathf.Min(maxHeight, size.y + extraHeight);
            float bodyWidth = Mathf.Max(80, size.x - 32);
            bool wrap = moduleTab == 0;
            float rightWidth = ModuleDockWidth(bodyWidth);
            float leftWidth = wrap ? bodyWidth : bodyWidth - 8 - rightWidth;
            Vector2 sourcePosition = moduleSourceScroll != null ? moduleSourceScroll.normalizedPosition : new Vector2(0, 1);
            Vector2 bayPosition = moduleBayScroll != null ? moduleBayScroll.normalizedPosition : new Vector2(0, 1);
            if (modulesWindow != null && !ClearModuleResults())
            {
                CustomUIWindow old = modulesWindow;
                modulesWindow = null; moduleResults = moduleIdleGrid = null;
                old.Close();
            }
            if (modulesWindow == null)
            {
                var shell = CustomUIManager.Window(ModEntry.Id + ".f10", ShopSettings.Text("f10.open") + " · " + moduleTarget.GetDisplayName(true), CustomUIManager.LAYER_META)
                    .SetSize(size.x, size.y).SetDraggable(true).SetCloseOnEscape(true);
                Vector2 desired = new Vector2(0, (availableSize.y - size.y) / 2 - 16);
                float x = Mathf.Max(0, (availableSize.x - size.x) / 2 - 16);
                float y = Mathf.Max(0, (availableSize.y - size.y) / 2 - 16);
                shell.SetPosition(new Vector2(Mathf.Clamp(desired.x, -x, x), Mathf.Clamp(desired.y, -y, y)));
                modulesWindow = shell.GetWindow();
                CustomUIWindow own = modulesWindow;
                shell.OnClose(() => { if (ReferenceEquals(modulesWindow, own)) { modulesWindow = null; moduleResults = moduleIdleGrid = null; moduleSourceScroll = moduleBayScroll = null; moduleTarget = null; moduleBay = null; moduleDockRows = 0; modulesDirty = moduleFilterDirty = false; selectedModules.Clear(); selectedInstalledModules.Clear(); CloseLayout(); } });
                if (modulesWindow.frame?.titleText != null) modulesWindow.frame.titleText.richText = false;
                DrawModuleToolbar(shell, bodyWidth);
                shell.BeginColumn(); moduleResults = shell.CurrentContainer; FlexibleHeight(moduleResults); shell.End();
                shell.Show();
            }
            else
            {
                RectTransform rect = modulesWindow.Rect;
                Vector2 position = rect.anchoredPosition + new Vector2((size.x - rect.rect.width) * .5f, (rect.rect.height - size.y) * .5f);
                modulesWindow.SetSize(size.x, size.y);
                float x = Mathf.Max(0, (availableSize.x - size.x) / 2 - 16);
                float y = Mathf.Max(0, (availableSize.y - size.y) / 2 - 16);
                rect.anchoredPosition = new Vector2(Mathf.Clamp(position.x, -x, x), Mathf.Clamp(position.y, -y, y));
            }
            if (modulesWindow.frame?.titleText != null)
                modulesWindow.frame.titleText.text = ShopSettings.Text("f10.open") + " · " + moduleTarget.GetDisplayName(true);
            RefreshModuleToolbar();
            var builder = new CustomUIBuilder(CustomUIManager.Instance, modulesWindow, moduleResults);
            DrawModuleFooter(builder, storage, bodyWidth);
            int columns = moduleColumns > 0 ? moduleColumns : Mathf.Max(1, Mathf.FloorToInt((leftWidth - 22) / 34));
            int visibleRows = moduleVisibleRows > 0 ? moduleVisibleRows : Mathf.Max(1,
                Mathf.FloorToInt((size.y - 140 - (ModuleFooterRows(bodyWidth) - 1) * 40) / 34));
            RectInt reserved = wrap ? ModuleDockArea(columns, rightWidth, dockRows) : default;
            List<List<ItemGrid.Entry>> idlePages = ItemGrid.Paginate(idleEntries, columns, visibleRows, reserved);
            int pages = moduleTab == 1 ? Math.Max(1, bays.Count) : moduleTab == 0 ? idlePages.Count : 1;
            modulePage = Math.Max(0, Math.Min(modulePage, pages - 1));
            builder.BeginRow(8);
            FlexibleHeight(builder.CurrentContainer);
            builder.BeginColumn(); FixedColumn(builder.CurrentContainer, leftWidth);
            DrawSourceHeader(builder, moduleTab == 1 && bays.Count > 0 ? bays[modulePage].Name :
                moduleTab == 2 ? ShopSettings.Text("grid.tab_storage") : ShopSettings.Text("grid.tab_idle"), modulePage, pages);
            builder.BeginScrollFlexible(60); RectTransform sourceContent = builder.CurrentContainer;
            RectTransform bayContent = null, dock = null;
            if (moduleTab == 0)
            {
                var entries = idlePages[modulePage];
                ItemGrid.Options options = ModuleGridOptions(leftWidth);
                options.Columns = columns;
                options.Inventory = storage?.Inventory as GameGridInventory;
                options.Proxy = options.Inventory != null;
                options.Reserved = reserved;
                RectTransform grid = ItemGrid.Add(builder, entries, options);
                moduleIdleGrid = grid;
                builder.BeginColumn(); dock = builder.CurrentContainer;
                dock.name = "ShopAssist_ModuleDock";
                FixedColumn(dock, reserved.width * 34 - 8);
                dock.GetComponent<LayoutElement>().ignoreLayout = true;
                dock.SetParent(grid, false); dock.anchorMin = dock.anchorMax = dock.pivot = new Vector2(0, 1);
                dock.anchoredPosition = new Vector2(reserved.x * 34 + 4, -4);
                dock.sizeDelta = new Vector2(reserved.width * 34 - 8, reserved.height * 34 - 8);
                bayContent = DrawCurrentModuleBay(builder, dock.sizeDelta.x, rows, previewCell, false);
                builder.End();
                if (entries.Count == 0) builder.AddLabel(ShopSettings.Text("f10.empty"));
            }
            else if (moduleTab == 1 && bays.Count > 0)
            {
                var grid = (GameGridInventory)bays[modulePage].Inventory;
                DrawRealModuleBoard(builder, grid, leftWidth, rows, new HashSet<GameItem>(displayed.Select(row => row.Item)));
            }
            else if (moduleTab == 2 && storage?.Inventory is GameGridInventory storageGrid)
                DrawRealModuleBoard(builder, storageGrid, leftWidth, rows,
                    compatibleOnly || moduleAttribute != 0 ? new HashSet<GameItem>(displayed.Select(row => row.Item)) : null);
            else if (moduleTab != 2) builder.AddLabel(ShopSettings.Text("f10.empty"));
            builder.End().End();
            if (!wrap)
            {
                builder.BeginColumn(); FixedColumn(builder.CurrentContainer, rightWidth);
                bayContent = DrawCurrentModuleBay(builder, rightWidth, rows, previewCell);
                builder.End();
            }
            builder.End();
            // The builder keeps the window inactive until Show; resolve its scroll ancestors afterwards.
            EnableBothAxes(sourceContent);
            if (!wrap) EnableBothAxes(bayContent);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(modulesWindow.Rect);
            Canvas.ForceUpdateCanvases();
            FitScroll(sourceContent);
            if (!wrap) FitScroll(bayContent);
            Canvas.ForceUpdateCanvases();
            bool changed = false;
            ScrollRect sourceScroll = sourceContent.GetComponentInParent<ScrollRect>();
            moduleSourceScroll = sourceScroll;
            if (sourceScroll?.viewport != null && sourceScroll.viewport.rect.width >= 34 && sourceScroll.viewport.rect.height >= 34)
            {
                int measuredColumns = Mathf.Max(1, Mathf.FloorToInt((sourceScroll.viewport.rect.width - 8) / 34));
                int measuredRows = Mathf.Max(1, Mathf.FloorToInt((sourceScroll.viewport.rect.height - 8) / 34));
                changed = measuredColumns != moduleColumns || measuredRows != moduleVisibleRows;
                moduleColumns = measuredColumns; moduleVisibleRows = measuredRows;
            }
            float grow = 0;
            float nextPreviewCell = previewCell;
            int nextDockRows = dockRows;
            ScrollRect bayScroll = wrap ? null : bayContent?.GetComponentInParent<ScrollRect>();
            moduleBayScroll = bayScroll;
            if (wrap && dock != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(dock);
                nextDockRows = Mathf.Max(1, Mathf.CeilToInt((LayoutUtility.GetPreferredHeight(dock) + 12) / 34));
                if (sourceScroll?.viewport != null && maxHeight - size.y > .5f)
                {
                    var measuredArea = ModuleDockArea(moduleColumns, rightWidth, nextDockRows);
                    int rowLimit = Mathf.Max(1, Mathf.CeilToInt((sourceScroll.viewport.rect.height + maxHeight - size.y) / 34));
                    float needed = ItemGrid.PackedRows(idleEntries, moduleColumns, measuredArea, rowLimit) * 34 + 8;
                    float deficit = needed - sourceScroll.viewport.rect.height;
                    if (deficit > .5f) grow = Mathf.Min(deficit + 2, maxHeight - size.y);
                }
                float horizontalDeficit = LayoutUtility.GetPreferredWidth(dock) - dock.rect.width;
                if (layoutOpen && layoutPlan != null && moduleBay is GameGridInventory bay && horizontalDeficit > .5f)
                    nextPreviewCell = Mathf.Max(12, PreviewCellSize(dock.rect.width, bay.inventoryShape.width, previewCell) -
                        (horizontalDeficit + 4) / (2 * bay.inventoryShape.width));
            }
            else if (layoutOpen && layoutPlan != null && bayScroll?.viewport != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(bayContent);
                float needed = Mathf.Max(LayoutUtility.GetPreferredHeight(bayContent), bayContent.rect.height);
                float deficit = needed - bayScroll.viewport.rect.height;
                if (deficit > .5f) grow = Mathf.Min(deficit + 4, maxHeight - size.y);
                if (moduleBay is GameGridInventory bay && bay.inventoryShape.width > 0)
                {
                    float cell = PreviewCellSize(rightWidth, bay.inventoryShape.width, previewCell);
                    float horizontalDeficit = LayoutUtility.GetPreferredWidth(bayContent) - bayScroll.viewport.rect.width;
                    if (horizontalDeficit > .5f && cell > 12)
                        nextPreviewCell = Mathf.Max(12, cell - (horizontalDeficit + 4) / (2 * bay.inventoryShape.width));
                }
            }
            if (sourceScroll != null) sourceScroll.normalizedPosition = sourcePosition;
            if (bayScroll != null) bayScroll.normalizedPosition = bayPosition;
            bool again = grow > .5f || changed && wrap || nextPreviewCell != previewCell || nextDockRows != dockRows;
            extraHeight += grow; previewCell = nextPreviewCell; dockRows = nextDockRows;
            return again;
        }

        private bool ClearModuleResults()
        {
            if (moduleResults == null || ModuleWindowElements?.GetValue(modulesWindow) is not Dictionary<string, CustomUIElement> elements) return false;
            foreach (var pair in elements.ToArray())
                if (pair.Value?.root != null && pair.Value.root.transform.IsChildOf(moduleResults)) elements.Remove(pair.Key);
            CustomUIBuilder.ClearContainer(moduleResults);
            moduleIdleGrid = null;
            moduleSourceScroll = moduleBayScroll = null;
            return true;
        }

        private Vector2 ModuleWindowSize(out Vector2 available, List<ItemGrid.Entry> entries,
            List<ShopInventory.Location> bays, ShopInventory.Location storage, int dockRows, int idleWidth)
        {
            RectTransform layer = CustomUIManager.Instance?.GetLayerRoot(CustomUIManager.LAYER_META);
            available = layer != null ? layer.rect.size : Vector2.zero;
            if (available.x < 100 || available.y < 100)
            {
                Canvas canvas = layer?.GetComponentInParent<Canvas>();
                available = canvas?.transform is RectTransform rect ? rect.rect.size : new Vector2(Screen.width, Screen.height);
            }
            float maxWidth = Mathf.Max(1, Mathf.Min(available.x * .8f, available.x - 32));
            if (moduleWindowWidth <= 0)
                moduleWindowWidth = Mathf.Min(maxWidth, Mathf.Max(760, idleWidth * 34 + ModuleDockWidth(maxWidth - 32) + 56));
            // Filtering must not move its own controls while the player uses them.
            float width = Mathf.Min(maxWidth, moduleWindowWidth);
            float maxHeight = Mathf.Max(1, Mathf.Min(available.y * .8f, available.y - 32));
            float minHeight = Mathf.Min(maxHeight, available.y * .40f);
            float bodyWidth = Mathf.Max(80, width - 32);
            float dockWidth = ModuleDockWidth(bodyWidth);
            float leftWidth = moduleTab == 0 ? bodyWidth : bodyWidth - 8 - dockWidth;
            int columns = Mathf.Max(1, Mathf.FloorToInt((leftWidth - 22) / 34));
            int leftRows = moduleTab == 0 ? ItemGrid.PackedRows(entries, columns, ModuleDockArea(columns, dockWidth, dockRows), Mathf.Max(1, Mathf.CeilToInt(maxHeight / 34f))) : moduleTab == 1 && bays.Count > 0 ?
                ((GameGridInventory)bays[Mathf.Clamp(modulePage, 0, bays.Count - 1)].Inventory).inventoryShape.height :
                (storage?.Inventory as GameGridInventory)?.inventoryShape.height ?? 1;
            int rightRows = (moduleBay as GameGridInventory)?.inventoryShape?.height ?? 1;
            // Preview adds a control row, three readable statistics and board headings.
            float extraControls = layoutOpen ? (layoutPlan == null ? 44 : 235) : 0;
            float chrome = (bodyWidth < 560 ? 225 : 185) + (ModuleFooterRows(bodyWidth) - 1) * 40;
            float contentHeight = moduleTab == 0 ? Mathf.Max(1, leftRows) * 34 :
                Mathf.Max(Mathf.Max(1, leftRows) * 34, rightRows * 34 + extraControls);
            return new Vector2(width, Mathf.Clamp(chrome + contentHeight, minHeight, maxHeight));
        }

        private float ModuleDockWidth(float bodyWidth)
        {
            int columns = (moduleBay as GameGridInventory)?.inventoryShape?.width ?? 4;
            float needed = layoutOpen ? Mathf.Max(330, columns * 56 + 64) : Mathf.Max(228, columns * 34 + 24);
            return Mathf.Ceil(Mathf.Min(needed, bodyWidth * .48f) / 34) * 34;
        }

        private RectInt ModuleDockArea(int columns, float width, int measuredRows)
        {
            int dockColumns = Mathf.Min(columns, Mathf.CeilToInt(width / 34));
            int rows = measuredRows > 0 ? measuredRows :
                ((moduleBay as GameGridInventory)?.inventoryShape?.height ?? 4) + (layoutOpen ? 9 : 3);
            return new RectInt(columns - dockColumns, 0, dockColumns, rows);
        }

        private static void FlexibleHeight(RectTransform rect)
        {
            LayoutElement layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = 60; layout.flexibleHeight = 1; layout.flexibleWidth = 1;
        }

        private static void FixedColumn(RectTransform rect, float width)
        {
            LayoutElement layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minWidth = layout.preferredWidth = width; layout.flexibleHeight = 1;
            var column = rect.GetComponent<VerticalLayoutGroup>();
            if (column != null) column.childForceExpandWidth = true;
        }

        private static void EnableBothAxes(RectTransform content)
        {
            ScrollRect scroll = content.GetComponentInParent<ScrollRect>();
            if (scroll != null)
            {
                scroll.horizontal = true; scroll.vertical = true;
                ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>() ?? content.gameObject.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            }
        }

        private static void FitScroll(RectTransform content)
        {
            ScrollRect scroll = content?.GetComponentInParent<ScrollRect>();
            if (scroll?.viewport == null) return;
            RectTransform viewport = scroll.viewport;
            float width = Mathf.Max(viewport.rect.width, LayoutUtility.GetPreferredWidth(content));
            bool overflow = width > viewport.rect.width + .5f;
            content.anchorMin = new Vector2(0, content.anchorMin.y);
            content.anchorMax = new Vector2(0, content.anchorMax.y);
            content.pivot = new Vector2(0, content.pivot.y);
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            content.anchoredPosition = new Vector2(0, content.anchoredPosition.y);
            if (!overflow) return;

            // Grid cells consume pointer drags for selection and moving items, so a visible
            // scrollbar is needed to reach content wider than the viewport.
            if (!ReferenceEquals(viewport, scroll.transform)) viewport.offsetMin += new Vector2(0, 16);
            var track = new GameObject("module_horizontal_scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            var rect = (RectTransform)track.transform;
            rect.SetParent(scroll.transform, false); rect.anchorMin = Vector2.zero; rect.anchorMax = new Vector2(1, 0);
            rect.pivot = new Vector2(.5f, 0); rect.offsetMin = new Vector2(0, 0); rect.offsetMax = new Vector2(0, 14);
            var trackImage = track.GetComponent<Image>(); trackImage.color = new Color(.12f, .11f, .15f, .95f);
            var sliding = new GameObject("sliding_area", typeof(RectTransform)).GetComponent<RectTransform>();
            sliding.SetParent(rect, false); sliding.anchorMin = Vector2.zero; sliding.anchorMax = Vector2.one;
            sliding.offsetMin = new Vector2(2, 2); sliding.offsetMax = new Vector2(-2, -2);
            var handle = new GameObject("handle", typeof(RectTransform), typeof(Image));
            var handleRect = (RectTransform)handle.transform;
            handleRect.SetParent(sliding, false); handleRect.anchorMin = Vector2.zero; handleRect.anchorMax = Vector2.one;
            handleRect.offsetMin = handleRect.offsetMax = Vector2.zero;
            Image handleImage = handle.GetComponent<Image>(); handleImage.color = new Color(.48f, .72f, .72f, 1);
            Scrollbar bar = track.GetComponent<Scrollbar>(); bar.direction = Scrollbar.Direction.LeftToRight;
            bar.handleRect = handleRect; bar.targetGraphic = handleImage; bar.value = 0;
            scroll.horizontalScrollbar = bar;
            scroll.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }

        private void DrawModuleToolbar(CustomUIBuilder builder, float width)
        {
            builder.BeginRow();
            builder.AddButton(ShopSettings.Text("grid.tab_idle"), () => SwitchModuleTab(0), "idle_tab");
            builder.Last.SetFixedWidth(66).SetInteractable(moduleTab != 0);
            builder.AddButton(ShopSettings.Text("grid.tab_other"), () => SwitchModuleTab(1), "installed_tab");
            builder.Last.SetFixedWidth(100).SetInteractable(moduleTab != 1);
            builder.AddButton(ShopSettings.Text("grid.tab_storage"), () => SwitchModuleTab(2), "storage_tab");
            builder.Last.SetFixedWidth(78).SetInteractable(moduleTab != 2);
            if (width < 560) builder.End().BeginRow();
            builder.AddToggle(ShopSettings.Text("grid.compatible"), compatibleOnly,
                value => { compatibleOnly = value; modulePage = 0; modulesDirty = true; }, "compatible");
            builder.Last.SetFixedWidth(120);
            builder.AddDropdown(ModuleAttributes(), moduleAttribute,
                index => { moduleAttribute = Mathf.Clamp(index, 0, 3); modulePage = 0; moduleFilterDirty = true; }, "attribute");
            builder.Last.SetFixedWidth(150);
            builder.End();
        }

        private static string[] ModuleAttributes() => new[] { ShopSettings.Text("f10.all_attributes"), ShopSettings.Text("f10.performance_desc"),
            ShopSettings.Text("f10.efficiency_desc"), ShopSettings.Text("f10.quality_desc") };

        private void RefreshModuleToolbar()
        {
            void Label(string id, string key)
            {
                CustomUIElement element = modulesWindow.Get(id);
                string value = ShopSettings.Text(key);
                if (element?.text != null && element.text.text != value) element.SetText(value);
            }
            Label("idle_tab", "grid.tab_idle");
            Label("installed_tab", "grid.tab_other");
            Label("storage_tab", "grid.tab_storage");
            Label("compatible", "grid.compatible");
            modulesWindow.Get("idle_tab")?.SetInteractable(moduleTab != 0);
            modulesWindow.Get("installed_tab")?.SetInteractable(moduleTab != 1);
            modulesWindow.Get("storage_tab")?.SetInteractable(moduleTab != 2);
            CustomUIElement attribute = modulesWindow.Get("attribute");
            string[] choices = ModuleAttributes();
            if (attribute?.dropdown != null && !attribute.dropdown.options.Select(option => option.text).SequenceEqual(choices))
            {
                attribute.SetOptions(choices);
                attribute.SetIndex(moduleAttribute, false);
            }
        }

        private void SwitchModuleTab(int tab) { moduleTab = tab; modulePage = 0; RenderModules(); }

        private void DrawSourceHeader(CustomUIBuilder builder, string title, int page, int pages)
        {
            builder.BeginRow().AddLabel(title, "source_title");
            builder.Last.SetFlexible(1, 0);
            FitModuleText(builder.Last);
            builder.WithTooltip(title);
            builder.AddLabel(ShopSettings.Format("grid.selected", selectedModules.Count), "selected_count");
            builder.Last.SetFixedWidth(100);
            FitModuleText(builder.Last);
            if (pages > 1)
            {
                builder.AddButton("‹", () => { if (modulePage > 0) { modulePage--; RenderModules(); } }, "prev"); builder.Last.SetFixedWidth(32);
                builder.AddLabel((page + 1) + "/" + pages, "module_page"); builder.Last.SetFixedWidth(45);
                builder.AddButton("›", () => { if (modulePage + 1 < pages) { modulePage++; RenderModules(); } }, "next"); builder.Last.SetFixedWidth(32);
            }
            builder.End();
        }

        private static void FitModuleText(CustomUIElement element)
        {
            element.SetPreferredSize(-1, 36);
            if (element.text == null) return;
            element.text.richText = false; element.text.enableWordWrapping = false;
            element.text.enableAutoSizing = true; element.text.fontSizeMin = 14; element.text.fontSizeMax = 24;
            element.text.fontSize = 24; element.text.overflowMode = TMPro.TextOverflowModes.Ellipsis;
        }

        private IOrderedEnumerable<ModuleRow> OrderModules(IEnumerable<ModuleRow> rows)
        {
            return rows.OrderByDescending(row => moduleAttribute == 0 ? 0 : ModuleAttributeValue(row.Item, moduleAttribute))
                .ThenByDescending(row => row.Compatibility)
                .ThenBy(row => row.Item.GetDisplayName(true), StringComparer.CurrentCulture)
                .ThenBy(row => row.Path, StringComparer.CurrentCulture);
        }

        private ItemGrid.Entry ModuleEntry(ModuleRow row)
        {
            GameItem item = row.Item;
            string state = ModuleState(item);
            bool needsInspection = AttentionBook.HasInspection("f10.open", item.uniqueId) || AttentionBook.HasInspection("f12.open", item.uniqueId);
            bool draggable = state.Length == 0 && ShopInventory.Movable(item) && item.MaxNumRemove() >= item.unitCount && !needsInspection;
            if (!draggable && state.Length == 0) state = ShopSettings.Text(needsInspection ?
                "inv.inspect" : "availability.item_movable");
            string compatible = row.Compatibility > 0 ? ShopSettings.Text("f10.type_match") :
                row.Compatibility < 0 ? ShopSettings.Text("f10.type_mismatch") : ShopSettings.Text("f10.type_unknown");
            return new ItemGrid.Entry(item, item.parentInventory) { Caption = item.GetDisplayName(true),
                Badge = state.Length > 0 ? state : moduleAttribute == 0 ? compatible :
                    ShopSettings.Text(moduleAttribute == 1 ? "f10.performance" : moduleAttribute == 2 ? "f10.efficiency" : "f10.quality") +
                    " " + ModuleAttributeValue(item, moduleAttribute).ToString("+0;-0;0") + "%",
                Marker = ModuleMarker(item),
                Reason = string.Join(" · ", new[] { row.Path, compatible, state }.Where(value => !string.IsNullOrEmpty(value))),
                Selected = row.Installed ? selectedInstalledModules.Contains(item) : selectedModules.Contains(item),
                Draggable = draggable };
        }

        private static string ModuleState(GameItem item) => item.IsTag(ModuleHelper.MODULE_STUCK_TAG) ? ShopSettings.Text("f10.stuck") :
            ItemPinHelper.IsPinned(item) ? ShopSettings.Text("f10.fixed") :
            item.parentInventory != null && item.parentInventory.IsRemoveLocked() ? ShopSettings.Text("f10.locked") : "";

        private static string ModuleMarker(GameItem item) => item.IsTag(ModuleHelper.MODULE_STUCK_TAG) ? ShopSettings.Text("grid.mark_stuck") :
            ItemPinHelper.IsPinned(item) ? ShopSettings.Text("grid.mark_fixed") :
            item.parentInventory != null && item.parentInventory.IsRemoveLocked() ? ShopSettings.Text("grid.mark_locked") : null;

        private ItemGrid.Options ModuleGridOptions(float width) => new ItemGrid.Options
        {
            Cards = false, Columns = moduleColumns > 0 ? moduleColumns : Mathf.Max(1, Mathf.FloorToInt((width - 22) / 34)), CellSize = 34, HighContrast = true,
            OnClick = entry => ChangeModuleSelection(entry, false),
            OnMarquee = entries =>
            {
                if (entries.Count == 0) return;
                var installed = new HashSet<GameInventory>(ModuleLocations().Where(location =>
                    location.IsMachine && location.Role == "module").Select(location => location.Inventory));
                foreach (ItemGrid.Entry entry in entries) ChangeModuleSelection(entry, true, installed);
            },
            CanDrop = CanMoveModule,
            OnDrop = MoveModule,
            OnReject = (entry, destination, shape) => ShopNotice.Show(ShopSettings.Text("f10.changed"))
        };

        private void ChangeModuleSelection(ItemGrid.Entry entry, bool add, HashSet<GameInventory> installedBays = null)
        {
            if (!SameModuleSession() || !ShopInventory.Owned(entry.Item) || !ReferenceEquals(entry.Item.parentInventory, entry.Source))
            { modulesDirty = true; return; }
            if (!entry.Draggable)
            {
                if (!add && !ItemLocator.TryLocate(entry.Item, out string reason)) ShopNotice.Show(ShopSettings.Text(reason));
                return;
            }
            bool installed = ReferenceEquals(entry.Source, moduleBay) || (installedBays != null ? installedBays.Contains(entry.Source) :
                ModuleLocations().Any(location => location.IsMachine && location.Role == "module" && ReferenceEquals(location.Inventory, entry.Source)));
            HashSet<GameItem> selection = installed ? selectedInstalledModules : selectedModules;
            if (add) selection.Add(entry.Item);
            else if (!selection.Remove(entry.Item)) selection.Add(entry.Item);
            entry.Selected = selection.Contains(entry.Item);
            modulesWindow?.Get("selected_count")?.SetText(ShopSettings.Format("grid.selected", selectedModules.Count));
            if (!installed && (layoutPlan != null || layoutStatus != null))
            { layoutPlan = null; layoutBoard = null; layoutStatus = null; modulesDirty = true; }
        }

        private void DrawRealModuleBoard(CustomUIBuilder builder, GameGridInventory grid, float width,
            List<ModuleRow> rows, HashSet<GameItem> matches, float cell = 34)
        {
            var entries = new List<ItemGrid.Entry>();
            var concealed = new List<GridShape>();
            foreach (GameItem item in grid.childItems)
            {
                if (item?.modifiedShape == null) continue;
                if (!ShopInventory.Owned(item)) { concealed.Add(item.modifiedShape); continue; }
                ItemGrid.Entry entry = ModuleBoardEntry(item, grid, rows);
                if (matches != null && !matches.Contains(item)) entry.Enabled = false;
                entries.Add(entry);
            }
            ItemGrid.Options options = ModuleGridOptions(width);
            options.Board = grid.inventoryShape; options.Inventory = grid; options.CellSize = cell; options.Concealed = concealed;
            ItemGrid.Add(builder, entries, options);
        }

        private ItemGrid.Entry ModuleBoardEntry(GameItem item, GameGridInventory grid, List<ModuleRow> rows)
        {
            bool isModule = item.IsTag(ModuleHelper.MODULE_TAG) || item.IsGameItemType("NODE");
            ModuleRow row = rows.FirstOrDefault(candidate => ReferenceEquals(candidate.Grid, grid) && ReferenceEquals(candidate.Item, item));
            if (row == null && isModule && ReferenceEquals(grid, moduleBay))
                row = new ModuleRow { Item = item, Grid = grid, Installed = true, Path = moduleTarget.GetDisplayName(true), Compatibility = ModuleCompatibility(moduleTarget, item) };
            return row == null ? new ItemGrid.Entry(item, grid) { Enabled = false, Selectable = false } : ModuleEntry(row);
        }

        private static float PreviewCellSize(float width, int columns, float measuredCell) => measuredCell > 0 ? measuredCell :
            Mathf.Clamp((width - 32) / Math.Max(1, columns * 2), 12, 34);

        private RectTransform DrawCurrentModuleBay(CustomUIBuilder builder, float width, List<ModuleRow> rows, float previewCell, bool scroll = true)
        {
            builder.BeginRow().AddLabel(ShopSettings.Text("grid.current_bay"));
            builder.Last.SetFixedWidth(Mathf.Max(70, Mathf.Min(180, width - 100)));
            FitModuleText(builder.Last);
            if (layoutEnabled.Value)
            {
                builder.AddButton(ShopSettings.Text("grid.arrange"), OpenLayout, "layout");
                builder.Last.SetFixedWidth(90);
            }
            builder.End();
            if (layoutOpen) DrawLayoutControls(builder);
            if (scroll) builder.BeginScrollFlexible(60);
            RectTransform content = builder.CurrentContainer;
            if (moduleBay is GameGridInventory bay)
            {
                bool preview = layoutOpen && layoutPlan != null && layoutBoard != null;
                float cell = preview ? PreviewCellSize(width, bay.inventoryShape.width, previewCell) :
                    Mathf.Clamp((width - 16) / Mathf.Max(1, bay.inventoryShape.width), 12, 34);
                if (preview)
                {
                    builder.BeginRow(8).BeginColumn();
                    builder.AddLabel(ShopSettings.Text("grid.current_bay"));
                    DrawRealModuleBoard(builder, bay, width / 2, rows, null, cell);
                    builder.End().BeginColumn();
                    builder.AddLabel(ShopSettings.Text("grid.preview_bay"));
                    DrawLayoutBoard(builder, cell);
                    builder.End().End();
                }
                else DrawRealModuleBoard(builder, bay, width, rows, null, cell);
            }
            if (scroll) builder.End();
            return content;
        }

        private void ChooseModuleStorage()
        {
            if (!SameModuleSession()) return;
            List<ShopInventory.Location> choices = ModuleStorageChoices();
            VisualChoices.ChooseLocation(ShopSettings.Text("f10.choose_storage"), () => SameModuleSession() ? ModuleStorageChoices() : new List<ShopInventory.Location>(), BoundModuleStorage(choices),
                location => { if (SameModuleSession()) BindModuleStorage(location); }, owner: modulesWindow);
        }

        private static float ModuleStorageLabelWidth(float width) => Mathf.Min(width, Mathf.Clamp(width * .42f, 180, 440));

        private static int ModuleFooterRows(float width) => width >= ModuleStorageLabelWidth(width) + 150 + 110 + 16 ? 1 :
            width >= 150 + 110 + 8 ? 2 : 3;

        private void DrawModuleFooter(CustomUIBuilder builder, ShopInventory.Location storage, float width)
        {
            string caption = ShopSettings.Text("grid.storage_missing");
            if (storage?.Inventory is GameGridInventory grid)
            {
                int free = 0, total = 0;
                for (int y = 0; y < grid.inventoryShape.height; y++) for (int x = 0; x < grid.inventoryShape.width; x++)
                {
                    if (grid.inventoryShape.Get(x, y) != 0) continue;
                    total++;
                    if (!grid.childItems.Any(item => item?.modifiedShape != null && item.modifiedShape.Get(x, y) == 1)) free++;
                }
                caption = ShopSettings.Format("grid.storage_footer", storage.Name, free, total);
            }
            int rowCount = ModuleFooterRows(width);
            builder.BeginRow(8).AddLabel(caption, "storage_status");
            builder.Last.SetFixedWidth(ModuleStorageLabelWidth(width));
            FitModuleText(builder.Last);
            builder.WithTooltip(caption);
            if (rowCount > 1) builder.End().BeginRow(8);
            builder.AddButton(ShopSettings.Text("grid.choose_storage"), ChooseModuleStorage, "storage_choose_footer");
            builder.Last.SetFixedWidth(Mathf.Min(150, width));
            if (rowCount > 2) builder.End().BeginRow(8);
            builder.AddButton(ShopSettings.Text("machines.refresh"), () => { modulesDirty = true; }, "refresh");
            builder.Last.SetFixedWidth(Mathf.Min(110, width));
            builder.End();
        }

        private static int ModuleAttributeValue(GameItem item, int attribute) => item.GetTagReadonly(attribute == 1 ?
            "BONUS_PERCENTAGE_PERFORMANCE_INT" : attribute == 2 ? "BONUS_PERCENTAGE_EFFICIENCY_INT" :
            "BONUS_PERCENTAGE_QUALITY_INT").GetInt();

        private List<ShopInventory.Location> ModuleStorageChoices() => ShopInventory.OwnedLocations(false)
            .Where(location => location.Ordinary && location.Inventory is GameGridInventory grid && grid.inventoryShape != null && ShopInventory.IsMainStockBranch(location) &&
                !location.Inventory.IsInsertLocked() && GeneralHelper.MayPlayerInsertInto(location.Inventory))
            .OrderBy(location => location.Name, StringComparer.CurrentCulture).ToList();

        private ShopInventory.Location BoundModuleStorage(List<ShopInventory.Location> choices)
        {
            try
            {
                string raw = ModHelper.GetModData(ModEntry.Id, ModuleStorageKey, null);
                if (raw == null) return null;
                ModuleStorageBinding binding = ShopData.Decode<ModuleStorageBinding>(raw, 1024);
                if (binding?.version != 1 || string.IsNullOrEmpty(binding.location) || binding.location.Length > 200) return null;
                var matches = choices.Where(location => location.Key() == binding.location).ToList();
                return matches.Count == 1 ? matches[0] : null;
            }
            catch (Exception error) { log("F10 module storage could not be read: " + error.Message); return null; }
        }

        private void BindModuleStorage(ShopInventory.Location previous)
        {
            if (!SameModuleSession() || previous == null || !ShopInventory.TryRefresh(previous, out ShopInventory.Location location) ||
                !location.Ordinary || !ShopInventory.IsMainStockBranch(location) || !(location.Inventory is GameGridInventory) ||
                location.Inventory.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(location.Inventory)) return;
            string key = location.Key(true);
            if (string.IsNullOrEmpty(key) || key.Length > 200 || ModuleStorageChoices().Count(other => other.Key() == key) != 1) return;
            try
            {
                string raw = ShopData.Encode(new ModuleStorageBinding { location = key });
                ModHelper.SetModData(ModEntry.Id, ModuleStorageKey, raw);
                if (ModHelper.GetModData(ModEntry.Id, ModuleStorageKey, null) != raw) throw new InvalidOperationException("Save was not retained");
                moduleTab = 2; modulePage = 0; modulesDirty = true;
            }
            catch (Exception error) { log("F10 module storage could not be saved: " + error); ShopNotice.Show(ShopSettings.Text("f10.storage_failed")); }
        }

        private bool CanMoveModule(ItemGrid.Entry entry, GameGridInventory destination, GridShape shape)
        {
            GameItem item = entry?.Item;
            if (!SameModuleSession() || item == null || destination == null || shape == null || item.parentInventory != entry.Source ||
                !(item.IsTag(ModuleHelper.MODULE_TAG) || item.IsGameItemType("NODE")) || !ShopInventory.Movable(item) ||
                item.MaxNumRemove() < item.unitCount || item.IsTag(ModuleHelper.MODULE_STUCK_TAG) ||
                AttentionBook.HasInspection("f10.open", item.uniqueId) || AttentionBook.HasInspection("f12.open", item.uniqueId) ||
                AttentionBook.HasInspection("f12.open", moduleTarget.uniqueId) || ShopInventory.WouldContainSelf(item, destination)) return false;
            var locations = ModuleLocations();
            bool validSource = locations.Any(location => ReferenceEquals(location.Inventory, entry.Source) &&
                (ShopInventory.SearchableGoods(location) || location.IsMachine && location.Role == "module"));
            if (!validSource) return false;
            bool isBay = ReferenceEquals(destination, moduleBay) || locations.Any(location =>
                location.IsMachine && location.Role == "module" && ReferenceEquals(location.Inventory, destination));
            if (!isBay && !ReferenceEquals(destination, BoundModuleStorage(ModuleStorageChoices())?.Inventory)) return false;
            SlotMarker slot = GridPlacements.Exact(destination, item, shape);
            return slot != null && ModuleLayout.SameShape(slot.itemGridShape, shape);
        }

        private void MoveModule(ItemGrid.Entry entry, GameGridInventory destination, GridShape shape)
        {
            if (!CanMoveModule(entry, destination, shape)) { ShopNotice.Show(ShopSettings.Text("f10.changed")); return; }
            GameItem item = entry.Item;
            var identity = OperationResult.Capture(item);
            var locations = ModuleLocations();
            string from = locations.Find(location => ReferenceEquals(location.Inventory, entry.Source))?.Name ?? "";
            string to = locations.Find(location => ReferenceEquals(location.Inventory, destination))?.Name ?? moduleTarget?.GetDisplayName(true) ?? "";
            if (ItemGridMove.TryMove(item, destination, shape, out bool restored))
            {
                selectedModules.Remove(item); selectedInstalledModules.Remove(item); layoutPlan = null; modulesDirty = true; return;
            }
            if (restored) ShopNotice.Show(ShopSettings.Text("f10.changed"));
            else
            {
                var result = new OperationResult("f10.open");
                result.SkipMissing(identity.Name, 1, "inv.inspect", from + " → " + to, identity.ItemId, true);
                ShopNotice.Report(result);
            }
            layoutPlan = null; modulesDirty = true;
        }

        private List<ModuleRow> ModuleRows(List<ShopInventory.Location> locations)
        {
            var rows = new List<ModuleRow>();
            var seen = new HashSet<GameItem>();
            foreach (ShopInventory.Location location in locations)
            {
                bool installed = location.Owner != null && location.IsMachine && location.Role == "module" && location.Inventory is GameGridInventory;
                if (installed && ReferenceEquals(location.Owner, moduleTarget)) continue;
                if (!installed && !ShopInventory.SearchableGoods(location)) continue;
                foreach (GameItem item in location.Inventory.childItems)
                {
                    if (item == null || !seen.Add(item) || !ShopInventory.Owned(item) ||
                        !(item.IsTag(ModuleHelper.MODULE_TAG) || item.IsGameItemType("NODE"))) continue;
                    rows.Add(new ModuleRow { Item = item, Path = location.Name, Installed = installed, Machine = location.Owner, Grid = location.Inventory as GameGridInventory,
                        Compatibility = ModuleCompatibility(moduleTarget, item) });
                }
            }
            return rows;
        }

        // Read the panel tree without opening machine windows or invoking their open callbacks.
        // A bay is public only when every node between it and its panel is active.
        private static List<ShopInventory.Location> ModuleLocations()
        {
            var goods = ShopInventory.MachineLocations().Where(ShopInventory.SearchableGoods).ToList();
            var result = new List<ShopInventory.Location>(goods);
            var seen = new HashSet<GameInventory>(goods.Select(location => location.Inventory));
            foreach (ShopInventory.Location parent in goods)
            foreach (GameItem owner in parent.Inventory.childItems)
            {
                if (owner == null || !owner.IsGameItemType("MACHINE") || !ShopInventory.Owned(owner) ||
                    owner.contentWindow == null || owner.contentWindow.isLocked || LockHelper.IsCurrentlyLocked(owner) ||
                    owner.IsTag("LOCKED_TAG") || owner.IsTag("TRACK_OPENED_TAG") && !owner.IsTag("OPENED_TAG") ||
                    !(owner.contentWindow.childElement is MachineryPanelRow row)) continue;
                foreach (MachineryPanel panel in row.Panels)
                {
                    if (panel == null || panel.isHidden || panel.role != "module" || panel.content == null || panel.handler == null) continue;
                    var nodes = MachineryContainerUtils.Collect(panel.content);
                    if (nodes == null) continue;
                    int index = 0;
                    foreach (var node in nodes)
                    {
                        index++;
                        if (!(node is GameGridInventory grid) || grid.inventoryShape == null || !(grid is PixelElement element) || element.handler == null ||
                            !PublicModuleBranch(element, panel) || !seen.Add(grid)) continue;
                        var path = new GameItem[parent.Path.Length + 1];
                        Array.Copy(parent.Path, path, parent.Path.Length); path[path.Length - 1] = owner;
                        string caption = panel.label?.GetText();
                        result.Add(new ShopInventory.Location { Inventory = grid, Owner = owner, IsMachine = true,
                            Ordinary = false, Role = "module", Page = -1, Path = path,
                            Name = parent.Name + " › " + owner.GetDisplayName(true) + " › " +
                                (string.IsNullOrEmpty(caption) ? ShopSettings.Text("f10.installed") : caption) + (nodes.Count > 1 ? " " + index : "") });
                    }
                }
            }
            return result;
        }

        private static bool PublicModuleBranch(PixelElement element, MachineryPanel panel)
        {
            Transform root = panel.handler.transform;
            for (Transform cursor = element.handler.transform; cursor != null; cursor = cursor.parent)
            {
                if (!cursor.gameObject.activeSelf) return false;
                if (ReferenceEquals(cursor, root)) return true;
            }
            return false;
        }

        // This is a type check only, never a promise of free space or permission to insert.
        private static int ModuleCompatibility(GameItem target, GameItem module)
        {
            string specific = ModuleFamily(target);
            if (specific == null || module == null) return 0;
            return module.IsTag("MODULE_TYPE_UNIVERSAL") || (specific.Length > 0 && module.IsTag(specific)) ? 1 : -1;
        }

        private static string ModuleFamily(GameItem machine)
        {
            if (machine == null || machine.IsTag("BROKEN_MACHINE_TAG")) return null;
            switch (machine.identifier)
            {
                case "hydroponic" when machine.IsTag("HYDROPONIC_MACHINE_TAG"): return "MODULE_TYPE_HYDROPONIC";
                case "moisture_farm" when machine.IsTag("STANDARD_MACHINE_TAG"): return "";
                case "evaporator" when machine.IsGameItemType("MACHINE"): return "MODULE_TYPE_EVAPORATOR";
                case "printer" when machine.IsTag("PRINTER_TAG"): return "MODULE_TYPE_PRINTER";
                case "furnace" when machine.IsTag("STANDARD_MACHINE_TAG"): return "MODULE_TYPE_FURNACE";
                default: return null;
            }
        }

        private void CloseModules()
        {
            CloseLayout();
            CustomUIWindow window = modulesWindow;
            modulesWindow = null; moduleResults = moduleIdleGrid = null; moduleSourceScroll = moduleBayScroll = null;
            moduleTarget = null; moduleBay = null; selectedModules.Clear(); selectedInstalledModules.Clear(); modulesDirty = moduleFilterDirty = false;
            moduleTab = 0; modulePage = 0;
            moduleWindowWidth = 0; moduleColumns = moduleVisibleRows = moduleDockRows = 0;
            window?.Close();
        }
    }
}
