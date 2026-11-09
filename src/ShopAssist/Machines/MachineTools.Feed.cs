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
            [JsonRequired] public bool stopped;
        }
        [Serializable] private sealed class FeedRules
        {
            [JsonRequired] public int version = 2;
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
            internal string Run;
            internal int Slot;
            internal bool ObserveOnly;
        }

        private ConfigEntry<bool> feedEnabled;
        private CustomUIWindow feedWindow;
        private GameItem feedMachine;
        private string feedRun, feedMaterial;
        private int feedSlot, feedLimit = 1;
        private ShopInventory.Location feedSource, feedDestination;
        private readonly List<FurnaceWrapper> furnaceWrappers = new List<FurnaceWrapper>();
        private readonly HashSet<GameItem> furnaceInCycle = new HashSet<GameItem>();
        private readonly HashSet<string> feedUnsaved = new HashSet<string>(StringComparer.Ordinal);
        private bool feedNeedsAttach, feedDirty;
        private string feedReadError;

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
            else if (feedWindow != null && feedDirty && !UIDragHandler.isDraggingAnyUI &&
                ItemMouseDragHandler.current?.IsDraggingItem != true) DrawFeed();
        }
        private void FeedSettingsChanged()
        {
            if (!feedEnabled.Value)
            {
                SuspendFeedTransactions();
                CloseFeed();
                UnwrapFurnaces();
                feedNeedsAttach = true;
            }
            else if (furnaceWrappers.Count == 0) feedNeedsAttach = true;
            machinesDirty = true; AttentionBook.Changed();
        }

        private void SuspendFeedTransactions()
        {
            if (!ShopInventory.InStore || !ReadFeedRules(out FeedRules data)) return;
            List<FeedRule> outstanding = data.rules.Where(rule => rule.staged.Count != 0 || rule.pending != 0).ToList();
            if (outstanding.Count == 0) return;
            foreach (FeedRule rule in outstanding) rule.pause = "f33.inspect";
            if (SaveFeedRules(data, 0)) return;
            foreach (FeedRule rule in outstanding) feedUnsaved.Add(FeedScope(data.run, data.slot, rule.machine));
            machinesDirty = true; AttentionBook.Changed();
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
            feedSource = feedDestination = null; feedMaterial = null; feedDirty = false; window?.Close();
        }

        private bool ReadFeedRules(out FeedRules data) => ReadFeedState(out data, out _) == MachineRuleReadStatus.Valid;

        private MachineRuleReadStatus ReadFeedState(out FeedRules data, out string raw)
        {
            data = null; raw = null;
            if (!ShopInventory.InStore || PlayerStore.Instance.saveSlotId < 0) return MachineRuleReadStatus.Unavailable;
            if (HasResetAttempt(FeedKey, PlayerStore.Instance.runID, PlayerStore.Instance.saveSlotId))
                return MachineRuleReadStatus.ResetPending;
            try
            {
                MachineRuleRecovery.ReadPending(ruleResetStore, FeedKey, PlayerStore.Instance.runID,
                    PlayerStore.Instance.saveSlotId, true, out MachineRuleResetJournal pending);
                if (pending != null) return MachineRuleReadStatus.ResetPending;
                raw = ruleResetStore.Read(FeedKey);
            }
            catch (Exception error)
            {
                LogFeedReadFailure(error);
                return MachineRuleReadStatus.Unavailable;
            }
            try { data = DecodeFeedRules(raw, PlayerStore.Instance.runID, PlayerStore.Instance.saveSlotId); feedReadError = null; return MachineRuleReadStatus.Valid; }
            catch (FormatException error) { LogFeedReadFailure(error); return MachineRuleReadStatus.Invalid; }
            catch (JsonException error) { LogFeedReadFailure(error); return MachineRuleReadStatus.Invalid; }
            catch (Exception error) { LogFeedReadFailure(error); return MachineRuleReadStatus.Unavailable; }
        }

        private void LogFeedReadFailure(Exception error)
        {
            if (feedReadError != error.Message) log("F33 kept unreadable or mismatched rules: " + error);
            feedReadError = error.Message;
        }

        private static FeedRules DecodeFeedRules(string raw, string run, int slot)
        {
            FeedRules data = raw == null ? new FeedRules { run = run, slot = slot } : ShopData.Decode<FeedRules>(raw, 65536);
            if (data == null || data.version != 2 || data.run != run || data.slot != slot ||
                data.rules == null || data.rules.Count > 100 || data.rules.Any(rule => rule == null || rule.machine <= 0 ||
                    string.IsNullOrEmpty(rule.source) || rule.source.Length > 160 || string.IsNullOrEmpty(rule.destination) ||
                    rule.destination.Length > 160 || !AllowedMaterial(rule.material) || rule.limit < 1 || rule.limit > 10 ||
                    rule.completed < 0 || rule.completed > rule.limit || rule.pending < 0 || rule.staged == null ||
                    !(rule.staged.Count == 0 || rule.staged.Count == 2) || rule.staged.Any(id => id <= 0) ||
                    rule.staged.Distinct().Count() != rule.staged.Count ||
                    rule.staged.Count > 0 && (rule.completed >= rule.limit || rule.pending != 0) || !AllowedPause(rule.pause)) ||
                data.rules.Select(rule => rule.machine).Distinct().Count() != data.rules.Count)
                throw new FormatException("Invalid F33 rules or save identity");
            return data;
        }
        private static bool AllowedMaterial(string id) => id == "scrap_metal" || id == "common_ore";
        private static bool AllowedPause(string pause) => pause == "" || pause == "f33.source" || pause == "f33.output" ||
            pause == "f33.power" || pause == "f33.machine" || pause == "f33.no_progress" || pause == "f33.inspect";
        private static string FeedScope(string run, int slot, int machine) => run + ":" + slot + ":" + machine;

        private bool SaveFeedRules(FeedRules data, int machine)
        {
            string key = FeedScope(data?.run, data?.slot ?? -1, machine);
            try
            {
                if (!ShopInventory.InStore || data == null || data.run != PlayerStore.Instance.runID || data.slot != PlayerStore.Instance.saveSlotId)
                    throw new InvalidOperationException("F33 save identity changed.");
                data.version = 2;
                string raw = ShopData.Encode(data);
                if (raw.Length > 65536) throw new InvalidOperationException("F33 data exceeds its size limit.");
                ModHelper.SetModData(ModEntry.Id, FeedKey, raw);
                if (ModHelper.GetModData(ModEntry.Id, FeedKey, null) != raw)
                    throw new InvalidOperationException("F33 update was not retained.");
                feedUnsaved.Remove(key);
                if (machine == 0 || feedMachine?.uniqueId == machine) feedDirty = true;
                machinesDirty = true; AttentionBook.Changed();
                return true;
            }
            catch (Exception error)
            {
                if (machine > 0) feedUnsaved.Add(key);
                log("F33 could not save batch state: " + error);
                if (machine == 0 || feedMachine?.uniqueId == machine) feedDirty = true;
                machinesDirty = true; AttentionBook.Changed();
                ShopNotice.Show(ShopSettings.Text("f33.save_failed"));
                return false;
            }
        }

        private void DrawFeed()
        {
            if (!FeedContext()) { CloseFeed(); return; }
            feedDirty = false;
            var builder = UiLayout.Rebuild(ref feedWindow, ModEntry.Id + ".f33", ShopSettings.Text("f33.open"), 580, 520);
            feedWindow = builder.GetWindow(); var own = feedWindow;
            builder.OnClose(() => { if (ReferenceEquals(feedWindow, own)) feedWindow = null; });
            builder.AddHeader(feedMachine.GetDisplayName(true));
            if (builder.Last.text != null) builder.Last.text.richText = false;
            MachineRuleReadStatus readStatus = ReadFeedState(out FeedRules data, out _);
            if (readStatus != MachineRuleReadStatus.Valid)
            {
                builder.AddWarning(ShopSettings.Text(readStatus == MachineRuleReadStatus.Invalid ? "f33.invalid" :
                    readStatus == MachineRuleReadStatus.ResetPending ? "maintenance.reset_failed" : "maintenance.read_failed"));
                if (readStatus == MachineRuleReadStatus.Invalid)
                    builder.AddButton(ShopSettings.Text("f33.reset"), () => ConfirmRuleReset(FeedKey, feedRun, feedSlot), "reset_rules");
                else if (readStatus == MachineRuleReadStatus.ResetPending)
                {
                    builder.AddButton(ShopSettings.Text("maintenance.reset_retry"), () => RetryRuleReset(FeedKey, feedRun, feedSlot), "retry_reset");
                    builder.AddButton(ShopSettings.Text("maintenance.reset_cancel"), () => CancelRuleReset(FeedKey, feedRun, feedSlot), "cancel_reset");
                }
                builder.Show(); return;
            }
            FeedRule rule = data.rules.Find(row => row.machine == feedMachine.uniqueId && row.machine > 0);
            if (rule != null)
            {
                builder.AddLabel(ShopSettings.Format("f33.progress", rule.completed, rule.limit));
                if (!string.IsNullOrEmpty(rule.pause)) builder.AddWarning(ShopSettings.Text(rule.pause));
                if (rule.stopped == true) builder.AddWarning(ShopSettings.Text("f33.stopped"));
                if (rule.pause == "f33.inspect" || rule.stopped || feedUnsaved.Contains(FeedScope(feedRun, feedSlot, rule.machine)))
                {
                    var trackedLocations = ShopInventory.CustomerLocations();
                    foreach (int id in rule.staged.Concat(rule.pending > 0 ? new[] { rule.pending } : Array.Empty<int>()))
                    {
                        GameItem tracked = FindUniqueOwnedItem(id);
                        string location = tracked == null ? null : trackedLocations.FirstOrDefault(place =>
                            ReferenceEquals(place.Inventory, tracked.parentInventory))?.Name;
                        builder.AddLabel(tracked == null ? ShopSettings.Text("f33.tracked_missing") :
                            ShopSettings.Format("f33.tracked", tracked.GetDisplayName(true), location ?? ShopSettings.Text("f30.unknown")));
                        if (builder.Last.text != null) builder.Last.text.richText = false;
                    }
                }
                builder.BeginRow().AddButton(ShopSettings.Text("f33.stop"), () =>
                {
                    if (FeedContext()) StopFeedRule(feedMachine.uniqueId, feedRun, feedSlot);
                    if (FeedContext()) DrawFeed();
                }, "stop");
                if (!string.IsNullOrEmpty(rule.pause) || rule.stopped == true) builder.AddButton(ShopSettings.Text("f33.resume"), () =>
                {
                    if (FeedContext()) ResumeFeedRule(feedMachine.uniqueId, feedRun, feedSlot);
                    if (FeedContext()) DrawFeed();
                }, "resume");
                if (rule.stopped)
                    builder.AddButton(ShopSettings.Text("f33.finish_tracking"), () =>
                    {
                        if (FeedContext()) ConfirmFinishFeed(feedMachine.uniqueId, feedRun, feedSlot);
                    }, "finish_tracking");
                if (FeedRecoveryRules.CanRestart(rule.completed, rule.limit, rule.staged.Count, rule.pending,
                    rule.pause != "", rule.stopped, feedUnsaved.Contains(FeedScope(feedRun, feedSlot, rule.machine))))
                    builder.AddButton(ShopSettings.Text("f33.restart"), () =>
                    {
                        if (FeedContext()) RestartFeedRule(feedMachine.uniqueId, feedRun, feedSlot);
                        if (FeedContext()) DrawFeed();
                    }, "restart");
                builder.End().Show(); return;
            }
            var locations = FeedLocations();
            if (feedSource != null) feedSource = locations.Find(location => ReferenceEquals(location.Inventory, feedSource.Inventory));
            if (feedDestination != null) feedDestination = locations.Find(location => ReferenceEquals(location.Inventory, feedDestination.Inventory));
            builder.BeginRow();
            VisualChoices.Location(builder, ShopSettings.Text("visual.source"), feedSource, () => FeedContext() ? FeedLocations() : new List<ShopInventory.Location>(), selected =>
            {
                if (!FeedContext() || !ShopInventory.TryRefresh(selected, out ShopInventory.Location live) || !live.Ordinary || !(live.Inventory is GameGridInventory))
                { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                feedSource = live; feedMaterial = null; DrawFeed();
            }, "source");
            VisualChoices.Slot(builder, ShopSettings.Text("visual.machine"), feedMachine, feedMachine.GetDisplayName(true), null, "machine");
            VisualChoices.Location(builder, ShopSettings.Text("visual.target"), feedDestination, () => FeedContext() ? FeedLocations() : new List<ShopInventory.Location>(), selected =>
            {
                if (!FeedContext() || !ShopInventory.TryRefresh(selected, out ShopInventory.Location live) || !live.Ordinary || !(live.Inventory is GameGridInventory))
                { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                feedDestination = live; DrawFeed();
            }, "destination");
            builder.End();
            var materials = FeedMaterials();
            var material = materials.FirstOrDefault(i => i.identifier == feedMaterial);
            VisualChoices.Slot(builder, ShopSettings.Text("f33.choose_material"), material, material?.GetDisplayName(true),
                () => VisualChoices.ChooseItem(ShopSettings.Text("f33.choose_material"), () => FeedContext() ? FeedMaterials() : new List<GameItem>(), material, selected =>
                {
                    if (!FeedContext() || !FeedMaterials().Contains(selected)) { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                    feedMaterial = selected.identifier; DrawFeed();
                }), "material");
            if (material != null)
                builder.AddLabel(ShopSettings.Format("f33.available", FeedCandidates(feedSource.Inventory as GameGridInventory, feedMaterial).Count));
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
            if (furnaceInCycle.Contains(feedMachine)) { ShopNotice.Show(ShopSettings.Text("f33.busy")); return; }
            if (FeedCandidates(source.Inventory as GameGridInventory, feedMaterial).Count < 2)
            { ShopNotice.Show(ShopSettings.Text("f33.source")); DrawFeed(); return; }
            int machineId = feedMachine.GetUniqueID();
            if (feedUnsaved.Contains(FeedScope(feedRun, feedSlot, machineId)) || AttentionBook.HasInspection("f33.open", machineId))
            { ShopNotice.Show(ShopSettings.Text("f33.inspect")); return; }
            if (!ReferenceEquals(FeedHost(machineId), feedMachine) || data.rules.Count >= 100 || data.rules.Any(rule => rule.machine == machineId))
            { ShopNotice.Show(ShopSettings.Text("f33.invalid")); return; }
            string sourceKey = source.Key(true), destinationKey = destination.Key(true);
            if (string.IsNullOrEmpty(sourceKey) || string.IsNullOrEmpty(destinationKey) ||
                !ReferenceEquals(FeedLocation(sourceKey)?.Inventory, source.Inventory) ||
                !ReferenceEquals(FeedLocation(destinationKey)?.Inventory, destination.Inventory))
            { ShopNotice.Show(ShopSettings.Text("f33.choose_all")); return; }
            data.rules.Add(new FeedRule { machine = machineId, source = sourceKey, destination = destinationKey,
                material = feedMaterial, limit = Mathf.Clamp(feedLimit, 1, 10), stopped = false });
            if (!SaveFeedRules(data, machineId)) return;
            WrapFurnace(feedMachine); DrawFeed();
        }

        private static List<GameItem> FeedCandidates(GameGridInventory source, string material) => source == null ? new List<GameItem>() :
            source.childItems.Where(item => item != null && item.identifier == material && item.unitCount == 1 &&
                ShopInventory.Movable(item)).ToList();

        private static List<ShopInventory.Location> FeedLocations() => ShopInventory.OwnedLocations(false)
            .Where(location => location.Ordinary && location.Inventory is GameGridInventory).ToList();

        private List<GameItem> FeedMaterials() => feedSource == null || !ShopInventory.TryRefresh(feedSource, out ShopInventory.Location source)
            ? new List<GameItem>() : source.Inventory.childItems.Where(item => item != null && AllowedMaterial(item.identifier) &&
                ShopInventory.Movable(item)).GroupBy(item => item.identifier).Select(group => group.First()).ToList();

        private static bool SameFeedScope(string run, int slot) => ShopInventory.InStore &&
            PlayerStore.Instance.runID == run && PlayerStore.Instance.saveSlotId == slot;

        private static GameItem FeedHost(int id)
        {
            GameItem machine = FindUniqueOwnedItem(id);
            return machine?.identifier == "furnace" && MachineAccessible(machine) ? machine : null;
        }

        private static ShopInventory.Location FeedLocation(string key)
        {
            var matches = ShopInventory.OwnedLocations(false).Where(location => location.Ordinary &&
                location.Inventory is GameGridInventory && location.Key() == key).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private void StopFeedRule(int id, string run, int slot)
        {
            if (!started || !SameFeedScope(run, slot) || !ReadFeedRules(out FeedRules data)) return;
            GameItem machine = FeedHost(id);
            if (machine == null || furnaceInCycle.Contains(machine)) { ShopNotice.Show(ShopSettings.Text("f33.busy")); return; }
            FeedRule rule = data.rules.Find(row => row.machine == id);
            if (rule == null) return;
            bool uncertain = FeedRecoveryRules.MustRetain(rule.staged.Count, rule.pending, rule.pause == "f33.inspect",
                feedUnsaved.Contains(FeedScope(run, slot, id)));
            if (uncertain) { rule.stopped = true; rule.pause = "f33.inspect"; }
            else data.rules.Remove(rule);
            if (!SaveFeedRules(data, id)) return;
            if (!uncertain) UnwrapFurnaces(machine);
        }

        private void RestartFeedRule(int id, string run, int slot)
        {
            if (!started || !feedEnabled.Value || !SameFeedScope(run, slot) ||
                feedUnsaved.Contains(FeedScope(run, slot, id)) || !ReadFeedRules(out FeedRules data)) return;
            GameItem machine = FeedHost(id);
            if (machine == null || furnaceInCycle.Contains(machine)) { ShopNotice.Show(ShopSettings.Text("f33.busy")); return; }
            FeedRule rule = data.rules.Find(row => row.machine == id);
            if (rule == null || !FeedRecoveryRules.CanRestart(rule.completed, rule.limit, rule.staged.Count, rule.pending,
                rule.pause != "", rule.stopped, feedUnsaved.Contains(FeedScope(run, slot, id)))) return;
            rule.completed = 0;
            if (SaveFeedRules(data, id)) WrapFurnace(machine);
        }

        private void ResumeFeedRule(int id, string run, int slot)
        {
            if (!started || !feedEnabled.Value || !SameFeedScope(run, slot) || !ReadFeedRules(out FeedRules data)) return;
            GameItem machine = FeedHost(id);
            FeedRule rule = data.rules.Find(row => row.machine == id);
            if (machine == null || rule == null || rule.pause == "" && rule.stopped != true &&
                !feedUnsaved.Contains(FeedScope(run, slot, id))) return;
            if (furnaceInCycle.Contains(machine)) { ShopNotice.Show(ShopSettings.Text("f33.busy")); return; }
            GameGridInventory input = FurnaceGrid(machine, "input"), output = FurnaceGrid(machine, "output");
            var source = FeedLocation(rule.source)?.Inventory as GameGridInventory;
            var destination = FeedLocation(rule.destination)?.Inventory as GameGridInventory;
            if (input == null || output == null) { ShopNotice.Show(ShopSettings.Text("f33.inspect")); return; }

            bool inInput = rule.staged.Count == 2 && input.childItems.Count == 2 && rule.staged.All(itemId =>
                input.childItems.Any(item => item.uniqueId == itemId && item.unitCount == 1));
            bool inSource = rule.staged.Count == 2 && source != null && rule.staged.All(itemId =>
                source.childItems.Any(item => item.uniqueId == itemId && item.unitCount == 1));
            GameItem product = rule.pending > 0 ? FindUniqueOwnedItem(rule.pending) : null;
            bool inOutput = product != null && ReferenceEquals(product.parentInventory, output) &&
                output.childItems.Count == 1 && product.identifier == "metal_ingot";
            bool delivered = product != null && destination != null && ReferenceEquals(product.parentInventory, destination) &&
                product.identifier == "metal_ingot";
            FeedRecoveryDecision decision = FeedRecoveryRules.Decide(rule.staged.Count != 0, rule.pending > 0, inInput, inSource,
                inOutput, delivered, input.childItems.Count == 0, output.childItems.Count == 0,
                rule.pause == "f33.inspect" || feedUnsaved.Contains(FeedScope(run, slot, id)));
            if (decision == FeedRecoveryDecision.Inspect) { ShopNotice.Show(ShopSettings.Text("f33.inspect")); return; }
            if (decision == FeedRecoveryDecision.ClearStaged) rule.staged.Clear();
            if (decision == FeedRecoveryDecision.ClearPending) rule.pending = 0;

            rule.pause = ""; rule.stopped = false;
            if (SaveFeedRules(data, id)) WrapFurnace(machine);
        }

        private void AcknowledgeFeedFailure(int id, string run, int slot)
        {
            if (!started || !SameFeedScope(run, slot) || !ReadFeedRules(out FeedRules data) ||
                data.rules.Any(rule => rule.machine == id)) return;
            GameItem machine = FeedHost(id);
            GameGridInventory input = FurnaceGrid(machine, "input"), output = FurnaceGrid(machine, "output");
            if (input == null || output == null || input.childItems.Count != 0 || output.childItems.Count != 0)
            { ShopNotice.Show(ShopSettings.Text("f33.inspect")); return; }
            if (feedUnsaved.Remove(FeedScope(run, slot, id))) { machinesDirty = true; AttentionBook.Changed(); }
        }

        private void ConfirmFinishFeed(int id, string run, int slot)
        {
            if (!started || !SameFeedScope(run, slot) || FeedHost(id) == null || !ReadFeedRules(out FeedRules data)) return;
            FeedRule rule = data.rules.Find(row => row.machine == id && row.stopped);
            if (rule == null) return;
            var prompt = GenericConfirmationUIManager.Instance;
            if (prompt == null) { ShopNotice.Show(ShopSettings.Text("f33.inspect")); return; }
            string snapshot = ShopData.Encode(rule);
            prompt.OpenPanel(ShopSettings.Text("f33.finish_warning"), ShopSettings.Text("f33.finish_tracking"),
                ShopSettings.Text("f33.finish_cancel"), () =>
                {
                    prompt.ClosePanel();
                    if (!started || !SameFeedScope(run, slot) || !ReadFeedRules(out FeedRules live)) return;
                    GameItem machine = FeedHost(id);
                    FeedRule currentRule = live.rules.Find(row => row.machine == id && row.stopped);
                    if (machine == null || furnaceInCycle.Contains(machine) || currentRule == null || ShopData.Encode(currentRule) != snapshot)
                    { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                    live.rules.Remove(currentRule);
                    if (!SaveFeedRules(live, id)) return;
                    UnwrapFurnaces(machine);
                    ShopNotice.Show(ShopSettings.Text("f33.finished"));
                }, () => prompt.ClosePanel());
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
                    catch (Exception error) { FeedFailure(item, error); }
                    bool ran = false;
                    try { record.Original(item, inventory, slot); ran = true; }
                    finally
                    {
                        if (cycle != null)
                        {
                            try { AfterFurnace(cycle, ran); }
                            catch (Exception error) { FeedFailure(item, error); }
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
                machine.uniqueId <= 0 || !ShopInventory.Owned(machine) ||
                !ReadFeedRules(out FeedRules data)) return null;
            FeedRule rule = data.rules.Find(row => row.machine == machine.uniqueId);
            if (rule == null) return null;
            if (!ReferenceEquals(FeedHost(rule.machine), machine)) { PauseFeed(data, rule, "f33.inspect"); return null; }
            GameGridInventory input = FurnaceGrid(machine, "input"), output = FurnaceGrid(machine, "output");
            if (input == null || output == null) { PauseFeed(data, rule, "f33.machine"); return null; }
            if (rule.staged.Count == 2)
            {
                if (output.childItems.Count == 0 && input.childItems.Count == 2 && rule.staged.All(id =>
                    input.childItems.Any(item => item.uniqueId == id && item.unitCount == 1)))
                    return new FurnaceCycle { Machine = machine, Staged = rule.staged.ToArray(), Run = data.run, Slot = data.slot,
                        ObserveOnly = rule.pause != "" || rule.stopped == true || feedUnsaved.Contains(FeedScope(data.run, data.slot, rule.machine)) };
                PauseFeed(data, rule, "f33.inspect"); return null;
            }
            if (rule.pause != "" || rule.stopped == true || feedUnsaved.Contains(FeedScope(data.run, data.slot, rule.machine)) ||
                ItemMouseDragHandler.current?.IsDraggingItem == true || UIDragHandler.isDraggingAnyUI) return null;
            var source = FeedLocation(rule.source);
            var destination = FeedLocation(rule.destination);
            if (!(source?.Inventory is GameGridInventory sourceGrid)) { PauseFeed(data, rule, "f33.source"); return null; }
            if (!(destination?.Inventory is GameGridInventory destinationGrid)) { PauseFeed(data, rule, "f33.output"); return null; }
            if (rule.pending > 0)
            {
                GameItem pending = FindUniqueOwnedItem(rule.pending);
                if (pending == null || pending.identifier != "metal_ingot") { PauseFeed(data, rule, "f33.inspect"); return null; }
                if (ReferenceEquals(pending.parentInventory, output))
                {
                    if (!MoveToOrdinary(pending, output, destinationGrid)) { PauseFeed(data, rule, "f33.output"); return null; }
                }
                else if (!ReferenceEquals(pending.parentInventory, destinationGrid))
                { PauseFeed(data, rule, "f33.inspect"); return null; }
                rule.pending = 0;
                if (!SaveFeedRules(data, rule.machine)) return null;
            }
            if (rule.completed >= rule.limit) return null;
            var modules = FurnaceGrid(machine, "module");
            if (modules == null || modules.childItems.Any(item => item.identifier == "furnace_module_blast" || item.identifier == "furnace_module_junk") ||
                input.childItems.Count != 0 || output.childItems.Count != 0 ||
                sourceGrid.childItems.Any(HopperHelper.IsHopper) || destinationGrid.childItems.Any(HopperHelper.IsHopper) ||
                !MachineHelper.CanPower(machine))
            { PauseFeed(data, rule, !MachineHelper.CanPower(machine) ? "f33.power" : "f33.machine"); return null; }
            var candidates = FeedCandidates(sourceGrid, rule.material).Take(2).ToList();
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
            var staged = candidates.Select(candidate => candidate.GetUniqueID()).ToList();
            if (staged.Any(id => id <= 0) || staged.Distinct().Count() != 2 ||
                candidates.Where((candidate, index) => !ReferenceEquals(FindUniqueOwnedItem(staged[index]), candidate)).Any())
            { PauseFeed(data, rule, "f33.inspect"); return null; }
            rule.staged = staged;
            if (!SaveFeedRules(data, rule.machine)) return null;
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
            return new FurnaceCycle { Machine = machine, Staged = rule.staged.ToArray(), Run = data.run, Slot = data.slot };
        }

        private void AfterFurnace(FurnaceCycle cycle, bool ran)
        {
            if (!SameFeedScope(cycle.Run, cycle.Slot) || !ReadFeedRules(out FeedRules data)) return;
            GameItem machine = cycle.Machine;
            if (machine == null) return;
            FeedRule rule = data.rules.Find(row => row.machine == machine.uniqueId);
            if (rule != null && !ReferenceEquals(FeedHost(rule.machine), machine)) { PauseFeed(data, rule, "f33.inspect"); return; }
            GameGridInventory input = FurnaceGrid(machine, "input"), output = FurnaceGrid(machine, "output");
            if (rule == null || input == null || output == null || !rule.staged.SequenceEqual(cycle.Staged)) return;
            int remaining = rule.staged.Count(id => input.childItems.Any(item => item.uniqueId == id));
            FeedCycleDecision decision = FeedRecoveryRules.AfterCycle(ran, remaining,
                output.childItems.Count == 1 && output.childItems[0]?.identifier == "metal_ingot");
            if (decision != FeedCycleDecision.Record)
            {
                if (decision == FeedCycleDecision.Inspect || !cycle.ObserveOnly)
                    PauseFeed(data, rule, decision == FeedCycleDecision.NoProgress ? "f33.no_progress" : "f33.inspect");
                return;
            }
            GameItem product = output.childItems[0];
            if (!ShopInventory.Owned(product)) { PauseFeed(data, rule, "f33.inspect"); return; }
            int productId = product.GetUniqueID();
            if (productId <= 0 || !ReferenceEquals(FindUniqueOwnedItem(productId), product))
            { PauseFeed(data, rule, "f33.inspect"); return; }
            rule.staged.Clear(); rule.completed++; rule.pending = productId;
            if (!SaveFeedRules(data, rule.machine)) return;
            if (!FeedRecoveryRules.CanTransfer(cycle.ObserveOnly, rule.pause != "", rule.stopped)) return;
            var destination = FeedLocation(rule.destination);
            if (!(destination?.Inventory is GameGridInventory grid) || !MoveToOrdinary(product, output, grid))
            { PauseFeed(data, rule, "f33.output"); return; }
            rule.pending = 0; SaveFeedRules(data, rule.machine);
        }

        private void PauseFeed(FeedRules data, FeedRule rule, string reason)
        {
            if (rule.pause == reason) return;
            rule.pause = reason; SaveFeedRules(data, rule.machine);
        }

        private void FeedFailure(GameItem machine, Exception error)
        {
            log("F33 stopped after an unexpected furnace error: " + error);
            if (machine == null || machine.uniqueId <= 0 || !ShopInventory.InStore) return;
            string key = FeedScope(PlayerStore.Instance.runID, PlayerStore.Instance.saveSlotId, machine.uniqueId);
            feedUnsaved.Add(key);
            if (ReadFeedRules(out FeedRules data))
            {
                FeedRule rule = data.rules.Find(row => row.machine == machine.uniqueId);
                if (rule != null) { rule.pause = "f33.inspect"; SaveFeedRules(data, machine.uniqueId); }
            }
            machinesDirty = true; AttentionBook.Changed();
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
                ShopInventory.WouldContainSelf(item, destination)) return false;
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
