using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private readonly List<GameObject> consumableSourceMarkers = new List<GameObject>();
        private readonly Dictionary<GameInventory, int> consumableSourceNumbers = new Dictionary<GameInventory, int>();
        private readonly System.Random batteryPriorityRandom = new System.Random();

        private IEnumerable<GameItem> BatteryCandidates(MaintenanceRule rule, GameItem installed, int required, GameGridInventory source) =>
            Ordered(source).Where(item => EligibleBattery(item, installed, required, rule.condition ?? 2))
                .OrderByDescending(item => BatteryGain(rule, installed, item)).ThenBy(item => item.uniqueId);

        private static double BatteryGain(MaintenanceRule rule, GameItem installed, GameItem candidate) =>
            ConsumableSelectionRules.BatteryGain(rule.condition ?? 2, installed.GetTagReadonly("power_source_item_energy").GetInt(),
                installed.GetTagReadonly("power_source_item_max_energy").GetInt(), candidate.GetTagReadonly("power_source_item_energy").GetInt(),
                candidate.GetTagReadonly("power_source_item_max_energy").GetInt());

        private void ProcessBatteryMaintenance()
        {
            if (!batteryEnabled.Value || !ShopInventory.InStore || !ReadMaintenance(out MaintenanceData data)) return;
            var owned = UniqueOwnedItems();
            var queue = new List<(GameItem host, int energy, int required)>();
            foreach (MaintenanceRule rule in data.rules.Where(value => value.kind == 31))
            {
                if (!owned.TryGetValue(rule.host, out GameItem host) || !BatteryHost(host) || !MachineAccessible(host) ||
                    !BatteryCapability(host).Allowed || AttentionBook.HasInspection("f31.open", host.uniqueId)) continue;
                GameSlotInventory seat = MachineHelper.GetBatterySlot(host);
                GameItem installed = seat?.childItem;
                int required = MachineryHelper.GetMachinePowerUsage(host);
                if (!OrdinaryBattery(installed) || required <= 0) continue;
                int energy = installed.GetTagReadonly("power_source_item_energy").GetInt();
                int capacity = installed.GetTagReadonly("power_source_item_max_energy").GetInt();
                if (!ConsumableSelectionRules.BatteryStateValid(energy, capacity, required)) continue;
                var source = ResolveMaintenanceSource(rule);
                GameItem candidate = source == null ? null : BatteryCandidates(rule, installed, required, source)
                    .FirstOrDefault(item => MaintenanceSwap.Ready(host, seat, source, installed, item, allowOtherBattery: true));
                bool cannotRun = !PowerHelper.CanDrawPowerSource(installed, required);
                if (candidate != null || cannotRun || source == null)
                    queue.Add((host, energy, required));
            }
            // The source is re-read for each exchange. Lower-ranked devices see the actual
            // remainder, including safely returned parts, and never take installed batteries.
            foreach (var entry in ConsumableSelectionRules.OrderBatteryMachines(queue, value => value.energy,
                value => value.required, batteryPriorityRandom))
            {
                try { ReplaceBattery(entry.host); }
                catch (Exception error) { log("Battery maintenance failed: " + error); RememberMaintenanceFailure(entry.host, 31, "f31.inspect", true); }
            }
        }

        private static bool FilterDue(MaintenanceRule rule, GameItem filter) => filter != null &&
            ConsumableSelectionRules.FilterDue(rule.condition ?? 0, DurabilityHelper.GetRealDurability(filter));

        private void QueueDueFilters()
        {
            if (!filterEnabled.Value || !ShopInventory.InStore || !ReadMaintenance(out MaintenanceData data)) return;
            var owned = UniqueOwnedItems();
            foreach (MaintenanceRule rule in data.rules.Where(value => value.kind == 35))
            {
                if (!owned.TryGetValue(rule.host, out GameItem host) || !IsFilterHost(host) || !MachineAccessible(host) ||
                    !FilterCapability(host, FilterPath.Any).Allowed || AttentionBook.HasInspection("f35.open", host.uniqueId)) continue;
                foreach (GameSlotInventory seat in FilterSlots(host))
                    if (AllowsFilter(rule, seat.childItem) && FilterDue(rule, seat.childItem)) QueueFilter(host, seat);
            }
        }

        private double FilterReplacementGain(PendingFilter pending)
        {
            if (!ReadMaintenance(out MaintenanceData data)) return 0;
            MaintenanceRule rule = data.rules.Find(value => value.kind == 35 && value.host == pending.Host.uniqueId);
            GameGridInventory source = rule == null ? null : ResolveMaintenanceSource(rule);
            return source == null ? 0 : source.childItems.Where(item => CompatibleFilter(rule, pending.Filter, item) &&
                    MaintenanceSwap.Ready(pending.Host, pending.Seat, source, pending.Filter, item, true))
                .Select(item => (double)DurabilityHelper.GetRealDurability(item) - DurabilityHelper.GetRealDurability(pending.Filter))
                .DefaultIfEmpty(0).Max();
        }

        private static List<ShopInventory.Location> ConsumableSources(int kind, GameItem host) => ShopInventory.OwnedLocations(false)
            .Where(location => location.Owner != host && IsMaintenanceSource(location, kind) && InventoryRoutes.AutoTakeAllowed(location.Inventory) &&
                location.Inventory.childItems.Any(item => ShopInventory.Owned(item) && (kind == 31 ? OrdinaryBattery(item) :
                    kind == 32 ? item.IsTag("SEED_ITEM_TAG") && HydroponicHelper.GetPlant(item) != null : EligibleFilter(item))))
            .OrderBy(location => location.Key() ?? location.Name, StringComparer.Ordinal).ToList();

        private void ChooseConsumableSource(string title, int kind, GameItem host, ShopInventory.Location selected,
            Action<ShopInventory.Location> changed, CustomUIWindow owner)
        {
            ClearConsumableSourceMarkers(); consumableSourceNumbers.Clear();
            List<ShopInventory.Location> Choices()
            {
                var sources = ConsumableSources(kind, host);
                foreach (var source in sources)
                {
                    if (!consumableSourceNumbers.TryGetValue(source.Inventory, out int number))
                        consumableSourceNumbers[source.Inventory] = number = consumableSourceNumbers.Count + 1;
                    source.Name = "#" + number + " · " + source.Name;
                }
                ShowConsumableSourceMarkers(sources);
                return sources;
            }
            VisualChoices.ChooseLocation(title, () => owner?.IsAlive == true ? Choices() : new List<ShopInventory.Location>(), selected,
                location => { ClearConsumableSourceMarkers(); changed(location); }, owner: owner);
        }

        private void ShowConsumableSourceMarkers(List<ShopInventory.Location> sources)
        {
            ClearConsumableSourceMarkers();
            foreach (var group in sources.Where(source => ConsumableSourceWindow(source)?.IsVisible() == true)
                .GroupBy(ConsumableSourceWindow))
            {
                PixelWindow window = group.Key;
                if (window.handler?.rectTransform == null) continue;
                var font = window.handler.gameObject.GetComponentInChildren<TMP_Text>()?.font ?? TMP_Settings.defaultFontAsset;
                var marker = new GameObject("ShopAssist_SupplyNumber", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                var rect = (RectTransform)marker.transform; rect.SetParent(window.handler.rectTransform, false);
                rect.anchorMin = rect.anchorMax = new Vector2(1, 1); rect.pivot = new Vector2(1, 1);
                rect.anchoredPosition = new Vector2(-4, -4); rect.sizeDelta = new Vector2(100, 30);
                var image = marker.GetComponent<UnityEngine.UI.Image>(); image.color = new Color(.12f, .24f, .28f, .95f); image.raycastTarget = false;
                var labelObject = new GameObject("Number", typeof(RectTransform), typeof(TextMeshProUGUI));
                var labelRect = (RectTransform)labelObject.transform; labelRect.SetParent(rect, false);
                labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one; labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
                var text = labelObject.GetComponent<TextMeshProUGUI>(); text.font = font;
                text.text = string.Join(" / ", group.Select(source => "#" + consumableSourceNumbers[source.Inventory]));
                text.fontSize = 20; text.alignment = TextAlignmentOptions.Center; text.color = Color.white; text.raycastTarget = false;
                consumableSourceMarkers.Add(marker);
            }
        }

        private static PixelWindow ConsumableSourceWindow(ShopInventory.Location source) => source.Owner?.contentWindow ??
            (source.Role == "stock" ? EmporiumEntry.Instance?.invWindow : null);

        private void ClearConsumableSourceMarkers()
        {
            foreach (GameObject marker in consumableSourceMarkers) if (marker != null) { marker.SetActive(false); UnityEngine.Object.Destroy(marker); }
            consumableSourceMarkers.Clear();
        }
    }
}
