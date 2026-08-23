using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MagmaVRC.UdonProfiler
{
    public sealed class UdonProfilerWindow : EditorWindow
    {
        enum RangeMode { Selected, Last60, Last300, All, Custom }

        const string Prefs = "MagmaVRC.UdonProfiler.";
        static readonly string[] RangeNames = { "Selected frame", "Last 60", "Last 300", "All", "Custom" };

        [MenuItem("Window/Udon Profiler")]
        [MenuItem("VRChat SDK/Udon Profiler")]
        static void Open() => GetWindow<UdonProfilerWindow>("Udon Profiler");

        GroupMode groupMode;
        RangeMode rangeMode;
        bool loopOnly;
        float questScale = 4f;
        string search = "";
        int selectedFrame = -1, customFrom = -1, customTo = -1;

        ProfileView view;
        int lastVersion = -1;
        bool dirty = true;
        double lastRebuild;
        string sortColumn = "pct";
        bool sortAsc;

        UdonProfilerChart chart;
        UdonProfilerTimeline timeline;
        ToolbarToggle recToggle;
        Column deltaColumn;
        int lastPausedOn = -1;
        MultiColumnTreeView tree;
        ScrollView detail;
        Label footRange, footStatus;
        readonly Label[] tileValues = new Label[4];
        readonly Label[] tileSubs = new Label[4];
        VisualElement tileMs;
        DropdownField rangeDropdown;

        void OnEnable()
        {
            groupMode = (GroupMode)EditorPrefs.GetInt(Prefs + "GroupMode", 0);
            rangeMode = (RangeMode)EditorPrefs.GetInt(Prefs + "Range", (int)RangeMode.Last60);
            if (rangeMode == RangeMode.Custom) rangeMode = RangeMode.Last60;
            loopOnly = EditorPrefs.GetBool(Prefs + "LoopOnly", false);
            questScale = EditorPrefs.GetFloat(Prefs + "QuestScale", 4f);
            UdonProfilerRecorder.Recording = EditorPrefs.GetBool(Prefs + "Record", true);
            UdonProfilerRecorder.ExternTiming = EditorPrefs.GetBool(Prefs + "ExternTiming", true);
            UdonProfilerRecorder.PauseAboveMs = EditorPrefs.GetFloat(Prefs + "PauseAbove", 0f);
            lastPausedOn = UdonProfilerRecorder.PausedOnFrame;
            EditorApplication.update += Tick;
        }

        void OnDisable() => EditorApplication.update -= Tick;

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.AddToClassList("up-root");
            var ussGuid = AssetDatabase.FindAssets("UdonProfilerWindow t:StyleSheet").FirstOrDefault();
            var uss = ussGuid == null ? null : AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(ussGuid));
            if (uss != null) root.styleSheets.Add(uss);

            root.Add(BuildToolbar());
            root.Add(BuildTiles());

            chart = new UdonProfilerChart { Scale = questScale };
            chart.FrameClicked += f => { selectedFrame = f; SetRange(RangeMode.Selected); };
            chart.RangeSelected += (a, b) => { customFrom = a; customTo = b; SetRange(RangeMode.Custom); };
            root.Add(chart);

            var fold = new Foldout { text = "Frame timeline", value = EditorPrefs.GetBool(Prefs + "Timeline", true) };
            fold.AddToClassList("up-timeline-fold");
            fold.RegisterValueChangedCallback(e => { if (e.target == fold) { EditorPrefs.SetBool(Prefs + "Timeline", e.newValue); Touch(); } });
            timeline = new UdonProfilerTimeline();
            timeline.BarClicked += (b, entry) => JumpTo(b, entry, 0, false);
            fold.Add(timeline);
            root.Add(fold);

            var split = new TwoPaneSplitView(1, 320, TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("up-split");
            split.Add(BuildTree());
            detail = new ScrollView();
            detail.AddToClassList("up-detail");
            split.Add(detail);
            root.Add(split);

            var footer = new VisualElement();
            footer.AddToClassList("up-footer");
            footer.Add(footRange = new Label());
            footer.Add(footStatus = new Label());
            footer.Add(new Label("Overhead: 2 timestamps/event + 1/extern while recording"));
            root.Add(footer);

            Touch();
        }

        VisualElement BuildToolbar()
        {
            var tb = new Toolbar();
            tb.AddToClassList("up-toolbar");

            var rec = recToggle = new ToolbarToggle { text = "Record", value = UdonProfilerRecorder.Recording };
            rec.AddToClassList("up-record");
            rec.RegisterValueChangedCallback(e => { UdonProfilerRecorder.Recording = e.newValue; EditorPrefs.SetBool(Prefs + "Record", e.newValue); if (e.newValue) { chart.AutoScroll = true; UdonProfilerRecorder.PausedOnFrame = -1; lastPausedOn = -1; } Touch(); });
            tb.Add(rec);

            tb.Add(new ToolbarButton(() =>
            {
                UdonProfilerRecorder.Clear();
                selectedFrame = customFrom = customTo = -1;
                if (rangeMode == RangeMode.Custom || rangeMode == RangeMode.Selected) SetRange(RangeMode.Last60); else Touch();
            }) { text = "Clear" });
            tb.Add(new ToolbarSpacer());

            var group = new DropdownField(Enum.GetNames(typeof(GroupMode)).ToList(), (int)groupMode);
            group.RegisterValueChangedCallback(_ => { groupMode = (GroupMode)group.index; EditorPrefs.SetInt(Prefs + "GroupMode", group.index); Touch(); });
            tb.Add(group);

            rangeDropdown = new DropdownField(RangeNames.ToList(), (int)rangeMode);
            rangeDropdown.RegisterValueChangedCallback(_ => { if (rangeDropdown.index >= 0) SetRange((RangeMode)rangeDropdown.index); });
            tb.Add(rangeDropdown);

            var loop = new ToolbarToggle { text = "Loop events only", value = loopOnly };
            loop.RegisterValueChangedCallback(e => { loopOnly = e.newValue; EditorPrefs.SetBool(Prefs + "LoopOnly", e.newValue); Touch(); });
            tb.Add(loop);

            var sf = new ToolbarSearchField();
            sf.AddToClassList("up-search");
            sf.RegisterValueChangedCallback(e => { search = e.newValue ?? ""; Touch(); });
            tb.Add(sf);
            tb.Add(new ToolbarSpacer { flex = true });

            var ext = new ToolbarToggle { text = "Extern timing", value = UdonProfilerRecorder.ExternTiming };
            ext.RegisterValueChangedCallback(e => { UdonProfilerRecorder.ExternTiming = e.newValue; EditorPrefs.SetBool(Prefs + "ExternTiming", e.newValue); });
            tb.Add(ext);

            var pause = new FloatField("Pause >") { value = (float)UdonProfilerRecorder.PauseAboveMs, tooltip = "Auto-pause recording when a frame's Udon ms exceeds this (0 = off)" };
            pause.AddToClassList("up-scale");
            pause.labelElement.style.minWidth = 0;
            pause.RegisterValueChangedCallback(e => { float v = Mathf.Max(0f, e.newValue); UdonProfilerRecorder.PauseAboveMs = v; EditorPrefs.SetFloat(Prefs + "PauseAbove", v); chart.MarkDirtyRepaint(); });
            tb.Add(pause);

            tb.Add(new ToolbarButton(() => { UdonProfilerModel.SetBaseline(view, groupMode); Touch(); }) { text = "Set baseline" });
            tb.Add(new ToolbarButton(() => { UdonProfilerModel.Baseline = null; Touch(); }) { text = "Clear baseline" });
            tb.Add(new ToolbarButton(() =>
            {
                if (view == null) return;
                var path = EditorUtility.SaveFilePanel("Export Udon profile", "", "udon-profile.csv", "csv");
                if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, UdonProfilerModel.ToCsv(view));
            }) { text = "Export CSV" });

            var scale = new FloatField("Quest x") { value = questScale, tooltip = "Editor Mono vs Quest IL2CPP multiplier; affects budget colouring only" };
            scale.AddToClassList("up-scale");
            scale.labelElement.style.minWidth = 0;
            scale.RegisterValueChangedCallback(e => { questScale = Mathf.Max(0.01f, e.newValue); EditorPrefs.SetFloat(Prefs + "QuestScale", questScale); chart.Scale = questScale; Touch(); });
            tb.Add(scale);
            return tb;
        }

        VisualElement BuildTiles()
        {
            var row = new VisualElement();
            row.AddToClassList("up-tiles");
            string[] names = { "Udon ms / frame", "Events / frame", "Externs / frame", "Active behaviours" };
            for (int i = 0; i < 4; i++)
            {
                var t = new VisualElement();
                t.AddToClassList("up-tile");
                t.Add(tileValues[i] = new Label("-"));
                tileValues[i].AddToClassList("up-tile-value");
                t.Add(tileSubs[i] = new Label(names[i]));
                tileSubs[i].AddToClassList("up-tile-label");
                row.Add(t);
                if (i == 0) tileMs = t;
            }
            return row;
        }

        VisualElement BuildTree()
        {
            var cols = new Columns();
            cols.Add(NameColumn());
            cols.Add(NumColumn("pct", "%", 52, n => view == null || view.avgMs <= 0 ? "" : (n.totalMs / Math.Max(1, view.frameCount) / view.avgMs * 100).ToString("0.0")));
            cols.Add(NumColumn("total", "Total ms", 70, n => n.totalMs.ToString("0.00")));
            cols.Add(NumColumn("self", "Self ms", 70, n => n.selfMs.ToString("0.00")));
            cols.Add(NumColumn("calls", "Calls", 56, n => n.calls.ToString()));
            cols.Add(NumColumn("externs", "Externs", 60, n => n.externCalls.ToString()));
            cols.Add(NumColumn("avg", "Avg ms", 64, n => n.calls == 0 ? "" : (n.totalMs / n.calls).ToString("0.000")));
            cols.Add(NumColumn("max", "Max ms", 64, n => n.maxMs.ToString("0.000")));
            cols.Add(NumColumn("extern", "Extern ms", 70, n => n.externMs.ToString("0.00")));
            deltaColumn = NumColumn("delta", "Delta", 60, n => double.IsNaN(n.baselineMs) ? "new" : n.baselineMs <= 0 ? "" : FormatDelta(Delta(n)));
            deltaColumn.visible = UdonProfilerModel.Baseline != null;
            var bind = deltaColumn.bindCell;
            deltaColumn.bindCell = (e, i) =>
            {
                bind(e, i);
                var n = tree.GetItemDataForIndex<Node>(i);
                double d = double.IsNaN(n.baselineMs) || n.baselineMs <= 0 ? 0 : Delta(n);
                e.EnableInClassList("up-delta-good", d <= -5);
                e.EnableInClassList("up-delta-bad", d >= 5);
            };
            cols.Add(deltaColumn);

            tree = new MultiColumnTreeView(cols) { sortingEnabled = true, selectionType = SelectionType.Single };
            tree.AddToClassList("up-tree");
            tree.columnSortingChanged += () =>
            {
                var s = tree.sortedColumns.FirstOrDefault();
                sortColumn = s?.columnName ?? "pct";
                sortAsc = s != null && s.direction == SortDirection.Ascending;
                Populate();
            };
            tree.selectionChanged += _ => ShowDetail();
            tree.itemsChosen += items =>
            {
                var n = items.FirstOrDefault() as Node;
                if (n == null || (n.kind != NodeKind.Behaviour && n.kind != NodeKind.Event)) return;
                var ub = UdonProfilerRecorder.GetInfo(n.behaviourId)?.behaviour;
                if (ub == null) return;
                var go = ub.gameObject;
                EditorGUIUtility.PingObject(go);
                Selection.activeGameObject = go;
            };
            return tree;
        }

        Column NameColumn()
        {
            return new Column
            {
                name = "name", title = "Name", stretchable = true, minWidth = 200, sortable = true,
                makeCell = () =>
                {
                    var c = new VisualElement();
                    c.AddToClassList("up-cell-name");
                    var k = new Label(); k.AddToClassList("up-kind"); c.Add(k);
                    c.Add(new Label());
                    var b = new Label("halted"); b.AddToClassList("up-badge"); c.Add(b);
                    return c;
                },
                bindCell = (e, i) =>
                {
                    var n = tree.GetItemDataForIndex<Node>(i);
                    ((Label)e[0]).text = n.kind == NodeKind.Group ? "G" : n.kind == NodeKind.Behaviour ? "B" : n.kind == NodeKind.Event ? "E" : "X";
                    ((Label)e[1]).text = n.name;
                    e[2].style.display = n.halted ? DisplayStyle.Flex : DisplayStyle.None;
                }
            };
        }

        static double Delta(Node n) => (n.totalMs - n.baselineMs) / n.baselineMs * 100;
        static string FormatDelta(double d) => (d < 0 ? "-" : "+") + Math.Abs(d).ToString("0.0") + "%";
        static double DeltaKey(Node n) => double.IsNaN(n.baselineMs) ? double.MaxValue : n.baselineMs <= 0 ? 0 : Delta(n);

        Column NumColumn(string id, string title, int width, Func<Node, string> fmt)
        {
            return new Column
            {
                name = id, title = title, width = width, sortable = true,
                makeCell = () => { var l = new Label(); l.AddToClassList("up-cell"); return l; },
                bindCell = (e, i) => ((Label)e).text = fmt(tree.GetItemDataForIndex<Node>(i))
            };
        }

        void SetRange(RangeMode m)
        {
            rangeMode = m;
            if (m != RangeMode.Custom) EditorPrefs.SetInt(Prefs + "Range", (int)m);
            if (rangeDropdown != null && rangeDropdown.index != (int)m) rangeDropdown.SetValueWithoutNotify(RangeNames[(int)m]);
            Touch();
        }

        void Touch() { dirty = true; Rebuild(); }

        void Tick()
        {
            if (tree == null) return;
            int paused = UdonProfilerRecorder.PausedOnFrame;
            if (paused >= 0 && (paused != lastPausedOn || (!UdonProfilerRecorder.Recording && recToggle.value)))
            {
                lastPausedOn = paused;
                recToggle.SetValueWithoutNotify(false);
                selectedFrame = paused;
                SetRange(RangeMode.Selected);
                return;
            }
            if (!dirty && UdonProfilerRecorder.Version == lastVersion) return;
            if (EditorApplication.timeSinceStartup - lastRebuild < 0.1) return;
            Rebuild();
        }

        void Rebuild()
        {
            if (tree == null) return;
            lastRebuild = EditorApplication.timeSinceStartup;
            lastVersion = UdonProfilerRecorder.Version;
            dirty = false;

            int newest = UdonProfilerRecorder.NewestFrame;
            int oldest = newest - UdonProfilerRecorder.FrameCount + 1;
            int from, to;
            if (selectedFrame >= 0 && selectedFrame < oldest) selectedFrame = -1;
            switch (rangeMode)
            {
                case RangeMode.Selected: from = to = selectedFrame >= 0 ? selectedFrame : newest; break;
                case RangeMode.Last60: from = newest - 59; to = newest; break;
                case RangeMode.Last300: from = newest - 299; to = newest; break;
                case RangeMode.Custom: from = customFrom; to = customTo; break;
                default: from = oldest; to = newest; break;
            }
            from = Mathf.Clamp(from, oldest, newest);
            to = Mathf.Clamp(to, from, newest);

            view = UdonProfilerModel.Build(from, to, groupMode, loopOnly, search);
            var full = from == oldest && to == newest && search.Length == 0 ? view : UdonProfilerModel.Build(oldest, newest, groupMode, loopOnly, "");
            chart.SetData(full);
            bool single = rangeMode == RangeMode.Selected;
            if (!chart.Dragging) chart.SetSelection(single ? from : -1, single ? -1 : from, single ? -1 : to);

            double scaledAvg = view.avgMs * questScale;
            tileMs.RemoveFromClassList("up-ok"); tileMs.RemoveFromClassList("up-warn"); tileMs.RemoveFromClassList("up-bad");
            tileMs.AddToClassList(scaledAvg < 2 ? "up-ok" : scaledAvg < 5 ? "up-warn" : "up-bad");
            tileValues[0].text = view.avgMs.ToString("0.00");
            tileSubs[0].text = $"Udon ms / frame | max {view.maxMs:0.00}";
            tileValues[1].text = view.avgEvents.ToString();
            tileValues[2].text = view.avgExterns.ToString();
            tileValues[3].text = view.activeBehaviours.ToString();

            footRange.text = view.frameCount == 0 ? "No frames" : $"Frames {FrameNo(full, view.fromFrame)}-{FrameNo(full, view.toFrame)} ({view.frameCount})";
            int pausedOn = UdonProfilerRecorder.PausedOnFrame;
            footStatus.text = (UdonProfilerRecorder.Recording ? (EditorApplication.isPlaying ? "Recording" : "Armed (enter play mode)") : pausedOn >= 0 ? $"paused on spike frame {FrameNo(full, pausedOn)}" : "Paused") + VmStatus();

            bool hadDelta = deltaColumn.visible;
            deltaColumn.visible = UdonProfilerModel.Baseline != null;
            if (hadDelta && !deltaColumn.visible && sortColumn == "delta") { sortColumn = "pct"; sortAsc = false; }

            if (EditorPrefs.GetBool(Prefs + "Timeline", true))
            {
                int tf = rangeMode == RangeMode.Selected ? from : newest;
                timeline.SetFrame(tf, FrameNo(full, tf));
            }

            Populate();
            ShowDetail();
        }

        static int FrameNo(ProfileView v, int ring)
        {
            int i = ring - v.fromFrame;
            return v.frameNumbers != null && i >= 0 && i < v.frameNumbers.Length ? v.frameNumbers[i] : ring;
        }

        void Populate()
        {
            var expanded = new HashSet<int>();
            if (tree.viewController != null)
                foreach (var id in tree.viewController.GetAllItemIds())
                    if (tree.viewController.IsExpanded(id)) expanded.Add(id);
            bool hasSelected = tree.selectedItem is Node;
            int selected = tree.selectedItem is Node sel ? sel.id : 0;

            tree.SetRootItems(Sorted(view.roots).Select(Item).ToList());
            foreach (var id in expanded) tree.ExpandItem(id, false);
            if (hasSelected && FindById(selected) != null) tree.SetSelectionByIdWithoutNotify(new[] { selected });
            else tree.ClearSelection();
            tree.RefreshItems();
        }

        TreeViewItemData<Node> Item(Node n)
        {
            List<TreeViewItemData<Node>> children = null;
            if (n.children != null && n.children.Count > 0)
            {
                children = new List<TreeViewItemData<Node>>(n.children.Count);
                foreach (var c in Sorted(n.children)) children.Add(Item(c));
            }
            return new TreeViewItemData<Node>(n.id, n, children);
        }

        IEnumerable<Node> Sorted(List<Node> nodes)
        {
            Func<Node, double> key = sortColumn switch
            {
                "name" => null,
                "self" => n => n.selfMs,
                "calls" => n => n.calls,
                "externs" => n => n.externCalls,
                "avg" => n => n.calls == 0 ? 0 : n.totalMs / n.calls,
                "max" => n => n.maxMs,
                "extern" => n => n.externMs,
                "delta" => DeltaKey,
                _ => n => n.totalMs
            };
            if (key == null)
                return sortAsc ? nodes.OrderBy(n => n.name, StringComparer.OrdinalIgnoreCase) : nodes.OrderByDescending(n => n.name, StringComparer.OrdinalIgnoreCase);
            return sortAsc ? nodes.OrderBy(key) : nodes.OrderByDescending(key);
        }

        void ShowDetail()
        {
            detail.Clear();
            if (!(tree.selectedItem is Node n))
            {
                detail.Add(Empty("Select a row"));
                return;
            }
            var info = n.kind == NodeKind.Group ? null : UdonProfilerRecorder.GetInfo(n.behaviourId);
            var title = new Label(n.name); title.AddToClassList("up-detail-title"); detail.Add(title);
            var sub = new Label(); sub.AddToClassList("up-detail-sub"); detail.Add(sub);
            switch (n.kind)
            {
                case NodeKind.Group: sub.text = $"{groupMode} group | {n.instances} instance{(n.instances == 1 ? "" : "s")}"; break;
                case NodeKind.Behaviour: sub.text = $"{info?.script}  |  {info?.objectPath}"; break;
                case NodeKind.Event: sub.text = $"{info?.script}  |  {info?.objectPath}  |  {UdonProfilerRecorder.EventName(n.behaviourId, n.entry)}"; break;
                case NodeKind.Extern: sub.text = n.sig; break;
            }
            if (n.halted) { var h = new Label("halted"); h.AddToClassList("up-badge"); h.style.alignSelf = Align.FlexStart; detail.Add(h); }
            detail.Add(new Label($"Total {n.totalMs:0.00} ms | Self {n.selfMs:0.00} ms | Extern {n.externMs:0.00} ms | {n.calls} calls"));

            if (n.kind == NodeKind.Group)
            {
                Section("Instances");
                var inst = view.InstancesOf(n);
                if (inst == null || inst.Count == 0) detail.Add(Empty("none"));
                else foreach (var b in inst)
                {
                    var bi = UdonProfilerRecorder.GetInfo(b.behaviourId);
                    Row(bi?.objectPath ?? b.name, b.calls.ToString(), b.totalMs.ToString("0.00"), true, b.behaviourId, b.entry, b.id, true);
                }
            }

            if (n.kind == NodeKind.Behaviour || n.kind == NodeKind.Event)
            {
                Section("Called by");
                var into = view.EdgesInto(n.behaviourId, n.entry);
                if (into == null || into.Count == 0) detail.Add(Empty("none"));
                else foreach (var e in into) Row(EdgeLabel(e.fromBehaviour, e.fromEntry, e.delayed), e.count.ToString(), e.ms.ToString("0.00"), e.fromBehaviour != 0, e.fromBehaviour, e.fromEntry, 0, false);

                Section("Calls");
                var outOf = view.EdgesOutOf(n.behaviourId, n.entry);
                if (outOf == null || outOf.Count == 0) detail.Add(Empty("none"));
                else foreach (var e in outOf) Row(EdgeLabel(e.toBehaviour, e.toEntry, e.delayed), e.count.ToString(), e.ms.ToString("0.00"), e.toBehaviour != 0, e.toBehaviour, e.toEntry, 0, false);
            }

            if (n.kind != NodeKind.Extern)
            {
                Section("Externs");
                var ex = view.ExternsOf(n);
                if (ex == null || ex.Count == 0) detail.Add(Empty("none"));
                else foreach (var r in ex) Row(r.sig, r.count.ToString(), r.ms.ToString("0.00"), false, 0, 0, 0, false);
            }
        }

        static readonly Type VmStats = Type.GetType("MagmaVRC.UdonVM.FastUdonVMStats, MagmaVRC.UdonVM.Editor");

        static string VmStatus()
        {
            if (VmStats == null || !EditorApplication.isPlaying) return "";
            bool enabled = (bool)VmStats.GetField("Enabled").GetValue(null);
            if (!enabled) return "  |  VM: stock";
            long fused = (long)VmStats.GetField("FusedHits").GetValue(null);
            return $"  |  VM: fast ({fused:N0} fused)";
        }

        static string EdgeLabel(int behaviourId, uint entry, bool delayed)
        {
            var i = UdonProfilerRecorder.GetInfo(behaviourId);
            return (delayed ? "(delayed) " : "") + (i?.script ?? "?") + " | " + (i?.objectName ?? "?") + " | " + UdonProfilerRecorder.EventName(behaviourId, entry);
        }

        void Section(string t) { var l = new Label(t); l.AddToClassList("up-section"); detail.Add(l); }
        static Label Empty(string t) { var l = new Label(t); l.AddToClassList("up-empty"); return l; }

        void Row(string name, string count, string ms, bool clickable, int behaviourId, uint entry, int nodeId, bool byNodeId)
        {
            var r = new VisualElement();
            r.AddToClassList("up-row");
            var nl = new Label(name) { tooltip = name }; nl.AddToClassList("up-row-name"); r.Add(nl);
            var cl = new Label(count); cl.AddToClassList("up-row-num"); r.Add(cl);
            var ml = new Label(ms); ml.AddToClassList("up-row-num"); r.Add(ml);
            if (clickable)
                r.RegisterCallback<ClickEvent>(_ => JumpTo(behaviourId, entry, nodeId, byNodeId));
            detail.Add(r);
        }

        void JumpTo(int behaviourId, uint entry, int nodeId, bool byNodeId)
        {
            var path = new List<int>();
            Node target = null;
            foreach (var root in view.roots)
                if (Find(root, behaviourId, entry, nodeId, byNodeId, path, out target)) break;
            if (target == null) return;
            for (int i = 0; i < path.Count - 1; i++) tree.ExpandItem(path[i], false);
            tree.SetSelectionById(target.id);
            tree.ScrollToItemById(target.id);
        }

        Node FindById(int id)
        {
            var path = new List<int>();
            foreach (var root in view.roots)
                if (Find(root, 0, 0, id, true, path, out var n)) return n;
            return null;
        }

        static bool Find(Node n, int behaviourId, uint entry, int nodeId, bool byNodeId, List<int> path, out Node found)
        {
            path.Add(n.id);
            found = null;
            bool match = byNodeId ? n.id == nodeId
                : n.behaviourId == behaviourId && (n.kind == NodeKind.Event ? n.entry == entry : n.kind == NodeKind.Behaviour && (n.entry == entry || entry == 0));
            if (match) { found = n; return true; }
            if (n.children != null)
                foreach (var c in n.children)
                    if (Find(c, behaviourId, entry, nodeId, byNodeId, path, out found)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }
}
