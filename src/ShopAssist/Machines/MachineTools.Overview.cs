using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

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
        private string machinesRun;
        private int machinesSlot;
        private bool machinesDirty, onlyAttention, machinesTruncated;
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
            RenderMachines();
        }

        private void TickMachineOverview()
        {
            if (!overviewEnabled.Value) { if (machinesWindow != null) CloseMachines(); return; }
            if (machinesWindow != null)
            {
                if (!machinesWindow.IsAlive || !SameMachineSession()) { CloseMachines(); return; }
                if (machinesDirty) RenderMachines();
            }
            KeyCode key = overviewKey();
            if (key != KeyCode.None && ShopInput.Ready() && Input.GetKeyDown(key)) OpenMachines();
        }

        private bool SameMachineSession() => ShopInventory.InStore && PlayerStore.Instance.runID == machinesRun &&
            PlayerStore.Instance.saveSlotId == machinesSlot;

        private void MachineSettingsChanged() { if (!overviewEnabled.Value) CloseMachines(); else machinesDirty = true; }

        private void RenderMachines()
        {
            if (!SameMachineSession()) { CloseMachines(); return; }
            machinesDirty = false;
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
            CustomUIWindow previous = machinesWindow;
            machinesWindow = null;
            previous?.Close();
            var builder = CustomUIManager.Window(ModEntry.Id + ".f30", ShopSettings.Text("f30.open"), CustomUIManager.LAYER_META)
                .SetSize(620, height).SetDraggable(true).SetCloseOnEscape(true);
            machinesWindow = builder.GetWindow();
            CustomUIWindow own = machinesWindow;
            builder.OnClose(() => { if (ReferenceEquals(machinesWindow, own)) { machinesWindow = null; machinesRun = null; } });
            if (machinesTruncated) builder.AddWarning(ShopSettings.Text("machines.partial"));
            builder.BeginRow().AddToggle(ShopSettings.Text("f30.attention"), onlyAttention, value => { onlyAttention = value; machinesDirty = true; });
            builder.AddFlexibleSpace().AddDropdown(labels, selected, index =>
            {
                filterPath = index <= 0 || index > paths.Count ? null : paths[index - 1];
                machinesDirty = true;
            }).End().BeginScroll(scrollHeight);
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
                var entry = new ItemGrid.Entry(row.Item) { Caption = row.Item.GetDisplayName(true), Badge = row.Status,
                    Reason = ShopSettings.Text("vm.location") + " · " + row.Path,
                    Selected = ReferenceEquals(row, selectedRow) };
                cards.Add(entry); cardRows[entry] = row;
            }
            if (cards.Count > 0)
                ItemGrid.Add(builder, cards, new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 140, CardHeight = 100,
                    OnClick = entry =>
                    {
                        if (!SameMachineSession() || !cardRows.TryGetValue(entry, out MachineRow row) ||
                            !ShopInventory.Owned(row.Item)) { machinesDirty = true; return; }
                        SelectOverview(row); RenderMachines();
                    } });
            foreach (MachineRow row in withoutShape)
            {
                builder.BeginRow().AddLabel(row.Item.GetDisplayName(true) + " · " + row.Path + " · " + row.Status);
                if (builder.Last.text != null) builder.Last.text.richText = false;
                if (VisibleParent(row.Item, out _)) builder.AddButton(ShopSettings.Text("f30.locate"), () => Locate(row.Item));
                builder.End();
            }
            if (shown.Count == 0) builder.AddLabel(ShopSettings.Text("f30.empty"));
            builder.End();
            if (selectedRow != null)
            {
                builder.BeginRow().AddLabel(ShopSettings.Text("vm.location") + " · " + selectedRow.Path);
                if (builder.Last.text != null) builder.Last.text.richText = false;
                if (VisibleParent(selectedRow.Item, out _))
                    builder.AddButton(ShopSettings.Text("f30.locate"), () => Locate(selectedRow.Item), "locate");
                builder.End().AddLabel(selectedRow.Status);
                if (builder.Last.text != null) builder.Last.text.richText = false;
            }
            builder.AddButton(ShopSettings.Text("machines.refresh"), () => { if (SameMachineSession()) RenderMachines(); }).Show();
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

        private static bool VisibleParent(GameItem item, out PixelWindow window)
        {
            window = null;
            if (!ShopInventory.Owned(item) || !(item is GameItemElement element) || element.handler == null ||
                !element.handler.gameObject.activeInHierarchy || WindowsHandler.current?.visibleWindows == null) return false;
            window = element.handler.FindParentWindow();
            return window != null && window.IsVisible() && WindowsHandler.current.visibleWindows.Contains(window);
        }

        private static void Locate(GameItem item) { if (VisibleParent(item, out PixelWindow window)) window.ToFront(); }

        private List<MachineRow> MachineRows()
        {
            var result = new List<MachineRow>();
            machinesTruncated = false;
            var locations = ShopInventory.MachineLocations();
            var seen = new HashSet<GameItem>();
            foreach (ShopInventory.Location location in locations)
            {
                if (location.IsMachine) continue;
                foreach (GameItem item in location.Inventory.childItems)
                {
                    if (item == null || !seen.Add(item) || !ShopInventory.Owned(item) ||
                        !(item.IsGameItemType("MACHINE") || item.IsTag("machinery") || item.IsTag("HYDROPONIC_MACHINE_TAG"))) continue;
                    bool attention;
                    string status;
                    switch (item.identifier)
                    {
                        case "moisture_farm": status = MoistureStatus(item, locations, out attention); break;
                        case "evaporator": case "printer": case "furnace": status = ProductionStatus(item, out attention); break;
                        default: status = HydroponicStatus(item, locations, out attention); break;
                    }
                    result.Add(new MachineRow { Item = item, Path = location.Name, Status = status, NeedsAttention = attention });
                    if (result.Count >= 100) { machinesTruncated = true; return result; }
                }
            }
            Dictionary<int, GameItem> owned = UniqueOwnedItems();
            foreach (AttentionItem issue in AttentionBook.Snapshot().Where(issue => issue.ItemId > 0 &&
                (issue.Feature == "f31.open" || issue.Feature == "f32.open" || issue.Feature == "f35.open")))
            {
                if (!owned.TryGetValue(issue.ItemId, out GameItem host)) continue;
                result.Add(new MachineRow { Item = host, Path = MachineLocation(host, locations), NeedsAttention = true,
                    Identity = issue.Key, ReasonKey = issue.ReasonKey, Status = ShopSettings.Text(issue.ReasonKey ?? "availability.unknown") });
                if (result.Count >= 100) { machinesTruncated = true; return result; }
            }
            return result;
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
                if (distillate.childItem != null && WaterHelper.GetCurrentCapacityML(distillate.childItem) > 0)
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
                    var chip = MachinePanel<GameSlotInventory>(machine, "chip");
                    if (chip == null) return ShopSettings.Text("f30.unknown") + suffix;
                    if (chip.childItem == null) status = "f30.chip";
                    else if (!WillRunCycle(machine)) status = "f30.materials";
                }
                else
                {
                    var bay = MachinePanel<GameGridInventory>(machine, "module");
                    if (bay == null) return ShopSettings.Text("f30.unknown") + suffix;
                    int required = bay.childItems.Any(i => i.identifier == "furnace_module_blast") ? 3 : 2;
                    if (input.childItems.Count(i => i.identifier == "scrap_metal" || i.identifier == "common_ore") < required)
                        status = "f30.materials";
                    // A furnace can still refuse an output shape. Only its native cycle may decide that.
                }
            }
            if (status != null) return ShopSettings.Text(status) + suffix;
            var battery = power.childItem;
            if (battery == null) return ShopSettings.Text("f30.battery") + suffix;
            if (!OrdinaryBattery(battery)) return ShopSettings.Text("f30.power_unknown") + suffix;
            if (battery.GetTagReadonly("power_source_item_energy").GetInt() < MachineryHelper.GetMachinePowerUsage(machine))
                return ShopSettings.Text("f30.power") + suffix;
            attention = suffix.Length != 0;
            return ShopSettings.Text(machine.identifier == "printer" && MachineProgressHelper.GetMachineState(machine) == MachineProgressHelper.STATE_WORKING
                ? "f30.working" : "f30.supplied") + suffix;
        }

        private static string HydroponicStatus(GameItem machine, List<ShopInventory.Location> locations, out bool attention)
        {
            attention = true;
            if (!machine.IsTag("HYDROPONIC_MACHINE_TAG")) return ShopSettings.Text("f30.unknown");
            var own = locations.Where(location => ReferenceEquals(location.Owner, machine)).ToList();
            GameSlotInventory seed = SingleSlot(own, "seed"), water = SingleSlot(own, "water"), power = SingleSlot(own, "power");
            var output = own.Where(location => location.Role == "output").Select(location => location.Inventory).ToList();
            if (seed == null || water == null || power == null || output.Count != 1) return ShopSettings.Text("f30.unknown");
            int waiting = output[0].childItems.Count;
            string suffix = waiting > 0 ? " · " + ShopSettings.Format("f30.output", waiting) : "";
            if (seed.childItem == null) return ShopSettings.Text("f30.seed") + suffix;
            if (water.childItem == null) return ShopSettings.Text("f30.water_container") + suffix;
            if (WaterHelper.GetCurrentCapacityML(water.childItem) < HydroponicHelper.GetWaterUsePerCycle(seed.childItem))
                return ShopSettings.Text("f30.water") + suffix;
            if (power.childItem == null) return ShopSettings.Text("f30.battery") + suffix;
            GameItem battery = power.childItem;
            if (!battery.IsTag("power_source_item") || battery.IsTag("CURRENT_CONVERTER_TAG") || battery.IsTag("HYPERTECH_BATTERY_TAG"))
                return ShopSettings.Text("f30.power_unknown") + suffix;
            if (battery.GetTagReadonly("power_source_item_energy").GetInt() < MachineryHelper.GetMachinePowerUsage(machine))
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
            if (!battery.IsTag("power_source_item") || battery.IsTag("CURRENT_CONVERTER_TAG") || battery.IsTag("HYPERTECH_BATTERY_TAG"))
                return ShopSettings.Text("f30.power_unknown");
            if (battery.GetTagReadonly("power_source_item_energy").GetInt() < MachineryHelper.GetMachinePowerUsage(machine))
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
            machinesWindow = null; machinesRun = null; machinesDirty = false; filterPath = null;
            selectedOverviewMachine = null; selectedOverviewPath = selectedOverviewSlot = null;
            window?.Close();
        }
    }

    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.UncheckedAccept))]
    internal static class MachineGridAddedPatch { private static void Postfix() => MachineTools.InventoryChanged(); }
    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.Expel))]
    internal static class MachineGridRemovedPatch { private static void Postfix() => MachineTools.InventoryChanged(); }
    [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.UncheckedAccept))]
    internal static class MachineSlotAddedPatch { private static void Postfix() => MachineTools.InventoryChanged(); }
    [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.Expel))]
    internal static class MachineSlotRemovedPatch { private static void Postfix() => MachineTools.InventoryChanged(); }
}
