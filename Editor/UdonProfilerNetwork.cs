using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace MagmaVRC.UdonProfiler
{
    /// <summary>Fail-soft networking hooks: RequestSerialization, ClientSim network events and ownership transfers.</summary>
    public static class UdonProfilerNetwork
    {
        /// <summary>True while the profiler is subscribed to UdonBehaviour.RequestSerializationHook.</summary>
        public static bool RequestHooked { get; private set; }
        /// <summary>True when ClientSim's network event proxy is patched.</summary>
        public static bool EventsPatched { get; private set; }
        /// <summary>True when ClientSim's SetOwner is patched.</summary>
        public static bool OwnerPatched { get; private set; }

        static readonly Action<UdonBehaviour> _onRequest = OnRequest;
        static readonly List<UdonBehaviour> _components = new List<UdonBehaviour>(4);
        static bool _warned;

        internal static void Install(Harmony harmony)
        {
            Patch(harmony, "VRC.SDK3.ClientSim.ClientSimNetworkCalling, VRC.ClientSim", "SendCustomNetworkEventProxy", nameof(SendPrefix), () => EventsPatched = true);
            Patch(harmony, "VRC.SDK3.ClientSim.ClientSimPlayerManager, VRC.ClientSim", "SetOwner", nameof(SetOwnerPrefix), () => OwnerPatched = true);
        }

        static void Patch(Harmony harmony, string typeName, string method, string prefix, Action done)
        {
            try
            {
                var type = Type.GetType(typeName);
                var target = type == null ? null : AccessTools.Method(type, method);
                if (target == null) return;
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(UdonProfilerNetwork), prefix));
                done();
            }
            catch (Exception e) { Debug.LogWarning($"[UdonProfiler] failed to patch {method}: {e.Message}"); }
        }

        internal static void SyncRequestHook(bool on)
        {
            if (on == RequestHooked) return;
            try
            {
                if (on) UdonBehaviour.RequestSerializationHook += _onRequest;
                else UdonBehaviour.RequestSerializationHook -= _onRequest;
                RequestHooked = on;
            }
            catch (Exception e) { Warn(e); }
        }

        static void Warn(Exception e)
        {
            if (_warned) return;
            _warned = true;
            Debug.LogWarning($"[UdonProfiler] network hook failed: {e.Message}");
        }

        static void OnRequest(UdonBehaviour ub)
        {
            if (!UdonProfilerRecorder.Recording || ub == null || Environment.CurrentManagedThreadId != UdonProfilerRecorder.MainThreadId) return;
            try { UdonProfilerRecorder.NetRequest(UdonProfilerRecorder.InfoFor(ub), UdonProfilerRecorder.IsLocalOwner(ub.gameObject)); }
            catch (Exception e) { Warn(e); }
        }

        static void SendPrefix(IUdonEventReceiver udonBehaviour, NetworkEventTarget target, string eventName, Memory<object> parameters)
        {
            if (!UdonProfilerRecorder.Recording || Environment.CurrentManagedThreadId != UdonProfilerRecorder.MainThreadId) return;
            try
            {
                var receiver = udonBehaviour as UdonBehaviour;
                var sender = UdonProfilerRecorder.CurrentEventInfo ?? UdonProfilerRecorder.InfoFor(receiver);
                if (sender == null) return;
                int payload = 0;
                var span = parameters.Span;
                for (int i = 0; i < span.Length; i++) payload += UdonProfilerNetEstimate.SizeOf(span[i]);
                bool hit = false;
                if (receiver != null)
                {
                    var meta = receiver.GetNetworkCallingMetadata(eventName);
                    if (meta != null) hit = UdonProfilerRecorder.NetRateHit(receiver.GetInstanceID(), eventName, meta.MaxEventsPerSecond, Time.realtimeSinceStartupAsDouble);
                }
                UdonProfilerRecorder.NetEvent(sender, target, payload, hit);
            }
            catch (Exception e) { Warn(e); }
        }

        static void SetOwnerPrefix(VRCPlayerApi player, GameObject obj)
        {
            if (!UdonProfilerRecorder.Recording || player == null || obj == null || Environment.CurrentManagedThreadId != UdonProfilerRecorder.MainThreadId) return;
            try
            {
                if (Networking.GetOwner(obj) == player) return;
                obj.GetComponents(_components);
                for (int i = 0; i < _components.Count; i++) UdonProfilerRecorder.NetTransfer(UdonProfilerRecorder.InfoFor(_components[i]));
            }
            catch (Exception e) { Warn(e); }
            finally { _components.Clear(); }
        }
    }
}
