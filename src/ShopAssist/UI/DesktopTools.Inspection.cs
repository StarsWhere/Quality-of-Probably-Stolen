using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed partial class DesktopTools
    {
        private const string InspectionPositionKey = "f18.position";
        private static readonly FieldInfo CounterMultiplier = typeof(CounterMoveChild).GetField(
            "movementMultiplier", BindingFlags.Instance | BindingFlags.NonPublic);
        private ConfigEntry<bool> inspectionEnabled;
        private ConfigEntry<int> inspectionZoom;
        private ConfigEntry<int> inspectionFrameOpacity;
        private ConfigEntry<int> inspectionTrayOpacity;
        private InspectionUIManager inspection;
        private GameItem inspectionItem;
        private Transform inspectionLens;
        private Transform inspectionTray;
        private Transform inspectionArt;
        private CounterMoveChild inspectionArtCounter;
        private CounterMoveChild inspectionTrayCounter;
        private Image inspectionFrameImage;
        private Image inspectionTrayImage;
        private DesktopInspectionObserver inspectionPanelObserver;
        private DesktopInspectionObserver inspectionArtObserver;
        private Vector3 inspectionBaseLensPosition;
        private Vector3 inspectionBaseTrayPosition;
        private Vector3 inspectionBaseArtPosition;
        private Vector3 inspectionBaseArtScale;
        private float inspectionBaseMultiplier;
        private float inspectionTrayMultiplier;
        private float inspectionOriginalFrameAlpha;
        private float inspectionOriginalTrayAlpha;
        private float inspectionAppliedFrameAlpha;
        private float inspectionAppliedTrayAlpha;
        private float inspectionAppliedZoom = 1f;
        private float inspectionAppliedMultiplier;
        private int lastFramePercent = -1;
        private int lastTrayPercent = -1;
        private int lastZoomPercent = -1;
        private Guid inspectionRun;
        private int inspectionSlot;
        private bool inspectionBadPosition;
        private bool inspectionDragging;
        private bool inspectionReleasing;
        private int inspectionSaveFrame = -1;
        private InspectionUIManager unsupportedInspection;
        private GameItem unsupportedInspectionItem;

        private void InitInspection(ShopSettings settings)
        {
            inspectionEnabled = settings.Toggle("f18.enabled", "f18.tip");
            inspectionZoom = settings.Integer("f18.zoom", "f18.zoom.tip", 100, 100, 300);
            inspectionFrameOpacity = settings.Integer("f18.frame", "f18.frame.tip", 100, 0, 100);
            inspectionTrayOpacity = settings.Integer("f18.tray", "f18.tray.tip", 100, 0, 100);
            settings.Action("f18.reset", ResetInspectionSettings, () => inspectionEnabled.Value && PlayerStore.HasInstance);
        }

        private void TickInspection()
        {
            if (!inspectionEnabled.Value || !PlayerStore.HasInstance)
            {
                ReleaseInspection(false, true);
                return;
            }
            InspectionUIManager current = InspectionUIManager.Instance;
            if (current == null || current.panel == null || !current.panel.activeInHierarchy || current.currentInspectingItem == null)
            {
                ReleaseInspection(true, true);
                unsupportedInspection = null;
                unsupportedInspectionItem = null;
                return;
            }
            if (inspection != current || !ReferenceEquals(inspectionItem, current.currentInspectingItem))
            {
                if (unsupportedInspection == current && ReferenceEquals(unsupportedInspectionItem, current.currentInspectingItem)) return;
                ReleaseInspection(false, true);
                if (!BindInspection(current))
                {
                    unsupportedInspection = current;
                    unsupportedInspectionItem = current.currentInspectingItem;
                    log("F18 did not modify an unsupported inspection view.");
                    return;
                }
                unsupportedInspection = null;
                unsupportedInspectionItem = null;
            }
            if (!InspectionIdentityMatches()) { ReleaseInspection(false, true); return; }

            bool lensDragging = UIDragHandler.isDraggingAnyUI && UIDragHandler.draggingID == "magnifyingGlass";
            if (lensDragging) inspectionDragging = true;
            else if (inspectionDragging)
            {
                inspectionDragging = false;
                inspectionSaveFrame = Time.frameCount + 1;
            }
            if (inspectionSaveFrame >= 0 && Time.frameCount >= inspectionSaveFrame)
            {
                inspectionSaveFrame = -1;
                SaveInspectionPosition();
            }

            int zoom = Mathf.Clamp(inspectionZoom.Value, 100, 300);
            int frame = Mathf.Clamp(inspectionFrameOpacity.Value, 0, 100);
            int tray = Mathf.Clamp(inspectionTrayOpacity.Value, 0, 100);
            if (!OwnsInspectionValues())
            {
                log("F18 stopped adjusting this view because its original components changed.");
                ReleaseInspection(false, false);
                inspectionFaulted = true;
                return;
            }
            if (!lensDragging && zoom != lastZoomPercent)
            {
                float next = zoom / 100f;
                Vector3 position = inspectionArt.localPosition;
                position.x = DesktopInspectionRules.Recenter(position.x, inspectionAppliedZoom, next);
                position.y = DesktopInspectionRules.Recenter(position.y, inspectionAppliedZoom, next);
                inspectionArt.localPosition = position;
                inspectionArt.localScale = new Vector3(inspectionBaseArtScale.x * next,
                    inspectionBaseArtScale.y * next, inspectionBaseArtScale.z);
                inspectionAppliedMultiplier = inspectionBaseMultiplier * next;
                CounterMultiplier.SetValue(inspectionArtCounter, inspectionAppliedMultiplier);
                inspectionAppliedZoom = next;
                lastZoomPercent = zoom;
            }
            if (frame != lastFramePercent)
            {
                Color color = inspectionFrameImage.color;
                color.a = inspectionOriginalFrameAlpha * frame / 100f;
                inspectionFrameImage.color = color;
                inspectionAppliedFrameAlpha = color.a;
                lastFramePercent = frame;
            }
            if (tray != lastTrayPercent)
            {
                Color color = inspectionTrayImage.color;
                color.a = inspectionOriginalTrayAlpha * tray / 100f;
                inspectionTrayImage.color = color;
                inspectionAppliedTrayAlpha = color.a;
                lastTrayPercent = tray;
            }
        }

        private bool BindInspection(InspectionUIManager manager)
        {
            if (!Guid.TryParse(PlayerStore.Instance.runID, out Guid run) || run == Guid.Empty || PlayerStore.Instance.saveSlotId < 0 ||
                CounterMultiplier == null || CounterMultiplier.FieldType != typeof(float) || !CounterMultiplier.IsPrivate || CounterMultiplier.IsStatic ||
                manager.magnifyingGlass == null || manager.blockerTray == null) return false;
            Transform lens = manager.magnifyingGlass.transform;
            Transform tray = manager.blockerTray.transform;
            Transform mask = tray.parent;
            UIDragHandler drag = manager.magnifyingGlass.GetComponent<UIDragHandler>();
            if (lens.parent != manager.panel.transform || mask == null || mask.parent != lens ||
                mask.GetComponent<Mask>() == null || drag == null || drag.id != "magnifyingGlass" ||
                !Approximately(mask.localScale, Vector3.one) || !Approximately(lens.localScale, Vector3.one)) return false;

            Component[] views = { manager.cigaretteUILarge, manager.fuelUILarge, manager.stampUILarge,
                manager.injectorUILarge, manager.ribwichUILarge, manager.appHandUILarge, manager.insBerryUILarge };
            Component active = null;
            foreach (Component view in views)
            {
                if (view == null || !view.gameObject.activeSelf) continue;
                if (active != null) return false;
                active = view;
            }
            if (active == null || active.transform.parent != mask) return false;
            var artCounter = active.GetComponent<CounterMoveChild>();
            var trayCounter = manager.blockerTray.GetComponent<CounterMoveChild>();
            var frameImage = manager.magnifyingGlass.GetComponent<Image>();
            var trayImage = manager.blockerTray.GetComponent<Image>();
            if (artCounter == null || !artCounter.enabled || trayCounter == null || !trayCounter.enabled ||
                frameImage == null || trayImage == null) return false;
            float artMultiplier = (float)CounterMultiplier.GetValue(artCounter);
            float trayMultiplier = (float)CounterMultiplier.GetValue(trayCounter);
            if (!DesktopLayoutRules.Finite(artMultiplier) || artMultiplier <= 0f ||
                !DesktopLayoutRules.Finite(trayMultiplier) || trayMultiplier <= 0f) return false;

            inspection = manager;
            inspectionItem = manager.currentInspectingItem;
            inspectionRun = run;
            inspectionSlot = PlayerStore.Instance.saveSlotId;
            inspectionLens = lens;
            inspectionTray = tray;
            inspectionArt = active.transform;
            inspectionArtCounter = artCounter;
            inspectionTrayCounter = trayCounter;
            inspectionFrameImage = frameImage;
            inspectionTrayImage = trayImage;
            inspectionBaseLensPosition = lens.localPosition;
            inspectionBaseTrayPosition = tray.localPosition;
            inspectionBaseArtPosition = active.transform.localPosition;
            inspectionBaseArtScale = active.transform.localScale;
            inspectionBaseMultiplier = artMultiplier;
            inspectionTrayMultiplier = trayMultiplier;
            inspectionOriginalFrameAlpha = inspectionAppliedFrameAlpha = frameImage.color.a;
            inspectionOriginalTrayAlpha = inspectionAppliedTrayAlpha = trayImage.color.a;
            inspectionAppliedZoom = 1f;
            inspectionAppliedMultiplier = artMultiplier;
            inspectionBadPosition = false;
            lastZoomPercent = lastFramePercent = lastTrayPercent = -1;

            // Restore lens position as though the original drag had moved it, so
            // both the enlarged image and backing tray retain their native mapping.
            if (TryReadInspectionPosition(out DesktopInspectionPosition saved))
            {
                Vector3 next = new Vector3(saved.X, saved.Y, inspectionBaseLensPosition.z);
                Vector3 delta = next - inspectionBaseLensPosition;
                lens.localPosition = next;
                tray.localPosition = new Vector3(inspectionBaseTrayPosition.x - delta.x * trayMultiplier,
                    inspectionBaseTrayPosition.y - delta.y * trayMultiplier, inspectionBaseTrayPosition.z);
                active.transform.localPosition = new Vector3(DesktopInspectionRules.CounterMove(inspectionBaseArtPosition.x, delta.x, artMultiplier, 1f),
                    DesktopInspectionRules.CounterMove(inspectionBaseArtPosition.y, delta.y, artMultiplier, 1f), inspectionBaseArtPosition.z);
            }

            inspectionPanelObserver = manager.panel.AddComponent<DesktopInspectionObserver>();
            inspectionPanelObserver.Disabled = OnInspectionViewDisabled;
            inspectionArtObserver = active.gameObject.AddComponent<DesktopInspectionObserver>();
            inspectionArtObserver.Disabled = OnInspectionViewDisabled;
            return true;
        }

        private bool InspectionIdentityMatches() => inspection != null && inspection.panel != null &&
            inspection.panel.activeInHierarchy && ReferenceEquals(inspectionItem, inspection.currentInspectingItem) &&
            PlayerStore.HasInstance && Guid.TryParse(PlayerStore.Instance.runID, out Guid run) && run == inspectionRun &&
            PlayerStore.Instance.saveSlotId == inspectionSlot && inspectionArt != null && inspectionLens != null && inspectionTray != null;

        private bool OwnsInspectionValues()
        {
            if (inspectionArt == null || inspectionArtCounter == null || inspectionFrameImage == null || inspectionTrayImage == null) return false;
            Vector3 expectedScale = new Vector3(inspectionBaseArtScale.x * inspectionAppliedZoom,
                inspectionBaseArtScale.y * inspectionAppliedZoom, inspectionBaseArtScale.z);
            return Approximately(inspectionArt.localScale, expectedScale) &&
                Mathf.Approximately((float)CounterMultiplier.GetValue(inspectionArtCounter), inspectionAppliedMultiplier) &&
                Mathf.Approximately(inspectionFrameImage.color.a, inspectionAppliedFrameAlpha) &&
                Mathf.Approximately(inspectionTrayImage.color.a, inspectionAppliedTrayAlpha);
        }

        private static bool Approximately(Vector3 a, Vector3 b) =>
            Mathf.Approximately(a.x, b.x) && Mathf.Approximately(a.y, b.y) && Mathf.Approximately(a.z, b.z);

        private bool TryReadInspectionPosition(out DesktopInspectionPosition value)
        {
            value = default;
            string raw = ModHelper.GetModData(ModEntry.Id, InspectionPositionKey, null);
            if (raw == null) return false;
            if (DesktopInspectionPosition.TryDecode(raw, inspectionRun, inspectionSlot, out value)) return true;
            if (!inspectionBadPosition) log("F18 position has an unsupported version or invalid data; it was preserved.");
            inspectionBadPosition = true;
            return false;
        }

        private void SaveInspectionPosition(bool closing = false)
        {
            bool sameRun = PlayerStore.HasInstance && Guid.TryParse(PlayerStore.Instance.runID, out Guid run) &&
                run == inspectionRun && PlayerStore.Instance.saveSlotId == inspectionSlot;
            if ((!InspectionIdentityMatches() && !(closing && sameRun)) || inspectionLens == null) return;
            TryReadInspectionPosition(out _);
            if (inspectionBadPosition) return;
            Vector3 position = inspectionLens.localPosition;
            if (!DesktopLayoutRules.Finite(position.x) || !DesktopLayoutRules.Finite(position.y)) return;
            try { ModHelper.SetModData(ModEntry.Id, InspectionPositionKey,
                new DesktopInspectionPosition(inspectionRun, inspectionSlot, position.x, position.y).Encode()); }
            catch (Exception error) { log("F18 could not save the lens position: " + error); }
        }

        private void OnInspectionViewDisabled()
        {
            if (inspectionReleasing) return;
            if (inspectionDragging || inspectionSaveFrame >= 0) SaveInspectionPosition(true);
            ReleaseInspection(false, true);
        }

        private void ReleaseInspection(bool save, bool restorePositions)
        {
            if (inspectionReleasing) return;
            inspectionReleasing = true;
            try
            {
                if (save && (inspectionDragging || inspectionSaveFrame >= 0)) SaveInspectionPosition(true);
                RemoveInspectionObserver(ref inspectionPanelObserver);
                RemoveInspectionObserver(ref inspectionArtObserver);
                if (inspectionArt != null && inspectionArtCounter != null && CounterMultiplier != null)
                {
                    Vector3 expected = new Vector3(inspectionBaseArtScale.x * inspectionAppliedZoom,
                        inspectionBaseArtScale.y * inspectionAppliedZoom, inspectionBaseArtScale.z);
                    if (Approximately(inspectionArt.localScale, expected))
                    {
                        if (restorePositions) inspectionArt.localPosition = inspectionBaseArtPosition;
                        else if (inspectionAppliedZoom > 0f)
                        {
                            Vector3 position = inspectionArt.localPosition;
                            position.x /= inspectionAppliedZoom;
                            position.y /= inspectionAppliedZoom;
                            inspectionArt.localPosition = position;
                        }
                        inspectionArt.localScale = inspectionBaseArtScale;
                    }
                    if (Mathf.Approximately((float)CounterMultiplier.GetValue(inspectionArtCounter), inspectionAppliedMultiplier))
                        CounterMultiplier.SetValue(inspectionArtCounter, inspectionBaseMultiplier);
                }
                if (inspectionLens != null && restorePositions) inspectionLens.localPosition = inspectionBaseLensPosition;
                if (inspectionTray != null && restorePositions) inspectionTray.localPosition = inspectionBaseTrayPosition;
                RestoreAlpha(inspectionFrameImage, inspectionAppliedFrameAlpha, inspectionOriginalFrameAlpha);
                RestoreAlpha(inspectionTrayImage, inspectionAppliedTrayAlpha, inspectionOriginalTrayAlpha);
            }
            finally
            {
                inspection = null; inspectionItem = null; inspectionLens = null; inspectionTray = null; inspectionArt = null;
                inspectionArtCounter = null; inspectionTrayCounter = null; inspectionFrameImage = null; inspectionTrayImage = null;
                inspectionDragging = false; inspectionSaveFrame = -1; inspectionReleasing = false;
            }
        }

        private static void RestoreAlpha(Image image, float applied, float original)
        {
            if (image == null || !Mathf.Approximately(image.color.a, applied)) return;
            Color color = image.color;
            color.a = original;
            image.color = color;
        }

        private static void RemoveInspectionObserver(ref DesktopInspectionObserver observer)
        {
            if (observer == null) return;
            observer.Disabled = null;
            UnityEngine.Object.Destroy(observer);
            observer = null;
        }

        private void ResetInspectionSettings()
        {
            if (!inspectionEnabled.Value || !PlayerStore.HasInstance) return;
            inspectionZoom.Set(100);
            inspectionFrameOpacity.Set(100);
            inspectionTrayOpacity.Set(100);
            try
            {
                ModHelper.GetConfig(ModEntry.Id).Save();
                if (!Guid.TryParse(PlayerStore.Instance.runID, out Guid run) || run == Guid.Empty) return;
                string raw = ModHelper.GetModData(ModEntry.Id, InspectionPositionKey, null);
                if (raw != null && !DesktopInspectionPosition.TryDecode(raw, run, PlayerStore.Instance.saveSlotId, out _))
                { log("F18 position could not be reset because its data is invalid; it was preserved."); return; }
                ModHelper.RemoveModData(ModEntry.Id, InspectionPositionKey);
                if (InspectionIdentityMatches())
                {
                    inspectionLens.localPosition = inspectionBaseLensPosition;
                    inspectionTray.localPosition = inspectionBaseTrayPosition;
                    inspectionArt.localPosition = inspectionBaseArtPosition * inspectionAppliedZoom;
                }
            }
            catch (Exception error) { log("F18 could not reset its settings: " + error); }
        }
    }

    internal sealed class DesktopInspectionObserver : MonoBehaviour
    {
        internal Action Disabled;
        private void OnDisable() => Disabled?.Invoke();
    }
}
