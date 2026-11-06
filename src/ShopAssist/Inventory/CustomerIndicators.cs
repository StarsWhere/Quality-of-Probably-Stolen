using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    internal sealed class CustomerIndicators : IDisposable
    {
        private sealed class Observation
        {
            internal HashSet<string> Types = new HashSet<string>(StringComparer.Ordinal);
            internal PublicRisk Risk;
        }

        private static CustomerIndicators current;
        private readonly ShopSettings settings;
        private readonly Action<string> log;
        private readonly ConfigEntry<bool> demandEnabled, inspectionEnabled, stolenEnabled, contrabandEnabled, inspectUnseen;
        private readonly ViewOutlines outlines = new ViewOutlines();
        private readonly Dictionary<GameItem, Observation> observations = new Dictionary<GameItem, Observation>();
        private readonly KnownRiskLedger<GameItem, GameInventory> risks = new KnownRiskLedger<GameItem, GameInventory>();
        private readonly HashSet<string> interests = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> restrictions = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<GameItemElement> visible = new HashSet<GameItemElement>();
        private readonly HashSet<GameItemElement> drawn = new HashSet<GameItemElement>();
        private readonly List<TreeNodeRender> scratchNodes = new List<TreeNodeRender>();
        private StoreClientInstance customer;
        private GameItemElement pendingTooltipItem;
        private RichText pendingTooltip;
        private int pendingTooltipFrame;
        private bool started, dirty = true;
        private string lastError;

        internal CustomerIndicators(ShopSettings settings, Action<string> log)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            demandEnabled = settings.Toggle("f03.enabled", "f03.tip", true);
            inspectionEnabled = settings.Toggle("f04.enabled", "f04.tip", true);
            stolenEnabled = settings.Toggle("f04.stolen", "f04.stolen.tip", true);
            contrabandEnabled = settings.Toggle("f04.contraband", "f04.contraband.tip", true);
            inspectUnseen = settings.Toggle("f04.unseen", "f04.unseen.tip");
        }

        internal event Action Changed;
        internal static event Action<GameItemElement, string> PublicTooltipShown;
        internal StoreClientInstance Customer => CurrentCustomer();
        internal bool HasPublicDemand => CurrentCustomer() != null && ReferenceEquals(customer, CurrentCustomer()) &&
            interests.Count != 0;

        public void Start()
        {
            if (started) return;
            if (current != null && !ReferenceEquals(current, this))
                throw new InvalidOperationException("Customer indicators are already active.");
            current = this;
            started = true;
            dirty = true;
            settings.Changed += Invalidate;
            VisibleInventoryViews.Changed += Invalidate;
            VisibleInventoryViews.ItemChanged += ItemViewChanged;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        private static StoreClientInstance CurrentCustomer()
        {
            if (!ShopInventory.InStore || !PlayerStore.Instance.isClientArrived) return null;
            StoreClientInstance instance = PlayerStore.Instance.currentClientInstance;
            return instance?.GetClientBlueprint() is StoreClient client && !client.isBusinessComplete ? instance : null;
        }

        public void Tick()
        {
            if (!started) return;
            outlines.Sync();
            StoreClientInstance active = CurrentCustomer();
            if (!ReferenceEquals(customer, active))
            {
                customer = active;
                interests.Clear();
                restrictions.Clear();
                dirty = true;
                NotifyChanged();
            }
            if (!dirty || WindowsHandler.current == null || WindowsHandler.current.visibleWindows == null ||
                RenderHandler.current == null) return;
            try
            {
                Refresh();
                dirty = false;
                lastError = null;
            }
            catch (Exception error)
            {
                outlines.Dispose();
                dirty = false;
                string message = error.ToString();
                if (message != lastError) log("F03/F04 display failed: " + message);
                lastError = message;
            }
        }

        private void Refresh()
        {
            visible.Clear();
            drawn.Clear();
            if (!ShopInventory.InStore)
            {
                outlines.KeepOnly(drawn);
                return;
            }
            var locations = new Dictionary<GameInventory, ShopInventory.Location>();
            foreach (ShopInventory.Location location in ShopInventory.OwnedLocations(true))
                if (location.Inventory != null) locations[location.Inventory] = location;
            foreach (PixelWindow window in WindowsHandler.current.visibleWindows)
                VisibleInventoryViews.Collect(window, scratchNodes, visible);

            bool inspector = IsInspector();
            var extra = inspector && inspectionEnabled.Value && inspectUnseen.Value ? RiskInAccessibleBags() : null;
            foreach (GameItemElement item in visible)
            {
                if (!locations.TryGetValue(item.parentInventory, out ShopInventory.Location location) ||
                    location.IsMachine || !GeneralHelper.IsItemEffectivelyOwned(item) ||
                    item.background == null || item.background.image == null || !item.background.image.enabled)
                    continue;
                PublicRisk direct = KnownRisk(item);
                if (direct != PublicRisk.None && ShopInventory.Owned(item))
                    RememberRisk(item, direct, locations);
            }

            var containedRisks = inspector && inspectionEnabled.Value ? risks.InsideSnapshot() : null;
            foreach (GameItemElement item in visible)
            {
                if (!locations.TryGetValue(item.parentInventory, out ShopInventory.Location location) ||
                    location.IsMachine || !GeneralHelper.IsItemEffectivelyOwned(item) ||
                    item.background == null || item.background.image == null || !item.background.image.enabled)
                    continue;
                ViewOutlineKind? kind = null;
                if (inspector && inspectionEnabled.Value && ShopInventory.Owned(item))
                {
                    PublicRisk direct = SelectedRisk(KnownRisk(item));
                    containedRisks.TryGetValue(item, out PublicRisk knownInside);
                    PublicRisk contained = SelectedRisk(knownInside);
                    if (extra != null)
                    {
                        direct |= SelectedRisk(PhysicalRisk(item));
                        if (extra.TryGetValue(item, out var hidden)) contained |= SelectedRisk(hidden);
                    }
                    if ((direct & PublicRisk.Contraband) != 0) kind = ViewOutlineKind.Contraband;
                    else if ((direct & PublicRisk.Stolen) != 0) kind = ViewOutlineKind.Stolen;
                    else if ((contained & PublicRisk.Contraband) != 0) kind = ViewOutlineKind.ContainsContraband;
                    else if ((contained & PublicRisk.Stolen) != 0) kind = ViewOutlineKind.ContainsStolen;
                }
                if (kind == null && demandEnabled.Value && !inspector && Match(item) == PublicMatch.Match)
                    kind = ViewOutlineKind.Demand;
                if (kind.HasValue)
                {
                    outlines.Show(item, kind.Value);
                    drawn.Add(item);
                }
            }
            outlines.KeepOnly(drawn);
        }

        private bool IsInspector()
        {
            StoreClientInstance active = CurrentCustomer();
            return active != null && active.GetClientBlueprint().clientIntent == StoreClient.ClientIntent.INSPECTION;
        }

        private PublicRisk SelectedRisk(PublicRisk value)
        {
            if (!stolenEnabled.Value) value &= ~PublicRisk.Stolen;
            if (!contrabandEnabled.Value) value &= ~PublicRisk.Contraband;
            return value;
        }

        internal PublicMatch Match(GameItem item)
        {
            if (!HasPublicDemand || !(item is GameItemElement element)) return PublicMatch.Unknown;
            if (restrictions.Contains("CONTRABAND") && (KnownRisk(element) & PublicRisk.Contraband) != 0)
                return PublicMatch.Unknown;
            ICollection<string> types = KnownTypes(element);
            return PublicClues.Match(interests, restrictions, types);
        }

        internal ICollection<string> KnownTypes(GameItemElement item)
        {
            if (HasOrdinaryTooltip(item)) return item.GetGameItemType();
            return observations.TryGetValue(item, out Observation observation) ? observation.Types : null;
        }

        internal static ICollection<string> PublicTypes(GameItemElement item) =>
            current != null && current.started ? current.KnownTypes(item) : null;

        internal static bool HasOrdinaryTooltip(GameItemElement item)
        {
            // Derived game/mod items can override GetTooltip entirely.
            return item != null && item.GetType() == typeof(GameItemElement) && item.HasTooltip() && item.tooltipRichTextFunc == null;
        }

        internal PublicRisk KnownRisk(GameItemElement item)
        {
            if (!HasOrdinaryTooltip(item))
                return observations.TryGetValue(item, out Observation observation) ? observation.Risk : PublicRisk.None;
            // These are exactly the fields the game's ordinary tooltip displays.
            return PhysicalRisk(item);
        }

        private static PublicRisk PhysicalRisk(GameItem item)
        {
            PublicRisk risk = item.IsTag(StolenHelper.STOLEN_TAG) ? PublicRisk.Stolen : PublicRisk.None;
            if (item.IsTag(ContrabandHelper.CONTRABAND_ITEM_TAG))
            {
                string level = item.GetTagReadonly(ContrabandHelper.CONTRABAND_LEVEL).GetString();
                if (level == ContrabandHelper.CONTRABAND_LEVEL_LOW || level == ContrabandHelper.CONTRABAND_LEVEL_MID ||
                    level == ContrabandHelper.CONTRABAND_LEVEL_HIGH || level == ContrabandHelper.CONTRABAND_LEVEL_CRITICAL)
                    risk |= PublicRisk.Contraband;
            }
            return risk;
        }

        private Dictionary<GameItem, PublicRisk> RiskInAccessibleBags()
        {
            var result = new Dictionary<GameItem, PublicRisk>();
            foreach (var location in ShopInventory.OwnedLocations(false))
            {
                var path = location.Path;
                if (!location.Ordinary || path.Length == 0 || Array.Exists(path, i => !ShopInventory.SafeContainerForPeek(i))) continue;
                GameItemElement closed = null;
                foreach (var parent in path)
                    if (parent is GameItemElement el && visible.Contains(el) && !parent.contentWindow.IsVisible()) { closed = el; break; }
                if (closed == null) continue;
                result.TryGetValue(closed, out var found);
                foreach (var child in location.Inventory.childItems)
                    if (ShopInventory.Owned(child)) found |= PhysicalRisk(child);
                if (found != PublicRisk.None) result[closed] = found;
            }
            return result;
        }

        private void RememberRisk(GameItemElement item, PublicRisk risk,
            Dictionary<GameInventory, ShopInventory.Location> locations)
        {
            var inventories = new List<GameInventory>();
            var ancestors = new List<GameItem>();
            var seen = new HashSet<GameItem>();
            GameItem cursor = item;
            while (cursor?.parentInventory != null && locations.TryGetValue(cursor.parentInventory, out ShopInventory.Location location))
            {
                inventories.Add(location.Inventory);
                if (location.Owner == null || !location.Ordinary || location.IsMachine ||
                    !(location.Owner is GameItemElement parent) || !seen.Add(parent) ||
                    !VisibleInventoryViews.Visible(parent)) break;
                ancestors.Add(parent);
                cursor = parent;
            }
            risks.Remember(item, risk, ancestors, inventories);
        }

        internal Dictionary<GameItem, PublicRisk> KnownRisksInside() => risks.InsideSnapshot();

        private void Invalidate() => dirty = true;
        private void ItemViewChanged(GameItemElement item)
        {
            dirty = true;
            NotifyChanged();
        }
        private void SceneChanged(Scene oldScene, Scene newScene) => Reset();

        private void Reset()
        {
            observations.Clear();
            risks.Clear();
            interests.Clear();
            restrictions.Clear();
            customer = null;
            pendingTooltipItem = null;
            pendingTooltip = null;
            outlines.Dispose();
            dirty = true;
            NotifyChanged();
        }

        internal static void DemandDisplayed(StoreClient client)
        {
            CustomerIndicators owner = current;
            if (owner == null || !owner.started || client == null || !client.IsClientBuying() ||
                NegociationUIManager.Instance == null || !NegociationUIManager.Instance.IsOpen) return;
            StoreClientInstance active = CurrentCustomer();
            if (active == null || !ReferenceEquals(active.GetClientBlueprint(), client)) return;
            owner.customer = active;
            owner.interests.Clear();
            owner.restrictions.Clear();
            if (client.clientBuyingTagList != null)
                foreach (string type in client.clientBuyingTagList)
                    if (TypeHelper.ALL_TYPES_LIST.Contains(type)) owner.interests.Add(type);
            if (client.clientBlackTagList != null)
                foreach (string type in client.clientBlackTagList)
                    if (TypeHelper.ALL_TYPES_LIST.Contains(type)) owner.restrictions.Add(type);
            if (client.noAcceptingContraband) owner.restrictions.Add("CONTRABAND");
            owner.dirty = true;
            owner.NotifyChanged();
        }

        internal static void TooltipBuilt(GameItemElement item, RichText text)
        {
            if (current == null || !current.started) return;
            current.pendingTooltipItem = item;
            current.pendingTooltip = text;
            current.pendingTooltipFrame = Time.frameCount;
        }

        internal static void TooltipDisplayed(RichText text)
        {
            CustomerIndicators owner = current;
            if (owner == null || !owner.started || owner.pendingTooltipItem == null || text == null ||
                owner.pendingTooltipFrame != Time.frameCount || !ReferenceEquals(owner.pendingTooltip, text) ||
                OverlayHandler.current == null || !VisibleInventoryViews.Visible(owner.pendingTooltipItem) ||
                OverlayHandler.current.GetTooltipWindow() == null || !OverlayHandler.current.GetTooltipWindow().IsVisible()) return;
            GameItemElement item = owner.pendingTooltipItem;
            owner.pendingTooltipItem = null;
            owner.pendingTooltip = null;
            PublicTooltipShown?.Invoke(item, text.nakedText ?? "");
            if (HasOrdinaryTooltip(item)) return;
            string displayed = text.nakedText ?? "";
            var observation = new Observation();
            foreach (string type in TypeHelper.ALL_TYPES_LIST)
                if (PublicClues.ContainsShownType(displayed, TypeHelper.GetTypeDisplayName(type))) observation.Types.Add(type);
            if (StolenTooltipShown(displayed)) observation.Risk |= PublicRisk.Stolen;
            if (ContrabandTooltipShown(displayed)) observation.Risk |= PublicRisk.Contraband;
            if (owner.observations.TryGetValue(item, out Observation prior) && prior.Risk == observation.Risk &&
                prior.Types.SetEquals(observation.Types)) return;
            owner.risks.InvalidateItem(item);
            owner.observations[item] = observation;
            owner.dirty = true;
            owner.NotifyChanged();
        }

        private static bool ContrabandTooltipShown(string displayed)
        {
            foreach (string key in new[] { "mech_contraband_low", "mech_contraband_mid", "mech_contraband_high", "mech_contraband_critical" })
            {
                string word = LocHelper.GetLocalizedMechanic(key);
                if (!string.IsNullOrEmpty(word) && displayed.IndexOf(word, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static bool StolenTooltipShown(string displayed)
        {
            string first = LocHelper.GetLocalizedMechanic("mech_stolen_tooltip", 51937);
            string second = LocHelper.GetLocalizedMechanic("mech_stolen_tooltip", 61938);
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second) || first == second) return false;
            int prefix = 0;
            while (prefix < first.Length && prefix < second.Length && first[prefix] == second[prefix]) prefix++;
            int suffix = 0;
            while (suffix < first.Length - prefix && suffix < second.Length - prefix &&
                first[first.Length - 1 - suffix] == second[second.Length - 1 - suffix]) suffix++;
            if (prefix + suffix < 2) return false;
            string beginning = first.Substring(0, prefix);
            string end = suffix == 0 ? "" : first.Substring(first.Length - suffix);
            string pattern = Regex.Escape(beginning) + @"\s*\d+\s*" + Regex.Escape(end);
            foreach (string line in displayed.Split('\n'))
                if (Regex.IsMatch(line, pattern, RegexOptions.CultureInvariant)) return true;
            return false;
        }

        internal static void InventoryChanging(GameInventory inventory, GameItem item)
        {
            CustomerIndicators owner = current;
            if (owner == null || !owner.started) return;
            if (item != null) owner.observations.Remove(item);
            owner.risks.InvalidateInventory(inventory, item);
            owner.dirty = true;
            owner.NotifyChanged();
        }

        internal static void ItemStateChanging(GameItem item)
        {
            CustomerIndicators owner = current;
            if (owner == null || !owner.started || item == null) return;
            bool changed = owner.observations.Remove(item);
            changed |= owner.risks.InvalidateItem(item);
            if (changed)
            {
                owner.dirty = true;
                owner.NotifyChanged();
            }
        }

        private void NotifyChanged()
        {
            foreach (Delegate handler in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { ((Action)handler)(); }
                catch (Exception error) { log("Public inventory observer failed: " + error); }
            }
        }

        public void Dispose()
        {
            if (!started) return;
            started = false;
            if (ReferenceEquals(current, this)) current = null;
            settings.Changed -= Invalidate;
            VisibleInventoryViews.Changed -= Invalidate;
            VisibleInventoryViews.ItemChanged -= ItemViewChanged;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
        }
    }

    [HarmonyPatch(typeof(NegociationUIManager), "BuildInfoBlock")]
    internal static class PublicDemandDisplayedPatch
    {
        private static void Postfix(StoreClient storeClient) => CustomerIndicators.DemandDisplayed(storeClient);
    }

    [HarmonyPatch(typeof(GameItemElement), nameof(GameItemElement.GetTooltip))]
    internal static class PublicTooltipBuiltPatch
    {
        private static void Postfix(GameItemElement __instance, RichText __result) => CustomerIndicators.TooltipBuilt(__instance, __result);
    }

    [HarmonyPatch(typeof(GameItemElement), nameof(GameItemElement.GetTooltipAdvanced))]
    internal static class PublicAdvancedTooltipBuiltPatch
    {
        private static void Postfix(GameItemElement __instance, RichText __result) => CustomerIndicators.TooltipBuilt(__instance, __result);
    }

    [HarmonyPatch(typeof(OverlayHandler), nameof(OverlayHandler.ShowTooltip), new Type[] { typeof(Vector2), typeof(Vector2), typeof(RichText), typeof(int) })]
    internal static class PublicTooltipDisplayedPatch
    {
        private static void Postfix(RichText richText) => CustomerIndicators.TooltipDisplayed(richText);
    }

    [HarmonyPatch(typeof(GameItem), nameof(GameItem.ValidateShapeState))]
    internal static class PublicItemStateChangingPatch
    {
        private static void Prefix(GameItem __instance) => CustomerIndicators.ItemStateChanging(__instance);
    }

    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.UncheckedAccept))]
    internal static class PublicGridAcceptPatch
    {
        private static void Prefix(GameGridInventory __instance, GameItem item) => CustomerIndicators.InventoryChanging(__instance, item);
    }

    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.Expel))]
    internal static class PublicGridExpelPatch
    {
        private static void Prefix(GameGridInventory __instance, GameItem item) => CustomerIndicators.InventoryChanging(__instance, item);
    }

    [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.UncheckedAccept))]
    internal static class PublicSlotAcceptPatch
    {
        private static void Prefix(GameSlotInventory __instance, GameItem item) => CustomerIndicators.InventoryChanging(__instance, item);
    }

    [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.Expel))]
    internal static class PublicSlotExpelPatch
    {
        private static void Prefix(GameSlotInventory __instance, GameItem item) => CustomerIndicators.InventoryChanging(__instance, item);
    }
}
