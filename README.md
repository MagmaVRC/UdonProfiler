# Udon Profiler

An editor-only profiler for VRChat Udon. It hooks the SDK's editor Udon runtime and records every event execution with timing, nesting, caller/callee edges and extern usage, then shows it in a UI Toolkit window. Nothing in this package ships in a world.

Unity's built-in profiler only shows Udon as a single `UdonBehaviour.ManagedUpdate()` block. This one answers: which scripts, which events, who calls whom, which externs, per frame.

## Install

Drop the package into `Packages/` (Unity 2022.3, `com.vrchat.worlds` >= 3.7). Open `MagmaVRC > Udon Profiler`, enter play mode (ClientSim), and it records.

## What it shows

- **Stat tiles**: Udon ms per frame (average and max over the selected range), events per frame, externs per frame, active behaviours. The colour uses a Quest scale factor (default 4x) so editor numbers are judged against a Quest-sized budget.
- **Frame chart**: one bar per frame. Click selects a frame, drag selects a range, the wheel scrolls. A dashed line shows the auto-pause threshold.
- **Frame timeline**: a flame chart of the selected frame in execution order and nesting depth, coloured per script. Click a bar to jump to its row.
- **Tree**: group by Script, GameObject or Event. Columns: % of Udon time, Total, Self, Calls, Externs, Avg, Max and p95 per call, Extern ms, GC KB allocated (when the Mono runtime exposes per-thread allocation), and Delta against a saved baseline. Sort by any column. Double-click a row to select the GameObject.
- **Detail pane**: for the selected row, its instances, who called it, what it calls (delayed events marked), and its externs by count and time.

## Toolbar

- **Record** / **Clear**.
- **Loop events only**: keep only `_update`, `_lateUpdate`, `_fixedUpdate`, `_postLateUpdate` roots so one-off spikes (`_start`, player joins) do not dominate the maximum.
- **Options**: **Extern timing** (per-extern timestamps), **Extern breakdown** (per-signature stats; off leaves per-event extern totals but skips the per-call dictionary work), **Unity Profiler markers** (each Udon event emits a `ProfilerMarker` so it nests inside Unity Profiler captures).
- **Pause >**: stop recording on the first frame above N ms and select it.
- **Set baseline** / **Clear baseline**: snapshot the current view; later captures show a signed percentage per row.
- **File**: **Export CSV**, **Save capture** / **Load capture** (`.udonprofile`, the full ring buffer with names and extern stats, reloadable in any editor session).
- **Quest x**: the budget scale factor.

## How it works

Harmony patches applied in the editor only:

- `UdonBehaviour.RunProgram(uint)`: a prefix and finalizer record one sample per event with a timestamp pair. Nested events (one behaviour calling `SendCustomEvent` on another) form the call tree.
- `UdonWrapper.GetExternFunctionDelegate`: each extern delegate is wrapped once so extern count and time are attributed to the running event, with nested event time subtracted.
- `UdonManager.ScheduleDelayedEvent`: delayed events are linked to the event that scheduled them instead of appearing as orphan roots.

Recording keeps a 600-frame ring buffer and allocates nothing on the hot path. Overhead while recording is two timestamps per event plus one per extern; extern signatures are interned to ints once at wrapper creation so no string hashing happens per call. The window rebuilds at most ~3x/sec while recording and not at all when its tab is hidden. Off the main thread (`_onAudioFilterRead`) nothing is recorded.

## Notes

- Frame numbers are Unity's `Time.frameCount`, so they line up with the Unity Profiler.
- Editor timings are Mono; Quest runs IL2CPP on a slower CPU. Use the numbers for ranking and the scale factor for rough budgets, not as absolute device timings.
- If the separate Fast Udon VM package is installed, the footer shows which VM is active. Its fused operations bypass the wrapper, so the extern count drops while it is on; timings stay comparable.

License: MIT.
