using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using VRC.SDKBase;

namespace MagmaVRC.UdonProfiler
{
    /// <summary>Synced variables of one program: their total fixed size and the variables whose size depends on the value.</summary>
    internal sealed class NetLayout
    {
        public int count;
        public int fixedBytes;
        public string[] variableNames = Array.Empty<string>();
    }

    /// <summary>Doc-derived size estimates for synced Udon data; ClientSim computes no real sizes.</summary>
    public static class UdonProfilerNetEstimate
    {
        /// <summary>Doc-derived maximum bytes per Manual serialization.</summary>
        public const int ManualLimitBytes = 280496;
        /// <summary>Doc-derived maximum bytes per Continuous serialization.</summary>
        public const int ContinuousLimitBytes = 200;
        /// <summary>Guessed length prefix of strings and arrays.</summary>
        public const int LengthPrefixBytes = 2;

        /// <summary>Wire size of a value of this type, or -1 when it depends on the value (strings, URLs, arrays) or the type is unknown.</summary>
        public static int FixedSize(Type t)
        {
            if (t == null) return -1;
            if (t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte)) return 1;
            if (t == typeof(short) || t == typeof(ushort) || t == typeof(char)) return 2;
            if (t == typeof(int) || t == typeof(uint) || t == typeof(float) || t == typeof(Color32)) return 4;
            if (t == typeof(long) || t == typeof(ulong) || t == typeof(double) || t == typeof(Vector2)) return 8;
            if (t == typeof(Vector3)) return 12;
            if (t == typeof(Vector4) || t == typeof(Quaternion) || t == typeof(Color)) return 16;
            return -1;
        }

        /// <summary>Estimated wire size of one value; 0 for null and unknown types.</summary>
        public static int SizeOf(object value)
        {
            switch (value)
            {
                case null: return 0;
                case string s: return LengthPrefixBytes + Encoding.UTF8.GetByteCount(s);
                case VRCUrl u: return LengthPrefixBytes + Encoding.UTF8.GetByteCount(u.Get() ?? "");
                case Array a:
                {
                    int fixedSize = FixedSize(a.GetType().GetElementType());
                    if (fixedSize >= 0) return LengthPrefixBytes + fixedSize * a.Length;
                    int total = LengthPrefixBytes;
                    foreach (var e in a) total += SizeOf(e);
                    return total;
                }
            }
            int f = FixedSize(value.GetType());
            return f < 0 ? 0 : f;
        }

        internal static NetLayout Layout(IEnumerable<(string name, Type type)> variables)
        {
            var layout = new NetLayout();
            List<string> variable = null;
            if (variables != null)
                foreach (var (name, type) in variables)
                {
                    layout.count++;
                    int f = FixedSize(type);
                    if (f >= 0) layout.fixedBytes += f;
                    else (variable ??= new List<string>()).Add(name);
                }
            if (variable != null) layout.variableNames = variable.ToArray();
            return layout;
        }

        internal static int Estimate(NetLayout layout, Func<string, object> read)
        {
            if (layout == null) return 0;
            long total = layout.fixedBytes;
            if (read != null)
                foreach (var name in layout.variableNames)
                {
                    object v;
                    try { v = read(name); }
                    catch (Exception) { v = null; }
                    total += SizeOf(v);
                }
            return (int)Math.Min(int.MaxValue, total);
        }
    }
}
