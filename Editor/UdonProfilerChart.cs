using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace MagmaVRC.UdonProfiler
{
    public sealed class UdonProfilerChart : VisualElement
    {
        public event Action<int> FrameClicked;
        public event Action<int, int> RangeSelected;

        public bool AutoScroll = true;
        public float Scale = 4f;

        const float BarWidth = 4f;
        const float Gap = 1f;
        const float Stride = BarWidth + Gap;

        static readonly Color Ok = new Color(0.30f, 0.78f, 0.46f);
        static readonly Color Warn = new Color(0.95f, 0.70f, 0.20f);
        static readonly Color Bad = new Color(0.92f, 0.30f, 0.30f);
        static readonly Color Sel = new Color(1f, 1f, 1f, 0.22f);
        static readonly Color Grid = new Color(1f, 1f, 1f, 0.06f);

        readonly Label _tip, _max;
        double[] ms = Array.Empty<double>();
        int[] events = Array.Empty<int>();
        int fromFrame;
        int count;
        float scrollX;
        int selectedFrame = -1;
        int rangeA = -1, rangeB = -1;
        int dragStartFrame = -1;
        bool dragging;
        int[] frameNumbers = Array.Empty<int>();

        public bool Dragging => dragging;
        int DragStart => Mathf.Clamp(dragStartFrame - fromFrame, 0, Mathf.Max(0, count - 1));

        public UdonProfilerChart()
        {
            AddToClassList("up-chart");
            _tip = new Label();
            _tip.AddToClassList("up-chart-tooltip");
            _tip.pickingMode = PickingMode.Ignore;
            _tip.style.display = DisplayStyle.None;
            Add(_tip);
            _max = new Label();
            _max.AddToClassList("up-chart-max");
            _max.pickingMode = PickingMode.Ignore;
            Add(_max);
            generateVisualContent += Paint;
            RegisterCallback<GeometryChangedEvent>(_ => MarkDirtyRepaint());
            RegisterCallback<PointerDownEvent>(OnDown);
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerUpEvent>(OnUp);
            RegisterCallback<PointerLeaveEvent>(_ => _tip.style.display = DisplayStyle.None);
            RegisterCallback<WheelEvent>(OnWheel);
        }

        public void SetData(ProfileView view)
        {
            ms = view?.frameMs ?? Array.Empty<double>();
            events = view?.frameEvents ?? Array.Empty<int>();
            frameNumbers = view?.frameNumbers ?? Array.Empty<int>();
            fromFrame = view?.fromFrame ?? 0;
            count = ms.Length;
            _max.text = count == 0 ? "" : $"max {view.maxMs:0.00} ms";
            if (AutoScroll) scrollX = Mathf.Max(0f, count * Stride - resolvedStyle.width);
            MarkDirtyRepaint();
        }

        public void SetSelection(int frame, int rangeFrom, int rangeTo)
        {
            selectedFrame = frame;
            rangeA = rangeFrom;
            rangeB = rangeTo;
            MarkDirtyRepaint();
        }

        int IndexAt(float x)
        {
            int i = (int)((x + scrollX) / Stride);
            return i < 0 || i >= count ? -1 : i;
        }

        void OnDown(PointerDownEvent e)
        {
            if (e.button != 0) return;
            int i = IndexAt(e.localPosition.x);
            dragging = i >= 0;
            if (!dragging) return;
            dragStartFrame = fromFrame + i;
            this.CapturePointer(e.pointerId);
        }

        void OnMove(PointerMoveEvent e)
        {
            int i = IndexAt(e.localPosition.x);
            if (i < 0)
            {
                _tip.style.display = DisplayStyle.None;
            }
            else
            {
                _tip.text = $"Frame {(i < frameNumbers.Length ? frameNumbers[i] : fromFrame + i)}  {ms[i]:0.00} ms  {(i < events.Length ? events[i] : 0)} events";
                _tip.style.display = DisplayStyle.Flex;
                float tx = Mathf.Clamp(e.localPosition.x + 12f, 0f, Mathf.Max(0f, resolvedStyle.width - 130f));
                _tip.style.left = tx;
                _tip.style.top = 4f;
            }
            if (dragging && i >= 0)
            {
                rangeA = Mathf.Min(DragStart, i) + fromFrame;
                rangeB = Mathf.Max(DragStart, i) + fromFrame;
                MarkDirtyRepaint();
            }
        }

        void OnUp(PointerUpEvent e)
        {
            if (!dragging) return;
            dragging = false;
            this.ReleasePointer(e.pointerId);
            int start = DragStart;
            int i = IndexAt(e.localPosition.x);
            if (i < 0) i = start;
            if (i == start)
            {
                selectedFrame = fromFrame + i;
                rangeA = rangeB = -1;
                FrameClicked?.Invoke(selectedFrame);
            }
            else
            {
                selectedFrame = -1;
                RangeSelected?.Invoke(Mathf.Min(start, i) + fromFrame, Mathf.Max(start, i) + fromFrame);
            }
            MarkDirtyRepaint();
        }

        void OnWheel(WheelEvent e)
        {
            float max = Mathf.Max(0f, count * Stride - resolvedStyle.width);
            scrollX = Mathf.Clamp(scrollX + e.delta.y * 10f, 0f, max);
            AutoScroll = scrollX >= max - 0.5f;
            MarkDirtyRepaint();
            e.StopPropagation();
        }

        void Paint(MeshGenerationContext ctx)
        {
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (w <= 0 || h <= 0 || count == 0) return;
            var p = ctx.painter2D;
            double peak = 0.1;
            for (int i = 0; i < count; i++) if (ms[i] > peak) peak = ms[i];
            double pause = UdonProfilerRecorder.PauseAboveMs;
            double budgetTop = Math.Max(peak, 5.0 / Scale);
            if (pause > 0) budgetTop = Math.Max(budgetTop, pause * 1.1);
            float yScale = (float)((h - 4f) / budgetTop);

            p.strokeColor = Grid;
            p.lineWidth = 1f;
            for (int g = 1; g <= 3; g++)
            {
                float y = h - (float)(g * budgetTop / 4 * yScale);
                p.BeginPath(); p.MoveTo(new Vector2(0, y)); p.LineTo(new Vector2(w, y)); p.Stroke();
            }

            int first = Mathf.Max(0, (int)(scrollX / Stride));
            int last = Mathf.Min(count - 1, (int)((scrollX + w) / Stride) + 1);
            for (int i = first; i <= last; i++)
            {
                float x = i * Stride - scrollX;
                float bh = Mathf.Max(1f, (float)ms[i] * yScale);
                double scaled = ms[i] * Scale;
                p.fillColor = scaled < 2 ? Ok : scaled < 5 ? Warn : Bad;
                p.BeginPath();
                p.MoveTo(new Vector2(x, h));
                p.LineTo(new Vector2(x, h - bh));
                p.LineTo(new Vector2(x + BarWidth, h - bh));
                p.LineTo(new Vector2(x + BarWidth, h));
                p.ClosePath();
                p.Fill();
            }

            if (pause > 0)
            {
                float py = h - (float)(pause * yScale);
                p.strokeColor = Bad;
                for (float x = 0; x < w; x += 8f)
                {
                    p.BeginPath(); p.MoveTo(new Vector2(x, py)); p.LineTo(new Vector2(Mathf.Min(w, x + 4f), py)); p.Stroke();
                }
            }

            if (rangeA >= 0 && rangeB >= rangeA)
                Overlay(p, rangeA - fromFrame, rangeB - fromFrame, h);
            else if (selectedFrame >= 0)
                Overlay(p, selectedFrame - fromFrame, selectedFrame - fromFrame, h);
        }

        void Overlay(Painter2D p, int a, int b, float h)
        {
            if (b < 0 || a >= count) return;
            float x0 = Mathf.Max(0, a) * Stride - scrollX - Gap * 0.5f;
            float x1 = (Mathf.Min(count - 1, b) + 1) * Stride - scrollX - Gap * 0.5f;
            p.fillColor = Sel;
            p.BeginPath();
            p.MoveTo(new Vector2(x0, 0)); p.LineTo(new Vector2(x1, 0));
            p.LineTo(new Vector2(x1, h)); p.LineTo(new Vector2(x0, h));
            p.ClosePath();
            p.Fill();
        }
    }
}
