using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    // Marks our windows and stops the native TreeNodeRender raycast at their background.
    internal sealed class WindowInput : MonoBehaviour
    {
        [HarmonyPatch(typeof(CustomUIWindow), nameof(CustomUIWindow.Close))]
        private static class ClosePatch
        {
            private static void Prefix(CustomUIWindow __instance)
            {
                // Unity destroys at frame end; a closed window must stop receiving input now.
                GameObject root = __instance?.root;
                if (root != null && root.GetComponent<WindowInput>() != null) root.SetActive(false);
            }
        }

        [HarmonyPatch(typeof(CustomUIWindow), nameof(CustomUIWindow.Show))]
        private static class ShowPatch
        {
            private static void Postfix(CustomUIWindow __instance)
            {
                GameObject root = __instance?.root;
                if (root == null || __instance.id == null ||
                    !__instance.id.StartsWith(ModEntry.Id + ".", StringComparison.Ordinal) ||
                    root.GetComponent<WindowInput>() != null) return;

                root.AddComponent<WindowInput>();
                var background = new GameObject("ShopAssist_WindowInput", typeof(RectTransform), typeof(Image),
                    typeof(TreeNodeRender), typeof(LayoutElement));
                RectTransform rect = (RectTransform)background.transform;
                rect.SetParent(root.transform, false);
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                rect.SetAsFirstSibling();
                background.GetComponent<LayoutElement>().ignoreLayout = true;
                Image image = background.GetComponent<Image>();
                image.color = Color.clear; image.raycastTarget = true;
                image.canvasRenderer.cullTransparentMesh = false;
            }
        }
    }
}
