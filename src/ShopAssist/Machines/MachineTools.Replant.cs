using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace ShopAssist
{
    internal sealed partial class MachineTools
    {
        private const string ReplantDataKey = "f32.rules";
        [Serializable] private sealed class SeedGene
        {
            [JsonRequired] public string id;
            [JsonRequired] public int rank;
        }
        [Serializable] private sealed class SeedRule
        {
            [JsonRequired] public int machine;
            [JsonRequired] public string plant;
            [JsonRequired] public List<SeedGene> genes = new List<SeedGene>();
            public int? state;
            public string reason;
            public string location;
        }
        [Serializable] private sealed class SeedRules
        {
            [JsonRequired] public int version = 3;
            [JsonRequired] public string run;
            [JsonRequired] public int slot;
            [JsonRequired] public List<SeedRule> rules = new List<SeedRule>();
        }
        private sealed class SeedChoice
        {
            internal GameItem Item;
            internal string Plant, Signature;
            internal List<SeedGene> Genes;
        }

        private ConfigEntry<bool> replantEnabled;
        private CustomUIWindow replantWindow;
        private GameItem replantMachine;
        private string replantRun;
        private int replantSlot;
        private string replantSourceKey;
        private bool replantSourceLoaded;
        private bool replantDirty, replantScanner;
        private readonly HashSet<int> replantQueue = new HashSet<int>();
        private bool replantNeedsScan = true;
        private string replantQueueRun;
        private string replantReadError;
        private int replantQueueSlot = -1;

        private void InitReplant()
        {
            replantEnabled = settings.Toggle("f32.enabled", "f32.tip");
            settings.Action("f32.open", OpenReplant, () => Availability("f32.open"));
        }

        internal static bool CanOpenReplantFor(GameItem item) => current?.started == true && current.replantEnabled.Value &&
            ShopInventory.InStore && item?.identifier == "hydroponic" && item.IsTag("HYDROPONIC_MACHINE_TAG") &&
            FindTarget(item, out _, out _);

        private static bool FocusedHydroponic(out GameItem machine)
        {
            machine = null;
            if (!ShopInventory.InStore || !FindTarget(out GameItem item, out _) || item.identifier != "hydroponic" ||
                !item.IsTag("HYDROPONIC_MACHINE_TAG")) return false;
            machine = item;
            return true;
        }

        private bool ReplantContext() => replantEnabled.Value && ShopInventory.InStore && replantMachine != null &&
            replantRun == PlayerStore.Instance.runID && replantSlot == PlayerStore.Instance.saveSlotId &&
            CanOpenReplantFor(replantMachine);

        private void TickReplant()
        {
            if (replantWindow != null)
            {
                if (!replantWindow.IsAlive || !ReplantContext()) CloseReplant();
                else if (replantDirty || replantScanner != PlantGeneticsHelper.IsScannerOn()) DrawReplant();
            }
            if (!replantEnabled.Value || !ShopInventory.InStore) return;
            EnsureReplantIdentity();
            SeedRules scannedRules = null;
            if (replantNeedsScan)
            {
                replantNeedsScan = false;
                if (ReadSeedRules(out scannedRules))
                    foreach (SeedRule rule in scannedRules.rules)
                        if (rule.state == (int)ReplantPhase.Pending) replantQueue.Add(rule.machine);
            }
            if (replantQueue.Count == 0 || !ShopInput.Ready() || UIDragHandler.isDraggingAnyUI ||
                ItemMouseDragHandler.current?.IsDraggingItem == true || DialogUIManager.Instance?.currentDialog != null ||
                DialoguePhoneUIManager.Instance?.currentDialog != null || StoreUIManager.Instance?.isAboutToStartMainDialog == true) return;
            foreach (int id in replantQueue.OrderBy(value => value).ToArray())
            {
                replantQueue.Remove(id);
                SeedRules initial = scannedRules;
                scannedRules = null;
                try { TryReplant(id, initial); }
                catch (Exception error) { log("F32 retry failed: " + error); MarkReplantInspection(id); }
            }
        }
        private void ReplantSettingsChanged()
        {
            if (!replantEnabled.Value) { CloseReplant(); replantQueue.Clear(); }
            else replantNeedsScan = true;
            AttentionBook.Changed();
        }

        private void EnsureReplantIdentity()
        {
            string run = PlayerStore.Instance.runID;
            int slot = PlayerStore.Instance.saveSlotId;
            if (run == replantQueueRun && slot == replantQueueSlot) return;
            replantQueue.Clear(); replantQueueRun = run; replantQueueSlot = slot; replantNeedsScan = true;
        }

        private void ResetReplant()
        {
            CloseReplant(); replantQueue.Clear(); replantQueueRun = null; replantQueueSlot = -1; replantNeedsScan = true; replantReadError = null;
        }

        private void OpenReplant()
        {
            if (!replantEnabled.Value || !FocusedHydroponic(out GameItem target) || CustomUIManager.Instance == null) return;
            OpenReplantFor(target);
        }

        internal static void OpenReplantFor(GameItem target)
        {
            if (!CanOpenReplantFor(target) || CustomUIManager.Instance == null) return;
            current.OpenReplantTarget(target);
        }

        private void OpenReplantTarget(GameItem target)
        {
            CloseReplant(); replantMachine = target; replantRun = PlayerStore.Instance.runID; replantSlot = PlayerStore.Instance.saveSlotId;
            replantNeedsScan = true;
            DrawReplant();
        }

        private bool ReadSeedRules(out SeedRules data)
        {
            data = null;
            if (!ShopInventory.InStore || !Guid.TryParse(PlayerStore.Instance.runID, out Guid run) || run == Guid.Empty ||
                PlayerStore.Instance.saveSlotId < 0) return false;
            try
            {
                string raw = ModHelper.GetModData(ModEntry.Id, ReplantDataKey, null);
                data = raw == null ? new SeedRules { run = PlayerStore.Instance.runID, slot = PlayerStore.Instance.saveSlotId } :
                    ShopData.Decode<SeedRules>(raw, 65536);
                if (data == null || data.version != 2 && data.version != 3 || data.run != PlayerStore.Instance.runID ||
                    data.slot != PlayerStore.Instance.saveSlotId || data.rules == null || data.rules.Count > 100 ||
                    data.rules.Any(rule => rule == null || rule.machine <= 0 || string.IsNullOrEmpty(rule.plant) ||
                        rule.plant.Length > 128 || !ValidGenes(rule.genes) || rule.location != null &&
                            !MaintenanceSourceKey.TryParse(rule.location, 32, out _, out _, out _)) ||
                    data.rules.Select(rule => rule.machine).Distinct().Count() != data.rules.Count)
                    throw new FormatException("Invalid F32 rules or save identity");
                foreach (SeedRule rule in data.rules)
                {
                    if (!rule.state.HasValue || rule.state < 0 || rule.state > 2 || !ValidReplantReason(rule))
                        throw new FormatException("Invalid F32 pending state");
                }
                replantReadError = null;
                data.version = 3;
                return true;
            }
            catch (Exception error)
            {
                data = null;
                if (replantReadError != error.Message) log("F32 retained unreadable or mismatched rules: " + error.Message);
                replantReadError = error.Message; return false;
            }
        }

        private static bool ValidReplantReason(SeedRule rule) => rule.reason == "" && rule.state == (int)ReplantPhase.Idle ||
            rule.state == (int)ReplantPhase.Pending && (rule.reason == "f32.pending" || rule.reason == "f32.missing" ||
                rule.reason == "f32.stack" || rule.reason == "f32.unavailable" || rule.reason == "f32.waiting") ||
            rule.state == (int)ReplantPhase.Pending && rule.reason == "f32.source_missing" ||
            rule.state == (int)ReplantPhase.Inspect && rule.reason == "f32.inspect";

        private static bool ValidGenes(List<SeedGene> genes) => genes != null && genes.Count <= 64 &&
            genes.All(gene => gene != null && !string.IsNullOrEmpty(gene.id) && gene.id.Length <= 128 && gene.rank > 0) &&
            genes.Select(gene => gene.id).Distinct(StringComparer.Ordinal).Count() == genes.Count &&
            genes.Select(gene => gene.id).SequenceEqual(genes.Select(gene => gene.id).OrderBy(id => id, StringComparer.Ordinal));

        private static List<SeedGene> Genes(GameItem seed)
        {
            if (seed == null || !seed.IsTag("SEED_ITEM_TAG") || HydroponicHelper.GetPlant(seed) == null) return null;
            var result = PlantGeneticsHelper.GetTraits(seed).Select(pair => new SeedGene { id = pair.trait.id, rank = pair.rank })
                .OrderBy(gene => gene.id, StringComparer.Ordinal).ToList();
            return ValidGenes(result) ? result : null;
        }

        private static string Signature(List<SeedGene> genes) => ShopData.Encode(genes);

        private List<SeedChoice> AvailableSeeds()
        {
            var result = new List<SeedChoice>();
            GameSlotInventory input = MachineHydroponic.GetInputSlot(replantMachine);
            GameGridInventory output = MachineHydroponic.GetOutputSlot(replantMachine);
            if (input == null || output == null) return result;
            GameGridInventory source = ResolveReplantSource(replantMachine, replantSourceKey);
            var items = source == null ? new List<GameItem>() : new List<GameItem>(source.childItems);
            if (input.childItem != null) items.Add(input.childItem);
            foreach (GameItem item in items)
            {
                if (item == null || !ShopInventory.Owned(item) || !item.IsTag("SEED_ITEM_TAG")) continue;
                string plant = HydroponicHelper.GetPlantId(item);
                List<SeedGene> genes = Genes(item);
                if (string.IsNullOrEmpty(plant) || plant.Length > 128 || genes == null) continue;
                string signature = Signature(genes);
                if (!result.Any(choice => choice.Plant == plant && choice.Signature == signature))
                    result.Add(new SeedChoice { Item = item, Plant = plant, Signature = signature, Genes = genes });
            }
            return result;
        }

        private void DrawReplant()
        {
            if (!ReplantContext()) { CloseReplant(); return; }
            replantDirty = false;
            bool validRules = ReadSeedRules(out SeedRules data);
            bool scanner = PlantGeneticsHelper.IsScannerOn(); replantScanner = scanner;
            SeedRule rule = validRules ? data.rules.Find(row => row.machine == replantMachine.uniqueId && row.machine > 0) : null;
            if (!replantSourceLoaded) { replantSourceKey = rule?.location; replantSourceLoaded = true; }
            List<SeedChoice> choices = validRules ? AvailableSeeds() : new List<SeedChoice>();
            SeedChoice selected = rule == null ? null : choices.Find(choice => choice.Plant == rule.plant &&
                choice.Signature == Signature(rule.genes));
            if (selected != null && !scanner && choices.Count(choice => choice.Plant == selected.Plant) > 1) selected = null;
            int count = choices.GroupBy(choice => choice.Plant).Sum(group => !scanner && group.Count() > 1 ? 1 : group.Count());
            int cardRows = (count + 3) / 4;
            float scrollHeight = count == 0 ? 36 : Mathf.Min(205, cardRows * 100 + 4);
            int statusHeight = rule != null && rule.state != (int)ReplantPhase.Idle ? 70 : 0;
            int height = Mathf.Clamp(Mathf.CeilToInt(265 + scrollHeight + (rule == null ? 0 : 56) + statusHeight), 350, 640);
            var builder = UiLayout.Rebuild(ref replantWindow, ModEntry.Id + ".f32", ShopSettings.Text("f32.open"), 600, height);
            replantWindow = builder.GetWindow(); CustomUIWindow own = replantWindow;
            builder.OnClose(() => { if (ReferenceEquals(replantWindow, own)) replantWindow = null; });
            if (!validRules) { builder.AddWarning(ShopSettings.Text("f32.invalid")).Show(); return; }
            ShopInventory.Location supply = ReplantSourceLocation(replantSourceKey);
            builder.BeginRow().AddButton(supply?.Name ?? ShopSettings.Text(replantSourceKey == null ? "vm.seed_output" : "f32.source_missing"), () =>
            {
                if (!ReplantContext()) return;
                ChooseConsumableSource(ShopSettings.Text("f32.choose_source"), 32, replantMachine, supply,
                    location => ChangeReplantSource(location.Key(true)), own);
            }, "source");
            if (replantSourceKey != null)
                builder.AddButton(ShopSettings.Text("vm.seed_output"), () => ChangeReplantSource(null), "own_output");
            builder.End();
            var header = new List<ItemGrid.Entry>
            {
                new ItemGrid.Entry(replantMachine) { Caption = replantMachine.GetDisplayName(true), Badge = ShopSettings.Text("visual.machine") }
            };
            if (selected != null)
                header.Add(new ItemGrid.Entry(selected.Item) { Caption = selected.Item.GetDisplayName(true),
                    Badge = ShopSettings.Text("f32.selected"), Selected = true, Reason = SeedDetails(selected, scanner) });
            ItemGrid.Add(builder, header, new ItemGrid.Options { Cards = true, Columns = 2, CardWidth = 150, CardHeight = 88 });
            if (selected != null)
            {
                string details = SeedDetails(selected, scanner);
                builder.AddLabel(SeedSource(selected.Item) + (string.IsNullOrEmpty(details) ? "" : " · " + details));
                if (builder.Last.text != null) builder.Last.text.richText = false;
            }
            else if (rule != null)
            {
                string name = PlantRegistry.Get(rule.plant)?.displayName;
                if (string.IsNullOrEmpty(name)) name = ShopSettings.Text("f32.unknown_plant");
                string details = SeedDetails(rule.genes, scanner);
                builder.AddLabel(ShopSettings.Format("f32.target", name) +
                    (string.IsNullOrEmpty(details) ? "" : " · " + details));
                if (builder.Last.text != null) builder.Last.text.richText = false;
            }
            if (rule != null && rule.state != (int)ReplantPhase.Idle)
            {
                builder.AddWarning(ShopSettings.Text(rule.reason));
                int machineId = replantMachine.uniqueId; string capturedRun = replantRun; int capturedSlot = replantSlot;
                if (rule.state == (int)ReplantPhase.Pending)
                    builder.AddButton(ShopSettings.Text("f32.retry"), () => RequestReplant(machineId, capturedRun, capturedSlot), "retry");
                else
                    builder.AddButton(ShopSettings.Text("f32.resolve"), () => ResolveReplant(machineId, capturedRun, capturedSlot), "resolve");
            }
            if (rule != null)
            {
                builder.AddButton(ShopSettings.Text("f32.stop"), () =>
                {
                    if (!ReplantContext() || !ReadSeedRules(out SeedRules live)) return;
                    if (live.rules.Any(row => row.machine == replantMachine.uniqueId && row.state == (int)ReplantPhase.Inspect)) return;
                    live.rules.RemoveAll(row => row.machine == replantMachine.uniqueId);
                    if (SaveSeedRules(live)) DrawReplant();
                }, "stop");
                builder.Last.SetInteractable(rule.state != (int)ReplantPhase.Inspect);
            }
            builder.BeginScroll(scrollHeight);
            if (choices.Count == 0) builder.AddLabel(ShopSettings.Text("f32.empty"));
            var cards = new List<ItemGrid.Entry>();
            var lookup = new Dictionary<ItemGrid.Entry, SeedChoice>();
            foreach (var group in choices.GroupBy(choice => choice.Plant))
            {
                if (!scanner && group.Count() > 1)
                {
                    SeedChoice ambiguous = group.First();
                    cards.Add(new ItemGrid.Entry(ambiguous.Item) { Caption = ambiguous.Item.GetDisplayName(true),
                        Enabled = false, Badge = ShopSettings.Text("f32.scan"), Reason = ShopSettings.Text("f32.scan") });
                    continue;
                }
                foreach (SeedChoice choice in group)
                {
                    bool activeRule = rule != null && rule.plant == choice.Plant && Signature(rule.genes) == choice.Signature;
                    string details = SeedDetails(choice, scanner);
                    string source = SeedSource(choice.Item);
                    var card = new ItemGrid.Entry(choice.Item) { Caption = choice.Item.GetDisplayName(true), Selected = activeRule,
                        Badge = (activeRule ? ShopSettings.Text("f32.selected") : source) +
                            (string.IsNullOrEmpty(details) ? "" : " · " + details),
                        Reason = source + (string.IsNullOrEmpty(details) ? "" : "\n" + details) };
                    cards.Add(card); lookup[card] = choice;
                }
            }
            if (cards.Count != 0)
                ItemGrid.Add(builder, cards, new ItemGrid.Options { Cards = true, Columns = 4, CardWidth = 130, CardHeight = 100,
                    OnClick = entry => { if (lookup.TryGetValue(entry, out SeedChoice choice)) BindSeed(choice); } });
            builder.End().Show();
        }

        private string SeedSource(GameItem seed) => ReferenceEquals(seed?.parentInventory, MachineHydroponic.GetInputSlot(replantMachine)) ?
            ShopSettings.Text("vm.seed_input") : ReplantSourceLocation(replantSourceKey)?.Name ?? ShopSettings.Text("vm.seed_output");

        private static ShopInventory.Location ReplantSourceLocation(string key)
        {
            if (key == null) return null;
            var matches = ShopInventory.OwnedLocations(false).Where(location => location.Key() == key && IsMaintenanceSource(location, 32)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static GameGridInventory ResolveReplantSource(GameItem machine, string key)
        {
            GameGridInventory source = key == null ? MachineHydroponic.GetOutputSlot(machine) : ReplantSourceLocation(key)?.Inventory as GameGridInventory;
            return source != null && InventoryRoutes.AutoTakeAllowed(source) ? source : null;
        }

        private void ChangeReplantSource(string key)
        {
            if (!ReplantContext() || key != null && !MaintenanceSourceKey.TryParse(key, 32, out _, out _, out _) ||
                !ReadSeedRules(out SeedRules data)) return;
            if (key != null && !ConsumableSources(32, replantMachine).Any(location => location.Key() == key))
            { ShopNotice.Show(ShopSettings.Text("inv.changed")); return; }
            SeedRule rule = data.rules.Find(value => value.machine == replantMachine.uniqueId);
            if (rule?.state == (int)ReplantPhase.Inspect) { ShopNotice.Show(ShopSettings.Text("f32.inspect")); return; }
            if (rule != null)
            {
                rule.location = key;
                if (!SaveSeedRules(data)) return;
                replantNeedsScan = true;
            }
            replantSourceKey = key;
            DrawReplant();
        }

        private static string SeedDetails(SeedChoice choice, bool scanner) => SeedDetails(choice.Genes, scanner);

        private static string SeedDetails(List<SeedGene> genes, bool scanner) => !scanner || genes == null || genes.Count == 0 ? "" :
            string.Join(" / ", genes.Select(gene =>
            {
                PlantTrait trait = PlantTraitRegistry.Get(gene.id);
                return (trait?.displayName ?? gene.id) + " " + PlantGeneticsHelper.RankLabel(gene.rank);
            }));

        private void BindSeed(SeedChoice choice)
        {
            if (!ReplantContext() || !ReadSeedRules(out SeedRules data)) return;
            if (data.rules.Any(row => row.machine == replantMachine.uniqueId && row.state == (int)ReplantPhase.Inspect))
            { ShopNotice.Show(ShopSettings.Text("f32.inspect")); return; }
            var available = AvailableSeeds();
            if (!available.Any(current => ReferenceEquals(current.Item, choice.Item) && current.Plant == choice.Plant &&
                current.Signature == choice.Signature) || !PlantGeneticsHelper.IsScannerOn() &&
                available.Count(current => current.Plant == choice.Plant) > 1) { DrawReplant(); return; }
            int machineId = replantMachine.GetUniqueID();
            SeedRule existing = data.rules.Find(row => row.machine == machineId);
            if (existing != null && existing.plant == choice.Plant && Signature(existing.genes) == choice.Signature &&
                existing.location == replantSourceKey) return;
            data.rules.RemoveAll(row => row.machine == machineId);
            if (data.rules.Count >= 100) { ShopNotice.Show(ShopSettings.Text("f32.limit")); return; }
            data.rules.Add(new SeedRule { machine = machineId, plant = choice.Plant, genes = choice.Genes,
                location = replantSourceKey, state = (int)ReplantPhase.Idle, reason = "" });
            if (SaveSeedRules(data)) DrawReplant();
        }

        private bool SaveSeedRules(SeedRules data)
        {
            try
            {
                string raw = ShopData.Encode(data);
                ModHelper.SetModData(ModEntry.Id, ReplantDataKey, raw);
                if (ModHelper.GetModData(ModEntry.Id, ReplantDataKey, null) != raw)
                    throw new InvalidOperationException("Replant state update was not retained.");
                replantDirty = true; machinesDirty = true; AttentionBook.Changed();
                return true;
            }
            catch (Exception error) { log("F32 could not save replant state: " + error); ShopNotice.Show(ShopSettings.Text("f32.invalid")); return false; }
        }

        private GameItem ReplantMachine(int id)
        {
            GameItem item = FindUniqueOwnedItem(id);
            return item?.identifier == "hydroponic" && MachineAccessible(item) ? item : null;
        }

        private void BeginReplant(GameItem machine)
        {
            if (!started || !replantEnabled.Value || !ShopInventory.InStore || machine == null || machine.uniqueId <= 0 ||
                machine.identifier != "hydroponic" || !ShopInventory.Owned(machine) || !ReadSeedRules(out SeedRules data)) return;
            SeedRule rule = data.rules.Find(row => row.machine == machine.uniqueId);
            if (rule == null || rule.state == (int)ReplantPhase.Inspect || MachineHydroponic.GetInputSlot(machine)?.childItem != null) return;
            if (rule.state != (int)ReplantPhase.Pending)
            {
                rule.state = (int)ReplantPhase.Pending; rule.reason = "f32.pending";
                if (!SaveSeedRules(data)) return;
            }
            EnsureReplantIdentity(); replantQueue.Add(machine.uniqueId);
        }

        private bool SetReplantState(SeedRules data, SeedRule rule, ReplantPhase state, string reason, GameItem machine, bool notify)
        {
            if (rule.state == (int)state && rule.reason == reason) return true;
            rule.state = (int)state; rule.reason = reason;
            if (!SaveSeedRules(data)) return false;
            if (notify) AttentionBook.Changed();
            return true;
        }

        private void MarkReplantInspection(int id)
        {
            if (!ReadSeedRules(out SeedRules data)) return;
            SeedRule rule = data.rules.Find(row => row.machine == id);
            if (rule != null && rule.state != (int)ReplantPhase.Idle)
                SetReplantState(data, rule, ReplantPhase.Inspect, "f32.inspect", ReplantMachine(id), true);
        }

        private void RequestReplant(int id, string run, int slot)
        {
            if (!started || !replantEnabled.Value || !ShopInventory.InStore || PlayerStore.Instance.runID != run ||
                PlayerStore.Instance.saveSlotId != slot || !ReadSeedRules(out SeedRules data)) return;
            if (!data.rules.Any(rule => rule.machine == id && rule.state == (int)ReplantPhase.Pending)) return;
            EnsureReplantIdentity(); replantQueue.Add(id);
        }

        private void ResolveReplant(int id, string run, int slot)
        {
            if (!started || !ShopInventory.InStore || PlayerStore.Instance.runID != run ||
                PlayerStore.Instance.saveSlotId != slot || !ReadSeedRules(out SeedRules data)) return;
            GameItem machine = ReplantMachine(id);
            GameSlotInventory input = machine == null ? null : MachineHydroponic.GetInputSlot(machine);
            if (input == null) { ShopNotice.Show(ShopSettings.Text("f32.machine_unavailable")); return; }
            SeedRule rule = data.rules.Find(row => row.machine == id && row.state == (int)ReplantPhase.Inspect);
            if (rule == null) return;
            bool alreadyPlanted = input.childItem != null;
            if (!SetReplantState(data, rule, alreadyPlanted ? ReplantPhase.Idle : ReplantPhase.Pending,
                alreadyPlanted ? "" : "f32.pending", machine, false)) return;
            if (!alreadyPlanted && replantEnabled.Value) { EnsureReplantIdentity(); replantQueue.Add(id); }
        }

        private void TryReplant(int id, SeedRules data = null)
        {
            if (!started || !ShopInventory.InStore || data == null && !ReadSeedRules(out data)) return;
            SeedRule rule = data.rules.Find(row => row.machine == id);
            if (rule == null) return;
            ReplantPhase phase = (ReplantPhase)rule.state.Value;
            if (!replantEnabled.Value || ReplantTaskRules.Decide(phase, replantEnabled.Value, false, false, false) == ReplantDecision.None) return;
            GameItem machine = ReplantMachine(id);
            if (machine == null) { SetReplantState(data, rule, ReplantPhase.Pending, "f32.unavailable", null, false); return; }
            GameSlotInventory input = MachineHydroponic.GetInputSlot(machine);
            GameGridInventory output = MachineHydroponic.GetOutputSlot(machine);
            if (input?.childItem != null)
            {
                if (ReplantTaskRules.Decide(phase, replantEnabled.Value, true, false, false) == ReplantDecision.Clear)
                    SetReplantState(data, rule, ReplantPhase.Idle, "", machine, false);
                return;
            }
            if (input == null || output == null || input.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(input) ||
                MachineProgressHelper.GetMachineState(machine) != MachineProgressHelper.STATE_READY)
            { SetReplantState(data, rule, ReplantPhase.Pending, "f32.waiting", machine, true); return; }
            string signature = Signature(rule.genes);
            GameGridInventory seedSource = ResolveReplantSource(machine, rule.location);
            if (seedSource == null) { SetReplantState(data, rule, ReplantPhase.Pending, "f32.source_missing", machine, true); return; }
            var matching = seedSource.childItems.Where(item => item != null && item.IsTag("SEED_ITEM_TAG") &&
                HydroponicHelper.GetPlantId(item) == rule.plant && Genes(item) is List<SeedGene> genes && Signature(genes) == signature).ToList();
            GameItem seed = matching.OrderBy(item => item.modifiedShape?.minY ?? 0).ThenBy(item => item.modifiedShape?.minX ?? 0)
                .ThenBy(item => item.uniqueId).FirstOrDefault(item => item.unitCount == 1 && ShopInventory.Movable(item) &&
                    MaintenanceSwap.NativeTransferAllowed(seedSource, input, item));
            if (ReplantTaskRules.Decide(phase, replantEnabled.Value, false, true, seed != null) != ReplantDecision.Attempt)
            {
                string reason = matching.Count == 0 ? "f32.missing" : matching.Any(item => item.unitCount == 1) ? "f32.unavailable" : "f32.stack";
                SetReplantState(data, rule, ReplantPhase.Pending, reason, machine, true); return;
            }
            SlotMarker slot = input.TryInventorySlot(seed, 1, seed.modifiedShape);
            if (slot == null || !ReferenceEquals(slot.inventory, input) || slot.targetItem != null || slot.numTransfer < 1)
            { SetReplantState(data, rule, ReplantPhase.Pending, "f32.waiting", machine, true); return; }
            if (!SetReplantState(data, rule, ReplantPhase.Inspect, "f32.inspect", machine, false)) return;
            // Saving the intent precedes the native move. Recheck every mutable boundary before committing it.
            if (!started || !replantEnabled.Value || !ShopInventory.InStore || PlayerStore.Instance.runID != data.run ||
                PlayerStore.Instance.saveSlotId != data.slot || !ReferenceEquals(ReplantMachine(id), machine) ||
                !ReadSeedRules(out SeedRules verified)) return;
            SeedRule pending = verified.rules.Find(row => row.machine == id && row.state == (int)ReplantPhase.Inspect &&
                row.plant == rule.plant && Signature(row.genes) == signature && row.location == rule.location);
            if (pending == null) return;
            if (!ReferenceEquals(MachineHydroponic.GetInputSlot(machine), input) || !ReferenceEquals(MachineHydroponic.GetOutputSlot(machine), output) ||
                input.childItem != null || input.IsInsertLocked() || !GeneralHelper.MayPlayerInsertInto(input) ||
                MachineProgressHelper.GetMachineState(machine) != MachineProgressHelper.STATE_READY ||
                !ReferenceEquals(ResolveReplantSource(machine, pending.location), seedSource) ||
                !ReferenceEquals(seed.parentInventory, seedSource) || seed.unitCount != 1 || !ShopInventory.Movable(seed) ||
                !MaintenanceSwap.NativeTransferAllowed(seedSource, input, seed))
            {
                SetReplantState(verified, pending, input.childItem != null ? ReplantPhase.Idle : ReplantPhase.Pending,
                    input.childItem != null ? "" : "f32.waiting", machine, false);
                return;
            }
            slot = input.TryInventorySlot(seed, 1, seed.modifiedShape);
            if (slot == null || !ReferenceEquals(slot.inventory, input) || slot.targetItem != null || slot.numTransfer < 1)
            { SetReplantState(verified, pending, ReplantPhase.Pending, "f32.waiting", machine, false); return; }
            try
            {
                int moved = slot.TryAcceptOnce(1);
                if (moved == 1 && ReferenceEquals(input.childItem, seed) && seed.unitCount == 1)
                {
                    if (ReadSeedRules(out SeedRules after))
                    {
                        SeedRule live = after.rules.Find(row => row.machine == id && row.state == (int)ReplantPhase.Inspect &&
                            row.plant == rule.plant && Signature(row.genes) == signature && row.location == pending.location);
                        if (live != null) SetReplantState(after, live, ReplantPhase.Idle, "", machine, false);
                    }
                    return;
                }
            }
            catch (Exception error) { log("F32 native seed move failed: " + error); }
            if (seed.parentInventory == null)
            {
                // Output slots reject insertion; put the intact original in a legal ordinary inventory if possible.
                foreach (ShopInventory.Location location in ShopInventory.OwnedLocations(false))
                {
                    if (!location.Ordinary || !(location.Inventory is GameGridInventory grid)) continue;
                    try
                    {
                        SlotMarker recovery = GridPlacements.Empty(grid, seed);
                        if (recovery != null && recovery.TryAcceptOnce(1) == 1 && seed.parentInventory != null) break;
                    }
                    catch (Exception error) { log("F32 seed recovery location failed: " + error); }
                }
            }
            log("F32 could not complete the original seed move for machine " + machine.uniqueId);
            AttentionBook.Changed();
        }

        private void CloseReplant()
        {
            CustomUIWindow window = replantWindow; replantWindow = null; replantMachine = null; replantRun = null;
            replantDirty = false;
            replantSourceKey = null; replantSourceLoaded = false;
            ClearConsumableSourceMarkers();
            window?.Close();
        }

        private static void ReplantInventoryChanged(GameInventory inventory)
        {
            MachineTools owner = current;
            if (owner?.started != true || inventory == null) return;
            try
            {
                if (owner.replantEnabled.Value && ShopInventory.InStore) owner.replantNeedsScan = true;
                if (owner.replantWindow != null && owner.replantMachine != null &&
                    (ReferenceEquals(inventory, MachineHydroponic.GetInputSlot(owner.replantMachine)) ||
                    ReferenceEquals(inventory, MachineHydroponic.GetOutputSlot(owner.replantMachine)))) owner.replantDirty = true;
            }
            catch (Exception error) { owner.log("F32 could not refresh seed cards: " + error.Message); owner.replantDirty = true; }
        }

        [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.UncheckedAccept))]
        private static class ReplantGridAdded
        { private static void Postfix(GameGridInventory __instance) => ReplantInventoryChanged(__instance); }
        [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.Expel))]
        private static class ReplantGridRemoved
        { private static void Postfix(GameGridInventory __instance) => ReplantInventoryChanged(__instance); }
        [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.UncheckedAccept))]
        private static class ReplantSlotAdded
        { private static void Postfix(GameSlotInventory __instance) => ReplantInventoryChanged(__instance); }
        [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.Expel))]
        private static class ReplantSlotRemoved
        { private static void Postfix(GameSlotInventory __instance) => ReplantInventoryChanged(__instance); }

        [HarmonyPatch(typeof(GameItem), "set_unitCount")]
        private static class ReplantQuantityChanged
        { private static void Postfix(GameItem __instance) => ReplantInventoryChanged(__instance?.parentInventory); }

        [HarmonyPatch(typeof(MachineProgressHelper), nameof(MachineProgressHelper.FinishProgressTypeMachine))]
        private static class ReplantMachineFinished
        {
            private static void Postfix(GameItem __0)
            {
                if (current?.started == true && current.replantEnabled.Value && __0?.identifier == "hydroponic")
                    current.replantNeedsScan = true;
            }
        }

        [HarmonyPatch(typeof(HydroponicHelper), nameof(HydroponicHelper.ContinueGrowing))]
        private static class ReplantAfterHarvest
        {
            private static void Prefix(GameItem growingSeed, GameItem hydroponic, out bool __state)
            {
                __state = false;
                try
                {
                    __state = current != null && current.started && current.replantEnabled.Value && growingSeed != null &&
                        hydroponic != null && hydroponic.identifier == "hydroponic" && !HydroponicHelper.IsPerennial(growingSeed) &&
                        HydroponicHelper.IsGrowingSeed(growingSeed) &&
                        MachineProgressHelper.GetMachineState(hydroponic) == MachineProgressHelper.STATE_WORKING &&
                        ReferenceEquals(MachineHydroponic.GetInputSlot(hydroponic)?.childItem, growingSeed) &&
                        MachineProgressHelper.IsProgressTypeMachineFinished(hydroponic);
                }
                catch (Exception error) { current?.log("F32 skipped an uncertain harvest: " + error.Message); }
            }
            private static void Postfix(GameItem hydroponic, bool __state)
            {
                if (!__state) return;
                try { current?.BeginReplant(hydroponic); }
                catch (Exception error) { current?.log("F32 stopped after harvest: " + error); }
            }
        }
    }
}
