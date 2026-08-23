using System;
using System.Collections.Generic;

namespace MagmaVRC.UdonProfiler
{
    public enum GroupMode { Script, GameObject, Event }
    public enum NodeKind { Group, Behaviour, Event, Extern }

    public class Node
    {
        public string name;
        public NodeKind kind;
        public int id;
        public double totalMs, selfMs, externMs, maxMs;
        public double baselineMs = double.NaN;
        public int calls, externCalls, instances;
        public bool halted;
        public int behaviourId;
        public uint entry;
        public string sig;
        public List<Node> children = new List<Node>();
        internal HashSet<int> instanceSet;
    }

    public struct Edge
    {
        public int fromBehaviour; public uint fromEntry;
        public int toBehaviour; public uint toEntry;
        public int count; public double ms; public bool delayed;
    }

    public struct ExternRow
    {
        public string sig; public int count; public double ms;
    }

    public class ProfileView
    {
        public int fromFrame, toFrame, frameCount;
        public double[] frameMs;
        public int[] frameEvents;
        public int[] frameNumbers;
        public double avgMs, maxMs;
        public int avgEvents, avgExterns, activeBehaviours;
        public List<Node> roots = new List<Node>();
        public List<Edge> edges = new List<Edge>();
        public List<ExternRow> topExterns = new List<ExternRow>();
        internal Dictionary<(int, uint, string), ExternStat> externsByEvent = new Dictionary<(int, uint, string), ExternStat>();
        internal Dictionary<int, Node> behaviourNodes = new Dictionary<int, Node>();

        public List<Edge> EdgesInto(int behaviourId, uint entry)
        {
            var r = new List<Edge>();
            foreach (var e in edges) if (e.toBehaviour == behaviourId && e.toEntry == entry) r.Add(e);
            r.Sort((a, b) => b.ms.CompareTo(a.ms));
            return r;
        }

        public List<Edge> EdgesOutOf(int behaviourId, uint entry)
        {
            var r = new List<Edge>();
            foreach (var e in edges) if (e.fromBehaviour == behaviourId && e.fromEntry == entry) r.Add(e);
            r.Sort((a, b) => b.ms.CompareTo(a.ms));
            return r;
        }

        public List<ExternRow> ExternsOf(Node n)
        {
            var acc = new Dictionary<string, ExternStat>();
            Collect(n, acc);
            var r = new List<ExternRow>(acc.Count);
            foreach (var kv in acc) r.Add(new ExternRow { sig = kv.Key, count = kv.Value.count, ms = kv.Value.ticks * UdonProfilerRecorder.MsPerTick });
            r.Sort((a, b) => b.ms.CompareTo(a.ms));
            return r;
        }

        void Collect(Node n, Dictionary<string, ExternStat> acc)
        {
            if (n.kind == NodeKind.Event || (n.kind == NodeKind.Behaviour && n.entry != 0))
            {
                foreach (var kv in externsByEvent)
                {
                    if (kv.Key.Item1 != n.behaviourId || kv.Key.Item2 != n.entry) continue;
                    acc.TryGetValue(kv.Key.Item3, out var st);
                    st.count += kv.Value.count; st.ticks += kv.Value.ticks;
                    acc[kv.Key.Item3] = st;
                }
                return;
            }
            if (n.kind == NodeKind.Extern) return;
            foreach (var c in n.children) Collect(c, acc);
        }

        public List<Node> InstancesOf(Node group)
        {
            var r = new List<Node>();
            Instances(group, r);
            r.Sort((a, b) => b.totalMs.CompareTo(a.totalMs));
            return r;
        }

        void Instances(Node n, List<Node> r)
        {
            if (n.kind == NodeKind.Behaviour) { r.Add(n); return; }
            if (n.kind == NodeKind.Event && n.behaviourId != 0 && behaviourNodes.TryGetValue(n.behaviourId, out var b) && !r.Contains(b)) { r.Add(b); return; }
            foreach (var c in n.children) Instances(c, r);
        }
    }

    public static class UdonProfilerModel
    {
        const int ExternChildLimit = 8;

        static bool IsLoop(string e) => e == "_update" || e == "_lateUpdate" || e == "_fixedUpdate" || e == "_postLateUpdate";

        public static ProfileView Build(int fromFrame, int toFrame, GroupMode mode, bool loopOnly, string search)
        {
            var v = new ProfileView();
            int newest = UdonProfilerRecorder.NewestFrame;
            int oldest = newest - UdonProfilerRecorder.FrameCount + 1;
            if (newest < 0) { v.frameMs = new double[0]; v.frameEvents = new int[0]; v.frameNumbers = new int[0]; return v; }
            fromFrame = Math.Max(fromFrame, oldest);
            toFrame = Math.Min(toFrame, newest);
            if (toFrame < fromFrame) { v.frameMs = new double[0]; v.frameEvents = new int[0]; v.frameNumbers = new int[0]; return v; }
            v.fromFrame = fromFrame; v.toFrame = toFrame;
            v.frameCount = toFrame - fromFrame + 1;
            v.frameMs = new double[v.frameCount];
            v.frameEvents = new int[v.frameCount];
            v.frameNumbers = new int[v.frameCount];

            double ms = UdonProfilerRecorder.MsPerTick;
            var groups = new Dictionary<string, Node>();
            var behaviours = new Dictionary<(string, int), Node>();
            var events = new Dictionary<(int, uint), Node>();
            var edgeMap = new Dictionary<(int, uint, int, uint, bool), Edge>();
            var externTotals = new Dictionary<string, ExternStat>();
            var active = new HashSet<int>();
            var kept = new HashSet<(int, uint)>();
            long totalExterns = 0, totalEvents = 0, totalTicks = 0;
            bool filter = !string.IsNullOrEmpty(search);
            var keep = new List<bool>();

            for (int f = fromFrame; f <= toFrame; f++)
            {
                var frame = UdonProfilerRecorder.GetFrame(f);
                if (frame == null) continue;
                v.frameNumbers[f - fromFrame] = frame.frameNumber;
                var samples = frame.samples;
                keep.Clear();
                long frameTicks = 0; int frameEvents = 0;
                for (int i = 0; i < samples.Count; i++)
                {
                    var s = samples[i];
                    bool k = s.parent >= 0 ? keep[s.parent] : !loopOnly || IsLoop(UdonProfilerRecorder.EventName(s.behaviourId, s.entry));
                    keep.Add(k);
                    if (!k) continue;
                    if (s.parent < 0) frameTicks += s.inclusiveTicks;
                    frameEvents++;
                    active.Add(s.behaviourId);
                    kept.Add((s.behaviourId, s.entry));

                    var info = UdonProfilerRecorder.GetInfo(s.behaviourId);
                    string evName = UdonProfilerRecorder.EventName(s.behaviourId, s.entry);
                    string groupKey = mode == GroupMode.Script ? info?.script ?? "?" : mode == GroupMode.GameObject ? info?.objectPath ?? "?" : evName;
                    if (!groups.TryGetValue(groupKey, out var g))
                    {
                        g = new Node { name = groupKey, kind = NodeKind.Group, id = Hash(0, groupKey.GetHashCode(), 0, mode.GetHashCode()), instanceSet = new HashSet<int>() };
                        groups[groupKey] = g;
                    }
                    Node b;
                    if (mode == GroupMode.Event)
                    {
                        if (!behaviours.TryGetValue((groupKey, s.behaviourId), out b))
                        {
                            b = new Node { name = info?.objectName ?? s.behaviourId.ToString(), kind = NodeKind.Behaviour, id = Hash(1, s.behaviourId, (int)s.entry, groupKey.GetHashCode()), behaviourId = s.behaviourId, entry = s.entry };
                            behaviours[(groupKey, s.behaviourId)] = b;
                            g.children.Add(b);
                        }
                        Accumulate(g, s, ms); Accumulate(b, s, ms);
                        g.instanceSet.Add(s.behaviourId);
                        if (!v.behaviourNodes.ContainsKey(s.behaviourId)) v.behaviourNodes[s.behaviourId] = b;
                    }
                    else
                    {
                        if (!behaviours.TryGetValue((groupKey, s.behaviourId), out b))
                        {
                            b = new Node { name = info?.objectName ?? s.behaviourId.ToString(), kind = NodeKind.Behaviour, id = Hash(1, s.behaviourId, 0, mode.GetHashCode()), behaviourId = s.behaviourId };
                            behaviours[(groupKey, s.behaviourId)] = b;
                            g.children.Add(b);
                            v.behaviourNodes[s.behaviourId] = b;
                        }
                        if (!events.TryGetValue((s.behaviourId, s.entry), out var e))
                        {
                            e = new Node { name = evName, kind = NodeKind.Event, id = Hash(2, s.behaviourId, (int)s.entry, mode.GetHashCode()), behaviourId = s.behaviourId, entry = s.entry };
                            events[(s.behaviourId, s.entry)] = e;
                            b.children.Add(e);
                        }
                        Accumulate(g, s, ms); Accumulate(b, s, ms); Accumulate(e, s, ms);
                        g.instanceSet.Add(s.behaviourId);
                    }
                    if (s.halted) { g.halted = true; b.halted = true; }

                    if (s.parent >= 0)
                    {
                        var p = samples[s.parent];
                        AddEdge(edgeMap, p.behaviourId, p.entry, s.behaviourId, s.entry, s.inclusiveTicks * ms, false);
                    }
                    else if (s.scheduledByBehaviour != 0)
                        AddEdge(edgeMap, s.scheduledByBehaviour, s.scheduledByEntry, s.behaviourId, s.entry, s.inclusiveTicks * ms, true);
                }
                foreach (var kv in frame.externs)
                {
                    if (loopOnly && !kept.Contains((kv.Key.behaviourId, kv.Key.entry))) continue;
                    v.externsByEvent.TryGetValue(kv.Key, out var st);
                    st.count += kv.Value.count; st.ticks += kv.Value.ticks;
                    v.externsByEvent[kv.Key] = st;
                    externTotals.TryGetValue(kv.Key.sig, out var t);
                    t.count += kv.Value.count; t.ticks += kv.Value.ticks;
                    externTotals[kv.Key.sig] = t;
                    totalExterns += kv.Value.count;
                }
                v.frameMs[f - fromFrame] = frameTicks * ms;
                v.frameEvents[f - fromFrame] = frameEvents;
                totalTicks += frameTicks; totalEvents += frameEvents;
                if (frameTicks * ms > v.maxMs) v.maxMs = frameTicks * ms;
            }

            v.avgMs = totalTicks * ms / v.frameCount;
            v.avgEvents = (int)Math.Round((double)totalEvents / v.frameCount);
            v.avgExterns = (int)Math.Round((double)totalExterns / v.frameCount);
            v.activeBehaviours = active.Count;

            foreach (var e in events.Values)
            {
                int added = 0;
                foreach (var row in v.ExternsOf(e))
                {
                    if (added++ >= ExternChildLimit) break;
                    e.children.Add(new Node { name = row.sig, kind = NodeKind.Extern, id = Hash(3, e.behaviourId, (int)e.entry, row.sig.GetHashCode()), totalMs = row.ms, selfMs = row.ms, externMs = row.ms, calls = row.count, externCalls = row.count, behaviourId = e.behaviourId, entry = e.entry, sig = row.sig });
                }
            }

            foreach (var g in groups.Values)
            {
                g.instances = g.instanceSet.Count;
                foreach (var b in g.children) b.instances = 1;
                if (!filter || Matches(g, search)) v.roots.Add(g);
            }
            v.roots.Sort((a, b) => b.totalMs.CompareTo(a.totalMs));
            Dedup(v.roots, new HashSet<int>());
            if (Baseline != null && BaselineMode == mode) ApplyBaseline(v.roots, v.frameCount);
            foreach (var e in edgeMap.Values) v.edges.Add(e);
            foreach (var kv in externTotals) v.topExterns.Add(new ExternRow { sig = kv.Key, count = kv.Value.count, ms = kv.Value.ticks * ms });
            v.topExterns.Sort((a, b) => b.ms.CompareTo(a.ms));
            return v;
        }

        public static Dictionary<int, double> Baseline;
        public static GroupMode BaselineMode;
        public static int BaselineFrames;

        public static void SetBaseline(ProfileView v, GroupMode mode)
        {
            if (v == null || v.frameCount == 0) { Baseline = null; return; }
            Baseline = new Dictionary<int, double>();
            BaselineMode = mode;
            BaselineFrames = v.frameCount;
            Flatten(v.roots, n => Baseline[n.id] = n.totalMs / v.frameCount);
        }

        static void ApplyBaseline(List<Node> nodes, int frames)
        {
            Flatten(nodes, n => { if (Baseline.TryGetValue(n.id, out var per)) n.baselineMs = per * frames; });
        }

        static void Flatten(List<Node> nodes, Action<Node> f)
        {
            foreach (var n in nodes) { f(n); Flatten(n.children, f); }
        }

        public static string ToCsv(ProfileView v)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("kind,name,script,object,event,total_ms,self_ms,extern_ms,max_ms,calls,externs,avg_ms,pct");
            void Walk(Node n, int depth)
            {
                var info = n.kind == NodeKind.Group ? null : UdonProfilerRecorder.GetInfo(n.behaviourId);
                string ev = n.kind == NodeKind.Event ? UdonProfilerRecorder.EventName(n.behaviourId, n.entry) : n.kind == NodeKind.Extern ? n.sig : "";
                double pct = v.avgMs > 0 ? n.totalMs / Math.Max(1, v.frameCount) / v.avgMs * 100 : 0;
                sb.Append(n.kind).Append(',').Append(Q(n.name)).Append(',').Append(Q(info?.script)).Append(',').Append(Q(info?.objectPath)).Append(',').Append(Q(ev)).Append(',')
                  .Append(n.totalMs.ToString("0.000")).Append(',').Append(n.selfMs.ToString("0.000")).Append(',').Append(n.externMs.ToString("0.000")).Append(',').Append(n.maxMs.ToString("0.000")).Append(',')
                  .Append(n.calls).Append(',').Append(n.externCalls).Append(',').Append(n.calls == 0 ? "" : (n.totalMs / n.calls).ToString("0.0000")).Append(',').Append(pct.ToString("0.00")).AppendLine();
                foreach (var c in n.children) Walk(c, depth + 1);
            }
            foreach (var r in v.roots) Walk(r, 0);
            return sb.ToString();
        }

        static string Q(string s) => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";

        public struct TimelineItem { public int behaviourId; public uint entry; public int depth; public double startMs, ms, externMs; public int parent; }

        public static List<TimelineItem> Timeline(int frameIndex)
        {
            var r = new List<TimelineItem>();
            var f = UdonProfilerRecorder.GetFrame(frameIndex);
            if (f == null || f.samples.Count == 0) return r;
            double ms = UdonProfilerRecorder.MsPerTick;
            long t0 = f.samples[0].startTicks;
            var depth = new List<int>(f.samples.Count);
            for (int i = 0; i < f.samples.Count; i++)
            {
                var s = f.samples[i];
                int d = s.parent < 0 ? 0 : depth[s.parent] + 1;
                depth.Add(d);
                r.Add(new TimelineItem { behaviourId = s.behaviourId, entry = s.entry, depth = d, startMs = (s.startTicks - t0) * ms, ms = s.inclusiveTicks * ms, externMs = s.externTicks * ms, parent = s.parent });
            }
            return r;
        }

        static void Dedup(List<Node> nodes, HashSet<int> seen)
        {
            foreach (var n in nodes)
            {
                while (!seen.Add(n.id)) n.id = Hash(9, n.id, n.kind.GetHashCode(), n.name.GetHashCode());
                Dedup(n.children, seen);
            }
        }

        static bool Matches(Node n, string s)
        {
            if (n.name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            n.children.RemoveAll(c => !Matches(c, s));
            return n.children.Count > 0;
        }

        static void Accumulate(Node n, Sample s, double ms)
        {
            n.calls++;
            double inc = s.inclusiveTicks * ms;
            if (inc > n.maxMs) n.maxMs = inc;
            n.totalMs += inc;
            n.selfMs += (s.inclusiveTicks - s.childTicks) * ms;
            n.externMs += s.externTicks * ms;
            n.externCalls += s.externCount;
        }

        static void AddEdge(Dictionary<(int, uint, int, uint, bool), Edge> map, int fb, uint fe, int tb, uint te, double ms, bool delayed)
        {
            var key = (fb, fe, tb, te, delayed);
            map.TryGetValue(key, out var e);
            e.fromBehaviour = fb; e.fromEntry = fe; e.toBehaviour = tb; e.toEntry = te; e.delayed = delayed;
            e.count++; e.ms += ms;
            map[key] = e;
        }

        static int Hash(int kind, int a, int b, int c)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + kind; h = h * 31 + a; h = h * 31 + b; h = h * 31 + c;
                return h;
            }
        }
    }
}
