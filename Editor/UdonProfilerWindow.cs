using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace MagmaVRC.UdonProfiler
{
    /// <summary>Editor window that shows recorded Udon events, internal methods and externs.</summary>
    public sealed class UdonProfilerWindow : EditorWindow
    {
        enum RangeMode { Selected, Last60, Last300, All, Custom }
        enum ViewKind { Tree, Flame, Network }

        const string Prefs = "MagmaVRC.UdonProfiler.";
        static readonly string[] RangeNames = { "Selected frame", "Last 60", "Last 300", "All", "Custom" };
        static readonly int[] StatCaps = { 1024, 2048, 4096, 8192 };
        static readonly Type VmStats = Type.GetType("MagmaVRC.UdonVM.FastUdonVMStats, MagmaVRC.UdonVM.Editor");
        static readonly FieldInfo VmEnabled = VmStats?.GetField("Enabled", BindingFlags.Public | BindingFlags.Static);
        static readonly FieldInfo VmFusedHits = VmStats?.GetField("FusedHits", BindingFlags.Public | BindingFlags.Static);

        [MenuItem("MagmaVRC/Udon Profiler")]
        static void Open() => GetWindow<UdonProfilerWindow>("Udon Profiler");

        GroupMode groupMode;
        RangeMode rangeMode;
        ViewKind viewKind;
        bool loopOnly, hideTiny, timelineOpen;
        float questScale = 4f;
        string search = "";
        int selectedFrame = -1, customFrom = -1, customTo = -1;
        int lastTo = -1;
        string rangeNote;

        ProfileView view;
        readonly UdonProfilerModel model = new UdonProfilerModel();
        int lastVersion = -1, lastGeneration = -1;
        bool dirty = true, pendingUserChange = true, membershipDirty = true;
        double lastRebuild, lastMembershipTime, lastSortTime;
        string sortColumn = "pct";
        bool sortAsc;

        bool populating, orderStale;
        int lastStructure = int.MinValue, lastOrderHash, lastFullHash;
        string lastSortKey;
        readonly HashSet<int> expanded = new HashSet<int>();
        readonly HashSet<int> itemIds = new HashSet<int>();
        readonly List<int> expandScratch = new List<int>();
        readonly List<Node> nodeScratch = new List<Node>();
        Comparison<Node> comparison;
        string comparisonKey;

        UdonProfilerChart chart;
        UdonProfilerTimeline timeline;
        UdonProfilerFlameGraph flame;
        UdonProfilerDetailPane detailPane;
        ScrollView flameScroll;
        ToolbarToggle recToggle, treeToggle, flameToggle, netToggle;
        VisualElement netPane;
        MultiColumnListView netList;
        readonly List<NetRow> netItems = new List<NetRow>();
        string netSortColumn = "bps";
        bool netSortAsc, keepMethodsChangedInPlay;
        Column deltaColumn, fusedColumn, instrColumn, selfInstrColumn;
        bool flameByInstr;
        int lastPausedOn = -1;
        int lastBuiltFrom = -1, lastBuiltTo = -1;
        bool lastBuiltSealed;
        MultiColumnTreeView tree;
        Label footRange, footStatus, footOverhead, timelineFrame, timelineNote;
        readonly Label[] tileValues = new Label[6];
        readonly Label[] tileSubs = new Label[6];
        VisualElement tileMs, tileInstr, tileNet;
        DropdownField rangeDropdown;

        void OnEnable()
        {
            groupMode = (GroupMode)EditorPrefs.GetInt(Prefs + "GroupMode", 0);
            rangeMode = (RangeMode)EditorPrefs.GetInt(Prefs + "Range", (int)RangeMode.Last60);
            if (rangeMode == RangeMode.Custom) rangeMode = RangeMode.Last60;
            viewKind = (ViewKind)EditorPrefs.GetInt(Prefs + "View", (int)ViewKind.Tree);
            loopOnly = EditorPrefs.GetBool(Prefs + "LoopOnly", false);
            hideTiny = EditorPrefs.GetBool(Prefs + "HideTiny", false);
            timelineOpen = EditorPrefs.GetBool(Prefs + "Timeline", true);
            questScale = EditorPrefs.GetFloat(Prefs + "QuestScale", 4f);
            flameByInstr = EditorPrefs.GetBool(Prefs + "FlameByInstr", false);
            if (!UdonProfilerRecorder.IsLoadedCapture && UdonProfilerRecorder.PausedOnFrame < 0)
                UdonProfilerRecorder.Recording = EditorPrefs.GetBool(Prefs + "Record", true);
            UdonProfilerRecorder.ExternTiming = EditorPrefs.GetBool(Prefs + "ExternTiming", true);
            UdonProfilerRecorder.DetailedExterns = EditorPrefs.GetBool(Prefs + "DetailedExterns", true);
            UdonProfilerRecorder.EmitMarkers = EditorPrefs.GetBool(Prefs + "EmitMarkers", true);
            UdonProfilerRecorder.MethodTiming = EditorPrefs.GetBool(Prefs + "MethodTiming", true);
            UdonProfilerRecorder.MethodSpans = EditorPrefs.GetBool(Prefs + "MethodSpans", true);
            UdonProfilerRecorder.CountFusedExterns = EditorPrefs.GetBool(Prefs + "CountFusedExterns", true);
            UdonProfilerRecorder.TrackAlloc = EditorPrefs.GetBool(Prefs + "TrackAlloc", true);
            UdonProfilerRecorder.MaxStatsPerFrame = EditorPrefs.GetInt(Prefs + "MaxStatsPerFrame", 4096);
            UdonProfilerRecorder.PauseAboveMs = EditorPrefs.GetFloat(Prefs + "PauseAbove", 0f);
            lastPausedOn = UdonProfilerRecorder.PausedOnFrame;
            UdonProfilerRecorder.Viewers++;
            UdonProfilerRecorder.SyncProbes();
            EditorApplication.update += Tick;
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            UdonProfilerRecorder.Viewers = Math.Max(0, UdonProfilerRecorder.Viewers - 1);
            UdonProfilerRecorder.SyncProbes();
        }

        bool visibleTab = true;
        void OnBecameVisible() { visibleTab = true; dirty = true; }
        void OnBecameInvisible() => visibleTab = false;

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.AddToClassList("up-root");
            var guids = AssetDatabase.FindAssets("UdonProfilerWindow t:StyleSheet");
            var uss = guids.Length == 0 ? null : AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
            if (uss != null) root.styleSheets.Add(uss);

            chart = new UdonProfilerChart { Scale = questScale, LoopOnly = loopOnly };
            chart.FrameClicked += f => { selectedFrame = f; SetRange(RangeMode.Selected); RebuildNow(); };
            chart.RangeSelected += (a, b) => { customFrom = a; customTo = b; SetRange(RangeMode.Custom); RebuildNow(); };
            chart.StepRequested += Step;

            root.Add(BuildToolbar());
            root.Add(BuildTiles());
            root.Add(chart);

            var fold = new Foldout { text = "Frame timeline", value = timelineOpen };
            fold.AddToClassList("up-timeline-fold");
            fold.RegisterValueChangedCallback(e =>
            {
                if (e.target != fold) return;
                timelineOpen = e.newValue;
                EditorPrefs.SetBool(Prefs + "Timeline", e.newValue);
                if (timelineOpen) { pendingUserChange = true; Touch(); }
            });
            var head = new VisualElement();
            head.AddToClassList("up-timeline-head");
            head.Add(new Button(() => Step(-1)) { text = "<" });
            head.Add(timelineFrame = new Label());
            head.Add(new Button(() => Step(1)) { text = ">" });
            head.Add(new Button(() => timeline.Fit()) { text = "Fit" });
            head.Add(timelineNote = new Label());
            fold.Add(head);
            timeline = new UdonProfilerTimeline();
            timeline.BarClicked += item => { var n = view?.FindByPath(item.path); if (n != null) JumpTo(n); };
            timeline.BarDoubleClicked += item =>
            {
                var src = item.kind == PathKind.Method ? UdonProfilerRecorder.Method(item.behaviourId, item.key) : EventSource(item.behaviourId, item.key);
                OpenSource(UdonProfilerRecorder.GetInfo(item.behaviourId)?.scriptAsset, src);
            };
            timeline.StepRequested += Step;
            fold.Add(timeline);
            root.Add(fold);

            var split = new TwoPaneSplitView(1, 320, TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("up-split");
            var left = new VisualElement();
            left.style.flexGrow = 1;
            left.Add(BuildTree());
            flameScroll = new ScrollView(ScrollViewMode.Vertical);
            flameScroll.AddToClassList("up-flame");
            flame = new UdonProfilerFlameGraph();
            flame.NodeSelected += i => detailPane.ShowFlame(flame.Data, i);
            flameScroll.Add(flame);
            left.Add(flameScroll);
            left.Add(BuildNetPane());
            split.Add(left);
            detailPane = new UdonProfilerDetailPane();
            detailPane.JumpRequested += JumpTo;
            split.Add(detailPane);
            root.Add(split);

            var footer = new VisualElement();
            footer.AddToClassList("up-footer");
            footer.Add(footRange = new Label());
            footer.Add(footStatus = new Label());
            footer.Add(footOverhead = new Label());
            root.Add(footer);

            ApplyViewKind();
            Rebuild();
        }

        VisualElement BuildToolbar()
        {
            var tb = new Toolbar();
            tb.AddToClassList("up-toolbar");

            var rec = recToggle = new ToolbarToggle { text = "Record", value = UdonProfilerRecorder.Recording };
            rec.AddToClassList("up-record");
            rec.RegisterValueChangedCallback(e =>
            {
                if (e.newValue && UdonProfilerRecorder.IsLoadedCapture)
                {
                    UdonProfilerRecorder.Clear();
                    selectedFrame = customFrom = customTo = -1;
                    if (rangeMode == RangeMode.Custom || rangeMode == RangeMode.Selected) ApplyRangeMode(RangeMode.Last60);
                }
                UdonProfilerRecorder.Recording = e.newValue;
                EditorPrefs.SetBool(Prefs + "Record", e.newValue);
                if (e.newValue)
                {
                    chart.AutoScroll = true;
                    UdonProfilerRecorder.PausedOnFrame = -1;
                    lastPausedOn = -1;
                }
                membershipDirty = true;
                Touch();
            });
            tb.Add(rec);

            tb.Add(new ToolbarButton(() =>
            {
                UdonProfilerRecorder.Clear();
                selectedFrame = customFrom = customTo = -1;
                if (rangeMode == RangeMode.Custom || rangeMode == RangeMode.Selected) SetRange(RangeMode.Last60);
                else { membershipDirty = true; pendingUserChange = true; Touch(); }
            }) { text = "Clear" });
            tb.Add(new ToolbarSpacer());

            treeToggle = new ToolbarToggle { text = "Tree", value = viewKind == ViewKind.Tree };
            treeToggle.AddToClassList("up-view-toggle");
            treeToggle.RegisterValueChangedCallback(e => { if (e.newValue) SetViewKind(ViewKind.Tree); else ApplyViewKind(); });
            tb.Add(treeToggle);
            flameToggle = new ToolbarToggle { text = "Flame", value = viewKind == ViewKind.Flame };
            flameToggle.AddToClassList("up-view-toggle");
            flameToggle.RegisterValueChangedCallback(e => { if (e.newValue) SetViewKind(ViewKind.Flame); else ApplyViewKind(); });
            tb.Add(flameToggle);
            netToggle = new ToolbarToggle { text = "Network", value = viewKind == ViewKind.Network, tooltip = "Per-behaviour sync and network event estimates (ClientSim sends nothing)" };
            netToggle.AddToClassList("up-view-toggle");
            netToggle.RegisterValueChangedCallback(e => { if (e.newValue) SetViewKind(ViewKind.Network); else ApplyViewKind(); });
            tb.Add(netToggle);
            tb.Add(new ToolbarSpacer());

            var group = new DropdownField(new List<string>(Enum.GetNames(typeof(GroupMode))), (int)groupMode);
            group.RegisterValueChangedCallback(_ =>
            {
                if (group.index < 0) return;
                groupMode = (GroupMode)group.index;
                EditorPrefs.SetInt(Prefs + "GroupMode", group.index);
                membershipDirty = true;
                Touch();
            });
            tb.Add(group);

            rangeDropdown = new DropdownField(new List<string>(RangeNames), (int)rangeMode);
            rangeDropdown.RegisterValueChangedCallback(_ => { if (rangeDropdown.index >= 0) SetRange((RangeMode)rangeDropdown.index); });
            tb.Add(rangeDropdown);

            var loop = new ToolbarToggle { text = "Loop events only", value = loopOnly };
            loop.RegisterValueChangedCallback(e =>
            {
                loopOnly = e.newValue;
                EditorPrefs.SetBool(Prefs + "LoopOnly", e.newValue);
                membershipDirty = true;
                Touch();
            });
            tb.Add(loop);

            var sf = new ToolbarSearchField();
            sf.AddToClassList("up-search");
            sf.RegisterValueChangedCallback(e => { search = e.newValue ?? ""; membershipDirty = true; Touch(); });
            tb.Add(sf);
            tb.Add(new ToolbarSpacer { flex = true });

            var opts = new ToolbarMenu { text = "Options" };
            void Opt(string label, string pref, Func<bool> get, Action<bool> set) =>
                opts.menu.AppendAction(label,
                    _ => { bool v = !get(); set(v); EditorPrefs.SetBool(Prefs + pref, v); },
                    _ => get() ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            Opt("Extern timing", "ExternTiming", () => UdonProfilerRecorder.ExternTiming, v => UdonProfilerRecorder.ExternTiming = v);
            Opt("Extern breakdown (per signature)", "DetailedExterns", () => UdonProfilerRecorder.DetailedExterns, v => UdonProfilerRecorder.DetailedExterns = v);
            Opt("Method timing (internal calls)", "MethodTiming", () => UdonProfilerRecorder.MethodTiming, v => UdonProfilerRecorder.MethodTiming = v);
            Opt("Method spans in timeline", "MethodSpans", () => UdonProfilerRecorder.MethodSpans, v => UdonProfilerRecorder.MethodSpans = v);
            Opt("Count fused externs (Fast VM)", "CountFusedExterns", () => UdonProfilerRecorder.CountFusedExterns, v => UdonProfilerRecorder.CountFusedExterns = v);
            Opt("GC alloc tracking", "TrackAlloc", () => UdonProfilerRecorder.TrackAlloc, v => UdonProfilerRecorder.TrackAlloc = v);
            Opt("Unity Profiler markers", "EmitMarkers", () => UdonProfilerRecorder.EmitMarkers, v => UdonProfilerRecorder.EmitMarkers = v);
            opts.menu.AppendSeparator();
            Opt("Hide rows under 0.1%", "HideTiny", () => hideTiny, v => { hideTiny = v; membershipDirty = true; Touch(); });
            Opt("Flame graph sized by instructions (Fast VM)", "FlameByInstr", () => flameByInstr, v => { flameByInstr = v; Touch(); });
            foreach (int cap in StatCaps)
                opts.menu.AppendAction("Stats per frame/" + cap,
                    _ => { UdonProfilerRecorder.MaxStatsPerFrame = cap; EditorPrefs.SetInt(Prefs + "MaxStatsPerFrame", cap); },
                    _ => UdonProfilerRecorder.MaxStatsPerFrame == cap ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            foreach (float rate in new[] { 5f, 10f, 20f })
                opts.menu.AppendAction("Continuous send rate (network estimate)/" + rate.ToString("0", CultureInfo.InvariantCulture) + " Hz",
                    _ => { UdonProfilerRecorder.ContinuousSendRate = rate; EditorPrefs.SetFloat(Prefs + "ContinuousRate", rate); },
                    _ => Mathf.Approximately(UdonProfilerRecorder.ContinuousSendRate, rate) ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            opts.menu.AppendSeparator();
            Opt("Stock VM hooks (next domain reload)", "StockHooks", () => EditorPrefs.GetBool(Prefs + "StockHooks", true), _ => { });
            if (UdonQuicBridge.Available)
            {
                opts.menu.AppendAction("Keep methods (disable UdonQuic inlining)", _ =>
                {
                    UdonQuicBridge.KeepMethods = !UdonQuicBridge.KeepMethods;
                    keepMethodsChangedInPlay = EditorApplication.isPlaying;
                    ShowNotification(new GUIContent("UdonQuic project setting (Inline + TailCalls passes) " + (UdonQuicBridge.KeepMethods ? "disabled" : "enabled") + "; applies on next play mode entry"), 4);
                    UpdateFooter();
                }, _ => UdonQuicBridge.KeepMethods ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
            opts.tooltip = UdonQuicBridge.Available
                ? "Keep methods changes UdonQuic's own project setting (disabled passes Inline and TailCalls); it applies when programs are re-optimized on the next play mode entry. Without it, inlined methods have no rows."
                : "Recorder and view options";
            tb.Add(opts);

            var pause = new FloatField("Pause >") { value = (float)UdonProfilerRecorder.PauseAboveMs, tooltip = "Auto-pause recording when a frame's Udon ms exceeds this (0 = off)" };
            pause.AddToClassList("up-scale");
            pause.labelElement.style.minWidth = 0;
            pause.RegisterValueChangedCallback(e =>
            {
                float v = Mathf.Max(0f, e.newValue);
                UdonProfilerRecorder.PauseAboveMs = v;
                EditorPrefs.SetFloat(Prefs + "PauseAbove", v);
                chart.Refresh();
            });
            tb.Add(pause);

            tb.Add(new ToolbarButton(() => { UdonProfilerModel.SetBaseline(view, groupMode); Touch(); }) { text = "Set baseline" });
            tb.Add(new ToolbarButton(() => { UdonProfilerModel.Baseline = null; Touch(); }) { text = "Clear baseline" });

            var file = new ToolbarMenu { text = "File" };
            file.menu.AppendAction("Export CSV", _ =>
            {
                if (view == null) return;
                var path = EditorUtility.SaveFilePanel("Export Udon profile", "", "udon-profile.csv", "csv");
                if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, UdonProfilerModel.ToCsv(view));
            }, _ => view == null ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
            file.menu.AppendAction("Export network CSV", _ =>
            {
                if (view?.network == null) return;
                var path = EditorUtility.SaveFilePanel("Export Udon network estimates", "", "udon-network.csv", "csv");
                if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, UdonProfilerModel.NetworkCsv(view.network));
            }, _ => view?.network != null && view.network.HasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            file.menu.AppendAction("Save capture", _ =>
            {
                var path = EditorUtility.SaveFilePanel("Save Udon capture", "", "udon-capture.udonprofile", "udonprofile");
                if (string.IsNullOrEmpty(path)) return;
                try { UdonProfilerCapture.Save(path); }
                catch (Exception ex) { EditorUtility.DisplayDialog("Udon Profiler", "Failed to save capture: " + ex.Message, "OK"); }
            }, _ => UdonProfilerRecorder.FrameCount == 0 ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
            file.menu.AppendAction("Load capture", _ =>
            {
                var path = EditorUtility.OpenFilePanel("Load Udon capture", "", "udonprofile");
                if (string.IsNullOrEmpty(path)) return;
                try { UdonProfilerCapture.Load(path); }
                catch (Exception ex)
                {
                    if (UdonProfilerRecorder.IsLoadedCapture) UdonProfilerRecorder.Clear();
                    UdonProfilerRecorder.Recording = recToggle.value;
                    recToggle.SetValueWithoutNotify(UdonProfilerRecorder.Recording);
                    Touch();
                    EditorUtility.DisplayDialog("Udon Profiler", "Failed to load capture: " + ex.Message, "OK");
                    return;
                }
                recToggle.SetValueWithoutNotify(false);
                EditorPrefs.SetBool(Prefs + "Record", false);
                selectedFrame = customFrom = customTo = -1;
                SetRange(RangeMode.All);
            });
            tb.Add(file);

            var scale = new FloatField("Quest x") { value = questScale, tooltip = "Editor Mono vs Quest IL2CPP multiplier; affects budget colouring only" };
            scale.AddToClassList("up-scale");
            scale.labelElement.style.minWidth = 0;
            scale.RegisterValueChangedCallback(e =>
            {
                questScale = Mathf.Max(0.01f, e.newValue);
                EditorPrefs.SetFloat(Prefs + "QuestScale", questScale);
                chart.Scale = questScale;
                chart.Refresh();
                UpdateTileClasses();
            });
            tb.Add(scale);
            return tb;
        }

        VisualElement BuildTiles()
        {
            var row = new VisualElement();
            row.AddToClassList("up-tiles");
            string[] names = { "Udon ms / frame", "Events / frame", "Externs / frame", "Active behaviours", "Udon instr / frame", "Net bytes/s (est.)" };
            for (int i = 0; i < names.Length; i++)
            {
                var t = new VisualElement();
                t.AddToClassList("up-tile");
                t.Add(tileValues[i] = new Label("-"));
                tileValues[i].AddToClassList("up-tile-value");
                t.Add(tileSubs[i] = new Label(names[i]));
                tileSubs[i].AddToClassList("up-tile-label");
                row.Add(t);
                if (i == 0) tileMs = t;
                if (i == 4)
                {
                    tileInstr = t;
                    t.tooltip = "Udon VM instructions executed per frame (Fast VM, stock-equivalent count); predicts Quest cost better than editor ms";
                    t.style.display = DisplayStyle.None;
                }
                if (i == 5)
                {
                    tileNet = t;
                    t.tooltip = "Estimated bytes per second sent: synced-variable serializations plus network event payloads. ClientSim sends nothing; see the Network view.";
                    t.style.display = DisplayStyle.None;
                }
            }
            return row;
        }

        VisualElement BuildTree()
        {
            var cols = new Columns();
            cols.Add(NameColumn());
            cols.Add(PctColumn());
            var total = NumColumn("total", "Total ms", 70, n => n.totalMs.ToString("0.00"));
            var bindTotal = total.bindCell;
            total.bindCell = (e, i) =>
            {
                bindTotal(e, i);
                var n = tree.GetItemDataForIndex<Node>(i);
                e.tooltip = n != null && n.kind == NodeKind.Extern ? $"avg {Avg(n):0.0000} ms per timed call" : "";
            };
            cols.Add(total);
            cols.Add(NumColumn("self", "Self ms", 70, n => n.selfMs.ToString("0.00")));
            const string instrTip = "Udon VM instructions over the range (Fast VM, stock-equivalent count)";
            instrColumn = NumColumn("instr", "Instr", 64, n => UdonProfilerModel.HasInstructions(view, n) ? UdonProfilerModel.FormatCount(n.instructions) : "", instrTip + ", inclusive");
            instrColumn.visible = false;
            cols.Add(instrColumn);
            selfInstrColumn = NumColumn("selfinstr", "Self instr", 70, n => UdonProfilerModel.HasInstructions(view, n) ? UdonProfilerModel.FormatCount(n.selfInstructions) : "", instrTip + ", excluding called methods and nested events");
            selfInstrColumn.visible = false;
            cols.Add(selfInstrColumn);
            cols.Add(NumColumn("calls", "Calls", 56, n => n.calls.ToString()));
            cols.Add(NumColumn("externs", "Externs", 60, n => n.externCalls.ToString()));
            fusedColumn = NumColumn("fused", "Fused", 56, n => n.fusedCalls.ToString(), "Fused and site-cache externs (Fast VM): counted, not timed");
            fusedColumn.visible = false;
            cols.Add(fusedColumn);
            cols.Add(NumColumn("avg", "Avg ms", 64, n => n.calls == 0 ? "" : Avg(n).ToString("0.000")));
            cols.Add(NumColumn("max", "Max ms", 64, n => n.maxMs.ToString("0.000")));
            cols.Add(NumColumn("p95", "p95 ms", 64, n => double.IsNaN(n.p95Ms) ? "" : n.p95Ms.ToString("0.000"), "approximate (histogram)"));
            cols.Add(NumColumn("extern", "Extern (self) ms", 90, n => n.externMs.ToString("0.00"), "externs run directly in this row; Event rows: see the detail pane for externs incl. methods"));
            if (UdonProfilerRecorder.AllocSupported)
                cols.Add(NumColumn("alloc", "GC KB", 60, n => n.kind == NodeKind.Extern ? "" : (n.allocBytes / 1024.0).ToString("0.0")));
            deltaColumn = NumColumn("delta", "Delta", 60, n => double.IsNaN(n.baselineMs) ? "new" : n.baselineMs <= 0 ? "" : FormatDelta(Delta(n)));
            deltaColumn.visible = UdonProfilerModel.Baseline != null;
            var bindDelta = deltaColumn.bindCell;
            deltaColumn.bindCell = (e, i) =>
            {
                bindDelta(e, i);
                var n = tree.GetItemDataForIndex<Node>(i);
                double d = n == null || double.IsNaN(n.baselineMs) || n.baselineMs <= 0 ? 0 : Delta(n);
                e.EnableInClassList("up-delta-good", d <= -5);
                e.EnableInClassList("up-delta-bad", d >= 5);
            };
            cols.Add(deltaColumn);

            tree = new MultiColumnTreeView(cols) { selectionType = SelectionType.Single };
#if UNITY_6000_3_OR_NEWER
            tree.sortingMode = ColumnSortingMode.Custom;
            tree.itemExpandedChanged += a =>
            {
                if (populating) return;
                if (a.isExpanded)
                {
                    expanded.Add(a.id);
                    if (orderStale)
                    {
                        orderStale = false;
                        tree.schedule.Execute(Repopulate);
                    }
                }
                else expanded.Remove(a.id);
            };
#else
            tree.sortingEnabled = true;
#endif
            tree.AddToClassList("up-tree");
            tree.columnSortingChanged += () =>
            {
                SortColumnDescription first = null;
                foreach (var s in tree.sortedColumns) { first = s; break; }
                sortColumn = first?.columnName ?? "pct";
                sortAsc = first != null && first.direction == SortDirection.Ascending;
                membershipDirty = true;
                Touch();
            };
            tree.selectionChanged += _ => detailPane?.Refresh(view, SelectedNode(), groupMode);
            tree.itemsChosen += items =>
            {
                Node n = null;
                foreach (var o in items) { n = o as Node; break; }
                OpenScript(n);
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
                    var sw = new VisualElement(); sw.AddToClassList("up-swatch"); c.Add(sw);
                    var k = new Label(); k.AddToClassList("up-kind"); c.Add(k);
                    c.Add(new Label());
                    var b = new Label("halted"); b.AddToClassList("up-badge"); c.Add(b);
                    c.AddManipulator(new ContextualMenuManipulator(BuildMenu));
                    return c;
                },
                bindCell = (e, i) =>
                {
                    var n = tree.GetItemDataForIndex<Node>(i);
                    e.userData = n;
                    if (n == null) return;
                    e[0].style.display = n.scriptId >= 0 ? DisplayStyle.Flex : DisplayStyle.None;
                    if (n.scriptId >= 0) e[0].style.backgroundColor = UdonProfilerPalette.Script(n.scriptId);
                    ((Label)e[1]).text = KindLetter(n.kind);
                    ((Label)e[2]).text = n.name;
                    e[3].style.display = n.halted ? DisplayStyle.Flex : DisplayStyle.None;
                }
            };
        }

        Column PctColumn()
        {
            var col = new Column
            {
                name = "pct", title = "%", width = 60, sortable = true,
                makeCell = () =>
                {
                    var c = new VisualElement();
                    c.AddToClassList("up-pct-cell");
                    var bar = new VisualElement { pickingMode = PickingMode.Ignore };
                    bar.AddToClassList("up-pct-bar");
                    c.Add(bar);
                    var l = new Label();
                    l.AddToClassList("up-cell");
                    l.style.flexGrow = 1;
                    c.Add(l);
                    c.AddManipulator(new ContextualMenuManipulator(BuildMenu));
                    return c;
                },
                bindCell = (e, i) =>
                {
                    var n = tree.GetItemDataForIndex<Node>(i);
                    e.userData = n;
                    double pct = n == null || view == null ? 0 : view.Pct(n);
                    e[0].style.width = Length.Percent((float)Math.Max(0, Math.Min(100, pct)));
                    ((Label)e[1]).text = n == null || view == null || view.avgMs <= 0 ? "" : pct.ToString("0.0");
                }
            };
            return col;
        }

        Column NumColumn(string id, string title, int width, Func<Node, string> fmt, string tip = null)
        {
            var col = new Column
            {
                name = id, title = title, width = width, sortable = true,
                makeCell = () =>
                {
                    var l = new Label();
                    l.AddToClassList("up-cell");
                    l.AddManipulator(new ContextualMenuManipulator(BuildMenu));
                    return l;
                },
                bindCell = (e, i) =>
                {
                    var n = tree.GetItemDataForIndex<Node>(i);
                    e.userData = n;
                    ((Label)e).text = n == null ? "" : fmt(n);
                }
            };
            if (tip != null)
            {
                col.makeHeader = () =>
                {
                    var h = new Label { tooltip = tip };
                    h.style.flexGrow = 1;
                    h.style.unityTextAlign = TextAnchor.MiddleLeft;
                    h.style.paddingLeft = 4;
                    return h;
                };
                col.bindHeader = e => ((Label)e).text = title;
            }
            return col;
        }

        static string KindLetter(NodeKind k)
        {
            switch (k)
            {
                case NodeKind.Group: return "G";
                case NodeKind.Behaviour: return "B";
                case NodeKind.Event: return "E";
                case NodeKind.Method: return "M";
                case NodeKind.Call: return "C";
                default: return "X";
            }
        }

        static double Avg(Node n) => n.kind == NodeKind.Extern ? n.totalMs / Math.Max(1, n.calls - n.fusedCalls) : n.totalMs / Math.Max(1, n.calls);
        static double Delta(Node n) => (n.totalMs - n.baselineMs) / n.baselineMs * 100;
        static string FormatDelta(double d) => (d < 0 ? "-" : "+") + Math.Abs(d).ToString("0.0") + "%";
        static double DeltaKey(Node n) => double.IsNaN(n.baselineMs) ? double.MaxValue : n.baselineMs <= 0 ? 0 : Delta(n);

        Node SelectedNode() => tree?.selectedItem as Node;

        void BuildMenu(ContextualMenuPopulateEvent evt)
        {
            Node n = null;
            for (var ve = evt.target as VisualElement; ve != null && n == null; ve = ve.parent) n = ve.userData as Node;
            if (n == null) return;
            evt.menu.AppendAction("Copy name", _ => EditorGUIUtility.systemCopyBuffer = n.name);
            evt.menu.AppendAction("Copy row", _ => EditorGUIUtility.systemCopyBuffer = CsvRow(n));
            evt.menu.AppendAction("Copy call path", _ => EditorGUIUtility.systemCopyBuffer = CallPath(n));

            var target = ScriptTarget(n);
            if (target != null)
            {
                string label = target.kind == NodeKind.Method ? "Open script at " + target.name : "Open script";
                bool can = CanOpenScript(target);
                evt.menu.AppendAction(label, _ => OpenScript(n), _ => can ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            }

            if (n.kind != NodeKind.Group)
            {
                var ub = UdonProfilerRecorder.GetInfo(n.behaviourId)?.behaviour;
                if (ub != null) evt.menu.AppendAction("Select object", _ => SelectObject(n));
            }
        }

        Node ScriptTarget(Node n)
        {
            if (n == null) return null;
            if (n.kind == NodeKind.Extern) n = n.parent;
            if (n == null || n.kind == NodeKind.Extern) return null;
            if (n.kind == NodeKind.Group && groupMode != GroupMode.Script) return null;
            return n;
        }

        static bool HasLine(Node n) => n.kind == NodeKind.Event || n.kind == NodeKind.Method || n.kind == NodeKind.Call || n.eventRole;

        static bool CanOpenScript(Node n) => UdonProfilerModel.ScriptAssetOf(n) != null || (HasLine(n) && UdonProfilerModel.SourceOf(n).HasFile);

        void OpenScript(Node n)
        {
            var target = ScriptTarget(n);
            if (target == null) return;
            OpenSource(UdonProfilerModel.ScriptAssetOf(target), HasLine(target) ? UdonProfilerModel.SourceOf(target) : default);
        }

        static SourceRef EventSource(int behaviourId, uint entry)
        {
            var src = UdonProfilerRecorder.SourceAt(behaviourId, entry);
            return src.HasFile ? src : UdonProfilerRecorder.SourceAt(behaviourId, entry + 8);
        }

        static void OpenSource(UnityEngine.Object asset, SourceRef src)
        {
            int line = src.line > 0 ? src.line : -1;
            if (src.HasFile)
            {
                string file = src.file.Replace('\\', '/');
                string root = System.IO.Path.GetDirectoryName(Application.dataPath)?.Replace('\\', '/');
                if (root != null && file.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) file = file.Substring(root.Length + 1);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(file);
                if (script != null) { AssetDatabase.OpenAsset(script, line); return; }
                if (!(asset is MonoScript) && System.IO.File.Exists(src.file)) { InternalEditorUtility.OpenFileAtLineExternal(src.file, Math.Max(line, 1)); return; }
            }
            if (asset != null) AssetDatabase.OpenAsset(asset, asset is MonoScript ? line : -1);
        }

        static void SelectObject(Node n)
        {
            if (n == null || n.kind == NodeKind.Group || n.kind == NodeKind.Extern) return;
            var ub = UdonProfilerRecorder.GetInfo(n.behaviourId)?.behaviour;
            if (ub == null) return;
            var go = ub.gameObject;
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        string CsvRow(Node n)
        {
            var ci = CultureInfo.InvariantCulture;
            int depth = 0;
            for (var p = n.parent; p != null; p = p.parent) depth++;
            var info = n.kind == NodeKind.Group ? null : UdonProfilerRecorder.GetInfo(n.behaviourId);
            string ev = n.kind == NodeKind.Event || (n.kind == NodeKind.Behaviour && groupMode == GroupMode.Event)
                ? UdonProfilerRecorder.EventName(n.behaviourId, n.entry)
                : n.kind == NodeKind.Extern ? n.sig : "";
            double pct = view != null ? view.Pct(n) : 0;
            var sb = new StringBuilder();
            sb.Append(n.kind).Append(',').Append(depth).Append(',').Append(Q(n.name)).Append(',').Append(Q(info?.script)).Append(',')
              .Append(Q(info?.objectPath)).Append(',').Append(Q(ev)).Append(',')
              .Append(n.totalMs.ToString("0.000", ci)).Append(',').Append(n.selfMs.ToString("0.000", ci)).Append(',')
              .Append(n.externMs.ToString("0.000", ci)).Append(',')
              .Append(double.IsNaN(n.externAllMs) ? "" : n.externAllMs.ToString("0.000", ci)).Append(',')
              .Append(n.maxMs.ToString("0.000", ci)).Append(',')
              .Append(double.IsNaN(n.p95Ms) ? "" : n.p95Ms.ToString("0.000", ci)).Append(',')
              .Append((n.allocBytes / 1024.0).ToString("0.0", ci)).Append(',')
              .Append(n.calls).Append(',').Append(n.externCalls).Append(',').Append(n.fusedCalls).Append(',')
              .Append(Avg(n).ToString("0.0000", ci)).Append(',').Append(pct.ToString("0.00", ci)).Append(',')
              .Append(UdonProfilerModel.InstrText(view, n, false, ci)).Append(',').Append(UdonProfilerModel.InstrText(view, n, true, ci));
            return sb.ToString();
        }

        static string Q(string s) => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";

        string CallPath(Node n)
        {
            nodeScratch.Clear();
            for (var p = n; p != null && nodeScratch.Count < 256; p = p.parent) nodeScratch.Add(p);
            var sb = new StringBuilder();
            for (int i = nodeScratch.Count - 1; i >= 0; i--)
            {
                sb.Append(nodeScratch[i].name);
                if (i > 0) sb.Append(" > ");
            }
            nodeScratch.Clear();
            return sb.ToString();
        }

        void SetViewKind(ViewKind k)
        {
            if (viewKind == k) { ApplyViewKind(); return; }
            viewKind = k;
            EditorPrefs.SetInt(Prefs + "View", (int)k);
            ApplyViewKind();
            Touch();
        }

        void ApplyViewKind()
        {
            treeToggle?.SetValueWithoutNotify(viewKind == ViewKind.Tree);
            flameToggle?.SetValueWithoutNotify(viewKind == ViewKind.Flame);
            netToggle?.SetValueWithoutNotify(viewKind == ViewKind.Network);
            if (tree != null) tree.style.display = viewKind == ViewKind.Tree ? DisplayStyle.Flex : DisplayStyle.None;
            if (flameScroll != null) flameScroll.style.display = viewKind == ViewKind.Flame ? DisplayStyle.Flex : DisplayStyle.None;
            if (netPane != null) netPane.style.display = viewKind == ViewKind.Network ? DisplayStyle.Flex : DisplayStyle.None;
        }

        VisualElement BuildNetPane()
        {
            netPane = new VisualElement();
            netPane.AddToClassList("up-net");
            var note = new Label("Estimates: ClientSim sends nothing. Bytes are summed synced-variable sizes (doc-derived), Manual requests coalesce to one serialization per frame while owned, owned Continuous behaviours are projected at the Options send rate. Network events run locally and synchronously; Others never runs; limits are doc-derived.");
            note.AddToClassList("up-net-note");
            netPane.Add(note);

            var cols = new Columns();
            cols.Add(NetColumn("script", "Script", 120, r => r.script, null, true));
            cols.Add(NetColumn("object", "Object", 140, r => r.objectName, null, true));
            cols.Add(NetColumn("sync", "Sync", 72, r => r.sync.ToString(), null, true));
            cols.Add(NetColumn("rps", "Req/s", 56, r => r.RequestsPerSec.ToString("0.0"), "RequestSerialization calls per second (all, including ignored)"));
            cols.Add(NetColumn("ignored", "Ignored", 56, r => r.ignored.ToString(), "Requests that cannot serialize: not Manual, or not the owner"));
            cols.Add(NetColumn("sps", "Ser/s", 56, r => r.SerializationsPerSec.ToString("0.0"), "Estimated serializations per second (Manual: one per frame with requests; Continuous: projected)"));
            cols.Add(NetColumn("bser", "B/ser", 60, r => r.serializations > 0 ? r.BytesPerSerialization.ToString("0") : "", "Estimated bytes per serialization (synced variables)"));
            cols.Add(NetColumn("sbps", "Ser B/s", 64, r => FormatBytes(r.SerializationBytesPerSec), "Estimated serialization bytes per second"));
            cols.Add(NetColumn("events", "Events a/o/x/s", 100, r => $"{r.eventsAll}/{r.eventsOwner}/{r.eventsOthers}/{r.eventsSelf}", "Network events sent over the range by target: All / Owner / Others / Self"));
            cols.Add(NetColumn("ebps", "Event B/s", 64, r => FormatBytes(r.EventBytesPerSec), "Estimated network event parameter bytes per second"));
            cols.Add(NetColumn("bps", "Total B/s", 66, r => FormatBytes(r.BytesPerSec), "Serialization plus event bytes per second"));
            cols.Add(NetColumn("rate", "Rate hits", 60, r => r.rateLimitHits.ToString(), "[NetworkCallable] sends over the event's per-second limit"));
            cols.Add(NetColumn("transfers", "Transfers", 64, r => r.transfers.ToString(), "Ownership transfers (Networking.SetOwner that changed the owner)"));
            cols.Add(NetColumn("over", "Over limit", 64, r => r.overLimit.ToString(), "Serializations above the doc-derived size limit (Manual 280,496 B, Continuous 200 B)"));

            netList = new MultiColumnListView(cols) { selectionType = SelectionType.Single, itemsSource = netItems };
#if UNITY_6000_3_OR_NEWER
            netList.sortingMode = ColumnSortingMode.Custom;
#else
            netList.sortingEnabled = true;
#endif
            netList.AddToClassList("up-tree");
            netList.style.flexGrow = 1;
            netList.columnSortingChanged += () =>
            {
                SortColumnDescription first = null;
                foreach (var s in netList.sortedColumns) { first = s; break; }
                netSortColumn = first?.columnName ?? "bps";
                netSortAsc = first != null && first.direction == SortDirection.Ascending;
                PopulateNet();
            };
            netList.selectionChanged += _ => ShowNetDetail();
            netList.itemsChosen += items =>
            {
                foreach (var o in items)
                {
                    if (o is NetRow r) OpenSource(UdonProfilerRecorder.GetInfo(r.behaviourId)?.scriptAsset, default);
                    break;
                }
            };
            netPane.Add(netList);
            return netPane;
        }

        Column NetColumn(string id, string title, int width, Func<NetRow, string> fmt, string tip, bool stretch = false)
        {
            var col = new Column
            {
                name = id, title = title, width = width, sortable = true, stretchable = stretch,
                makeCell = () =>
                {
                    var l = new Label();
                    l.AddToClassList("up-cell");
                    l.AddManipulator(new ContextualMenuManipulator(BuildNetMenu));
                    return l;
                },
                bindCell = (e, i) =>
                {
                    var r = i >= 0 && i < netItems.Count ? netItems[i] : null;
                    e.userData = r;
                    ((Label)e).text = r == null ? "" : fmt(r);
                    e.tooltip = r != null && (id == "object" || id == "script") ? r.objectPath : "";
                }
            };
            if (tip != null)
            {
                col.makeHeader = () =>
                {
                    var h = new Label { tooltip = tip };
                    h.style.flexGrow = 1;
                    h.style.unityTextAlign = TextAnchor.MiddleLeft;
                    h.style.paddingLeft = 4;
                    return h;
                };
                col.bindHeader = e => ((Label)e).text = title;
            }
            return col;
        }

        void BuildNetMenu(ContextualMenuPopulateEvent evt)
        {
            NetRow r = null;
            for (var ve = evt.target as VisualElement; ve != null && r == null; ve = ve.parent) r = ve.userData as NetRow;
            if (r == null) return;
            var info = UdonProfilerRecorder.GetInfo(r.behaviourId);
            evt.menu.AppendAction("Open script", _ => OpenSource(info?.scriptAsset, default), _ => info?.scriptAsset != null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            var ub = info?.behaviour;
            if (ub != null) evt.menu.AppendAction("Select object", _ => { Selection.activeGameObject = ub.gameObject; EditorGUIUtility.PingObject(ub.gameObject); });
        }

        static Func<NetRow, IComparable> NetKey(string column)
        {
            switch (column)
            {
                case "script": return r => r.script;
                case "object": return r => r.objectName;
                case "sync": return r => (int)r.sync;
                case "rps": return r => r.requests;
                case "ignored": return r => r.ignored;
                case "sps": return r => r.serializations;
                case "bser": return r => r.BytesPerSerialization;
                case "sbps": return r => r.estBytes;
                case "events": return r => r.Events;
                case "ebps": return r => r.eventBytes;
                case "rate": return r => r.rateLimitHits;
                case "transfers": return r => r.transfers;
                case "over": return r => r.overLimit;
                default: return r => r.estBytes + r.eventBytes;
            }
        }

        void PopulateNet()
        {
            if (netList == null) return;
            var selected = netList.selectedItem as NetRow;
            netItems.Clear();
            if (view?.network != null) netItems.AddRange(view.network.rows);
            var key = NetKey(netSortColumn);
            bool asc = netSortAsc;
            netItems.Sort((a, b) =>
            {
                int c = Comparer<IComparable>.Default.Compare(key(a), key(b));
                if (!asc) c = -c;
                return c != 0 ? c : a.behaviourId.CompareTo(b.behaviourId);
            });
            netList.RefreshItems();
            int idx = selected == null ? -1 : netItems.IndexOf(selected);
            if (idx >= 0) netList.SetSelectionWithoutNotify(new[] { idx });
            else netList.ClearSelection();
            ShowNetDetail();
        }

        void ShowNetDetail()
        {
            if (detailPane == null || viewKind != ViewKind.Network) return;
            detailPane.ShowNet(netList?.selectedItem as NetRow, view?.network);
        }

        void ApplyRangeMode(RangeMode m)
        {
            rangeMode = m;
            if (m != RangeMode.Custom) EditorPrefs.SetInt(Prefs + "Range", (int)m);
            if (rangeDropdown != null && rangeDropdown.index != (int)m) rangeDropdown.SetValueWithoutNotify(RangeNames[(int)m]);
        }

        void SetRange(RangeMode m)
        {
            ApplyRangeMode(m);
            rangeNote = null;
            membershipDirty = true;
            pendingUserChange = true;
            Touch();
        }

        void Touch() => dirty = true;

        void RebuildNow()
        {
            membershipDirty = true;
            pendingUserChange = true;
            Rebuild();
        }

        void Step(int delta)
        {
            int count = UdonProfilerRecorder.FrameCount;
            if (count == 0) return;
            int newest = UdonProfilerRecorder.NewestFrame;
            int oldest = newest - count + 1;
            int cur = rangeMode == RangeMode.Selected && selectedFrame >= 0 ? selectedFrame : lastTo;
            selectedFrame = Mathf.Clamp(cur + delta, oldest, newest);
            SetRange(RangeMode.Selected);
            RebuildNow();
        }

        void Tick()
        {
            if (tree == null) return;
            int paused = UdonProfilerRecorder.PausedOnFrame;
            if (paused >= 0 && (paused != lastPausedOn || (!UdonProfilerRecorder.Recording && recToggle.value)))
            {
                lastPausedOn = paused;
                recToggle.SetValueWithoutNotify(false);
                EditorPrefs.SetBool(Prefs + "Record", false);
                selectedFrame = paused;
                SetRange(RangeMode.Selected);
                return;
            }
            if (!visibleTab) return;
            if (!dirty && UdonProfilerRecorder.Version == lastVersion) return;
            bool live = UdonProfilerRecorder.Recording && EditorApplication.isPlaying;
            double now = EditorApplication.timeSinceStartup;
            if (now - lastRebuild < (live ? 0.3 : 0.1)) return;
            if (!dirty && FixedSealedRange(live))
            {
                lastRebuild = now;
                lastVersion = UdonProfilerRecorder.Version;
                chart.Refresh();
                UpdateFooter();
                return;
            }
            Rebuild();
        }

        bool FixedSealedRange(bool live)
        {
            if (view == null || !lastBuiltSealed || UdonProfilerRecorder.Generation != lastGeneration) return false;
            int newest = UdonProfilerRecorder.NewestFrame;
            int oldest = newest - UdonProfilerRecorder.FrameCount + 1;
            switch (rangeMode)
            {
                case RangeMode.Selected: return selectedFrame >= oldest && selectedFrame == lastBuiltTo;
                case RangeMode.Custom: return customFrom >= oldest && customFrom == lastBuiltFrom && customTo == lastBuiltTo;
                case RangeMode.All: return !live && lastBuiltFrom == oldest && lastBuiltTo == newest;
                default: return false;
            }
        }

        void Rebuild()
        {
            if (tree == null) return;
            double now = EditorApplication.timeSinceStartup;
            lastRebuild = now;
            lastVersion = UdonProfilerRecorder.Version;
            lastGeneration = UdonProfilerRecorder.Generation;
            dirty = false;
            bool userChange = pendingUserChange;
            pendingUserChange = false;

            int count = UdonProfilerRecorder.FrameCount;
            int newest = UdonProfilerRecorder.NewestFrame;
            int oldest = newest - count + 1;
            if (selectedFrame >= 0 && selectedFrame < oldest) selectedFrame = -1;
            if (rangeMode == RangeMode.Custom && customTo < oldest)
            {
                ApplyRangeMode(RangeMode.Last60);
                rangeNote = "range evicted";
                membershipDirty = true;
                userChange = true;
            }
            int from, to;
            switch (rangeMode)
            {
                case RangeMode.Selected: from = to = selectedFrame >= 0 ? selectedFrame : newest; break;
                case RangeMode.Last60: from = newest - 59; to = newest; break;
                case RangeMode.Last300: from = newest - 299; to = newest; break;
                case RangeMode.Custom: from = customFrom; to = customTo; break;
                default: from = oldest; to = newest; break;
            }
            if (count == 0) { from = 0; to = -1; }
            else
            {
                from = Mathf.Clamp(from, oldest, newest);
                to = Mathf.Clamp(to, from, newest);
            }
            lastTo = to;
            lastBuiltFrom = from;
            lastBuiltTo = to;
            lastBuiltSealed = count > 0 && UdonProfilerRecorder.GetFrame(to)?.isSealed == true;

            bool live = UdonProfilerRecorder.Recording && EditorApplication.isPlaying;
            bool refresh = membershipDirty || !live || now - lastMembershipTime >= 1.0;
            view = model.Build(new ViewQuery
            {
                from = from,
                to = to,
                mode = groupMode,
                loopOnly = loopOnly,
                search = search,
                hideBelowPct = hideTiny ? 0.1 : 0,
                refreshMembership = refresh
            });
            if (refresh)
            {
                lastMembershipTime = now;
                membershipDirty = false;
            }

            chart.LoopOnly = loopOnly;
            chart.Refresh();
            bool single = rangeMode == RangeMode.Selected;
            if (!chart.Dragging) chart.SetSelection(single ? from : -1, single ? -1 : from, single ? -1 : to);

            UpdateTiles();
            UpdateFooter();

            bool hadDelta = deltaColumn.visible;
            deltaColumn.visible = UdonProfilerModel.Baseline != null;
            if (hadDelta && !deltaColumn.visible && sortColumn == "delta") { sortColumn = "pct"; sortAsc = false; }
            bool showFused = FastVmActive() || view.avgFused > 0;
            if (fusedColumn.visible != showFused) fusedColumn.visible = showFused;
            bool showInstr = view.hasInstructions;
            if (instrColumn.visible != showInstr)
            {
                instrColumn.visible = showInstr;
                selfInstrColumn.visible = showInstr;
                if (!showInstr && (sortColumn == "instr" || sortColumn == "selfinstr")) { sortColumn = "pct"; sortAsc = false; }
            }

            if (timelineOpen)
            {
                int tf = single ? from : to;
                timeline.SetFrame(tf, userChange);
                timelineFrame.text = count == 0 ? "No frame" : $"Frame {FrameNo(tf)}";
                var td = timeline.Data;
                timelineNote.text = td != null && td.droppedSpans > 0 ? $"{td.droppedSpans} method spans dropped (cap)" : "";
            }

            if (viewKind == ViewKind.Tree)
            {
                PopulateTree(live, now);
                detailPane.Refresh(view, SelectedNode(), groupMode);
            }
            else if (viewKind == ViewKind.Flame)
            {
                flame.SetHighlight(search);
                flame.SetData(model.BuildFlame(flameByInstr));
                detailPane.ShowFlame(flame.Data, flame.Selected);
            }
            else PopulateNet();
        }

        void UpdateTiles()
        {
            if (view == null) return;
            UpdateTileClasses();
            tileValues[0].text = view.avgMs.ToString("0.00");
            tileSubs[0].text = $"Udon ms / frame | max {view.maxMs:0.00}";
            tileValues[1].text = view.avgEvents.ToString();
            tileValues[2].text = view.avgExterns.ToString();
            tileSubs[2].text = view.avgFused > 0 ? $"Externs / frame | {view.avgFused} fused" : "Externs / frame";
            tileValues[3].text = view.activeBehaviours.ToString();
            tileInstr.style.display = view.hasInstructions ? DisplayStyle.Flex : DisplayStyle.None;
            if (view.hasInstructions)
            {
                tileValues[4].text = UdonProfilerModel.FormatCount(view.avgInstructions);
                tileSubs[4].text = view.avgMs > 0 ? $"Udon instr / frame | {UdonProfilerModel.FormatCount(view.avgInstructions / view.avgMs)} per ms" : "Udon instr / frame";
            }
            var net = view.network;
            bool hasNet = net != null && net.HasData;
            tileNet.style.display = hasNet ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasNet)
            {
                tileValues[5].text = FormatBytes(net.bytesPerSec);
                tileSubs[5].text = $"Net bytes/s (est.) | {net.rows.Count} behaviour{(net.rows.Count == 1 ? "" : "s")}";
            }
        }

        internal static string FormatBytes(double b)
        {
            double a = Math.Abs(b);
            if (a < 1024) return b.ToString("0");
            if (a < 1024 * 1024) return (b / 1024).ToString(a < 10 * 1024 ? "0.0" : "0") + " KB";
            return (b / (1024 * 1024)).ToString("0.00") + " MB";
        }

        void UpdateTileClasses()
        {
            if (view == null || tileMs == null) return;
            double scaled = view.avgMs * questScale;
            tileMs.EnableInClassList("up-ok", scaled < 2);
            tileMs.EnableInClassList("up-warn", scaled >= 2 && scaled < 5);
            tileMs.EnableInClassList("up-bad", scaled >= 5);
        }

        void UpdateFooter()
        {
            if (footRange == null) return;
            string range = view == null || view.frameCount == 0 ? "No frames" : $"Frames {FrameNo(view.fromFrame)}-{FrameNo(view.toFrame)} ({view.frameCount})";
            if (rangeNote != null) range += "  |  " + rangeNote;
            footRange.text = range;

            int pausedOn = UdonProfilerRecorder.PausedOnFrame;
            string status = UdonProfilerRecorder.Recording
                ? (EditorApplication.isPlaying ? "Recording" : "Armed (enter play mode)")
                : pausedOn >= 0 ? $"paused on spike frame {FrameNo(pausedOn)}"
                : UdonProfilerRecorder.IsLoadedCapture ? "Loaded capture" : "Paused";
            if (EditorApplication.isPlaying) status += VmStatus() + "  |  methods: " + UdonProfilerRecorder.MethodHookStatus;
            else if (UdonProfilerRecorder.FrameCount > 0 && UdonProfilerRecorder.RecordedVm != null)
                status += $"  |  recorded on {UdonProfilerRecorder.RecordedVm}  |  methods: {UdonProfilerRecorder.RecordedMethodHooks}";
            if (view != null && view.droppedSpans > 0) status += "  |  spans capped";
            if (UdonProfilerRecorder.PathTableFull) status += "  |  path table full";
            footStatus.text = status;

            string overhead = "Overhead: 2 timestamps per event, extern and internal call while recording";
            double callNs = UdonProfilerRecorder.ProbeCallNs, eventNs = UdonProfilerRecorder.ProbeEventNs;
            if (callNs > 0 || eventNs > 0)
                overhead += $"; probe ~{callNs:0} ns/call, {UdonProfilerRecorder.ProbeFusedNs:0} ns/fused op, {eventNs:0} ns/nested event (subtracted)";
            if (!EditorApplication.isPlaying) keepMethodsChangedInPlay = false;
            if (UdonQuicBridge.Available)
            {
                if (keepMethodsChangedInPlay) overhead += "  |  Keep methods change applies on next play";
                else if (!UdonQuicBridge.KeepMethods) overhead += "  |  UdonQuic inlines methods: inlined methods have no rows (Options > Keep methods)";
            }
            footOverhead.text = overhead;
        }

        static int FrameNo(int serial) => UdonProfilerModel.Summary(serial)?.frameNumber ?? serial;

        static bool FastVmActive() => VmEnabled != null && EditorApplication.isPlaying && VmEnabled.GetValue(null) is bool b && b;

        static string VmStatus()
        {
            if (VmEnabled == null || !EditorApplication.isPlaying) return "";
            if (!FastVmActive()) return "  |  VM: stock";
            long fused = VmFusedHits?.GetValue(null) is long l ? l : 0;
            return $"  |  VM: fast ({fused:N0} fused), fused externs untimed";
        }

        Comparison<Node> GetComparison(string key)
        {
            if (key == comparisonKey && comparison != null) return comparison;
            comparisonKey = key;
            if (sortColumn == "name")
            {
                var cmp = StringComparer.OrdinalIgnoreCase;
                comparison = sortAsc
                    ? (a, b) => { int c = cmp.Compare(a.name, b.name); return c != 0 ? c : a.id.CompareTo(b.id); }
                    : (Comparison<Node>)((a, b) => { int c = cmp.Compare(b.name, a.name); return c != 0 ? c : a.id.CompareTo(b.id); });
                return comparison;
            }
            Func<Node, double> k;
            switch (sortColumn)
            {
                case "self": k = n => n.selfMs; break;
                case "instr": k = n => UdonProfilerModel.HasInstructions(view, n) ? n.instructions : -1; break;
                case "selfinstr": k = n => UdonProfilerModel.HasInstructions(view, n) ? n.selfInstructions : -1; break;
                case "calls": k = n => n.calls; break;
                case "externs": k = n => n.externCalls; break;
                case "fused": k = n => n.fusedCalls; break;
                case "avg": k = Avg; break;
                case "max": k = n => n.maxMs; break;
                case "p95": k = n => n.p95Ms; break;
                case "extern": k = n => n.externMs; break;
                case "alloc": k = n => n.allocBytes; break;
                case "delta": k = DeltaKey; break;
                default: k = n => n.totalMs; break;
            }
            comparison = sortAsc
                ? (a, b) => { int c = k(a).CompareTo(k(b)); return c != 0 ? c : a.id.CompareTo(b.id); }
                : (Comparison<Node>)((a, b) => { int c = k(b).CompareTo(k(a)); return c != 0 ? c : a.id.CompareTo(b.id); });
            return comparison;
        }

        static void SortAll(List<Node> nodes, Comparison<Node> cmp)
        {
            if (nodes == null) return;
            if (nodes.Count > 1) nodes.Sort(cmp);
            for (int i = 0; i < nodes.Count; i++)
            {
                var c = nodes[i].children;
                if (c != null && c.Count > 0) SortAll(c, cmp);
            }
        }

        const int FnvBasis = unchecked((int)2166136261);
        static int Fnv(int h, int v) => unchecked((h ^ v) * 16777619);

        int Hash(List<Node> nodes, int h, bool all)
        {
            if (nodes == null) return h;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                h = Fnv(h, n.id);
                if (n.children != null && n.children.Count > 0 && (all || expanded.Contains(n.id))) h = Hash(n.children, h, all);
            }
            return h;
        }

        int OrderHash()
        {
#if UNITY_6000_3_OR_NEWER
            return Hash(view.roots, FnvBasis, false);
#else
            return Hash(view.roots, FnvBasis, true);
#endif
        }

        void PopulateTree(bool live, double now)
        {
            if (view == null) return;
            string sortKey = sortColumn + (sortAsc ? "+" : "-");
            bool structural = view.structureStamp != lastStructure || sortKey != lastSortKey;
            bool resort = structural || !live || now - lastSortTime >= 1.0;
            if (!resort) { tree.RefreshItems(); return; }
            SortAll(view.roots, GetComparison(sortKey));
            lastSortTime = now;
            int orderHash = OrderHash();
            int fullHash = Hash(view.roots, FnvBasis, true);
            if (orderHash == lastOrderHash && !structural)
            {
                if (fullHash != lastFullHash) orderStale = true;
                tree.RefreshItems();
                return;
            }
            ApplyItems(orderHash, fullHash);
            lastStructure = view.structureStamp;
            lastSortKey = sortKey;
        }

        void Repopulate()
        {
            if (view == null || tree == null || viewKind != ViewKind.Tree) return;
            ApplyItems(OrderHash(), Hash(view.roots, FnvBasis, true));
        }

        void ApplyItems(int orderHash, int fullHash)
        {
#if !UNITY_6000_3_OR_NEWER
            if (tree.viewController != null)
            {
                expanded.Clear();
                foreach (var id in tree.viewController.GetAllItemIds())
                    if (tree.viewController.IsExpanded(id)) expanded.Add(id);
            }
#endif
            populating = true;
            try
            {
                expandScratch.Clear();
                expandScratch.AddRange(expanded);
                int selected = SelectedNode()?.id ?? 0;
                bool hasSelected = SelectedNode() != null;
                itemIds.Clear();
                tree.SetRootItems(Items(view.roots));
                foreach (int id in expandScratch)
                {
                    if (!itemIds.Contains(id)) continue;
#if UNITY_6000_3_OR_NEWER
                    tree.ExpandItem(id, false, false);
#else
                    tree.ExpandItem(id, false);
#endif
                }
                if (hasSelected && itemIds.Contains(selected)) tree.SetSelectionByIdWithoutNotify(new[] { selected });
                else tree.SetSelectionWithoutNotify(Array.Empty<int>());
                tree.RefreshItems();
                orderStale = false;
                lastOrderHash = orderHash;
                lastFullHash = fullHash;
            }
            finally
            {
                populating = false;
            }
        }

        List<TreeViewItemData<Node>> Items(List<Node> nodes)
        {
            var list = new List<TreeViewItemData<Node>>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                itemIds.Add(n.id);
                var children = n.children != null && n.children.Count > 0 ? Items(n.children) : null;
                list.Add(new TreeViewItemData<Node>(n.id, n, children));
            }
            return list;
        }

        void JumpTo(Node n)
        {
            if (n == null || tree == null) return;
            if (viewKind != ViewKind.Tree)
            {
                SetViewKind(ViewKind.Tree);
                Rebuild();
            }
            if (!itemIds.Contains(n.id)) return;
            nodeScratch.Clear();
            for (var p = n.parent; p != null && nodeScratch.Count < 256; p = p.parent) nodeScratch.Add(p);
            populating = true;
            try
            {
                for (int i = nodeScratch.Count - 1; i >= 0; i--)
                {
                    int id = nodeScratch[i].id;
                    expanded.Add(id);
#if UNITY_6000_3_OR_NEWER
                    tree.ExpandItem(id, false, false);
#else
                    tree.ExpandItem(id, false);
#endif
                }
            }
            finally
            {
                populating = false;
            }
            nodeScratch.Clear();
            tree.RefreshItems();
            tree.SetSelectionById(n.id);
            tree.ScrollToItemById(n.id);
        }
    }
}
