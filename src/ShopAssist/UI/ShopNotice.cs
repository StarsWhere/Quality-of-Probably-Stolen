using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ShopAssist
{
    internal static class ShopNotice
    {
        private static GameObject panel;
        private static CustomUIElement label;
        private static float until;
        private sealed class Notice { internal string Message; internal float Seconds; }
        private static readonly Queue<Notice> pending = new Queue<Notice>();
        private static string current;
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
            if (string.IsNullOrWhiteSpace(message)) return;
            if (panel != null && panel.activeSelf && Time.unscaledTime < until)
            {
                if (current != message && !pending.Any(notice => notice.Message == message))
                    pending.Enqueue(new Notice { Message = message, Seconds = Mathf.Clamp(seconds, 1f, 30f) });
                return;
            }
            if (pending.Count > 0)
            {
                if (!pending.Any(notice => notice.Message == message)) pending.Enqueue(new Notice { Message = message, Seconds = seconds });
                Notice next = pending.Dequeue(); Display(next.Message, next.Seconds);
            }
            else Display(message, seconds);
        }

        private static void Display(string message, float seconds)
        {
            var manager = CustomUIManager.Instance;
            if (manager == null || string.IsNullOrWhiteSpace(message)) return;
            if (panel == null)
            {
                var layer = manager.GetLayerRoot(CustomUIManager.LAYER_META);
                if (layer == null) return;
                panel = new GameObject(ModEntry.Id + ".notice", typeof(RectTransform), typeof(CanvasGroup));
                var rect = (RectTransform)panel.transform;
                rect.SetParent(layer, false);
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1);
                rect.anchoredPosition = new Vector2(0, -12);
                rect.sizeDelta = new Vector2(420, 54);
                var group = panel.GetComponent<CanvasGroup>();
                group.interactable = false;
                group.blocksRaycasts = false;
                var bg = panel.AddComponent<Image>();
                bg.color = new Color(0.09f, 0.10f, 0.12f, 0.88f);
                bg.raycastTarget = false;
                label = manager.BuildInto(rect).AddLabel("", "message").Last;
                label.text.alignment = TMPro.TextAlignmentOptions.Center;
                label.text.richText = false;
                label.text.color = Color.white;
            }
            panel.SetActive(true);
            panel.transform.SetAsLastSibling();
            label.SetText(message);
            current = message;
            until = Time.unscaledTime + Mathf.Clamp(seconds, 1f, 30f);
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
            refresh?.Invoke();
            lastResult = result; resultRun = PlayerStore.Instance.runID; resultSlot = PlayerStore.Instance.saveSlotId;
            resultGround = ground;
            string summary = ShopSettings.Format("operation.summary", ShopSettings.Text(result.ActionKey), result.Completed, result.Remaining);
            if (result.NeedsInspection) summary += " · " + ShopSettings.Text("operation.inspect");
            else
            {
                var reasons = result.Items.Where(item => item.Status != OperationItemStatus.Completed && item.Reason != null)
                    .Select(item => item.Reason).Distinct().ToArray();
                if (reasons.Length == 1) summary += " · " + ShopSettings.Text(reasons[0]);
            }
            Show(summary, result.Remaining > 0 ? 7f : 4f);
            if (result.Remaining > 0 && (GenericConfirmationUIManager.Instance == null || !GenericConfirmationUIManager.Instance.panel.activeInHierarchy))
                OpenLastResult();
        }

        internal static void OpenLastResult()
        {
            if (!HasResult || CustomUIManager.Instance == null) return;
            OperationResult result = lastResult;
            var builder = CustomUIManager.Window(ModEntry.Id + ".operation", ShopSettings.Text("operation.details"), CustomUIManager.LAYER_META)
                .SetSize(620, 420).SetDraggable(true).SetCloseOnEscape(true);
            resultWindow = builder.GetWindow(); CustomUIWindow own = resultWindow;
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
                panel.SetActive(false); current = null;
                if (pending.Count > 0)
                {
                    Notice next = pending.Dequeue(); Display(next.Message, next.Seconds);
                }
            }
            if (resultWindow?.IsAlive == true && !HasResult) { resultWindow.Close(); resultWindow = null; }
        }

        internal static void ResetSession()
        {
            pending.Clear(); current = null; until = 0; lastResult = null; resultRun = null; resultGround = null;
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
