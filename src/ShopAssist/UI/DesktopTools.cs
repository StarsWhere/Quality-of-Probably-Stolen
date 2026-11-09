using System;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    internal sealed partial class DesktopTools : IDisposable
    {
        private readonly Action<string> log;
        private bool started;
        private bool bargainFaulted;
        private bool inspectionFaulted;

        internal DesktopTools(ShopSettings settings, Action<string> log)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            InitBargain(settings);
            InitInspection(settings);
        }

        public void Start()
        {
            if (started) return;
            started = true;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        public void Tick()
        {
            if (!started) return;
            if (!bargainEnabled.Value) bargainFaulted = false;
            if (!bargainFaulted)
            {
                try { TickBargain(); }
                catch (Exception error)
                {
                    log("F14 stopped after an error: " + error);
                    try { ResetBargain(); } catch (Exception cleanup) { log("F14 cleanup failed: " + cleanup); }
                    bargainFaulted = true;
                }
            }
            if (!inspectionEnabled.Value) inspectionFaulted = false;
            if (!inspectionFaulted)
            {
                try { TickInspection(); }
                catch (Exception error)
                {
                    log("F18 stopped after an error: " + error);
                    try { ReleaseInspection(false, true); } catch (Exception cleanup) { log("F18 cleanup failed: " + cleanup); }
                    inspectionFaulted = true;
                }
            }
        }

        private void Reset()
        {
            ResetBargain();
            bargainFaulted = false;
            ReleaseInspection(false, true);
            inspectionFaulted = false;
            unsupportedInspection = null;
            unsupportedInspectionItem = null;
        }

        private void SceneChanged(Scene previous, Scene current) => Reset();

        public void Dispose()
        {
            if (!started) return;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            Reset();
            started = false;
        }
    }
}
