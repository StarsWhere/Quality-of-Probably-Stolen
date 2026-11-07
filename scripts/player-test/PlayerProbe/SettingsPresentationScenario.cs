using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Runs in the guarded isolated Player through the existing scenario dispatcher.
public sealed class SettingsPresentationScenario
{
    private const string Mod = "starswhere.shopassist";
    private static readonly string[] Sections = { "shopassist", "shopassist.batch", "shopassist.advanced" };
    private static readonly int[][] Tiers = {
        new[] { 2, 3, 4, 5, 13, 14, 15, 16, 17, 18, 19, 20, 21, 23, 28, 38, 39, 40, 41, 42 },
        new[] { 1, 7, 8, 9, 22, 24, 25, 26, 27, 29, 31, 32, 33, 35, 37 },
        new[] { 10, 11, 12, 30, 34, 36 }
    };
    private static readonly int[][] Categories = {
        new[] { 2, 5, 7, 8, 9, 20, 22, 25, 26, 36, 38, 39, 40, 41 },
        new[] { 1, 3, 4, 11, 16, 23, 24, 27, 28, 29, 34 },
        new[] { 10, 12, 19, 30, 31, 32, 33, 35, 37 },
        new[] { 13, 14, 15, 17, 18, 21 },
        new[] { 42 }
    };
    private static readonly string[] ChoiceKeys = { "f09.mode", "f14.side", "f20.side", "f24.destination", "f39.corner", "f39.style", "f42.mode", "f42.speed" };
    private static readonly string[][] ChoiceLabels = {
        new[] { "f09.always", "f09.conflicts", "f09.simulate" },
        new[] { "f14.auto", "f14.left", "f14.right" },
        new[] { "f20.auto", "f20.left", "f20.right" },
        new[] { "f24.front", "f24.box", "f24.trash" },
        new[] { "f39.top_left", "f39.top_right", "f39.bottom_left", "f39.bottom_right" },
        new[] { "f39.filled", "f39.outline" },
        new[] { "f42.hold", "f42.toggle" },
        new[] { "f42.speed2", "f42.speed4", "f42.speed8", "f42.speed16" }
    };
    private static readonly int[] Widths = { 1280, 1280, 1920, 1920 };
    private static readonly int[] Heights = { 720, 720, 1080, 1080 };
    private static readonly int[] Resolutions = { 3, 3, 0, 0 };
    private static readonly string[] Languages = { "zh", "en", "zh", "en" };

    [Serializable] private sealed class ConfigFile { public ConfigFileEntry[] entries = null; }
    [Serializable] private sealed class ConfigFileEntry { public string key = null; public string value = null; }

    private readonly string captureId = Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly float deadline = Time.realtimeSinceStartup + 170f;
    private GameOptionUIManager options;
    private GameOptionRegistry.Entry playbackKey;
    private LoadedMod mod;
    private ModConfig config;
    private Dictionary<string, object> original;
    private string originalLanguage, screenshot;
    private int originalMode, originalResolution, originalDivisor;
    private KeyCode originalPlaybackKey;
    private int originalPlaybackPreference;
    private bool hadPlaybackPreference;
    private int step, round, pass, waitFrame;
    private float stableAfter, screenshotDeadline;
    private CustomUIWindow window;
    private bool initialized, restored;

    public bool Tick(out string result)
    {
        try { return TickCore(out result); }
        catch
        {
            try { RestoreImmediately(); }
            catch (Exception error) { Debug.LogWarning("[PLAYER-QA] Settings cleanup failed: " + error); }
            throw;
        }
    }

    private bool TickCore(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Settings presentation timed out at step " + step + " pass " + pass);
        if (step == 0)
        {
            if (MainMenuUIController.Instance == null || CustomUIManager.Instance == null || EmporiumOption.Instance == null) return false;
            options = Resources.FindObjectsOfTypeAll<GameOptionUIManager>().FirstOrDefault(value => value.gameObject.scene.IsValid());
            mod = ModLoader.Mods.Find(value => value.Manifest != null && value.Manifest.ID == Mod);
            config = ModConfig.Find(Mod);
            if (options == null || mod == null || config == null) return false;
            Need(mod.IsEnabled && mod.Instance != null && mod.LoadError == null, "ShopAssist is not enabled");
            Need(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "Run settings presentation with the rendered Player");
            Need(config.Entries.Select(entry => entry.Key).Distinct().Count() == config.Entries.Count,
                "Duplicate saved settings");
            for (int feature = 1; feature <= 42; feature++)
            {
                if (feature == 6) continue;
                Need(config.Get("f" + feature.ToString("00") + ".enabled")?.ValueType == typeof(bool),
                    "Missing feature switch: " + feature);
            }
            Need(!config.Entries.Any(entry => entry.Key == "f06.enabled"), "Removed F06 switch is still registered");
            Need(config.Get("f19.pinned")?.ValueType == typeof(string), "Missing pinned liquid setting");
            original = Snapshot();
            originalLanguage = LocHelper.GetCurrentLocaleCode();
            Need(!string.IsNullOrEmpty(originalLanguage), "Current native locale is unavailable");
            originalMode = EmporiumOption.Instance.fullscreenMode;
            originalResolution = EmporiumOption.Instance.resolution;
            originalDivisor = EmporiumOption.Instance.borderlessDivisor;
            initialized = true;
            playbackKey = GameOptionRegistry.Sections.SelectMany(section => section.entries)
                .FirstOrDefault(entry => entry.id == Mod + ":f42.key");
            Need(playbackKey?.getKey != null && playbackKey.setKey != null, "Missing playback key registration");
            originalPlaybackKey = playbackKey.getKey();
            hadPlaybackPreference = PlayerPrefs.HasKey(Mod + ".f42.key");
            originalPlaybackPreference = PlayerPrefs.GetInt(Mod + ".f42.key", (int)KeyCode.None);
            playbackKey.setKey(KeyCode.None);
            PlayerPrefs.SetInt(Mod + ".f42.key", (int)KeyCode.None);
            PlayerPrefs.Save();
            config.Save();
            VerifyFile();
            step = 1;
            return false;
        }
        if (step == 1)
        {
            CheckCancel();
            TestCrossTierApply();
            step = 2;
            return false;
        }
        if (step == 2)
        {
            TestChoices(round++);
            if (round == 4) step = 3;
            return false;
        }
        if (step == 3)
        {
            TestResetAndRestore();
            ChangeDisplay();
            step = 4;
            return false;
        }
        if (step == 4)
        {
            if (Screen.width != Widths[pass] || Screen.height != Heights[pass] ||
                LocHelper.GetCurrentLocaleCode() != Languages[pass] || Time.realtimeSinceStartup < stableAfter || Time.frameCount < waitFrame)
                return false;
            window = OpenNative();
            waitFrame = Time.frameCount + 4;
            step = 5;
            return false;
        }
        if (step == 5)
        {
            if (Time.frameCount < waitFrame) return false;
            CheckPresentation(window, true, Languages[pass]);
            CheckPlaybackJump(window, true, Languages[pass]);
            CheckSamples(window, true, Languages[pass]);
            if (pass == 0 || pass == 3) Capture("native");
            waitFrame = Time.frameCount + 4;
            step = 6;
            return false;
        }
        if (step == 6)
        {
            if (Time.frameCount < waitFrame || !CaptureReady()) return false;
            options.CloseUI();
            window = OpenMods();
            waitFrame = Time.frameCount + 4;
            step = 7;
            return false;
        }
        if (step == 7)
        {
            if (Time.frameCount < waitFrame) return false;
            CheckPresentation(window, false, Languages[pass]);
            CheckPlaybackJump(window, false, Languages[pass]);
            CheckSamples(window, false, Languages[pass]);
            if (pass == 1 || pass == 2) Capture("mods");
            waitFrame = Time.frameCount + 4;
            step = 8;
            return false;
        }
        if (step == 8)
        {
            if (Time.frameCount < waitFrame || !CaptureReady()) return false;
            window.Close(); window = null;
            if (++pass < Widths.Length) { ChangeDisplay(); step = 4; return false; }
            RestoreDisplayAndLanguage();
            waitFrame = Time.frameCount + 10;
            step = 9;
            return false;
        }
        if (step == 9)
        {
            if (Time.frameCount < waitFrame || LocHelper.GetCurrentLocaleCode() != originalLanguage) return false;
            AssertValues(original, "final restoration");
            VerifyFile();
            restored = true;
            result = "PASS " + original.Count + " settings across native and Mods views; unbound playback jump preserves drafts; grouped rows, localized labels and dropdowns; draft/cancel/reset/apply/persistence at 1280x720 and 1920x1080 in Chinese and English";
            return true;
        }
        throw new InvalidOperationException("Unknown settings presentation step " + step);
    }

    private void CheckCancel()
    {
        byte[] saved = File.ReadAllBytes(config.FilePath);
        window = OpenNative();
        var nativeChoice = Control(window, "f09.mode", true);
        nativeChoice.SetIndex(((int)original["f09.mode"] + 1) % ChoiceLabels[0].Length, true);
        Control(window, "f15.enabled", true).SetBool(!(bool)original["f15.enabled"], true);
        Control(window, "f05.auto_insert", true).SetBool(!(bool)original["f05.auto_insert"], true);
        Control(window, "f04.unseen", true).SetBool(!(bool)original["f04.unseen"], true);
        AssertValues(original, "native draft"); AssertFile(saved, "native draft");
        CloseWithConfirmation(true, () =>
        {
            Need(Control(window, "f09.mode", true).GetIndex() == ((int)original["f09.mode"] + 1) % ChoiceLabels[0].Length &&
                Control(window, "f15.enabled", true).GetBool() != (bool)original["f15.enabled"] &&
                Control(window, "f05.auto_insert", true).GetBool() != (bool)original["f05.auto_insert"] &&
                Control(window, "f04.unseen", true).GetBool() != (bool)original["f04.unseen"], "Continuing native settings lost the draft");
        });
        window = OpenNative();
        Need(Control(window, "f09.mode", true).GetIndex() == (int)original["f09.mode"] &&
            Control(window, "f15.enabled", true).GetBool() == (bool)original["f15.enabled"] &&
            Control(window, "f05.auto_insert", true).GetBool() == (bool)original["f05.auto_insert"] &&
            Control(window, "f04.unseen", true).GetBool() == (bool)original["f04.unseen"], "Closing native options did not cancel the draft");
        options.CloseUI(); AssertValues(original, "native cancel"); AssertFile(saved, "native cancel");

        window = OpenMods();
        Control(window, "f24.destination", false).SetIndex(((int)original["f24.destination"] + 1) % ChoiceLabels[3].Length, true);
        Control(window, "f15.enabled", false).SetBool(!(bool)original["f15.enabled"], true);
        Control(window, "f05.auto_insert", false).SetBool(!(bool)original["f05.auto_insert"], true);
        Control(window, "f04.unseen", false).SetBool(!(bool)original["f04.unseen"], true);
        AssertValues(original, "Mods draft"); AssertFile(saved, "Mods draft");
        CloseWithConfirmation(false, () =>
        {
            Need(Control(window, "f24.destination", false).GetIndex() == ((int)original["f24.destination"] + 1) % ChoiceLabels[3].Length &&
                Control(window, "f15.enabled", false).GetBool() != (bool)original["f15.enabled"] &&
                Control(window, "f05.auto_insert", false).GetBool() != (bool)original["f05.auto_insert"] &&
                Control(window, "f04.unseen", false).GetBool() != (bool)original["f04.unseen"], "Continuing Mods settings lost the draft");
        });
        window = OpenMods();
        Need(Control(window, "f24.destination", false).GetIndex() == (int)original["f24.destination"] &&
            Control(window, "f15.enabled", false).GetBool() == (bool)original["f15.enabled"] &&
            Control(window, "f05.auto_insert", false).GetBool() == (bool)original["f05.auto_insert"] &&
            Control(window, "f04.unseen", false).GetBool() == (bool)original["f04.unseen"], "Closing Mods settings did not cancel the draft");
        window.Close(); window = null;
        AssertValues(original, "Mods cancel"); AssertFile(saved, "Mods cancel");
    }

    private void CloseWithConfirmation(bool native, Action verifyDraft)
    {
        CustomUIWindow owner = window;
        Dictionary<string, object> committed = Snapshot();
        byte[] saved = File.ReadAllBytes(config.FilePath);
        void CloseOwner() { if (native) options.CloseUI(); else owner.Close(); }
        CustomUIWindow Prompt() => CustomUIManager.Instance.GetWindow(Mod + ".settings.discard");
        CloseOwner();
        var prompt = Prompt();
        Need(owner?.IsAlive == true && prompt?.IsAlive == true && prompt.Get("continue")?.button != null,
            "Closing an edited settings view did not show confirmation");
        prompt.Get("continue").button.onClick.Invoke();
        Need(owner.IsAlive && prompt.IsAlive != true, "Continue did not keep the settings window");
        verifyDraft();
        AssertValues(committed, "continue editing"); AssertFile(saved, "continue editing");
        CloseOwner();
        prompt = Prompt();
        Need(owner.IsAlive && prompt?.IsAlive == true && prompt.Get("discard")?.button != null,
            "Closing the same draft again did not offer discard");
        prompt.Get("discard").button.onClick.Invoke();
        Need(!owner.IsAlive, "Discard did not close the settings window");
        AssertValues(committed, "discard draft"); AssertFile(saved, "discard draft");
    }

    private void TestCrossTierApply()
    {
        string[] keys = { "f01.wholesale", "f05.auto_insert", "f04.unseen" };
        foreach (bool native in new[] { true, false })
        {
            window = native ? OpenNative() : OpenMods();
            byte[] saved = File.ReadAllBytes(config.FilePath);
            foreach (string key in keys) Control(window, key, native).SetBool(!(bool)original[key], true);
            AssertValues(original, "cross-tier draft"); AssertFile(saved, "cross-tier draft");
            Apply(window);
            foreach (string key in keys)
                Need((bool)config.Get(key).BoxedValue != (bool)original[key], "Apply did not commit " + key);
            foreach (string key in keys) Control(window, key, native).SetBool((bool)original[key], true);
            Apply(window);
            AssertValues(original, "cross-tier restore"); VerifyFile();
            if (native) options.CloseUI(); else window.Close();
            window = null;
        }
    }

    private void TestChoices(int n)
    {
        Dictionary<string, object> before = Snapshot();
        byte[] saved = File.ReadAllBytes(config.FilePath);
        window = OpenNative();
        for (int i = 0; i < ChoiceKeys.Length; i++)
            Choice(window, ChoiceKeys[i], true, ChoiceLabels[i].Length).SetIndex(n % ChoiceLabels[i].Length, true);
        AssertValues(before, "native choice draft"); AssertFile(saved, "native choice draft");
        Apply(window);
        for (int i = 0; i < ChoiceKeys.Length; i++)
            Need((int)config.Get(ChoiceKeys[i]).BoxedValue == n % ChoiceLabels[i].Length, "Native Apply lost " + ChoiceKeys[i]);
        AssertOtherValues(original); VerifyFile();
        options.CloseUI();

        before = Snapshot(); saved = File.ReadAllBytes(config.FilePath);
        window = OpenMods();
        for (int i = 0; i < ChoiceKeys.Length; i++)
        {
            var control = Choice(window, ChoiceKeys[i], false, ChoiceLabels[i].Length);
            Need(control.GetIndex() == n % ChoiceLabels[i].Length, "Mods did not reflect native Apply for " + ChoiceKeys[i]);
            control.SetIndex((n + 1) % ChoiceLabels[i].Length, true);
        }
        AssertValues(before, "Mods choice draft"); AssertFile(saved, "Mods choice draft");
        Apply(window);
        for (int i = 0; i < ChoiceKeys.Length; i++)
            Need((int)config.Get(ChoiceKeys[i]).BoxedValue == (n + 1) % ChoiceLabels[i].Length, "Mods Apply lost " + ChoiceKeys[i]);
        AssertOtherValues(original); VerifyFile();
        window.Close();
        window = OpenNative();
        for (int i = 0; i < ChoiceKeys.Length; i++)
            Need(Choice(window, ChoiceKeys[i], true, ChoiceLabels[i].Length).GetIndex() == (n + 1) % ChoiceLabels[i].Length,
                "Native options did not reflect Mods Apply for " + ChoiceKeys[i]);
        options.CloseUI(); window = null;
    }

    private void TestResetAndRestore()
    {
        window = OpenMods();
        ConfigEntry toggle = config.Get("f15.enabled");
        Control(window, toggle.Key, false).SetBool(!(bool)toggle.BoxedDefault, true);
        ConfigEntry choice = config.Get("f09.mode");
        Control(window, choice.Key, false).SetIndex(((int)choice.BoxedDefault + 1) % ChoiceLabels[0].Length, true);
        Control(window, "f16.volume", false).SetFloat(73, true);
        Need(window.Get("value_f16.volume")?.GetText() == "73", "Numeric readout did not follow the pending slider value");
        Apply(window);
        Need(!Equals(toggle.BoxedValue, toggle.BoxedDefault) && !Equals(choice.BoxedValue, choice.BoxedDefault), "Could not prepare non-default reset values");
        Dictionary<string, object> before = Snapshot();
        byte[] saved = File.ReadAllBytes(config.FilePath);
        Reset(window);
        AssertDraftDefaults(window); AssertValues(before, "reset draft"); AssertFile(saved, "reset draft");
        CloseWithConfirmation(false, () => AssertDraftDefaults(window));
        window = OpenMods();
        Need(Equals(WidgetValue(Control(window, toggle.Key, false), toggle), before[toggle.Key]) &&
            Equals(WidgetValue(Control(window, choice.Key, false), choice), before[choice.Key]), "Cancel after Reset lost committed values");
        Reset(window); AssertDraftDefaults(window); Apply(window);
        foreach (var entry in config.Entries)
            Need(Equals(entry.BoxedValue, entry.Key == "f19.pinned" ? before[entry.Key] : entry.BoxedDefault), "Reset Apply changed an unexpected value for " + entry.Key);
        VerifyFile();
        foreach (var entry in config.Entries.Where(entry => entry.Key != "f19.pinned")) SetWidget(Control(window, entry.Key, false), entry, original[entry.Key]);
        Apply(window);
        AssertValues(original, "restoring all settings through Mods Apply"); VerifyFile();
        window.Close(); window = null;
    }

    private void CheckPresentation(CustomUIWindow view, bool native, string language)
    {
        Need(mod.Localization.TryGetValue(language, out var words), "Missing locale " + language);
        Canvas.ForceUpdateCanvases();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int tier = 0; tier < Sections.Length; tier++)
        {
            string sectionId = Mod + ":" + Sections[tier];
            var registry = GameOptionRegistry.FindSection(sectionId);
            Need(registry != null && registry.entries.Count > 0, "Missing native section " + sectionId);
            var nativeKeys = registry.entries.Select(entry => entry.id.Substring((Mod + ":").Length)).ToArray();
            Need(nativeKeys.All(key => config.Get(key) != null), "Native section contains an unknown saved setting");
            string gearSection = config.Get(nativeKeys[0]).Section;
            var expected = config.Entries.Where(entry => entry.Section == gearSection).Select(entry => entry.Key).ToArray();
            string titleId = "section_" + (native ? sectionId : gearSection);
            string title = string.Format(words["settings.tier_heading"], words["section.tier" + (tier + 1)], words["section.tier" + (tier + 1) + ".description"]);
            Need(view.Get(titleId)?.GetText() == title,
                "Wrong " + language + " section title: " + titleId);
            Need(expected.All(key => Tier(key) == tier), "Setting in the wrong tier: " + sectionId);
            var keys = expected.Where(key => key != "f19.pinned").ToArray();
            Need(new HashSet<string>(keys).SetEquals(native ? nativeKeys : expected.Where(key => key != "f19.pinned")), "Section membership differs: " + sectionId);
            var rows = new List<KeyValuePair<string, Transform>>();
            foreach (string key in keys)
            {
                Need(seen.Add(key), "Setting appears twice: " + key);
                string id = native ? Mod + ":" + key : key;
                var control = view.Get(id);
                var label = view.Get("label_" + id);
                Need(control?.root != null && label?.text != null && words.TryGetValue(key, out string expectedLabel) &&
                    label.GetText().Contains(expectedLabel), "Missing or untranslated actual label " + id);
                Transform row = label.root.transform.parent;
                Need(row != null && row.name == "ShopAssist_Setting_" + key && control.root.transform.parent == row,
                    "Missing named setting row " + id);
                var layout = row.GetComponent<HorizontalLayoutGroup>();
                bool primary = Primary(key);
                int idNumber = int.Parse(key.Substring(1, 2));
                bool grouped = idNumber == 8 || idNumber == 9 || idNumber == 12;
                Need(layout != null && layout.padding.left == (primary ? (grouped ? 12 : 0) : (grouped ? 30 : 18)) &&
                    layout.padding.top == (primary ? (grouped ? 3 : 9) : 1) && layout.padding.bottom == (primary ? 3 : 1),
                    "Wrong setting indentation or group spacing: " + id);
                if (key == "f07.enabled" || key == "f10.enabled")
                    Need(label.GetText().Contains(words["settings.group" + (key == "f07.enabled" ? "7" : "10")]),
                        "Missing related-feature group label: " + key);
                Need(((label.text.fontStyle & FontStyles.Bold) != 0) == primary, "Wrong primary emphasis: " + id);
                rows.Add(new KeyValuePair<string, Transform>(key, row));
            }
            var ordered = rows.OrderBy(pair => pair.Value.GetSiblingIndex()).Select(pair => pair.Key).ToArray();
            Need(ordered.Select(Rank).SequenceEqual(ordered.Select(Rank).OrderBy(value => value)), "Rows are not grouped by feature in " + sectionId);
            foreach (var category in ordered.GroupBy(Category))
            {
                string first = category.First();
                string id = native ? Mod + ":" + first : first;
                Need(view.Get("label_" + id).GetText().Contains(words["section.category" + (category.Key + 1)]),
                    "Missing purpose group heading in " + sectionId + ": " + first);
            }
            foreach (var group in ordered.GroupBy(key => key.Substring(0, 3)))
            {
                string[] members = group.ToArray();
                string master = group.Key + ".enabled";
                string first = members.Contains(master) ? master : group.Key == "f05" ? "f05.auto_insert" : "f04.unseen";
                Need(members[0] == first, "Group has no leading primary: " + group.Key);
                if (first == "f05.auto_insert" || first == "f04.unseen")
                {
                    string id = native ? Mod + ":" + first : first;
                    Need(view.Get("label_" + id).GetText().Contains(words[master]), "Cross-tier option omitted its parent: " + first);
                }
            }
        }
        Need(seen.Count == config.Entries.Count - 1, "Settings were lost from " + (native ? "native options" : "Mods"));
        var storedPins = view.Get(native ? Mod + ":f19.pinned" : "f19.pinned");
        Need(storedPins?.root?.activeInHierarchy != true, "Stored pin data is exposed as a raw field");
        for (int i = 0; i < ChoiceKeys.Length; i++)
        {
            var control = Choice(view, ChoiceKeys[i], native, ChoiceLabels[i].Length);
            Need(config.Get(ChoiceKeys[i]).ValueType == typeof(int), "Choice did not keep integer storage: " + ChoiceKeys[i]);
            for (int j = 0; j < ChoiceLabels[i].Length; j++)
                Need(words.TryGetValue(ChoiceLabels[i][j], out string label) && control.dropdown.options[j].text == label,
                    "Wrong localized choice: " + ChoiceKeys[i] + " index " + j + " locale " + language);
        }
    }

    private void CheckSamples(CustomUIWindow view, bool native, string language)
    {
        var words = mod.Localization[language];
        var keys = config.Entries.Where(entry => !native || entry.Key != "f19.pinned")
            .OrderByDescending(entry => words[entry.Key].Length).Take(3).Select(entry => entry.Key)
            .Concat(new[] { "f05.auto_insert", "f04.unseen" }).Distinct().ToArray();
        Need(keys.Length >= 3, "Not enough long settings to inspect");
        foreach (string key in keys)
        {
            string id = native ? Mod + ":" + key : key;
            TMP_Text text = view.Get("label_" + id)?.text;
            var control = Control(view, key, native);
            Need(text != null && text.gameObject.activeInHierarchy && text.font != null, "Missing rendered label " + id);
            ScrollTo(text.rectTransform);
            text.ForceMeshUpdate(true, true);
            Need(text.textInfo.characterCount > 0 && !text.isTextOverflowing && !text.isTextTruncated,
                "Label overflow or truncation: " + id + " locale " + language);
            var characters = text.textInfo.characterInfo.Take(text.textInfo.characterCount)
                .Where(character => character.elementType == TMP_TextElementType.Character && !char.IsWhiteSpace(character.character)).ToArray();
            Need(characters.Length > 0 && characters.All(character => character.isVisible && character.textElement != null &&
                character.textElement.unicode == character.character), "Missing or substituted glyph in " + id + " locale " + language);
            Rect area = text.rectTransform.rect;
            Bounds rendered = text.textBounds;
            Need(rendered.min.x >= area.xMin - 2 && rendered.max.x <= area.xMax + 2 &&
                rendered.min.y >= area.yMin - 2 && rendered.max.y <= area.yMax + 2,
                "Rendered text exceeds its label rectangle: " + id + " rect=" + area + " text=" + rendered);
            Rect labelBounds = ScreenBounds(text.rectTransform), controlBounds = ScreenBounds(control.Rect);
            Need(labelBounds.xMax <= controlBounds.xMin + 2, "Label overlaps its control: " + id);
            var scroll = text.GetComponentInParent<ScrollRect>();
            Need(scroll?.viewport != null, "Label has no scroll viewport: " + id);
            Rect viewport = ScreenBounds(scroll.viewport);
            Need(labelBounds.xMin >= viewport.xMin - 2 && labelBounds.xMax <= viewport.xMax + 2 &&
                labelBounds.yMin >= viewport.yMin - 2 && labelBounds.yMax <= viewport.yMax + 2,
                "Long label is clipped by its viewport: " + id + " label=" + labelBounds + " viewport=" + viewport);
        }
        string representative = native ? Mod + ":" + keys[0] : keys[0];
        ScrollTo(view.Get("label_" + representative).text.rectTransform);
    }

    private void CheckPlaybackJump(CustomUIWindow view, bool native, string language)
    {
        var jump = view.Get(Mod + ":f42.key.jump");
        Need(jump?.button != null && jump.text != null && jump.root.activeInHierarchy && jump.button.IsInteractable(),
            "Missing playback shortcut jump in " + (native ? "native options" : "Mods settings"));
        ScrollTo(jump.Rect);
        Canvas.ForceUpdateCanvases();
        string expected = string.Format(mod.Localization[language]["settings.playback_key"], mod.Localization[language]["settings.key_unbound"]);
        jump.text.ForceMeshUpdate(true, true);
        Need(jump.GetText() == expected && jump.text.textInfo.characterInfo.Take(jump.text.textInfo.characterCount).Any(character => character.isVisible) &&
            !jump.text.isTextTruncated && !jump.text.isTextOverflowing,
            "Unbound playback status is missing or truncated: " + jump.GetText());
        var jumpScroll = jump.Rect.GetComponentInParent<ScrollRect>();
        Need(jumpScroll?.viewport != null, "Playback shortcut jump has no settings viewport");
        Rect jumpArea = ScreenBounds(jumpScroll.viewport), jumpBounds = ScreenBounds(jump.Rect);
        Need(jumpBounds.xMin >= jumpArea.xMin - 2 && jumpBounds.xMax <= jumpArea.xMax + 2 &&
            jumpBounds.yMin >= jumpArea.yMin - 2 && jumpBounds.yMax <= jumpArea.yMax + 2,
            "Playback shortcut jump is outside its settings viewport");

        var draft = Control(view, "f15.enabled", native);
        bool before = draft.GetBool();
        bool committed = (bool)config.Get("f15.enabled").BoxedValue;
        byte[] saved = File.ReadAllBytes(config.FilePath);
        draft.SetBool(!before, true);
        jump.button.onClick.Invoke();
        var nativeWindow = CustomUIManager.Instance.GetWindow("game_options");
        Need(nativeWindow?.IsAlive == true && (!native || ReferenceEquals(nativeWindow, view)) && view.IsAlive &&
            draft.GetBool() == !before && (bool)config.Get("f15.enabled").BoxedValue == committed,
            "Playback jump replaced the settings window or lost its unapplied draft");
        var key = nativeWindow.Get(Mod + ":f42.key");
        Need(key?.root != null && key.button != null && playbackKey.getKey() == KeyCode.None,
            "Playback jump did not open the unbound native key control");
        Canvas.ForceUpdateCanvases();
        var keyScroll = key.Rect.GetComponentInParent<ScrollRect>();
        Need(keyScroll?.viewport != null && ScreenBounds(keyScroll.viewport).Contains(ScreenBounds(key.Rect).center),
            "Playback jump did not locate its native key control");
        AssertFile(saved, "playback key jump");
        if (!native) options.CloseUI();
        draft.SetBool(before, true);
    }

    private static void ScrollTo(RectTransform rect)
    {
        var scroll = rect.GetComponentInParent<ScrollRect>();
        Need(scroll?.viewport != null && scroll.content != null, "Missing settings scroll viewport");
        Canvas.ForceUpdateCanvases();
        float hidden = scroll.content.rect.height - scroll.viewport.rect.height;
        if (hidden > 0)
        {
            Vector3 local = scroll.viewport.InverseTransformPoint(rect.TransformPoint(rect.rect.center));
            scroll.StopMovement();
            scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll.verticalNormalizedPosition + (local.y - scroll.viewport.rect.center.y) / hidden);
            Canvas.ForceUpdateCanvases();
        }
    }

    private static Rect ScreenBounds(RectTransform rect)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return Rect.MinMaxRect(low.x, low.y, high.x, high.y);
    }

    private void Capture(string entrance)
    {
        string folder = Application.persistentDataPath;
        System.IO.Directory.CreateDirectory(folder);
        screenshot = Path.Combine(folder, "qa-settings-" + Widths[pass] + "x" + Heights[pass] + "-" + Languages[pass] + "-" + entrance + "-" + captureId + ".png");
        screenshotDeadline = Time.realtimeSinceStartup + 8f;
        ScreenCapture.CaptureScreenshot(screenshot);
    }

    private bool CaptureReady()
    {
        if (screenshot == null) return true;
        if (File.Exists(screenshot) && new FileInfo(screenshot).Length > 0) { screenshot = null; return true; }
        Need(Time.realtimeSinceStartup < screenshotDeadline, "Settings screenshot was not written: " + screenshot);
        return false;
    }

    private void ChangeDisplay()
    {
        options.CloseUI();
        options.StartCoroutine(LocHelper.SetLocale(Languages[pass]));
        EmporiumOption.Instance.fullscreenMode = 2;
        EmporiumOption.Instance.resolution = Resolutions[pass];
        EmporiumOption.Instance.ApplySetting();
        stableAfter = Time.realtimeSinceStartup + 1.5f;
        waitFrame = Time.frameCount + 10;
    }

    private void RestoreDisplayAndLanguage()
    {
        RestorePlaybackKey();
        options.StartCoroutine(LocHelper.SetLocale(originalLanguage));
        EmporiumOption.Instance.fullscreenMode = originalMode;
        EmporiumOption.Instance.resolution = originalResolution;
        EmporiumOption.Instance.borderlessDivisor = originalDivisor;
        EmporiumOption.Instance.ApplySetting();
    }

    private void RestorePlaybackKey()
    {
        if (playbackKey == null) return;
        playbackKey.setKey(originalPlaybackKey);
        if (hadPlaybackPreference) PlayerPrefs.SetInt(Mod + ".f42.key", originalPlaybackPreference);
        else PlayerPrefs.DeleteKey(Mod + ".f42.key");
        PlayerPrefs.Save();
        playbackKey = null;
    }

    private void RestoreImmediately()
    {
        if (!initialized || restored) return;
        if (window?.IsAlive == true) window.Close();
        options?.CloseUI();
        foreach (var entry in config.Entries)
        {
            object value = original[entry.Key];
            if (entry is ConfigEntry<bool> boolean) boolean.Set((bool)value);
            else if (entry is ConfigEntry<int> number) number.Set((int)value);
            else if (entry is ConfigEntry<string> text) text.Set((string)value);
            else throw new InvalidOperationException("Unsupported setting type during cleanup: " + entry.Key);
        }
        config.Save();
        RestoreDisplayAndLanguage();
        restored = true;
    }

    private CustomUIWindow OpenNative()
    {
        options.OpenUI();
        var result = CustomUIManager.Instance.GetWindow("game_options");
        Need(result?.IsAlive == true, "Native options did not open");
        return result;
    }
    private CustomUIWindow OpenMods()
    {
        var result = ModConfigWindow.Open(mod);
        Need(result?.IsAlive == true, "Mods settings did not open");
        return result;
    }
    private static CustomUIElement Control(CustomUIWindow view, string key, bool native)
    {
        var value = view.Get(native ? Mod + ":" + key : key);
        Need(value?.IsAlive == true, "Missing settings control " + key);
        return value;
    }
    private static CustomUIElement Choice(CustomUIWindow view, string key, bool native, int count)
    {
        var value = Control(view, key, native);
        Need(value.dropdown != null && value.slider == null && value.dropdown.options.Count == count,
            "Expected a text dropdown with " + count + " choices: " + key);
        return value;
    }
    private static bool Primary(string key) => key.EndsWith(".enabled", StringComparison.Ordinal) || key == "f04.unseen" || key == "f05.auto_insert" || key == "f01.wholesale";
    private static int Tier(string key)
    {
        if (key == "f05.auto_insert" || key == "f01.wholesale") return 1;
        if (key == "f04.unseen") return 2;
        int id = int.Parse(key.Substring(1, 2));
        return Array.FindIndex(Tiers, values => values.Contains(id));
    }
    private static int Category(string key)
    {
        int id = int.Parse(key.Substring(1, 2));
        return Array.FindIndex(Categories, values => values.Contains(id));
    }
    private static int Rank(string key)
    {
        int id = int.Parse(key.Substring(1, 2));
        int group = id == 8 || id == 9 ? 7 : id == 12 ? 10 : id;
        return Category(key) * 1000000 + group * 1000 + id * 10 +
            (key == "f" + id.ToString("00") + ".enabled" ? 0 : Primary(key) ? 1 : 2);
    }
    private static void Apply(CustomUIWindow view) { var button = view.Get("apply")?.button; Need(button != null, "Missing Apply button"); button.onClick.Invoke(); }
    private static void Reset(CustomUIWindow view) { var button = view.Get("reset")?.button; Need(button != null, "Missing Reset button"); button.onClick.Invoke(); }
    private Dictionary<string, object> Snapshot() => config.Entries.ToDictionary(entry => entry.Key, entry => entry.BoxedValue, StringComparer.Ordinal);
    private void AssertValues(Dictionary<string, object> expected, string phase)
    {
        Need(config.Entries.Count == original.Count, "Saved setting count changed during " + phase);
        foreach (var entry in config.Entries)
            Need(expected.TryGetValue(entry.Key, out object value) && Equals(entry.BoxedValue, value), "Unexpected value for " + entry.Key + " during " + phase);
    }
    private void AssertOtherValues(Dictionary<string, object> expected)
    {
        foreach (var entry in config.Entries)
            if (!ChoiceKeys.Contains(entry.Key)) Need(Equals(entry.BoxedValue, expected[entry.Key]), "Choice Apply changed another setting: " + entry.Key);
    }
    private static void AssertFile(byte[] expected, string phase) => Need(File.ReadAllBytes(ModConfig.Find(Mod).FilePath).SequenceEqual(expected), "Config file changed during " + phase);
    private void VerifyFile()
    {
        Need(File.Exists(config.FilePath), "Saved config file is missing");
        ConfigFile data = JsonUtility.FromJson<ConfigFile>(File.ReadAllText(config.FilePath));
        Need(data?.entries != null && data.entries.Select(entry => entry.key).Distinct().Count() == data.entries.Length, "Malformed or duplicate saved config entries");
        var entries = data.entries.ToDictionary(entry => entry.key, entry => entry.value, StringComparer.Ordinal);
        foreach (var entry in config.Entries)
            Need(entries.TryGetValue(entry.Key, out string value) && value == entry.Serialize(), "Saved config is missing or changed: " + entry.Key);
    }
    private void AssertDraftDefaults(CustomUIWindow view)
    {
        foreach (var entry in config.Entries.Where(entry => entry.Key != "f19.pinned"))
            Need(Equals(WidgetValue(Control(view, entry.Key, false), entry), entry.BoxedDefault), "Reset did not show default for " + entry.Key);
        foreach (var entry in config.Entries.Where(value => value.ValueType == typeof(int) && !ChoiceKeys.Contains(value.Key)))
            Need(view.Get("value_" + entry.Key)?.GetText() == entry.BoxedDefault.ToString(), "Reset left a stale numeric readout for " + entry.Key);
    }
    private static object WidgetValue(CustomUIElement widget, ConfigEntry entry)
    {
        if (entry.ValueType == typeof(bool)) return widget.GetBool();
        if (entry.ValueType == typeof(int)) return widget.dropdown != null ? widget.GetIndex() : Mathf.RoundToInt(widget.GetFloat());
        if (entry.ValueType == typeof(string)) return widget.GetString();
        throw new InvalidOperationException("Unsupported config type: " + entry.Key);
    }
    private static void SetWidget(CustomUIElement widget, ConfigEntry entry, object value)
    {
        if (entry.ValueType == typeof(bool)) widget.SetBool((bool)value, true);
        else if (entry.ValueType == typeof(int) && widget.dropdown != null) widget.SetIndex((int)value, true);
        else if (entry.ValueType == typeof(int)) widget.SetFloat((int)value, true);
        else if (entry.ValueType == typeof(string)) widget.SetString((string)value, true);
        else throw new InvalidOperationException("Unsupported config type: " + entry.Key);
    }
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
