using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private sealed class MaintenanceProblem
        {
            internal int Kind, ItemId, Slot;
            internal string Run, Reason;
        }

        private readonly Dictionary<string, MaintenanceProblem> maintenanceProblems = new Dictionary<string, MaintenanceProblem>();

        private static GameItem FindUniqueOwnedItem(int id)
        {
            if (!ShopInventory.InStore || id <= 0) return null;
            UniqueOwnedItems().TryGetValue(id, out GameItem found);
            return found;
        }

        private static Dictionary<int, GameItem> UniqueOwnedItems() => !ShopInventory.InStore ? new Dictionary<int, GameItem>() :
            PlayerStore.Instance.FindAllItem().Where(item => item != null && item.uniqueId > 0 && ShopInventory.Owned(item)).Distinct()
                .GroupBy(item => item.uniqueId).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.First());

        private static string MachineLocation(GameItem item, List<ShopInventory.Location> locations = null)
        {
            if (item == null || !ShopInventory.InStore) return "";
            locations = locations ?? ShopInventory.MachineLocations();
            return locations.FirstOrDefault(location => !location.IsMachine && location.Inventory.childItems.Any(child => ReferenceEquals(child, item)))?.Name ?? "";
        }

        private static bool MachineAccessible(GameItem item, List<ShopInventory.Location> locations = null)
        {
            if (item == null || !ShopInventory.InStore) return false;
            locations = locations ?? ShopInventory.MachineLocations();
            return locations.Any(location => !location.IsMachine && location.Inventory.childItems.Any(child => ReferenceEquals(child, item)));
        }

        private void RememberMaintenanceFailure(GameItem host, int kind, string reason, bool inspect = false, GameItem filter = null)
        {
            if (!started || !ShopInventory.InStore || host == null || !ShopInventory.Owned(host) || host.uniqueId <= 0 ||
                !ReadMaintenance(out MaintenanceData data)) return;
            MaintenanceRule configured = data.rules.Find(rule => rule.kind == kind && rule.host == host.uniqueId);
            if (configured == null || kind == 35 && filter != null && !AllowsFilter(configured, filter)) return;
            if (inspect)
            {
                var result = new OperationResult(kind == 31 ? "f31.open" : "f35.open");
                result.Skip(host, 1, reason, MachineLocation(host), true);
                AttentionBook.Report(result);
                return;
            }
            if (kind == 35) { machinesDirty = true; AttentionBook.Changed(); return; }
            // Compatibility is derived from the current methods rather than stored as a second source.
            if (reason == "maintenance.conflict" || reason == "maintenance.unsupported")
            {
                ActionAvailability capability = BatteryCapability(host);
                if (!capability.Allowed && capability.ReasonKey == reason) { AttentionBook.Changed(); machinesDirty = true; return; }
            }
            string key = kind + ":" + host.uniqueId;
            if (maintenanceProblems.TryGetValue(key, out MaintenanceProblem old) && old.Run == PlayerStore.Instance.runID &&
                old.Slot == PlayerStore.Instance.saveSlotId && old.Reason == reason) return;
            maintenanceProblems[key] = new MaintenanceProblem { Kind = kind, ItemId = host.uniqueId,
                Run = PlayerStore.Instance.runID, Slot = PlayerStore.Instance.saveSlotId, Reason = reason };
            machinesDirty = true; AttentionBook.Changed();
        }

        private void ClearMaintenanceFailure(int kind, int id)
        {
            if (maintenanceProblems.Remove(kind + ":" + id)) { machinesDirty = true; AttentionBook.Changed(); }
        }

        private string CurrentBatteryProblem(GameItem host, MaintenanceRule rule)
        {
            if (!batteryEnabled.Value) return "f31.paused";
            if (host.IsTag("BROKEN_MACHINE_TAG")) return "f30.broken";
            ActionAvailability capability = BatteryCapability(host);
            if (!capability.Allowed) return capability.ReasonKey;
            var power = MachineHelper.GetBatterySlot(host);
            GameItem installed = power?.childItem;
            if (!OrdinaryBattery(installed) || ItemPinHelper.IsPinned(installed)) return "maintenance.swap_unavailable";
            int energy = installed.GetTagReadonly("power_source_item_energy").GetInt();
            int capacity = installed.GetTagReadonly("power_source_item_max_energy").GetInt();
            if (energy < 0 || capacity <= 0) return "maintenance.swap_unavailable";
            if ((long)energy * 100 >= (long)capacity * rule.threshold) return null;
            GameGridInventory source = ResolveMaintenanceSource(rule);
            if (source == null) return "maintenance.source_missing";
            int required = MachineryHelper.GetMachinePowerUsage(host);
            if (required < 0) return "maintenance.unsupported";
            var candidates = Ordered(source).Where(candidate => candidate.identifier == installed.identifier && OrdinaryBattery(candidate) &&
                PowerHelper.CanDrawPowerSource(candidate, required) && candidate.GetTagReadonly("power_source_item_energy").GetInt() > energy).ToList();
            if (candidates.Count == 0) return "f31.missing";
            if (candidates.Any(candidate => MaintenanceSwap.Ready(host, power, source, installed, candidate))) return "f31.next_attempt";
            return candidates.Any(candidate => MaintenanceSwap.CallbackCapability(power, source, installed, candidate).Allowed) ?
                "maintenance.swap_unavailable" : "maintenance.conflict";
        }

        private IEnumerable<AttentionItem> MachineAttentionItems()
        {
            var items = new Dictionary<string, AttentionItem>(StringComparer.Ordinal);
            if (!started || !ShopInventory.InStore) return items.Values;
            string run = PlayerStore.Instance.runID; int slot = PlayerStore.Instance.saveSlotId;
            List<ShopInventory.Location> locations = ShopInventory.MachineLocations();
            Dictionary<int, GameItem> owned = UniqueOwnedItems();
            GameItem Find(int id) => owned.TryGetValue(id, out GameItem found) ? found : null;
            void Add(string key, string feature, GameItem host, string reason, bool inspect = false, Action retry = null, Action resolve = null)
            {
                items[key] = new AttentionItem { Key = key, Feature = feature, ItemId = host?.uniqueId ?? 0,
                    Title = host?.GetDisplayName(true) ?? ShopSettings.Text(feature), Location = MachineLocation(host, locations),
                    ReasonKey = reason, NeedsInspection = inspect, Retry = retry, Resolve = resolve };
            }
            if (ReadSeedRules(out SeedRules seeds))
                foreach (SeedRule rule in seeds.rules.Where(row => row.state != (int)ReplantPhase.Idle))
                {
                    int id = rule.machine; GameItem host = Find(id);
                    if (host?.identifier != "hydroponic" || !MachineAccessible(host, locations)) host = null;
                    if (rule.state == (int)ReplantPhase.Pending && rule.reason == "f32.pending" && replantEnabled.Value && host != null) continue;
                    string reason = host == null ? "f32.machine_unavailable" :
                        !replantEnabled.Value && rule.state == (int)ReplantPhase.Pending ? "f32.paused" : rule.reason;
                    bool inspect = rule.state == (int)ReplantPhase.Inspect;
                    Action retry = !inspect && replantEnabled.Value && host != null ? (Action)(() => RequestReplant(id, run, slot)) : null;
                    Action resolve = inspect && host != null ? (Action)(() => ResolveReplant(id, run, slot)) : null;
                    Add("f32:" + id, "f32.open", host, reason, inspect, retry, resolve);
                    items["f32:" + id].ActionLabelKey = "f32.retry";
                    items["f32:" + id].ResolveLabelKey = "f32.resolve";
                    if (host == null) items["f32:" + id].ItemId = id;
                }
            else Add("f32:invalid", "f32.open", null, "f32.invalid", true);

            if (!ReadMaintenance(out MaintenanceData maintenance))
                Add("maintenance:invalid", "f31.open", null, "maintenance.invalid", true);
            var filterRows = MaintenanceFilterRows(locations, maintenance).ToList();
            foreach (MachineRow row in filterRows)
            {
                if (AttentionBook.HasInspection("f35.open", row.Item.uniqueId)) continue;
                Add("f35:" + row.Item.uniqueId + ":" + row.Identity, "f35.open", row.Item, row.ReasonKey);
                int separator = row.Status?.IndexOf(" · ", StringComparison.Ordinal) ?? -1;
                if (row.Status?.StartsWith("#", StringComparison.Ordinal) == true && separator > 0)
                    items["f35:" + row.Item.uniqueId + ":" + row.Identity].Title += " · " + row.Status.Substring(0, separator);
            }

            foreach (var pair in maintenanceProblems.ToArray())
            {
                string key = pair.Key; MaintenanceProblem problem = pair.Value;
                if (problem.Run != run || problem.Slot != slot) continue;
                if (AttentionBook.HasInspection(problem.Kind == 31 ? "f31.open" : "f35.open", problem.ItemId)) continue;
                GameItem host = Find(problem.ItemId);
                if (host == null) continue;
                MaintenanceRule rule = maintenance?.rules.Find(row => row.kind == problem.Kind && row.host == problem.ItemId);
                if (rule == null) continue;
                string currentReason = CurrentBatteryProblem(host, rule);
                if (currentReason == null) continue;
                ActionAvailability currentCapability = BatteryCapability(host);
                if (batteryEnabled.Value && !currentCapability.Allowed && currentCapability.ReasonKey == currentReason) continue;
                Add("maintenance:" + key, "f31.open", host, currentReason, false, null, () =>
                {
                    if (!ShopInventory.InStore || PlayerStore.Instance.runID != run || PlayerStore.Instance.saveSlotId != slot ||
                        !ReferenceEquals(FindUniqueOwnedItem(problem.ItemId), host) ||
                        !maintenanceProblems.TryGetValue(key, out MaintenanceProblem live) || live != problem) return;
                    ClearMaintenanceFailure(problem.Kind, problem.ItemId);
                });
            }

            if (maintenance != null)
                foreach (MaintenanceRule rule in maintenance.rules)
                {
                    if (!(rule.kind == 31 ? batteryEnabled.Value : filterEnabled.Value)) continue;
                    if (AttentionBook.HasInspection(rule.kind == 31 ? "f31.open" : "f35.open", rule.host)) continue;
                    GameItem host = Find(rule.host);
                    if (host == null || !(rule.kind == 31 ? BatteryHost(host) : IsFilterHost(host))) continue;
                    if (rule.kind == 31)
                    {
                        ActionAvailability capability = BatteryCapability(host);
                        if (!capability.Allowed) Add("f31:" + rule.host + ":capability", "f31.open", host, capability.ReasonKey);
                        continue;
                    }
                    GameItem filter = FilterSlots(host).FirstOrDefault()?.childItem;
                    if (filter != null && !AllowsFilter(rule, filter)) filter = null;
                    ActionAvailability general = FilterCapability(host, filter, FilterPath.Any);
                    if (!general.Allowed)
                    {
                        if (!filterRows.Any(row => row.Item.uniqueId == rule.host && row.ReasonKey == general.ReasonKey))
                            Add("f35:" + rule.host + ":capability", "f35.open", host, general.ReasonKey);
                        continue;
                    }
                    if (host.identifier != "water_pitcher") continue;
                    ActionAvailability transfer = FilterCapability(host, filter, FilterPath.Transfer);
                    ActionAvailability added = FilterCapability(host, filter, FilterPath.Added);
                    if (!transfer.Allowed) Add("f35:" + rule.host + ":transfer", "f35.open", host,
                        transfer.Code == ActionUnavailableReason.Unsupported ? "f35.transfer_unsupported" : "f35.transfer_conflict");
                    if (!added.Allowed) Add("f35:" + rule.host + ":added", "f35.open", host,
                        added.Code == ActionUnavailableReason.Unsupported ? "f35.added_unsupported" : "f35.added_conflict");
                }
            return items.Values;
        }
    }
}
