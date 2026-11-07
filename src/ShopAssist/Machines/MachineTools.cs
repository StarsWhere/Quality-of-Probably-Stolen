using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    internal sealed partial class MachineTools : IDisposable
    {
        private static MachineTools current;
        private readonly ShopSettings settings;
        private readonly Action<string> log;
        private readonly LiquidBars liquid;
        private bool started;

        internal MachineTools(ShopSettings settings, Action<string> log)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            liquid = new LiquidBars(settings, log);
            InitModuleBrowser();
            InitModuleLayout();
            InitReplant();
            InitMaintenance();
            InitFeed();
            InitMachineOverview();
        }

        internal void Start()
        {
            if (started) return;
            if (current != null && !ReferenceEquals(current, this)) throw new InvalidOperationException("Machine tools are already active.");
            current = this;
            started = true;
            AttentionBook.Register("machines", MachineAttentionItems);
            liquid.Start();
            StartMaintenance();
            settings.Changed += SettingsChanged;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            ModHook.OnWakingUpLate += Invalidate;
            SceneManager.activeSceneChanged += SceneChanged;
            StartFeed();
        }

        internal void Tick()
        {
            if (!started) return;
            liquid.Tick();
            TickModuleBrowser();
            TickModuleLayout();
            TickReplant();
            TickMaintenance();
            TickFeed();
            TickMachineOverview();
        }

        private void SettingsChanged()
        {
            liquid.SettingsChanged();
            ModuleSettingsChanged();
            LayoutSettingsChanged();
            ReplantSettingsChanged();
            MaintenanceSettingsChanged();
            FeedSettingsChanged();
            MachineSettingsChanged();
        }

        private void Reset()
        {
            liquid.Reset();
            CloseLayout();
            ResetReplant();
            ResetMaintenance();
            SuspendFeedTransactions();
            CloseFeed();
            CloseModules();
            CloseMachines();
        }

        private void SceneChanged(Scene before, Scene after) => Reset();

        public void Dispose()
        {
            if (!started) return;
            AttentionBook.Unregister("machines");
            StopFeed();
            if (started)
            {
                settings.Changed -= SettingsChanged;
                ModHook.OnGameLoadedInit -= Reset;
                ModHook.OnLeavingStoreEarly -= Reset;
                ModHook.OnWakingUpLate -= Invalidate;
                SceneManager.activeSceneChanged -= SceneChanged;
            }
            Reset();
            StopMaintenance();
            liquid.Dispose();
            if (ReferenceEquals(current, this)) current = null;
            started = false;
        }

        private static void Invalidate()
        {
            if (current == null || !current.started) return;
            current.modulesDirty = true;
            current.machinesDirty = true;
            current.overviewStructureDirty = true;
            current.overviewIssuesDirty = true;
            current.maintenanceDirty = true;
            AttentionBook.Changed();
        }

        internal static void InventoryChanged(GameInventory inventory, GameItem item) => OverviewInventoryChanged(inventory, item);
    }
}
