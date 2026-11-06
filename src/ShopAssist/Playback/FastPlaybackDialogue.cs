using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    // Presentation clocks only. The native dialogue callbacks and phone state machine stay in charge.
    internal static class FastPlaybackDialogue
    {
        private static Action<string> log;
        private static PhoneUIManager quickDialOwner;

        internal static void Install(Harmony harmony, Action<string> warning)
        {
            log = warning;
            PatchFlow(harmony, typeof(DialogUIManager));
            PatchFlow(harmony, typeof(DialoguePhoneUIManager));
            PatchClientLeave(harmony, typeof(DialogUIManager));
            PatchClientLeave(harmony, typeof(DialoguePhoneUIManager));
            PatchAdvance(harmony, typeof(DialogUIManager));
            PatchAdvance(harmony, typeof(DialoguePhoneUIManager));
            PatchPhoneEnd(harmony);
            PatchPhone(harmony);
        }

        internal static void Reset() { quickDialOwner = null; }
        internal static void Dispose() { Reset(); log = null; }

        private static void PatchPhoneEnd(Harmony harmony)
        {
            var method = AccessTools.DeclaredMethod(typeof(DialoguePhoneUIManager), "EndConversation", Type.EmptyTypes);
            if (method == null || AccessTools.Field(typeof(DialoguePhoneUIManager), "currentDialog") == null ||
                AccessTools.Field(typeof(DialoguePhoneUIManager), "conversationEpoch") == null)
            {
                Warn("phone dialogue completion state is unavailable");
                return;
            }
            try { harmony.Patch(method, postfix: Hook(nameof(ForwardPhoneEnded))); }
            catch (Exception e)
            {
                try { harmony.Unpatch(method, Method(nameof(ForwardPhoneEnded))); }
                catch (Exception unpatchError) { Warn("phone dialogue completion rollback failed: " + unpatchError.Message); }
                Warn("phone dialogue completion patch failed: " + e.Message);
            }
        }

        private static void ForwardPhoneEnded(DialoguePhoneUIManager __instance) => FastPlayback.PhoneEnded(__instance);

        private static void PatchAdvance(Harmony harmony, Type manager)
        {
            var method = AccessTools.DeclaredMethod(manager, "OnAdvancePressed", Type.EmptyTypes);
            if (method == null) { Warn(manager.Name + ".OnAdvancePressed is unavailable"); return; }
            try { harmony.Patch(method, postfix: Hook(nameof(NoteAdvance))); }
            catch (Exception e)
            {
                try { harmony.Unpatch(method, Method(nameof(NoteAdvance))); }
                catch (Exception unpatchError) { Warn(manager.Name + ".OnAdvancePressed rollback failed: " + unpatchError.Message); }
                Warn(manager.Name + ".OnAdvancePressed patch failed: " + e.Message);
            }
        }

        private static void NoteAdvance() => FastPlayback.NoteDialogueAdvance();

        private static void PatchFlow(Harmony harmony, Type manager)
        {
            var method = AccessTools.DeclaredMethod(manager, "ScheduleFlow", new[] { typeof(float), typeof(Action) });
            if (method == null) { Warn(manager.Name + ".ScheduleFlow is unavailable"); return; }
            try { harmony.Patch(method, transpiler: Hook(nameof(ReplaceFlowDelay))); }
            catch (Exception e)
            {
                try { harmony.Unpatch(method, Method(nameof(ReplaceFlowDelay))); }
                catch (Exception unpatchError) { Warn(manager.Name + ".ScheduleFlow rollback failed: " + unpatchError.Message); }
                Warn(manager.Name + ".ScheduleFlow patch failed: " + e.Message);
            }
        }

        private static IEnumerable<CodeInstruction> ReplaceFlowDelay(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var native = AccessTools.Method(typeof(MetaHelper), nameof(MetaHelper.WaitAndDo), new[] { typeof(float), typeof(Action) });
            var start = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), new[] { typeof(IEnumerator) });
            var actionCtor = AccessTools.Constructor(typeof(Action), new[] { typeof(object), typeof(IntPtr) });
            var sites = code.Select((instruction, index) => new { instruction, index })
                .Where(x => x.instruction.Calls(native)).Select(x => x.index).ToArray();
            if (sites.Length != 1 || sites[0] < 2 || sites[0] + 1 >= code.Count ||
                code[sites[0] - 1].opcode != OpCodes.Newobj || !Equals(code[sites[0] - 1].operand, actionCtor) ||
                code[sites[0] - 2].opcode != OpCodes.Ldftn ||
                !(code[sites[0] - 2].operand is MethodInfo callback) ||
                callback.Name.IndexOf("<ScheduleFlow>", StringComparison.Ordinal) < 0 ||
                !code[sites[0] + 1].Calls(start))
            {
                Warn(__originalMethod.DeclaringType.Name + ".ScheduleFlow layout changed; native timing retained");
                return code;
            }
            code[sites[0]].operand = AccessTools.Method(typeof(FastPlayback), nameof(FastPlayback.WaitAndDo));
            return code;
        }

        private static void PatchClientLeave(Harmony harmony, Type manager)
        {
            var method = AccessTools.DeclaredMethod(manager, "CurrentClientLeave", new[] { typeof(float) });
            if (method == null) { Warn(manager.Name + ".CurrentClientLeave is unavailable"); return; }
            try { harmony.Patch(method, transpiler: Hook(nameof(ReplaceClientLeaveDelay))); }
            catch (Exception e)
            {
                try { harmony.Unpatch(method, Method(nameof(ReplaceClientLeaveDelay))); }
                catch (Exception unpatchError) { Warn(manager.Name + ".CurrentClientLeave rollback failed: " + unpatchError.Message); }
                Warn(manager.Name + ".CurrentClientLeave patch failed: " + e.Message);
            }
        }

        private static IEnumerable<CodeInstruction> ReplaceClientLeaveDelay(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var native = AccessTools.Method(typeof(MetaHelper), nameof(MetaHelper.WaitAndDo), new[] { typeof(float), typeof(Action) });
            var start = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), new[] { typeof(IEnumerator) });
            var dismiss = AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.DismissCurrentClient),
                new[] { typeof(bool), typeof(bool), typeof(bool) });
            var sites = code.Select((instruction, index) => new { instruction, index })
                .Where(x => x.instruction.Calls(native)).Select(x => x.index).ToArray();
            var callbacks = code.Select((instruction, index) => new { instruction, index })
                .Where(x => x.instruction.opcode == OpCodes.Ldftn && x.instruction.operand is MethodInfo callback &&
                    callback.Name.IndexOf("<CurrentClientLeave>", StringComparison.Ordinal) >= 0 &&
                    callback.DeclaringType?.DeclaringType == __originalMethod.DeclaringType)
                .Select(x => x.index).ToArray();
            int directDismissals = code.Count(instruction => instruction.Calls(dismiss));
            bool store = __originalMethod.DeclaringType == typeof(DialogUIManager);
            if (native == null || start == null || dismiss == null || sites.Length != 1 || callbacks.Length != 1 ||
                sites[0] + 1 >= code.Count || callbacks[0] >= sites[0] || sites[0] - callbacks[0] > 8 ||
                !(code[callbacks[0]].operand is MethodInfo callback) || !IsExpectedLeaveCallback(callback, dismiss) ||
                !code[sites[0] + 1].Calls(start) || directDismissals != (store ? 1 : 0) ||
                store && !code.Any(instruction => instruction.Calls(AccessTools.Method(typeof(float), nameof(float.Equals), new[] { typeof(float) }))))
            {
                Warn(__originalMethod.DeclaringType.Name + ".CurrentClientLeave layout changed; native timing retained");
                return code;
            }
            code[sites[0]].operand = Method(nameof(WaitForSameClient));
            return code;
        }

        private static bool IsExpectedLeaveCallback(MethodInfo callback, MethodInfo dismiss)
        {
            try
            {
                // call PlayerStore.Instance; false, true, true; callvirt DismissCurrentClient; ret.
                byte[] il = callback.GetMethodBody()?.GetILAsByteArray();
                if (il == null || il.Length != 14 || il[0] != 0x28 || il[5] != 0x16 || il[6] != 0x17 ||
                    il[7] != 0x17 || il[8] != 0x6f || il[13] != 0x2a) return false;
                return Equals(callback.Module.ResolveMethod(BitConverter.ToInt32(il, 1)),
                        AccessTools.PropertyGetter(typeof(PlayerStore), nameof(PlayerStore.Instance))) &&
                    Equals(callback.Module.ResolveMethod(BitConverter.ToInt32(il, 9)), dismiss);
            }
            catch (Exception) { return false; }
        }

        private static IEnumerator WaitForSameClient(float seconds, Action action)
        {
            var player = PlayerStore.HasInstance ? PlayerStore.Instance : null;
            var client = player?.currentClientInstance;
            var run = player?.runID;
            int slot = player == null ? -1 : player.saveSlotId;
            int epoch = FastPlayback.Epoch;
            return FastPlayback.WaitAndDo(seconds, () =>
            {
                if (player == null || !PlayerStore.HasInstance || !ReferenceEquals(player, PlayerStore.Instance) ||
                    !ReferenceEquals(player.currentClientInstance, client) || player.runID != run ||
                    player.saveSlotId != slot || FastPlayback.Epoch != epoch) return;
                action();
            });
        }

        private static void PatchPhone(Harmony harmony)
        {
            var type = typeof(PhoneUIManager);
            var update = AccessTools.DeclaredMethod(type, "Update", Type.EmptyTypes);
            var auto = AccessTools.DeclaredMethod(type, "AutoCall", new[] { typeof(long) });
            var resetMethods = new[]
            {
                AccessTools.DeclaredMethod(type, "PressNumber", new[] { typeof(int) }),
                AccessTools.DeclaredMethod(type, "StopCall", Type.EmptyTypes),
                AccessTools.DeclaredMethod(type, "CloseUI", Type.EmptyTypes),
                AccessTools.DeclaredMethod(type, "StartCalling", Type.EmptyTypes)
            };
            if (update == null || auto == null || resetMethods.Any(m => m == null) ||
                AccessTools.Field(type, "clickTimer") == null || AccessTools.Field(type, "callingTimer") == null ||
                AccessTools.Field(type, "sequenceTimer") == null)
            {
                Warn("phone timing methods or fields are unavailable");
                return;
            }

            // Install the origin-clearing hooks first; a partial install must never make manual dialing faster.
            var installed = new List<(MethodBase original, MethodInfo patch)>();
            try
            {
                var clear = Method(nameof(ClearQuickDial));
                foreach (var method in resetMethods)
                {
                    installed.Add((method, clear));
                    harmony.Patch(method, prefix: new HarmonyMethod(clear));
                }
                var mark = Method(nameof(MarkQuickDialFallback));
                installed.Add((auto, clear));
                installed.Add((auto, mark));
                harmony.Patch(auto, prefix: new HarmonyMethod(clear), transpiler: new HarmonyMethod(mark));
                var clock = Method(nameof(ReplacePhoneClocks));
                installed.Add((update, clock));
                harmony.Patch(update, transpiler: new HarmonyMethod(clock));
            }
            catch (Exception e)
            {
                foreach (var entry in installed.AsEnumerable().Reverse())
                    try { harmony.Unpatch(entry.original, entry.patch); }
                    catch (Exception unpatchError) { Warn("phone patch rollback failed: " + unpatchError.Message); }
                Reset();
                Warn("phone timing patch failed: " + e.Message);
            }
        }

        private static IEnumerable<CodeInstruction> ReplacePhoneClocks(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var delta = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            var click = AccessTools.Field(typeof(PhoneUIManager), "clickTimer");
            var calling = AccessTools.Field(typeof(PhoneUIManager), "callingTimer");
            var sequence = AccessTools.Field(typeof(PhoneUIManager), "sequenceTimer");
            var sites = code.Select((instruction, index) => new { instruction, index })
                .Where(x => x.instruction.Calls(delta)).Select(x => x.index).ToArray();
            if (sites.Length != 3 ||
                !IsTimerIncrement(code, sites[0], click, OpCodes.Sub) ||
                !IsTimerIncrement(code, sites[1], calling, OpCodes.Add) ||
                !IsTimerIncrement(code, sites[2], sequence, OpCodes.Sub) ||
                !HasNoLabels(code[sites[0]]) || code[sites[0]].blocks.Count != 0)
            {
                Warn("PhoneUIManager.Update timer layout changed; native timing retained");
                return code;
            }

            code[sites[1]].operand = Method(nameof(PresentationDelta));
            code[sites[2]].operand = Method(nameof(PresentationDelta));
            // Preserve the existing stack and pass this particular phone to the quick-dial clock.
            code[sites[0]].opcode = OpCodes.Ldarg_0;
            code[sites[0]].operand = null;
            code.Insert(sites[0] + 1, new CodeInstruction(OpCodes.Call, Method(nameof(DialDelta))));
            return code;
        }

        private static bool IsTimerIncrement(List<CodeInstruction> code, int site, FieldInfo field, OpCode operation)
        {
            return field != null && site >= 3 && site + 2 < code.Count &&
                code[site - 3].opcode == OpCodes.Ldarg_0 && code[site - 2].opcode == OpCodes.Ldarg_0 &&
                code[site - 1].opcode == OpCodes.Ldfld && Equals(code[site - 1].operand, field) &&
                code[site + 1].opcode == operation && code[site + 2].opcode == OpCodes.Stfld &&
                Equals(code[site + 2].operand, field);
        }

        private static bool HasNoLabels(CodeInstruction instruction)
        {
            // Do not expose Harmony's runtime-specific Label type through the netstandard build.
            var labels = AccessTools.Field(typeof(CodeInstruction), "labels")?.GetValue(instruction) as ICollection;
            return labels != null && labels.Count == 0;
        }

        private static IEnumerable<CodeInstruction> MarkQuickDialFallback(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var click = AccessTools.Field(typeof(PhoneUIManager), "clickTimer");
            var stop = AccessTools.DeclaredMethod(typeof(PhoneUIManager), "StopCall", Type.EmptyTypes);
            var willAnswer = AccessTools.DeclaredMethod(typeof(PhoneUIManager), "WillAnswerCall", new[] { typeof(long) });
            var startDialog = AccessTools.DeclaredMethod(typeof(PhoneUIManager), "StartPhoneDialog", new[] { typeof(long) });
            var sites = code.Select((instruction, index) => new { instruction, index })
                .Where(x => x.instruction.opcode == OpCodes.Stfld && Equals(x.instruction.operand, click))
                .Select(x => x.index).ToArray();
            if (sites.Length != 1 || sites[0] < 2 || sites[0] + 1 >= code.Count ||
                code[sites[0] - 2].opcode != OpCodes.Ldarg_0 || code[sites[0] - 1].opcode != OpCodes.Ldc_R4 ||
                !Equals(code[sites[0] - 1].operand, 2.25f) || code[sites[0] + 1].opcode != OpCodes.Ret ||
                code[sites[0]].blocks.Count != 0 || code[sites[0] + 1].blocks.Count != 0 ||
                code.Count(i => i.Calls(stop)) != 1 || code.Count(i => i.Calls(willAnswer)) != 1 ||
                code.Count(i => i.Calls(startDialog)) != 1)
            {
                Warn("PhoneUIManager.AutoCall fallback layout changed; quick-dial delay remains native");
                return code;
            }
            code.Insert(sites[0] + 1, new CodeInstruction(OpCodes.Ldarg_0));
            code.Insert(sites[0] + 2, new CodeInstruction(OpCodes.Call, Method(nameof(RememberQuickDial))));
            return code;
        }

        private static void ClearQuickDial() { quickDialOwner = null; }
        private static void RememberQuickDial(PhoneUIManager phone) { quickDialOwner = phone; }
        private static float PresentationDelta() { return Time.deltaTime * FastPlayback.Rate; }
        private static float DialDelta(PhoneUIManager phone)
        {
            return Time.deltaTime * (ReferenceEquals(quickDialOwner, phone) ? FastPlayback.Rate : 1f);
        }

        private static MethodInfo Method(string name) => AccessTools.DeclaredMethod(typeof(FastPlaybackDialogue), name);
        private static HarmonyMethod Hook(string name) => new HarmonyMethod(Method(name));
        private static void Warn(string message) { log?.Invoke(message); }
    }
}
