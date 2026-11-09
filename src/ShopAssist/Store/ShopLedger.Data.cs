using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ShopAssist
{
    internal sealed partial class ShopLedger
    {
        internal enum Kind { Other, Purchase, Sale, Wholesale, Appraisal, Rent, Service, Barter }

        [Serializable]
        internal sealed class DayEntry
        {
            public int day;
            public Kind kind;
            public long income, expense, knownMargin;
            public int events, unknownCost, knownCostCount;
        }

        [Serializable]
        internal sealed class History
        {
            [JsonRequired] public int schemaVersion = 2;
            [JsonRequired] public List<DayEntry> entries = new List<DayEntry>();
            public List<DayEntry> legacy = new List<DayEntry>();
            public List<Transaction> transactions = new List<Transaction>();
        }

        [Serializable]
        internal sealed class Transaction
        {
            [JsonRequired] public int day;
            [JsonRequired] public Kind kind;
            [JsonRequired] public long change;
            [JsonProperty(Required = Required.AllowNull)] public int? cost;
        }

        internal static History DecodeHistory(string raw)
        {
            History decoded = ShopData.Decode<History>(raw, 8388608);
            if (decoded == null || (decoded.schemaVersion != 1 && decoded.schemaVersion != 2) || !ValidEntries(decoded.entries))
                throw new FormatException("Invalid history");
            if (decoded.schemaVersion == 1)
            {
                decoded.legacy = decoded.entries.Select(Clone).ToList();
                decoded.transactions = new List<Transaction>();
                decoded.schemaVersion = 2;
                return decoded;
            }
            var document = JObject.Parse(raw);
            if (document["legacy"] == null || document["transactions"] == null || !ValidEntries(decoded.legacy) ||
                decoded.transactions == null || decoded.transactions.Count > 40000 || decoded.transactions.Any(row => row == null ||
                    row.day <= 0 || !Enum.IsDefined(typeof(Kind), row.kind) || row.cost < 0 ||
                    row.kind != Kind.Sale && row.cost.HasValue || row.change == 0 && row.kind != Kind.Barter))
                throw new FormatException("Invalid transaction history");
            var totals = decoded.legacy.ToDictionary(row => (row.day, row.kind), Clone);
            foreach (Transaction transaction in decoded.transactions)
            {
                var key = (transaction.day, transaction.kind);
                if (!totals.TryGetValue(key, out DayEntry row))
                    totals[key] = row = new DayEntry { day = transaction.day, kind = transaction.kind };
                AddToRow(row, transaction.change, transaction.cost);
            }
            var actualEntries = decoded.entries.ToDictionary(row => (row.day, row.kind));
            if (totals.Count != actualEntries.Count || totals.Any(row =>
            {
                DayEntry expected = row.Value;
                return !actualEntries.TryGetValue(row.Key, out DayEntry actual) || actual.income != expected.income || actual.expense != expected.expense ||
                    actual.knownMargin != expected.knownMargin || actual.events != expected.events ||
                    actual.unknownCost != expected.unknownCost || actual.knownCostCount != expected.knownCostCount;
            })) throw new FormatException("Ledger totals do not match transactions");
            return decoded;
        }

        private static bool ValidEntries(List<DayEntry> entries) => entries != null && entries.Count <= 40000 &&
            !entries.Any(row => row == null || row.day <= 0 || !Enum.IsDefined(typeof(Kind), row.kind) ||
                row.income < 0 || row.expense < 0 || row.events < 0 || row.unknownCost < 0 || row.knownCostCount < 0 ||
                (long)row.unknownCost + row.knownCostCount > row.events) &&
            entries.Select(row => (row.day, row.kind)).Distinct().Count() == entries.Count;

        private static DayEntry Clone(DayEntry row) => new DayEntry { day = row.day, kind = row.kind,
            income = row.income, expense = row.expense, knownMargin = row.knownMargin, events = row.events,
            unknownCost = row.unknownCost, knownCostCount = row.knownCostCount };

        internal static void Append(History history, int day, Kind kind, long change, int? cost)
        {
            if (history == null || history.schemaVersion != 2 || history.transactions == null || history.entries == null ||
                history.transactions.Count >= 40000 || day < 1 || !Enum.IsDefined(typeof(Kind), kind) || cost < 0 ||
                kind != Kind.Sale && cost.HasValue || change == 0 && kind != Kind.Barter)
                throw new FormatException("Invalid or full ledger");
            AddToTotals(history.entries, day, kind, change, cost);
            history.transactions.Add(new Transaction { day = day, kind = kind, change = change, cost = cost });
        }

        private static void AddToTotals(List<DayEntry> entries, int day, Kind kind, long change, int? cost)
        {
            DayEntry row = entries.Find(candidate => candidate.day == day && candidate.kind == kind);
            if (row == null)
            {
                if (entries.Count >= 40000) throw new FormatException("Ledger day limit reached");
                row = new DayEntry { day = day, kind = kind };
            }
            AddToRow(row, change, cost);
            if (!entries.Contains(row)) entries.Add(row);
        }

        private static void AddToRow(DayEntry row, long change, int? cost)
        {
            long income, expense, margin;
            int events, known, unknown;
            checked
            {
                events = row.events + 1;
                income = row.income + (change > 0 ? change : 0);
                expense = row.expense - (change < 0 ? change : 0);
                margin = row.knownMargin;
                known = row.knownCostCount; unknown = row.unknownCost;
                if (row.kind == Kind.Sale && change >= 0)
                {
                    if (cost.HasValue) { margin += change - cost.Value; known++; }
                    else unknown++;
                }
            }
            row.events = events; row.income = income; row.expense = expense;
            row.knownMargin = margin; row.knownCostCount = known; row.unknownCost = unknown;
        }
    }
}
