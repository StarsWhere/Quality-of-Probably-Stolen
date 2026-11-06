using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    internal static class ShopNotice
    {
        internal enum NoticeKind { Status, Warning, Error }
        private static GameObject panel;
        private static CustomUIElement label;
        private static float until;
        private sealed class Notice { internal Guid Id; internal NoticeKind Kind; internal string Subject, Message; internal float Seconds; }
        private static readonly Queue<Notice> pending = new Queue<Notice>();
        private static readonly HashSet<Guid> seen = new HashSet<Guid>();
        private static readonly Queue<Guid> recent = new Queue<Guid>();
        private static NoticeKind currentKind;
        private static Notice activeNotice;
        private static readonly Stack<Notice> interrupted = new Stack<Notice>();
        private static float born;
        private static OperationResult lastResult;
        private static string resultRun;
        private static int resultSlot;
        private static GameInventory resultGround;
        private static CustomUIWindow resultWindow;
        internal static bool HasResult => lastResult != null && PlayerStore.HasInstance &&
            resultRun == PlayerStore.Instance.runID && resultSlot == PlayerStore.Instance.saveSlotId &&
            (resultGround == null ? ShopInventory.InStore : FieldScope(resultGround));

        private static bool FieldScope(GameInventory ground) => ground != null &&
            EmporiumEntry.Instance?.afterhourInventory == ground &&
            EmporiumEntry.Instance.afterhourWindow?.IsVisible() == true &&
            MapUIManager.Instance?.Current?.Id == AfterhourLocation.DUMPING_GROUND;

        internal static void Show(string message, float seconds = 4f)
        {
            ShowEvent(Guid.NewGuid(), NoticeKind.Status, null, message, seconds);
        }

        internal static void ShowEvent(Guid id, NoticeKind kind, string subject, string message, float seconds = 4f)
        {
            if (string.IsNullOrWhiteSpace(message) || !seen.Add(id)) return;
            recent.Enqueue(id);
            while (recent.Count > 512) seen.Remove(recent.Dequeue());
            var notice = new Notice { Id = id, Kind = kind, Subject = subject, Message = message, Seconds = Mathf.Clamp(seconds, 1f, 30f) };
            if (panel != null && panel.activeSelf && Time.unscaledTime < until)
            {
                if (kind > currentKind)
                {
                    if (activeNotice != null)
                    {
                        activeNotice.Seconds = Mathf.Max(1f, until - Time.unscaledTime);
                        interrupted.Push(activeNotice);
                    }
                    Display(notice);
                }
                else pending.Enqueue(notice);
                return;
            }
            if (pending.Count > 0)
            {
                pending.Enqueue(notice);
                Display(pending.Dequeue());
            }
            else Display(notice);
        }

        private static void Display(Notice notice)
        {
            var manager = CustomUIManager.Instance;
            if (manager == null) return;
            if (panel == null)
            {
                var layer = manager.GetLayerRoot(CustomUIManager.LAYER_META);
                if (layer == null) return;
                panel = new GameObject(ModEntry.Id + ".notice", typeof(RectTransform), typeof(CanvasGroup));
                var rect = (RectTransform)panel.transform;
                rect.SetParent(layer, false);
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1);
                rect.anchoredPosition = new Vector2(0, -12);
                rect.sizeDelta = new Vector2(360, 36);
                var group = panel.GetComponent<CanvasGroup>();
                group.interactable = false;
                group.blocksRaycasts = false;
                var bg = panel.AddComponent<Image>();
                bg.sprite = MachineryStyle.ButtonNormal;
                bg.type = Image.Type.Sliced;
                bg.color = MachineryStyle.PANEL;
                bg.raycastTarget = false;
                label = manager.BuildInto(rect).UseMachineryStyle(true).AddLabel("", "message").Last;
                label.text.alignment = TMPro.TextAlignmentOptions.Center;
                label.text.richText = false;
                label.text.color = MachineryStyle.BODY_TEXT;
                label.text.fontSize = 15;
                label.text.enableAutoSizing = false;
                label.text.enableWordWrapping = true;
                label.text.margin = new Vector4(12, 4, 12, 4);
            }
            panel.SetActive(true);
            panel.transform.SetAsLastSibling();
            label.SetText(notice.Message);
            float width = Mathf.Clamp(label.text.GetPreferredValues(notice.Message).x + 28, 200, UiLayout.Fit(560, 100).x);
            ((RectTransform)panel.transform).sizeDelta = new Vector2(width, Mathf.Max(36, label.text.GetPreferredValues(notice.Message, width - 24, 0).y + 12));
            activeNotice = notice; currentKind = notice.Kind; born = Time.unscaledTime;
            until = born + notice.Seconds;
        }

        internal static void Report(OperationResult result)
        {
            if (result == null || !ShopInventory.InStore) return;
            Publish(result, null);
        }

        internal static void ReportField(OperationResult result, GameInventory ground, Action refresh = null)
        {
            if (result == null || !PlayerStore.HasInstance || string.IsNullOrEmpty(PlayerStore.Instance.runID) || !FieldScope(ground)) return;
            Publish(result, ground, refresh);
        }

        private static void Publish(OperationResult result, GameInventory ground, Action refresh = null)
        {
            AttentionBook.Report(result, true);
            lastResult = result; resultRun = PlayerStore.Instance.runID; resultSlot = PlayerStore.Instance.saveSlotId;
            resultGround = ground;
            refresh?.Invoke();
            if (resultWindow?.IsAlive == true && resultWindow.root.activeInHierarchy) OpenLastResult();
            string summary = ShopSettings.Format("operation.summary", ShopSettings.Text(result.ActionKey), result.Completed, result.Remaining);
            if (result.NeedsInspection) summary += " · " + ShopSettings.Text("operation.inspect");
            else
            {
                var reasons = result.Items.Where(item => item.Status != OperationItemStatus.Completed && item.Reason != null)
                    .Select(item => item.Reason).Distinct().ToArray();
                if (reasons.Length == 1) summary += " · " + ShopSettings.Text(reasons[0]);
            }
            ShowEvent(result.EventId, result.NeedsInspection ? NoticeKind.Error : result.Remaining > 0 ? NoticeKind.Warning : NoticeKind.Status,
                result.ActionKey, summary, result.Remaining > 0 ? 7f : 4f);
        }

        internal static void OpenLastResult()
        {
            if (!HasResult || CustomUIManager.Instance == null) return;
            OperationResult result = lastResult;
            var builder = UiLayout.Rebuild(ref resultWindow, ModEntry.Id + ".operation", ShopSettings.Text("operation.details"), 620, 420);
            CustomUIWindow own = resultWindow;
            builder.OnClose(() => { if (ReferenceEquals(resultWindow, own)) resultWindow = null; });
            builder.AddLabel(ShopSettings.Format("operation.summary", ShopSettings.Text(result.ActionKey), result.Completed, result.Remaining));
            if (!string.IsNullOrEmpty(result.Detail))
            { builder.AddLabel(result.Detail); if (builder.Last.text != null) builder.Last.text.richText = false; }
            builder.BeginScrollFlexible(150);
            foreach (var item in result.Items)
            {
                builder.AddLabel(ShopSettings.Format("operation.item", item.Name, item.Quantity,
                    item.Status == OperationItemStatus.Completed ? ShopSettings.Text("operation.done") : ShopSettings.Text(item.Reason ?? "availability.unknown")));
                if (builder.Last.text != null) builder.Last.text.richText = false;
                if (!string.IsNullOrEmpty(item.Location))
                { builder.AddLabel(item.Location); if (builder.Last.text != null) builder.Last.text.richText = false; }
            }
            builder.End();
            if (result.NeedsInspection && ShopInventory.InStore) builder.AddButton(ShopSettings.Text("attention.open"), AttentionBook.Open, "attention");
            builder.Show();
        }

        internal static void Tick()
        {
            if (panel != null && panel.activeSelf && Time.unscaledTime >= until)
            {
                panel.SetActive(false);
                if (interrupted.Count > 0) Display(interrupted.Pop());
                else if (pending.Count > 0)
                {
                    Display(pending.Dequeue());
                }
            }
            if (panel != null && panel.activeSelf)
                panel.GetComponent<CanvasGroup>().alpha = Mathf.Min(Mathf.Clamp01((Time.unscaledTime - born) / .12f),
                    Mathf.Clamp01((until - Time.unscaledTime) / .25f));
            if (resultWindow?.IsAlive == true && !HasResult) { resultWindow.Close(); resultWindow = null; }
        }

        internal static void ResetSession()
        {
            pending.Clear(); interrupted.Clear(); activeNotice = null; seen.Clear(); recent.Clear(); until = 0; lastResult = null; resultRun = null; resultGround = null;
            if (panel != null) panel.SetActive(false);
            CustomUIWindow previous = resultWindow; resultWindow = null; previous?.Close();
        }

        internal static void Clear()
        {
            ResetSession();
            if (panel != null) UnityEngine.Object.Destroy(panel);
            panel = null;
            label = null;
        }
    }
}
