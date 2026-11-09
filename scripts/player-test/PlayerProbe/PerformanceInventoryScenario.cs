using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.UI;

// Identical native fixtures and interactions can be run against either package.
// Samples are real ShopLoop-driven ModEntry.Update calls, never manually invoked updates.
public class PerformanceInventoryScenario
{
    private const string Mod = "starswhere.shopassist";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Frames = 60, WarmupFrames = 12;
    private static readonly string[] Enabled = { "f05.enabled", "f08.enabled", "f09.enabled", "f10.enabled", "f12.enabled", "f40.enabled", "f41.enabled" };
    private static PerformanceInventoryScenario active;
    private readonly QaNewRun start = new QaNewRun();
    private readonly List<GameItemElement>[] goods = { new List<GameItemElement>(), new List<GameItemElement>() };
    private readonly GameItem[] bags = new GameItem[2];
    private readonly GameGridInventory[] grids = new GameGridInventory[2];
    private readonly List<string> rows = new List<string>();
    private readonly long[] ticks = new long[Frames], bytes = new long[Frames];
    private readonly long[] groupTicks = new long[Frames * 4];
    private Harmony harmony;
    private ProfilerRecorder allocations;
    private QaSyntheticInput input;
    private GameItem machine;
    private int state, scale, kind, round, warmUntil, sampleCount, actions;
    private int locations, updateLocations, windows, updateWindows, groupUpdates, groupChecks;
    private long beginTicks, beginBytes, actionTicks, actionBytes, groupStart;
    private int groupSamples;
    private bool measuring, inUpdate, inGroupUpdate;
    private bool requestedResolution, localeRequested, localeComplete, localeReady;
    private float resolutionDeadline, localeDeadline, stableAfter, warmAfter;
    private int stableFrame;
    private string allocationSource = "unsupported";
    private long allocationProbe;
    private static byte[] calibrationBuffer;
    private string output, environmentOutput;
    private string[] environment;
    private readonly int finalKind;
    private readonly string part;

    protected PerformanceInventoryScenario(bool large, int firstKind = 0, int finalKind = 5, string part = null)
    {
        scale = large ? 1 : 0; kind = firstKind; this.finalKind = finalKind; this.part = part;
    }

    public bool Tick(out string result)
    {
        result = null;
        if (state == 0)
        {
            if (!requestedResolution)
            {
                requestedResolution = true;
                resolutionDeadline = Time.realtimeSinceStartup + 5f;
                Screen.SetResolution(1024, 768, false);
                return false;
            }
            if (Screen.width != 1024 || Screen.height != 768)
            {
                if (Time.realtimeSinceStartup >= resolutionDeadline)
                    throw new InvalidOperationException("Could not set the isolated Player to 1024x768: " + Screen.width + "x" + Screen.height);
                return false;
            }
            if (!localeRequested)
            {
                if (MainMenuUIController.Instance == null || CustomUIManager.Instance == null) return false;
                localeRequested = true;
                localeDeadline = Time.realtimeSinceStartup + 20f;
                ES3.Save("locale", "en");
                if (LocHelper.GetCurrentLocaleCode() == "en") localeComplete = true;
                else MainMenuUIController.Instance.StartCoroutine(SetEnglish());
                return false;
            }
            if (!localeReady)
            {
                if (!localeComplete || LocHelper.GetCurrentLocaleCode() != "en")
                {
                    if (Time.realtimeSinceStartup >= localeDeadline) throw new InvalidOperationException("English locale did not settle in the main menu: complete=" + localeComplete + ", locale=" + LocHelper.GetCurrentLocaleCode());
                    return false;
                }
                if (stableAfter == 0)
                {
                    stableAfter = Time.realtimeSinceStartup + .5f; stableFrame = Time.frameCount + 3;
                    return false;
                }
                if (Time.realtimeSinceStartup < stableAfter || Time.frameCount < stableFrame) return false;
                localeReady = true;
            }
            if (!start.Tick()) return false;
            state = 1; return false;
        }
        if (state == 1)
        {
            if (Screen.width != 1024 || Screen.height != 768) { Screen.SetResolution(1024, 768, false); return false; }
            if (ItemMouseDoubleClickHandler.current == null || ItemMultiSelectHandler.current == null ||
                StoreUIManager.Instance?.gameOptionUIManager == null) return false;
            ConfigureEnvironment();
            state = 5; return false;
        }
        if (state == 5)
        {
            if (Time.realtimeSinceStartup >= resolutionDeadline)
                throw new InvalidOperationException("Native display or English locale did not settle");
            if (!localeComplete || LocHelper.GetCurrentLocaleCode() != "en" || Screen.width != 1280 || Screen.height != 720 ||
                Time.realtimeSinceStartup < stableAfter || Time.frameCount < stableFrame) return false;
            // Wait for the native 1280x720 adjustment coroutine to finish before
            // requesting a size that is not in the game's resolution selector.
            Screen.SetResolution(1024, 768, false);
            stableAfter = Time.realtimeSinceStartup + .5f; stableFrame = Time.frameCount + 3;
            state = 6; return false;
        }
        if (state == 6)
        {
            if (Time.realtimeSinceStartup >= resolutionDeadline)
                throw new InvalidOperationException("Final 1024x768 display did not settle");
            if (Screen.width != 1024 || Screen.height != 768 || Time.realtimeSinceStartup < stableAfter || Time.frameCount < stableFrame) return false;
            LetterboxManager.Refresh();
            EmporiumEntry.Instance.SetAspectRatio();
            EmporiumEntry.Instance.Validate(true);
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
            stableAfter = Time.realtimeSinceStartup + .5f; stableFrame = Time.frameCount + 3;
            state = 7; return false;
        }
        if (state == 7)
        {
            if (Time.realtimeSinceStartup >= resolutionDeadline)
                throw new InvalidOperationException("Final performance environment did not settle");
            if (Time.realtimeSinceStartup < stableAfter || Time.frameCount < stableFrame) return false;
            Prepare();
            state = 2; return false;
        }
        if (state == 2)
        {
            if (scale == 1 && goods[0].Count < 500)
            {
                for (int i = 0; i < 12 && goods[0].Count < 500; i++) { Add(0); Add(1); }
                return false;
            }
            SetCase();
            CheckEnvironment();
            StartWarmup();
            state = 3; return false;
        }
        if (state == 3)
        {
            if (Time.frameCount < warmUntil || Time.realtimeSinceStartup < warmAfter) return false;
            if (kind == 2 && input == null)
            {
                StartDrag();
                StartWarmup();
                return false;
            }
            BeginSample();
            if (kind == 4) RunAction(OpenRoutingPreview);
            state = 4; return false;
        }
        if (state == 4)
        {
            if (kind == 2 && !ItemMultiSelectHandler.current.IsGroupDragging)
                throw new InvalidOperationException("Native group drag ended during measurement");
            if (kind == 3 && sampleCount > 0 && sampleCount % 10 == 0 && actions < 5)
                RunAction(ChangeModuleAttribute);
            if (sampleCount < Frames) return false;
            EndSample();
            CheckEnvironment();
            CleanupCase();
            if (++round == 3) { round = 0; kind++; }
            if (kind == finalKind)
            {
                harmony.UnpatchAll(harmony.Id); active = null;
                if (allocations.Valid) allocations.Dispose();
                result = "PASS isolated native inventory performance; rows=" + (rows.Count - 2) + "; output=" + output;
                return true;
            }
            state = 2; return false;
        }
        throw new InvalidOperationException("Unknown performance scenario state");
    }

    private void ConfigureEnvironment()
    {
        var mod = ModLoader.Mods.Find(value => value.Manifest?.ID == Mod);
        var config = ModConfig.Find(Mod);
        if (mod == null || config == null || config.Entries.Count == 0)
            throw new InvalidOperationException("ShopAssist configuration is unavailable");
        var settings = ModConfigWindow.Open(mod);
        if (settings?.Get("reset")?.button == null || settings.Get("apply")?.button == null)
            throw new InvalidOperationException("Native MOD configuration Reset/Apply is unavailable");
        settings.Get("reset").button.onClick.Invoke();
        settings.Get("apply").button.onClick.Invoke();
        foreach (var entry in config.Entries)
            if (!Equals(entry.BoxedValue, entry.BoxedDefault))
                throw new InvalidOperationException("Native Reset/Apply did not restore " + entry.Key);
        settings.Close();

        KeyBindHelper.ResetToDefaults();
        KeyBindHelper.Save();
        foreach (var entry in ModShortcuts())
        {
            if (entry.getKey == null || entry.setKey == null)
                throw new InvalidOperationException("Missing MOD shortcut accessor: " + entry.id);
            entry.setKey(KeyCode.None);
        }
        TradeScenario.SetOptions(true, Enabled);
        foreach (var entry in config.Entries)
        {
            object expected = Enabled.Contains(entry.Key) ? (object)true : entry.BoxedDefault;
            if (!Equals(entry.BoxedValue, expected))
                throw new InvalidOperationException("Unexpected MOD configuration after setup: " + entry.Key);
        }

        // Set the native options that can affect input, display, and background
        // work to explicit values instead of inheriting the retained QA profile.
        var options = EmporiumOption.Instance;
        options.masterVolume = options.musicVolume = options.backgroundVolume = .5f;
        options.dialogSpeed = options.dialogVolume = .5f;
        options.enableTypewriterEffect = true; options.autoNextDialog = true; options.autoCallNext = false;
        options.instantDismissCustomer = true; options.instantCallSecurity = false; options.instantCallNextCustomer = true;
        options.swapDragRotateFlip = false; options.disableSortMenu = false; options.sortAutoRotate = false;
        options.singleClickInteract = false; options.singleClickActivate = false; options.autoOpenNegotiationUI = true;
        options.newspaperToTrash = false; options.singleClickChoice = true;
        options.showModSourceInTooltip = true; options.showModSourceInRecorder = true; options.saveHistoryDays = 3;
        options.optionSave = true; options.hasWishlistRecommended = false; options.consentAnalytics = false;
        options.fpsCapWhileMinimized = false; options.enableFpsCap = false;
        options.fullscreenMode = 2; options.borderlessDivisor = 1;
        options.resolution = EmporiumOption.GetResolutionValueFromDimensions(1280, 720);
        if (options.resolution < 0) throw new InvalidOperationException("Native 1280x720 option unavailable");
        options.ApplySetting();
        resolutionDeadline = Time.realtimeSinceStartup + 20f;
        stableAfter = Time.realtimeSinceStartup + .5f; stableFrame = Time.frameCount + 4;
    }

    private IEnumerator SetEnglish()
    {
        yield return LocHelper.SetLocale("en");
        localeComplete = true;
    }

    private static GameOptionRegistry.Entry[] ModShortcuts()
    {
        var entries = GameOptionRegistry.Sections.SelectMany(section => section.entries)
            .Where(entry => entry.source == Mod && entry.kind == GameOptionRegistry.Kind.KeyBind)
            .OrderBy(entry => entry.id, StringComparer.Ordinal).ToArray();
        if (entries.Length == 0 || entries.Select(entry => entry.id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidOperationException("MOD shortcuts are missing or duplicated");
        return entries;
    }

    private void Prepare()
    {
        if (active != null) throw new InvalidOperationException("Performance sampler already active");
        EmporiumEntry.Instance.invElement.SetShape(20, 15);
        for (int i = 0; i < 2; i++)
        {
            bags[i] = New("backpack_large"); Put(EmporiumEntry.Instance.invElement, bags[i]);
            grids[i] = bags[i].contentWindow?.childElement as GameGridInventory;
            if (grids[i] == null) throw new InvalidOperationException("Native backpack grid unavailable");
            grids[i].SetShape(25, 22);
        }
        machine = New("hydroponic"); Put(EmporiumEntry.Instance.invElement, machine);
        for (int i = 0; i < 50; i++) { Add(0); Add(1); }
        active = this;
        harmony = new Harmony("qa.performance.inventory");
        var assembly = ModLoader.Mods.Find(value => value.Manifest?.ID == Mod)?.Instance?.GetType().Assembly;
        var entry = assembly?.GetType("ShopAssist.ModEntry");
        var inventory = assembly?.GetType("ShopAssist.ShopInventory");
        var group = assembly?.GetType("ShopAssist.GroupPlacement");
        Patch(entry?.GetMethod("Update", Flags), nameof(UpdateStart), nameof(UpdateEnd));
        Patch(inventory?.GetMethod("Locations", Flags), nameof(LocationCalled));
        Patch(typeof(CustomUIManager).GetMethods(Flags).Single(method => method.Name == "Window" && method.GetParameters().Length == 3), nameof(WindowCalled));
        Patch(typeof(ItemMultiSelectHandler).GetMethod("OnEventUpdate", Flags), nameof(GroupUpdate), nameof(GroupUpdateEnd));
        Patch(group?.GetMethod("Legal", Flags), nameof(GroupLegal));
        allocations = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1,
            ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
        long managedBefore = GC.GetAllocatedBytesForCurrentThread();
        long profilerBefore = allocations.Valid ? allocations.CurrentValue : 0;
        calibrationBuffer = new byte[65536]; calibrationBuffer[0] = 1;
        long profilerDelta = allocations.Valid ? allocations.CurrentValue - profilerBefore : 0;
        long managedDelta = GC.GetAllocatedBytesForCurrentThread() - managedBefore;
        if (managedDelta >= 65536) { allocationSource = "managed_thread"; allocationProbe = managedDelta; }
        else if (profilerDelta >= 65536) { allocationSource = "unity_current_thread"; allocationProbe = profilerDelta; }
        string label = (scale == 0 ? "small" : "large") + (part == null ? "" : "-" + part);
        output = Path.Combine(Application.persistentDataPath, "qa-performance-inventory-" + label + ".csv");
        environmentOutput = Path.Combine(Application.persistentDataPath, "qa-performance-" + label + "-environment.txt");
        environment = CaptureEnvironment();
        File.WriteAllLines(environmentOutput, environment);
        rows.Add("# Real ModEntry.Update and native group-update samples include QA Harmony and Stopwatch overhead. Actions are timed separately outside Update; routing includes the native options and tools launcher, module filtering times only attribute changes. Window calls count ShopAssist CustomUIManager.Window calls, not draw calls. One/two windows means extra backpack windows in addition to stock. Screen=" + Screen.width + "x" + Screen.height + "; graphics=" + SystemInfo.graphicsDeviceType + "; allocation_source=" + allocationSource + "; allocation_probe_bytes=" + allocationProbe + ". Unsupported allocation values are -1.");
        rows.Add("scale,items,case,round,frames,update_mean_us,update_p95_us,update_alloc_mean_bytes,update_alloc_p95_bytes,locations_all,locations_in_update,window_calls_all,window_calls_in_update,group_update_calls,group_legal_calls,group_update_mean_us,group_update_p95_us,actions,action_mean_us,action_alloc_mean_bytes");
        File.WriteAllLines(output, rows);
    }

    private static string Value(object value)
    {
        if (value == null) return "<null>";
        if (value is float f) return f.ToString("R", CultureInfo.InvariantCulture);
        if (value is double d) return d.ToString("R", CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string Escaped(object value) => value == null ? "null" : "value:" + Uri.EscapeDataString(Value(value));

    private static void CaptureCanvas(List<string> lines, string name, Canvas canvas)
    {
        if (canvas == null) throw new InvalidOperationException(name + " root canvas unavailable");
        void Add(string key, object value) => lines.Add(name + "." + key + "=" + Escaped(value));
        Add("renderMode", canvas.renderMode); Add("scaleFactor", canvas.scaleFactor);
        Add("referencePixelsPerUnit", canvas.referencePixelsPerUnit);
        Rect pixel = canvas.pixelRect;
        Add("pixelRect.x", pixel.x); Add("pixelRect.y", pixel.y);
        Add("pixelRect.width", pixel.width); Add("pixelRect.height", pixel.height);
        var scaler = canvas.GetComponent<CanvasScaler>();
        Add("scaler.present", scaler != null);
        if (scaler == null) return;
        Add("scaler.enabled", scaler.enabled); Add("scaler.uiScaleMode", scaler.uiScaleMode);
        Add("scaler.scaleFactor", scaler.scaleFactor); Add("scaler.referencePixelsPerUnit", scaler.referencePixelsPerUnit);
        Add("scaler.referenceResolution.x", scaler.referenceResolution.x); Add("scaler.referenceResolution.y", scaler.referenceResolution.y);
        Add("scaler.screenMatchMode", scaler.screenMatchMode); Add("scaler.matchWidthOrHeight", scaler.matchWidthOrHeight);
        Add("scaler.physicalUnit", scaler.physicalUnit); Add("scaler.fallbackScreenDPI", scaler.fallbackScreenDPI);
        Add("scaler.defaultSpriteDPI", scaler.defaultSpriteDPI); Add("scaler.dynamicPixelsPerUnit", scaler.dynamicPixelsPerUnit);
    }

    private static string[] CaptureEnvironment()
    {
        if (!string.Equals(LocHelper.GetCurrentLocaleCode(), "en", StringComparison.Ordinal) ||
            ES3.LoadString("locale", "", null, null) != "en" ||
            Screen.width != 1024 || Screen.height != 768 || Screen.fullScreenMode != FullScreenMode.Windowed ||
            QualitySettings.vSyncCount != 0 || Application.targetFrameRate != 60 ||
            EmporiumOption.Instance.fpsCapWhileMinimized || EmporiumOption.Instance.enableFpsCap)
            throw new InvalidOperationException("Performance environment changed: locale=" + LocHelper.GetCurrentLocaleCode() +
                ", saved=" + ES3.LoadString("locale", "", null, null) + ", screen=" + Screen.width + "x" + Screen.height +
                ", mode=" + Screen.fullScreenMode + ", vsync=" + QualitySettings.vSyncCount + ", target=" + Application.targetFrameRate +
                ", minimizedCap=" + EmporiumOption.Instance.fpsCapWhileMinimized + ", cap=" + EmporiumOption.Instance.enableFpsCap);
        if (UISettings.current == null) throw new InvalidOperationException("Native UI settings unavailable");

        var lines = new List<string> { "performance_environment_version=2" };
        void Add(string key, object value) => lines.Add(key + "=" + Escaped(value));
        var config = ModConfig.Find(Mod);
        if (config == null || config.Entries.Count == 0 || config.Entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() != config.Entries.Count)
            throw new InvalidOperationException("MOD configuration is missing or duplicated");
        foreach (var entry in config.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            Add("mod." + entry.Key + ".type", entry.ValueType.FullName);
            Add("mod." + entry.Key + ".serialized", entry.Serialize());
            Add("mod." + entry.Key + ".default", entry.BoxedDefault);
        }
        foreach (var entry in ModShortcuts())
        {
            if (entry.getKey == null || entry.getKey() != KeyCode.None)
                throw new InvalidOperationException("MOD shortcut changed: " + entry.id);
            Add("shortcut.mod." + entry.id, (int)entry.getKey());
        }
        if (KeyBindHelper.Bindings.Count == 0 || KeyBindHelper.Bindings.Select(binding => binding.id).Distinct(StringComparer.Ordinal).Count() != KeyBindHelper.Bindings.Count)
            throw new InvalidOperationException("Native shortcuts are missing or duplicated");
        foreach (var binding in KeyBindHelper.Bindings.OrderBy(binding => binding.id, StringComparer.Ordinal))
        {
            var key = KeyBindHelper.Get(binding.id);
            if (key != KeyBindHelper.GetDefault(binding.id))
                throw new InvalidOperationException("Native shortcut changed: " + binding.id);
            Add("shortcut.native." + binding.id, (int)key);
        }
        foreach (var field in typeof(EmporiumOption).GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(field => field.Name, StringComparer.Ordinal))
            if (field.FieldType == typeof(bool) || field.FieldType == typeof(int) || field.FieldType == typeof(float))
                Add("nativeOption." + field.Name, field.GetValue(EmporiumOption.Instance));
        Add("locale", LocHelper.GetCurrentLocaleCode()); Add("locale.saved", ES3.LoadString("locale", "", null, null));
        Add("screen.width", Screen.width); Add("screen.height", Screen.height); Add("screen.fullScreenMode", Screen.fullScreenMode);
        Add("display.systemWidth", Display.main.systemWidth); Add("display.systemHeight", Display.main.systemHeight);
        Add("graphics", SystemInfo.graphicsDeviceType); Add("vSyncCount", QualitySettings.vSyncCount);
        Add("targetFrameRate", Application.targetFrameRate); Add("isFocused", Application.isFocused);
        Add("runInBackground", Application.runInBackground); Add("timeScale", Time.timeScale);
        Add("input.multiSelectKey", (int)ItemMultiSelectHandler.current.key);
        foreach (var field in typeof(UISettings).GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(field => field.Name, StringComparer.Ordinal))
            if (field.FieldType == typeof(bool) || field.FieldType == typeof(int) || field.FieldType == typeof(float))
                Add("ui." + field.Name, field.GetValue(UISettings.current));
        var newGame = NewGameData.Instance;
        Add("newGame.scenario", newGame.startScenarioId); Add("newGame.startType", newGame.startType);
        Add("newGame.difficulty", newGame.difficultyId); Add("newGame.hardMode", newGame.hardMode);
        Add("newGame.endlessMode", newGame.endlessMode); Add("newGame.skipIntro", newGame.skipIntro);
        Add("newGame.disableTutorialHint", newGame.disableTutorialHint);
        Add("newGame.perks", string.Join(",", newGame.startingPerks.Select(perk => perk.id).OrderBy(id => id, StringComparer.Ordinal)));
        Canvas.ForceUpdateCanvases();
        var nativeCanvas = RenderHandler.current?.windowUICanvas;
        CaptureCanvas(lines, "canvas.native", nativeCanvas);
        CaptureCanvas(lines, "canvas.nativeRoot", nativeCanvas?.rootCanvas);
        var layer = CustomUIManager.Instance?.GetLayerRoot(CustomUIManager.LAYER_META);
        var customCanvas = layer?.GetComponentInParent<Canvas>();
        CaptureCanvas(lines, "canvas.custom", customCanvas);
        CaptureCanvas(lines, "canvas.customRoot", customCanvas?.rootCanvas);
        return lines.OrderBy(line => line, StringComparer.Ordinal).ToArray();
    }

    private void CheckEnvironment()
    {
        var actual = CaptureEnvironment();
        if (!environment.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidOperationException("Performance environment changed during the scenario; compare with " + environmentOutput);
    }

    private void StartWarmup()
    {
        warmUntil = Time.frameCount + WarmupFrames;
        warmAfter = Time.realtimeSinceStartup + .5f;
    }

    private void Patch(MethodInfo original, string prefix, string postfix = null)
    {
        if (original == null) throw new InvalidOperationException("Missing performance hook " + prefix);
        harmony.Patch(original, prefix: new HarmonyMethod(typeof(PerformanceInventoryScenario).GetMethod(prefix, Flags)),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(PerformanceInventoryScenario).GetMethod(postfix, Flags)));
    }

    private static GameItem New(string id)
    {
        var item = DirectoryMaster.Item(id);
        if (item == null) throw new InvalidOperationException("Missing fixture " + id);
        GeneralHelper.SetItemOwned(item, true); return item;
    }
    private static void Put(GameInventory grid, GameItem item)
    {
        SlotMarker slot = grid.TryFindOneValidInventorySlot(item);
        if (slot == null || slot.targetItem != null || slot.TryAcceptOnce(item.unitCount) != item.unitCount || item.parentInventory != grid)
            throw new InvalidOperationException("Could not place " + item.identifier);
    }
    private void Add(int index)
    {
        var item = New("system_module_performance") as GameItemElement;
        if (item == null) throw new InvalidOperationException("Fixture is not an item element");
        item.SetShape(new GridShapeBuilder(1, 1).SetDataFill(1).Build());
        int n = goods[index].Count;
        var shape = new GridShapeBuilder(item.modifiedShape).SetPosition(n % 25, 1 + n / 25).Build();
        var slot = grids[index].TryInventorySlot(item, item.unitCount, shape);
        if (slot == null || slot.targetItem != null || slot.TryAcceptOnce(item.unitCount) != item.unitCount || item.parentInventory != grids[index])
            throw new InvalidOperationException("Could not place fixture " + index + ":" + n);
        goods[index].Add(item);
    }

    private void SetCase()
    {
        CleanupCase();
        ItemMouseDoubleClickHandler.current.OpenContentAction(bags[0], new Vector2(25, 80));
        if (kind != 0) ItemMouseDoubleClickHandler.current.OpenContentAction(bags[1], new Vector2(500, 80));
        if (!bags[0].contentWindow.IsVisible() || (kind != 0 && !bags[1].contentWindow.IsVisible()))
            throw new InvalidOperationException("Native inventory windows did not open");
        if (kind == 3)
        {
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(260, 90));
            RoutingScenario.RunFeatureAction("f10.open");
            if (ModuleAttribute() == null) throw new InvalidOperationException("Module attribute control unavailable");
        }
    }

    private void StartDrag()
    {
        Canvas.ForceUpdateCanvases();
        Vector3 point = Vector3.zero; bool found = false;
        RectTransform rect = grids[0].handler.rectTransform;
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        float cell = UISettings.current.gridSize;
        for (int y = 0; y < grids[0].inventoryShape.height && !found; y++)
            for (int x = 0; x < grids[0].inventoryShape.width && !found; x++)
            {
                Vector3 candidate = RectTransformUtility.WorldToScreenPoint(camera,
                    rect.TransformPoint(new Vector3((x + .5f) * cell, -(y + .5f) * cell)));
                if (candidate.x < 0 || candidate.y < 0 || candidate.x >= Screen.width || candidate.y >= Screen.height) continue;
                if (RenderHandler.RaycastFindParentElement<GameGridInventory>(candidate) == grids[0] &&
                    RenderHandler.RaycastElement<GameItem>(candidate) == null) { point = candidate; found = true; }
            }
        if (!found) throw new InvalidOperationException("No visible empty native source-grid cell for group drag");
        RoutingScenario.SetSelection(goods[0].Take(4).ToArray());
        var handler = ItemMultiSelectHandler.current;
        var stateField = typeof(ItemMultiSelectHandler).GetField("state", Flags);
        stateField.SetValue(handler, Enum.Parse(stateField.FieldType, "PendingGroupDrag"));
        input = new QaSyntheticInput(true) { Held = handler.key, Mouse = point };
        typeof(ItemMultiSelectHandler).GetMethod("StartGroupDrag", Flags).Invoke(handler, null);
        if (!handler.IsGroupDragging) throw new InvalidOperationException("Native group drag did not start");
    }

    private static TMPro.TMP_Dropdown ModuleAttribute() => CustomUIManager.Instance.GetWindow(Mod + ".f10")?.Get("attribute")?.dropdown;
    private void ChangeModuleAttribute()
    {
        var attribute = ModuleAttribute();
        if (attribute == null) throw new InvalidOperationException("Module filter closed during measurement");
        attribute.value = actions % 2 == 0 ? 1 : 0;
    }
    private void OpenRoutingPreview()
    {
        RoutingScenario.SetSelection(goods[0].Take(20).ToArray());
        RoutingScenario.RunFeatureAction("f08.open");
        if (CustomUIManager.Instance.GetWindow(Mod + ".route_preview") == null)
            throw new InvalidOperationException("Routing preview did not open");
    }
    private void CleanupCase()
    {
        if (ItemMultiSelectHandler.current?.IsGroupDragging == true) ItemMultiSelectHandler.current.Cancel();
        input?.Dispose(); input = null;
        CustomUIManager.Instance?.GetWindow(Mod + ".f10")?.Close();
        CustomUIManager.Instance?.GetWindow(Mod + ".route_preview")?.Close();
        machine?.contentWindow?.Hide();
        foreach (GameItem bag in bags) bag?.contentWindow?.Hide();
    }
    private void BeginSample()
    {
        sampleCount = actions = locations = updateLocations = windows = updateWindows = groupUpdates = groupChecks = 0;
        groupSamples = 0; inGroupUpdate = false;
        actionTicks = actionBytes = 0; measuring = true;
    }
    private void RunAction(Action action)
    {
        long allocated = Allocated(), started = Stopwatch.GetTimestamp();
        action();
        actionTicks += Stopwatch.GetTimestamp() - started;
        if (allocationSource != "unsupported") actionBytes += Allocated() - allocated;
        actions++;
    }
    private long Allocated() => allocationSource == "managed_thread" ? GC.GetAllocatedBytesForCurrentThread() :
        allocationSource == "unity_current_thread" ? allocations.CurrentValue : -1;
    private void EndSample()
    {
        measuring = false;
        if (sampleCount != Frames) throw new InvalidOperationException("Incomplete real-frame sample");
        var sortedTicks = (long[])ticks.Clone(); Array.Sort(sortedTicks);
        var sortedBytes = (long[])bytes.Clone(); Array.Sort(sortedBytes);
        var sortedGroup = groupTicks.Take(groupSamples).OrderBy(value => value).ToArray();
        double micros = 1000000d / Stopwatch.Frequency;
        string[] names = { "idle_one_window", "idle_two_windows", "native_group_drag", "module_attribute_filter", "routing_preview" };
        string F(double n) => n.ToString("F3", CultureInfo.InvariantCulture);
        rows.Add(string.Join(",", scale == 0 ? "small" : "large", scale == 0 ? "100" : "1000", names[kind], (round + 1).ToString(),
            sampleCount.ToString(), F(ticks.Average() * micros), F(sortedTicks[(int)Math.Ceiling(Frames * .95) - 1] * micros),
            F(allocationSource == "unsupported" ? -1 : bytes.Average()), (allocationSource == "unsupported" ? -1 : sortedBytes[(int)Math.Ceiling(Frames * .95) - 1]).ToString(), locations.ToString(), updateLocations.ToString(),
            windows.ToString(), updateWindows.ToString(), groupUpdates.ToString(), groupChecks.ToString(),
            F(groupSamples == 0 ? 0 : sortedGroup.Average() * micros),
            F(groupSamples == 0 ? 0 : sortedGroup[(int)Math.Ceiling(groupSamples * .95) - 1] * micros), actions.ToString(),
            F(actions == 0 ? 0 : actionTicks * micros / actions), F(allocationSource == "unsupported" ? -1 : actions == 0 ? 0 : (double)actionBytes / actions)));
        File.AppendAllText(output, rows[rows.Count - 1] + "\n");
    }

    private static void UpdateStart()
    {
        var a = active; if (a == null || !a.measuring || a.sampleCount >= Frames) return;
        a.inUpdate = true; a.beginBytes = a.Allocated(); a.beginTicks = Stopwatch.GetTimestamp();
    }
    private static void UpdateEnd()
    {
        var a = active; if (a == null || !a.inUpdate) return;
        long end = Stopwatch.GetTimestamp(), allocated = a.Allocated();
        a.ticks[a.sampleCount] = end - a.beginTicks; a.bytes[a.sampleCount] = a.allocationSource == "unsupported" ? -1 : allocated - a.beginBytes;
        a.sampleCount++; a.inUpdate = false;
    }
    private static void LocationCalled() { var a = active; if (a?.measuring != true || a.sampleCount >= Frames) return; a.locations++; if (a.inUpdate) a.updateLocations++; }
    private static void WindowCalled(string __0) { var a = active; if (a?.measuring != true || a.sampleCount >= Frames || __0 == null || !__0.StartsWith(Mod + ".", StringComparison.Ordinal)) return; a.windows++; if (a.inUpdate) a.updateWindows++; }
    private static void GroupUpdate()
    {
        var a = active;
        if (a?.measuring != true || a.sampleCount >= Frames || ItemMultiSelectHandler.current?.IsGroupDragging != true) return;
        a.groupUpdates++; a.inGroupUpdate = true; a.groupStart = Stopwatch.GetTimestamp();
    }
    private static void GroupUpdateEnd()
    {
        var a = active; if (a == null || !a.inGroupUpdate) return;
        long elapsed = Stopwatch.GetTimestamp() - a.groupStart;
        if (a.groupSamples < a.groupTicks.Length) a.groupTicks[a.groupSamples++] = elapsed;
        a.inGroupUpdate = false;
    }
    private static void GroupLegal() { var a = active; if (a?.measuring == true && a.sampleCount < Frames) a.groupChecks++; }
}

public sealed class PerformanceSmallScenario : PerformanceInventoryScenario { public PerformanceSmallScenario() : base(false) { } }
public sealed class PerformanceSmallCoreScenario : PerformanceInventoryScenario { public PerformanceSmallCoreScenario() : base(false, 0, 3, "core") { } }
public sealed class PerformanceSmallFilterScenario : PerformanceInventoryScenario { public PerformanceSmallFilterScenario() : base(false, 3, 4, "filter") { } }
// The filter case may exceed the Player's 180-second deadline when combined
// with the other cases. Every part creates the same fixture and environment.
public sealed class PerformanceLargeScenario : PerformanceInventoryScenario { public PerformanceLargeScenario() : base(true, 0, 3, "core") { } }
public sealed class PerformanceLargeFilterScenario : PerformanceInventoryScenario { public PerformanceLargeFilterScenario() : base(true, 3, 4, "filter") { } }
public sealed class PerformanceLargeRoutingScenario : PerformanceInventoryScenario { public PerformanceLargeRoutingScenario() : base(true, 4, 5, "routing") { } }
