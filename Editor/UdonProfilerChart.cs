using System;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace MagmaVRC.UdonProfiler
{
    internal sealed class UdonProfilerChart : VisualElement
    {
        public event Action<int> FrameClicked;
        public event Action<int, int> RangeSelected;
        public event Action<int> StepRequested;

        public bool AutoScroll = true;
        public float Scale = 4f;
        public bool LoopOnly;

        const float MinPx = 1f, MaxPx = 24f;

        readonly Label _tip, _max, _warnLabel, _badLabel;
        readonly VisualElement _hoverLayer;
        readonly UdonProfilerQuads _quads = new UdonProfilerQuads();
        readonly UdonProfilerQuads _hoverQuads = new UdonProfilerQuads();
        readonly StringBuilder _sb = new StringBuilder();
        readonly int[] _top = new int[3];

        float pxPerFrame = 5f;
        float scrollX;
        int oldest, count;
        double budgetTop = 1;
        int selectedFrame = -1;
        int rangeA = -1, rangeB = -1;
        int dragStart = -1;
        bool dragging;
        int hover = -1;
        double anchorSerial = double.NaN;
        float lastX;
        bool pointerIn;

        public bool Dragging => dragging;

        public UdonProfilerChart()
        {
            AddToClassList("up-chart");
            focusable = true;
            _hoverLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            _hoverLayer.style.position = Position.Absolute;
            _hoverLayer.style.left = 0; _hoverLayer.style.top = 0; _hoverLayer.style.right = 0; _hoverLayer.style.bottom = 0;
            _hoverLayer.generateVisualContent += PaintHover;
            Add(_hoverLayer);
            _warnLabel = BudgetLabel("2 ms Quest");
            _badLabel = BudgetLabel("5 ms Quest");
            _max = new Label { pickingMode = PickingMode.Ignore };
            _max.AddToClassList("up-chart-max");
            Add(_max);
            _tip = new Label { pickingMode = PickingMode.Ignore };
            _tip.AddToClassList("up-chart-tooltip");
            _tip.style.display = DisplayStyle.None;
            Add(_tip);
            generateVisualContent += Paint;
            RegisterCallback<GeometryChangedEvent>(_ => { if (AutoScroll) scrollX = MaxScroll(); Relayout(); });
            RegisterCallback<PointerDownEvent>(OnDown);
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerUpEvent>(OnUp);
            RegisterCallback<PointerLeaveEvent>(_ => { pointerIn = false; SetHover(-1, 0); });
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<KeyDownEvent>(OnKey);
        }

        Label BudgetLabel(string text)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("up-chart-budget");
            Add(l);
            return l;
        }

        public void Refresh()
        {
            count = UdonProfilerRecorder.FrameCount;
            oldest = UdonProfilerRecorder.NewestFrame - count + 1;
            if (AutoScroll) scrollX = MaxScroll();
            else
            {
                if (!double.IsNaN(anchorSerial)) scrollX = (float)((anchorSerial - oldest) * pxPerFrame);
                scrollX = Mathf.Clamp(scrollX, 0f, MaxScroll());
                anchorSerial = oldest + scrollX / (double)pxPerFrame;
            }
            if (pointerIn && !dragging) SetHover(SerialAt(lastX), lastX);
            else if (hover >= 0 && (hover < oldest || hover >= oldest + count)) SetHover(-1, 0);
            Relayout();
        }

        void StoreAnchor() => anchorSerial = AutoScroll ? double.NaN : oldest + scrollX / (double)pxPerFrame;

        public void SetSelection(int frame, int rangeFrom, int rangeTo)
        {
            selectedFrame = frame;
            rangeA = rangeFrom;
            rangeB = rangeTo;
            MarkDirtyRepaint();
        }

        float Width => resolvedStyle.width;
        float MaxScroll() => Mathf.Max(0f, count * pxPerFrame - (float.IsNaN(Width) ? 0f : Width));

        int SerialAt(float x)
        {
            int i = Mathf.FloorToInt((x + scrollX) / pxPerFrame);
            return i < 0 || i >= count ? -1 : oldest + i;
        }

        int SerialAtClamped(float x)
        {
            if (count == 0) return dragStart;
            return oldest + Mathf.Clamp(Mathf.FloorToInt((x + scrollX) / pxPerFrame), 0, count - 1);
        }

        float XOf(int serial) => (serial - oldest) * pxPerFrame - scrollX;

        double FrameMs(FrameSummary s) => (LoopOnly ? s.loopTicks : s.totalTicks) * UdonProfilerRecorder.MsPerTick;

        void VisibleRange(out int first, out int last)
        {
            float w = float.IsNaN(Width) ? 0f : Width;
            first = Mathf.Max(0, Mathf.FloorToInt(scrollX / pxPerFrame));
            last = Mathf.Min(count - 1, Mathf.FloorToInt((scrollX + w) / pxPerFrame) + 1);
        }

        void Relayout()
        {
            float h = resolvedStyle.height;
            double peak = 0.1;
            VisibleRange(out int first, out int last);
            for (int i = first; i <= last; i++)
            {
                var s = UdonProfilerModel.Summary(oldest + i);
                if (s == null) continue;
                double ms = FrameMs(s);
                if (ms > peak) peak = ms;
            }
            double scale = Math.Max(0.01, Scale);
            double pause = UdonProfilerRecorder.PauseAboveMs;
            budgetTop = Math.Max(peak, 5.0 / scale);
            if (pause > 0) budgetTop = Math.Max(budgetTop, pause * 1.1);
            _max.text = count == 0 ? "" : $"max {peak:0.00} ms";
            PlaceBudget(_warnLabel, 2.0 / scale, h);
            PlaceBudget(_badLabel, 5.0 / scale, h);
            MarkDirtyRepaint();
            _hoverLayer.MarkDirtyRepaint();
        }

        void PlaceBudget(Label l, double ms, float h)
        {
            if (float.IsNaN(h) || h <= 0 || count == 0) { l.style.display = DisplayStyle.None; return; }
            float y = YOf(ms, h);
            l.style.display = y >= 10f ? DisplayStyle.Flex : DisplayStyle.None;
            l.style.top = y - 12f;
        }

        float YOf(double ms, float h) => h - (float)(ms * (h - 4f) / budgetTop);

        void OnDown(PointerDownEvent e)
        {
            Focus();
            if (e.button != 0) return;
            int s = SerialAt(e.localPosition.x);
            dragging = s >= 0;
            if (!dragging) return;
            dragStart = s;
            this.CapturePointer(e.pointerId);
        }

        void OnMove(PointerMoveEvent e)
        {
            lastX = e.localPosition.x;
            pointerIn = true;
            SetHover(SerialAt(lastX), lastX);
            if (dragging)
            {
                int s = SerialAtClamped(lastX);
                rangeA = Math.Min(dragStart, s);
                rangeB = Math.Max(dragStart, s);
                selectedFrame = -1;
                MarkDirtyRepaint();
            }
        }

        void OnUp(PointerUpEvent e)
        {
            if (!dragging) return;
            dragging = false;
            this.ReleasePointer(e.pointerId);
            int s = SerialAtClamped(e.localPosition.x);
            if (s == dragStart)
            {
                selectedFrame = s;
                rangeA = rangeB = -1;
                FrameClicked?.Invoke(s);
            }
            else
            {
                selectedFrame = -1;
                RangeSelected?.Invoke(Math.Min(dragStart, s), Math.Max(dragStart, s));
            }
            MarkDirtyRepaint();
        }

        void OnWheel(WheelEvent e)
        {
            float x = e.localMousePosition.x;
            if (e.ctrlKey || e.commandKey)
            {
                float old = pxPerFrame;
                float idx = (x + scrollX) / old;
                pxPerFrame = Mathf.Clamp(old * (e.delta.y < 0 ? 1.25f : 0.8f), MinPx, MaxPx);
                scrollX = idx * pxPerFrame - x;
            }
            else
            {
                float d = e.delta.y != 0 ? e.delta.y : e.delta.x;
                scrollX += d * 10f;
            }
            float max = MaxScroll();
            scrollX = Mathf.Clamp(scrollX, 0f, max);
            AutoScroll = scrollX >= max - 0.5f;
            StoreAnchor();
            if (hover >= 0) SetHover(SerialAt(x), x);
            Relayout();
            e.StopPropagation();
        }

        void OnKey(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.LeftArrow) { StepRequested?.Invoke(-1); e.StopPropagation(); }
            else if (e.keyCode == KeyCode.RightArrow) { StepRequested?.Invoke(1); e.StopPropagation(); }
        }

        void SetHover(int serial, float x)
        {
            if (serial != hover)
            {
                hover = serial;
                _hoverLayer.MarkDirtyRepaint();
            }
            var s = serial >= 0 ? UdonProfilerModel.Summary(serial) : null;
            if (s == null) { _tip.style.display = DisplayStyle.None; return; }
            _tip.text = Readout(s);
            _tip.style.display = DisplayStyle.Flex;
            _tip.style.left = Mathf.Clamp(x + 12f, 0f, Mathf.Max(0f, Width - 260f));
            _tip.style.top = 4f;
        }

        string Readout(FrameSummary s)
        {
            int events = LoopOnly ? s.loopEvents : s.events;
            int externs = LoopOnly ? s.loopExterns : s.externs;
            int fused = LoopOnly ? s.loopFused : s.fused;
            _sb.Clear();
            _sb.Append("Frame ").Append(s.frameNumber).Append("  ").Append(FrameMs(s).ToString("0.00")).Append(" ms  ")
               .Append(events).Append(" events  ").Append(externs).Append(" externs (").Append(fused).Append(" fused)");
            var ticks = LoopOnly ? s.segLoopTicks : s.segTicks;
            int n = 0;
            for (int i = 0; i < s.segmentCount; i++)
            {
                if (ticks[i] <= 0) continue;
                int pos;
                if (n < 3) pos = n++;
                else if (ticks[i] > ticks[_top[2]]) pos = 2;
                else continue;
                while (pos > 0 && ticks[_top[pos - 1]] < ticks[i]) { _top[pos] = _top[pos - 1]; pos--; }
                _top[pos] = i;
            }
            if (n > 0)
            {
                _sb.Append("\nTop: ");
                for (int k = 0; k < n; k++)
                {
                    int i = _top[k];
                    if (k > 0) _sb.Append(", ");
                    _sb.Append(ScriptLabel(s.segScript[i])).Append(' ').Append((ticks[i] * UdonProfilerRecorder.MsPerTick).ToString("0.00"));
                }
            }
            return _sb.ToString();
        }

        static string ScriptLabel(int scriptId) => scriptId < 0 ? "(other)" : UdonProfilerRecorder.ScriptName(scriptId) ?? "?";

        void Paint(MeshGenerationContext ctx)
        {
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (w <= 0 || h <= 0) return;
            _quads.Begin(ctx);

            Color32 grid = UdonProfilerPalette.Grid;
            for (int g = 1; g <= 3; g++)
            {
                float y = YOf(g * budgetTop / 4, h);
                _quads.Add(0, y, w, y + 1f, grid);
            }

            if (count > 0)
            {
                double msPerTick = UdonProfilerRecorder.MsPerTick;
                float yPerMs = (float)((h - 4f) / budgetTop);
                float bw = Mathf.Max(1f, pxPerFrame - 1f);
                VisibleRange(out int first, out int last);
                for (int i = first; i <= last; i++)
                {
                    var s = UdonProfilerModel.Summary(oldest + i);
                    if (s == null) continue;
                    float x0 = i * pxPerFrame - scrollX;
                    float x1 = Mathf.Min(w, x0 + bw);
                    x0 = Mathf.Max(0f, x0);
                    if (x1 <= x0) continue;
                    var ticks = LoopOnly ? s.segLoopTicks : s.segTicks;
                    float total = (float)(FrameMs(s) * yPerMs);
                    if (total <= 0f) continue;
                    if (total < 1f)
                    {
                        int big = -1;
                        for (int k = 0; k < s.segmentCount; k++) if (big < 0 || ticks[k] > ticks[big]) big = k;
                        _quads.Add(x0, h - 1f, x1, h, UdonProfilerPalette.Script(big >= 0 ? s.segScript[big] : -1));
                        continue;
                    }
                    float y = h, carry = 0f;
                    for (int k = 0; k < s.segmentCount; k++)
                    {
                        if (ticks[k] <= 0) continue;
                        float hh = carry + (float)(ticks[k] * msPerTick * yPerMs);
                        if (hh < 0.5f && k < s.segmentCount - 1) { carry = hh; continue; }
                        carry = 0f;
                        _quads.Add(x0, Mathf.Max(0f, y - hh), x1, y, UdonProfilerPalette.Script(s.segScript[k]));
                        y -= hh;
                    }
                }
            }

            double scale = Math.Max(0.01, Scale);
            Dashes(YOf(2.0 / scale, h), w, UdonProfilerPalette.Warn);
            Dashes(YOf(5.0 / scale, h), w, UdonProfilerPalette.Bad);
            double pause = UdonProfilerRecorder.PauseAboveMs;
            if (pause > 0) Dashes(YOf(pause, h), w, UdonProfilerPalette.Lighten(UdonProfilerPalette.Bad, 0.35f));

            if (rangeA >= 0 && rangeB >= rangeA) Overlay(rangeA, rangeB, w, h, UdonProfilerPalette.Sel);
            else if (selectedFrame >= 0) Overlay(selectedFrame, selectedFrame, w, h, UdonProfilerPalette.Sel);
            _quads.End();
        }

        void Dashes(float y, float w, Color c)
        {
            if (y < 0 || float.IsNaN(y)) return;
            var c32 = (Color32)new Color(c.r, c.g, c.b, 0.8f);
            for (float x = 0; x < w; x += 8f) _quads.Add(x, y, Mathf.Min(w, x + 4f), y + 1f, c32);
        }

        void Overlay(int a, int b, float w, float h, Color c)
        {
            if (b < oldest || a >= oldest + count) return;
            float x0 = Mathf.Max(0f, XOf(Math.Max(a, oldest)) - 0.5f);
            float x1 = Mathf.Min(w, XOf(Math.Min(oldest + count - 1, b) + 1) - 0.5f);
            _quads.Add(x0, 0, x1, h, c);
        }

        void PaintHover(MeshGenerationContext ctx)
        {
            if (hover < oldest || hover >= oldest + count) return;
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (w <= 0 || h <= 0) return;
            float x0 = Mathf.Max(0f, XOf(hover) - 0.5f), x1 = Mathf.Min(w, XOf(hover + 1) - 0.5f);
            _hoverQuads.Begin(ctx);
            _hoverQuads.Add(x0, 0, x1, h, UdonProfilerPalette.Hover);
            _hoverQuads.End();
        }
    }
}
