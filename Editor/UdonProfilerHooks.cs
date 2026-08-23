using System;
using System.Reflection;
using HarmonyLib;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common.Delegates;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Wrapper;

namespace MagmaVRC.UdonProfiler
{
    [InitializeOnLoad]
    public static class UdonProfilerHooks
    {
        const string HarmonyId = "dev.magmavrc.udonprofiler";
        public static bool RunProgramPatched { get; private set; }
        public static bool ExternPatched { get; private set; }
        public static bool SchedulerPatched { get; private set; }

        static UdonProfilerHooks()
        {
            UdonProfilerRecorder.MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var programField = AccessTools.Field(typeof(UdonBehaviour), "_program");
            if (programField != null)
                UdonProfilerRecorder.SetProgramGetter(ub => (IUdonProgram)programField.GetValue(ub));

            var harmony = new Harmony(HarmonyId);
            Try("RunProgram", () =>
            {
                var target = AccessTools.Method(typeof(UdonBehaviour), "RunProgram", new[] { typeof(uint) });
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(UdonProfilerHooks), nameof(RunProgramPrefix)),
                    finalizer: new HarmonyMethod(typeof(UdonProfilerHooks), nameof(RunProgramFinalizer)));
                RunProgramPatched = true;
            });
            Try("GetExternFunctionDelegate", () =>
            {
                var target = AccessTools.Method(typeof(UdonWrapper), nameof(UdonWrapper.GetExternFunctionDelegate));
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(UdonProfilerHooks), nameof(ExternPostfix)));
                ExternPatched = true;
            });
            Try("ScheduleDelayedEvent", () =>
            {
                foreach (var m in typeof(UdonManager).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != nameof(UdonManager.ScheduleDelayedEvent)) continue;
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(UdonProfilerHooks), nameof(SchedulePrefix)));
                }
                SchedulerPatched = true;
            });

            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.EnteredPlayMode) UdonProfilerRecorder.Clear();
            };
        }

        static void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Debug.LogWarning($"[UdonProfiler] failed to patch {what}: {e.Message}"); }
        }

        static void RunProgramPrefix(UdonBehaviour __instance, uint entryPoint, out int __state)
        {
            if (__instance == null || __instance.HasError) { __state = -1; return; }
            __state = UdonProfilerRecorder.Push(__instance, entryPoint);
        }

        static void RunProgramFinalizer(UdonBehaviour __instance, int __state)
        {
            if (__state >= 0) UdonProfilerRecorder.Pop(__state, __instance.HasError);
        }

        static void ExternPostfix(string externFunctionSignature, ref UdonExternDelegate __result)
        {
            if (__result == null) return;
            var original = __result;
            string sig = externFunctionSignature;
            __result = (IUdonHeap heap, Span<uint> addresses) =>
            {
                if (!UdonProfilerRecorder.ExternBegin(out long start, out long child))
                {
                    original(heap, addresses);
                    return;
                }
                try { original(heap, addresses); }
                finally { UdonProfilerRecorder.ExternEnd(sig, start, child); }
            };
        }

        static void SchedulePrefix(IUdonEventReceiver eventReceiver, string eventName)
        {
            UdonProfilerRecorder.Scheduled(eventReceiver as UdonBehaviour, eventName);
        }

        internal static string ScriptName(UdonBehaviour ub)
        {
            try
            {
                var t = UdonSharpEditorUtility.GetUdonSharpBehaviourType(ub);
                if (t != null) return t.Name;
            }
            catch { }
            return ub.programSource != null ? ub.programSource.name : "<no program>";
        }
    }
}
