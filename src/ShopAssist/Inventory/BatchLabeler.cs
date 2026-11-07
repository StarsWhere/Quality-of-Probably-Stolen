using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    internal sealed class BatchLabeler : IDisposable
    {
        private static readonly FieldInfo LabelTarget = AccessTools.Field(typeof(LabelerUIManager), "curItem");
        private static readonly FieldInfo BlueLabelTarget = AccessTools.Field(typeof(LabelerBlueUIManager), "curItem");
        private static BatchLabeler active;
        internal static bool CanOpenFor(GameItem item) => active?.enabled.Value == true && active.HasTool &&
            active.phase == 0 && (item == active.sample || active.Eligible(item));
        internal static bool AppliesTo(GameItem item) => active?.sample != null && item != null &&
            ShopInventory.Owned(item) && item.identifier == active.sample.identifier;
        internal static ActionAvailability AvailabilityFor(GameItem item)
        {
            if (active == null || !AppliesTo(item)) return ActionAvailability.Blocked(ActionUnavailableReason.MissingObject, "availability.object");
            ActionAvailability availability = active.Availability();
            if (!availability.Allowed) return availability;
            if (active.phase != 0) return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.label_busy");
            if (item == active.sample) return ActionAvailability.Ready;
            if (RequestBook.Reserved(item)) return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.item_reserved");
            if (!ShopInventory.Movable(item)) return ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.item_movable");
            return active.Eligible(item) ? ActionAvailability.Ready :
                ActionAvailability.Blocked(ActionUnavailableReason.Conflict, "availability.label_target");
        }
        internal static void OpenFor(GameItem item) { if (CanOpenFor(item)) active.Draw(item == active.sample ? null : item); }
        private readonly ShopSettings settings;
        private readonly ConfigEntry<bool> enabled, overwrite;
        private readonly Func<KeyCode> key;
        private GameItem tool, sample, editing;
        private readonly Queue<GameItem> queue = new Queue<GameItem>();
        private readonly Dictionary<GameItem, GameInventory> homes = new Dictionary<GameItem, GameInventory>();
        private readonly List<GameItem> candidates = new List<GameItem>();
        private readonly HashSet<GameItem> picked = new HashSet<GameItem>();
        private CustomUIWindow window;
        private OperationResult batchResult;
        private int phase, done, skipped, page;
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
        private static bool AnyEditorOpen => LabelerUIManager.Instance?.panel?.activeSelf == true ||
            LabelerBlueUIManager.Instance?.panel?.activeSelf == true;
        private bool OwnsEditor()
        {
            if (editing == null) return false;
            if (Blue)
            {
                var ui = LabelerBlueUIManager.Instance;
                return ui?.panel?.activeSelf == true && BlueLabelTarget != null && ReferenceEquals(BlueLabelTarget.GetValue(ui), editing);
            }
            var standard = LabelerUIManager.Instance;
            return standard?.panel?.activeSelf == true && LabelTarget != null && ReferenceEquals(LabelTarget.GetValue(standard), editing);
        }
        private static bool VisibleItem(GameItem item) => item is GameItemElement element && VisibleInventoryViews.Visible(element);
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
                var blue = LabelerBlueUIManager.Instance;
                var standard = LabelerUIManager.Instance;
                bool visible = __instance.identifier == "labeler_blue" ? blue?.panel?.activeSelf == true && BlueLabelTarget != null &&
                    ReferenceEquals(BlueLabelTarget.GetValue(blue), targetItem) : standard?.panel?.activeSelf == true && LabelTarget != null &&
                    ReferenceEquals(LabelTarget.GetValue(standard), targetItem);
                if (visible) { a.tool = __instance; a.sample = targetItem; }
            }
        }

        private void Draw() => Draw(null);

        private void Draw(GameItem initial)
        {
            if (!enabled.Value || !ShopInventory.InStore || phase != 0) return;
            if (!HasTool || (Blue && !GeneralHelper.HasCustomName(sample)))
            {
                var missing = UiLayout.Rebuild(ref window, ModEntry.Id + ".label", ShopSettings.Text("f22.open"), 475, 245);
                missing.OnClose(() => window = null);
                missing.AddLabel(ShopSettings.Text("f22.sample")).Show(); return;
            }
            var selection = initial == null ? ShopSelection.Current() : new List<GameItem> { initial };
            var selected = new HashSet<GameItem>(selection);
            var places = ShopInventory.OwnedLocations(true).Where(ShopInventory.VisibleGoods).ToList();
            candidates.Clear(); picked.Clear(); homes.Clear(); page = 0;
            var items = places.SelectMany(l => l.Inventory.childItems).Where(item => VisibleItem(item) && Eligible(item)).Distinct().ToList();
            foreach (GameItem item in items)
            {
                candidates.Add(item); homes[item] = item.parentInventory;
                if (selected.Contains(item)) picked.Add(item);
            }
            name = Blue ? GeneralHelper.GetCustomName(sample) : null;
            Render();
        }

        private void Render()
        {
            if (!enabled.Value || !ShopInventory.InStore || phase != 0) return;
            const int perPage = 12;
            page = Math.Max(0, Math.Min(page, (candidates.Count - 1) / perPage));
            var b = UiLayout.Rebuild(ref window, ModEntry.Id + ".label", ShopSettings.Text("f22.open"), 475, 480);
            CustomUIWindow own = window;
            b.OnClose(() => { if (ReferenceEquals(window, own)) { window = null; candidates.Clear(); picked.Clear(); } });
            b.AddLabel(ShopSettings.Format(Blue ? "f22.copyName" : "f22.repeat", Blue ? name : sample.GetDisplayName(true)));
            if (candidates.Count == 0) { b.AddLabel(ShopSettings.Text("f22.none")).Show(); return; }
            b.BeginRow().AddButton(ShopSettings.Text("f22.select_all"), () =>
            {
                foreach (GameItem item in candidates) picked.Add(item);
                Render();
            }, "select_all").AddButton(ShopSettings.Text("f22.clear"), () => { picked.Clear(); Render(); }, "clear")
                .AddLabel(ShopSettings.Format("f22.selected", picked.Count, candidates.Count), "selected").End();
            b.BeginScrollFlexible(110);
            var places = ShopInventory.OwnedLocations(true).Where(ShopInventory.VisibleGoods).ToList();
            ItemGrid.Add(b, candidates.Skip(page * perPage).Take(perPage).Select(item => new ItemGrid.Entry(item)
            { Selected = picked.Contains(item), Caption = item.GetDisplayName(true), Reason = places.Find(l => l.Inventory == homes[item])?.Name }),
                new ItemGrid.Options { Cards = true, Columns = 3, CardWidth = 135, CardHeight = 100,
                    OnClick = entry =>
                    {
                        if (!picked.Add(entry.Item)) picked.Remove(entry.Item);
                        entry.Selected = picked.Contains(entry.Item);
                        window?.Get("selected")?.SetText(ShopSettings.Format("f22.selected", picked.Count, candidates.Count));
                        window?.Get("apply")?.SetInteractable(picked.Count != 0);
                    } });
            b.End();
            UiLayout.Pager(b, page, candidates.Count, perPage, next => { page = next; Render(); });
            b.AddButton(ShopSettings.Text("f22.apply"), () =>
            {
                if (!enabled.Value || !HasTool || phase != 0 || picked.Count == 0 || (Blue && name != GeneralHelper.GetCustomName(sample))) return;
                done = skipped = 0; queue.Clear(); batchResult = new OperationResult("f22.open");
                foreach (GameItem item in candidates.Where(picked.Contains)) queue.Enqueue(item);
                phase = 1; window?.Close();
            }, "apply");
            b.Last.SetInteractable(picked.Count != 0);
            b.Show();
        }

        internal void Tick()
        {
            try { TickBatch(); }
            catch (Exception error)
            {
                Debug.LogError("[ShopAssist] Batch labeling stopped: " + error);
                if (editing != null && batchResult != null)
                {
                    batchResult.Skip(editing, 1, "inv.inspect", needsInspection: true);
                    skipped++;
                }
                if (phase >= 2 && OwnsEditor())
                {
                    if (Blue) LabelerBlueUIManager.Instance.CloseUI();
                    else LabelerUIManager.Instance.CloseUI();
                }
                editing = null;
                Reset();
            }
        }

        private void TickBatch()
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
                if (queue.Count == 0)
                {
                    phase = 0; homes.Clear();
                    if (skipped > 0 && batchResult != null) ShopNotice.Report(batchResult);
                    else ShopNotice.Show(ShopSettings.Format("f22.result", done, skipped));
                    batchResult = null; return;
                }
                if (AnyEditorOpen) { Reset(); return; }
                editing = queue.Dequeue();
                if (editing == null || !homes.TryGetValue(editing, out GameInventory home) || editing.parentInventory != home ||
                    !VisibleItem(editing) || !Eligible(editing))
                { skipped++; batchResult?.Skip(editing, 1, "inv.changed"); editing = null; return; }
                phase = 2;
                tool.Target(editing);
                return;
            }
            if (!OwnsEditor())
            {
                // Repeat writes immediately; if another editor took over before
                // the save/close step, the prior target needs a manual check.
                if (phase == 3 && editing != null)
                { batchResult?.Skip(editing, 1, "inv.inspect", needsInspection: true); skipped++; editing = null; }
                Reset(); return;
            }
            bool valid = editing != null && homes.TryGetValue(editing, out GameInventory currentHome) &&
                editing.parentInventory == currentHome && VisibleItem(editing) && (phase == 3 || Eligible(editing));
            if (Blue)
            {
                var ui = LabelerBlueUIManager.Instance;
                if (ui != null && ui.panel.activeSelf)
                {
                    if (valid && ui.inputField != null && (ui.inputField.characterLimit <= 0 || name.Length <= ui.inputField.characterLimit))
                    {
                        ui.inputField.SetTextWithoutNotify(name);
                        if (!OwnsEditor()) { Reset(); return; }
                        ui.OnEditEnd(name);
                        if (!OwnsEditor())
                        { batchResult?.Skip(editing, 1, "inv.inspect", needsInspection: true); skipped++; editing = null; Reset(); return; }
                        valid = GeneralHelper.GetCustomName(editing) == name;
                    }
                    else valid = false;
                    if (OwnsEditor()) ui.CloseUI();
                }
                else valid = false;
            }
            else
            {
                var ui = LabelerUIManager.Instance;
                if (ui == null || !ui.panel.activeSelf) valid = false;
                else if (phase == 2 && valid && ui.CanRepeat())
                {
                    phase = 3; ui.Repeat();
                    if (!OwnsEditor())
                    { batchResult?.Skip(editing, 1, "inv.inspect", needsInspection: true); skipped++; editing = null; Reset(); }
                    return;
                }
                else
                {
                    // Repeat rebuilds its rows. Wait a frame before native CloseUI saves those labels.
                    valid = phase == 3 && valid;
                    if (OwnsEditor()) ui.CloseUI();
                }
            }
            if (valid) { done++; batchResult?.Complete(editing, 1); }
            else { skipped++; batchResult?.Skip(editing, 1, phase == 3 ? "inv.inspect" : "inv.changed", needsInspection: phase == 3); }
            editing = null; phase = 1;
        }
        private void Reset()
        {
            bool interrupted = phase != 0 && batchResult != null;
            if (phase >= 2 && editing != null)
            {
                if (Blue && OwnsEditor()) LabelerBlueUIManager.Instance.CloseUI();
                if (!Blue && OwnsEditor())
                {
                    LabelerUIManager.Instance.CloseUI();
                    if (phase == 3 && homes.TryGetValue(editing, out GameInventory home) && editing.parentInventory == home)
                    { done++; batchResult?.Complete(editing, 1); editing = null; }
                }
                if (phase == 3 && editing != null)
                { batchResult?.Skip(editing, 1, "inv.inspect", needsInspection: true); skipped++; editing = null; }
            }
            if (interrupted)
            {
                if (editing != null) batchResult.Skip(editing, 1, "operation.not_attempted");
                foreach (GameItem item in queue) batchResult.Skip(item, 1, "operation.not_attempted");
                batchResult.Detail = ShopSettings.Format("f22.interrupted", done, skipped, queue.Count + (editing == null ? 0 : 1));
                if (ShopInventory.InStore) ShopNotice.Report(batchResult);
                else ShopNotice.Show(batchResult.Detail);
            }
            if (window != null && window.IsAlive) window.Close();
            window = null; queue.Clear(); homes.Clear(); candidates.Clear(); picked.Clear(); batchResult = null;
            editing = tool = sample = null; phase = 0;
        }
        public void Dispose()
        {
            if (active == this) active = null;
            ModHook.OnGameLoadedInit -= Reset; ModHook.OnLeavingStoreEarly -= Reset; settings.Changed -= Changed; Reset();
        }
    }
}
