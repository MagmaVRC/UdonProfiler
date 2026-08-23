using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MagmaVRC.UdonProfiler
{
    public sealed class UdonProfilerTimeline : VisualElement
    {
        public event Action<int, uint> BarClicked;

        const float RowH = 14f;
        const float AxisH = 14f;
        const float PadX = 4f;

        static readonly Color[] Palette =
        {
            new Color(0.36f, 0.52f, 0.70f), new Color(0.42f, 0.64f, 0.50f), new Color(0.72f, 0.55f, 0.36f),
            new Color(0.62f, 0.46f, 0.66f), new Color(0.70f, 0.42f, 0.44f), new Color(0.40f, 0.62f, 0.66f),
            new Color(0.66f, 0.64f, 0.38f), new Color(0.50f, 0.50f, 0.62f),
        };
        static readonly Color Axis = new Color(1f, 1f, 1f, 0.18f);

        readonly Label _tip, _empty, _title;
        readonly List<Label> _labels = new List<Label>();
        List<UdonProfilerModel.TimelineItem> items = new List<UdonProfilerModel.TimelineItem>();
        readonly List<Label> _ticks = new List<Label>();
        double totalMs;
        int frame = -1;
        int hover = -1;

        public UdonProfilerTimeline()
        {
            AddToClassList("up-timeline");
            _empty = new Label("no Udon this frame");
            _empty.AddToClassList("up-empty");
            _empty.pickingMode = PickingMode.Ignore;
            Add(_empty);
            _title = new Label();
            _title.AddToClassList("up-chart-max");
            _title.pickingMode = PickingMode.Ignore;
            Add(_title);
            _tip = new Label();
            _tip.AddToClassList("up-chart-tooltip");
            _tip.pickingMode = PickingMode.Ignore;
            _tip.style.display = DisplayStyle.None;
            Add(_tip);
            generateVisualContent += Paint;
            RegisterCallback<GeometryChangedEvent>(_ => Layout());
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerLeaveEvent>(_ => { hover = -1; _tip.style.display = DisplayStyle.None; MarkDirtyRepaint(); });
            RegisterCallback<ClickEvent>(_ => { if (hover >= 0) BarClicked?.Invoke(items[hover].behaviourId, items[hover].entry); });
        }

        public void SetFrame(int frameIndex, int frameNumber)
        {
            _title.text = $"frame {frameNumber}";
            if (frameIndex == frame && frameIndex < UdonProfilerRecorder.NewestFrame) return;
            frame = frameIndex;
            items = UdonProfilerModel.Timeline(frameIndex);
            totalMs = 0;
            for (int i = 0; i < items.Count; i++)
            {
                double end = items[i].startMs + items[i].ms;
                if (end > totalMs) totalMs = end;
            }
            hover = -1;
            Layout();
        }

        float Width => Mathf.Max(1f, resolvedStyle.width - PadX * 2);
        float XOf(double ms) => PadX + (float)(totalMs <= 0 ? 0 : ms / totalMs) * Width;

        void Layout()
        {
            _empty.style.display = items.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            int li = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                float x0 = XOf(it.startMs), x1 = XOf(it.startMs + it.ms);
                if (x1 - x0 < 40f) continue;
                var l = Get(_labels, li++, "up-timeline-label");
                var info = UdonProfilerRecorder.GetInfo(it.behaviourId);
                l.text = (info?.script ?? "?") + " | " + UdonProfilerRecorder.EventName(it.behaviourId, it.entry);
                l.style.left = x0 + 3f; l.style.top = it.depth * RowH; l.style.width = x1 - x0 - 6f; l.style.height = RowH;
                l.style.display = DisplayStyle.Flex;
            }
            for (int i = li; i < _labels.Count; i++) _labels[i].style.display = DisplayStyle.None;

            int ti = 0;
            if (items.Count > 0)
            {
                double step = NiceStep(totalMs / Mathf.Max(2, (int)(Width / 70f)));
                float ay = resolvedStyle.height - AxisH;
                for (double t = 0; t <= totalMs + 1e-9 && step > 0; t += step)
                {
                    var l = Get(_ticks, ti++, "up-timeline-tick");
                    l.text = t.ToString("0.##") + " ms";
                    l.style.left = XOf(t) + 2f; l.style.top = ay; l.style.display = DisplayStyle.Flex;
                }
            }
            for (int i = ti; i < _ticks.Count; i++) _ticks[i].style.display = DisplayStyle.None;
            MarkDirtyRepaint();
        }

        Label Get(List<Label> list, int i, string cls)
        {
            if (i < list.Count) return list[i];
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.AddToClassList(cls);
            list.Add(l);
            Add(l);
            _tip.BringToFront();
            return l;
        }

        static double NiceStep(double raw)
        {
            if (raw <= 0) return 0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * mag;
        }

        int HitTest(Vector2 p)
        {
            int row = (int)(p.y / RowH);
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var it = items[i];
                if (it.depth != row) continue;
                if (p.x >= XOf(it.startMs) && p.x <= XOf(it.startMs + it.ms)) return i;
            }
            return -1;
        }

        void OnMove(PointerMoveEvent e)
        {
            int h = HitTest(e.localPosition);
            if (h != hover) { hover = h; MarkDirtyRepaint(); }
            if (h < 0) { _tip.style.display = DisplayStyle.None; return; }
            var it = items[h];
            var info = UdonProfilerRecorder.GetInfo(it.behaviourId);
            _tip.text = $"{info?.script ?? "?"}\n{info?.objectPath ?? "?"}\n{UdonProfilerRecorder.EventName(it.behaviourId, it.entry)}\n{it.ms:0.000} ms | extern {it.externMs:0.000} ms";
            _tip.style.display = DisplayStyle.Flex;
            _tip.style.left = Mathf.Clamp(e.localPosition.x + 12f, 0f, Mathf.Max(0f, resolvedStyle.width - 220f));
            _tip.style.top = Mathf.Clamp(e.localPosition.y + 12f, 0f, Mathf.Max(0f, resolvedStyle.height - 60f));
        }

        static Color ColorFor(string script)
        {
            int h = 0;
            if (script != null) for (int i = 0; i < script.Length; i++) h = unchecked(h * 31 + script[i]);
            return Palette[(h & 0x7fffffff) % Palette.Length];
        }

        void Paint(MeshGenerationContext ctx)
        {
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (w <= 0 || h <= 0) return;
            var p = ctx.painter2D;
            float ay = h - AxisH;
            p.strokeColor = Axis; p.lineWidth = 1f;
            p.BeginPath(); p.MoveTo(new Vector2(0, ay)); p.LineTo(new Vector2(w, ay)); p.Stroke();
            if (items.Count == 0) return;

            double step = NiceStep(totalMs / Mathf.Max(2, (int)(Width / 70f)));
            for (double t = 0; t <= totalMs + 1e-9 && step > 0; t += step)
            {
                float x = XOf(t);
                p.BeginPath(); p.MoveTo(new Vector2(x, ay)); p.LineTo(new Vector2(x, ay + 4f)); p.Stroke();
            }

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                float x0 = XOf(it.startMs), x1 = Mathf.Max(x0 + 1f, XOf(it.startMs + it.ms));
                float y0 = it.depth * RowH;
                if (y0 + RowH > ay) continue;
                p.fillColor = i == hover ? Color.Lerp(ColorFor(UdonProfilerRecorder.GetInfo(it.behaviourId)?.script), Color.white, 0.25f) : ColorFor(UdonProfilerRecorder.GetInfo(it.behaviourId)?.script);
                Rect(p, x0, y0 + 1f, x1 - 0.5f, y0 + RowH - 1f);
            }
        }

        static void Rect(Painter2D p, float x0, float y0, float x1, float y1)
        {
            p.BeginPath();
            p.MoveTo(new Vector2(x0, y0)); p.LineTo(new Vector2(x1, y0));
            p.LineTo(new Vector2(x1, y1)); p.LineTo(new Vector2(x0, y1));
            p.ClosePath();
            p.Fill();
        }
    }
}
