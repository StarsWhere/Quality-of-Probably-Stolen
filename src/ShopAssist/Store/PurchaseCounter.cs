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
        private static bool buyCompatible, transferCompatible;
        private static MethodInfo transferMethod;
        private static Action<PlayerStore, GameItem> transfer;
        [ThreadStatic] private static GameItem purchaseItem;
        private readonly ConfigEntry<bool> enabled, wholesale;
        private readonly Action<string> log;
        private StoreClientInstance originalWholesale;
        private Harmony harmony;

        internal PurchaseCounter(ShopSettings settings, Action<string> log)
        {
            this.log = log;
            enabled = settings.Toggle("f01.enabled", "f01.tip");
            wholesale = settings.Toggle("f01.wholesale", "f01.wholesale.tip");
        }

        internal void Start()
        {
            active = this;
            buyCompatible = transferCompatible = false;
            transferMethod = AccessTools.DeclaredMethod(typeof(PlayerStore), "MoveDealItemToInventory", new[] { typeof(GameItem) });
            var buy = AccessTools.DeclaredMethod(typeof(PlayerStore), nameof(PlayerStore.BuyItem), new[] { typeof(GameItem) });
            if (transferMethod == null || transferMethod.IsStatic || transferMethod.ReturnType != typeof(void) ||
                buy == null || buy.IsStatic || buy.ReturnType != typeof(void))
            {
                log("F01: purchase transfer method is unavailable or incompatible; native purchase routing retained.");
                return;
            }
            try
            {
                harmony = new Harmony("mod." + ModEntry.Id + ".f01.purchase");
                harmony.Patch(transferMethod, transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PurchaseCounter), nameof(TransferTranspiler))));
                transfer = (Action<PlayerStore, GameItem>)Delegate.CreateDelegate(typeof(Action<PlayerStore, GameItem>), transferMethod);
                harmony.Patch(buy, transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PurchaseCounter), nameof(BuyTranspiler))));
                if (buyCompatible && transferCompatible) return;
                log("F01: purchase transfer sites were not uniquely recognized; native purchase routing retained.");
            }
            catch (Exception error)
            {
                log("F01: purchase transfer patch failed; native purchase routing retained: " + error.Message);
            }
            buyCompatible = transferCompatible = false;
            Unpatch();
        }

        public void Dispose()
        {
            originalWholesale = null;
            if (ReferenceEquals(active, this)) active = null;
            buyCompatible = transferCompatible = false;
            Unpatch();
        }

        private void Unpatch()
        {
            if (harmony == null) return;
            try { harmony.UnpatchAll(harmony.Id); }
            catch (Exception error) { log("F01: purchase transfer patch cleanup failed: " + error.Message); }
            harmony = null;
        }

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

        // The shared transfer method also serves barter and wholesale. Limit interception to
        // the exact item in BuyItem's call and restore the enclosing scope after nested calls.
        private static void TransferPurchasedItem(PlayerStore store, GameItem item)
        {
            GameItem previous = purchaseItem;
            purchaseItem = item;
            try { transfer(store, item); }
            finally { purchaseItem = previous; }
        }

        private static IEnumerable<CodeInstruction> BuyTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var calls = code.Select((instruction, index) => new { instruction, index })
                .Where(pair => transferMethod != null && pair.instruction.Calls(transferMethod)).ToList();
            buyCompatible = calls.Count == 1 && calls[0].index >= 2 && calls[0].instruction.opcode == OpCodes.Call &&
                code[calls[0].index - 2].opcode == OpCodes.Ldarg_0 && code[calls[0].index - 1].opcode == OpCodes.Ldarg_1;
            if (!buyCompatible)
            {
                active?.log("F01: BuyItem transfer call changed; native purchase routing retained.");
                return code;
            }
            code[calls[0].index].operand = AccessTools.DeclaredMethod(typeof(PurchaseCounter), nameof(TransferPurchasedItem));
            return code;
        }

        private static IEnumerable<CodeInstruction> TransferTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var mainStock = AccessTools.PropertyGetter(typeof(EmporiumEntry), nameof(EmporiumEntry.invElement));
            var accept = AccessTools.Method(typeof(GraphUtils), nameof(GraphUtils.TryAcceptAll),
                new[] { typeof(GraphNodeStorage), typeof(GameItem), typeof(int) });
            var sites = new List<int>();
            for (int i = 3; mainStock != null && accept != null && i < code.Count; i++)
                if (code[i].Calls(accept) && code[i].opcode == OpCodes.Call && code[i - 1].opcode == OpCodes.Ldc_I4_M1 &&
                    code[i - 2].opcode == OpCodes.Ldarg_1 && code[i - 3].Calls(mainStock)) sites.Add(i);
            transferCompatible = sites.Count == 1;
            if (!transferCompatible)
            {
                active?.log("F01: indoor transfer call changed; native purchase routing retained.");
                return code;
            }
            code[sites[0]].operand = AccessTools.DeclaredMethod(typeof(PurchaseCounter), nameof(PlaceBoughtItem));
            return code;
        }

        private static int PlaceBoughtItem(GraphNodeStorage target, GameItem item, int amount)
        {
            var entry = EmporiumEntry.Instance;
            if (buyCompatible && transferCompatible && ReferenceEquals(purchaseItem, item) &&
                active != null && active.enabled.Value && ShopInventory.InStore && entry != null &&
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
