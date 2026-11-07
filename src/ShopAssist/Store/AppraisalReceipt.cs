using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;

namespace ShopAssist
{
    // Routes only the newly created receipt in a single native fee invocation.
    internal sealed class AppraisalReceipt : IDisposable
    {
        private const string DataKey = "f24.target";
        private static AppraisalReceipt active;
        internal static bool AppliesTo(GameItem item) => item != null && ShopInventory.Owned(item) &&
            ShopInventory.IsOrdinaryContainer(item);
        internal static ActionAvailability AvailabilityFor(GameItem item)
        {
            if (active?.enabled.Value != true)
                return ActionAvailability.Blocked(ActionUnavailableReason.Disabled, "availability.disabled");
            if (!ShopInventory.InStore)
                return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.store");
            if (!AppliesTo(item) || !ShopInventory.SafeContainerForPeek(item) || !ShopInventory.OwnedLocations(false)
                .Any(l => l.Owner == item && l.Ordinary && l.Inventory is GameGridInventory && !l.IsMachine && !l.Inventory.IsInsertLocked()))
                return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "f24.no_targets");
            return ActionAvailability.Ready;
        }
        internal static bool CanBind(GameItem item) => AvailabilityFor(item).Allowed;
        internal static void OpenBind(GameItem item)
        {
            if (!CanBind(item)) return;
            AppraisalReceipt owner = active;
            string run = PlayerStore.Instance.runID;
            int slot = PlayerStore.Instance.saveSlotId;
            bool Current() => ReferenceEquals(active, owner) && ShopInventory.InStore && PlayerStore.Instance.runID == run &&
                PlayerStore.Instance.saveSlotId == slot && CanBind(item);
            IList<ShopInventory.Location> Candidates() => Current() ? ShopInventory.OwnedLocations(false)
                .Where(l => l.Owner == item && l.Ordinary && l.Inventory is GameGridInventory && !l.IsMachine &&
                    !l.Inventory.IsInsertLocked()).ToList() :
                new List<ShopInventory.Location>();
            var candidates = Candidates();
            if (candidates.Count == 1) active.Bind(candidates[0]);
            else VisualChoices.ChooseLocation(ShopSettings.Text("f24.choose"), Candidates, null,
                location => { if (Current() && location.Owner == item) owner.Bind(location); });
        }
        [ThreadStatic] private static Scope scope;
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<int> destination;
        private readonly Action<string> log;
        private CustomUIWindow window;
        private bool started;

        private sealed class Binding
        {
            [JsonRequired] public int version;
            [JsonRequired] public string run;
            [JsonRequired] public int slot;
            [JsonRequired] public int owner;
            [JsonRequired] public string role;
            [JsonRequired] public int page;
        }
        private sealed class Scope
        {
            internal Scope Parent;
            internal GameItem Receipt;
            internal int Created;
            internal bool Claimed;
        }
        private sealed class Delivery
        {
            internal bool Fallback;
            internal bool Routed;
        }

        internal AppraisalReceipt(ShopSettings settings, Action<string> log)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            enabled = settings.Toggle("f24.enabled", "f24.tip");
            destination = settings.Choice("f24.destination", "f24.destination.tip", new[] { "f24.front", "f24.box", "f24.trash" });
            settings.Action("f24.choose", ChooseTarget, () => enabled.Value && ShopInventory.InStore);
        }

        public void Start()
        {
            if (started) return;
            started = true; active = this;
            ModHook.OnGameLoadedInit += Close;
            ModHook.OnLeavingStoreEarly += Close;
        }

        private void ChooseTarget()
        {
            if (!enabled.Value || !ShopInventory.InStore || CustomUIManager.Instance == null || !ReadBinding(out _)) return;
            if (window != null && window.IsAlive) { Close(); return; }
            string run = PlayerStore.Instance.runID;
            int slot = PlayerStore.Instance.saveSlotId;
            bool Current() => enabled.Value && ShopInventory.InStore && PlayerStore.Instance.runID == run && PlayerStore.Instance.saveSlotId == slot;
            IList<ShopInventory.Location> Options() => Current() ? ShopInventory.OwnedLocations(false)
                .Where(l => l.Ordinary && !l.IsMachine && l.Owner != null && l.Inventory is GameGridInventory &&
                    !l.Inventory.IsInsertLocked() && ShopInventory.SafeContainerForPeek(l.Owner) &&
                    (l.Role == "contents" || l.Role == "storage")).ToList() : new List<ShopInventory.Location>();
            VisualChoices.ChooseLocation(ShopSettings.Text("f24.choose"), Options, null, location => { if (Current()) Bind(location); });
        }

        private void Bind(ShopInventory.Location location)
        {
            if (!enabled.Value || !ShopInventory.InStore || !ReadBinding(out _) ||
                !ShopInventory.TryRefresh(location, out ShopInventory.Location current) || !current.Ordinary || current.IsMachine ||
                current.Owner == null || current.Inventory.IsInsertLocked() || !ShopInventory.SafeContainerForPeek(current.Owner) ||
                (current.Role != "contents" && current.Role != "storage") || current.Page < -1 || current.Page > 4 ||
                !Guid.TryParse(PlayerStore.Instance.runID, out Guid run) || run == Guid.Empty || PlayerStore.Instance.saveSlotId < 0) return;
            int id = current.Owner.GetUniqueID(); // A binding is created only by this explicit player action.
            if (id <= 0) return;
            var binding = new Binding { version = 1, run = run.ToString("N"), slot = PlayerStore.Instance.saveSlotId,
                owner = id, role = current.Role, page = current.Page };
            try
            {
                ModHelper.SetModData(ModEntry.Id, DataKey, ShopData.Encode(binding));
                Close(); ShopNotice.Show(ShopSettings.Text("f24.bound"));
            }
            catch (Exception error) { log("F24 could not save the destination: " + error); ShopNotice.Show(ShopSettings.Text("f24.bind_failed")); }
        }

        private bool ReadBinding(out Binding value)
        {
            value = null;
            string raw = ModHelper.GetModData(ModEntry.Id, DataKey, null);
            if (raw == null) return true;
            try
            {
                Binding parsed = ShopData.Decode<Binding>(raw, 1024);
                if (parsed == null || parsed.version != 1 || !Guid.TryParseExact(parsed.run, "N", out Guid stored) || stored == Guid.Empty ||
                    parsed.slot < 0 || parsed.owner <= 0 || (parsed.role != "contents" && parsed.role != "storage") || parsed.page < -1 || parsed.page > 4 ||
                    !Guid.TryParse(PlayerStore.Instance.runID, out Guid current) || current != stored || PlayerStore.Instance.saveSlotId != parsed.slot)
                    throw new FormatException("Unsupported or mismatched F24 binding");
                value = parsed; return true;
            }
            catch (Exception error)
            {
                log("F24 destination is invalid or unsupported and was retained: " + error.Message);
                ShopNotice.Show(ShopSettings.Text("f24.bad_data"));
                return false;
            }
        }

        private GameGridInventory Target()
        {
            int mode = destination.Value;
            if (mode == 2)
            {
                var entry = EmporiumEntry.Instance;
                if (entry?.trashcan == null || entry.trashcanInvElement == null ||
                    !ReferenceEquals(entry.trashcanInvElement.GetParentItem(), entry.trashcan)) return null;
                return entry.trashcanInvElement;
            }
            if (mode != 1 || !ReadBinding(out Binding binding) || binding == null) return null;
            var candidates = ShopInventory.OwnedLocations(false).Where(l => l.Ordinary && !l.IsMachine && l.Owner != null &&
                l.Owner.uniqueId == binding.owner && l.Role == binding.role && l.Page == binding.page &&
                l.Inventory is GameGridInventory && ShopInventory.SafeContainerForPeek(l.Owner)).ToArray();
            return candidates.Length == 1 ? (GameGridInventory)candidates[0].Inventory : null;
        }

        private bool Route(GameItem item)
        {
            if (item == null) return Decline("receipt is null");
            if (!enabled.Value || destination.Value == 0 || !ShopInventory.InStore || item.identifier != "slip" ||
                item.parentInventory != null || item.unitCount != 1 || !GeneralHelper.IsItemOwned(item) || ContrabandHelper.IsContraband(item) ||
                !(item is GameItemElement element) || element.IsDestroyed())
                return Decline("receipt state (id=" + item.identifier + ", unplaced=" + (item.parentInventory == null) +
                    ", count=" + item.unitCount + ", owned=" + GeneralHelper.IsItemOwned(item) + ", addCallback=" +
                    (item.onSlotAddItemFunc != null) + ", element=" + (item is GameItemElement) + ")");
            if (!IsNativeItemCallback(item)) return Decline("unrecognized receipt add callback");
            GameGridInventory target = Target();
            GameGridInventory front = EmporiumEntry.Instance?.frontInvinvElement;
            if (target == null || front == null || target == front) return Decline("selected destination is unavailable");
            // The original item callback has different effects inside the weighted counter.
            if (target.identifier == "backInv" || GameItem.FindParentWithIdentifier(target, "backInv") != null)
                return Decline("destination is inside the weighted counter");
            if (!CompatibleCallbacks(front, target))
            {
                string source = front.onSlotAddItemEndFunc == null ? "none" : string.Join(",", front.onSlotAddItemEndFunc.GetInvocationList().Select(d => d.Method.DeclaringType?.FullName + "." + d.Method.Name));
                string dest = target.onSlotAddItemEndFunc == null ? "none" : string.Join(",", target.onSlotAddItemEndFunc.GetInvocationList().Select(d => d.Method.DeclaringType?.FullName + "." + d.Method.Name));
                return Decline("unrecognized inventory callbacks (front=" + source + "; target=" + dest + ")");
            }
            if (!CompatiblePatches(out string patchReason)) return Decline("another patch may alter delivery: " + patchReason);
            if (StoreStation.Instance?.storeEventManager == null) return Decline("event state unavailable");
            // Event effects are determined solely from item types in this game build.
            // Affected receipts retain the original counter path, since leaving that
            // counter later also removes the event feature.
            if (StoreStation.Instance.storeEventManager.GetActiveEvents().Any(e => e == null || e.IsItemAffectedByThisEvent(item)))
                return Decline("active event affects the receipt");
            StoreClient client = PlayerStore.Instance.currentClientInstance?.GetClientBlueprint();
            if (client != null && (client.onItemAddedToClient != null || client.onItemAddedToCounters != null)) return Decline("customer has item callbacks");
            SlotMarker slot = GridPlacements.Empty(target, item);
            if (slot == null || slot.targetItem != null || slot.inventory != target || slot.numTransfer < 1) return Decline("destination has no verified empty placement");
            GridShape original = item.modifiedShape.Clone();
            try
            {
                // The original delivery sets ownership before inserting. It is already set on
                // this receipt, so the verified native call is a no-op on the item state.
                GeneralHelper.SetItemOwned(item, true);
                int accepted = slot.TryAcceptOnce(1);
                if (accepted == 1 && ReferenceEquals(item.parentInventory, target) && item.unitCount == 1)
                {
                    // The checked original front callback only forwards this call.
                    PlayerStore.Instance.AddedItemToClientArea(item);
                    StartingPerkContent.HandleGamblingAddictionItemAdded(item);
                    log("F24 routed the new appraisal receipt.");
                    return true;
                }
            }
            catch (Exception error)
            {
                log("F24 destination failed: " + error);
                if (item.parentInventory != null || element.IsDestroyed())
                {
                    ShopNotice.Show(ShopSettings.Text("f24.inspect"));
                    return true; // The instance changed hands: never submit it a second time.
                }
            }
            if (item.parentInventory != null || element.IsDestroyed() || item.unitCount != 1)
            {
                ShopNotice.Show(ShopSettings.Text("f24.inspect"));
                return true;
            }
            item.modifiedShape = original;
            return false;
        }

        private bool Decline(string reason) { log("F24 retained the original delivery: " + reason + "."); return false; }

        private static bool IsNativeItemCallback(GameItem item)
        {
            var calls = item.onSlotAddItemFunc?.GetInvocationList();
            if (calls == null || calls.Length != 1 || !ReferenceEquals(calls[0].Target, item)) return false;
            MethodInfo method = calls[0].Method;
            if (method.DeclaringType != typeof(GameItem) || method.Name != "<.ctor>b__177_0" || HasForeignPatches(method)) return false;
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            return il != null && il.Length == 236 && il[0] == 0x02 && il[1] == 0x28 && il[6] == 0x39 &&
                method.Module.ResolveMethod(BitConverter.ToInt32(il, 2)) == AccessTools.Method(typeof(GameItem), nameof(GameItem.IsRootedInBackInv));
        }

        private static bool CompatibleCallbacks(GameGridInventory front, GameGridInventory target)
        {
            if (front.onSlotAddItemFunc != null || front.onSlotAddItemLateFunc != null || front.onSlotAddItemEndFunc == null) return false;
            var calls = front.onSlotAddItemEndFunc.GetInvocationList();
            if (calls.Length != 1 || !IsNativeFrontCallback(calls[0].Method) || HasForeignPatches(calls[0].Method)) return false;
            if (target.onSlotAddItemFunc != null || target.onSlotAddItemLateFunc != null) return false;
            if (target.onSlotAddItemEndFunc == null) return true;
            if (target != EmporiumEntry.Instance.trashcanInvElement) return false;
            calls = target.onSlotAddItemEndFunc.GetInvocationList();
            return calls.Length == 1 && calls[0].Method.Name == "<Trashcan>b__1" &&
                calls[0].Method.DeclaringType?.DeclaringType == typeof(MiscItemDirectory) && !HasForeignPatches(calls[0].Method);
        }

        private static bool IsNativeFrontCallback(MethodInfo method)
        {
            if (method == null || method.Name != "<Start>b__184_21" || method.DeclaringType?.DeclaringType != typeof(EmporiumEntry)) return false;
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null || il.Length != 12 || il[0] != 0x28 || il[5] != 0x03 || il[6] != 0x6f || il[11] != 0x2a) return false;
            return method.Module.ResolveMethod(BitConverter.ToInt32(il, 1)) == typeof(PlayerStore).GetProperty(nameof(PlayerStore.Instance)).GetGetMethod() &&
                method.Module.ResolveMethod(BitConverter.ToInt32(il, 7)) == AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.AddedItemToClientArea));
        }

        private static bool CompatiblePatches(out string reason)
        {
            var methods = new MethodBase[] {
                AccessTools.Method(typeof(AppraisalServiceHelper), nameof(AppraisalServiceHelper.GiveAppraisalFee)),
                AccessTools.Method(typeof(PreBuiltItemHelper), nameof(PreBuiltItemHelper.CreatePaperWithText)),
                AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.AddDirectSellingItemToTable), new[] { typeof(GameItem), typeof(bool), typeof(bool), typeof(bool), typeof(int) }),
                AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.TryAddDirectSellingItemToTable), new[] { typeof(GameItem), typeof(bool), typeof(bool), typeof(bool), typeof(int) }),
                AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.AddedItemToClientArea)),
                AccessTools.Method(typeof(StartingPerkContent), nameof(StartingPerkContent.HandleGamblingAddictionItemAdded)),
                AccessTools.Method(typeof(GeneralHelper), nameof(GeneralHelper.SetItemOwned)),
                AccessTools.Method(typeof(StoreEvent), nameof(StoreEvent.IsItemAffectedByThisEvent)),
                AccessTools.Method(typeof(NegociationData), nameof(NegociationData.IsNegociationDataAffectItem)),
                AccessTools.Method(typeof(GameGridInventory), nameof(GameGridInventory.UncheckedAccept)),
                AccessTools.Method(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce))
            };
            foreach (MethodBase method in methods)
            {
                if (method == null) { reason = "missing method"; return false; }
                var patches = Harmony.GetPatchInfo(method);
                if (patches != null && patches.Owners.Any(owner => owner != "mod." + ModEntry.Id))
                { reason = method.DeclaringType?.Name + "." + method.Name + " [" + string.Join(",", patches.Owners) + "]"; return false; }
            }
            reason = null; return true;
        }

        private static bool HasForeignPatches(MethodBase method)
        {
            var patches = Harmony.GetPatchInfo(method);
            return patches != null && patches.Owners.Any(owner => owner != "mod." + ModEntry.Id);
        }

        [HarmonyPatch(typeof(AppraisalServiceHelper), nameof(AppraisalServiceHelper.GiveAppraisalFee))]
        private static class FeeScope
        {
            private static void Prefix(out Scope __state)
            {
                __state = null;
                if (active == null || !active.enabled.Value || active.destination.Value == 0) return;
                __state = new Scope { Parent = scope }; scope = __state;
            }
            private static void Finalizer(Scope __state, Exception __exception)
            {
                if (__state == null) return;
                if (__exception == null && !__state.Claimed && __state.Created > 0 && active != null)
                {
                    active.log("F24 receipt creation or delivery was ambiguous; retained the original path.");
                    ShopNotice.Show(ShopSettings.Text("f24.inspect"));
                }
                if (ReferenceEquals(scope, __state)) scope = __state?.Parent;
                else scope = null;
            }
        }

        [HarmonyPatch(typeof(PreBuiltItemHelper), nameof(PreBuiltItemHelper.CreatePaperWithText))]
        private static class CreatedSlip
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(GameItem __result)
            {
                if (scope == null) return;
                scope.Created++;
                if (scope.Created == 1) scope.Receipt = __result;
            }
        }

        [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.AddDirectSellingItemToTable),
            new[] { typeof(GameItem), typeof(bool), typeof(bool), typeof(bool), typeof(int) })]
        private static class DeliverSlip
        {
            private static bool Prefix(GameItem __0, bool __1, bool __2, bool __3, int __4, out Delivery __state)
            {
                __state = null;
                var owner = active;
                var current = scope;
                if (owner == null || current == null || current.Claimed || current.Created != 1 || !ReferenceEquals(current.Receipt, __0) ||
                    !owner.enabled.Value || owner.destination.Value == 0) return true;
                current.Claimed = true;
                __state = new Delivery();
                try
                {
                    if (__1 && !__2 && !__3 && __4 == 0 && owner.Route(__0))
                    { __state.Routed = true; return false; }
                }
                catch (Exception error) { owner.log("F24 could not prepare receipt routing: " + error); }
                __state.Fallback = true;
                return true;
            }

            private static void Postfix(GameItem __0, Delivery __state)
            {
                if (__state == null || !__state.Fallback || active == null) return;
                bool atFront = __0 != null && EmporiumEntry.Instance != null && ReferenceEquals(__0.parentInventory, EmporiumEntry.Instance.frontInvinvElement);
                bool destroyed = __0 is GameItemElement element && element.IsDestroyed();
                ShopNotice.Show(ShopSettings.Text(atFront ? "f24.fallback" : destroyed ? "f24.front_full" : "f24.inspect"));
                active.log(atFront ? "F24 destination unavailable or unsupported; used the original counter." :
                    destroyed ? "F24 fell back to the original counter; the receipt could not be placed." :
                    "F24 could not verify the receipt's final location after the original delivery.");
            }
        }

        private void Close() { window?.Close(); window = null; }
        public void Dispose()
        {
            if (!started) return;
            if (started) { ModHook.OnGameLoadedInit -= Close; ModHook.OnLeavingStoreEarly -= Close; }
            Close(); if (active == this) active = null;
            started = false;
        }
    }
}
