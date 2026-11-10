using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace MagmaVRC.UdonProfiler
{
    internal sealed class UdonProfilerDetailPane : ScrollView
    {
        public event Action<Node> JumpRequested;

        enum Target { None, NodeId, Event }

        sealed class Row
        {
            public VisualElement root;
            public Label name;
            public readonly Label[] nums = new Label[4];
            public Target target;
            public int nodeId, behaviourId;
            public uint entry;
        }

        sealed class Section
        {
            public Label title;
            public VisualElement body;
            public Label empty;
            public readonly List<Row> rows = new List<Row>();
            public int used;
            public bool active;
        }

        static readonly Comparison<Node> ByTotalDesc = (a, b) =>
        {
            int c = b.totalMs.CompareTo(a.totalMs);
            return c != 0 ? c : a.id.CompareTo(b.id);
        };

        readonly Label _placeholder, _title, _sub, _methodLine, _crumb, _halted, _stats, _net;
        readonly Section _instances, _calledBy, _calls, _methods, _externs, _children;
        readonly Section[] _sections;
        readonly List<Node> _scratch = new List<Node>();
        readonly List<int> _flameScratch = new List<int>();
        readonly StringBuilder _sb = new StringBuilder();

        ProfileView _view;
        int _lastId = int.MinValue, _lastGen = int.MinValue;
        NodeKind _lastKind;
        bool _lastFlame;

        public UdonProfilerDetailPane()
        {
            AddToClassList("up-detail");
            _placeholder = Text("up-empty");
            _title = Text("up-detail-title");
            _sub = Text("up-detail-sub");
            _methodLine = Text("up-detail-sub");
            _crumb = Text("up-detail-sub");
            _halted = Text("up-badge");
            _halted.text = "halted";
            _halted.style.alignSelf = Align.FlexStart;
            _stats = Text(null);
            _stats.style.whiteSpace = WhiteSpace.Normal;
            _net = Text("up-detail-sub");
            _net.style.whiteSpace = WhiteSpace.Normal;
            _instances = NewSection();
            _calledBy = NewSection();
            _calls = NewSection();
            _methods = NewSection();
            _externs = NewSection();
            _children = NewSection();
            _sections = new[] { _instances, _calledBy, _calls, _methods, _externs, _children };
            ShowPlaceholder("Select a row");
        }

        Label Text(string cls)
        {
            var l = new Label();
            if (cls != null) l.AddToClassList(cls);
            Add(l);
            return l;
        }

        Section NewSection()
        {
            var s = new Section { title = new Label(), body = new VisualElement(), empty = new Label("none") };
            s.title.AddToClassList("up-section");
            s.empty.AddToClassList("up-empty");
            s.body.Add(s.empty);
            Add(s.title);
            Add(s.body);
            return s;
        }

        public void Refresh(ProfileView view, Node selected, GroupMode mode)
        {
            _view = view;
            if (view == null || selected == null) { ShowPlaceholder("Select a row"); return; }
            var n = selected;
            if (_lastFlame || n.id != _lastId || n.kind != _lastKind || view.generation != _lastGen) scrollOffset = Vector2.zero;
            _lastFlame = false; _lastId = n.id; _lastKind = n.kind; _lastGen = view.generation;

            _placeholder.style.display = DisplayStyle.None;
            var info = n.kind == NodeKind.Group ? null : UdonProfilerRecorder.GetInfo(n.behaviourId);
            Show(_title, n.name);
            string sub;
            switch (n.kind)
            {
                case NodeKind.Group: sub = $"{mode} group | {n.instances} instance{(n.instances == 1 ? "" : "s")}"; break;
                case NodeKind.Behaviour:
                    sub = $"{info?.script}  |  {info?.objectPath}";
                    if (mode == GroupMode.Event) sub += "  |  " + UdonProfilerRecorder.EventName(n.behaviourId, n.entry);
                    break;
                case NodeKind.Event: sub = $"{info?.script}  |  {info?.objectPath}  |  {UdonProfilerRecorder.EventName(n.behaviourId, n.entry)}"; break;
                case NodeKind.Extern: sub = n.sig; break;
                default: sub = $"{info?.script}  |  {info?.objectPath}"; break;
            }
            Show(_sub, sub);
            if (n.kind == NodeKind.Method)
            {
                var src = UdonProfilerModel.SourceOf(n);
                string where = src.HasFile ? $"{src.file}:{src.line}" : "no source";
                Show(_methodLine, $"Method | {where} | names: {info?.map?.NamingSource ?? "none"}");
            }
            else Show(_methodLine, null);
            Show(_crumb, n.parent != null ? Breadcrumb(n) : null);
            _halted.style.display = n.halted ? DisplayStyle.Flex : DisplayStyle.None;
            Show(_stats, Stats(n, view));
            Show(_net, n.kind == NodeKind.Behaviour ? NetText(view.NetOf(n.behaviourId), info?.syncMode ?? NetSync.Unknown) : null);

            foreach (var s in _sections) s.active = false;

            if (n.kind == NodeKind.Group)
            {
                Begin(_instances, "Instances");
                var inst = view.InstancesOf(n);
                if (inst != null)
                    foreach (var b in inst)
                    {
                        var bi = UdonProfilerRecorder.GetInfo(b.behaviourId);
                        AddRow(_instances, bi?.objectPath ?? b.name, Target.NodeId, b.id, 0, 0, b.calls.ToString(), b.totalMs.ToString("0.00"));
                    }
            }

            if (n.kind == NodeKind.Behaviour || n.kind == NodeKind.Event)
            {
                Begin(_calledBy, "Called by");
                var into = view.EdgesInto(n);
                if (into != null)
                    foreach (var e in into)
                        AddRow(_calledBy, EdgeLabel(e.fromBehaviour, e.fromEntry, e.delayed), e.fromBehaviour != 0 ? Target.Event : Target.None, 0, e.fromBehaviour, e.fromEntry, e.count.ToString(), e.ms.ToString("0.00"));
                Begin(_calls, "Calls");
                var outOf = view.EdgesOutOf(n);
                if (outOf != null)
                    foreach (var e in outOf)
                        AddRow(_calls, EdgeLabel(e.toBehaviour, e.toEntry, e.delayed), e.toBehaviour != 0 ? Target.Event : Target.None, 0, e.toBehaviour, e.toEntry, e.count.ToString(), e.ms.ToString("0.00"));
            }

            if (n.kind == NodeKind.Event || n.kind == NodeKind.Method || (n.kind == NodeKind.Behaviour && mode == GroupMode.Event))
            {
                Begin(_methods, "Methods  (calls, ms)");
                _scratch.Clear();
                if (n.children != null)
                    foreach (var c in n.children)
                        if (c.kind == NodeKind.Method) _scratch.Add(c);
                _scratch.Sort(ByTotalDesc);
                foreach (var c in _scratch) AddRow(_methods, c.name, Target.NodeId, c.id, 0, 0, c.calls.ToString(), c.totalMs.ToString("0.00"));
                _scratch.Clear();
            }

            if (n.kind != NodeKind.Extern)
            {
                Begin(_externs, "Externs  (count, fused, ms, avg)");
                var ex = view.ExternsOf(n);
                if (ex != null)
                    foreach (var r in ex)
                    {
                        double avg = r.ms / Math.Max(1, r.count - r.fused);
                        AddRow(_externs, r.sig, Target.None, 0, 0, 0, r.count.ToString(), r.fused.ToString(), r.ms.ToString("0.00"), avg.ToString("0.0000"));
                    }
            }

            FinishSections();
        }

        public void ShowFlame(FlameGraph g, int index)
        {
            _view = null;
            if (g == null || index < 0 || index >= g.count) { ShowPlaceholder("Select a block"); return; }
            if (!_lastFlame || _lastId != index) scrollOffset = Vector2.zero;
            _lastFlame = true; _lastId = index; _lastGen = int.MinValue;

            ref readonly var n = ref g.nodes[index];
            _placeholder.style.display = DisplayStyle.None;
            Show(_title, n.label);
            string script = n.scriptId >= 0 ? UdonProfilerRecorder.ScriptName(n.scriptId) : null;
            Show(_sub, script != null && n.kind != NodeKind.Extern ? $"{n.kind}  |  {script}" : n.kind.ToString());
            Show(_methodLine, null);
            Show(_crumb, null);
            _halted.style.display = DisplayStyle.None;
            double root = g.count > 0 ? g.nodes[0].totalMs : 0;
            double pct = root > 0 ? n.totalMs / root * 100 : 0;
            string stats = $"Total {n.totalMs:0.000} ms/frame | Self {n.selfMs:0.000} ms/frame | {pct:0.0}% of Udon | {n.calls:0.##} calls/frame";
            if (n.kind == NodeKind.Extern) stats += $" | {n.fused} fused";
            else if (g.hasInstructions) stats += $" | instr {UdonProfilerModel.FormatCount(n.instructions)}/frame, self {UdonProfilerModel.FormatCount(n.selfInstructions)}/frame";
            Show(_stats, stats);
            Show(_net, null);

            foreach (var s in _sections) s.active = false;
            Begin(_children, "Children  (calls/frame, ms/frame)");
            _flameScratch.Clear();
            for (int c = n.firstChild; c >= 0 && c < g.count; c = g.nodes[c].nextSibling) _flameScratch.Add(c);
            var nodes = g.nodes;
            _flameScratch.Sort((a, b) =>
            {
                int r = nodes[b].totalMs.CompareTo(nodes[a].totalMs);
                return r != 0 ? r : a.CompareTo(b);
            });
            for (int i = 0; i < _flameScratch.Count && i < 10; i++)
            {
                ref readonly var c = ref nodes[_flameScratch[i]];
                AddRow(_children, c.label, Target.None, 0, 0, 0, c.calls.ToString("0.##"), c.totalMs.ToString("0.000"));
            }
            FinishSections();
        }

        void ShowPlaceholder(string text)
        {
            _lastId = int.MinValue;
            _lastGen = int.MinValue;
            _lastFlame = false;
            _placeholder.text = text;
            _placeholder.style.display = DisplayStyle.Flex;
            Show(_title, null);
            Show(_sub, null);
            Show(_methodLine, null);
            Show(_crumb, null);
            Show(_stats, null);
            Show(_net, null);
            _halted.style.display = DisplayStyle.None;
            foreach (var s in _sections) s.active = false;
            FinishSections();
        }

        public void ShowNet(NetRow r, NetSummary summary)
        {
            _view = null;
            if (r == null)
            {
                string total = summary != null && summary.HasData
                    ? $"Select a behaviour\n\n{summary.rows.Count} behaviours, ~{UdonProfilerWindow.FormatBytes(summary.bytesPerSec)} B/s estimated over {summary.seconds:0.0} s"
                    : "No network activity in this range";
                ShowPlaceholder(total);
                return;
            }
            if (!_lastFlame || _lastId != r.behaviourId) scrollOffset = Vector2.zero;
            _lastFlame = true; _lastId = r.behaviourId; _lastGen = int.MinValue;
            _placeholder.style.display = DisplayStyle.None;
            Show(_title, r.objectName);
            Show(_sub, $"{r.script}  |  {r.objectPath}");
            Show(_methodLine, null);
            Show(_crumb, null);
            _halted.style.display = DisplayStyle.None;
            Show(_stats, NetText(r, r.sync));
            Show(_net, $"Totals over {r.seconds:0.0} s: {r.requests} requests ({r.ignored} ignored), {r.serializations} serializations, ~{r.estBytes:N0} B, {r.Events} events, ~{r.eventBytes:N0} B event payload.");
            foreach (var s in _sections) s.active = false;
            FinishSections();
        }

        internal static string NetText(NetRow r, NetSync sync)
        {
            if (r == null) return sync == NetSync.Manual || sync == NetSync.Continuous ? $"Network (est.): {sync} sync, no activity in range" : null;
            var sb = new StringBuilder();
            sb.Append("Network (est.): ").Append(sync).Append(" sync | ")
              .Append(r.RequestsPerSec.ToString("0.0")).Append(" req/s");
            if (r.ignored > 0) sb.Append(" (").Append(r.ignored).Append(" ignored)");
            sb.Append(" | ").Append(r.SerializationsPerSec.ToString("0.0")).Append(" ser/s");
            if (r.serializations > 0) sb.Append(", ~").Append(r.BytesPerSerialization.ToString("0")).Append(" B/ser, ~").Append(UdonProfilerWindow.FormatBytes(r.SerializationBytesPerSec)).Append(" B/s");
            if (r.overLimit > 0) sb.Append(" | ").Append(r.overLimit).Append(" over limit");
            if (r.Events > 0)
                sb.Append(" | events all ").Append(r.eventsAll).Append(", owner ").Append(r.eventsOwner).Append(", others ").Append(r.eventsOthers)
                  .Append(", self ").Append(r.eventsSelf).Append(", ~").Append(UdonProfilerWindow.FormatBytes(r.EventBytesPerSec)).Append(" B/s");
            if (r.rateLimitHits > 0) sb.Append(" | ").Append(r.rateLimitHits).Append(" rate-limit hits");
            if (r.transfers > 0) sb.Append(" | ").Append(r.transfers).Append(" ownership transfer").Append(r.transfers == 1 ? "" : "s");
            return sb.ToString();
        }

        static void Show(Label l, string text)
        {
            if (string.IsNullOrEmpty(text)) { l.style.display = DisplayStyle.None; return; }
            l.text = text;
            l.style.display = DisplayStyle.Flex;
        }

        string Breadcrumb(Node n)
        {
            _scratch.Clear();
            for (var p = n.parent; p != null && _scratch.Count < 128; p = p.parent) _scratch.Add(p);
            _sb.Clear();
            for (int i = _scratch.Count - 1; i >= 0; i--)
            {
                _sb.Append(_scratch[i].name);
                if (i > 0) _sb.Append(" > ");
            }
            _scratch.Clear();
            return _sb.ToString();
        }

        string Stats(Node n, ProfileView view)
        {
            _sb.Clear();
            _sb.Append("Total ").Append(n.totalMs.ToString("0.00")).Append(" ms | Self ").Append(n.selfMs.ToString("0.00"))
               .Append(" ms | extern (self) ").Append(n.externMs.ToString("0.00")).Append(" ms");
            if (!double.IsNaN(n.externAllMs))
                _sb.Append(" | extern (incl. methods) ").Append((n.externAllMs / Math.Max(1, view.frameCount)).ToString("0.000")).Append(" ms");
            if (!double.IsNaN(n.p95Ms)) _sb.Append(" | p95 ").Append(n.p95Ms.ToString("0.000")).Append(" ms");
            if (UdonProfilerModel.HasInstructions(view, n))
            {
                double frames = Math.Max(1, view.frameCount);
                _sb.Append(" | instr ").Append(UdonProfilerModel.FormatCount(n.instructions / frames)).Append("/frame, self ")
                   .Append(UdonProfilerModel.FormatCount(n.selfInstructions / frames)).Append("/frame");
                if (n.calls > 0) _sb.Append(", ").Append(UdonProfilerModel.FormatCount((double)n.instructions / n.calls)).Append("/call");
            }
            _sb.Append(" | ").Append(n.calls).Append(" calls");
            if (n.fusedCalls > 0) _sb.Append(" | ").Append(n.fusedCalls).Append(" fused");
            if (n.kind == NodeKind.Extern)
                _sb.Append(" | avg ").Append((n.totalMs / Math.Max(1, n.calls - n.fusedCalls)).ToString("0.0000")).Append(" ms");
            else if (UdonProfilerRecorder.AllocSupported)
                _sb.Append(" | GC ").Append((n.allocBytes / 1024.0).ToString("0.0")).Append(" KB");
            return _sb.ToString();
        }

        static string EdgeLabel(int behaviourId, uint entry, bool delayed)
        {
            var i = UdonProfilerRecorder.GetInfo(behaviourId);
            return (delayed ? "(delayed) " : "") + (i?.script ?? "?") + " | " + (i?.objectName ?? "?") + " | " + UdonProfilerRecorder.EventName(behaviourId, entry);
        }

        static void Begin(Section s, string title)
        {
            s.active = true;
            s.used = 0;
            s.title.text = title;
        }

        void AddRow(Section s, string name, Target target, int nodeId, int behaviourId, uint entry, string n0 = null, string n1 = null, string n2 = null, string n3 = null)
        {
            Row r;
            if (s.used < s.rows.Count) r = s.rows[s.used];
            else
            {
                r = new Row { root = new VisualElement(), name = new Label() };
                r.root.AddToClassList("up-row");
                r.name.AddToClassList("up-row-name");
                r.root.Add(r.name);
                for (int i = 0; i < r.nums.Length; i++)
                {
                    var l = new Label();
                    l.AddToClassList("up-row-num");
                    r.nums[i] = l;
                    r.root.Add(l);
                }
                var captured = r;
                r.root.RegisterCallback<ClickEvent>(_ => OnRowClick(captured));
                s.rows.Add(r);
                s.body.Add(r.root);
            }
            s.used++;
            r.name.text = name ?? "";
            r.name.tooltip = name ?? "";
            SetNum(r.nums[0], n0);
            SetNum(r.nums[1], n1);
            SetNum(r.nums[2], n2);
            SetNum(r.nums[3], n3);
            r.target = target;
            r.nodeId = nodeId;
            r.behaviourId = behaviourId;
            r.entry = entry;
            r.root.style.display = DisplayStyle.Flex;
        }

        static void SetNum(Label l, string text)
        {
            if (text == null) { l.style.display = DisplayStyle.None; return; }
            l.text = text;
            l.style.display = DisplayStyle.Flex;
        }

        void FinishSections()
        {
            foreach (var s in _sections)
            {
                var d = s.active ? DisplayStyle.Flex : DisplayStyle.None;
                s.title.style.display = d;
                s.body.style.display = d;
                int used = s.active ? s.used : 0;
                for (int i = used; i < s.rows.Count; i++) s.rows[i].root.style.display = DisplayStyle.None;
                s.empty.style.display = s.active && used == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        void OnRowClick(Row r)
        {
            if (_view == null || r.target == Target.None) return;
            var n = r.target == Target.NodeId ? _view.FindById(r.nodeId) : _view.FindEvent(r.behaviourId, r.entry);
            if (n != null) JumpRequested?.Invoke(n);
        }
    }
}
