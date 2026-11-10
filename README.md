# Udon Profiler

An editor-only profiler for VRChat Udon. It hooks the SDK's editor Udon runtime and records every event execution and every internal method call with timing, nesting, caller/callee edges and extern usage, then shows it in a UI Toolkit window. Nothing in this package ships in a world.

Unity's built-in profiler only shows Udon as a single `UdonBehaviour.ManagedUpdate()` block. This one answers: which scripts, which events, which methods inside them, who calls whom, which externs, per frame.

## Install

Drop the package into `Packages/` (Unity 6000.3 (2022.3 compatible), `com.vrchat.worlds` >= 3.7). Open `MagmaVRC > Udon Profiler`, enter play mode (ClientSim), and it records.

## What it shows

- **Stat tiles**: Udon ms per frame (average and max over the selected range), events per frame, externs per frame (with fused counts on the Fast VM), active behaviours, and on the Fast VM Udon instructions per frame. The colour uses a Quest scale factor (default 4x) so editor numbers are judged against a Quest-sized budget.
- **Frame chart**: one bar per frame, stacked by script colour. Dashed budget lines mark 2 ms and 5 ms on Quest (scaled by Quest x), and a red dashed line the auto-pause threshold. Hover shows the frame's time, events, externs and its top three scripts. Click selects a frame, drag selects a range.
- **Frame timeline**: the selected frame (or the newest frame of the range) in execution order and nesting depth. Events are coloured per script and their internal methods in a lighter shade underneath. Zoom, pan, step between frames with `<` / `>`, and click a bar to jump to its row. Rows deeper than the timeline height are counted in a `+N deeper` corner label.
- **Tree**: group by Script, GameObject or Event. Internal methods nest under the event that ran them, externs under the event or method that called them. Columns: % of Udon time (with a bar), Total, Self, Instr and Self instr (Fast VM, see below), Calls, Externs, Fused (Fast VM), Avg, Max, p95 per call, Extern (self), GC KB allocated (when the Mono runtime exposes per-thread allocation), and Delta against a saved baseline. Rows carry a script colour swatch. Sort by any column. Double-click a row to open its script in the configured external editor (Unity's External Script Editor, e.g. Visual Studio): Event, Call and Method rows at the U# line, Behaviour and Script groups at the top, Extern rows at their calling method or event. **Select object** is in the context menu.
- **Flame graph**: switch the left pane from Tree to Flame for an icicle view of the range: All Udon, then script events, then methods, then externs, sized by average ms per frame, or by instructions per frame with **Flame graph sized by instructions** (Options, Fast VM). Instances of the same script merge.
- **Instruction counts** (Fast VM only): how many Udon VM instructions each event and method executed, inclusive and self. Instruction count predicts Quest cost better than editor Mono ms because it does not depend on the editor's JIT or the Fast VM's speedups. Counts are stock-equivalent: a fused superinstruction counts every `PUSH`, `EXTERN` and `JUMP_IF_FALSE` it replaces. They appear in the tree, the stat tile, the timeline and flame tooltips, the detail pane and the CSV. On the stock VM, and in captures from before v3, they are blank rather than 0. Extern rows have no count; an `EXTERN` instruction counts toward its caller.
- **Network view** (estimates): switch the left pane to **Network** for a sortable per-behaviour table over the selected range: script, object, sync mode, RequestSerialization calls per second (and ignored ones: not Manual or not the owner), serializations per second, estimated bytes per serialization and per second, network events by target (All / Owner / Others / Self) with parameter bytes per second, [NetworkCallable] rate-limit hits, ownership transfers, and serializations over the size limit. The **Net bytes/s** tile shows the range total, Behaviour rows in the tree get a network line in the detail pane, and **File > Export network CSV** writes the table. ClientSim sends nothing, so every number is an estimate:
  - Manual: each frame with at least one owned request counts one serialization at frame end (requests coalesce); bytes are the summed sizes of the synced variables at that point (bool/byte 1, short/char 2, int/float/Color32 4, long/double/Vector2 8, Vector3 12, Vector4/Quaternion/Color 16, strings and URLs UTF-8 plus a 2-byte length guess, arrays element size times length plus 2).
  - Continuous: an owned behaviour with synced variables is projected at **Continuous send rate** (Options, 5 / 10 / 20 Hz, default 10) while recording.
  - Limits are doc-derived (Manual 280,496 B, Continuous 200 B). Scene objects get no OnPre/PostSerialization in ClientSim, network events run locally and synchronously inside the sender's extern, and Others never runs; Others events are still counted here.
- **Context menu** on tree rows: copy name, copy row (CSV), copy call path, open the script (at the method's line for method rows), select the GameObject.
- **Detail pane**: for the selected row, its instances, who called it, what it calls (delayed events marked), its methods, and its externs by count, fused count, time and average. Method rows show their source location and where their names came from. In the flame view it shows the selected block and its largest children.
- **Hide rows under 0.1%** (Options) drops tiny rows from the tree; they reappear once they cross 0.15%.

## Toolbar

- **Record** / **Clear**. Recording after loading a capture clears the capture first.
- **Tree** / **Flame** / **Network**: the left pane's view.
- **Loop events only**: keep only `_update`, `_lateUpdate`, `_fixedUpdate`, `_postLateUpdate` roots so one-off spikes (`_start`, player joins) do not dominate the maximum.
- **Options**:
  - **Extern timing** (per-extern timestamps) and **Extern breakdown** (per-signature stats).
  - **Method timing (internal calls)**, **Method spans in timeline**, **Count fused externs (Fast VM)**.
  - **GC alloc tracking**, **Unity Profiler markers** (each Udon event emits a `ProfilerMarker`).
  - **Hide rows under 0.1%**, **Flame graph sized by instructions (Fast VM)**.
  - **Stats per frame** (1024 / 2048 / 4096 / 8192): the per-frame cap on distinct call paths.
  - **Continuous send rate (network estimate)**: 5, 10 or 20 Hz.
  - **Stock VM hooks (next domain reload)**: the stock-VM transpiler that times methods when the Fast VM is off.
  - **Keep methods (disable UdonQuic inlining)**, shown only when UdonQuic is installed: sets UdonQuic's own project setting to skip its Inline and TailCalls passes (bits 1 and 2 of the disabled-pass mask; checked when both are set). UdonQuic re-optimizes every program on entering play mode, so it applies on the next play mode entry. Without it, methods UdonQuic inlined have no rows; their time is in the caller. Turn it off again before shipping.
- **Pause >**: stop recording on the first frame above N ms and select it.
- **Set baseline** / **Clear baseline**: snapshot the current view; later captures show a signed percentage per row.
- **File**: **Export CSV**, **Save capture** / **Load capture** (`.udonprofile`, the full ring buffer with names, call paths, stats and method spans, reloadable in any editor session).
- **Quest x**: the budget scale factor.

## Controls

- **Ctrl+wheel** zooms around the cursor in the chart, the timeline and the flame graph. The plain wheel pans the chart and the timeline, and scrolls the flame graph.
- **Drag** (left beyond a few pixels, or middle) pans the timeline and the flame graph. In the chart a drag selects a range.
- **Click** a timeline bar to select its row in the tree; **double-click** it to open its script at the event or method line, or double-click empty timeline space to fit the frame. In the flame graph double-click focuses a block, or resets the zoom on empty space. Double-click a Network row to open its script.
- **Escape** resets the flame graph zoom.
- **Left / Right** arrows step one frame back or forward in the chart and the timeline.

## How it works

Harmony patches applied in the editor only:

- `UdonBehaviour.RunProgram(uint)`: a prefix and finalizer record one sample per event with a timestamp pair. Nested events (one behaviour calling `SendCustomEvent` on another) form the call tree.
- `UdonWrapper.GetExternFunctionDelegate`: each extern delegate is wrapped once so extern count and time are attributed to the running event or method, with nested event time subtracted.
- `UdonManager.ScheduleDelayedEvent`: delayed events are linked to the event that scheduled them instead of appearing as orphan roots.
- Networking (fail-soft, ClientSim found by reflection): a `UdonBehaviour.RequestSerializationHook` subscriber while recording, and prefixes on ClientSim's `ClientSimNetworkCalling.SendCustomNetworkEventProxy` (sender = the running event's behaviour) and `ClientSimPlayerManager.SetOwner` (counted only when the owner changes). Counters live per behaviour per frame in the ring; pending Manual serializations and Continuous projections are settled when the frame is sealed.
- Internal method calls: each program is analysed once per program asset. A stack simulation classifies every `JUMP`, `JUMP_IF_FALSE` and `JUMP_INDIRECT` as a call, a tail call, a return or a plain jump (U# calls and returns, UdonQuic tail calls and threaded jumps, UdonLuau heap calls and coroutine resumes), and names methods from U# debug info, exports or addresses.
  - On the Fast Udon VM the profiler sets the VM's probe (`FastUdonVMProbe`); the VM reports flagged jumps and fused externs.
  - On the stock VM a transpiler on `UdonVM.Interpret` reports the same jumps. It is fail-soft: a layout mismatch or a failed self-test leaves the stock VM unpatched and shows events only.
- Each call opens a method frame; a return closes frames until it matches the return address (pop-until-match), so missed returns, halts and exceptions never leave frames open past their event.
- Instruction counts use the Fast VM's global `FastUdonVMStats.Instructions` counter as a second clock: the recorder reads it (through a delegate compiled once) when an event or method opens and closes, next to the timestamps, so self = inclusive - children works as for time. Nothing runs per instruction beyond the counter increment the VM already does.
- Per frame the recorder aggregates time per call path into an array of stats and, optionally, a capped list of method spans for the timeline. Call depth, the path table, stats per frame and spans per frame are capped (see Limits).

Recording keeps a 600-frame ring buffer. Steady-state recording allocates nothing after warm-up; the profiler's own allocations (array growth, first-seen programs) are excluded from the GC alloc column. The window rebuilds at most about 3x per second while recording, reuses the previous merge for unchanged ranges, and does not rebuild while its tab is hidden. Off the main thread (`_onAudioFilterRead`) nothing is recorded.

## Limits

- Method timing and fused-extern counting run only while a profiler window is open in play mode. With the window closed the Fast VM pays only its null checks, and events and timed externs record as before. Opening the window mid-session shows method rows from the next event pushes on.
- UdonQuic inlines small and single-caller methods; they have no rows and their time appears in the caller, unless **Keep methods** (Options) is on. The footer says so while it is off. Tail calls show as siblings replacing the caller frame. A method reached only by tail calls (not a call target anywhere) is folded into its caller's self time. A tail call to the method laid out directly after the caller becomes a fallthrough after UdonQuic and is merged into the caller as well.
- Probe overhead: each internal call and return, each counted fused op and the recorder bookkeeping of each nested event (SendCustomEvent) cost a little time. The footer shows the live calibration (ns per call, per fused op and per nested event), taken once per domain when the window first records in play mode. The calibrated costs are subtracted from method and event times, extern times, frame totals, the chart, the budget lines and Pause >; a small residual remains. Not subtracted: the Fast VM's own jump probe (a few ns per call), the Harmony hook dispatch around each nested event, and the timestamps of timed externs.
- An event that re-enters itself (directly or through SendCustomEvent from one of its methods) merges both occurrences into one row: the row's self and method children include the nested occurrence, so self plus children can exceed its total.
- The first run of each program asset, and the first time a call path is seen, include one-time analysis work in the caller's self time for that call (allocations are excluded from the GC column).
- Fused and site-cache externs on the Fast VM are counted but not timed; their time is part of the calling method's self time. Counting them costs one probe call per fused op; its calibrated cost is subtracted and a small residual lands in self time. Turn off "Count fused externs" for cleaner self times.
- Extern columns: "Extern (self)" in the tree counts externs run directly in that row. The timeline tooltip and the detail pane's "extern (incl. methods)" include the event's methods, like the 0.1 Event-row extern column.
- UdonLuau programs have no U# debug info: methods are named `sub_0x...` or by export name; coroutine resumes are `resume_0x...`.
- Udon Graph programs have no internal calls; only events are shown.
- On the stock VM only calls proven by the static analysis are timed; programs that fail it show events only.
- p95 is approximate (histogram).
- Caps: 2048 method spans per frame (timeline only), 4096 stats per frame by default (Options, 1024-8192; calls beyond it fold into their caller's self time and are flagged), call depth 64, 262144 paths per session ("path table full" in the footer; later root events go to one overflow row).
- Memory: the ring holds 600 frames, each sized to its predecessor's counts plus 25%. Stats are 64 bytes, so a world that fills the cap every frame needs up to cap x 600 x 64 bytes (about 157 MB at 4096, 315 MB at 8192); a steady N stats per frame needs about N x 1.25 x 600 x 64 bytes. Method spans are 32 bytes, up to 2048 x 600 x 32 bytes (about 39 MB) with spans on. Typical worlds use a small fraction. Fused extern site caches take 16 bytes per fused site per behaviour that ran it (capped at 16 MB), call-site caches 8 bytes per call site per behaviour that made an internal call, and the path table's child cache 256 KB.
- Instruction counts: a nested event's instructions count toward its caller's inclusive count, like its time. Counts are the same with the Fast VM's fused ops on or off. If something resets `FastUdonVMStats` mid-event, that event's count is clamped at its children's total.
- Capture format v4 (`.udonprofile`, adds network counters, sync modes, frame durations and the recording VM; v3 added instruction counts). v1 to v3 captures still load, without the newer data.

## Notes

- Frame numbers are Unity's `Time.frameCount`, so they line up with the Unity Profiler.
- Editor timings are Mono; Quest runs IL2CPP on a slower CPU. Use the numbers for ranking and the scale factor for rough budgets, not as absolute device timings.
- In play mode the footer shows which VM is active and how method calls are hooked (`fast VM probe`, `stock VM transpiler`, or why they are off). Outside play mode it shows the VM and hook status the frames in the ring were recorded with (also saved in v4 captures), and nothing VM-specific when there is no data.

License: MIT.
