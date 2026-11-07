using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ShopAssist
{
    internal sealed class WaterGather : IDisposable
    {
        private readonly ShopSettings settings;
        private readonly ConfigEntry<bool> enabled, crossGrade;
        private readonly Func<KeyCode> key;
        private readonly HashSet<GameItem> chosen = new HashSet<GameItem>();
        private GameItem target, pendingFilter, pendingFilterHost;
        private CustomUIWindow window;

        private sealed class Fluid
        {
            internal GameItem Item;
            internal GameInventory From;
            internal int Grade, Volume;
        }
        internal WaterGather(ShopSettings settings)
        {
            this.settings = settings;
            enabled = settings.Toggle("f26.enabled", "f26.tip");
            crossGrade = settings.Toggle("f26.mix", "f26.mix.tip");
            key = settings.Key("f26.key", "f26.key.tip");
            settings.Action("f26.open", Draw, () => enabled.Value && ShopInventory.InStore);
        }
        internal void Start()
        {
            settings.Changed += Changed; ModHook.OnGameLoadedInit += Close; ModHook.OnLeavingStoreEarly += Close;
            InventoryContextActions.RegisterItem("f26.open", i => ShopSettings.Text("f26.open"), AppliesTo, AvailabilityFor, OpenFor);
        }
        private static bool AppliesTo(GameItem item) => ShopInventory.InStore && ShopInventory.Owned(item) && VesselKind(item);
        private ActionAvailability AvailabilityFor(GameItem item)
        {
            if (!enabled.Value) return ActionAvailability.Blocked(ActionUnavailableReason.Disabled, "availability.disabled", "f26.enabled");
            if (!AppliesTo(item)) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.object");
            if (NeedsInspection(item)) return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.water_inspect");
            if (RequestBook.Reserved(item)) return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.water_reserved");
            if (!ShopInventory.Movable(item)) return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.item_movable");
            if (!WineLiquidAllowed(item)) return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.water_unavailable");
            if (Grade(item) == -1) return ActionAvailability.Blocked(ActionUnavailableReason.MissingResource, "availability.water_test");
            if (WaterHelper.GetFreeCapacity(item) <= 0) return ActionAvailability.Blocked(ActionUnavailableReason.MissingResource, "availability.water_full");
            return VisibleVessels().Contains(item) ? ActionAvailability.Ready :
                ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.water_location");
        }
        private void OpenFor(GameItem item)
        {
            if (!enabled.Value || !ShopInventory.InStore || !Vessel(item) || NeedsInspection(item) || Grade(item) == -1 || WaterHelper.GetFreeCapacity(item) <= 0) return;
            var all = VisibleVessels();
            if (!all.Contains(item)) return;
            target = item; chosen.Clear(); DrawWith(all);
        }
        private void Changed() { if (!enabled.Value) Close(); }
        private static int Grade(GameItem item)
        {
            if (WaterHelper.GetCurrentCapacityUL(item) == 0) return -2;
            var publicQuality = item.FindItemFeatureByCategory("CATEGORY_WATER_PURITY");
            if (publicQuality != null && !publicQuality.isDisabled && (publicQuality.IsExposed() || publicQuality.IsFeatureDiscovered()))
                return PublicWaterGrade(WaterHelper.GetWaterPurity(item));
            // Wine bottles use a transient native purity feature. Their retained
            // scan text is public; require it to match the current reading before
            // using the grade, so stale or untested contents stay unknown.
            if (item.identifier != "wine_bottle" || !item.IsTag(WaterHelper.LAST_SCAN_RESULT_TAG)) return -1;
            int purity = WaterHelper.GetWaterPurity(item);
            string shown = string.Format(CultureInfo.CurrentCulture, "[{0}]: {1:F2}% {2}",
                LocHelper.GetLocalizedMechanic("mech_last_scan"), purity / 100.0, WaterFeatureHelper.GetWaterDisplayName(purity));
            return item.GetTagReadonly(WaterHelper.LAST_SCAN_RESULT_TAG).GetString() == shown ?
                PublicWaterGrade(purity) : -1;
        }
        private static int PublicWaterGrade(int purity) => purity >= WaterFeatureHelper.GUTTERFLOW_WATER_PURITY ?
            WaterFeatureHelper.GetPurityArrayIndex(purity) : -1;
        private static bool WineLiquidAllowed(GameItem item) => item != null &&
            (item.identifier != "wine_bottle" || WineHelper.CanModifyLiquid(item));
        private static bool Vessel(GameItem item) => VesselKind(item) && WineLiquidAllowed(item) && ShopInventory.Movable(item) && !RequestBook.Reserved(item);
        private static bool VesselKind(GameItem item)
        {
            if (item == null || !item.IsTag(WaterHelper.LIQUID_CONTAINER_TAG) || item.IsTag("CHEMICAL_PRODUCT_TAG") ||
                item.IsTag(WaterHelper.LIQUID_CONTAINER_DROPPER_TAG)) return false;
            switch (item.identifier)
            {
                case "bottled_water": case "bottled_water_premium": case "small_bottled_water": case "large_bottled_water":
                case "water_jug": case "water_tank": case "water_pitcher": case "empty_beer_bottle": case "mini_bottle": case "wine_bottle":
                    return true;
                default: return false;
            }
        }
        private static List<GameItem> VisibleVessels()
        {
            var stock = ShopInventory.OwnedLocations(true).Where(ShopInventory.VisibleGoods).SelectMany(l => l.Inventory.childItems).ToList();
            var all = stock.Where(Vessel).ToList();
            foreach (var farm in stock)
            {
                if (farm.identifier != "moisture_farm" || !ShopInventory.Owned(farm) || farm.IsTag("BROKEN_MACHINE_TAG") ||
                    farm.IsTag("LOCKED_WORKING_TAG") || LockHelper.IsCurrentlyLocked(farm) ||
                    !(farm.contentWindow?.childElement is MachineryPanelRow panels)) continue;
                var port = panels.GetPanel("container");
                if (port == null || port.isHidden) continue;
                var nodes = MachineryContainerUtils.Collect(port.content);
                if (nodes.Count == 1 && nodes[0] is GameSlotInventory socket && !socket.IsRemoveLocked() && Vessel(socket.childItem))
                    all.Add(socket.childItem);
            }
            return all.Distinct().ToList();
        }
        private static string Ml(long ul) => (ul / 1000m).ToString("0.###", CultureInfo.CurrentCulture);
        private static bool NeedsInspection(GameItem item) => item != null && AttentionBook.HasInspection("f26.open", item.uniqueId);
        private static bool SameScope(string run, int slot) => ShopInventory.InStore && PlayerStore.Instance.runID == run &&
            PlayerStore.Instance.saveSlotId == slot;
        private bool GradesAllowed(List<Fluid> plan, int destinationGrade) => crossGrade.Value || plan.Count == 0 ||
            plan.All(f => f.Grade == (destinationGrade < 0 ? plan[0].Grade : destinationGrade));
        private static string Caption(GameItem i)
        {
            var host = i.parentInventory?.GetParentItem();
            var prefix = host?.identifier == "moisture_farm" ? host.GetDisplayName(true) + " › " : "";
            return prefix + i.GetDisplayName(true) + " · " + Ml(WaterHelper.GetCurrentCapacityUL(i)) + " mL";
        }
        private bool CanPour(GameItem item, int grade)
        {
            if (item == null || target == null || item == target || !Vessel(item) || !Vessel(target) || grade < 0 || NeedsInspection(item) || NeedsInspection(target) ||
                !item.MayTarget(target) || !item.CanTarget(target)) return false;
            int dest = Grade(target);
            return dest != -1 && (dest == -2 || dest == grade || crossGrade.Value);
        }
        private CustomUIBuilder Window()
        {
            var b = UiLayout.Rebuild(ref window, ModEntry.Id + ".water", ShopSettings.Text("f26.open"), 575, 420);
            window = b.GetWindow();
            CustomUIWindow own = window;
            b.OnClose(() =>
            {
                if (!ReferenceEquals(window, own)) return;
                window = null; target = null; chosen.Clear();
            });
            return b;
        }
        private void Draw()
        {
            if (!enabled.Value || !ShopInventory.InStore) return;
            DrawWith(VisibleVessels());
        }
        private void DrawWith(List<GameItem> all)
        {
            if (target != null && !all.Contains(target)) target = null;
            string run = PlayerStore.Instance.runID;
            int slot = PlayerStore.Instance.saveSlotId;
            IList<GameItem> Destinations() => enabled.Value && SameScope(run, slot) ? VisibleVessels()
                .Where(i => !NeedsInspection(i) && Grade(i) != -1 && WaterHelper.GetFreeCapacity(i) > 0).ToList() : new List<GameItem>();
            var b = Window();
            VisualChoices.Slot(b, ShopSettings.Text("visual.target"), target, target == null ? null : Caption(target),
                () => VisualChoices.ChooseItem(ShopSettings.Text("visual.target"), Destinations, target, item =>
                { if (Destinations().Contains(item)) { target = item; chosen.Remove(item); Draw(); } }), "target");
            if (target == null) { b.AddLabel(ShopSettings.Text("f26.hint")).Show(); return; }
            b.AddLabel(ShopSettings.Format("f26.free", Ml(WaterHelper.GetFreeCapacity(target))));
            b.BeginScrollFlexible(125);
            var entries = all.Where(item => CanPour(item, Grade(item))).Select(item => new ItemGrid.Entry(item)
            {
                Selected = chosen.Contains(item), Caption = Caption(item), Badge = WaterFeatureHelper.GetWaterDisplayName(item)
            }).ToList();
            RectTransform grid = null;
            void RefreshSelection()
            {
                int grade = Grade(target);
                var first = entries.FirstOrDefault(entry => chosen.Contains(entry.Item) && Grade(entry.Item) >= 0);
                int selectedGrade = grade == -2 && first != null ? Grade(first.Item) : grade;
                foreach (var row in entries)
                {
                    row.Selected = chosen.Contains(row.Item);
                    row.Enabled = row.Selected || (crossGrade.Value || selectedGrade < 0 || selectedGrade == Grade(row.Item));
                    if (grid != null) ItemGrid.RefreshEntry(grid, row);
                }
            }
            RefreshSelection();
            grid = ItemGrid.Add(b, entries, new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 130, CardHeight = 96,
                OnClick = entry =>
                {
                    if (!chosen.Add(entry.Item)) chosen.Remove(entry.Item);
                    RefreshSelection();
                } });
            b.End().AddButton(ShopSettings.Text("f26.preview"), Preview, "preview").Show();
        }
        private void Preview()
        {
            var all = VisibleVessels();
            if (!enabled.Value || !all.Contains(target) || NeedsInspection(target) || Grade(target) == -1) { Draw(); return; }
            var dest = target;
            string run = PlayerStore.Instance.runID;
            int slot = PlayerStore.Instance.saveSlotId;
            var home = dest.parentInventory;
            int before = WaterHelper.GetCurrentCapacityUL(dest), space = WaterHelper.GetFreeCapacity(dest), destGrade = Grade(dest);
            var filter = WaterHelper.GetFilterItem(dest);
            int filterDurability = filter == null ? -1 : DurabilityHelper.GetRealDurability(filter);
            bool exhausted = ReferenceEquals(pendingFilterHost, dest) && ReferenceEquals(pendingFilter, filter) &&
                filter != null && filterDurability <= 0;
            if (!exhausted) { pendingFilter = null; pendingFilterHost = null; }
            var plan = all.Where(i => chosen.Contains(i) && CanPour(i, Grade(i))).Select(i => new Fluid {
                Item = i, From = i.parentInventory, Volume = WaterHelper.GetCurrentCapacityUL(i), Grade = Grade(i) }).ToList();
            if (!GradesAllowed(plan, destGrade)) { Draw(); return; }
            var b = Window();
            VisualChoices.Slot(b, ShopSettings.Text("visual.target"), dest, Caption(dest), null, "target");
            b.AddLabel(ShopSettings.Format("f26.free", Ml(space)));
            b.BeginScrollFlexible(100);
            ItemGrid.Add(b, plan.Select(f => new ItemGrid.Entry(f.Item) { Caption = Caption(f.Item), Selected = true }),
                new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 130, CardHeight = 96 });
            b.End();
            bool mixing = (before > 0 && plan.Count > 0) || plan.Count > 1;
            if (mixing) b.AddLabel(ShopSettings.Text("f26.mixNotice"));
            if (filter != null) b.AddLabel(ShopSettings.Text("f26.filter"));
            b.BeginRow().AddButton(ShopSettings.Text("f29.back"), Draw);
            if (plan.Count > 0 && space > 0) b.AddButton(ShopSettings.Text(exhausted ? "f26.pourUnfiltered" : mixing ? "f26.approveMix" : "f26.pour"),
                () => Execute(dest, home, before, destGrade, space, filter, filterDurability, plan, run, slot), "pour");
            b.End().Show();
        }
        private void Execute(GameItem dest, GameInventory home, int before, int destGrade, int space,
            GameItem expectedFilter, int expectedFilterDurability, List<Fluid> plan, string run, int slot)
        {
            if (!enabled.Value || !SameScope(run, slot) || dest != target) return;
            var all = new HashSet<GameItem>(VisibleVessels());
            if (!GradesAllowed(plan, destGrade) || !all.Contains(dest) || NeedsInspection(dest) || dest.parentInventory != home || Grade(dest) != destGrade || WaterHelper.GetCurrentCapacityUL(dest) != before ||
                WaterHelper.GetFreeCapacity(dest) != space || !ReferenceEquals(WaterHelper.GetFilterItem(dest), expectedFilter) ||
                (expectedFilter != null && DurabilityHelper.GetRealDurability(expectedFilter) != expectedFilterDurability) ||
                plan.Any(f => !all.Contains(f.Item) || f.Item.parentInventory != f.From ||
                WaterHelper.GetCurrentCapacityUL(f.Item) != f.Volume || Grade(f.Item) != f.Grade || !CanPour(f.Item, f.Grade)))
            { Draw(); ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            long outflow = 0, inflow = 0;
            int expectedVolume = before;
            int remainingAt = plan.Count;
            bool paused = false, full = false, stopped = false;
            var result = new OperationResult("f26.open");
            for (int index = 0; index < plan.Count; index++)
            {
                var f = plan[index];
                if (!enabled.Value || !SameScope(run, slot)) return;
                all = new HashSet<GameItem>(VisibleVessels());
                string reason = null;
                if (WaterHelper.GetFreeCapacity(dest) <= 0) reason = "inv.changed";
                else if (!GradesAllowed(plan, destGrade) || target != dest || !all.Contains(dest) || !all.Contains(f.Item) ||
                    NeedsInspection(dest) || NeedsInspection(f.Item) || f.Item.parentInventory != f.From || dest.parentInventory != home ||
                    WaterHelper.GetCurrentCapacityUL(f.Item) != f.Volume || Grade(f.Item) != f.Grade ||
                    !f.Item.MayTarget(dest) || !f.Item.CanTarget(dest) || !SameScope(run, slot) ||
                    target != dest || f.Item.parentInventory != f.From || dest.parentInventory != home ||
                    !GradesAllowed(plan, destGrade) || !Vessel(f.Item) || !Vessel(dest) || Grade(f.Item) != f.Grade ||
                    WaterHelper.GetCurrentCapacityUL(f.Item) != f.Volume || WaterHelper.GetCurrentCapacityUL(dest) != expectedVolume) reason = "inv.changed";
                if (reason != null)
                {
                    if (!SameScope(run, slot)) return;
                    result.Skip(f.Item, 1, reason); remainingAt = index; stopped = true; break;
                }
                int priorOut = WaterHelper.GetCurrentCapacityUL(f.Item), priorIn = WaterHelper.GetCurrentCapacityUL(dest);
                int priorFree = WaterHelper.GetFreeCapacity(dest);
                var filter = WaterHelper.GetFilterItem(dest);
                int filterBefore = filter == null ? 0 : DurabilityHelper.GetRealDurability(filter);
                int sourceId = f.Item.GetUniqueID(), destId = dest.GetUniqueID();
                string sourceName = f.Item.GetDisplayName(true), destName = dest.GetDisplayName(true);
                // The native action also updates temperature, filter wear, sounds and container properties.
                int lost, gained, afterOut;
                try
                {
                    f.Item.Target(dest);
                    if (!SameScope(run, slot)) return;
                    if (f.Item == null || dest == null) throw new InvalidOperationException("A liquid vessel disappeared during the native action.");
                    afterOut = WaterHelper.GetCurrentCapacityUL(f.Item);
                    lost = priorOut - afterOut;
                    gained = WaterHelper.GetCurrentCapacityUL(dest) - priorIn;
                }
                catch (Exception error)
                {
                    Debug.LogWarning("[ShopAssist] F26 native pour interrupted: " + error);
                    if (!SameScope(run, slot)) return;
                    result.SkipMissing(sourceName, 1, "inv.inspect", itemId: sourceId, needsInspection: true);
                    result.SkipMissing(destName, 1, "inv.inspect", itemId: destId, needsInspection: true);
                    remainingAt = index; stopped = true; break;
                }
                if (lost < 0 || lost > priorOut || gained < 0 || gained > lost || gained > priorFree)
                {
                    result.SkipMissing(sourceName, 1, "inv.inspect", itemId: sourceId, needsInspection: true);
                    result.SkipMissing(destName, 1, "inv.inspect", itemId: destId, needsInspection: true);
                    remainingAt = index; stopped = true; break;
                }
                outflow += lost; inflow += gained; expectedVolume += gained;
                if (lost == 0)
                {
                    result.Skip(f.Item, 1, "attention.action_failed"); remainingAt = index; stopped = true; break;
                }
                bool sourceRemaining = afterOut > 0;
                if (WaterHelper.GetFreeCapacity(dest) <= 0)
                {
                    if (!sourceRemaining) result.Complete(f.Item, 1);
                    remainingAt = sourceRemaining ? index : index + 1; full = true; break;
                }
                // Stop between sources when filtration is exhausted. This also gives
                // automatic replacement mods a frame to install a fresh filter.
                if ((sourceRemaining || index + 1 < plan.Count) && WaterHelper.GetFreeCapacity(dest) > 0 && filterBefore > 0 &&
                    ReferenceEquals(WaterHelper.GetFilterItem(dest), filter) && DurabilityHelper.GetRealDurability(filter) <= 0)
                {
                    if (!sourceRemaining) result.Complete(f.Item, 1);
                    pendingFilter = filter; pendingFilterHost = dest; remainingAt = sourceRemaining ? index : index + 1; paused = true; break;
                }
                if (sourceRemaining)
                {
                    result.Skip(f.Item, 1, "attention.action_failed"); remainingAt = index; stopped = true; break;
                }
                result.Complete(f.Item, 1);
            }
            for (int i = 0; i < remainingAt; i++) chosen.Remove(plan[i].Item);
            if (!paused) { pendingFilter = null; pendingFilterHost = null; }
            if (stopped)
            {
                for (int i = remainingAt + 1; i < plan.Count; i++) result.Skip(plan[i].Item, 1, "operation.not_attempted");
                result.Detail = ShopSettings.Format("f26.confirmed", Ml(outflow), Ml(inflow));
            }
            // Persist uncertain native results before drawing from potentially damaged vessels.
            if (result.NeedsInspection) { ShopNotice.Report(result); return; }
            Draw();
            if (stopped) ShopNotice.Report(result);
            else
            {
                string message = ShopSettings.Format(paused ? "f26.filterPaused" : "f26.result", Ml(outflow), Ml(inflow));
                if (full) message += " · " + ShopSettings.Text("f08.full");
                ShopNotice.Show(message);
            }
        }

        internal void Tick()
        {
            if (enabled.Value && ShopInventory.InStore && key() != KeyCode.None && ShopInput.Ready() && Input.GetKeyDown(key())) Draw();
        }
        private void Close() { if (window != null && window.IsAlive) window.Close(); window = null; target = null; pendingFilter = null; pendingFilterHost = null; chosen.Clear(); }
        public void Dispose()
        {
            InventoryContextActions.Unregister("f26.open");
            settings.Changed -= Changed; ModHook.OnGameLoadedInit -= Close; ModHook.OnLeavingStoreEarly -= Close; Close();
        }
    }
}
