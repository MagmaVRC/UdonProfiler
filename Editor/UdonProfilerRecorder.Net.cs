using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace MagmaVRC.UdonProfiler
{
    public static partial class UdonProfilerRecorder
    {
        sealed class RateWindow
        {
            public double[] times;
            public int head, count;
        }

        /// <summary>Assumed send rate, in serializations per second, of an owned Continuous behaviour with synced variables.</summary>
        public static float ContinuousSendRate = 10f;

        internal static Func<BehaviourInfo, int> NetBytesOverride;
        internal static Func<BehaviourInfo, bool> NetOwnerOverride;
        internal static Func<float> DeltaSource;

        static readonly List<BehaviourInfo> _netPending = new List<BehaviourInfo>();
        static readonly List<BehaviourInfo> _netContinuous = new List<BehaviourInfo>();
        static readonly Dictionary<(int, string), RateWindow> _rates = new Dictionary<(int, string), RateWindow>();
        static readonly List<(string name, Type type)> _layoutScratch = new List<(string, Type)>();

        static void ClearNet()
        {
            _netPending.Clear();
            _netContinuous.Clear();
            _rates.Clear();
        }

        internal static BehaviourInfo InfoFor(UdonBehaviour ub)
        {
            if (ub == null) return null;
            int id = ub.GetInstanceID();
            return _infos.TryGetValue(id, out var info) ? info : Register(ub, id);
        }

        internal static void AddContinuousForTest(BehaviourInfo info) => _netContinuous.Add(info);

        internal static BehaviourInfo CurrentEventInfo => _curEvent >= 0 ? _curInfo : null;

        static bool NetFrame()
        {
            if (!_recording || Environment.CurrentManagedThreadId != MainThreadId) return false;
            if (IsLoadedCapture)
            {
                if (_sp != 0) return false;
                Clear();
            }
            int frame = FrameSource == null ? Time.frameCount : FrameSource();
            if (_current == null || frame != _currentFrameNumber)
            {
                if (_sp != 0) return false;
                BeginFrame(frame);
                if (!_recording || _current == null) return false;
            }
            return true;
        }

        static int NetSlot(BehaviourInfo info)
        {
            var f = _current;
            int stamp = f.serial + 1;
            if (info.netStamp == stamp && info.netSlot < f.netCount && f.net[info.netSlot].behaviourId == info.id) return info.netSlot;
            if (f.netCount == f.net.Length)
            {
                long a0 = SelfBegin();
                Array.Resize(ref f.net, Math.Max(8, f.net.Length * 2));
                SelfEnd(a0);
            }
            f.AddNet(info.id, out int idx);
            info.netStamp = stamp;
            info.netSlot = idx;
            return idx;
        }

        internal static void NetRequest(BehaviourInfo info, bool owner)
        {
            if (info == null || !NetFrame()) return;
            int slot = NetSlot(info);
            ref var s = ref _current.net[slot];
            s.requests++;
            if (info.syncMode != NetSync.Manual || !owner)
            {
                s.requestsIgnored++;
                return;
            }
            int stamp = _current.serial + 1;
            if (info.netPendingStamp == stamp) return;
            info.netPendingStamp = stamp;
            if (_netPending.Count == _netPending.Capacity)
            {
                long a0 = SelfBegin();
                _netPending.Capacity = Math.Max(16, _netPending.Capacity * 2);
                SelfEnd(a0);
            }
            _netPending.Add(info);
        }

        internal static void NetEvent(BehaviourInfo sender, NetworkEventTarget target, int payloadBytes, bool rateLimited)
        {
            if (sender == null || !NetFrame()) return;
            int slot = NetSlot(sender);
            ref var s = ref _current.net[slot];
            switch (target)
            {
                case NetworkEventTarget.All: s.eventsAll++; break;
                case NetworkEventTarget.Owner: s.eventsOwner++; break;
                case NetworkEventTarget.Others: s.eventsOthers++; break;
                default: s.eventsSelf++; break;
            }
            s.eventBytes += Math.Max(0, payloadBytes);
            if (rateLimited) s.rateLimitHits++;
        }

        internal static void NetTransfer(BehaviourInfo info)
        {
            if (info == null || !NetFrame()) return;
            int slot = NetSlot(info);
            _current.net[slot].ownershipTransfers++;
        }

        /// <summary>Records one send of a rate-limited network event and returns true when it exceeds <paramref name="maxPerSecond"/> within the last second.</summary>
        internal static bool NetRateHit(int receiverId, string eventName, int maxPerSecond, double now)
        {
            if (maxPerSecond <= 0) return false;
            int cap = Math.Min(maxPerSecond, 100);
            var key = (receiverId, eventName ?? "");
            if (!_rates.TryGetValue(key, out var w) || w.times.Length != cap)
            {
                long a0 = SelfBegin();
                w = new RateWindow { times = new double[cap] };
                _rates[key] = w;
                SelfEnd(a0);
            }
            while (w.count > 0 && now - w.times[w.head] >= 1.0)
            {
                w.head = (w.head + 1) % w.times.Length;
                w.count--;
            }
            if (w.count >= w.times.Length) return true;
            w.times[(w.head + w.count) % w.times.Length] = now;
            w.count++;
            return false;
        }

        static void FlushNet()
        {
            var f = _current;
            if (f == null || (_netPending.Count == 0 && _netContinuous.Count == 0)) return;
            long a0 = SelfBegin();
            try
            {
                int stamp = f.serial + 1;
                for (int i = 0; i < _netPending.Count; i++)
                {
                    var info = _netPending[i];
                    if (info.netPendingStamp != stamp) continue;
                    info.netPendingStamp = 0;
                    int bytes = Math.Max(0, NetBytes(info));
                    int slot = NetSlot(info);
                    ref var s = ref f.net[slot];
                    s.serializations++;
                    s.estBytes += bytes;
                    if (bytes > UdonProfilerNetEstimate.ManualLimitBytes) s.overLimit++;
                }
                _netPending.Clear();
                float dt = f.deltaTime;
                if (ContinuousSendRate <= 0 || dt <= 0) return;
                for (int i = 0; i < _netContinuous.Count; i++)
                {
                    var info = _netContinuous[i];
                    if (!NetOwned(info)) { info.netContinuousAcc = 0; continue; }
                    info.netContinuousAcc += dt * ContinuousSendRate;
                    int k = (int)info.netContinuousAcc;
                    if (k <= 0) continue;
                    info.netContinuousAcc -= k;
                    int bytes = NetBytes(info);
                    if (bytes < 0) continue;
                    int slot = NetSlot(info);
                    ref var s = ref f.net[slot];
                    s.serializations += k;
                    s.estBytes += (long)bytes * k;
                    if (bytes > UdonProfilerNetEstimate.ContinuousLimitBytes) s.overLimit += k;
                }
            }
            catch (Exception e) { WarnOnce("network estimate failed", e); _netPending.Clear(); }
            finally { SelfEnd(a0); }
        }

        static bool NetOwned(BehaviourInfo info)
        {
            var over = NetOwnerOverride;
            if (over != null) return over(info);
            var ub = info.behaviour;
            if (ub == null) return false;
            try { return Networking.IsOwner(ub.gameObject); }
            catch (Exception) { return false; }
        }

        internal static bool IsLocalOwner(GameObject go)
        {
            if (go == null) return false;
            try { return Networking.IsOwner(go); }
            catch (Exception) { return true; }
        }

        static int NetBytes(BehaviourInfo info)
        {
            var over = NetBytesOverride;
            if (over != null) return over(info);
            var ub = info.behaviour;
            if (ub == null) return -1;
            object program = _programRef != null ? _programRef(ub) : null;
            if (program == null) return -1;
            if (!ReferenceEquals(program, info.netProgram) || info.netLayout == null)
            {
                info.netProgram = program;
                info.netLayout = BuildLayout(ub);
            }
            var layout = info.netLayout;
            if (layout.count == 0) return -1;
            return layout.variableNames.Length == 0 ? layout.fixedBytes : UdonProfilerNetEstimate.Estimate(layout, ub.GetProgramVariable);
        }

        static NetLayout BuildLayout(UdonBehaviour ub)
        {
            _layoutScratch.Clear();
            IUdonSyncMetadataTable table = null;
            try { table = ub.SyncMetadataTable; }
            catch (Exception) { table = null; }
            if (table != null)
                foreach (var m in table.GetAllSyncMetadata())
                {
                    if (m == null || string.IsNullOrEmpty(m.Name)) continue;
                    Type t;
                    try { t = ub.GetProgramVariableType(m.Name); }
                    catch (Exception) { t = null; }
                    _layoutScratch.Add((m.Name, t));
                }
            var layout = UdonProfilerNetEstimate.Layout(_layoutScratch);
            _layoutScratch.Clear();
            return layout;
        }
    }
}
