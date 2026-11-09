using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    internal sealed partial class ShopLedger : IDisposable
    {
        private const string DataKey = "ledger";
        private const string WindowId = ModEntry.Id + ".ledger";
        private static ShopLedger active;
        private static long nextReceipt;
        private readonly ConfigEntry<bool> receipts, ledger, showCash, showRep, sound;
        private readonly ConfigEntry<int> seconds, volume;
        private readonly ShopSettings settings;
        private readonly Action<string> log;

        internal sealed class DoorSummary
        {
            internal int Day, Unknown;
            internal long Income, Expense, Margin;
            internal bool Valid;
        }

        private sealed class Span
        {
            internal long id;
            internal Kind kind;
            internal int cash;
            internal long childCash;
            internal int? cost;
            internal Span parent;
            internal StoreReputation faction;
            internal double rep;
        }

        private History history;
        private PlayerStore owner;
        private string run;
        private int slot, balance;
        private bool loaded, wasRecording, loading;
        private Span current;
        private CustomUIWindow window;
        private int viewedDay, viewedKind;
        private string dayDraft = "";
        private readonly Dictionary<(string Run, int Slot), int> lastViewedDays = new Dictionary<(string, int), int>();

        internal ShopLedger(ShopSettings settings, Action<string> log)
        {
            this.settings = settings;
            this.log = log;
            receipts = settings.Toggle("f16.enabled", "f16.tip");
            showCash = settings.Toggle("f16.money", "f16.money.tip", true);
            showRep = settings.Toggle("f16.rep", "f16.rep.tip", true);
            sound = settings.Toggle("f16.sound", "f16.sound.tip");
            volume = settings.Integer("f16.volume", "f16.volume.tip", 50, 0, 100);
            seconds = settings.Integer("f16.seconds", "f16.seconds.tip", 4, 1, 12);
            ledger = settings.Toggle("f28.enabled", "f28.tip");
            settings.Action("f28.open", OpenHistory, () => ledger.Value);
        }

        internal void Start()
        {
            active = this;
            settings.Changed += SettingsChanged;
            ModHook.OnGameLoadedInit += StartLoad;
            ModHook.OnGameLoadedLate += FinishLoad;
            ModHook.OnNewGameStarted += NewRun;
        }

        private void StartLoad() { Reset(); loading = true; }
        private void FinishLoad() { Reset(); loading = false; }
        private void NewRun(string _) => FinishLoad();
        private void Reset()
        {
            TradeFeedback.Clear();
            if (window != null && window.IsAlive) window.Close();
            window = null;
            owner = null;
            history = null;
            current = null;
            wasRecording = loaded = false;
        }

        private bool Ensure()
        {
            if (loading || (!receipts.Value && !ledger.Value) || !PlayerStore.HasInstance) return false;
            var store = PlayerStore.Instance;
            if (string.IsNullOrEmpty(store.runID) || store.saveSlotId < 0) return false;
            if (!ReferenceEquals(owner, store) || store.runID != run || store.saveSlotId != slot)
            {
                Reset();
                owner = store;
                run = store.runID;
                slot = store.saveSlotId;
                balance = store.playerCash;
            }
            if (ledger.Value && !loaded)
            {
                loaded = true;
                string raw = ModHelper.GetModData(ModEntry.Id, DataKey, null);
                try
                {
                    history = raw == null ? new History() : DecodeHistory(raw);
                }
                catch (Exception error)
                {
                    history = null;
                    log("F28 kept unreadable ledger: " + error.Message);
                }
            }
            if (wasRecording != ledger.Value) { balance = store.playerCash; wasRecording = ledger.Value; }
            return true;
        }

        internal static bool DoorAvailable => active != null && active.ledger.Value && active.Ensure();

        internal static DoorSummary Summary(int day)
        {
            var summary = new DoorSummary { Day = day };
            if (active == null || !active.ledger.Value || !active.Ensure() || active.history == null) return summary;
            summary.Valid = true;
            foreach (DayEntry row in active.history.entries)
            {
                if (row.day != day) continue;
                summary.Income += row.income; summary.Expense += row.expense;
                summary.Margin += row.knownMargin; summary.Unknown += row.unknownCost;
            }
            return summary;
        }

        internal static void OpenDoorDay(int day)
        {
            if (active == null || day < 1 || !active.ledger.Value || !active.Ensure()) return;
            active.lastViewedDays[(active.run, active.slot)] = day;
            active.OpenHistory();
        }

        private void SettingsChanged()
        {
            if (!receipts.Value) TradeFeedback.Clear();
            if (!ledger.Value && window != null && window.IsAlive) window.Close();
            if (owner != null && wasRecording != ledger.Value)
            {
                balance = owner.playerCash;
                wasRecording = ledger.Value;
            }
        }

        private void Record(Kind kind, long change, int? cost = null)
        {
            if (!ledger.Value || history == null || (change == 0 && kind != Kind.Barter)) return;
            int day = StoreStation.GetDayCounter();
            if (day < 1) return;
            try
            {
                Append(history, day, kind, change, kind == Kind.Sale ? cost : null);
                string raw = ShopData.Encode(history);
                if (raw.Length > 8388608) throw new FormatException("Ledger size limit reached");
                CheckedModData.Write(DataKey, raw);
            }
            catch (Exception error)
            {
                history = null;
                log("F28 recording stopped after an invalid write: " + error.Message);
                ShopNotice.Show(ShopSettings.Text("f28.write_error"));
            }
        }

        private void Reconcile()
        {
            if (!Ensure() || current != null) return;
            Record(Kind.Other, (long)owner.playerCash - balance);
            balance = owner.playerCash;
        }

        internal void Tick()
        {
            if (ledger.Value) Reconcile();
        }

        // Native purchase records are transaction totals, not reconstructed per-unit costs.
        internal static int? PurchasePrice(GameItem item)
        {
            if (item == null || !item.IsTag("BOUGHT_PRICE_TAG")) return null;
            int value = item.GetTagReadonly("BOUGHT_PRICE_TAG").GetInt();
            return value >= 0 ? value : (int?)null;
        }

        private Span Enter(Kind kind, GameItem sold = null)
        {
            if (!Ensure()) return null;
            if (current == null) Reconcile();
            var span = new Span { id = ++nextReceipt, parent = current, kind = kind, cash = owner.playerCash };
            span.cost = PurchasePrice(sold);
            if (current == null && receipts.Value && showRep.Value && owner.currentClientInstance != null)
            {
                var client = owner.currentClientInstance.GetClientBlueprint();
                if (client != null && client.clientFaction != null && owner.storeReputations != null)
                    span.faction = StoreReputation.GetStoreReputation(client.clientFaction);
                if (span.faction != null) span.rep = span.faction.GetReputationExact();
            }
            return current = span;
        }

        private void Exit(Span span, bool failed)
        {
            if (span == null || !ReferenceEquals(span, current)) return;
            current = span.parent;
            if (!PlayerStore.HasInstance || !ReferenceEquals(owner, PlayerStore.Instance)) return;
            long change = (long)owner.playerCash - span.cash;
            if (current != null) current.childCash += change;
            Record(failed ? Kind.Other : span.kind, change - span.childCash, span.cost);
            if (current != null) return;
            balance = owner.playerCash;
            bool trade = span.kind == Kind.Purchase || span.kind == Kind.Sale || span.kind == Kind.Wholesale ||
                span.kind == Kind.Barter || span.kind == Kind.Appraisal;
            if (!failed && trade && receipts.Value)
            {
                double delta = span.faction == null ? 0 : span.faction.GetReputationExact() - span.rep;
                TradeFeedback.Show(span.id, change, span.faction, delta, showCash.Value, showRep.Value,
                    seconds.Value, sound.Value, volume.Value);
            }
        }

        [HarmonyPatch]
        private static class Changes
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.OnWholesaleClick));
                yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.BuyMultipleItem));
                yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.SellMultiple));
                yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.BuyItem));
                yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.SellItem));
                yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.AcceptBarter));
                yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.OnRentPaid));
                yield return AccessTools.Method(typeof(AppraisalServiceHelper), nameof(AppraisalServiceHelper.GiveAppraisalFee));
            }

            private static void Prefix(MethodBase __originalMethod, object[] __args, out Span __state)
            {
                __state = null;
                if (active == null || (!active.receipts.Value && !active.ledger.Value)) return;
                try
                {
                    var kind = Kind.Other;
                    switch (__originalMethod.Name)
                    {
                        case nameof(NegociationUIManager.OnWholesaleClick): kind = Kind.Wholesale; break;
                        case nameof(PlayerStore.BuyItem): case nameof(NegociationUIManager.BuyMultipleItem): kind = Kind.Purchase; break;
                        case nameof(PlayerStore.SellItem): case nameof(NegociationUIManager.SellMultiple): kind = Kind.Sale; break;
                        case nameof(PlayerStore.AcceptBarter): kind = Kind.Barter; break;
                        case nameof(PlayerStore.OnRentPaid): kind = Kind.Rent; break;
                        case nameof(AppraisalServiceHelper.GiveAppraisalFee): kind = Kind.Appraisal; break;
                    }
                    __state = active.Enter(kind, kind == Kind.Sale && __args.Length > 0 ? __args[0] as GameItem : null);
                }
                catch (Exception ex) { active.log("F16/F28 transaction start: " + ex.Message); }
            }

            private static void Finalizer(Span __state, Exception __exception)
            {
                try { active?.Exit(__state, __exception != null); }
                catch (Exception ex) { active?.log("F16/F28 transaction end: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SaveGame))]
        private static class BeforeSave
        {
            private static void Prefix()
            {
                try { if (active != null && active.ledger.Value) active.Reconcile(); }
                catch (Exception ex) { active?.log("F28 save reconciliation: " + ex.Message); }
            }
        }

        // The native nightly loop debits the fee immediately before this callback.
        [HarmonyPatch(typeof(RunStats), nameof(RunStats.OnServicePaid))]
        private static class PaidService
        {
            private static void Postfix(int cost)
            {
                var book = active;
                if (book == null || !book.ledger.Value || book.current != null || cost <= 0 ||
                    !book.wasRecording || !PlayerStore.HasInstance || !ReferenceEquals(book.owner, PlayerStore.Instance)) return;
                try
                {
                    int ending = book.owner.playerCash;
                    book.Record(Kind.Other, (long)ending + cost - book.balance);
                    book.Record(Kind.Service, -cost);
                    book.balance = ending;
                }
                catch (Exception error) { book.log("F28 service reconciliation: " + error.Message); }
            }
        }

        private void OpenHistory()
        {
            if (!Ensure() || CustomUIManager.Instance == null) return;
            int today = Math.Max(1, StoreStation.GetDayCounter());
            viewedDay = lastViewedDays.TryGetValue((run, slot), out int last) ? Math.Max(1, Math.Min(today, last)) : today;
            dayDraft = viewedDay.ToString(CultureInfo.InvariantCulture);
            viewedKind = 0;
            DrawHistory();
        }

        private bool HistoryScope(string expectedRun, int expectedSlot) => ledger.Value && PlayerStore.HasInstance &&
            ReferenceEquals(owner, PlayerStore.Instance) && run == expectedRun && slot == expectedSlot &&
            PlayerStore.Instance.runID == expectedRun && PlayerStore.Instance.saveSlotId == expectedSlot;

        private void ViewDay(int day, string expectedRun, int expectedSlot)
        {
            if (!HistoryScope(expectedRun, expectedSlot) || !Ensure() || day < 1 || day > StoreStation.GetDayCounter()) return;
            viewedDay = day;
            dayDraft = day.ToString(CultureInfo.InvariantCulture);
            lastViewedDays[(run, slot)] = day;
            DrawHistory();
        }

        private void ApplyDay(string expectedRun, int expectedSlot)
        {
            if (!HistoryScope(expectedRun, expectedSlot) || !Ensure() || window?.IsAlive != true) return;
            if (!int.TryParse(dayDraft.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int day) ||
                day < 1 || day > StoreStation.GetDayCounter())
            {
                var feedback = window.Get("day_feedback");
                feedback.SetText(ShopSettings.Format("f28.invalid_day", Math.Max(1, StoreStation.GetDayCounter())));
                feedback.SetVisible(true);
                return;
            }
            ViewDay(day, expectedRun, expectedSlot);
        }

        private void DrawHistory()
        {
            if (!Ensure() || !ledger.Value || CustomUIManager.Instance == null) return;
            string expectedRun = run;
            int expectedSlot = slot;
            var b = UiLayout.Rebuild(ref window, WindowId, ShopSettings.Text("f28.open"), 620, 390);
            b.OnClose(() => window = null);
            if (history == null) { b.AddLabel(ShopSettings.Text("f28.invalid")).Show(); return; }
            b.BeginRow().AddButton("‹", () => ViewDay(viewedDay - 1, expectedRun, expectedSlot));
            b.Last.SetFixedWidth(34).SetInteractable(viewedDay > 1);
            void SetDayDraft(string value) { if (HistoryScope(expectedRun, expectedSlot)) dayDraft = value ?? ""; }
            b.AddInput(ShopSettings.Text("f28.day_input"), dayDraft, SetDayDraft, "day_input", SetDayDraft);
            if (b.Last.input != null) b.Last.input.characterLimit = 10;
            b.Last.SetPreferredSize(130, 28);
            b.AddButton(ShopSettings.Text("f28.go"), () => ApplyDay(expectedRun, expectedSlot), "day_go");
            b.AddButton("›", () => ViewDay(viewedDay + 1, expectedRun, expectedSlot));
            b.Last.SetFixedWidth(34).SetInteractable(viewedDay < StoreStation.GetDayCounter());
            b.End();
            b.AddLabel("", "day_feedback"); b.Last.SetVisible(false);
            var kinds = new List<string> { ShopSettings.Text("f28.all") };
            foreach (Kind kind in Enum.GetValues(typeof(Kind)))
                kinds.Add(ShopSettings.Text("f28." + kind.ToString().ToLowerInvariant()));
            b.AddDropdown(kinds, viewedKind, index => { if (HistoryScope(expectedRun, expectedSlot)) { viewedKind = index; DrawHistory(); } }, "kind");
            long income = 0, expense = 0, margin = 0;
            int unknown = 0, known = 0;
            b.AddLabel(ShopSettings.Format("f28.cash", PlayerStore.Instance.playerCash));
            LedgerRow(b, ShopSettings.Text("f28.kind"), ShopSettings.Text("f28.change"), ShopSettings.Text("f28.profit"));
            b.BeginScrollFlexible(110);
            bool any = false;
            foreach (var row in history.entries)
            {
                if (row.day != viewedDay || (viewedKind > 0 && (int)row.kind != viewedKind - 1)) continue;
                income += row.income; expense += row.expense; margin += row.knownMargin;
                known += row.knownCostCount; unknown += row.unknownCost;
            }
            foreach (var row in history.legacy)
            {
                if (row.day != viewedDay || (viewedKind > 0 && (int)row.kind != viewedKind - 1)) continue;
                any = true;
                LedgerRow(b, ShopSettings.Format("f28.legacy", ShopSettings.Text("f28." + row.kind.ToString().ToLowerInvariant()), row.events),
                    (row.income - row.expense).ToString("+0;-0;0"), row.kind == Kind.Sale ?
                        (row.unknownCost > 0 ? ShopSettings.Text("f28.unknown") : row.knownMargin.ToString("+0;-0;0")) : "—");
            }
            foreach (var row in history.transactions)
            {
                if (row.day != viewedDay || (viewedKind > 0 && (int)row.kind != viewedKind - 1)) continue;
                any = true;
                LedgerRow(b, ShopSettings.Text("f28." + row.kind.ToString().ToLowerInvariant()), row.change.ToString("+0;-0;0"),
                    row.kind != Kind.Sale || row.change < 0 ? "—" : row.cost.HasValue ?
                        (row.change - row.cost.Value).ToString("+0;-0;0") : ShopSettings.Text("f28.unknown"));
            }
            if (!any) b.AddLabel(ShopSettings.Text("f28.empty"));
            b.End().AddLabel(ShopSettings.Format("f28.total", income - expense));
            if (known > 0 || unknown > 0) b.AddLabel(ShopSettings.Format("f28.margin", margin, unknown));
            b.Show();
        }

        private static void LedgerRow(CustomUIBuilder builder, string kind, string income, string expense)
        {
            builder.BeginRow().AddLabel(kind);
            builder.Last.SetPreferredSize(230, 28);
            builder.AddLabel(income); builder.Last.SetPreferredSize(110, 28);
            if (builder.Last.text != null) builder.Last.text.alignment = TMPro.TextAlignmentOptions.Right;
            builder.AddLabel(expense); builder.Last.SetPreferredSize(110, 28);
            if (builder.Last.text != null) builder.Last.text.alignment = TMPro.TextAlignmentOptions.Right;
            builder.End();
        }

        public void Dispose()
        {
            if (active == this) active = null;
            settings.Changed -= SettingsChanged;
            ModHook.OnGameLoadedInit -= StartLoad;
            ModHook.OnGameLoadedLate -= FinishLoad;
            ModHook.OnNewGameStarted -= NewRun;
            Reset();
        }
    }
}
