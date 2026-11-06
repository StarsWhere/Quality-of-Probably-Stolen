using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopAssist
{
    internal enum OperationItemStatus { Completed, Skipped, NeedsInspection }

    internal sealed class OperationResult
    {
        internal sealed class ItemResult
        {
            internal int ItemId, Quantity;
            internal string Name, Location, Reason;
            internal OperationItemStatus Status;
        }

        internal string ActionKey { get; }
        internal string Detail { get; set; }
        internal readonly List<ItemResult> Items = new List<ItemResult>();
        internal int Completed => Items.Where(i => i.Status == OperationItemStatus.Completed).Sum(i => i.Quantity);
        internal int Remaining => Items.Where(i => i.Status != OperationItemStatus.Completed).Sum(i => i.Quantity);
        internal bool NeedsInspection => Items.Any(i => i.Status == OperationItemStatus.NeedsInspection);

        internal OperationResult(string actionKey) { ActionKey = actionKey ?? throw new ArgumentNullException(nameof(actionKey)); }
        internal void Complete(GameItem item, int quantity, string location = null) => Add(item, quantity, null, location, OperationItemStatus.Completed);
        internal void Skip(GameItem item, int quantity, string reason, string location = null, bool needsInspection = false) =>
            Add(item, quantity, reason, location, needsInspection || reason == "inv.inspect" ? OperationItemStatus.NeedsInspection : OperationItemStatus.Skipped);
        internal void SkipMissing(string name, int quantity, string reason, string location = null, int itemId = 0, bool needsInspection = false)
        {
            if (quantity <= 0) return;
            Items.Add(new ItemResult { ItemId = Math.Max(0, itemId), Name = name ?? "", Quantity = quantity,
                Reason = reason, Location = location ?? "", Status = needsInspection || reason == "inv.inspect" ?
                    OperationItemStatus.NeedsInspection : OperationItemStatus.Skipped });
        }

        private void Add(GameItem item, int quantity, string reason, string location, OperationItemStatus status)
        {
            if (quantity <= 0) return;
            Items.Add(new ItemResult { ItemId = item == null ? 0 : item.GetUniqueID(), Name = item?.GetDisplayName(true) ?? "",
                Quantity = quantity, Reason = reason, Location = location ?? "", Status = status });
        }
    }
}
