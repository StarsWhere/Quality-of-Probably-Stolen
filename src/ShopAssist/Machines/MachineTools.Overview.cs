using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private sealed class MachineRow
        {
            internal GameItem Item;
            internal string Path, Status, ReasonKey, Identity;
            internal bool NeedsAttention;
        }

        private ConfigEntry<bool> overviewEnabled;
        private Func<KeyCode> overviewKey;
        private CustomUIWindow machinesWindow;
        private RectTransform machinesScroll;
        private string machinesRun;
        private int machinesSlot, machinesRevision;
        private bool machinesDirty, onlyAttention, machinesTruncated;
        private bool overviewStructureDirty = true, overviewIssuesDirty = true, machineAttentionDirty;
        private float nextMachinesRefresh, nextMachineAttention, nextConverterRefresh;
        private List<ShopInventory.Location> overviewLocations;
        private List<MachineRow> overviewBaseRows, overviewIssueRows;
        private Dictionary<GameInventory, GameItem> overviewInventoryOwners;
        private Dictionary<int, GameItem> overviewOwned;
        private Dictionary<GameItem, MachineRow> overviewRowsByItem;
        private readonly HashSet<GameItem> overviewChangedMachines = new HashSet<GameItem>();
        private string filterPath;
        private GameItem selectedOverviewMachine;
        private string selectedOverviewPath, selectedOverviewSlot;

        private void InitMachineOverview()
        {
            overviewEnabled = settings.Toggle("f30.enabled", "f30.tip");
            overviewKey = settings.Key("f30.key", "f30.key.tip");
            settings.Action("f30.open", OpenMachines, () => overviewEnabled.Value && ShopInventory.InStore);
        }

        private void OpenMachines()
        {
            if (!overviewEnabled.Value || !ShopInventory.InStore || CustomUIManager.Instance == null) return;
            CloseMachines();
            machinesRun = PlayerStore.Instance.runID;
            machinesSlot = PlayerStore.Instance.saveSlotId;
            overviewStructureDirty = overviewIssuesDirty = true;
            RenderMachines();
        }

        private void TickMachineOverview()
        {
            if (machineAttentionDirty && Time.unscaledTime >= nextMachineAttention)
            {
                machineAttentionDirty = false;
                nextMachineAttention = Time.unscaledTime + 0.5f;
                AttentionBook.Changed();
                if (machinesWindow != null) { overviewIssuesDirty = true; machinesDirty = true; }
            }
            if (!overviewEnabled.Value) { if (machinesWindow != null) CloseMachines(); return; }
            if (machinesWindow != null)
            {
                if (!machinesWindow.IsAlive || !SameMachineSession()) { CloseMachines(); return; }
                if (Time.unscaledTime >= nextConverterRefresh && overviewBaseRows != null)
                {
                    nextConverterRefresh = Time.unscaledTime + 2f;
                    foreach (MachineRow row in overviewBaseRows)
                    {
                        GameItem battery = MachinePanel<GameSlotInventory>(row.Item, "power")?.childItem;
                        if (battery?.IsTag("CURRENT_CONVERTER_TAG") == true)
                        { overviewChangedMachines.Add(row.Item); machinesDirty = true; }
                    }
                }
                if (machinesDirty && Time.unscaledTime >= nextMachinesRefresh && !OverviewInteractionBusy()) RenderMachines();
            }
            KeyCode key = overviewKey();
            if (key != KeyCode.None && ShopInput.Ready() && Input.GetKeyDown(key)) OpenMachines();
        }

        private bool SameMachineSession() => ShopInventory.InStore && PlayerStore.Instance.runID == machinesRun &&
            PlayerStore.Instance.saveSlotId == machinesSlot;

        private bool OverviewInteractionBusy()
        {
            if (StoreUIManager.IsAnyTyping() || UIDragHandler.isDraggingAnyUI || Input.GetMouseButton(0) ||
                ItemMouseDragHandler.current?.IsDraggingItem == true || ItemGrid.DragActive) return true;
            return machinesWindow?.root != null &&
                (machinesWindow.root.GetComponentsInChildren<TMPro.TMP_Dropdown>(true).Any(dropdown => dropdown.IsExpanded) ||
                 machinesWindow.root.GetComponentsInChildren<Dropdown>(true).Any(dropdown =>
                     dropdown.transform.Find("Dropdown List")?.gameObject.activeInHierarchy == true));
        }

        private void MachineSettingsChanged()
        {
            if (!overviewEnabled.Value) CloseMachines();
            else { overviewStructureDirty = overviewIssuesDirty = true; machinesDirty = true; }
        }

        private void RenderMachines()
        {
            if (!SameMachineSession()) { CloseMachines(); return; }
            machinesDirty = false;
            nextMachinesRefresh = Time.unscaledTime + 0.3f;
            List<MachineRow> rows = MachineRows();
            var paths = rows.Select(row => row.Path).Distinct().OrderBy(path => path, StringComparer.CurrentCulture).ToList();
            if (filterPath != null && !paths.Contains(filterPath)) filterPath = null;
            var labels = new List<string> { ShopSettings.Text("f30.all") };
            labels.AddRange(paths);
            int selected = filterPath == null ? 0 : paths.IndexOf(filterPath) + 1;
            var shown = rows.Where(row => (!onlyAttention || row.NeedsAttention) &&
                (filterPath == null || row.Path == filterPath)).ToList();
            int shaped = shown.Count(row => row.Item.modifiedShape != null && row.Item.modifiedShape.globalWidth > 0 &&
                row.Item.modifiedShape.globalHeight > 0);
            int gridRows = (shaped + 3) / 4;
            float scrollHeight = Mathf.Min(205, Mathf.Max(50, gridRows * 100 + (shown.Count - shaped) * 28 + 4));
            int height = Mathf.Clamp(Mathf.CeilToInt(240 + scrollHeight), 350, 480);
            float scrollPosition = machinesScroll?.GetComponentInParent<ScrollRect>()?.verticalNormalizedPosition ?? 1f;
            if (machinesWindow == null || !machinesWindow.IsAlive)
            {
                var shell = CustomUIManager.Window(ModEntry.Id + ".f30", ShopSettings.Text("f30.open"), CustomUIManager.LAYER_META)
                    .SetDraggable(true).SetCloseOnEscape(true);
                machinesWindow = shell.GetWindow();
                CustomUIWindow opened = machinesWindow;
                shell.OnClose(() =>
                {
                    if (ReferenceEquals(machinesWindow, opened))
                    { machinesWindow = null; machinesScroll = null; machinesRun = null; machinesRevision++; }
                }).Show();
            }
            CustomUIWindow own = machinesWindow;
            int revision = ++machinesRevision;
            bool CurrentView() => ReferenceEquals(machinesWindow, own) && own.IsAlive &&
                machinesRevision == revision && SameMachineSession();
            Vector2 position = own.Rect.anchoredPosition;
            UiLayout.Clear(own.ContentRoot, own);
            var builder = new CustomUIBuilder(CustomUIManager.Instance, own, own.ContentRoot);
            Vector2 size = UiLayout.Fit(620, height);
            builder.SetSize(size.x, size.y);
            own.Rect.anchoredPosition = position;
            if (machinesTruncated) builder.AddWarning(ShopSettings.Text("machines.partial"));
            builder.BeginRow().AddToggle(ShopSettings.Text("f30.attention"), onlyAttention, value => { if (CurrentView()) { onlyAttention = value; machinesDirty = true; } });
            builder.AddFlexibleSpace().AddDropdown(labels, selected, index =>
            {
                if (!CurrentView()) return;
                filterPath = index <= 0 || index > paths.Count ? null : paths[index - 1];
                machinesDirty = true;
            }).End().BeginScroll(scrollHeight);
            machinesScroll = builder.CurrentContainer;
            MachineRow selectedRow = shown.Find(row => ReferenceEquals(row.Item, selectedOverviewMachine) &&
                row.Path == selectedOverviewPath && OverviewSlot(row) == selectedOverviewSlot);
            if (selectedRow == null && shown.Count > 0) selectedRow = shown[0];
            SelectOverview(selectedRow);
            var cards = new List<ItemGrid.Entry>();
            var cardRows = new Dictionary<ItemGrid.Entry, MachineRow>();
            var withoutShape = new List<MachineRow>();
            foreach (MachineRow row in shown)
            {
                GridShape shape = row.Item.modifiedShape;
                if (shape == null || shape.globalWidth < 1 || shape.globalHeight < 1)
                { withoutShape.Add(row); continue; }
                var entry = new ItemGrid.Entry(row.Item) { Caption = row.Item.GetDisplayName(true), StatusLabel = row.Status,
                    SourceLabel = row.Path,
                    Selected = ReferenceEquals(row, selectedRow) };
                cards.Add(entry); cardRows[entry] = row;
            }
            if (cards.Count > 0)
                ItemGrid.Add(builder, cards, new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 140, CardHeight = 100,
                    OnClick = entry =>
                    {
                        if (!CurrentView()) return;
                        if (!cardRows.TryGetValue(entry, out MachineRow row) || !ShopInventory.Owned(row.Item))
                        { overviewStructureDirty = true; machinesDirty = true; return; }
                        SelectOverview(row); RenderMachines();
                    } });
            foreach (MachineRow row in withoutShape)
            {
                builder.BeginRow().AddLabel(row.Item.GetDisplayName(true) + " · " + row.Path + " · " + row.Status);
                if (builder.Last.text != null) builder.Last.text.richText = false;
                if (ShopInventory.Owned(row.Item)) builder.AddButton(ShopSettings.Text("f30.locate"), () => { if (CurrentView()) Locate(row.Item); });
                builder.End();
            }
            if (shown.Count == 0) builder.AddLabel(ShopSettings.Text("f30.empty"));
            builder.End();
            if (selectedRow != null)
            {
                builder.BeginRow().AddLabel(ShopSettings.Text("vm.location") + " · " + selectedRow.Path);
                if (builder.Last.text != null) builder.Last.text.richText = false;
                if (ShopInventory.Owned(selectedRow.Item))
                    builder.AddButton(ShopSettings.Text("f30.locate"), () => { if (CurrentView()) Locate(selectedRow.Item); }, "locate");
                builder.End().AddLabel(selectedRow.Status);
                if (builder.Last.text != null) builder.Last.text.richText = false;
            }
            builder.AddButton(ShopSettings.Text("machines.refresh"), () =>
            {
                if (!CurrentView()) return;
                overviewStructureDirty = overviewIssuesDirty = true;
                RenderMachines();
            });
            CustomUIBuilder.RebuildLayout(own.ContentRoot);
            UiLayout.RestoreScroll(machinesScroll, scrollPosition);
        }

        private static string OverviewSlot(MachineRow row)
        {
            if (!string.IsNullOrEmpty(row?.Identity)) return row.Identity;
            if (row?.Status == null || !row.Status.StartsWith("#", StringComparison.Ordinal)) return "";
            int separator = row.Status.IndexOf(" · ", StringComparison.Ordinal);
            return separator < 0 ? "" : row.Status.Substring(0, separator);
        }
        private void SelectOverview(MachineRow row)
        {
            selectedOverviewMachine = row?.Item; selectedOverviewPath = row?.Path; selectedOverviewSlot = OverviewSlot(row);
        }

        private static void Locate(GameItem item)
        {
            if (!ItemLocator.TryLocate(item, out string reason)) ShopNotice.Show(ShopSettings.Text(reason));
        }

        private List<MachineRow> MachineRows()
        {
            if (overviewStructureDirty || overviewLocations == null)
            {
                overviewStructureDirty = false;
                overviewIssuesDirty = true;
                overviewLocations = ShopInventory.MachineLocations();
                overviewInventoryOwners = new Dictionary<GameInventory, GameItem>();
                foreach (ShopInventory.Location location in overviewLocations.Where(location => location.IsMachine && location.Owner != null))
                    overviewInventoryOwners[location.Inventory] = location.Owner;
                overviewOwned = UniqueOwnedItems();
                overviewBaseRows = new List<MachineRow>();
                overviewRowsByItem = new Dictionary<GameItem, MachineRow>();
                var seen = new HashSet<GameItem>();
                foreach (ShopInventory.Location location in overviewLocations)
                {
                    if (location.IsMachine) continue;
                    foreach (GameItem item in location.Inventory.childItems)
                    {
                        if (item == null || !seen.Add(item) || !ShopInventory.Owned(item) || !OverviewMachine(item)) continue;
                        var row = new MachineRow { Item = item, Path = location.Name };
                        UpdateMachineRow(row);
                        overviewBaseRows.Add(row);
                        overviewRowsByItem[item] = row;
                        if (overviewBaseRows.Count > 100) break;
                    }
                    if (overviewBaseRows.Count > 100) break;
                }
                overviewChangedMachines.Clear();
            }
            else if (overviewChangedMachines.Count > 0)
            {
                foreach (MachineRow row in overviewBaseRows)
                    if (overviewChangedMachines.Contains(row.Item)) UpdateMachineRow(row);
                overviewChangedMachines.Clear();
            }
            if (overviewIssuesDirty || overviewIssueRows == null)
            {
                overviewIssuesDirty = false;
                overviewIssueRows = new List<MachineRow>();
                foreach (AttentionItem issue in AttentionBook.Snapshot().Where(issue => issue.ItemId > 0 &&
                    (issue.Feature == "f31.open" || issue.Feature == "f32.open" || issue.Feature == "f33.open" || issue.Feature == "f35.open")))
                {
                    if (!overviewOwned.TryGetValue(issue.ItemId, out GameItem host)) continue;
                    overviewIssueRows.Add(new MachineRow { Item = host, Path = MachineLocation(host, overviewLocations), NeedsAttention = true,
                        Identity = issue.Key, ReasonKey = issue.ReasonKey, Status = ShopSettings.Text(issue.ReasonKey ?? "availability.unknown") });
                }
            }
            machinesTruncated = overviewBaseRows.Count + overviewIssueRows.Count > 100;
            return overviewBaseRows.Concat(overviewIssueRows).Take(100).ToList();
        }

        private static bool OverviewMachine(GameItem item) => item.IsGameItemType("MACHINE") ||
            item.IsTag("machinery") || item.IsTag("HYDROPONIC_MACHINE_TAG");

        private void UpdateMachineRow(MachineRow row)
        {
            try
            {
                bool attention;
                switch (row.Item.identifier)
                {
                    case "moisture_farm": row.Status = MoistureStatus(row.Item, overviewLocations, out attention); break;
                    case "evaporator": case "printer": case "furnace": row.Status = ProductionStatus(row.Item, out attention); break;
                    default: row.Status = HydroponicStatus(row.Item, overviewLocations, out attention); break;
                }
                row.NeedsAttention = attention;
            }
            catch (Exception error)
            {
                row.Status = ShopSettings.Text("f30.unknown"); row.NeedsAttention = true;
                log("F30 could not read " + row.Item.identifier + ": " + error.Message);
            }
        }

        private static string ProductionStatus(GameItem machine, out bool attention)
        {
            attention = true;
            if (machine.IsTag("BROKEN_MACHINE_TAG")) return ShopSettings.Text("f30.broken");
            var power = MachinePanel<GameSlotInventory>(machine, "power");
            if (power == null) return ShopSettings.Text("f30.unknown");
            string suffix = "";
            string status = null;
            if (machine.identifier == "evaporator")
            {
                var vessel = MachinePanel<GameSlotInventory>(machine, "container");
                var distillate = MachinePanel<GameSlotInventory>(machine, "distillate");
                if (vessel == null || distillate == null) return ShopSettings.Text("f30.unknown");
                if (distillate.childItem != null && WaterHelper.GetCurrentCapacityUL(distillate.childItem) > 0)
                    suffix = " · " + ShopSettings.Text("f30.distillate");
                if (vessel.childItem == null) status = "f30.water_container";
                else if (!MachineEvaporator.CanEvaporateContainer(vessel.childItem)) status = "f30.no_evaporation";
            }
            else
            {
                var input = MachinePanel<GameGridInventory>(machine, "input");
                var output = MachinePanel<GameGridInventory>(machine, "output");
                if (input == null || output == null) return ShopSettings.Text("f30.unknown");
                if (output.childItems.Count > 0) suffix = " · " + ShopSettings.Format("f30.output", output.childItems.Count);
                if (machine.identifier == "printer")
                {
                    string state = MachineProgressHelper.GetMachineState(machine);
                    if (state != MachineProgressHelper.STATE_READY && state != MachineProgressHelper.STATE_WORKING)
                        return ShopSettings.Text("f30.unknown") + suffix;
                    var chip = MachinePanel<GameSlotInventory>(machine, "chip");
                    if (chip == null) return ShopSettings.Text("f30.unknown") + suffix;
                    if (state == MachineProgressHelper.STATE_READY)
                    {
                        if (chip.childItem == null) status = "f30.chip";
                        else if (!MachineProgressHelper.CanStart(chip.childItem, machine) ||
                            !MachineProgressHelper.HasRequiredAdditionalItems(chip.childItem, input.childItems)) status = "f30.materials";
                    }
                }
                else
                {
                    var bay = MachinePanel<GameGridInventory>(machine, "module");
                    if (bay == null) return ShopSettings.Text("f30.unknown") + suffix;
                    int required = GraphUtils.FindChildType<GameItem>(bay, i => i.identifier == "furnace_module_blast") != null ? 3 : 2;
                    int metal = GraphUtils.FindAllChildrenType<GameItem>(input,
                        i => i.identifier == "scrap_metal" || i.identifier == "common_ore").Count;
                    bool junk = MachineFurnace.HaveJunkModule(bay) &&
                        GraphUtils.FindAllChildrenType<GameItem>(input, i => i.identifier == "junk").Count >= 6;
                    if (metal < required && !junk)
                        status = "f30.materials";
                }
            }
            if (status != null) return ShopSettings.Text(status) + suffix;
            var battery = power.childItem;
            if (battery == null) return ShopSettings.Text("f30.battery") + suffix;
            if (!battery.IsTag("power_source_item")) return ShopSettings.Text("f30.power_unknown") + suffix;
            if (!MachineHelper.CanPower(machine, power))
                return ShopSettings.Text("f30.power") + suffix;
            if (machine.identifier == "furnace") return ShopSettings.Text("f30.verify_output") + suffix;
            attention = suffix.Length != 0;
            return ShopSettings.Text(machine.identifier == "printer"
                ? MachineProgressHelper.GetMachineState(machine) == MachineProgressHelper.STATE_WORKING ? "f30.working" : "f30.ready"
                : "f30.supplied") + suffix;
        }

        private static string HydroponicStatus(GameItem machine, List<ShopInventory.Location> locations, out bool attention)
        {
            attention = true;
            if (machine.IsTag("BROKEN_MACHINE_TAG")) return ShopSettings.Text("f30.broken");
            if (!machine.IsTag("HYDROPONIC_MACHINE_TAG")) return ShopSettings.Text("f30.unknown");
            if (machine.identifier != "hydroponic") return ShopSettings.Text("f30.unknown");
            var own = locations.Where(location => ReferenceEquals(location.Owner, machine)).ToList();
            GameSlotInventory seed = SingleSlot(own, "seed"), water = SingleSlot(own, "water"), power = SingleSlot(own, "power");
            var output = own.Where(location => location.Role == "output").Select(location => location.Inventory).ToList();
            if (seed == null || water == null || power == null || output.Count != 1) return ShopSettings.Text("f30.unknown");
            int waiting = output[0].childItems.Count;
            string suffix = waiting > 0 ? " · " + ShopSettings.Format("f30.output", waiting) : "";
            if (seed.childItem == null) return ShopSettings.Text("f30.seed") + suffix;
            if (water.childItem == null) return ShopSettings.Text("f30.water_container") + suffix;
            if (!(bool)AccessTools.Method(typeof(MachineHydroponic), "CanWater").Invoke(null, new object[] { machine }))
                return ShopSettings.Text("f30.water") + suffix;
            if (power.childItem == null) return ShopSettings.Text("f30.battery") + suffix;
            GameItem battery = power.childItem;
            if (!battery.IsTag("power_source_item"))
                return ShopSettings.Text("f30.power_unknown") + suffix;
            if (!MachineHelper.CanPower(machine, power))
                return ShopSettings.Text("f30.power") + suffix;
            string state = MachineProgressHelper.GetMachineState(machine);
            if (state != MachineProgressHelper.STATE_READY && state != MachineProgressHelper.STATE_WORKING)
                return ShopSettings.Text("f30.unknown") + suffix;
            attention = waiting > 0;
            return ShopSettings.Text(state == MachineProgressHelper.STATE_WORKING ? "f30.working" : "f30.ready") + suffix;
        }

        private static string MoistureStatus(GameItem machine, List<ShopInventory.Location> locations, out bool attention)
        {
            attention = true;
            if (machine.IsTag("BROKEN_MACHINE_TAG")) return ShopSettings.Text("f30.broken");
            var own = locations.FindAll(s => s.Owner == machine);
            var bottle = SingleSlot(own, "container"); var power = SingleSlot(own, "power");
            if (bottle == null || power == null) return ShopSettings.Text("f30.unknown");
            if (bottle.childItem == null) return ShopSettings.Text("f30.water_container");
            if (WaterHelper.GetFreeCapacity(bottle.childItem) == 0) return ShopSettings.Text("f30.farmFull");
            var battery = power.childItem;
            if (battery == null) return ShopSettings.Text("f30.battery");
            if (!battery.IsTag("power_source_item"))
                return ShopSettings.Text("f30.power_unknown");
            if (!MachineHelper.CanPower(machine, power))
                return ShopSettings.Text("f30.power");
            attention = false;
            return ShopSettings.Text("f30.ready");
        }

        private static GameSlotInventory SingleSlot(List<ShopInventory.Location> locations, string role)
        {
            var matches = locations.Where(location => location.Role == role).Select(location => location.Inventory).ToList();
            return matches.Count == 1 ? matches[0] as GameSlotInventory : null;
        }

        private void CloseMachines()
        {
            CustomUIWindow window = machinesWindow;
            machinesWindow = null; machinesScroll = null; machinesRun = null; machinesDirty = false; filterPath = null; machinesRevision++;
            selectedOverviewMachine = null; selectedOverviewPath = selectedOverviewSlot = null;
            overviewLocations = null; overviewBaseRows = null; overviewIssueRows = null;
            overviewInventoryOwners = null; overviewRowsByItem = null; overviewOwned = null;
            overviewChangedMachines.Clear(); overviewStructureDirty = overviewIssuesDirty = true;
            window?.Close();
        }

        private static void OverviewInventoryChanged(GameInventory inventory, GameItem item)
        {
            MachineTools owner = current;
            if (owner?.started != true) return;
            owner.modulesDirty = owner.maintenanceDirty = true;
            owner.machineAttentionDirty = true;
            if (owner.machinesWindow == null) return;
            if (inventory != null && owner.overviewInventoryOwners != null &&
                owner.overviewInventoryOwners.TryGetValue(inventory, out GameItem machine))
            { owner.overviewChangedMachines.Add(machine); owner.machinesDirty = true; }
            else if (item != null && (item.contentWindow != null || OverviewMachine(item)))
            { owner.overviewStructureDirty = true; owner.machinesDirty = true; }
        }

        internal static void OverviewItemChanged(GameItem item)
        {
            MachineTools owner = current;
            if (owner?.started != true || owner.machinesWindow == null || item == null || owner.overviewRowsByItem == null) return;
            GameItem machine = null;
            if (owner.overviewRowsByItem.ContainsKey(item)) machine = item;
            else if (item.parentInventory != null && owner.overviewInventoryOwners != null)
                owner.overviewInventoryOwners.TryGetValue(item.parentInventory, out machine);
            if (machine == null) return;
            owner.overviewChangedMachines.Add(machine);
            owner.machinesDirty = true;
        }

        internal static void OverviewAttentionChanged()
        {
            if (current?.started != true || current.machinesWindow == null) return;
            current.overviewIssuesDirty = true;
            current.machinesDirty = true;
        }
    }

    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.UncheckedAccept))]
    internal static class MachineGridAddedPatch { private static void Postfix(GameGridInventory __instance, GameItem __0, bool __result) { if (__result) MachineTools.InventoryChanged(__instance, __0); } }
    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.Expel))]
    internal static class MachineGridRemovedPatch { private static void Postfix(GameGridInventory __instance, GameItem __0, bool __result) { if (__result) MachineTools.InventoryChanged(__instance, __0); } }
    [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.UncheckedAccept))]
    internal static class MachineSlotAddedPatch { private static void Postfix(GameSlotInventory __instance, GameItem __0, bool __result) { if (__result) MachineTools.InventoryChanged(__instance, __0); } }
    [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.Expel))]
    internal static class MachineSlotRemovedPatch { private static void Postfix(GameSlotInventory __instance, GameItem __0, bool __result) { if (__result) MachineTools.InventoryChanged(__instance, __0); } }
    [HarmonyPatch(typeof(GameItem), "set_unitCount")]
    internal static class MachineQuantityChangedPatch { private static void Postfix(GameItem __instance) => MachineTools.OverviewItemChanged(__instance); }
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.ModifyTag), new Type[] { typeof(string), typeof(Action<TagState>), typeof(bool) })]
    internal static class MachineTagChangedPatch { private static void Postfix(GameItem __instance) => MachineTools.OverviewItemChanged(__instance); }
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.EnableTag), new Type[] { typeof(string), typeof(bool) })]
    internal static class MachineTagEnabledPatch { private static void Postfix(GameItem __instance, bool __result) { if (__result) MachineTools.OverviewItemChanged(__instance); } }
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.DisableTag), new Type[] { typeof(string), typeof(bool) })]
    internal static class MachineTagDisabledPatch { private static void Postfix(GameItem __instance, bool __result) { if (__result) MachineTools.OverviewItemChanged(__instance); } }
}
