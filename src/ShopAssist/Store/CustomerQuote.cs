using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ShopAssist
{
    // Estimates the value the native counter would calculate without adding
    // features to the real item or invoking the customer's callbacks.
    internal static class CustomerQuote
    {
        private static readonly MethodInfo Compose = AccessTools.Method(typeof(GameItem), "ComposeStagedValue",
            new[] { typeof(long), typeof(long), typeof(long), typeof(double), typeof(int), typeof(int) });
        private static readonly HashSet<string> ReportedErrors = new HashSet<string>(StringComparer.Ordinal);

        private sealed class Stages
        {
            internal long Innate, Market, Inherited;
            internal double Final = 1;
            internal readonly HashSet<string> Ids = new HashSet<string>(StringComparer.Ordinal);

            internal void Add(ItemFeature feature, bool existing = false)
            {
                if (feature == null || feature.identifier == null) throw new InvalidOperationException("Invalid item feature.");
                if (!Ids.Add(feature.identifier) && !existing) return;
                if (feature.isDisabled) return;
                int value = feature.GetCurrentValueMod();
                switch (feature.GetValueStage())
                {
                    case ItemFeature.ValueStage.Market:
                        Market += value;
                        if (feature.identifier != "retailMarkUp" &&
                            (feature.featureType == ItemFeature.FeatureType.Temporary ||
                             feature.featureType == ItemFeature.FeatureType.TemporaryBuying ||
                             feature.featureType == ItemFeature.FeatureType.TemporarySelling ||
                             feature.featureType == ItemFeature.FeatureType.ClientTemporary)) Inherited += value;
                        break;
                    case ItemFeature.ValueStage.Final:
                        Final *= Math.Max(0, 1 + value / 100d);
                        break;
                    default:
                        Innate += value;
                        break;
                }
            }
        }

        internal static bool TryGet(GameItem item, StoreClient client, out int value, out bool onCounter)
        {
            value = 0; onCounter = false;
            if (item == null || client == null || !PlayerStore.HasInstance || !ShopInventory.InStore ||
                PlayerStore.Instance.currentClientInstance?.GetClientBlueprint() != client || item.unitCount <= 0) return false;
            try
            {
                long result;
                if (ReferenceEquals(item.parentInventory, EmporiumEntry.Instance?.backInvinvElement))
                {
                    result = item.GetNegociatedValue();
                    onCounter = true;
                }
                else
                {
                    if (Compose == null || item.itemFeatures == null || StoreStation.Instance?.storeEventManager == null) return false;
                    result = Estimate(item, client);
                }
                if (result < 0 || result > int.MaxValue) return false;
                value = (int)result;
                return true;
            }
            catch (Exception error)
            {
                string detail = error.GetType().Name + ": " + error.Message;
                if (ReportedErrors.Count < 16 && ReportedErrors.Add(detail))
                    UnityEngine.Debug.LogWarning("[ShopAssist] Customer quote unavailable: " + detail);
                return false;
            }
        }

        private static long Estimate(GameItem item, StoreClient client)
        {
            var stages = new Stages();
            foreach (ItemFeature feature in item.itemFeatures) stages.Add(feature, true);
            bool owned = GeneralHelper.IsItemOwned(item);
            bool selling = client.clientIntent == StoreClient.ClientIntent.BUY || client.clientIntent == StoreClient.ClientIntent.SELLNBUY;
            bool markup = owned && selling && PlayerStore.Instance.IsCurrentClientAcceptMarkup();
            if (owned && selling)
            {
                if (markup) stages.Add(ItemFeatureList.RetailMarkUp());
                if (client.perfumeInfluence == 1) stages.Add(ItemFeatureList.NeuroActivePerfumeSelling());
                else if (client.perfumeInfluence == 2) stages.Add(ItemFeatureList.PheromonePerfumeSelling());
                if (HealthData.HasAnyAddiction() && PlayerStore.Instance.healthData.GetTotalAddictionPenalty() != 0)
                    stages.Add(ItemFeatureList.AddictionSellPenalty());
                if (StartingPerk.IsAwfulNegotiator()) stages.Add(ItemFeatureList.AwfulNegotiatorSellPenalty());
                if (client.clientFaction == "FACTION_UPPER_LEVEL" && StoreReputation.IsPerkUnlocked("UL_TIPS"))
                    stages.Add(ItemFeatureList.Pourboire());
                AddContraband(item, stages);
                if (item.IsGameItemType("RESTRICTED_1")) stages.Add(ItemFeatureList.RestrictedLowMarkUp());
                else if (item.IsGameItemType("RESTRICTED_2")) stages.Add(ItemFeatureList.RestrictedMidMarkUp());
                else if (item.IsGameItemType("RESTRICTED_3")) stages.Add(ItemFeatureList.RestrictedHighMarkUp());
            }
            if (owned) AddKnownCustomerFeatures(client, stages);
            AddEvents(item, stages);

            int misc = unchecked((int)item.lateUnitValue);
            if (!item.IsTag("IGNORE_CHILD_VALUE"))
                foreach (GameItem child in item.FindAllChildItems())
                {
                    if (child == null || child.IsTag("ITEM_BEHIND_LOCK_TAG")) continue;
                    var childStages = new Stages();
                    if (child.itemFeatures != null) foreach (ItemFeature feature in child.itemFeatures) childStages.Add(feature, true);
                    if (owned && selling) AddContraband(child, childStages);
                    long additional = childStages.Market - ExistingMarket(child);
                    bool childMarkup = markup && GeneralHelper.IsItemEffectivelyOwned(child);
                    long childValue = child.GetCurrentValue(true, false, true, childMarkup, stages.Inherited + additional);
                    misc = unchecked(misc + (int)childValue);
                }
            long raw = (long)Compose.Invoke(null, new object[] { item.unitValue + item.GetComplexeItemValue(), stages.Innate,
                stages.Market, stages.Final, misc, WaterHelper.GetContainerPrice(item) });
            return GameItem.CheckValueSafe(raw);
        }

        private static long ExistingMarket(GameItem item)
        {
            long result = 0;
            if (item.itemFeatures == null) return 0;
            foreach (ItemFeature feature in item.itemFeatures)
                if (feature != null && !feature.isDisabled && feature.GetValueStage() == ItemFeature.ValueStage.Market)
                    result += feature.GetCurrentValueMod();
            return result;
        }

        private static void AddContraband(GameItem item, Stages stages)
        {
            if (!item.IsTag(ContrabandHelper.CONTRABAND_ITEM_TAG)) return;
            string level = item.GetTagReadonly(ContrabandHelper.CONTRABAND_LEVEL).GetString();
            if (level == ContrabandHelper.CONTRABAND_LEVEL_LOW) stages.Add(ItemFeatureList.ContrabandLowMarkUp());
            else if (level == ContrabandHelper.CONTRABAND_LEVEL_MID) stages.Add(ItemFeatureList.ContrabandMidMarkUp());
            else if (level == ContrabandHelper.CONTRABAND_LEVEL_HIGH) stages.Add(ItemFeatureList.ContrabandHighMarkUp());
            else if (level == ContrabandHelper.CONTRABAND_LEVEL_CRITICAL) stages.Add(ItemFeatureList.ContrabandCriticalMarkUp());
        }

        private static void AddEvents(GameItem item, Stages stages)
        {
            var types = item.GetGameItemType();
            foreach (StoreEvent current in StoreStation.Instance.storeEventManager.GetActiveEvents())
            {
                if (current?.negociationDatas == null) continue;
                foreach (NegociationData line in current.negociationDatas)
                {
                    if (line == null || !types.Contains(line.itemType) || stages.Ids.Contains(line.identifier)) continue;
                    stages.Add(ItemFeature.CreateFeatureFromNegociationData(line));
                }
            }
        }

        private static void AddKnownCustomerFeatures(StoreClient client, Stages stages)
        {
            if (client.onItemAddedToWeighted == null) return;
            foreach (Delegate callback in client.onItemAddedToWeighted.GetInvocationList())
            {
                MethodInfo method = callback.Method;
                if (method.DeclaringType?.Assembly != typeof(StoreClient).Assembly) continue;
                string owner = method.DeclaringType.FullName ?? "";
                if (owner == "StoreClient+<>c" && method.Name.StartsWith("<SetVariety>", StringComparison.Ordinal))
                    stages.Add(ItemFeatureList.VarietyBuyerBonus());
                else if (owner == "StoreClient+<>c" && method.Name.StartsWith("<SetTourist>", StringComparison.Ordinal))
                    stages.Add(ItemFeatureList.TouristTax());
                else if (owner.StartsWith("BargainUIManager+<>c__DisplayClass142_", StringComparison.Ordinal) &&
                    method.Name.StartsWith("<ApplyOwnedItemFeature>", StringComparison.Ordinal))
                {
                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    string id = method.DeclaringType.GetField("featureId", flags)?.GetValue(callback.Target) as string;
                    var creator = method.DeclaringType.GetField("creator", flags)?.GetValue(callback.Target) as Delegate;
                    if (creator?.Method.DeclaringType?.Assembly != typeof(StoreClient).Assembly) continue;
                    if (id == "bargainMarkup" && creator.Method.Name.StartsWith("<OfferMarkup>", StringComparison.Ordinal) &&
                        creator.Target != null && creator.Target.GetType().GetField("percent", flags)?.GetValue(creator.Target) is int percent)
                        stages.Add(ItemFeatureList.BargainMarkup(percent));
                    else if (id == "blackmailMarkup" && creator.Method.Name.StartsWith("<Blackmail>", StringComparison.Ordinal))
                        stages.Add(ItemFeatureList.BlackmailMarkup(20));
                    else if (id == "threatenMarkup" && creator.Method.Name.StartsWith("<ThreatenAug>", StringComparison.Ordinal))
                        stages.Add(ItemFeatureList.ThreatenMarkup(20));
                }
            }
        }
    }
}
