using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShopAssist
{
    internal static class VisualChoices
    {
        private static CustomUIWindow picker;
        private static RectTransform body;
        private static CustomUIElement message;
        private static CustomUIWindow parent, requestedParent;
        private static string run;
        private static int slot;

        internal static void Slot(CustomUIBuilder builder, string label, GameItem item, string caption, Action select, string id, bool selected = true)
        {
            if (select != null)
            {
                Action original = select;
                CustomUIWindow owner = UiLayout.Window(builder);
                select = () =>
                {
                    if (owner?.IsAlive != true || !owner.root.activeInHierarchy) return;
                    CustomUIWindow previous = requestedParent;
                    try { requestedParent = owner; original(); }
                    finally { requestedParent = previous; }
                };
            }
            builder.BeginPanel(155, 115, padding: 5, id: id + "_slot").AddLabel(label);
            if (item != null)
                ItemGrid.Add(builder, new[] { new ItemGrid.Entry(item) { Caption = caption, Selected = select != null && selected, Selectable = select != null } },
                    new ItemGrid.Options { Cards = true, Columns = 1, CardWidth = 140, CardHeight = 74,
                        OnClick = select == null ? null : (Action<ItemGrid.Entry>)(_ => select()) });
            else if (select != null) builder.AddButton(string.IsNullOrEmpty(caption) ? ShopSettings.Text("visual.choose") : caption, select, id);
            else builder.AddLabel(caption ?? "—");
            builder.End();
        }

        internal static void Location(CustomUIBuilder builder, string title, ShopInventory.Location selected,
            IList<ShopInventory.Location> choices, Action<ShopInventory.Location> changed, string id)
        {
            var only = selected == null ? choices.Where(location => location != null && location.Inventory != null).Take(2).ToList() : null;
            if (only?.Count == 1)
            {
                ShopInventory.Location candidate = only[0];
                Slot(builder, title, candidate.Owner, candidate.Name, () =>
                {
                    if (!ShopInventory.TryRefresh(candidate, out var live)) { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                    changed(live);
                }, id, false);
                return;
            }
            Slot(builder, title, selected?.Owner, selected?.Name, () => ChooseLocation(title, choices, selected, changed), id);
        }

        internal static void ChooseLocation(string title, IList<ShopInventory.Location> locations,
            ShopInventory.Location selected, Action<ShopInventory.Location> changed, int page = 0, CustomUIWindow owner = null)
        {
            string query = "";
            Action render = null;
            CustomUIWindow own = Picker(title, value => { query = value ?? ""; page = 0; render?.Invoke(); }, owner);
            render = () =>
            {
                if (!Active(own) || body == null) return;
                UiLayout.Clear(body, own);
                var builder = new CustomUIBuilder(CustomUIManager.Instance, own, body);
                var choices = locations.Where(l => l?.Inventory != null && Matches(l.Name, query)).ToList();
                foreach (var location in choices.Where(l => l.Owner == null))
                {
                    builder.AddButton(location.Name, () =>
                    {
                        if (!Active(own)) return;
                        if (!ShopInventory.TryRefresh(location, out var live)) { Changed(own); render(); return; }
                        Close(); changed(live);
                    }, "location_" + choices.IndexOf(location));
                }
                var containers = choices.Where(l => l.Owner != null).ToList();
                page = Mathf.Clamp(page, 0, Math.Max(0, (containers.Count - 1) / 12));
                var visible = containers.Skip(page * 12).Take(12).ToList();
                var rows = visible.Select(l => new ItemGrid.Entry(l.Owner) { Caption = l.Name,
                    Selected = selected?.Inventory == l.Inventory,
                    StatusLabel = l.Inventory.childItems.Count + " " + ShopSettings.Text("visual.stacks") }).ToList();
                int columns = Mathf.Clamp(Mathf.FloorToInt((own.Rect.rect.width - 40) / 133), 1, 4);
                ItemGrid.Add(builder, rows, new ItemGrid.Options { Cards = true, Columns = columns, CardWidth = 130, CardHeight = 98,
                    OnClick = entry =>
                    {
                        if (!Active(own)) return;
                        var location = visible[rows.IndexOf(entry)];
                        if (!ShopInventory.TryRefresh(location, out var live)) { Changed(own); render(); return; }
                        Close(); changed(live);
                    } });
                if (choices.Count == 0) builder.AddLabel(ShopSettings.Text("visual.no_containers"));
                UiLayout.Pager(builder, page, containers.Count, 12, next => { page = next; render(); });
                CustomUIBuilder.RebuildLayout(body);
            };
            render();
        }

        internal static void ChooseItem(string title, IList<GameItem> items, GameItem selected, Action<GameItem> changed, int page = 0, CustomUIWindow owner = null)
        {
            var sources = items.Where(item => item?.parentInventory != null).Distinct().ToDictionary(item => item, item => item.parentInventory);
            string query = "";
            Action render = null;
            CustomUIWindow own = Picker(title, value => { query = value ?? ""; page = 0; render?.Invoke(); }, owner);
            render = () =>
            {
                if (!Active(own) || body == null) return;
                UiLayout.Clear(body, own);
                var live = sources.Keys.Where(i => i.parentInventory == sources[i] && i.unitCount > 0 &&
                    (!(i is GameItemElement e) || !e.IsDestroyed())).ToList();
                if (live.Count != sources.Count) Changed(own);
                var choices = live.Where(i => Matches(i.GetDisplayName(true), query)).ToList();
                page = Mathf.Clamp(page, 0, Math.Max(0, (choices.Count - 1) / 12));
                var builder = new CustomUIBuilder(CustomUIManager.Instance, own, body);
                int columns = Mathf.Clamp(Mathf.FloorToInt((own.Rect.rect.width - 40) / 133), 1, 4);
                ItemGrid.Add(builder, choices.Skip(page * 12).Take(12).Select(i => new ItemGrid.Entry(i) { Selected = i == selected }),
                    new ItemGrid.Options { Cards = true, Columns = columns, CardWidth = 130, CardHeight = 98,
                        OnClick = entry =>
                        {
                            if (!Active(own)) return;
                            if (entry.Item.parentInventory != sources[entry.Item] || entry.Item.parentInventory != entry.Source || entry.Item.unitCount <= 0 ||
                                entry.Item is GameItemElement e && e.IsDestroyed()) { Changed(own); render(); return; }
                            Close(); changed(entry.Item);
                        } });
                if (choices.Count == 0) builder.AddLabel(ShopSettings.Text("visual.no_items"));
                UiLayout.Pager(builder, page, choices.Count, 12, next => { page = next; render(); });
                CustomUIBuilder.RebuildLayout(body);
            };
            render();
        }

        private static bool Matches(string value, string query) => string.IsNullOrWhiteSpace(query) ||
            (value ?? "").IndexOf(query.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0;

        private static void Changed(CustomUIWindow own)
        {
            if (picker == own) message?.SetText(ShopSettings.Text("inv.changed")).SetVisible(true);
        }

        private static CustomUIWindow Picker(string title, Action<string> search, CustomUIWindow owner)
        {
            Close();
            parent = owner ?? requestedParent;
            run = PlayerStore.HasInstance ? PlayerStore.Instance.runID : null;
            slot = PlayerStore.HasInstance ? PlayerStore.Instance.saveSlotId : -1;
            Vector2 size = UiLayout.Fit(580, 440);
            var builder = CustomUIManager.Window(ModEntry.Id + ".choose", title, CustomUIManager.LAYER_META)
                .SetSize(size.x, size.y).SetDraggable(true).SetCloseOnEscape(true);
            picker = builder.GetWindow();
            var own = picker;
            builder.OnClose(() => { if (ReferenceEquals(picker, own)) { picker = null; body = null; message = null; } });
            builder.AddInput(ShopSettings.Text("settings.query"), "", search, "query", search);
            builder.AddLabel("", "changed"); message = builder.Last; message.SetVisible(false);
            builder.BeginScrollFlexible(120); body = builder.CurrentContainer;
            builder.End().Show();
            return own;
        }

        private static bool Active(CustomUIWindow own)
        {
            if (picker != own || own?.IsAlive != true) return false;
            if (!ShopInventory.InStore || run != PlayerStore.Instance.runID || slot != PlayerStore.Instance.saveSlotId ||
                parent != null && (parent.IsAlive != true || !parent.root.activeInHierarchy))
            { Close(); return false; }
            return true;
        }

        internal static void Tick() { if (picker != null) Active(picker); }

        internal static void Close()
        {
            var old = picker; picker = null; parent = null; body = null; message = null; run = null; slot = -1; old?.Close();
        }
    }
}
