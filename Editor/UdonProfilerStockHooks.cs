using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.VM;
using UdonOpCode = VRC.Udon.VM.Common.OpCode;

namespace MagmaVRC.UdonProfiler
{
    internal static class UdonProfilerStockHooks
    {
        public static volatile bool Enabled;
        public static bool Patched { get; private set; }
        internal static Action<uint, uint, byte> Observer;

        static bool _layoutWarned;

        public static Action Install(Harmony harmony) => () =>
        {
            var target = AccessTools.Method(typeof(VRC.Udon.VM.UdonVM), "Interpret");
            try { harmony.Patch(target, transpiler: new HarmonyMethod(typeof(UdonProfilerStockHooks), nameof(Transpiler))); }
            catch (Exception)
            {
                Patched = false;
                throw;
            }
            string reason = "layout not recognised";
            if (Patched && SelfTest(out reason)) return;
            try { harmony.Unpatch(target, HarmonyPatchType.Transpiler, harmony.Id); }
            catch (Exception) { }
            if (Patched) Debug.LogWarning($"[UdonProfiler] stock-VM hook self-test failed ({reason}); stock-VM method timing off");
            Patched = false;
        };

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var original = new List<CodeInstruction>(instructions);
            var code = new List<CodeInstruction>(original.Count);
            foreach (var c in original) code.Add(new CodeInstruction(c.opcode, c.operand) { labels = new List<Label>(c.labels), blocks = new List<ExceptionBlock>(c.blocks) });

            var check = AccessTools.Method(typeof(VRC.Udon.VM.UdonVM), "CheckJumpTarget");
            var pc = AccessTools.Field(typeof(VRC.Udon.VM.UdonVM), "_programCounter");
            var enabled = AccessTools.Field(typeof(UdonProfilerStockHooks), nameof(Enabled));
            if (check == null || pc == null || enabled == null) return Fail(original);

            int sw = -1;
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode == OpCodes.Switch && code[i].operand is Label[] l && l.Length >= 9)
                {
                    sw = i;
                    break;
                }
            }
            if (sw < 0) return Fail(original);
            var labels = (Label[])code[sw].operand;

            var cases = new (Label label, string hook)[]
            {
                (labels[4], nameof(OnJumpIfFalse)),
                (labels[5], nameof(OnJump)),
                (labels[8], nameof(OnJumpIndirect)),
            };
            var sites = new List<(int k, MethodInfo hook)>(3);
            foreach (var (label, hookName) in cases)
            {
                int start = -1;
                for (int i = 0; i < code.Count; i++)
                {
                    if (code[i].labels.Contains(label))
                    {
                        start = i;
                        break;
                    }
                }
                if (start < 0) return Fail(original);
                int k = -1;
                for (int i = start; i < code.Count && i <= start + 40; i++)
                {
                    if (code[i].opcode == OpCodes.Call && Equals(code[i].operand, check))
                    {
                        k = i;
                        break;
                    }
                }
                if (k < 1 || k + 1 >= code.Count || !IsLdloc(code[k - 1].opcode)) return Fail(original);
                var hook = AccessTools.Method(typeof(UdonProfilerStockHooks), hookName);
                if (hook == null) return Fail(original);
                sites.Add((k, hook));
            }
            sites.Sort((a, b) => b.k.CompareTo(a.k));
            for (int s = 1; s < sites.Count; s++)
                if (sites[s].k == sites[s - 1].k) return Fail(original);

            foreach (var (k, hook) in sites)
            {
                var ld = code[k - 1];
                var skip = il.DefineLabel();
                var next = code[k + 1];
                next.labels.Add(skip);
                code.InsertRange(k + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldsfld, enabled),
                    new CodeInstruction(OpCodes.Brfalse, skip),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld, pc),
                    new CodeInstruction(ld.opcode, ld.operand),
                    new CodeInstruction(OpCodes.Call, hook),
                });
            }
            Patched = true;
            return code;
        }

        static IEnumerable<CodeInstruction> Fail(List<CodeInstruction> original)
        {
            Patched = false;
            if (!_layoutWarned)
            {
                _layoutWarned = true;
                Debug.LogWarning("[UdonProfiler] UdonVM.Interpret layout not recognised; stock-VM method timing off");
            }
            return original;
        }

        static bool IsLdloc(OpCode op) => op == OpCodes.Ldloc || op == OpCodes.Ldloc_S || op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3;

        sealed class SelfTestTime : IUdonVMTimeSource
        {
            public long CurrentTime => 0;
        }

        static bool SelfTest(out string reason)
        {
            bool savedEnabled = Enabled;
            var savedObserver = Observer;
            var seen = new List<(uint pc, uint target, byte kind)>();
            try
            {
                var bytes = new List<byte>(52);
                void Word(uint v)
                {
                    bytes.Add((byte)(v >> 24));
                    bytes.Add((byte)(v >> 16));
                    bytes.Add((byte)(v >> 8));
                    bytes.Add((byte)v);
                }
                Word((uint)UdonOpCode.PUSH); Word(0);
                Word((uint)UdonOpCode.JUMP); Word(32);
                Word((uint)UdonOpCode.PUSH); Word(2);
                Word((uint)UdonOpCode.JUMP_IF_FALSE); Word(0xFFFFFFFCu);
                Word((uint)UdonOpCode.PUSH); Word(1);
                Word((uint)UdonOpCode.COPY);
                Word((uint)UdonOpCode.JUMP_INDIRECT); Word(1);

                var heap = new UdonHeap(3u);
                heap.SetHeapVariable<uint>(0, 16u);
                heap.SetHeapVariable<uint>(1, 0u);
                heap.SetHeapVariable<bool>(2, false);
                var program = new UdonProgram("UDON", 1, bytes.ToArray(), heap, new UdonSymbolTable(), new UdonSymbolTable(), new UdonSyncMetadataTable(), 0);
                var wrapper = (IUdonWrapper)FormatterServices.GetUninitializedObject(typeof(VRC.Udon.Wrapper.UdonWrapper));
                var vm = new VRC.Udon.VM.UdonVM(wrapper, new SelfTestTime());
                if (!vm.LoadProgram(program))
                {
                    reason = "self-test program did not load";
                    return false;
                }
                vm.SetProgramCounter(0);
                Enabled = true;
                Observer = (p, t, k) => seen.Add((p, t, k));
                uint result = vm.Interpret();
                Enabled = savedEnabled;
                Observer = savedObserver;
                var expected = new (uint, uint, byte)[] { (8u, 32u, 0), (44u, 16u, 1), (24u, 0xFFFFFFFCu, 2) };
                bool ok = result == 0 && seen.Count == expected.Length;
                for (int i = 0; ok && i < expected.Length; i++) ok = seen[i] == expected[i];
                reason = ok ? null : $"result {result}, observed {string.Join(", ", seen)}";
                return ok;
            }
            catch (Exception e)
            {
                reason = e.Message;
                return false;
            }
            finally
            {
                Enabled = savedEnabled;
                Observer = savedObserver;
            }
        }

        static bool Lookup(uint pc, out byte f)
        {
            f = 0;
            var map = UdonProfilerRecorder.CurrentMap;
            if (map == null || !map.Simulated) return false;
            uint w = pc >> 2;
            var flags = map.Flags;
            if (flags == null || w >= (uint)flags.Length) return false;
            f = flags[w];
            return f != 0 && (f & UdonProgramMap.UnverifiedFlag) == 0;
        }

        public static void OnJump(uint pc, uint target)
        {
            Observer?.Invoke(pc, target, 0);
            if (!Lookup(pc, out byte f)) return;
            if ((f & (UdonProgramMap.CallFlag | UdonProgramMap.HeapCallFlag)) != 0) UdonProfilerRecorder.OnCall(pc, target);
            else if ((f & UdonProgramMap.TailFlag) != 0) UdonProfilerRecorder.OnTail(pc, target);
        }

        public static void OnJumpIndirect(uint pc, uint target)
        {
            Observer?.Invoke(pc, target, 1);
            if (!Lookup(pc, out byte f)) return;
            if ((f & UdonProgramMap.CallFlag) != 0) UdonProfilerRecorder.OnCall(pc, target);
            else if ((f & UdonProgramMap.ReturnFlag) != 0) UdonProfilerRecorder.OnReturn(pc, target);
        }

        public static void OnJumpIfFalse(uint pc, uint target)
        {
            Observer?.Invoke(pc, target, 2);
            if (Lookup(pc, out byte f) && (f & UdonProgramMap.TailFlag) != 0) UdonProfilerRecorder.OnTail(pc, target);
        }
    }

    internal static class UdonProfilerProbe
    {
        static Type _probe;
        static FieldInfo _classify, _heapSlots, _call, _tail, _return, _register, _extern, _generation;

        static readonly Func<IUdonProgram, byte[]> ClassifyFn = p => UdonProgramMap.For(p)?.Flags;
        static readonly Func<IUdonProgram, uint[]> HeapSlotsFn = p => UdonProgramMap.For(p)?.HeapSlots;
        internal static readonly Action<uint, uint> CallFn = UdonProfilerRecorder.OnCall;
        internal static readonly Action<uint, uint> TailFn = UdonProfilerRecorder.OnTail;
        internal static readonly Action<uint, uint> ReturnFn = UdonProfilerRecorder.OnReturn;
        internal static Func<string, int> RegisterFn = NewRegisterFn();
        internal static readonly Action<int> ExternFn = UdonProfilerRecorder.OnFusedExtern;

        public static bool Found => _probe != null;

        static Func<string, int> NewRegisterFn() =>
            (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), typeof(UdonProfilerRecorder).GetMethod(nameof(UdonProfilerRecorder.RegisterFusedSite), BindingFlags.NonPublic | BindingFlags.Static));

        public static void RenewRegister()
        {
            RegisterFn = NewRegisterFn();
            if (_probe == null) return;
            try
            {
                _register.SetValue(null, RegisterFn);
                _generation.SetValue(null, (int)_generation.GetValue(null) + 1);
            }
            catch (Exception e)
            {
                _probe = null;
                Debug.LogWarning($"[UdonProfiler] Fast VM probe update failed: {e.Message}");
            }
        }

        public static void Bind()
        {
            _probe = null;
            try
            {
                var t = Type.GetType("MagmaVRC.UdonVM.FastUdonVMProbe, MagmaVRC.UdonVM.Editor");
                if (t != null)
                {
                    const BindingFlags F = BindingFlags.Public | BindingFlags.Static;
                    _classify = t.GetField("Classify", F);
                    _heapSlots = t.GetField("HeapCallSlots", F);
                    _call = t.GetField("Call", F);
                    _tail = t.GetField("Tail", F);
                    _return = t.GetField("Return", F);
                    _register = t.GetField("RegisterExtern", F);
                    _extern = t.GetField("Extern", F);
                    _generation = t.GetField("Generation", F);
                    if (_classify != null && _heapSlots != null && _call != null && _tail != null && _return != null && _register != null && _extern != null && _generation != null)
                    {
                        _register.SetValue(null, RegisterFn);
                        _generation.SetValue(null, (int)_generation.GetValue(null) + 1);
                        _probe = t;
                    }
                }
            }
            catch (Exception e)
            {
                _probe = null;
                Debug.LogWarning($"[UdonProfiler] Fast VM probe binding failed: {e.Message}");
            }
            UdonProfilerRecorder.ResetProbeSync();
        }

        public static void Set(bool methods, bool externs)
        {
            if (_probe == null) return;
            try
            {
                _heapSlots.SetValue(null, methods ? HeapSlotsFn : null);
                _classify.SetValue(null, methods ? ClassifyFn : null);
                _call.SetValue(null, methods ? CallFn : null);
                _tail.SetValue(null, methods ? TailFn : null);
                _return.SetValue(null, methods ? ReturnFn : null);
                _extern.SetValue(null, externs ? ExternFn : null);
                _generation.SetValue(null, (int)_generation.GetValue(null) + 1);
            }
            catch (Exception e)
            {
                _probe = null;
                Debug.LogWarning($"[UdonProfiler] Fast VM probe update failed: {e.Message}");
            }
        }
    }
}
