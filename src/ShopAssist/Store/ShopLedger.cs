using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace ShopAssist
{
    internal sealed class ShopLedger : IDisposable
    {
        private enum Kind { Other, Purchase, Sale, Wholesale, Appraisal, Rent, Service, Barter }
        private const string DataKey = "ledger";
        private const string WindowId = ModEntry.Id + ".ledger";
        private static ShopLedger active;
        private readonly ConfigEntry<bool> receipts, ledger, showCash, showRep, sound;
        private readonly ConfigEntry<int> seconds, volume;
        private readonly ShopSettings settings;
        private readonly Action<string> log;

        [Serializable]
        private sealed class DayEntry
        {
            public int day;
            public Kind kind;
            public long income, expense, knownMargin;
            public int events, unknownCost, knownCostCount;
        }

        [Serializable]
        private sealed class History
        {
            [JsonRequired] public int schemaVersion = 1;
            [JsonRequired] public List<DayEntry> entries = new List<DayEntry>();
        }

        private sealed class Span
        {
            internal Kind kind;
            internal int cash;
            internal long childCash;
            internal int? cost;
            internal Span parent;
            internal StoreReputation faction;
            internal double rep, reportedRep;
        }

        private History history;
        private PlayerStore owner;
        private string run;
        private int slot, balance;
        private bool loaded, wasRecording, loading;
        private Span current;
        private CustomUIWindow window;
        private int viewedDay, viewedKind;

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
                    history = raw == null ? new History() : ShopData.Decode<History>(raw, 8388608);
                    if (history == null || history.schemaVersion != 1 || history.entries == null ||
                        history.entries.Count > 40000 || history.entries.Exists(e => e == null || e.day <= 0 ||
                        !Enum.IsDefined(typeof(Kind), e.kind) || e.income < 0 || e.expense < 0 ||
                        e.events < 0 || e.unknownCost < 0 || e.knownCostCount < 0))
                        throw new FormatException("Invalid history");
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

        private void SettingsChanged()
        {
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
            var row = history.entries.FindLast(e => e.day == day && e.kind == kind);
            if (row == null)
            {
                row = new DayEntry { day = day, kind = kind };
                history.entries.Add(row);
            }
            row.events++;
            if (change > 0) row.income += change; else row.expense -= change;
            if (kind == Kind.Sale && change >= 0)
            {
                if (cost.HasValue) { row.knownMargin += change - cost.Value; row.knownCostCount++; }
                else row.unknownCost++;
            }
            ModHelper.SetModData(ModEntry.Id, DataKey, ShopData.Encode(history));
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

        private Span Enter(Kind kind, GameItem sold = null)
        {
            if (!Ensure()) return null;
            if (current == null) Reconcile();
            var span = new Span { parent = current, kind = kind, cash = owner.playerCash };
            if (sold != null && sold.IsTag("BOUGHT_PRICE_TAG"))
                span.cost = sold.GetTagReadonly("BOUGHT_PRICE_TAG").GetInt();
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
                var parts = new List<string>(2);
                if (showCash.Value && change != 0)
                    parts.Add(ShopSettings.Format(change > 0 ? "f16.in" : "f16.out", Math.Abs(change)));
                if (span.faction != null && showRep.Value)
                {
                    double delta = span.faction.GetReputationExact() - span.rep - span.reportedRep;
                    if (Math.Abs(delta) > 0.000001)
                        parts.Add(span.faction.factionDisplay + " " + delta.ToString("+0.##;-0.##", CultureInfo.CurrentCulture));
                }
                if (parts.Count > 0)
                {
                    ShopNotice.Show(string.Join("  ·  ", parts), seconds.Value);
                    var source = sound.Value ? EmporiumAudioPlayer.Instance?.audioRegister : null;
                    if (source != null && source.clip != null && volume.Value > 0)
                        source.PlayOneShot(source.clip, volume.Value / 100f);
                }
            }
        }

        // Reputation Pop observes this specific native callback, not every change to the
        // customer's faction. Subtract only the changes it will actually report.
        [HarmonyPatch(typeof(StoreReputation), nameof(StoreReputation.OnItemTraded))]
        private static class PopOverlap
        {
            private sealed class Observation
            {
                internal Span span;
                internal double before;
            }

            private static void Prefix(string factionId, out Observation __state)
            {
                __state = null;
                var span = active?.current;
                if (span == null || !ShopPeers.Running("reputation_pop") || StoreUIManager.Instance == null) return;
                while (span.parent != null) span = span.parent;
                if (span.faction == null || span.faction.factionId != factionId) return;
                __state = new Observation { span = span, before = span.faction.GetReputationExact() };
            }

            private static void Postfix(Observation __state)
            {
                if (__state == null) return;
                double delta = __state.span.faction.GetReputationExact() - __state.before;
                if (Math.Abs(delta) >= 0.005) __state.span.reportedRep += delta;
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
            viewedDay = StoreStation.GetDayCounter();
            viewedKind = 0;
            DrawHistory();
        }

        private void DrawHistory()
        {
            if (!Ensure() || !ledger.Value || CustomUIManager.Instance == null) return;
            var b = CustomUIManager.Window(WindowId, ShopSettings.Text("f28.open"), CustomUIManager.LAYER_META)
                .SetSize(620, 390).SetDraggable(true).SetCloseOnEscape(true);
            window = b.GetWindow();
            b.OnClose(() => window = null);
            if (history == null) { b.AddLabel(ShopSettings.Text("f28.invalid")).Show(); return; }
            b.BeginRow().AddButton("‹", () => { if (viewedDay > 1) { viewedDay--; DrawHistory(); } })
                .AddLabel(ShopSettings.Format("f28.day", viewedDay))
                .AddButton("›", () => { if (viewedDay < StoreStation.GetDayCounter()) { viewedDay++; DrawHistory(); } }).End();
            var kinds = new List<string> { ShopSettings.Text("f28.all") };
            foreach (Kind kind in Enum.GetValues(typeof(Kind)))
                kinds.Add(ShopSettings.Text("f28." + kind.ToString().ToLowerInvariant()));
            b.AddDropdown(kinds, viewedKind, index => { viewedKind = index; DrawHistory(); }, "kind");
            long income = 0, expense = 0, margin = 0;
            int unknown = 0, known = 0;
            b.AddLabel(ShopSettings.Format("f28.cash", PlayerStore.Instance.playerCash));
            LedgerRow(b, ShopSettings.Text("f28.kind"), ShopSettings.Text("f28.income"), ShopSettings.Text("f28.expense"));
            b.BeginScrollFlexible(110);
            foreach (var row in history.entries)
            {
                if (row.day != viewedDay || (viewedKind > 0 && (int)row.kind != viewedKind - 1)) continue;
                LedgerRow(b, ShopSettings.Text("f28." + row.kind.ToString().ToLowerInvariant()),
                    row.income.ToString("N0"), row.expense.ToString("N0"));
                income += row.income; expense += row.expense; margin += row.knownMargin;
                known += row.knownCostCount; unknown += row.unknownCost;
            }
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
