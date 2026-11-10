using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace MagmaVRC.UdonProfiler
{
    internal static class UdonProfilerPalette
    {
        public static readonly Color Ok = new Color(0.30f, 0.78f, 0.46f);
        public static readonly Color Warn = new Color(0.95f, 0.70f, 0.20f);
        public static readonly Color Bad = new Color(0.92f, 0.30f, 0.30f);
        public static readonly Color Grid = new Color(1f, 1f, 1f, 0.06f);
        public static readonly Color Sel = new Color(1f, 1f, 1f, 0.22f);
        public static readonly Color Hover = new Color(1f, 1f, 1f, 0.16f);
        public static readonly Color Axis = new Color(1f, 1f, 1f, 0.18f);
        public static readonly Color Extern = new Color(0.52f, 0.53f, 0.56f);
        public static readonly Color Text = new Color(0.93f, 0.93f, 0.93f);

        static readonly Color[] Colors =
        {
            new Color(0.36f, 0.52f, 0.70f), new Color(0.42f, 0.64f, 0.50f), new Color(0.72f, 0.55f, 0.36f),
            new Color(0.62f, 0.46f, 0.66f), new Color(0.70f, 0.42f, 0.44f), new Color(0.40f, 0.62f, 0.66f),
            new Color(0.66f, 0.64f, 0.38f), new Color(0.50f, 0.50f, 0.62f),
            new Color(0.56f, 0.44f, 0.38f), new Color(0.38f, 0.56f, 0.42f), new Color(0.60f, 0.50f, 0.56f),
            new Color(0.46f, 0.56f, 0.58f),
        };

        static Color[] _script = new Color[64];
        static Color[] _method = new Color[64];
        static bool[] _known = new bool[64];

        public static Color Script(int scriptId)
        {
            if (scriptId < 0) return Extern;
            Ensure(scriptId);
            return _script[scriptId];
        }

        public static Color Method(int scriptId)
        {
            if (scriptId < 0) return Lighten(Extern, 0.22f);
            Ensure(scriptId);
            return _method[scriptId];
        }

        public static Color Lighten(Color c, float t) => new Color(c.r + (1f - c.r) * t, c.g + (1f - c.g) * t, c.b + (1f - c.b) * t, c.a);

        static void Ensure(int id)
        {
            if (id >= _known.Length)
            {
                int n = Math.Max(id + 1, _known.Length * 2);
                Array.Resize(ref _script, n);
                Array.Resize(ref _method, n);
                Array.Resize(ref _known, n);
            }
            if (_known[id]) return;
            string name = UdonProfilerRecorder.ScriptName(id);
            int h = 0;
            if (name != null) for (int i = 0; i < name.Length; i++) h = unchecked(h * 31 + name[i]);
            var c = Colors[(h & 0x7fffffff) % Colors.Length];
            _script[id] = c;
            _method[id] = Lighten(c, 0.22f);
            _known[id] = true;
        }
    }

    internal sealed class UdonProfilerQuads
    {
        public const int MaxQuadsPerBatch = 16000;

        struct Quad { public float x0, y0, x1, y1; public Color32 c; }

        Quad[] _buf = new Quad[256];
        int _count;
        MeshGenerationContext _mgc;

        public void Begin(MeshGenerationContext mgc)
        {
            _mgc = mgc;
            _count = 0;
        }

        public void Add(float x0, float y0, float x1, float y1, Color32 c)
        {
            if (x1 <= x0 || y1 <= y0 || c.a == 0) return;
            if (_count == _buf.Length) Array.Resize(ref _buf, Math.Min(MaxQuadsPerBatch, _buf.Length * 2));
            ref var q = ref _buf[_count++];
            q.x0 = x0; q.y0 = y0; q.x1 = x1; q.y1 = y1; q.c = c;
            if (_count >= MaxQuadsPerBatch) Flush();
        }

        public void End()
        {
            Flush();
            _mgc = null;
        }

        void Flush()
        {
            int n = _count;
            _count = 0;
            if (n == 0 || _mgc == null) return;
            var md = _mgc.Allocate(n * 4, n * 6);
            for (int i = 0; i < n; i++)
            {
                ref var q = ref _buf[i];
                md.SetNextVertex(new Vertex { position = new Vector3(q.x0, q.y0, Vertex.nearZ), tint = q.c });
                md.SetNextVertex(new Vertex { position = new Vector3(q.x1, q.y0, Vertex.nearZ), tint = q.c });
                md.SetNextVertex(new Vertex { position = new Vector3(q.x1, q.y1, Vertex.nearZ), tint = q.c });
                md.SetNextVertex(new Vertex { position = new Vector3(q.x0, q.y1, Vertex.nearZ), tint = q.c });
            }
            for (int i = 0; i < n; i++)
            {
                int i0 = i * 4;
                md.SetNextIndex((ushort)i0);
                md.SetNextIndex((ushort)(i0 + 1));
                md.SetNextIndex((ushort)(i0 + 2));
                md.SetNextIndex((ushort)(i0 + 2));
                md.SetNextIndex((ushort)(i0 + 3));
                md.SetNextIndex((ushort)i0);
            }
        }
    }
}
