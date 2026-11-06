using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    // Shared sizing and navigation for the existing native custom-window builder.
    internal static class UiLayout
    {
        private static readonly FieldInfo Elements = AccessTools.Field(typeof(CustomUIWindow), "elements");
        private static readonly FieldInfo BuilderWindow = AccessTools.Field(typeof(CustomUIBuilder), "window");
        // The native GetWindow() hides its window; use this for layout/context reads.
        internal static CustomUIWindow Window(CustomUIBuilder builder) => builder == null ? null :
            BuilderWindow?.GetValue(builder) as CustomUIWindow;
        internal static CustomUIBuilder Rebuild(ref CustomUIWindow window, string id, string title, float width, float height)
        {
            Vector2 size = Fit(width, height);
            if (window?.IsAlive != true)
            {
                var shell = CustomUIManager.Window(id, title, CustomUIManager.LAYER_META)
                    .SetSize(size.x, size.y).SetCloseOnEscape(true).SetDraggable(true);
                window = shell.GetWindow();
                return shell;
            }
            float[] positions = window.root.GetComponentsInChildren<ScrollRect>().Select(scroll => scroll.verticalNormalizedPosition).ToArray();
            Clear(window.ContentRoot, window);
            window.SetTitle(title); window.SetSize(size.x, size.y);
            var restore = window.root.GetComponent<RestoreScrollPosition>() ?? window.root.AddComponent<RestoreScrollPosition>();
            restore.Positions = positions; restore.Frame = Time.frameCount + 2;
            return new CustomUIBuilder(CustomUIManager.Instance, window, window.ContentRoot);
        }

        private sealed class RestoreScrollPosition : MonoBehaviour
        {
            internal float[] Positions;
            internal int Frame;
            private void LateUpdate()
            {
                if (Positions == null || Time.frameCount < Frame) return;
                Canvas.ForceUpdateCanvases();
                var scrolls = GetComponentsInChildren<ScrollRect>();
                for (int i = 0; i < Math.Min(scrolls.Length, Positions.Length); i++)
                    scrolls[i].verticalNormalizedPosition = Mathf.Clamp01(Positions[i]);
                Positions = null;
            }
        }
        internal static Vector2 Fit(float width, float height)
        {
            Canvas canvas = CustomUIManager.Instance?.GetLayerRoot(CustomUIManager.LAYER_META)?.GetComponentInParent<Canvas>();
            float scale = canvas != null && canvas.scaleFactor > 0 ? canvas.scaleFactor : 1f;
            return new Vector2(Mathf.Min(width, Mathf.Max(280, Screen.width / scale - 24)),
                Mathf.Min(height, Mathf.Max(220, Screen.height / scale - 24)));
        }

        internal static void Pager(CustomUIBuilder builder, int page, int count, int pageSize, Action<int> change)
        {
            int pages = Math.Max(1, (count + pageSize - 1) / pageSize);
            if (pages <= 1) return;
            page = Math.Max(0, Math.Min(page, pages - 1));
            int current = page;
            builder.BeginRow().AddFlexibleSpace().AddButton("‹", () => { if (current > 0) change(current - 1); }, "previous");
            builder.Last.SetFixedWidth(34).SetInteractable(current > 0);
            builder.AddLabel((current + 1) + " / " + pages);
            builder.AddButton("›", () => { if (current + 1 < pages) change(current + 1); }, "next");
            builder.Last.SetFixedWidth(34).SetInteractable(current + 1 < pages);
            builder.AddFlexibleSpace().End();
        }

        internal static void Clear(Transform root, CustomUIWindow window = null)
        {
            if (root != null && window != null && Elements?.GetValue(window) is Dictionary<string, CustomUIElement> elements)
            {
                var remove = new List<string>();
                foreach (var entry in elements)
                    if (entry.Value?.root == null || entry.Value.root.transform.IsChildOf(root)) remove.Add(entry.Key);
                foreach (string id in remove) elements.Remove(id);
            }
            if (root is RectTransform rect) CustomUIBuilder.ClearContainer(rect);
        }

        internal static void RestoreScroll(Transform root, float value)
        {
            if (root == null) return;
            Canvas.ForceUpdateCanvases();
            var scroll = root.GetComponentInParent<ScrollRect>();
            if (scroll != null) scroll.verticalNormalizedPosition = Mathf.Clamp01(value);
        }

        [HarmonyPatch(typeof(CustomUIManager), nameof(CustomUIManager.CreateWindow))]
        private static class NativeFrame
        {
            private static void Prefix(string id, GameObject windowPrefabOverride, ref CustomUIManager.Style style)
            {
                if (id?.StartsWith(ModEntry.Id + ".", StringComparison.Ordinal) == true &&
                    windowPrefabOverride == null && style == CustomUIManager.Style.Auto)
                    style = CustomUIManager.Style.Machinery;
            }
        }
    }
}
