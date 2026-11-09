using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed partial class DesktopTools
    {
        private const string BargainPositionKey = "f14.position";
        private ConfigEntry<bool> bargainEnabled;
        private ConfigEntry<int> bargainSide;
        private BargainUIManager bargain;
        private RectTransform bargainRect;
        private RectTransform tabletRect;
        private ZoomFollower bargainFollower;
        private GameObject bargainDragObject;
        private GameObject bargainCloseObject;
        private BargainArtPart[] bargainArt;
        private BargainArtPart bargainInner;
        private RectTransform bargainPlate;
        private bool bargainMirrored;
        private bool bargainWasOpen;
        private bool bargainDragging;
        private bool bargainPositionDirty;
        private bool bargainBadData;
        private int bargainCloseFrame = -1;
        private int previousBargainMode = -1;
        private int previousScreenWidth;
        private int previousScreenHeight;
        private float previousTabletScale;
        private Vector3 previousTabletPosition;
        private Vector3 previousItemPosition;
        private Vector2 previousCanvasSize;
        private GameItem previousBargainItem;
        private Vector3 originalBargainOffset;
        private Vector2 bargainPointerStart;
        private Vector2 bargainAnchorStart;
        private Guid bargainRun;
        private int bargainSlot;

        private sealed class BargainArtPart
        {
            internal RectTransform Rect;
            internal Vector3 Position, Scale;
            internal BargainArtPart(RectTransform rect)
            { Rect = rect; Position = rect.localPosition; Scale = rect.localScale; }
        }

        private void InitBargain(ShopSettings settings)
        {
            bargainEnabled = settings.Toggle("f14.enabled", "f14.tip");
            bargainSide = settings.Choice("f14.side", "f14.side.tip",
                new[] { "f14.auto", "f14.left", "f14.right" });
            settings.Action("f14.reset", ResetBargainPosition, BargainAvailable);
        }

        private bool BargainAvailable() => bargainEnabled.Value && PlayerStore.HasInstance &&
            PlayerStore.Instance.playerLocation == PlayerStore.PlayerLocation.IN_STORE && StoreUIManager.Instance != null;

        private void TickBargain()
        {
            if (!bargainEnabled.Value || !BargainAvailable()) { ResetBargain(); return; }
            BargainUIManager current = BargainUIManager.Instance;
            GameObject target = NegociationUIManager.Instance != null ? NegociationUIManager.Instance.uiObject : null;
            if (current == null || current.uiObject == null || target == null || !current.IsOpen)
            {
                bargainWasOpen = false;
                bargainCloseFrame = -1;
                return;
            }
            if (bargain != current || bargainRect == null || tabletRect == null || tabletRect.gameObject != target)
            {
                ResetBargain();
                bargain = current;
                bargainRect = current.uiObject.transform as RectTransform;
                tabletRect = target.transform as RectTransform;
                bargainFollower = current.uiObject.GetComponent<ZoomFollower>();
                if (bargainRect == null || tabletRect == null || bargainFollower == null) { ResetBargain(); return; }
            }
            if (!Guid.TryParse(PlayerStore.Instance.runID, out Guid run) || run == Guid.Empty) return;
            if (!bargainWasOpen || bargainRun != run || bargainSlot != PlayerStore.Instance.saveSlotId)
            {
                bargainRun = run;
                bargainSlot = PlayerStore.Instance.saveSlotId;
                originalBargainOffset = tabletRect.InverseTransformPoint(bargainRect.position);
                bargainBadData = false;
                bargainPositionDirty = true;
                bargainWasOpen = true;
                if (!InstallBargainControls()) { ResetBargain(); return; }
            }
            if (bargainCloseFrame >= 0 && Time.frameCount > bargainCloseFrame)
            {
                bargainCloseFrame = -1;
                if (bargain != null && bargain.IsOpen) bargain.CloseUI();
                return;
            }
            int mode = Mathf.Clamp(bargainSide.Value, 0, 2);
            GameItem item = PlayerStore.Instance.currentNegociatedItem;
            Vector3 itemPosition = item is GameItemElement element && element.rectTransform != null
                ? element.rectTransform.position : Vector3.zero;
            Vector2 canvasSize = bargainRect.parent is RectTransform parent ? parent.rect.size : Vector2.zero;
            if (mode != previousBargainMode || Screen.width != previousScreenWidth || Screen.height != previousScreenHeight ||
                !Mathf.Approximately(previousTabletScale, tabletRect.localScale.x) ||
                previousTabletPosition != tabletRect.position || previousCanvasSize != canvasSize ||
                (mode == 0 && (item != previousBargainItem || itemPosition != previousItemPosition)))
                bargainPositionDirty = true;
            previousBargainMode = mode;
            previousScreenWidth = Screen.width;
            previousScreenHeight = Screen.height;
            previousTabletScale = tabletRect.localScale.x;
            previousTabletPosition = tabletRect.position;
            previousCanvasSize = canvasSize;
            previousBargainItem = item;
            previousItemPosition = itemPosition;
            if (bargainPositionDirty && !bargainDragging && bargainRect.rect.width > 0f && tabletRect.rect.width > 0f)
            {
                bargainPositionDirty = false;
                ApplyBargainPosition(mode, item);
            }
        }

        private bool InstallBargainControls()
        {
            BargainPanelFrame frame = bargain != null ? bargain.GetComponentInChildren<BargainPanelFrame>(true) : null;
            // The frame is on the panel root, not on the manager object.
            if (frame == null && bargain != null && bargain.uiObject != null)
                frame = bargain.uiObject.GetComponent<BargainPanelFrame>();
            if (frame == null || frame.panel == null || frame.header == null) return false;
            if (bargainArt == null)
            {
                string[] names = { "Plate", "Line", "Spine", "Scanlines", "Frame" };
                var parts = new BargainArtPart[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    var part = frame.panel.Find(names[i]) as RectTransform;
                    if (part == null || part.GetComponent<Image>() == null || part.parent != frame.panel) return false;
                    parts[i] = new BargainArtPart(part);
                }
                if (frame.inner == null || frame.inner.parent != frame.panel) return false;
                bargainArt = parts;
                bargainInner = new BargainArtPart(frame.inner);
                bargainPlate = parts[0].Rect;
            }
            if (bargainDragObject == null)
            {
                bargainDragObject = new GameObject("ShopAssist Bargain Drag", typeof(RectTransform), typeof(Image), typeof(DesktopBargainDrag));
                var rect = (RectTransform)bargainDragObject.transform;
                rect.SetParent(frame.header.rectTransform, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                var image = bargainDragObject.GetComponent<Image>();
                image.color = Color.clear;
                image.raycastTarget = true;
                var drag = bargainDragObject.GetComponent<DesktopBargainDrag>();
                drag.Begin = BeginBargainDrag;
                drag.Move = MoveBargainDrag;
                drag.End = EndBargainDrag;
            }
            if (bargainCloseObject == null)
            {
                var native = NegociationUIManager.Instance?.uiObject?.GetComponent<NegotiationTabletFrame>()?.closeButton;
                if (native == null || native.GetComponent<Image>() == null) return false;
                bargainCloseObject = UnityEngine.Object.Instantiate(native.gameObject, frame.panel, false);
                bargainCloseObject.name = "ShopAssist Bargain Close";
                var rect = (RectTransform)bargainCloseObject.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.sizeDelta = new Vector2(32f, 32f);
                var background = bargainCloseObject.GetComponent<Image>();
                background.raycastTarget = true;
                var button = bargainCloseObject.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                bargainCloseObject.AddComponent<DesktopBargainClose>().Click = () => bargainCloseFrame = Time.frameCount;
            }
            bargainCloseObject.transform.SetAsLastSibling();
            return true;
        }

        private void ApplyBargainPosition(int mode, GameItem item)
        {
            if (bargainRect == null || tabletRect == null || bargainFollower == null || tabletRect.localScale.x <= 0f) return;
            Vector2 offset;
            bool customPosition = TryReadBargainOffset(mode, out offset);
            if (!customPosition)
            {
                float left = tabletRect.rect.xMin - bargainRect.rect.xMax - 4f;
                float right = tabletRect.rect.xMax - bargainRect.rect.xMin + 4f;
                bool useLeft = mode == 1 || (mode == 0 && PreferBargainLeft(left, right, item));
                offset = new Vector2(useLeft ? left : right, 0f);
            }
            SetBargainMirrored(offset.x < 0f);
            bargainRect.position = tabletRect.TransformPoint(new Vector3(offset.x, offset.y, 0f));
            if (customPosition) ClampBargainToScreen(); else ClampBargainPairToScreen();
            bargainFollower.SetTarget(tabletRect, 1f, tabletRect.localScale.x);
        }

        private void SetBargainMirrored(bool mirrored)
        {
            if (bargainArt == null || bargainInner?.Rect == null || bargainPlate == null || bargainRect == null) return;
            float center = bargainRect.rect.center.x;
            foreach (BargainArtPart part in bargainArt)
            {
                if (part.Rect == null) return;
                Vector3 position = part.Position;
                Vector3 scale = part.Scale;
                if (mirrored) { position.x = 2f * center - position.x; scale.x = -scale.x; }
                part.Rect.localPosition = position;
                part.Rect.localScale = scale;
            }
            Vector3 innerPosition = bargainInner.Position;
            if (mirrored)
            {
                float innerCenter = innerPosition.x + bargainInner.Rect.rect.center.x * bargainInner.Scale.x;
                innerPosition.x += 2f * (center - innerCenter);
            }
            bargainInner.Rect.localPosition = innerPosition;
            bargainInner.Rect.localScale = bargainInner.Scale;
            bargainMirrored = mirrored;
            if (bargainCloseObject != null)
            {
                var corners = new Vector3[4];
                bargainPlate.GetWorldCorners(corners);
                float right = float.NegativeInfinity;
                foreach (Vector3 corner in corners) right = Mathf.Max(right, bargainRect.InverseTransformPoint(corner).x);
                ((RectTransform)bargainCloseObject.transform).anchoredPosition =
                    new Vector2(right - bargainRect.rect.xMin - 40f, -8f);
            }
        }

        private bool PreferBargainLeft(float left, float right, GameItem item)
        {
            if (Screen.width <= 0 || Screen.height <= 0) return false;
            Camera camera = CanvasCamera(bargainRect);
            DesktopRect current = ScreenBounds(bargainRect, camera);
            Vector2 center = RectTransformUtility.WorldToScreenPoint(camera, bargainRect.position);
            Vector2 leftCenter = RectTransformUtility.WorldToScreenPoint(camera, tabletRect.TransformPoint(new Vector3(left, 0f, 0f)));
            Vector2 rightCenter = RectTransformUtility.WorldToScreenPoint(camera, tabletRect.TransformPoint(new Vector3(right, 0f, 0f)));
            DesktopRect leftBounds = Translate(current, leftCenter - center);
            DesktopRect rightBounds = Translate(current, rightCenter - center);
            DesktopRect? selected = null;
            if (item is GameItemElement element && element.rectTransform != null && element.rectTransform.gameObject.activeInHierarchy)
                selected = ScreenBounds(element.rectTransform, CanvasCamera(element.rectTransform));
            return DesktopBargainRules.PreferLeft(leftBounds, rightBounds,
                new DesktopRect(0, 0, Screen.width, Screen.height), selected);
        }

        private static DesktopRect Translate(DesktopRect rect, Vector2 delta) =>
            new DesktopRect(rect.Left + delta.x, rect.Bottom + delta.y, rect.Right + delta.x, rect.Top + delta.y);

        private static Camera CanvasCamera(RectTransform rect)
        {
            Canvas canvas = rect != null ? rect.GetComponentInParent<Canvas>() : null;
            return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        private static DesktopRect ScreenBounds(RectTransform rect, Camera camera)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return new DesktopRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        private void ClampBargainToScreen()
        {
            if (Screen.width <= 0 || Screen.height <= 0 || !(bargainRect.parent is RectTransform parent)) return;
            Camera camera = CanvasCamera(bargainRect);
            DesktopRect bounds = ScreenBounds(bargainRect, camera);
            float dx = bounds.Right - bounds.Left > Screen.width ? -bounds.Left :
                bounds.Left < 0 ? -bounds.Left : bounds.Right > Screen.width ? Screen.width - bounds.Right : 0;
            float dy = bounds.Top - bounds.Bottom > Screen.height ? -bounds.Bottom :
                bounds.Bottom < 0 ? -bounds.Bottom : bounds.Top > Screen.height ? Screen.height - bounds.Top : 0;
            if (Mathf.Approximately(dx, 0f) && Mathf.Approximately(dy, 0f)) return;
            Vector2 center = RectTransformUtility.WorldToScreenPoint(camera, bargainRect.position);
            if (RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, center, camera, out Vector3 from) &&
                RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, center + new Vector2(dx, dy), camera, out Vector3 to))
                bargainRect.position += to - from;
        }

        private void ClampBargainPairToScreen()
        {
            if (Screen.width <= 0 || bargainRect.parent != tabletRect.parent || !(bargainRect.parent is RectTransform parent))
            { ClampBargainToScreen(); return; }
            Camera camera = CanvasCamera(bargainRect);
            DesktopRect panel = ScreenBounds(bargainRect, camera), tablet = ScreenBounds(tabletRect, camera);
            float left = Mathf.Min(panel.Left, tablet.Left), right = Mathf.Max(panel.Right, tablet.Right);
            if (right - left > Screen.width) { ClampBargainToScreen(); return; }
            float dx = left < 0f ? -left : right > Screen.width ? Screen.width - right : 0f;
            if (Mathf.Approximately(dx, 0f)) return;
            Vector2 center = RectTransformUtility.WorldToScreenPoint(camera, tabletRect.position);
            if (RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, center, camera, out Vector3 from) &&
                RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, center + new Vector2(dx, 0f), camera, out Vector3 to))
            {
                tabletRect.position += to - from;
                bargainRect.position += to - from;
            }
        }

        private bool BeginBargainDrag(PointerEventData pointer)
        {
            if (!BargainAvailable() || bargain == null || !bargain.IsOpen || bargainRect == null ||
                !(bargainRect.parent is RectTransform parent) || ItemMouseDragHandler.current?.IsDraggingItem == true) return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, pointer.position, pointer.pressEventCamera, out bargainPointerStart))
                return false;
            bargainAnchorStart = bargainRect.anchoredPosition;
            bargainDragging = true;
            return true;
        }

        private void MoveBargainDrag(PointerEventData pointer)
        {
            if (!bargainDragging || bargainRect == null || !(bargainRect.parent is RectTransform parent)) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, pointer.position, pointer.pressEventCamera, out Vector2 current)) return;
            bargainRect.anchoredPosition = bargainAnchorStart + current - bargainPointerStart;
            ClampBargainToScreen();
            if (tabletRect != null) SetBargainMirrored(tabletRect.InverseTransformPoint(bargainRect.position).x < 0f);
            if (bargainFollower != null && tabletRect != null && tabletRect.localScale.x > 0)
                bargainFollower.SetTarget(tabletRect, 1f, tabletRect.localScale.x);
        }

        private void EndBargainDrag()
        {
            if (!bargainDragging) return;
            bargainDragging = false;
            if (bargainRect == null || tabletRect == null || !BargainAvailable()) return;
            int mode = Mathf.Clamp(bargainSide.Value, 0, 2);
            TryReadBargainOffset(mode, out _); // Do not overwrite unknown or corrupt data.
            if (bargainBadData) return;
            Vector3 offset = tabletRect.InverseTransformPoint(bargainRect.position);
            if (!DesktopLayoutRules.Finite(offset.x) || !DesktopLayoutRules.Finite(offset.y)) return;
            string value = new DesktopBargainPosition(bargainRun, bargainSlot, mode, offset.x, offset.y).Encode();
            try { ModHelper.SetModData(ModEntry.Id, BargainPositionKey, value); }
            catch (Exception error) { log("F14 could not save panel position: " + error); }
        }

        private bool TryReadBargainOffset(int mode, out Vector2 offset)
        {
            offset = Vector2.zero;
            string raw = ModHelper.GetModData(ModEntry.Id, BargainPositionKey, null);
            if (raw == null) return false;
            if (!DesktopBargainPosition.TryDecode(raw, bargainRun, bargainSlot, out DesktopBargainPosition value))
            {
                if (!bargainBadData) log("F14 position has an unsupported version or invalid data; it was preserved.");
                bargainBadData = true;
                return false;
            }
            offset = new Vector2(value.X, value.Y);
            return value.Mode == mode;
        }

        private void ResetBargainPosition()
        {
            if (!BargainAvailable() || !Guid.TryParse(PlayerStore.Instance.runID, out bargainRun) || bargainRun == Guid.Empty) return;
            bargainSlot = PlayerStore.Instance.saveSlotId;
            bargainBadData = false;
            TryReadBargainOffset(Mathf.Clamp(bargainSide.Value, 0, 2), out _);
            if (bargainBadData) return;
            try { ModHelper.RemoveModData(ModEntry.Id, BargainPositionKey); bargainPositionDirty = true; }
            catch (Exception error) { log("F14 could not reset panel position: " + error); }
        }

        private void ResetBargain()
        {
            if (bargainMirrored) SetBargainMirrored(false);
            if (bargainWasOpen && bargainRect != null && tabletRect != null && bargainFollower != null &&
                bargain != null && bargain.IsOpen && tabletRect.localScale.x > 0f)
            {
                bargainRect.position = tabletRect.TransformPoint(originalBargainOffset);
                bargainFollower.SetTarget(tabletRect, 1f, tabletRect.localScale.x);
            }
            if (bargainDragObject != null) UnityEngine.Object.Destroy(bargainDragObject);
            if (bargainCloseObject != null) UnityEngine.Object.Destroy(bargainCloseObject);
            bargainDragObject = null;
            bargainCloseObject = null;
            bargainArt = null;
            bargainInner = null;
            bargainPlate = null;
            bargainMirrored = false;
            bargain = null;
            bargainRect = null;
            tabletRect = null;
            bargainFollower = null;
            bargainWasOpen = false;
            bargainDragging = false;
            bargainPositionDirty = false;
            bargainCloseFrame = -1;
            previousBargainMode = -1;
            previousBargainItem = null;
        }
    }

    internal sealed class DesktopBargainDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        internal Func<PointerEventData, bool> Begin;
        internal Action<PointerEventData> Move;
        internal Action End;
        private bool dragging;
        public void OnBeginDrag(PointerEventData eventData)
        {
            dragging = eventData.button == PointerEventData.InputButton.Left && (Begin?.Invoke(eventData) ?? false);
            if (dragging) eventData.Use();
        }
        public void OnDrag(PointerEventData eventData) { if (dragging) { Move?.Invoke(eventData); eventData.Use(); } }
        public void OnEndDrag(PointerEventData eventData)
        {
            if (!dragging) return;
            dragging = false;
            End?.Invoke();
            eventData.Use();
        }
        private void OnDisable() { if (dragging) { dragging = false; End?.Invoke(); } }
    }

    internal sealed class DesktopBargainClose : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        internal Action Click;
        private int pressedPointer = int.MinValue;
        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            pressedPointer = eventData.pointerId;
            eventData.Use();
        }
        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.pointerId == pressedPointer) eventData.Use();
        }
        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || eventData.pointerId != pressedPointer) return;
            pressedPointer = int.MinValue;
            eventData.Use();
            Click?.Invoke();
        }
        private void OnDisable() => pressedPointer = int.MinValue;
    }
}
