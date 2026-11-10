using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace MagmaVRC.UdonProfiler
{
    /// <summary>Saves and loads recorded frames as <c>.udonprofile</c> captures.</summary>
    public static class UdonProfilerCapture
    {
        public const int MagicV1 = 0x55445031;
        public const int MagicV2 = 0x55445032;
        public const int FormatVersion = 4;

        /// <summary>Writes every frame in the ring with names, call paths, stats and spans.</summary>
        public static void Save(string path)
        {
            int count = Math.Max(0, UdonProfilerRecorder.FrameCount);
            int newest = UdonProfilerRecorder.NewestFrame;
            int oldest = newest - count + 1;
            var paths = UdonProfilerRecorder.Paths;
            int pathCount = paths.Count;

            var behaviours = new List<int>();
            var seen = new HashSet<int>();
            var resolved = new HashSet<(int, uint)>();
            var methodOwners = new HashSet<int>();
            void Use(int b) { if (b != 0 || UdonProfilerRecorder.GetInfo(0) != null) { if (seen.Add(b)) behaviours.Add(b); } }
            void Name(int b, uint e) { if (resolved.Add((b, e))) UdonProfilerRecorder.EventName(b, e); }

            for (int p = 0; p < pathCount; p++)
            {
                ref readonly var n = ref paths[p];
                if (p == PathTable.OverflowPath) continue;
                Use(n.behaviourId);
                if (n.kind == PathKind.Event) Name(n.behaviourId, n.key);
                else if (n.kind == PathKind.Method) methodOwners.Add(n.behaviourId);
            }
            for (int s = oldest; s <= newest; s++)
            {
                var f = UdonProfilerRecorder.GetFrame(s);
                if (f == null) continue;
                for (int i = 0; i < f.sampleCount; i++)
                {
                    ref readonly var smp = ref f.samples[i];
                    Use(smp.behaviourId);
                    Name(smp.behaviourId, smp.entry);
                    if (smp.scheduledByBehaviour != 0) { Use(smp.scheduledByBehaviour); Name(smp.scheduledByBehaviour, smp.scheduledByEntry); }
                }
            }
            foreach (int b in methodOwners) UdonProfilerRecorder.EnsureMethodNames(b);

            using var w = new BinaryWriter(File.Create(path));
            w.Write(MagicV2);
            w.Write(FormatVersion);
            w.Write(Stopwatch.Frequency);
            w.Write(UdonProfilerRecorder.RecordedVm ?? "");
            w.Write(UdonProfilerRecorder.RecordedMethodHooks ?? "");

            int scripts = UdonProfilerRecorder.ScriptCount;
            w.Write(scripts);
            for (int i = 0; i < scripts; i++) w.Write(UdonProfilerRecorder.ScriptName(i) ?? "");

            w.Write(behaviours.Count);
            foreach (int b in behaviours)
            {
                var info = UdonProfilerRecorder.GetInfo(b);
                w.Write(b);
                w.Write(info?.script ?? "?");
                w.Write(info?.objectName ?? "?");
                w.Write(info?.objectPath ?? "?");
                w.Write(info?.objectKey ?? "");
                w.Write((byte)(info?.syncMode ?? NetSync.Unknown));
                if (info == null) { w.Write(0); w.Write(0); continue; }
                w.Write(info.events.Count);
                foreach (var kv in info.events) { w.Write(kv.Key); w.Write(kv.Value ?? ""); }
                w.Write(info.methods.Count);
                foreach (var kv in info.methods) { w.Write(kv.Key); w.Write(kv.Value.name ?? ""); w.Write(kv.Value.file ?? ""); w.Write(kv.Value.line); }
            }

            int sigs = UdonProfilerRecorder.SigCount;
            w.Write(sigs);
            for (int i = 0; i < sigs; i++) w.Write(UdonProfilerRecorder.Sig(i) ?? "");

            w.Write(pathCount);
            for (int p = 0; p < pathCount; p++)
            {
                ref readonly var n = ref paths[p];
                w.Write(n.parent);
                w.Write((byte)n.kind);
                w.Write(n.behaviourId);
                w.Write(n.key);
            }

            int written = 0;
            for (int s = oldest; s <= newest; s++) if (UdonProfilerRecorder.GetFrame(s) != null) written++;
            w.Write(written);
            for (int s = oldest; s <= newest; s++)
            {
                var f = UdonProfilerRecorder.GetFrame(s);
                if (f == null) continue;
                w.Write(f.frameNumber); w.Write(f.totalTicks); w.Write(f.externCount); w.Write(f.fusedCount); w.Write(f.droppedSpans); w.Write(f.droppedStats);
                w.Write(f.hasInstructions); w.Write(f.totalInstructions);
                w.Write(f.deltaTime);
                w.Write(f.sampleCount);
                for (int i = 0; i < f.sampleCount; i++)
                {
                    ref readonly var x = ref f.samples[i];
                    w.Write(x.behaviourId); w.Write(x.entry); w.Write(x.parent); w.Write(x.path); w.Write(x.depth);
                    w.Write(x.startTicks); w.Write(x.inclusiveTicks); w.Write(x.childTicks); w.Write(x.externTicks); w.Write(x.externCount); w.Write(x.fusedCount);
                    w.Write(x.scheduledByBehaviour); w.Write(x.scheduledByEntry);
                    w.Write((byte)((x.halted ? 1 : 0) | (x.folded ? 2 : 0)));
                    w.Write(x.allocBytes); w.Write(x.childAlloc);
                    w.Write(x.instructions); w.Write(x.childInstructions);
                }
                w.Write(f.statCount);
                for (int i = 0; i < f.statCount; i++)
                {
                    ref readonly var st = ref f.stats[i];
                    w.Write(st.path); w.Write(st.calls); w.Write(st.inclusiveTicks); w.Write(st.selfTicks); w.Write(st.externTicks);
                    w.Write(st.externCount); w.Write(st.fusedCount); w.Write(st.maxTicks);
                    w.Write(st.instructions); w.Write(st.selfInstructions);
                }
                w.Write(f.spanCount);
                for (int i = 0; i < f.spanCount; i++)
                {
                    ref readonly var sp = ref f.spans[i];
                    w.Write(sp.path); w.Write(sp.depth); w.Write(sp.startTicks); w.Write(sp.ticks);
                    w.Write(sp.instructions);
                }
                w.Write(f.netCount);
                for (int i = 0; i < f.netCount; i++)
                {
                    ref readonly var n = ref f.net[i];
                    w.Write(n.behaviourId); w.Write(n.requests); w.Write(n.requestsIgnored); w.Write(n.serializations); w.Write(n.overLimit); w.Write(n.estBytes);
                    w.Write(n.eventsAll); w.Write(n.eventsOwner); w.Write(n.eventsOthers); w.Write(n.eventsSelf); w.Write(n.eventBytes);
                    w.Write(n.rateLimitHits); w.Write(n.ownershipTransfers);
                }
            }
        }

        /// <summary>Replaces the ring with a saved capture (v1 to v4); throws <see cref="System.IO.IOException"/> for other files. Captures before v3 load without instruction counts, before v4 without network data, frame durations or the recording VM.</summary>
        public static void Load(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            int magic = r.ReadInt32();
            if (magic == MagicV1) { LoadV1(r); return; }
            if (magic != MagicV2) throw new IOException("not an Udon profiler capture");
            int version = r.ReadInt32();
            if (version > FormatVersion || version < 2) throw new IOException("unsupported Udon profiler capture version " + version);
            LoadV2(r, version);
        }

        static Func<long, long> Scale(long freq)
        {
            if (freq <= 0 || freq == Stopwatch.Frequency) return t => t;
            double k = (double)Stopwatch.Frequency / freq;
            return t => (long)(t * k);
        }

        static void LoadV2(BinaryReader r, int version)
        {
            bool instr = version >= 3, net = version >= 4;
            var T = Scale(r.ReadInt64());
            string vm = net ? r.ReadString() : null, hooks = net ? r.ReadString() : null;
            int scripts = r.ReadInt32();
            var scriptNames = new string[Math.Max(0, scripts)];
            for (int i = 0; i < scriptNames.Length; i++) scriptNames[i] = r.ReadString();

            UdonProfilerRecorder.ResetForLoad();
            UdonProfilerRecorder.RecordedVm = string.IsNullOrEmpty(vm) ? null : vm;
            UdonProfilerRecorder.RecordedMethodHooks = string.IsNullOrEmpty(hooks) ? null : hooks;
            foreach (var s in scriptNames) UdonProfilerRecorder.ScriptId(s);

            int behaviourCount = r.ReadInt32();
            for (int i = 0; i < behaviourCount; i++)
            {
                int id = r.ReadInt32();
                var info = new BehaviourInfo { script = r.ReadString(), objectName = r.ReadString(), objectPath = r.ReadString() };
                string key = r.ReadString();
                info.objectKey = string.IsNullOrEmpty(key) ? info.objectPath : key;
                if (net) info.syncMode = (NetSync)r.ReadByte();
                int events = r.ReadInt32();
                for (int e = 0; e < events; e++) { uint a = r.ReadUInt32(); info.events[a] = r.ReadString(); }
                int methods = r.ReadInt32();
                for (int m = 0; m < methods; m++)
                {
                    uint target = r.ReadUInt32();
                    string name = r.ReadString(), file = r.ReadString();
                    int line = r.ReadInt32();
                    info.methods[target] = new SourceRef { name = name, file = string.IsNullOrEmpty(file) ? null : file, line = line };
                }
                UdonProfilerRecorder.RegisterLoadedBehaviour(id, info);
            }

            int sigCount = r.ReadInt32();
            var sigMap = new int[Math.Max(0, sigCount)];
            for (int i = 0; i < sigMap.Length; i++) sigMap[i] = UdonProfilerRecorder.SigId(r.ReadString());

            var paths = UdonProfilerRecorder.Paths;
            int pathCount = r.ReadInt32();
            var remap = new int[Math.Max(0, pathCount)];
            for (int i = 0; i < remap.Length; i++)
            {
                int parent = r.ReadInt32();
                var kind = (PathKind)r.ReadByte();
                int b = r.ReadInt32();
                uint key = r.ReadUInt32();
                if (i == PathTable.OverflowPath) { remap[i] = PathTable.OverflowPath; continue; }
                int newParent = parent >= 0 && parent < i ? remap[parent] : -1;
                if (kind == PathKind.Extern) key = key < (uint)sigMap.Length ? (uint)sigMap[key] : (uint)UdonProfilerRecorder.SigId("?");
                if (kind != PathKind.Event && newParent < 0) { remap[i] = PathTable.OverflowPath; continue; }
                int sid = b == 0 ? -1 : UdonProfilerRecorder.GetInfo(b)?.scriptId ?? -1;
                int p = paths.Child(newParent, kind, b, key, sid);
                remap[i] = p >= 0 ? p : newParent >= 0 ? newParent : PathTable.OverflowPath;
            }
            int Map(int p) => (uint)p < (uint)remap.Length ? remap[p] : PathTable.OverflowPath;

            int frames = r.ReadInt32();
            int skip = Math.Max(0, frames - UdonProfilerRecorder.Capacity);
            var scratch = new Frame();
            int loaded = 0;
            for (int i = 0; i < frames; i++)
            {
                Frame f;
                if (i < skip) { f = scratch; f.Reset(0, -1); }
                else f = UdonProfilerRecorder.LoadFrame(loaded++);
                f.frameNumber = r.ReadInt32();
                f.totalTicks = T(r.ReadInt64());
                f.externCount = r.ReadInt32();
                f.fusedCount = r.ReadInt32();
                f.droppedSpans = r.ReadInt32();
                f.droppedStats = r.ReadInt32();
                if (instr) { f.hasInstructions = r.ReadBoolean(); f.totalInstructions = r.ReadInt64(); }
                if (net) f.deltaTime = r.ReadSingle();
                int samples = r.ReadInt32();
                for (int k = 0; k < samples; k++)
                {
                    ref var s = ref f.AddSample(out _);
                    s.behaviourId = r.ReadInt32(); s.entry = r.ReadUInt32(); s.parent = r.ReadInt32(); s.path = Map(r.ReadInt32()); s.depth = r.ReadInt32();
                    s.startTicks = T(r.ReadInt64()); s.inclusiveTicks = T(r.ReadInt64()); s.childTicks = T(r.ReadInt64()); s.externTicks = T(r.ReadInt64());
                    s.externCount = r.ReadInt32(); s.fusedCount = r.ReadInt32();
                    s.scheduledByBehaviour = r.ReadInt32(); s.scheduledByEntry = r.ReadUInt32();
                    byte flags = r.ReadByte();
                    s.halted = (flags & 1) != 0; s.folded = (flags & 2) != 0;
                    s.allocBytes = r.ReadInt64(); s.childAlloc = r.ReadInt64();
                    if (instr) { s.instructions = r.ReadInt64(); s.childInstructions = r.ReadInt64(); }
                    s.bucket = Histogram.Bucket(s.inclusiveTicks);
                }
                int stats = r.ReadInt32();
                for (int k = 0; k < stats; k++)
                {
                    int p = Map(r.ReadInt32());
                    ref var st = ref f.AddStat(p, out _);
                    st.calls = r.ReadInt32(); st.inclusiveTicks = T(r.ReadInt64()); st.selfTicks = T(r.ReadInt64()); st.externTicks = T(r.ReadInt64());
                    st.externCount = r.ReadInt32(); st.fusedCount = r.ReadInt32(); st.maxTicks = T(r.ReadInt64());
                    if (instr) { st.instructions = r.ReadInt64(); st.selfInstructions = r.ReadInt64(); }
                }
                int spans = r.ReadInt32();
                for (int k = 0; k < spans; k++)
                {
                    ref var sp = ref f.AddSpan(out _);
                    sp.path = Map(r.ReadInt32()); sp.depth = r.ReadInt32(); sp.startTicks = T(r.ReadInt64()); sp.ticks = T(r.ReadInt64());
                    if (instr) sp.instructions = r.ReadInt64();
                }
                if (!net) continue;
                int nets = r.ReadInt32();
                for (int k = 0; k < nets; k++)
                {
                    ref var n = ref f.AddNet(r.ReadInt32(), out _);
                    n.requests = r.ReadInt32(); n.requestsIgnored = r.ReadInt32(); n.serializations = r.ReadInt32(); n.overLimit = r.ReadInt32(); n.estBytes = r.ReadInt64();
                    n.eventsAll = r.ReadInt32(); n.eventsOwner = r.ReadInt32(); n.eventsOthers = r.ReadInt32(); n.eventsSelf = r.ReadInt32(); n.eventBytes = r.ReadInt64();
                    n.rateLimitHits = r.ReadInt32(); n.ownershipTransfers = r.ReadInt32();
                }
            }
            UdonProfilerRecorder.CommitLoad(loaded);
        }

        static void LoadV1(BinaryReader r)
        {
            var T = Scale(r.ReadInt64());
            UdonProfilerRecorder.ResetForLoad();
            int behaviourCount = r.ReadInt32();
            for (int i = 0; i < behaviourCount; i++)
            {
                int id = r.ReadInt32();
                var info = new BehaviourInfo { script = r.ReadString(), objectName = r.ReadString(), objectPath = r.ReadString() };
                info.objectKey = info.objectPath;
                int events = r.ReadInt32();
                for (int e = 0; e < events; e++) { uint a = r.ReadUInt32(); info.events[a] = r.ReadString(); }
                UdonProfilerRecorder.RegisterLoadedBehaviour(id, info);
            }
            int sigCount = r.ReadInt32();
            var sigMap = new int[Math.Max(0, sigCount)];
            for (int i = 0; i < sigMap.Length; i++) sigMap[i] = UdonProfilerRecorder.SigId(r.ReadString());

            var paths = UdonProfilerRecorder.Paths;
            int frames = Math.Min(r.ReadInt32(), UdonProfilerRecorder.Capacity);
            var statOf = new Dictionary<int, int>();
            var firstPath = new Dictionary<(int, uint), int>();
            for (int fi = 0; fi < frames; fi++)
            {
                var f = UdonProfilerRecorder.LoadFrame(fi);
                statOf.Clear();
                firstPath.Clear();
                f.frameNumber = r.ReadInt32();
                f.totalTicks = T(r.ReadInt64());
                f.externCount = r.ReadInt32();
                int samples = r.ReadInt32();
                for (int k = 0; k < samples; k++)
                {
                    int b = r.ReadInt32(); uint entry = r.ReadUInt32(); int parent = r.ReadInt32();
                    long start = T(r.ReadInt64()), incl = T(r.ReadInt64()), child = T(r.ReadInt64()), ext = T(r.ReadInt64());
                    int extCount = r.ReadInt32();
                    int schedB = r.ReadInt32(); uint schedE = r.ReadUInt32();
                    bool halted = r.ReadBoolean();
                    long alloc = r.ReadInt64(), childAlloc = r.ReadInt64();
                    if (parent >= k) parent = -1;
                    int parentPath = parent >= 0 ? f.samples[parent].path : -1;
                    int depth = parent >= 0 ? f.samples[parent].depth + 1 : 0;
                    int sid = UdonProfilerRecorder.GetInfo(b)?.scriptId ?? -1;
                    int p = paths.Child(parentPath, PathKind.Event, b, entry, sid);
                    bool folded = false;
                    if (p < 0)
                    {
                        if (parent < 0) p = PathTable.OverflowPath;
                        else { p = parentPath; folded = true; }
                    }
                    ref var s = ref f.AddSample(out _);
                    s.behaviourId = b; s.entry = entry; s.parent = parent; s.path = p; s.depth = depth; s.folded = folded;
                    s.startTicks = start; s.inclusiveTicks = incl; s.childTicks = child; s.externTicks = ext; s.externCount = extCount;
                    s.scheduledByBehaviour = schedB; s.scheduledByEntry = schedE; s.halted = halted; s.allocBytes = alloc; s.childAlloc = childAlloc;
                    s.bucket = Histogram.Bucket(incl);

                    if (!folded && !firstPath.ContainsKey((b, entry))) firstPath[(b, entry)] = p;
                    if (!statOf.TryGetValue(p, out int si)) { f.AddStat(p, out si); statOf[p] = si; }
                    ref var st = ref f.stats[si];
                    if (!folded)
                    {
                        st.calls++;
                        st.inclusiveTicks += incl;
                        if (incl > st.maxTicks) st.maxTicks = incl;
                    }
                    st.selfTicks += incl - child;
                    st.externTicks += ext;
                    st.externCount += extCount;
                }
                int externs = r.ReadInt32();
                for (int k = 0; k < externs; k++)
                {
                    int b = r.ReadInt32(); uint entry = r.ReadUInt32(); int sig = r.ReadInt32(); int count = r.ReadInt32(); long ticks = T(r.ReadInt64());
                    if (!firstPath.TryGetValue((b, entry), out int owner) || (uint)sig >= (uint)sigMap.Length) { f.droppedStats++; continue; }
                    int ownerBehaviour = paths[owner].behaviourId, ownerScript = paths[owner].scriptId;
                    int p = paths.Child(owner, PathKind.Extern, ownerBehaviour, (uint)sigMap[sig], ownerScript);
                    if (p < 0) { f.droppedStats++; continue; }
                    if (!statOf.TryGetValue(p, out int si)) { f.AddStat(p, out si); statOf[p] = si; }
                    ref var st = ref f.stats[si];
                    st.calls += count;
                    st.inclusiveTicks += ticks;
                    long avg = count > 0 ? ticks / count : ticks;
                    if (avg > st.maxTicks) st.maxTicks = avg;
                }
            }
            UdonProfilerRecorder.CommitLoad(frames);
        }
    }
}
