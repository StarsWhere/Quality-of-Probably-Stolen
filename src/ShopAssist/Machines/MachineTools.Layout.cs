using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private ConfigEntry<bool> layoutEnabled;
        private bool layoutOpen;
        private ModuleLayout.Plan layoutPlan;
        private GridShape layoutBoard;
        private int layoutObjective;
        private string layoutStatus;

        private void InitModuleLayout() => layoutEnabled = settings.Toggle("f12.enabled", "f12.tip");

        private void TickModuleLayout()
        {
            if (layoutOpen && (!layoutEnabled.Value || modulesWindow == null || !modulesWindow.IsAlive || !SameModuleSession())) CloseLayout();
        }

        private void LayoutSettingsChanged() { if (!layoutEnabled.Value) { CloseLayout(); modulesDirty = true; } }

        private void OpenLayout()
        {
            if (!layoutEnabled.Value || !SameModuleSession() || CustomUIManager.Instance == null) return;
            if (layoutOpen) { CloseLayout(); DrawLayout(); return; }
            layoutOpen = true; layoutObjective = 0; layoutPlan = null; layoutBoard = null; layoutStatus = null;
            DrawLayout();
        }

        private void DrawLayout()
        {
            if (!SameModuleSession()) { CloseLayout(); return; }
            RenderModules();
        }

        private void DrawLayoutControls(CustomUIBuilder builder)
        {
            builder.BeginRow();
            builder.AddDropdown(new[] { ShopSettings.Text("f10.performance"), ShopSettings.Text("f10.efficiency"), ShopSettings.Text("f10.quality") },
                layoutObjective, value => { layoutObjective = value; layoutPlan = null; layoutStatus = null; DrawLayout(); }, "objective");
            builder.Last.SetFlexible(1, 0);
            builder.AddButton(ShopSettings.Text("f12.preview"), BuildLayout, "preview");
            builder.Last.SetFixedWidth(86);
            if (layoutPlan != null)
            {
                builder.AddButton(ShopSettings.Text("f12.apply"), ApplyLayout, "apply");
                builder.Last.SetFixedWidth(82);
            }
            builder.End();
            if (!string.IsNullOrEmpty(layoutStatus))
            {
                builder.AddLabel(layoutStatus == "grid.too_many" ? ShopSettings.Format(layoutStatus, selectedModules.Count) : ShopSettings.Text(layoutStatus), "layout_status");
                FitModuleText(builder.Last);
                builder.Last.SetPreferredSize(-1, 44);
                if (builder.Last.text != null) builder.Last.text.enableWordWrapping = true;
            }
            if (layoutPlan != null)
            {
                var before = layoutPlan.Before; var after = layoutPlan.After;
                LayoutStat(builder, "f10.performance", before.Performance, after.Performance);
                LayoutStat(builder, "f10.efficiency", before.Efficiency, after.Efficiency);
                LayoutStat(builder, "f10.quality", before.Quality, after.Quality);
            }
        }

        private static void LayoutStat(CustomUIBuilder builder, string name, int before, int after)
        {
            builder.AddLabel(ShopSettings.Format("grid.stat_delta", ShopSettings.Text(name), before, after, after - before),
                "layout_" + name.Substring(name.IndexOf('.') + 1));
            FitModuleText(builder.Last);
        }

        private void DrawLayoutBoard(CustomUIBuilder builder, float cell)
        {
            var entries = new List<ItemGrid.Entry>();
            for (int i = 0; i < layoutPlan.Parts.Count; i++)
            {
                ModuleLayout.Part part = layoutPlan.Parts[i]; GridShape shape = layoutPlan.Shapes[i];
                if (shape == null) continue;
                entries.Add(new ItemGrid.Entry(part.Item, part.Source, shape) { Selected = !part.Existing });
            }
            ItemGrid.Add(builder, entries, new ItemGrid.Options { Board = layoutBoard, CellSize = cell, HighContrast = true });
        }

        private bool LayoutContext(out GameGridInventory bay)
        {
            bay = moduleBay as GameGridInventory;
            if (!layoutEnabled.Value || !SameModuleSession() || ModuleFamily(moduleTarget) == null ||
                AttentionBook.HasInspection("f12.open", moduleTarget.uniqueId)) return false;
            return bay != null && !bay.IsInsertLocked() && !bay.IsRemoveLocked() &&
                !bay.childItems.Any(item => item != null && AttentionBook.HasInspection("f12.open", item.uniqueId)) &&
                GeneralHelper.MayPlayerInsertInto(bay) && bay.inventoryShape != null &&
                FindTarget(moduleTarget, out GameItem focused, out GameInventory foundBay) &&
                ReferenceEquals(focused, moduleTarget) && ReferenceEquals(foundBay, bay);
        }

        private bool SnapshotLayout(out List<ModuleLayout.Part> parts, out GameGridInventory bay)
        {
            parts = new List<ModuleLayout.Part>();
            if (!LayoutContext(out bay) || selectedModules.Count > 5) return false;
            foreach (GameItem item in bay.childItems)
            {
                bool fixedPosition = FixedInLayout(item);
                ModuleLayout.Part part = ModuleLayout.Snapshot(item, true, fixedPosition);
                if (part == null) return false;
                parts.Add(part);
            }
            var locations = ShopInventory.OwnedLocations(false);
            foreach (GameItem item in selectedModules)
            {
                if (!Candidate(item, bay, locations)) return false;
                ModuleLayout.Part part = ModuleLayout.Snapshot(item, false, false);
                if (part == null) return false;
                parts.Add(part);
            }
            return parts.Count > 0 && parts.Count <= 12;
        }

        private bool FixedInLayout(GameItem item) => moduleTarget.identifier == "evaporator" ||
            !ShopInventory.Movable(item) || item.IsTag("MODULE_STUCK_TAG");

        private bool Candidate(GameItem item, GameGridInventory bay, List<ShopInventory.Location> locations)
        {
            return item != null && item.unitCount > 0 && ShopInventory.Movable(item) && item.MaxNumRemove() >= item.unitCount &&
                !ShopInventory.WouldContainSelf(item, bay) && ModuleCompatibility(moduleTarget, item) > 0 &&
                !AttentionBook.HasInspection("f10.open", item.uniqueId) && !AttentionBook.HasInspection("f12.open", item.uniqueId) &&
                locations.Any(location => ShopInventory.SearchableGoods(location) && ReferenceEquals(location.Inventory, item.parentInventory));
        }

        private void BuildLayout()
        {
            layoutPlan = null; layoutBoard = null; layoutStatus = null;
            if (selectedModules.Count > 5) { layoutStatus = "grid.too_many"; DrawLayout(); return; }
            if (!SnapshotLayout(out List<ModuleLayout.Part> parts, out GameGridInventory bay))
            { layoutStatus = "f12.unsupported"; DrawLayout(); return; }
            var current = parts.ConvertAll(part => part.Existing ? part.Original : null);
            if (!ModuleLayout.Evaluate(parts, current, out ModuleLayout.Values model) || !NativeEquals(model))
            { layoutStatus = "f12.unsupported"; DrawLayout(); return; }
            ModuleLayout.Outcome outcome = ModuleLayout.Search(bay, parts, layoutObjective, out layoutPlan);
            if (outcome != ModuleLayout.Outcome.Improved)
                layoutStatus = outcome == ModuleLayout.Outcome.Timeout ? "f12.timeout" :
                    outcome == ModuleLayout.Outcome.NoImprovement ? "f12.no_better" : "f12.unsupported";
            else layoutBoard = bay.inventoryShape.Clone();
            DrawLayout();
        }

        private bool NativeEquals(ModuleLayout.Values value) =>
            moduleTarget.GetTagReadonly("TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT").GetInt() == value.Performance &&
            moduleTarget.GetTagReadonly("TOTAL_PERCENTAGE_POWER_BONUS_INT").GetInt() == value.Efficiency &&
            moduleTarget.GetTagReadonly("TOTAL_PERCENTAGE_QUALITY_BONUS_INT").GetInt() == value.Quality;

        private bool LayoutStillValid(ModuleLayout.Plan plan, GameGridInventory bay)
        {
            if (plan == null || !LayoutContext(out GameGridInventory live) || !ReferenceEquals(live, bay) ||
                !ModuleLayout.SameShape(layoutBoard, bay.inventoryShape) ||
                selectedModules.Count != plan.Parts.Count(p => !p.Existing)) return false;
            var locations = ShopInventory.OwnedLocations(false);
            var existing = plan.Parts.Where(p => p.Existing).Select(p => p.Item).ToList();
            if (bay.childItems.Count != existing.Count || bay.childItems.Any(item => !existing.Contains(item))) return false;
            var movable = plan.Parts.Where(p => p.Existing && !p.Fixed).Select(p => p.Item).ToList();
            for (int i = 0; i < plan.Parts.Count; i++)
            {
                ModuleLayout.Part part = plan.Parts[i]; GridShape target = plan.Shapes[i];
                if (!ModuleLayout.Same(part)) return false;
                bool nowFixed = part.Existing && FixedInLayout(part.Item);
                if (part.Existing && (part.Fixed != nowFixed || part.Fixed && !ModuleLayout.SameShape(part.Original, target))) return false;
                if (!part.Existing && (!selectedModules.Contains(part.Item) || !Candidate(part.Item, bay, locations))) return false;
                if (target == null) { if (part.Existing) return false; continue; }
                if (!(part.Existing && part.Fixed) &&
                    (!bay.MayHaveValidInventorySlot(part.Item) || !bay.CheckShapePlacement(part.Item, target, movable))) return false;
                for (int j = 0; j < i; j++) if (plan.Shapes[j] != null && target.Intersects(plan.Shapes[j])) return false;
            }
            return ModuleLayout.Evaluate(plan.Parts, plan.Parts.ConvertAll(p => p.Existing ? p.Original : null), out var before) && NativeEquals(before);
        }

        private void ApplyLayout()
        {
            ModuleLayout.Plan plan = layoutPlan;
            if (!(moduleBay is GameGridInventory bay) || !LayoutStillValid(plan, bay))
            { layoutPlan = null; layoutStatus = "f12.changed"; DrawLayout(); return; }
            var added = new List<ModuleLayout.Part>();
            OperationResult.ItemIdentity machineIdentity;
            var identities = new List<(OperationResult.ItemIdentity Item, string Source)>();
            try
            {
                machineIdentity = OperationResult.Capture(moduleTarget);
                var locations = ModuleLocations();
                foreach (ModuleLayout.Part part in plan.Parts)
                    identities.Add((OperationResult.Capture(part.Item),
                        locations.Find(location => ReferenceEquals(location.Inventory, part.Source))?.Name ?? ""));
            }
            catch (Exception error)
            {
                log("F12 could not capture the layout before applying it: " + error);
                layoutPlan = null; layoutStatus = "f12.changed"; DrawLayout(); return;
            }
            try
            {
                SetExistingShapes(plan, usePlanned: true);
                // Check each destination against the live native bay before moving any new item.
                for (int i = 0; i < plan.Parts.Count; i++)
                {
                    ModuleLayout.Part part = plan.Parts[i];
                    if (part.Existing || plan.Shapes[i] == null) continue;
                    if (ExactSlot(bay, part.Item, plan.Shapes[i]) == null) throw new InvalidOperationException("Preflight rejected a module placement");
                }
                for (int i = 0; i < plan.Parts.Count; i++)
                {
                    ModuleLayout.Part part = plan.Parts[i]; GridShape shape = plan.Shapes[i];
                    if (part.Existing || shape == null) continue;
                    if (!Candidate(part.Item, bay, ShopInventory.OwnedLocations(false))) throw new InvalidOperationException("Module source changed");
                    SlotMarker slot = ExactSlot(bay, part.Item, shape);
                    added.Add(part);
                    if (slot == null || slot.TryAcceptOnce(part.Item.unitCount) != part.Item.unitCount ||
                        !ReferenceEquals(part.Item.parentInventory, bay))
                    {
                        if (part.Item.parentInventory == null) RestoreDetached(part, bay);
                        throw new InvalidOperationException("Native module move did not complete");
                    }
                }
                RecomputeModules(bay);
                if (!NativeEquals(plan.After)) throw new InvalidOperationException("Native module totals differ from the preview");
                foreach (var part in added) selectedModules.Remove(part.Item);
                layoutPlan = null; layoutStatus = "f12.applied"; modulesDirty = true;
            }
            catch (Exception error)
            {
                bool allReturned = true;
                for (int i = added.Count - 1; i >= 0; i--)
                {
                    ModuleLayout.Part part = added[i];
                    try { if (!ReturnPart(part, bay)) allReturned = false; }
                    catch (Exception recoveryError) { allReturned = false; log("F12 module recovery failed: " + recoveryError); }
                }
                if (allReturned)
                {
                    try { SetExistingShapes(plan, usePlanned: false); }
                    catch (Exception recoveryError) { allReturned = false; log("F12 could not restore earlier positions: " + recoveryError); }
                }
                try { RecomputeModules(bay); }
                catch (Exception recoveryError) { allReturned = false; log("F12 could not refresh module totals: " + recoveryError); }
                if (!allReturned)
                {
                    var result = new OperationResult("f12.open");
                    result.SkipMissing(machineIdentity.Name, 1, "f12.inspect", machineIdentity.Name, machineIdentity.ItemId, true);
                    foreach (var identity in identities)
                        result.SkipMissing(identity.Item.Name, 1, "f12.inspect", identity.Source + " → " + machineIdentity.Name, identity.Item.ItemId, true);
                    ShopNotice.Report(result);
                }
                layoutPlan = null; layoutStatus = allReturned ? "f12.changed" : "f12.inspect"; modulesDirty = true;
                log("F12 stopped while applying the layout: " + error);
            }
            DrawLayout();
        }

        private static SlotMarker ExactSlot(GameGridInventory inventory, GameItem item, GridShape shape)
        {
            if (inventory == null || item == null || shape == null || inventory.IsInsertLocked() ||
                !GeneralHelper.MayPlayerInsertInto(inventory)) return null;
            SlotMarker slot = inventory.TryInventorySlot(item, item.unitCount, shape);
            return slot != null && ReferenceEquals(slot.inventory, inventory) && slot.targetItem == null &&
                slot.itemGridShape != null && slot.numTransfer >= item.unitCount ? slot : null;
        }

        private static void SetExistingShapes(ModuleLayout.Plan plan, bool usePlanned)
        {
            for (int i = 0; i < plan.Parts.Count; i++)
            {
                ModuleLayout.Part part = plan.Parts[i];
                if (!part.Existing) continue;
                GridShape shape = usePlanned ? plan.Shapes[i] : part.Original;
                if (!ModuleLayout.SameShape(part.Item.modifiedShape, shape))
                    part.Item.modifiedShape = new GridShapeBuilder(part.Item.modifiedShape).SetTransform(shape);
            }
            foreach (ModuleLayout.Part part in plan.Parts) if (part.Existing) part.Item.ValidateShapeState();
        }

        private void RecomputeModules(GameGridInventory bay)
        {
            ModuleEffectHelper.ComputeAllModuleTempStat(bay.childItems);
            ModuleHelper.ComputeModuleEffect(bay, null, moduleTarget);
            ModuleHelper.ApplyBasicModuleEffect(bay, null, moduleTarget);
            MachineryHelper.UpdatePowerUsageProcessTypeMachine(moduleTarget);
        }

        private bool ReturnPart(ModuleLayout.Part part, GameGridInventory bay)
        {
            if (ReferenceEquals(part.Item.parentInventory, part.Source)) return true;
            if (part.Source is GameGridInventory source)
            {
                try
                {
                    SlotMarker slot = ExactSlot(source, part.Item, part.Original);
                    if (slot != null && slot.TryAcceptOnce(part.Item.unitCount) == part.Item.unitCount &&
                        ReferenceEquals(part.Item.parentInventory, source)) return true;
                }
                catch (Exception error) { log("F12 could not return a module to its source: " + error); }
            }
            if (part.Item.parentInventory == null) RestoreDetached(part, bay);
            return ReferenceEquals(part.Item.parentInventory, part.Source);
        }

        private void RestoreDetached(ModuleLayout.Part part, GameGridInventory bay)
        {
            if (part.Item.parentInventory != null) return;
            var destinations = new List<GameGridInventory>();
            if (part.Source is GameGridInventory source) destinations.Add(source);
            if (bay != null && !destinations.Contains(bay)) destinations.Add(bay);
            foreach (var location in ShopInventory.OwnedLocations(false))
                if (location.Ordinary && location.Inventory is GameGridInventory grid && !destinations.Contains(grid)) destinations.Add(grid);
            foreach (GameGridInventory grid in destinations)
            {
                try
                {
                    SlotMarker slot = ReferenceEquals(grid, part.Source) ? ExactSlot(grid, part.Item, part.Original) : null;
                    if (slot == null) slot = GridPlacements.Empty(grid, part.Item);
                    if (slot != null && slot.TryAcceptOnce(part.Item.unitCount) == part.Item.unitCount && part.Item.parentInventory != null) return;
                }
                catch (Exception error) { log("F12 module recovery location failed: " + error); }
            }
        }

        private void CloseLayout()
        {
            layoutOpen = false; layoutPlan = null; layoutBoard = null; layoutStatus = null;
        }
    }
}
