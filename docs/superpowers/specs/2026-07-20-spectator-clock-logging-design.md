# Spectator Clock Logging — Design

**Date:** 2026-07-20
**Branch:** `variant/COE2026`
**Status:** Design approved, not yet implemented.

## 1. Why

The tournament client shows two independent visual defects while spectating. They have different
mechanisms and must not be investigated with the same tool:

1. **Frame presentation.** Frames are delivered unevenly — roughly 22 dropped frames per 67 s of
   steady-state play. Measured by the existing `FramePacingRecorder`
   (`osu.Game/Utils/FramePacing/`). See `docs/frame-pacing-investigation.md`.
2. **Frame content.** Frames are delivered *evenly* but depict unevenly spaced moments in gameplay
   time: stepping through an OBS capture frame-by-frame, the slider ball advances further on some
   frames than others despite every captured frame carrying a new game frame.

This spec covers **(2) only**. The first live pacing capture
(`frame-pacing-20260720-164501.242.csv`) argues that (2) is not caused by (1):

- 91.6% of presented frames sampled exactly 4 update frames, at even 4.16 ms wall deltas,
  delivered 16.67 ms apart. Frame *production* is even.
- The operator marker at 141207 ms — a moment a hitch was perceived — sits in a stretch delivering
  149 frames in 149 intervals at 59.99 Hz, with a maximum update delta of 7.30 ms. Production was
  pristine while a hitch was seen.

So the unevenness is in the *time value* the gameplay clock reports, not in when frames appear. The
pacing recorder is structurally blind to this: it samples frame periods and never records the time a
frame depicts.

## 2. Candidate mechanisms

The spectated-player clock chain is
`MasterGameplayClockContainer` → `SpectatorPlayerClock` → `GameplayClockContainer` →
`FramedBeatmapClock` → `FrameStabilityContainer` → replay handler.

Four independent sources of uneven advancement live in it:

| # | Mechanism | Location |
| --- | --- | --- |
| 1 | **Replay-frame snapping** — time set to precisely the replay frame's end time regardless of incoming time | `osu.Game/Rulesets/Replays/FramedReplayInputHandler.cs:135-139` |
| 2 | **Rate switching** between 2.0 / 0.5 / 1.0 with no easing at transitions | `osu.Game/Screens/OnlinePlay/Multiplayer/Spectate/SpectatorPlayerClock.cs:120-124` |
| 3 | **Hard freezes** — `IsRunning = false` when halted or frame-starved: zero-elapsed frames, then full-rate resumption | `osu.Game/Screens/OnlinePlay/Multiplayer/Spectate/SpectatorSyncManager.cs:233` |
| 4 | **Master `Stop()`/`Start()`** against a network-driven live-edge ceiling, propagating to every tile at once | `osu.Game/Screens/OnlinePlay/Multiplayer/Spectate/SpectatorSyncManager.cs:275-286` |

Mechanism 1 is the leading suspect, and it independently explains why hitching is reported as worse
on Double Time: replay frames are recorded in *map* time, so a source player's real-time frame
interval maps to 1.5× coarser map-time steps under DT. `docs/frame-pacing-investigation.md` notes
that hypothesis 2 (DT is worse) had no known mechanism, because `TrianglesV2.Velocity` is constant
and does not scale with rate. This supplies one.

## 3. Design

Deliberately **separate from `FramePacingRecorder`** — no shared types, files, or hotkeys — so that
the presentation investigation and the clock investigation stay independent.

### Log site A — `SpectatorPlayerClock.ProcessFrame()`

Emitted after `CurrentTime += elapsed` (`:147`), carrying state that exists nowhere else in the
chain:

```
[clock:spc] ts=1418472.331 i=0 uid=8047881 t=84213.402 el=4.167 rate=1.00 run=1 cu=0 sd=0 halt=0 wait=0 abd=0 mt=84213.402 mel=4.167
```

| Field | Meaning |
| --- | --- |
| `i` | instance index (see §4) |
| `uid` | `UserId` of the spectated player |
| `t`, `el` | this clock's `CurrentTime` and `ElapsedFrameTime` |
| `rate` | `Rate` — 2.0 catching up, 0.5 slowing, 1.0 nominal (mechanism 2) |
| `run`, `cu`, `sd`, `halt`, `wait`, `abd` | `IsRunning`, `IsCatchingUp`, `IsSlowingDown`, `IsHalted`, `WaitingOnFrames`, `Abandoned` (mechanism 3) |
| `mt`, `mel` | master clock `CurrentTime` and `ElapsedFrameTime` (mechanism 4) |

`mt`/`mel` make master stutter separable from this clock's own rate switching.

### Log site B — `FrameStabilityContainer.updateClock()`

One line per invocation, capturing `proposedTime` at each mutation:

```
[clock:fsc] ts=1418472.334 i=0 it=0 ref=84213.402 stab=84213.402 rep=84210.938 fin=84210.938 st=Valid dir=1 wait=0
```

| Field | Captured at |
| --- | --- |
| `ref` | `referenceClock.CurrentTime`, `:145` — what the spectator chain handed in |
| `stab` | after `applyFrameStability()`, `:150` — clamped to ≤1 frame interval |
| `rep` | after `updateReplay()`, `:154` — **replay-frame snap** |
| `fin` | `manualClock.CurrentTime`, `:195` — the time the slider ball is drawn at |
| `it` | `do/while` iteration index from `:111-122` |
| `st`, `dir`, `wait` | `state`, `direction`, `waitingOnFrames.Value` |

**`rep != stab` on a line is mechanism 1 firing**, visible directly without inference.

Because `:145` reads the chain's output and `:195` is the final drawn time, this single site spans
the whole transformation. Site A exists only to attribute what happens *upstream* of `ref`.

### Correlation

Both lines carry `ts=`, from raw `Stopwatch.GetTimestamp()` ticks converted to milliseconds. That
source is process-wide and monotonic, so the two sites share a timebase with no shared object,
static initialiser, or plumbing between them. Deltas across sites are directly comparable.

The epoch is arbitrary (not process start), so `ts` values are large; only differences are
meaningful.

## 4. Instance selection

Neither class knows about grid tiles. Each gets a `static int` construction counter and stores its
own index; `private const int log_instance = 0` selects which instance logs.

Creation order is driven by `PlayerArea` construction on both sides, so instance 0 is expected to be
the same tile in both logs. **This is an assumption, not a guarantee.** The `uid` field on the `spc`
line is the means of confirming it.

This is the weakest part of the design. If it proves wrong, the replacement is to resolve the
`SpectatorPlayerClock`'s `UserId` down the chain to `FrameStabilityContainer` — more plumbing than a
throwaway diagnostic warrants unless actually needed.

## 5. Enabling

`private static readonly bool log_clock = false;` at the top of each file, with a `logClockFrame`
helper guarded by it. Off by default; enabling or changing the logged instance requires a rebuild.

`static readonly` rather than `const`: a `const false` guard makes the code after the helper's
early-return unreachable, which the compiler flags (CS0162) and the warnings-as-errors build rejects.
`static readonly` leaves one always-false, fully-predicted branch per frame — negligible, and the
idiomatic way to express a rebuild-time-off flag without dead-code warnings.

Chosen over a hotkey because the tournament sidebar consumes S, B, I, D, M, G and W regardless of
modifiers (`osu.Game.Tournament/TournamentSceneManager.cs:308-316`), and over an environment
variable to keep the change minimal.

## 6. Output

`LoggingTarget.Performance` (→ `performance.log`) at `LogLevel.Verbose`, keeping roughly
500–1000 lines/sec out of the runtime log. One `grep '\[clock:'` collects both sites.

Unlike the pacing recorder — whose sampling paths must not allocate, since a recorder that allocated
per frame would manufacture the GC hitches it exists to detect — this tool has no such constraint.
It measures clock *values*, not frame timing, so the string formatting it costs does not corrupt
what it measures. This freedom is a direct consequence of keeping the two tools separate.

## 7. Scope and non-goals

- **`FrameStabilityContainer` is upstream shared code**, used by all gameplay rather than only
  spectating. With `log_clock = false` it is inert, but the file will need care on any rebase onto
  `ppy/master`.
- **No test coverage.** This is diagnostic scaffolding read by a human. A test asserting log-line
  format would pin down a format intended to change as the investigation moves.
- **No fixes.** This spec adds observation only. No clock behaviour changes.
- **`SpectatorSyncManager` is not instrumented directly.** Its decisions surface through the flag
  fields on site A, which is sufficient to identify mechanisms 3 and 4.

## 8. Success criteria

The capture answers, without inference:

1. Does `fin` advance in even steps while wall time advances evenly? If yes, the content problem is
   not in this chain and the investigation moves elsewhere.
2. If uneven — which stage introduces it? `ref` uneven → upstream (mechanisms 2/3/4, disambiguated
   by site A's flags). `rep != stab` → mechanism 1.
3. What is the quantisation step of `rep`, and does it match the expected map-time replay frame
   interval of the source player under DT?
