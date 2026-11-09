using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShopAssist
{
    public sealed class ModEntry : IMod
    {
        public const string Id = "starswhere.shopassist";
        private readonly List<Module> modules = new List<Module>();
        private ShopSettings settings;
        private ModLog log;
        private GameObject host;

        private sealed class Module
        {
            internal string Name;
            internal Action Start, Tick, Stop;
            internal bool Running;
        }

        private void Add(string name, Action start, Action tick, Action stop) =>
            modules.Add(new Module { Name = name, Start = start, Tick = tick, Stop = stop });

        public void Init(ModManifest manifest)
        {
            log = new ModLog(manifest);
            settings = new ShopSettings();
            Action<string> warn = message => log.Warning(message);
            Add("Feedback", () => AttentionBook.Start(settings, warn), AttentionBook.Tick, AttentionBook.Dispose);
            var playback = new FastPlayback(settings, warn);
            Add("F42", playback.Start, playback.Tick, playback.Dispose);
            var enabled = settings.Toggle("f15.enabled", "f15.tip");
            var deadlines = settings.Toggle("f21.enabled", "f21.tip");
            var deadlineDays = settings.Integer("f21.days", "f21.days.tip", 1, 0, 3);
            MorningCalendar calendar = null;
            Add("F15", () =>
            {
                calendar = new MorningCalendar(() => enabled.Value, () => deadlines.Value, () => deadlineDays.Value, message => log.Log(message));
                settings.Changed += calendar.SettingsChanged;
                calendar.Start();
            }, () => calendar.Tick(), () =>
            {
                if (calendar == null) return;
                settings.Changed -= calendar.SettingsChanged;
                calendar.Dispose();
                calendar = null;
            });
            var labels = new AreaLabels(settings, warn);
            Add("F02", labels.Start, labels.Tick, labels.Dispose);
            var tasks = new TodoBook(settings, warn);
            Add("F17", tasks.Start, tasks.Tick, tasks.Dispose);
            var desktop = new DesktopTools(settings, warn);
            Add("Desktop", desktop.Start, desktop.Tick, desktop.Dispose);
            var purchases = new PurchaseCounter(settings, warn);
            Add("F01", purchases.Start, null, purchases.Dispose);
            var ledger = new ShopLedger(settings, warn);
            Add("F16/F28", ledger.Start, ledger.Tick, ledger.Dispose);
            var yeast = new YeastReturn(settings, warn);
            Add("F37", yeast.Start, null, yeast.Dispose);
            var machines = new MachineTools(settings, warn);
            Add("F10/F19/F30", machines.Start, machines.Tick, machines.Dispose);
            var clues = new CustomerIndicators(settings, warn);
            Add("F03/F04", clues.Start, clues.Tick, clues.Dispose);
            var itemLabels = new ItemLabels(settings, warn);
            Add("F38", itemLabels.Start, itemLabels.Tick, itemLabels.Dispose);
            var knownMarkers = new KnownMarkers(settings, clues, warn);
            Add("F39", knownMarkers.Start, knownMarkers.Tick, knownMarkers.Dispose);
            var listings = new CustomerInventory(settings, clues, warn);
            Add("F11", listings.Start, listings.Tick, listings.Dispose);
            var shortcuts = new ContextShortcuts(settings, warn);
            Add("Context buttons", shortcuts.Start, shortcuts.Tick, shortcuts.Dispose);
            var cargo = new TripCargo(settings, warn);
            Add("F25", cargo.Start, cargo.Tick, cargo.Dispose);
            var peek = new ContainerPeek(settings, warn);
            Add("F20", peek.Start, peek.Tick, peek.Dispose);
            var basket = new CustomerBasket(settings, clues, listings.PurchaseLabel);
            Add("F34", basket.Start, basket.Tick, basket.Dispose);
            var packing = new FieldPacking(settings);
            Add("F36", packing.Start, packing.Tick, packing.Dispose);
            var sheet = new AppraisalWorksheet(settings, warn);
            Add("F23", sheet.Start, sheet.Tick, sheet.Dispose);
            var organizer = new InventoryOrganizer(settings, warn);
            Add("F05", organizer.Start, organizer.Tick, organizer.Dispose);
            var routes = new InventoryRoutes(settings, clues, warn);
            Add("F07/F08/F09", routes.Start, routes.Tick, routes.Dispose);
            var groupRotate = settings.Toggle("f40.enabled", "f40.tip", true);
            var groupPlace = settings.Toggle("f41.enabled", "f41.tip", true);
            Add("F40/F41", () => GroupPlacement.Register(() => groupPlace.Value, () => groupRotate.Value, warn),
                null, GroupPlacement.Unregister);
            var slips = new AppraisalReceipt(settings, warn);
            Add("F24", slips.Start, null, slips.Dispose);
        }

        public void OnEnable()
        {
            if (host != null) return;
            host = new GameObject(Id);
            Object.DontDestroyOnLoad(host);
            try
            {
                settings.Attach();
                foreach (var module in modules)
                {
                    module.Running = true;
                    try { module.Start(); }
                    catch (Exception error) { Failed(module, error); }
                }
                host.AddComponent<ShopLoop>().Tick = Update;
                log.Log("Enabled.");
            }
            catch { OnDisable(); throw; }
        }

        private void Update()
        {
            foreach (var module in modules)
            {
                if (!module.Running || module.Tick == null) continue;
                try { module.Tick(); }
                catch (Exception error) { Failed(module, error); }
            }
            ShopNotice.Tick();
            ItemLocator.Tick();
            TradeFeedback.Tick();
            VisualChoices.Tick();
        }

        private void Failed(Module module, Exception error)
        {
            log.Error(module.Name + " stopped: " + error);
            Stop(module);
        }

        private void Stop(Module module)
        {
            if (!module.Running) return;
            module.Running = false;
            try { module.Stop(); }
            catch (Exception error) { log.Error(module.Name + " cleanup: " + error); }
        }

        public void OnDisable()
        {
            if (host == null) return;
            host.SetActive(false);
            for (int i = modules.Count - 1; i >= 0; i--) Stop(modules[i]);
            try { settings.Detach(); }
            finally
            {
                VisualChoices.Close();
                ShopNotice.Clear();
                ItemLocator.Clear();
                TradeFeedback.Clear();
                Object.Destroy(host);
                host = null;
            }
        }
    }
}
