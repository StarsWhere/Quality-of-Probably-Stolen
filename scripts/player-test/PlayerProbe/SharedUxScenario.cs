using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

public sealed class SharedUxScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private readonly float deadline = Time.realtimeSinceStartup + 100;
    private GameItem first, second;
    private CustomUIWindow modules;
    private float wait;
    private int step;

    public bool Tick(out string result)
    {
        result = null;
        Need(Time.realtimeSinceStartup < deadline, "Shared UX scenario timed out at " + step);
        if (step == 0) { if (start.Tick()) step++; return false; }
        if (step == 1)
        {
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f10.enabled", "f16.enabled", "f16.money", "f16.rep");
            TradeScenario.SetOptions(false, "f16.sound", "f15.enabled", "f17.enabled", "f21.enabled");
            first = Put("hydroponic", "QA bound machine A"); second = Put("hydroponic", "QA other machine B");
            ItemMouseDoubleClickHandler.current.OpenContentAction(first, new Vector2(160, 340));
            ItemMouseDoubleClickHandler.current.OpenContentAction(second, new Vector2(Screen.width - 160, 340));
            first.contentWindow.ToFront(); step++; return false;
        }
        if (step == 2)
        {
            var button = QaItemUi.WindowAction(first.contentWindow, "f10.open");
            if (button == null || button.isDisabled) return false;
            button.Select();
            modules = CustomUIManager.Instance.GetWindow(Mod + ".f10");
            Need(modules?.IsAlive == true, "Direct module action did not open its browser");
            second.contentWindow.ToFront(); wait = Time.unscaledTime + .3f; step++; return false;
        }
        if (step == 3)
        {
            if (Time.unscaledTime < wait) return false;
            Need(CustomUIManager.Instance.GetWindow(Mod + ".f10") == modules && modules.IsAlive &&
                modules.root.GetComponentsInChildren<TMP_Text>().Any(t => t.text.Contains("QA bound machine A")),
                "Looking at another machine changed the bound module target");
            Need(modules.useMachineryStyle, "Module browser did not use the native machinery frame");
            modules.Close();
            var player = PlayerStore.Instance;
            player.playerCash = 200000;
            var client = StoreClientList.CreateJunkerSellOnly(); client.clientFaction = "FACTION_UPPER_LEVEL";
            player.currentClientInstance = StoreClientInstance.CreateClientInstance(client); player.isClientArrived = true;
            foreach (int n in new[] { 1, 2 })
            {
                var item = DirectoryMaster.Item("wine_yeast"); GeneralHelper.SetItemOwned(item, false);
                var slot = EmporiumEntry.Instance.backInvinvElement.TryFindOneValidInventorySlot(item);
                Need(slot != null && slot.TryAcceptOnce() > 0, "Could not put receipt fixture on the counter");
                player.BuyItem(item);
            }
            wait = Time.unscaledTime + .3f; step++; return false;
        }
        if (step == 4)
        {
            if (Time.unscaledTime < wait) return false;
            var receipts = UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>()
                .Where(t => t.gameObject.activeInHierarchy && t.name.StartsWith(Mod + ".receipt.", StringComparison.Ordinal)).ToArray();
            Need(receipts.Length == 2 && receipts[0].text == receipts[1].text,
                "Two same-value native transactions did not produce independent receipts");
            Need(receipts.All(t => t.enabled && t.color.a > .5f && !t.raycastTarget && t.fontSize >= 14),
                "A receipt is invisible, unreadable or intercepts input");
            Need(receipts.All(t => t.GetComponent<TreeNodeRender>().image?.enabled != true), "A receipt drew an opaque backing block");
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-ux-receipts.png"));
            result = "PASS direct bound machine action and distinct readable nonblocking transaction receipts";
            return true;
        }
        return false;
    }

    private static GameItem Put(string id, string name)
    {
        var item = DirectoryMaster.Item(id); GeneralHelper.SetItemOwned(item); GeneralHelper.SetCustomName(item, name);
        var slot = EmporiumEntry.Instance.invElement.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.TryAcceptOnce() > 0, "Could not place " + name); return item;
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
