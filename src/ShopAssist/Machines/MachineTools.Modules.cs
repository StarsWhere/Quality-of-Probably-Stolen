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

        // This records only what the current idle view drew. It is never used to authorize a move.
        private sealed class ModuleEntrySnapshot
        {
            private readonly GameItem item;
            private readonly GameInventory source;
            private readonly GridShape shape;
            private readonly int count;
            private readonly bool selected, enabled, selectable, hasArt;
            private readonly string caption, badge, marker, reason;
            private readonly Sprite sprite;
            private readonly Color color;
            private readonly Vector2 rotationPivot, anchorMin, anchorMax, pivot, position, size;

            internal ModuleEntrySnapshot(ItemGrid.Entry entry)
            {
                item = entry.Item; source = entry.Source; shape = entry.Shape?.Clone(); count = item.unitCount;
                selected = entry.Selected; enabled = entry.Enabled; selectable = entry.Selectable;
                caption = entry.Caption; badge = entry.Badge; marker = entry.Marker; reason = entry.Reason;
                var element = item as GameItemElement;
                var background = element?.background;
                hasArt = background?.image?.sprite != null && background.rectTransform != null && element.rotator?.rectTransform != null;
                if (!hasArt) return;
                sprite = background.image.sprite; color = background.image.color; rotationPivot = element.rotator.rectTransform.pivot;
                RectTransform rect = background.rectTransform;
                anchorMin = rect.anchorMin; anchorMax = rect.anchorMax; pivot = rect.pivot; position = rect.anchoredPosition; size = rect.sizeDelta;
            }

            internal bool Matches(ItemGrid.Entry entry)
            {
                if (entry == null || !ReferenceEquals(item, entry.Item) || !ReferenceEquals(source, entry.Source) || item == null ||
                    count != item.unitCount || !SameModuleShape(shape, entry.Shape) || selected != entry.Selected || enabled != entry.Enabled ||
                    selectable != entry.Selectable || caption != entry.Caption || badge != entry.Badge || marker != entry.Marker || reason != entry.Reason) return false;
                var element = item as GameItemElement;
                var background = element?.background;
                bool nowHasArt = background?.image?.sprite != null && background.rectTransform != null && element.rotator?.rectTransform != null;
                if (hasArt != nowHasArt) return false;
                if (!hasArt) return true;
                RectTransform rect = background.rectTransform;
                return ReferenceEquals(sprite, background.image.sprite) && color.Equals(background.image.color) &&
                    rotationPivot.Equals(element.rotator.rectTransform.pivot) && anchorMin.Equals(rect.anchorMin) && anchorMax.Equals(rect.anchorMax) &&
                    pivot.Equals(rect.pivot) && position.Equals(rect.anchoredPosition) && size.Equals(rect.sizeDelta);
            }
        }

        private sealed class ModuleGridSnapshot
        {
            private sealed class Cell
            {
                internal GameItem Item;
                internal GameInventory Source;
                internal GridShape Shape;
                internal int Count;
                internal bool Owned;
                internal ModuleEntrySnapshot Entry;
                internal ModuleLayout.Part Part;
            }

            private readonly GameGridInventory grid;
            private readonly GridShape board;
            private readonly List<Cell> cells = new List<Cell>();

            internal ModuleGridSnapshot(MachineTools owner, GameGridInventory value, bool showBay, List<ModuleRow> rows)
            {
                grid = value; board = grid?.inventoryShape?.Clone();
                if (grid == null) return;
                foreach (GameItem item in grid.childItems)
                {
                    var cell = new Cell { Item = item, Source = item?.parentInventory, Shape = item?.modifiedShape?.Clone(), Count = item?.unitCount ?? 0 };
                    if (showBay && item?.modifiedShape != null)
                    {
                        cell.Owned = ShopInventory.Owned(item);
                        if (cell.Owned)
                        {
                            cell.Entry = new ModuleEntrySnapshot(owner.ModuleBoardEntry(item, grid, rows));
                            if (item.IsTag(ModuleHelper.MODULE_TAG) || item.IsGameItemType("NODE"))
                                cell.Part = ModuleLayout.Snapshot(item, true, false);
                        }
                    }
                    cells.Add(cell);
                }
            }

            internal bool Matches(MachineTools owner, GameGridInventory value, bool showBay, List<ModuleRow> rows)
            {
                if (!ReferenceEquals(grid, value)) return false;
                if (grid == null) return true;
                if (!SameModuleShape(board, grid.inventoryShape) || cells.Count != grid.childItems.Count) return false;
                for (int i = 0; i < cells.Count; i++)
                {
                    Cell before = cells[i]; GameItem item = grid.childItems[i];
                    if (!ReferenceEquals(before.Item, item) || !ReferenceEquals(before.Source, item?.parentInventory) ||
                        before.Count != (item?.unitCount ?? 0) || !SameModuleShape(before.Shape, item?.modifiedShape)) return false;
                    if (!showBay || item?.modifiedShape == null) continue;
                    if (before.Owned != ShopInventory.Owned(item)) return false;
                    if (!before.Owned) continue;
                    if (before.Entry == null || !before.Entry.Matches(owner.ModuleBoardEntry(item, grid, rows)) ||
                        before.Part != null && !ModuleLayout.Same(before.Part)) return false;
                }
                return true;
            }
        }

        private static bool SameModuleShape(GridShape left, GridShape right) =>
            left == null ? right == null : right != null && ModuleLayout.SameShape(left, right);

        private sealed class ModuleViewSnapshot
        {
            private static readonly string[] TextKeys = { "f10.open", "f10.search", "f10.empty", "grid.tab_idle", "grid.tab_other", "grid.tab_storage",
                "grid.compatible", "grid.selected", "grid.current_bay", "grid.arrange", "grid.storage_footer", "grid.storage_missing",
                "grid.choose_storage", "grid.more", "machines.refresh", "f10.all_types", "f10.universal", "f10.hydroponic",
                "f10.evaporator", "f10.printer", "f10.furnace", "f10.other" };
            private readonly CustomUIWindow window;
            private readonly RectTransform results, idleGrid;
            private readonly ScrollRect scroll;
            private readonly GameItem target;
            private readonly string title, storageName, culture;
            private readonly string[] texts;
            private readonly int page, category, columns, visibleRows, dockRows, width, screenWidth, screenHeight, selectedCount;
            private readonly bool compatible, menu, layoutEnabled;
            private readonly float windowWidth, canvasScale, nativeGridSize;
            private readonly Vector2 layerSize, windowSize, resultsSize, viewportSize;
            private readonly Vector3 layerScale;
            private readonly ModuleGridSnapshot bay, storage;
            private readonly List<ModuleEntrySnapshot> entries;

            internal ModuleViewSnapshot(MachineTools owner, List<ItemGrid.Entry> displayed, int idleWidth,
                ShopInventory.Location location, List<ModuleRow> rows)
            {
                window = owner.modulesWindow; results = owner.moduleResults; idleGrid = owner.moduleIdleGrid; scroll = owner.moduleSourceScroll;
                target = owner.moduleTarget; title = target.GetDisplayName(true); storageName = location?.Name;
                culture = LocHelper.GetCurrentLocaleCode() + "|" + System.Globalization.CultureInfo.CurrentCulture.Name + "|" +
                    System.Globalization.CultureInfo.CurrentUICulture.Name;
                texts = TextKeys.Select(ShopSettings.Text).ToArray();
                page = owner.modulePage; category = owner.moduleCategory; columns = owner.moduleColumns; visibleRows = owner.moduleVisibleRows;
                dockRows = owner.moduleDockRows; width = idleWidth; compatible = owner.compatibleOnly; menu = owner.moduleMenu;
                layoutEnabled = owner.layoutEnabled.Value; selectedCount = owner.selectedModules.Count; windowWidth = owner.moduleWindowWidth;
                screenWidth = Screen.width; screenHeight = Screen.height;
                RectTransform layer = CustomUIManager.Instance.GetLayerRoot(CustomUIManager.LAYER_META);
                layerSize = layer != null ? layer.rect.size : Vector2.zero; layerScale = layer != null ? layer.lossyScale : Vector3.zero;
                canvasScale = layer?.GetComponentInParent<Canvas>()?.scaleFactor ?? 0;
                nativeGridSize = UISettings.current?.gridSize ?? 0;
                windowSize = window.Rect.rect.size; resultsSize = results.rect.size; viewportSize = scroll?.viewport != null ? scroll.viewport.rect.size : Vector2.zero;
                bay = new ModuleGridSnapshot(owner, owner.moduleBay as GameGridInventory, true, rows);
                storage = new ModuleGridSnapshot(owner, location?.Inventory as GameGridInventory, false, rows);
                entries = displayed.Select(entry => new ModuleEntrySnapshot(entry)).ToList();
            }

            internal bool Matches(MachineTools owner, List<ItemGrid.Entry> displayed, int idleWidth,
                ShopInventory.Location location, List<ModuleRow> rows)
            {
                if (window == null || !window.IsAlive || !ReferenceEquals(window, owner.modulesWindow) ||
                    results == null || !ReferenceEquals(results, owner.moduleResults) || idleGrid == null || !idleGrid.gameObject.activeInHierarchy ||
                    !ReferenceEquals(idleGrid, owner.moduleIdleGrid) || scroll == null || scroll.viewport == null || !ReferenceEquals(scroll, owner.moduleSourceScroll) ||
                    !ReferenceEquals(target, owner.moduleTarget) || title != target.GetDisplayName(true) || storageName != location?.Name ||
                    page != owner.modulePage || category != owner.moduleCategory || columns != owner.moduleColumns || visibleRows != owner.moduleVisibleRows ||
                    dockRows != owner.moduleDockRows || width != idleWidth || compatible != owner.compatibleOnly || menu != owner.moduleMenu ||
                    layoutEnabled != owner.layoutEnabled.Value || selectedCount != owner.selectedModules.Count || windowWidth != owner.moduleWindowWidth ||
                    screenWidth != Screen.width || screenHeight != Screen.height || nativeGridSize != (UISettings.current?.gridSize ?? 0) ||
                    culture != LocHelper.GetCurrentLocaleCode() + "|" + System.Globalization.CultureInfo.CurrentCulture.Name + "|" +
                        System.Globalization.CultureInfo.CurrentUICulture.Name ||
                    !windowSize.Equals(window.Rect.rect.size) || !resultsSize.Equals(results.rect.size) || !viewportSize.Equals(scroll.viewport.rect.size)) return false;
                RectTransform layer = CustomUIManager.Instance.GetLayerRoot(CustomUIManager.LAYER_META);
                if (!(layer != null ? layer.rect.size : Vector2.zero).Equals(layerSize) || !(layer != null ? layer.lossyScale : Vector3.zero).Equals(layerScale) ||
                    (layer?.GetComponentInParent<Canvas>()?.scaleFactor ?? 0) != canvasScale ||
                    !texts.SequenceEqual(TextKeys.Select(ShopSettings.Text)) || entries.Count != displayed.Count ||
                    !bay.Matches(owner, owner.moduleBay as GameGridInventory, true, rows) ||
                    !storage.Matches(owner, location?.Inventory as GameGridInventory, false, rows)) return false;
                for (int i = 0; i < entries.Count; i++) if (!entries[i].Matches(displayed[i])) return false;
                return true;
            }
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
        private bool modulesDirty, moduleSearchDirty, compatibleOnly, moduleMenu;
        private int modulePage;
        private int moduleCategory, moduleTab, moduleColumns, moduleVisibleRows, moduleDockRows;
        private float moduleWindowWidth;
        private string moduleSearch = "";
        private ModuleViewSnapshot moduleView;
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

        internal static bool ModulesAvailableFor(GameItem target) => ModulesAvailableFor(target, target?.contentWindow);

        internal static bool ModulesAvailableFor(GameItem target, PixelWindow window) => target != null && current != null && current.started &&
            current.modulesEnabled.Value && ShopInventory.InStore && FindWindowTarget(window, target, out _, out _);

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
                if ((modulesDirty || moduleSearchDirty) && !ItemGrid.DragActive) RenderModules(moduleSearchDirty && !modulesDirty);
            }
            KeyCode key = modulesKey();
            if (key != KeyCode.None && ShopInput.Ready() && Input.GetKeyDown(key)) OpenModules();
        }

        private bool SameModuleSession() => ShopInventory.InStore && PlayerStore.Instance.runID == modulesRun &&
            PlayerStore.Instance.saveSlotId == modulesSlot && moduleTarget != null && ShopInventory.Owned(moduleTarget) &&
            moduleTarget.contentWindow != null && moduleTarget.contentWindow.IsVisible() && !moduleTarget.contentWindow.isLocked && moduleBay != null;

        private void ModuleSettingsChanged() { if (!modulesEnabled.Value) CloseModules(); else modulesDirty = true; }

        private void RenderModules(bool searchOnly = false)
        {
            if (!SameModuleSession()) { CloseModules(); return; }
            if (ItemGrid.DragActive) { modulesDirty = true; return; }
            modulesDirty = moduleSearchDirty = false;
            var locations = ModuleLocations();
            List<ModuleRow> rows = ModuleRows(locations);
            List<ShopInventory.Location> choices = ModuleStorageChoices();
            ShopInventory.Location storage = BoundModuleStorage(choices);
            var available = new HashSet<GameItem>(rows.Where(row => !row.Installed).Select(row => row.Item));
            selectedModules.RemoveWhere(item => !available.Contains(item));
            selectedInstalledModules.RemoveWhere(item => item == null || !ShopInventory.Owned(item));
            var matching = rows.Where(r => r.Installed == (moduleTab == 1) && (!compatibleOnly || r.Compatibility > 0) &&
                (string.IsNullOrWhiteSpace(moduleSearch) || r.Item.GetDisplayName(true).IndexOf(moduleSearch.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0) &&
                (moduleCategory == 0 || ModuleCategory(r.Item) == moduleCategory)).ToList();
            var displayed = OrderModules(matching).ToList();
            bool filtered = compatibleOnly || !string.IsNullOrWhiteSpace(moduleSearch) || moduleCategory != 0;
            var bays = locations.Where(location => location.IsMachine && location.Role == "module" &&
                location.Inventory is GameGridInventory && !ReferenceEquals(location.Inventory, moduleBay) &&
                (!filtered || displayed.Any(row => ReferenceEquals(row.Grid, location.Inventory))))
                .OrderBy(location => location.Name, StringComparer.CurrentCulture).ToList();
            var idleEntries = displayed.Select(ModuleEntry).ToList();
            int idleWidth = rows.Where(row => !row.Installed).Sum(row => row.Item.modifiedShape?.globalWidth ?? 0);
            if (searchOnly && moduleTab == 0 && !layoutOpen && moduleView != null &&
                moduleView.Matches(this, idleEntries, idleWidth, storage, rows)) return;
            moduleView = null;
            float extraHeight = 0, previewCell = 0;
            int dockRows = moduleDockRows;
            bool settled = false;
            for (int reflows = 0; reflows <= 4; reflows++)
                if (!RenderModuleResults(rows, displayed, bays, idleEntries, idleWidth, storage,
                    ref extraHeight, ref previewCell, ref dockRows)) { settled = true; break; }
            moduleDockRows = dockRows;
            if (settled && moduleTab == 0 && !layoutOpen && modulesWindow?.IsAlive == true && moduleIdleGrid != null)
                moduleView = new ModuleViewSnapshot(this, idleEntries, idleWidth, storage, rows);
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
                shell.OnClose(() => { if (ReferenceEquals(modulesWindow, own)) { modulesWindow = null; moduleResults = moduleIdleGrid = null; moduleSourceScroll = moduleBayScroll = null; moduleTarget = null; moduleBay = null; moduleDockRows = 0; moduleView = null; modulesDirty = moduleSearchDirty = false; selectedModules.Clear(); selectedInstalledModules.Clear(); CloseLayout(); } });
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
            if (moduleMenu)
            {
                builder.BeginRow().AddButton(ShopSettings.Text("grid.choose_storage"), ChooseModuleStorage, "storage_change");
                builder.Last.SetFixedWidth(150);
                builder.AddButton(ShopSettings.Text("machines.refresh"), () => { modulesDirty = true; }, "refresh");
                builder.Last.SetFixedWidth(110);
                builder.End();
            }
            int columns = moduleColumns > 0 ? moduleColumns : Mathf.Max(1, Mathf.FloorToInt((leftWidth - 22) / 34));
            int visibleRows = moduleVisibleRows > 0 ? moduleVisibleRows : Mathf.Max(1, Mathf.FloorToInt((size.y - 140) / 34));
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
                DrawRealModuleBoard(builder, storageGrid, leftWidth, rows, null);
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
            // A search or a category change must not move its own controls while the player uses them.
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
            float chrome = (width - 32 < 760 ? 225 : 185) + (moduleMenu ? 40 : 0);
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
            if (width < 760) builder.End().BeginRow();
            builder.AddInput(ShopSettings.Text("f10.search"), moduleSearch, ChangeModuleSearch, "search", ChangeModuleSearch);
            builder.Last.SetFixedWidth(Mathf.Clamp(width * .22f, 130, 220));
            builder.AddToggle(ShopSettings.Text("grid.compatible"), compatibleOnly,
                value => { compatibleOnly = value; modulePage = 0; modulesDirty = true; }, "compatible");
            builder.Last.SetFixedWidth(120);
            builder.AddDropdown(ModuleCategories(), moduleCategory,
                index => { moduleCategory = index; modulePage = 0; modulesDirty = true; }, "category");
            builder.Last.SetFixedWidth(130);
            builder.End();
        }

        private static string[] ModuleCategories() => new[] { ShopSettings.Text("f10.all_types"), ShopSettings.Text("f10.universal"),
            ShopSettings.Text("f10.hydroponic"), ShopSettings.Text("f10.evaporator"), ShopSettings.Text("f10.printer"),
            ShopSettings.Text("f10.furnace"), ShopSettings.Text("f10.other") };

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
            if (modulesWindow.Get("search")?.input?.placeholder is TMPro.TMP_Text placeholder)
                placeholder.text = ShopSettings.Text("f10.search");
            CustomUIElement category = modulesWindow.Get("category");
            string[] choices = ModuleCategories();
            if (category?.dropdown != null && !category.dropdown.options.Select(option => option.text).SequenceEqual(choices))
            {
                category.SetOptions(choices);
                category.SetIndex(moduleCategory, false);
            }
        }

        private void SwitchModuleTab(int tab) { moduleTab = tab; modulePage = 0; RenderModules(); }

        private void ChangeModuleSearch(string value)
        {
            string next = value ?? "";
            if (moduleSearch == next) return;
            moduleSearch = next; modulePage = 0; moduleSearchDirty = true;
        }

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
            return rows.OrderByDescending(row => row.Compatibility)
                .ThenBy(row => row.Item.GetDisplayName(true), StringComparer.CurrentCulture)
                .ThenBy(row => row.Path, StringComparer.CurrentCulture);
        }

        private ItemGrid.Entry ModuleEntry(ModuleRow row)
        {
            GameItem item = row.Item;
            string state = ModuleState(item);
            string compatible = row.Compatibility > 0 ? ShopSettings.Text("f10.type_match") :
                row.Compatibility < 0 ? ShopSettings.Text("f10.type_mismatch") : ShopSettings.Text("f10.type_unknown");
            return new ItemGrid.Entry(item, item.parentInventory) { Caption = item.GetDisplayName(true),
                Badge = state.Length > 0 ? state : compatible,
                Marker = ModuleMarker(item),
                Reason = string.Join(" · ", new[] { row.Path, compatible, state }.Where(value => !string.IsNullOrEmpty(value))),
                Selected = row.Installed ? selectedInstalledModules.Contains(item) : selectedModules.Contains(item),
                Enabled = state.Length == 0 && ShopInventory.Movable(item) };
        }

        private static string ModuleState(GameItem item) => item.IsTag(ModuleHelper.MODULE_STUCK_TAG) ? ShopSettings.Text("f10.stuck") :
            ItemPinHelper.IsPinned(item) ? ShopSettings.Text("f10.fixed") :
            RequestBook.Reserved(item) ? ShopSettings.Text("f10.reserved") :
            item.parentInventory != null && item.parentInventory.IsRemoveLocked() ? ShopSettings.Text("f10.locked") : "";

        private static string ModuleMarker(GameItem item) => item.IsTag(ModuleHelper.MODULE_STUCK_TAG) ? ShopSettings.Text("grid.mark_stuck") :
            ItemPinHelper.IsPinned(item) ? ShopSettings.Text("grid.mark_fixed") : RequestBook.Reserved(item) ? ShopSettings.Text("grid.mark_reserved") :
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
            VisualChoices.ChooseLocation(ShopSettings.Text("f10.choose_storage"), choices, BoundModuleStorage(choices),
                location => { if (SameModuleSession()) { BindModuleStorage(location); moduleMenu = false; } }, owner: modulesWindow);
        }

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
            builder.BeginRow().AddLabel(caption, "storage_status");
            builder.Last.SetFixedWidth(Mathf.Clamp(width * .42f, 180, 440));
            FitModuleText(builder.Last);
            builder.WithTooltip(caption);
            if (storage == null)
            {
                builder.AddButton(ShopSettings.Text("grid.choose_storage"), ChooseModuleStorage, "storage_choose_footer");
                builder.Last.SetFixedWidth(150);
            }
            builder.AddButton(ShopSettings.Text("grid.more"), () => { moduleMenu = !moduleMenu; modulesDirty = true; }, "menu");
            builder.Last.SetFixedWidth(70);
            builder.End();
        }

        private static int ModuleCategory(GameItem item)
        {
            if (item.IsTag("MODULE_TYPE_UNIVERSAL")) return 1;
            if (item.IsTag("MODULE_TYPE_HYDROPONIC")) return 2;
            if (item.IsTag("MODULE_TYPE_EVAPORATOR")) return 3;
            if (item.IsTag("MODULE_TYPE_PRINTER")) return 4;
            if (item.IsTag("MODULE_TYPE_FURNACE")) return 5;
            return 6;
        }

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
                item.MaxNumRemove() < item.unitCount || item.IsTag(ModuleHelper.MODULE_STUCK_TAG) || RequestBook.Reserved(item) ||
                ShopInventory.WouldContainSelf(item, destination)) return false;
            var locations = ModuleLocations();
            bool validSource = locations.Any(location => ReferenceEquals(location.Inventory, entry.Source) &&
                (location.Ordinary || location.IsMachine && location.Role == "module"));
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
            if (ItemGridMove.TryMove(item, destination, shape, out bool restored))
            {
                selectedModules.Remove(item); selectedInstalledModules.Remove(item); layoutPlan = null; modulesDirty = true; return;
            }
            ShopNotice.Show(ShopSettings.Text(restored ? "f10.changed" : "f10.inspect"));
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
                if (!installed && !location.Ordinary) continue;
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
            var ordinary = ShopInventory.MachineLocations().Where(location => location.Ordinary).ToList();
            var result = new List<ShopInventory.Location>(ordinary);
            var seen = new HashSet<GameInventory>(ordinary.Select(location => location.Inventory));
            foreach (ShopInventory.Location parent in ordinary)
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
            moduleTarget = null; moduleBay = null; selectedModules.Clear(); selectedInstalledModules.Clear(); modulesDirty = moduleSearchDirty = false;
            moduleTab = 0; modulePage = 0; moduleMenu = false;
            moduleWindowWidth = 0; moduleColumns = moduleVisibleRows = moduleDockRows = 0; moduleView = null;
            window?.Close();
        }
    }
}
