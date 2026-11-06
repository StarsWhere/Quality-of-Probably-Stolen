using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ShopAssist
{
    internal sealed class PurchaseCounter : IDisposable
    {
        private static PurchaseCounter active;
        private static bool compatible;
        private readonly ConfigEntry<bool> enabled, wholesale;
        private readonly Action<string> log;
        private StoreClientInstance originalWholesale;

        internal PurchaseCounter(ShopSettings settings, Action<string> log)
        {
            this.log = log;
            enabled = settings.Toggle("f01.enabled", "f01.tip");
            wholesale = settings.Toggle("f01.wholesale", "f01.wholesale.tip");
        }

        internal void Start()
        {
            active = this;
            if (!compatible) log("F01: indoor purchase site not recognized; native purchase routing retained.");
        }

        public void Dispose() { originalWholesale = null; if (ReferenceEquals(active, this)) active = null; }

        [HarmonyPatch(typeof(NegociationUIManager), nameof(NegociationUIManager.OnWholesaleClick))]
        private static class WholesalePurchase
        {
            private static bool Prefix(NegociationUIManager __instance, out List<GameItem> __state)
            {
                __state = null;
                var a = active;
                if (a == null || !a.wholesale.Value || !ShopInventory.InStore || NegociationUIManager.IsTradeLocked()) return true;
                var customer = PlayerStore.Instance.currentClientInstance;
                if (customer == null || customer.GetClientBlueprint()?.isOfferWholesale != true || a.originalWholesale == customer) return true;
                var entry = EmporiumEntry.Instance;
                var paid = entry.GetAllNonOwnedItem();
                if (paid.Count == 0 || paid.Any(i => i.IsTag(ScratcherHelper.SCRATCHER_TAG) || i.IsTag(LotteryHelper.LOTTERY_TICKET_TAG))) return true;
                // Vanilla SwapInv already moves the whole front row toward the back counter.
                // Check the whole row before payment, including owned goods that may take up a slot.
                var reserved = new List<GridShape>();
                foreach (var item in entry.frontInvinvElement.childItems)
                {
                    var marker = GridPlacements.Empty(entry.backInvinvElement, item, reserved);
                    if (marker != null) { reserved.Add(marker.itemGridShape.Clone()); continue; }
                    var prompt = GenericConfirmationUIManager.Instance;
                    if (prompt == null) { ShopNotice.Show(ShopSettings.Text("f01.wholesaleFull")); return false; }
                    prompt.OpenPanel(ShopSettings.Text("f01.wholesaleFull"), ShopSettings.Text("f01.originalWholesale"),
                        ShopSettings.Text("f37.free"), () =>
                        {
                            if (!ShopInventory.InStore || PlayerStore.Instance.currentClientInstance != customer ||
                                customer.GetClientBlueprint()?.isOfferWholesale != true) return;
                            a.originalWholesale = customer;
                            try { __instance.OnWholesaleClick(); } finally { a.originalWholesale = null; }
                        });
                    return false;
                }
                __state = paid; return true;
            }
            private static void Postfix(List<GameItem> __state)
            {
                if (__state == null || active == null || !ShopInventory.InStore) return;
                var entry = EmporiumEntry.Instance;
                int left = 0;
                foreach (var item in __state)
                {
                    if (!ShopInventory.Owned(item) || item.parentInventory != entry.frontInvinvElement) continue;
                    // Only attempt a verified, empty back-counter placement for an already paid item.
                    var marker = GridPlacements.Empty(entry.backInvinvElement, item);
                    if (marker == null) { left++; continue; }
                    var home = item.modifiedShape.Clone();
                    if (marker.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == entry.backInvinvElement) continue;
                    if (item.parentInventory == null) GridPlacements.Exact(entry.frontInvinvElement, item, home)?.TryAcceptOnce(item.unitCount);
                    left++;
                }
                if (left > 0) ShopNotice.Show(ShopSettings.Format("f01.wholesaleLeft", left));
            }
        }

        // Only the indoor main-stock transfer is redirected. Ownership, money, the outside
        // branch and the game's final weighted-counter callback stay in the original method.
        [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.BuyItem))]
        private static class IndoorPurchase
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var mainStock = AccessTools.PropertyGetter(typeof(EmporiumEntry), nameof(EmporiumEntry.invElement));
                var accept = AccessTools.Method(typeof(GraphUtils), nameof(GraphUtils.TryAcceptAll),
                    new[] { typeof(GraphNodeStorage), typeof(GameItem), typeof(int) });
                int site = -1;
                compatible = false;
                for (int i = 3; i < code.Count; i++)
                {
                    if (!code[i].Calls(accept) || code[i - 1].opcode != OpCodes.Ldc_I4_M1 ||
                        code[i - 2].opcode != OpCodes.Ldarg_1 || !code[i - 3].Calls(mainStock)) continue;
                    if (site >= 0) return code;
                    site = i;
                }
                if (site >= 0)
                {
                    code[site].operand = AccessTools.Method(typeof(PurchaseCounter), nameof(PlaceBoughtItem));
                    compatible = true;
                }
                return code;
            }
        }

        private static int PlaceBoughtItem(GraphNodeStorage target, GameItem item, int amount)
        {
            var entry = EmporiumEntry.Instance;
            if (active != null && active.enabled.Value && ShopInventory.InStore && entry != null &&
                item != null && ReferenceEquals(item.parentInventory, entry.backInvinvElement) &&
                ReferenceEquals(target, entry.invElement) && GeneralHelper.IsItemEffectivelyOwned(item) &&
                !item.IsTag(ScratcherHelper.SCRATCHER_TAG) && !item.IsTag(LotteryHelper.LOTTERY_TICKET_TAG))
            {
                // Keep the exact instance where it is. The original caller discards the remainder.
                return amount < 0 ? item.unitCount : amount;
            }
            return GraphUtils.TryAcceptAll(target, item, amount);
        }
    }
}
