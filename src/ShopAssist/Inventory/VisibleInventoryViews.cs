using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShopAssist
{
    // Only the game's visible render tree is a source of visible-item facts.
    internal static class VisibleInventoryViews
    {
        internal static event Action Changed;
        internal static event Action<GameItemElement> ItemChanged;

        internal static void NotifyChanged()
        {
            foreach (Delegate handler in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { ((Action)handler)(); }
                catch (Exception error) { Debug.LogError("[ShopAssist] View observer failed: " + error); }
            }
        }

        internal static void NotifyItemChanged(GameItemElement item)
        {
            foreach (Delegate handler in ItemChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { ((Action<GameItemElement>)handler)(item); }
                catch (Exception error) { Debug.LogError("[ShopAssist] Item view observer failed: " + error); }
            }
        }

        internal static bool Visible(PixelWindow window)
        {
            return window != null && window.handler != null && window.handler.gameObject.activeInHierarchy &&
                window.IsVisible();
        }

        internal static bool Visible(GameItemElement item)
        {
            if (item == null || item.IsDestroyed() || item.handler == null || item.middle == null ||
                !item.handler.gameObject.activeInHierarchy || item.parentInventory == null || item.isDebugMenu ||
                WindowsHandler.current == null || WindowsHandler.current.visibleWindows == null)
                return false;
            PixelWindow parent = item.handler.FindParentWindow();
            return Visible(parent) && WindowsHandler.current.visibleWindows.Contains(parent);
        }

        internal static void Collect(PixelWindow window, List<TreeNodeRender> scratch, HashSet<GameItemElement> result)
        {
            if (!Visible(window)) return;
            scratch.Clear();
            window.handler.GetComponentsInChildren(false, scratch);
            foreach (TreeNodeRender node in scratch)
                if (node != null && node.element is GameItemElement item && Visible(item)) result.Add(item);
        }
    }
}
