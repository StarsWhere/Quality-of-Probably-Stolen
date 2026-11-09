using System;
using System.Collections.Generic;
using System.Linq;
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
        private CustomUIWindow maintenanceCopyWindow;
        private RectTransform maintenanceScroll;
        private string maintenanceRun;
        private int maintenanceSlot, maintenanceRevision;
        private GameItem maintenanceHost;
        private GameItem maintenanceDraftHost;
        private ShopInventory.Location maintenanceSource;
        private int maintenanceKind, maintenanceCondition;
        private int maintenanceMaterials, maintenanceGrade;
        private bool maintenanceDirty;
        private float nextMaintenanceCheck;
        private Harmony filterHarmony;
        private bool filterHookFailed;
        private string maintenanceReadError;
        private readonly Dictionary<GameSlotInventory, PendingFilter> pendingFilters = new Dictionary<GameSlotInventory, PendingFilter>();

        private sealed class PendingFilter
        {
            internal GameItem Host, Filter;
            internal GameSlotInventory Seat;
            internal GameInventory Parent;
            internal string Run;
            internal int Slot;
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
            maintenanceDirty = true;
            AttentionBook.Changed();
        }

        private void TickMaintenance()
        {
            if (!VisualChoices.IsOpen) ClearConsumableSourceMarkers();
            if (maintenanceWindow != null && (!maintenanceWindow.IsAlive || !SameMaintenanceSession())) CloseMaintenance();
            else if (maintenanceWindow != null && maintenanceDirty && !UIDragHandler.isDraggingAnyUI &&
                ItemMouseDragHandler.current?.IsDraggingItem != true) RenderMaintenance();
            if (maintenanceCopyWindow != null && (!maintenanceCopyWindow.IsAlive || maintenanceWindow?.IsAlive != true || !SameMaintenanceSession()))
                CloseMaintenanceCopy();
            if (!filterEnabled.Value) pendingFilters.Clear();
            if (!ShopInput.Ready() || UIDragHandler.isDraggingAnyUI ||
                ItemMouseDragHandler.current?.IsDraggingItem == true || DialogUIManager.Instance?.currentDialog != null ||
                DialoguePhoneUIManager.Instance?.currentDialog != null || StoreUIManager.Instance?.isAboutToStartMainDialog == true) return;
            if (Time.unscaledTime >= nextMaintenanceCheck)
            {
                nextMaintenanceCheck = Time.unscaledTime + 1f;
                ProcessBatteryMaintenance();
                QueueDueFilters();
            }
            foreach (PendingFilter pending in pendingFilters.Values.OrderByDescending(value => DurabilityHelper.GetRealDurability(value.Filter) == 0)
                .ThenByDescending(FilterReplacementGain).ThenBy(value => value.Host.uniqueId)
                .ThenBy(value => FilterSlots(value.Host).IndexOf(value.Seat)).ToArray())
            {
                pendingFilters.Remove(pending.Seat);
                try { ReplaceFilter(pending); }
                catch (Exception error) { log("F35 replacement failed: " + error); RememberMaintenanceFailure(pending.Host, 35, "f35.inspect", true); }
            }
        }

        private void UpdateMaintenanceHooks()
        {
            if (!started) return;
            if (!filterEnabled.Value && filterHarmony != null) { filterHarmony.UnpatchAll(filterHarmony.Id); filterHarmony = null; filterHookFailed = false; }
            if (!filterEnabled.Value) filterHookFailed = false;
            if (filterEnabled.Value && filterHarmony == null && !filterHookFailed)
            {
                try
                {
                    filterHarmony = new Harmony("mod." + ModEntry.Id + ".f35");
                    filterHarmony.Patch(AccessTools.Method(typeof(WaterHelper), nameof(WaterHelper.TransferLiquid)),
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(BeginFilterTransfer))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(EndFilterProcessing))));
                    filterHarmony.Patch(AccessTools.Method(typeof(WaterHelper), nameof(WaterHelper.AddWater)),
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(BeginFilterFill))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(EndFilterProcessing))));
                    filterHarmony.Patch(AccessTools.Method(typeof(AquacultureHelper), nameof(AquacultureHelper.TickTank)),
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(BeginTankProcessing))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(EndFilterProcessing))));
                    filterHarmony.Patch(AccessTools.Method(typeof(WaterHelper), nameof(WaterHelper.HandleFilter)),
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(BeforeFilterProcessing))),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(MachineTools), nameof(ObserveFilterProcessing))));
                }
                catch (Exception error)
                {
                    filterHarmony?.UnpatchAll(filterHarmony.Id); filterHarmony = null; filterHookFailed = true;
                    log("F35 is unavailable for this game build: " + error.Message);
                }
            }
        }

        private void ReplaceBattery(GameItem host)
        {
            if (!BatteryHost(host) || !ShopInventory.InStore || !ShopInventory.Owned(host) || host.uniqueId <= 0 ||
                ItemMouseDragHandler.current?.IsDraggingItem == true || UIDragHandler.isDraggingAnyUI) return;
            if (AttentionBook.HasInspection("f31.open", host.uniqueId)) return;
            if (!ReadMaintenance(out MaintenanceData data)) return;
            MaintenanceRule rule = data.rules.SingleOrDefault(r => r.kind == 31 && r.host == host.uniqueId);
            if (rule == null || !BatteryCapability(host).Allowed) return;
            var power = MachineHelper.GetBatterySlot(host);
            GameItem old = power?.childItem;
            if (!OrdinaryBattery(old) || ItemPinHelper.IsPinned(old)) return;
            int energy = old.GetTagReadonly("power_source_item_energy").GetInt();
            int capacity = old.GetTagReadonly("power_source_item_max_energy").GetInt();
            if (energy < 0 || capacity <= 0) return;
            GameGridInventory source = ResolveMaintenanceSource(rule);
            int required = MachineryHelper.GetMachinePowerUsage(host);
            if (source == null || required <= 0) { RememberMaintenanceFailure(host, 31, "maintenance.source_missing"); return; }
            var eligible = BatteryCandidates(rule, old, required, source).ToList();
            if (eligible.Count == 0 && PowerHelper.CanDrawPowerSource(old, required)) { ClearMaintenanceFailure(31, host.uniqueId); return; }
            GameItem spare = eligible.FirstOrDefault(candidate => MaintenanceSwap.Ready(host, power, source, old, candidate, allowOtherBattery: true));
            if (spare == null)
            {
                string reason = eligible.Count == 0 ? "f31.missing" : eligible.Any(candidate =>
                    MaintenanceSwap.CallbackCapability(power, source, old, candidate).Allowed) ? "maintenance.swap_unavailable" : "maintenance.conflict";
                RememberMaintenanceFailure(host, 31, reason); return;
            }
            int oldEnergy = old.GetTagReadonly("power_source_item_energy").GetInt();
            int newEnergy = spare.GetTagReadonly("power_source_item_energy").GetInt();
            if (!MaintenanceSwap.Try(host, power, source, old, spare, allowOtherBattery: true) || old.GetTagReadonly("power_source_item_energy").GetInt() != oldEnergy ||
                spare.GetTagReadonly("power_source_item_energy").GetInt() != newEnergy)
            { log("F31 exchange did not complete as expected; inspect both batteries and the swap buffer."); RememberMaintenanceFailure(host, 31, "f31.inspect", true); return; }
            ClearMaintenanceFailure(31, host.uniqueId);
            log("F31 replaced the registered " + host.identifier + " battery.");
        }

        private static bool BatteryHost(GameItem host) => host != null && !host.IsTag("BROKEN_MACHINE_TAG") &&
            host.IsTag("machinery") && MachineryHelper.GetMachinePowerUsage(host) > 0 &&
            MachineHelper.GetBatterySlot(host) is GameSlotInventory slot && ReferenceEquals(slot.GetParentItem(), host) &&
            OrdinaryBattery(slot.childItem);

        private static bool OrdinaryBattery(GameItem item) => item != null && item.IsTag("power_source_item") &&
            !item.IsTag("CURRENT_CONVERTER_TAG") && !item.IsTag("HYPERTECH_BATTERY_TAG") &&
            !item.IsTag("BREEDER_POWER_SOURCE_ITEM_TAG") && !item.IsTag("power_source_item_recharge");

        private static bool EligibleBattery(GameItem candidate, GameItem installed, int required, int condition = 0) =>
            OrdinaryBattery(candidate) && OrdinaryBattery(installed) && PowerHelper.CanDrawPowerSource(candidate, required) &&
            ConsumableSelectionRules.BatteryImproves(condition, installed.GetTagReadonly("power_source_item_energy").GetInt(),
                installed.GetTagReadonly("power_source_item_max_energy").GetInt(), candidate.GetTagReadonly("power_source_item_energy").GetInt(),
                candidate.GetTagReadonly("power_source_item_max_energy").GetInt(), required);

        private void ReplaceFilter(PendingFilter pending)
        {
            if (!ShopInventory.InStore || pending.Run != PlayerStore.Instance.runID || pending.Slot != PlayerStore.Instance.saveSlotId ||
                !IsFilterHost(pending.Host) || !ShopInventory.Owned(pending.Host) || pending.Host.uniqueId <= 0 ||
                pending.Host.parentInventory != pending.Parent || !FilterSlots(pending.Host).Contains(pending.Seat) ||
                pending.Seat.childItem != pending.Filter || !EligibleFilter(pending.Filter) ||
                ItemPinHelper.IsPinned(pending.Filter)) return;
            if (AttentionBook.HasInspection("f35.open", pending.Host.uniqueId)) return;
            if (!FilterCapability(pending.Host, FilterPath.Any).Allowed) return;
            GameSlotInventory slot = pending.Seat;
            if (!ReadMaintenance(out MaintenanceData data)) return;
            MaintenanceRule rule = data.rules.SingleOrDefault(r => r.kind == 35 && r.host == pending.Host.uniqueId);
            if (rule == null || !AllowsFilter(rule, pending.Filter) || !FilterDue(rule, pending.Filter)) return;
            GameGridInventory source = ResolveMaintenanceSource(rule);
            if (source == null) { RememberMaintenanceFailure(pending.Host, 35, "maintenance.source_missing"); return; }
            int oldDurability = DurabilityHelper.GetRealDurability(pending.Filter);
            var eligible = ConsumableSelectionRules.OrderFilters(
                Ordered(source).Where(candidate => CompatibleFilter(rule, pending.Filter, candidate)),
                DurabilityHelper.GetRealDurability, candidate => candidate.identifier == pending.Filter.identifier)
                .ThenBy(candidate => candidate.uniqueId).ToList();
            if (!ConsumableSelectionRules.FilterNeedsWork(rule.condition ?? 0, oldDurability, true, eligible.Count != 0)) return;
            GameItem spare = eligible.FirstOrDefault(candidate => MaintenanceSwap.Ready(pending.Host, slot, source, pending.Filter, candidate, true));
            if (spare == null)
            {
                string reason = eligible.Count == 0 ? "f35.missing" : eligible.Any(candidate =>
                    MaintenanceSwap.CallbackCapability(slot, source, pending.Filter, candidate).Allowed) ? "maintenance.swap_unavailable" : "maintenance.conflict";
                RememberMaintenanceFailure(pending.Host, 35, reason); return;
            }
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
                        if (!AllowsFilter(rule, filter) || !FilterDue(rule, filter)) continue;
                        if (sources == null) sources = ShopInventory.OwnedLocations(false);
                        GameGridInventory source = ResolveMaintenanceSource(rule, sources);
                        var candidates = source == null ? new List<GameItem>() : source.childItems.Where(i => CompatibleFilter(rule, filter, i)).ToList();
                        if (!ConsumableSelectionRules.FilterNeedsWork(rule.condition ?? 0, DurabilityHelper.GetRealDurability(filter),
                            source != null, candidates.Count != 0)) continue;
                        bool available = candidates.Any(i => MaintenanceSwap.Ready(host, slots[index], source, filter, i, true));
                        ActionAvailability capability = FilterCapability(host, FilterPath.Any);
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

        private static bool IsMaintenanceSource(ShopInventory.Location location, int kind) => location != null &&
            location.Inventory is GameGridInventory && location.Page >= -1 && location.Page <= 4 &&
            (location.Owner == null ? location.Ordinary && location.Role == "stock" &&
                ReferenceEquals(location.Inventory, EmporiumEntry.Instance?.invElement) :
             (location.Ordinary && !location.IsMachine && ShopInventory.SafeContainerForPeek(location.Owner)) ||
             (kind == 31 && location.Owner.identifier == "recharger" && location.Role == "batteries" && ShopInventory.Owned(location.Owner)));

        private void RenderMaintenance()
        {
            if (!SameMaintenanceSession() || !ReadMaintenance(out MaintenanceData data)) { CloseMaintenance(); return; }
            maintenanceDirty = false;
            var hosts = MaintenanceHosts(maintenanceKind);
            if (maintenanceHost != null && !hosts.Contains(maintenanceHost))
            { CloseMaintenance(); ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            if (maintenanceHost == null) maintenanceHost = hosts.OrderByDescending(h => h.contentWindow.focusStamp).FirstOrDefault();
            var sources = MaintenanceSources();
            var saved = maintenanceHost == null ? null : data.rules.Find(r => r.kind == maintenanceKind && r.host == maintenanceHost.uniqueId);
            if (!ReferenceEquals(maintenanceDraftHost, maintenanceHost))
            {
                maintenanceDraftHost = maintenanceHost;
                maintenanceSource = saved == null ? null : sources.FirstOrDefault(l => l.Key() == saved.location);
                maintenanceCondition = saved?.condition ?? (maintenanceKind == 31 ? 2 : 0);
                maintenanceMaterials = saved?.materials ?? 7;
                maintenanceGrade = saved?.grade ?? 0;
            }
            else if (maintenanceSource != null) maintenanceSource = sources.FirstOrDefault(l => l.Inventory == maintenanceSource.Inventory);
            int selectedHost = maintenanceHost == null ? -1 : hosts.IndexOf(maintenanceHost);
            int selectedSource = maintenanceSource == null ? -1 : sources.IndexOf(maintenanceSource);
            float windowHeight = 410f;
            if (maintenanceHost != null)
            {
                if (maintenanceHost.uniqueId > 0 && AttentionBook.HasInspection(maintenanceKind == 31 ? "f31.open" : "f35.open", maintenanceHost.uniqueId)) windowHeight += 36f;
                ActionAvailability currentCapability = maintenanceKind == 31 ? BatteryCapability(maintenanceHost) : FilterCapability(maintenanceHost, FilterPath.Any);
                if (!currentCapability.Allowed) windowHeight += 44f;
                else if (maintenanceKind == 35 && maintenanceHost.identifier == "water_pitcher" && currentCapability.Allowed)
                {
                    if (!FilterCapability(maintenanceHost, FilterPath.Transfer).Allowed) windowHeight += 44f;
                    if (!FilterCapability(maintenanceHost, FilterPath.Added).Allowed) windowHeight += 44f;
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
            if (maintenanceHost != null)
            {
                if (maintenanceHost.uniqueId > 0 && AttentionBook.HasInspection(maintenanceKind == 31 ? "f31.open" : "f35.open", maintenanceHost.uniqueId))
                    builder.AddWarning(ShopSettings.Text(maintenanceKind == 31 ? "f31.inspect" : "f35.inspect"));
                ActionAvailability capability = maintenanceKind == 31 ? BatteryCapability(maintenanceHost) : FilterCapability(maintenanceHost, FilterPath.Any);
                if (!capability.Allowed) builder.AddWarning(capability.Message);
                else if (maintenanceKind == 35 && maintenanceHost.identifier == "water_pitcher" && capability.Allowed)
                {
                    ActionAvailability transfer = FilterCapability(maintenanceHost, FilterPath.Transfer);
                    ActionAvailability added = FilterCapability(maintenanceHost, FilterPath.Added);
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
            VisualChoices.Slot(builder, ShopSettings.Text("visual.source"), maintenanceSource?.Owner, maintenanceSource?.Name, () =>
            {
                if (!CurrentView()) return;
                ChooseConsumableSource(ShopSettings.Text("visual.source"), maintenanceKind, maintenanceHost, maintenanceSource, source =>
                {
                    if (!CurrentView() || !ShopInventory.TryRefresh(source, out ShopInventory.Location refreshed) ||
                        !IsMaintenanceSource(refreshed, maintenanceKind) || refreshed.Owner == maintenanceHost ||
                        !InventoryRoutes.AutoTakeAllowed(refreshed.Inventory))
                    { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                    maintenanceSource = refreshed; RebuildMaintenance();
                }, own);
            }, "source");
            builder.AddLabel("→");
            VisualChoices.Slot(builder, ShopSettings.Text("visual.machine"), maintenanceHost, maintenanceHost.GetDisplayName(true),
                () =>
                {
                    if (!CurrentView()) return;
                    VisualChoices.ChooseItem(ShopSettings.Text("visual.machine"), () => SameMaintenanceSession() ? MaintenanceHosts(maintenanceKind) : new List<GameItem>(), maintenanceHost, host =>
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
            if (maintenanceSource?.Inventory is GameGridInventory spareGrid)
            {
                var counts = MaintenanceSpareCounts(maintenanceHost, spareGrid);
                builder.AddLabel(counts.known ? ShopSettings.Format("maintenance.spare_count", counts.matching, counts.ready) :
                    ShopSettings.Text("maintenance.spare_unknown"));
            }
            if (maintenanceKind == 31)
            {
                builder.AddDropdown(new[] { ShopSettings.Text("f31.best_energy"), ShopSettings.Text("f31.best_percentage"),
                    ShopSettings.Text("f31.cannot_run") }, maintenanceCondition,
                    value => { if (CurrentView()) { maintenanceCondition = Mathf.Clamp(value, 0, 2); maintenanceDirty = true; } }, "condition");
            }
            if (maintenanceKind == 35)
            {
                builder.AddDropdown(new[] { ShopSettings.Text("f35.exhausted"), ShopSettings.Text("f35.best_remaining") }, maintenanceCondition,
                    value => { if (CurrentView()) { maintenanceCondition = Mathf.Clamp(value, 0, 1); maintenanceDirty = true; } }, "condition");
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
                            maintenanceDirty = true;
                        }, "allow_" + id);
                }
                builder.End().AddDropdown(new[] { ShopSettings.Text("f35.same"), ShopSettings.Text("f35.equivalent"), ShopSettings.Text("f35.any") },
                    maintenanceGrade, index => { if (CurrentView()) { maintenanceGrade = Mathf.Clamp(index, 0, 2); maintenanceDirty = true; } }, "grade");
            }
            builder.BeginRow();
            builder.AddButton(ShopSettings.Text("maintenance.enable"), () => { if (CurrentView()) SaveMaintenance(); }, "enable");
            builder.Last.SetInteractable(CanSaveMaintenance());
            builder.AddButton(ShopSettings.Text("maintenance.disable"), () => { if (CurrentView()) RemoveMaintenance(); }, "disable");
            builder.Last.SetInteractable(saved != null);
            if (saved != null)
                builder.AddButton(ShopSettings.Text("maintenance.copy"), () => { if (CurrentView()) OpenMaintenanceCopy(); }, "copy");
            builder.End().End();
            CustomUIBuilder.RebuildLayout(own.ContentRoot);
            UiLayout.RestoreScroll(maintenanceScroll, scrollPosition);
        }

        private bool CanSaveMaintenance() => maintenanceHost != null && maintenanceSource != null &&
            (maintenanceKind != 35 || maintenanceMaterials != 0);

        private List<ShopInventory.Location> MaintenanceSources() => ConsumableSources(maintenanceKind, maintenanceHost);

        private (bool known, int matching, int ready) MaintenanceSpareCounts(GameItem host, GameGridInventory source)
        {
            if (host == null || source == null) return (false, 0, 0);
            var matches = new HashSet<GameItem>();
            var ready = new HashSet<GameItem>();
            if (maintenanceKind == 31)
            {
                GameSlotInventory seat = MachineHelper.GetBatterySlot(host);
                GameItem old = seat?.childItem;
                if (!OrdinaryBattery(old)) return (false, 0, 0);
                int required = MachineryHelper.GetMachinePowerUsage(host);
                bool capable = BatteryCapability(host).Allowed;
                foreach (GameItem candidate in Ordered(source).Where(item => EligibleBattery(item, old, required, maintenanceCondition)))
                {
                    matches.Add(candidate);
                    if (capable && MaintenanceSwap.Ready(host, seat, source, old, candidate, allowOtherBattery: true)) ready.Add(candidate);
                }
            }
            else
            {
                var preview = new MaintenanceRule { kind = 35, materials = maintenanceMaterials, grade = maintenanceGrade };
                var seats = FilterSlots(host).Where(seat => AllowsFilter(preview, seat?.childItem)).ToList();
                if (seats.Count == 0) return (false, 0, 0);
                foreach (GameSlotInventory seat in seats)
                {
                    GameItem old = seat.childItem;
                    bool capable = FilterCapability(host, FilterPath.Any).Allowed;
                    foreach (GameItem candidate in Ordered(source).Where(item => CompatibleFilter(preview, old, item)))
                    {
                        matches.Add(candidate);
                        if (capable && MaintenanceSwap.Ready(host, seat, source, old, candidate, true)) ready.Add(candidate);
                    }
                }
            }
            return (true, matches.Count, ready.Count);
        }

        private void OpenMaintenanceCopy()
        {
            if (!SameMaintenanceSession() || maintenanceWindow?.IsAlive != true || maintenanceHost == null ||
                maintenanceHost.uniqueId <= 0 || !ReadMaintenance(out MaintenanceData data)) return;
            GameItem origin = maintenanceHost;
            MaintenanceRule saved = data.rules.Find(rule => rule.kind == maintenanceKind && rule.host == origin.uniqueId);
            if (saved == null) return;
            string snapshot = ShopData.Encode(saved);
            int kind = maintenanceKind; string run = maintenanceRun; int slot = maintenanceSlot;
            var locations = ShopInventory.MachineLocations();
            var targets = locations.Where(location => !location.IsMachine).SelectMany(location => location.Inventory.childItems)
                .Where(item => item != null && item != origin && ShopInventory.Owned(item) &&
                    (kind == 31 ? BatteryHost(item) : IsFilterHost(item))).Distinct().ToList();
            if (targets.Count == 0) { ShopNotice.Show(ShopSettings.Text("maintenance.no_targets")); return; }
            var selected = new HashSet<GameItem>();
            var builder = UiLayout.Rebuild(ref maintenanceCopyWindow, ModEntry.Id + ".maintenance.copy",
                ShopSettings.Text("maintenance.copy"), 580, 470);
            CustomUIWindow own = maintenanceCopyWindow;
            builder.OnClose(() => { if (ReferenceEquals(maintenanceCopyWindow, own)) maintenanceCopyWindow = null; });
            builder.BeginScrollFlexible(120);
            ItemGrid.Add(builder, targets.Select(item => new ItemGrid.Entry(item)
            {
                Caption = item.GetDisplayName(true),
                Badge = data.rules.Any(rule => rule.kind == kind && rule.host == item.uniqueId && item.uniqueId > 0)
                    ? ShopSettings.Text("maintenance.configured") : null
            }), new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 130, CardHeight = 100,
                OnClick = entry => { if (!selected.Add(entry.Item)) selected.Remove(entry.Item); entry.Selected = selected.Contains(entry.Item); } });
            builder.End().AddButton(ShopSettings.Text("maintenance.copy_apply"), () =>
            {
                if (!ReferenceEquals(maintenanceCopyWindow, own) || !own.IsAlive) return;
                CopyMaintenanceRules(origin, kind, run, slot, snapshot, selected);
            }, "copy_apply").Show();
        }

        private void CopyMaintenanceRules(GameItem origin, int kind, string run, int slot, string snapshot, HashSet<GameItem> selected)
        {
            if (!SameMaintenanceSession() || maintenanceRun != run || maintenanceSlot != slot ||
                !ReferenceEquals(maintenanceHost, origin) || !ShopInventory.Owned(origin) || selected == null || selected.Count == 0 ||
                !ReadMaintenance(out MaintenanceData data)) return;
            MaintenanceRule saved = data.rules.Find(rule => rule.kind == kind && rule.host == origin.uniqueId);
            if (saved == null || ShopData.Encode(saved) != snapshot || ResolveMaintenanceSource(saved) == null)
            { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            List<ShopInventory.Location> locations = ShopInventory.MachineLocations();
            int copied = 0, skipped = 0;
            foreach (GameItem target in selected)
            {
                if (target == null || !ShopInventory.Owned(target) || !MachineAccessible(target, locations) ||
                    !(kind == 31 ? BatteryHost(target) : IsFilterHost(target))) { skipped++; continue; }
                int id = target.GetUniqueID();
                if (id <= 0 || !ReferenceEquals(FindUniqueOwnedItem(id), target) || data.rules.Count >= 128 ||
                    data.rules.Any(rule => rule.kind == kind && rule.host == id) ||
                    AttentionBook.HasInspection(kind == 31 ? "f31.open" : "f35.open", id))
                { skipped++; continue; }
                data.rules.Add(new MaintenanceRule { kind = kind, host = id, location = saved.location,
                    threshold = saved.threshold, condition = saved.condition, materials = saved.materials, grade = saved.grade });
                copied++;
            }
            if (copied != 0 && !SaveMaintenanceData(data)) return;
            CloseMaintenanceCopy(); machinesDirty = true; AttentionBook.Changed();
            ShopNotice.Show(ShopSettings.Format("maintenance.copy_result", copied, skipped));
        }

        private void CloseMaintenanceCopy()
        {
            CustomUIWindow previous = maintenanceCopyWindow;
            maintenanceCopyWindow = null;
            previous?.Close();
        }

        private void SaveMaintenance()
        {
            if (!SameMaintenanceSession() || maintenanceHost == null || !MaintenanceHosts(maintenanceKind).Contains(maintenanceHost) ||
                maintenanceSource == null || !ShopInventory.TryRefresh(maintenanceSource, out ShopInventory.Location source) ||
                !IsMaintenanceSource(source, maintenanceKind) || source.Owner == maintenanceHost || !ReadMaintenance(out MaintenanceData data)) return;
            int hostId = maintenanceHost.GetUniqueID();
            string sourceKey = source.Key(true);
            if (hostId <= 0 || !ReferenceEquals(FindUniqueOwnedItem(hostId), maintenanceHost) ||
                !ParseMaintenanceSource(sourceKey, maintenanceKind, out _, out _, out _) ||
                MaintenanceSources().Count(location => location.Key() == sourceKey) != 1)
            { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            data.rules.RemoveAll(r => r.kind == maintenanceKind && r.host == hostId);
            if (data.rules.Count >= 128) return;
            data.rules.Add(new MaintenanceRule { kind = maintenanceKind, host = hostId, location = sourceKey,
                threshold = 0,
                condition = maintenanceCondition,
                materials = maintenanceKind == 35 ? maintenanceMaterials : (int?)null,
                grade = maintenanceKind == 35 ? maintenanceGrade : (int?)null });
            try
            {
                if (!SaveMaintenanceData(data)) return;
                ActionAvailability capability = maintenanceKind == 31 ? BatteryCapability(maintenanceHost) : FilterCapability(maintenanceHost, FilterPath.Any);
                bool partial = capability.Allowed && maintenanceKind == 35 && maintenanceHost.identifier == "water_pitcher" &&
                    (!FilterCapability(maintenanceHost, FilterPath.Transfer).Allowed || !FilterCapability(maintenanceHost, FilterPath.Added).Allowed);
                CloseMaintenance(); AttentionBook.Changed(); machinesDirty = true;
                ShopNotice.Show(ShopSettings.Text(!capability.Allowed ? "maintenance.saved_inactive" : partial ? "maintenance.saved_partial" : "maintenance.saved"));
            }
            catch (Exception error) { log("Maintenance settings could not be saved: " + error); ShopNotice.Show(ShopSettings.Text("maintenance.failed")); }
        }

        private void RemoveMaintenance()
        {
            if (!SameMaintenanceSession() || maintenanceHost == null || !MaintenanceHosts(maintenanceKind).Contains(maintenanceHost) || maintenanceHost.uniqueId <= 0 || !ReadMaintenance(out MaintenanceData data)) return;
            data.rules.RemoveAll(r => r.kind == maintenanceKind && r.host == maintenanceHost.uniqueId);
            try { if (!SaveMaintenanceData(data)) return; ClearMaintenanceFailure(maintenanceKind, maintenanceHost.uniqueId);
                CloseMaintenance(); AttentionBook.Changed(); machinesDirty = true; ShopNotice.Show(ShopSettings.Text("maintenance.removed")); }
            catch (Exception error) { log("Maintenance settings could not be removed: " + error); ShopNotice.Show(ShopSettings.Text("maintenance.failed")); }
        }

        private bool ReadMaintenance(out MaintenanceData data) =>
            ReadMaintenanceState(out data, out _) == MachineRuleReadStatus.Valid;

        private MachineRuleReadStatus ReadMaintenanceState(out MaintenanceData data, out string raw)
        {
            data = null; raw = null;
            if (!ShopInventory.InStore || !Guid.TryParse(PlayerStore.Instance.runID, out Guid run) || run == Guid.Empty || PlayerStore.Instance.saveSlotId < 0)
                return MachineRuleReadStatus.Unavailable;
            if (HasResetAttempt(MaintenanceKey, PlayerStore.Instance.runID, PlayerStore.Instance.saveSlotId))
                return MachineRuleReadStatus.ResetPending;
            try
            {
                MachineRuleRecovery.ReadPending(ruleResetStore, MaintenanceKey, PlayerStore.Instance.runID,
                    PlayerStore.Instance.saveSlotId, true, out MachineRuleResetJournal pending);
                if (pending != null) return MachineRuleReadStatus.ResetPending;
                raw = ruleResetStore.Read(MaintenanceKey);
            }
            catch (Exception error)
            {
                LogMaintenanceReadFailure(error);
                return MachineRuleReadStatus.Unavailable;
            }
            try { data = DecodeMaintenance(raw, run, PlayerStore.Instance.saveSlotId); maintenanceReadError = null; return MachineRuleReadStatus.Valid; }
            catch (FormatException error) { LogMaintenanceReadFailure(error); return MachineRuleReadStatus.Invalid; }
            catch (JsonException error) { LogMaintenanceReadFailure(error); return MachineRuleReadStatus.Invalid; }
            catch (Exception error) { LogMaintenanceReadFailure(error); return MachineRuleReadStatus.Unavailable; }
        }

        private void LogMaintenanceReadFailure(Exception error)
        {
            if (maintenanceReadError != error.Message) log("Maintenance settings were preserved: " + error);
            maintenanceReadError = error.Message;
        }

        private bool SaveMaintenanceData(MaintenanceData data)
        {
            try
            {
                if (!ShopInventory.InStore || data == null || !Guid.TryParse(PlayerStore.Instance.runID, out Guid run) ||
                    data.run != run.ToString("N") || data.slot != PlayerStore.Instance.saveSlotId)
                    throw new InvalidOperationException("Maintenance save identity changed.");
                data.version = MaintenanceVersion;
                string raw = ShopData.Encode(data);
                if (raw.Length > 65536) throw new InvalidOperationException("Maintenance data exceeds its size limit.");
                ModHelper.SetModData(ModEntry.Id, MaintenanceKey, raw);
                if (ModHelper.GetModData(ModEntry.Id, MaintenanceKey, null) != raw)
                    throw new InvalidOperationException("Maintenance update was not retained.");
                return true;
            }
            catch (Exception error) { log("Maintenance settings could not be saved: " + error); ShopNotice.Show(ShopSettings.Text("maintenance.failed")); return false; }
        }

        private GameGridInventory ResolveMaintenanceSource(MaintenanceRule rule, List<ShopInventory.Location> locations = null)
        {
            locations = locations ?? ShopInventory.OwnedLocations(false);
            if (!ParseMaintenanceSource(rule.location, rule.kind, out int sourceId, out string role, out int page)) return null;
            var matches = locations.Where(l => IsMaintenanceSource(l, rule.kind) && l.Key() == rule.location).ToArray();
            if (matches.Length == 1) return InventoryRoutes.AutoTakeAllowed(matches[0].Inventory) ? matches[0].Inventory as GameGridInventory : null;
            if (matches.Length != 0 || rule.kind != 31 || role != "batteries" || page != -1) return null;
            var owners = locations.Where(l => !l.IsMachine).SelectMany(l => l.Inventory.childItems)
                .Where(i => i != null && i.uniqueId == sourceId && i.identifier == "recharger" && ShopInventory.Owned(i)).Distinct().ToArray();
            if (owners.Length != 1 || owners[0].contentWindow == null || owners[0].contentWindow.isLocked ||
                !(owners[0].contentWindow.childElement is MachineryPanelRow row)) return null;
            MachineryPanel panel = row.GetPanel("batteries");
            if (panel == null || panel.isHidden || panel.content == null) return null;
            var nodes = MachineryContainerUtils.Collect(panel.content);
            return nodes.Count == 1 && nodes[0] is GameGridInventory grid && InventoryRoutes.AutoTakeAllowed(grid) ? grid : null;
        }

        private void RebuildMaintenance() { if (maintenanceWindow?.IsAlive == true) RenderMaintenance(); }
        private void CloseMaintenance()
        {
            ClearConsumableSourceMarkers();
            CloseMaintenanceCopy();
            CustomUIWindow window = maintenanceWindow;
            maintenanceWindow = null; maintenanceScroll = null; maintenanceRun = null; maintenanceRevision++;
            maintenanceHost = null; maintenanceDraftHost = null; maintenanceSource = null; maintenanceDirty = false;
            window?.Close();
        }
        private void ResetMaintenance() { CloseMaintenance(); ClearConsumableSourceMarkers(); consumableSourceNumbers.Clear(); pendingFilters.Clear(); maintenanceProblems.Clear(); maintenanceReadError = null; AttentionBook.Changed(); }
        private void StopMaintenance()
        {
            ResetMaintenance();
            filterHarmony?.UnpatchAll(filterHarmony.Id); filterHarmony = null;
        }
    }
}
