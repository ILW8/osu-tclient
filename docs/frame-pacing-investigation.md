# Frame Pacing Investigation — Status and Resume Notes

**Last updated:** 2026-07-20
**Branch:** `variant/COE2026`
**Status:** Measurement tool built and merged to branch. **Core mechanism verified in the real
client** — first live capture taken 2026-07-20 (see §5). No cause identified yet — no fixes
attempted.

---

## 1. The problem

Frame delivery in the osu! tournament client is perceptibly inconsistent, most visibly on the
gameplay screen while spectating a multiplayer room.

Two hypotheses, which do **not** share a mechanism and may be two independent problems:

1. **The animated triangle backgrounds on buttons** (`ScreenButton` in the sidebar, `RoundedButton`
   on the setup screen).
2. **Hitching is worse on maps played with Double Time.**

Note on hypothesis 1: `TrianglesV2.Velocity` is a constant `1` and is not beat-driven, so triangle
cost does not vary with DT. If both effects are real, they are separate problems.

## 2. What the code review turned up

Found while investigating, none of it acted on. Ranked by suspicion:

| Suspect | Location | Note |
| --- | --- | --- |
| `TrianglesV2` invalidates its draw node **unconditionally every frame** | `osu.Game/Graphics/Backgrounds/TrianglesV2.cs:118` | Forces a draw-node rebuild that clears and re-copies the whole particle list (`:234-235`) and re-emits vertices per particle (`:259`), whether or not anything changed. Every `RoundedButton`/`TourneyButton` on screen does this forever. `AimCount` scales with `DrawWidth` (`:141`), so wide setup-screen buttons cost more than narrow sidebar ones. **This is hypothesis 1's mechanism, confirmed to exist.** |
| `TournamentPlayerGrid.Update` re-lays out all 16 slots every frame | `osu.Game.Tournament/Components/TournamentPlayerGrid.cs:114-115` | Unconditionally assigns `cell.Size` and `cell.Position` per tile every frame, invalidating each `PlayerArea`'s whole nested `Player`/`DrawableRuleset` subtree even when nothing changed. A cheap "did anything change" guard would eliminate it. |
| `FileBasedIPC` does 4 synchronous file reads on the update thread every 250 ms | `osu.Game.Tournament/IPC/FileBasedIPC.cs:72-152` | Blocking I/O on the update thread, 4× per second, each in a bare `catch {}`. Only applies in stable/file mode, **not** in multiplayer spectating mode. Upstream code, not ours. |
| No `FrameSync` / `ExecutionMode` override anywhere in the tournament project | — | The tourney client inherits whatever `framework.ini` has. Worth checking on the affected machine. |
| Up to 12 `TourneyVideo` decoder threads alive for the whole session | `osu.Game.Tournament/TournamentSceneManager.cs:85-109` | All 13 screens are constructed eagerly and only ever `Hide()`n, never unloaded. Hidden screens aren't drawn, but FFmpeg decoder threads and their buffers persist. |
| `TournamentSpectatorScreen` per-frame LINQ + bindable churn | `Components/TournamentSpectatorScreen.cs:204-210`, `:246-248` | `updateTeamScores()` allocates a closure per frame per player; `checkAudioSource()` runs a LINQ scan every frame when no candidate audio source exists. |
| `LazerIpc` `FileSystemWatcher` with every `NotifyFilter` bit set | `osu.Game.Tournament/IPC/LazerIpc.cs:61-70` | Watches a directory it writes to 5×/sec. Callbacks are filtered so it shouldn't stall frames, but it is needless kernel notification traffic. |

Also relevant: `TournamentGame.cs:113` forces windowed mode when the window is too short, which
leaves presentation pacing to the compositor.

## 3. Existing osu!/framework tooling (before this work)

- **Ctrl+F11** — framework frame statistics overlay. Per-thread frame-time bar graph with per-phase
  colour breakdown. Real-time only, no capture.
- **Ctrl+F2** — global statistics overlay; toggling it also enables `LogPerformanceIssues`, which
  writes spike traces to `performance.log`.
- **Ctrl+F1** — draw visualiser.
- **FPS counter** — Settings → Graphics → Show FPS. `osu.Game/Graphics/UserInterface/FPSCounter.cs`;
  auto-unhides on any frame >20 ms (`spike_time_ms`, line 35). Damped averages, so it smooths away
  the spikes of interest.
- **Renderer settings** — `osu.Game/Overlays/Settings/Sections/Graphics/RendererSettings.cs`:
  renderer, frame limiter (`FrameworkSetting.FrameSync`), threading mode (`ExecutionMode`).
- **GC latency mode** — Settings → Debug → Memory. If hitches vanish under `SustainedLowLatency`,
  they are GC pauses.
- **Latency Certifier** — Settings → Maintenance. `osu.Game/Screens/Utility/`. Perceptual A/B, not
  measurement, but the one place that manipulates per-area frame rate.
- `osu.Game.Benchmarks` is BenchmarkDotNet but **every benchmark in it is algorithmic**. Nothing
  measures frame timing. Don't expect it to help.
- **No Tracy / Superluminal / ETW integration anywhere in the repo.**

## 4. What was built

A frame pacing recorder. Design spec and implementation plan:

- `docs/superpowers/specs/2026-07-20-frame-pacing-recorder-design.md`
- `docs/superpowers/plans/2026-07-20-frame-pacing-recorder.md`

Five files in `osu.Game/Utils/FramePacing/`:

| File | Responsibility |
| --- | --- |
| `FrameSample.cs` | 7-field readonly struct: frame index, `Stopwatch` timestamp, delta ms, time slept ms, GC gen0/1/2 counts |
| `FrameSampleBuffer.cs` | Fixed-capacity (`1 << 17`) wrapping ring buffer. Allocation-free, lock-free writes |
| `FramePacingStatistics.cs` | p50/p95/p99/p99.9/max, frames over threshold, max consecutive-frame delta |
| `FramePacingCsvWriter.cs` | Merges update series, draw series and markers into one time-ordered CSV |
| `FramePacingRecorder.cs` | The `Drawable`. Samples update thread in `Update()`, draw thread via a custom `DrawNode.Draw()`. Hotkeys, off-thread flush |

Wired in at `osu.Game.Tournament/TournamentGameBase.cs` — added **outside** the
`if (ipc is MultiplayerMatchIPCInfo matchIpc)` branch, so it is present in every mode. Idle until a
session is started.

Tests: `osu.Game.Tests/NonVisual/{FrameSampleBufferTest,FramePacingStatisticsTest,FramePacingCsvWriterTest}.cs`
and `osu.Game.Tests/Visual/Utils/TestSceneFramePacingRecorder.cs`.

### Usage

- **Ctrl+Shift+R** — start / stop a recording session
- **Ctrl+Shift+P** — drop a marker at the moment a hitch is *perceived*

On stop it writes `frame-pacing-<timestamp>.csv` and `frame-pacing-<timestamp>-summary.txt` into
`exports/` under `Logger.Storage`, and logs the full path. The existing "Export logs" button picks
these up.

CSV columns: `timestamp_ms,thread,frame_index,delta_ms,time_slept_ms,gc_gen0,gc_gen1,gc_gen2`.
The `thread` column is `update`, `draw`, or `marker`. Marker rows carry only a timestamp; the other
columns are empty (deliberately — zeros would break GC-counter plots).

### Design constraints worth not breaking

- **`Update()` and `recordDrawFrame()` must not allocate, box, close over locals, call LINQ, or
  lock.** The tool hunts GC hitches; a recorder that allocated per frame would manufacture them.
- **Do not call `Invalidate(Invalidation.DrawNode)` per frame.** `DrawNode.Draw()` runs every frame
  regardless — invalidation only re-runs `ApplyState()`. Per-frame invalidation is the exact
  anti-pattern being investigated in `TrianglesV2`.
- Spike threshold is 20 ms, matching `FPSCounter.spike_time_ms`, so the two agree on what a spike is.

## 5. Verification status — read before trusting any output

**Verified (controller-run, not taken on a subagent's word):**
- `dotnet test osu.Game.Tests -c Debug --filter "FullyQualifiedName~FramePacing|FullyQualifiedName~FrameSampleBuffer"` → **31/31 passing**
- `dotnet build osu.Game.Tournament -c Debug` → **succeeded, 0 warnings**
- Working tree clean.

**Verified in the real client** by the first live capture, `frame-pacing-20260720-164501.242.csv`
(142.1 s on the Gameplay screen, tournament client, macOS):

1. **Draw-thread capture works.** **8426 draw rows** alongside 31918 update rows. The headless test
   renderer never invokes draw nodes (measured: 939 update frames vs **0** draw frames), so this path
   still has **no automated coverage** — but it is now confirmed live, and the §5 blocker that gated
   the rest of the investigation is cleared.
2. **Hotkeys reach the recorder.** Ctrl+Shift+R started and stopped the session; Ctrl+Shift+P
   recorded 1 marker row. CSV and summary were written to `logs/exports/` as designed.
3. **Execution mode is genuinely multithreaded** — the summary reports distinct update and draw
   thread ids, so the two series are independent. Update clock 240 Hz, draw clock 1000 Hz,
   throttling on.

**Still NOT verified:**
1. **Recording does not perturb the measurement.** Only one session was captured; no back-to-back
   pair exists to compare medians against.
2. **Cross-check against Ctrl+F11.** Not attempted.

### Remaining manual procedure

1. Record twice in a row on the same screen; compare medians in the two summaries. If starting a
   recording shifts the median, the recorder is perturbing what it measures.
2. Cross-check against **Ctrl+F11** — the overlay damps, so exact agreement isn't expected, but an
   order-of-magnitude disagreement means something is wrong with the capture.

Then: capture a DT map and a non-DT map and compare, to settle hypothesis 2.

## 6. How to interpret the data — gotchas

- **`time_slept_ms` is the key discriminator.** Long frame with no sleep → work overran the budget.
  Long frame that did sleep → the throttler mispredicted.
- **But draw-thread vsync waits are NOT counted in `time_slept_ms`.** `ThrottledFrameClock.TimeSlept`
  counts only the clock's own throttle sleep; time blocked in the renderer's buffer swap is excluded.
  So a vsync-limited draw frame shows a long delta with zero sleep and must *not* be read as a work
  overrun.
- **Markers lag the hitch by human reaction time** (~200–300 ms). Search *backwards* from a marker
  row. The summary file states this.
- **Mean FPS is deliberately absent.** An even 60fps and a hitching 60fps have identical means. Use
  the percentiles and `worst consecutive delta`.
- **This measures frame *production*, not *presentation*.** The client is forced windowed when the
  window is too short (`TournamentGame.cs:113`), leaving presentation to the compositor. An even
  recorded series alongside visibly stuttery capture output is an informative result — it redirects
  the investigation to the presentation path.
- **In single-threaded `ExecutionMode` the update and draw series collapse to one cadence** and the
  split conveys nothing. The summary states which mode was in effect.
- **No per-phase attribution.** The recorder says a frame was long, not which phase made it long.

## 7. Framework facts discovered (verified, save re-deriving)

Against `ppy.osu.Framework` **2026.527.0** (NuGet, DLL only — no source locally; use
`./UseLocalFramework.sh` against a sibling `../osu-framework` checkout at the matching tag).

- `osu.Framework.Statistics.PerformanceMonitor` and `FrameStatistics` are **internal**, and
  `GameThread.Monitor` is non-public. The rich per-phase data behind Ctrl+F11 is unreachable from
  `osu.Game` without reflection or a local framework build.
- Public and sufficient: `GameHost.{Update,Draw,Input}Thread.Clock` → `ThrottledFrameClock`, with
  `ElapsedFrameTime`, `TimeSlept`, `FramesPerSecond`, `MaximumUpdateHz`, `Throttling`.
- **`ElapsedFrameTime` is a true post-throttle frame *period*, not work time.** Confirmed by
  decompiling: `FramedClock.get_ElapsedFrameTime` is computed live as `CurrentTime - LastFrameTime`,
  and `ThrottledFrameClock.sleepAndUpdateCurrent` advances `CurrentTime` past the sleep before the
  frame body runs. Holds in every throttle mode including Unlimited and `Throttling == false`. This
  is the load-bearing assumption of the whole tool.
- `ThrottledFrameClock.TimeSlept` is assigned before the frame body runs in every branch, so reading
  it inside `Update()`/`Draw()` always gives the current frame's sleep, never a stale carry-over.
- `DrawNode.ApplyState()` runs on the **update** thread; `DrawNode.Draw(IRenderer)` runs on the
  **draw** thread. The framework keeps several draw node instances per drawable (triple buffering),
  so a draw node must not accumulate its own state.
- `Storage.GetFullPath(string, bool createIfNotExisting = false)`, `Storage.GetStorageForDirectory`,
  `Logger.Log(string, LoggingTarget = Runtime, LogLevel = Verbose, bool = true)`, and
  `LoggingTarget.Performance` all exist and are public.

## 8. Tournament-client gotcha

`ScreenButton.OnKeyDown` (`osu.Game.Tournament/TournamentSceneManager.cs:308-316`) matches on
`e.Key` **without checking modifiers**. So the sidebar consumes **S, B, I, D, M, G, W** even when
Ctrl/Shift are held. Any new hotkey must avoid those letters. R and P were chosen for this reason.

## 9. Commits

`cddca04dae` (plan) → `76bd2ccbfa`, on `variant/COE2026`:

```
76bd2ccbfa Fix frame pacing recorder final review findings
80e65068c0 add frame pacing recorder to tournament client
0d55031c02 Fix frame pacing recorder session-generation guard and docs
d808586363 Fix frame pacing recorder review findings
ea63913776 add frame pacing recorder drawable with hotkey-driven capture
dc9eab8da7 fix review findings on frame pacing csv writer
da7543a404 add csv serialisation for captured frame series
f0b7bd94c4 add pacing statistics with percentiles and jitter metric
337acf8cce add frame sample struct and ring buffer for frame pacing capture
```

Plus `ac864f41a2` (design spec) and `cddca04dae` (implementation plan).

## 10. Open items

- **Done:** the manual verification run in §5 — draw rows confirmed, investigation unblocked.
- **Pending:** the two remaining §5 checks (back-to-back recordings for perturbation, Ctrl+F11
  cross-check). Neither gates analysis of captures already taken.
- **Unanswered question:** the spec and plan text are now stale in two places — they say draw
  `TimeSlept` is "always zero" (it is now recorded) and describe markers as a flag column on the
  frame they landed on (they are their own rows). The user was asked whether to update those docs
  and had not answered.
- **Left deliberately unfixed:** the 20 ms spike threshold is fixed rather than budget-relative, so
  on a 120 Hz-capped update thread a doubled frame (16.7 ms) is not counted in `FramesOverThreshold`.
  Disclosed in the summary file; the percentiles still expose those frames.
  `FrameSampleBuffer.Capacity` has no callers.
- **Known-benign, documented, not fixed:** `Snapshot()` can return one torn sample at the stop
  instant (`FrameSample` is 40 bytes, the copy is not atomic). `StopRecording()` does
  `Snapshot()` + sort synchronously on the update thread, so stopping can itself cause a hitch — it
  lands after `recording = false`, so no captured data is compromised.

## 11. Follow-on work (not started)

Once a baseline exists, add **runtime A/B toggles** for individual suspects so a change can be
flipped mid-session and compared *within a single run*, cancelling out machine state and thermal
drift. Start with suppressing the unconditional per-frame `Invalidate(Invalidation.DrawNode)` in
`TrianglesV2.Update()` (`osu.Game/Graphics/Backgrounds/TrianglesV2.cs:118`).

The A/B toggle, not a rebuild-and-compare, is the right shape: a 0.3 ms effect is not measurable
across two separate runs on a machine whose thermal and background state has moved.
