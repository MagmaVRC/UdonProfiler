using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace MagmaVRC.UdonProfiler.Tests
{
    internal static class ModelTestRing
    {
        public static long T(double ms) => (long)Math.Round(ms / UdonProfilerRecorder.MsPerTick);
        public static double Ms(long ticks) => ticks * UdonProfilerRecorder.MsPerTick;
        public static PathTable Paths => UdonProfilerRecorder.Paths;

        public static BehaviourInfo Beh(int id, string script, string obj, params (uint addr, string name)[] events)
        {
            var info = new BehaviourInfo { script = script, objectName = obj, objectPath = "Root/" + obj, objectKey = "Scene:Root[0]/" + obj + "[" + id + "]#c0" };
            foreach (var e in events) info.events[e.addr] = e.name;
            UdonProfilerRecorder.RegisterLoadedBehaviour(id, info);
            return info;
        }

        public static void Name(int b, uint target, string name) => UdonProfilerRecorder.GetInfo(b).methods[target] = new SourceRef { name = name };

        public static int Ev(int parent, int b, uint entry) => Paths.Child(parent, PathKind.Event, b, entry, UdonProfilerRecorder.GetInfo(b)?.scriptId ?? -1);

        public static int Me(int parent, uint target) { var n = Paths[parent]; return Paths.Child(parent, PathKind.Method, n.behaviourId, target, n.scriptId); }

        public static int Ex(int parent, string sig) { var n = Paths[parent]; return Paths.Child(parent, PathKind.Extern, n.behaviourId, (uint)UdonProfilerRecorder.SigId(sig), n.scriptId); }

        public static Frame Frame(int serial)
        {
            var f = UdonProfilerRecorder.LoadFrame(serial);
            f.frameNumber = serial + 1;
            return f;
        }

        public static int Sample(Frame f, int path, int parent, double startMs, double inclMs, double childMs = 0, double extMs = 0, int depth = 0,
            bool folded = false, long alloc = 0, int b = 0, uint entry = 0, int schedB = 0, uint schedE = 0, bool halted = false, long instr = 0)
        {
            var n = Paths[path];
            ref var s = ref f.AddSample(out int i);
            s.instructions = instr;
            if (parent < 0) f.totalInstructions += instr;
            s.behaviourId = b != 0 ? b : n.behaviourId;
            s.entry = b != 0 ? entry : n.key;
            s.parent = parent; s.path = path; s.depth = depth; s.folded = folded;
            s.startTicks = T(startMs) + 1000; s.inclusiveTicks = T(inclMs); s.childTicks = T(childMs); s.externTicks = T(extMs);
            s.allocBytes = alloc; s.scheduledByBehaviour = schedB; s.scheduledByEntry = schedE; s.halted = halted;
            s.bucket = Histogram.Bucket(s.inclusiveTicks);
            if (parent < 0) f.totalTicks += s.inclusiveTicks;
            return i;
        }

        public static void Stat(Frame f, int path, int calls, double inclMs, double selfMs, double extMs = 0, int extCount = 0, int fused = 0, double maxMs = -1, long instr = 0, long selfInstr = 0)
        {
            ref var st = ref f.AddStat(path, out _);
            st.instructions = instr; st.selfInstructions = selfInstr;
            st.calls = calls; st.inclusiveTicks = T(inclMs); st.selfTicks = T(selfMs); st.externTicks = T(extMs);
            st.externCount = extCount; st.fusedCount = fused; st.maxTicks = T(maxMs >= 0 ? maxMs : inclMs / Math.Max(1, calls));
        }

        public static void Span(Frame f, int path, int depth, double startMs, double ms)
        {
            ref var sp = ref f.AddSpan(out _);
            sp.path = path; sp.depth = depth; sp.startTicks = T(startMs) + 1000; sp.ticks = T(ms);
        }

        public static ProfileView Build(UdonProfilerModel m, GroupMode mode = GroupMode.Script, bool loop = false, string search = null, double hide = 0, bool refresh = true, int from = 0, int to = int.MaxValue)
            => m.Build(new ViewQuery { from = from, to = to, mode = mode, loopOnly = loop, search = search, hideBelowPct = hide, refreshMembership = refresh });

        public static Node Find(ProfileView v, Func<Node, bool> pred)
        {
            Node Walk(List<Node> nodes)
            {
                if (nodes == null) return null;
                foreach (var n in nodes) { if (pred(n)) return n; var r = Walk(n.children); if (r != null) return r; }
                return null;
            }
            return Walk(v.roots);
        }

        public static List<Node> All(ProfileView v)
        {
            var list = new List<Node>();
            void Walk(List<Node> nodes) { if (nodes == null) return; foreach (var n in nodes) { list.Add(n); Walk(n.children); } }
            Walk(v.roots);
            return list;
        }
    }

    public abstract class ModelTestBase
    {
        bool _recording;

        [SetUp]
        public void BaseSetUp()
        {
            _recording = UdonProfilerRecorder.Recording;
            UdonProfilerRecorder.ResetForLoad();
            UdonProfilerModel.Baseline = null;
        }

        [TearDown]
        public void BaseTearDown()
        {
            UdonProfilerRecorder.Clear();
            UdonProfilerModel.Baseline = null;
            UdonProfilerRecorder.Recording = _recording;
        }
    }

    public class ModelTests : ModelTestBase
    {
        const double Eps = 1e-3;

        static void Ring(int frames, Action<Frame, int> fill)
        {
            for (int i = 0; i < frames; i++) fill(ModelTestRing.Frame(i), i);
            UdonProfilerRecorder.CommitLoad(frames);
        }

        static void InstrRing(bool has)
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"));
            ModelTestRing.Name(1, 0x100, "M");
            int a = ModelTestRing.Ev(-1, 1, 0x10), m = ModelTestRing.Me(a, 0x100), ex = ModelTestRing.Ex(m, "Sig.__X");
            Ring(4, (f, i) =>
            {
                f.hasInstructions = has;
                ModelTestRing.Sample(f, a, -1, 0, 10, instr: has ? 100 : 0);
                ModelTestRing.Stat(f, a, 1, 10, 6, instr: has ? 100 : 0, selfInstr: has ? 60 : 0);
                ModelTestRing.Stat(f, m, 1, 4, 3, instr: has ? 40 : 0, selfInstr: has ? 40 : 0);
                ModelTestRing.Stat(f, ex, 2, 1, 1);
            });
        }

        [Test]
        public void InstructionCounts()
        {
            InstrRing(true);
            var model = new UdonProfilerModel();
            var v = ModelTestRing.Build(model);
            Assert.IsTrue(v.hasInstructions);
            Assert.AreEqual(100, v.avgInstructions, Eps);
            var ev = ModelTestRing.Find(v, n => n.kind == NodeKind.Event);
            var me = ModelTestRing.Find(v, n => n.kind == NodeKind.Method);
            var g = ModelTestRing.Find(v, n => n.kind == NodeKind.Group);
            var ex = ModelTestRing.Find(v, n => n.kind == NodeKind.Extern);
            Assert.AreEqual(400, ev.instructions);
            Assert.AreEqual(240, ev.selfInstructions);
            Assert.AreEqual(160, me.instructions);
            Assert.AreEqual(160, me.selfInstructions);
            Assert.AreEqual(400, g.instructions);
            Assert.AreEqual(400, g.selfInstructions);
            Assert.IsTrue(UdonProfilerModel.HasInstructions(v, ev));
            Assert.IsFalse(UdonProfilerModel.HasInstructions(v, ex));
            StringAssert.Contains(",400,240", UdonProfilerModel.ToCsv(v));

            var fl = model.BuildFlame(true);
            Assert.IsTrue(fl.byInstructions);
            Assert.AreEqual(100, fl.nodes[0].size, Eps);
            Assert.AreEqual(100, fl.totalInstructions, Eps);
            for (int i = 1; i < fl.count; i++)
            {
                ref readonly var n = ref fl.nodes[i];
                if (n.kind == NodeKind.Event) { Assert.AreEqual(100, n.size, Eps); Assert.AreEqual(60, n.selfInstructions, Eps); }
                if (n.kind == NodeKind.Method) Assert.AreEqual(40, n.size, Eps);
                if (n.kind == NodeKind.Extern) Assert.AreEqual(0, n.size, Eps);
            }
            var ms = model.BuildFlame(false);
            Assert.IsFalse(ms.byInstructions);
            Assert.AreEqual(10, ms.nodes[0].size, Eps);
        }

        [Test]
        public void InstructionCountsUnavailable()
        {
            InstrRing(false);
            var model = new UdonProfilerModel();
            var v = ModelTestRing.Build(model);
            Assert.IsFalse(v.hasInstructions);
            var ev = ModelTestRing.Find(v, n => n.kind == NodeKind.Event);
            Assert.IsFalse(UdonProfilerModel.HasInstructions(v, ev));
            Assert.AreEqual("", UdonProfilerModel.InstrText(v, ev, false, System.Globalization.CultureInfo.InvariantCulture));
            StringAssert.EndsWith(",,", UdonProfilerModel.ToCsv(v).Split('\n')[1].TrimEnd('\r'));
            var fl = model.BuildFlame(true);
            Assert.IsFalse(fl.byInstructions);
            Assert.AreEqual(10, fl.nodes[0].size, Eps);
        }

        [Test]
        public void NestedSameScriptDoesNotDoubleCount()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"), (0x20, "_b"));
            int a = ModelTestRing.Ev(-1, 1, 0x10), b = ModelTestRing.Ev(a, 1, 0x20);
            Ring(4, (f, i) =>
            {
                int s = ModelTestRing.Sample(f, a, -1, 0, 10, 4);
                ModelTestRing.Sample(f, b, s, 1, 4, depth: 1);
                ModelTestRing.Stat(f, a, 1, 10, 6);
                ModelTestRing.Stat(f, b, 1, 4, 4);
            });
            var v = ModelTestRing.Build(new UdonProfilerModel());
            var g = ModelTestRing.Find(v, n => n.kind == NodeKind.Group);
            Assert.AreEqual(40, g.totalMs, Eps);
            Assert.LessOrEqual(v.Pct(g), 100.0001);
            var x = ModelTestRing.Find(v, n => n.kind == NodeKind.Behaviour);
            Assert.AreEqual(40, x.totalMs, Eps);
        }

        [Test]
        public void EventModeShowsExternRows()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_update"));
            int e = ModelTestRing.Ev(-1, 1, 0x10), x = ModelTestRing.Ex(e, "Sig.__A");
            Ring(2, (f, i) => { ModelTestRing.Sample(f, e, -1, 0, 5, extMs: 1); ModelTestRing.Stat(f, e, 1, 5, 5, 1, 3); ModelTestRing.Stat(f, x, 3, 1, 0); });
            var v = ModelTestRing.Build(new UdonProfilerModel(), GroupMode.Event);
            var beh = ModelTestRing.Find(v, n => n.kind == NodeKind.Behaviour);
            Assert.NotNull(beh.children);
            Assert.IsTrue(beh.children.Exists(c => c.kind == NodeKind.Extern && c.name == "Sig.__A"));
        }

        [Test]
        public void ScriptModeBehaviourRowHasEdges()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"), (0x30, "_e"));
            ModelTestRing.Beh(2, "T", "Y", (0x20, "_b"), (0x40, "_d"));
            int a = ModelTestRing.Ev(-1, 1, 0x10), b = ModelTestRing.Ev(a, 2, 0x20);
            int d = ModelTestRing.Ev(-1, 2, 0x40), e = ModelTestRing.Ev(d, 1, 0x30);
            Ring(2, (f, i) =>
            {
                int s = ModelTestRing.Sample(f, a, -1, 0, 4, 1); ModelTestRing.Sample(f, b, s, 1, 1, depth: 1);
                s = ModelTestRing.Sample(f, d, -1, 5, 4, 1); ModelTestRing.Sample(f, e, s, 6, 1, depth: 1);
                ModelTestRing.Stat(f, a, 1, 4, 3); ModelTestRing.Stat(f, b, 1, 1, 1); ModelTestRing.Stat(f, d, 1, 4, 3); ModelTestRing.Stat(f, e, 1, 1, 1);
            });
            var v = ModelTestRing.Build(new UdonProfilerModel());
            var x = ModelTestRing.Find(v, n => n.kind == NodeKind.Behaviour && n.behaviourId == 1);
            Assert.AreEqual(1, v.EdgesOutOf(x).Count);
            Assert.AreEqual(1, v.EdgesInto(x).Count);
            Assert.AreEqual(2, v.EdgesOutOf(x)[0].count);
        }

        [Test]
        public void MethodRowsNestAndMerge()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"));
            ModelTestRing.Name(1, 0x100, "Foo"); ModelTestRing.Name(1, 0x300, "Bar"); ModelTestRing.Name(1, 0x400, "Bar");
            int e = ModelTestRing.Ev(-1, 1, 0x10), foo = ModelTestRing.Me(e, 0x100), foo2 = ModelTestRing.Me(foo, 0x100);
            int bar1 = ModelTestRing.Me(e, 0x300), bar2 = ModelTestRing.Me(e, 0x400);
            Ring(1, (f, i) =>
            {
                ModelTestRing.Sample(f, e, -1, 0, 10);
                ModelTestRing.Stat(f, e, 1, 10, 3); ModelTestRing.Stat(f, foo, 1, 5, 2); ModelTestRing.Stat(f, foo2, 1, 3, 3);
                ModelTestRing.Stat(f, bar1, 1, 1, 1); ModelTestRing.Stat(f, bar2, 1, 1, 1);
            });
            var v = ModelTestRing.Build(new UdonProfilerModel());
            var ev = ModelTestRing.Find(v, n => n.kind == NodeKind.Event);
            var fooRow = ev.children.Find(c => c.name == "Foo");
            Assert.AreEqual(5, fooRow.totalMs, Eps);
            Assert.AreEqual(3, fooRow.children.Find(c => c.name == "Foo").totalMs, Eps);
            var bar = ev.children.FindAll(c => c.name == "Bar");
            Assert.AreEqual(1, bar.Count);
            Assert.AreEqual(2, bar[0].calls);
            Assert.AreEqual(2, bar[0].totalMs, Eps);
            double sum = ev.selfMs;
            foreach (var c in ev.children) if (c.kind == NodeKind.Method || c.kind == NodeKind.Call) sum += c.totalMs;
            Assert.AreEqual(ev.totalMs, sum, Eps);
        }

        [Test]
        public void EventModeSelfInvariant()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"));
            ModelTestRing.Beh(2, "S", "Y", (0x20, "_f"));
            ModelTestRing.Name(1, 0x100, "M");
            int e = ModelTestRing.Ev(-1, 1, 0x10), m = ModelTestRing.Me(e, 0x100), fe = ModelTestRing.Ev(e, 2, 0x20);
            Ring(3, (f, i) =>
            {
                int s = ModelTestRing.Sample(f, e, -1, 0, 10, 2);
                ModelTestRing.Sample(f, fe, s, 5, 2, depth: 1);
                ModelTestRing.Stat(f, e, 1, 10, 5); ModelTestRing.Stat(f, m, 1, 3, 3); ModelTestRing.Stat(f, fe, 1, 2, 2);
            });
            var model = new UdonProfilerModel();
            var v = ModelTestRing.Build(model, GroupMode.Event);
            var beh = ModelTestRing.Find(v, n => n.kind == NodeKind.Behaviour && n.behaviourId == 1);
            double sum = beh.selfMs;
            foreach (var c in beh.children) if (c.kind == NodeKind.Method || c.kind == NodeKind.Call) sum += c.totalMs;
            Assert.AreEqual(beh.totalMs, sum, Eps);
            Assert.AreEqual(30, beh.totalMs, Eps);
            double self = beh.selfMs;
            var vs = ModelTestRing.Build(model);
            var ev = ModelTestRing.Find(vs, n => n.kind == NodeKind.Event && n.behaviourId == 1);
            Assert.AreEqual(self, ev.selfMs, Eps);
        }

        [Test]
        public void LoopOnlyDropsNonLoopRoots()
        {
            ModelTestRing.Beh(1, "U", "X", (0x10, "_update"));
            ModelTestRing.Beh(2, "C", "Y", (0x20, "_custom"));
            int u = ModelTestRing.Ev(-1, 1, 0x10), c = ModelTestRing.Ev(-1, 2, 0x20);
            Ring(2, (f, i) =>
            {
                ModelTestRing.Sample(f, u, -1, 0, 3); ModelTestRing.Sample(f, c, -1, 4, 1);
                ModelTestRing.Stat(f, u, 1, 3, 3); ModelTestRing.Stat(f, c, 1, 1, 1);
            });
            var s = UdonProfilerModel.Summary(0);
            Assert.AreEqual(ModelTestRing.T(3), s.loopTicks);
            Assert.AreEqual(ModelTestRing.T(4), s.totalTicks);
            Assert.AreEqual(1, s.loopEvents);
            var v = ModelTestRing.Build(new UdonProfilerModel(), loop: true);
            Assert.AreEqual(1, v.roots.Count);
            Assert.AreEqual("U", v.roots[0].name);
            Assert.AreEqual(3, v.avgMs, Eps);
            Assert.AreEqual(100, v.Pct(v.roots[0]), 1e-6);
        }

        static void SimpleRing(double bMs = 1)
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"));
            ModelTestRing.Beh(2, "S", "Y", (0x20, "_b"));
            ModelTestRing.Name(1, 0x100, "M");
            int a = ModelTestRing.Ev(-1, 1, 0x10), m = ModelTestRing.Me(a, 0x100), x = ModelTestRing.Ex(m, "Sig.__X"), b = ModelTestRing.Ev(-1, 2, 0x20);
            Ring(2, (f, i) =>
            {
                ModelTestRing.Sample(f, a, -1, 0, 99); ModelTestRing.Sample(f, b, -1, 100, bMs);
                ModelTestRing.Stat(f, a, 1, 99, 90); ModelTestRing.Stat(f, m, 1, 9, 8, 1, 1); ModelTestRing.Stat(f, x, 1, 1, 0);
                ModelTestRing.Stat(f, b, 1, bMs, bMs);
            });
        }

        [Test]
        public void StableIdsAcrossBuildsAndReload()
        {
            SimpleRing();
            var v1 = ModelTestRing.Build(new UdonProfilerModel());
            var ids1 = ModelTestRing.All(v1).ConvertAll(n => n.id);
            var v2 = ModelTestRing.Build(new UdonProfilerModel());
            CollectionAssert.AreEquivalent(ids1, ModelTestRing.All(v2).ConvertAll(n => n.id));
            UdonProfilerRecorder.ResetForLoad();
            SimpleRing();
            var v3 = ModelTestRing.Build(new UdonProfilerModel());
            CollectionAssert.AreEquivalent(ids1, ModelTestRing.All(v3).ConvertAll(n => n.id));
        }

        [Test]
        public void StructureStampStickyAndHysteresis()
        {
            var model = new UdonProfilerModel();
            SimpleRing(0.05);
            var v = ModelTestRing.Build(model, hide: 0.1);
            int s0 = v.structureStamp;
            Assert.IsNull(ModelTestRing.Find(v, n => n.kind == NodeKind.Event && n.behaviourId == 2));

            UdonProfilerRecorder.ResetForLoad(); SimpleRing(0.12);
            v = ModelTestRing.Build(model, hide: 0.1, refresh: true);
            Assert.AreEqual(s0, v.structureStamp);

            UdonProfilerRecorder.ResetForLoad(); SimpleRing(0.3);
            v = ModelTestRing.Build(model, hide: 0.1, refresh: false);
            Assert.AreEqual(s0, v.structureStamp);
            v = ModelTestRing.Build(model, hide: 0.1, refresh: true);
            Assert.AreNotEqual(s0, v.structureStamp);
            Assert.NotNull(ModelTestRing.Find(v, n => n.kind == NodeKind.Event && n.behaviourId == 2));

            int s1 = v.structureStamp;
            UdonProfilerRecorder.ResetForLoad(); SimpleRing(0.4);
            v = ModelTestRing.Build(model, hide: 0.1, refresh: true);
            Assert.AreEqual(s1, v.structureStamp);
        }

        [Test]
        public void FlameGraphConsistent()
        {
            SimpleRing();
            var model = new UdonProfilerModel();
            ModelTestRing.Build(model);
            var g = model.BuildFlame();
            double events = 0;
            for (int i = 1; i < g.count; i++)
            {
                ref readonly var n = ref g.nodes[i];
                if (n.depth == 1) events += n.totalMs;
                double kids = 0;
                for (int c = n.firstChild; c >= 0; c = g.nodes[c].nextSibling) kids += g.nodes[c].totalMs;
                Assert.LessOrEqual(kids, n.totalMs + Eps);
            }
            Assert.AreEqual(events, g.nodes[0].totalMs, Eps);
            Assert.AreEqual(100, g.totalMs, Eps);
            var labels = new List<string>();
            for (int i = 0; i < g.count; i++) labels.Add(g.nodes[i].label + "|" + g.nodes[i].totalMs);
            var g2 = model.BuildFlame();
            Assert.AreSame(g, g2);
            for (int i = 0; i < g2.count; i++) Assert.AreEqual(labels[i], g2.nodes[i].label + "|" + g2.nodes[i].totalMs);
        }

        [Test]
        public void SummarySegmentsAndEdgesGrow()
        {
            const int Scripts = 12;
            var paths = new int[Scripts];
            for (int i = 0; i < Scripts; i++)
            {
                ModelTestRing.Beh(i + 1, "S" + i, "O" + i, (0x10, "_a"), (0x20, "_b"));
                paths[i] = ModelTestRing.Ev(-1, i + 1, 0x10);
            }
            var f = ModelTestRing.Frame(0);
            long total = 0;
            for (int i = 0; i < Scripts; i++)
            {
                int s = ModelTestRing.Sample(f, paths[i], -1, i * 10, 2 + i, 1);
                int nested = ModelTestRing.Ev(paths[i], (i + 1) % Scripts + 1, 0x20);
                ModelTestRing.Sample(f, nested, s, i * 10 + 1, 1, depth: 1);
                ModelTestRing.Stat(f, paths[i], 1, 2 + i, 1 + i);
                ModelTestRing.Stat(f, nested, 1, 1, 1);
                total += ModelTestRing.T(2 + i);
            }
            UdonProfilerRecorder.CommitLoad(1);
            f.isSealed = false;
            var sum = UdonProfilerModel.Summary(0);
            long segs = 0;
            for (int i = 0; i < sum.segmentCount; i++) segs += sum.segTicks[i];
            Assert.AreEqual((double)f.totalTicks, segs, Scripts);
            Assert.AreEqual(Scripts, sum.segmentCount);
            Assert.AreEqual(Scripts, sum.edgeCount);
            f.totalTicks += 5;
            f.isSealed = true;
            Assert.AreEqual(f.totalTicks, UdonProfilerModel.Summary(0).totalTicks);
        }

        [Test]
        public void TimelineSelfAndCache()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"));
            ModelTestRing.Beh(2, "S", "Y", (0x20, "_f"));
            ModelTestRing.Name(1, 0x100, "M"); ModelTestRing.Name(1, 0x200, "Spanless");
            int e = ModelTestRing.Ev(-1, 1, 0x10), m = ModelTestRing.Me(e, 0x100), q = ModelTestRing.Me(e, 0x200), fe = ModelTestRing.Ev(q, 2, 0x20);
            var f = ModelTestRing.Frame(0);
            int s = ModelTestRing.Sample(f, e, -1, 0, 10, 2);
            ModelTestRing.Span(f, m, 1, 1, 3);
            ModelTestRing.Sample(f, fe, s, 6, 2, depth: 1);
            ModelTestRing.Stat(f, e, 1, 10, 3);
            UdonProfilerRecorder.CommitLoad(1);
            var d = UdonProfilerModel.Timeline(0);
            var ev = d.items.Find(it => it.kind == PathKind.Event && it.depth == 0);
            Assert.AreEqual(10 - 3 - 2, ev.selfMs, Eps);
            Assert.AreEqual("S._a", ev.label);
            Assert.IsTrue(d.items.Exists(it => it.kind == PathKind.Method && it.label == "M"));
            UdonProfilerRecorder.Version++;
            Assert.AreSame(d, UdonProfilerModel.Timeline(0));
            Assert.AreEqual(0, UdonProfilerModel.Timeline(5).items.Count);
        }

        [Test]
        public void ExternAverageUsesTimedCalls()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"));
            int e = ModelTestRing.Ev(-1, 1, 0x10), x = ModelTestRing.Ex(e, "Sig.__X");
            Ring(1, (f, i) => { ModelTestRing.Sample(f, e, -1, 0, 5); ModelTestRing.Stat(f, e, 1, 5, 5, 2, 10, 6); ModelTestRing.Stat(f, x, 10, 2, 0, fused: 6); });
            var v = ModelTestRing.Build(new UdonProfilerModel());
            var row = ModelTestRing.Find(v, n => n.kind == NodeKind.Extern);
            Assert.AreEqual(10, row.calls);
            Assert.AreEqual(6, row.fusedCalls);
            string line = Array.Find(UdonProfilerModel.ToCsv(v).Split('\n'), l => l.StartsWith("Extern,"));
            var cols = line.TrimEnd('\r').Split(',');
            Assert.AreEqual(2.0 / 4, double.Parse(cols[16], System.Globalization.CultureInfo.InvariantCulture), Eps);
        }

        [Test]
        public void OverflowPathGroup()
        {
            ModelTestRing.Beh(5, "S", "X", (0x10, "_a"));
            Ring(1, (f, i) => { ModelTestRing.Sample(f, PathTable.OverflowPath, -1, 0, 2, b: 5, entry: 0x10); ModelTestRing.Stat(f, PathTable.OverflowPath, 1, 2, 2); });
            var v = ModelTestRing.Build(new UdonProfilerModel());
            Assert.NotNull(ModelTestRing.Find(v, n => n.kind == NodeKind.Group && n.name == "(path table full)"));
        }

        [Test]
        public void StickyReachability()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"), (0x20, "_b"));
            int a = ModelTestRing.Ev(-1, 1, 0x10), b = ModelTestRing.Ev(-1, 1, 0x20);
            Ring(4, (f, i) =>
            {
                ModelTestRing.Sample(f, a, -1, 0, 2); ModelTestRing.Stat(f, a, 1, 2, 2);
                if (i < 2) { ModelTestRing.Sample(f, b, -1, 3, 1); ModelTestRing.Stat(f, b, 1, 1, 1); }
            });
            var model = new UdonProfilerModel();
            var v = ModelTestRing.Build(model, from: 0, to: 1);
            int stamp = v.structureStamp;
            var bRow = ModelTestRing.Find(v, n => n.kind == NodeKind.Event && n.entry == 0x20);
            Assert.NotNull(bRow);
            v = ModelTestRing.Build(model, from: 2, to: 3, refresh: false);
            Assert.AreEqual(stamp, v.structureStamp);
            Assert.AreSame(bRow, ModelTestRing.Find(v, n => n.kind == NodeKind.Event && n.entry == 0x20));
            Assert.AreEqual(0, bRow.totalMs);
            v = ModelTestRing.Build(model, from: 2, to: 3, refresh: true);
            Assert.IsNull(ModelTestRing.Find(v, n => n.kind == NodeKind.Event && n.entry == 0x20));
        }

        [Test]
        public void ExternsOfMergesSameSig()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"));
            ModelTestRing.Name(1, 0x100, "M"); ModelTestRing.Name(1, 0x200, "N");
            int e = ModelTestRing.Ev(-1, 1, 0x10), m = ModelTestRing.Me(e, 0x100), n2 = ModelTestRing.Me(e, 0x200);
            int x1 = ModelTestRing.Ex(m, "Sig.__X"), x2 = ModelTestRing.Ex(n2, "Sig.__X"), y = ModelTestRing.Ex(m, "Sig.__Y");
            Ring(1, (f, i) =>
            {
                ModelTestRing.Sample(f, e, -1, 0, 10);
                ModelTestRing.Stat(f, e, 1, 10, 4); ModelTestRing.Stat(f, m, 1, 3, 3, 3, 3); ModelTestRing.Stat(f, n2, 1, 3, 3, 1, 1);
                ModelTestRing.Stat(f, x1, 2, 2, 0); ModelTestRing.Stat(f, x2, 1, 1, 0); ModelTestRing.Stat(f, y, 1, 0.5, 0);
            });
            var v = ModelTestRing.Build(new UdonProfilerModel());
            var g = ModelTestRing.Find(v, n => n.kind == NodeKind.Group);
            var rows = v.ExternsOf(g);
            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("Sig.__X", rows[0].sig);
            Assert.AreEqual(3, rows[0].count);
            Assert.AreEqual(3, rows[0].ms, Eps);
            Assert.AreSame(rows, v.ExternsOf(g));
        }

        [Test]
        public void StableBehaviourIdsIndependentOfOrder()
        {
            void Make(bool swap)
            {
                var x = new BehaviourInfo { script = "S", objectName = "P", objectPath = "Root/P", objectKey = "Scene:Root[0]/P[0]#c0" };
                var y = new BehaviourInfo { script = "S", objectName = "P", objectPath = "Root/P", objectKey = "Scene:Root[0]/P[1]#c0" };
                x.events[0x10] = "_a"; y.events[0x10] = "_a";
                if (swap) { UdonProfilerRecorder.RegisterLoadedBehaviour(2, y); UdonProfilerRecorder.RegisterLoadedBehaviour(1, x); }
                else { UdonProfilerRecorder.RegisterLoadedBehaviour(1, x); UdonProfilerRecorder.RegisterLoadedBehaviour(2, y); }
                int first = swap ? 2 : 1, second = swap ? 1 : 2;
                int a = ModelTestRing.Ev(-1, first, 0x10), b = ModelTestRing.Ev(-1, second, 0x10);
                Ring(1, (f, i) => { ModelTestRing.Sample(f, a, -1, 0, 1); ModelTestRing.Sample(f, b, -1, 2, 1); ModelTestRing.Stat(f, a, 1, 1, 1); ModelTestRing.Stat(f, b, 1, 1, 1); });
            }
            Make(false);
            var v = ModelTestRing.Build(new UdonProfilerModel(), GroupMode.GameObject);
            int idX = ModelTestRing.Find(v, n => n.kind == NodeKind.Behaviour && n.behaviourId == 1).id;
            int idY = ModelTestRing.Find(v, n => n.kind == NodeKind.Behaviour && n.behaviourId == 2).id;
            UdonProfilerRecorder.ResetForLoad();
            Make(true);
            v = ModelTestRing.Build(new UdonProfilerModel(), GroupMode.GameObject);
            Assert.AreEqual(idX, ModelTestRing.Find(v, n => n.kind == NodeKind.Behaviour && n.behaviourId == 1).id);
            Assert.AreEqual(idY, ModelTestRing.Find(v, n => n.kind == NodeKind.Behaviour && n.behaviourId == 2).id);
        }

        [Test]
        public void FoldedReentrancySkipsAlloc()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"));
            int e = ModelTestRing.Ev(-1, 1, 0x10);
            Ring(1, (f, i) =>
            {
                int s = ModelTestRing.Sample(f, e, -1, 0, 5, 2, alloc: 100);
                ModelTestRing.Sample(f, e, s, 1, 2, depth: 1, folded: true, alloc: 40);
                ModelTestRing.Stat(f, e, 1, 5, 5);
            });
            var v = ModelTestRing.Build(new UdonProfilerModel());
            var row = ModelTestRing.Find(v, n => n.kind == NodeKind.Event);
            Assert.AreEqual(100, row.allocBytes);
            Assert.AreEqual(5, row.p95Ms, 1);
        }

        [Test]
        public void ExternSemantics()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_a"));
            ModelTestRing.Name(1, 0x100, "M");
            int e = ModelTestRing.Ev(-1, 1, 0x10), m = ModelTestRing.Me(e, 0x100);
            Ring(3, (f, i) =>
            {
                ModelTestRing.Sample(f, e, -1, 0, 10, extMs: 3);
                ModelTestRing.Stat(f, e, 1, 10, 6, 1, 1); ModelTestRing.Stat(f, m, 1, 4, 4, 2, 1);
            });
            var v = ModelTestRing.Build(new UdonProfilerModel());
            var ev = ModelTestRing.Find(v, n => n.kind == NodeKind.Event);
            var mr = ModelTestRing.Find(v, n => n.kind == NodeKind.Method);
            var g = ModelTestRing.Find(v, n => n.kind == NodeKind.Group);
            Assert.AreEqual(3, ev.externMs, Eps);
            Assert.AreEqual(9, ev.externAllMs, Eps);
            Assert.AreEqual(6, mr.externMs, Eps);
            Assert.IsTrue(double.IsNaN(mr.externAllMs));
            Assert.AreEqual(9, g.externAllMs, Eps);
        }

        [Test]
        public void MergeMemoSkipsUnchangedRanges()
        {
            SimpleRing();
            var model = new UdonProfilerModel();
            var v = ModelTestRing.Build(model);
            double total = v.roots[0].totalMs;
            int runs = model.MergeRuns;
            UdonProfilerRecorder.Version++;
            v = ModelTestRing.Build(model, refresh: false);
            Assert.AreEqual(runs, model.MergeRuns);
            Assert.AreEqual(total, v.roots[0].totalMs, Eps);
            v = ModelTestRing.Build(model, search: "Y", refresh: true);
            Assert.AreEqual(runs, model.MergeRuns);
            Assert.IsNull(ModelTestRing.Find(v, n => n.kind == NodeKind.Behaviour && n.behaviourId == 1));
            ModelTestRing.Build(model, from: 1, to: 1);
            Assert.AreEqual(runs + 1, model.MergeRuns);
        }

        [Test]
        public void EdgesMatchBruteForce()
        {
            const int Behaviours = 101;
            for (int i = 1; i <= Behaviours; i++) ModelTestRing.Beh(i, "S", "O" + i, (0x10, "_a"), (0x20, "_b"));
            var roots = new int[Behaviours + 1];
            var nested = new int[Behaviours + 1];
            for (int i = 1; i <= Behaviours; i++) roots[i] = ModelTestRing.Ev(-1, i, 0x10);
            for (int i = 1; i <= Behaviours; i++) nested[i] = ModelTestRing.Ev(roots[i], i % Behaviours + 1, 0x20);
            Ring(3, (f, k) =>
            {
                for (int i = 1; i <= Behaviours; i++)
                {
                    int s = ModelTestRing.Sample(f, roots[i], -1, i * 3, 2, 1);
                    ModelTestRing.Sample(f, nested[i], s, i * 3 + 1, 1, depth: 1);
                    if (i < 100) ModelTestRing.Sample(f, roots[i], -1, i * 3 + 2, 0.5, schedB: i % Behaviours + 1, schedE: 0x20);
                    ModelTestRing.Stat(f, roots[i], 2, 2.5, 1.5); ModelTestRing.Stat(f, nested[i], 1, 1, 1);
                }
            });
            var model = new UdonProfilerModel();
            var v = ModelTestRing.Build(model);
            Assert.GreaterOrEqual(UdonProfilerModel.Summary(0).edgeCount, 199);
            var r = ModelTestRing.Find(v, n => n.kind == NodeKind.Event && n.behaviourId == 7 && n.entry == 0x10);
            int scans = model.EdgeScans;
            var into = v.EdgesInto(r);
            var outOf = v.EdgesOutOf(r);
            Assert.AreEqual(scans + 1, model.EdgeScans);
            Assert.AreEqual(1, into.Count);
            Assert.AreEqual(3, into[0].count);
            Assert.IsTrue(into[0].delayed);
            Assert.AreEqual(1, outOf.Count);
            int bruteOut = 0;
            for (int s = 0; s < 3; s++)
            {
                var fs = UdonProfilerModel.Summary(s);
                for (int i = 0; i < fs.edgeCount; i++) if (fs.edges[i].fromBehaviour == 7 && fs.edges[i].fromEntry == 0x10) bruteOut += fs.edges[i].count;
            }
            Assert.AreEqual(bruteOut, outOf[0].count);
            Assert.AreSame(into, v.EdgesInto(r));
            Assert.AreEqual(scans + 1, model.EdgeScans);
        }

        [Test]
        public void IncrementalMergeEqualsFull()
        {
            ModelTestRing.Beh(1, "S", "X", (0x10, "_update"));
            ModelTestRing.Beh(2, "T", "Y", (0x20, "_b"));
            ModelTestRing.Name(1, 0x100, "M");
            int e = ModelTestRing.Ev(-1, 1, 0x10), m = ModelTestRing.Me(e, 0x100), x = ModelTestRing.Ex(m, "Sig.__X"), b = ModelTestRing.Ev(-1, 2, 0x20);
            var rnd = new Random(7);
            Ring(400, (f, i) =>
            {
                double inc = 1 + rnd.NextDouble() * 5, mm = inc * 0.5;
                ModelTestRing.Sample(f, e, -1, 0, inc, extMs: 0.2, alloc: i, halted: i % 17 == 0);
                ModelTestRing.Stat(f, e, 1, inc, inc - mm, 0.1, 1); ModelTestRing.Stat(f, m, 1, mm, mm - 0.1, 0.1, 1, 2); ModelTestRing.Stat(f, x, 3, 0.1, 0, fused: 2);
                if (i % 3 == 0) { ModelTestRing.Sample(f, b, -1, 10, 0.5); ModelTestRing.Stat(f, b, 1, 0.5, 0.5); }
            });
            var live = new UdonProfilerModel();
            foreach (int to in new[] { 100, 130, 160 })
            {
                var v = ModelTestRing.Build(live, from: to - 59, to: to, refresh: false);
                var fresh = ModelTestRing.Build(new UdonProfilerModel(), from: to - 59, to: to);
                foreach (var n in ModelTestRing.All(fresh))
                {
                    var o = v.FindById(n.id);
                    Assert.NotNull(o, n.name);
                    Assert.AreEqual(n.totalMs, o.totalMs, 1e-6, n.name);
                    Assert.AreEqual(n.selfMs, o.selfMs, 1e-6, n.name);
                    Assert.AreEqual(n.calls, o.calls, n.name);
                    Assert.AreEqual(n.fusedCalls, o.fusedCalls, n.name);
                    Assert.AreEqual(n.allocBytes, o.allocBytes, n.name);
                    Assert.AreEqual(n.halted, o.halted, n.name);
                    Assert.AreEqual(n.externAllMs, o.externAllMs, 1e-6, n.name);
                    if (!double.IsNaN(n.p95Ms)) Assert.GreaterOrEqual(o.p95Ms, n.p95Ms - 1e-6, n.name);
                }
                Assert.AreEqual(fresh.activeBehaviours, v.activeBehaviours);
            }
            var last = ModelTestRing.Build(live, from: 102, to: 161, refresh: true);
            var full = ModelTestRing.Build(new UdonProfilerModel(), from: 102, to: 161);
            foreach (var n in ModelTestRing.All(full))
            {
                Assert.AreEqual(n.maxMs, last.FindById(n.id).maxMs, 1e-6, n.name);
                Assert.AreEqual(n.p95Ms, last.FindById(n.id).p95Ms, 1e-6, n.name);
            }
        }
    }
}
