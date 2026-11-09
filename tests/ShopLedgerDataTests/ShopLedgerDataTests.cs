using System;
using System.Linq;
using ShopAssist;

internal static class ShopLedgerDataTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(string raw)
    {
        try { ShopLedger.DecodeHistory(raw); }
        catch (Exception) { return; }
        throw new InvalidOperationException("Invalid ledger was accepted");
    }

    private static void Main()
    {
        const string old = "{\"schemaVersion\":1,\"entries\":[{\"day\":1,\"kind\":2,\"income\":120,\"expense\":0,\"knownMargin\":35,\"events\":2,\"unknownCost\":1,\"knownCostCount\":1}]}";
        var history = ShopLedger.DecodeHistory(old);
        Check(history.schemaVersion == 2 && history.entries.Single().income == 120 && history.legacy.Single().events == 2 &&
            history.transactions.Count == 0 && !ReferenceEquals(history.entries[0], history.legacy[0]),
            "Old totals must remain separate from future transaction details");
        ShopLedger.Append(history, 1, ShopLedger.Kind.Sale, 80, 30);
        ShopLedger.Append(history, 1, ShopLedger.Kind.Sale, 40, null);
        ShopLedger.Append(history, 1, ShopLedger.Kind.Purchase, -25, null);
        var sales = history.entries.Single(row => row.kind == ShopLedger.Kind.Sale);
        Check(sales.income == 240 && sales.knownMargin == 85 && sales.knownCostCount == 2 && sales.unknownCost == 2 &&
            sales.events == 4 && history.legacy.Single().income == 120 && history.transactions.Count == 3,
            "Individual sales must update totals without changing the historical aggregate");
        string saved = ShopData.Encode(history);
        var restored = ShopLedger.DecodeHistory(saved);
        Check(restored.transactions.Count == 3 && restored.transactions[0].cost == 30 && restored.transactions[1].cost == null &&
            restored.entries.Single(row => row.kind == ShopLedger.Kind.Purchase).expense == 25,
            "Transactions and unknown costs must survive serialization");
        Reject(saved.Replace("\"income\":240", "\"income\":241"));
        Reject(saved.Replace("\"cost\":30", "\"cost\":-1"));
        Reject(saved.Replace("\"cost\":30", "\"wrong\":30"));
        Reject("{\"schemaVersion\":2,\"entries\":[]}");
        int before = history.transactions.Count;
        try { ShopLedger.Append(history, 1, ShopLedger.Kind.Sale, long.MinValue, null); }
        catch (OverflowException) { }
        Check(history.transactions.Count == before && sales.income == 240 && sales.expense == 0,
            "An overflow must not partially update the ledger");
        Console.WriteLine("PASS ledger migration, separate transactions, known and unknown costs, validation and overflow");
    }
}
