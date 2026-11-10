using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
using R = MagmaVRC.UdonProfiler.UdonProfilerRecorder;
using E = MagmaVRC.UdonProfiler.UdonProfilerNetEstimate;

namespace MagmaVRC.UdonProfiler.Tests
{
    public class NetEstimateTests
    {
        [Test]
        public void FixedSizes()
        {
            Assert.AreEqual(1, E.FixedSize(typeof(bool)));
            Assert.AreEqual(1, E.FixedSize(typeof(byte)));
            Assert.AreEqual(2, E.FixedSize(typeof(short)));
            Assert.AreEqual(2, E.FixedSize(typeof(char)));
            Assert.AreEqual(4, E.FixedSize(typeof(int)));
            Assert.AreEqual(4, E.FixedSize(typeof(float)));
            Assert.AreEqual(8, E.FixedSize(typeof(long)));
            Assert.AreEqual(8, E.FixedSize(typeof(double)));
            Assert.AreEqual(8, E.FixedSize(typeof(Vector2)));
            Assert.AreEqual(12, E.FixedSize(typeof(Vector3)));
            Assert.AreEqual(16, E.FixedSize(typeof(Quaternion)));
            Assert.AreEqual(16, E.FixedSize(typeof(Color)));
            Assert.AreEqual(4, E.FixedSize(typeof(Color32)));
            Assert.AreEqual(-1, E.FixedSize(typeof(string)));
            Assert.AreEqual(-1, E.FixedSize(typeof(int[])));
            Assert.AreEqual(-1, E.FixedSize(null));
        }

        [Test]
        public void ValueSizes()
        {
            Assert.AreEqual(0, E.SizeOf(null));
            Assert.AreEqual(4, E.SizeOf(5));
            Assert.AreEqual(12, E.SizeOf(Vector3.one));
            Assert.AreEqual(E.LengthPrefixBytes + 3, E.SizeOf("abc"));
            Assert.AreEqual(E.LengthPrefixBytes + 2, E.SizeOf("é"));
            Assert.AreEqual(E.LengthPrefixBytes + 4 * 5, E.SizeOf(new int[5]));
            Assert.AreEqual(E.LengthPrefixBytes + 12 * 2, E.SizeOf(new Vector3[2]));
            Assert.AreEqual(E.LengthPrefixBytes + (E.LengthPrefixBytes + 1) + (E.LengthPrefixBytes + 2) + 0, E.SizeOf(new[] { "a", "bc", null }));
            Assert.AreEqual(E.LengthPrefixBytes + 4, E.SizeOf(new VRCUrl("http")));
            Assert.AreEqual(0, E.SizeOf(new object()));
        }

        [Test]
        public void LayoutAndEstimate()
        {
            var layout = E.Layout(new List<(string, Type)> { ("a", typeof(int)), ("b", typeof(Vector3)), ("s", typeof(string)), ("arr", typeof(float[])), ("u", null) });
            Assert.AreEqual(5, layout.count);
            Assert.AreEqual(16, layout.fixedBytes);
            CollectionAssert.AreEqual(new[] { "s", "arr", "u" }, layout.variableNames);
            var values = new Dictionary<string, object> { { "s", "hello" }, { "arr", new float[3] }, { "u", null } };
            Assert.AreEqual(16 + (E.LengthPrefixBytes + 5) + (E.LengthPrefixBytes + 12), E.Estimate(layout, n => values[n]));
            Assert.AreEqual(16, E.Estimate(layout, _ => throw new InvalidOperationException()));
            Assert.AreEqual(0, E.Estimate(E.Layout(null), null));
        }
    }

    public class NetRecorderTests
    {
        const int B1 = 2001, B2 = 2002, B3 = 2003;
        int _frame;
        bool _recording;
        float _rate;
        BehaviourInfo _manual, _continuous, _none;
        readonly Dictionary<int, int> _bytes = new Dictionary<int, int>();
        readonly HashSet<int> _owned = new HashSet<int>();

        static BehaviourInfo Info(string s, NetSync sync) => new BehaviourInfo { script = s, objectName = s, objectPath = s, objectKey = s, syncMode = sync };

        [SetUp]
        public void SetUp()
        {
            _recording = R.Recording;
            _rate = R.ContinuousSendRate;
            R.Recording = false;
            R.Clear();
            R.SetProbeCosts(0, 0, 0, 0);
            _manual = Info("M", NetSync.Manual);
            _continuous = Info("C", NetSync.Continuous);
            _none = Info("N", NetSync.None);
            R.RegisterLoadedBehaviour(B1, _manual);
            R.RegisterLoadedBehaviour(B2, _continuous);
            R.RegisterLoadedBehaviour(B3, _none);
            _bytes.Clear();
            _owned.Clear();
            _frame = 1;
            R.FrameSource = () => _frame;
            R.DeltaSource = () => 0.05f;
            R.NetBytesOverride = i => _bytes.TryGetValue(i.id, out int b) ? b : -1;
            R.NetOwnerOverride = i => _owned.Contains(i.id);
            R.Recording = true;
        }

        [TearDown]
        public void TearDown()
        {
            R.Recording = false;
            R.Clear();
            R.FrameSource = null;
            R.DeltaSource = null;
            R.NetBytesOverride = null;
            R.NetOwnerOverride = null;
            R.ContinuousSendRate = _rate;
            R.ResetCalibration();
            R.Recording = _recording;
            R.SyncProbes();
        }

        static NetStat Net(Frame f, int id)
        {
            for (int i = 0; i < f.netCount; i++) if (f.net[i].behaviourId == id) return f.net[i];
            Assert.Fail("no net stat for " + id);
            return default;
        }

        static bool HasNet(Frame f, int id)
        {
            for (int i = 0; i < f.netCount; i++) if (f.net[i].behaviourId == id) return true;
            return false;
        }

        void Event(Action body)
        {
            int s = R.PushCore(B1, _manual, 0x10);
            body();
            R.Pop(s, false);
        }

        [Test]
        public void ManualRequestsCoalesceAtFrameEnd()
        {
            _bytes[B1] = 40;
            Event(() =>
            {
                R.NetRequest(_manual, true);
                R.NetRequest(_manual, true);
            });
            Event(() => R.NetRequest(_manual, true));
            Event(() => R.NetRequest(_manual, false));
            var f0 = R.GetFrame(R.NewestFrame);
            Assert.AreEqual(0, Net(f0, B1).serializations, "serializations are counted at frame end");
            _frame++;
            Event(() => { });
            var n = Net(f0, B1);
            Assert.IsTrue(f0.isSealed);
            Assert.AreEqual(4, n.requests);
            Assert.AreEqual(1, n.requestsIgnored);
            Assert.AreEqual(1, n.serializations);
            Assert.AreEqual(40, n.estBytes);
            Assert.AreEqual(0, n.overLimit);
            Assert.IsFalse(HasNet(R.GetFrame(R.NewestFrame), B1));
        }

        [Test]
        public void IgnoredWhenNotManualAndOverLimit()
        {
            _bytes[B1] = E.ManualLimitBytes + 1;
            Event(() =>
            {
                R.NetRequest(_none, true);
                R.NetRequest(_continuous, true);
                R.NetRequest(_manual, true);
            });
            R.Recording = false;
            var f = R.GetFrame(R.NewestFrame);
            Assert.AreEqual(1, Net(f, B3).requestsIgnored);
            Assert.AreEqual(0, Net(f, B3).serializations);
            Assert.AreEqual(1, Net(f, B2).requestsIgnored);
            Assert.AreEqual(1, Net(f, B1).serializations);
            Assert.AreEqual(1, Net(f, B1).overLimit);
        }

        [Test]
        public void ContinuousProjectedWhileOwned()
        {
            R.ContinuousSendRate = 10f;
            var c = new BehaviourInfo { script = "C2", objectName = "C2", objectPath = "C2", objectKey = "C2", syncMode = NetSync.Continuous };
            R.RegisterLoadedBehaviour(B2 + 10, c);
            R.AddContinuousForTest(c);
            _bytes[c.id] = 250;
            _owned.Add(c.id);
            for (int i = 0; i < 4; i++) { Event(() => { }); _frame++; }
            Event(() => { });
            long ser = 0, bytes = 0, over = 0;
            for (int s = 0; s < R.NewestFrame; s++)
            {
                var f = R.GetFrame(s);
                if (!HasNet(f, c.id)) continue;
                ser += Net(f, c.id).serializations;
                bytes += Net(f, c.id).estBytes;
                over += Net(f, c.id).overLimit;
            }
            Assert.AreEqual(2, ser);
            Assert.AreEqual(500, bytes);
            Assert.AreEqual(2, over);
        }

        [Test]
        public void EventsTransfersAndRateLimit()
        {
            Event(() =>
            {
                R.NetEvent(_manual, NetworkEventTarget.All, 8, false);
                R.NetEvent(_manual, NetworkEventTarget.Others, 4, true);
                R.NetEvent(_manual, NetworkEventTarget.Self, 0, false);
                R.NetTransfer(_none);
            });
            var f = R.GetFrame(R.NewestFrame);
            var n = Net(f, B1);
            Assert.AreEqual(1, n.eventsAll);
            Assert.AreEqual(1, n.eventsOthers);
            Assert.AreEqual(1, n.eventsSelf);
            Assert.AreEqual(0, n.eventsOwner);
            Assert.AreEqual(12, n.eventBytes);
            Assert.AreEqual(1, n.rateLimitHits);
            Assert.AreEqual(1, Net(f, B3).ownershipTransfers);

            for (int i = 0; i < 5; i++) Assert.IsFalse(R.NetRateHit(B1, "Fire", 5, 10.0 + i * 0.01));
            Assert.IsTrue(R.NetRateHit(B1, "Fire", 5, 10.1));
            Assert.IsFalse(R.NetRateHit(B1, "Other", 5, 10.1));
            Assert.IsFalse(R.NetRateHit(B1, "Fire", 5, 11.01));
        }

        [Test]
        public void ModelAggregatesAndCaptureRoundTrips()
        {
            _bytes[B1] = 100;
            for (int i = 0; i < 4; i++)
            {
                Event(() => { R.NetRequest(_manual, true); R.NetEvent(_manual, NetworkEventTarget.All, 10, false); });
                _frame++;
            }
            R.Recording = false;
            var model = new UdonProfilerModel();
            var v = model.Build(new ViewQuery { from = 0, to = R.NewestFrame, refreshMembership = true });
            Assert.IsTrue(v.network.HasData);
            var row = v.NetOf(B1);
            Assert.NotNull(row);
            Assert.AreEqual(4, row.serializations);
            Assert.AreEqual(400, row.estBytes);
            Assert.AreEqual(4, row.eventsAll);
            Assert.AreEqual(0.2, row.seconds, 1e-4);
            Assert.AreEqual(40, row.eventBytes);
            Assert.AreEqual(440 / 0.2, row.BytesPerSec, 1e-2);
            Assert.AreEqual(440 / 0.2, v.network.bytesPerSec, 1e-2);
            Assert.AreEqual(100, row.BytesPerSerialization, 1e-9);
            StringAssert.Contains("\"M\"", UdonProfilerModel.NetworkCsv(v.network));

            string file = Path.Combine(Path.GetTempPath(), "udonprofiler-net-" + Guid.NewGuid().ToString("N") + ".udonprofile");
            try
            {
                string vm = R.RecordedVm;
                Assert.NotNull(vm);
                UdonProfilerCapture.Save(file);
                UdonProfilerCapture.Load(file);
                Assert.AreEqual(vm, R.RecordedVm);
                Assert.AreEqual(NetSync.Manual, R.GetInfo(B1).syncMode);
                var v2 = new UdonProfilerModel().Build(new ViewQuery { from = 0, to = R.NewestFrame, refreshMembership = true });
                var row2 = v2.NetOf(B1);
                Assert.NotNull(row2);
                Assert.AreEqual(400, row2.estBytes);
                Assert.AreEqual(0.2, row2.seconds, 1e-4);
            }
            finally { if (File.Exists(file)) File.Delete(file); }
        }

        [Test]
        public void ClearForgetsRecordedVm()
        {
            Event(() => { });
            Assert.NotNull(R.RecordedVm);
            R.Clear();
            Assert.IsNull(R.RecordedVm);
            Assert.IsNull(R.RecordedMethodHooks);
        }
    }
}
