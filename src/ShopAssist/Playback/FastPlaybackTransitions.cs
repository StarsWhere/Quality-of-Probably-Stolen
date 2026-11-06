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
    // These patches touch only the presentation iterators listed below. Their original
    // callbacks, state checks, and settlement work continue to run in native order.
    internal static class FastPlaybackTransitions
    {
        private enum Shape { BlackScreen, FinishBlackScreen, CaseOpening, Wound, BodyFall }

        private static readonly Dictionary<MethodBase, Shape> shapes = new Dictionary<MethodBase, Shape>();
        private static readonly Dictionary<MethodBase, bool> compatible = new Dictionary<MethodBase, bool>();
        private static Action<string> warning;
        private static readonly ConstructorInfo waitConstructor = AccessTools.Constructor(typeof(WaitForSeconds), new[] { typeof(float) });
        private static readonly MethodInfo deltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
        private static readonly MethodInfo scaledDelta = AccessTools.Method(typeof(FastPlaybackTransitions), nameof(ScaledDelta));
        private static readonly MethodInfo delay = AccessTools.Method(typeof(FastPlayback), "Delay", new[] { typeof(float) });
        private static readonly MethodInfo finishDelay = AccessTools.Method(typeof(FastPlaybackTransitions), nameof(FinishDelay));
        private static readonly MethodInfo scope = AccessTools.Method(typeof(FastPlayback), "Scope", new[] { typeof(IEnumerator) });
        private static readonly MethodInfo bodyFactory = AccessTools.Method(typeof(StoreUIManager), "BodyFallRoutine", Type.EmptyTypes);
        private static readonly MethodInfo startCoroutine = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), new[] { typeof(IEnumerator) });

        internal static void Install(Harmony harmony, Action<string> warn)
        {
            warning = warn;
            Install(harmony, typeof(StoreUIManager), "BlackScreenSequence", Shape.BlackScreen);
            Install(harmony, typeof(MainMenuUIController), "BlackScreenSequence", Shape.BlackScreen);
            Install(harmony, typeof(StoreUIManager), "PlayerFinishBlackScreenSequence", Shape.FinishBlackScreen);
            Install(harmony, typeof(StoreClientUniqueList), "CaseOpeningRoutine", Shape.CaseOpening);
            bool body = Install(harmony, typeof(StoreUIManager), "BodyFallRoutine", Shape.BodyFall);
            bool wound = body && Install(harmony, typeof(StoreUIManager), "WoundAnimationRoutine", Shape.Wound);
            if (!wound || !body)
            {
                // These animations overlap; accelerating just one can reorder the
                // body's completion relative to the remaining wound frames.
                Uninstall(harmony, typeof(StoreUIManager), "WoundAnimationRoutine");
                Uninstall(harmony, typeof(StoreUIManager), "BodyFallRoutine");
                Warn("Fast playback: shot-result animations require both compatible iterators");
            }
        }

        private static bool Install(Harmony harmony, Type owner, string name, Shape shape)
        {
            MethodInfo factory = AccessTools.Method(owner, name);
            MethodInfo move = factory == null ? null : AccessTools.EnumeratorMoveNext(factory);
            MethodInfo transpiler = AccessTools.Method(typeof(FastPlaybackTransitions), nameof(Transpiler));
            MethodInfo postfix = AccessTools.Method(typeof(FastPlaybackTransitions),
                shape == Shape.FinishBlackScreen ? nameof(ScopeFinish) : nameof(ScopeResult));
            if (factory == null || move == null || transpiler == null || postfix == null ||
                waitConstructor == null || deltaTime == null || delay == null || scaledDelta == null || finishDelay == null ||
                shape == Shape.Wound && (scope == null || bodyFactory == null || startCoroutine == null))
            {
                Warn("Fast playback: missing transition hook " + owner.Name + "." + name);
                return false;
            }

            shapes[move] = shape;
            compatible[move] = false;
            try
            {
                harmony.Patch(move, transpiler: new HarmonyMethod(transpiler));
                if (!compatible[move])
                {
                    harmony.Unpatch(move, transpiler);
                    return false;
                }
                harmony.Patch(factory, postfix: new HarmonyMethod(postfix));
                return true;
            }
            catch (Exception error)
            {
                // A factory without its matching transformed iterator must never be installed.
                try { harmony.Unpatch(factory, postfix); }
                catch (Exception rollback) { Warn("Fast playback transition rollback failed: " + rollback.Message); }
                try { harmony.Unpatch(move, transpiler); }
                catch (Exception rollback) { Warn("Fast playback iterator rollback failed: " + rollback.Message); }
                compatible[move] = false;
                Warn("Fast playback: could not patch " + owner.Name + "." + name + ": " + error.Message);
                return false;
            }
        }

        private static void Uninstall(Harmony harmony, Type owner, string name)
        {
            MethodInfo factory = AccessTools.Method(owner, name);
            MethodInfo move = factory == null ? null : AccessTools.EnumeratorMoveNext(factory);
            try
            {
                if (factory != null) harmony.Unpatch(factory, AccessTools.Method(typeof(FastPlaybackTransitions), nameof(ScopeResult)));
                if (move != null) harmony.Unpatch(move, AccessTools.Method(typeof(FastPlaybackTransitions), nameof(Transpiler)));
            }
            catch (Exception error) { Warn("Fast playback transition rollback failed: " + error.Message); }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            if (!shapes.TryGetValue(__originalMethod, out Shape shape)) return code;
            var waits = Sites(code, instruction => instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, waitConstructor));
            var deltas = Sites(code, instruction => instruction.Calls(deltaTime));
            if (!Matches(code, shape, waits, deltas))
            {
                compatible[__originalMethod] = false;
                Warn("Fast playback: unexpected iterator structure in " + __originalMethod.DeclaringType?.FullName + "; using original timing");
                return code;
            }

            foreach (int site in waits)
            {
                code[site].opcode = OpCodes.Call;
                code[site].operand = shape == Shape.FinishBlackScreen ? finishDelay : delay;
            }
            if (shape == Shape.BlackScreen)
                foreach (int site in deltas) code[site].operand = scaledDelta;
            if (shape == Shape.Wound)
            {
                // BodyFallRoutine is a tiny iterator factory. Mono can inline it in
                // this caller, bypassing the factory postfix, so scope the result at
                // both native StartCoroutine sites as well.
                var calls = Sites(code, instruction => instruction.Calls(bodyFactory));
                for (int i = calls.Count - 1; i >= 0; i--)
                    code.Insert(calls[i] + 1, new CodeInstruction(OpCodes.Call, scope));
            }
            compatible[__originalMethod] = true;
            return code;
        }

        private static List<int> Sites(List<CodeInstruction> code, Func<CodeInstruction, bool> matches)
        {
            var result = new List<int>();
            for (int i = 0; i < code.Count; i++) if (matches(code[i])) result.Add(i);
            return result;
        }

        private static bool Matches(List<CodeInstruction> code, Shape shape, List<int> waits, List<int> deltas)
        {
            switch (shape)
            {
                case Shape.BlackScreen:
                    return waits.Count == 1 && deltas.Count == 2 && FieldBefore(code, waits[0], "waitTime") &&
                        FieldCount(code, "sequenceTime") == 4 && FieldCount(code, "playFullSequence") == 1 &&
                        FieldCount(code, "actionDuringBlack") == 1 && FieldCount(code, "actionAfterBlack") == 1;
                case Shape.FinishBlackScreen:
                    return waits.Count == 3 && deltas.Count == 0 && ConstantBefore(code, waits[0], 0.5f) &&
                        ConstantBefore(code, waits[1], 0.002f) && ConstantBefore(code, waits[2], 0.002f) &&
                        FieldCount(code, "fast") == 1;
                case Shape.CaseOpening:
                    return waits.Count == 4 && deltas.Count == 2 && ConstantBefore(code, waits[0], 1f) &&
                        ConstantBefore(code, waits[1], 0.3f) && GapBefore(code, waits[2]) &&
                        ConstantBefore(code, waits[3], 1.5f) && CallCount(code, "DialogUIManager", "IsLineStillDisplaying") == 2 &&
                        CallCount(code, "StoreClientUniqueList", "DestroyShown") >= 3;
                case Shape.Wound:
                    return waits.Count == 1 && deltas.Count == 0 && FieldBefore(code, waits[0], "woundFrameDuration") &&
                        BodyStartSites(code);
                case Shape.BodyFall:
                    return waits.Count == 1 && deltas.Count == 0 && FieldBefore(code, waits[0], "bodyFrameDuration") &&
                        CallCount(code, "StoreUIManager", "StartShootoutSequence") == 1;
                default: return false;
            }
        }

        private static bool ConstantBefore(List<CodeInstruction> code, int index, float value) =>
            index > 0 && code[index - 1].opcode == OpCodes.Ldc_R4 && code[index - 1].operand is float actual && actual == value;

        private static bool FieldBefore(List<CodeInstruction> code, int index, string name) =>
            index > 0 && code[index - 1].opcode == OpCodes.Ldfld && code[index - 1].operand is FieldInfo field && field.Name == name;

        private static int FieldCount(List<CodeInstruction> code, string name) =>
            code.Count(instruction => instruction.operand is FieldInfo field && field.Name == name);

        private static int CallCount(List<CodeInstruction> code, string type, string name) =>
            code.Count(instruction => instruction.operand is MethodInfo method && method.DeclaringType?.Name == type && method.Name == name);

        private static bool GapBefore(List<CodeInstruction> code, int index)
        {
            if (index < 5 || !(code[index - 1].operand is MethodInfo method) || method.Name != "get_Item") return false;
            return code.Skip(index - 5).Take(5).Any(instruction =>
                instruction.operand is FieldInfo field && field.Name.StartsWith("<gaps>", StringComparison.Ordinal));
        }

        private static bool BodyStartSites(List<CodeInstruction> code)
        {
            if (bodyFactory == null || startCoroutine == null) return false;
            var calls = Sites(code, instruction => instruction.Calls(bodyFactory));
            return calls.Count == 2 && calls.All(index => index + 1 < code.Count &&
                code[index + 1].Calls(startCoroutine) && HasNoLabels(code[index + 1]) &&
                code[index].blocks.Count == 0 && code[index + 1].blocks.Count == 0);
        }

        private static bool HasNoLabels(CodeInstruction instruction)
        {
            var labels = AccessTools.Field(typeof(CodeInstruction), "labels")?.GetValue(instruction) as ICollection;
            return labels != null && labels.Count == 0;
        }

        private static float ScaledDelta() => Time.deltaTime * FastPlayback.Rate;

        private static void ScopeResult(ref IEnumerator __result)
        {
            if (__result != null) __result = FastPlayback.Scope(__result);
        }

        private static void ScopeFinish(ref IEnumerator __result)
        {
            if (__result != null) __result = FastPlayback.Scope(FinishFrames(__result));
        }

        private sealed class FinishWait
        {
            internal readonly float Seconds;
            internal FinishWait(float seconds) { Seconds = seconds; }
        }

        private static object FinishDelay(float seconds) => new FinishWait(seconds);

        // The game's 100 fade steps each yield for 0.002 seconds. A normal Unity
        // coroutine can advance at most one such step per frame, so merely shortening
        // those waits would not make the fade faster. Keep the original iterator and
        // its color writes, but permit multiple visual steps in an accelerated frame.
        private static IEnumerator FinishFrames(IEnumerator original)
        {
            const float stepSeconds = 0.002f;
            int frame = -1;
            float credit = 0f;
            bool waitingForStep = false;
            try
            {
                while (true)
                {
                    if (waitingForStep)
                    {
                        if (frame != Time.frameCount)
                        {
                            frame = Time.frameCount;
                            credit += Mathf.Min(1f, Time.deltaTime / stepSeconds) * Mathf.Clamp(FastPlayback.Rate, 1f, 32f);
                        }
                        if (Time.deltaTime <= 0f || credit < 1f)
                        {
                            yield return null;
                            continue;
                        }
                        credit -= 1f;
                        waitingForStep = false;
                    }
                    if (!original.MoveNext()) yield break;
                    if (original.Current is FinishWait wait)
                    {
                        if (wait.Seconds == stepSeconds)
                        {
                            if (frame < 0)
                            {
                                frame = Time.frameCount;
                                credit = Time.deltaTime > 0f ? Mathf.Max(0f,
                                    Mathf.Min(1f, Time.deltaTime / stepSeconds) * Mathf.Clamp(FastPlayback.Rate, 1f, 32f) - 1f) : 0f;
                            }
                            waitingForStep = true;
                        }
                        else
                        {
                            yield return FastPlayback.Delay(wait.Seconds);
                            frame = -1;
                            credit = 0f;
                        }
                    }
                    else
                    {
                        // If another patch changes the iterator after installation,
                        // allow its native yield instead of interpreting it as a frame.
                        yield return original.Current;
                        frame = -1;
                        credit = 0f;
                    }
                }
            }
            finally { (original as IDisposable)?.Dispose(); }
        }

        internal static void Reset() { }
        internal static void Dispose() { warning = null; }
        private static void Warn(string message) { warning?.Invoke(message); }
    }
}
