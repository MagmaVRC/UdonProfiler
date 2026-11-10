using System;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRC.Udon;
using R = MagmaVRC.UdonProfiler.UdonProfilerRecorder;
using Object = UnityEngine.Object;

namespace MagmaVRC.UdonProfiler.Tests
{
    public class RecorderStackTests
    {
        const int B1 = 1001, B2 = 1002, B3 = 1003;
        const uint E = 0x100, F = 0x400, MA = 0x200, MB = 0x300, MC = 0x340;

        int _frame;
        BehaviourInfo _i1, _i2, _i3;
        bool _recording, _methodTiming, _countFused, _spans, _externTiming, _detailed, _markers, _trackAlloc;
        int _maxSpans, _maxStats;
        double _pause;

        static BehaviourInfo Info(string script) => new BehaviourInfo { script = script, objectName = script, objectPath = script, objectKey = script };

        [SetUp]
        public void SetUp()
        {
            _recording = R.Recording;
            _methodTiming = R.MethodTiming;
            _countFused = R.CountFusedExterns;
            _spans = R.MethodSpans;
            _externTiming = R.ExternTiming;
            _detailed = R.DetailedExterns;
            _markers = R.EmitMarkers;
            _trackAlloc = R.TrackAlloc;
            _maxSpans = R.MaxSpansPerFrame;
            _maxStats = R.MaxStatsPerFrame;
            _pause = R.PauseAboveMs;

            R.Recording = false;
            R.Clear();
            R.SetProbeCosts(0, 0, 0, 0);
            R.MethodTiming = true;
            R.CountFusedExterns = true;
            R.MethodSpans = true;
            R.ExternTiming = true;
            R.DetailedExterns = true;
            R.EmitMarkers = true;
            R.TrackAlloc = true;
            R.MaxSpansPerFrame = 2048;
            R.MaxStatsPerFrame = 4096;
            R.PauseAboveMs = 0;
            R.PausedOnFrame = -1;
            RegisterAll();
            _frame = 1;
            R.FrameSource = () => _frame;
            R.Recording = true;
        }

        void RegisterAll()
        {
            _i1 = Info("ScriptA");
            _i2 = Info("ScriptB");
            _i3 = Info("ScriptC");
            R.RegisterLoadedBehaviour(B1, _i1);
            R.RegisterLoadedBehaviour(B2, _i2);
            R.RegisterLoadedBehaviour(B3, _i3);
        }

        [TearDown]
        public void TearDown()
        {
            R.Recording = false;
            R.Clear();
            R.FrameSource = null;
            R.InstructionSource = null;
            R.Viewers = 0;
            R.ProbesOutsidePlayMode = false;
            R.MethodTiming = _methodTiming;
            R.CountFusedExterns = _countFused;
            R.MethodSpans = _spans;
            R.ExternTiming = _externTiming;
            R.DetailedExterns = _detailed;
            R.EmitMarkers = _markers;
            R.TrackAlloc = _trackAlloc;
            R.MaxSpansPerFrame = _maxSpans;
            R.MaxStatsPerFrame = _maxStats;
            R.PauseAboveMs = _pause;
            R.PausedOnFrame = -1;
            R.SetProbeCosts(0, 0, 0, 0);
            R.ResetCalibration();
            R.Recording = _recording;
            R.SyncProbes();
        }

        static void Spin(double us = 50)
        {
            long until = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * us / 1e6);
            while (Stopwatch.GetTimestamp() < until) { }
        }

        static Frame Cur => R.GetFrame(R.NewestFrame);

        static int Find(int parent, PathKind kind, int b, uint key) => R.Paths.Find(parent, kind, b, key);

        static bool TryStat(Frame f, int path, out PathStat st)
        {
            for (int i = 0; i < f.statCount; i++)
            {
                if (f.stats[i].path != path) continue;
                st = f.stats[i];
                return true;
            }
            st = default;
            return false;
        }

        static PathStat Stat(Frame f, int path)
        {
            Assert.IsTrue(TryStat(f, path, out var st), $"no stat for path {path}");
            return st;
        }

        static long SelfSum(Frame f)
        {
            long s = 0;
            for (int i = 0; i < f.statCount; i++)
                if (R.Paths[f.stats[i].path].kind != PathKind.Extern) s += f.stats[i].selfTicks;
            return s;
        }

        static int CountPaths(PathKind kind, int behaviourId)
        {
            int n = 0;
            for (int p = 0; p < R.Paths.Count; p++)
                if (R.Paths[p].kind == kind && R.Paths[p].behaviourId == behaviourId) n++;
            return n;
        }

        static void Call(uint pc, uint target) => R.OnCall(pc, target);
        static void Return(uint callPc) => R.OnReturn(0x7000, callPc + 8);

        static void Extern(string sig, int[] site, double us = 10)
        {
            Assert.IsTrue(R.ExternBegin(out long start, out long child));
            Spin(us);
            R.ExternEnd(R.SigId(sig), start, child, site);
        }

        static int[] NewSite() => new[] { -1, -1, -1 };

        [Test]
        public void CallsNestUnderEvent()
        {
            int s = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            Spin();
            Call(0x40, MB);
            Spin();
            Return(0x40);
            Return(0x10);
            Pop(s);
            var f = Cur;
            int e = Find(-1, PathKind.Event, B1, E);
            int a = Find(e, PathKind.Method, B1, MA);
            int b = Find(a, PathKind.Method, B1, MB);
            Assert.GreaterOrEqual(e, 0);
            Assert.GreaterOrEqual(a, 0);
            Assert.GreaterOrEqual(b, 0);
            long ei = f.samples[s].inclusiveTicks, ai = Stat(f, a).inclusiveTicks, bi = Stat(f, b).inclusiveTicks;
            Assert.GreaterOrEqual(ei, ai);
            Assert.GreaterOrEqual(ai, bi);
            Assert.Greater(bi, 0);
            Assert.AreEqual(f.totalTicks, SelfSum(f));
            Assert.AreEqual(0, R.StackDepth);
        }

        static void Pop(int s) => R.Pop(s, false);

        [Test]
        public void InstructionCountsInclusiveAndSelf()
        {
            long ic = 1000;
            R.InstructionSource = () => ic;
            int s0 = R.PushCore(B1, _i1, E);
            ic += 10;
            Call(0x10, MA);
            ic += 20;
            Call(0x40, MB);
            ic += 5;
            Return(0x40);
            ic += 3;
            Return(0x10);
            ic += 2;
            int s1 = R.PushCore(B2, _i2, F);
            ic += 7;
            Pop(s1);
            ic += 1;
            Pop(s0);
            var f = Cur;
            Assert.IsTrue(f.hasInstructions);
            int e = Find(-1, PathKind.Event, B1, E);
            int a = Find(e, PathKind.Method, B1, MA);
            int b = Find(a, PathKind.Method, B1, MB);
            int n = Find(e, PathKind.Event, B2, F);
            Assert.AreEqual(48, f.samples[s0].instructions);
            Assert.AreEqual(7, f.samples[s0].childInstructions);
            Assert.AreEqual(7, f.samples[s1].instructions);
            Assert.AreEqual(48, f.totalInstructions);
            Assert.AreEqual(48, Stat(f, e).instructions);
            Assert.AreEqual(13, Stat(f, e).selfInstructions);
            Assert.AreEqual(28, Stat(f, a).instructions);
            Assert.AreEqual(23, Stat(f, a).selfInstructions);
            Assert.AreEqual(5, Stat(f, b).instructions);
            Assert.AreEqual(5, Stat(f, b).selfInstructions);
            Assert.AreEqual(7, Stat(f, n).selfInstructions);
            long self = 0;
            for (int i = 0; i < f.statCount; i++) self += f.stats[i].selfInstructions;
            Assert.AreEqual(f.totalInstructions, self);
            Assert.AreEqual(2, f.spanCount);
            Assert.AreEqual(28, f.spans[0].instructions);
            Assert.AreEqual(5, f.spans[1].instructions);

            var tl = UdonProfilerModel.Timeline(R.NewestFrame);
            Assert.IsTrue(tl.hasInstructions);
            var root = tl.items.Find(x => x.kind == PathKind.Event && x.sample == s0);
            Assert.AreEqual(48, root.instructions);
            Assert.AreEqual(13, root.selfInstructions);
        }

        [Test]
        public void InstructionCountsUnavailableWithoutSource()
        {
            int s = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            Return(0x10);
            Pop(s);
            var f = Cur;
            Assert.IsFalse(f.hasInstructions);
            Assert.AreEqual(0, f.totalInstructions);
            Assert.AreEqual(0, f.samples[s].instructions);
            Assert.IsFalse(UdonProfilerModel.Summary(R.NewestFrame).hasInstructions);
        }

        [Test]
        public void ReturnMatching()
        {
            int s = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            Call(0x40, MB);
            R.OnReturn(0x7000, 0x9990);
            Assert.AreEqual(3, R.StackDepth);
            Return(0x10);
            Assert.AreEqual(1, R.StackDepth);
            Pop(s);
            var f = Cur;
            int a = Find(Find(-1, PathKind.Event, B1, E), PathKind.Method, B1, MA);
            Assert.AreEqual(1, Stat(f, a).calls);
            Assert.AreEqual(1, Stat(f, Find(a, PathKind.Method, B1, MB)).calls);
        }

        [Test]
        public void PopClosesOpenMethods()
        {
            int s = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            Spin();
            Call(0x40, MB);
            Spin();
            Pop(s);
            var f = Cur;
            Assert.AreEqual(0, R.StackDepth);
            Assert.AreEqual(2, f.spanCount);
            Assert.Greater(f.spans[0].ticks, 0);
            Assert.Greater(f.spans[1].ticks, 0);
            Assert.AreEqual(f.totalTicks, SelfSum(f));
        }

        [Test]
        public void TailCalls()
        {
            int s = R.PushCore(B1, _i1, E);
            int e = Find(-1, PathKind.Event, B1, E);
            Call(0x10, MA);
            Spin();
            R.OnTail(0x30, MB);
            Assert.AreEqual(2, R.StackDepth);
            int a = Find(e, PathKind.Method, B1, MA);
            int b = Find(e, PathKind.Method, B1, MB);
            Assert.AreEqual(1, Stat(Cur, a).calls);
            Assert.GreaterOrEqual(b, 0);
            R.OnTail(0x34, MB);
            Assert.AreEqual(2, R.StackDepth);
            R.OnTail(0x60, MC);
            Assert.AreEqual(2, R.StackDepth);
            Return(0x10);
            Assert.AreEqual(1, R.StackDepth);
            Assert.AreEqual(1, Stat(Cur, b).calls);
            Assert.AreEqual(1, Stat(Cur, Find(e, PathKind.Method, B1, MC)).calls);
            R.OnTail(0x80, E);
            R.OnTail(0x80, E + 8);
            Assert.AreEqual(1, R.StackDepth);
            Pop(s);
            Assert.AreEqual(Cur.totalTicks, SelfSum(Cur));
        }

        [Test]
        public void TailToBodyLabelIgnored()
        {
            var asm = MapAsm.VarChange();
            _i1.map = asm.Map();
            uint export = asm["_onVarChange_x"];
            uint label = asm["lbl"];
            Assert.AreEqual(label, _i1.map.BodyLabel(export));
            int s = R.PushCore(B1, _i1, export);
            R.OnTail(0x80, label);
            Assert.AreEqual(1, R.StackDepth);
            R.OnTail(0x80, export + 8);
            Assert.AreEqual(1, R.StackDepth);
            R.OnTail(0x88, MA);
            Assert.AreEqual(2, R.StackDepth);
            Pop(s);
        }

        [Test]
        public void DeepRecursionFolds()
        {
            int s = R.PushCore(B1, _i1, E);
            for (int i = 0; i < 100; i++) Call(0x10, MA);
            Spin();
            for (int i = 0; i < 100; i++) Return(0x10);
            Assert.AreEqual(1, R.StackDepth);
            Pop(s);
            var f = Cur;
            Assert.AreEqual(PathTable.MaxDepth, CountPaths(PathKind.Method, B1));
            int calls = 0;
            for (int i = 0; i < f.statCount; i++)
                if (R.Paths[f.stats[i].path].kind == PathKind.Method) calls += f.stats[i].calls;
            Assert.AreEqual(PathTable.MaxDepth, calls);
            Assert.AreEqual(f.totalTicks, SelfSum(f));
        }

        [Test]
        public void NestedEventInsideMethod()
        {
            int s0 = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            int s1 = R.PushCore(B2, _i2, F);
            Spin();
            Pop(s1);
            Return(0x10);
            Pop(s0);
            var f = Cur;
            int a = Find(Find(-1, PathKind.Event, B1, E), PathKind.Method, B1, MA);
            Assert.AreEqual(s0, f.samples[s1].parent);
            Assert.AreEqual(a, R.Paths[f.samples[s1].path].parent);
            Assert.IsFalse(f.samples[s1].folded);
            var sum = UdonProfilerModel.Summary(R.NewestFrame);
            Assert.IsNotNull(sum);
            bool found = false;
            for (int i = 0; i < sum.edgeCount; i++)
            {
                var ed = sum.edges[i];
                if (ed.toBehaviour == B2 && ed.toEntry == F)
                {
                    found = true;
                    Assert.AreEqual(B1, ed.fromBehaviour);
                    Assert.AreEqual(E, ed.fromEntry);
                    Assert.IsFalse(ed.delayed);
                }
            }
            Assert.IsTrue(found);
            Assert.AreEqual(f.totalTicks, SelfSum(f));
        }

        [Test]
        public void ExternPaths()
        {
            int s = R.PushCore(B1, _i1, E);
            int e = Find(-1, PathKind.Event, B1, E);
            Call(0x10, MA);
            int a = Find(e, PathKind.Method, B1, MA);
            Extern("Test.__timed__SystemVoid", NewSite());
            int timed = Find(a, PathKind.Extern, B1, (uint)R.SigId("Test.__timed__SystemVoid"));
            Assert.GreaterOrEqual(timed, 0);
            Assert.AreEqual(1, Stat(Cur, timed).calls);

            int fusedSite = R.RegisterFusedSite("Test.__fused__SystemVoid");
            R.OnFusedExtern(fusedSite);
            int fused = Find(a, PathKind.Extern, B1, (uint)R.SigId("Test.__fused__SystemVoid"));
            Assert.GreaterOrEqual(fused, 0);
            Assert.AreEqual(1, Stat(Cur, fused).fusedCount);
            Assert.AreEqual(1, Stat(Cur, fused).calls);

            for (int i = 0; i < 300; i++) R.OnFusedExtern(R.RegisterFusedSite("Test.__many" + i + "__SystemVoid"));
            Assert.IsTrue(R.CountFusedExterns);
            Assert.IsTrue(R.DetailedExterns);
            Return(0x10);

            int siteA = R.RegisterFusedSite("Test.__shared__SystemVoid");
            int siteB = R.RegisterFusedSite("Test.__shared__SystemVoid");
            Call(0x20, MA);
            R.OnFusedExtern(siteA);
            Return(0x20);
            Call(0x30, MB);
            R.OnFusedExtern(siteB);
            Return(0x30);
            int count = R.Paths.Count;
            Call(0x20, MA);
            R.OnFusedExtern(siteA);
            Return(0x20);
            Call(0x30, MB);
            R.OnFusedExtern(siteB);
            Return(0x30);
            Assert.AreEqual(count, R.Paths.Count);
            Pop(s);
        }

        [Test]
        public void SharedHelperHitsCaches()
        {
            int s = R.PushCore(B1, _i1, E);
            var timedSite = NewSite();
            int fusedSite = R.RegisterFusedSite("Test.__helperFused__SystemVoid");
            int count = 0, slow = 0;
            for (int i = 0; i < 1000; i++)
            {
                Call(0x10, MA);
                Helper(timedSite, fusedSite);
                Return(0x10);
                Call(0x20, MB);
                Helper(timedSite, fusedSite);
                Return(0x20);
                if (i == 0)
                {
                    count = R.Paths.Count;
                    slow = R.Paths.SlowLookups;
                }
            }
            Assert.AreEqual(count, R.Paths.Count);
            Assert.AreEqual(slow, R.Paths.SlowLookups);
            Pop(s);
        }

        static void Helper(int[] timedSite, int fusedSite)
        {
            const uint HelperTarget = 0x500, Inner = 0x600;
            Call(0x50, HelperTarget);
            Assert.IsTrue(R.ExternBegin(out long start, out long child));
            R.ExternEnd(R.SigId("Test.__helperTimed__SystemVoid"), start, child, timedSite);
            R.OnFusedExtern(fusedSite);
            Call(0x58, Inner);
            Return(0x58);
            Return(0x50);
        }

        [Test]
        public void AutoPauseOncePerSpike()
        {
            R.Recording = false;
            R.Recording = true;
            R.PauseAboveMs = 1e-6;
            Pop(SlowEvent(1));
            Pop(SlowEvent(2));
            _frame = 3;
            Assert.AreEqual(-1, R.PushCore(B1, _i1, E));
            Assert.IsFalse(R.Recording);
            Assert.AreEqual(1, R.PausedOnFrame);
            int spikeSerial = R.PausedOnFrame;
            Assert.IsTrue(R.GetFrame(spikeSerial).isSealed);

            R.Recording = true;
            _frame = 4;
            int s = R.PushCore(B1, _i1, E);
            Assert.GreaterOrEqual(s, 0);
            Spin();
            Pop(s);
            _frame = 5;
            int t = R.PushCore(B1, _i1, E);
            Assert.GreaterOrEqual(t, 0);
            Assert.IsTrue(R.Recording);
            Assert.AreEqual(spikeSerial, R.PausedOnFrame);
            Pop(t);
        }

        int SlowEvent(int frame)
        {
            _frame = frame;
            int s = R.PushCore(B1, _i1, E);
            Spin();
            return s;
        }

        [Test]
        public void ManualPauseSealsAndResumes()
        {
            R.PauseAboveMs = 1e-6;
            R.Recording = false;
            R.Recording = true;
            _frame = 10;
            Pop(SlowEvent(10));
            Pop(SlowEvent(11));
            Assert.IsTrue(R.Recording);
            int serial = R.NewestFrame;
            int version = R.Version;
            R.Recording = false;
            Assert.IsTrue(R.GetFrame(serial).isSealed);
            Assert.Greater(R.Version, version);
            R.PausedOnFrame = -1;
            R.Recording = true;
            _frame = 12;
            int s = R.PushCore(B1, _i1, E);
            Assert.GreaterOrEqual(s, 0);
            Assert.IsTrue(R.Recording);
            Assert.AreEqual(-1, R.PausedOnFrame);
            Pop(s);

            R.PauseAboveMs = 0;
            int before = R.NewestFrame;
            R.Recording = false;
            R.Recording = true;
            int again = R.PushCore(B1, _i1, E);
            Assert.GreaterOrEqual(again, 0);
            Assert.AreEqual(before + 1, R.NewestFrame);
            Pop(again);
        }

        [Test]
        public void LoadedCaptureThenPush()
        {
            R.ResetForLoad();
            R.CommitLoad(0);
            Assert.IsTrue(R.IsLoadedCapture);
            R.Recording = true;
            Assert.IsFalse(R.IsLoadedCapture);
            RegisterAll();
            int s = R.PushCore(B1, _i1, E);
            Assert.GreaterOrEqual(s, 0);
            Pop(s);
            Assert.AreEqual(1, R.FrameCount);
            Assert.IsFalse(R.IsLoadedCapture);
        }

        [Test]
        public void SpanCap()
        {
            R.MaxSpansPerFrame = 4;
            int s = R.PushCore(B1, _i1, E);
            for (uint i = 0; i < 10; i++)
            {
                Call(0x10 + i * 16, MA + i * 4);
                Return(0x10 + i * 16);
            }
            Pop(s);
            Assert.AreEqual(4, Cur.spanCount);
            Assert.AreEqual(6, Cur.droppedSpans);
        }

        [Test]
        public void StatCapFoldsIntoParent()
        {
            R.MaxStatsPerFrame = 3;
            int s = R.PushCore(B1, _i1, E);
            for (uint i = 0; i < 5; i++)
            {
                Call(0x10 + i * 16, MA + i * 4);
                Spin(10);
                Return(0x10 + i * 16);
            }
            Pop(s);
            var f = Cur;
            Assert.AreEqual(3, f.statCount);
            var ev = Stat(f, Find(-1, PathKind.Event, B1, E));
            Assert.AreEqual(1, ev.calls);
            int methodCalls = 0;
            for (int i = 0; i < f.statCount; i++)
                if (R.Paths[f.stats[i].path].kind == PathKind.Method) methodCalls += f.stats[i].calls;
            Assert.AreEqual(2, methodCalls);
            Assert.AreEqual(f.totalTicks, SelfSum(f));
        }

        static void FillPathTable()
        {
            for (uint k = 0; R.Paths.Count < PathTable.MaxPaths; k++) R.Paths.Child(-1, PathKind.Event, 99999, k, -1);
            R.Paths.Child(-1, PathKind.Event, 99999, 0xFFFFFF00u, -1);
            Assert.IsTrue(R.Paths.Full);
        }

        [Test]
        public void PathTableFullOverflows()
        {
            FillPathTable();
            int s = R.PushCore(B3, _i3, 0x999);
            var f = Cur;
            Assert.AreEqual(PathTable.OverflowPath, f.samples[s].path);
            Assert.IsFalse(f.samples[s].folded);
            Assert.IsTrue(R.PathTableFull);
            Call(0x10, MA);
            Assert.AreEqual(0, f.spanCount);
            int dropped = f.droppedStats;
            var before = Stat(f, PathTable.OverflowPath);
            Extern("Test.__overflow__SystemVoid", NewSite());
            Assert.AreEqual(dropped + 1, f.droppedStats);
            var after = Stat(f, PathTable.OverflowPath);
            Assert.AreEqual(before.calls, after.calls);
            Assert.AreEqual(before.inclusiveTicks, after.inclusiveTicks);
            Return(0x10);
            Pop(s);
            Assert.AreEqual(1, Stat(f, PathTable.OverflowPath).calls);
            Assert.AreEqual(f.totalTicks, SelfSum(f));
        }

        [Test]
        public void FoldedNestedEventFoldsItsMethods()
        {
            int s = R.PushCore(B1, _i1, E);
            for (int i = 0; i < PathTable.MaxDepth; i++) Call(0x10, MA);
            int n = R.PushCore(B2, _i2, F);
            Assert.IsTrue(Cur.samples[n].folded);
            Call(0x20, MB);
            Return(0x20);
            Pop(n);
            Pop(s);
            Assert.AreEqual(0, CountPaths(PathKind.Method, B2));
            Assert.AreEqual(0, CountPaths(PathKind.Event, B2));
            Assert.AreEqual(Cur.totalTicks, SelfSum(Cur));
        }

        [Test]
        public void SiteCachePerBehaviour()
        {
            var asm = MapAsm.CallReturn();
            var map = asm.Map();
            _i1.map = map;
            _i2.map = map;
            uint c1 = asm["c1"], c2 = asm["c2"], add = asm["add"];

            int s = R.PushCore(B1, _i1, E);
            Call(c1, add);
            Return(c1);
            int count = R.Paths.Count;
            Call(c1, add);
            Return(c1);
            Assert.AreEqual(count, R.Paths.Count);
            Pop(s);

            for (int round = 0; round < 3; round++)
            {
                if (round == 1) count = R.Paths.Count;
                foreach (var (b, info) in new[] { (B1, _i1), (B2, _i2) })
                {
                    int t = R.PushCore(b, info, E);
                    Call(c2, add);
                    Return(c2);
                    Pop(t);
                }
            }
            Assert.AreEqual(count, R.Paths.Count);
            Assert.IsNotNull(_i1.sitePath);
            Assert.IsNotNull(_i2.sitePath);

            int u = R.PushCore(B1, _i1, E);
            int e = Find(-1, PathKind.Event, B1, E);
            Call(c1, 0x9000);
            Return(c1);
            Call(c1, 0x9100);
            Return(c1);
            Pop(u);
            int p1 = Find(e, PathKind.Method, B1, 0x9000);
            int p2 = Find(e, PathKind.Method, B1, 0x9100);
            Assert.GreaterOrEqual(p1, 0);
            Assert.GreaterOrEqual(p2, 0);
            Assert.AreNotEqual(p1, p2);
            Assert.AreEqual(0x9000u, R.Paths[p1].key);
            Assert.AreEqual(0x9100u, R.Paths[p2].key);

            R.Clear();
            _i1.map = map;
            R.RegisterLoadedBehaviour(B1, _i1);
            _frame++;
            int v0 = R.PushCore(B1, _i1, 0x7777);
            Pop(v0);
            int v = R.PushCore(B1, _i1, E);
            Call(c1, add);
            int fresh = Find(Find(-1, PathKind.Event, B1, E), PathKind.Method, B1, add);
            Return(c1);
            Pop(v);
            Assert.GreaterOrEqual(fresh, 0);
            Assert.AreEqual(PathKind.Method, R.Paths[fresh].kind);
            Assert.AreEqual(add, R.Paths[fresh].key);
            Assert.AreEqual(1, Stat(Cur, fresh).calls);
        }

        static Type ProbeType => Type.GetType("MagmaVRC.UdonVM.FastUdonVMProbe, MagmaVRC.UdonVM.Editor");

        static object ProbeField(string name) => ProbeType.GetField(name, BindingFlags.Public | BindingFlags.Static).GetValue(null);

        [Test]
        public void ProbeSync()
        {
            if (ProbeType == null) Assert.Ignore("udonvm assembly not loaded");
            try
            {
                R.Viewers = 1;
                R.ProbesOutsidePlayMode = true;
                R.Recording = true;
                UdonProfilerProbe.Bind();
                if (!UdonProfilerProbe.Found) Assert.Ignore("FastUdonVMProbe fields not found");
                R.SyncProbes();
                foreach (var f in new[] { "Call", "Tail", "Return", "Classify", "HeapCallSlots", "Extern", "RegisterExtern" })
                    Assert.IsNotNull(ProbeField(f), f);

                R.Viewers = 0;
                R.SyncProbes();
                foreach (var f in new[] { "Call", "Tail", "Return", "Classify", "HeapCallSlots", "Extern" })
                    Assert.IsNull(ProbeField(f), f);
                Assert.IsNotNull(ProbeField("RegisterExtern"));

                R.Viewers = 1;
                R.ProbesOutsidePlayMode = false;
                R.SyncProbes();
                if (!Application.isPlaying) Assert.IsNull(ProbeField("Call"));
            }
            finally
            {
                R.Viewers = 0;
                R.ProbesOutsidePlayMode = false;
                R.SyncProbes();
            }
        }

        [Test]
        public void PathArraysGrowInsideSamples()
        {
            for (uint k = 0; k < 3000; k++) R.Paths.Child(-1, PathKind.Event, 88888, k, -1);
            int s = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            Extern("Test.__growTimed__SystemVoid", NewSite());
            R.OnFusedExtern(R.RegisterFusedSite("Test.__growFused__SystemVoid"));
            Return(0x10);
            Pop(s);
            Assert.IsTrue(R.MethodTiming);
            Assert.IsTrue(R.DetailedExterns);
            Assert.IsTrue(R.CountFusedExterns);
            int a = Find(Find(-1, PathKind.Event, B1, E), PathKind.Method, B1, MA);
            Assert.Greater(a, 3000);
            Assert.AreEqual(1, Stat(Cur, a).calls);
        }

        [Test]
        public void StatArrayGrowsInsideExterns()
        {
            int s = R.PushCore(B1, _i1, E);
            var f = Cur;
            int i = 0;
            while (f.statCount < f.stats.Length) Extern("Test.__fill" + i++ + "__SystemVoid", NewSite(), 0);
            Extern("Test.__overTimed__SystemVoid", NewSite());
            int timed = Find(Find(-1, PathKind.Event, B1, E), PathKind.Extern, B1, (uint)R.SigId("Test.__overTimed__SystemVoid"));
            Assert.AreEqual(1, Stat(f, timed).calls);
            Assert.Greater(Stat(f, timed).inclusiveTicks, 0);
            while (f.statCount < f.stats.Length) Extern("Test.__fill" + i++ + "__SystemVoid", NewSite(), 0);
            R.OnFusedExtern(R.RegisterFusedSite("Test.__overFused__SystemVoid"));
            int fused = Find(Find(-1, PathKind.Event, B1, E), PathKind.Extern, B1, (uint)R.SigId("Test.__overFused__SystemVoid"));
            Assert.AreEqual(1, Stat(f, fused).fusedCount);
            Assert.AreEqual(1, Stat(f, fused).calls);
            Pop(s);
        }

        [Test]
        public void FramesArePreSized()
        {
            int s = R.PushCore(B1, _i1, E);
            for (int i = 0; i < 299; i++) Extern("Test.__size" + i + "__SystemVoid", NewSite(), 0);
            Pop(s);
            Assert.AreEqual(300, Cur.statCount);
            R.RingSlot(R.NewestFrame + 1).stats = new PathStat[128];
            _frame++;
            int t = R.PushCore(B1, _i1, E);
            Assert.AreEqual(384, Cur.stats.Length);
            Pop(t);

            var frame = new Frame();
            frame.EnsureCapacity(0, 300, 0);
            Assert.AreEqual(384, frame.stats.Length);
            frame.EnsureCapacity(0, 250, 0);
            Assert.AreEqual(384, frame.stats.Length);
            frame.EnsureCapacity(0, 100, 0);
            Assert.AreEqual(128, frame.stats.Length);
            R.MaxStatsPerFrame = 1024;
            frame.EnsureCapacity(0, 5000, 0);
            Assert.AreEqual(1024, frame.stats.Length);
        }

        [Test]
        public void SampleStartPrecedesChildren()
        {
            int s = R.PushCore(B1, _i1, E);
            Spin(5);
            Call(0x10, MA);
            int n = R.PushCore(B2, _i2, F);
            Pop(n);
            Return(0x10);
            Pop(s);
            var f = Cur;
            Assert.Greater(f.samples[s].startTicks, 0);
            Assert.LessOrEqual(f.samples[s].startTicks, f.spans[0].startTicks);
            Assert.Greater(f.samples[n].startTicks, f.samples[s].startTicks);
        }

        [Test]
        public void ScheduledFromMethodUsesEventEntry()
        {
            var go = new GameObject("RecorderTestTarget") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var target = go.AddComponent<UdonBehaviour>();
                int targetId = target.GetInstanceID();
                var info = Info("Target");
                info.events[0x800] = "_x";
                R.RegisterLoadedBehaviour(targetId, info);
                int s = R.PushCore(B1, _i1, E);
                Call(0x10, MA);
                R.Scheduled(target, "_x");
                Return(0x10);
                Pop(s);
                _frame++;
                int t = R.PushCore(targetId, info, 0x800);
                Pop(t);
                Assert.AreEqual(B1, Cur.samples[t].scheduledByBehaviour);
                Assert.AreEqual(E, Cur.samples[t].scheduledByEntry);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void MethodTimingOffMidEvent()
        {
            int s = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            Call(0x40, MB);
            Spin();
            R.MethodTiming = false;
            Assert.AreEqual(1, R.StackDepth);
            var f = Cur;
            Assert.Greater(f.spans[0].ticks, 0);
            Assert.Greater(f.spans[1].ticks, 0);
            Spin();
            Pop(s);
            Assert.AreEqual(f.totalTicks, SelfSum(f));
            int e = Find(-1, PathKind.Event, B1, E);
            int a = Find(e, PathKind.Method, B1, MA);
            Assert.Greater(Stat(f, e).selfTicks, 0);
            Assert.Less(Stat(f, a).inclusiveTicks, f.samples[s].inclusiveTicks);
            R.MethodTiming = true;

            _frame++;
            int o = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            Assert.IsTrue(R.ExternBegin(out long start, out long child));
            Spin();
            int n = R.PushCore(B2, _i2, F);
            Call(0x20, MB);
            R.MethodTiming = false;
            Assert.AreEqual(3, R.StackDepth);
            Pop(n);
            Assert.AreEqual(2, R.StackDepth);
            Spin();
            R.ExternEnd(R.SigId("Test.__send__SystemVoid"), start, child, NewSite());
            Pop(o);
            Assert.AreEqual(0, R.StackDepth);
            var g = Cur;
            int oe = Find(-1, PathKind.Event, B1, E);
            int x = Find(oe, PathKind.Method, B1, MA);
            var xs = Stat(g, x);
            Assert.AreEqual(1, xs.calls);
            Assert.AreEqual(1, xs.externCount);
            Assert.GreaterOrEqual(xs.externTicks, 0);
            Assert.AreEqual(0, Stat(g, oe).externCount);
            Assert.AreEqual(g.totalTicks, SelfSum(g));
        }

        [Test]
        public void DrawnDepthSkipsSpanlessMethods()
        {
            R.MethodSpans = false;
            int s = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            int n = R.PushCore(B2, _i2, F);
            Pop(n);
            Return(0x10);
            Pop(s);
            Assert.AreEqual(0, Cur.samples[s].depth);
            Assert.AreEqual(1, Cur.samples[n].depth);
            Assert.AreEqual(0, Cur.spanCount);
        }

        [Test]
        public void LoadedCaptureClearsBeforeRegister()
        {
            R.ResetForLoad();
            R.CommitLoad(0);
            R.Recording = true;
            Assert.IsFalse(R.IsLoadedCapture);
            Assert.AreEqual(0, R.BehaviourCount);
            var info = Info("Fresh");
            R.RegisterLoadedBehaviour(B1, info);
            int s = R.PushCore(B1, info, E);
            Pop(s);
            Assert.AreSame(info, R.GetInfo(B1));
            Assert.AreEqual(1, R.FrameCount);
        }

        [Test]
        public void ProbeCompensation()
        {
            const long Huge = 1024L * 10_000_000;
            R.SetProbeCosts(Huge, Huge, Huge, Huge);
            int s = R.PushCore(B1, _i1, E);
            for (int i = 0; i < 10; i++)
            {
                Call(0x10, MA);
                Spin(5);
                Return(0x10);
            }
            Pop(s);
            var f = Cur;
            int a = Find(Find(-1, PathKind.Event, B1, E), PathKind.Method, B1, MA);
            Assert.AreEqual(0, Stat(f, a).inclusiveTicks);
            Assert.AreEqual(0, Stat(f, a).selfTicks);
            Assert.AreEqual(0, f.samples[s].inclusiveTicks);
            Assert.AreEqual(0, f.totalTicks);
            Assert.AreEqual(f.totalTicks, SelfSum(f));

            R.SetProbeCosts(0, 0, 0, 0);
            _frame++;
            s = R.PushCore(B1, _i1, E);
            for (int i = 0; i < 10; i++)
            {
                Call(0x10, MA);
                Spin(5);
                Return(0x10);
            }
            Pop(s);
            Assert.Greater(Cur.samples[s].inclusiveTicks, 0);
            Assert.AreEqual(10, Stat(Cur, a).calls);

            R.SetProbeCosts(0, 0, Huge, 0);
            _frame++;
            s = R.PushCore(B1, _i1, E);
            int site = R.RegisterFusedSite("Test.__compFused__SystemVoid");
            Assert.IsTrue(R.ExternBegin(out long start, out long child));
            for (int i = 0; i < 5; i++) R.OnFusedExtern(site);
            Spin(5);
            R.ExternEnd(R.SigId("Test.__compTimed__SystemVoid"), start, child, NewSite());
            Assert.AreEqual(0, Cur.samples[s].externTicks);
            Pop(s);

            R.SetProbeCosts(0, 0, 0, Huge);
            _frame++;
            s = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            Assert.IsTrue(R.ExternBegin(out start, out child));
            int n = R.PushCore(B2, _i2, F);
            Spin(20);
            Pop(n);
            R.ExternEnd(R.SigId("Test.__compSend__SystemVoid"), start, child, NewSite());
            Return(0x10);
            Pop(s);
            var g = Cur;
            int m = Find(Find(-1, PathKind.Event, B1, E), PathKind.Method, B1, MA);
            Assert.AreEqual(0, g.samples[s].externTicks);
            Assert.Greater(g.samples[n].inclusiveTicks, 0);
            Assert.AreEqual(g.samples[n].inclusiveTicks, Stat(g, m).inclusiveTicks);

            R.SetProbeCosts(0, 0, 0, 0);
            _frame++;
            s = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            Assert.IsTrue(R.ExternBegin(out start, out child));
            n = R.PushCore(B2, _i2, F);
            Pop(n);
            Spin(20);
            R.ExternEnd(R.SigId("Test.__compSend__SystemVoid"), start, child, NewSite());
            Return(0x10);
            Pop(s);
            Assert.Greater(Cur.samples[s].externTicks, 0);
        }

        [Test]
        public void CalibrationLeavesRingUntouched()
        {
            int s = R.PushCore(B1, _i1, E);
            Call(0x10, MA);
            Return(0x10);
            Pop(s);
            var current = R.CurrentFrame;
            Assert.IsNotNull(current);
            int samples = current.sampleCount, stats = current.statCount;
            var sample = current.samples[0];
            var stat = current.stats[0];
            R.MethodTiming = false;
            try
            {
                R.Viewers = 1;
                R.ProbesOutsidePlayMode = true;
                R.ResetCalibration();
                R.CalibrateProbes();
                Assert.Greater(R.ProbeCallNs, 0);
                Assert.Greater(R.ProbeEventNs, 0);
                Assert.IsFalse(R.MethodTiming);
                Assert.AreEqual(0, R.StackDepth);
                Assert.AreSame(current, R.CurrentFrame);
                Assert.AreEqual(samples, current.sampleCount);
                Assert.AreEqual(stats, current.statCount);
                Assert.AreEqual(sample.inclusiveTicks, current.samples[0].inclusiveTicks);
                Assert.AreEqual(sample.path, current.samples[0].path);
                Assert.AreEqual(stat.path, current.stats[0].path);
                Assert.AreEqual(stat.calls, current.stats[0].calls);
                Assert.AreEqual(stat.selfTicks, current.stats[0].selfTicks);
            }
            finally
            {
                R.Viewers = 0;
                R.ProbesOutsidePlayMode = false;
                R.SyncProbes();
            }
        }

        [Test]
        public void FoldedFlagOnSelfReentrantEvent()
        {
            var stack = new int[PathTable.MaxDepth + 2];
            for (int i = 0; i < stack.Length; i++) stack[i] = R.PushCore(B1, _i1, E);
            var f = Cur;
            int last = stack[stack.Length - 1];
            Assert.IsTrue(f.samples[last].folded);
            Assert.AreEqual(f.samples[f.samples[last].parent].path, f.samples[last].path);
            Assert.IsFalse(f.samples[stack[0]].folded);
            for (int i = stack.Length - 1; i >= 0; i--) Pop(stack[i]);
            Assert.AreEqual(f.totalTicks, SelfSum(f));
        }

        [Test]
        public void FusedSiteGrowthKeepsCaches()
        {
            int s = R.PushCore(B1, _i1, E);
            int first = R.RegisterFusedSite("Test.__early__SystemVoid");
            R.OnFusedExtern(first);
            for (int i = 0; i < 300; i++) R.RegisterFusedSite("Test.__later" + i + "__SystemVoid");
            int count = R.Paths.Count, slow = R.Paths.SlowLookups;
            R.OnFusedExtern(first);
            Assert.AreEqual(count, R.Paths.Count);
            Assert.AreEqual(slow, R.Paths.SlowLookups);
            Pop(s);
        }
    }
}
