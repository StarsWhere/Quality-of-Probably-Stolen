using System;
using System.Collections.Generic;

namespace ShopAssist
{
    internal static class FilterProcessingRules
    {
        internal static bool Transferred(bool handled, int before, int after) => handled && after < before;
        internal static bool Filled(bool handled, int before, int after) => handled || after > before;
    }

    // Only the outermost successful native operation may publish filter work.
    internal sealed class FilterProcessingTracker<TFrame, TItem>
    {
        internal sealed class Scope
        {
            internal TFrame Frame;
            internal Scope Parent;
            internal bool Failed;
            internal readonly List<TItem> Items = new List<TItem>();
        }

        internal Scope Current { get; private set; }

        internal Scope Enter(TFrame frame)
        {
            var scope = new Scope { Frame = frame, Parent = Current };
            Current = scope;
            return scope;
        }

        internal void Complete(Scope scope, bool succeeded, Action<TItem> publish)
        {
            if (scope == null || !ReferenceEquals(Current, scope))
                throw new InvalidOperationException("Unbalanced filter processing scope.");
            Current = scope.Parent;
            bool failed = !succeeded || scope.Failed;
            if (scope.Parent != null)
            {
                if (failed) scope.Parent.Failed = true;
                else scope.Parent.Items.AddRange(scope.Items);
            }
            else if (!failed)
            {
                foreach (TItem item in scope.Items) publish(item);
            }
        }
    }
}
