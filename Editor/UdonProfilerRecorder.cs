using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UdonSharp;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;
using Debug = UnityEngine.Debug;

namespace MagmaVRC.UdonProfiler
{
    public static partial class UdonProfilerRecorder
    {
        struct Entry
        {
            public PathKind kind;
            public bool folded;
            public bool markerActive;
            public int path;
            public int sample;
            public int behaviourId;
            public int scriptId;
            public uint target;
            public int stat;
            public int span;
            public uint ret;
            public long start, child, ext, startAlloc, selfAllocStart;
            public long overheadStart;
            public long instrStart, instrChild;
            public int extCount, fused;
            public int prevEvent;
            public UdonProgramMap prevMap;
            public BehaviourInfo prevInfo;
        }

        struct FusedSite { public int sig, gen, parent, path; }

        public const int Capacity = 600;
        public static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        const int MaxStack = 512;
        const int MaxFusedSites = 1 << 20;
        const uint TailRet = 0xFFFFFFFEu;
        const long NoAlloc = long.MinValue;

        static bool _recording = true, _methodTiming = true, _countFused = true;

        public static bool Recording
        {
            get => _recording;
            set
            {
                bool old = _recording;
                _recording = value;
                if (old && !value) SealCurrent();
                else if (!old && value)
                {
                    if (IsLoadedCapture) Clear();
                    _pauseGraceSerial = NewestFrame + 1;
                }
                SyncProbes();
            }
        }

        public static bool MethodTiming
        {
            get => _methodTiming;
            set
            {
                bool old = _methodTiming;
                _methodTiming = value;
                if (old && !value && _curEvent >= 0 && _current != null)
                {
                    long now = Stopwatch.GetTimestamp();
                    while (_sp - 1 > _curEvent) CloseTop(now);
                }
                SyncProbes();
            }
        }

        public static bool CountFusedExterns
        {
            get => _countFused;
            set
            {
                _countFused = value;
                SyncProbes();
            }
        }

        static bool _methodSpans = true, _detailedExterns = true, _emitMarkers = true, _trackAlloc = true;

        public static bool MethodSpans
        {
            get => _methodSpans;
            set => SetProbeOption(ref _methodSpans, value);
        }

        public static bool DetailedExterns
        {
            get => _detailedExterns;
            set => SetProbeOption(ref _detailedExterns, value);
        }

        public static bool EmitMarkers
        {
            get => _emitMarkers;
            set => SetProbeOption(ref _emitMarkers, value);
        }

        public static bool TrackAlloc
        {
            get => _trackAlloc;
            set => SetProbeOption(ref _trackAlloc, value);
        }

        static void SetProbeOption(ref bool field, bool value)
        {
            if (field == value) return;
            field = value;
            _calibrated = false;
            SyncProbes();
        }

        public static int MaxSpansPerFrame = 2048;
        public static int MaxStatsPerFrame = 4096;
        public static bool ExternTiming = true;
        public static double PauseAboveMs;
        public static int PausedOnFrame = -1;

        public static int Version;
        public static int Generation;
        public static int FrameCount;
        public static int NewestFrame = -1;
        public static bool IsLoadedCapture { get; private set; }
        public static bool AllocSupported => _allocGetter != null;
        static readonly PathTable _livePaths = new PathTable();
        static PathTable _paths = _livePaths, _calibPaths;
        public static PathTable Paths => _paths;

        /// <summary>Calibrated cost of one internal call plus return, in ns, subtracted from method and event times; 0 until calibrated.</summary>
        public static double ProbeCallNs => FxToNs(_callCostFx + _retCostFx);
        /// <summary>Calibrated cost of one fused-extern count, in ns, subtracted the same way.</summary>
        public static double ProbeFusedNs => FxToNs(_fusedCostFx);
        /// <summary>Calibrated recorder cost of one nested event push and pop outside the nested event's own time, in ns, subtracted from the enclosing frames and extern.</summary>
        public static double ProbeEventNs => FxToNs(_eventCostFx);

        internal static int MainThreadId = Environment.CurrentManagedThreadId;
        internal static int Viewers;
        internal static bool ProbesOutsidePlayMode;
        internal static Func<string, int> ArityOverride;
        internal static Func<int> FrameSource;

        static Entry[] _stack = new Entry[64];
        static int _sp;
        static int _drawDepth;
        static int _curEvent = -1;
        static UdonProgramMap _curMap;
        static BehaviourInfo _curInfo;
        static bool _methodsLive, _externsLive, _probeSynced;
        static readonly Frame[] _ring = new Frame[Capacity];
        static Frame _current;
        static int _currentFrameNumber = -1;
        static int[] _statSlot = new int[1024], _statStamp = new int[1024];
        static ProfilerMarker[] _markerOfPath = new ProfilerMarker[1024];
        static readonly object _siteLock = new object();
        static int _siteCount;
        static FusedSite[] _sites = new FusedSite[256];
        static long _overhead;
        static long _callCostFx, _retCostFx, _retMissCostFx, _fusedCostFx, _eventCostFx;
        static bool _calibrated;
        const int SizeHistory = 64;
        static readonly int[] _histSamples = new int[SizeHistory], _histStats = new int[SizeHistory], _histSpans = new int[SizeHistory];
        static int _histPos;
        static bool _externEndWarned, _siteCapWarned;
        static int _calibSite = -1;
        static long _selfAlloc;
        static int _selfDepth;
        static long _selfT0, _selfA0;
        static int _pauseGraceSerial = -1;
        static readonly Dictionary<int, BehaviourInfo> _infos = new Dictionary<int, BehaviourInfo>();
        static readonly List<BehaviourInfo> _byIndex = new List<BehaviourInfo>();
        static readonly Dictionary<int, List<(string ev, int behaviour, uint entry, int serial)>> _scheduled = new Dictionary<int, List<(string, int, uint, int)>>();
        static readonly List<int> _expiredKeys = new List<int>();
        static readonly List<UdonBehaviour> _components = new List<UdonBehaviour>(4);
        static readonly List<Transform> _chain = new List<Transform>(16);
        static readonly HashSet<string> _warned = new HashSet<string>();

        static AccessTools.FieldRef<UdonBehaviour, IUdonProgram> _programRef;
        static AccessTools.FieldRef<UdonBehaviour, AbstractSerializedUdonProgramAsset> _serializedRef;

        static readonly List<string> _sigs = new List<string>();
        static readonly Dictionary<string, int> _sigIds = new Dictionary<string, int>();
        static readonly List<string> _scripts = new List<string>();
        static readonly Dictionary<string, int> _scriptIds = new Dictionary<string, int>();

        static readonly Dictionary<string, int> _arity = new Dictionary<string, int>();
        static IUdonWrapper _arityWrapper;

        static FieldInfo _fastEnabledField;
        static bool _fastEnabledLooked;

        static readonly Func<long> _allocGetter = MakeAllocGetter();
        static readonly Func<long> _vmInstructions = MakeInstructionGetter();
        static Func<long> _instr;
        internal static Func<long> InstructionSource;

        static Func<long> MakeInstructionGetter()
        {
            try
            {
                var f = Type.GetType("MagmaVRC.UdonVM.FastUdonVMStats, MagmaVRC.UdonVM.Editor")?.GetField("Instructions", BindingFlags.Public | BindingFlags.Static);
                if (f == null || f.FieldType != typeof(long)) return null;
                return System.Linq.Expressions.Expression.Lambda<Func<long>>(System.Linq.Expressions.Expression.Field(null, f)).Compile();
            }
            catch (Exception) { return null; }
        }

        static long InstrNow()
        {
            var f = _instr;
            return f != null ? f() : 0;
        }

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

        static double FxToNs(long fx) => fx / 1024.0 * 1e9 / Stopwatch.Frequency;

        /// <summary>"fast VM probe", "stock VM transpiler", or "off: reason".</summary>
        public static string MethodHookStatus
        {
            get
            {
                string s = HookStatus(FastVmActive());
                if (!_methodTiming) s += " (paused)";
                return s;
            }
        }

        /// <summary>The VM ("Fast VM" or "stock VM") the frames in the ring were recorded on; null when unknown or empty.</summary>
        public static string RecordedVm { get; internal set; }

        /// <summary>The method hook status (as <see cref="MethodHookStatus"/>, without pause) when the frames in the ring were recorded; null when unknown or empty.</summary>
        public static string RecordedMethodHooks { get; internal set; }

        static string HookStatus(bool fast) => fast
            ? (UdonProfilerProbe.Found ? "fast VM probe" : "off: Fast VM without probe")
            : (UdonProfilerStockHooks.Patched ? "stock VM transpiler" : "off: stock VM not patched");

        public static bool MethodHooksActive => !MethodHookStatus.StartsWith("off", StringComparison.Ordinal);

        static bool FastVmActive()
        {
            if (!EditorApplication.isPlaying) return false;
            if (!_fastEnabledLooked)
            {
                _fastEnabledLooked = true;
                try { _fastEnabledField = Type.GetType("MagmaVRC.UdonVM.FastUdonVMStats, MagmaVRC.UdonVM.Editor")?.GetField("Enabled", BindingFlags.Public | BindingFlags.Static); }
                catch (Exception) { _fastEnabledField = null; }
            }
            try { return _fastEnabledField != null && _fastEnabledField.GetValue(null) is bool b && b; }
            catch (Exception) { return false; }
        }

        public static Frame GetFrame(int serial)
        {
            if (serial < 0 || serial > NewestFrame || serial < NewestFrame - FrameCount + 1) return null;
            return _ring[serial % Capacity];
        }

        public static BehaviourInfo GetInfo(int behaviourId) => _infos.TryGetValue(behaviourId, out var i) ? i : null;
        public static int BehaviourCount => _byIndex.Count;
        public static BehaviourInfo BehaviourAt(int index) => index >= 0 && index < _byIndex.Count ? _byIndex[index] : null;

        public static string Sig(int id)
        {
            lock (_siteLock) return id >= 0 && id < _sigs.Count ? _sigs[id] : "?";
        }

        public static int SigCount
        {
            get { lock (_siteLock) return _sigs.Count; }
        }

        internal static int SigId(string sig)
        {
            sig ??= "";
            lock (_siteLock)
            {
                if (_sigIds.TryGetValue(sig, out int id)) return id;
                id = _sigs.Count;
                _sigs.Add(sig);
                _sigIds[sig] = id;
                return id;
            }
        }

        public static int ScriptId(string script)
        {
            script ??= "";
            if (_scriptIds.TryGetValue(script, out int id)) return id;
            id = _scripts.Count;
            _scripts.Add(script);
            _scriptIds[script] = id;
            return id;
        }

        public static string ScriptName(int scriptId) => scriptId >= 0 && scriptId < _scripts.Count ? _scripts[scriptId] : "?";
        public static int ScriptCount => _scripts.Count;

        public static string EventName(int behaviourId, uint entry)
        {
            var info = GetInfo(behaviourId);
            if (info == null) return "0x" + entry.ToString("X");
            if (info.events.TryGetValue(entry, out var n)) return n;
            long a0 = SelfBegin();
            n = ResolveEventName(info, entry);
            info.events[entry] = n;
            SelfEnd(a0);
            return n;
        }

        static string ResolveEventName(BehaviourInfo info, uint entry)
        {
            try
            {
                var ub = info.behaviour;
                var program = ub != null && _programRef != null ? _programRef(ub) : null;
                if (program != null && program.EntryPoints.TryGetSymbolFromAddress(entry, out string name)) return name;
            }
            catch { }
            return "0x" + entry.ToString("X");
        }

        public static SourceRef Method(int behaviourId, uint target)
        {
            var info = GetInfo(behaviourId);
            if (info == null) return new SourceRef { name = "sub_0x" + target.ToString("X") };
            if (info.methods.TryGetValue(target, out var r)) return r;
            long a0 = SelfBegin();
            var map = info.map;
            if (map != null)
            {
                try
                {
                    if (!map.NamesResolved) map.ResolveNames();
                    r = map.Method(target);
                }
                catch (Exception e)
                {
                    WarnOnce("method naming failed", e);
                    r = default;
                }
            }
            if (string.IsNullOrEmpty(r.name)) r = new SourceRef { name = "sub_0x" + target.ToString("X"), file = r.file, line = r.line };
            if (map != null || info.behaviour == null) info.methods[target] = r;
            SelfEnd(a0);
            return r;
        }

        public static SourceRef SourceAt(int behaviourId, uint address)
        {
            var map = GetInfo(behaviourId)?.map;
            if (map == null) return default;
            try
            {
                if (!map.NamesResolved) map.ResolveNames();
                return map.At(address);
            }
            catch (Exception) { return default; }
        }

        public static string PathLabel(int path)
        {
            if (path == PathTable.OverflowPath) return "(path table full)";
            if (path < 0 || path >= Paths.Count) return "?";
            ref readonly var n = ref Paths[path];
            switch (n.kind)
            {
                case PathKind.Event: return EventName(n.behaviourId, n.key);
                case PathKind.Method: return Method(n.behaviourId, n.key).name;
                default: return Sig((int)n.key);
            }
        }

        public static bool PathTableFull => Paths.Full;

        public static void Clear()
        {
            _current = null;
            _currentFrameNumber = -1;
            _sp = 0;
            _drawDepth = 0;
            _curEvent = -1;
            _curMap = null;
            _curInfo = null;
            FrameCount = 0;
            NewestFrame = -1;
            _scheduled.Clear();
            _infos.Clear();
            _byIndex.Clear();
            Paths.Clear();
            Array.Clear(_statStamp, 0, _statStamp.Length);
            Array.Clear(_markerOfPath, 0, _markerOfPath.Length);
            Array.Clear(_stack, 0, _stack.Length);
            PausedOnFrame = -1;
            IsLoadedCapture = false;
            _selfAlloc = 0;
            _selfDepth = 0;
            _pauseGraceSerial = -1;
            Array.Clear(_histSamples, 0, SizeHistory);
            Array.Clear(_histStats, 0, SizeHistory);
            Array.Clear(_histSpans, 0, SizeHistory);
            _histPos = 0;
            RecordedVm = null;
            RecordedMethodHooks = null;
            ClearNet();
            Generation++;
            Version++;
        }

        internal static void ResetFusedSites()
        {
            lock (_siteLock)
            {
                _siteCount = 0;
                _sites = new FusedSite[256];
            }
            _calibSite = -1;
            _siteCapWarned = false;
            UdonProfilerProbe.RenewRegister();
        }

        internal static void ResetForLoad()
        {
            Recording = false;
            Clear();
            IsLoadedCapture = true;
        }

        internal static void RegisterLoadedBehaviour(int id, BehaviourInfo info)
        {
            info.index = _byIndex.Count;
            info.id = id;
            info.netStamp = info.netPendingStamp = 0;
            _byIndex.Add(info);
            info.scriptId = ScriptId(info.script);
            info.rootPaths.Clear();
            info.nestedGen = -1;
            info.siteGen = -1;
            _infos[id] = info;
        }

        internal static Frame LoadFrame(int serial)
        {
            var f = _ring[((serial % Capacity) + Capacity) % Capacity];
            f.Reset(0, serial);
            return f;
        }

        internal static void CommitLoad(int frames)
        {
            frames = Math.Max(0, Math.Min(frames, Capacity));
            FrameCount = frames;
            NewestFrame = frames - 1;
            for (int i = 0; i < frames; i++) _ring[i].isSealed = true;
            Version++;
            Generation++;
        }

        internal static void EnsureMethodNames(int behaviourId)
        {
            int count = Paths.Count;
            for (int p = 0; p < count; p++)
            {
                ref readonly var n = ref Paths[p];
                if (n.kind == PathKind.Method && n.behaviourId == behaviourId) Method(behaviourId, n.key);
            }
        }

        internal static UdonProgramMap CurrentMap => _curMap;

        internal static void SetProgramRefs(AccessTools.FieldRef<UdonBehaviour, IUdonProgram> program, AccessTools.FieldRef<UdonBehaviour, AbstractSerializedUdonProgramAsset> serialized)
        {
            _programRef = program;
            _serializedRef = serialized;
        }

        internal static void SetProbeCosts(long callFx, long retFx, long fusedFx, long eventFx)
        {
            _callCostFx = callFx;
            _retCostFx = retFx;
            _fusedCostFx = fusedFx;
            _eventCostFx = eventFx;
            _calibrated = true;
        }

        internal static void ResetProbeSync() => _probeSynced = false;
        internal static void ResetCalibration() => _calibrated = false;
        internal static int StackDepth => _sp;
        internal static Frame CurrentFrame => _current;
        internal static Frame RingSlot(int serial) => _ring[((serial % Capacity) + Capacity) % Capacity];

        static void WarnOnce(string what, Exception e)
        {
            if (!_warned.Add(what)) return;
            Debug.LogWarning($"[UdonProfiler] {what}: {e.Message}");
        }

        static bool AllocOn => TrackAlloc && _allocGetter != null;

        static long SelfBegin()
        {
            if (Environment.CurrentManagedThreadId != MainThreadId) return NoAlloc;
            if (_selfDepth++ == 0)
            {
                _selfA0 = AllocOn ? _allocGetter() : NoAlloc;
                _selfT0 = Stopwatch.GetTimestamp();
            }
            return 0;
        }

        static void SelfEnd(long token)
        {
            if (token == NoAlloc || _selfDepth <= 0 || --_selfDepth > 0) return;
            _overhead += (Stopwatch.GetTimestamp() - _selfT0) << 10;
            if (_selfA0 != NoAlloc && _allocGetter != null) _selfAlloc += _allocGetter() - _selfA0;
        }

        static BehaviourInfo Register(UdonBehaviour ub, int id)
        {
            long a0 = SelfBegin();
            try
            {
                var info = new BehaviourInfo { behaviour = ub, objectName = ub.name, objectPath = Path(ub.transform), objectKey = ObjectKey(ub), id = id };
                info.script = UdonProfilerHooks.ScriptName(ub);
                try { info.syncMode = (NetSync)(byte)ub.SyncMethod; }
                catch (Exception) { info.syncMode = NetSync.Unknown; }
                if (info.syncMode == NetSync.Continuous) _netContinuous.Add(info);
                info.index = _byIndex.Count;
                _byIndex.Add(info);
                info.scriptId = ScriptId(info.script);
                try
                {
                    var usp = ub.programSource as UdonSharpProgramAsset;
                    UnityEngine.Object asset = usp != null ? usp.sourceCsScript : null;
                    info.scriptAsset = asset != null ? asset : ub.programSource;
                }
                catch (Exception) { info.scriptAsset = null; }
                _infos[id] = info;
                return info;
            }
            finally { SelfEnd(a0); }
        }

        static string Path(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        static string ObjectKey(UdonBehaviour ub)
        {
            var t = ub.transform;
            var sb = new StringBuilder(64);
            sb.Append(t.gameObject.scene.name).Append(':');
            _chain.Clear();
            for (var c = t; c != null; c = c.parent) _chain.Add(c);
            for (int i = _chain.Count - 1; i >= 0; i--)
            {
                if (i != _chain.Count - 1) sb.Append('/');
                sb.Append(_chain[i].name).Append('[').Append(_chain[i].GetSiblingIndex()).Append(']');
            }
            _chain.Clear();
            ub.GetComponents(_components);
            sb.Append("#c").Append(_components.IndexOf(ub));
            _components.Clear();
            return sb.ToString();
        }

        static bool HasSlot(int path) => (uint)path < (uint)_statStamp.Length && _statStamp[path] == _current.serial + 1;

        static int SlotOf(int path)
        {
            if (path >= _statStamp.Length) EnsurePathArrays();
            if (_statStamp[path] == _current.serial + 1) return _statSlot[path];
            if (_current.statCount == _current.stats.Length) GrowStats();
            _current.AddStat(path, out int idx);
            _statSlot[path] = idx;
            _statStamp[path] = _current.serial + 1;
            return idx;
        }

        static void EnsurePathArrays()
        {
            int need = Paths.Count;
            if (need <= _statStamp.Length) return;
            long a0 = SelfBegin();
            int n = _statStamp.Length;
            while (n < need) n *= 2;
            Array.Resize(ref _statSlot, n);
            Array.Resize(ref _statStamp, n);
            Array.Resize(ref _markerOfPath, n);
            SelfEnd(a0);
        }

        static int NewChild(int parent, PathKind kind, int behaviourId, uint key, int scriptId)
        {
            var paths = Paths;
            int p = paths.Find(parent, kind, behaviourId, key);
            if (p < 0)
            {
                if (paths.Full || (parent >= 0 && parent < paths.Count && paths[parent].depth >= PathTable.MaxDepth)) return -1;
                long a0 = SelfBegin();
                p = Paths.Child(parent, kind, behaviourId, key, scriptId);
                SelfEnd(a0);
            }
            if (p >= _statStamp.Length) EnsurePathArrays();
            return p;
        }

        static void GrowStats()
        {
            long a0 = SelfBegin();
            Array.Resize(ref _current.stats, Math.Max(128, _current.stats.Length * 2));
            SelfEnd(a0);
        }

        static void GrowSamples()
        {
            long a0 = SelfBegin();
            Array.Resize(ref _current.samples, Math.Max(64, _current.samples.Length * 2));
            SelfEnd(a0);
        }

        static void GrowSpans()
        {
            long a0 = SelfBegin();
            Array.Resize(ref _current.spans, Math.Max(64, _current.spans.Length * 2));
            SelfEnd(a0);
        }

        static void GrowStack()
        {
            long a0 = SelfBegin();
            Array.Resize(ref _stack, _stack.Length * 2);
            SelfEnd(a0);
        }

        static void SealCurrent()
        {
            if (_current == null) return;
            FlushNet();
            _current.isSealed = true;
            Version++;
            _histSamples[_histPos] = _current.sampleCount;
            _histStats[_histPos] = _current.statCount;
            _histSpans[_histPos] = _current.spanCount;
            _histPos = (_histPos + 1) % SizeHistory;
            _current = null;
            _currentFrameNumber = -1;
            ResetStack();
        }

        static void ResetStack()
        {
            for (int i = 0; i < _sp; i++)
            {
                _stack[i].prevMap = null;
                _stack[i].prevInfo = null;
            }
            _sp = 0;
            _drawDepth = 0;
            _curEvent = -1;
            _curMap = null;
            _curInfo = null;
        }

        static void BeginFrame(int number)
        {
            if (_current != null)
            {
                bool spike = PauseAboveMs > 0 && _current.serial != _pauseGraceSerial && _current.totalTicks * MsPerTick > PauseAboveMs;
                int sealedSerial = _current.serial;
                SealCurrent();
                if (spike)
                {
                    Recording = false;
                    PausedOnFrame = sealedSerial;
                    return;
                }
            }
            ResetStack();
            NewestFrame++;
            if (FrameCount < Capacity) FrameCount++;
            _current = _ring[NewestFrame % Capacity];
            _current.Reset(number, NewestFrame);
            _current.deltaTime = DeltaSource != null ? DeltaSource() : Time.unscaledDeltaTime;
            bool fast = FastVmActive();
            _instr = InstructionSource ?? (_vmInstructions != null && fast ? _vmInstructions : null);
            _current.hasInstructions = _instr != null;
            if (RecordedVm == null)
            {
                RecordedVm = fast ? "Fast VM" : "stock VM";
                RecordedMethodHooks = HookStatus(fast);
            }
            int maxSamples = 0, maxStats = 0, maxSpans = 0;
            for (int i = 0; i < SizeHistory; i++)
            {
                if (_histSamples[i] > maxSamples) maxSamples = _histSamples[i];
                if (_histStats[i] > maxStats) maxStats = _histStats[i];
                if (_histSpans[i] > maxSpans) maxSpans = _histSpans[i];
            }
            long a0 = SelfBegin();
            _current.EnsureCapacity(maxSamples, maxStats, maxSpans);
            SelfEnd(a0);
            _currentFrameNumber = number;
            if ((NewestFrame & 63) == 0) ExpireScheduled();
        }

        static void ExpireScheduled()
        {
            if (_scheduled.Count == 0) return;
            int limit = NewestFrame - Capacity;
            _expiredKeys.Clear();
            foreach (var kv in _scheduled)
            {
                var list = kv.Value;
                list.RemoveAll(x => x.serial < limit);
                if (list.Count == 0) _expiredKeys.Add(kv.Key);
            }
            foreach (int k in _expiredKeys) _scheduled.Remove(k);
            _expiredKeys.Clear();
        }

        internal static int Push(UdonBehaviour ub, uint entry)
        {
            if (!_recording || Environment.CurrentManagedThreadId != MainThreadId) return -1;
            if (IsLoadedCapture) Clear();
            if (!_calibrated && _sp == 0 && (_methodsLive || _externsLive)) CalibrateProbes();
            int id = ub.GetInstanceID();
            if (!_infos.TryGetValue(id, out var info)) info = Register(ub, id);
            if (_methodsLive && _programRef != null) RefreshProgram(ub, info);
            return PushCore(id, info, entry);
        }

        static void RefreshProgram(UdonBehaviour ub, BehaviourInfo info)
        {
            IUdonProgram program = null;
            try { program = _programRef(ub); }
            catch (Exception e) { WarnOnce("program lookup failed", e); }
            if (ReferenceEquals(program, info.program)) return;
            info.program = program;
            info.siteParent = info.sitePath = null;
            info.siteKey = null;
            info.siteGen = -1;
            long a0 = SelfBegin();
            try { info.map = program == null ? null : UdonProgramMap.For(ub, program, _serializedRef != null ? _serializedRef(ub) : null); }
            catch (Exception e)
            {
                info.map = null;
                WarnOnce("program analysis failed", e);
            }
            SelfEnd(a0);
        }

        static void PreAnalyzeKnown()
        {
            if (_programRef == null || _sp != 0) return;
            for (int i = 0; i < _byIndex.Count; i++)
            {
                var info = _byIndex[i];
                var ub = info?.behaviour;
                if (ub == null) continue;
                try { RefreshProgram(ub, info); }
                catch (Exception e) { WarnOnce("program analysis failed", e); }
            }
            _pauseGraceSerial = NewestFrame + 1;
        }

        internal static int PushCore(int behaviourId, BehaviourInfo info, uint entry)
        {
            if (!_recording || Environment.CurrentManagedThreadId != MainThreadId) return -1;
            int frame = FrameSource == null ? Time.frameCount : FrameSource();
            if (_current == null || frame != _currentFrameNumber)
            {
                BeginFrame(frame);
                if (!_recording || _current == null) return -1;
            }

            int id = behaviourId;
            int parentPath = _sp > 0 ? _stack[_sp - 1].path : -1;
            bool parentFolded = _sp > 0 && _stack[_sp - 1].folded;
            int path;
            bool folded = false;
            if (_sp == 0)
            {
                if (!info.rootPaths.TryGetValue(entry, out path))
                {
                    path = NewChild(-1, PathKind.Event, id, entry, info.scriptId);
                    if (path < 0) path = PathTable.OverflowPath;
                    info.rootPaths[entry] = path;
                }
            }
            else
            {
                path = -1;
                if (!parentFolded)
                {
                    if (info.nestedGen == Generation && info.nestedParent == parentPath && info.nestedEntry == entry) path = info.nestedPath;
                    else
                    {
                        path = NewChild(parentPath, PathKind.Event, id, entry, info.scriptId);
                        info.nestedGen = Generation;
                        info.nestedParent = parentPath;
                        info.nestedEntry = entry;
                        info.nestedPath = path;
                    }
                }
                if (path < 0)
                {
                    folded = true;
                    path = parentPath;
                }
            }
            if (path >= _statStamp.Length) EnsurePathArrays();

            if (_current.sampleCount == _current.samples.Length) GrowSamples();
            int index;
            {
                ref var s = ref _current.AddSample(out index);
                s.behaviourId = id;
                s.entry = entry;
                s.parent = _curEvent >= 0 ? _stack[_curEvent].sample : -1;
                s.path = path;
                s.folded = folded;
                s.depth = _drawDepth;
            }

            if (_curEvent < 0 && _scheduled.Count > 0 && _scheduled.TryGetValue(id, out var list) && list.Count > 0)
            {
                string ev = EventName(id, entry);
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].ev != ev) continue;
                    _current.samples[index].scheduledByBehaviour = list[i].behaviour;
                    _current.samples[index].scheduledByEntry = list[i].entry;
                    list.RemoveAt(i);
                    break;
                }
            }

            if (_sp == _stack.Length) GrowStack();
            int stat = folded ? _stack[_sp - 1].stat : SlotOf(path);
            bool marker = false;
            if (EmitMarkers && !folded)
            {
                if (_markerOfPath[path].Handle == IntPtr.Zero)
                {
                    long a0 = SelfBegin();
                    string name = "Udon " + info.script + "." + EventName(id, entry);
                    _markerOfPath[path] = new ProfilerMarker(name);
                    SelfEnd(a0);
                }
                _markerOfPath[path].Begin();
                marker = true;
            }

            if (info.siteParent != null && info.siteGen != Generation)
            {
                FillUnknown(info.siteParent);
                info.siteGen = Generation;
            }

            ref var e = ref _stack[_sp];
            e = default;
            e.kind = PathKind.Event;
            e.path = path;
            e.folded = folded;
            e.markerActive = marker;
            e.sample = index;
            e.behaviourId = id;
            e.scriptId = info.scriptId;
            e.target = entry;
            e.stat = stat;
            e.span = -1;
            e.prevEvent = _curEvent;
            e.prevMap = _curMap;
            e.prevInfo = _curInfo;
            e.selfAllocStart = _selfAlloc;
            e.overheadStart = _overhead;
            e.startAlloc = AllocOn ? _allocGetter() : NoAlloc;
            e.instrStart = InstrNow();
            long now = Stopwatch.GetTimestamp();
            e.start = now;
            _current.samples[index].startTicks = now;

            _curEvent = _sp;
            _curMap = info.map;
            _curInfo = info;
            _sp++;
            _drawDepth++;
            return index;
        }

        internal static void Pop(int sampleIndex, bool halted)
        {
            if (sampleIndex < 0 || _current == null || Environment.CurrentManagedThreadId != MainThreadId) return;
            int k = -1;
            for (int i = _sp - 1; i >= 0; i--)
            {
                if (_stack[i].kind == PathKind.Event && _stack[i].sample == sampleIndex)
                {
                    k = i;
                    break;
                }
            }
            if (k < 0) return;
            long now = Stopwatch.GetTimestamp();
            while (_sp - 1 > k) CloseTop(now);
            CloseEvent(now, halted);
        }

        static void CloseEvent(long now, bool halted)
        {
            int k = _sp - 1;
            ref var e = ref _stack[k];
            long incl = Corrected(ref e, now);
            long instr = InstrIncl(ref e);
            long alloc = 0;
            if (e.startAlloc != NoAlloc && _allocGetter != null)
                alloc = Math.Max(0, _allocGetter() - e.startAlloc - (_selfAlloc - e.selfAllocStart));
            int sampleIndex = e.sample;
            int parentSample;
            {
                ref var s = ref _current.samples[sampleIndex];
                s.inclusiveTicks = incl;
                s.instructions = instr;
                s.halted = halted;
                s.bucket = Histogram.Bucket(incl);
                s.allocBytes = alloc;
                parentSample = s.parent;
            }
            CloseStats(ref e, incl, instr);
            if (e.markerActive) _markerOfPath[e.path].End();
            _curEvent = e.prevEvent;
            _curMap = e.prevMap;
            _curInfo = e.prevInfo;
            e.prevMap = null;
            e.prevInfo = null;
            _sp--;
            _drawDepth--;
            _current.mutation++;
            if (_sp > 0)
            {
                _stack[_sp - 1].child += incl;
                _stack[_sp - 1].instrChild += instr;
                _overhead += _eventCostFx;
            }
            if (parentSample >= 0)
            {
                ref var ps = ref _current.samples[parentSample];
                ps.childTicks += incl;
                ps.childAlloc += alloc;
                ps.childInstructions += instr;
            }
            else
            {
                _current.totalTicks += incl;
                _current.totalInstructions += instr;
            }
        }

        static long Corrected(ref Entry e, long now) => Math.Max(e.child, now - e.start - ((_overhead - e.overheadStart) >> 10));

        static long InstrIncl(ref Entry e) => Math.Max(e.instrChild, InstrNow() - e.instrStart);

        static void CloseStats(ref Entry e, long incl, long instr)
        {
            ref var st = ref _current.stats[e.stat];
            if (!e.folded)
            {
                st.calls++;
                st.inclusiveTicks += incl;
                st.instructions += instr;
                if (incl > st.maxTicks) st.maxTicks = incl;
            }
            st.selfTicks += incl - e.child;
            st.selfInstructions += instr - e.instrChild;
            st.externTicks += e.ext;
            st.externCount += e.extCount;
            st.fusedCount += e.fused;
        }

        static void CloseTop(long now)
        {
            if (_stack[_sp - 1].kind == PathKind.Event)
            {
                CloseEvent(now, false);
                return;
            }
            ref var e = ref _stack[_sp - 1];
            long incl = Corrected(ref e, now);
            long instr = InstrIncl(ref e);
            CloseStats(ref e, incl, instr);
            if (e.span >= 0)
            {
                _current.spans[e.span].ticks = incl;
                _current.spans[e.span].instructions = instr;
                _drawDepth--;
            }
            _sp--;
            if (_sp > 0)
            {
                _stack[_sp - 1].child += incl;
                _stack[_sp - 1].instrChild += instr;
            }
        }

        internal static void Scheduled(UdonBehaviour target, string eventName)
        {
            if (!_recording || target == null || Environment.CurrentManagedThreadId != MainThreadId) return;
            if (_curEvent < 0 || _current == null) return;
            int fromBehaviour = _stack[_curEvent].behaviourId;
            uint fromEntry = _stack[_curEvent].target;
            int to = target.GetInstanceID();
            long a0 = SelfBegin();
            if (!_scheduled.TryGetValue(to, out var list)) _scheduled[to] = list = new List<(string, int, uint, int)>(2);
            list.Add((eventName, fromBehaviour, fromEntry, NewestFrame));
            SelfEnd(a0);
        }

        internal static void OnCall(uint pc, uint target)
        {
            if (_curEvent < 0 || !_recording || !_methodTiming || Environment.CurrentManagedThreadId != MainThreadId) return;
            try { PushMethod(pc >> 2, target, pc + 8); }
            catch (Exception e) { DisableMethods(e); }
        }

        static void PushMethod(uint w, uint target, uint ret)
        {
            _overhead += _callCostFx;
            int topPath, topStat, behaviourId, scriptId;
            bool topFolded;
            {
                ref var top = ref _stack[_sp - 1];
                topPath = top.path;
                topStat = top.stat;
                behaviourId = top.behaviourId;
                scriptId = top.scriptId;
                topFolded = top.folded;
            }
            int path = -1;
            if (!topFolded && _sp < MaxStack)
            {
                var map = _curMap;
                var info = _curInfo;
                int si = map != null && info != null && map.SiteIndex != null && w < (uint)map.SiteIndex.Length ? map.SiteIndex[w] : -1;
                if (si >= 0)
                {
                    if (info.siteParent == null || info.siteParent.Length < map.SiteCount) AllocSites(info, map.SiteCount);
                    if (info.siteParent[si] == topPath && info.siteKey[si] == target) path = info.sitePath[si];
                    else
                    {
                        path = NewChild(topPath, PathKind.Method, behaviourId, target, scriptId);
                        info.siteParent[si] = topPath;
                        info.sitePath[si] = path;
                        info.siteKey[si] = target;
                    }
                }
                else path = NewChild(topPath, PathKind.Method, behaviourId, target, scriptId);
            }
            if (path >= 0 && !HasSlot(path) && _current.statCount >= MaxStatsPerFrame) path = -1;
            bool folded = path < 0;
            int stat = folded ? topStat : SlotOf(path);
            if (folded) path = topPath;
            if (_sp == _stack.Length) GrowStack();
            int span = -1;
            if (!folded && MethodSpans && MaxSpansPerFrame > 0)
            {
                if (_current.spanCount < MaxSpansPerFrame)
                {
                    if (_current.spanCount == _current.spans.Length) GrowSpans();
                    ref var sp = ref _current.AddSpan(out span);
                    sp.path = path;
                    sp.depth = _drawDepth;
                }
                else _current.droppedSpans++;
            }
            if (span >= 0) _drawDepth++;
            ref var e = ref _stack[_sp++];
            e = default;
            e.kind = PathKind.Method;
            e.path = path;
            e.folded = folded;
            e.stat = stat;
            e.sample = -1;
            e.behaviourId = behaviourId;
            e.scriptId = scriptId;
            e.target = target;
            e.ret = ret;
            e.span = span;
            e.prevEvent = -1;
            e.overheadStart = _overhead;
            e.instrStart = InstrNow();
            long now = Stopwatch.GetTimestamp();
            e.start = now;
            if (span >= 0) _current.spans[span].startTicks = now;
        }

        static void AllocSites(BehaviourInfo info, int n)
        {
            long a0 = SelfBegin();
            info.siteParent = new int[n];
            FillUnknown(info.siteParent);
            info.sitePath = new int[n];
            info.siteKey = new uint[n];
            info.siteGen = Generation;
            SelfEnd(a0);
        }

        static void FillUnknown(int[] a)
        {
            for (int i = 0; i < a.Length; i++) a[i] = -1;
        }

        internal static void OnReturn(uint pc, uint target)
        {
            if (_curEvent < 0 || _sp - 1 <= _curEvent || !_recording || Environment.CurrentManagedThreadId != MainThreadId) return;
            try
            {
                int floor = _curEvent;
                if (target >= 0xFFFFFFFCu)
                {
                    _overhead += _retCostFx;
                    long now = Stopwatch.GetTimestamp();
                    while (_sp - 1 > floor) CloseTop(now);
                    return;
                }
                for (int i = _sp - 1; i > floor; i--)
                {
                    if (_stack[i].ret != target) continue;
                    _overhead += _retCostFx;
                    long now = Stopwatch.GetTimestamp();
                    while (_sp > i) CloseTop(now);
                    return;
                }
                _overhead += _retMissCostFx;
            }
            catch (Exception e) { DisableMethods(e); }
        }

        internal static void OnTail(uint pc, uint target)
        {
            if (_curEvent < 0 || !_recording || !_methodTiming || Environment.CurrentManagedThreadId != MainThreadId) return;
            try
            {
                if (_sp - 1 > _curEvent)
                {
                    uint ret;
                    {
                        ref var top = ref _stack[_sp - 1];
                        if (top.target == target) return;
                        ret = top.ret;
                    }
                    _overhead += _retCostFx;
                    CloseTop(Stopwatch.GetTimestamp());
                    PushMethod(pc >> 2, target, ret);
                }
                else
                {
                    uint e = _stack[_curEvent].target;
                    if (target == e || target == e + 8 || (_curMap != null && target == _curMap.BodyLabel(e))) return;
                    PushMethod(pc >> 2, target, TailRet);
                }
            }
            catch (Exception ex) { DisableMethods(ex); }
        }

        static void DisableMethods(Exception e)
        {
            MethodTiming = false;
            Debug.LogWarning($"[UdonProfiler] method timing disabled: {e.Message}");
        }

        internal static bool ExternBegin(out long start, out long childBefore)
        {
            if (_sp == 0 || !_recording || !ExternTiming || Environment.CurrentManagedThreadId != MainThreadId)
            {
                start = 0;
                childBefore = 0;
                return false;
            }
            childBefore = _stack[_sp - 1].child + (_overhead >> 10);
            start = Stopwatch.GetTimestamp();
            return true;
        }

        internal static void ExternEnd(int sigId, long start, long childBefore, int[] site)
        {
            if (_current == null || _sp == 0) return;
            long end = Stopwatch.GetTimestamp();
            try
            {
                int topIndex = _sp - 1;
                long elapsed = end - start - (_stack[topIndex].child + (_overhead >> 10) - childBefore);
                if (elapsed < 0) elapsed = 0;
                int topPath = _stack[topIndex].path;
                _stack[topIndex].ext += elapsed;
                _stack[topIndex].extCount++;
                if (_curEvent >= 0)
                {
                    int si = _stack[_curEvent].sample;
                    _current.samples[si].externTicks += elapsed;
                    _current.samples[si].externCount++;
                }
                _current.externCount++;
                if (!DetailedExterns || sigId < 0) return;
                int path;
                if (site != null && site[0] == Generation && site[1] == topPath) path = site[2];
                else
                {
                    int ownerBehaviour, ownerScript;
                    {
                        ref readonly var owner = ref Paths[topPath];
                        ownerBehaviour = owner.behaviourId;
                        ownerScript = owner.scriptId;
                    }
                    path = NewChild(topPath, PathKind.Extern, ownerBehaviour, (uint)sigId, ownerScript);
                    if (site != null)
                    {
                        site[0] = Generation;
                        site[1] = topPath;
                        site[2] = path;
                    }
                }
                if (path < 0 || (!HasSlot(path) && _current.statCount >= MaxStatsPerFrame))
                {
                    _current.droppedStats++;
                    return;
                }
                int slot = SlotOf(path);
                ref var st = ref _current.stats[slot];
                st.calls++;
                st.inclusiveTicks += elapsed;
                if (elapsed > st.maxTicks) st.maxTicks = elapsed;
            }
            catch (Exception e)
            {
                DetailedExterns = false;
                if (!_externEndWarned)
                {
                    _externEndWarned = true;
                    Debug.LogWarning($"[UdonProfiler] detailed extern stats disabled: {e.Message}");
                }
            }
        }

        internal static int RegisterFusedSite(string sig)
        {
            long a0 = SelfBegin();
            int id;
            bool capped = false;
            lock (_siteLock)
            {
                if (_siteCount >= MaxFusedSites)
                {
                    id = -1;
                    capped = !_siteCapWarned;
                    _siteCapWarned = true;
                }
                else
                {
                    id = _siteCount++;
                    if (id == _sites.Length)
                    {
                        var grown = new FusedSite[id * 2];
                        Array.Copy(_sites, grown, id);
                        _sites = grown;
                    }
                    _sites[id] = new FusedSite { sig = SigId(sig), gen = -1, parent = -1, path = -1 };
                }
            }
            SelfEnd(a0);
            if (capped) Debug.LogWarning($"[UdonProfiler] fused extern site limit ({MaxFusedSites}) reached; further fused externs are counted without per-path detail.");
            return id;
        }

        internal static void OnFusedExtern(int siteId)
        {
            if (_sp == 0 || !_recording || !_countFused || Environment.CurrentManagedThreadId != MainThreadId) return;
            try
            {
                _overhead += _fusedCostFx;
                int topIndex = _sp - 1;
                int topPath = _stack[topIndex].path;
                _stack[topIndex].extCount++;
                _stack[topIndex].fused++;
                if (_curEvent >= 0)
                {
                    int si = _stack[_curEvent].sample;
                    _current.samples[si].externCount++;
                    _current.samples[si].fusedCount++;
                }
                _current.externCount++;
                _current.fusedCount++;
                var sites = _sites;
                if (!DetailedExterns || (uint)siteId >= (uint)sites.Length) return;
                ref var fs = ref sites[siteId];
                int path;
                if (fs.gen == Generation && fs.parent == topPath) path = fs.path;
                else
                {
                    int ownerBehaviour, ownerScript;
                    {
                        ref readonly var owner = ref Paths[topPath];
                        ownerBehaviour = owner.behaviourId;
                        ownerScript = owner.scriptId;
                    }
                    path = NewChild(topPath, PathKind.Extern, ownerBehaviour, (uint)fs.sig, ownerScript);
                    fs.gen = Generation;
                    fs.parent = topPath;
                    fs.path = path;
                }
                if (path < 0 || (!HasSlot(path) && _current.statCount >= MaxStatsPerFrame))
                {
                    _current.droppedStats++;
                    return;
                }
                int slot = SlotOf(path);
                ref var st = ref _current.stats[slot];
                st.calls++;
                st.fusedCount++;
            }
            catch (Exception e) { DisableFused(e); }
        }

        static void DisableFused(Exception e)
        {
            CountFusedExterns = false;
            Debug.LogWarning($"[UdonProfiler] fused extern counting disabled: {e.Message}");
        }

        internal static int ExternArity(string sig)
        {
            var over = ArityOverride;
            if (over != null) return over(sig);
            if (sig == null) return -1;
            lock (UdonProgramMap.CacheLock)
            {
                if (_arity.TryGetValue(sig, out int known)) return known;
                if (_arityWrapper == null)
                {
                    try { _arityWrapper = VRC.Udon.Editor.UdonEditorManager.Instance.GetWrapper(); }
                    catch (Exception) { _arityWrapper = null; }
                    if (_arityWrapper == null) return -1;
                }
                int n;
                try { n = _arityWrapper.GetExternFunctionParameterCount(sig); }
                catch (Exception) { n = -1; }
                _arity[sig] = n;
                return n;
            }
        }

        internal static void SyncProbes()
        {
            bool active = Viewers > 0 && (ProbesOutsidePlayMode || EditorApplication.isPlayingOrWillChangePlaymode);
            if (active && _recording && !_calibrated && _sp == 0 && Environment.CurrentManagedThreadId == MainThreadId) CalibrateProbes();
            bool methods = active && _recording && _methodTiming;
            bool externs = active && _recording && _countFused;
            if (!_probeSynced || methods != _methodsLive || externs != _externsLive) UdonProfilerProbe.Set(methods, externs);
            bool turnedOn = methods && !_methodsLive;
            _methodsLive = methods;
            if (turnedOn && Environment.CurrentManagedThreadId == MainThreadId) PreAnalyzeKnown();
            _externsLive = externs;
            _probeSynced = true;
            UdonProfilerStockHooks.Enabled = methods && UdonProfilerStockHooks.Patched;
            UdonProfilerNetwork.SyncRequestHook(_recording);
        }

        internal static void CalibrateProbes()
        {
            _calibrated = true;
            _callCostFx = _retCostFx = _retMissCostFx = _fusedCostFx = _eventCostFx = 0;
            const uint CalibTarget = 0xFFFFFFF0u, CalibEntry = 0xFFFFFFF4u, MissTarget = 0x7FFFFFF0u;
            const int CalibBehaviour = int.MinValue, Saved = 16;
            var savedCurrent = _current;
            int savedNumber = _currentFrameNumber, savedDraw = _drawDepth, savedEvent = _curEvent;
            var savedMap = _curMap;
            var savedInfo = _curInfo;
            long savedOverhead = _overhead, savedSelfAlloc = _selfAlloc;
            bool savedMethods = _methodTiming, savedFused = _countFused;
            var savedSlot = new int[Saved];
            var savedStamp = new int[Saved];
            var savedMarker = new ProfilerMarker[Saved];
            Array.Copy(_statSlot, savedSlot, Saved);
            Array.Copy(_statStamp, savedStamp, Saved);
            Array.Copy(_markerOfPath, savedMarker, Saved);
            _calibPaths ??= new PathTable();
            _calibPaths.Clear();
            _paths = _calibPaths;
            try
            {
                var calInfo = new BehaviourInfo { script = "calibration", objectName = "", objectPath = "", objectKey = "" };
                NewChild(PathTable.OverflowPath, PathKind.Method, 0, CalibTarget, -1);
                if (_calibSite < 0) _calibSite = RegisterFusedSite("__udonprofiler_calibration");
                int site = _calibSite;
                lock (_siteLock)
                {
                    if ((uint)site < (uint)_sites.Length) _sites[site].gen = -1;
                }
                var frame = new Frame();
                frame.Reset(0, -2);
                _current = frame;
                _currentFrameNumber = FrameSource == null ? Time.frameCount : FrameSource();
                _curMap = null;
                _curInfo = null;
                _drawDepth = 0;
                _methodTiming = true;
                _countFused = true;
                frame.AddSample(out int s0);
                _stack[0] = default;
                _stack[0].kind = PathKind.Event;
                _stack[0].path = PathTable.OverflowPath;
                _stack[0].sample = s0;
                _stack[0].scriptId = -1;
                _stack[0].span = -1;
                _stack[0].prevEvent = -1;
                _stack[0].stat = SlotOf(PathTable.OverflowPath);
                _sp = 1;
                _curEvent = 0;
                long bestPair = long.MaxValue, bestRet = long.MaxValue, bestEvent = long.MaxValue, bestFused = long.MaxValue, bestMiss = long.MaxValue;
                for (int batch = 0; batch < 7; batch++)
                {
                    frame.spanCount = 0;
                    frame.sampleCount = 1;
                    _stack[0].child = 0;
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < 256; i++)
                    {
                        UdonProfilerProbe.CallFn(8u, CalibTarget);
                        UdonProfilerProbe.ReturnFn(84u, 16u);
                    }
                    long t1 = Stopwatch.GetTimestamp();
                    long inMethods = _stack[0].child;
                    _stack[0].child = 0;
                    long t2 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < 256; i++) Pop(PushCore(CalibBehaviour, calInfo, CalibEntry), false);
                    long t3 = Stopwatch.GetTimestamp();
                    long inEvents = _stack[0].child;
                    for (int i = 0; i < 256; i++) UdonProfilerProbe.ExternFn(site);
                    long t4 = Stopwatch.GetTimestamp();
                    UdonProfilerProbe.CallFn(8u, CalibTarget);
                    long t5 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < 256; i++) UdonProfilerProbe.ReturnFn(84u, MissTarget);
                    long t6 = Stopwatch.GetTimestamp();
                    UdonProfilerProbe.ReturnFn(84u, 16u);
                    bestPair = Math.Min(bestPair, t1 - t0);
                    bestRet = Math.Min(bestRet, inMethods);
                    bestEvent = Math.Min(bestEvent, t3 - t2 - inEvents);
                    bestFused = Math.Min(bestFused, t4 - t3);
                    bestMiss = Math.Min(bestMiss, t6 - t5);
                }
                _retCostFx = Math.Max(0, bestRet * 1024 / 256);
                _callCostFx = Math.Max(0, bestPair * 1024 / 256 - _retCostFx);
                _eventCostFx = Math.Max(0, bestEvent * 1024 / 256);
                _fusedCostFx = Math.Max(0, bestFused * 1024 / 256);
                _retMissCostFx = Math.Max(0, bestMiss * 1024 / 256);
            }
            catch (Exception e)
            {
                _callCostFx = _retCostFx = _retMissCostFx = _fusedCostFx = _eventCostFx = 0;
                WarnOnce("probe calibration", e);
            }
            finally
            {
                for (int i = 0; i < _sp; i++) _stack[i] = default;
                _sp = 0;
                _paths = _livePaths;
                Array.Copy(savedSlot, _statSlot, Saved);
                Array.Copy(savedStamp, _statStamp, Saved);
                Array.Copy(savedMarker, _markerOfPath, Saved);
                _curEvent = savedEvent;
                _current = savedCurrent;
                _currentFrameNumber = savedNumber;
                _drawDepth = savedDraw;
                _curMap = savedMap;
                _curInfo = savedInfo;
                _overhead = savedOverhead;
                _selfAlloc = savedSelfAlloc;
                _methodTiming = savedMethods && _methodTiming;
                _countFused = savedFused && _countFused;
            }
        }
    }
}
