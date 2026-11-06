using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private const string FeedKey = "f33.rules";
        [Serializable] private sealed class FeedRule
        {
            [JsonRequired] public int machine;
            [JsonRequired] public string source, destination, material, pause = "";
            [JsonRequired] public int limit, completed, pending;
            [JsonRequired] public List<int> staged = new List<int>();
        }
        [Serializable] private sealed class FeedRules
        {
            [JsonRequired] public int version = 1;
            [JsonRequired] public string run;
            [JsonRequired] public int slot;
            [JsonRequired] public List<FeedRule> rules = new List<FeedRule>();
        }
        private sealed class FurnaceWrapper
        {
            internal WeakReference<GameItem> Machine;
            internal Action<GameItem, GameInventory, SlotMarker> Original, Wrapped;
        }
        private sealed class FurnaceCycle
        {
            internal GameItem Machine;
            internal int[] Staged;
        }

        private ConfigEntry<bool> feedEnabled;
        private CustomUIWindow feedWindow;
        private GameItem feedMachine;
        private string feedRun, feedMaterial;
        private int feedSlot, feedLimit = 1;
        private ShopInventory.Location feedSource, feedDestination;
        private readonly List<FurnaceWrapper> furnaceWrappers = new List<FurnaceWrapper>();
        private readonly HashSet<GameItem> furnaceInCycle = new HashSet<GameItem>();
        private bool feedNeedsAttach;

        private void InitFeed()
        {
            feedEnabled = settings.Toggle("f33.enabled", "f33.tip");
            settings.Action("f33.open", OpenFeed, () => Availability("f33.open"));
        }

        private void StartFeed()
        {
            ModHook.OnGameLoadedInit += FeedLoaded;
            feedNeedsAttach = true;
        }
        private void StopFeed()
        {
            ModHook.OnGameLoadedInit -= FeedLoaded;
            UnwrapFurnaces(); furnaceInCycle.Clear(); feedNeedsAttach = false; CloseFeed();
        }
        private void FeedLoaded() { UnwrapFurnaces(); feedNeedsAttach = true; }
        private void UnwrapFurnaces(GameItem only = null)
        {
            foreach (FurnaceWrapper record in furnaceWrappers.ToArray())
            {
                if (!record.Machine.TryGetTarget(out GameItem machine)) { furnaceWrappers.Remove(record); continue; }
                if (only != null && !ReferenceEquals(only, machine)) continue;
                var callbacks = machine.onCycleEndSlotItemFunc?.GetInvocationList();
                if (callbacks != null)
                {
                    Action<GameItem, GameInventory, SlotMarker> restored = null;
                    foreach (Delegate callback in callbacks)
                        restored += callback.Equals(record.Wrapped) ? record.Original : (Action<GameItem, GameInventory, SlotMarker>)callback;
                    machine.onCycleEndSlotItemFunc = restored;
                }
                furnaceWrappers.Remove(record);
            }
        }
        private void TickFeed()
        {
            if (feedEnabled.Value && feedNeedsAttach && ShopInventory.InStore) { feedNeedsAttach = false; WrapAvailableFurnaces(); }
            if (feedWindow != null && (!feedWindow.IsAlive || !FeedContext())) CloseFeed();
        }
        private void FeedSettingsChanged()
        {
            if (!feedEnabled.Value)
            {
                CloseFeed();
                UnwrapFurnaces();
                feedNeedsAttach = true;
            }
            else if (furnaceWrappers.Count == 0) feedNeedsAttach = true;
        }

        internal static bool CanOpenFeedFor(GameItem item) => current?.started == true && current.feedEnabled.Value &&
            ShopInventory.InStore && item?.identifier == "furnace" && FindTarget(item, out _, out _);

        private static bool FocusedFurnace(out GameItem furnace)
        {
            furnace = null;
            if (!ShopInventory.InStore || !FindTarget(out GameItem item, out _) || item.identifier != "furnace") return false;
            furnace = item; return true;
        }
        private bool FeedContext() => feedEnabled.Value && ShopInventory.InStore && feedMachine != null &&
            feedRun == PlayerStore.Instance.runID && feedSlot == PlayerStore.Instance.saveSlotId &&
            CanOpenFeedFor(feedMachine);

        private void OpenFeed()
        {
            if (!feedEnabled.Value || !FocusedFurnace(out GameItem machine) || CustomUIManager.Instance == null) return;
            OpenFeedFor(machine);
        }

        internal static void OpenFeedFor(GameItem machine)
        {
            if (!CanOpenFeedFor(machine) || CustomUIManager.Instance == null) return;
            current.OpenFeedTarget(machine);
        }

        private void OpenFeedTarget(GameItem machine)
        {
            CloseFeed(); feedMachine = machine; feedRun = PlayerStore.Instance.runID; feedSlot = PlayerStore.Instance.saveSlotId;
            feedLimit = 1; DrawFeed();
        }
        private void CloseFeed()
        {
            CustomUIWindow window = feedWindow; feedWindow = null; feedMachine = null; feedRun = null;
            feedSource = feedDestination = null; feedMaterial = null; window?.Close();
        }

        private bool ReadFeedRules(out FeedRules data)
        {
            data = null;
            if (!ShopInventory.InStore) return false;
            try
            {
                string raw = ModHelper.GetModData(ModEntry.Id, FeedKey, null);
                data = raw == null ? new FeedRules { run = PlayerStore.Instance.runID, slot = PlayerStore.Instance.saveSlotId } :
                    ShopData.Decode<FeedRules>(raw, 65536);
                if (data == null || data.version != 1 || data.run != PlayerStore.Instance.runID || data.slot != PlayerStore.Instance.saveSlotId ||
                    data.rules == null || data.rules.Count > 100 || data.rules.Any(rule => rule == null || rule.machine <= 0 ||
                        string.IsNullOrEmpty(rule.source) || rule.source.Length > 160 || string.IsNullOrEmpty(rule.destination) ||
                        rule.destination.Length > 160 || !AllowedMaterial(rule.material) || rule.limit < 1 || rule.limit > 10 ||
                        rule.completed < 0 || rule.completed > rule.limit || rule.pending < 0 || rule.staged == null ||
                        !(rule.staged.Count == 0 || rule.staged.Count == 2) || rule.staged.Any(id => id <= 0) ||
                        rule.staged.Distinct().Count() != rule.staged.Count ||
                        rule.staged.Count > 0 && (rule.completed >= rule.limit || rule.pending != 0) || !AllowedPause(rule.pause)) ||
                    data.rules.Select(rule => rule.machine).Distinct().Count() != data.rules.Count)
                    throw new FormatException("Invalid F33 rules or save identity");
                return true;
            }
            catch (Exception error) { data = null; log("F33 kept unreadable or mismatched rules: " + error.Message); return false; }
        }
        private static bool AllowedMaterial(string id) => id == "scrap_metal" || id == "common_ore";
        private static bool AllowedPause(string pause) => pause == "" || pause == "f33.source" || pause == "f33.output" ||
            pause == "f33.power" || pause == "f33.machine" || pause == "f33.no_progress" || pause == "f33.inspect";
        private static void SaveFeedRules(FeedRules data) => ModHelper.SetModData(ModEntry.Id, FeedKey, ShopData.Encode(data));

        private void DrawFeed()
        {
            if (!FeedContext()) { CloseFeed(); return; }
            var builder = UiLayout.Rebuild(ref feedWindow, ModEntry.Id + ".f33", ShopSettings.Text("f33.open"), 580, 520);
            feedWindow = builder.GetWindow(); var own = feedWindow;
            builder.OnClose(() => { if (ReferenceEquals(feedWindow, own)) feedWindow = null; });
            builder.AddHeader(feedMachine.GetDisplayName(true));
            if (builder.Last.text != null) builder.Last.text.richText = false;
            if (!ReadFeedRules(out FeedRules data)) { builder.AddWarning(ShopSettings.Text("f33.invalid")).Show(); return; }
            FeedRule rule = data.rules.Find(row => row.machine == feedMachine.uniqueId && row.machine > 0);
            if (rule != null)
            {
                builder.AddLabel(ShopSettings.Format("f33.progress", rule.completed, rule.limit));
                if (!string.IsNullOrEmpty(rule.pause)) builder.AddWarning(ShopSettings.Text(rule.pause));
                builder.BeginRow().AddButton(ShopSettings.Text("f33.stop"), () =>
                {
                    if (!FeedContext() || !ReadFeedRules(out FeedRules live)) return;
                    live.rules.RemoveAll(row => row.machine == feedMachine.uniqueId); SaveFeedRules(live);
                    UnwrapFurnaces(feedMachine); DrawFeed();
                }, "stop");
                if (!string.IsNullOrEmpty(rule.pause)) builder.AddButton(ShopSettings.Text("f33.resume"), () =>
                {
                    if (!FeedContext() || !ReadFeedRules(out FeedRules live)) return;
                    FeedRule row = live.rules.Find(r => r.machine == feedMachine.uniqueId);
                    if (row == null) return;
                    row.pause = ""; SaveFeedRules(live); DrawFeed();
                }, "resume");
                builder.End().Show(); return;
            }
            var locations = ShopInventory.OwnedLocations(false).Where(location => location.Ordinary && location.Inventory is GameGridInventory).ToList();
            if (feedSource != null) feedSource = locations.Find(location => ReferenceEquals(location.Inventory, feedSource.Inventory));
            if (feedDestination != null) feedDestination = locations.Find(location => ReferenceEquals(location.Inventory, feedDestination.Inventory));
            builder.BeginRow();
            VisualChoices.Location(builder, ShopSettings.Text("visual.source"), feedSource, locations, selected =>
            { feedSource = selected; feedMaterial = null; DrawFeed(); }, "source");
            VisualChoices.Slot(builder, ShopSettings.Text("visual.machine"), feedMachine, feedMachine.GetDisplayName(true), null, "machine");
            VisualChoices.Location(builder, ShopSettings.Text("visual.target"), feedDestination, locations, selected =>
            { feedDestination = selected; DrawFeed(); }, "destination");
            builder.End();
            var materials = feedSource == null ? new List<GameItem>() : feedSource.Inventory.childItems.Where(item =>
                item != null && AllowedMaterial(item.identifier) && ShopInventory.Movable(item)).GroupBy(item => item.identifier).Select(group => group.First()).ToList();
            var material = materials.FirstOrDefault(i => i.identifier == feedMaterial);
            VisualChoices.Slot(builder, ShopSettings.Text("f33.choose_material"), material, material?.GetDisplayName(true),
                () => VisualChoices.ChooseItem(ShopSettings.Text("f33.choose_material"), materials, material, selected =>
                { feedMaterial = selected.identifier; DrawFeed(); }), "material");
            builder.AddLabel(ShopSettings.Format("f33.batch_count", feedLimit), "limit_label");
            builder.AddSlider(1, 10, feedLimit, value =>
            {
                feedLimit = Mathf.RoundToInt(value);
                feedWindow?.Get("limit_label")?.SetText(ShopSettings.Format("f33.batch_count", feedLimit));
            }, wholeNumbers: true, id: "limit");
            builder.AddButton(ShopSettings.Text("f33.bind"), BindFeed, "bind").Show();
        }

        private void BindFeed()
        {
            if (!FeedContext() || !ReadFeedRules(out FeedRules data) || feedSource == null || feedDestination == null ||
                !AllowedMaterial(feedMaterial) || !ShopInventory.TryRefresh(feedSource, out var source) ||
                !ShopInventory.TryRefresh(feedDestination, out var destination) || !source.Ordinary || !destination.Ordinary)
            { ShopNotice.Show(ShopSettings.Text("f33.choose_all")); return; }
            if (!source.Inventory.childItems.Any(item => item != null && item.parentInventory == source.Inventory &&
                item.identifier == feedMaterial && item.unitCount > 0 && ShopInventory.Owned(item)))
            { feedMaterial = null; ShopNotice.Show(ShopSettings.Text("inv.changed")); DrawFeed(); return; }
            int machineId = feedMachine.GetUniqueID();
            if (data.rules.Count >= 100 || data.rules.Any(rule => rule.machine == machineId))
            { ShopNotice.Show(ShopSettings.Text("f33.invalid")); return; }
            string sourceKey = source.Key(true), destinationKey = destination.Key(true);
            if (string.IsNullOrEmpty(sourceKey) || string.IsNullOrEmpty(destinationKey))
            { ShopNotice.Show(ShopSettings.Text("f33.choose_all")); return; }
            data.rules.Add(new FeedRule { machine = machineId, source = sourceKey, destination = destinationKey,
                material = feedMaterial, limit = Mathf.Clamp(feedLimit, 1, 10) });
            SaveFeedRules(data); WrapFurnace(feedMachine); DrawFeed();
        }

        private void WrapAvailableFurnaces()
        {
            if (!started || !ShopInventory.InStore || !ReadFeedRules(out FeedRules data)) return;
            var configured = new HashSet<int>(data.rules.Select(rule => rule.machine));
            foreach (GameItem item in PlayerStore.Instance.FindAllItem())
                if (item != null && item.identifier == "furnace" && configured.Contains(item.uniqueId) && ShopInventory.Owned(item)) WrapFurnace(item);
        }

        private void WrapFurnace(GameItem machine)
        {
            if (!started || machine == null || machine.onCycleEndSlotItemFunc == null ||
                furnaceWrappers.Any(record => record.Machine.TryGetTarget(out GameItem found) && ReferenceEquals(found, machine))) return;
            var record = new FurnaceWrapper { Machine = new WeakReference<GameItem>(machine), Original = machine.onCycleEndSlotItemFunc };
            record.Wrapped = (item, inventory, slot) =>
            {
                if (!furnaceInCycle.Add(item)) { record.Original(item, inventory, slot); return; }
                FurnaceCycle cycle = null;
                try
                {
                    try { cycle = BeforeFurnace(item); }
                    catch (Exception error) { log("F33 skipped furnace preparation: " + error); }
                    bool ran = false;
                    try { record.Original(item, inventory, slot); ran = true; }
                    finally
                    {
                        if (cycle != null)
                        {
                            try { AfterFurnace(cycle, ran); }
                            catch (Exception error) { log("F33 stopped after furnace cycle: " + error); }
                        }
                    }
                }
                finally { furnaceInCycle.Remove(item); }
            };
            machine.onCycleEndSlotItemFunc = record.Wrapped;
            furnaceWrappers.Add(record);
        }

        private static GameGridInventory FurnaceGrid(GameItem machine, string role) => MachinePanel<GameGridInventory>(machine, role);

        private static T MachinePanel<T>(GameItem machine, string role) where T : GameInventory
        {
            if (!(machine?.contentWindow?.childElement is MachineryPanelRow row)) return null;
            var panels = row.Panels.Where(panel => panel != null && !panel.isHidden && panel.role == role && panel.content != null).ToList();
            if (panels.Count != 1) return null;
            var nodes = MachineryContainerUtils.Collect(panels[0].content);
            return nodes?.Count == 1 ? nodes[0] as T : null;
        }

        private FurnaceCycle BeforeFurnace(GameItem machine)
        {
            if (!started || !feedEnabled.Value || !ShopInventory.InStore || machine == null || machine.identifier != "furnace" ||
                machine.uniqueId <= 0 || !ShopInventory.Owned(machine) || ItemMouseDragHandler.current?.IsDraggingItem == true ||
                !ReadFeedRules(out FeedRules data)) return null;
            FeedRule rule = data.rules.Find(row => row.machine == machine.uniqueId);
            if (rule == null) return null;
            GameGridInventory input = FurnaceGrid(machine, "input"), output = FurnaceGrid(machine, "output");
            if (input == null || output == null) { PauseFeed(data, rule, "f33.machine"); return null; }
            if (rule.staged.Count == 2)
            {
                if (input.childItems.Count == 2 && rule.staged.All(id => input.childItems.Any(item => item.uniqueId == id)))
                    return new FurnaceCycle { Machine = machine, Staged = rule.staged.ToArray() };
                PauseFeed(data, rule, "f33.inspect"); return null;
            }
            if (rule.pause != "") return null;
            var locations = ShopInventory.OwnedLocations(false);
            var source = locations.Find(location => location.Ordinary && location.Key() == rule.source);
            var destination = locations.Find(location => location.Ordinary && location.Key() == rule.destination);
            if (!(source?.Inventory is GameGridInventory sourceGrid)) { PauseFeed(data, rule, "f33.source"); return null; }
            if (!(destination?.Inventory is GameGridInventory destinationGrid)) { PauseFeed(data, rule, "f33.output"); return null; }
            if (rule.pending > 0)
            {
                GameItem pending = output.childItems.Find(item => item.uniqueId == rule.pending);
                if (pending == null || !MoveToOrdinary(pending, output, destinationGrid)) { PauseFeed(data, rule, "f33.output"); return null; }
                rule.pending = 0; SaveFeedRules(data);
            }
            if (rule.completed >= rule.limit) return null;
            var modules = FurnaceGrid(machine, "module");
            if (modules == null || modules.childItems.Any(item => item.identifier == "furnace_module_blast" || item.identifier == "furnace_module_junk") ||
                input.childItems.Count != 0 || output.childItems.Count != 0 ||
                sourceGrid.childItems.Any(HopperHelper.IsHopper) || destinationGrid.childItems.Any(HopperHelper.IsHopper) ||
                !MachineHelper.CanPower(machine))
            { PauseFeed(data, rule, !MachineHelper.CanPower(machine) ? "f33.power" : "f33.machine"); return null; }
            var candidates = sourceGrid.childItems.Where(item => item != null && item.identifier == rule.material && item.unitCount == 1 &&
                ShopInventory.Movable(item) && !RequestBook.Reserved(item)).Take(2).ToList();
            if (candidates.Count != 2) { PauseFeed(data, rule, "f33.source"); return null; }
            GameItem sample = DirectoryMaster.Item("metal_ingot");
            if (sample == null) { PauseFeed(data, rule, "f33.machine"); return null; }
            try
            {
                if (GridPlacements.Empty(destinationGrid, sample) == null) { PauseFeed(data, rule, "f33.output"); return null; }
            }
            finally { GraphUtils.DestroyIfUnplaced(sample); }
            var reserved = new List<GridShape>();
            foreach (GameItem candidate in candidates)
            {
                SlotMarker marker = GridPlacements.Empty(input, candidate, reserved);
                if (marker == null) { PauseFeed(data, rule, "f33.machine"); return null; }
                reserved.Add(marker.itemGridShape.Clone());
            }
            rule.staged = candidates.Select(candidate => candidate.GetUniqueID()).ToList();
            SaveFeedRules(data);
            var moved = new List<(GameItem item, GridShape original)>();
            for (int i = 0; i < candidates.Count; i++)
            {
                GameItem candidate = candidates[i];
                bool success = false;
                try
                {
                    GridShape old = candidate.modifiedShape.Clone();
                    moved.Add((candidate, old));
                    SlotMarker marker = GridPlacements.Exact(input, candidate, reserved[i]);
                    success = marker != null && marker.TryAcceptOnce(1) == 1 && ReferenceEquals(candidate.parentInventory, input);
                }
                catch (Exception error) { log("F33 native material move failed: " + error); }
                if (!success)
                {
                    bool recovered = true;
                    foreach (var previous in moved.AsEnumerable().Reverse())
                    {
                        try { if (!ReturnToSource(previous.item, sourceGrid, previous.original)) recovered = false; }
                        catch (Exception error) { recovered = false; log("F33 material recovery failed: " + error); }
                    }
                    if (recovered) rule.staged.Clear();
                    PauseFeed(data, rule, recovered ? "f33.source" : "f33.inspect"); return null;
                }
            }
            return new FurnaceCycle { Machine = machine, Staged = rule.staged.ToArray() };
        }

        private void AfterFurnace(FurnaceCycle cycle, bool ran)
        {
            if (!ReadFeedRules(out FeedRules data)) return;
            GameItem machine = cycle.Machine;
            FeedRule rule = data.rules.Find(row => row.machine == machine.uniqueId);
            GameGridInventory input = FurnaceGrid(machine, "input"), output = FurnaceGrid(machine, "output");
            if (rule == null || input == null || output == null || !rule.staged.SequenceEqual(cycle.Staged)) return;
            int remaining = rule.staged.Count(id => input.childItems.Any(item => item.uniqueId == id));
            if (!ran || remaining != 0)
            {
                PauseFeed(data, rule, remaining == 2 ? "f33.no_progress" : "f33.inspect"); return;
            }
            if (output.childItems.Count != 1 || output.childItems[0].identifier != "metal_ingot")
            { PauseFeed(data, rule, "f33.inspect"); return; }
            GameItem product = output.childItems[0];
            rule.staged.Clear(); rule.completed++; rule.pending = product.GetUniqueID(); SaveFeedRules(data);
            var destination = ShopInventory.OwnedLocations(false).Find(location => location.Ordinary && location.Key() == rule.destination);
            if (!(destination?.Inventory is GameGridInventory grid) || !MoveToOrdinary(product, output, grid))
            { PauseFeed(data, rule, "f33.output"); return; }
            rule.pending = 0; SaveFeedRules(data);
        }

        private void PauseFeed(FeedRules data, FeedRule rule, string reason)
        {
            if (rule.pause == reason) return;
            rule.pause = reason; SaveFeedRules(data); ShopNotice.Show(ShopSettings.Text(reason));
        }

        private bool ReturnToSource(GameItem item, GameGridInventory source, GridShape original)
        {
            if (ReferenceEquals(item.parentInventory, source)) return true;
            try
            {
                SlotMarker marker = GridPlacements.Exact(source, item, original) ?? GridPlacements.Empty(source, item);
                if (marker != null && marker.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, source)) return true;
            }
            catch (Exception error) { log("F33 could not return material to its source: " + error); }
            if (item.parentInventory == null)
            {
                foreach (var location in ShopInventory.OwnedLocations(false))
                {
                    if (!location.Ordinary || !(location.Inventory is GameGridInventory grid)) continue;
                    try
                    {
                        SlotMarker recovery = GridPlacements.Empty(grid, item);
                        if (recovery != null && recovery.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory != null) break;
                    }
                    catch (Exception error) { log("F33 material recovery location failed: " + error); }
                }
            }
            return ReferenceEquals(item.parentInventory, source);
        }

        private bool MoveToOrdinary(GameItem item, GameGridInventory source, GameGridInventory destination)
        {
            if (item == null || !ReferenceEquals(item.parentInventory, source) || !ShopInventory.Movable(item) ||
                RequestBook.Reserved(item) || ShopInventory.WouldContainSelf(item, destination)) return false;
            SlotMarker slot = GridPlacements.Empty(destination, item);
            if (slot == null) return false;
            try { if (slot.TryAcceptOnce(item.unitCount) == item.unitCount && ReferenceEquals(item.parentInventory, destination)) return true; }
            catch (Exception error) { log("F33 output transfer failed: " + error); }
            if (item.parentInventory == null)
            {
                foreach (var location in ShopInventory.OwnedLocations(false))
                {
                    if (!location.Ordinary || !(location.Inventory is GameGridInventory grid)) continue;
                    try
                    {
                        SlotMarker recovery = GridPlacements.Empty(grid, item);
                        if (recovery != null && recovery.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory != null) break;
                    }
                    catch (Exception error) { log("F33 output recovery location failed: " + error); }
                }
            }
            return false;
        }

    }
}
