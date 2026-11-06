using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace ShopAssist
{
    // A receipt belongs to a native transaction, never to its formatted text.
    internal static class TradeFeedback
    {
        private sealed class Receipt
        {
            internal long Id;
            internal string Run, FactionId, FactionName;
            internal int Slot;
            internal long Cash;
            internal double Reputation;
            internal bool Money, Rep;
            internal bool PlaySound;
            internal int Volume;
            internal float Born, Seconds;
            internal TreeNodeRender Main, Detail;
            internal Color MainColor, DetailColor;
        }

        private static readonly List<Receipt> visible = new List<Receipt>();
        private static readonly Queue<Receipt> pending = new Queue<Receipt>();
        private static readonly HashSet<long> seen = new HashSet<long>();
        private static readonly Queue<long> recent = new Queue<long>();
        internal static int Count => visible.Count;

        internal static void Show(long eventId, long cash, StoreReputation faction, double reputation,
            bool money, bool rep, float seconds, bool playSound, int volume)
        {
            if (!PlayerStore.HasInstance || RenderHandler.current?.overlayRoot == null || !seen.Add(eventId)) return;
            recent.Enqueue(eventId);
            while (recent.Count > 256) seen.Remove(recent.Dequeue());
            money &= cash != 0;
            rep &= faction != null && Math.Abs(reputation) >= 0.000001;
            if (!money && !rep) return;
            var receipt = new Receipt { Id = eventId, Run = PlayerStore.Instance.runID, Slot = PlayerStore.Instance.saveSlotId,
                Cash = cash, Reputation = reputation, Money = money, Rep = rep, FactionId = faction?.factionId,
                FactionName = faction?.factionDisplay, Seconds = Mathf.Clamp(seconds, 1, 12), PlaySound = playSound, Volume = volume };
            pending.Enqueue(receipt);
            Tick();
        }

        private static void Display(Receipt receipt)
        {
            receipt.Born = Time.unscaledTime;
            bool money = receipt.Money, rep = receipt.Rep;
            long cash = receipt.Cash;
            receipt.MainColor = money ? cash > 0 ? new Color(.8f, .94f, .71f) : new Color(1f, .69f, .61f) : FactionColor(receipt.FactionId);
            receipt.DetailColor = FactionColor(receipt.FactionId);
            string amount = money ? ShopSettings.Format(cash > 0 ? "f16.in" : "f16.out", Math.Abs(cash)) : RepText(receipt);
            receipt.Main = Make(ModEntry.Id + ".receipt." + receipt.Id.ToString("D12"), amount, 18, receipt.MainColor);
            if (money && rep)
            {
                receipt.Detail = Make("ShopAssist_TradeReputation", RepText(receipt), 12, receipt.DetailColor);
                receipt.Detail.SetParent(receipt.Main);
                receipt.Detail.rectTransform.anchorMin = receipt.Detail.rectTransform.anchorMax = new Vector2(.5f, .5f);
                receipt.Detail.rectTransform.anchoredPosition = new Vector2(0, -19);
            }
            visible.Add(receipt);
            if (receipt.PlaySound && receipt.Volume > 0)
            {
                var source = EmporiumAudioPlayer.Instance?.audioRegister;
                if (source?.clip != null) source.PlayOneShot(source.clip, Mathf.Clamp01(receipt.Volume / 100f));
            }
        }

        private static string RepText(Receipt receipt)
        {
            double value = receipt.Reputation;
            string number = Math.Abs(value) < .005 ? (value > 0 ? "+<0.01" : "-<0.01") : value.ToString("+0.##;-0.##", CultureInfo.CurrentCulture);
            return receipt.FactionName + " " + number;
        }

        private static Color FactionColor(string id)
        {
            switch (id)
            {
                case "FACTION_SECURITY": return new Color(.98f, .45f, .4f);
                case "FACTION_REVOLUTION": return new Color(.45f, .9f, .52f);
                case "FACTION_UPPER_LEVEL": case "FACTION_TOURIST": return new Color(1f, .64f, .8f);
                case "FACTION_BLACK_MARKET": return new Color(.68f, .66f, .78f);
                default: return new Color(.85f, .85f, .82f);
            }
        }

        private static TreeNodeRender Make(string name, string text, float size, Color color)
        {
            TreeNodeRender node = TreeNodeRender.InstantiateText();
            node.SetParent(RenderHandler.current.overlayRoot);
            node.gameObject.name = name;
            if (node.image != null) { node.image.enabled = false; node.image.raycastTarget = false; }
            node.text.enabled = true;
            node.text.text = text; node.text.fontSize = size;
            node.text.enableAutoSizing = false; node.text.enableWordWrapping = false;
            node.text.richText = false; node.text.raycastTarget = false;
            node.text.alignment = TextAlignmentOptions.Center;
            node.text.color = color;
            node.text.outlineWidth = .15f; node.text.outlineColor = new Color32(24, 20, 30, 255);
            node.rectTransform.pivot = new Vector2(.5f, .5f);
            node.rectTransform.sizeDelta = new Vector2(300, 28);
            return node;
        }

        internal static void Tick()
        {
            for (int i = visible.Count - 1; i >= 0; i--)
            {
                Receipt receipt = visible[i];
                if (!PlayerStore.HasInstance || receipt.Run != PlayerStore.Instance.runID || receipt.Slot != PlayerStore.Instance.saveSlotId ||
                    !ShopInventory.InStore || receipt.Main == null || Time.unscaledTime - receipt.Born >= receipt.Seconds)
                { Destroy(receipt); visible.RemoveAt(i); }
            }
            while (pending.Count > 0 && visible.Count < 4)
            {
                Receipt receipt = pending.Dequeue();
                if (PlayerStore.HasInstance && ShopInventory.InStore && receipt.Run == PlayerStore.Instance.runID &&
                    receipt.Slot == PlayerStore.Instance.saveSlotId && RenderHandler.current?.overlayRoot != null)
                    Display(receipt);
            }
            if (visible.Count == 0) return;
            var renderer = RenderHandler.current;
            var overlay = renderer != null ? renderer.overlayRoot : null;
            if (overlay == null) { Clear(); return; }
            Canvas hostCanvas = overlay.GetComponentInParent<Canvas>();
            Canvas canvas = hostCanvas != null ? hostCanvas.rootCanvas : null;
            if (canvas == null) return;
            var manager = CustomUIManager.Instance;
            RectTransform layer = manager != null ? manager.GetLayerRoot(CustomUIManager.LAYER_META) : null;
            Canvas controlHost = layer != null ? layer.GetComponentInParent<Canvas>() : null;
            Canvas controls = controlHost != null ? controlHost.rootCanvas : null;
            // The game overlay scales more strongly than its interface canvas. Keep receipt
            // typography at control scale instead of enlarging it with the pixel-art scene.
            float textScale = Mathf.Max(1f, controls?.scaleFactor ?? 1f) / Mathf.Max(.01f, canvas.scaleFactor);
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var screen = new Vector2(LetterboxManager.GameAreaX + LetterboxManager.GameAreaWidth * .5f,
                LetterboxManager.GameAreaY + LetterboxManager.GameAreaHeight * .575f);
            for (int i = 0; i < visible.Count; i++)
            {
                Receipt receipt = visible[i];
                float age = Time.unscaledTime - receipt.Born;
                float alpha = Mathf.Min(Mathf.Clamp01(age / .12f), Mathf.Clamp01((receipt.Seconds - age) / .45f));
                float drift = Mathf.Min(age, 1f) * (receipt.Reputation < 0 && !receipt.Money ? -7f : 7f);
                receipt.Main.rectTransform.localScale = Vector3.one * textScale;
                if (receipt.Detail != null) receipt.Detail.rectTransform.localScale = Vector3.one;
                float offset = ((visible.Count - 1 - i) * 42f + drift) * textScale;
                Put(receipt.Main, screen, offset, camera, receipt.MainColor, alpha);
                if (receipt.Detail != null) { Color detail = receipt.DetailColor; detail.a = alpha; receipt.Detail.text.color = detail; }
            }
        }

        private static void Put(TreeNodeRender node, Vector2 screen, float offset, Camera camera, Color color, float alpha)
        {
            if (node == null || !(node.rectTransform.parent is RectTransform parent)) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen, camera, out Vector2 local))
            {
                node.rectTransform.anchorMin = node.rectTransform.anchorMax = parent.pivot;
                node.rectTransform.anchoredPosition = local + new Vector2(0, offset);
            }
            color.a = alpha; node.text.color = color;
        }

        private static void Destroy(Receipt receipt)
        {
            foreach (TreeNodeRender node in new[] { receipt.Main, receipt.Detail })
                if (node != null) { node.gameObject.SetActive(false); UnityEngine.Object.Destroy(node.gameObject); }
        }

        internal static void Clear()
        {
            foreach (Receipt receipt in visible) Destroy(receipt);
            visible.Clear(); pending.Clear(); seen.Clear(); recent.Clear();
        }
    }
}
