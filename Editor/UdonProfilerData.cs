using System;
using System.Collections.Generic;
using VRC.Udon;

namespace MagmaVRC.UdonProfiler
{
    public enum PathKind : byte { Event, Method, Extern }

    /// <summary>One UdonBehaviour.RunProgram execution.</summary>
    public struct Sample
    {
        public int behaviourId;
        public uint entry;
        public int parent;
        public int path;
        public int depth;
        public long startTicks;
        public long inclusiveTicks;
        public long childTicks;
        public long externTicks;
        public int externCount;
        public int fusedCount;
        public int scheduledByBehaviour;
        public uint scheduledByEntry;
        public bool halted;
        public bool folded;
        public byte bucket;
        public long allocBytes;
        public long childAlloc;
        public long instructions;
        public long childInstructions;
    }

    /// <summary>Per-frame totals for one call path.</summary>
    public struct PathStat
    {
        public int path;
        public int calls;
        public long inclusiveTicks;
        public long selfTicks;
        public long externTicks;
        public int externCount;
        public int fusedCount;
        public long maxTicks;
        public long instructions;
        public long selfInstructions;
    }

    /// <summary>Log-scale duration buckets shared by the recorder and the model.</summary>
    public static class Histogram
    {
        public const int Buckets = 64;
        public static readonly double MinTicks = System.Diagnostics.Stopwatch.Frequency / 2_000_000.0;

        public static byte Bucket(long ticks)
        {
            if (ticks <= 0) return 0;
            double b = Math.Log(ticks / MinTicks, 2) * 3;
            if (!(b > 0)) return 0;
            return b >= Buckets - 1 ? (byte)(Buckets - 1) : (byte)(int)b;
        }

        public static double MidTicks(int bucket) => MinTicks * Math.Pow(2, (bucket + 0.5) / 3);
    }

    /// <summary>One timed method call inside a frame.</summary>
    public struct Span
    {
        public int path;
        public int depth;
        public long startTicks;
        public long ticks;
        public long instructions;
    }

    /// <summary>A node of the global call-path tree.</summary>
    public struct PathNode
    {
        public int parent;
        public PathKind kind;
        public int behaviourId;
        public uint key;
        public int depth;
        public int eventPath;
        public int scriptId;
        public int firstChild;
        public int nextSibling;
        public int childCount;
    }

    /// <summary>Sync mode of a behaviour, in the order of VRC.SDKBase.Networking.SyncType.</summary>
    public enum NetSync : byte { Unknown, None, Continuous, Manual }

    /// <summary>Networking counters of one behaviour in one frame; byte counts are estimates.</summary>
    public struct NetStat
    {
        public int behaviourId;
        public int requests;
        public int requestsIgnored;
        public int serializations;
        public int overLimit;
        public long estBytes;
        public int eventsAll, eventsOwner, eventsOthers, eventsSelf;
        public long eventBytes;
        public int rateLimitHits;
        public int ownershipTransfers;
    }

    /// <summary>A source location for a method or event; <see cref="line"/> is 1-based, 0 when unknown.</summary>
    public struct SourceRef
    {
        public string name;
        public string file;
        public int line;
        public bool HasFile => !string.IsNullOrEmpty(file);
    }

    /// <summary>One recorded frame in the ring buffer.</summary>
    public sealed class Frame
    {
        public int frameNumber;
        public int serial = -1;
        public bool isSealed;
        public int mutation;
        public long totalTicks;
        public int externCount;
        public int fusedCount;
        public int droppedSpans;
        public int droppedStats;
        /// <summary>True when Udon instruction counts were recorded for this frame (Fast VM active).</summary>
        public bool hasInstructions;
        public long totalInstructions;
        public Sample[] samples = new Sample[64];
        public int sampleCount;
        public PathStat[] stats = new PathStat[128];
        public int statCount;
        public Span[] spans = new Span[64];
        public int spanCount;
        /// <summary>Unscaled frame duration in seconds; 0 when unknown.</summary>
        public float deltaTime;
        public NetStat[] net = Array.Empty<NetStat>();
        public int netCount;

        public void Reset(int number, int serialIndex)
        {
            frameNumber = number;
            serial = serialIndex;
            isSealed = false;
            mutation = 0;
            deltaTime = 0;
            netCount = 0;
            totalTicks = 0;
            externCount = 0;
            fusedCount = 0;
            droppedSpans = 0;
            droppedStats = 0;
            hasInstructions = false;
            totalInstructions = 0;
            sampleCount = 0;
            statCount = 0;
            spanCount = 0;
        }

        public void EnsureCapacity(int samples, int stats, int spans)
        {
            Fit(ref this.samples, sampleCount, Target(samples, 64, int.MaxValue));
            Fit(ref this.stats, statCount, Target(stats, 128, Math.Max(128, RoundUp64(UdonProfilerRecorder.MaxStatsPerFrame))));
            Fit(ref this.spans, spanCount, Target(spans, 64, Math.Max(64, RoundUp64(UdonProfilerRecorder.MaxSpansPerFrame))));
        }

        public ref Sample AddSample(out int index)
        {
            if (sampleCount == samples.Length) Array.Resize(ref samples, samples.Length * 2);
            index = sampleCount++;
            ref var s = ref samples[index];
            s = default;
            return ref s;
        }

        public ref PathStat AddStat(int path, out int index)
        {
            if (statCount == stats.Length) Array.Resize(ref stats, stats.Length * 2);
            index = statCount++;
            ref var s = ref stats[index];
            s = default;
            s.path = path;
            return ref s;
        }

        public ref Span AddSpan(out int index)
        {
            if (spanCount == spans.Length) Array.Resize(ref spans, spans.Length * 2);
            index = spanCount++;
            ref var s = ref spans[index];
            s = default;
            return ref s;
        }

        public ref NetStat AddNet(int behaviourId, out int index)
        {
            if (netCount == net.Length) Array.Resize(ref net, Math.Max(8, net.Length * 2));
            index = netCount++;
            ref var s = ref net[index];
            s = default;
            s.behaviourId = behaviourId;
            return ref s;
        }

        static int RoundUp64(int n) => (n + 63) & ~63;

        static int Target(int need, int initial, int cap)
        {
            if (need < 0) need = 0;
            long grown = need + (long)need / 4;
            int t = grown >= int.MaxValue - 64 ? int.MaxValue & ~63 : Math.Max(initial, RoundUp64((int)grown));
            return Math.Min(t, cap);
        }

        static void Fit<T>(ref T[] array, int count, int target)
        {
            if (target < count) return;
            if (array.Length >= target && array.Length <= 2 * (long)target) return;
            if (count == 0) array = new T[target];
            else Array.Resize(ref array, target);
        }
    }

    /// <summary>Names and identity of one UdonBehaviour seen while recording or loaded from a capture.</summary>
    public sealed class BehaviourInfo
    {
        public UdonBehaviour behaviour;
        public int index = -1;
        public string script;
        public string objectName;
        public string objectPath;
        /// <summary>Deterministic identity: scene name, then each transform as name[siblingIndex], then #c and the index among the GameObject's UdonBehaviours.</summary>
        public string objectKey;
        public int scriptId = -1;
        public NetSync syncMode;
        public UnityEngine.Object scriptAsset;
        public readonly Dictionary<uint, string> events = new Dictionary<uint, string>();
        public readonly Dictionary<uint, SourceRef> methods = new Dictionary<uint, SourceRef>();
        internal readonly Dictionary<uint, int> rootPaths = new Dictionary<uint, int>();
        internal object program;
        internal UdonProgramMap map;
        internal int[] siteParent, sitePath;
        internal uint[] siteKey;
        internal int siteGen = -1;
        internal int nestedParent = -1, nestedPath = -1, nestedGen = -1;
        internal uint nestedEntry;
        internal int id;
        internal int netStamp, netSlot, netPendingStamp;
        internal object netProgram;
        internal NetLayout netLayout;
        internal float netContinuousAcc;
    }

    /// <summary>Global tree of call paths; ids are dense, parents always precede children.</summary>
    public sealed class PathTable
    {
        public const int MaxPaths = 1 << 18;
        public const int MaxDepth = 64;
        /// <summary>Root Event path that absorbs root events once the table is full; behaviourId 0, key 0xFFFFFFFF, scriptId -1.</summary>
        public const int OverflowPath = 0;
        const int SiblingScanLimit = 16;

        PathNode[] _nodes = new PathNode[1024];
        readonly Dictionary<(int, uint), int> _roots = new Dictionary<(int, uint), int>();
        readonly Dictionary<(int parent, byte kind, int behaviourId, uint key), int> _children = new Dictionary<(int, byte, int, uint), int>();
        readonly int[] _childCache = new int[1 << 16];
        internal int SlowLookups;

        public int Count { get; private set; }
        public bool Full { get; private set; }
        public ref readonly PathNode this[int id] => ref _nodes[id];

        public PathTable() { Clear(); }

        /// <summary>Returns the existing child of <paramref name="parent"/> with this kind, behaviour and key, or -1; never inserts.</summary>
        public int Find(int parent, PathKind kind, int behaviourId, uint key)
        {
            int slot = Slot(parent, kind, behaviourId, key);
            int c = _childCache[slot];
            if (c >= 0 && Matches(c, parent, kind, behaviourId, key)) return c;
            SlowLookups++;
            int found = Lookup(parent, kind, behaviourId, key);
            if (found >= 0) _childCache[slot] = found;
            return found;
        }

        /// <summary>Returns the child of <paramref name="parent"/> (-1 for a root) with this kind, behaviour and key, creating it if needed; -1 when the table is full or the depth limit is reached.</summary>
        public int Child(int parent, PathKind kind, int behaviourId, uint key, int scriptId)
        {
            if (parent >= Count) return -1;
            if (parent < 0 && kind != PathKind.Event) return -1;
            int slot = Slot(parent, kind, behaviourId, key);
            int c = _childCache[slot];
            if (c >= 0 && Matches(c, parent, kind, behaviourId, key)) return c;
            SlowLookups++;
            int found = Lookup(parent, kind, behaviourId, key);
            if (found >= 0)
            {
                _childCache[slot] = found;
                return found;
            }
            int depth = parent < 0 ? 0 : _nodes[parent].depth + 1;
            if (depth > MaxDepth) return -1;
            if (Count >= MaxPaths)
            {
                Full = true;
                return -1;
            }
            int id = Insert(parent, kind, behaviourId, key, scriptId, depth);
            _childCache[slot] = id;
            return id;
        }

        public void Clear()
        {
            Count = 0;
            Full = false;
            _roots.Clear();
            _children.Clear();
            for (int i = 0; i < _childCache.Length; i++) _childCache[i] = -1;
            Insert(-1, PathKind.Event, 0, 0xFFFFFFFFu, -1, 0);
        }

        int Insert(int parent, PathKind kind, int behaviourId, uint key, int scriptId, int depth)
        {
            int id = Count;
            if (id == _nodes.Length) Array.Resize(ref _nodes, id * 2);
            ref var n = ref _nodes[id];
            n.parent = parent;
            n.kind = kind;
            n.behaviourId = behaviourId;
            n.key = key;
            n.depth = depth;
            n.scriptId = scriptId;
            n.eventPath = kind == PathKind.Event || parent < 0 ? id : _nodes[parent].eventPath;
            n.firstChild = -1;
            n.childCount = 0;
            Count = id + 1;
            if (parent < 0)
            {
                n.nextSibling = -1;
                _roots[(behaviourId, key)] = id;
                return id;
            }
            ref var p = ref _nodes[parent];
            n.nextSibling = p.firstChild;
            p.firstChild = id;
            p.childCount++;
            if (p.childCount == SiblingScanLimit + 1)
            {
                for (int c = p.firstChild; c >= 0; c = _nodes[c].nextSibling)
                {
                    ref readonly var cn = ref _nodes[c];
                    _children[(parent, (byte)cn.kind, cn.behaviourId, cn.key)] = c;
                }
            }
            else if (p.childCount > SiblingScanLimit + 1) _children[(parent, (byte)kind, behaviourId, key)] = id;
            return id;
        }

        int Lookup(int parent, PathKind kind, int behaviourId, uint key)
        {
            if (parent < 0)
            {
                if (kind != PathKind.Event) return -1;
                return _roots.TryGetValue((behaviourId, key), out int r) ? r : -1;
            }
            if (parent >= Count) return -1;
            ref readonly var p = ref _nodes[parent];
            if (p.childCount > SiblingScanLimit)
                return _children.TryGetValue((parent, (byte)kind, behaviourId, key), out int d) ? d : -1;
            for (int c = p.firstChild; c >= 0; c = _nodes[c].nextSibling)
            {
                ref readonly var n = ref _nodes[c];
                if (n.key == key && n.behaviourId == behaviourId && n.kind == kind) return c;
            }
            return -1;
        }

        bool Matches(int c, int parent, PathKind kind, int behaviourId, uint key)
        {
            ref readonly var n = ref _nodes[c];
            return n.parent == parent && n.key == key && n.behaviourId == behaviourId && n.kind == kind;
        }

        static int Slot(int parent, PathKind kind, int behaviourId, uint key)
        {
            unchecked
            {
                uint h = (uint)parent * 0x9E3779B1u ^ (uint)behaviourId * 0x85EBCA77u ^ key * 0xC2B2AE3Du ^ (uint)kind * 0x27D4EB2Fu;
                h ^= h >> 15;
                h *= 0x2C1B3C6Du;
                h ^= h >> 12;
                return (int)(h & 0xFFFF);
            }
        }
    }
}
