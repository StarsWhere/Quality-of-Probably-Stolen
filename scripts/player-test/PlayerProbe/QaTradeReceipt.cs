using System;
using System.Linq;
using TMPro;
using UnityEngine;

internal static class QaTradeReceipt
{
    internal static GameObject Latest() => UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>()
        .Where(text => text.gameObject.activeInHierarchy && text.gameObject.name.StartsWith("starswhere.shopassist.receipt.", StringComparison.Ordinal))
        .OrderBy(text => text.gameObject.name, StringComparer.Ordinal).LastOrDefault()?.gameObject;

    internal static string Text()
    {
        var root = Latest();
        return root == null ? "" : string.Join("  ·  ", root.GetComponentsInChildren<TMP_Text>()
            .Where(label => label.enabled && !string.IsNullOrEmpty(label.text)).Select(label => label.text));
    }
}
