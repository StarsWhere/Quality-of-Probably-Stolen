using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Public feature and known-risk evidence are exercised using real inventory views.
public sealed class ItemLabelsScenario
{
    private const string Mod = "starswhere.shopassist";
    private readonly QaNewRun start = new QaNewRun();
    private GameItemElement bag, unseenBag, stolen, inside, unknownCustom;
    private string counterfeitLabel, labLabel;
    private int step;
    private float since = Time.realtimeSinceStartup, until;
    private static void Need(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }
    private static GameItemElement Item(string id)
    {
        var item = DirectoryMaster.Item(id) as GameItemElement;
        Need(item != null, "Missing " + id); GeneralHelper.SetItemOwned(item, true); return item;
    }
    private static void Put(GameInventory inventory, GameItem item)
    {
        var slot = inventory.TryFindOneValidInventorySlot(item);
        Need(slot != null && slot.targetItem == null && slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory,
            "Cannot place " + item.identifier);
    }
    private static void PutRotated(GameGridInventory inventory, GameItem item)
    {
        var pose = new GridShapeBuilder(item.modifiedShape);
        for (int y = 0; y < inventory.inventoryShape.height; y++)
        for (int x = 0; x < inventory.inventoryShape.width; x++)
        {
            pose.SetTransform(x, y, false, 1);
            var slot = inventory.TryInventorySlot(item, item.unitCount, pose);
            if (slot == null || slot.inventory != inventory || slot.targetItem != null || slot.numTransfer < item.unitCount) continue;
            Need(slot.TryAcceptOnce(item.unitCount) == item.unitCount && item.parentInventory == inventory,
                "Cannot place rotated " + item.identifier);
            Need(item.modifiedShape.orientation == 1 && item.modifiedShape.globalWidth != item.modifiedShape.globalHeight,
                "The rotated fixture must have a non-square global shape");
            return;
        }
        throw new InvalidOperationException("No room for rotated " + item.identifier);
    }
    private static void AddCondition(GameItem item, ItemCondition publicCondition, ItemCondition realCondition, string id)
    {
        // Use the game's localized condition values while keeping the fixture
        // side effect-free on an unrelated test container.
        publicCondition.onActivateActionId = null;
        var feature = new ItemFeature(ItemFeature.FeatureType.Special) { identifier = id, category = publicCondition.category };
        feature.SetConditions(publicCondition, realCondition); item.AddItemFeature(feature);
    }
    private static Transform Mark(GameItemElement item) => item.middle.transform.Find("ShopAssist_KnownStolen");
    private static Transform Labels(GameItemElement item) => item.middle.transform.Find("ShopAssist_PublicLabels");
    private static string Text(Transform node) => node == null ? "" : string.Join("|", node.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text));
    private static bool Visible(Transform node) => node != null && node.gameObject.activeInHierarchy;
    private static bool Overlap(Transform first, Transform second)
    {
        if (!(first is RectTransform a) || !(second is RectTransform b)) return false;
        var ac = new Vector3[4]; var bc = new Vector3[4]; a.GetWorldCorners(ac); b.GetWorldCorners(bc);
        return ac[0].x < bc[2].x && ac[2].x > bc[0].x && ac[0].y < bc[2].y && ac[2].y > bc[0].y;
    }
    private static void WithinGlobalShape(GameItemElement item, Transform node, string description)
    {
        Need(item != null && item.modifiedShape != null && item.middle?.rectTransform != null &&
            UISettings.current != null && UISettings.current.gridSize > 0 && node is RectTransform && Visible(node),
            "Cannot verify visible overlay bounds: " + description);
        RectTransform parent = item.middle.rectTransform;
        var rect = (RectTransform)node;
        float width = item.modifiedShape.globalWidth * UISettings.current.gridSize;
        float height = item.modifiedShape.globalHeight * UISettings.current.gridSize;
        Need(width > 0 && height > 0, "Invalid global shape for " + description);
        float left = parent.rect.xMin, top = parent.rect.yMax;
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        foreach (Vector3 world in corners)
        {
            Vector3 point = parent.InverseTransformPoint(world);
            Need(point.x >= left - .1f && point.x <= left + width + .1f &&
                point.y <= top + .1f && point.y >= top - height - .1f,
                description + " extends outside the actual global grid footprint: corner=" + point +
                " bounds=" + new Rect(left, top - height, width, height));
        }
    }
    private static string FullLabel(GameItemElement item)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var loaded = ModLoader.Mods.FirstOrDefault(mod => mod.Manifest?.ID == Mod)?.Instance;
        var modules = loaded?.GetType().GetField("modules", flags)?.GetValue(loaded) as IEnumerable;
        if (modules == null) return null;
        foreach (object module in modules)
        {
            Type type = module.GetType();
            if ((string)type.GetField("Name", flags)?.GetValue(module) != "F38") continue;
            var tick = type.GetField("Tick", flags)?.GetValue(module) as Action;
            object owner = tick?.Target;
            var labels = owner?.GetType().GetField("labels", flags)?.GetValue(owner) as IDictionary;
            object label = labels != null && labels.Contains(item) ? labels[item] : null;
            return label?.GetType().GetField("Full", flags)?.GetValue(label) as string;
        }
        return null;
    }
    private static bool LabelRaycastReachesItem(GameItemElement item)
    {
        if (!(Labels(item) is RectTransform rect) || EventSystem.current == null) return false;
        var canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var hits = new List<RaycastResult>();
        var samples = new List<string>();
        foreach (float x in new[] { .1f, .3f, .5f, .7f, .9f })
            foreach (float y in new[] { .25f, .5f, .75f })
            {
                Vector3 local = new Vector3(Mathf.Lerp(rect.rect.xMin, rect.rect.xMax, x), Mathf.Lerp(rect.rect.yMin, rect.rect.yMax, y), 0);
                Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(local));
                hits.Clear(); EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
                var target = RenderHandler.RaycastElement<GameItemElement>(point);
                if (ReferenceEquals(target, item)) return true;
                if (samples.Count < 5) samples.Add(point + " native=" + PathOf(target?.handler?.transform) + " => " +
                    string.Join(" | ", hits.Take(4).Select(hit => PathOf(hit.gameObject?.transform))));
            }
        Debug.Log("[PLAYER-QA] Label hit failed for " + PathOf(item.handler.transform) + " label=" + PathOf(rect) + " " + string.Join(" ; ", samples));
        return false;
    }
    private static string PathOf(Transform node)
    {
        string path = "";
        for (int i = 0; node != null && i < 10; i++, node = node.parent) path = node.name + "/" + path;
        return path;
    }
    private static void DumpVisuals(params (string label, Transform node)[] nodes)
    {
        var output = new StringBuilder();
        foreach (var (name, node) in nodes)
        {
            output.AppendLine(name + " " + PathOf(node));
            if (node == null) continue;
            foreach (var child in node.GetComponentsInChildren<Transform>(true))
            {
                output.Append("  ").Append(PathOf(child)).Append(" active=").Append(child.gameObject.activeInHierarchy);
                if (child is RectTransform rect)
                {
                    var corners = new Vector3[4]; rect.GetWorldCorners(corners);
                    var canvas = child.GetComponentInParent<Canvas>();
                    Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    output.Append(" rect=").Append(rect.rect).Append(" pos=").Append(rect.anchoredPosition).Append(" size=").Append(rect.sizeDelta)
                        .Append(" screen=").Append(RectTransformUtility.WorldToScreenPoint(camera, corners[0]))
                        .Append(".. ").Append(RectTransformUtility.WorldToScreenPoint(camera, corners[2]));
                }
                var image = child.GetComponent<Image>();
                if (image != null) output.Append(" image=").Append(image.enabled).Append(" rgba=").Append(image.color)
                    .Append(" alpha=").Append(image.canvasRenderer.GetAlpha()).Append(" culled=").Append(image.canvasRenderer.cull);
                var text = child.GetComponent<TMP_Text>();
                if (text != null) output.Append(" text=").Append(text.enabled).Append(" rgba=").Append(text.color)
                    .Append(" alpha=").Append(text.canvasRenderer.GetAlpha()).Append(" culled=").Append(text.canvasRenderer.cull)
                    .Append(" size=").Append(text.fontSize).Append(" value=").Append(text.text.Replace("\n", "|"));
                output.AppendLine();
            }
            for (Transform parent = node.parent; parent != null; parent = parent.parent)
            {
                var mask = parent.GetComponent<RectMask2D>(); var group = parent.GetComponent<CanvasGroup>();
                if (mask != null || group != null)
                    output.Append("  ancestor=").Append(PathOf(parent)).Append(" mask=").Append(mask != null && mask.enabled)
                        .Append(" groupAlpha=").Append(group == null ? -1 : group.alpha).AppendLine();
            }
        }
        File.WriteAllText(Path.Combine(Application.persistentDataPath, "qa-label-render.txt"), output.ToString());
    }
    public bool Tick(out string result)
    {
        result = null; Need(Time.realtimeSinceStartup - since < 90, "Item label QA timeout at step " + step);
        if (step == 0) { if (start.Tick()) { until = Time.realtimeSinceStartup + 4; step++; } return false; }
        if (step == 1)
        {
            if (Time.realtimeSinceStartup < until) return false;
            if (EmporiumEntry.Instance?.storeInteractables?.activeInHierarchy != true || ItemMouseDoubleClickHandler.current == null) return false;
            TradeScenario.SetOptions(true, "f02.enabled", "f38.enabled", "f39.enabled", "f04.enabled", "f04.unseen");
            var root = EmporiumEntry.Instance.invElement;
            bag = Item("backpack_medium"); unseenBag = Item("backpack_medium");
            GeneralHelper.SetCustomName(bag, "背包");
            stolen = Item("wine_yeast"); inside = Item("wine_yeast"); unknownCustom = Item("processed_milk");
            StolenHelper.SetHeat(stolen, 42); StolenHelper.SetHeat(inside, 42); StolenHelper.SetHeat(unknownCustom, 42);
            var unobservedInside = Item("wine_yeast"); StolenHelper.SetHeat(unobservedInside, 42);
            var counterfeit = ItemConditionList.CreateCounterfeitCigarette();
            var lab = ItemConditionList.CreateLabPure();
            counterfeitLabel = counterfeit.display; labLabel = lab.display;
            AddCondition(bag, counterfeit, ItemConditionList.CreateGenuineCigarette(), "qa_public_counterfeit");
            AddCondition(bag, lab, ItemConditionList.CreateJunkGrade(), "qa_public_lab");
            var extra = new ItemFeature(ItemFeature.FeatureType.Special).SetPublicDisplay("A").SetActualDisplay("SECRET-A");
            extra.identifier = "qa_public_extra"; bag.AddItemFeature(extra);
            AddCondition(unseenBag, ItemConditionList.CreateLabPure(), ItemConditionList.CreateJunkGrade(), "qa_other_lab");
            var hidden = new ItemFeature(ItemFeature.FeatureType.Special).SetPublicDisplay("HIDDEN").SetActualDisplay("HIDDEN-TRUE");
            hidden.identifier = "qa_hidden";
            hidden.SetPublicHidden(true); bag.AddItemFeature(hidden);
            string prefix = LocHelper.GetLocalizedMechanic("mech_label_tooltip_prefix");
            unknownCustom.tooltipRichTextFunc = (item, inv, slot, shape, state) => new RichTextBuilder().AddLine(prefix + " Z");
            PutRotated(root, bag);
            foreach (var item in new[] { unseenBag, stolen, unknownCustom }) Put(root, item);
            Put((GameGridInventory)bag.contentWindow.childElement, inside);
            Put((GameGridInventory)unseenBag.contentWindow.childElement, unobservedInside);
            unseenBag.contentWindow.Hide(); ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(Screen.width * .76f, Screen.height * .70f));
            Need(bag.contentWindow.IsVisible() && !unseenBag.contentWindow.IsVisible(), "Container fixture visibility unexpected");
            PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateInspectionClient());
            PlayerStore.Instance.isClientArrived = true;
            step++; return false;
        }
        if (step == 2)
        {
            if (!Visible(Mark(stolen)) || !Visible(Mark(inside)) || !Visible(Mark(bag)) || !Visible(Labels(bag))) return false;
            Need(Text(Mark(stolen)).Contains("!") && Text(Mark(bag)).Contains("+"), "Direct and contained markers are not distinct");
            var name = bag.middle.transform.Find("ShopAssist_AreaName");
            Need(Visible(name) && !Overlap(name, Labels(bag)) && !Overlap(name, Mark(bag)) && !Overlap(Labels(bag), Mark(bag)),
                "Area name / public labels / stolen marker overlap");
            Canvas.ForceUpdateCanvases();
            WithinGlobalShape(bag, name, "rotated area name");
            WithinGlobalShape(bag, Labels(bag), "rotated public labels");
            WithinGlobalShape(bag, Mark(bag), "rotated contained-risk marker");
            WithinGlobalShape(stolen, Mark(stolen), "direct-risk marker");
            WithinGlobalShape(inside, Mark(inside), "inside direct-risk marker");
            Need(!Visible(Mark(unseenBag)) && !Visible(Mark(unknownCustom)), "Marker inferred hidden container or custom-tooltip risk");
            string labels = Text(Labels(bag));
            Need(labels.Contains(counterfeitLabel) && labels.Contains("+1") && !labels.Contains("SECRET") && !labels.Contains("HIDDEN"),
                "Public fake labels / overflow / hidden values incorrect: " + labels);
            Need(Visible(Labels(unseenBag)) && Text(Labels(unseenBag)).Contains(labLabel),
                "Native laboratory-grade label was not readable: " + Text(Labels(unseenBag)));
            var labText = Labels(unseenBag).GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
            Need(labText != null, "Laboratory-grade label text missing"); labText.ForceMeshUpdate();
            Need(!labText.isTextTruncated, "Laboratory-grade label is still truncated after fitting");
            Need(labText.isActiveAndEnabled && Labels(unseenBag).GetComponent<Image>()?.isActiveAndEnabled == true &&
                Mark(bag).GetComponent<Image>()?.isActiveAndEnabled == true && Mark(stolen).GetComponent<Image>()?.isActiveAndEnabled == true &&
                Mark(stolen).GetComponentsInChildren<TMP_Text>(true).Any(text => text.isActiveAndEnabled),
                "A label or marker graphic is disabled even though its node is active");
            string full = FullLabel(bag);
            Need(full != null && full.Contains(counterfeitLabel) && full.Contains(labLabel) && full.Contains("A") &&
                !full.Contains("SECRET") && !full.Contains("HIDDEN"), "Hover text lost a public label or exposed a hidden value: " + full);
            Need(LabelRaycastReachesItem(bag), "No point on the compact label can reach its item through the real UI raycast");
            Need(!Visible(Labels(unknownCustom)), "Custom label appeared before its tooltip was shown");
            DumpVisuals(("area", name), ("bag label", Labels(bag)), ("bag marker", Mark(bag)),
                ("lab label", Labels(unseenBag)), ("direct marker", Mark(stolen)), ("bag background", bag.background.transform));
            var overlay = StoreUIManager.Instance.controlHintOverlay;
            if (overlay != null && overlay.activeSelf) StoreUIManager.Instance.ToggleControlHintOverlay();
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-public-label-markers-open.png"));
            until = Time.realtimeSinceStartup + 1; step = 20; return false;
        }
        if (step == 20)
        {
            if (Time.realtimeSinceStartup < until) return false;
            var tooltip = unknownCustom.GetTooltip();
            OverlayHandler.current.ShowTooltip(Vector2.zero, Vector2.zero, tooltip, 60);
            step = 3; return false;
        }
        if (step == 3)
        {
            if (!Visible(Labels(unknownCustom))) return false;
            Need(Text(Labels(unknownCustom)).Contains("Z") && !Visible(Mark(unknownCustom)),
                "Observed custom tooltip did not control both public labels and risk");
            OverlayHandler.current.HideTooltip(); bag.contentWindow.Hide();
            step++; return false;
        }
        if (step == 4)
        {
            if (!Visible(Mark(bag))) return false;
            Need(!Visible(Mark(unseenBag)), "Unseen bag inherited F04 deep-inspection evidence");
            TradeScenario.SetOptions(false, "f04.enabled");
            step++; return false;
        }
        if (step == 5)
        {
            Need(Visible(Mark(stolen)) && Visible(Mark(bag)), "Known markers depend on F04");
            var overlay = StoreUIManager.Instance.controlHintOverlay;
            if (overlay != null && overlay.activeSelf) StoreUIManager.Instance.ToggleControlHintOverlay();
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "qa-public-label-markers.png"));
            until = Time.realtimeSinceStartup + 1; step = 51; return false;
        }
        if (step == 51)
        {
            if (Time.realtimeSinceStartup < until) return false;
            ItemMouseDoubleClickHandler.current.OpenContentAction(bag, new Vector2(300, 180));
            Put(EmporiumEntry.Instance.invElement, inside); bag.contentWindow.Hide(); step = 6; return false;
        }
        if (step == 6)
        {
            if (Visible(Mark(bag))) return false;
            Need(Visible(Mark(inside)), "Moved known stolen item lost its direct marker");
            until = Time.realtimeSinceStartup + 1; step++; return false;
        }
        if (Time.realtimeSinceStartup < until) return false;
        result = "PASS public fake/hidden/overflow labels, observed custom tooltip, independent direct/contained stolen marks and movement invalidation"; return true;
    }
}
