using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;

namespace MagmaVRC.UdonProfiler.Tests
{
    public class CaptureTests : ModelTestBase
    {
        string _file;

        [SetUp]
        public void CreateFile() => _file = Path.Combine(Path.GetTempPath(), "udonprofiler-" + Guid.NewGuid().ToString("N") + ".udonprofile");

        [TearDown]
        public void DeleteFile() { try { if (File.Exists(_file)) File.Delete(_file); } catch (IOException) { } }

        static string PathSig(int p)
        {
            var paths = UdonProfilerRecorder.Paths;
            if ((uint)p >= (uint)paths.Count) return "?";
            var parts = new List<string>();
            for (int q = p; q >= 0; q = paths[q].parent)
            {
                var n = paths[q];
                string key = n.kind == PathKind.Extern ? UdonProfilerRecorder.Sig((int)n.key) : n.key.ToString("X");
                parts.Add(n.kind + ":" + n.behaviourId + ":" + key);
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        static List<string> Snapshot()
        {
            var r = new List<string>();
            int oldest = UdonProfilerRecorder.NewestFrame - UdonProfilerRecorder.FrameCount + 1;
            for (int s = oldest; s <= UdonProfilerRecorder.NewestFrame; s++)
            {
                var f = UdonProfilerRecorder.GetFrame(s);
                r.Add($"F {f.frameNumber} {f.totalTicks} {f.externCount} {f.fusedCount} {f.droppedSpans} {f.droppedStats} {f.hasInstructions} {f.totalInstructions}");
                for (int i = 0; i < f.sampleCount; i++)
                {
                    var x = f.samples[i];
                    r.Add($"S {x.behaviourId} {x.entry:X} {x.parent} {PathSig(x.path)} {x.depth} {x.startTicks} {x.inclusiveTicks} {x.childTicks} {x.externTicks} {x.externCount} {x.fusedCount} {x.scheduledByBehaviour} {x.scheduledByEntry:X} {x.halted} {x.folded} {x.allocBytes} {x.childAlloc} {x.bucket} {x.instructions} {x.childInstructions}");
                }
                for (int i = 0; i < f.statCount; i++)
                {
                    var st = f.stats[i];
                    r.Add($"T {PathSig(st.path)} {st.calls} {st.inclusiveTicks} {st.selfTicks} {st.externTicks} {st.externCount} {st.fusedCount} {st.maxTicks} {st.instructions} {st.selfInstructions}");
                }
                for (int i = 0; i < f.spanCount; i++)
                {
                    var sp = f.spans[i];
                    r.Add($"P {PathSig(sp.path)} {sp.depth} {sp.startTicks} {sp.ticks} {sp.instructions}");
                }
            }
            return r;
        }

        void BuildRing()
        {
            var x = ModelTestRing.Beh(1, "S", "X", (0x10, "_a"));
            x.objectKey = "K1";
            var y = new BehaviourInfo { script = "T", objectName = "Y", objectPath = "Root/Y", objectKey = "" };
            UdonProfilerRecorder.RegisterLoadedBehaviour(2, y);
            x.methods[0x100] = new SourceRef { name = "M", file = null, line = 0 };
            x.methods[0x200] = new SourceRef { name = "N", file = "Assets/N.cs", line = 12 };
            int e = ModelTestRing.Ev(-1, 1, 0x10), m = ModelTestRing.Me(e, 0x100), n = ModelTestRing.Me(e, 0x200);
            int ex = ModelTestRing.Ex(m, "Sig.__X"), f2 = ModelTestRing.Ev(m, 2, 0x20), r2 = ModelTestRing.Ev(-1, 2, 0x30);
            for (int i = 0; i < 3; i++)
            {
                var f = ModelTestRing.Frame(i);
                int s = ModelTestRing.Sample(f, e, -1, 0, 10, 3, 1, alloc: 50, halted: i == 1);
                int s2 = ModelTestRing.Sample(f, f2, s, 2, 3, 1, depth: 2);
                ModelTestRing.Sample(f, f2, s2, 2.5, 1, depth: 3, folded: true, b: 2, entry: 0x20);
                ModelTestRing.Sample(f, r2, -1, 11, 1, schedB: 1, schedE: 0x10);
                ModelTestRing.Stat(f, e, 1, 10, 4, 0.5, 1);
                ModelTestRing.Stat(f, m, 1, 5, 2, 0.5, 2, 1);
                ModelTestRing.Stat(f, n, 1, 1, 1);
                ModelTestRing.Stat(f, ex, 2, 0.5, 0, fused: 1);
                ModelTestRing.Stat(f, f2, 1, 3, 3);
                ModelTestRing.Stat(f, r2, 1, 1, 1);
                ModelTestRing.Span(f, m, 1, 1, 5);
                ModelTestRing.Span(f, n, 1, 7, 1);
                f.droppedStats = i + 2;
                f.droppedSpans = i;
                f.fusedCount = 1;
                f.hasInstructions = i != 2;
                f.totalInstructions = 500 + i;
                for (int k = 0; k < f.sampleCount; k++) { f.samples[k].instructions = 100 + k; f.samples[k].childInstructions = 10 + k; }
                for (int k = 0; k < f.statCount; k++) { f.stats[k].instructions = 200 + k; f.stats[k].selfInstructions = 20 + k; }
                for (int k = 0; k < f.spanCount; k++) f.spans[k].instructions = 300 + k;
            }
            UdonProfilerRecorder.CommitLoad(3);
        }

        [Test]
        public void V2RoundTrip()
        {
            BuildRing();
            var before = Snapshot();
            var model = new UdonProfilerModel();
            var v1 = ModelTestRing.Build(model);
            var totals = ModelTestRing.All(v1).ConvertAll(r => r.id + ":" + r.totalMs.ToString("0.000") + ":" + r.calls);
            UdonProfilerCapture.Save(_file);
            UdonProfilerRecorder.ResetForLoad();
            UdonProfilerCapture.Load(_file);

            Assert.IsTrue(UdonProfilerRecorder.IsLoadedCapture);
            CollectionAssert.AreEqual(before, Snapshot());
            var x = UdonProfilerRecorder.GetInfo(1);
            var y = UdonProfilerRecorder.GetInfo(2);
            Assert.AreEqual("K1", x.objectKey);
            Assert.AreEqual("Root/Y", y.objectKey);
            Assert.IsNull(x.methods[0x100].file);
            Assert.AreEqual("Assets/N.cs", x.methods[0x200].file);
            Assert.AreEqual(12, x.methods[0x200].line);
            Assert.AreEqual("_a", x.events[0x10]);
            Assert.AreEqual("0x20", UdonProfilerRecorder.EventName(2, 0x20));
            Assert.AreEqual("0x30", UdonProfilerRecorder.EventName(2, 0x30));

            var f = UdonProfilerRecorder.GetFrame(0);
            Assert.AreEqual(-1, UdonProfilerRecorder.Paths[f.samples[0].path].parent);
            Assert.IsTrue(f.samples[2].folded);
            Assert.IsTrue(UdonProfilerRecorder.GetFrame(1).samples[0].halted);
            Assert.AreEqual(2, f.droppedStats);
            Assert.IsTrue(f.hasInstructions);
            Assert.AreEqual(500, f.totalInstructions);
            Assert.IsFalse(UdonProfilerRecorder.GetFrame(2).hasInstructions);

            var v2 = ModelTestRing.Build(new UdonProfilerModel());
            CollectionAssert.AreEquivalent(totals, ModelTestRing.All(v2).ConvertAll(r => r.id + ":" + r.totalMs.ToString("0.000") + ":" + r.calls));
        }

        [Test]
        public void V1Loads()
        {
            using (var w = new BinaryWriter(File.Create(_file)))
            {
                w.Write(UdonProfilerCapture.MagicV1);
                w.Write(Stopwatch.Frequency);
                w.Write(1);
                w.Write(1); w.Write("S"); w.Write("O"); w.Write("R/O");
                w.Write(2); w.Write(0x10u); w.Write("_update"); w.Write(0x20u); w.Write("_n");
                w.Write(1); w.Write("Sig.__X");
                w.Write(2);
                for (int i = 0; i < 2; i++)
                {
                    w.Write(100 + i); w.Write(ModelTestRing.T(10)); w.Write(4);
                    w.Write(2);
                    w.Write(1); w.Write(0x10u); w.Write(-1);
                    w.Write(1000L); w.Write(ModelTestRing.T(10)); w.Write(ModelTestRing.T(3)); w.Write(ModelTestRing.T(2)); w.Write(4);
                    w.Write(0); w.Write(0u); w.Write(false); w.Write(64L); w.Write(16L);
                    w.Write(1); w.Write(0x20u); w.Write(0);
                    w.Write(2000L); w.Write(ModelTestRing.T(3)); w.Write(0L); w.Write(0L); w.Write(0);
                    w.Write(0); w.Write(0u); w.Write(i == 1); w.Write(16L); w.Write(0L);
                    w.Write(2);
                    w.Write(1); w.Write(0x10u); w.Write(0); w.Write(4); w.Write(ModelTestRing.T(2));
                    w.Write(9); w.Write(0x99u); w.Write(0); w.Write(1); w.Write(ModelTestRing.T(1));
                }
            }
            UdonProfilerCapture.Load(_file);
            Assert.AreEqual(2, UdonProfilerRecorder.FrameCount);
            var f = UdonProfilerRecorder.GetFrame(1);
            Assert.AreEqual(101, f.frameNumber);
            Assert.AreEqual(1, f.samples[1].depth);
            Assert.AreEqual(UdonProfilerRecorder.Paths[f.samples[1].path].parent, f.samples[0].path);
            Assert.AreEqual(1, f.droppedStats);
            Assert.AreEqual("R/O", UdonProfilerRecorder.GetInfo(1).objectKey);
            Assert.IsFalse(f.hasInstructions);

            var v = ModelTestRing.Build(new UdonProfilerModel());
            Assert.IsFalse(v.hasInstructions);
            var ev = ModelTestRing.Find(v, n => n.kind == NodeKind.Event && n.name == "_update");
            Assert.NotNull(ev);
            Assert.AreEqual(20, ev.totalMs, 1e-3);
            Assert.AreEqual(14, ev.selfMs, 1e-3);
            var ex = ev.children.Find(c => c.kind == NodeKind.Extern);
            Assert.AreEqual("Sig.__X", ex.sig);
            Assert.AreEqual(8, ex.calls);
            Assert.AreEqual(4, ex.totalMs, 1e-3);
            Assert.NotNull(ev.children.Find(c => c.kind == NodeKind.Call));
        }

        [Test]
        public void V2LoadsWithoutInstructions()
        {
            using (var w = new BinaryWriter(File.Create(_file)))
            {
                w.Write(UdonProfilerCapture.MagicV2); w.Write(2); w.Write(Stopwatch.Frequency);
                w.Write(0);
                w.Write(1); w.Write(1); w.Write("S"); w.Write("O"); w.Write("R/O"); w.Write("K"); w.Write(1); w.Write(0x10u); w.Write("_update"); w.Write(0);
                w.Write(0);
                w.Write(2);
                w.Write(-1); w.Write((byte)PathKind.Event); w.Write(0); w.Write(0xFFFFFFFFu);
                w.Write(-1); w.Write((byte)PathKind.Event); w.Write(1); w.Write(0x10u);
                w.Write(1);
                w.Write(7); w.Write(ModelTestRing.T(2)); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                w.Write(1);
                w.Write(1); w.Write(0x10u); w.Write(-1); w.Write(1); w.Write(0);
                w.Write(1000L); w.Write(ModelTestRing.T(2)); w.Write(0L); w.Write(0L); w.Write(0); w.Write(0);
                w.Write(0); w.Write(0u); w.Write((byte)0); w.Write(0L); w.Write(0L);
                w.Write(1);
                w.Write(1); w.Write(1); w.Write(ModelTestRing.T(2)); w.Write(ModelTestRing.T(2)); w.Write(0L); w.Write(0); w.Write(0); w.Write(ModelTestRing.T(2));
                w.Write(0);
            }
            UdonProfilerCapture.Load(_file);
            Assert.AreEqual(1, UdonProfilerRecorder.FrameCount);
            var f = UdonProfilerRecorder.GetFrame(0);
            Assert.AreEqual(7, f.frameNumber);
            Assert.IsFalse(f.hasInstructions);
            var v = ModelTestRing.Build(new UdonProfilerModel());
            Assert.IsFalse(v.hasInstructions);
            var ev = ModelTestRing.Find(v, n => n.kind == NodeKind.Event && n.name == "_update");
            Assert.NotNull(ev);
            Assert.AreEqual(2, ev.totalMs, 1e-3);
        }

        [Test]
        public void RandomFileThrows()
        {
            var rnd = new Random(3);
            var bytes = new byte[256];
            rnd.NextBytes(bytes);
            bytes[0] = 0x12;
            File.WriteAllBytes(_file, bytes);
            Assert.That(() => UdonProfilerCapture.Load(_file), Throws.InstanceOf<IOException>());
            File.WriteAllBytes(_file, new byte[2]);
            Assert.That(() => UdonProfilerCapture.Load(_file), Throws.InstanceOf<IOException>());
        }
    }
}
