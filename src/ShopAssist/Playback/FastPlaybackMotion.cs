using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;

namespace ShopAssist
{
    // Only the customer/guard paths and their associated presentation waits are accelerated.
    internal static class FastPlaybackMotion
    {
        private sealed class HeldTween
        {
            internal MonoBehaviour Owner;
            internal DOTweenPath Path;
            internal Tween Tween;
            internal float Baseline;
            internal float Applied;
        }

        private sealed class WaitSite
        {
            internal string Callback;
            internal float? Constant;
            internal bool DurationField;
            internal OpCode? Arithmetic;
            internal bool DelayArgument;
        }

        private static readonly Dictionary<Tween, HeldTween> held = new Dictionary<Tween, HeldTween>();
        private static readonly HashSet<MethodBase> validOwnerHooks = new HashSet<MethodBase>();
        private static readonly List<Tween> removeTweens = new List<Tween>();
        private static FieldInfo clientPath, guardPath;
        private static Action<string> warn;
        private static bool installed;

        internal static void Install(Harmony harmony, Action<string> warning)
        {
            if (installed) return;
            if (harmony == null) throw new ArgumentNullException(nameof(harmony));
            installed = true;
            warn = warning;
            clientPath = AccessTools.Field(typeof(StoreClientMono), "dOTweenPath");
            guardPath = AccessTools.Field(typeof(GenericClientMono), "dOTweenPath");

            Patch(harmony, typeof(StoreShutterButton), "LerpShutter", nameof(ShutterTime));
            Patch(harmony, typeof(StoreShutterButton), "OpenShutter", nameof(Waits));
            Patch(harmony, typeof(StoreShutterButton), "EndAndClose", nameof(Waits));
            Patch(harmony, typeof(StoreUIManager), "RunMorningDialogue", nameof(Waits));
            Patch(harmony, typeof(StoreUIManager), "OnNextClientArrived", nameof(Waits));
            Patch(harmony, typeof(StoreUIManager), "StartArrestSequence", nameof(Waits));
            Patch(harmony, typeof(StoreClientMono), "OnLeft", nameof(Waits));
            Patch(harmony, typeof(WantedElement), "OnArrested", nameof(Waits));
            Patch(harmony, typeof(EmporiumAudioPlayer), "PlayWalking", nameof(Waits));

            if (clientPath == null || clientPath.FieldType != typeof(DOTweenPath))
                Warning("Customer path field is unavailable; customer path speed is unchanged.");
            else
            {
                PatchOwner(harmony, typeof(StoreClientMono), "Start", nameof(CustomerStarted));
                PatchOwner(harmony, typeof(StoreClientMono), "StartApprochingStore", nameof(CustomerStarted));
                PatchOwner(harmony, typeof(StoreClientMono), "StartLeavingStore", nameof(CustomerStarted));
                PatchOwner(harmony, typeof(StoreClientMono), "StartLeavingStoreRight", nameof(CustomerStarted));
            }
            if (guardPath == null || guardPath.FieldType != typeof(DOTweenPath))
                Warning("Guard path field is unavailable; guard path speed is unchanged.");
            else
            {
                PatchOwner(harmony, typeof(GenericClientMono), "Start", nameof(GuardStarted));
                PatchOwner(harmony, typeof(GenericClientMono), "StartArrive", nameof(GuardStarted));
                PatchOwner(harmony, typeof(GenericClientMono), "StartLeave", nameof(GuardStarted));
            }
        }

        internal static void Tick()
        {
            if (!installed) return;
            removeTweens.Clear();
            foreach (var pair in held)
            {
                HeldTween item = pair.Value;
                if (!IsBoundActive(item.Owner) || item.Path == null || !item.Path.gameObject.activeInHierarchy ||
                    item.Path.tween != item.Tween || !item.Tween.IsActive() || !item.Tween.IsPlaying())
                {
                    Restore(item);
                    removeTweens.Add(pair.Key);
                    continue;
                }
                Apply(item);
            }
            foreach (var tween in removeTweens) held.Remove(tween);
        }

        internal static void Reset()
        {
            foreach (var item in held.Values) Restore(item);
            held.Clear();
            removeTweens.Clear();
        }

        internal static void Dispose()
        {
            Reset();
            installed = false;
            validOwnerHooks.Clear();
            clientPath = guardPath = null;
            warn = null;
        }

        private static void Patch(Harmony harmony, Type type, string name, string transpiler)
        {
            MethodInfo method = AccessTools.Method(type, name);
            if (method == null) { Warning(type.Name + "." + name + " was not found; its original behavior is unchanged."); return; }
            try { harmony.Patch(method, transpiler: Hook(transpiler)); }
            catch (Exception error) { Warning(type.Name + "." + name + " could not be patched: " + error.Message); }
        }

        private static void PatchOwner(Harmony harmony, Type type, string name, string postfix)
        {
            MethodInfo method = AccessTools.Method(type, name, Type.EmptyTypes);
            if (method == null || method.ReturnType != typeof(void))
            {
                Warning(type.Name + "." + name + " was not found; its original behavior is unchanged.");
                return;
            }
            try { harmony.Patch(method, postfix: Hook(postfix), transpiler: Hook(nameof(VerifyOwnerHook))); }
            catch (Exception error) { validOwnerHooks.Remove(method); Warning(type.Name + "." + name + " could not be observed: " + error.Message); }
        }

        private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(FastPlaybackMotion), name));
        private static void Warning(string message) => warn?.Invoke(message);

        private static IEnumerable<CodeInstruction> VerifyOwnerHook(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var codes = instructions.ToList();
            bool valid = __originalMethod.Name == "Start";
            if (!valid)
            {
                string field = __originalMethod.Name == "StartLeavingStoreRight" ? "pathRight" : "dOTweenPath";
                string action = __originalMethod.Name == "StartLeavingStore" || __originalMethod.Name == "StartLeave"
                    ? "DOPlayBackwards" : "DOPlay";
                valid = codes.Any(code => code.opcode == OpCodes.Ldfld && code.operand is FieldInfo f &&
                        f.DeclaringType == __originalMethod.DeclaringType && f.Name == field && f.FieldType == typeof(DOTweenPath)) &&
                    codes.Any(code => code.operand is MethodInfo m && m.Name == action &&
                        m.DeclaringType == typeof(DG.Tweening.Core.ABSAnimationComponent));
            }
            if (valid) validOwnerHooks.Add(__originalMethod);
            else
            {
                validOwnerHooks.Remove(__originalMethod);
                Warning(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " has an unexpected path; its original behavior is unchanged.");
            }
            return codes;
        }

        private static void CustomerStarted(StoreClientMono __instance, MethodBase __originalMethod)
        {
            if (installed && validOwnerHooks.Contains(__originalMethod)) Register(__instance);
        }

        private static void GuardStarted(GenericClientMono __instance, MethodBase __originalMethod)
        {
            if (installed && validOwnerHooks.Contains(__originalMethod)) Register(__instance);
        }

        private static void Register(MonoBehaviour owner)
        {
            if (!IsBoundActive(owner)) return;
            CapturePaths(owner);
        }

        private static bool IsBoundActive(MonoBehaviour owner)
        {
            if (owner == null || !owner.isActiveAndEnabled || !owner.gameObject.activeInHierarchy) return false;
            StoreUIManager manager = StoreUIManager.Instance;
            if (manager == null) return false;
            if (owner is StoreClientMono customer) return manager.clientGameObject == customer;
            if (owner is GenericClientMono guard) return manager.secGameObject == guard || manager.secGameObject2 == guard;
            return false;
        }

        private static void CapturePaths(MonoBehaviour owner)
        {
            if (owner is StoreClientMono customer)
            {
                Capture(owner, clientPath?.GetValue(customer) as DOTweenPath);
                Capture(owner, customer.pathRight);
            }
            else if (owner is GenericClientMono guard) Capture(owner, guardPath?.GetValue(guard) as DOTweenPath);
        }

        private static void Capture(MonoBehaviour owner, DOTweenPath path)
        {
            if (path == null || !path.gameObject.activeInHierarchy) return;
            Tween tween = path.tween;
            if (tween == null || !tween.IsActive() || !tween.IsPlaying()) return;
            if (!held.TryGetValue(tween, out HeldTween item))
            {
                item = new HeldTween { Owner = owner, Path = path, Tween = tween, Baseline = tween.timeScale, Applied = tween.timeScale };
                held.Add(tween, item);
            }
            else if (item.Owner != owner || item.Path != path) return;
            Apply(item);
        }

        private static void Apply(HeldTween item)
        {
            float current = item.Tween.timeScale;
            // A different writer owns any value that changed since our last write.
            if (!Same(current, item.Applied)) item.Baseline = current;
            item.Applied = item.Baseline * FastPlayback.Rate;
            if (!Same(current, item.Applied)) item.Tween.timeScale = item.Applied;
        }

        private static void Restore(HeldTween item)
        {
            if (item.Path != null && item.Path.tween == item.Tween && item.Tween != null && item.Tween.IsActive() &&
                Same(item.Tween.timeScale, item.Applied))
                item.Tween.timeScale = item.Baseline;
        }

        private static bool Same(float left, float right) => left.Equals(right);

        private static IEnumerable<CodeInstruction> ShutterTime(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var codes = instructions.ToList();
            MethodInfo delta = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            FieldInfo elapsed = AccessTools.Field(typeof(StoreShutterButton), "elapsedTime");
            var matches = new List<int>();
            for (int i = 1; i + 2 < codes.Count; i++)
                if (codes[i].Calls(delta) && codes[i - 1].LoadsField(elapsed) && codes[i + 1].opcode == OpCodes.Add &&
                    codes[i + 2].opcode == OpCodes.Stfld && Equals(codes[i + 2].operand, elapsed)) matches.Add(i);
            if (matches.Count != 2 || codes.Count(code => code.Calls(delta)) != 2)
            {
                Warning(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " has unexpected shutter timing; its original behavior is unchanged.");
                return codes;
            }
            MethodInfo replacement = AccessTools.Method(typeof(FastPlaybackMotion), nameof(ScaledDeltaTime));
            foreach (int index in matches) codes[index].operand = replacement;
            return codes;
        }

        private static float ScaledDeltaTime() => Time.deltaTime * FastPlayback.Rate;

        private static IEnumerable<CodeInstruction> Waits(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var codes = instructions.ToList();
            WaitSite[] sites = Sites(__originalMethod);
            MethodInfo native = AccessTools.Method(typeof(MetaHelper), "WaitAndDo", new[] { typeof(float), typeof(Action) });
            MethodInfo replacement = AccessTools.Method(typeof(FastPlayback), "WaitAndDo", new[] { typeof(float), typeof(Action) });
            var indices = new List<int>();
            for (int i = 0; i < codes.Count; i++) if (codes[i].Calls(native)) indices.Add(i);
            bool valid = native != null && replacement != null && sites != null && indices.Count == sites.Length;
            int start = 0;
            if (valid && __originalMethod.DeclaringType == typeof(StoreClientMono) && __originalMethod.Name == "OnLeft")
                valid = MatchesClientLeave(codes, indices[0]);
            else if (valid)
            {
                for (int i = 0; i < sites.Length; i++)
                {
                    if (!Matches(codes, start, indices[i], sites[i])) { valid = false; break; }
                    start = indices[i] + 1;
                }
            }
            if (valid && __originalMethod.DeclaringType == typeof(EmporiumAudioPlayer))
                valid = codes.Any(code => code.opcode == OpCodes.Ldc_I4_4);
            if (!valid)
            {
                Warning(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " has unexpected presentation waits; its original behavior is unchanged.");
                return codes;
            }
            foreach (int index in indices) codes[index].operand = replacement;
            return codes;
        }

        private static bool MatchesClientLeave(List<CodeInstruction> codes, int call)
        {
            // The compiler caches the Action. Verify its complete construction and that the
            // native coroutine consumes this exact half-second wait before replacing it.
            if (call < 11 || call + 2 >= codes.Count ||
                !codes[call - 11].Calls(AccessTools.PropertyGetter(typeof(StoreUIManager), "Instance")) ||
                codes[call - 10].opcode != OpCodes.Ldc_R4 || !(codes[call - 10].operand is float seconds) || seconds != .5f ||
                codes[call - 9].opcode != OpCodes.Ldsfld || !(codes[call - 9].operand is FieldInfo cache) ||
                cache.FieldType != typeof(Action) || codes[call - 8].opcode != OpCodes.Dup ||
                (codes[call - 7].opcode != OpCodes.Brtrue && codes[call - 7].opcode != OpCodes.Brtrue_S) ||
                codes[call - 7].operand == null || !HasLabel(codes[call], codes[call - 7].operand) ||
                codes[call - 6].opcode != OpCodes.Pop || codes[call - 5].opcode != OpCodes.Ldsfld ||
                !(codes[call - 5].operand is FieldInfo singleton) || codes[call - 4].opcode != OpCodes.Ldftn ||
                !(codes[call - 4].operand is MethodInfo callback) || callback.IsStatic || callback.ReturnType != typeof(void) ||
                callback.GetParameters().Length != 0 || callback.DeclaringType?.DeclaringType != typeof(StoreClientMono) ||
                singleton.DeclaringType != callback.DeclaringType || singleton.FieldType != callback.DeclaringType || singleton.Name != "<>9" ||
                cache.DeclaringType != callback.DeclaringType ||
                !((callback.Name == "<OnLeft>b__42_0" && cache.Name == "<>9__42_0") ||
                  (callback.Name == "<OnLeft>b__37_0" && cache.Name == "<>9__37_0")) ||
                codes[call - 3].opcode != OpCodes.Newobj || !(codes[call - 3].operand is ConstructorInfo ctor) ||
                ctor.DeclaringType != typeof(Action) || codes[call - 2].opcode != OpCodes.Dup ||
                codes[call - 1].opcode != OpCodes.Stsfld || !Equals(codes[call - 1].operand, cache) ||
                !codes[call + 1].Calls(AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), new[] { typeof(IEnumerator) })) ||
                codes[call + 2].opcode != OpCodes.Pop) return false;
            return true;
        }

        private static bool HasLabel(CodeInstruction instruction, object label)
        {
            var labels = AccessTools.Field(typeof(CodeInstruction), "labels")?.GetValue(instruction) as IList;
            return labels != null && labels.Contains(label);
        }

        private static bool Matches(List<CodeInstruction> codes, int start, int end, WaitSite site)
        {
            var segment = codes.Skip(start).Take(end - start).ToList();
            int callback = segment.FindIndex(code => code.opcode == OpCodes.Ldftn && code.operand is MethodInfo method && method.Name == site.Callback);
            if (callback < 0 || !segment.Skip(callback + 1).Any(code => code.opcode == OpCodes.Newobj &&
                code.operand is ConstructorInfo ctor && ctor.DeclaringType == typeof(Action))) return false;
            var before = segment.Take(callback).ToList();
            if (site.DurationField && !before.Any(code => code.LoadsField(AccessTools.Field(typeof(StoreShutterButton), "duration")))) return false;
            if (site.Constant.HasValue && !before.Any(code => code.opcode == OpCodes.Ldc_R4 && code.operand is float value && value == site.Constant.Value)) return false;
            if (site.Arithmetic.HasValue && !before.Any(code => code.opcode == site.Arithmetic.Value)) return false;
            if (site.DelayArgument && !before.Any(code => code.opcode == OpCodes.Ldarg_1)) return false;
            return true;
        }

        private static WaitSite[] Sites(MethodBase method)
        {
            Type type = method.DeclaringType;
            switch (method.Name)
            {
                case "OpenShutter" when type == typeof(StoreShutterButton):
                    return new[] { new WaitSite { Callback = "<OpenShutter>b__26_0", DurationField = true } };
                case "EndAndClose" when type == typeof(StoreShutterButton):
                    return new[] {
                        new WaitSite { Callback = "<EndAndClose>b__29_0", DurationField = true },
                        new WaitSite { Callback = "<EndAndClose>b__1", DurationField = true, Constant = 1.25f, Arithmetic = OpCodes.Add },
                        new WaitSite { Callback = "<EndAndClose>b__2", DurationField = true, Constant = .2f, Arithmetic = OpCodes.Sub }
                    };
                case "RunMorningDialogue" when type == typeof(StoreUIManager):
                    return new[] { new WaitSite { Callback = "<RunMorningDialogue>b__134_0", Constant = 2f } };
                case "OnNextClientArrived" when type == typeof(StoreUIManager):
                    return new[] { new WaitSite { Callback = "<OnNextClientArrived>b__0", Constant = .25f } };
                case "StartArrestSequence" when type == typeof(StoreUIManager):
                    return new[] { new WaitSite { Callback = "<StartArrestSequence>b__124_0", DelayArgument = true } };
                case "OnLeft" when type == typeof(StoreClientMono):
                    return new[] { new WaitSite { Callback = "<OnLeft>b__42_0", Constant = .5f } };
                case "OnArrested" when type == typeof(WantedElement):
                    return new[] { new WaitSite { Callback = "<OnArrested>b__6_0", Constant = 1.5f } };
                case "PlayWalking" when type == typeof(EmporiumAudioPlayer):
                    return new[] { new WaitSite { Callback = "<PlayWalking>b__39_0", Constant = .55f, Arithmetic = OpCodes.Mul } };
                default: return null;
            }
        }
    }
}
