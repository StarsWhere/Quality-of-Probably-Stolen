using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

public sealed class SettingsFinderScenario
{
    private const string Mod = "starswhere.shopassist";
    private GameOptionUIManager options;
    private CustomUIWindow native;
    private ConfigEntry<bool> entry;
    private bool original;
    private int step, captureFrame;
    private static CustomUIWindow Find() => CustomUIManager.Instance.GetWindow(Mod + ".settings.find");
    private static bool Visible(string key) => Find()?.Get("jump_" + key)?.root?.activeInHierarchy == true;
    private static void Need(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }
    public bool Tick(out string result)
    {
        result = null;
        if (step == 0)
        {
            if (MainMenuUIController.Instance == null || CustomUIManager.Instance == null) return false;
            options = Resources.FindObjectsOfTypeAll<GameOptionUIManager>().FirstOrDefault(o => o.gameObject.scene.IsValid());
            if (options == null || EmporiumOption.Instance == null) return false;
            options.OpenUI(); native = CustomUIManager.Instance.GetWindow("game_options");
            entry = ModConfig.Find(Mod)?.Get("f15.enabled") as ConfigEntry<bool>; Need(entry != null, "Missing F15 setting");
            original = entry.Value; native.Get(Mod + ":f15.enabled").SetBool(false, true);
            native.Get(Mod + ":settings.find").button.onClick.Invoke(); step++; return false;
        }
        if (step == 1)
        {
            var w = Find(); Need(w?.IsAlive == true, "Main-menu settings finder did not open");
            Need(w.Get("tier").dropdown.options.Count == 4, "Finder must contain all levels and three tier filters");
            var f11 = ModConfig.Find(Mod)?.Get("f11.enabled") as ConfigEntry<bool>;
            var f11Toggle = native.Get(Mod + ":f11.enabled");
            var keyEntry = GameOptionRegistry.Sections.SelectMany(section => section.entries)
                .FirstOrDefault(value => value.id == Mod + ":f11.key");
            var keyControl = native.Get(Mod + ":f11.key");
            Need(f11 != null && f11Toggle?.toggle != null && keyEntry?.getKey != null && keyControl?.root != null,
                "F11's actual switch or native key binding is missing");
            bool enabled = f11.Value, draftEnabled = f11Toggle.GetBool();
            KeyCode binding = keyEntry.getKey();
            string preference = Mod + ".f11.key";
            bool hasPreference = PlayerPrefs.HasKey(preference);
            int savedPreference = PlayerPrefs.GetInt(preference, -1);
            var assembly = ModLoader.Mods.First(value => value.Manifest?.ID == Mod).Instance.GetType().Assembly;
            var catalog = assembly.GetType("ShopAssist.FeatureCatalog", true);
            var defaultToggle = catalog.GetMethod("ToggleDefault", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Need(defaultToggle != null && !(bool)defaultToggle.Invoke(null, new object[] { "f11.enabled", true }),
                "F11 is not disabled by default");
            w.Get("tier").SetIndex(0, true); w.Get("only").SetBool(false, true);
            foreach (string query in new[] { "找货", "库存找货", "Find goods", "Stock search", "F11" })
            {
                w.Get("query").SetString(query, true);
                Need(Visible("f11.enabled") && Visible("f11.key"), "F11 switch or binding is missing for search: " + query);
            }
            foreach (string key in new[] { "f11.enabled", "f11.key" })
            {
                w.Get("jump_" + key).button.onClick.Invoke();
                var control = native.Get(Mod + ":" + key);
                var rect = control.root.transform as RectTransform;
                var scroll = rect?.GetComponentInParent<ScrollRect>();
                Canvas.ForceUpdateCanvases();
                Need((Find() == null || !Find().IsAlive) && scroll?.viewport != null &&
                    scroll.viewport.rect.Contains(scroll.viewport.InverseTransformPoint(rect.TransformPoint(rect.rect.center))),
                    "F11 search did not focus the actual native option: " + key);
                Need(f11.Value == enabled && f11Toggle.GetBool() == draftEnabled && keyEntry.getKey() == binding &&
                    PlayerPrefs.HasKey(preference) == hasPreference && PlayerPrefs.GetInt(preference, -1) == savedPreference,
                    "Finding an F11 control changed its switch or key binding");
                native.Get(Mod + ":settings.find").button.onClick.Invoke();
                w = Find(); Need(w?.IsAlive == true, "Settings finder did not reopen");
                w.Get("query").SetString("F11", true);
            }
            w.Get("tier").SetIndex(1, true);
            w.Get("query").SetString("f42", true);
            Need(Visible("f42.enabled") && Visible("f42.mode") && Visible("f42.speed") && Visible("f42.key") && !Visible("f15.enabled"),
                "Essentials tier does not contain playback settings and shortcut");
            w.Get("tier").SetIndex(2, true);
            Need(!Visible("f42.enabled"), "Playback appeared in the rules tier");
            w.Get("query").SetString("f05.auto_insert", true);
            Need(Visible("f05.auto_insert") && Find().Get("jump_f05.auto_insert").GetText().Contains(ModHelper.GetLocalized(Mod, "f05.enabled")),
                "Cross-tier auto-arrange lost its parent or rules placement");
            w.Get("tier").SetIndex(3, true);
            w.Get("query").SetString("f04.unseen", true);
            Need(Visible("f04.unseen") && Find().Get("jump_f04.unseen").GetText().Contains(ModHelper.GetLocalized(Mod, "f04.enabled")),
                "Cross-tier hidden-risk option lost its parent or planning placement");
            w.Get("tier").SetIndex(0, true);
            var input = w.Get("query").input;
            w.Get("query").SetString("本周和下周", true);
            Need(ReferenceEquals(Find(), w) && ReferenceEquals(Find().Get("query").input, input) && Visible("f15.enabled"),
                "Live description search replaced its input or did not find the shutter calendar");
            Find().Get("query").SetString("f16.volume", true);
            string volume = Find().Get("jump_f16.volume")?.GetText();
            Need(volume != null && volume.Contains(ModHelper.GetLocalized(Mod, "settings.kind.value")) &&
                volume.Contains(ModHelper.GetLocalized(Mod, "f16.enabled")), "Search result omitted its setting type or parent feature");
            bool purchase = native.Get(Mod + ":f01.enabled").GetBool(), wholesale = native.Get(Mod + ":f01.wholesale").GetBool();
            native.Get(Mod + ":f01.enabled").SetBool(false, true);
            native.Get(Mod + ":f01.wholesale").SetBool(true, true);
            Find().Get("query").SetString("f01", true);
            Find().Get("only").SetBool(true, true);
            Need(Visible("f01.wholesale") && !Visible("f01.enabled"),
                "Enabled filter coupled independent ordinary and wholesale purchase switches");
            native.Get(Mod + ":f01.enabled").SetBool(purchase, true);
            native.Get(Mod + ":f01.wholesale").SetBool(wholesale, true);
            bool arrange = native.Get(Mod + ":f05.enabled").GetBool(), auto = native.Get(Mod + ":f05.auto_insert").GetBool();
            native.Get(Mod + ":f05.enabled").SetBool(false, true);
            native.Get(Mod + ":f05.auto_insert").SetBool(true, true);
            Find().Get("query").SetString("f05.auto_insert", true);
            Need(!Visible("f05.auto_insert"), "Auto-arrange ignored its disabled parent");
            native.Get(Mod + ":f05.enabled").SetBool(true, true);
            Need(Visible("f05.auto_insert"), "Auto-arrange was hidden despite both switches being enabled");
            native.Get(Mod + ":f05.auto_insert").SetBool(false, true);
            Need(!Visible("f05.auto_insert"), "Auto-arrange ignored its own disabled switch");
            native.Get(Mod + ":f05.enabled").SetBool(arrange, true);
            native.Get(Mod + ":f05.auto_insert").SetBool(auto, true);
            string[] related = { "f07.enabled", "f08.enabled", "f09.enabled", "f10.enabled", "f12.enabled" };
            bool[] draft = related.Select(key => native.Get(Mod + ":" + key).GetBool()).ToArray();
            native.Get(Mod + ":f07.enabled").SetBool(false, true);
            native.Get(Mod + ":f08.enabled").SetBool(true, true);
            native.Get(Mod + ":f09.enabled").SetBool(true, true);
            Find().Get("query").SetString("f08.enabled", true);
            Need(Visible("f08.enabled"), "Routing execution was incorrectly coupled to rule editing");
            native.Get(Mod + ":f08.enabled").SetBool(false, true);
            Find().Get("query").SetString("f09.enabled", true);
            Need(!Visible("f09.enabled"), "Routing preview ignored its execution dependency");
            native.Get(Mod + ":f10.enabled").SetBool(false, true);
            native.Get(Mod + ":f12.enabled").SetBool(true, true);
            Find().Get("query").SetString("f12.enabled", true);
            Need(!Visible("f12.enabled"), "Module layout ignored its cabinet dependency");
            native.Get(Mod + ":f10.enabled").SetBool(true, true);
            Need(Visible("f12.enabled"), "Module layout stayed hidden after enabling its cabinet");
            for (int i = 0; i < related.Length; i++) native.Get(Mod + ":" + related[i]).SetBool(draft[i], true);
            Find().Get("only").SetBool(false, true);
            w = Find();
            w.Get("query").SetString("F15", true);
            w = Find(); Need(Visible("f15.enabled"), "Feature ID search did not find F15");
            w.Get("only").SetBool(true, true);
            Need(!Visible("f15.enabled") && entry.Value == original, "Enabled filter ignored draft or changed configuration");
            Find().Get("only").SetBool(false, true); Find().Get("jump_f15.enabled").button.onClick.Invoke();
            step++; return false;
        }
        if (step == 2)
        {
            var toggle = native.Get(Mod + ":f15.enabled"); var rect = (RectTransform)toggle.root.transform;
            var scroll = rect.GetComponentInParent<ScrollRect>(); Need(scroll?.viewport != null, "No native scroll viewport");
            var point = scroll.viewport.InverseTransformPoint(rect.TransformPoint(rect.rect.center));
            Need(scroll.viewport.rect.Contains(point) && (Find() == null || !Find().IsAlive) && entry.Value == original,
                "Jump did not scroll the actual setting into view or accidentally applied edits");
            toggle.SetBool(!original, true); native.Get("apply").button.onClick.Invoke();
            Need(entry.Value == !original, "Actual Apply did not commit the selected native setting");
            toggle.SetBool(original, true); native.Get("apply").button.onClick.Invoke();
            native.Get(Mod + ":settings.find").button.onClick.Invoke();
            Find().Get("query").SetString("f16", true);
            Canvas.ForceUpdateCanvases();
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-settings-finder.png"));
            captureFrame = Time.frameCount + 4; step++; return false;
        }
        if (step == 3)
        {
            if (Time.frameCount < captureFrame) return false;
            var label = Find()?.Get("jump_f16.enabled")?.text;
            Need(label != null, "Settings result disappeared before rendering");
            label.ForceMeshUpdate(true, true);
            Need(label.textInfo.characterCount > 0 && !label.isTextTruncated && !label.isTextOverflowing,
                "Settings search result text did not render completely");
            var resultRect = Find().Get("jump_f16.enabled").Rect;
            var resultScroll = resultRect.GetComponentInParent<ScrollRect>();
            Need(resultScroll?.viewport != null && resultRect.rect.width >= resultScroll.viewport.rect.width - 36 &&
                Mathf.Abs(resultRect.rect.width - Find().Get("jump_f16.volume").Rect.rect.width) < 1,
                "Search results do not fill the available width evenly");
            options.CloseUI();
            result = "PASS feature ID and description search, setting type and parent, independent draft filters, scroll to actual native option and explicit Apply"; return true;
        }
        return false;
    }
}
