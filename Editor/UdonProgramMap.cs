using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UdonSharp;
using UdonSharp.Compiler;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.VM.Common;

namespace MagmaVRC.UdonProfiler
{
    /// <summary>Static call/return classification and method naming for one Udon program.</summary>
    public sealed class UdonProgramMap
    {
        public const byte CallFlag = 1;
        public const byte TailFlag = 2;
        public const byte ReturnFlag = 4;
        public const byte HeapCallFlag = 8;
        public const byte UnverifiedFlag = 0x80;

        const uint EndAddress = 0xFFFFFFFCu;
        const string SwitchSig = "SystemUInt32Array.__Get__SystemInt32__SystemUInt32";

        public readonly byte[] Flags;
        public readonly bool Simulated;
        public readonly bool QuicOptimized;
        public string NamingSource { get; private set; }
        public bool NamesResolved { get; private set; }
        public IReadOnlyCollection<uint> MethodEntries => _analysis.entries;

        internal int[] SiteIndex;
        internal int SiteCount;
        internal uint[] HeapSlots;

        internal static readonly object CacheLock = new object();
        internal static bool KeepStacks;

        readonly MapAnalysis _analysis;
        readonly Dictionary<uint, string> _exports = new Dictionary<uint, string>();
        readonly AbstractUdonProgramSource _programSource;
        readonly bool _hasProgramSource;
        readonly int _codeLength;
        readonly Dictionary<uint, SourceRef> _methodCache = new Dictionary<uint, SourceRef>();
        MapRange[] _ranges;

        struct MapRange
        {
            public uint start, end;
            public SourceRef source;
        }

        sealed class MapAssetEntry
        {
            public int byteLength;
            public UdonProgramMap map;
        }

        static ConditionalWeakTable<IUdonProgram, UdonProgramMap> _byProgram = new ConditionalWeakTable<IUdonProgram, UdonProgramMap>();
        static ConditionalWeakTable<AbstractSerializedUdonProgramAsset, MapAssetEntry> _byAsset = new ConditionalWeakTable<AbstractSerializedUdonProgramAsset, MapAssetEntry>();
        static readonly Dictionary<ulong, MapAnalysis> _byDigest = new Dictionary<ulong, MapAnalysis>();

        UdonProgramMap(MapAnalysis analysis, IUdonProgram program, AbstractUdonProgramSource programSource)
        {
            _analysis = analysis;
            Flags = analysis.flags;
            Simulated = analysis.simulated;
            QuicOptimized = analysis.quicOptimized;
            SiteIndex = analysis.siteIndex;
            SiteCount = analysis.siteCount;
            HeapSlots = analysis.heapSlots;
            _programSource = programSource;
            _hasProgramSource = !ReferenceEquals(programSource, null);
            _codeLength = analysis.codeLength;
            NamingSource = "none";
            try
            {
                var table = program.EntryPoints;
                if (table != null)
                {
                    foreach (var name in table.GetExportedSymbols())
                    {
                        if (!table.TryGetAddressFromSymbol(name, out uint address)) continue;
                        if (!_exports.ContainsKey(address)) _exports[address] = name;
                    }
                }
            }
            catch (Exception) { }
        }

        public bool IsMethodEntry(uint address) => _analysis.entries.Contains(address);
        public bool IsResumeTarget(uint address) => _analysis.resumeTargets.Contains(address);
        public uint BodyLabel(uint export) => _analysis.bodyLabels.TryGetValue(export, out uint label) ? label : export;

        internal int StackSecondAt(uint address)
        {
            var second = _analysis.stackSecond;
            uint w = address >> 2;
            return second != null && w < (uint)second.Length ? second[w] : -2;
        }

        public SourceRef Method(uint target)
        {
            if (_methodCache.TryGetValue(target, out var r)) return r;
            if (!NamesResolved) ResolveNames();
            if (!RangeAt(target, out r))
            {
                if (_exports.TryGetValue(target, out var name) || (target >= 8 && _exports.TryGetValue(target - 8, out name))) r = new SourceRef { name = name };
                else if (IsResumeTarget(target) || (!_analysis.allTargets.Contains(target) && !_analysis.entries.Contains(target))) r = new SourceRef { name = "resume_0x" + target.ToString("X") };
                else r = new SourceRef { name = "sub_0x" + target.ToString("X") };
            }
            _methodCache[target] = r;
            return r;
        }

        public SourceRef At(uint address)
        {
            if (!NamesResolved) ResolveNames();
            return RangeAt(address, out var r) ? r : default;
        }

        bool RangeAt(uint address, out SourceRef source)
        {
            source = default;
            var ranges = _ranges;
            if (ranges == null || ranges.Length == 0) return false;
            int lo = 0, hi = ranges.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (ranges[mid].start <= address)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            for (int i = found; i >= 0; i--)
            {
                if (address < ranges[i].end)
                {
                    source = ranges[i].source;
                    return true;
                }
                if (found - i >= 8) break;
            }
            return false;
        }

        public void ResolveNames()
        {
            if (NamesResolved) return;
            NamesResolved = true;
            try
            {
                var asset = _programSource as UdonSharpProgramAsset;
                if (asset != null)
                {
                    var methods = DebugInfoOf(asset);
                    if (methods != null && methods.Count > 0)
                    {
                        uint maxEnd = 0;
                        foreach (var m in methods) if (m != null && m.methodEndAddress > maxEnd) maxEnd = m.methodEndAddress;
                        if (maxEnd == (uint)_codeLength)
                        {
                            var list = new List<MapRange>(methods.Count);
                            foreach (var m in methods)
                            {
                                if (m == null) continue;
                                int line = 0;
                                if (m.debugMarkers != null && m.debugMarkers.Count > 0)
                                {
                                    int min = int.MaxValue;
                                    foreach (var marker in m.debugMarkers) if (marker.startLine < min) min = marker.startLine;
                                    line = min + 1;
                                }
                                list.Add(new MapRange
                                {
                                    start = m.methodStartAddress,
                                    end = m.methodEndAddress,
                                    source = new SourceRef { name = m.methodName, file = string.IsNullOrEmpty(m.containingFilePath) ? null : m.containingFilePath, line = line },
                                });
                            }
                            list.Sort((a, b) => a.start.CompareTo(b.start));
                            _ranges = list.ToArray();
                        }
                    }
                }
            }
            catch (Exception) { _ranges = null; }
            NamingSource = _ranges != null ? "debug info" : _exports.Count > 0 ? "exports" : "none";
        }

        static Type _debugCacheType;
        static FieldInfo _methodInfosField;
        static bool _debugReflected;

        static List<MethodDebugInfo> DebugInfoOf(UdonSharpProgramAsset asset)
        {
            const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (!_debugReflected)
            {
                _debugReflected = true;
                _debugCacheType = typeof(UdonSharpProgramAsset).Assembly.GetType("UdonSharp.UdonSharpEditorCache");
                _methodInfosField = typeof(AssemblyDebugInfo).GetField("_methodDebugInfos", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            var cacheType = _debugCacheType;
            if (cacheType == null || _methodInfosField == null) return null;
            object instance = cacheType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            var get = cacheType.GetMethod("GetDebugInfo", Any);
            var kinds = cacheType.GetNestedType("DebugInfoType", BindingFlags.Public | BindingFlags.NonPublic);
            if (instance == null || get == null) return null;
            object editorKind = kinds != null ? Enum.Parse(kinds, "Editor") : null;
            object last = cacheType.GetProperty("LastBuildType", Any)?.GetValue(instance) ?? editorKind;
            var info = last != null ? get.Invoke(instance, new[] { asset, last }) as AssemblyDebugInfo : null;
            if (info == null && editorKind != null && !Equals(last, editorKind)) info = get.Invoke(instance, new[] { asset, editorKind }) as AssemblyDebugInfo;
            return info != null ? _methodInfosField.GetValue(info) as List<MethodDebugInfo> : null;
        }

        public static UdonProgramMap For(UdonBehaviour ub, IUdonProgram program, AbstractSerializedUdonProgramAsset serialized)
        {
            if (program == null) return null;
            var code = program.ByteCode;
            if (code == null) return null;
            var source = ub != null ? ub.programSource : null;
            lock (CacheLock)
            {
                if (_byProgram.TryGetValue(program, out var known) && (known._hasProgramSource || ub == null || ReferenceEquals(source, null))) return known;
                if (!ReferenceEquals(serialized, null) && _byAsset.TryGetValue(serialized, out var entry) && entry.map != null && entry.byteLength == code.Length)
                {
                    Store(program, entry.map);
                    return entry.map;
                }
                var analysis = AnalysisFor(program, known);
                if (analysis == null) return null;
                var map = new UdonProgramMap(analysis, program, source);
                if (!ReferenceEquals(serialized, null))
                {
                    _byAsset.Remove(serialized);
                    _byAsset.Add(serialized, new MapAssetEntry { byteLength = code.Length, map = map });
                }
                Store(program, map);
                return map;
            }
        }

        public static UdonProgramMap For(IUdonProgram program)
        {
            if (program == null || program.ByteCode == null) return null;
            lock (CacheLock)
            {
                if (_byProgram.TryGetValue(program, out var known)) return known;
                var analysis = AnalysisFor(program, null);
                if (analysis == null) return null;
                var map = new UdonProgramMap(analysis, program, null);
                Store(program, map);
                return map;
            }
        }

        public static UdonProgramMap Build(IUdonProgram program, Func<string, int> externArity)
        {
            if (program == null || program.ByteCode == null) return null;
            lock (CacheLock)
            {
                if (!Decode(program.ByteCode, out var ins, out var insOfWord)) return null;
                var analysis = Analyze(program, externArity ?? UdonProfilerRecorder.ExternArity, ins, insOfWord, new MapHeap(program));
                return new UdonProgramMap(analysis, program, null);
            }
        }

        public static void ClearCache()
        {
            lock (CacheLock)
            {
                _byProgram = new ConditionalWeakTable<IUdonProgram, UdonProgramMap>();
                _byAsset = new ConditionalWeakTable<AbstractSerializedUdonProgramAsset, MapAssetEntry>();
                _byDigest.Clear();
            }
        }

        static void Store(IUdonProgram program, UdonProgramMap map)
        {
            _byProgram.Remove(program);
            _byProgram.Add(program, map);
        }

        static MapAnalysis AnalysisFor(IUdonProgram program, UdonProgramMap known)
        {
            if (known != null && known._codeLength == program.ByteCode.Length) return known._analysis;
            if (!Decode(program.ByteCode, out var ins, out var insOfWord)) return null;
            var heap = new MapHeap(program);
            ulong digest = Digest(program, ins, heap);
            if (_byDigest.TryGetValue(digest, out var analysis)) return analysis;
            analysis = Analyze(program, UdonProfilerRecorder.ExternArity, ins, insOfWord, heap);
            _byDigest[digest] = analysis;
            return analysis;
        }

        struct Ins
        {
            public uint addr;
            public OpCode op;
            public uint operand;
        }

        static bool Decode(byte[] code, out Ins[] ins, out int[] insOfWord)
        {
            int words = code.Length / 4;
            insOfWord = new int[words];
            var list = new List<Ins>(words);
            uint i = 0;
            while (i + 4 <= (uint)code.Length)
            {
                var op = (OpCode)ReadUInt(code, i);
                switch (op)
                {
                    case OpCode.NOP:
                    case OpCode.POP:
                    case OpCode.COPY:
                        insOfWord[i / 4] = list.Count;
                        list.Add(new Ins { addr = i, op = op });
                        i += 4;
                        break;
                    case OpCode.PUSH:
                    case OpCode.JUMP_IF_FALSE:
                    case OpCode.JUMP:
                    case OpCode.EXTERN:
                    case OpCode.ANNOTATION:
                    case OpCode.JUMP_INDIRECT:
                        if (i + 8 > (uint)code.Length)
                        {
                            ins = null;
                            return false;
                        }
                        insOfWord[i / 4] = list.Count;
                        insOfWord[i / 4 + 1] = -1;
                        list.Add(new Ins { addr = i, op = op, operand = ReadUInt(code, i + 4) });
                        i += 8;
                        break;
                    default:
                        ins = null;
                        return false;
                }
            }
            ins = list.ToArray();
            return true;
        }

        static uint ReadUInt(byte[] b, uint i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);

        sealed class MapHeap
        {
            struct SlotInfo
            {
                public bool readable, isUInt, exported;
                public uint value;
            }

            static readonly Dictionary<Type, FieldInfo> SigFields = new Dictionary<Type, FieldInfo>();

            readonly IUdonHeap _heap;
            readonly IUdonSymbolTable _symbols;
            readonly uint _capacity;
            readonly Dictionary<uint, SlotInfo> _slots = new Dictionary<uint, SlotInfo>();
            readonly Dictionary<uint, string> _sigs = new Dictionary<uint, string>();
            readonly Dictionary<uint, uint[]> _arrays = new Dictionary<uint, uint[]>();

            public MapHeap(IUdonProgram program)
            {
                try
                {
                    _heap = program.Heap;
                    _symbols = program.SymbolTable;
                    _capacity = _heap != null ? _heap.GetHeapCapacity() : 0;
                }
                catch (Exception)
                {
                    _heap = null;
                    _capacity = 0;
                }
            }

            public bool Readable(uint slot)
            {
                try { return _heap != null && slot < _capacity && _heap.IsHeapVariableInitialized(slot); }
                catch (Exception) { return false; }
            }

            SlotInfo Info(uint slot)
            {
                if (_slots.TryGetValue(slot, out var s)) return s;
                s = default;
                try
                {
                    if (Readable(slot))
                    {
                        s.readable = true;
                        if (_heap.GetHeapVariableType(slot) == typeof(uint))
                        {
                            s.isUInt = true;
                            s.value = _heap.GetHeapVariable<uint>(slot);
                            s.exported = _symbols != null && _symbols.TryGetSymbolFromAddress(slot, out string name) && _symbols.HasExportedSymbol(name);
                        }
                    }
                }
                catch (Exception) { s = default; }
                _slots[slot] = s;
                return s;
            }

            public bool AddressConst(uint slot, out uint value)
            {
                var s = Info(slot);
                value = s.value;
                return s.isUInt && !s.exported;
            }

            public bool UIntInfo(uint slot, out bool readable, out uint value, out bool exported)
            {
                var s = Info(slot);
                readable = s.readable;
                value = s.value;
                exported = s.exported;
                return s.isUInt;
            }

            public string Sig(uint slot)
            {
                if (_sigs.TryGetValue(slot, out var sig)) return sig;
                sig = null;
                try
                {
                    if (Readable(slot))
                    {
                        if (_heap.TryGetHeapVariable(slot, out string s)) sig = s;
                        else
                        {
                            object o = _heap.GetHeapVariable(slot);
                            if (o != null)
                            {
                                var t = o.GetType();
                                if (!SigFields.TryGetValue(t, out var f))
                                {
                                    f = t.Name == "CachedUdonExternDelegate" ? t.GetField("externSignature", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                                    SigFields[t] = f;
                                }
                                if (f != null) sig = f.GetValue(o) as string;
                            }
                        }
                    }
                }
                catch (Exception) { sig = null; }
                _sigs[slot] = sig;
                return sig;
            }

            public uint[] UIntArray(uint slot)
            {
                if (_arrays.TryGetValue(slot, out var a)) return a;
                a = null;
                try
                {
                    if (Readable(slot)) a = _heap.GetHeapVariable(slot) as uint[];
                }
                catch (Exception) { a = null; }
                _arrays[slot] = a;
                return a;
            }
        }

        static ulong Digest(IUdonProgram program, Ins[] ins, MapHeap heap)
        {
            const uint Unreadable = 0xA5A5A5A5u;
            ulong h = 14695981039346656037UL;
            void B(uint v)
            {
                for (int k = 0; k < 4; k++)
                {
                    h ^= (byte)(v >> (k * 8));
                    h *= 1099511628211UL;
                }
            }
            var code = program.ByteCode;
            for (int i = 0; i < code.Length; i++)
            {
                h ^= code[i];
                h *= 1099511628211UL;
            }
            foreach (var x in ins)
            {
                if (x.op == OpCode.PUSH)
                {
                    if (heap.UIntInfo(x.operand, out bool readable, out uint v, out bool exported))
                    {
                        B(x.operand);
                        B(v);
                        B(exported ? 1u : 0u);
                    }
                    else if (!readable) B(Unreadable);
                    else
                    {
                        var arr = heap.UIntArray(x.operand);
                        if (arr != null)
                        {
                            B(x.operand);
                            B((uint)arr.Length);
                            foreach (uint a in arr) B(a);
                        }
                    }
                }
                else if (x.op == OpCode.EXTERN)
                {
                    var sig = heap.Sig(x.operand);
                    if (sig == null) B(Unreadable);
                    else
                    {
                        B((uint)sig.Length);
                        foreach (char c in sig) B(c);
                    }
                }
            }
            foreach (uint e in ExportAddresses(program)) B(e);
            return h;
        }

        static List<uint> ExportAddresses(IUdonProgram program)
        {
            var list = new List<uint>();
            try
            {
                var table = program.EntryPoints;
                if (table != null)
                {
                    foreach (var name in table.GetExportedSymbols())
                        if (table.TryGetAddressFromSymbol(name, out uint a) && !list.Contains(a)) list.Add(a);
                }
            }
            catch (Exception) { }
            list.Sort();
            return list;
        }

        sealed class MapAnalysis
        {
            public byte[] flags;
            public bool simulated, quicOptimized;
            public int codeLength;
            public HashSet<uint> entries = new HashSet<uint>();
            public HashSet<uint> resumeTargets = new HashSet<uint>();
            public HashSet<uint> allTargets = new HashSet<uint>();
            public Dictionary<uint, uint> bodyLabels = new Dictionary<uint, uint>();
            public int[] siteIndex;
            public int siteCount;
            public uint[] heapSlots;
            public int[] stackSecond;
        }

        const int InitialPool = 4096;
        const int MaxKeptPool = 1 << 20;
        static int[] _nodeSlot = new int[InitialPool], _nodeParent = new int[InitialPool], _nodeDepth = new int[InitialPool];
        static int _nodeCount;
        static Dictionary<long, int> _intern = new Dictionary<long, int>();
        static readonly List<int> _mergeA = new List<int>(), _mergeB = new List<int>();

        static void ResetPools()
        {
            _intern.Clear();
            _nodeCount = 1;
            _nodeSlot[0] = -1;
            _nodeParent[0] = -1;
            _nodeDepth[0] = 0;
        }

        static void TrimPools()
        {
            if (_nodeSlot.Length <= MaxKeptPool) return;
            _nodeSlot = new int[InitialPool];
            _nodeParent = new int[InitialPool];
            _nodeDepth = new int[InitialPool];
            _intern = new Dictionary<long, int>();
        }

        static int SPush(int slot, int s)
        {
            long key = ((long)slot << 32) | (uint)s;
            if (_intern.TryGetValue(key, out int id)) return id;
            id = _nodeCount++;
            if (id == _nodeSlot.Length)
            {
                Array.Resize(ref _nodeSlot, id * 2);
                Array.Resize(ref _nodeParent, id * 2);
                Array.Resize(ref _nodeDepth, id * 2);
            }
            _nodeSlot[id] = slot;
            _nodeParent[id] = s;
            _nodeDepth[id] = _nodeDepth[s] + 1;
            _intern[key] = id;
            return id;
        }

        static int SPop(int s) => _nodeParent[s];
        static int STop(int s) => _nodeSlot[s];
        static int SDepth(int s) => _nodeDepth[s];

        static int SNth(int s, int k)
        {
            for (int i = 0; i < k && s > 0; i++) s = _nodeParent[s];
            return s > 0 ? _nodeSlot[s] : -1;
        }

        static int SMerge(int a, int b)
        {
            _mergeA.Clear();
            _mergeB.Clear();
            while (a != b)
            {
                _mergeA.Add(_nodeSlot[a]);
                _mergeB.Add(_nodeSlot[b]);
                a = _nodeParent[a];
                b = _nodeParent[b];
            }
            int c = a;
            for (int i = _mergeA.Count - 1; i >= 0; i--) c = SPush(_mergeA[i] == _mergeB[i] ? _mergeA[i] : -1, c);
            return c;
        }

        enum JumpKind : byte { None, Terminal, Call, HeapCall, Plain, Return, Switch, IndirectCall, ReturnCandidate }

        sealed class Sim
        {
            public Ins[] ins;
            public int[] insOfWord;
            public int codeLength;
            public MapHeap heap;
            public Func<string, int> arity;
            public Dictionary<string, int> arityCache = new Dictionary<string, int>();
            public int[] stackIn;
            public JumpKind[] kind;
            public uint[] heapSlotOf;
            public bool[] queued;
            public readonly Stack<int> work = new Stack<int>();
            public bool fail;
            public readonly HashSet<uint> calls = new HashSet<uint>(), heapCalls = new HashSet<uint>(), indirectCalls = new HashSet<uint>();
            public readonly HashSet<uint> entries = new HashSet<uint>(), resumeTargets = new HashSet<uint>(), dispatchTargets = new HashSet<uint>();
            public readonly List<int> pending = new List<int>();
            public readonly HashSet<int> pendingSet = new HashSet<int>();

            public int IndexOf(uint addr)
            {
                if (addr >= (uint)codeLength || (addr & 3) != 0) return -1;
                return insOfWord[addr >> 2];
            }

            public bool IsInsAddr(uint addr) => IndexOf(addr) >= 0;

            public void Flow(int i, int s)
            {
                if (fail || i < 0 || i >= ins.Length) return;
                int cur = stackIn[i];
                if (cur < 0)
                {
                    stackIn[i] = s;
                    Enqueue(i);
                    return;
                }
                if (cur == s) return;
                if (SDepth(cur) != SDepth(s))
                {
                    fail = true;
                    return;
                }
                int m = SMerge(cur, s);
                if (m != cur)
                {
                    stackIn[i] = m;
                    Enqueue(i);
                }
            }

            public void FlowAddr(uint addr, int s)
            {
                if (addr >= EndAddress || addr >= (uint)codeLength) return;
                int i = IndexOf(addr);
                if (i < 0)
                {
                    fail = true;
                    return;
                }
                Flow(i, s);
            }

            void Enqueue(int i)
            {
                if (queued[i]) return;
                queued[i] = true;
                work.Push(i);
            }

            public void Drain()
            {
                while (!fail && work.Count > 0)
                {
                    int i = work.Pop();
                    queued[i] = false;
                    Step(i);
                }
            }

            int Arity(string sig)
            {
                if (sig == null) return -1;
                if (arityCache.TryGetValue(sig, out int n)) return n;
                try { n = arity(sig); }
                catch (Exception) { n = -1; }
                arityCache[sig] = n;
                return n;
            }

            void AddPending(int i)
            {
                if (pendingSet.Add(i)) pending.Add(i);
            }

            void Step(int i)
            {
                int s = stackIn[i];
                ref readonly var x = ref ins[i];
                switch (x.op)
                {
                    case OpCode.NOP:
                    case OpCode.ANNOTATION:
                        Flow(i + 1, s);
                        break;
                    case OpCode.PUSH:
                        Flow(i + 1, SPush((int)x.operand, s));
                        break;
                    case OpCode.POP:
                        if (SDepth(s) < 1) { fail = true; return; }
                        Flow(i + 1, SPop(s));
                        break;
                    case OpCode.COPY:
                        if (SDepth(s) < 2) { fail = true; return; }
                        Flow(i + 1, SPop(SPop(s)));
                        break;
                    case OpCode.JUMP_IF_FALSE:
                    {
                        if (SDepth(s) < 1) { fail = true; return; }
                        int after = SPop(s);
                        if (x.operand < EndAddress) FlowAddr(x.operand, after);
                        Flow(i + 1, after);
                        break;
                    }
                    case OpCode.EXTERN:
                    {
                        int n = Arity(heap.Sig(x.operand));
                        if (n < 0 || n > SDepth(s)) { fail = true; return; }
                        int after = s;
                        for (int k = 0; k < n; k++) after = SPop(after);
                        Flow(i + 1, after);
                        break;
                    }
                    case OpCode.JUMP:
                        StepJump(i, s);
                        break;
                    case OpCode.JUMP_INDIRECT:
                        StepIndirect(i, s);
                        break;
                    default:
                        fail = true;
                        break;
                }
            }

            void StepJump(int i, int s)
            {
                uint a = ins[i].addr, t = ins[i].operand;
                if (kind[i] == JumpKind.None) kind[i] = ClassifyJump(i, s, a, t);
                switch (kind[i])
                {
                    case JumpKind.Call:
                        if (SDepth(s) < 1) { fail = true; return; }
                        calls.Add(a);
                        entries.Add(t);
                        FlowAddr(t, SPush(-1, 0));
                        Flow(i + 1, SPop(s));
                        break;
                    case JumpKind.HeapCall:
                        heapCalls.Add(a);
                        entries.Add(t);
                        FlowAddr(t, 0);
                        Flow(i + 1, s);
                        break;
                    case JumpKind.Plain:
                        FlowAddr(t, s);
                        break;
                }
            }

            JumpKind ClassifyJump(int i, int s, uint a, uint t)
            {
                if (t >= EndAddress || t >= (uint)codeLength) return JumpKind.Terminal;
                if (SDepth(s) > 0)
                {
                    int top = STop(s);
                    if (top >= 0 && heap.AddressConst((uint)top, out uint v) && v == a + 8) return JumpKind.Call;
                }
                int p = i - 1;
                if (p >= 0 && ins[p].op == OpCode.COPY && ins[p].addr == a - 4 && stackIn[p] >= 0)
                {
                    int copyIn = stackIn[p];
                    if (SDepth(copyIn) == SDepth(s) + 2)
                    {
                        int src = SNth(copyIn, 1);
                        int dest = STop(copyIn);
                        if (src >= 0 && dest >= 0 && heap.AddressConst((uint)src, out uint v) && v == a + 8)
                        {
                            heapSlotOf[a >> 2] = (uint)dest;
                            return JumpKind.HeapCall;
                        }
                    }
                }
                return JumpKind.Plain;
            }

            void StepIndirect(int i, int s)
            {
                uint a = ins[i].addr, x = ins[i].operand;
                if (kind[i] == JumpKind.None) kind[i] = ClassifyIndirect(i, a, x);
                switch (kind[i])
                {
                    case JumpKind.Return:
                    {
                        int copyIn = stackIn[i - 1];
                        if (copyIn < 0 || SDepth(copyIn) < 2) return;
                        int src = SNth(copyIn, 1);
                        if (src >= 0 && heap.AddressConst((uint)src, out uint v) && v < EndAddress && IsInsAddr(v))
                        {
                            dispatchTargets.Add(v);
                            FlowAddr(v, SPop(SPop(copyIn)));
                        }
                        break;
                    }
                    case JumpKind.Switch:
                    {
                        int externIn = stackIn[i - 1];
                        if (externIn < 0 || SDepth(externIn) < 3) return;
                        int table = SNth(externIn, 2);
                        if (table < 0) return;
                        var values = heap.UIntArray((uint)table);
                        if (values == null) return;
                        foreach (uint v in values)
                            if (v < EndAddress && IsInsAddr(v)) FlowAddr(v, s);
                        break;
                    }
                    case JumpKind.IndirectCall:
                        if (SDepth(s) < 1) { fail = true; return; }
                        indirectCalls.Add(a);
                        AddPending(i);
                        Flow(i + 1, SPop(s));
                        break;
                    case JumpKind.ReturnCandidate:
                        AddPending(i);
                        break;
                }
            }

            JumpKind ClassifyIndirect(int i, uint a, uint x)
            {
                if (i >= 2 && ins[i - 2].op == OpCode.PUSH && ins[i - 2].operand == x && ins[i - 1].op == OpCode.COPY) return JumpKind.Return;
                if (i >= 1 && ins[i - 1].op == OpCode.EXTERN && heap.Sig(ins[i - 1].operand) == SwitchSig) return JumpKind.Switch;
                if (i >= 1 && ins[i - 1].op == OpCode.PUSH && heap.AddressConst(ins[i - 1].operand, out uint v) && v == a + 8) return JumpKind.IndirectCall;
                return JumpKind.ReturnCandidate;
            }

            public void SeedDispatch(List<uint> exports)
            {
                foreach (uint e in exports)
                {
                    int i = IndexOf(e);
                    if (i < 0 || ins[i].op != OpCode.PUSH) continue;
                    if (!heap.AddressConst(ins[i].operand, out uint v) || v >= EndAddress || v == e) continue;
                    int vi = IndexOf(v);
                    if (vi < 0 || stackIn[vi] >= 0) continue;
                    dispatchTargets.Add(v);
                    Flow(vi, 0);
                }
                Drain();
            }

            public void ResolveIndirect()
            {
                var heapContinuations = new HashSet<uint>();
                foreach (uint a in heapCalls) heapContinuations.Add(a + 8);
                var targetsBySlot = new Dictionary<int, List<uint>>();
                for (int round = 0; round < 16 && !fail; round++)
                {
                    targetsBySlot.Clear();
                    for (int c = 0; c < ins.Length; c++)
                    {
                        if (ins[c].op != OpCode.COPY) continue;
                        int cin = stackIn[c];
                        if (cin < 0 || SDepth(cin) < 2) continue;
                        int dest = STop(cin), src = SNth(cin, 1);
                        if (dest < 0 || src < 0 || !heap.AddressConst((uint)src, out uint v) || !IsInsAddr(v)) continue;
                        if (!targetsBySlot.TryGetValue(dest, out var list)) targetsBySlot[dest] = list = new List<uint>();
                        if (!list.Contains(v)) list.Add(v);
                    }
                    bool any = false;
                    for (int p = 0; p < pending.Count; p++)
                    {
                        int j = pending[p];
                        if (!targetsBySlot.TryGetValue((int)ins[j].operand, out var targets)) continue;
                        bool indirectCall = kind[j] == JumpKind.IndirectCall;
                        foreach (uint v in targets)
                        {
                            if (indirectCall) resumeTargets.Add(v);
                            else if (heapContinuations.Contains(v)) continue;
                            int vi = IndexOf(v);
                            if (vi < 0 || stackIn[vi] >= 0) continue;
                            Flow(vi, indirectCall ? SPush(-1, 0) : stackIn[j]);
                            any = true;
                        }
                    }
                    Drain();
                    if (!any) break;
                }
            }
        }

        static MapAnalysis Analyze(IUdonProgram program, Func<string, int> arity, Ins[] ins, int[] insOfWord, MapHeap heap)
        {
            var code = program.ByteCode;
            int words = code.Length / 4;
            var a = new MapAnalysis { codeLength = code.Length, flags = new byte[words] };
            try { a.quicOptimized = program.SymbolTable != null && program.SymbolTable.TryGetAddressFromSymbol("__const_quic_optimized", out _); }
            catch (Exception) { a.quicOptimized = false; }
            var exports = ExportAddresses(program);
            var heapSlots = new uint[words];
            bool anyHeapSlot = false;

            ResetPools();
            var sim = new Sim
            {
                ins = ins,
                insOfWord = insOfWord,
                codeLength = code.Length,
                heap = heap,
                arity = arity,
                stackIn = new int[ins.Length],
                kind = new JumpKind[ins.Length],
                heapSlotOf = heapSlots,
                queued = new bool[ins.Length],
            };
            try
            {
                for (int i = 0; i < ins.Length; i++) sim.stackIn[i] = -1;
                foreach (uint e in exports) sim.FlowAddr(e, 0);
                sim.Drain();
                if (!sim.fail) sim.SeedDispatch(exports);
                if (!sim.fail) sim.ResolveIndirect();
            }
            catch (Exception) { sim.fail = true; }

            a.simulated = !sim.fail;
            foreach (uint e in exports)
            {
                a.entries.Add(e);
                int i = sim.IndexOf(e);
                if (i >= 0 && ins[i].op == OpCode.PUSH && heap.AddressConst(ins[i].operand, out uint v) && v >= EndAddress) a.entries.Add(e + 8);
            }

            bool[] reached = new bool[ins.Length];
            if (a.simulated)
            {
                for (int i = 0; i < ins.Length; i++) reached[i] = sim.stackIn[i] >= 0;
                if (KeepStacks)
                {
                    a.stackSecond = new int[words];
                    for (int w = 0; w < words; w++) a.stackSecond[w] = -2;
                    for (int i = 0; i < ins.Length; i++)
                        if (reached[i] && SDepth(sim.stackIn[i]) >= 2) a.stackSecond[ins[i].addr >> 2] = SNth(sim.stackIn[i], 1);
                }
                a.entries.UnionWith(sim.entries);
                a.resumeTargets.UnionWith(sim.resumeTargets);
                foreach (uint c in sim.calls) a.flags[c >> 2] |= CallFlag;
                foreach (uint c in sim.heapCalls)
                {
                    a.flags[c >> 2] |= HeapCallFlag;
                    anyHeapSlot = true;
                }
                foreach (uint c in sim.indirectCalls) a.flags[c >> 2] |= CallFlag;
                for (int i = 0; i < ins.Length; i++)
                {
                    if (!reached[i]) continue;
                    var k = sim.kind[i];
                    if (k == JumpKind.Return || k == JumpKind.ReturnCandidate) a.flags[ins[i].addr >> 2] |= ReturnFlag;
                }
                var sorted = new List<uint>(a.entries);
                sorted.Sort();
                for (int i = 0; i < ins.Length; i++)
                {
                    if (!reached[i]) continue;
                    bool candidate = ins[i].op == OpCode.JUMP_IF_FALSE || (ins[i].op == OpCode.JUMP && sim.kind[i] == JumpKind.Plain);
                    if (!candidate) continue;
                    uint at = ins[i].addr, t = ins[i].operand;
                    if (t >= EndAddress || !a.entries.Contains(t)) continue;
                    if (t <= at && NearestAtOrBelow(sorted, at) == t) continue;
                    a.flags[at >> 2] |= TailFlag;
                }
            }
            else
            {
                Array.Clear(heapSlots, 0, heapSlots.Length);
            }

            a.allTargets.UnionWith(a.entries);
            var constValues = new HashSet<uint>();
            foreach (var x in ins)
                if (x.op == OpCode.PUSH && heap.AddressConst(x.operand, out uint v)) constValues.Add(v);
            for (int i = 0; i < ins.Length; i++)
            {
                if (reached[i]) continue;
                ref readonly var x = ref ins[i];
                uint at = x.addr;
                if (x.op == OpCode.JUMP)
                {
                    uint t = x.operand;
                    if (t >= EndAddress || t >= (uint)code.Length) continue;
                    if (constValues.Contains(at + 8))
                    {
                        a.flags[at >> 2] |= CallFlag | UnverifiedFlag;
                        a.allTargets.Add(t);
                    }
                    else if (i >= 3 && ins[i - 1].op == OpCode.COPY && ins[i - 1].addr == at - 4
                        && ins[i - 2].op == OpCode.PUSH && ins[i - 2].addr == at - 12
                        && ins[i - 3].op == OpCode.PUSH && ins[i - 3].addr == at - 20
                        && heap.AddressConst(ins[i - 3].operand, out uint back) && back == at + 8)
                    {
                        a.flags[at >> 2] |= HeapCallFlag | UnverifiedFlag;
                        heapSlots[at >> 2] = ins[i - 2].operand;
                        anyHeapSlot = true;
                        a.allTargets.Add(t);
                    }
                }
                else if (x.op == OpCode.JUMP_INDIRECT)
                {
                    if (i >= 2 && ins[i - 2].op == OpCode.PUSH && ins[i - 2].operand == x.operand && ins[i - 1].op == OpCode.COPY)
                        a.flags[at >> 2] |= ReturnFlag | UnverifiedFlag;
                    else if (i >= 1 && ins[i - 1].op == OpCode.EXTERN && heap.Sig(ins[i - 1].operand) == SwitchSig) { }
                    else if (i >= 1 && ins[i - 1].op == OpCode.PUSH && heap.AddressConst(ins[i - 1].operand, out uint v) && v == at + 8)
                        a.flags[at >> 2] |= CallFlag | UnverifiedFlag;
                    else a.flags[at >> 2] |= ReturnFlag | UnverifiedFlag;
                }
            }

            a.heapSlots = anyHeapSlot ? heapSlots : null;
            a.siteIndex = new int[words];
            for (int w = 0; w < words; w++)
            {
                a.siteIndex[w] = -1;
                if ((a.flags[w] & (CallFlag | TailFlag | HeapCallFlag)) != 0) a.siteIndex[w] = a.siteCount++;
            }

            var jumpTargets = new HashSet<uint>();
            foreach (var x in ins)
                if ((x.op == OpCode.JUMP || x.op == OpCode.JUMP_IF_FALSE) && x.operand < (uint)code.Length) jumpTargets.Add(x.operand);
            foreach (uint e in exports) a.bodyLabels[e] = FindBodyLabel(e, ins, sim, heap, a.entries, jumpTargets);

            TrimPools();
            return a;
        }

        static uint FindBodyLabel(uint e, Ins[] ins, Sim sim, MapHeap heap, HashSet<uint> entries, HashSet<uint> jumpTargets)
        {
            int start = sim.IndexOf(e);
            if (start < 0) return e;
            for (int i = start + 1, n = 0; i < ins.Length && n < 64; i++, n++)
            {
                var op = ins[i].op;
                if (op == OpCode.JUMP || op == OpCode.JUMP_INDIRECT || ins[i].addr >= EndAddress) break;
                uint at = ins[i].addr;
                if (entries.Contains(at) || jumpTargets.Contains(at)) return at;
            }
            bool sentinel = ins[start].op == OpCode.PUSH && heap.AddressConst(ins[start].operand, out uint v) && v >= EndAddress;
            return sentinel ? e + 8 : e;
        }

        static uint NearestAtOrBelow(List<uint> sorted, uint at)
        {
            int lo = 0, hi = sorted.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] <= at)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return found >= 0 ? sorted[found] : uint.MaxValue;
        }
    }
}
