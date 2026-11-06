using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    internal sealed class BatchLabeler : IDisposable
    {
        private static BatchLabeler active;
        internal static bool CanOpenFor(GameItem item) => active?.enabled.Value == true && active.HasTool &&
            active.phase == 0 && (item == active.sample || active.Eligible(item));
        internal static void OpenFor(GameItem item) { if (CanOpenFor(item)) active.Draw(item == active.sample ? null : item); }
        private readonly ShopSettings settings;
        private readonly ConfigEntry<bool> enabled, overwrite;
        private readonly Func<KeyCode> key;
        private GameItem tool, sample, editing;
        private readonly Queue<GameItem> queue = new Queue<GameItem>();
        private readonly Dictionary<GameItem, GameInventory> homes = new Dictionary<GameItem, GameInventory>();
        private CustomUIWindow window;
        private int phase, done, skipped;
        private string name;

        internal BatchLabeler(ShopSettings settings)
        {
            this.settings = settings;
            enabled = settings.Toggle("f22.enabled", "f22.tip");
            overwrite = settings.Toggle("f22.overwrite", "f22.overwrite.tip");
            key = settings.Key("f22.key", "f22.key.tip");
            settings.Action("f22.open", Draw, Availability);
        }
        internal void Start()
        {
            active = this;
            ModHook.OnGameLoadedInit += Reset; ModHook.OnLeavingStoreEarly += Reset; settings.Changed += Changed;
        }
        private void Changed() { if (!enabled.Value) Reset(); }
        private bool Blue => tool?.identifier == "labeler_blue";
        private bool HasTool => ShopInventory.InStore && ShopInventory.Owned(tool) && ShopInventory.Owned(sample) &&
            ItemSelectHandler.current != null && (ItemSelectHandler.current.GetEquippedItem() == null ||
                ItemSelectHandler.current.GetEquippedItem() == tool);
        private ActionAvailability Availability()
        {
            if (!enabled.Value) return ActionAvailability.Blocked(ActionUnavailableReason.Disabled, "availability.disabled", "f22.enabled");
            if (!ShopInventory.InStore) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.store");
            if (!ShopInventory.Owned(tool)) return ActionAvailability.Blocked(ActionUnavailableReason.MissingResource, "availability.labeler");
            if (!ShopInventory.Owned(sample)) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.label_sample");
            if (ItemSelectHandler.current == null) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.object");
            var equipped = ItemSelectHandler.current.GetEquippedItem();
            return equipped == null || equipped == tool ? ActionAvailability.Ready :
                ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.label_equipped");
        }
        private bool Eligible(GameItem item) => item != null && item != sample && item != tool && item.identifier == sample.identifier &&
            ShopInventory.Movable(item) && !RequestBook.Reserved(item) && tool.MayTarget(item) && tool.CanTarget(item) &&
            (Blue ? item.identifier != "dossier" && (overwrite.Value || !GeneralHelper.HasCustomName(item)) :
                LabelerHelper.GetEditableLabelCount(item) > 0 && (overwrite.Value ||
                item.itemFeatures.All(f => !LabelerHelper.IsEditableLabelFeature(f) || (f.fakeCondition == null && !f.IsExposed()))));

        [HarmonyPatch(typeof(GameItem), nameof(GameItem.Target))]
        private static class ManualSample
        {
            private static void Postfix(GameItem __instance, GameItem targetItem)
            {
                var a = active;
                if (a == null || !a.enabled.Value || a.phase != 0 ||
                    (__instance.identifier != "labeler" && __instance.identifier != "labeler_blue") || !ShopInventory.Owned(targetItem)) return;
                bool visible = __instance.identifier == "labeler_blue" ? LabelerBlueUIManager.Instance?.panel.activeSelf == true :
                    LabelerUIManager.Instance?.panel.activeSelf == true;
                if (visible) { a.tool = __instance; a.sample = targetItem; }
            }
        }

        private void Draw() => Draw(null);

        private void Draw(GameItem initial)
        {
            if (!enabled.Value || !ShopInventory.InStore || phase != 0) return;
            var b = UiLayout.Rebuild(ref window, ModEntry.Id + ".label", ShopSettings.Text("f22.open"), 475, 365);
            b.OnClose(() => window = null);
            if (!HasTool || (Blue && !GeneralHelper.HasCustomName(sample)))
            { b.AddLabel(ShopSettings.Text("f22.sample")).Show(); return; }
            var selection = initial == null ? ShopSelection.Current() : new List<GameItem> { initial };
            var selected = new HashSet<GameItem>(selection);
            var places = ShopInventory.OwnedLocations(true).Where(l => l.Ordinary).ToList();
            var items = initial == null
                ? places.SelectMany(l => l.Inventory.childItems).Where(i =>
                    (selected.Count == 0 || selected.Contains(i)) && Eligible(i)).Take(128).ToList()
                : places.Any(l => l.Inventory == initial.parentInventory && l.Inventory.childItems.Contains(initial)) && Eligible(initial)
                    ? new List<GameItem> { initial } : new List<GameItem>();
            b.SetSize(475, Math.Max(245, Math.Min(480, 145 + ((items.Count + 2) / 3) * 100)));
            name = Blue ? GeneralHelper.GetCustomName(sample) : null;
            b.AddLabel(ShopSettings.Format(Blue ? "f22.copyName" : "f22.repeat", Blue ? name : sample.GetDisplayName(true)));
            if (items.Count == 0) { b.AddLabel(ShopSettings.Text("f22.none")).Show(); return; }
            var picked = new HashSet<GameItem>();
            var sources = new Dictionary<GameItem, GameInventory>();
            b.BeginScrollFlexible(110);
            foreach (var item in items) { sources[item] = item.parentInventory; if (selected.Contains(item)) picked.Add(item); }
            ItemGrid.Add(b, items.Select(item => new ItemGrid.Entry(item)
            { Selected = picked.Contains(item), Caption = item.GetDisplayName(true), Reason = places.Find(l => l.Inventory == item.parentInventory).Name }),
                new ItemGrid.Options { Cards = true, Columns = 3, CardWidth = 135, CardHeight = 100,
                    OnClick = entry => { if (!picked.Add(entry.Item)) picked.Remove(entry.Item); entry.Selected = picked.Contains(entry.Item); } });
            b.End().AddButton(ShopSettings.Text("f22.apply"), () =>
            {
                if (!enabled.Value || !HasTool || phase != 0 || picked.Count == 0 || (Blue && name != GeneralHelper.GetCustomName(sample))) return;
                done = skipped = 0; homes.Clear(); queue.Clear();
                foreach (var i in items.Where(picked.Contains)) { queue.Enqueue(i); homes[i] = sources[i]; }
                window?.Close(); phase = 1;
            }, "apply").Show();
        }

        internal void Tick()
        {
            if (!enabled.Value) return;
            if (phase == 0)
            {
                if (key() != KeyCode.None && ShopInput.Ready() && Input.GetKeyDown(key())) Draw();
                return;
            }
            // Opening a native paper or custom window clears the equipped tool.
            // The user's recorded template still identifies the real labeler;
            // choosing another tool or cancelling the batch revokes that context.
            if (!HasTool || Input.GetKeyDown(KeyCode.Escape)) { Reset(); return; }
            if (phase == 1)
            {
                if (queue.Count == 0) { phase = 0; homes.Clear(); ShopNotice.Show(ShopSettings.Format("f22.result", done, skipped)); return; }
                editing = queue.Dequeue();
                if (editing.parentInventory != homes[editing] || !Eligible(editing)) { skipped++; editing = null; return; }
                tool.Target(editing);
                phase = 2;
                return;
            }
            bool valid = editing != null && editing.parentInventory == homes[editing] && Eligible(editing);
            if (Blue)
            {
                var ui = LabelerBlueUIManager.Instance;
                if (ui != null && ui.panel.activeSelf)
                {
                    if (valid && ui.inputField != null && (ui.inputField.characterLimit <= 0 || name.Length <= ui.inputField.characterLimit))
                    {
                        ui.inputField.SetTextWithoutNotify(name);
                        ui.OnEditEnd(name);
                        valid = GeneralHelper.GetCustomName(editing) == name;
                    }
                    else valid = false;
                    ui.CloseUI();
                }
                else valid = false;
            }
            else
            {
                var ui = LabelerUIManager.Instance;
                if (ui == null || !ui.panel.activeSelf) valid = false;
                else if (phase == 2 && valid && ui.CanRepeat())
                {
                    ui.Repeat(); phase = 3; return;
                }
                else
                {
                    // Repeat rebuilds its rows. Wait a frame before native CloseUI saves those labels.
                    valid = phase == 3 && editing.parentInventory == homes[editing];
                    ui.CloseUI();
                }
            }
            if (valid) done++; else skipped++;
            editing = null; phase = 1;
        }
        private void Reset()
        {
            if (phase >= 2 && editing != null)
            {
                if (Blue && LabelerBlueUIManager.Instance?.panel.activeSelf == true) LabelerBlueUIManager.Instance.CloseUI();
                if (!Blue && LabelerUIManager.Instance?.panel.activeSelf == true) LabelerUIManager.Instance.CloseUI();
            }
            if (window != null && window.IsAlive) window.Close();
            window = null; queue.Clear(); homes.Clear(); editing = tool = sample = null; phase = 0;
        }
        public void Dispose()
        {
            if (active == this) active = null;
            ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Reset; settings.Changed -= Changed; Reset();
        }
    }
}
