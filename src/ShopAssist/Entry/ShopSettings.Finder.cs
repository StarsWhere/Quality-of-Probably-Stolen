using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ShopAssist
{
    internal sealed partial class ShopSettings
    {
        private readonly List<string> shortcutKeys = new List<string>();
        private CustomUIWindow finder;
        private string search = "";
        private int searchTier, searchPage;
        private bool onlyEnabled;
        private readonly Dictionary<string, CustomUIElement> finderRows = new Dictionary<string, CustomUIElement>();
        private readonly Dictionary<Toggle, UnityAction<bool>> finderDraftListeners = new Dictionary<Toggle, UnityAction<bool>>();
        private RectTransform finderResults;
        private CustomUIElement finderNone, finderPrevious, finderNext, finderPage;
        private int finderCount;
        private bool finderBuilding;

        private void OpenFinder()
        {
            searchPage = 0;
            RenderFinder();
        }

        private bool EnabledInOptions(CustomUIWindow options, string key)
        {
            bool Read(string name)
            {
                var toggle = options.Get(ModEntry.Id + ":" + name);
                return toggle?.toggle != null ? toggle.GetBool() : (config.Get(name) as ConfigEntry<bool>)?.Value == true;
            }
            return FeatureCatalog.RequiredSettings(key).All(Read);
        }

        private string ResultKind(string key)
        {
            if (shortcutKeys.Contains(key)) return Text("settings.kind.key");
            if (commands.Any(command => command.Label == key)) return Text("settings.kind.action");
            if (PrimaryOption(key)) return Text("settings.kind.feature");
            if (choiceLabels.ContainsKey(key)) return Text("settings.kind.choice");
            return Text(config.Get(key)?.ValueType == typeof(bool) ? "settings.kind.toggle" : "settings.kind.value");
        }

        private bool SearchMatches(string key)
        {
            string query = search.Trim();
            string group = FeatureCatalog.GroupKey(FeatureCatalog.For(key));
            return query.Length == 0 || key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                Text(key).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                Description(key).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                Text(FeatureCatalog.For(key).SettingKey).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                group != null && Text(group).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void RenderFinder()
        {
            if (finderBuilding) return;
            var options = CustomUIManager.Instance?.GetWindow("game_options");
            if (options == null || !options.IsAlive) { CloseFinder(); return; }
            if (finder?.IsAlive != true)
            {
                ClearFinderViews();
                finderBuilding = true;
                try
                {
                var b = CustomUIManager.Window(ModEntry.Id + ".settings.find", Text("settings.find"),
                    StoreUIManager.Instance == null ? "menu" : CustomUIManager.LAYER_META)
                    .SetSize(640, 520).SetDraggable(true).SetCloseOnEscape(true);
                finder = b.GetWindow();
                CustomUIWindow own = finder;
                b.OnClose(() => { if (ReferenceEquals(finder, own)) { finder = null; ClearFinderViews(); } });
                b.AddInput(Text("settings.query"), search, ChangeSearch, "query", ChangeSearch);
                b.BeginRow().AddDropdown(new List<string> { Text("settings.any"), Text("section.category1"), Text("section.category2"), Text("section.category3"), Text("section.category4") },
                    searchTier, i => { searchTier = i; searchPage = 0; RenderFinder(); }, "tier")
                    .AddToggle(Text("settings.only"), onlyEnabled, v => { onlyEnabled = v; searchPage = 0; RenderFinder(); }, "only").End();
                b.BeginScrollFlexible(130);
                finderResults = b.CurrentContainer;
                var resultLayout = finderResults.GetComponent<VerticalLayoutGroup>();
                if (resultLayout != null)
                {
                    resultLayout.childControlWidth = true;
                    resultLayout.childForceExpandWidth = true;
                }
                b.AddLabel(Text("settings.none")); finderNone = b.Last;
                b.End().BeginRow().AddFlexibleSpace()
                    .AddButton("‹", () => { if (searchPage > 0) { searchPage--; RenderFinder(); } }, "prev");
                finderPrevious = b.Last; finderPrevious.SetFixedWidth(36);
                b.AddLabel("", "page"); finderPage = b.Last;
                b.AddButton("›", () => { if ((searchPage + 1) * 12 < finderCount) { searchPage++; RenderFinder(); } }, "next");
                finderNext = b.Last; finderNext.SetFixedWidth(36);
                b.AddFlexibleSpace().End().Show();
                foreach (var entry in config.Entries)
                {
                    Toggle toggle = options.Get(ModEntry.Id + ":" + entry.Key)?.toggle;
                    if (toggle == null || finderDraftListeners.ContainsKey(toggle)) continue;
                    UnityAction<bool> changed = _ => { if (finder?.IsAlive == true) RenderFinder(); };
                    finderDraftListeners.Add(toggle, changed);
                    toggle.onValueChanged.AddListener(changed);
                }
                }
                finally { finderBuilding = false; }
            }
            var commandKeys = new HashSet<string>(commands.Select(command => command.Label));
            var list = config.Entries.Select(e => e.Key).Concat(shortcutKeys).Concat(commands.Select(c => c.Label)).Distinct()
                .Where(key => key.Length > 3 && key[0] == 'f' && key[3] == '.' &&
                    (commandKeys.Contains(key) || options.Get(ModEntry.Id + ":" + key)?.root != null) &&
                    (searchTier == 0 || Level(key) == searchTier - 1) && (!onlyEnabled || EnabledInOptions(options, key)) &&
                    SearchMatches(key))
                .OrderBy(OptionOrder).ToList();
            const int perPage = 12;
            finderCount = list.Count;
            searchPage = Math.Max(0, Math.Min(searchPage, (list.Count - 1) / perPage));
            var visible = list.Skip(searchPage * perPage).Take(perPage).ToList();
            var visibleKeys = new HashSet<string>(visible);
            foreach (var row in finderRows)
                if (row.Value.root != null && row.Value.root.activeSelf && !visibleKeys.Contains(row.Key)) row.Value.SetVisible(false);
            finderNone.SetVisible(list.Count == 0);
            int order = 1;
            foreach (var key in visible)
            {
                string context = Text(FeatureCatalog.CategoryKey(FeatureCatalog.For(key).Category)) + " · " + ResultKind(key);
                if (!PrimaryOption(key)) context += " · " + Text(FeatureCatalog.For(key).SettingKey);
                var availability = commands.Any(c => c.Label == key) ? Evaluate(key) : ActionAvailability.Ready;
                if (!finderRows.TryGetValue(key, out CustomUIElement row))
                {
                    var resultBuilder = new CustomUIBuilder(CustomUIManager.Instance, finder, finderResults);
                    resultBuilder.AddButton("", () => LocateOption(options, key), "jump_" + key);
                    row = resultBuilder.Last; finderRows.Add(key, row);
                    row.SetPreferredSize(-1, 54);
                    var itemLayout = row.root.GetComponent<LayoutElement>() ?? row.root.AddComponent<LayoutElement>();
                    itemLayout.minWidth = 0; itemLayout.preferredWidth = 0; itemLayout.flexibleWidth = 1;
                    row.text.enableWordWrapping = true;
                    row.text.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
                    row.text.margin = new Vector4(12, 4, 8, 4);
                    row.text.enableAutoSizing = true;
                    row.text.fontSizeMin = 12;
                    row.text.fontSizeMax = 18;
                }
                string title = "<b>" + Text(key) + "</b>\n<size=80%>" + context + "</size>";
                if (row.GetText() != title) row.SetText(title);
                row.SetTooltip(Description(key) + (availability.Allowed ? "" : "\n" + availability.Message));
                row.SetInteractable(availability.Allowed || availability.SettingKey != null);
                if (!row.root.activeSelf) row.SetVisible(true);
                if (row.root.transform.GetSiblingIndex() != order) row.root.transform.SetSiblingIndex(order);
                order++;
            }
            finderPrevious.SetInteractable(searchPage > 0);
            finderNext.SetInteractable((searchPage + 1) * perPage < list.Count);
            finderPage.SetText(Format("settings.page", searchPage + 1, Math.Max(1, (list.Count + perPage - 1) / perPage), list.Count));
        }

        private void ChangeSearch(string value)
        {
            value = value ?? "";
            if (search == value) return;
            search = value; searchPage = 0;
            RenderFinder();
        }

        private void ClearFinderViews()
        {
            foreach (var pair in finderDraftListeners)
                if (pair.Key != null) pair.Key.onValueChanged.RemoveListener(pair.Value);
            finderDraftListeners.Clear(); finderRows.Clear();
            finderResults = null; finderNone = finderPrevious = finderNext = finderPage = null; finderCount = 0;
        }

        private void LocateOption(CustomUIWindow options, string key)
        {
            if (!options.IsAlive || CustomUIManager.Instance.GetWindow("game_options") != options) { CloseFinder(); return; }
            if (commands.Any(c => c.Label == key))
            {
                var availability = Evaluate(key);
                CloseFinder();
                if (availability.SettingKey != null) OpenSetting(availability.SettingKey); else Run(key);
                return;
            }
            var control = options.Get(ModEntry.Id + ":" + key);
            if (control?.root == null) return;
            CloseFinder();
            FocusControl(control);
        }

        internal void OpenSetting(string key)
        {
            toolsWindow?.Close(); toolsWindow = null;
            var options = CustomUIManager.Instance?.GetWindow("game_options");
            if (options?.IsAlive == true && options.IsVisible)
            {
                LocateOption(options, key);
                return;
            }
            var mod = ModLoader.Mods.Find(value => value.Manifest?.ID == ModEntry.Id);
            if (mod == null) return;
            var window = ModConfigWindow.Open(mod);
            var control = window?.Get(key);
            if (control?.root != null) FocusControl(control);
        }

        private static void FocusControl(CustomUIElement control)
        {
            var rect = control.root.transform as RectTransform;
            var scroll = rect?.GetComponentInParent<ScrollRect>();
            if (scroll != null && scroll.viewport != null && scroll.content != null)
            {
                Canvas.ForceUpdateCanvases();
                float hidden = scroll.content.rect.height - scroll.viewport.rect.height;
                if (hidden > 0)
                {
                    Vector3 local = scroll.viewport.InverseTransformPoint(rect.TransformPoint(rect.rect.center));
                    scroll.StopMovement();
                    scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll.verticalNormalizedPosition +
                        (local.y - scroll.viewport.rect.center.y) / hidden);
                    Canvas.ForceUpdateCanvases();
                }
            }
            var selectable = control.root.GetComponentInChildren<Selectable>();
            if (selectable != null && selectable.IsInteractable()) EventSystem.current?.SetSelectedGameObject(selectable.gameObject);
        }

        private void CloseFinder() { if (finder != null && finder.IsAlive) finder.Close(); finder = null; ClearFinderViews(); }

        [HarmonyPatch(typeof(GameOptionUIManager), nameof(GameOptionUIManager.CloseUI))]
        private static class OptionsClosed { private static void Postfix() => attached?.CloseFinder(); }
    }
}
