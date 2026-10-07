using System;
using UnityEngine;
namespace DynamicMapsExtended
{
    // Observational frame timing only, enabled by the existing F12 debug setting.
    // No gameplay or marker lifecycle changes, no per-frame allocations.
    internal static class FrameDiagnostics
    {
        private static readonly float[] Samples = new float[8192];
        private static int _count;
        private static int _nativeRefreshes;
        internal static void RecordNativeRefresh() { if (Plugin.DebugEnabled) _nativeRefreshes++; }
        private static float _started;
        private static string _reason = "steady state";
        internal static void Reset(string reason)
        {
            _count = 0;
            _nativeRefreshes = 0;
            _started = Time.realtimeSinceStartup;
            _reason = reason;
        }
        internal static void Update()
        {
            if (!Plugin.DebugEnabled) { _count = 0; _nativeRefreshes = 0; _started = 0f; return; }
            if (_started == 0f) _started = Time.realtimeSinceStartup;
            var ms = Time.unscaledDeltaTime * 1000f;
            if (ms > 0f && _count < Samples.Length) Samples[_count++] = ms;
            if (Time.realtimeSinceStartup - _started < 15f && _count < Samples.Length) return;
            if (_count > 0)
            {
                double total = 0; int slow = 0;
                for (int i = 0; i < _count; i++) { total += Samples[i]; if (Samples[i] > 33.333f) slow++; }
                Array.Sort(Samples, 0, _count);
                var p95 = Samples[Math.Min(_count - 1, (int)Math.Ceiling(_count * .95) - 1)];
                var p99 = Samples[Math.Min(_count - 1, (int)Math.Ceiling(_count * .99) - 1)];
                Plugin.Debug($"Frame window: reason={_reason}, location={DynamicMapsBridge.GetCurrentLocationOrNull() ?? "menu"}, frames={_count}, mean={total / _count:F2}ms, p95={p95:F2}ms, p99={p99:F2}ms, max={Samples[_count - 1]:F2}ms, over33ms={slow}, nativeRefreshRequests={_nativeRefreshes}. Includes loading, pauses and other mods; not GPU attribution.");
                Plugin.Debug("Frame window artwork: " + MapStyleManager.DescribeOverrides());
            }
            Reset("steady state");
        }
    }
}
