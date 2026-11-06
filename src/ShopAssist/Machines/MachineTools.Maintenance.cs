using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private const string MaintenanceKey = "machineMaintenance";
        private ConfigEntry<bool> batteryEnabled, filterEnabled;
        private CustomUIWindow maintenanceWindow;
        private RectTransform maintenanceScroll;
        private string maintenanceRun;
        private int maintenanceSlot, maintenanceRevision;
        private GameItem maintenanceHost;
        private ShopInventory.Location maintenanceSource;
        private int maintenanceKind, maintenanceThreshold = 20, maintenancePriority;
        private int maintenanceMaterials, maintenanceGrade;
        private Harmony batteryHarmony, filterHarmony;
        private bool batteryHookFailed, filterHookFailed;
        private string maintenanceReadError;
        private readonly Dictionary<string, MethodBase> batteryCycles = new Dictionary<string, MethodBase>();
        private readonly Dictionary<int, int> batteryAttemptDay = new Dictionary<int, int>();
        private readonly Dictionary<GameSlotInventory, PendingFilter> pendingFilters = new Dictionary<GameSlotInventory, PendingFilter>();

        private sealed class MaintenanceData
        {
            [JsonRequired] public int version;
            [JsonRequired] public string run;
            [JsonRequired] public int slot;
            [JsonRequired] public List<MaintenanceRule> rules;
        }
        private sealed class MaintenanceRule
        {
            [JsonRequired] public int kind, host, source, page, threshold, priority;
            [JsonRequired] public string role;
            public int? materials, grade;
        }
        private sealed class PendingFilter
        {
            internal GameItem Host, Filter;
            internal GameSlotInventory Seat;
            internal GameInventory Parent;
            internal string Run;
            internal int Slot;
        }
        private sealed class WaterObservation
        {
            internal GameItem Host, Filter, Source;
            internal int AmountBefore;
        }

        private void InitMaintenance()
        {
            batteryEnabled = settings.Toggle("f31.enabled", "f31.tip");
            filterEnabled = settings.Toggle("f35.enabled", "f35.tip");
            settings.Action("f31.open", () => OpenMaintenance(31), () => Availability("f31.open"));
            settings.Action("f35.open", () => OpenMaintenance(35), () => Availability("f35.open"));
        }

        internal static bool CanOpenMaintenanceFor(GameItem host, int kind) => current?.started == true && ShopInventory.InStore &&
            ShopInventory.Owned(host) && (kind == 31 ? current.batteryEnabled.Value && BatteryHost(host) :
                kind == 35 && current.filterEnabled.Value && IsFilterHost(host));
        internal static void OpenMaintenanceFor(GameItem host, int kind)
        {
            if (!CanOpenMaintenanceFor(host, kind)) return;
            if (!current.MaintenanceHosts(kind).Contains(host)) { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            current.OpenMaintenance(kind, host);
        }

        private void StartMaintenance() => UpdateMaintenanceHooks();
        private void MaintenanceSettingsChanged()
        {
            if (maintenanceWindow != null && ((maintenanceKind == 31 && !batteryEnabled.Value) || (maintenanceKind == 35 && !filterEnabled.Value))) CloseMaintenance();
            if (!filterEnabled.Value) { pendingFilters.Clear(); machinesDirty = true; }
            UpdateMaintenanceHooks();
            AttentionBook.Changed();
        }

        private void TickMaintenance()
        {
            if (maintenanceWindow != null && (!maintenanceWindow.IsAlive || !SameMaintenanceSession())) CloseMaintenance();
            if (!filterEnabled.Value) { pendingFilters.Clear(); return; }
            if (pendingFilters.Count == 0 || !ShopInput.Ready() || UIDragHandler.isDraggingAnyUI ||
                ItemMouseDragHandler.current?.IsDraggingItem == true || DialogUIManager.Instance?.currentDialog != null ||
                DialoguePhoneUIManager.Instance?.currentDialog != null || StoreUIManager.Instance?.isAboutToStartMainDialog == true) return;
            foreach (PendingFilter pending in pendingFilters.Values.ToArray())
            {
                pendingFilters.Remove(pending.Seat);
                try { ReplaceFilter(pending); }
                catch (Exception error) { log("F35 replacement failed: " + error); RememberMaintenanceFailure(pending.Host, 35, "f35.inspect", true); }
            }
        }

        private void UpdateMaintenanceHooks()
        {
            if (!started) return;
            if (!batteryEnabled.Value && batteryHarmony != null) { batteryHarmony.UnpatchAll(batteryHarmony.Id); batteryHarmony = null; batteryCycles.Clear(); batteryHookFailed = false; }
            if (!filterEnabled.Value && filterHarmony != null) { filterHarmony.UnpatchAll(filterHarmony.Id); filterHarmony = null; filterHookFailed = false; }
            if (!batteryEnabled.Value) batteryHookFailed = false;
            if (!filterEnabled.Value) filterHookFailed = false;
            if (batteryEnabled.Value && batteryHarmony == null && !batteryHookFailed)
            {
                try
                {
                    batteryHarmony = new Harmony("mod." + ModEntry.Id + ".f31");
                    AddBatteryCycle(AccessTools.Method(typeof(MachineHydroponic), nameof(MachineHydroponic.Hydroponic)), "<Hydroponic>b__2",
                        AccessTools.Method(typeof(MachineHelper), nameof(MachineHelper.TryDrawCyclePower)));
                    AddBatteryCycle(AccessTools.Method(typeof(MachinePrinter), nameof(MachinePrinter.Printer)), "<Printer>b__4",
                        AccessTools.Method(typeof(PowerHelper), nameof(PowerHelper.DrawPowerSource)));
                }
                catch (Exception error)
                {
                    batteryHarmony?.UnpatchAll(batteryHarmony.Id); batteryHarmony = null; batteryCycles.Clear(); batteryHookFailed = true;
                    log("F31 is unavailable for this game build: " + error.Message);
                }
            }
            if (filterEnabled.Value && filterHarmony == null && !filterHookFailed)
            {
                try
                {
                    filterHarmony = new Harmony("mod." + ModEntry.Id + ".f35");
                    MethodInfo postfix = AccessTools.Method(typeof(MachineTools), nameof(AfterWaterTransfer));
                    MethodInfo afterFill = AccessTools.Method(typeof(MachineTools), nameof(AfterWaterAdded));
                    filterHarmony.Patch(AccessTools.Method(typeof(WaterHelper), nameof(WaterHelper.TransferLiquid)),
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(BeforeWaterTransfer))), postfix: new HarmonyMethod(postfix));
                    filterHarmony.Patch(AccessTools.Method(typeof(WaterHelper), nameof(WaterHelper.AddWater)),
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(BeforeWaterAdded))), postfix: new HarmonyMethod(afterFill));
                    filterHarmony.Patch(AccessTools.Method(typeof(AquacultureHelper), "RunFilter", new[] { typeof(GameItem), typeof(GameItem) }),
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(BeforeTankFilter))),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(AfterTankFilter))));
                }
                catch (Exception error)
                {
                    filterHarmony?.UnpatchAll(filterHarmony.Id); filterHarmony = null; filterHookFailed = true;
                    log("F35 is unavailable for this game build: " + error.Message);
                }
            }
        }

        private void AddBatteryCycle(MethodInfo factory, string callback, MethodInfo drawPower)
        {
            // Resolve the exact callback actually installed by the known native factory.
            // Compiler-generated closure types are not addressable as C# types.
            byte[] il = factory?.GetMethodBody()?.GetILAsByteArray();
            if (il == null) throw new MissingMethodException("Unsupported native factory for " + callback);
            var found = new List<MethodInfo>();
            for (int i = 0; i + 5 < il.Length; i++)
            {
                if (il[i] != 0xfe || il[i + 1] != 0x06) continue; // ldftn
                try
                {
                    if (!(factory.Module.ResolveMethod(BitConverter.ToInt32(il, i + 2)) is MethodInfo method) ||
                        method.Name != callback || method.DeclaringType?.DeclaringType != factory.DeclaringType) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 3 && parameters[0].ParameterType == typeof(GameItem) &&
                        parameters[1].ParameterType == typeof(GameInventory) && parameters[2].ParameterType == typeof(SlotMarker)) found.Add(method);
                }
                catch (Exception) { }
            }
            MethodInfo cycle = found.Distinct().SingleOrDefault();
            if (!Calls(cycle, AccessTools.Method(typeof(MachineHelper), nameof(MachineHelper.CanPower), new[] { typeof(GameItem), typeof(GameSlotInventory) })) ||
                !Calls(cycle, drawPower)) throw new MissingMethodException(factory.DeclaringType.Name, callback);
            batteryHarmony.Patch(cycle, prefix: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(BeforeBatteryCycle))));
            batteryCycles[factory.DeclaringType == typeof(MachineHydroponic) ? "hydroponic" : "printer"] = cycle;
        }

        private static bool Calls(MethodInfo method, MethodInfo expected)
        {
            if (method == null || expected == null) return false;
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6f) continue;
                try { if (method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) == expected) return true; }
                catch (Exception) { }
            }
            return false;
        }

        private static void BeforeBatteryCycle(GameItem __0, MethodBase __originalMethod)
        {
            var tools = current;
            if (tools == null || !tools.started || !tools.batteryEnabled.Value || !CurrentConverterHelper.isNightCycle) return;
            if (__0 != null && __0.uniqueId > 0 && AttentionBook.HasInspection("f31.open", __0.uniqueId)) return;
            ActionAvailability capability = tools.BatteryCapability(__0, __originalMethod);
            if (!capability.Allowed) { tools.RememberMaintenanceFailure(__0, 31, capability.ReasonKey); return; }
            try { tools.ReplaceBattery(__0); }
            catch (Exception error) { tools.log("F31 replacement failed: " + error); tools.RememberMaintenanceFailure(__0, 31, "f31.inspect", true); }
        }

        private static void BeforeWaterTransfer(GameItem __0, GameItem __1, out WaterObservation __state)
        {
            __state = ObserveWater(__1, __0);
        }
        private static void BeforeWaterAdded(GameItem __0, out WaterObservation __state)
        {
            __state = ObserveWater(__0, null);
        }
        private static WaterObservation ObserveWater(GameItem host, GameItem source)
        {
            var tools = current;
            if (tools == null || !tools.started || !tools.filterEnabled.Value || host?.identifier != "water_pitcher" ||
                !ShopInventory.InStore || !ShopInventory.Owned(host)) return null;
            try
            {
                var filter = (host.contentWindow?.childElement as GameSlotInventory)?.childItem;
                if (!EligibleFilter(filter)) return null;
                ActionAvailability capability = tools.FilterCapability(host, filter, source == null ? FilterPath.Added : FilterPath.Transfer);
                if (!capability.Allowed) { tools.RememberMaintenanceFailure(host, 35, capability.ReasonKey, filter: filter); return null; }
                return new WaterObservation { Host = host, Filter = filter, Source = source,
                    AmountBefore = WaterHelper.GetCurrentCapacityML(source ?? host) };
            }
            catch (Exception error) { tools.log("F35 could not observe water processing: " + error.Message); return null; }
        }
        private static void AfterWaterTransfer(WaterObservation __state)
        {
            if (__state == null) return;
            try { if (WaterHelper.GetCurrentCapacityML(__state.Source) < __state.AmountBefore) QueueFilter(__state); }
            catch (Exception error) { current?.log("F35 could not verify water transfer: " + error.Message); }
        }
        private static void AfterWaterAdded(WaterObservation __state)
        {
            if (__state == null) return;
            try { if (WaterHelper.GetCurrentCapacityML(__state.Host) > __state.AmountBefore) QueueFilter(__state); }
            catch (Exception error) { current?.log("F35 could not verify added water: " + error.Message); }
        }
        private static void QueueFilter(WaterObservation observation)
        {
            GameItem host = observation.Host;
            var tools = current;
            if (tools == null || !tools.started || !tools.filterEnabled.Value || host?.identifier != "water_pitcher" ||
                !ShopInventory.InStore || !ShopInventory.Owned(host)) return;
            try
            {
                var slot = host.contentWindow?.childElement as GameSlotInventory;
                GameItem filter = slot?.childItem;
                if (filter == observation.Filter) tools.QueueFilter(host, slot);
            }
            catch (Exception error) { tools.log("F35 could not observe the processed filter: " + error.Message); }
        }

        private void ReplaceBattery(GameItem host)
        {
            if (!BatteryHost(host) || !ShopInventory.InStore || !ShopInventory.Owned(host) || host.uniqueId <= 0 ||
                StoreStation.GetDayCounter() < 1 || ItemMouseDragHandler.current?.IsDraggingItem == true || UIDragHandler.isDraggingAnyUI) return;
            if (AttentionBook.HasInspection("f31.open", host.uniqueId)) return;
            if (!ReadMaintenance(out MaintenanceData data)) return;
            MaintenanceRule rule = data.rules.SingleOrDefault(r => r.kind == 31 && r.host == host.uniqueId);
            if (rule == null || batteryAttemptDay.TryGetValue(host.uniqueId, out int doneDay) && doneDay == StoreStation.GetDayCounter()) return;
            var power = MachineHelper.GetBatterySlot(host);
            GameItem old = power?.childItem;
            if (!OrdinaryBattery(old) || ItemPinHelper.IsPinned(old)) return;
            int energy = old.GetTagReadonly("power_source_item_energy").GetInt();
            int capacity = old.GetTagReadonly("power_source_item_max_energy").GetInt();
            if (energy < 0 || capacity <= 0 || (long)energy * 100 >= (long)capacity * rule.threshold) return;
            if (!WillRunCycle(host)) return;
            batteryAttemptDay[host.uniqueId] = StoreStation.GetDayCounter();
            GameGridInventory source = ResolveMaintenanceSource(rule);
            int required = MachineryHelper.GetMachinePowerUsage(host);
            if (source == null || required < 0) { RememberMaintenanceFailure(host, 31, "maintenance.source_missing"); return; }
            IEnumerable<GameItem> candidates = Ordered(source);
            if (rule.priority == 1) candidates = candidates.OrderByDescending(candidate => candidate.GetTagReadonly("power_source_item_energy").GetInt());
            var eligible = candidates.Where(candidate => candidate.identifier == old.identifier && OrdinaryBattery(candidate) &&
                PowerHelper.CanDrawPowerSource(candidate, required) && candidate.GetTagReadonly("power_source_item_energy").GetInt() > energy).ToList();
            GameItem spare = eligible.FirstOrDefault(candidate => MaintenanceSwap.Ready(host, power, source, old, candidate));
            if (spare == null)
            {
                string reason = eligible.Count == 0 ? "f31.missing" : eligible.Any(candidate =>
                    MaintenanceSwap.CallbackCapability(power, source, old, candidate).Allowed) ? "maintenance.swap_unavailable" : "maintenance.conflict";
                RememberMaintenanceFailure(host, 31, reason); return;
            }
            int oldEnergy = old.GetTagReadonly("power_source_item_energy").GetInt();
            int newEnergy = spare.GetTagReadonly("power_source_item_energy").GetInt();
            if (!MaintenanceSwap.Try(host, power, source, old, spare) || old.GetTagReadonly("power_source_item_energy").GetInt() != oldEnergy ||
                spare.GetTagReadonly("power_source_item_energy").GetInt() != newEnergy)
            { log("F31 exchange did not complete as expected; inspect both batteries and the swap buffer."); RememberMaintenanceFailure(host, 31, "f31.inspect", true); return; }
            ClearMaintenanceFailure(31, host.uniqueId);
            log("F31 replaced the registered " + host.identifier + " battery before its night cycle.");
        }

        private static bool BatteryHost(GameItem host) => host != null && !host.IsTag("BROKEN_MACHINE_TAG") &&
            (host.identifier == "hydroponic" || (host.identifier == "printer" && host.IsTag("PRINTER_TAG")));

        private static bool WillRunCycle(GameItem host)
        {
            string state = MachineProgressHelper.GetMachineState(host);
            if (state != MachineProgressHelper.STATE_READY && state != MachineProgressHelper.STATE_WORKING) return false;
            if (host.identifier == "printer")
            {
                var chip = MachinePanel<GameSlotInventory>(host, "chip")?.childItem;
                var input = MachinePanel<GameGridInventory>(host, "input");
                return chip != null && (state == MachineProgressHelper.STATE_WORKING || (input != null &&
                    MachineProgressHelper.CanStart(chip, host) && MachineProgressHelper.HasRequiredAdditionalItems(chip, input.childItems)));
            }
            var seed = MachineHydroponic.GetInputSlot(host)?.childItem;
            GameItem water = MachineHydroponic.GetWaterSlotItem(host);
            return seed != null && water != null && WaterHelper.GetCurrentCapacityML(water) >= HydroponicHelper.GetWaterUsePerCycle(seed);
        }

        private static bool OrdinaryBattery(GameItem item) => item != null && item.IsTag("power_source_item") &&
            !item.IsTag("CURRENT_CONVERTER_TAG") && !item.IsTag("HYPERTECH_BATTERY_TAG") &&
            !item.IsTag("BREEDER_POWER_SOURCE_ITEM_TAG") && !item.IsTag("power_source_item_recharge");

        private void ReplaceFilter(PendingFilter pending)
        {
            if (!ShopInventory.InStore || pending.Run != PlayerStore.Instance.runID || pending.Slot != PlayerStore.Instance.saveSlotId ||
                !IsFilterHost(pending.Host) || !ShopInventory.Owned(pending.Host) || pending.Host.uniqueId <= 0 ||
                pending.Host.parentInventory != pending.Parent || !FilterSlots(pending.Host).Contains(pending.Seat) ||
                pending.Seat.childItem != pending.Filter || !EligibleFilter(pending.Filter) || AquaOwns(pending.Host, pending.Filter.identifier) ||
                DurabilityHelper.GetRealDurability(pending.Filter) != 0 || ItemPinHelper.IsPinned(pending.Filter)) return;
            if (AttentionBook.HasInspection("f35.open", pending.Host.uniqueId)) return;
            GameSlotInventory slot = pending.Seat;
            if (!ReadMaintenance(out MaintenanceData data)) return;
            MaintenanceRule rule = data.rules.SingleOrDefault(r => r.kind == 35 && r.host == pending.Host.uniqueId);
            if (rule == null || !AllowsFilter(rule, pending.Filter)) return;
            GameGridInventory source = ResolveMaintenanceSource(rule);
            if (source == null) { RememberMaintenanceFailure(pending.Host, 35, "maintenance.source_missing"); return; }
            var eligible = Ordered(source).Where(candidate => CompatibleFilter(rule, pending.Filter, candidate))
                .OrderByDescending(candidate => candidate.identifier == pending.Filter.identifier).ToList();
            GameItem spare = eligible.FirstOrDefault(candidate => MaintenanceSwap.Ready(pending.Host, slot, source, pending.Filter, candidate, true));
            if (spare == null)
            {
                string reason = eligible.Count == 0 ? "f35.missing" : eligible.Any(candidate =>
                    MaintenanceSwap.CallbackCapability(slot, source, pending.Filter, candidate).Allowed) ? "maintenance.swap_unavailable" : "maintenance.conflict";
                RememberMaintenanceFailure(pending.Host, 35, reason); return;
            }
            int oldDurability = DurabilityHelper.GetRealDurability(pending.Filter);
            int newDurability = DurabilityHelper.GetRealDurability(spare);
            if (!MaintenanceSwap.Try(pending.Host, slot, source, pending.Filter, spare, true) ||
                DurabilityHelper.GetRealDurability(pending.Filter) != oldDurability || DurabilityHelper.GetRealDurability(spare) != newDurability)
            { machinesDirty = true;
                log("F35 exchange did not complete as expected; inspect both filters and the swap buffer."); RememberMaintenanceFailure(pending.Host, 35, "f35.inspect", true); return; }
            machinesDirty = true;
            ClearMaintenanceFailure(35, pending.Host.uniqueId);
            log("F35 replaced " + pending.Filter.identifier + " after processing in " + pending.Host.identifier + ".");
        }

        private IEnumerable<MachineRow> MaintenanceFilterRows(List<ShopInventory.Location> locations, MaintenanceData data)
        {
            if (!filterEnabled.Value || data == null) yield break;
            var rules = data.rules.Where(r => r.kind == 35).ToDictionary(r => r.host);
            List<ShopInventory.Location> sources = null;
            foreach (ShopInventory.Location location in locations.Where(l => !l.IsMachine))
            {
                foreach (GameItem host in location.Inventory.childItems)
                {
                    if (!IsFilterHost(host) || !ShopInventory.Owned(host) ||
                        !rules.TryGetValue(host.uniqueId, out MaintenanceRule rule)) continue;
                    var slots = FilterSlots(host);
                    for (int index = 0; index < slots.Count; index++)
                    {
                        GameItem filter = slots[index].childItem;
                        if (!AllowsFilter(rule, filter) || DurabilityHelper.GetRealDurability(filter) != 0 || AquaOwns(host, filter.identifier)) continue;
                        if (sources == null) sources = ShopInventory.OwnedLocations(false);
                        GameGridInventory source = ResolveMaintenanceSource(rule, sources);
                        var candidates = source == null ? new List<GameItem>() : source.childItems.Where(i => CompatibleFilter(rule, filter, i)).ToList();
                        bool available = candidates.Any(i => MaintenanceSwap.Ready(host, slots[index], source, filter, i, true));
                        ActionAvailability capability = FilterCapability(host, filter, FilterPath.Any);
                        string reason = !capability.Allowed ? capability.ReasonKey : available ? "f35.pending" : source == null ? "maintenance.source_missing" : candidates.Count == 0 ? "f35.missing" :
                            candidates.Any(i => MaintenanceSwap.CallbackCapability(slots[index], source, filter, i).Allowed) ? "maintenance.swap_unavailable" : "maintenance.conflict";
                        yield return new MachineRow { Item = host, Path = location.Name, NeedsAttention = true,
                            Identity = "filter:" + index, ReasonKey = reason,
                            Status = (slots.Count > 1 ? "#" + (index + 1) + " · " : "") + ShopSettings.Text(reason) };
                    }
                }
            }
        }

        private static IEnumerable<GameItem> Ordered(GameGridInventory inventory) => inventory.childItems.Where(i => i?.modifiedShape != null)
            .OrderBy(i => i.modifiedShape.minY).ThenBy(i => i.modifiedShape.minX);
        private void OpenMaintenance(int kind, GameItem host = null)
        {
            if (!ShopInventory.InStore || CustomUIManager.Instance == null || (kind == 31 ? !batteryEnabled.Value : !filterEnabled.Value) ||
                !ReadMaintenance(out _)) return;
            if (host != null && !MaintenanceHosts(kind).Contains(host)) { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            CloseMaintenance(); maintenanceKind = kind; maintenanceHost = host;
            maintenanceRun = PlayerStore.Instance.runID; maintenanceSlot = PlayerStore.Instance.saveSlotId;
            RenderMaintenance();
        }

        private bool SameMaintenanceSession() => started && ShopInventory.InStore &&
            PlayerStore.Instance.runID == maintenanceRun && PlayerStore.Instance.saveSlotId == maintenanceSlot &&
            (maintenanceKind == 31 ? batteryEnabled.Value : maintenanceKind == 35 && filterEnabled.Value);

        private List<GameItem> MaintenanceHosts(int kind)
        {
            if (WindowsHandler.current?.visibleWindows == null) return new List<GameItem>();
            var result = new List<GameItem>();
            foreach (PixelWindow window in WindowsHandler.current.visibleWindows)
            {
                if (window == null || !window.IsVisible() || window.isLocked || window.parentItems == null || window.parentItems.Count != 1) continue;
                GameItem item = window.parentItems[0];
                if (item == null || item.contentWindow != window || !ShopInventory.Owned(item) ||
                    !(kind == 31 ? BatteryHost(item) : IsFilterHost(item)) || result.Contains(item)) continue;
                result.Add(item);
            }
            return result;
        }

        private static bool IsMaintenanceSource(ShopInventory.Location location, int kind) => location?.Owner != null &&
            location.Inventory is GameGridInventory &&
            ((location.Ordinary && !location.IsMachine && ShopInventory.SafeContainerForPeek(location.Owner)) ||
             (kind == 31 && location.Owner.identifier == "recharger" && location.Role == "batteries" && ShopInventory.Owned(location.Owner))) &&
            location.Page >= -1 && location.Page <= 4;

        private void RenderMaintenance()
        {
            if (!SameMaintenanceSession() || !ReadMaintenance(out MaintenanceData data)) { CloseMaintenance(); return; }
            var hosts = MaintenanceHosts(maintenanceKind);
            if (maintenanceHost != null && !hosts.Contains(maintenanceHost))
            { CloseMaintenance(); ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            if (maintenanceHost == null) maintenanceHost = hosts.OrderByDescending(h => h.contentWindow.focusStamp).FirstOrDefault();
            var sources = ShopInventory.OwnedLocations(false).Where(l => IsMaintenanceSource(l, maintenanceKind) && l.Owner != maintenanceHost).Take(100).ToList();
            var saved = maintenanceHost == null ? null : data.rules.Find(r => r.kind == maintenanceKind && r.host == maintenanceHost.uniqueId);
            if (maintenanceSource == null || !sources.Any(l => l.Inventory == maintenanceSource.Inventory))
            {
                maintenanceSource = saved == null ? sources.FirstOrDefault() :
                    sources.FirstOrDefault(l => l.Owner.uniqueId == saved.source && l.Role == saved.role && l.Page == saved.page);
                maintenanceThreshold = saved?.threshold ?? 20;
                maintenancePriority = saved?.priority ?? 0;
                maintenanceMaterials = saved?.materials ?? 7;
                maintenanceGrade = saved?.grade ?? 0;
            }
            else maintenanceSource = sources.First(l => l.Inventory == maintenanceSource.Inventory);
            int selectedHost = maintenanceHost == null ? -1 : hosts.IndexOf(maintenanceHost);
            int selectedSource = maintenanceSource == null ? -1 : sources.IndexOf(maintenanceSource);
            float windowHeight = 410f;
            if (maintenanceHost != null)
            {
                if (maintenanceHost.uniqueId > 0 && AttentionBook.HasInspection(maintenanceKind == 31 ? "f31.open" : "f35.open", maintenanceHost.uniqueId)) windowHeight += 36f;
                bool aqua = maintenanceKind == 35 && maintenanceHost.identifier == "water_pitcher" &&
                    (AquaOwns(maintenanceHost, "water_filter") || AquaOwns(maintenanceHost, "kotton_fabric"));
                if (aqua) windowHeight += 44f;
                GameItem currentFilter = maintenanceKind == 35 ? FilterSlots(maintenanceHost).FirstOrDefault()?.childItem : null;
                ActionAvailability currentCapability = maintenanceKind == 31 ? BatteryCapability(maintenanceHost) : FilterCapability(maintenanceHost, currentFilter, FilterPath.Any);
                if (!currentCapability.Allowed && currentCapability.ReasonKey != "f35.aqua_taken") windowHeight += 44f;
                else if (maintenanceKind == 35 && maintenanceHost.identifier == "water_pitcher" && currentCapability.Allowed)
                {
                    if (!FilterCapability(maintenanceHost, currentFilter, FilterPath.Transfer).Allowed) windowHeight += 44f;
                    if (!FilterCapability(maintenanceHost, currentFilter, FilterPath.Added).Allowed) windowHeight += 44f;
                }
            }
            float scrollPosition = maintenanceScroll?.GetComponentInParent<ScrollRect>()?.verticalNormalizedPosition ?? 1f;
            if (maintenanceWindow == null || !maintenanceWindow.IsAlive)
            {
                var shell = CustomUIManager.Instance.CreateWindow(ModEntry.Id + ".maintenance", ShopSettings.Text(maintenanceKind == 31 ? "f31.open" : "f35.open"), CustomUIManager.LAYER_META)
                    .SetDraggable(true).SetCloseOnEscape();
                maintenanceWindow = shell.GetWindow();
                CustomUIWindow opened = maintenanceWindow;
                shell.OnClose(() =>
                {
                    if (ReferenceEquals(maintenanceWindow, opened))
                    { maintenanceWindow = null; maintenanceScroll = null; maintenanceRun = null; maintenanceRevision++; }
                }).Center().Show();
            }
            CustomUIWindow own = maintenanceWindow;
            int revision = ++maintenanceRevision;
            bool CurrentView() => ReferenceEquals(maintenanceWindow, own) && own.IsAlive &&
                maintenanceRevision == revision && SameMaintenanceSession();
            Vector2 position = own.Rect.anchoredPosition;
            UiLayout.Clear(own.ContentRoot, own);
            var builder = new CustomUIBuilder(CustomUIManager.Instance, own, own.ContentRoot);
            Vector2 size = UiLayout.Fit(620f, Mathf.Min(windowHeight, 560f));
            builder.SetSize(size.x, size.y);
            own.Rect.anchoredPosition = position;
            builder.BeginScrollFlexible(120);
            maintenanceScroll = builder.CurrentContainer;
            if (maintenanceKind == 35 && maintenanceHost?.identifier == "water_pitcher" &&
                (AquaOwns(maintenanceHost, "water_filter") || AquaOwns(maintenanceHost, "kotton_fabric")))
                builder.AddLabel(ShopSettings.Text("f35.aqua"));
            if (maintenanceHost != null)
            {
                if (maintenanceHost.uniqueId > 0 && AttentionBook.HasInspection(maintenanceKind == 31 ? "f31.open" : "f35.open", maintenanceHost.uniqueId))
                    builder.AddWarning(ShopSettings.Text(maintenanceKind == 31 ? "f31.inspect" : "f35.inspect"));
                GameItem filter = maintenanceKind == 35 ? FilterSlots(maintenanceHost).FirstOrDefault()?.childItem : null;
                ActionAvailability capability = maintenanceKind == 31 ? BatteryCapability(maintenanceHost) : FilterCapability(maintenanceHost, filter, FilterPath.Any);
                if (!capability.Allowed && capability.ReasonKey != "f35.aqua_taken") builder.AddWarning(capability.Message);
                else if (maintenanceKind == 35 && maintenanceHost.identifier == "water_pitcher" && capability.Allowed)
                {
                    ActionAvailability transfer = FilterCapability(maintenanceHost, filter, FilterPath.Transfer);
                    ActionAvailability added = FilterCapability(maintenanceHost, filter, FilterPath.Added);
                    if (!transfer.Allowed) builder.AddWarning(ShopSettings.Text(transfer.Code == ActionUnavailableReason.Unsupported ? "f35.transfer_unsupported" : "f35.transfer_conflict"));
                    if (!added.Allowed) builder.AddWarning(ShopSettings.Text(added.Code == ActionUnavailableReason.Unsupported ? "f35.added_unsupported" : "f35.added_conflict"));
                }
            }
            if (hosts.Count == 0)
            {
                var available = ShopInventory.MachineLocations().Where(l => !l.IsMachine).SelectMany(l => l.Inventory.childItems)
                    .Where(i => ShopInventory.Owned(i) && (maintenanceKind == 31 ? BatteryHost(i) : IsFilterHost(i))).Distinct().ToList();
                ItemGrid.Add(builder, available.Select(i => new ItemGrid.Entry(i)), new ItemGrid.Options
                {
                    Cards = true, Columns = 4, CardWidth = 135, CardHeight = 100,
                    OnClick = entry =>
                    {
                        if (!CurrentView() || !ShopInventory.Owned(entry.Item) ||
                            !(maintenanceKind == 31 ? BatteryHost(entry.Item) : IsFilterHost(entry.Item)))
                        { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                        ItemMouseDoubleClickHandler.current?.OpenContentAction(entry.Item, Input.mousePosition);
                        if (!CurrentView() || !MaintenanceHosts(maintenanceKind).Contains(entry.Item))
                        { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                        maintenanceHost = entry.Item; maintenanceSource = null; RebuildMaintenance();
                    }
                });
                if (available.Count == 0) builder.AddLabel(ShopSettings.Text("maintenance.no_host"));
                builder.End();
                CustomUIBuilder.RebuildLayout(own.ContentRoot);
                UiLayout.RestoreScroll(maintenanceScroll, scrollPosition);
                return;
            }
            builder.BeginRow();
            VisualChoices.Location(builder, ShopSettings.Text("visual.source"), maintenanceSource, sources, source =>
            {
                if (!CurrentView() || !ShopInventory.TryRefresh(source, out ShopInventory.Location refreshed) ||
                    !IsMaintenanceSource(refreshed, maintenanceKind) || refreshed.Owner == maintenanceHost)
                { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                maintenanceSource = refreshed; RebuildMaintenance();
            }, "source");
            builder.AddLabel("→");
            VisualChoices.Slot(builder, ShopSettings.Text("visual.machine"), maintenanceHost, maintenanceHost.GetDisplayName(true),
                () =>
                {
                    if (!CurrentView()) return;
                    VisualChoices.ChooseItem(ShopSettings.Text("visual.machine"), hosts, maintenanceHost, host =>
                    {
                        if (!CurrentView() || !MaintenanceHosts(maintenanceKind).Contains(host))
                        { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                        maintenanceHost = host; maintenanceSource = null; RebuildMaintenance();
                    });
                }, "host");
            GameItem installed = maintenanceKind == 31 ? MachineHelper.GetBatterySlot(maintenanceHost)?.childItem :
                FilterSlots(maintenanceHost).FirstOrDefault()?.childItem;
            VisualChoices.Slot(builder, ShopSettings.Text("visual.installed"), installed, installed?.GetDisplayName(true), null, "installed");
            builder.End();
            if (sources.Count == 0) builder.AddLabel(ShopSettings.Text("maintenance.no_source"));
            if (maintenanceKind == 31)
            {
                builder.AddLabel(ShopSettings.Text("f31.threshold") + " " + maintenanceThreshold + "%", "threshold_label");
                builder.AddSlider(0, 100, maintenanceThreshold, value =>
                {
                    if (!CurrentView()) return;
                    maintenanceThreshold = Mathf.Clamp(Mathf.RoundToInt(value), 0, 100);
                    maintenanceWindow?.Get("threshold_label")?.SetText(ShopSettings.Text("f31.threshold") + " " + maintenanceThreshold + "%");
                }, true, "threshold");
                builder.AddDropdown(new[] { ShopSettings.Text("f31.position"), ShopSettings.Text("f31.charge") }, maintenancePriority,
                    value => { if (CurrentView()) maintenancePriority = value == 1 ? 1 : 0; }, "priority");
            }
            if (maintenanceKind == 35)
            {
                builder.BeginRow();
                foreach (string id in FilterKinds)
                {
                    int bit = FilterBit(id);
                    builder.AddToggle(LocHelper.GetLocalizedItem("item_" + id + "_name"), (maintenanceMaterials & bit) != 0,
                        on =>
                        {
                            if (!CurrentView()) return;
                            if (on) maintenanceMaterials |= bit; else maintenanceMaterials &= ~bit;
                            maintenanceWindow?.Get("enable")?.SetInteractable(CanSaveMaintenance());
                        }, "allow_" + id);
                }
                builder.End().AddDropdown(new[] { ShopSettings.Text("f35.same"), ShopSettings.Text("f35.equivalent"), ShopSettings.Text("f35.any") },
                    maintenanceGrade, index => { if (CurrentView()) maintenanceGrade = Mathf.Clamp(index, 0, 2); }, "grade");
            }
            builder.BeginRow();
            builder.AddButton(ShopSettings.Text("maintenance.enable"), () => { if (CurrentView()) SaveMaintenance(); }, "enable");
            builder.Last.SetInteractable(CanSaveMaintenance());
            builder.AddButton(ShopSettings.Text("maintenance.disable"), () => { if (CurrentView()) RemoveMaintenance(); }, "disable");
            builder.Last.SetInteractable(saved != null);
            builder.End().End();
            CustomUIBuilder.RebuildLayout(own.ContentRoot);
            UiLayout.RestoreScroll(maintenanceScroll, scrollPosition);
        }

        private bool CanSaveMaintenance() => maintenanceHost != null && maintenanceSource != null &&
            (maintenanceKind != 35 || maintenanceMaterials != 0);

        private void SaveMaintenance()
        {
            if (!SameMaintenanceSession() || maintenanceHost == null || !MaintenanceHosts(maintenanceKind).Contains(maintenanceHost) ||
                maintenanceSource == null || !ShopInventory.TryRefresh(maintenanceSource, out ShopInventory.Location source) ||
                !IsMaintenanceSource(source, maintenanceKind) || source.Owner == maintenanceHost || !ReadMaintenance(out MaintenanceData data)) return;
            int hostId = maintenanceHost.GetUniqueID(), sourceId = source.Owner.GetUniqueID();
            if (hostId <= 0 || sourceId <= 0) return;
            data.rules.RemoveAll(r => r.kind == maintenanceKind && r.host == hostId);
            if (data.rules.Count >= 128) return;
            data.rules.Add(new MaintenanceRule { kind = maintenanceKind, host = hostId, source = sourceId, role = source.Role,
                page = source.Page, threshold = maintenanceKind == 31 ? Mathf.Clamp(maintenanceThreshold, 0, 100) : 0,
                priority = maintenanceKind == 31 && maintenancePriority == 1 ? 1 : 0,
                materials = maintenanceKind == 35 ? maintenanceMaterials : (int?)null,
                grade = maintenanceKind == 35 ? maintenanceGrade : (int?)null });
            try
            {
                ModHelper.SetModData(ModEntry.Id, MaintenanceKey, ShopData.Encode(data));
                GameItem filter = maintenanceKind == 35 ? FilterSlots(maintenanceHost).FirstOrDefault()?.childItem : null;
                if (filter != null && (maintenanceMaterials & FilterBit(filter.identifier)) == 0) filter = null;
                ActionAvailability capability = maintenanceKind == 31 ? BatteryCapability(maintenanceHost) : FilterCapability(maintenanceHost, filter, FilterPath.Any);
                bool partial = capability.Allowed && maintenanceKind == 35 && maintenanceHost.identifier == "water_pitcher" &&
                    (!FilterCapability(maintenanceHost, filter, FilterPath.Transfer).Allowed || !FilterCapability(maintenanceHost, filter, FilterPath.Added).Allowed);
                CloseMaintenance(); AttentionBook.Changed(); machinesDirty = true;
                ShopNotice.Show(ShopSettings.Text(!capability.Allowed ? "maintenance.saved_inactive" : partial ? "maintenance.saved_partial" : "maintenance.saved"));
            }
            catch (Exception error) { log("Maintenance settings could not be saved: " + error); ShopNotice.Show(ShopSettings.Text("maintenance.failed")); }
        }

        private void RemoveMaintenance()
        {
            if (!SameMaintenanceSession() || maintenanceHost == null || !MaintenanceHosts(maintenanceKind).Contains(maintenanceHost) || maintenanceHost.uniqueId <= 0 || !ReadMaintenance(out MaintenanceData data)) return;
            data.rules.RemoveAll(r => r.kind == maintenanceKind && r.host == maintenanceHost.uniqueId);
            try { ModHelper.SetModData(ModEntry.Id, MaintenanceKey, ShopData.Encode(data)); ClearMaintenanceFailure(maintenanceKind, maintenanceHost.uniqueId);
                CloseMaintenance(); AttentionBook.Changed(); machinesDirty = true; ShopNotice.Show(ShopSettings.Text("maintenance.removed")); }
            catch (Exception error) { log("Maintenance settings could not be removed: " + error); ShopNotice.Show(ShopSettings.Text("maintenance.failed")); }
        }

        private bool ReadMaintenance(out MaintenanceData data)
        {
            data = null;
            if (!ShopInventory.InStore || !Guid.TryParse(PlayerStore.Instance.runID, out Guid run) || run == Guid.Empty || PlayerStore.Instance.saveSlotId < 0) return false;
            try
            {
                string raw = ModHelper.GetModData(ModEntry.Id, MaintenanceKey, null);
                if (raw == null)
                {
                    data = new MaintenanceData { version = 2, run = run.ToString("N"), slot = PlayerStore.Instance.saveSlotId, rules = new List<MaintenanceRule>() };
                    maintenanceReadError = null; return true;
                }
                data = ShopData.Decode<MaintenanceData>(raw, 65536);
                if (data == null || (data.version != 1 && data.version != 2) || !Guid.TryParseExact(data.run, "N", out Guid stored) || stored != run ||
                    data.slot != PlayerStore.Instance.saveSlotId || data.rules == null || data.rules.Count > 128 ||
                    data.rules.Any(r => r == null || (r.kind != 31 && r.kind != 35) || r.host <= 0 || r.source <= 0 ||
                        (r.role != "contents" && r.role != "storage" && (r.kind != 31 || r.role != "batteries")) ||
                        r.page < -1 || r.page > 4 || r.threshold < 0 || r.threshold > 100 || r.priority < 0 || r.priority > 1 ||
                        (r.kind == 35 && (r.threshold != 0 || r.priority != 0))) ||
                    data.rules.GroupBy(r => r.kind + ":" + r.host).Any(g => g.Count() != 1)) throw new FormatException("Invalid maintenance data");
                foreach (var rule in data.rules)
                {
                    if (data.version == 1 && rule.kind == 35) { rule.materials = 1; rule.grade = 0; }
                    if (rule.kind == 35 && (!rule.materials.HasValue || rule.materials < 0 || rule.materials > 7 ||
                        !rule.grade.HasValue || rule.grade < 0 || rule.grade > 2) ||
                        rule.kind == 31 && (rule.materials.HasValue || rule.grade.HasValue)) throw new FormatException("Invalid filter preferences");
                }
                data.version = 2;
                maintenanceReadError = null;
                return true;
            }
            catch (Exception error)
            {
                data = null;
                if (maintenanceReadError != error.Message) log("Maintenance settings were preserved: " + error.Message);
                maintenanceReadError = error.Message; return false;
            }
        }

        private GameGridInventory ResolveMaintenanceSource(MaintenanceRule rule, List<ShopInventory.Location> locations = null)
        {
            locations = locations ?? ShopInventory.OwnedLocations(false);
            var matches = locations.Where(l => IsMaintenanceSource(l, rule.kind) && l.Owner.uniqueId == rule.source && l.Role == rule.role && l.Page == rule.page).ToArray();
            if (matches.Length == 1) return matches[0].Inventory as GameGridInventory;
            if (matches.Length != 0 || rule.kind != 31 || rule.role != "batteries" || rule.page != -1) return null;
            var owners = locations.Where(l => !l.IsMachine).SelectMany(l => l.Inventory.childItems)
                .Where(i => i != null && i.uniqueId == rule.source && i.identifier == "recharger" && ShopInventory.Owned(i)).Distinct().ToArray();
            if (owners.Length != 1 || owners[0].contentWindow == null || owners[0].contentWindow.isLocked ||
                !(owners[0].contentWindow.childElement is MachineryPanelRow row)) return null;
            MachineryPanel panel = row.GetPanel("batteries");
            if (panel == null || panel.isHidden || panel.content == null) return null;
            var nodes = MachineryContainerUtils.Collect(panel.content);
            return nodes.Count == 1 ? nodes[0] as GameGridInventory : null;
        }

        private void RebuildMaintenance() { if (maintenanceWindow?.IsAlive == true) RenderMaintenance(); }
        private void CloseMaintenance()
        {
            CustomUIWindow window = maintenanceWindow;
            maintenanceWindow = null; maintenanceScroll = null; maintenanceRun = null; maintenanceRevision++;
            maintenanceHost = null; maintenanceSource = null;
            window?.Close();
        }
        private void ResetMaintenance() { CloseMaintenance(); pendingFilters.Clear(); batteryAttemptDay.Clear(); maintenanceProblems.Clear(); maintenanceReadError = null; AttentionBook.Changed(); }
        private void StopMaintenance()
        {
            ResetMaintenance();
            batteryHarmony?.UnpatchAll(batteryHarmony.Id); batteryHarmony = null; batteryCycles.Clear();
            filterHarmony?.UnpatchAll(filterHarmony.Id); filterHarmony = null;
        }
    }
}
