using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ShopAssist
{
    internal sealed class DoorBoard
    {
        private const float DoorWidth = 302f, DoorHeight = 241f;
        private const float Width = 250f, Height = 172f;
        private readonly Func<bool> calendarEnabled;
        private readonly Func<bool> deadlinesEnabled;
        private readonly Func<int> deadlineDays;
        private readonly Action<string> log;
        private readonly List<RaycastResult> hits = new List<RaycastResult>();
        private readonly List<Button> controls = new List<Button>();
        private readonly List<DoorNativeBlockerFilter> blockerFilters = new List<DoorNativeBlockerFilter>();
        private static readonly FieldInfo WindowOrder = AccessTools.Field(typeof(CustomUIManager), "windowOrder");
        private static CustomUIManager windowOwner;
        private static List<CustomUIWindow> foregroundWindows;
        private RectTransform layer, viewport, paper, badge, note, wallLedger, taskRegion, wallTaskRegion, calendarRegion;
        private CanvasGroup boardGroup, wallGroup, noteGroup, ledgerGroup;
        private Camera sceneCamera;
        private StoreShutterButton shutter;
        private Transform blockerContainer;
        private SpriteRenderer wires;
        private string signature, lastError;
        private float nextRead, focusUntil, retryAfter;
        private bool positionDirty = true, positionReady;
        private Vector3 lastShutterPosition, lastShutterStart;
        private Vector2 lastLayerPivot;
        private Matrix4x4 lastWireMatrix, lastLayerMatrix, lastSceneView, lastSceneProjection, lastCanvasView, lastCanvasProjection;
        private Rect lastPixelRect, lastCanvasPixelRect;
        private Camera lastCanvasCamera;
        private Sprite lastWireSprite;
        private bool lastSettledClosed, lastSettledOpen, lastFlipX, lastFlipY, lastNoteOpen;
        private bool noteOpen, settledClosed, settledOpen, hasCalendar, hasTasks, hasDeadlines, hasDoorLedger, hasWallLedger;
        private int selectedDay, detailOffset, taskOffset, deadlineOffset;
        private int readDay;
        private bool readNight;
        private List<DoorCalendar.Day> calendar = new List<DoorCalendar.Day>();
        private List<TodoBook.DoorTask> tasks = new List<TodoBook.DoorTask>();
        private ShopLedger.DoorSummary ledgerSummary;
        private readonly List<Deadline> deadlines = new List<Deadline>();

        private sealed class Deadline
        {
            internal string Title;
            internal int Days;
        }

        internal DoorBoard(Func<bool> calendarEnabled, Func<bool> deadlinesEnabled, Func<int> deadlineDays, Action<string> log)
        {
            this.calendarEnabled = calendarEnabled ?? throw new ArgumentNullException(nameof(calendarEnabled));
            this.deadlinesEnabled = deadlinesEnabled ?? throw new ArgumentNullException(nameof(deadlinesEnabled));
            this.deadlineDays = deadlineDays ?? throw new ArgumentNullException(nameof(deadlineDays));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        internal void Invalidate() { signature = null; nextRead = 0f; }

        internal void Tick()
        {
            if (Time.unscaledTime < retryAfter) return;
            try
            {
                if (!Ready()) { Hide(); return; }
                bool enableCalendar = calendarEnabled();
                bool enableTasks = TodoBook.DoorAvailable;
                bool enableDeadlines = deadlinesEnabled();
                bool enableLedger = ShopLedger.DoorAvailable;
                if (!enableTasks) noteOpen = false;
                if (!enableCalendar && !enableTasks && !enableDeadlines && !enableLedger) { Hide(); return; }
                if (!Attach()) { Hide(); return; }
                Vector3 at = shutter.shutterGameObject.transform.position;
                settledClosed = (at - shutter.startPosition).sqrMagnitude < 0.00001f;
                settledOpen = (at - shutter.endPosition).sqrMagnitude < 0.00001f;
                // The native controller hides this transform's GameObject in Awake,
                // yet keeps moving it as the physical shutter's animation driver.
                bool doorVisible = EmporiumEntry.Instance != null && EmporiumEntry.Instance.storeInteractables != null &&
                    EmporiumEntry.Instance.storeInteractables.activeInHierarchy && sceneCamera.isActiveAndEnabled;
                bool morning = PlayerStore.Instance.storeState == PlayerStore.StoreState.MORNING;
                bool showDoorLedger = enableLedger && morning && settledClosed;
                bool showWallLedger = enableLedger && !morning && settledOpen;
                bool any = enableCalendar || enableTasks || enableDeadlines || showDoorLedger;
                bool obscured = BlockingModal();
                viewport.gameObject.SetActive(any && doorVisible && !obscured);
                badge.gameObject.SetActive(enableTasks && wires.gameObject.activeInHierarchy && !obscured);
                wallLedger.gameObject.SetActive(showWallLedger && doorVisible && !obscured);
                if (!settledClosed && !settledOpen) noteOpen = false;
                bool onDoor = settledClosed && any && doorVisible;
                note.gameObject.SetActive(enableTasks && noteOpen && !onDoor && settledOpen && !obscured);
                Vector3 shutterPosition = shutter.shutterGameObject.transform.position;
                Rect pixelRect = sceneCamera.pixelRect;
                Camera canvasCamera = CanvasCamera();
                bool wireChanged = !positionReady || wires.transform.localToWorldMatrix != lastWireMatrix ||
                    wires.sprite != lastWireSprite || wires.flipX != lastFlipX || wires.flipY != lastFlipY;
                bool canvasChanged = canvasCamera != lastCanvasCamera || layer.localToWorldMatrix != lastLayerMatrix ||
                    layer.pivot != lastLayerPivot || (canvasCamera != null &&
                    (canvasCamera.worldToCameraMatrix != lastCanvasView || canvasCamera.projectionMatrix != lastCanvasProjection ||
                    canvasCamera.pixelRect != lastCanvasPixelRect));
                if (positionDirty || !positionReady || shutterPosition != lastShutterPosition || pixelRect != lastPixelRect ||
                    shutter.startPosition != lastShutterStart || sceneCamera.worldToCameraMatrix != lastSceneView ||
                    sceneCamera.projectionMatrix != lastSceneProjection || settledClosed != lastSettledClosed ||
                    settledOpen != lastSettledOpen || noteOpen != lastNoteOpen || wireChanged || canvasChanged)
                {
                    if (Position(canvasCamera))
                    {
                        lastShutterPosition = shutterPosition; lastShutterStart = shutter.startPosition; lastPixelRect = pixelRect;
                        lastWireMatrix = wires.transform.localToWorldMatrix; lastWireSprite = wires.sprite;
                        lastFlipX = wires.flipX; lastFlipY = wires.flipY;
                        lastLayerMatrix = layer.localToWorldMatrix; lastLayerPivot = layer.pivot;
                        lastSceneView = sceneCamera.worldToCameraMatrix; lastSceneProjection = sceneCamera.projectionMatrix;
                        lastCanvasCamera = canvasCamera;
                        if (canvasCamera != null)
                        {
                            lastCanvasView = canvasCamera.worldToCameraMatrix; lastCanvasProjection = canvasCamera.projectionMatrix;
                            lastCanvasPixelRect = canvasCamera.pixelRect;
                        }
                        lastSettledClosed = settledClosed; lastSettledOpen = settledOpen; lastNoteOpen = noteOpen;
                        positionReady = true; positionDirty = false;
                    }
                }
                if (focusUntil > 0f && Time.unscaledTime >= focusUntil) { focusUntil = 0f; signature = null; }
                if (obscured || (!onDoor && !badge.gameObject.activeInHierarchy && !note.gameObject.activeInHierarchy &&
                    !wallLedger.gameObject.activeInHierarchy))
                {
                    boardGroup.interactable = boardGroup.blocksRaycasts = false;
                    wallGroup.interactable = wallGroup.blocksRaycasts = false;
                    noteGroup.interactable = noteGroup.blocksRaycasts = false;
                    ledgerGroup.interactable = ledgerGroup.blocksRaycasts = false;
                    return;
                }
                bool showCalendar = enableCalendar && onDoor, showDeadlines = enableDeadlines && onDoor;
                if (Time.unscaledTime >= nextRead || signature == null ||
                    showCalendar != hasCalendar || enableTasks != hasTasks || showDeadlines != hasDeadlines ||
                    showDoorLedger != hasDoorLedger || showWallLedger != hasWallLedger || readDay != StoreStation.GetDayCounter() ||
                    readNight != (PlayerStore.Instance.storeState == PlayerStore.StoreState.CLOSED))
                {
                    nextRead = Time.unscaledTime + 0.5f;
                    ReadAndBuild(showCalendar, enableTasks, showDeadlines, showDoorLedger, showWallLedger);
                }
                bool canInteract = CanInteract() && !OtherUiAtPointer();
                boardGroup.interactable = boardGroup.blocksRaycasts = canInteract && settledClosed && doorVisible;
                wallGroup.interactable = wallGroup.blocksRaycasts = canInteract && (settledClosed || settledOpen);
                noteGroup.interactable = noteGroup.blocksRaycasts = canInteract && settledOpen;
                ledgerGroup.interactable = ledgerGroup.blocksRaycasts = canInteract && showWallLedger;
                if (canInteract && (settledClosed || settledOpen) && Input.mouseScrollDelta.y != 0f &&
                    !CoveredByForeground(Input.mousePosition))
                {
                    RectTransform target = note.gameObject.activeInHierarchy ? wallTaskRegion : taskRegion;
                    if (hasTasks && target != null && RectTransformUtility.RectangleContainsScreenPoint(target, Input.mousePosition, CanvasCamera()))
                    {
                        int maximum = Math.Max(0, tasks.Count - 5);
                        int next = Mathf.Clamp(taskOffset + (Input.mouseScrollDelta.y < 0f ? 1 : -1), 0, maximum);
                        if (next != taskOffset) { taskOffset = next; signature = null; }
                    }
                    else if (hasCalendar && selectedDay != 0 && calendarRegion != null &&
                        RectTransformUtility.RectangleContainsScreenPoint(calendarRegion, Input.mousePosition, CanvasCamera()))
                    {
                        DoorCalendar.Day day = calendar.Find(item => item.Number == selectedDay);
                        int maximum = day == null ? 0 : Math.Max(0, day.Events.Count - 1);
                        int next = Mathf.Clamp(detailOffset + (Input.mouseScrollDelta.y < 0f ? 1 : -1), 0, maximum);
                        if (next != detailOffset) { detailOffset = next; signature = null; }
                    }
                }
                lastError = null;
            }
            catch (Exception error)
            {
                string message = error.ToString();
                if (lastError != message) log("Door board is unavailable and will retry: " + message);
                lastError = message;
                ResetObjects();
                retryAfter = Time.unscaledTime + 1f;
            }
        }

        private static bool Ready() => ShopInventory.InStore && StoreUIManager.Instance != null &&
            StoreStation.Instance != null && StoreStation.Instance.storeEventManager != null &&
            StoreStation.GetDayCounter() > 0 && StoreShutterButton.Instance != null &&
            StoreShutterButton.Instance.shutterGameObject != null && CustomUIManager.Instance != null;

        private bool Attach()
        {
            var currentShutter = StoreShutterButton.Instance;
            var currentWires = StoreUIManager.Instance.wireRenderer;
            // Native shutter art is rendered by the moving Blocker container on the
            // inventory canvas. Place the board directly after that art and before
            // foreground windows; paper and raised-window canvases remain above it.
            Transform currentBlocker = currentShutter.blockerGameObject != null ? currentShutter.blockerGameObject.transform.parent : null;
            var currentLayer = currentBlocker != null ? currentBlocker.parent as RectTransform : null;
            Camera camera = sceneCamera;
            if (camera == null || camera.gameObject.scene != currentShutter.gameObject.scene)
            {
                camera = Camera.main;
                GameObject cameraObject = GameObject.Find("Emporium_camera");
                if (cameraObject != null && cameraObject.GetComponent<Camera>() != null)
                    camera = cameraObject.GetComponent<Camera>();
            }
            if (currentLayer == null || camera == null || currentWires == null || currentWires.sprite == null) return false;
            if (layer != currentLayer || shutter != currentShutter || wires != currentWires || blockerContainer != currentBlocker || viewport == null)
            {
                ResetObjects();
                layer = currentLayer; shutter = currentShutter; wires = currentWires; blockerContainer = currentBlocker;
                viewport = NewRect("ShopAssist_DoorViewport", layer, 0, 0, DoorWidth, DoorHeight);
                viewport.anchorMin = viewport.anchorMax = layer.pivot;
                viewport.gameObject.AddComponent<RectMask2D>();
                paper = NewRect("ShopAssist_DoorBoard", viewport, 0, 0, Width, Height);
                boardGroup = paper.gameObject.AddComponent<CanvasGroup>();
                paper.gameObject.AddComponent<DoorRaycastFilter>().Allowed = point => settledClosed && CanInteract() && !CoveredByForeground(point);
                badge = NewRect("ShopAssist_DoorBadge", layer, 0, 0, 27, 14);
                badge.anchorMin = badge.anchorMax = layer.pivot;
                wallGroup = badge.gameObject.AddComponent<CanvasGroup>();
                badge.gameObject.AddComponent<DoorRaycastFilter>().Allowed = point => (settledClosed || settledOpen) && CanInteract() && !CoveredByForeground(point);
                note = NewRect("ShopAssist_DoorNote", layer, 0, 0, 106, 145);
                note.anchorMin = note.anchorMax = layer.pivot;
                noteGroup = note.gameObject.AddComponent<CanvasGroup>();
                note.gameObject.AddComponent<DoorRaycastFilter>().Allowed = point => settledOpen && CanInteract() && !CoveredByForeground(point);
                wallLedger = NewRect("ShopAssist_WallLedger", layer, 0, 0, 106, 31);
                wallLedger.anchorMin = wallLedger.anchorMax = layer.pivot;
                ledgerGroup = wallLedger.gameObject.AddComponent<CanvasGroup>();
                wallLedger.gameObject.AddComponent<DoorRaycastFilter>().Allowed = point => settledOpen && CanInteract() && !CoveredByForeground(point);
                AttachBlockerFilter(shutter.blockerGameObject);
                if (shutter.blockerMaskObject != null && shutter.blockerMaskObject.gameObject != shutter.blockerGameObject)
                    AttachBlockerFilter(shutter.blockerMaskObject.gameObject);
                PositionLayers();
                signature = null; positionDirty = true; positionReady = false;
            }
            PositionLayers();
            sceneCamera = camera;
            return true;
        }

        private void PositionLayers()
        {
            if (blockerContainer == null || viewport == null) return;
            int at = blockerContainer.GetSiblingIndex() + 1;
            if (viewport.GetSiblingIndex() != at) viewport.SetSiblingIndex(at);
            if (badge.GetSiblingIndex() != at + 1) badge.SetSiblingIndex(at + 1);
            if (note.GetSiblingIndex() != at + 2) note.SetSiblingIndex(at + 2);
            if (wallLedger.GetSiblingIndex() != at + 3) wallLedger.SetSiblingIndex(at + 3);
        }

        private void AttachBlockerFilter(GameObject target)
        {
            if (target == null) return;
            var filter = target.AddComponent<DoorNativeBlockerFilter>();
            filter.IsDoorControl = IsDoorControl;
            blockerFilters.Add(filter);
        }

        private bool IsDoorControl(Vector2 point)
        {
            return CanInteract() && HasControlAt(point) && !CoveredByForeground(point);
        }

        private bool HasControlAt(Vector2 point)
        {
            foreach (Button control in controls)
            {
                if (control == null || !control.gameObject.activeInHierarchy || !control.interactable) continue;
                Transform target = control.transform;
                if (target.IsChildOf(paper) && !settledClosed || target.IsChildOf(badge) && !(settledClosed || settledOpen) ||
                    target.IsChildOf(note) && !settledOpen || target.IsChildOf(wallLedger) && !settledOpen) continue;
                if (target.IsChildOf(paper) && !Contains(viewport, point)) continue;
                if (Contains(target as RectTransform, point)) return true;
            }
            return false;
        }

        private bool Position(Camera canvasCamera)
        {
            Rect pixels = sceneCamera.pixelRect;
            if (pixels.width <= 0f || pixels.height <= 0f) return false;
            Vector2 topLeft = new Vector2(pixels.x + pixels.width * 329f / 960f,
                pixels.y + pixels.height * (1f - 24f / 540f));
            Vector2 bottomRight = new Vector2(pixels.x + pixels.width * 631f / 960f,
                pixels.y + pixels.height * (1f - 265f / 540f));
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, topLeft, canvasCamera, out Vector2 tl) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, bottomRight, canvasCamera, out Vector2 br)) return false;
            viewport.anchorMin = viewport.anchorMax = layer.pivot;
            badge.anchorMin = badge.anchorMax = layer.pivot;
            note.anchorMin = note.anchorMax = layer.pivot;
            wallLedger.anchorMin = wallLedger.anchorMax = layer.pivot;
            viewport.anchoredPosition = tl;
            viewport.sizeDelta = new Vector2(br.x - tl.x, tl.y - br.y);
            float sx = viewport.sizeDelta.x / DoorWidth, sy = viewport.sizeDelta.y / DoorHeight;
            paper.localScale = new Vector3(sx, sy, 1f);
            Vector3 currentScreen = sceneCamera.WorldToScreenPoint(shutter.shutterGameObject.transform.position);
            Vector3 closedScreen = sceneCamera.WorldToScreenPoint(shutter.startPosition);
            paper.anchoredPosition = new Vector2((DoorWidth - Width) * 0.5f * sx +
                (currentScreen.x - closedScreen.x) * viewport.sizeDelta.x / (bottomRight.x - topLeft.x),
                -(DoorHeight - Height) * 0.5f * sy +
                (currentScreen.y - closedScreen.y) * viewport.sizeDelta.y / (topLeft.y - bottomRight.y));

            // wire1 and wire2 have the same housing pixels; use the actual sprite's
            // pivot, pixels-per-unit and transform rather than a full-screen collider.
            Sprite sprite = wires.sprite;
            Vector2 pivot = sprite.pivot;
            float px = 723f / 960f * sprite.rect.width;
            float py = (540f - 89f) / 540f * sprite.rect.height;
            float localX = (px - pivot.x) / sprite.pixelsPerUnit * (wires.flipX ? -1f : 1f);
            float localY = (py - pivot.y) / sprite.pixelsPerUnit * (wires.flipY ? -1f : 1f);
            Vector3 housing = wires.transform.TransformPoint(new Vector3(localX, localY, 0f));
            Vector2 wallScreen = sceneCamera.WorldToScreenPoint(housing);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, wallScreen, canvasCamera, out Vector2 wall)) return false;
            badge.pivot = new Vector2(0.5f, 0.5f);
            badge.anchoredPosition = wall;
            badge.localScale = new Vector3(sx, sy, 1f);
            note.anchoredPosition = wall + new Vector2(40f * sx, 18f * sy);
            note.localScale = new Vector3(sx, sy, 1f);
            wallLedger.anchoredPosition = wall + new Vector2(40f * sx, (noteOpen ? -132f : -20f) * sy);
            wallLedger.localScale = new Vector3(sx, sy, 1f);
            return true;
        }

        private Camera CanvasCamera()
        {
            Canvas canvas = layer != null ? layer.GetComponentInParent<Canvas>() : null;
            return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        private void ReadAndBuild(bool showCalendar, bool showTasks, bool showDeadlines, bool showDoorLedger, bool showWallLedger)
        {
            hasCalendar = showCalendar; hasTasks = showTasks; hasDeadlines = showDeadlines;
            hasDoorLedger = showDoorLedger; hasWallLedger = showWallLedger;
            int today = StoreStation.GetDayCounter();
            readDay = today;
            readNight = PlayerStore.Instance.storeState == PlayerStore.StoreState.CLOSED;
            calendar = showCalendar ? DoorCalendar.Read(today) : new List<DoorCalendar.Day>();
            ledgerSummary = showDoorLedger ? ShopLedger.Summary(today - 1) : showWallLedger ? ShopLedger.Summary(today) : null;
            bool tasksValid = !showTasks || TodoBook.DoorTasks(out tasks);
            if (!showTasks || !tasksValid) tasks = new List<TodoBook.DoorTask>();
            deadlines.Clear();
            if (showDeadlines)
            {
                PlayerStore store = PlayerStore.Instance;
                if (store.IsPayingRent()) deadlines.Add(new Deadline { Title = ShopSettings.Text("door.rent"), Days = Math.Max(0, store.dayUntilRent) });
                else if (store.isPayingMortgage) deadlines.Add(new Deadline { Title = ShopSettings.Text("door.mortgage"), Days = Math.Max(0, store.dayUntilRent) });
                deadlines.Sort((a, b) => a.Days.CompareTo(b.Days));
            }
            taskOffset = Mathf.Clamp(taskOffset, 0, Math.Max(0, tasks.Count - 5));
            deadlineOffset = Mathf.Clamp(deadlineOffset, 0, Math.Max(0, deadlines.Count - 2));
            var key = new StringBuilder().Append(PlayerStore.Instance.runID).Append(':').Append(PlayerStore.Instance.saveSlotId)
                .Append('|').Append(today).Append(':').Append(readNight).Append(':').Append(settledClosed)
                .Append(':').Append(badge.gameObject.activeInHierarchy).Append(':').Append(note.gameObject.activeInHierarchy)
                .Append('|').Append(showCalendar).Append(showTasks).Append(showDeadlines)
                .Append(showDoorLedger).Append(showWallLedger)
                .Append(tasksValid).Append('|').Append(selectedDay).Append('|').Append(detailOffset).Append('|').Append(taskOffset).Append('|').Append(deadlineOffset)
                .Append('|').Append(ShopSettings.Text("door.tasks")).Append('|').Append(noteOpen);
            foreach (var day in calendar)
                foreach (var item in day.Events) key.Append('|').Append(day.Number).Append(':').Append(item.Id).Append(':').Append(item.Name).Append(':').Append(item.Detail).Append(':').Append((int)item.DayType);
            foreach (var item in tasks) key.Append('|').Append(item.Id).Append(':').Append(item.Text).Append(':').Append(item.Done);
            foreach (var item in deadlines) key.Append('|').Append(item.Title).Append(':').Append(item.Days);
            if (ledgerSummary != null) key.Append('|').Append(ledgerSummary.Day).Append(':').Append(ledgerSummary.Valid)
                .Append(':').Append(ledgerSummary.Income).Append(':').Append(ledgerSummary.Expense)
                .Append(':').Append(ledgerSummary.Margin).Append(':').Append(ledgerSummary.Unknown);
            string next = key.ToString();
            if (next == signature) return;
            signature = next;
            if (settledClosed && viewport.gameObject.activeInHierarchy) BuildBoard(today, tasksValid);
            BuildWall(tasksValid);
            controls.Clear();
            controls.AddRange(paper.GetComponentsInChildren<Button>(true));
            controls.AddRange(badge.GetComponentsInChildren<Button>(true));
            controls.AddRange(note.GetComponentsInChildren<Button>(true));
            controls.AddRange(wallLedger.GetComponentsInChildren<Button>(true));
        }

        private void BuildBoard(int today, bool tasksValid)
        {
            Clear(paper);
            taskRegion = calendarRegion = null;
            if (!hasCalendar && !hasTasks && !hasDeadlines && !hasDoorLedger) return;
            if (hasCalendar || hasTasks)
                Fill(paper, "paper", 0, 0, Width, Height, new Color32(185, 173, 145, 250));
            if (hasCalendar)
            {
                float calendarWidth = hasTasks ? 159 : 241;
                calendarRegion = NewRect("ShopAssist_DoorCalendarRegion", paper, 0, 0, calendarWidth, hasDeadlines ? 145 : Height);
                if (selectedDay == 0) BuildCalendar(today, calendarWidth);
                else BuildDayDetail(calendarWidth);
            }
            if (hasTasks)
            {
                float x = hasCalendar ? 168 : 5;
                float width = hasCalendar ? 77 : 240;
                BuildTasks(paper, x, 4, width, hasDeadlines ? 138 : 164, tasksValid, false);
            }
            if (hasDeadlines) BuildDeadlines();
            if (hasDoorLedger) BuildLedger(paper, 0, Height + 3, Width, 27);
        }

        private void BuildCalendar(int today, float width)
        {
            Fill(paper, "calendar-heading", 4, 4, width, 14, new Color32(104, 94, 76, 255));
            Label(paper, "calendar-title", 8, 4, width - 8, 13, ShopSettings.Text("door.calendar"), 9, Color.white, TextAlignmentOptions.Left);
            float col = width / 4f, top = 21, row = hasDeadlines ? 52 : 65;
            string[] weekdays = { "door.mon", "door.tue", "door.wed", "door.thu", "door.fri", "door.sat", "door.sun" };
            var previous = new Dictionary<StoreEvent, int>();
            for (int i = 0; i < calendar.Count; i++)
            {
                int week = i / 4, column = i % 4;
                if (column == 0) previous.Clear();
                float x = 4 + col * column, y = top + row * week;
                var day = calendar[i];
                Fill(paper, "cell", x, y, col - 1, row - 1,
                    day.Number == today ? new Color32(244, 220, 163, 255) : new Color32(229, 219, 195, 255));
                string weekday = ShopSettings.Text(weekdays[(day.Number + 6) % 7]);
                var date = Label(paper, "date", x + 2, y + 2, col - 4, 10, weekday, 8, Ink(), TextAlignmentOptions.Left);
                date.enableAutoSizing = true;
                date.fontSizeMin = 5;
                date.fontSizeMax = 8;
                if (day.Number < 1) continue;
                var used = new HashSet<int>();
                var slots = new Dictionary<DoorCalendar.Entry, int>();
                foreach (var entry in day.Events)
                    if (entry.Event != null && previous.TryGetValue(entry.Event, out int track) && track < 3 && used.Add(track)) slots[entry] = track;
                foreach (var entry in day.Events)
                {
                    if (slots.ContainsKey(entry)) continue;
                    int track = 0;
                    while (used.Contains(track)) track++;
                    if (track < 3) { used.Add(track); slots[entry] = track; }
                }
                var next = new Dictionary<StoreEvent, int>();
                foreach (var pair in slots)
                {
                    var entry = pair.Key; int track = pair.Value;
                    if (entry.Event != null) next[entry.Event] = track;
                    Color color = EventColor(entry);
                    float ey = y + 12 + track * 11;
                    bool before = entry.DayType == StoreEventManager.EventDayType.MiddleDay || entry.DayType == StoreEventManager.EventDayType.LastDay;
                    bool after = entry.DayType == StoreEventManager.EventDayType.MiddleDay || entry.DayType == StoreEventManager.EventDayType.FirstDay;
                    Fill(paper, "event-bar", x + (before ? 0 : 1), ey, col - (before ? 0 : 1) - (after ? 0 : 1), 10, color);
                    Fill(paper, "event-icon", x + 2, ey + 3, 3, 3, new Color(1, 1, 1, 0.9f));
                    Label(paper, "event-name", x + 6, ey, col - 7, 9, entry.Name, 6.2f, Color.white, TextAlignmentOptions.Left).overflowMode = TextOverflowModes.Truncate;
                }
                previous = next;
                if (day.Events.Count > 3) Label(paper, "more", x + 2, y + 1, col - 4, 9,
                    "+" + (day.Events.Count - 3), 7, Ink(), TextAlignmentOptions.Right);
                int target = day.Number;
                var button = Button(paper, "ShopAssist_DoorDay_" + target, x, y, col - 1, row - 1, "", () => { selectedDay = target; detailOffset = 0; Invalidate(); }, true);
                var hover = button.gameObject.AddComponent<DoorHover>();
                hover.Enter = () => ShowHover(day, x, y, width);
                hover.Exit = HideHover;
            }
            DoorCalendar.Day upcoming = calendar.Find(day => day.Number >= today && day.Events.Count > 0);
            float summaryY = top + row * 2 + 2;
            Fill(paper, "calendar-summary", 4, summaryY, width - 1, 16, new Color32(212, 200, 169, 255));
            string summary = upcoming == null ? ShopSettings.Text("door.no_events") :
                ShopSettings.Format("door.event_summary", upcoming.Number, upcoming.Events[0].Name);
            Label(paper, "calendar-summary-text", 8, summaryY + 1, width - 9, 14, summary, 7.5f, Ink(), TextAlignmentOptions.Left);
        }

        private RectTransform hoverPanel, taskHover;
        private void ShowHover(DoorCalendar.Day day, float x, float y, float calendarWidth)
        {
            HideHover();
            if (day.Events.Count == 0) return;
            hoverPanel = NewRect("ShopAssist_DoorHover", paper, Mathf.Clamp(x, 4, Math.Max(4, calendarWidth - 94)),
                Mathf.Clamp(y + 14, 27, hasDeadlines ? 96 : 122), 94, 46);
            var image = hoverPanel.gameObject.AddComponent<Image>(); image.color = new Color32(48, 50, 48, 245); image.raycastTarget = false;
            var lines = new List<string>();
            foreach (var e in day.Events) lines.Add(e.Name);
            Label(hoverPanel, "details", 4, 3, 86, 40, string.Join("\n", lines), 7, Color.white, TextAlignmentOptions.TopLeft);
        }
        private void HideHover() { if (hoverPanel != null) Object.Destroy(hoverPanel.gameObject); hoverPanel = null; }

        private void BuildDayDetail(float width)
        {
            DoorCalendar.Day day = calendar.Find(item => item.Number == selectedDay);
            if (day == null) { selectedDay = 0; Invalidate(); return; }
            Button(paper, "ShopAssist_DoorBack", 5, 4, 22, 13, "<", () => { selectedDay = 0; Invalidate(); });
            Label(paper, "day-title", 31, 5, width - 79, 13, ShopSettings.Format("door.day", day.Number), 9, Ink(), TextAlignmentOptions.Left);
            if (day.Events.Count > 1)
            {
                Button(paper, "ShopAssist_DoorDetailUp", width - 42, 4, 18, 13, "^", () => { detailOffset = Math.Max(0, detailOffset - 1); Invalidate(); });
                Button(paper, "ShopAssist_DoorDetailDown", width - 22, 4, 18, 13, "v", () => { detailOffset = Math.Min(day.Events.Count - 1, detailOffset + 1); Invalidate(); });
            }
            float y = 22;
            for (int i = detailOffset; i < day.Events.Count; i++)
            {
                var item = day.Events[i];
                if (y > (hasDeadlines ? 125 : 151)) break;
                Fill(paper, "event-icon", 7, y + 3, 5, 5, EventColor(item));
                Label(paper, "detail-name", 16, y, width - 22, 13, item.Name, 8, Ink(), TextAlignmentOptions.Left);
                y += 14;
                if (!string.IsNullOrEmpty(item.Detail))
                {
                    Label(paper, "detail-effects", 16, y, width - 23, 26, item.Detail, 7, Ink(), TextAlignmentOptions.TopLeft);
                    y += 28;
                }
            }
            if (day.Events.Count == 0) Label(paper, "empty", 7, y, width - 14, 20, ShopSettings.Text("door.no_events"), 8, Ink(), TextAlignmentOptions.Left);
        }

        private void BuildTasks(RectTransform parent, float x, float y, float width, float height, bool valid, bool wall)
        {
            Color backing = wall ? new Color32(247, 231, 174, 255) : new Color32(237, 227, 199, 255);
            RectTransform region = Fill(parent, "task-paper", x, y, width, height, backing).rectTransform;
            if (wall) wallTaskRegion = region; else taskRegion = region;
            Fill(parent, "task-heading", x + 2, y + 1, width - 4, 15, new Color32(104, 94, 76, 255));
            if (!wall && focusUntil > Time.unscaledTime)
                Fill(parent, "task-focus", x + 2, y + 1, width - 4, 15, new Color32(206, 183, 121, 255));
            Label(parent, "task-title", x + 4, y + 3, width - (wall ? 57 : 35), 12, ShopSettings.Text("door.tasks"), 8.5f, Color.white, TextAlignmentOptions.Left);
            Button(parent, "ShopAssist_DoorEditTasks", x + width - 26, y + 2, 22, 13, "+", () => TodoBook.OpenForDoor());
            if (!valid)
            {
                Label(parent, "invalid", x + 4, y + 20, width - 8, 35, ShopSettings.Text("f17.invalid"), 7, Ink(), TextAlignmentOptions.TopLeft);
                return;
            }
            if (tasks.Count == 0)
                Label(parent, "empty", x + 4, y + 22, width - 8, 20, ShopSettings.Text("door.no_tasks"), 7, Ink(), TextAlignmentOptions.Left);
            const int capacity = 5;
            int shown = Math.Min(capacity, tasks.Count - taskOffset);
            for (int i = 0; i < shown; i++)
            {
                TodoBook.DoorTask row = tasks[taskOffset + i];
                float ty = y + 18 + i * 17;
                Fill(parent, "check", x + 5, ty + 3, 9, 9, row.Done ? new Color32(99, 127, 86, 255) : new Color32(255, 250, 230, 255));
                if (row.Done)
                {
                    var shortStroke = Fill(parent, "tick-left", x + 6, ty + 7, 4, 2, Color.white).rectTransform;
                    shortStroke.localEulerAngles = new Vector3(0, 0, -38);
                    var longStroke = Fill(parent, "tick-right", x + 9, ty + 6, 6, 2, Color.white).rectTransform;
                    longStroke.localEulerAngles = new Vector3(0, 0, 42);
                }
                string taskText = row.Target > 1 ? row.Text + " " + row.Progress + "/" + row.Target : row.Text;
                Label(parent, "task-text", x + 18, ty, width - 22, 16, taskText, 7.3f,
                    row.Done ? new Color32(120, 117, 105, 255) : Ink(), TextAlignmentOptions.Left);
                var taskButton = Button(parent, "ShopAssist_DoorTask_" + row.Id, x + 2, ty, width - 4, 17, "", () =>
                {
                    if (!TodoBook.DoorSetDone(row, !row.Done)) ShopNotice.Show(ShopSettings.Text("door.task_error"));
                    Invalidate();
                }, true);
                var hover = taskButton.gameObject.AddComponent<DoorHover>();
                hover.Enter = () => ShowTaskHover(parent, row.Text, x, y, width, height, ty);
                hover.Exit = HideTaskHover;
            }
            if (tasks.Count > capacity)
            {
                Button(parent, "ShopAssist_DoorTasksUp", x + 3, y + height - 16, 18, 13, "^", () => { taskOffset = Math.Max(0, taskOffset - 1); Invalidate(); });
                Label(parent, "task-page", x + 22, y + height - 15, width - 44, 12,
                    (taskOffset + 1) + "–" + (taskOffset + shown) + "/" + tasks.Count, 7, Ink(), TextAlignmentOptions.Center);
                Button(parent, "ShopAssist_DoorTasksDown", x + width - 21, y + height - 16, 18, 13, "v", () => { taskOffset = Math.Min(tasks.Count - capacity, taskOffset + 1); Invalidate(); });
            }
        }

        private void ShowTaskHover(RectTransform parent, string text, float x, float y, float width, float height, float rowY)
        {
            HideTaskHover();
            float boxHeight = Mathf.Min(72, height - 22);
            taskHover = NewRect("ShopAssist_DoorTaskHover", parent, x + 3,
                Mathf.Clamp(rowY + 16, y + 18, y + height - boxHeight - 2), width - 6, boxHeight);
            var image = taskHover.gameObject.AddComponent<Image>(); image.color = new Color32(48, 50, 48, 245); image.raycastTarget = false;
            var label = Label(taskHover, "task-detail", 3, 3, width - 12, boxHeight - 6, text, 7, Color.white, TextAlignmentOptions.TopLeft);
            label.enableWordWrapping = true;
        }

        private void HideTaskHover() { if (taskHover != null) Object.Destroy(taskHover.gameObject); taskHover = null; }

        private void BuildDeadlines()
        {
            float y = Height - 26;
            Fill(paper, "deadlines", 0, y, Width, 26, new Color32(69, 69, 60, 245));
            if (deadlines.Count == 0)
                Label(paper, "deadline-empty", 6, y + 3, Width - 30, 20, ShopSettings.Text("door.no_deadlines"), 8, Color.white, TextAlignmentOptions.Left);
            for (int i = 0; i < Math.Min(2, deadlines.Count - deadlineOffset); i++)
            {
                var item = deadlines[deadlineOffset + i];
                Color color = item.Days <= deadlineDays() ? new Color32(255, 199, 126, 255) : Color.white;
                Label(paper, "deadline-row", 6, y + 2 + i * 11, Width - 32, 11,
                    item.Days < 0 ? item.Title : ShopSettings.Format("door.deadline", item.Title, item.Days), 7.5f, color, TextAlignmentOptions.Left);
            }
            if (deadlines.Count > 2)
            {
                Button(paper, "ShopAssist_DoorDeadlineUp", Width - 21, y + 1, 17, 11, "^", () => { deadlineOffset = Math.Max(0, deadlineOffset - 1); Invalidate(); });
                Button(paper, "ShopAssist_DoorDeadlineDown", Width - 21, y + 14, 17, 11, "v", () => { deadlineOffset = Math.Min(deadlines.Count - 2, deadlineOffset + 1); Invalidate(); });
            }
        }

        private void BuildWall(bool valid)
        {
            if (badge.gameObject.activeInHierarchy)
            {
                Clear(badge);
                int outstanding = 0;
                foreach (var task in tasks) if (!task.Done) outstanding++;
                Button(badge, "ShopAssist_DoorBadgeButton", 0, 0, 27, 14, ShopSettings.Text("door.badge") + " " + outstanding, () =>
                {
                    if (settledClosed && viewport.gameObject.activeInHierarchy)
                    {
                        focusUntil = Time.unscaledTime + 2f;
                        taskOffset = 0; noteOpen = false; Invalidate();
                    }
                    else { noteOpen = !noteOpen; Invalidate(); }
                });
            }
            if (note.gameObject.activeInHierarchy)
            {
                Clear(note);
                BuildTasks(note, 0, 0, 106, 145, valid, true);
                Button(note, "ShopAssist_DoorNoteClose", 58, 2, 19, 13, "x", () => { noteOpen = false; Invalidate(); });
            }
            if (wallLedger.gameObject.activeInHierarchy)
            {
                Clear(wallLedger);
                BuildLedger(wallLedger, 0, 0, 106, 31);
            }
        }

        private void BuildLedger(RectTransform parent, float x, float y, float width, float height)
        {
            if (ledgerSummary == null) return;
            Fill(parent, "ledger-paper", x, y, width, height, new Color32(69, 69, 60, 245));
            string title = hasDoorLedger ? ShopSettings.Text("f28.yesterday") : ShopSettings.Text("f28.today");
            string first = !ledgerSummary.Valid ? ShopSettings.Text("f28.invalid") : ledgerSummary.Day < 1 ?
                ShopSettings.Text("f28.no_yesterday") : ShopSettings.Format("f28.door_cash", title, ledgerSummary.Income - ledgerSummary.Expense);
            var cash = Label(parent, "ledger-cash", x + 4, y + 1, width - 8, 11, first, 7.5f, Color.white, TextAlignmentOptions.Left);
            cash.enableAutoSizing = true; cash.fontSizeMin = 5; cash.fontSizeMax = 7.5f;
            if (ledgerSummary.Valid && ledgerSummary.Day > 0)
            {
                var profit = Label(parent, "ledger-profit", x + 4, y + 13, width - 8, 11,
                    ShopSettings.Format("f28.door_profit", ledgerSummary.Margin, ledgerSummary.Unknown), 7, Color.white, TextAlignmentOptions.Left);
                profit.enableAutoSizing = true; profit.fontSizeMin = 5; profit.fontSizeMax = 7;
            }
            if (ledgerSummary.Day > 0 && ledgerSummary.Valid)
            {
                int day = ledgerSummary.Day;
                Button(parent, "ShopAssist_LedgerSummary", x, y, width, height, "", () => ShopLedger.OpenDoorDay(day), true);
            }
        }

        private static Color EventColor(DoorCalendar.Entry item)
        {
            if (item.Event != null && item.Event.eventType == StoreEvent.EventType.INNATE) return new Color32(91, 92, 89, 255);
            if (item.Event != null && item.Event.IsSurplus()) return new Color32(156, 55, 56, 255);
            if (item.Event != null && item.Event.IsShortage()) return new Color32(92, 111, 70, 255);
            return item.Event == null ? new Color32(133, 91, 64, 255) : new Color32(88, 114, 129, 255);
        }

        private static Color Ink() => new Color32(63, 62, 55, 255);

        private static RectTransform NewRect(string name, Transform parent, float x, float y, float width, float height)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        private static Image Fill(Transform parent, string name, float x, float y, float width, float height, Color color)
        {
            var image = NewRect(name, parent, x, y, width, height).gameObject.AddComponent<Image>();
            image.color = color; image.raycastTarget = false;
            return image;
        }

        private static TextMeshProUGUI Label(Transform parent, string name, float x, float y, float width, float height,
            string value, float size, Color color, TextAlignmentOptions alignment)
        {
            var label = NewRect(name, parent, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
            TMP_Text native = StoreUIManager.Instance?.storeCalendar?.dayTMP;
            if (native != null && native.font != null) { label.font = native.font; label.fontSharedMaterial = native.fontSharedMaterial; }
            label.text = value ?? ""; label.fontSize = size; label.color = color; label.alignment = alignment;
            label.richText = false; label.raycastTarget = false; label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            return label;
        }

        private static Button Button(Transform parent, string name, float x, float y, float width, float height,
            string value, Action clicked, bool clear = false)
        {
            var rect = NewRect(name, parent, x, y, width, height);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = clear ? new Color(1, 1, 1, 0.001f) : new Color32(111, 105, 82, 255);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => clicked());
            if (!string.IsNullOrEmpty(value)) Label(rect, "label", 1, 0, width - 2, height, value, 8, Color.white, TextAlignmentOptions.Center);
            return button;
        }

        private static void Clear(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--) Object.Destroy(parent.GetChild(i).gameObject);
        }

        private static bool CanInteract()
        {
            StoreUIManager ui = StoreUIManager.Instance;
            return ShopInventory.InStore && PlayerStore.HasInstance && ui != null && CustomUIManager.Instance != null &&
                !PlayerStore.Instance.cinematicMode && !StoreUIManager.IsAnyTyping() &&
                !KeyBindHelper.IsCapturing && !UIDragHandler.isDraggingAnyUI && ItemMouseDragHandler.current?.IsDraggingItem != true &&
                !BlockingModal();
        }

        private static bool BlockingModal()
        {
            StoreUIManager ui = StoreUIManager.Instance;
            return ui.isAboutToStartMainDialog || DialogUIManager.Instance?.currentDialog != null || DialoguePhoneUIManager.Instance?.currentDialog != null ||
                (ui.gameOptionUIManager != null && ui.gameOptionUIManager.IsOpen) ||
                (ui.escapeUIManager != null && ui.escapeUIManager.menuPanel.activeInHierarchy) ||
                (ui.blackscreen != null && ui.blackscreen.gameObject.activeInHierarchy) ||
                (ui.controlHintOverlay != null && ui.controlHintOverlay.activeInHierarchy) ||
                (ui.closeShutterConfirmationPanel != null && ui.closeShutterConfirmationPanel.activeInHierarchy) ||
                (GenericConfirmationUIManager.Instance != null && GenericConfirmationUIManager.Instance.panel.activeInHierarchy) ||
                (ui.confirmPanelUIManager != null && ui.confirmPanelUIManager.panel.activeInHierarchy) ||
                (AdvCalendarUIManager.Instance != null && AdvCalendarUIManager.Instance.UIPanel != null && AdvCalendarUIManager.Instance.UIPanel.activeInHierarchy) ||
                (PCUIManager.Instance != null && PCUIManager.Instance.panel != null && PCUIManager.Instance.panel.activeInHierarchy) ||
                (MapUIManager.Instance != null && MapUIManager.Instance.mapPanel != null && MapUIManager.Instance.mapPanel.activeInHierarchy) ||
                (EndOfDayUIManager.Instance != null && EndOfDayUIManager.Instance.IsOpen) ||
                CustomUIManager.Instance.IsOpen(ModEntry.Id + ".todo");
        }

        private bool OtherUiAtPointer()
        {
            if (!HasControlAt(Input.mousePosition)) return false;
            if (CoveredByForeground(Input.mousePosition)) return true;
            EventSystem system = EventSystem.current;
            if (system == null) return true;
            var pointer = new PointerEventData(system) { position = Input.mousePosition };
            bool boardRaycasts = boardGroup.blocksRaycasts, wallRaycasts = wallGroup.blocksRaycasts, noteRaycasts = noteGroup.blocksRaycasts,
                ledgerRaycasts = ledgerGroup.blocksRaycasts;
            boardGroup.blocksRaycasts = wallGroup.blocksRaycasts = noteGroup.blocksRaycasts = ledgerGroup.blocksRaycasts = true;
            try { hits.Clear(); system.RaycastAll(pointer, hits); }
            finally { boardGroup.blocksRaycasts = boardRaycasts; wallGroup.blocksRaycasts = wallRaycasts; noteGroup.blocksRaycasts = noteRaycasts; ledgerGroup.blocksRaycasts = ledgerRaycasts; }
            foreach (RaycastResult hit in hits)
            {
                GameObject target = hit.gameObject;
                if (target == null) continue;
                if (target.transform.IsChildOf(paper) || target.transform.IsChildOf(badge) || target.transform.IsChildOf(note) ||
                    target.transform.IsChildOf(wallLedger)) return false;
                if (hit.module is GraphicRaycaster) return true;
            }
            return false;
        }

        private static bool CoveredByForeground(Vector2 point)
        {
            GameObject paperCanvas = StoreUIManager.Instance.paperUICanvasObject;
            if (paperCanvas != null)
            {
                foreach (Transform child in paperCanvas.transform)
                    if (child.gameObject.activeInHierarchy && Contains(child as RectTransform, point)) return true;
                foreach (GameObject docked in UIDragHandler.GetActiveDockedPanels(paperCanvas.transform))
                    if (docked != null && docked.activeInHierarchy && Contains(docked.transform as RectTransform, point)) return true;
            }
            if (WindowsHandler.current?.visibleWindows != null)
                foreach (PixelWindow window in WindowsHandler.current.visibleWindows)
                    if (window != null && (window.canMove || window.forceRaycast) && window.handler != null &&
                        window.handler.gameObject.activeInHierarchy && Contains(window.handler.rectTransform, point)) return true;
            CustomUIManager manager = CustomUIManager.Instance;
            if (windowOwner != manager)
            {
                windowOwner = manager;
                foregroundWindows = WindowOrder?.GetValue(manager) as List<CustomUIWindow>;
            }
            if (foregroundWindows != null)
            {
                foreach (CustomUIWindow window in foregroundWindows)
                {
                    CustomUIWindowFrame frame = window?.frame;
                    if (frame != null && frame.gameObject.activeInHierarchy && Contains(frame.transform as RectTransform, point)) return true;
                }
            }
            else
            {
                foreach (CustomUIWindowFrame frame in Object.FindObjectsOfType<CustomUIWindowFrame>())
                    if (frame != null && frame.gameObject.activeInHierarchy && Contains(frame.transform as RectTransform, point)) return true;
            }
            return false;
        }

        private static bool Contains(RectTransform rect, Vector2 point)
        {
            if (rect == null) return false;
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(rect, point, camera);
        }

        private void Hide()
        {
            if (viewport != null) viewport.gameObject.SetActive(false);
            if (badge != null) badge.gameObject.SetActive(false);
            if (note != null) note.gameObject.SetActive(false);
            if (wallLedger != null) wallLedger.gameObject.SetActive(false);
        }

        private void ResetObjects()
        {
            foreach (DoorNativeBlockerFilter filter in blockerFilters)
                if (filter != null) { filter.IsDoorControl = null; Object.Destroy(filter); }
            blockerFilters.Clear(); controls.Clear();
            if (viewport != null) Object.Destroy(viewport.gameObject);
            if (badge != null) Object.Destroy(badge.gameObject);
            if (note != null) Object.Destroy(note.gameObject);
            if (wallLedger != null) Object.Destroy(wallLedger.gameObject);
            viewport = paper = badge = note = wallLedger = layer = taskRegion = wallTaskRegion = calendarRegion = null;
            boardGroup = wallGroup = noteGroup = ledgerGroup = null;
            shutter = null; blockerContainer = null; wires = null; sceneCamera = null; hoverPanel = taskHover = null; signature = null;
            positionReady = false; positionDirty = true;
        }

        internal void Reset()
        {
            ResetObjects(); noteOpen = false; selectedDay = detailOffset = taskOffset = deadlineOffset = 0;
            nextRead = focusUntil = retryAfter = 0f; lastError = null;
        }
    }

    internal sealed class DoorHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        internal Action Enter, Exit;
        public void OnPointerEnter(PointerEventData eventData) => Enter?.Invoke();
        public void OnPointerExit(PointerEventData eventData) => Exit?.Invoke();
    }

    internal sealed class DoorRaycastFilter : MonoBehaviour, ICanvasRaycastFilter
    {
        internal Func<Vector2, bool> Allowed;
        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera) => Allowed?.Invoke(screenPoint) == true;
    }

    internal sealed class DoorNativeBlockerFilter : MonoBehaviour, ICanvasRaycastFilter
    {
        internal Func<Vector2, bool> IsDoorControl;
        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera) => IsDoorControl?.Invoke(screenPoint) != true;
    }
}
