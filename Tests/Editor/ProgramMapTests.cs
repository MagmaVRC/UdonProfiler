using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.EditorBindings;
using VRC.Udon.ProgramSources;
using Object = UnityEngine.Object;

namespace MagmaVRC.UdonProfiler.Tests
{
    internal sealed class MapAsm
    {
        public static readonly UdonEditorInterface Editor = new UdonEditorInterface();
        public const string IntAdd = "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32";
        public const string IntNeg = "SystemInt32.__op_UnaryMinus__SystemInt32__SystemInt32";
        public const string Switch = "SystemUInt32Array.__Get__SystemInt32__SystemUInt32";

        public IUdonProgram Program;
        public readonly Dictionary<string, uint> Labels = new Dictionary<string, uint>();

        public uint this[string label] => Labels[label];
        public uint Word(string label) => Labels[label] >> 2;
        public uint Slot(string symbol) => Program.SymbolTable.GetAddressFromSymbol(symbol);

        public static int Arity(string sig) => Editor.GetWrapper().GetExternFunctionParameterCount(sig);

        public static MapAsm Build(string data, string code)
        {
            var asm = new MapAsm();
            uint addr = 0;
            foreach (var raw in code.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(".", StringComparison.Ordinal)) continue;
                if (line.EndsWith(":", StringComparison.Ordinal))
                {
                    asm.Labels[line.Substring(0, line.Length - 1)] = addr;
                    continue;
                }
                string op = line.Split(',')[0].Trim();
                addr += op == "NOP" || op == "POP" || op == "COPY" ? 4u : 8u;
            }
            foreach (var kv in asm.Labels) data = data.Replace("{" + kv.Key + "}", "0x" + kv.Value.ToString("X"));
            asm.Program = Editor.Assemble(".data_start\n" + data + "\n.data_end\n.code_start\n" + code + "\n.code_end\n");
            return asm;
        }

        public UdonProgramMap Map(Func<string, int> arity = null) => UdonProgramMap.Build(Program, arity ?? Arity);

        public const string Ret = "PUSH, ret\nCOPY\nJUMP_INDIRECT, ret";

        public static MapAsm CallReturn() => Build(@"
ret: %SystemUInt32, null
a: %SystemInt32, 3
r: %SystemInt32, 0
k1: %SystemUInt32, {r1}
k2: %SystemUInt32, {r2}", $@"
.export _start
_start:
PUSH, k1
c1:
JUMP, add
r1:
PUSH, k2
c2:
JUMP, add
r2:
JUMP, 0xFFFFFFFC
add:
PUSH, r
PUSH, a
PUSH, r
EXTERN, ""{IntAdd}""
PUSH, ret
COPY
rt:
JUMP_INDIRECT, ret");

        public static MapAsm JifTail() => Build(@"
ret: %SystemUInt32, null
c: %SystemBoolean, null
k1: %SystemUInt32, {r1}
k2: %SystemUInt32, {r2}
k3: %SystemUInt32, {r3}", $@"
.export _start
_start:
PUSH, k1
ja:
JUMP, fnA
r1:
PUSH, k2
jb:
JUMP, fnB
r2:
PUSH, k3
jc:
JUMP, fnC
r3:
JUMP, 0xFFFFFFFC
fnA:
PUSH, c
jif:
JUMP_IF_FALSE, fnC
jt:
JUMP, fnB
fnB:
NOP
{Ret}
fnC:
NOP
{Ret}");

        public static MapAsm VarChange() => Build(@"
ret: %SystemUInt32, null
halt: %SystemUInt32, 0xFFFFFFFF
v1: %SystemInt32, 0
v2: %SystemInt32, 0
k1: %SystemUInt32, {r1}", $@"
.export _onVarChange_x
_onVarChange_x:
PUSH, v1
PUSH, v2
COPY
PUSH, v2
PUSH, v1
COPY
PUSH, halt
lbl:
NOP
{Ret}
.export _start
_start:
PUSH, halt
PUSH, k1
JUMP, lbl
r1:
{Ret}");
    }

    public class ProgramMapTests
    {
        readonly List<Object> _objects = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            UdonProgramMap.ClearCache();
            UdonProfilerRecorder.ArityOverride = null;
        }

        [TearDown]
        public void TearDown()
        {
            UdonProfilerRecorder.ArityOverride = null;
            UdonProgramMap.KeepStacks = false;
            UdonProgramMap.ClearCache();
            foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
        }

        static bool Has(UdonProgramMap m, uint word, byte flag) => (m.Flags[word] & flag) != 0;

        static void NoTail(UdonProgramMap m)
        {
            for (int w = 0; w < m.Flags.Length; w++) Assert.AreEqual(0, m.Flags[w] & UdonProgramMap.TailFlag, $"TailFlag at word {w}");
        }

        [Test]
        public void CallReturnClassified()
        {
            var a = MapAsm.CallReturn();
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(UdonProgramMap.CallFlag, m.Flags[a.Word("c1")]);
            Assert.AreEqual(UdonProgramMap.CallFlag, m.Flags[a.Word("c2")]);
            Assert.AreEqual(UdonProgramMap.ReturnFlag, m.Flags[a.Word("rt")]);
            Assert.AreEqual(a.Program.ByteCode.Length / 4, m.Flags.Length);
            Assert.IsTrue(m.IsMethodEntry(a["add"]));
        }

        [Test]
        public void NestedCallInArguments()
        {
            var a = MapAsm.Build(@"
ret: %SystemUInt32, null
x: %SystemInt32, 0
p: %SystemInt32, 0
k1: %SystemUInt32, {r1}
k2: %SystemUInt32, {r2}", $@"
.export _start
_start:
PUSH, k1
PUSH, k2
jg:
JUMP, g
r2:
PUSH, x
PUSH, p
COPY
jf:
JUMP, f
r1:
JUMP, 0xFFFFFFFC
g:
{MapAsm.Ret}
f:
{MapAsm.Ret}");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.IsTrue(Has(m, a.Word("jg"), UdonProgramMap.CallFlag));
            Assert.IsTrue(Has(m, a.Word("jf"), UdonProgramMap.CallFlag));
            Assert.IsFalse(Has(m, a.Word("jf"), UdonProgramMap.HeapCallFlag));
        }

        [Test]
        public void EventSentinelEntries()
        {
            var a = MapAsm.Build(@"
ret: %SystemUInt32, null
halt: %SystemUInt32, 0xFFFFFFFF", $@"
.export _e
_e:
PUSH, halt
NOP
{MapAsm.Ret}");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            CollectionAssert.Contains(m.MethodEntries, a["_e"]);
            CollectionAssert.Contains(m.MethodEntries, a["_e"] + 8);
            Assert.AreEqual(a["_e"] + 8, m.BodyLabel(a["_e"]));
        }

        [Test]
        public void SwitchNotFlaggedAndFallbackCoversUnreached()
        {
            var a = MapAsm.Build(@"
ret: %SystemUInt32, null
table: %SystemUInt32Array, null
idx: %SystemInt32, 0
outv: %SystemUInt32, 0
k: %SystemUInt32, {back}", $@"
.export _start
_start:
PUSH, table
PUSH, idx
PUSH, outv
EXTERN, ""{MapAsm.Switch}""
sw:
JUMP_INDIRECT, outv
case0:
PUSH, k
cj:
JUMP, f
back:
JUMP, 0xFFFFFFFC
f:
{MapAsm.Ret}");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(0, m.Flags[a.Word("sw")]);
            Assert.AreEqual(UdonProgramMap.CallFlag | UdonProgramMap.UnverifiedFlag, m.Flags[a.Word("cj")]);
            NoTail(m);
        }

        [Test]
        public void QuicTailCall()
        {
            var a = MapAsm.Build(@"
ret: %SystemUInt32, null
k1: %SystemUInt32, {r1}
k2: %SystemUInt32, {r2}", $@"
.export _start
_start:
PUSH, k1
ja:
JUMP, fnA
r1:
PUSH, k2
jb:
JUMP, fnB
r2:
JUMP, 0xFFFFFFFC
fnA:
NOP
jt:
JUMP, fnB
fnB:
NOP
{MapAsm.Ret}");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(UdonProgramMap.TailFlag, m.Flags[a.Word("jt")]);
            Assert.AreEqual(UdonProgramMap.CallFlag, m.Flags[a.Word("jb")]);
        }

        [Test]
        public void ThreadedJifTail()
        {
            var a = MapAsm.JifTail();
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(UdonProgramMap.TailFlag, m.Flags[a.Word("jif")]);
            Assert.AreEqual(UdonProgramMap.TailFlag, m.Flags[a.Word("jt")]);
        }

        [Test]
        public void BackEdgesNotTails()
        {
            var a = MapAsm.Build(@"
ret: %SystemUInt32, null
halt: %SystemUInt32, 0xFFFFFFFF
c: %SystemBoolean, null
k1: %SystemUInt32, {r1}
k2: %SystemUInt32, {r2}", $@"
.export _start
_start:
PUSH, halt
loop:
NOP
PUSH, k1
JUMP, M
r1:
PUSH, k2
JUMP, O
r2:
PUSH, c
JUMP_IF_FALSE, done
back1:
JUMP, loop
done:
{MapAsm.Ret}
O:
NOP
{MapAsm.Ret}
M:
NOP
PUSH, c
JUMP_IF_FALSE, jo
back2:
JUMP, M
jo:
JUMP, O");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(0, m.Flags[a.Word("back1")]);
            Assert.AreEqual(0, m.Flags[a.Word("back2")]);
            Assert.AreEqual(UdonProgramMap.TailFlag, m.Flags[a.Word("jo")]);
        }

        [Test]
        public void CopiedTrampolines()
        {
            var a = MapAsm.Build(@"
ret: %SystemUInt32, null
c: %SystemBoolean, null
k1: %SystemUInt32, {r1}", @"
.export _start
_start:
PUSH, k1
JUMP, f
r1:
JUMP, 0xFFFFFFFC
f:
PUSH, c
JUMP_IF_FALSE, second
PUSH, ret
COPY
rt1:
JUMP_INDIRECT, ret
second:
PUSH, ret
COPY
rt2:
JUMP_INDIRECT, ret");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(UdonProgramMap.ReturnFlag, m.Flags[a.Word("rt1")]);
            Assert.AreEqual(UdonProgramMap.ReturnFlag, m.Flags[a.Word("rt2")]);
        }

        [Test]
        public void LuauHeapCall()
        {
            var a = MapAsm.Build(@"
back: %SystemUInt32, {bk}
slot: %SystemUInt32, 0", @"
.export _start
_start:
PUSH, back
PUSH, slot
COPY
hj:
JUMP, f
bk:
JUMP, 0xFFFFFFFC
f:
NOP
fr:
JUMP_INDIRECT, slot");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(UdonProgramMap.HeapCallFlag, m.Flags[a.Word("hj")]);
            Assert.AreEqual(UdonProgramMap.ReturnFlag, m.Flags[a.Word("fr")]);
            Assert.IsNotNull(m.HeapSlots);
            Assert.AreEqual(a.Slot("slot"), m.HeapSlots[a.Word("hj")]);
        }

        [Test]
        public void LuauHeapCallWithSeparatedPushes()
        {
            var a = MapAsm.Build(@"
back: %SystemUInt32, {bk}
slot: %SystemUInt32, 0
x: %SystemInt32, 0
y: %SystemInt32, 0", @"
.export _start
_start:
PUSH, back
PUSH, x
PUSH, y
COPY
PUSH, slot
COPY
hj:
JUMP, f
bk:
JUMP, 0xFFFFFFFC
f:
NOP
fr:
JUMP_INDIRECT, slot");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(UdonProgramMap.HeapCallFlag, m.Flags[a.Word("hj")]);
            Assert.AreEqual(a.Slot("slot"), m.HeapSlots[a.Word("hj")]);
        }

        [Test]
        public void LuauCoroutineResume()
        {
            var a = MapAsm.Build(@"
ret: %SystemUInt32, null
c: %SystemBoolean, null
back: %SystemUInt32, {bk}
resumeAt: %SystemUInt32, 0
bodyc: %SystemUInt32, {body}
yieldc: %SystemUInt32, {yl}
k1: %SystemUInt32, {r1}", $@"
.export _start
_start:
NOP
PUSH, bodyc
PUSH, resumeAt
COPY
PUSH, back
rs:
JUMP_INDIRECT, resumeAt
bk:
JUMP, 0xFFFFFFFC
body:
PUSH, k1
jc:
JUMP, g
r1:
PUSH, yieldc
PUSH, resumeAt
COPY
PUSH, ret
COPY
yr:
JUMP_INDIRECT, ret
yl:
PUSH, c
JUMP_IF_FALSE, done
lp:
JUMP, body
done:
{MapAsm.Ret}
g:
NOP
{MapAsm.Ret}");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(UdonProgramMap.CallFlag, m.Flags[a.Word("rs")]);
            Assert.AreEqual(UdonProgramMap.CallFlag, m.Flags[a.Word("jc")]);
            Assert.AreEqual(UdonProgramMap.ReturnFlag, m.Flags[a.Word("yr")]);
            Assert.IsTrue(m.IsResumeTarget(a["body"]));
            Assert.IsTrue(m.IsResumeTarget(a["yl"]));
            CollectionAssert.DoesNotContain(m.MethodEntries, a["body"]);
            CollectionAssert.DoesNotContain(m.MethodEntries, a["yl"]);
            Assert.AreEqual(0, m.Flags[a.Word("lp")] & UdonProgramMap.TailFlag);
            StringAssert.StartsWith("resume_0x", m.Method(a["yl"]).name);
        }

        static MapAsm Dispatch(bool otherFirst)
        {
            string other = @"
.export _other
_other:
PUSH, k3
JUMP, f
r3:
JUMP, 0xFFFFFFFC";
            string handler = @"
.export _h
_h:
PUSH, dispc
JUMP, f";
            return MapAsm.Build(@"
ret: %SystemUInt32, null
dispc: %SystemUInt32, {disp}
k2: %SystemUInt32, {r2}
k3: %SystemUInt32, {r3}", (otherFirst ? other + handler : handler + other) + $@"
f:
NOP
{MapAsm.Ret}
disp:
PUSH, k2
jd:
JUMP, g
r2:
JUMP, 0xFFFFFFFC
g:
NOP
{MapAsm.Ret}");
        }

        [Test]
        public void LuauDispatchReached([Values(false, true)] bool otherFirst)
        {
            var a = Dispatch(otherFirst);
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(UdonProgramMap.CallFlag, m.Flags[a.Word("jd")]);
        }

        [Test]
        public void MergeReenqueuesDeeperSlots()
        {
            UdonProgramMap.KeepStacks = true;
            var a = MapAsm.Build(@"
ret: %SystemUInt32, null
c: %SystemBoolean, null
a1: %SystemInt32, 0
a2: %SystemInt32, 0
b: %SystemInt32, 0", @"
.export _start
_start:
PUSH, c
JUMP_IF_FALSE, other
PUSH, a1
PUSH, b
JUMP, blk
other:
PUSH, a2
PUSH, b
JUMP, blk
blk:
NOP
POP
PUSH, ret
cp:
COPY
JUMP_INDIRECT, ret");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(-1, m.StackSecondAt(a["cp"]));
        }

        [Test]
        public void BalancedExternsBeforeCall()
        {
            var a = MapAsm.Build(@"
ret: %SystemUInt32, null
x: %SystemInt32, 1
y: %SystemInt32, 2
z: %SystemInt32, 0
k1: %SystemUInt32, {r1}", $@"
.export _start
_start:
PUSH, k1
PUSH, x
PUSH, y
PUSH, z
EXTERN, ""{MapAsm.IntAdd}""
jc:
JUMP, f
r1:
JUMP, 0xFFFFFFFC
f:
{MapAsm.Ret}");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            Assert.AreEqual(UdonProgramMap.CallFlag, m.Flags[a.Word("jc")]);
        }

        [Test]
        public void UnknownArityFallsBack()
        {
            var a = MapAsm.Build(@"
ret: %SystemUInt32, null
r: %SystemInt32, 0
k1: %SystemUInt32, {r1}
k2: %SystemUInt32, {r2}", $@"
.export _start
_start:
PUSH, r
PUSH, r
PUSH, r
EXTERN, ""{MapAsm.IntAdd}""
PUSH, k1
c1:
JUMP, fnA
r1:
PUSH, k2
JUMP, fnB
r2:
JUMP, 0xFFFFFFFC
fnA:
NOP
JUMP, fnB
fnB:
NOP
{MapAsm.Ret}");
            var m = a.Map(_ => -1);
            Assert.IsFalse(m.Simulated);
            Assert.AreEqual(UdonProgramMap.CallFlag | UdonProgramMap.UnverifiedFlag, m.Flags[a.Word("c1")]);
            NoTail(m);
        }

        [Test]
        public void GraphLikeProgramHasNoFlags()
        {
            var a = MapAsm.Build(@"
c: %SystemBoolean, null", @"
.export _start
_start:
NOP
PUSH, c
JUMP_IF_FALSE, l
NOP
l:
JUMP, 0xFFFFFFFC");
            var m = a.Map();
            Assert.IsTrue(m.Simulated);
            foreach (byte f in m.Flags) Assert.AreEqual(0, f);
            Assert.AreEqual(0, m.SiteCount);
        }

        [Test]
        public void PlainLoopNotFlagged()
        {
            var a = MapAsm.Build(@"
c: %SystemBoolean, null", @"
.export _start
_start:
NOP
loop:
PUSH, c
JUMP_IF_FALSE, done
bk:
JUMP, loop
done:
JUMP, 0xFFFFFFFC");
            var m = a.Map();
            Assert.AreEqual(0, m.Flags[a.Word("bk")]);
        }

        [Test]
        public void BodyLabels()
        {
            var v = MapAsm.VarChange();
            var mv = v.Map();
            Assert.IsTrue(mv.Simulated);
            Assert.AreEqual(v["lbl"], mv.BodyLabel(v["_onVarChange_x"]));

            var e = MapAsm.Build(@"
ret: %SystemUInt32, null
halt: %SystemUInt32, 0xFFFFFFFF", $@"
.export _e
_e:
PUSH, halt
NOP
{MapAsm.Ret}");
            Assert.AreEqual(e["_e"] + 8, e.Map().BodyLabel(e["_e"]));

            var s = MapAsm.Build(@"
ret: %SystemUInt32, null
halt: %SystemUInt32, 0xFFFFFFFF
k1: %SystemUInt32, {r1}", $@"
.export _s
_s:
PUSH, halt
JUMP, M
M:
NOP
{MapAsm.Ret}
.export _start
_start:
PUSH, halt
PUSH, k1
JUMP, M
r1:
{MapAsm.Ret}");
            var ms = s.Map();
            Assert.IsTrue(ms.IsMethodEntry(s["M"]));
            Assert.AreNotEqual(s["M"], ms.BodyLabel(s["_s"]));
            Assert.AreEqual(s["_s"] + 8, ms.BodyLabel(s["_s"]));
            Assert.AreEqual(s["M"], ms.BodyLabel(s["M"]));
        }

        [Test]
        public void SiteIndexIsDense()
        {
            var a = MapAsm.JifTail();
            var m = a.Map();
            int expected = 0;
            for (int w = 0; w < m.Flags.Length; w++)
            {
                bool site = (m.Flags[w] & (UdonProgramMap.CallFlag | UdonProgramMap.TailFlag | UdonProgramMap.HeapCallFlag)) != 0;
                if (site) Assert.AreEqual(expected++, m.SiteIndex[w], $"word {w}");
                else Assert.AreEqual(-1, m.SiteIndex[w], $"word {w}");
            }
            Assert.AreEqual(expected, m.SiteCount);
            Assert.AreEqual(5, m.SiteCount);
        }

        [Test]
        public void UninitialisedExternSlotDoesNotThrow()
        {
            var bytes = new List<byte>();
            void W(uint v)
            {
                bytes.Add((byte)(v >> 24));
                bytes.Add((byte)(v >> 16));
                bytes.Add((byte)(v >> 8));
                bytes.Add((byte)v);
            }
            W(1); W(0);
            W(1); W(0);
            W(1); W(0);
            W(6); W(5);
            W(5); W(0xFFFFFFFC);
            var heap = new UdonHeap(8u);
            heap.SetHeapVariable<int>(0, 1);
            var entries = new UdonSymbolTable(new List<IUdonSymbol> { new UdonSymbol("_start", typeof(object), 0) }, new[] { "_start" });
            var program = new UdonProgram("UDON", 1, bytes.ToArray(), heap, entries, new UdonSymbolTable(), new UdonSyncMetadataTable(), 0);
            UdonProgramMap m = null;
            Assert.DoesNotThrow(() => m = UdonProgramMap.Build(program, MapAsm.Arity));
            Assert.IsNotNull(m);
            Assert.IsFalse(m.Simulated);
        }

        [Test]
        public void CachesShareAnalysisNotNames()
        {
            int managersBefore = Object.FindObjectsByType<UdonManager>(FindObjectsSortMode.None).Length;
            UdonProfilerRecorder.ArityOverride = MapAsm.Arity;

            var asset1 = ScriptableObject.CreateInstance<SerializedUdonProgramAsset>();
            var asset2 = ScriptableObject.CreateInstance<SerializedUdonProgramAsset>();
            _objects.Add(asset1);
            _objects.Add(asset2);

            var p1 = MapAsm.CallReturn().Program;
            var p2 = MapAsm.CallReturn().Program;
            var p3 = MapAsm.CallReturn().Program;
            var m1 = UdonProgramMap.For(null, p1, asset1);
            var m2 = UdonProgramMap.For(null, p2, asset1);
            var m3 = UdonProgramMap.For(null, p3, asset2);
            Assert.IsNotNull(m1);
            Assert.AreSame(m1, m2);
            Assert.AreNotSame(m1, m3);
            Assert.AreSame(m1.Flags, m3.Flags);

            string Source(string sig) => $@"
.data_start
    r: %SystemInt32, 0
.data_end
.code_start
    .export _start
    _start:
    PUSH, r
    PUSH, r
    PUSH, r
    EXTERN, ""{sig}""
    JUMP, 0xFFFFFFFC
.code_end
";
            var add = UdonProgramMap.For(MapAsm.Editor.Assemble(Source(MapAsm.IntAdd)));
            var neg = UdonProgramMap.For(MapAsm.Editor.Assemble(Source(MapAsm.IntNeg)));
            Assert.IsNotNull(add);
            Assert.IsNotNull(neg);
            Assert.AreNotSame(add.Flags, neg.Flags);

            var go = new GameObject("MapTestBehaviour") { hideFlags = HideFlags.HideAndDontSave };
            _objects.Add(go);
            var ub = go.AddComponent<UdonBehaviour>();
            var source = ScriptableObject.CreateInstance<VRC.Udon.Editor.ProgramSources.UdonProgramAsset>();
            _objects.Add(source);
            ub.programSource = source;
            var p4 = MapAsm.CallReturn().Program;
            var exportsOnly = UdonProgramMap.For(p4);
            var bound = UdonProgramMap.For(ub, p4, null);
            Assert.AreNotSame(exportsOnly, bound);
            Assert.AreSame(exportsOnly.Flags, bound.Flags);
            Assert.AreSame(bound, UdonProgramMap.For(p4));

            int managersAfter = Object.FindObjectsByType<UdonManager>(FindObjectsSortMode.None).Length;
            Assert.AreEqual(managersBefore, managersAfter, "an UdonManager was created");
            if (!Application.isPlaying) Assert.AreEqual(0, managersAfter);
        }

        [Test]
        public void NamingFallsBackToExports()
        {
            var a = MapAsm.CallReturn();
            var m = a.Map();
            m.ResolveNames();
            Assert.AreEqual("exports", m.NamingSource);
            Assert.AreEqual("_start", m.Method(a["_start"]).name);
            Assert.AreEqual("sub_0x" + a["add"].ToString("X"), m.Method(a["add"]).name);
        }
    }
}
