using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    // Alt+drag starts the game's own band selection on an occupied cell.
    internal static class OccupiedSelectionGesture
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo State = typeof(ItemMultiSelectHandler).GetField("state", Fields);
        private static readonly FieldInfo Press = typeof(ItemMultiSelectHandler).GetField("pressPosition", Fields);
        private static readonly FieldInfo Window = typeof(ItemMultiSelectHandler).GetField("bandWindow", Fields);
        private static readonly FieldInfo Inventory = typeof(ItemMultiSelectHandler).GetField("bandInventory", Fields);
        private static Func<bool> enabled;

        internal static void Register(Func<bool> active)
        {
            if (enabled != null || active == null) throw new InvalidOperationException("Occupied-cell selection is already registered.");
            enabled = active;
        }
        internal static void Unregister() { enabled = null; }

        private static bool TryBegin(ItemMultiSelectHandler handler, ISet<KeyCode> pressed)
        {
            if (enabled == null || !enabled() || handler == null || pressed == null || !pressed.Contains(handler.key) ||
                !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) || !ShopInventory.InStore ||
                ItemPinHelper.BlocksClicks || handler.IsGroupDragging || ItemMouseDragHandler.current == null ||
                ItemMouseDragHandler.current.IsDraggingItem || StoreUIManager.Instance == null ||
                StoreUIManager.Instance.hoveringStoreUI || UIDragHandler.isDraggingAnyUI || InputActionManager.current == null ||
                State?.FieldType.IsEnum != true || Press?.FieldType != typeof(Vector3) ||
                Window?.FieldType != typeof(PixelWindow) || Inventory?.FieldType != typeof(GameInventory)) return false;
            GameItemElement item = RenderHandler.RaycastElement<GameItemElement>(Input.mousePosition);
            return Begin(handler, item, Input.mousePosition);
        }

        // The point and target are captured once at press time; subsequent
        // movement and release are handled entirely by the native state machine.
        internal static bool Begin(ItemMultiSelectHandler handler, GameItemElement item, Vector3 position)
        {
            if (enabled == null || !enabled() || handler == null || InputActionManager.current == null ||
                State?.FieldType.IsEnum != true || Press?.FieldType != typeof(Vector3) ||
                Window?.FieldType != typeof(PixelWindow) || Inventory?.FieldType != typeof(GameInventory)) return false;
            string state = State.GetValue(handler)?.ToString();
            if (state != "Idle" && state != "Selected") return false;
            if (!Enum.IsDefined(State.FieldType, "PendingBand")) return false;
            if (item == null || !VisibleInventoryViews.Visible(item) || !ShopInventory.Owned(item) ||
                !(item.parentInventory is GameGridInventory inventory)) return false;
            bool ordinary = false;
            foreach (ShopInventory.Location location in ShopInventory.OwnedLocations(true))
                if (location.Inventory == inventory && !location.IsMachine &&
                    ((location.Ordinary && ShopInventory.IsMainStockBranch(location)) ||
                        ShopInventory.CanRouteFromCounter(item, location))) { ordinary = true; break; }
            PixelWindow window = item.handler.FindParentWindow();
            if (!ordinary || !VisibleInventoryViews.Visible(window)) return false;
            if (state == "Selected") handler.Cancel();
            Press.SetValue(handler, position);
            Window.SetValue(handler, window);
            Inventory.SetValue(handler, inventory);
            State.SetValue(handler, Enum.Parse(State.FieldType, "PendingBand"));
            InputActionManager.current.ResetAfter(handler);
            return true;
        }

        [HarmonyPatch(typeof(ItemMultiSelectHandler), "OnEventPress")]
        private static class PressPatch
        {
            private static bool Prefix(ItemMultiSelectHandler __instance, ISet<KeyCode> keysPressed) => !TryBegin(__instance, keysPressed);
        }
    }
}
