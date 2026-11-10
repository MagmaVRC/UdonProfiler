using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MagmaVRC.UdonProfiler
{
    internal sealed class UdonProfilerFlameGraph : VisualElement
    {
        public event Action<int> NodeSelected;

        const float RowH = 16f;
        const int MaxLabels = 300;
        const float LabelMinPx = 50f;

        static readonly Color RootColor = new Color(0.42f, 0.43f, 0.47f);

        readonly Label _tip, _empty;
        readonly VisualElement _hoverLayer;
        readonly List<Label> _labels = new List<Label>();
        readonly UdonProfilerQuads _quads = new UdonProfilerQuads();
        readonly UdonProfilerQuads _hoverQuads = new UdonProfilerQuads();
        readonly List<(NodeKind kind, string label)> _chain = new List<(NodeKind, string)>();
        readonly List<(NodeKind kind, string label)> _zoomChain = new List<(NodeKind, string)>();
        readonly List<int> _stack = new List<int>();

        FlameGraph data;
        int selected = -1;
        int hover = -1;
        Vector2 lastPos;
        bool pointerIn;
        string highlight;
        double viewStart, viewSpan;
        bool zoomed;
        double lastRootTotal;
        float assignedHeight = -1f;
        int labelCount;

        bool pressed, panning;
        float pressX;
        double pressViewStart;

        public FlameGraph Data => data;
        public int Selected => selected;

        public UdonProfilerFlameGraph()
        {
            focusable = true;
            style.minHeight = RowH;
            _hoverLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            _hoverLayer.style.position = Position.Absolute;
            _hoverLayer.style.left = 0; _hoverLayer.style.top = 0; _hoverLayer.style.right = 0; _hoverLayer.style.bottom = 0;
            _hoverLayer.generateVisualContent += PaintHover;
            Add(_hoverLayer);
            _empty = new Label("no data") { pickingMode = PickingMode.Ignore };
            _empty.AddToClassList("up-empty");
            Add(_empty);
            _tip = new Label { pickingMode = PickingMode.Ignore };
            _tip.AddToClassList("up-chart-tooltip");
            _tip.style.display = DisplayStyle.None;
            Add(_tip);
            generateVisualContent += Paint;
            RegisterCallback<GeometryChangedEvent>(_ => Layout());
            RegisterCallback<PointerDownEvent>(OnDown);
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerUpEvent>(OnUp);
            RegisterCallback<PointerLeaveEvent>(_ => { pointerIn = false; if (!pressed) SetHover(-1, Vector2.zero); });
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<KeyDownEvent>(OnKey);
        }

        public void SetData(FlameGraph g)
        {
            data = g;
            double total = Total;
            int resolved = ResolveChain(_chain);
            bool chainOk = _chain.Count == 0 || resolved >= 0;
            selected = resolved;
            if (selected < 0) _chain.Clear();
            int z = zoomed && _zoomChain.Count > 0 ? ResolveChain(_zoomChain) : -1;
            bool keep = zoomed && _zoomChain.Count == 0 && chainOk && lastRootTotal > 0 && Math.Abs(total - lastRootTotal) / lastRootTotal < 0.5;
            lastRootTotal = total;
            if (z >= 0 && data.nodes[z].size > 0)
            {
                viewStart = data.nodes[z].start;
                viewSpan = data.nodes[z].size;
                ClampView();
            }
            else if (keep) ClampView();
            else { zoomed = false; _zoomChain.Clear(); viewStart = 0; viewSpan = total; }
            float h = Mathf.Max(RowH, ((data?.maxDepth ?? 0) + 1) * RowH);
            if (h != assignedHeight)
            {
                assignedHeight = h;
                style.height = h;
            }
            Layout();
            if (pointerIn && !pressed) SetHover(HitTest(lastPos), lastPos);
            else SetHover(-1, Vector2.zero);
        }

        public void SetHighlight(string text)
        {
            string h = string.IsNullOrEmpty(text) ? null : text;
            if (h == highlight) return;
            highlight = h;
            MarkDirtyRepaint();
        }

        public void ResetZoom()
        {
            zoomed = false;
            _zoomChain.Clear();
            viewStart = 0;
            viewSpan = Total;
            Layout();
        }

        int Count => data?.count ?? 0;
        double Total => Count > 0 ? data.nodes[0].size : 0;

        void ClampView()
        {
            double total = Total;
            if (total <= 0) { viewStart = 0; viewSpan = 0; return; }
            viewSpan = Math.Min(Math.Max(viewSpan, total * 1e-6), total);
            viewStart = Math.Min(Math.Max(0, viewStart), total - viewSpan);
        }

        int ResolveChain(List<(NodeKind kind, string label)> chain)
        {
            if (chain.Count == 0 || Count == 0) return -1;
            ref readonly var root = ref data.nodes[0];
            if (root.kind != chain[0].kind || root.label != chain[0].label) return -1;
            int n = 0;
            for (int k = 1; k < chain.Count; k++)
            {
                int found = -1;
                for (int c = data.nodes[n].firstChild; c >= 0 && c < Count; c = data.nodes[c].nextSibling)
                {
                    ref readonly var cn = ref data.nodes[c];
                    if (cn.kind == chain[k].kind && cn.label == chain[k].label) { found = c; break; }
                }
                if (found < 0) return -1;
                n = found;
            }
            return n;
        }

        void ChainOf(int index, List<(NodeKind kind, string label)> chain)
        {
            chain.Clear();
            if (index < 0) return;
            _stack.Clear();
            for (int n = index; n >= 0 && n < Count && _stack.Count <= Count; n = data.nodes[n].parent)
            {
                _stack.Add(n);
                if (n == 0) break;
            }
            for (int i = _stack.Count - 1; i >= 0; i--) chain.Add((data.nodes[_stack[i]].kind, data.nodes[_stack[i]].label));
        }

        void Select(int index)
        {
            selected = index;
            ChainOf(index, _chain);
            MarkDirtyRepaint();
            NodeSelected?.Invoke(index);
        }

        float Width => Mathf.Max(1f, resolvedStyle.width);
        float XOf(double at) => (float)(viewSpan <= 0 ? 0 : (at - viewStart) / viewSpan) * Width;

        void Layout()
        {
            _empty.style.display = Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            labelCount = 0;
            if (Count > 0 && viewSpan > 0) LabelWalk(0);
            for (int i = labelCount; i < _labels.Count; i++) _labels[i].style.display = DisplayStyle.None;
            MarkDirtyRepaint();
            _hoverLayer.MarkDirtyRepaint();
        }

        void LabelWalk(int n)
        {
            if (labelCount >= MaxLabels) return;
            ref readonly var node = ref data.nodes[n];
            float x0 = XOf(node.start), x1 = XOf(node.start + node.size);
            if (x1 - x0 < 0.5f || x1 < 0 || x0 > Width) return;
            float cx0 = Mathf.Max(0f, x0), cx1 = Mathf.Min(Width, x1);
            if (cx1 - cx0 >= LabelMinPx)
            {
                Label l;
                if (labelCount < _labels.Count) l = _labels[labelCount];
                else
                {
                    l = new Label { pickingMode = PickingMode.Ignore };
                    l.AddToClassList("up-flame-label");
                    _labels.Add(l);
                    Add(l);
                    _tip.BringToFront();
                }
                labelCount++;
                l.text = node.label;
                l.style.left = cx0 + 3f; l.style.top = node.depth * RowH; l.style.width = cx1 - cx0 - 6f; l.style.height = RowH;
                l.style.display = DisplayStyle.Flex;
            }
            for (int c = node.firstChild; c >= 0 && c < Count; c = data.nodes[c].nextSibling) LabelWalk(c);
        }

        int HitTest(Vector2 p)
        {
            if (Count == 0 || viewSpan <= 0) return -1;
            int row = Mathf.FloorToInt(p.y / RowH);
            if (row < 0) return -1;
            int n = 0;
            if (!Covers(n, p.x)) return -1;
            for (int d = 1; d <= row; d++)
            {
                int found = -1;
                for (int c = data.nodes[n].firstChild; c >= 0 && c < Count; c = data.nodes[c].nextSibling)
                    if (Covers(c, p.x)) { found = c; break; }
                if (found < 0) return -1;
                n = found;
            }
            return data.nodes[n].depth == row ? n : -1;
        }

        bool Covers(int n, float x)
        {
            ref readonly var node = ref data.nodes[n];
            float x0 = XOf(node.start), x1 = XOf(node.start + node.size);
            return x1 - x0 >= 0.5f && x >= x0 && x <= x1;
        }

        void OnDown(PointerDownEvent e)
        {
            Focus();
            if (e.button == 0 && e.clickCount == 2)
            {
                int n = HitTest(e.localPosition);
                if (n >= 0)
                {
                    ref readonly var node = ref data.nodes[n];
                    if (node.size > 0)
                    {
                        ChainOf(n, _zoomChain);
                        zoomed = true;
                        viewStart = node.start;
                        viewSpan = node.size;
                        ClampView();
                        Layout();
                    }
                }
                else ResetZoom();
                e.StopPropagation();
                return;
            }
            if (e.button != 0 && e.button != 2) return;
            pressed = true;
            panning = e.button == 2;
            pressX = e.localPosition.x;
            pressViewStart = viewStart;
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
                    viewStart = pressViewStart - dx / Width * viewSpan;
                    ClampView();
                    zoomed = viewSpan < Total || viewStart > 0;
                    _zoomChain.Clear();
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
            if (click) Select(HitTest(e.localPosition));
        }

        void OnWheel(WheelEvent e)
        {
            if (!(e.ctrlKey || e.commandKey)) return;
            double total = Total;
            if (total > 0 && viewSpan > 0)
            {
                float x = e.localMousePosition.x;
                double t = viewStart + x / Width * viewSpan;
                viewSpan = Math.Min(Math.Max(viewSpan * (e.delta.y < 0 ? 1 / 1.25 : 1.25), total * 1e-6), total);
                viewStart = t - x / Width * viewSpan;
                ClampView();
                zoomed = viewSpan < total;
                _zoomChain.Clear();
                SetHover(HitTest(e.localMousePosition), e.localMousePosition);
                Layout();
            }
            e.StopPropagation();
        }

        void OnKey(KeyDownEvent e)
        {
            if (e.keyCode != KeyCode.Escape) return;
            ResetZoom();
            e.StopPropagation();
        }

        void SetHover(int h, Vector2 p)
        {
            if (h != hover)
            {
                hover = h;
                _hoverLayer.MarkDirtyRepaint();
            }
            if (h < 0 || h >= Count) { _tip.style.display = DisplayStyle.None; return; }
            ref readonly var n = ref data.nodes[h];
            double total = Total;
            double pct = total > 0 ? n.size / total * 100 : 0;
            string of = data.byInstructions ? "Udon instructions" : "Udon";
            string text = $"{n.label}\n{n.totalMs:0.000} ms/frame ({pct:0.0}% of {of})\nself {n.selfMs:0.000} ms  calls {n.calls:0.##}/frame";
            if (n.kind == NodeKind.Extern) text += $"  fused {n.fused}";
            else if (data.hasInstructions) text += $"\n{UdonProfilerModel.FormatCount(n.instructions)} instr/frame  self {UdonProfilerModel.FormatCount(n.selfInstructions)}";
            _tip.text = text;
            _tip.style.display = DisplayStyle.Flex;
            _tip.style.left = Mathf.Clamp(p.x + 12f, 0f, Mathf.Max(0f, resolvedStyle.width - 280f));
            _tip.style.top = p.y + 14f;
        }

        Color ColorOf(in FlameNode n)
        {
            Color c;
            switch (n.kind)
            {
                case NodeKind.Event: c = UdonProfilerPalette.Script(n.scriptId); break;
                case NodeKind.Method: c = UdonProfilerPalette.Method(n.scriptId); break;
                case NodeKind.Extern: c = UdonProfilerPalette.Extern; break;
                default: c = RootColor; break;
            }
            if (highlight != null && (n.label == null || n.label.IndexOf(highlight, StringComparison.OrdinalIgnoreCase) < 0)) c.a = 0.45f;
            return c;
        }

        void Paint(MeshGenerationContext ctx)
        {
            if (Count == 0 || viewSpan <= 0) return;
            float w = resolvedStyle.width;
            if (w <= 0) return;
            _quads.Begin(ctx);
            _stack.Clear();
            _stack.Add(0);
            while (_stack.Count > 0)
            {
                int i = _stack[_stack.Count - 1];
                _stack.RemoveAt(_stack.Count - 1);
                ref readonly var n = ref data.nodes[i];
                float x0 = XOf(n.start), x1 = XOf(n.start + n.size);
                if (x1 - x0 < 0.5f || x1 < 0 || x0 > w) continue;
                x0 = Mathf.Max(0f, x0);
                x1 = Mathf.Min(w, x1);
                float y0 = n.depth * RowH;
                float gx1 = x1 - x0 > 2f ? x1 - 0.5f : x1;
                _quads.Add(x0, y0 + 1f, gx1, y0 + RowH - 1f, ColorOf(n));
                for (int c = n.firstChild; c >= 0 && c < Count; c = data.nodes[c].nextSibling) _stack.Add(c);
            }
            if (selected >= 0 && selected < Count)
            {
                ref readonly var s = ref data.nodes[selected];
                float x0 = Mathf.Max(0f, XOf(s.start)), x1 = Mathf.Min(w, XOf(s.start + s.size));
                if (x1 > x0)
                {
                    float y0 = s.depth * RowH, y1 = y0 + RowH;
                    Color32 t = UdonProfilerPalette.Text;
                    _quads.Add(x0, y0, x1, y0 + 1f, t);
                    _quads.Add(x0, y1 - 1f, x1, y1, t);
                    _quads.Add(x0, y0, x0 + 1f, y1, t);
                    _quads.Add(x1 - 1f, y0, x1, y1, t);
                }
            }
            _quads.End();
        }

        void PaintHover(MeshGenerationContext ctx)
        {
            if (hover < 0 || hover >= Count || viewSpan <= 0) return;
            ref readonly var n = ref data.nodes[hover];
            float w = resolvedStyle.width;
            float x0 = Mathf.Max(0f, XOf(n.start)), x1 = Mathf.Min(w, XOf(n.start + n.size));
            if (x1 <= x0) return;
            float y0 = n.depth * RowH;
            _hoverQuads.Begin(ctx);
            _hoverQuads.Add(x0, y0 + 1f, x1, y0 + RowH - 1f, UdonProfilerPalette.Hover);
            _hoverQuads.End();
        }
    }
}
