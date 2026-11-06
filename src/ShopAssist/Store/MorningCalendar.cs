using System;
using UnityEngine.SceneManagement;

namespace ShopAssist
{
    // The module runs even with F15 off: F17 and F21 own independent parts of the board.
    internal sealed class MorningCalendar : IDisposable
    {
        private readonly DoorBoard board;
        private bool started;

        internal MorningCalendar(Func<bool> isEnabled, Action<string> log)
        {
            board = new DoorBoard(isEnabled, log);
        }

        internal void Start()
        {
            if (started) return;
            started = true;
            TodoBook.DoorChanged += board.Invalidate;
            ModHook.OnGameLoadedInit += Reset;
            ModHook.OnLeavingStoreEarly += Reset;
            SceneManager.activeSceneChanged += SceneChanged;
        }

        internal void SettingsChanged() => board.Invalidate();
        internal void Tick() { if (started) board.Tick(); }
        private void Reset() => board.Reset();
        private void SceneChanged(Scene previous, Scene current) => Reset();

        public void Dispose()
        {
            if (!started) return;
            started = false;
            TodoBook.DoorChanged -= board.Invalidate;
            ModHook.OnGameLoadedInit -= Reset;
            ModHook.OnLeavingStoreEarly -= Reset;
            SceneManager.activeSceneChanged -= SceneChanged;
            board.Reset();
        }
    }
}
