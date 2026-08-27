using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Unity.Profiling;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace MagmaVRC.UdonProfiler
{
    public struct Sample
    {
        public int behaviourId;
        public uint entry;
        public int parent;
        public long startTicks;
        public long inclusiveTicks;
        public long childTicks;
        public long externTicks;
        public int externCount;
        public int scheduledByBehaviour;
        public uint scheduledByEntry;
        public bool halted;
        public long startAlloc;
        public long allocBytes;
        public long childAlloc;
        internal bool markerActive;
    }

    public struct ExternStat
    {
        public int count;
        public long ticks;
    }

    public class Frame
    {
        public int frameNumber;
        public readonly List<Sample> samples = new List<Sample>(256);
        public readonly Dictionary<(int behaviourId, uint entry, int sig), ExternStat> externs = new Dictionary<(int, uint, int), ExternStat>();
        public long totalTicks;
        public int externCount;

        public void Reset(int number)
        {
            frameNumber = number;
            samples.Clear();
            externs.Clear();
            totalTicks = 0;
            externCount = 0;
        }
    }

    public class BehaviourInfo
    {
        public UdonBehaviour behaviour;
        public string script;
        public string objectName;
        public string objectPath;
        public Dictionary<uint, string> events = new Dictionary<uint, string>();
    }

    public static class UdonProfilerRecorder
    {
        public const int Capacity = 600;
        public static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        public static bool Recording = true;
        public static bool ExternTiming = true;
        public static bool DetailedExterns = true;
        public static bool EmitMarkers = true;
        public static double PauseAboveMs = 0;
        public static int PausedOnFrame = -1;
        public static int Version;
        public static int FrameCount;
        public static int NewestFrame = -1;
        internal static int MainThreadId = Environment.CurrentManagedThreadId;

        static readonly Frame[] _ring = new Frame[Capacity];
        static Frame _current;
        static int _currentFrameNumber = -1;
        static readonly List<int> _stack = new List<int>(32);
        static readonly Dictionary<int, BehaviourInfo> _infos = new Dictionary<int, BehaviourInfo>();
        static readonly Dictionary<(int, string), (int behaviour, uint entry)> _scheduled = new Dictionary<(int, string), (int, uint)>();
        static Func<UdonBehaviour, IUdonProgram> _programGetter;

        static readonly List<string> _sigs = new List<string>();
        static readonly Dictionary<string, int> _sigIds = new Dictionary<string, int>();
        static readonly Dictionary<(int, uint), ProfilerMarker> _markers = new Dictionary<(int, uint), ProfilerMarker>();

        static readonly Func<long> _allocGetter = MakeAllocGetter();
        public static bool AllocSupported => _allocGetter != null;

        static Func<long> MakeAllocGetter()
        {
            try
            {
                var m = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null) return null;
                var f = (Func<long>)m.CreateDelegate(typeof(Func<long>));
                if (f() < 0) return null;
                return f;
            }
            catch { return null; }
        }

        static UdonProfilerRecorder()
        {
            for (int i = 0; i < Capacity; i++) _ring[i] = new Frame();
        }

        internal static void SetProgramGetter(Func<UdonBehaviour, IUdonProgram> getter) => _programGetter = getter;

        public static Frame GetFrame(int index) => index < 0 ? null : _ring[index % Capacity];

        internal static int SigId(string sig)
        {
            if (_sigIds.TryGetValue(sig, out int id)) return id;
            id = _sigs.Count;
            _sigs.Add(sig);
            _sigIds[sig] = id;
            return id;
        }

        public static string Sig(int id) => id >= 0 && id < _sigs.Count ? _sigs[id] : "?";

        public static void Clear()
        {
            _stack.Clear();
            _current = null;
            _currentFrameNumber = -1;
            FrameCount = 0;
            NewestFrame = -1;
            _scheduled.Clear();
            _infos.Clear();
            _markers.Clear();
            PausedOnFrame = -1;
            Version++;
        }

        public static BehaviourInfo GetInfo(int behaviourId) => _infos.TryGetValue(behaviourId, out var i) ? i : null;

        public static string EventName(int behaviourId, uint entry)
        {
            var info = GetInfo(behaviourId);
            if (info == null) return "0x" + entry.ToString("X");
            if (info.events.TryGetValue(entry, out var n)) return n;
            n = ResolveEventName(info.behaviour, entry);
            info.events[entry] = n;
            return n;
        }

        static string ResolveEventName(UdonBehaviour ub, uint entry)
        {
            try
            {
                var program = ub != null && _programGetter != null ? _programGetter(ub) : null;
                if (program != null && program.EntryPoints.TryGetSymbolFromAddress(entry, out string name)) return name;
            }
            catch { }
            return "0x" + entry.ToString("X");
        }

        static BehaviourInfo Register(UdonBehaviour ub, int id)
        {
            var info = new BehaviourInfo { behaviour = ub, objectName = ub.name, objectPath = Path(ub.transform) };
            info.script = UdonProfilerHooks.ScriptName(ub);
            _infos[id] = info;
            return info;
        }

        static string Path(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        static void BeginFrame(int number)
        {
            if (_current != null)
            {
                Version++;
                if (PauseAboveMs > 0 && _current.totalTicks * MsPerTick > PauseAboveMs)
                {
                    Recording = false;
                    PausedOnFrame = NewestFrame;
                    return;
                }
            }
            NewestFrame++;
            if (FrameCount < Capacity) FrameCount++;
            _current = _ring[NewestFrame % Capacity];
            _current.Reset(number);
            _currentFrameNumber = number;
        }

        static ProfilerMarker Marker(BehaviourInfo info, int id, uint entry)
        {
            if (_markers.TryGetValue((id, entry), out var m)) return m;
            m = new ProfilerMarker("Udon " + (info?.script ?? id.ToString()) + "." + EventName(id, entry));
            _markers[(id, entry)] = m;
            return m;
        }

        public static int Push(UdonBehaviour ub, uint entry)
        {
            if (!Recording || Environment.CurrentManagedThreadId != MainThreadId) return -1;
            int frame = Time.frameCount;
            if (frame != _currentFrameNumber)
            {
                BeginFrame(frame);
                if (!Recording) return -1;
            }

            int id = ub.GetInstanceID();
            if (!_infos.TryGetValue(id, out var info)) info = Register(ub, id);

            var s = new Sample
            {
                behaviourId = id,
                entry = entry,
                parent = _stack.Count > 0 ? _stack[_stack.Count - 1] : -1,
                scheduledByBehaviour = 0,
            };
            if (s.parent < 0 && _scheduled.Count > 0)
            {
                var key = (id, EventName(id, entry));
                if (_scheduled.TryGetValue(key, out var by))
                {
                    s.scheduledByBehaviour = by.behaviour;
                    s.scheduledByEntry = by.entry;
                    _scheduled.Remove(key);
                }
            }
            if (EmitMarkers)
            {
                Marker(info, id, entry).Begin();
                s.markerActive = true;
            }
            if (_allocGetter != null) s.startAlloc = _allocGetter();
            s.startTicks = Stopwatch.GetTimestamp();
            int index = _current.samples.Count;
            _current.samples.Add(s);
            _stack.Add(index);
            return index;
        }

        public static void Pop(int index, bool halted)
        {
            if (index < 0 || _current == null || index >= _current.samples.Count) return;
            long now = Stopwatch.GetTimestamp();
            var samples = _current.samples;
            var s = samples[index];
            s.inclusiveTicks = now - s.startTicks;
            if (_allocGetter != null && s.startAlloc > 0) s.allocBytes = Math.Max(0, _allocGetter() - s.startAlloc);
            s.halted = halted;
            samples[index] = s;
            if (s.markerActive) _markers[(s.behaviourId, s.entry)].End();

            int top = _stack.Count - 1;
            if (top >= 0 && _stack[top] == index) _stack.RemoveAt(top);
            else
            {
                int at = _stack.LastIndexOf(index);
                if (at >= 0) _stack.RemoveRange(at, _stack.Count - at);
            }

            if (s.parent >= 0)
            {
                var p = samples[s.parent];
                p.childTicks += s.inclusiveTicks;
                p.childAlloc += s.allocBytes;
                samples[s.parent] = p;
            }
            else _current.totalTicks += s.inclusiveTicks;
        }

        public static void Scheduled(UdonBehaviour target, string eventName)
        {
            if (!Recording || target == null || Environment.CurrentManagedThreadId != MainThreadId) return;
            if (_stack.Count == 0 || _current == null) return;
            var top = _current.samples[_stack[_stack.Count - 1]];
            _scheduled[(target.GetInstanceID(), eventName)] = (top.behaviourId, top.entry);
        }

        public static bool ExternBegin(out long start, out long childTicks)
        {
            if (!Recording || !ExternTiming || _stack.Count == 0 || Environment.CurrentManagedThreadId != MainThreadId)
            {
                start = 0; childTicks = 0;
                return false;
            }
            childTicks = _current.samples[_stack[_stack.Count - 1]].childTicks;
            start = Stopwatch.GetTimestamp();
            return true;
        }

        public static void ExternEnd(int sigId, long start, long childTicksBefore)
        {
            if (_current == null || _stack.Count == 0) return;
            int index = _stack[_stack.Count - 1];
            var samples = _current.samples;
            var s = samples[index];
            long elapsed = Stopwatch.GetTimestamp() - start - (s.childTicks - childTicksBefore);
            s.externTicks += elapsed;
            s.externCount++;
            samples[index] = s;
            _current.externCount++;

            if (!DetailedExterns) return;
            var key = (s.behaviourId, s.entry, sigId);
            _current.externs.TryGetValue(key, out var st);
            st.count++;
            st.ticks += elapsed;
            _current.externs[key] = st;
        }

        const int CaptureMagic = 0x55445031;

        public static void SaveCapture(string path)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(CaptureMagic);
            w.Write((long)Stopwatch.Frequency);

            var behaviourIds = new HashSet<int>();
            int count = FrameCount;
            int oldest = NewestFrame - count + 1;
            for (int f = oldest; f <= NewestFrame; f++)
            {
                var frame = GetFrame(f);
                foreach (var s in frame.samples) behaviourIds.Add(s.behaviourId);
                foreach (var kv in frame.externs) behaviourIds.Add(kv.Key.behaviourId);
            }

            w.Write(behaviourIds.Count);
            foreach (int id in behaviourIds)
            {
                var info = GetInfo(id);
                w.Write(id);
                w.Write(info?.script ?? "?");
                w.Write(info?.objectName ?? "?");
                w.Write(info?.objectPath ?? "?");
                var events = info?.events ?? new Dictionary<uint, string>();
                w.Write(events.Count);
                foreach (var kv in events) { w.Write(kv.Key); w.Write(kv.Value); }
            }

            w.Write(_sigs.Count);
            foreach (var s in _sigs) w.Write(s);

            w.Write(count);
            for (int f = oldest; f <= NewestFrame; f++)
            {
                var frame = GetFrame(f);
                w.Write(frame.frameNumber);
                w.Write(frame.totalTicks);
                w.Write(frame.externCount);
                w.Write(frame.samples.Count);
                foreach (var s in frame.samples)
                {
                    w.Write(s.behaviourId); w.Write(s.entry); w.Write(s.parent);
                    w.Write(s.startTicks); w.Write(s.inclusiveTicks); w.Write(s.childTicks);
                    w.Write(s.externTicks); w.Write(s.externCount);
                    w.Write(s.scheduledByBehaviour); w.Write(s.scheduledByEntry);
                    w.Write(s.halted);
                    w.Write(s.allocBytes); w.Write(s.childAlloc);
                }
                w.Write(frame.externs.Count);
                foreach (var kv in frame.externs)
                {
                    w.Write(kv.Key.behaviourId); w.Write(kv.Key.entry); w.Write(kv.Key.sig);
                    w.Write(kv.Value.count); w.Write(kv.Value.ticks);
                }
            }
        }

        public static void LoadCapture(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != CaptureMagic) throw new IOException("not an Udon profiler capture");
            long freq = r.ReadInt64();
            double tickScale = (double)Stopwatch.Frequency / freq;

            Recording = false;
            Clear();

            int behaviourCount = r.ReadInt32();
            for (int i = 0; i < behaviourCount; i++)
            {
                int id = r.ReadInt32();
                var info = new BehaviourInfo { script = r.ReadString(), objectName = r.ReadString(), objectPath = r.ReadString() };
                int evCount = r.ReadInt32();
                for (int e = 0; e < evCount; e++) info.events[r.ReadUInt32()] = r.ReadString();
                _infos[id] = info;
            }

            int sigCount = r.ReadInt32();
            var sigMap = new int[sigCount];
            for (int i = 0; i < sigCount; i++) sigMap[i] = SigId(r.ReadString());

            int frames = Math.Min(r.ReadInt32(), Capacity);
            long T(long t) => (long)(t * tickScale);
            for (int f = 0; f < frames; f++)
            {
                var frame = _ring[f];
                frame.Reset(r.ReadInt32());
                frame.totalTicks = T(r.ReadInt64());
                frame.externCount = r.ReadInt32();
                int sampleCount = r.ReadInt32();
                for (int i = 0; i < sampleCount; i++)
                {
                    var s = new Sample
                    {
                        behaviourId = r.ReadInt32(), entry = r.ReadUInt32(), parent = r.ReadInt32(),
                        startTicks = T(r.ReadInt64()), inclusiveTicks = T(r.ReadInt64()), childTicks = T(r.ReadInt64()),
                        externTicks = T(r.ReadInt64()), externCount = r.ReadInt32(),
                        scheduledByBehaviour = r.ReadInt32(), scheduledByEntry = r.ReadUInt32(),
                        halted = r.ReadBoolean(),
                    };
                    s.allocBytes = r.ReadInt64(); s.childAlloc = r.ReadInt64();
                    frame.samples.Add(s);
                }
                int externCount = r.ReadInt32();
                for (int i = 0; i < externCount; i++)
                {
                    var key = (r.ReadInt32(), r.ReadUInt32(), sigMap[r.ReadInt32()]);
                    frame.externs[key] = new ExternStat { count = r.ReadInt32(), ticks = T(r.ReadInt64()) };
                }
            }
            FrameCount = frames;
            NewestFrame = frames - 1;
            Version++;
        }
    }
}
