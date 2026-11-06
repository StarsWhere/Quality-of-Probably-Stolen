using System;
using System.Collections.Generic;

namespace ShopAssist
{
    internal static class ShopInventory
    {
        internal sealed class Location
        {
            internal GameInventory Inventory;
            internal GameItem Owner;
            internal GameItem[] Path;
            internal string Name, Role;
            internal int Page = -1;
            internal bool IsMachine, Ordinary;

            // Called with allocate=true only when the player explicitly binds this location.
            internal string Key(bool allocate = false)
            {
                if (Owner == null) return "root:" + Role;
                int id = allocate ? Owner.GetUniqueID() : Owner.uniqueId;
                return id > 0 ? "item:" + id + ":" + Role + ":" + Page : null;
            }
        }

        internal static bool InStore => PlayerStore.HasInstance && EmporiumEntry.Instance != null &&
            PlayerStore.Instance.playerLocation == PlayerStore.PlayerLocation.IN_STORE && !string.IsNullOrEmpty(PlayerStore.Instance.runID);

        internal static bool Owned(GameItem item) => item != null && item.parentInventory != null &&
            !item.IsTag("ITEM_HIDDEN_TAG") && !item.IsTag("ITEM_BEHIND_LOCK_TAG") && GeneralHelper.IsItemEffectivelyOwned(item);

        internal static bool Movable(GameItem item) => Owned(item) && item.unitCount > 0 &&
            !ItemPinHelper.IsPinned(item) && !DebrisHelper.IsDebris(item) && !item.parentInventory.IsRemoveLocked() &&
            item.MayRemove() && item.MaxNumRemove() > 0;

        private static bool AccessibleWindow(GameItem item)
        {
            return Owned(item) && item.contentWindow != null && !item.contentWindow.isLocked &&
                !LockHelper.IsCurrentlyLocked(item) && !item.IsTag("LOCKED_TAG") &&
                !(item.IsTag("TRACK_OPENED_TAG") && !item.IsTag("OPENED_TAG")) &&
                item.identifier != "smuggler_bay" && item.identifier != "smuggler_bay_mod" && item.identifier != "smuggler_bay_mini";
        }

        private static bool IsStorageBay(string id) => id == "storage_bay" || id == "storage_bay_large" ||
            id == "storage_bay_chem" || id == "storage_bay_gun";

        internal static bool SafeContainerForPeek(GameItem item)
        {
            if (!AccessibleWindow(item) || item.contentWindow.openCallback != null) return false;
            if (IsStorageBay(item.identifier)) return true;
            if (!(item.contentWindow.childElement is GameGridInventory)) return false;
            switch (item.identifier)
            {
                case "fanny_pack": case "satchel": case "kids_backpack": case "kids_backpack_large":
                case "backpack_small": case "backpack_medium": case "backpack_medium_military":
                case "backpack_large": case "backpack_large_military": case "toolbox":
                case "pouch_general": case "pouch_general_large":
                case "sec_box": case "med_box": case "eng_box": case "service_box": case "evidence_box":
                    return true;
                default: return false;
            }
        }

        internal static bool TryRefresh(Location previous, out Location refreshed)
            => TryRefresh(previous, OwnedLocations(false), out refreshed);

        internal static bool TryRefreshCustomer(Location previous, out Location refreshed)
            => TryRefresh(previous, CustomerLocations(), out refreshed);

        internal static bool TryRefresh(Location previous, List<Location> locations, out Location refreshed)
        {
            refreshed = null;
            if (previous == null || locations == null) return false;
            foreach (var current in locations)
            {
                if (current.Inventory != previous.Inventory || current.Owner != previous.Owner ||
                    current.Role != previous.Role || current.Page != previous.Page ||
                    current.Path.Length != previous.Path.Length) continue;
                bool same = true;
                for (int i = 0; i < current.Path.Length; i++)
                    if (current.Path[i] != previous.Path[i]) { same = false; break; }
                if (same) { refreshed = current; return true; }
            }
            return false;
        }

        internal static GameGridInventory OutingBag(GameItem bag)
        {
            var slot = EmporiumEntry.Instance?.afterhourPocketSlotInvBackpack;
            if (slot == null || !slot.stealUI || slot.childItem != bag || bag == null || bag.parentInventory != slot ||
                !Owned(bag) || !bag.IsTag("BACKPACK_TAG") || bag.contentWindow == null || bag.contentWindow.openCallback != null ||
                LockHelper.IsCurrentlyLocked(bag) || bag.IsTag("LOCKED_TAG") ||
                (bag.IsTag("TRACK_OPENED_TAG") && !bag.IsTag("OPENED_TAG"))) return null;
            // This native equipment slot locks the separate window and embeds its grid in the trip UI.
            // Only use the exact grid that the slot owns and renders, never the contents of a genuinely locked container.
            if (!(bag.contentWindow.childElement is GameGridInventory grid) || !slot.children.Contains(grid) ||
                grid.IsInsertLocked() || grid.IsRemoveLocked()) return null;
            return grid;
        }

        internal static List<Location> OwnedLocations(bool onlyOpen) => Locations(onlyOpen, false);
        internal static List<Location> MachineLocations() => Locations(false, true);
        internal static List<Location> CustomerLocations() => Locations(false, true, true);

        private static List<Location> Locations(bool onlyOpen, bool includeClosedMachines, bool customerQuery = false)
        {
            var result = new List<Location>();
            if (!InStore) return result;
            var seen = new HashSet<GameInventory>();
            var visited = new HashSet<GameItem>();
            var e = EmporiumEntry.Instance;
            void Add(GameInventory inv, GameItem owner, string name, string role, int page, bool machine, bool ordinary, GameItem[] path = null)
            {
                if (inv == null || !seen.Add(inv)) return;
                result.Add(new Location { Inventory = inv, Owner = owner, Name = name, Role = role,
                    Page = page, IsMachine = machine, Ordinary = ordinary, Path = path ?? Array.Empty<GameItem>() });
            }
            Add(e.invElement, null, ShopSettings.Text("inv.stock"), "stock", -1, false, true);
            Add(e.backInvinvElement, null, ShopSettings.Text("inv.counter"), "counter", -1, false, false);
            Add(e.showcaseElement, null, ShopSettings.Text("inv.showcase"), "showcase", -1, false, false);
            Add(e.frontInvinvElement, null, ShopSettings.Text("inv.front"), "front", -1, false, false);
            for (int i = 0; i < result.Count; i++)
            {
                var location = result[i];
                if (location.IsMachine || location.Inventory.IsRemoveLocked()) continue;
                foreach (var item in location.Inventory.childItems)
                {
                    if (!AccessibleWindow(item) || !visited.Add(item)) continue;
                    var w = item.contentWindow;
                    if (onlyOpen && !w.IsVisible()) continue;
                    bool storage = SafeContainerForPeek(item);
                    bool machine = item.IsGameItemType("MACHINE");
                    bool closedMachine = !w.IsVisible() && machine && includeClosedMachines && w.childElement is MachineryPanelRow;
                    if (!w.IsVisible() && !storage && !closedMachine) continue;
                    string path = location.Name + " › " + item.GetDisplayName(true);
                    var hosts = new GameItem[location.Path.Length + 1];
                    Array.Copy(location.Path, hosts, location.Path.Length);
                    hosts[hosts.Length - 1] = item;
                    if (w.childElement is GameGridInventory grid)
                    {
                        Add(grid, item, path, "contents", -1, machine || !storage, storage && !machine, hosts);
                        continue;
                    }
                    if (!(w.childElement is MachineryPanelRow panels)) continue;
                    foreach (var panel in panels.Panels)
                    {
                        if (panel == null || panel.isHidden || panel.content == null || string.IsNullOrEmpty(panel.role)) continue;
                        string role = panel.role;
                        // Read-only machine views may inspect the known module bay without opening a second window.
                        // Other closed-machine roles are read only for explicitly adapted equipment.
                        if (closedMachine && !customerQuery && role != "module" && item.identifier != "hydroponic" &&
                            item.identifier != "moisture_farm" && item.identifier != "furnace" &&
                            item.identifier != "printer" && item.identifier != "evaporator") continue;
                        if (panel.content is MachineryTabs tabs)
                        {
                            for (int page = 0; page < tabs.count; page++)
                            {
                                if (onlyOpen && page != tabs.selected) continue;
                                Add(tabs.GetTabInventory(page), item, path + " › " + tabs.GetCaption(page),
                                    role, page, !storage || role != "storage", storage && role == "storage", hosts);
                            }
                            continue;
                        }
                        var nodes = MachineryContainerUtils.Collect(panel.content);
                        if (nodes == null || (!customerQuery && nodes.Count != 1)) continue;
                        string caption = panel.label?.GetText();
                        for (int index = 0; index < nodes.Count; index++)
                        {
                            if (!(nodes[index] is GameInventory single)) continue;
                            if (customerQuery && !PublicPanelBranch(single, panel)) continue;
                            string suffix = nodes.Count > 1 ? " " + (index + 1) : "";
                            Add(single, item, path + " › " + (string.IsNullOrEmpty(caption) ? role : caption) + suffix,
                                role, -1, !storage || role != "storage", storage && role == "storage", hosts);
                        }
                    }
                }
            }
            return result;
        }

        // A closed window disables an ancestor above the panel. Hidden nested
        // panels, however, disable a node within the panel and must not be read.
        private static bool PublicPanelBranch(GameInventory inventory, MachineryPanel panel)
        {
            if (!(inventory is PixelElement element) || element.handler == null || panel?.handler == null) return false;
            var root = panel.handler.transform;
            for (var cursor = element.handler.transform; cursor != null; cursor = cursor.parent)
            {
                if (!cursor.gameObject.activeSelf) return false;
                if (ReferenceEquals(cursor, root)) return true;
            }
            return false;
        }

        internal static bool WouldContainSelf(GameItem item, GameInventory target)
        {
            var seen = new HashSet<GameItem>();
            for (var parent = target?.GetParentItem(); parent != null; parent = parent.parentInventory?.GetParentItem())
                if (ReferenceEquals(item, parent) || !seen.Add(parent)) return true;
            return false;
        }

        // Moves a complete existing instance into an empty grid placement. This
        // path intentionally excludes split/merge and callback-heavy machine slots.
        internal static bool TryMoveIntact(GameItem item, Location source, Location target, out string reason, GridShape expected = null) =>
            MoveIntact(item, source, target, false, out reason, expected);

        internal static bool CanRouteFromCounter(GameItem item, Location source) => InStore && source != null && source.Owner == null &&
            source.Role == "counter" && source.Inventory == EmporiumEntry.Instance.backInvinvElement &&
            item != null && item.parentInventory == source.Inventory && SettledCounterEvidence.Allows(item);

        internal static bool IsMainStockBranch(Location location)
        {
            if (!InStore || location == null) return false;
            var stock = EmporiumEntry.Instance.invElement;
            if (location.Owner == null) return location.Role == "stock" && location.Inventory == stock;
            return location.Path != null && location.Path.Length > 0 && location.Path[0].parentInventory == stock;
        }

        internal static bool TryMoveToTaggedStock(GameItem item, Location source, Location target, out string reason, GridShape expected)
        {
            reason = "inv.changed";
            if (!IsMainStockBranch(target) || !target.Ordinary || source == null) return false;
            return MoveIntact(item, source, target, !IsMainStockBranch(source), out reason, expected);
        }

        private static bool MoveIntact(GameItem item, Location source, Location target, bool fromCounter, out string reason, GridShape expected)
        {
            reason = "inv.changed";
            if (!InStore || source == null || target == null || (fromCounter ? !CanRouteFromCounter(item, source) : !source.Ordinary) || !target.Ordinary ||
                !ReferenceEquals(item?.parentInventory, source.Inventory) || !Movable(item) ||
                item.MaxNumRemove() < item.unitCount || WouldContainSelf(item, target.Inventory) ||
                target.Inventory.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(target.Inventory)) return false;
            if (source.Inventory == target.Inventory) return true;
            var destination = target.Inventory as GameGridInventory;
            var slot = expected == null ? GridPlacements.Empty(destination, item) : GridPlacements.Exact(destination, item, expected);
            if (slot == null || slot.targetItem != null || slot.itemGridShape == null ||
                slot.numTransfer < item.unitCount || slot.inventory != target.Inventory)
            { reason = "inv.noSpace"; return false; }
            var original = item.modifiedShape.Clone();
            int count = item.unitCount;
            int moved = 0;
            try { moved = slot.TryAcceptOnce(count); }
            catch (Exception error) { UnityEngine.Debug.LogWarning("[ShopAssist] Item move interrupted: " + error); }
            if (moved == count && ReferenceEquals(item.parentInventory, target.Inventory) && item.unitCount == count &&
                !source.Inventory.childItems.Contains(item) && target.Inventory.childItems.Contains(item)) return true;
            if (item.unitCount != count) { reason = "inv.inspect"; return false; }
            try
            {
                if (item.parentInventory == null && !source.Inventory.IsInsertLocked())
                    GridPlacements.Exact(source.Inventory as GameGridInventory, item, original)?.TryAcceptOnce(count);
                // Native SlotMarker can change the transform even when a hook vetoes Expel.
                // Restore the pose in place if the original grid still owns the item.
                if (ReferenceEquals(item.parentInventory, source.Inventory) && !SamePose(item.modifiedShape, original) &&
                    source.Inventory is GameGridInventory grid && grid.childItems.Contains(item) &&
                    grid.CheckShapePlacement(item, original, new List<GameItem> { item }))
                {
                    item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetTransform(original);
                    grid.Validate();
                }
            }
            catch (Exception error) { UnityEngine.Debug.LogWarning("[ShopAssist] Item restore interrupted: " + error); }
            if (item.parentInventory != source.Inventory || !SamePose(item.modifiedShape, original) ||
                target.Inventory.childItems.Contains(item) || !source.Inventory.childItems.Contains(item)) reason = "inv.inspect";
            return false;
        }

        private static bool SamePose(GridShape a, GridShape b) => a.minX == b.minX && a.minY == b.minY &&
            a.orientation == b.orientation && a.flipped == b.flipped;
    }
}
