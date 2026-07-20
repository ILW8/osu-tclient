# Frame Pacing Recorder — Design

**Date:** 2026-07-20
**Branch:** `variant/COE2026`
**Status:** Approved, pending implementation plan

## Problem

Frame delivery in the osu! tournament client is perceptibly inconsistent, most visibly on the
gameplay screen while spectating a multiplayer room. Two causes are suspected — the animated
`TrianglesV2` backgrounds on buttons, and something that scales with Double Time — but there is
currently no way to quantify the hitching, so neither hypothesis can be tested and no code change
can be shown to help.

This spec covers **only the measurement tool**. It deliberately does not propose fixes.

### Why existing tooling is insufficient

| Tool | Why it falls short |
| --- | --- |
| Ctrl+F11 frame statistics overlay | Real-time visual only; no capture, no percentiles, nothing to diff between builds. |
| `FPSCounter` (Settings → Graphics → Show FPS) | Damped averages designed for readability; smooths away exactly the spikes of interest. |
| Ctrl+F2 + `LogPerformanceIssues` | Logs spikes past a fixed threshold with no surrounding frame-time series, so pacing cannot be reconstructed. |
| `osu.Game.Benchmarks` | BenchmarkDotNet; every benchmark is algorithmic (parsing, difficulty, Realm). Nothing measures frame timing. |

### Framework constraint

`osu.Framework.Statistics.PerformanceMonitor` and `FrameStatistics` are **internal**, and
`GameThread.Monitor` is non-public (verified by reflection against `ppy.osu.Framework` 2026.527.0,
the version referenced in `osu.Game/osu.Game.csproj:42`). The per-phase breakdown behind Ctrl+F11 is
therefore unreachable from `osu.Game` without reflection or a local framework build via
`UseLocalFramework.sh`.

Public and sufficient: `GameHost.{Update,Draw,Input}Thread.Clock` (`ThrottledFrameClock`), exposing
`ElapsedFrameTime`, `TimeSlept`, `FramesPerSecond`, `MaximumUpdateHz`, and `Throttling`. This is the
same surface `FPSCounter` uses (`osu.Game/Graphics/UserInterface/FPSCounter.cs:127-129`).

## Goals

1. Capture a per-frame timing series during a real session, on demand.
2. Let the operator mark the moment a hitch is *perceived*, to correlate perception with data.
3. Report pacing statistics (percentiles, worst consecutive-frame delta), not throughput averages.
4. Attribute hitches to GC where GC is the cause.
5. Perturb the measured system as little as possible.

## Non-goals

- Fixing any performance problem.
- Per-phase attribution (`Work`/`SwapBuffer`/`Scheduler`) — blocked by the internal-API constraint above.
- Automated regression detection in CI.
- Measuring *presentation* (see Limitations).

## Design

### Data sources

Two independent samplers, each running on the thread it measures so no series is aliased by
cross-thread sampling.

**Update thread.** An `Update()` override on the recorder reads `updateClock.ElapsedFrameTime` and
`updateClock.TimeSlept`. `TimeSlept` is the key discriminator:

- long frame, no sleep → work overran the frame budget
- long frame, did sleep → the throttler mispredicted

**Draw thread.** `DrawNode.Draw()` runs on the draw thread, so the recorder's custom draw node
timestamps each real draw frame with a `Stopwatch`. Deltas between consecutive invocations are true
frame-delivery intervals. This avoids reflection entirely and is the primary pacing signal.

### Per-frame record

| Field | Purpose |
| --- | --- |
| frame index | ordering, marker correlation |
| thread (update/draw) | which series |
| delta ms | the measurement |
| `TimeSlept` (update only) | work-overrun vs. throttler-misprediction |
| `GC.CollectionCount(0/1/2)` | GC attribution |

GC counters are near-free to read and are included from the first run: the spectator screen has
known per-frame allocation (`TournamentSpectatorScreen.cs:204-210`, `:246-248`), so GC is a live
hypothesis that this makes immediately falsifiable.

### Not perturbing the measurement

- Preallocated ring buffers, one per thread. Fixed capacity, sized for a several-minute session.
- No allocation and no locking on the hot path. A recorder that allocates per frame would
  manufacture the GC hitches being hunted; this is the single most important constraint in the design.
- Serialisation and file writes happen off-thread, on flush.
- Zero cost when not recording: the samplers early-out on a single volatile bool.

### Control surface

- **Start/stop hotkey** — bounds a recording session.
- **Marker hotkey** — stamps the current frame index at the moment the operator *perceives* a hitch.

The marker is the feature that turns "hitching seems worse with DT" into evidence. It also answers a
prior question: whether perceived hitches coincide with frames the data calls long. If they do not,
the problem is downstream of frame production and the investigation should redirect.

### Output

Written to the `exports/` path under `Logger.Storage`, so the existing "Export logs" flow
(`osu.Game/Overlays/Settings/Sections/General/QuickActionSettings.cs:92`) collects them.

1. **Raw CSV**, one row per frame per the schema above — permits arbitrary offline analysis.
2. **Summary**: p50 / p95 / p99 / p99.9 / max, count of frames over a threshold, and the largest
   consecutive-frame delta. Percentiles and the consecutive delta are the pacing signal. Mean FPS is
   excluded by design: an even 60fps and a hitching 60fps have identical means.

Marker timestamps are recorded in the CSV as a flag column on the frame they landed on.

### Placement

Self-contained component under `osu.Game/Utils/`, with no dependency on tournament types, so it is
usable from the main client as well. Instantiated from `TournamentGame`.

## Limitations

These are known and accepted; they are recorded so that a clean result is not misread.

1. **Single-threaded execution mode.** With `ExecutionMode.SingleThread` the draw and update series
   collapse to one cadence and the split conveys nothing. The tournament project overrides neither
   `FrameSync` nor `ExecutionMode` (no override exists anywhere in `osu.Game.Tournament`), so the
   affected machine's `framework.ini` must be checked and recorded alongside any capture.
2. **Production, not presentation.** This measures when frames are produced, not when they reach the
   display. The tournament client is forced to windowed mode when the window is too short
   (`osu.Game.Tournament/TournamentGame.cs:113`), leaving presentation to the compositor. A perfectly
   even recorded series is therefore consistent with visibly stuttery capture output, and would
   itself be an informative result — it would redirect the investigation to the presentation path.
3. **No per-phase attribution.** The recorder says a frame was long, not which phase made it long.
   If phase attribution becomes necessary, the escalation is `UseLocalFramework.sh` against an
   `../osu-framework` checkout at the tag matching 2026.527.0.

## Success criteria

The tool is successful when it can answer, from captured data rather than impression:

- How long are the hitches, and how often do they occur?
- Do they coincide with GC collections?
- Are they work overruns or throttler mispredictions?
- Does the rate change measurably between a DT map and a non-DT map?
- Do frames the operator marks as perceived hitches line up with long frames in the series?

## Follow-on work (not in this spec)

Once the recorder can quantify a baseline, add runtime A/B toggles for individual suspects — e.g.
suppressing the unconditional per-frame `Invalidate(Invalidation.DrawNode)` in
`TrianglesV2.Update()` (`osu.Game/Graphics/Backgrounds/TrianglesV2.cs:118`) — so that a change can be
flipped mid-session and compared within a single run, cancelling out machine state and thermal drift.
