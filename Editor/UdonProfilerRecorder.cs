using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
        public readonly Dictionary<(int behaviourId, uint entry, string sig), ExternStat> externs = new Dictionary<(int, uint, string), ExternStat>();
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
        public static double PauseAboveMs = 0;
        public static int PausedOnFrame = -1;
        public static int Version;
        public static int FrameCount;
        public static int NewestFrame = -1;
        internal static int MainThreadId = Thread.CurrentThread.ManagedThreadId;

        static readonly Frame[] _ring = new Frame[Capacity];
        static Frame _current;
        static int _currentFrameNumber = -1;
        static readonly List<int> _stack = new List<int>(32);
        static readonly Dictionary<int, BehaviourInfo> _infos = new Dictionary<int, BehaviourInfo>();
        static readonly Dictionary<(int, string), (int behaviour, uint entry)> _scheduled = new Dictionary<(int, string), (int, uint)>();
        static Func<UdonBehaviour, IUdonProgram> _programGetter;

        static UdonProfilerRecorder()
        {
            for (int i = 0; i < Capacity; i++) _ring[i] = new Frame();
        }

        internal static void SetProgramGetter(Func<UdonBehaviour, IUdonProgram> getter) => _programGetter = getter;

        public static Frame GetFrame(int index) => index < 0 ? null : _ring[index % Capacity];

        public static void Clear()
        {
            _stack.Clear();
            _current = null;
            _currentFrameNumber = -1;
            FrameCount = 0;
            NewestFrame = -1;
            _scheduled.Clear();
            _infos.Clear();
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

        public static int Push(UdonBehaviour ub, uint entry)
        {
            if (!Recording || Thread.CurrentThread.ManagedThreadId != MainThreadId) return -1;
            int frame = Time.frameCount;
            if (frame != _currentFrameNumber)
            {
                BeginFrame(frame);
                if (!Recording) return -1;
            }

            int id = ub.GetInstanceID();
            if (!_infos.ContainsKey(id)) Register(ub, id);

            var s = new Sample
            {
                behaviourId = id,
                entry = entry,
                parent = _stack.Count > 0 ? _stack[_stack.Count - 1] : -1,
                startTicks = Stopwatch.GetTimestamp(),
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
            s.halted = halted;
            samples[index] = s;

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
                samples[s.parent] = p;
            }
            else _current.totalTicks += s.inclusiveTicks;
        }

        public static void Scheduled(UdonBehaviour target, string eventName)
        {
            if (!Recording || target == null || Thread.CurrentThread.ManagedThreadId != MainThreadId) return;
            if (_stack.Count == 0 || _current == null) return;
            var top = _current.samples[_stack[_stack.Count - 1]];
            _scheduled[(target.GetInstanceID(), eventName)] = (top.behaviourId, top.entry);
        }

        public static bool ExternBegin(out long start, out long childTicks)
        {
            if (!Recording || !ExternTiming || _stack.Count == 0 || Thread.CurrentThread.ManagedThreadId != MainThreadId)
            {
                start = 0; childTicks = 0;
                return false;
            }
            childTicks = _current.samples[_stack[_stack.Count - 1]].childTicks;
            start = Stopwatch.GetTimestamp();
            return true;
        }

        public static void ExternEnd(string sig, long start, long childTicksBefore)
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

            var key = (s.behaviourId, s.entry, sig);
            _current.externs.TryGetValue(key, out var st);
            st.count++;
            st.ticks += elapsed;
            _current.externs[key] = st;
        }
    }
}
