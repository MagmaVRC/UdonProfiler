using System;
using System.Reflection;

namespace MagmaVRC.UdonProfiler
{
    /// <summary>Reflection access to UdonQuic's project setting that disables its Inline and TailCalls passes; absent when UdonQuic is not installed.</summary>
    public static class UdonQuicBridge
    {
        /// <summary>UdonQuic pass bits 1 (Inline) and 2 (TailCalls).</summary>
        public const uint KeepMethodsMask = (1u << 1) | (1u << 2);

        static readonly PropertyInfo DisabledPasses = Find();

        static PropertyInfo Find()
        {
            try
            {
                var p = Type.GetType("Magma.VRC.UdonQuic.QuicSettings, Magma.VRC.UdonQuic.Editor")?.GetProperty("DisabledPasses", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return p != null && p.PropertyType == typeof(uint) && p.CanRead && p.CanWrite ? p : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>True when UdonQuic is installed and its pass mask is reachable.</summary>
        public static bool Available => DisabledPasses != null;

        /// <summary>True when UdonQuic's Inline and TailCalls passes are both disabled; setting it changes UdonQuic's project setting, which applies the next time programs are optimized (on entering play mode).</summary>
        public static bool KeepMethods
        {
            get
            {
                if (DisabledPasses == null) return false;
                try { return ((uint)DisabledPasses.GetValue(null) & KeepMethodsMask) == KeepMethodsMask; }
                catch (Exception) { return false; }
            }
            set
            {
                if (DisabledPasses == null) return;
                try
                {
                    uint mask = (uint)DisabledPasses.GetValue(null);
                    uint next = value ? mask | KeepMethodsMask : mask & ~KeepMethodsMask;
                    if (next != mask) DisabledPasses.SetValue(null, next);
                }
                catch (Exception e) { UnityEngine.Debug.LogWarning($"[UdonProfiler] could not change UdonQuic passes: {e.Message}"); }
            }
        }
    }
}
