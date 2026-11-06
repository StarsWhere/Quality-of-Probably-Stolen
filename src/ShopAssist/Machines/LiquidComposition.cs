using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShopAssist
{
    // RefreshUI's displayed text must exactly match the selected sample before
    // any per-component measurement is projected into our own controls.
    internal static class LiquidComposition
    {
        internal sealed class Row
        {
            internal Liquid Liquid;
            internal int Amount;
            internal double Fraction;
            internal string Volume => WaterScannerUIManager.FormatAmount(Amount);
            internal string Percent => Fraction > 0 && Fraction * 100 < .01 ? "<0.01%" :
                (Fraction * 100).ToString("F2", CultureInfo.CurrentCulture) + "%";
        }
        internal static bool TryRead(GameItem sample, string displayed, out List<Row> rows, out int total)
        {
            rows = new List<Row>(); total = 0;
            if (sample == null || Liquid.Liquids == null || Liquid.Liquids.Count == 0 || Liquid.Liquids.Count > 128) return false;
            long sum = 0;
            foreach (Liquid liquid in Liquid.Liquids)
            {
                if (liquid == null || string.IsNullOrEmpty(liquid.currentPart) || string.IsNullOrEmpty(liquid.displayName)) return false;
                int amount = sample.GetTagReadonly(liquid.currentPart).GetInt();
                if (amount < 0) return false;
                sum += amount;
                if (sum > int.MaxValue) return false;
                if (amount > 0) rows.Add(new Row { Liquid = liquid, Amount = amount });
            }
            if (sum == 0 || rows.Count == 0) return false;
            total = (int)sum;
            var expected = new StringBuilder();
            foreach (Row row in rows)
            {
                row.Fraction = (double)row.Amount / total;
                float originalPercent = (float)row.Amount / total * 100f;
                expected.Append(row.Liquid.displayName).Append(": ").Append(row.Volume).Append(" (")
                    .Append(originalPercent.ToString("F2", CultureInfo.CurrentCulture)).Append("%)\n");
            }
            if (string.Equals(expected.ToString(), displayed ?? "", StringComparison.Ordinal)) return true;
            rows.Clear(); total = 0; return false;
        }
    }
}
