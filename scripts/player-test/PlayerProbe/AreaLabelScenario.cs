using System;
using System.IO;
using TMPro;
using UnityEngine;

// Places a named test area through native inventory APIs, then uses real options.
internal sealed class AreaLabelScenario
{
    private readonly string name = "QA Area " + Guid.NewGuid().ToString("N").Substring(0, 8);
    private GameItemElement item;
    private ConfigEntry<bool> setting;
    private CustomUIElement toggle;
    private CustomUIElement apply;
    private int step;
    private float since = Time.realtimeSinceStartup;
    private float waitUntil;

    internal bool Tick(out string result)
    {
        result = null;
        if (Time.realtimeSinceStartup - since > 30) throw new InvalidOperationException("F02 QA timed out at step " + step);
        switch (step)
        {
            case 0:
            {
                var config = ModConfig.Find("starswhere.shopassist");
                setting = config?.Get("f02.enabled") as ConfigEntry<bool>;
                Require(setting != null && EmporiumEntry.Instance.invWindow != null && EmporiumEntry.Instance.invElement != null,
                    "F02 setting or native inventory is unavailable");
                if (!setting.Value)
                {
                    var options = StoreUIManager.Instance.gameOptionUIManager;
                    options.OpenUI();
                    var window = CustomUIManager.Instance.GetWindow("game_options");
                    window.Get("starswhere.shopassist:f02.enabled").SetBool(true, true);
                    window.Get("apply").button.onClick.Invoke();
                    Require(setting.Value, "F02 could not be enabled through options");
                    options.CloseUI();
                }
                item = DirectoryMaster.Item("storage_bay") as GameItemElement;
                Require(item != null, "Native storage area could not be created");
                GeneralHelper.SetCustomName(item, name);
                GraphUtils.EmporiumTryAdd(EmporiumEntry.Instance.invElement, item);
                Require(item.parentInventory != null, "Native inventory did not accept the QA area");
                Advance(1); return false;
            }
            case 1:
            {
                var label = Label();
                if (label == null || !label.gameObject.activeInHierarchy || !label.isActiveAndEnabled) return false;
                Require(label.text == name && !label.richText && !label.raycastTarget,
                    "F02 visible label text or input behavior is incorrect");
                var options = StoreUIManager.Instance.gameOptionUIManager;
                options.OpenUI();
                var window = CustomUIManager.Instance.GetWindow("game_options");
                toggle = window?.Get("starswhere.shopassist:f02.enabled");
                apply = window?.Get("apply");
                Require(toggle != null && apply?.button != null, "F02 option controls are missing");
                toggle.SetBool(false, true);
                Require(setting.Value, "F02 draft was applied before pressing Apply");
                waitUntil = Time.realtimeSinceStartup + 0.2f;
                Advance(2); return false;
            }
            case 2:
                if (Time.realtimeSinceStartup < waitUntil) return false;
                Require(Label() != null && Label().gameObject.activeInHierarchy, "F02 draft disabled its visible label before Apply");
                apply.button.onClick.Invoke();
                Require(!setting.Value, "F02 Apply did not disable the feature");
                Advance(3); return false;
            case 3:
            {
                var label = Label();
                if (label != null && label.gameObject.activeInHierarchy) return false;
                toggle.SetBool(true, true);
                apply.button.onClick.Invoke();
                Require(setting.Value, "F02 Apply did not re-enable the feature");
                StoreUIManager.Instance.gameOptionUIManager.CloseUI();
                Advance(4); return false;
            }
            case 4:
            {
                var label = Label();
                if (label == null || !label.gameObject.activeInHierarchy || !label.isActiveAndEnabled) return false;
                Require(label.text == name, "F02 label did not return with the correct text after re-enable");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-area-label.png"));
                    waitUntil = Time.realtimeSinceStartup + 0.5f;
                    Advance(5);
                    return false;
                }
                result = "PASS F02 native visible text '" + name + "', draft isolation and disable/re-enable";
                return true;
            }
            case 5:
                if (Time.realtimeSinceStartup < waitUntil) return false;
                result = "PASS F02 native visible text '" + name + "', draft isolation and disable/re-enable";
                return true;
            default: throw new InvalidOperationException("Unknown F02 QA step");
        }
    }

    private TMP_Text Label()
    {
        if (item == null || item.middle == null) return null;
        var root = item.middle.transform.Find("ShopAssist_AreaName");
        return root == null ? null : root.GetComponentInChildren<TMP_Text>(true);
    }
    private void Advance(int next) { step = next; since = Time.realtimeSinceStartup; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
