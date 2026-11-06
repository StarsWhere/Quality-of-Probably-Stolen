using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShopAssist
{
    internal static class VisualChoices
    {
        private static CustomUIWindow picker;

        internal static void Slot(CustomUIBuilder builder, string label, GameItem item, string caption, Action select, string id, bool selected = true)
        {
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
            ShopInventory.Location selected, Action<ShopInventory.Location> changed, int page = 0)
        {
            var choices = locations.Where(l => l != null && l.Inventory != null).ToList();
            var builder = Picker(title);
            var roots = choices.Where(l => l.Owner == null).ToList();
            if (roots.Count > 0)
            {
                builder.BeginRow();
                foreach (var location in roots)
                    builder.AddButton(location.Name, () => { if (ShopInventory.TryRefresh(location, out var live)) { Close(); changed(live); } },
                        "location_" + choices.IndexOf(location));
                builder.End();
            }
            var containers = choices.Where(l => l.Owner != null).ToList();
            page = Mathf.Clamp(page, 0, Math.Max(0, (containers.Count - 1) / 12));
            var rows = containers.Skip(page * 12).Take(12).Select(l => new ItemGrid.Entry(l.Owner)
                { Caption = l.Name, Selected = selected?.Inventory == l.Inventory,
                    Badge = l.Inventory.childItems.Count + " " + ShopSettings.Text("visual.stacks") }).ToList();
            builder.BeginScrollFlexible(160);
            ItemGrid.Add(builder, rows, new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 130, CardHeight = 98,
                OnClick = entry =>
                {
                    var location = containers[page * 12 + rows.IndexOf(entry)];
                    if (!ShopInventory.TryRefresh(location, out var live)) { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                    Close(); changed(live);
                } });
            builder.End();
            if (choices.Count == 0) builder.AddLabel(ShopSettings.Text("visual.no_containers"));
            Pages(builder, page, containers.Count, p => ChooseLocation(title, locations, selected, changed, p));
            builder.Show();
        }

        internal static void ChooseItem(string title, IList<GameItem> items, GameItem selected, Action<GameItem> changed, int page = 0)
        {
            var choices = items.Where(i => i != null && i.parentInventory != null).Distinct().ToList();
            if (choices.Count == 1)
            {
                var entry = new ItemGrid.Entry(choices[0]);
                if (entry.Shape != null && entry.Item.parentInventory == entry.Source && entry.Item.unitCount > 0 &&
                    (!(entry.Item is GameItemElement element) || !element.IsDestroyed()))
                { Close(); changed(entry.Item); return; }
            }
            page = Mathf.Clamp(page, 0, Math.Max(0, (choices.Count - 1) / 12));
            var builder = Picker(title);
            builder.BeginScrollFlexible(160);
            ItemGrid.Add(builder, choices.Skip(page * 12).Take(12).Select(i => new ItemGrid.Entry(i) { Selected = i == selected }),
                new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 130, CardHeight = 98,
                    OnClick = entry => { if (entry.Item.parentInventory == entry.Source) { Close(); changed(entry.Item); } } });
            builder.End();
            if (choices.Count == 0) builder.AddLabel(ShopSettings.Text("visual.no_items"));
            Pages(builder, page, choices.Count, p => ChooseItem(title, items, selected, changed, p));
            builder.Show();
        }

        private static CustomUIBuilder Picker(string title)
        {
            Close();
            var builder = CustomUIManager.Window(ModEntry.Id + ".choose", title, CustomUIManager.LAYER_META)
                .SetSize(580, 440).SetDraggable(true).SetCloseOnEscape(true);
            picker = builder.GetWindow();
            var own = picker;
            builder.OnClose(() => { if (ReferenceEquals(picker, own)) picker = null; });
            return builder;
        }

        private static void Pages(CustomUIBuilder builder, int page, int count, Action<int> go)
        {
            int pages = Math.Max(1, (count + 11) / 12);
            if (pages <= 1) return;
            builder.BeginRow().AddButton("‹", () => { if (page > 0) go(page - 1); }, "previous")
                .AddLabel((page + 1) + " / " + pages)
                .AddButton("›", () => { if (page + 1 < pages) go(page + 1); }, "next").End();
        }

        internal static void Close()
        {
            var old = picker; picker = null; old?.Close();
        }
    }
}
