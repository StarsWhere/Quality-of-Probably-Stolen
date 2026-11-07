using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

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
        private static Action refreshPicker;
        private static float refreshAt;
        internal static bool IsOpen => picker?.IsAlive == true;

        private sealed class LocationStamp
        {
            internal GameInventory Inventory;
            internal GameItem Owner;
            internal GameItem[] Path;
            internal string Name, Role;
            internal int Page, Count;
        }
        private sealed class ItemStamp
        {
            internal GameItem Item;
            internal GameInventory Source;
            internal string Name;
            internal int Count;
        }

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
            Func<IList<ShopInventory.Location>> choices, Action<ShopInventory.Location> changed, string id)
        {
            if (choices == null) throw new ArgumentNullException(nameof(choices));
            var only = selected == null ? (choices() ?? Array.Empty<ShopInventory.Location>())
                .Where(location => location?.Inventory != null).Take(2).ToList() : null;
            if (only?.Count == 1)
            {
                ShopInventory.Location candidate = only[0];
                Slot(builder, title, candidate.Owner, candidate.Name, () =>
                {
                    var current = (choices() ?? Array.Empty<ShopInventory.Location>())
                        .Where(location => location?.Inventory != null).Take(2).ToList();
                    if (current.Count != 1 || !StillOffered(() => current, candidate))
                    { ChooseLocation(title, choices, selected, changed); return; }
                    if (!ShopInventory.TryRefresh(candidate, out var live)) { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
                    changed(live);
                }, id, false);
                return;
            }
            Slot(builder, title, selected?.Owner, selected?.Name, () => ChooseLocation(title, choices, selected, changed), id);
        }

        internal static void ChooseLocation(string title, Func<IList<ShopInventory.Location>> locations,
            ShopInventory.Location selected, Action<ShopInventory.Location> changed, int page = 0, CustomUIWindow owner = null)
        {
            if (locations == null) throw new ArgumentNullException(nameof(locations));
            string query = "";
            Action render = null;
            CustomUIWindow own = Picker(title, value => { query = value ?? ""; page = 0; render?.Invoke(); }, owner);
            List<LocationStamp> last = null;
            render = () =>
            {
                if (!Active(own) || body == null) return;
                var current = (locations() ?? Array.Empty<ShopInventory.Location>()).Where(l => l?.Inventory != null).ToList();
                last = Stamp(current);
                float scroll = body.GetComponentInParent<ScrollRect>()?.verticalNormalizedPosition ?? 1f;
                UiLayout.Clear(body, own);
                var builder = new CustomUIBuilder(CustomUIManager.Instance, own, body);
                var choices = current.Where(l => Matches(l.Name, query)).ToList();
                foreach (var location in choices.Where(l => l.Owner == null))
                {
                    builder.AddButton(location.Name, () =>
                    {
                        if (!Active(own)) return;
                        if (!StillOffered(locations, location) || !ShopInventory.TryRefresh(location, out var live)) { Changed(own); render(); return; }
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
                        if (!StillOffered(locations, location) || !ShopInventory.TryRefresh(location, out var live)) { Changed(own); render(); return; }
                        Close(); changed(live);
                    } });
                if (choices.Count == 0) builder.AddLabel(ShopSettings.Text("visual.no_containers"));
                UiLayout.Pager(builder, page, containers.Count, 12, next => { page = next; render(); });
                CustomUIBuilder.RebuildLayout(body);
                UiLayout.RestoreScroll(body, scroll);
            };
            refreshPicker = () =>
            {
                if (!Active(own) || UIDragHandler.isDraggingAnyUI ||
                    Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2) ||
                    Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || Input.GetMouseButtonUp(2)) return;
                var current = (locations() ?? Array.Empty<ShopInventory.Location>()).Where(l => l?.Inventory != null).ToList();
                if (!Same(last, Stamp(current))) render();
            };
            render();
        }

        private static List<LocationStamp> Stamp(IEnumerable<ShopInventory.Location> locations) => locations.Select(l => new LocationStamp
        {
            Inventory = l.Inventory, Owner = l.Owner, Path = l.Path?.ToArray(), Name = l.Name, Role = l.Role, Page = l.Page,
            Count = l.Inventory.childItems?.Count ?? 0
        }).ToList();

        private static bool StillOffered(Func<IList<ShopInventory.Location>> locations, ShopInventory.Location expected) =>
            (locations() ?? Array.Empty<ShopInventory.Location>()).Any(location => location != null &&
                ReferenceEquals(location.Inventory, expected.Inventory) && ReferenceEquals(location.Owner, expected.Owner) &&
                location.Role == expected.Role && location.Page == expected.Page && location.Path != null && expected.Path != null &&
                location.Path.SequenceEqual(expected.Path));

        private static bool Same(List<LocationStamp> first, List<LocationStamp> second)
        {
            if (first == null || first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++)
            {
                var a = first[i]; var b = second[i];
                if (!ReferenceEquals(a.Inventory, b.Inventory) || !ReferenceEquals(a.Owner, b.Owner) ||
                    a.Name != b.Name || a.Role != b.Role || a.Page != b.Page || a.Count != b.Count ||
                    a.Path == null != (b.Path == null) || a.Path != null && !a.Path.SequenceEqual(b.Path)) return false;
            }
            return true;
        }

        internal static void ChooseItem(string title, Func<IList<GameItem>> items, GameItem selected, Action<GameItem> changed, int page = 0, CustomUIWindow owner = null)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            string query = "";
            List<ItemStamp> last = null;
            Action render = null;
            CustomUIWindow own = Picker(title, value => { query = value ?? ""; page = 0; render?.Invoke(); }, owner);
            List<GameItem> Current() => (items() ?? Array.Empty<GameItem>()).Where(item => item?.parentInventory != null && item.unitCount > 0 &&
                (!(item is GameItemElement element) || !element.IsDestroyed())).Distinct().ToList();
            render = () =>
            {
                if (!Active(own) || body == null) return;
                var current = Current();
                last = StampItems(current);
                var sources = current.ToDictionary(item => item, item => item.parentInventory);
                var choices = current.Where(item => Matches(item.GetDisplayName(true), query)).ToList();
                page = Mathf.Clamp(page, 0, Math.Max(0, (choices.Count - 1) / 12));
                float scroll = body.GetComponentInParent<ScrollRect>()?.verticalNormalizedPosition ?? 1f;
                UiLayout.Clear(body, own);
                var builder = new CustomUIBuilder(CustomUIManager.Instance, own, body);
                int columns = Mathf.Clamp(Mathf.FloorToInt((own.Rect.rect.width - 40) / 133), 1, 4);
                ItemGrid.Add(builder, choices.Skip(page * 12).Take(12).Select(item => new ItemGrid.Entry(item) { Selected = item == selected }),
                    new ItemGrid.Options { Cards = true, Columns = columns, CardWidth = 130, CardHeight = 98,
                        OnClick = entry =>
                        {
                            if (!Active(own)) return;
                            if (!sources.TryGetValue(entry.Item, out GameInventory source) || entry.Item.parentInventory != source ||
                                entry.Item.parentInventory != entry.Source || !Current().Contains(entry.Item))
                            { Changed(own); render(); return; }
                            Close(); changed(entry.Item);
                        } });
                if (choices.Count == 0) builder.AddLabel(ShopSettings.Text("visual.no_items"));
                UiLayout.Pager(builder, page, choices.Count, 12, next => { page = next; render(); });
                CustomUIBuilder.RebuildLayout(body);
                UiLayout.RestoreScroll(body, scroll);
            };
            refreshPicker = () =>
            {
                if (!Active(own) || UIDragHandler.isDraggingAnyUI ||
                    Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2) ||
                    Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || Input.GetMouseButtonUp(2)) return;
                if (!SameItems(last, StampItems(Current()))) render();
            };
            render();
        }

        private static List<ItemStamp> StampItems(IEnumerable<GameItem> items) => items.Select(item => new ItemStamp
        { Item = item, Source = item.parentInventory, Name = item.GetDisplayName(true), Count = item.unitCount }).ToList();

        private static bool SameItems(List<ItemStamp> first, List<ItemStamp> second)
        {
            if (first == null || first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++)
                if (!ReferenceEquals(first[i].Item, second[i].Item) || !ReferenceEquals(first[i].Source, second[i].Source) ||
                    first[i].Name != second[i].Name || first[i].Count != second[i].Count) return false;
            return true;
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
            builder.OnClose(() => { if (ReferenceEquals(picker, own)) { picker = null; body = null; message = null; refreshPicker = null; } });
            builder.AddInput(ShopSettings.Text("inv.search"), "", search, "query", search);
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

        internal static void Tick()
        {
            if (picker == null || !Active(picker) || Time.unscaledTime < refreshAt) return;
            refreshAt = Time.unscaledTime + 0.25f;
            try { refreshPicker?.Invoke(); }
            catch (Exception error)
            {
                Debug.LogError("[ShopAssist] Choice refresh failed: " + error);
                Close();
                ShopNotice.Show(ShopSettings.Text("inv.changed"));
            }
        }

        internal static void Close()
        {
            var old = picker; picker = null; parent = null; body = null; message = null; refreshPicker = null; run = null; slot = -1; old?.Close();
        }
    }
}
