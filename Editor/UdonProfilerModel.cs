using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MagmaVRC.UdonProfiler
{
    public enum GroupMode { Script, GameObject, Event }
    public enum NodeKind { Group, Behaviour, Event, Method, Call, Extern }

    /// <summary>One row of the profile tree. Instances are persistent and keep their id across builds.</summary>
    public sealed class Node
    {
        public int id;
        public NodeKind kind;
        public string name;
        public Node parent;
        public List<Node> children;
        public int behaviourId;
        public uint entry;
        public int path = -1;
        public int scriptId = -1;
        public string sig;
        public double totalMs, selfMs, externMs, maxMs;
        public double externAllMs = double.NaN;
        public double p95Ms = double.NaN;
        public double baselineMs = double.NaN;
        public long allocBytes;
        public long instructions, selfInstructions;
        public int calls, externCalls, fusedCalls, instances;
        public bool halted;

        internal int row = -1;
        internal int stamp = -1;
        internal int attachStamp = -1;
        internal bool eventRole;
        internal bool decided;
        internal int decidedGen = int.MinValue;
        internal bool hiddenTiny;
        internal bool member;
        internal int haltedCount;
        internal int[] hist;
        internal int histMark = -1;
        internal int keepMark = -1;
        internal List<Node> childBuf;
        internal List<Node> externSet;
    }

    public struct EdgeRow { public int fromBehaviour; public uint fromEntry; public int toBehaviour; public uint toEntry; public int count; public double ms; public bool delayed; }
    public struct ExternRow { public string sig; public int count; public int fused; public double ms; }
    public struct EdgeStat { public int fromBehaviour; public uint fromEntry; public int toBehaviour; public uint toEntry; public bool delayed; public int count; public long ticks; }

    /// <summary>Parameters of one <see cref="UdonProfilerModel.Build"/> call.</summary>
    public struct ViewQuery
    {
        public int from, to;
        public GroupMode mode;
        public bool loopOnly;
        public string search;
        public double hideBelowPct;
        public bool refreshMembership;
    }

    /// <summary>Cached per-frame totals, script segments and event edges.</summary>
    public sealed class FrameSummary
    {
        public int serial = -1, frameNumber;
        public long totalTicks, loopTicks;
        public long instructions, loopInstructions;
        public bool hasInstructions;
        public int events, loopEvents, externs, loopExterns, fused, loopFused, droppedSpans;
        public int segmentCount;
        public int[] segScript = new int[8];
        public long[] segTicks = new long[8];
        public long[] segLoopTicks = new long[8];
        public int edgeCount;
        public EdgeStat[] edges = new EdgeStat[8];
    }

    public struct TimelineItem
    {
        public PathKind kind;
        public int path;
        public int behaviourId;
        public uint key;
        public int scriptId;
        public int depth;
        public int sample;
        public double startMs, ms, selfMs, externMs;
        public long instructions, selfInstructions;
        public int externCount, fusedCount;
        public bool halted;
        public string label;
    }

    /// <summary>Timeline items of one frame.</summary>
    public sealed class TimelineData
    {
        public int serial = -1, frameNumber, generation, mutation;
        public bool isSealed, hasInstructions;
        public readonly List<TimelineItem> items = new List<TimelineItem>();
        public double totalMs;
        public int maxDepth;
        public int droppedSpans;
    }

    public struct FlameNode
    {
        public int parent, firstChild, nextSibling, depth;
        public NodeKind kind;
        public string label;
        public int scriptId;
        public double startMs, totalMs, selfMs;
        public double instructions, selfInstructions;
        /// <summary>Layout start and width in the graph's size unit: ms, or instructions when <see cref="FlameGraph.byInstructions"/> is set.</summary>
        public double start, size;
        public double calls;
        public int fused;
        public int behaviourId;
        public uint key;
        public string sig;
    }

    /// <summary>Merged call-path tree of the last build, as per-frame averages.</summary>
    public sealed class FlameGraph
    {
        public FlameNode[] nodes = new FlameNode[256];
        public int count;
        public int maxDepth;
        public double totalMs, totalInstructions;
        public int frames;
        public bool hasInstructions, byInstructions;
    }

    /// <summary>Result of one <see cref="UdonProfilerModel.Build"/> call.</summary>
    public sealed class ProfileView
    {
        public int fromFrame, toFrame, frameCount, generation;
        public double avgMs, maxMs;
        public int avgEvents, avgExterns, avgFused, activeBehaviours, droppedSpans;
        /// <summary>True when at least one frame in the range has instruction counts; node and average counts are meaningless otherwise.</summary>
        public bool hasInstructions;
        /// <summary>Average Udon instructions per frame over the frames that have counts.</summary>
        public double avgInstructions;
        public readonly List<Node> roots = new List<Node>();
        public int structureStamp;
        /// <summary>Networking totals of the range; estimates.</summary>
        public NetSummary network;

        internal UdonProfilerModel model;

        /// <summary>Networking totals of one behaviour over the range, or null when it had no network activity.</summary>
        public NetRow NetOf(int behaviourId) => model?.NetOf(behaviourId);

        /// <summary>Returns the row with this id when it is reached and attached in this build.</summary>
        public Node FindById(int id) => model?.FindById(id);

        /// <summary>Returns the row a call path maps to in this view's mode, or null when not reached.</summary>
        public Node FindByPath(int path) => model?.FindByPath(path);

        /// <summary>Returns the Event row (Behaviour row in Event mode) of an event, or null when not reached.</summary>
        public Node FindEvent(int behaviourId, uint entry) => model?.FindEvent(behaviourId, entry);

        /// <summary>Share of the average frame time, in percent.</summary>
        public double Pct(Node n) => n == null || avgMs <= 0 || frameCount <= 0 ? 0 : n.totalMs / frameCount / avgMs * 100;

        /// <summary>Edges whose callee matches the node, merged and sorted by ms.</summary>
        public List<EdgeRow> EdgesInto(Node n) => model != null ? model.Edges(n, true) : new List<EdgeRow>();

        /// <summary>Edges whose caller matches the node, merged and sorted by ms.</summary>
        public List<EdgeRow> EdgesOutOf(Node n) => model != null ? model.Edges(n, false) : new List<EdgeRow>();

        /// <summary>Externs run by the node (directly for Event and Method rows, by any descendant for aggregates), by signature.</summary>
        public List<ExternRow> ExternsOf(Node n) => model != null ? model.ExternsOf(n) : new List<ExternRow>();

        /// <summary>Reached Behaviour rows under a group, sorted by total time.</summary>
        public List<Node> InstancesOf(Node group)
        {
            var r = new List<Node>();
            if (group?.children == null || model == null) return r;
            foreach (var c in group.children)
                if (c.kind == NodeKind.Behaviour && model.IsReached(c)) r.Add(c);
            r.Sort((a, b) => b.totalMs != a.totalMs ? b.totalMs.CompareTo(a.totalMs) : a.id.CompareTo(b.id));
            return r;
        }
    }

    /// <summary>Aggregates recorded frames into tree views, frame summaries, timelines and flame graphs.</summary>
    public sealed partial class UdonProfilerModel
    {
        const int ExternChildLimit = 8;
        const string OverflowName = "(path table full)";

        struct Acc { public long incl, self, ext, alloc, max, extAll, instr, selfInstr; public int calls, extCount, fused, mark; }

        static readonly Dictionary<int, (int, NodeKind, string)> _owner = new Dictionary<int, (int, NodeKind, string)>();

        int _gen = int.MinValue;
        Acc[] _acc = new Acc[1024];
        int _accStamp = 1;
        readonly List<int> _touched = new List<int>();
        int[] _maxSerial = new int[1024];
        bool[] _maxStale = new bool[1024];
        readonly List<int> _staleList = new List<int>();

        readonly List<Node> _rows = new List<Node>();
        readonly Dictionary<(int, NodeKind, string), Node> _rowByKey = new Dictionary<(int, NodeKind, string), Node>();
        readonly Dictionary<int, Node> _byId = new Dictionary<int, Node>();
        readonly int[][] _rowOf = new int[3][];
        readonly int[][] _callRowOf = new int[3][];
        readonly byte[][] _counts = new byte[3][];
        readonly Dictionary<(int, int), Node>[] _behRow = { new Dictionary<(int, int), Node>(), new Dictionary<(int, int), Node>(), new Dictionary<(int, int), Node>() };
        readonly Dictionary<(int, string), int> _behKeyOwner = new Dictionary<(int, string), int>();
        readonly Dictionary<(int, uint), Node>[] _eventRow = { new Dictionary<(int, uint), Node>(), new Dictionary<(int, uint), Node>(), new Dictionary<(int, uint), Node>() };
        int[] _behIndexOfPath = new int[1024];

        int _stamp;
        readonly List<Node> _reached = new List<Node>();
        List<Node> _attached = new List<Node>();
        List<Node> _attachedNext = new List<Node>();
        readonly List<Node> _histRows = new List<Node>();
        int _histStamp;
        int[] _behFrames = new int[64];
        int[] _behFrameMark = new int[64];
        int _frameMark;
        int _lastStructureHash = int.MinValue;
        string _search;

        GroupMode _mode;
        readonly ProfileView _view = new ProfileView();

        int _memoFrom, _memoTo, _memoGen, _memoVersion, _memoMutation;
        GroupMode _memoMode;
        bool _memoLoop, _memoValid, _flameValid;
        internal int MergeRuns, EdgeScans;

        int _incFrom, _incTo, _incGen, _incLength;
        GroupMode _incMode;
        bool _incLoop, _incValid;
        int _ovSerial = -1;
        readonly List<PathStat> _ovStats = new List<PathStat>();
        readonly List<(int path, long alloc, long ext, byte bucket, bool halted, int beh)> _ovSamples = new List<(int, long, long, byte, bool, int)>();
        readonly List<int> _ovBehs = new List<int>();

        readonly Dictionary<int, (List<EdgeRow> into, List<EdgeRow> outOf)> _edgeCache = new Dictionary<int, (List<EdgeRow>, List<EdgeRow>)>();
        readonly Dictionary<(int, uint, int, uint, bool), int> _edgeIndex = new Dictionary<(int, uint, int, uint, bool), int>();
        readonly Dictionary<int, List<ExternRow>> _externCache = new Dictionary<int, List<ExternRow>>();
        long[] _extTicksBySig = new long[64];
        int[] _extCallsBySig = new int[64];
        int[] _extFusedBySig = new int[64];
        readonly List<int> _touchedSigs = new List<int>();

        public UdonProfilerModel()
        {
            _view.model = this;
            for (int m = 0; m < 3; m++) { _rowOf[m] = Filled(1024, -1); _callRowOf[m] = Filled(1024, -1); _counts[m] = new byte[1024]; }
            Array.Fill(_behIndexOfPath, -2);
        }

        static int[] Filled(int n, int v) { var a = new int[n]; Array.Fill(a, v); return a; }

        static int Grow(int need, int have) { int n = Math.Max(have, 16); while (n < need) n *= 2; return n; }

        static void GrowFilled(ref int[] a, int need, int fill)
        {
            if (a.Length >= need) return;
            int old = a.Length;
            Array.Resize(ref a, Grow(need, old));
            for (int i = old; i < a.Length; i++) a[i] = fill;
        }

        void EnsurePathArrays()
        {
            int need = UdonProfilerRecorder.Paths.Count;
            if (_acc.Length < need) Array.Resize(ref _acc, Grow(need, _acc.Length));
            if (_maxSerial.Length < need) { Array.Resize(ref _maxSerial, Grow(need, _maxSerial.Length)); Array.Resize(ref _maxStale, _maxSerial.Length); }
            GrowFilled(ref _behIndexOfPath, need, -2);
            for (int m = 0; m < 3; m++)
            {
                GrowFilled(ref _rowOf[m], need, -1);
                GrowFilled(ref _callRowOf[m], need, -1);
                if (_counts[m].Length < need) Array.Resize(ref _counts[m], Grow(need, _counts[m].Length));
            }
            if (_flameKeyOfPath.Length < need) GrowFilled(ref _flameKeyOfPath, need, -1);
        }

        void CheckGen()
        {
            int g = UdonProfilerRecorder.Generation;
            if (g == _gen) { EnsurePathArrays(); return; }
            _gen = g;
            for (int m = 0; m < 3; m++)
            {
                Array.Fill(_rowOf[m], -1);
                Array.Fill(_callRowOf[m], -1);
                Array.Clear(_counts[m], 0, _counts[m].Length);
                _behRow[m].Clear();
                _eventRow[m].Clear();
            }
            _behKeyOwner.Clear();
            Array.Fill(_behIndexOfPath, -2);
            Array.Fill(_flameKeyOfPath, -1);
            _accStamp++;
            _touched.Clear();
            _staleList.Clear();
            Array.Clear(_maxStale, 0, _maxStale.Length);
            _incValid = false;
            _memoValid = false;
            _flameValid = false;
            _ovSerial = -1;
            _ovStats.Clear(); _ovSamples.Clear(); _ovBehs.Clear();
            EnsurePathArrays();
        }

        static int IdFor(int parentId, NodeKind kind, string name)
        {
            unchecked
            {
                uint h = 2166136261;
                void Mix(int v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (i * 8)); h *= 16777619; } }
                Mix(parentId);
                Mix((int)kind);
                foreach (char c in name ?? "") { h ^= (byte)c; h *= 16777619; h ^= (byte)(c >> 8); h *= 16777619; }
                int id = (int)h;
                if (id == 0) id = 1;
                var key = (parentId, kind, name);
                while (_owner.TryGetValue(id, out var o) && !o.Equals(key)) { id++; if (id == 0) id = 1; }
                _owner[id] = key;
                return id;
            }
        }

        Node RowFor(Node parent, NodeKind kind, string key, string display)
        {
            int pid = parent?.id ?? 0;
            if (_rowByKey.TryGetValue((pid, kind, key), out var n)) return n;
            n = new Node { id = IdFor(pid, kind, key), kind = kind, name = display, parent = parent, row = _rows.Count };
            _rows.Add(n);
            _rowByKey[(pid, kind, key)] = n;
            _byId[n.id] = n;
            return n;
        }

        static string ScriptOf(BehaviourInfo info) => info?.script ?? "?";

        Node GroupRow(GroupMode m, string key, int scriptId)
        {
            var g = RowFor(null, NodeKind.Group, m + ":" + key, key);
            g.scriptId = m == GroupMode.Script ? scriptId : -1;
            return g;
        }

        Node BehaviourRow(Node group, int b, int m, int scriptId)
        {
            if (_behRow[m].TryGetValue((group.id, b), out var row)) return row;
            var info = UdonProfilerRecorder.GetInfo(b);
            string baseKey = info?.objectKey;
            if (string.IsNullOrEmpty(baseKey)) baseKey = info?.objectPath ?? b.ToString(CultureInfo.InvariantCulture);
            string key = baseKey;
            for (int n = 2; _behKeyOwner.TryGetValue((group.id, key), out int owner) && owner != b; n++) key = baseKey + "#" + n.ToString(CultureInfo.InvariantCulture);
            _behKeyOwner[(group.id, key)] = b;
            row = RowFor(group, NodeKind.Behaviour, key, info?.objectName ?? b.ToString(CultureInfo.InvariantCulture));
            row.behaviourId = b;
            row.scriptId = scriptId;
            row.instances = 1;
            _behRow[m][(group.id, b)] = row;
            return row;
        }

        Node EventRowFor(int p, in PathNode n, int m)
        {
            var mode = (GroupMode)m;
            Node row;
            if (p == PathTable.OverflowPath)
            {
                var g = GroupRow(mode, OverflowName, -1);
                if (mode == GroupMode.Event) { row = RowFor(g, NodeKind.Behaviour, OverflowName, OverflowName); row.eventRole = true; }
                else row = RowFor(g, NodeKind.Event, OverflowName, OverflowName);
                row.behaviourId = n.behaviourId; row.entry = n.key; row.scriptId = -1;
                if (row.path < 0) row.path = p;
                return row;
            }
            if (_eventRow[m].TryGetValue((n.behaviourId, n.key), out row)) { if (row.path < 0) row.path = p; return row; }
            var info = UdonProfilerRecorder.GetInfo(n.behaviourId);
            string ev = UdonProfilerRecorder.EventName(n.behaviourId, n.key);
            string gk = mode == GroupMode.Script ? ScriptOf(info) : mode == GroupMode.GameObject ? info?.objectPath ?? "?" : ev;
            var group = GroupRow(mode, gk, n.scriptId);
            var beh = BehaviourRow(group, n.behaviourId, m, n.scriptId);
            if (mode == GroupMode.Event) { row = beh; row.eventRole = true; }
            else { row = RowFor(beh, NodeKind.Event, ev, ev); row.scriptId = n.scriptId; }
            row.behaviourId = n.behaviourId;
            row.entry = n.key;
            row.path = p;
            _eventRow[m][(n.behaviourId, n.key)] = row;
            return row;
        }

        internal int RowOf(int p, int m)
        {
            int r = _rowOf[m][p];
            if (r >= 0) return r;
            var n = UdonProfilerRecorder.Paths[p];
            Node row;
            if (n.kind == PathKind.Event) row = EventRowFor(p, n, m);
            else
            {
                var parentRow = n.parent >= 0 ? _rows[RowOf(n.parent, m)] : GroupRow((GroupMode)m, "?", -1);
                if (n.kind == PathKind.Method)
                {
                    string name = UdonProfilerRecorder.Method(n.behaviourId, n.key).name ?? "sub_0x" + n.key.ToString("X");
                    row = RowFor(parentRow, NodeKind.Method, name, name);
                    row.scriptId = n.scriptId;
                }
                else
                {
                    string sig = UdonProfilerRecorder.Sig((int)n.key);
                    row = RowFor(parentRow, NodeKind.Extern, sig, sig);
                    row.sig = sig;
                    row.scriptId = -1;
                }
                row.behaviourId = n.behaviourId;
                row.entry = n.key;
                if (row.path < 0) row.path = p;
            }
            _rowOf[m][p] = row.row;
            return row.row;
        }

        internal int CallRowOf(int p, int m)
        {
            int r = _callRowOf[m][p];
            if (r >= 0) return r;
            var n = UdonProfilerRecorder.Paths[p];
            if (n.kind != PathKind.Event || n.parent < 0) return -1;
            var caller = _rows[RowOf(n.parent, m)];
            var info = UdonProfilerRecorder.GetInfo(n.behaviourId);
            string script = ScriptOf(info), ev = UdonProfilerRecorder.EventName(n.behaviourId, n.key);
            string objKey = info?.objectKey;
            if (string.IsNullOrEmpty(objKey)) objKey = info?.objectPath ?? n.behaviourId.ToString(CultureInfo.InvariantCulture);
            string key = p == PathTable.OverflowPath ? OverflowName : script + "." + ev + "@" + objKey;
            var row = RowFor(caller, NodeKind.Call, key, "-> " + script + "." + ev + " (" + (info?.objectName ?? "?") + ")");
            row.behaviourId = n.behaviourId;
            row.entry = n.key;
            row.scriptId = n.scriptId;
            if (row.path < 0) row.path = p;
            _callRowOf[m][p] = row.row;
            return row.row;
        }

        static Node GroupOf(Node r) { while (r.parent != null) r = r.parent; return r; }

        static Node BehOf(Node eventRow) => eventRow.eventRole || eventRow.kind == NodeKind.Behaviour ? eventRow : eventRow.parent;

        byte Counts(int p, int m)
        {
            byte c = _counts[m][p];
            if ((c & 0x80) != 0) return c;
            var paths = UdonProfilerRecorder.Paths;
            int own = RowOf(p, m);
            bool isEvent = paths[p].kind == PathKind.Event;
            Node ownRow = _rows[own];
            Node beh = isEvent ? BehOf(ownRow) : null, grp = GroupOf(ownRow);
            int call = isEvent && paths[p].parent >= 0 ? CallRowOf(p, m) : -1;
            bool b0 = true, b1 = true, b2 = true, b3 = true;
            for (int q = paths[p].parent; q >= 0; q = paths[q].parent)
            {
                int qr = RowOf(q, m);
                if (qr == own) b0 = false;
                if (isEvent && paths[q].kind == PathKind.Event)
                {
                    var qRow = _rows[qr];
                    if (BehOf(qRow) == beh) b1 = false;
                    if (GroupOf(qRow) == grp) b2 = false;
                    if (call >= 0 && paths[q].parent >= 0 && CallRowOf(q, m) == call) b3 = false;
                }
            }
            c = (byte)(0x80 | (b0 ? 1 : 0) | (b1 ? 2 : 0) | (b2 ? 4 : 0) | (b3 ? 8 : 0));
            _counts[m][p] = c;
            return c;
        }

        int BehIndexOf(int p, int behaviourId)
        {
            if (p == PathTable.OverflowPath) return UdonProfilerRecorder.GetInfo(behaviourId)?.index ?? -1;
            int v = _behIndexOfPath[p];
            if (v != -2) return v;
            v = UdonProfilerRecorder.GetInfo(UdonProfilerRecorder.Paths[p].behaviourId)?.index ?? -1;
            _behIndexOfPath[p] = v;
            return v;
        }

        internal bool IsReached(Node n) => n != null && n.stamp == _stamp;
        bool IsAttached(Node n) => n != null && n.attachStamp == _attachPass;

        internal Node FindById(int id) => _byId.TryGetValue(id, out var n) && IsReached(n) && IsAttached(n) ? n : null;

        internal Node FindByPath(int path)
        {
            if (_gen != UdonProfilerRecorder.Generation || path < 0 || path >= UdonProfilerRecorder.Paths.Count || path >= _rowOf[0].Length) return null;
            var n = _rows[RowOf(path, (int)_mode)];
            return IsReached(n) ? n : null;
        }

        internal Node FindEvent(int behaviourId, uint entry)
        {
            if (_gen != UdonProfilerRecorder.Generation) return null;
            return _eventRow[(int)_mode].TryGetValue((behaviourId, entry), out var n) && IsReached(n) ? n : null;
        }
    }

    public sealed partial class UdonProfilerModel
    {
        /// <summary>Builds (or reuses) the view for a frame range; returns the same model-owned instance every call.</summary>
        public ProfileView Build(in ViewQuery q)
        {
            CheckGen();
            var v = _view;
            int newest = UdonProfilerRecorder.NewestFrame;
            int oldest = newest - UdonProfilerRecorder.FrameCount + 1;
            int from = Math.Max(q.from, oldest), to = Math.Min(q.to, newest);
            int m = (int)q.mode;
            _mode = q.mode;
            v.generation = _gen;
            if (newest < 0 || to < from)
            {
                BuildEmpty(v);
                v.network = BuildNetwork(0, -1);
                return v;
            }
            v.network = BuildNetwork(from, to);
            var toFrame = UdonProfilerRecorder.GetFrame(to);
            bool sealedRange = toFrame != null && toFrame.isSealed;
            bool memoHit = _memoValid && _memoFrom == from && _memoTo == to && _memoMode == q.mode && _memoLoop == q.loopOnly && _memoGen == _gen
                && (sealedRange || (UdonProfilerRecorder.Version == _memoVersion && toFrame != null && toFrame.mutation == _memoMutation));
            if (memoHit)
            {
                if (q.refreshMembership) { _search = q.search; Structure(v, q, true); }
                ApplyBaseline(v);
                return v;
            }

            FramePass(v, from, to, q.loopOnly);
            Merge(from, to, m, q.loopOnly);
            MergeRuns++;
            if (q.refreshMembership && _staleList.Count > 0) RecomputeStaleMax(from, to, q.loopOnly);
            Accumulate(v, m);
            if (q.refreshMembership || _search == null) _search = q.search;
            Structure(v, q, q.refreshMembership);
            Percentiles();
            ApplyBaseline(v);

            _memoValid = true; _memoFrom = from; _memoTo = to; _memoMode = q.mode; _memoLoop = q.loopOnly; _memoGen = _gen;
            _memoVersion = UdonProfilerRecorder.Version; _memoMutation = toFrame?.mutation ?? 0;
            _flameValid = false;
            _edgeCache.Clear();
            _externCache.Clear();
            return v;
        }

        void BuildEmpty(ProfileView v)
        {
            v.fromFrame = v.toFrame = 0; v.frameCount = 0;
            v.avgMs = v.maxMs = 0; v.avgEvents = v.avgExterns = v.avgFused = v.activeBehaviours = v.droppedSpans = 0;
            v.hasInstructions = false; v.avgInstructions = 0;
            _stamp++;
            foreach (var r in _attached) { r.children = null; r.childBuf?.Clear(); }
            _attached.Clear();
            v.roots.Clear();
            int h = 0;
            if (h != _lastStructureHash) { _lastStructureHash = h; v.structureStamp++; }
            _memoValid = false;
            _incValid = false;
            _flameValid = false;
            _accStamp++;
            _touched.Clear();
            _edgeCache.Clear();
            _externCache.Clear();
        }

        void FramePass(ProfileView v, int from, int to, bool loop)
        {
            long total = 0, events = 0, externs = 0, fused = 0, max = 0, instr = 0;
            int dropped = 0, instrFrames = 0;
            for (int s = from; s <= to; s++)
            {
                var fs = Summary(s);
                if (fs == null) continue;
                if (fs.hasInstructions)
                {
                    instrFrames++;
                    instr += loop ? fs.loopInstructions : fs.instructions;
                }
                long t = loop ? fs.loopTicks : fs.totalTicks;
                total += t;
                events += loop ? fs.loopEvents : fs.events;
                externs += loop ? fs.loopExterns : fs.externs;
                fused += loop ? fs.loopFused : fs.fused;
                if (t > max) max = t;
                dropped += fs.droppedSpans;
            }
            int n = to - from + 1;
            double ms = UdonProfilerRecorder.MsPerTick;
            v.fromFrame = from; v.toFrame = to; v.frameCount = n;
            v.avgMs = total * ms / n;
            v.maxMs = max * ms;
            v.avgEvents = (int)Math.Round((double)events / n);
            v.avgExterns = (int)Math.Round((double)externs / n);
            v.avgFused = (int)Math.Round((double)fused / n);
            v.droppedSpans = dropped;
            v.hasInstructions = instrFrames > 0;
            v.avgInstructions = instrFrames > 0 ? (double)instr / instrFrames : 0;
        }

        void Merge(int from, int to, int m, bool loop)
        {
            var toFrame = UdonProfilerRecorder.GetFrame(to);
            int lastSealed = toFrame != null && !toFrame.isSealed ? to - 1 : to;
            int length = to - from + 1;
            bool inc = _incValid && _incMode == (GroupMode)m && _incLoop == loop && _incGen == _gen && _incLength == length
                && length <= UdonProfilerRecorder.Capacity / 2 && from >= _incFrom && from - _incFrom <= length / 2 && from <= _incTo + 1 && lastSealed >= _incTo;
            if (inc)
                for (int s = _incFrom; s < from; s++)
                    if (UdonProfilerRecorder.GetFrame(s) == null) { inc = false; break; }

            if (inc)
            {
                UndoOverlay(m);
                for (int s = _incFrom; s < from; s++) ApplyFrame(UdonProfilerRecorder.GetFrame(s), -1, m, loop, false);
                for (int s = _incTo + 1; s <= lastSealed; s++) { var f = UdonProfilerRecorder.GetFrame(s); if (f != null) ApplyFrame(f, 1, m, loop, false); }
            }
            else
            {
                _accStamp++;
                _touched.Clear();
                _staleList.Clear();
                foreach (var r in _histRows) { if (r.hist != null) Array.Clear(r.hist, 0, r.hist.Length); r.haltedCount = 0; r.histMark = -1; }
                _histRows.Clear();
                _histStamp++;
                Array.Clear(_behFrames, 0, _behFrames.Length);
                _ovSerial = -1; _ovStats.Clear(); _ovSamples.Clear(); _ovBehs.Clear();
                for (int s = from; s <= lastSealed; s++) { var f = UdonProfilerRecorder.GetFrame(s); if (f != null) ApplyFrame(f, 1, m, loop, false); }
            }
            if (lastSealed < to && toFrame != null) { ApplyFrame(toFrame, 1, m, loop, true); _ovSerial = to; }
            _incValid = true; _incFrom = from; _incTo = lastSealed; _incMode = (GroupMode)m; _incLoop = loop; _incGen = _gen; _incLength = length;
        }

        void Touch(int p)
        {
            ref var a = ref _acc[p];
            if (a.mark == _accStamp) return;
            a = default;
            a.mark = _accStamp;
            _maxSerial[p] = -1;
            _maxStale[p] = false;
            _touched.Add(p);
        }

        void ApplyFrame(Frame f, int sign, int m, bool loop, bool overlay)
        {
            if (f.statCount > 0 || f.sampleCount > 0) EnsurePathArrays();
            int serial = f.serial;
            int pathCount = UdonProfilerRecorder.Paths.Count;
            for (int i = 0; i < f.statCount; i++)
            {
                ref readonly var st = ref f.stats[i];
                int p = st.path;
                if ((uint)p >= (uint)pathCount || (loop && !RootLoop(p))) continue;
                AddStat(in st, sign, serial, overlay);
                if (overlay) _ovStats.Add(st);
            }
            _frameMark++;
            for (int i = 0; i < f.sampleCount; i++)
            {
                ref readonly var s = ref f.samples[i];
                if (s.folded)
                {
                    if (s.externTicks == 0) continue;
                    int q = s.parent;
                    while (q >= 0 && q < f.sampleCount && f.samples[q].folded) q = f.samples[q].parent;
                    int fp = q >= 0 && q < f.sampleCount ? f.samples[q].path : s.path;
                    if ((uint)fp >= (uint)pathCount || (loop && !RootLoop(fp))) continue;
                    Touch(fp);
                    _acc[fp].extAll += sign * s.externTicks;
                    if (overlay) _ovSamples.Add((fp, 0, s.externTicks, 0, false, -2));
                    continue;
                }
                int p = s.path;
                if ((uint)p >= (uint)pathCount || (loop && !RootLoop(p))) continue;
                int beh = BehIndexOf(p, s.behaviourId);
                bool newBeh = false;
                if (beh >= 0)
                {
                    if (beh >= _behFrames.Length) { Array.Resize(ref _behFrames, Grow(beh + 1, _behFrames.Length)); Array.Resize(ref _behFrameMark, _behFrames.Length); }
                    if (_behFrameMark[beh] != _frameMark) { _behFrameMark[beh] = _frameMark; newBeh = true; _behFrames[beh] += sign; if (overlay) _ovBehs.Add(beh); }
                }
                AddSample(p, s.allocBytes, s.externTicks, s.bucket, s.halted, sign, m);
                if (overlay) _ovSamples.Add((p, s.allocBytes, s.externTicks, s.bucket, s.halted, newBeh ? beh : -1));
            }
        }

        void AddStat(in PathStat st, int sign, int serial, bool overlay)
        {
            int p = st.path;
            Touch(p);
            ref var a = ref _acc[p];
            a.calls += sign * st.calls;
            a.incl += sign * st.inclusiveTicks;
            a.self += sign * st.selfTicks;
            a.ext += sign * st.externTicks;
            a.extCount += sign * st.externCount;
            a.fused += sign * st.fusedCount;
            a.instr += sign * st.instructions;
            a.selfInstr += sign * st.selfInstructions;
            if (sign > 0)
            {
                if (st.maxTicks > a.max)
                {
                    a.max = st.maxTicks;
                    if (overlay) MarkStale(p);
                    else _maxSerial[p] = serial;
                }
            }
            else if (_maxSerial[p] == serial) MarkStale(p);
        }

        void MarkStale(int p)
        {
            if (_maxStale[p]) return;
            _maxStale[p] = true;
            _staleList.Add(p);
        }

        void AddSample(int p, long alloc, long ext, byte bucket, bool halted, int sign, int m)
        {
            Touch(p);
            ref var a = ref _acc[p];
            a.alloc += sign * alloc;
            a.extAll += sign * ext;
            var r = _rows[RowOf(p, m)];
            AddHist(r, bucket, halted, sign);
            var b = BehOf(r);
            if (b != r && b != null) AddHist(b, bucket, halted, sign);
            var g = GroupOf(r);
            if (g != r && g != b) AddHist(g, bucket, halted, sign);
        }

        void AddHist(Node r, byte bucket, bool halted, int sign)
        {
            if (r.histMark != _histStamp)
            {
                r.histMark = _histStamp;
                if (r.hist == null) r.hist = new int[Histogram.Buckets];
                else Array.Clear(r.hist, 0, r.hist.Length);
                r.haltedCount = 0;
                _histRows.Add(r);
            }
            r.hist[Math.Min(bucket, (byte)(Histogram.Buckets - 1))] += sign;
            if (halted) r.haltedCount += sign;
        }

        void UndoOverlay(int m)
        {
            if (_ovSerial < 0) return;
            foreach (var st in _ovStats)
            {
                ref var a = ref _acc[st.path];
                if (a.mark != _accStamp) continue;
                a.calls -= st.calls; a.incl -= st.inclusiveTicks; a.self -= st.selfTicks; a.ext -= st.externTicks;
                a.extCount -= st.externCount; a.fused -= st.fusedCount;
                a.instr -= st.instructions; a.selfInstr -= st.selfInstructions;
            }
            foreach (var s in _ovSamples)
            {
                if (s.beh == -2)
                {
                    ref var fa = ref _acc[s.path];
                    if (fa.mark == _accStamp) fa.extAll -= s.ext;
                    continue;
                }
                AddSample(s.path, s.alloc, s.ext, s.bucket, s.halted, -1, m);
                if (s.beh >= 0) _behFrames[s.beh]--;
            }
            _ovSerial = -1;
            _ovStats.Clear(); _ovSamples.Clear(); _ovBehs.Clear();
        }

        void RecomputeStaleMax(int from, int to, bool loop)
        {
            foreach (int p in _staleList) { _acc[p].max = 0; _maxSerial[p] = -1; }
            int pathCount = UdonProfilerRecorder.Paths.Count;
            for (int s = from; s <= to; s++)
            {
                var f = UdonProfilerRecorder.GetFrame(s);
                if (f == null) continue;
                for (int i = 0; i < f.statCount; i++)
                {
                    ref readonly var st = ref f.stats[i];
                    int p = st.path;
                    if ((uint)p >= (uint)pathCount || !_maxStale[p] || (loop && !RootLoop(p))) continue;
                    if (st.maxTicks > _acc[p].max) { _acc[p].max = st.maxTicks; _maxSerial[p] = f.isSealed ? s : -1; }
                }
            }
            foreach (int p in _staleList) _maxStale[p] = false;
            _staleList.Clear();
        }

        static bool IsZero(in Acc a) => a.calls == 0 && a.incl == 0 && a.self == 0 && a.ext == 0 && a.extCount == 0 && a.fused == 0 && a.alloc == 0 && a.extAll == 0 && a.instr == 0 && a.selfInstr == 0;

        void ZeroNumbers(Node r)
        {
            r.totalMs = r.selfMs = r.externMs = r.maxMs = 0;
            r.externAllMs = r.kind == NodeKind.Event || r.kind == NodeKind.Behaviour || r.kind == NodeKind.Group ? 0 : double.NaN;
            r.p95Ms = double.NaN;
            r.baselineMs = double.NaN;
            r.allocBytes = 0;
            r.instructions = r.selfInstructions = 0;
            r.calls = r.externCalls = r.fusedCalls = 0;
            r.halted = false;
            if (r.kind == NodeKind.Group) r.instances = 0;
        }

        void Reach(Node r)
        {
            for (; r != null && r.stamp != _stamp; r = r.parent)
            {
                r.stamp = _stamp;
                ZeroNumbers(r);
                _reached.Add(r);
            }
        }

        void Accumulate(ProfileView v, int m)
        {
            _stamp++;
            _reached.Clear();
            foreach (var r in _attached) ZeroNumbers(r);
            double ms = UdonProfilerRecorder.MsPerTick;
            var paths = UdonProfilerRecorder.Paths;
            var mode = (GroupMode)m;
            for (int t = 0; t < _touched.Count; t++)
            {
                int p = _touched[t];
                ref readonly var a = ref _acc[p];
                if (IsZero(in a)) continue;
                var kind = paths[p].kind;
                var R = _rows[RowOf(p, m)];
                Reach(R);
                double incl = a.incl * ms, self = a.self * ms, ext = a.ext * ms, max = a.max * ms;
                if (kind == PathKind.Extern)
                {
                    R.calls += a.calls;
                    R.totalMs += incl;
                    R.selfMs = R.externMs = R.totalMs;
                    R.externCalls = R.calls;
                    R.fusedCalls += a.fused;
                    if (max > R.maxMs) R.maxMs = max;
                    continue;
                }
                byte bits = Counts(p, m);
                bool isEvent = kind == PathKind.Event;
                double extAll = a.extAll * ms;
                R.calls += a.calls; R.selfMs += self; R.externMs += ext; R.externCalls += a.extCount; R.fusedCalls += a.fused;
                R.selfInstructions += a.selfInstr;
                if (max > R.maxMs) R.maxMs = max;
                if ((bits & 1) != 0) { R.totalMs += incl; R.allocBytes += a.alloc; R.instructions += a.instr; }
                if (isEvent) R.externAllMs += extAll;
                for (var n = R.parent; n != null; n = n.parent)
                {
                    bool isBeh = n.kind == NodeKind.Behaviour && !n.eventRole && mode != GroupMode.Event;
                    if (!isBeh && n.kind != NodeKind.Group) continue;
                    n.selfMs += self; n.externMs += ext; n.externCalls += a.extCount; n.fusedCalls += a.fused;
                    n.selfInstructions += a.selfInstr;
                    if (!isEvent) continue;
                    n.externAllMs += extAll;
                    n.calls += a.calls;
                    if (max > n.maxMs) n.maxMs = max;
                    if ((isBeh && (bits & 2) != 0) || (n.kind == NodeKind.Group && (bits & 4) != 0)) { n.totalMs += incl; n.allocBytes += a.alloc; n.instructions += a.instr; }
                }
                if (isEvent && paths[p].parent >= 0)
                {
                    int cr = CallRowOf(p, m);
                    if (cr >= 0)
                    {
                        var C = _rows[cr];
                        Reach(C);
                        C.calls += a.calls;
                        if ((bits & 8) != 0) { C.totalMs += incl; C.instructions += a.instr; }
                        C.selfMs = 0;
                    }
                }
            }
            foreach (var r in _reached) r.halted = r.histMark == _histStamp && r.haltedCount > 0;
            int active = 0;
            for (int i = 0; i < _behFrames.Length; i++) if (_behFrames[i] > 0) active++;
            v.activeBehaviours = active;
        }

        void Percentiles()
        {
            double ms = UdonProfilerRecorder.MsPerTick;
            foreach (var r in _reached)
            {
                if (r.hist == null || r.histMark != _histStamp) continue;
                int n = 0;
                for (int b = 0; b < r.hist.Length; b++) n += r.hist[b];
                if (n <= 0) continue;
                if (n == 1) { r.p95Ms = r.maxMs; continue; }
                int target = (int)Math.Ceiling(0.95 * n), cum = 0;
                for (int b = 0; b < r.hist.Length; b++)
                {
                    cum += r.hist[b];
                    if (cum >= target) { r.p95Ms = Math.Min(Histogram.MidTicks(b) * ms, r.maxMs); break; }
                }
            }
        }

        void ApplyBaseline(ProfileView v)
        {
            var bl = Baseline;
            bool on = bl != null && BaselineMode == _mode;
            foreach (var r in _attached)
                r.baselineMs = on && bl.TryGetValue(r.id, out var per) ? per * v.frameCount : double.NaN;
        }
    }

    public sealed partial class UdonProfilerModel
    {
        int _keepStamp;
        readonly List<Node> _candidates = new List<Node>();
        readonly List<Node> _scratchNodes = new List<Node>();

        void Structure(ProfileView v, in ViewQuery q, bool refresh)
        {
            int frames = Math.Max(1, v.frameCount);
            double thr = q.hideBelowPct > 0 ? v.avgMs * q.hideBelowPct / 100 : 0;
            _candidates.Clear();

            foreach (var r in _reached)
            {
                if (r.decided && !refresh && r.decidedGen != _gen)
                {
                    r.decidedGen = _gen;
                    _firstSeen.Add(r);
                }
                else if (!r.decided || refresh)
                {
                    bool first = !r.decided;
                    r.decided = true;
                    r.decidedGen = _gen;
                    if (first) _firstSeen.Add(r);
                    if (r.kind == NodeKind.Group || thr <= 0) r.hiddenTiny = false;
                    else
                    {
                        double per = r.totalMs / frames;
                        if (first || !r.hiddenTiny) r.hiddenTiny = per < thr;
                        else r.hiddenTiny = !(per > thr * 1.5);
                    }
                    if (r.kind == NodeKind.Extern && first && !refresh)
                    {
                        var set = r.parent.externSet ??= new List<Node>();
                        r.member = set.Count < ExternChildLimit;
                        if (r.member) set.Add(r);
                    }
                }
            }
            if (refresh) RefreshExternSets();

            if (refresh)
            {
                foreach (var r in _reached) if (Passes(r)) _candidates.Add(r);
            }
            else
            {
                foreach (var r in _attached) _candidates.Add(r);
                foreach (var r in _firstSeen) if (Passes(r)) _candidates.Add(r);
            }
            _firstSeen.Clear();
            _attachPass++;

            _keepStamp++;
            string s = _search;
            bool filter = !string.IsNullOrEmpty(s);
            foreach (var r in _candidates)
            {
                if (filter && !SelfOrAncestorMatches(r, s)) continue;
                for (var n = r; n != null && n.keepMark != _keepStamp; n = n.parent) n.keepMark = _keepStamp;
            }

            foreach (var r in _attached) { r.children = null; r.childBuf?.Clear(); }
            _attachedNext.Clear();
            v.roots.Clear();
            foreach (var r in _candidates) Attach(r, v);

            var t = _attached; _attached = _attachedNext; _attachedNext = t;
            foreach (var r in _attached)
                if (r.childBuf != null && r.childBuf.Count > 0) r.children = r.childBuf;

            foreach (var r in _attached)
            {
                if (r.kind != NodeKind.Group) continue;
                int inst = 0;
                if (r.children != null)
                    foreach (var c in r.children)
                        if (c.kind == NodeKind.Behaviour && (c.totalMs > 0 || c.stamp == _stamp)) inst++;
                r.instances = inst;
            }

            int h = _attached.Count;
            unchecked
            {
                foreach (var r in _attached)
                {
                    uint x = (uint)(r.id * (int)0x9E3779B1) ^ (uint)((r.parent?.id ?? 0) * (int)0x85EBCA77);
                    h += (int)((x << 13) | (x >> 19));
                }
            }
            if (h != _lastStructureHash) { _lastStructureHash = h; v.structureStamp++; }
        }

        void Attach(Node r, ProfileView v)
        {
            if (r.keepMark != _keepStamp) return;
            for (var n = r; n != null; n = n.parent)
            {
                if (n.attachStamp == _attachPass) break;
                n.attachStamp = _attachPass;
                _attachedNext.Add(n);
                if (n.parent == null) v.roots.Add(n);
                else (n.parent.childBuf ??= new List<Node>()).Add(n);
            }
        }

        bool Passes(Node r)
        {
            if (r.hiddenTiny) return false;
            if (r.kind == NodeKind.Extern && !r.member) return false;
            return true;
        }

        static bool SelfOrAncestorMatches(Node r, string s)
        {
            for (var n = r; n != null; n = n.parent)
                if (n.name != null && n.name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        void RefreshExternSets()
        {
            _scratchNodes.Clear();
            foreach (var r in _reached) if (r.kind == NodeKind.Extern && r.parent != null) _scratchNodes.Add(r);
            _scratchNodes.Sort((a, b) => a.parent.id != b.parent.id ? a.parent.id.CompareTo(b.parent.id) : ByTotalDesc(a, b));
            for (int i = 0; i < _scratchNodes.Count;)
            {
                var p = _scratchNodes[i].parent;
                int end = i;
                while (end < _scratchNodes.Count && _scratchNodes[end].parent == p) end++;
                var set = p.externSet ??= new List<Node>();
                for (int k = set.Count - 1; k >= 0; k--) if (set[k].stamp != _stamp) { set[k].member = false; set.RemoveAt(k); }
                set.Sort(ByTotalDesc);
                for (int k = i; k < end; k++)
                {
                    var c = _scratchNodes[k];
                    if (c.member) continue;
                    if (set.Count < ExternChildLimit) { c.member = true; set.Add(c); set.Sort(ByTotalDesc); continue; }
                    var last = set[set.Count - 1];
                    if (c.totalMs > last.totalMs * 1.2) { last.member = false; set[set.Count - 1] = c; c.member = true; set.Sort(ByTotalDesc); }
                }
                i = end;
            }
            _scratchNodes.Clear();
        }

        readonly List<Node> _firstSeen = new List<Node>();
        int _attachPass;

        static int ByTotalDesc(Node a, Node b) => a.totalMs != b.totalMs ? b.totalMs.CompareTo(a.totalMs) : a.id.CompareTo(b.id);
    }

    public sealed partial class UdonProfilerModel
    {
        readonly List<EdgeRow> _edgeScratch = new List<EdgeRow>();
        int[] _sigMark = new int[64];
        int _sigStamp = 1;

        internal List<EdgeRow> Edges(Node n, bool into)
        {
            if (n == null || !IsReached(n)) return new List<EdgeRow>();
            if (_edgeCache.TryGetValue(n.id, out var cached)) return into ? cached.into : cached.outOf;
            var inList = new List<EdgeRow>();
            var outList = new List<EdgeRow>();
            bool anyEntry;
            if (n.kind == NodeKind.Event || n.eventRole) anyEntry = false;
            else if (n.kind == NodeKind.Behaviour) anyEntry = true;
            else { _edgeCache[n.id] = (inList, outList); return into ? inList : outList; }
            EdgeScans++;
            int b = n.behaviourId;
            uint e = n.entry;
            var v = _view;
            double ms = UdonProfilerRecorder.MsPerTick;
            for (int dir = 0; dir < 2; dir++)
            {
                _edgeIndex.Clear();
                _edgeScratch.Clear();
                for (int s = v.fromFrame; s <= v.toFrame && v.frameCount > 0; s++)
                {
                    var fs = Summary(s);
                    if (fs == null) continue;
                    for (int i = 0; i < fs.edgeCount; i++)
                    {
                        ref readonly var ed = ref fs.edges[i];
                        bool match = dir == 0
                            ? ed.toBehaviour == b && (anyEntry || ed.toEntry == e)
                            : ed.fromBehaviour == b && (anyEntry || ed.fromEntry == e);
                        if (!match) continue;
                        var key = (ed.fromBehaviour, ed.fromEntry, ed.toBehaviour, ed.toEntry, ed.delayed);
                        if (!_edgeIndex.TryGetValue(key, out int idx))
                        {
                            idx = _edgeScratch.Count;
                            _edgeIndex[key] = idx;
                            _edgeScratch.Add(new EdgeRow { fromBehaviour = ed.fromBehaviour, fromEntry = ed.fromEntry, toBehaviour = ed.toBehaviour, toEntry = ed.toEntry, delayed = ed.delayed });
                        }
                        var row = _edgeScratch[idx];
                        row.count += ed.count;
                        row.ms += ed.ticks * ms;
                        _edgeScratch[idx] = row;
                    }
                }
                var list = dir == 0 ? inList : outList;
                list.AddRange(_edgeScratch);
                list.Sort((x, y) => y.ms.CompareTo(x.ms));
            }
            _edgeIndex.Clear();
            _edgeCache[n.id] = (inList, outList);
            return into ? inList : outList;
        }

        internal List<ExternRow> ExternsOf(Node n)
        {
            if (n == null || !IsReached(n)) return new List<ExternRow>();
            if (_externCache.TryGetValue(n.id, out var cached)) return cached;
            var result = new List<ExternRow>();
            _externCache[n.id] = result;
            if (n.kind == NodeKind.Call) return result;
            bool aggregate = (n.kind == NodeKind.Group || n.kind == NodeKind.Behaviour) && !n.eventRole;
            int sigCount = UdonProfilerRecorder.SigCount;
            if (_extTicksBySig.Length < sigCount)
            {
                int len = Grow(sigCount, _extTicksBySig.Length);
                _extTicksBySig = new long[len]; _extCallsBySig = new int[len]; _extFusedBySig = new int[len];
            }
            var paths = UdonProfilerRecorder.Paths;
            int m = (int)_mode;
            _touchedSigs.Clear();
            foreach (int p in _touched)
            {
                if (paths[p].kind != PathKind.Extern) continue;
                ref readonly var a = ref _acc[p];
                if (IsZero(in a)) continue;
                var x = _rows[RowOf(p, m)];
                bool hit;
                if (n.kind == NodeKind.Extern) hit = x == n;
                else if (aggregate) { hit = false; for (var q = x.parent; q != null; q = q.parent) if (q == n) { hit = true; break; } }
                else hit = x.parent == n;
                if (!hit) continue;
                int k = (int)paths[p].key;
                if ((uint)k >= (uint)_extTicksBySig.Length) continue;
                if (_sigMark.Length < _extTicksBySig.Length) Array.Resize(ref _sigMark, _extTicksBySig.Length);
                if (_sigMark[k] != _sigStamp) { _sigMark[k] = _sigStamp; _touchedSigs.Add(k); }
                _extTicksBySig[k] += a.incl;
                _extCallsBySig[k] += a.calls;
                _extFusedBySig[k] += a.fused;
            }
            _sigStamp++;
            double ms = UdonProfilerRecorder.MsPerTick;
            foreach (int k in _touchedSigs)
            {
                result.Add(new ExternRow { sig = UdonProfilerRecorder.Sig(k), count = _extCallsBySig[k], fused = _extFusedBySig[k], ms = _extTicksBySig[k] * ms });
                _extTicksBySig[k] = 0; _extCallsBySig[k] = 0; _extFusedBySig[k] = 0;
            }
            result.Sort((x, y) => x.ms != y.ms ? y.ms.CompareTo(x.ms) : string.CompareOrdinal(x.sig, y.sig));
            return result;
        }

        public static Dictionary<int, double> Baseline;
        public static GroupMode BaselineMode;
        public static int BaselineFrames;

        /// <summary>Stores every attached row's per-frame total of a view as the baseline for that grouping mode; null or an empty view clears it.</summary>
        public static void SetBaseline(ProfileView v, GroupMode mode)
        {
            if (v == null || v.frameCount == 0) { Baseline = null; return; }
            var bl = new Dictionary<int, double>();
            void Walk(List<Node> nodes)
            {
                if (nodes == null) return;
                foreach (var n in nodes) { bl[n.id] = n.totalMs / v.frameCount; Walk(n.children); }
            }
            Walk(v.roots);
            Baseline = bl;
            BaselineMode = mode;
            BaselineFrames = v.frameCount;
        }

        /// <summary>Writes the attached tree depth-first as CSV.</summary>
        public static string ToCsv(ProfileView v)
        {
            var sb = new StringBuilder();
            sb.AppendLine("kind,depth,name,script,object,event,total_ms,self_ms,extern_self_ms,extern_incl_methods_ms,max_ms,p95_ms,alloc_kb,calls,externs,fused,avg_ms,pct,instr,self_instr");
            if (v == null) return sb.ToString();
            var ci = CultureInfo.InvariantCulture;
            void Walk(Node n, int depth)
            {
                var info = n.kind == NodeKind.Group ? null : UdonProfilerRecorder.GetInfo(n.behaviourId);
                string ev = n.kind == NodeKind.Event || n.eventRole ? UdonProfilerRecorder.EventName(n.behaviourId, n.entry)
                    : n.kind == NodeKind.Extern ? n.sig : n.kind == NodeKind.Call ? n.name : "";
                double avg = n.kind == NodeKind.Extern ? n.totalMs / Math.Max(1, n.calls - n.fusedCalls) : n.totalMs / Math.Max(1, n.calls);
                sb.Append(n.kind).Append(',').Append(depth.ToString(ci)).Append(',').Append(Q(n.name)).Append(',').Append(Q(info?.script)).Append(',').Append(Q(info?.objectPath)).Append(',').Append(Q(ev)).Append(',')
                  .Append(n.totalMs.ToString("0.000", ci)).Append(',').Append(n.selfMs.ToString("0.000", ci)).Append(',').Append(n.externMs.ToString("0.000", ci)).Append(',')
                  .Append(double.IsNaN(n.externAllMs) ? "" : n.externAllMs.ToString("0.000", ci)).Append(',')
                  .Append(n.maxMs.ToString("0.000", ci)).Append(',').Append(double.IsNaN(n.p95Ms) ? "" : n.p95Ms.ToString("0.000", ci)).Append(',')
                  .Append((n.allocBytes / 1024.0).ToString("0.0", ci)).Append(',')
                  .Append(n.calls.ToString(ci)).Append(',').Append(n.externCalls.ToString(ci)).Append(',').Append(n.fusedCalls.ToString(ci)).Append(',')
                  .Append(avg.ToString("0.0000", ci)).Append(',').Append(v.Pct(n).ToString("0.00", ci)).Append(',')
                  .Append(InstrText(v, n, false, ci)).Append(',').Append(InstrText(v, n, true, ci)).AppendLine();
                if (n.children != null) foreach (var c in n.children) Walk(c, depth + 1);
            }
            foreach (var r in v.roots) Walk(r, 0);
            return sb.ToString();
        }

        static string Q(string s) => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";

        /// <summary>True when the row has meaningful instruction counts in this view: counts were recorded and the row is not an extern.</summary>
        public static bool HasInstructions(ProfileView v, Node n) => v != null && v.hasInstructions && n != null && n.kind != NodeKind.Extern;

        /// <summary>Invariant inclusive or self instruction count of a row for CSV, or an empty string when unavailable.</summary>
        public static string InstrText(ProfileView v, Node n, bool self, IFormatProvider ci) =>
            HasInstructions(v, n) ? (self ? n.selfInstructions : n.instructions).ToString(ci) : "";

        /// <summary>Short count text: 950, 12.3k, 4.56M.</summary>
        public static string FormatCount(double n)
        {
            double a = Math.Abs(n);
            if (a < 10_000) return n.ToString("0");
            if (a < 1_000_000) return (n / 1e3).ToString(a < 100_000 ? "0.0" : "0") + "k";
            if (a < 1_000_000_000) return (n / 1e6).ToString(a < 10_000_000 ? "0.00" : a < 100_000_000 ? "0.0" : "0") + "M";
            return (n / 1e9).ToString("0.00") + "G";
        }

        /// <summary>Source location of a Method, Event, Call or Event-mode Behaviour row; default for other rows.</summary>
        public static SourceRef SourceOf(Node n)
        {
            if (n == null) return default;
            if (n.kind == NodeKind.Method) return UdonProfilerRecorder.Method(n.behaviourId, n.entry);
            if (n.kind == NodeKind.Event || n.kind == NodeKind.Call || n.eventRole)
            {
                var src = UdonProfilerRecorder.SourceAt(n.behaviourId, n.entry);
                if (!src.HasFile) src = UdonProfilerRecorder.SourceAt(n.behaviourId, n.entry + 8);
                return src;
            }
            return default;
        }

        /// <summary>Script asset of the row's behaviour; for a Script-mode group, of its first instance.</summary>
        public static UnityEngine.Object ScriptAssetOf(Node n)
        {
            if (n == null) return null;
            if (n.kind == NodeKind.Group)
            {
                if (n.scriptId < 0 || n.children == null) return null;
                foreach (var c in n.children)
                {
                    var a = UdonProfilerRecorder.GetInfo(c.behaviourId)?.scriptAsset;
                    if (a != null) return a;
                }
                return null;
            }
            return UdonProfilerRecorder.GetInfo(n.behaviourId)?.scriptAsset;
        }
    }

    public sealed partial class UdonProfilerModel
    {
        static readonly FrameSummary[] _sums = new FrameSummary[UdonProfilerRecorder.Capacity];
        static readonly int[] _sumGen = new int[UdonProfilerRecorder.Capacity];
        static readonly int[] _sumSerial = Filled(UdonProfilerRecorder.Capacity, -1);
        static readonly bool[] _sumSealed = new bool[UdonProfilerRecorder.Capacity];
        static readonly FrameSummary _liveSum = new FrameSummary();
        static int _liveSerial = -1, _liveMutation, _liveSamples, _liveGen;
        static long[] _seg = new long[16], _segLoop = new long[16];
        static readonly Dictionary<(int, uint, int, uint, bool), int> _sumEdgeIndex = new Dictionary<(int, uint, int, uint, bool), int>();
        static sbyte[] _rootLoop = new sbyte[1024];
        static int _rootLoopGen = int.MinValue;

        /// <summary>Cached totals of one ring frame; null when the serial is not in the ring.</summary>
        public static FrameSummary Summary(int serial)
        {
            var f = UdonProfilerRecorder.GetFrame(serial);
            if (f == null) return null;
            int gen = UdonProfilerRecorder.Generation;
            if (f.isSealed)
            {
                int slot = serial % UdonProfilerRecorder.Capacity;
                var s = _sums[slot];
                if (s != null && _sumGen[slot] == gen && _sumSerial[slot] == serial && _sumSealed[slot]) return s;
                s ??= _sums[slot] = new FrameSummary();
                Compute(f, s);
                _sumGen[slot] = gen; _sumSerial[slot] = serial; _sumSealed[slot] = true;
                return s;
            }
            if (_liveSerial == serial && _liveMutation == f.mutation && _liveSamples == f.sampleCount && _liveGen == gen) return _liveSum;
            Compute(f, _liveSum);
            _liveSerial = serial; _liveMutation = f.mutation; _liveSamples = f.sampleCount; _liveGen = gen;
            return _liveSum;
        }

        static bool IsLoop(string e) => e == "_update" || e == "_lateUpdate" || e == "_fixedUpdate" || e == "_postLateUpdate";

        internal static bool RootLoop(int p)
        {
            var paths = UdonProfilerRecorder.Paths;
            if (p < 0 || p >= paths.Count) return false;
            int gen = UdonProfilerRecorder.Generation;
            if (gen != _rootLoopGen) { _rootLoopGen = gen; Array.Clear(_rootLoop, 0, _rootLoop.Length); }
            if (_rootLoop.Length < paths.Count) Array.Resize(ref _rootLoop, Grow(paths.Count, _rootLoop.Length));
            sbyte v = _rootLoop[p];
            if (v != 0) return v > 0;
            int q = p;
            while (paths[q].parent >= 0) q = paths[q].parent;
            bool loop = q != PathTable.OverflowPath && IsLoop(UdonProfilerRecorder.EventName(paths[q].behaviourId, paths[q].key));
            _rootLoop[p] = (sbyte)(loop ? 1 : -1);
            return loop;
        }

        static void Compute(Frame f, FrameSummary s)
        {
            var paths = UdonProfilerRecorder.Paths;
            int pathCount = paths.Count;
            s.serial = f.serial; s.frameNumber = f.frameNumber;
            s.totalTicks = f.totalTicks; s.loopTicks = 0;
            s.instructions = f.totalInstructions; s.loopInstructions = 0; s.hasInstructions = f.hasInstructions;
            s.events = f.sampleCount; s.loopEvents = 0;
            s.externs = f.externCount; s.fused = f.fusedCount; s.loopExterns = 0; s.loopFused = 0;
            s.droppedSpans = f.droppedSpans;
            s.edgeCount = 0;
            _sumEdgeIndex.Clear();
            for (int i = 0; i < f.sampleCount; i++)
            {
                ref readonly var smp = ref f.samples[i];
                bool loop = RootLoop(smp.path);
                if (loop) { s.loopEvents++; if (smp.parent < 0) { s.loopTicks += smp.inclusiveTicks; s.loopInstructions += smp.instructions; } }
                if (smp.parent >= 0 && smp.parent < f.sampleCount)
                {
                    ref readonly var par = ref f.samples[smp.parent];
                    AddEdge(s, par.behaviourId, par.entry, smp.behaviourId, smp.entry, false, smp.inclusiveTicks);
                }
                else if (smp.scheduledByBehaviour != 0)
                    AddEdge(s, smp.scheduledByBehaviour, smp.scheduledByEntry, smp.behaviourId, smp.entry, true, smp.inclusiveTicks);
            }
            int maxSlot = -1;
            for (int i = 0; i < f.statCount; i++)
            {
                ref readonly var st = ref f.stats[i];
                int p = st.path;
                if ((uint)p >= (uint)pathCount) continue;
                ref readonly var node = ref paths[p];
                if (node.kind == PathKind.Extern) continue;
                int slot = node.scriptId + 1;
                if (slot < 0) slot = 0;
                if (slot >= _seg.Length) { int len = Grow(slot + 1, _seg.Length); Array.Resize(ref _seg, len); Array.Resize(ref _segLoop, len); }
                if (slot > maxSlot) maxSlot = slot;
                _seg[slot] += st.selfTicks;
                if (RootLoop(p))
                {
                    _segLoop[slot] += st.selfTicks;
                    s.loopExterns += st.externCount;
                    s.loopFused += st.fusedCount;
                }
            }
            int n = 0;
            for (int slot = 0; slot <= maxSlot; slot++)
            {
                if (_seg[slot] == 0 && _segLoop[slot] == 0) continue;
                if (n == s.segScript.Length)
                {
                    int len = n * 2;
                    Array.Resize(ref s.segScript, len); Array.Resize(ref s.segTicks, len); Array.Resize(ref s.segLoopTicks, len);
                }
                s.segScript[n] = slot - 1; s.segTicks[n] = _seg[slot]; s.segLoopTicks[n] = _segLoop[slot];
                n++;
                _seg[slot] = 0; _segLoop[slot] = 0;
            }
            s.segmentCount = n;
        }

        static void AddEdge(FrameSummary s, int fb, uint fe, int tb, uint te, bool delayed, long ticks)
        {
            var key = (fb, fe, tb, te, delayed);
            int idx = -1;
            if (s.edgeCount <= 32)
            {
                for (int i = 0; i < s.edgeCount; i++)
                {
                    ref readonly var e = ref s.edges[i];
                    if (e.fromBehaviour == fb && e.fromEntry == fe && e.toBehaviour == tb && e.toEntry == te && e.delayed == delayed) { idx = i; break; }
                }
            }
            else if (!_sumEdgeIndex.TryGetValue(key, out idx)) idx = -1;
            if (idx < 0)
            {
                if (s.edgeCount == s.edges.Length) Array.Resize(ref s.edges, s.edges.Length * 2);
                idx = s.edgeCount++;
                s.edges[idx] = new EdgeStat { fromBehaviour = fb, fromEntry = fe, toBehaviour = tb, toEntry = te, delayed = delayed };
                if (s.edgeCount == 33)
                    for (int i = 0; i < s.edgeCount; i++)
                    {
                        ref readonly var e = ref s.edges[i];
                        _sumEdgeIndex[(e.fromBehaviour, e.fromEntry, e.toBehaviour, e.toEntry, e.delayed)] = i;
                    }
                else if (s.edgeCount > 33) _sumEdgeIndex[key] = idx;
            }
            s.edges[idx].count++;
            s.edges[idx].ticks += ticks;
        }
    }

    public sealed partial class UdonProfilerModel
    {
        static readonly TimelineData _timeline = new TimelineData();
        static TimelineItem[] _tlItems = new TimelineItem[256];
        static long[] _tlStart = new long[256];
        static int[] _tlOrder = new int[256];
        static int[] _tlStack = new int[64];
        static string[] _eventLabelOfPath = new string[1024], _methodLabelOfPath = new string[1024];
        static int _labelGen = int.MinValue;
        static readonly TimelineSweepOrder _sweepOrder = new TimelineSweepOrder();
        static readonly Comparer<TimelineItem> _byDepthStart = Comparer<TimelineItem>.Create((a, b) => a.depth != b.depth ? a.depth.CompareTo(b.depth) : a.startMs.CompareTo(b.startMs));

        sealed class TimelineSweepOrder : IComparer<int>
        {
            public int Compare(int a, int b)
            {
                int c = _tlStart[a].CompareTo(_tlStart[b]);
                return c != 0 ? c : _tlItems[a].depth.CompareTo(_tlItems[b].depth);
            }
        }

        /// <summary>Timeline items of one frame, cached by serial and generation (and mutation while the frame is open); empty when the frame left the ring.</summary>
        public static TimelineData Timeline(int serial)
        {
            var d = _timeline;
            var f = UdonProfilerRecorder.GetFrame(serial);
            int gen = UdonProfilerRecorder.Generation;
            if (f == null)
            {
                if (d.serial != -1 || d.generation != gen || d.items.Count > 0)
                {
                    d.items.Clear();
                    d.serial = -1; d.frameNumber = 0; d.generation = gen; d.mutation = 0; d.isSealed = false;
                    d.totalMs = 0; d.maxDepth = 0; d.droppedSpans = 0; d.hasInstructions = false;
                }
                return d;
            }
            if (d.serial == serial && d.generation == gen && (d.isSealed || (!f.isSealed && f.mutation == d.mutation))) return d;
            if (gen != _labelGen)
            {
                _labelGen = gen;
                Array.Clear(_eventLabelOfPath, 0, _eventLabelOfPath.Length);
                Array.Clear(_methodLabelOfPath, 0, _methodLabelOfPath.Length);
            }
            var paths = UdonProfilerRecorder.Paths;
            int pathCount = paths.Count;
            if (_eventLabelOfPath.Length < pathCount)
            {
                int len = Grow(pathCount, _eventLabelOfPath.Length);
                Array.Resize(ref _eventLabelOfPath, len);
                Array.Resize(ref _methodLabelOfPath, len);
            }
            double ms = UdonProfilerRecorder.MsPerTick;
            int cap = f.sampleCount + f.spanCount;
            if (_tlItems.Length < cap)
            {
                int len = Grow(cap, _tlItems.Length);
                _tlItems = new TimelineItem[len]; _tlStart = new long[len]; _tlOrder = new int[len];
            }
            int n = 0;
            long t0 = f.sampleCount > 0 ? f.samples[0].startTicks : 0;
            for (int i = 0; i < f.sampleCount; i++)
            {
                ref readonly var s = ref f.samples[i];
                if (s.startTicks < t0) t0 = s.startTicks;
            }
            for (int i = 0; i < f.sampleCount; i++)
            {
                ref readonly var s = ref f.samples[i];
                var info = UdonProfilerRecorder.GetInfo(s.behaviourId);
                string label;
                if (s.folded || s.path == PathTable.OverflowPath || (uint)s.path >= (uint)pathCount) label = ScriptOf(info) + "." + UdonProfilerRecorder.EventName(s.behaviourId, s.entry);
                else label = _eventLabelOfPath[s.path] ??= ScriptOf(info) + "." + UdonProfilerRecorder.EventName(s.behaviourId, s.entry);
                double itemMs = s.inclusiveTicks * ms;
                _tlItems[n] = new TimelineItem
                {
                    kind = PathKind.Event, path = s.path, behaviourId = s.behaviourId, key = s.entry, scriptId = info?.scriptId ?? -1,
                    depth = s.depth, sample = i, startMs = (s.startTicks - t0) * ms, ms = itemMs, selfMs = itemMs,
                    externMs = s.externTicks * ms, externCount = s.externCount, fusedCount = s.fusedCount, halted = s.halted, label = label,
                    instructions = s.instructions, selfInstructions = s.instructions,
                };
                _tlStart[n] = s.startTicks;
                n++;
            }
            for (int i = 0; i < f.spanCount; i++)
            {
                ref readonly var sp = ref f.spans[i];
                if (sp.ticks <= 0) continue;
                int b = 0, sid = -1; uint key = 0;
                string label;
                if ((uint)sp.path < (uint)pathCount)
                {
                    ref readonly var node = ref paths[sp.path];
                    b = node.behaviourId; key = node.key; sid = node.scriptId;
                    label = _methodLabelOfPath[sp.path] ??= UdonProfilerRecorder.Method(b, key).name ?? "sub_0x" + key.ToString("X");
                }
                else label = "?";
                double itemMs = sp.ticks * ms;
                _tlItems[n] = new TimelineItem
                {
                    kind = PathKind.Method, path = sp.path, behaviourId = b, key = key, scriptId = sid,
                    depth = sp.depth, sample = -1, startMs = (sp.startTicks - t0) * ms, ms = itemMs, selfMs = itemMs, label = label,
                    instructions = sp.instructions, selfInstructions = sp.instructions,
                };
                _tlStart[n] = sp.startTicks;
                n++;
            }

            for (int i = 0; i < n; i++) _tlOrder[i] = i;
            Array.Sort(_tlOrder, 0, n, _sweepOrder);
            int sp0 = 0;
            for (int k = 0; k < n; k++)
            {
                int it = _tlOrder[k];
                while (sp0 > 0 && _tlItems[_tlStack[sp0 - 1]].depth >= _tlItems[it].depth) sp0--;
                if (sp0 > 0)
                {
                    ref var parent = ref _tlItems[_tlStack[sp0 - 1]];
                    parent.selfMs -= _tlItems[it].ms;
                    parent.selfInstructions -= _tlItems[it].instructions;
                }
                if (sp0 == _tlStack.Length) Array.Resize(ref _tlStack, sp0 * 2);
                _tlStack[sp0++] = it;
            }

            Array.Sort(_tlItems, 0, n, _byDepthStart);
            d.items.Clear();
            double total = 0;
            int maxDepth = 0;
            for (int i = 0; i < n; i++)
            {
                ref var it = ref _tlItems[i];
                if (it.selfMs < 0) it.selfMs = 0;
                if (it.selfInstructions < 0) it.selfInstructions = 0;
                d.items.Add(it);
                if (it.startMs + it.ms > total) total = it.startMs + it.ms;
                if (it.depth > maxDepth) maxDepth = it.depth;
                it.label = null;
            }
            d.serial = serial; d.frameNumber = f.frameNumber; d.generation = gen; d.mutation = f.mutation; d.isSealed = f.isSealed;
            d.totalMs = total; d.maxDepth = maxDepth; d.droppedSpans = f.droppedSpans; d.hasInstructions = f.hasInstructions;
            return d;
        }
    }

    public sealed partial class UdonProfilerModel
    {
        int[] _flameKeyOfPath = Filled(1024, -1);
        readonly Dictionary<(int, byte, int, int), int> _flameKeys = new Dictionary<(int, byte, int, int), int>();
        readonly Dictionary<string, int> _flameNames = new Dictionary<string, int>();
        readonly List<string> _flameLabel = new List<string> { "All Udon" };
        readonly List<int> _flameParentKey = new List<int> { -1 };
        readonly List<byte> _flameKind = new List<byte> { 0 };
        readonly List<int> _flameScript = new List<int> { -1 };
        int[] _nodeOfKey = new int[256], _nodeStamp = new int[256];
        int _flameStamp;
        readonly FlameGraph _flame = new FlameGraph();
        readonly List<int> _flameChildren = new List<int>();
        int[] _flameQueue = new int[256];
        Comparison<int> _flameOrder;

        int FlameOrder(int x, int y)
        {
            var nodes = _flame.nodes;
            return nodes[x].size != nodes[y].size ? nodes[y].size.CompareTo(nodes[x].size) : string.CompareOrdinal(nodes[x].label, nodes[y].label);
        }

        int FlameName(string s)
        {
            s ??= "";
            if (_flameNames.TryGetValue(s, out int id)) return id;
            id = _flameNames.Count;
            _flameNames[s] = id;
            return id;
        }

        int FlameKeyOf(int p)
        {
            int k = _flameKeyOfPath[p];
            if (k >= 0) return k;
            var n = UdonProfilerRecorder.Paths[p];
            int parentKey = n.parent < 0 ? 0 : FlameKeyOf(n.parent);
            int nameId, sid = n.scriptId;
            string label;
            if (n.kind == PathKind.Extern)
            {
                nameId = (1 << 24) + (int)n.key;
                sid = -1;
                label = UdonProfilerRecorder.Sig((int)n.key);
            }
            else if (n.kind == PathKind.Event)
            {
                string ev = p == PathTable.OverflowPath ? OverflowName : UdonProfilerRecorder.EventName(n.behaviourId, n.key);
                nameId = FlameName(ev);
                label = p == PathTable.OverflowPath ? OverflowName : (sid >= 0 ? UdonProfilerRecorder.ScriptName(sid) : ScriptOf(UdonProfilerRecorder.GetInfo(n.behaviourId))) + "." + ev;
            }
            else
            {
                label = UdonProfilerRecorder.Method(n.behaviourId, n.key).name ?? "sub_0x" + n.key.ToString("X");
                nameId = FlameName(label);
            }
            var key = (parentKey, (byte)n.kind, sid, nameId);
            if (!_flameKeys.TryGetValue(key, out k))
            {
                k = _flameLabel.Count;
                _flameKeys[key] = k;
                _flameLabel.Add(label);
                _flameParentKey.Add(parentKey);
                _flameKind.Add((byte)n.kind);
                _flameScript.Add(sid);
            }
            _flameKeyOfPath[p] = k;
            return k;
        }

        int NodeOfKey(int k, int p)
        {
            if (k >= _nodeStamp.Length) { int len = Grow(k + 1, _nodeStamp.Length); Array.Resize(ref _nodeStamp, len); Array.Resize(ref _nodeOfKey, len); }
            if (_nodeStamp[k] == _flameStamp) return _nodeOfKey[k];
            int parent = k == 0 ? -1 : NodeOfKey(_flameParentKey[k], -1);
            var g = _flame;
            if (g.count == g.nodes.Length) Array.Resize(ref g.nodes, g.count * 2);
            int idx = g.count++;
            var kind = (PathKind)_flameKind[k];
            g.nodes[idx] = new FlameNode
            {
                parent = parent, firstChild = -1, nextSibling = -1, depth = parent < 0 ? 0 : g.nodes[parent].depth + 1,
                kind = k == 0 ? NodeKind.Group : kind == PathKind.Event ? NodeKind.Event : kind == PathKind.Method ? NodeKind.Method : NodeKind.Extern,
                label = _flameLabel[k], scriptId = _flameScript[k],
            };
            if (p >= 0)
            {
                ref readonly var pn = ref UdonProfilerRecorder.Paths[p];
                g.nodes[idx].behaviourId = pn.behaviourId;
                g.nodes[idx].key = pn.key;
                if (pn.kind == PathKind.Extern) g.nodes[idx].sig = _flameLabel[k];
            }
            if (parent >= 0) { g.nodes[idx].nextSibling = g.nodes[parent].firstChild; g.nodes[parent].firstChild = idx; }
            _nodeStamp[k] = _flameStamp;
            _nodeOfKey[k] = idx;
            return idx;
        }

        bool _flameByInstr;

        /// <summary>Refills the model-owned flame graph from the last build's range and filter; returns the same instance every call.</summary>
        /// <param name="byInstructions">Size blocks by Udon instructions instead of ms; ignored when the range has no instruction counts.</param>
        public FlameGraph BuildFlame(bool byInstructions = false)
        {
            var g = _flame;
            if (_flameValid && _flameByInstr == byInstructions && _gen == UdonProfilerRecorder.Generation) return g;
            _flameValid = true;
            _flameByInstr = byInstructions;
            _flameStamp++;
            g.count = 0; g.maxDepth = 0; g.totalMs = 0; g.totalInstructions = 0;
            g.hasInstructions = _view.hasInstructions;
            g.byInstructions = byInstructions && g.hasInstructions;
            int frames = Math.Max(1, _view.frameCount);
            g.frames = _view.frameCount;
            NodeOfKey(0, -1);
            g.nodes[0].label = "All Udon";
            if (_gen != UdonProfilerRecorder.Generation) return g;
            EnsurePathArrays();
            double ms = UdonProfilerRecorder.MsPerTick;
            var paths = UdonProfilerRecorder.Paths;
            foreach (int p in _touched)
            {
                ref readonly var a = ref _acc[p];
                if (IsZero(in a) || a.mark != _accStamp) continue;
                int idx = NodeOfKey(FlameKeyOf(p), p);
                ref var fn = ref g.nodes[idx];
                if (fn.behaviourId == 0 && fn.key == 0)
                {
                    fn.behaviourId = paths[p].behaviourId;
                    fn.key = paths[p].key;
                    if (paths[p].kind == PathKind.Extern) fn.sig = fn.label;
                }
                if (paths[p].kind == PathKind.Extern) { fn.totalMs += a.incl * ms; fn.selfMs = fn.totalMs; fn.fused += a.fused; fn.calls += a.calls; }
                else { fn.totalMs += a.incl * ms; fn.selfMs += a.self * ms; fn.calls += a.calls; fn.instructions += a.instr; fn.selfInstructions += a.selfInstr; }
            }
            double root = 0, rootInstr = 0;
            bool byInstr = g.byInstructions;
            for (int i = 1; i < g.count; i++)
            {
                ref var fn = ref g.nodes[i];
                fn.totalMs /= frames; fn.selfMs /= frames; fn.calls /= frames;
                fn.instructions /= frames; fn.selfInstructions /= frames;
                fn.size = byInstr ? fn.instructions : fn.totalMs;
                if (fn.depth == 1) { root += fn.totalMs; rootInstr += fn.instructions; }
                if (fn.depth > g.maxDepth) g.maxDepth = fn.depth;
            }
            g.nodes[0].totalMs = root;
            g.nodes[0].selfMs = 0;
            g.nodes[0].instructions = rootInstr;
            g.nodes[0].selfInstructions = 0;
            g.nodes[0].size = byInstr ? rootInstr : root;
            g.totalMs = root;
            g.totalInstructions = rootInstr;

            if (_flameQueue.Length < g.count) _flameQueue = new int[Grow(g.count, _flameQueue.Length)];
            int head = 0, tail = 0;
            _flameQueue[tail++] = 0;
            g.nodes[0].startMs = 0;
            g.nodes[0].start = 0;
            while (head < tail)
            {
                int n = _flameQueue[head++];
                _flameChildren.Clear();
                for (int c = g.nodes[n].firstChild; c >= 0; c = g.nodes[c].nextSibling) _flameChildren.Add(c);
                if (_flameChildren.Count == 0) continue;
                var nodes = g.nodes;
                _flameChildren.Sort(_flameOrder ??= FlameOrder);
                double start = nodes[n].startMs, at = nodes[n].start;
                nodes[n].firstChild = _flameChildren[0];
                for (int i = 0; i < _flameChildren.Count; i++)
                {
                    int c = _flameChildren[i];
                    nodes[c].startMs = start;
                    nodes[c].start = at;
                    start += nodes[c].totalMs;
                    at += nodes[c].size;
                    nodes[c].nextSibling = i + 1 < _flameChildren.Count ? _flameChildren[i + 1] : -1;
                    _flameQueue[tail++] = c;
                }
            }
            return g;
        }
    }
}
