using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Uses the game's own load UI, including its virtualized rows and confirmation.
internal sealed class QaLoadSlot
{
    private readonly int slot;
    private readonly Guid run;
    private int step;
    private int index;
    private int count;
    private bool scrolled;
    private bool confirmed;

    internal QaLoadSlot(int slot, Guid run) { this.slot = slot; this.run = run; }

    internal bool Tick()
    {
        switch (step)
        {
            case 0:
            {
                if (SceneManager.GetActiveScene().name != "EmporiumMenu" || MainMenuUIController.Instance == null || CustomUIManager.Instance == null) return false;
                var slots = SaveUIManager.GetOrderedSlotIds();
                if (slots == null || !slots.Contains(slot)) throw new InvalidOperationException("QA slot missing from native save list: " + slot);
                index = slots.IndexOf(slot); count = slots.Count;
                MainMenuUIController.Instance.OnLoadGameClick();
                step = 1;
                return false;
            }
            case 1:
            {
                if (!LoadGameUI.IsOpen) return false;
                var window = CustomUIManager.Instance.GetWindow("load_game");
                var frame = window?.root?.GetComponent<LoadGameFrame>();
                if (frame == null || frame.content == null) return false;
                var bindings = typeof(LoadGameUI).GetField("bound", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary;
                if (bindings == null) throw new InvalidOperationException("Native load screen bindings are unavailable");
                Button latest = null;
                foreach (DictionaryEntry row in bindings)
                {
                    object plate = row.Value; Type type = plate?.GetType();
                    FieldInfo slotField = type?.GetField("slotId", BindingFlags.Instance | BindingFlags.Public);
                    FieldInfo rootField = type?.GetField("root", BindingFlags.Instance | BindingFlags.Public);
                    if (slotField == null || rootField == null || (int)slotField.GetValue(plate) != slot) continue;
                    var root = rootField.GetValue(plate) as RectTransform;
                    var button = root != null ? root.Find("Body/LoadLatest") : null;
                    if (button != null && button.gameObject.activeInHierarchy) latest = button.GetComponent<Button>();
                }
                if (latest == null)
                {
                    if (!scrolled && frame.scroll != null && count > 1) { frame.scroll.verticalNormalizedPosition = 1f - (float)index / (count - 1); scrolled = true; }
                    return false;
                }
                if (!latest.interactable) throw new InvalidOperationException("Native QA save button is disabled");
                latest.onClick.Invoke(); step = 2; return false;
            }
            case 2:
            {
                if (SceneManager.GetActiveScene().name != "InventoryScene")
                {
                    if (LoadGameUI.IsOpen && !confirmed)
                    {
                        var frame = CustomUIManager.Instance.GetWindow("load_game")?.root?.GetComponent<LoadGameFrame>();
                        if (frame != null && frame.dialogRoot != null && frame.dialogRoot.activeInHierarchy)
                        {
                            if (frame.dialogConfirm?.button == null) throw new InvalidOperationException("Native load warning cannot be confirmed");
                            frame.dialogConfirm.button.onClick.Invoke(); confirmed = true;
                        }
                    }
                    return false;
                }
                if (!PlayerStore.HasInstance || StoreUIManager.Instance == null || EmporiumEntry.Instance == null) return false;
                var player = PlayerStore.Instance;
                return player.saveSlotId == slot && Guid.TryParse(player.runID, out Guid loaded) && loaded == run &&
                    player.playerLocation == PlayerStore.PlayerLocation.IN_STORE;
            }
            default: throw new InvalidOperationException("Unknown QA load step");
        }
    }
}

// Selects a fresh gunsmith run through the same controls used by the player.
internal sealed class QaNewRun
{
    private int step;
    private float sceneReadyAt;
    internal bool Tick()
    {
        switch (step)
        {
            case 0:
                if (SceneManager.GetActiveScene().name != "EmporiumMenu" || MainMenuUIController.Instance == null || CustomUIManager.Instance == null) return false;
                if (NewGameData.Instance.saveSlot >= 0 || !string.IsNullOrEmpty(NewGameData.Instance.saveFile)) throw new InvalidOperationException("New game still references a save");
                MainMenuUIController.Instance.OnNewGameClick(); step = 1; return false;
            case 1:
            {
                if (!NewGameSpecializationUI.IsOpen || EventSystem.current == null) return false;
                var frame = CustomUIManager.Instance.GetWindow("new_game_specialization")?.root?.GetComponent<NewGameSpecializationFrame>();
                var slot = frame?.stripSlots?.Find("slot_gunsmith");
                var click = slot != null ? slot.GetComponent<WildNetworkSlot>() : null;
                if (click == null || frame.continueButton == null) return false;
                click.OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left });
                if (NewGameSpecializationUI.Selected == null || NewGameSpecializationUI.Selected.id != "gunsmith" || !frame.continueButton.interactable)
                    throw new InvalidOperationException("Could not select native gunsmith scenario");
                frame.continueButton.onClick.Invoke(); step = 2; return false;
            }
            case 2:
            {
                var menu = MainMenuUIController.Instance;
                if (menu == null) return false;
                var confirmation = menu.confirmPanelUIManager;
                if (confirmation != null && confirmation.panel != null && confirmation.panel.activeInHierarchy) { confirmation.OnYesClick(); return false; }
                if (!NewGamePerksUI.IsOpen) return false;
                var frame = CustomUIManager.Instance.GetWindow("new_game_perks")?.root?.GetComponent<NewGamePerksFrame>();
                if (frame?.continueButton == null) return false;
                if (!frame.continueButton.interactable) throw new InvalidOperationException("Native perks continue unavailable");
                frame.continueButton.onClick.Invoke(); step = 3; return false;
            }
            case 3:
            {
                if (!NewGameDifficultyUI.IsOpen) return false;
                var frame = CustomUIManager.Instance.GetWindow("new_game_difficulty")?.root?.GetComponent<NewGameDifficultyFrame>();
                if (frame?.continueButton == null) return false;
                if (!frame.continueButton.interactable || !NewGameData.Instance.skipIntro) throw new InvalidOperationException("Native new game not ready");
                frame.continueButton.onClick.Invoke(); step = 4; return false;
            }
            case 4:
                if (SceneManager.GetActiveScene().name != "InventoryScene" || !PlayerStore.HasInstance ||
                    StoreUIManager.Instance == null || EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true ||
                    !Guid.TryParse(PlayerStore.Instance.runID, out Guid run) || run == Guid.Empty || PlayerStore.Instance.saveSlotId < 0) return false;
                if (sceneReadyAt == 0) sceneReadyAt = Time.realtimeSinceStartup + 3f;
                if (Time.realtimeSinceStartup < sceneReadyAt || StoreUIManager.Instance.blackscreen?.gameObject.activeInHierarchy == true) return false;
                if (StoreUIManager.Instance.controlHintOverlay?.activeSelf == true) StoreUIManager.Instance.ToggleControlHintOverlay();
                var stock = EmporiumEntry.Instance.invWindow;
                if (stock?.rectTransform?.parent == null || !stock.IsVisible() || stock.canMove || !StoreUIManager.Instance.IsInnateWindow(stock)) return false;
                return true;
            default: throw new InvalidOperationException("Unknown QA new-game step");
        }
    }
}
