using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MagmaVRC.UdonProfiler
{
    internal sealed class UdonProfilerTimeline : VisualElement
    {
        public event Action<TimelineItem> BarClicked;
        public event Action<TimelineItem> BarDoubleClicked;
        public event Action<int> StepRequested;

        const float RowH = 14f;
        const float AxisH = 14f;
        const float PadX = 4f;
        const int MaxRows = 16;
        const int MaxLabels = 200;
        const double MinSpanMs = 0.001;

        readonly Label _tip, _empty, _deeper;
        readonly VisualElement _hoverLayer;
        readonly List<Label> _labels = new List<Label>();
        readonly List<Label> _ticks = new List<Label>();
        readonly UdonProfilerQuads _quads = new UdonProfilerQuads();
        readonly UdonProfilerQuads _hoverQuads = new UdonProfilerQuads();
        List<int>[] _rows = new List<int>[0];
        int _rowCount;

        TimelineData data;
        int drawnSerial = int.MinValue, drawnGen = int.MinValue, drawnMutation = int.MinValue;
        bool drawnSealed;
        double totalMs, viewStartMs, viewSpanMs;
        int stickyRows, visibleRows = 1;
        float assignedHeight = -1f;
        int hover = -1;
        Vector2 lastPos;
        bool pointerIn;

        bool pressed, panning;
        float pressX;
        double pressViewStart;

        public TimelineData Data => data;

        public UdonProfilerTimeline()
        {
            AddToClassList("up-timeline");
            focusable = true;
            _hoverLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            _hoverLayer.style.position = Position.Absolute;
            _hoverLayer.style.left = 0; _hoverLayer.style.top = 0; _hoverLayer.style.right = 0; _hoverLayer.style.bottom = 0;
            _hoverLayer.generateVisualContent += PaintHover;
            Add(_hoverLayer);
            _empty = new Label("no Udon this frame") { pickingMode = PickingMode.Ignore };
            _empty.AddToClassList("up-empty");
            Add(_empty);
            _deeper = new Label { pickingMode = PickingMode.Ignore };
            _deeper.AddToClassList("up-chart-max");
            Add(_deeper);
            _tip = new Label { pickingMode = PickingMode.Ignore };
            _tip.AddToClassList("up-chart-tooltip");
            _tip.style.display = DisplayStyle.None;
            Add(_tip);
            generateVisualContent += Paint;
            UpdateHeight();
            RegisterCallback<GeometryChangedEvent>(_ => Layout());
            RegisterCallback<PointerDownEvent>(OnDown);
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerUpEvent>(OnUp);
            RegisterCallback<PointerLeaveEvent>(_ => { pointerIn = false; if (!pressed) SetHover(-1, Vector2.zero); });
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<KeyDownEvent>(OnKey);
        }

        public void SetFrame(int serial, bool userChange)
        {
            var d = UdonProfilerModel.Timeline(serial);
            if (d == null) return;
            bool changed = d != data || d.serial != drawnSerial || d.generation != drawnGen || d.mutation != drawnMutation || d.isSealed != drawnSealed;
            if (!changed && !userChange) return;
            bool genChanged = d.generation != drawnGen;
            double prevTotal = totalMs;
            bool wasWhole = viewSpanMs <= 0 || (Math.Abs(viewSpanMs - prevTotal) < 1e-9 && viewStartMs <= 1e-9);
            data = d;
            drawnSerial = d.serial; drawnGen = d.generation; drawnMutation = d.mutation; drawnSealed = d.isSealed;
            totalMs = d.totalMs;
            if (changed) IndexRows();
            if (genChanged) stickyRows = 0;
            if (userChange || wasWhole) FitX();
            else ClampView();
            UpdateHeight();
            Layout();
            if (pointerIn && !pressed) SetHover(HitTest(lastPos), lastPos);
            else SetHover(-1, Vector2.zero);
        }

        public void Fit()
        {
            FitX();
            stickyRows = 0;
            UpdateHeight();
            Layout();
        }

        void FitX()
        {
            viewStartMs = 0;
            viewSpanMs = Math.Max(0, totalMs);
        }

        void ClampView()
        {
            if (totalMs <= 0) { viewStartMs = 0; viewSpanMs = 0; return; }
            viewSpanMs = Math.Min(Math.Max(viewSpanMs, Math.Min(MinSpanMs, totalMs)), totalMs);
            viewStartMs = Math.Min(Math.Max(0, viewStartMs), totalMs - viewSpanMs);
        }

        void IndexRows()
        {
            for (int i = 0; i < _rowCount; i++) _rows[i].Clear();
            _rowCount = 0;
            if (data == null) return;
            var items = data.items;
            for (int i = 0; i < items.Count; i++)
            {
                int d = Math.Max(0, items[i].depth);
                if (d >= _rows.Length)
                {
                    int old = _rows.Length;
                    Array.Resize(ref _rows, Math.Max(d + 1, old * 2));
                    for (int k = old; k < _rows.Length; k++) _rows[k] = new List<int>();
                }
                if (d >= _rowCount) _rowCount = d + 1;
                _rows[d].Add(i);
            }
        }

        void UpdateHeight()
        {
            int depthRows = data == null ? 1 : Math.Min(data.maxDepth + 1, MaxRows);
            stickyRows = Math.Max(Math.Max(stickyRows, depthRows), 1);
            visibleRows = stickyRows;
            float h = Mathf.Max(60f, visibleRows * RowH + AxisH);
            if (h != assignedHeight)
            {
                assignedHeight = h;
                style.height = h;
            }
        }

        float Width => Mathf.Max(1f, resolvedStyle.width - PadX * 2);
        float XOf(double ms) => PadX + (float)(viewSpanMs <= 0 ? 0 : (ms - viewStartMs) / viewSpanMs) * Width;
        double MsAt(float x) => viewStartMs + (x - PadX) / Width * viewSpanMs;

        int FirstAtOrBefore(List<int> row, double ms)
        {
            var items = data.items;
            int lo = 0, hi = row.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (items[row[mid]].startMs <= ms) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return best;
        }

        void Layout()
        {
            int count = data == null ? 0 : data.items.Count;
            _empty.style.display = count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            int li = 0;
            int deeper = 0;
            float right = PadX + Width;
            if (count > 0 && viewSpanMs > 0)
            {
                var items = data.items;
                double viewEnd = viewStartMs + viewSpanMs;
                for (int d = 0; d < _rowCount; d++)
                {
                    var row = _rows[d];
                    if (d >= visibleRows) { deeper += row.Count; continue; }
                    int start = Math.Max(0, FirstAtOrBefore(row, viewStartMs));
                    for (int r = start; r < row.Count && li < MaxLabels; r++)
                    {
                        var it = items[row[r]];
                        if (it.startMs > viewEnd) break;
                        float x0 = Mathf.Max(PadX, XOf(it.startMs)), x1 = Mathf.Min(right, XOf(it.startMs + it.ms));
                        if (x1 - x0 < 40f) continue;
                        var l = Get(_labels, li++, "up-timeline-label");
                        l.text = it.label;
                        l.style.left = x0 + 3f; l.style.top = d * RowH; l.style.width = x1 - x0 - 6f; l.style.height = RowH;
                        l.style.display = DisplayStyle.Flex;
                    }
                }
            }
            for (int i = li; i < _labels.Count; i++) _labels[i].style.display = DisplayStyle.None;
            _deeper.text = deeper > 0 ? $"+{deeper} deeper" : "";

            int ti = 0;
            if (count > 0 && viewSpanMs > 0)
            {
                double step = NiceStep(viewSpanMs / Mathf.Max(2, (int)(Width / 70f)));
                float ay = resolvedStyle.height - AxisH;
                if (step > 0 && !float.IsNaN(ay))
                    for (double t = Math.Ceiling(viewStartMs / step) * step; t <= viewStartMs + viewSpanMs + 1e-9 && ti < 64; t += step)
                    {
                        var l = Get(_ticks, ti++, "up-timeline-tick");
                        l.text = t.ToString(step < 0.01 ? "0.####" : "0.##") + " ms";
                        l.style.left = XOf(t) + 2f; l.style.top = ay; l.style.display = DisplayStyle.Flex;
                    }
            }
            for (int i = ti; i < _ticks.Count; i++) _ticks[i].style.display = DisplayStyle.None;
            MarkDirtyRepaint();
            _hoverLayer.MarkDirtyRepaint();
        }

        Label Get(List<Label> list, int i, string cls)
        {
            if (i < list.Count) return list[i];
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.AddToClassList(cls);
            list.Add(l);
            Add(l);
            _deeper.BringToFront();
            _tip.BringToFront();
            return l;
        }

        static double NiceStep(double raw)
        {
            if (raw <= 0 || double.IsNaN(raw)) return 0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * mag;
        }

        int HitTest(Vector2 p)
        {
            if (data == null || viewSpanMs <= 0) return -1;
            int row = Mathf.FloorToInt(p.y / RowH);
            if (row < 0 || row >= visibleRows || row >= _rowCount) return -1;
            var r = _rows[row];
            int k = FirstAtOrBefore(r, MsAt(p.x));
            if (k < 0) return -1;
            int idx = r[k];
            var it = data.items[idx];
            return p.x <= XOf(it.startMs + it.ms) + 1f ? idx : -1;
        }

        void OnDown(PointerDownEvent e)
        {
            Focus();
            if (e.button == 0 && e.clickCount == 2)
            {
                int h = HitTest(e.localPosition);
                if (h >= 0) BarDoubleClicked?.Invoke(data.items[h]);
                else Fit();
                e.StopPropagation();
                return;
            }
            if (e.button != 0 && e.button != 2) return;
            pressed = true;
            panning = e.button == 2;
            pressX = e.localPosition.x;
            pressViewStart = viewStartMs;
            this.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        void OnMove(PointerMoveEvent e)
        {
            lastPos = e.localPosition;
            pointerIn = true;
            if (pressed && this.HasPointerCapture(e.pointerId) && (e.pressedButtons & 5) != 0)
            {
                float dx = e.localPosition.x - pressX;
                if (!panning && Mathf.Abs(dx) > 3f) panning = true;
                if (panning)
                {
                    viewStartMs = pressViewStart - dx / Width * viewSpanMs;
                    ClampView();
                    SetHover(-1, Vector2.zero);
                    Layout();
                    return;
                }
            }
            SetHover(HitTest(e.localPosition), e.localPosition);
        }

        void OnUp(PointerUpEvent e)
        {
            if (!pressed) return;
            bool click = !panning && e.button == 0;
            pressed = false;
            panning = false;
            if (this.HasPointerCapture(e.pointerId)) this.ReleasePointer(e.pointerId);
            if (!click) return;
            int h = HitTest(e.localPosition);
            if (h >= 0) BarClicked?.Invoke(data.items[h]);
        }

        void OnWheel(WheelEvent e)
        {
            if (totalMs <= 0 || viewSpanMs <= 0) return;
            float x = e.localMousePosition.x;
            float d = e.delta.y != 0 ? e.delta.y : e.delta.x;
            if (e.ctrlKey || e.commandKey)
            {
                double t = MsAt(x);
                double span = viewSpanMs * (e.delta.y < 0 ? 1 / 1.25 : 1.25);
                viewSpanMs = Math.Min(Math.Max(span, Math.Min(MinSpanMs, totalMs)), totalMs);
                viewStartMs = t - (x - PadX) / Width * viewSpanMs;
            }
            else if (d != 0)
            {
                viewStartMs += Math.Sign(d) * viewSpanMs * 0.1;
            }
            ClampView();
            SetHover(HitTest(e.localMousePosition), e.localMousePosition);
            Layout();
            e.StopPropagation();
        }

        void OnKey(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.LeftArrow) { StepRequested?.Invoke(-1); e.StopPropagation(); }
            else if (e.keyCode == KeyCode.RightArrow) { StepRequested?.Invoke(1); e.StopPropagation(); }
        }

        void SetHover(int h, Vector2 p)
        {
            if (h != hover)
            {
                hover = h;
                _hoverLayer.MarkDirtyRepaint();
            }
            if (h < 0 || data == null) { _tip.style.display = DisplayStyle.None; return; }
            var it = data.items[h];
            var info = UdonProfilerRecorder.GetInfo(it.behaviourId);
            string script = info?.script ?? (it.scriptId >= 0 ? UdonProfilerRecorder.ScriptName(it.scriptId) : null) ?? "?";
            string capped = data.droppedSpans > 0 ? " (spans capped)" : "";
            string instr = data.hasInstructions ? $"\n{it.instructions:N0} instr  self {it.selfInstructions:N0}" : "";
            if (it.kind == PathKind.Method)
                _tip.text = $"{it.label}()\n{script}\n{it.ms:0.000} ms  self {it.selfMs:0.000} ms{capped}{instr}";
            else
                _tip.text = $"{it.label}\n{script} | {info?.objectPath ?? "?"}\n{it.ms:0.000} ms  self {it.selfMs:0.000} ms{capped}{instr}\nextern (incl. methods) {it.externMs:0.000} ms ({it.externCount}, {it.fusedCount} fused)"
                    + (it.halted ? "\nhalted" : "");
            _tip.style.display = DisplayStyle.Flex;
            _tip.style.left = Mathf.Clamp(p.x + 12f, 0f, Mathf.Max(0f, resolvedStyle.width - 260f));
            _tip.style.top = Mathf.Clamp(p.y + 12f, 0f, Mathf.Max(0f, resolvedStyle.height - 70f));
        }

        void Paint(MeshGenerationContext ctx)
        {
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (w <= 0 || h <= 0) return;
            float ay = h - AxisH;
            _quads.Begin(ctx);
            _quads.Add(0, ay, w, ay + 1f, UdonProfilerPalette.Axis);
            int count = data == null ? 0 : data.items.Count;
            if (count > 0 && viewSpanMs > 0)
            {
                double step = NiceStep(viewSpanMs / Mathf.Max(2, (int)(Width / 70f)));
                if (step > 0)
                    for (double t = Math.Ceiling(viewStartMs / step) * step; t <= viewStartMs + viewSpanMs + 1e-9; t += step)
                    {
                        float x = XOf(t);
                        _quads.Add(x, ay, x + 1f, ay + 4f, UdonProfilerPalette.Axis);
                    }

                var items = data.items;
                double viewEnd = viewStartMs + viewSpanMs;
                float left = PadX, right = PadX + Width;
                Color32 bad = UdonProfilerPalette.Bad;
                for (int d = 0; d < _rowCount && d < visibleRows; d++)
                {
                    var row = _rows[d];
                    float y0 = d * RowH;
                    if (y0 + RowH > ay) break;
                    int start = Math.Max(0, FirstAtOrBefore(row, viewStartMs));
                    for (int r = start; r < row.Count; r++)
                    {
                        var it = items[row[r]];
                        if (it.startMs > viewEnd) break;
                        float x0 = XOf(it.startMs), x1 = XOf(it.startMs + it.ms);
                        if (x1 < left) continue;
                        if (x1 - x0 < 0.5f)
                        {
                            if (d != 0 || it.kind != PathKind.Event) continue;
                            x1 = x0 + 1f;
                        }
                        x0 = Mathf.Max(left, x0);
                        x1 = Mathf.Min(right, x1);
                        if (x1 <= x0) continue;
                        float gx1 = x1 - x0 > 2f ? x1 - 0.5f : x1;
                        var c = it.kind == PathKind.Method ? UdonProfilerPalette.Method(it.scriptId) : UdonProfilerPalette.Script(it.scriptId);
                        _quads.Add(x0, y0 + 1f, gx1, y0 + RowH - 1f, c);
                        if (it.halted && it.kind == PathKind.Event) _quads.Add(x0, y0 + 1f, gx1, y0 + 2f, bad);
                    }
                }
            }
            _quads.End();
        }

        void PaintHover(MeshGenerationContext ctx)
        {
            if (hover < 0 || data == null || hover >= data.items.Count) return;
            var it = data.items[hover];
            if (it.depth >= visibleRows) return;
            float x0 = Mathf.Max(PadX, XOf(it.startMs)), x1 = Mathf.Min(PadX + Width, Mathf.Max(XOf(it.startMs) + 1f, XOf(it.startMs + it.ms)));
            if (x1 <= x0) return;
            float y0 = it.depth * RowH;
            _hoverQuads.Begin(ctx);
            _hoverQuads.Add(x0, y0 + 1f, x1, y0 + RowH - 1f, UdonProfilerPalette.Hover);
            _hoverQuads.End();
        }
    }
}
