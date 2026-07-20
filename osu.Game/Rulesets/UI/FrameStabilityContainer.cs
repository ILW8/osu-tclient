// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Timing;
using osu.Game.Input.Handlers;
using osu.Game.Screens.Play;

namespace osu.Game.Rulesets.UI
{
    /// <summary>
    /// A container which consumes a parent gameplay clock and standardises frame counts for children.
    /// Will ensure a minimum of 50 frames per clock second is maintained, regardless of any system lag or seeks.
    /// </summary>
    [Cached(typeof(IGameplayClock))]
    [Cached(typeof(IFrameStableClock))]
    public sealed partial class FrameStabilityContainer : Container, IHasReplayHandler, IFrameStableClock
    {
        public ReplayInputHandler? ReplayInputHandler { get; set; }

        private int invalidBassTimeLogCount;

        /// <summary>
        /// The number of CPU milliseconds to spend at most during seek catch-up.
        /// </summary>
        private const double max_catchup_milliseconds = 10;

        /// <summary>
        /// Whether to emit per-frame clock diagnostics, for investigating gameplay time advancing
        /// unevenly across evenly delivered frames. Flip to <c>true</c> and rebuild to enable.
        /// See docs/superpowers/specs/2026-07-20-spectator-clock-logging-design.md.
        /// </summary>
        /// <remarks>
        /// Deliberately <c>static readonly</c> rather than <c>const</c>: a <c>const false</c> would make the
        /// guarded block unreachable at compile time, which the compiler flags as dead code. The JIT folds
        /// this away just as effectively once the static constructor has run.
        /// </remarks>
        private static readonly bool log_clock = false;

        /// <summary>
        /// Which instance emits diagnostics when <see cref="log_clock"/> is set. Instances are numbered in
        /// construction order, which follows PlayerArea construction — so 0 is expected to be the same tile
        /// as instance 0 in the SpectatorPlayerClock log. That correspondence is an assumption; confirm it
        /// against the <c>uid</c> field there.
        /// </summary>
        private const int log_instance = 0;

        private static int instanceCounter;

        private readonly int instanceIndex;

        /// <summary>
        /// Whether to enable frame-stable playback.
        /// </summary>
        internal bool FrameStablePlayback { get; set; } = true;

        private readonly Bindable<bool> isCatchingUp = new Bindable<bool>();

        private readonly Bindable<bool> waitingOnFrames = new Bindable<bool>();

        public double GameplayStartTime { get; }

        private IGameplayClock? parentGameplayClock;

        /// <summary>
        /// A clock which is used as reference for time, rate and running state.
        /// </summary>
        private IClock referenceClock = null!;

        /// <summary>
        /// A local manual clock which tracks the reference clock.
        /// Values are transferred from <see cref="referenceClock"/> each update call.
        /// </summary>
        private readonly ManualClock manualClock;

        /// <summary>
        /// The main framed clock which has stability applied to it.
        /// This gets exposed to children as an <see cref="IGameplayClock"/>.
        /// </summary>
        private readonly FramedClock framedClock;

        [Resolved]
        private OsuGame? game { get; set; }

        private readonly Stopwatch stopwatch = new Stopwatch();

        /// <summary>
        /// The current direction of playback to be exposed to frame stable children.
        /// </summary>
        /// <remarks>
        /// Initially it is presumed that playback will proceed in the forward direction.
        /// </remarks>
        private int direction = 1;

        private PlaybackState state;

        private bool hasReplayAttached => ReplayInputHandler != null;

        private bool firstConsumption = true;

        public FrameStabilityContainer(double gameplayStartTime = double.MinValue)
        {
            RelativeSizeAxes = Axes.Both;

            framedClock = new FramedClock(manualClock = new ManualClock());

            GameplayStartTime = gameplayStartTime;

            // Interlocked rather than a plain increment: screens are loaded on background threads, so
            // construction is not guaranteed to be confined to the update thread.
            instanceIndex = Interlocked.Increment(ref instanceCounter) - 1;
        }

        [BackgroundDependencyLoader(true)]
        private void load(IGameplayClock? gameplayClock)
        {
            if (gameplayClock != null)
            {
                parentGameplayClock = gameplayClock;
                IsPaused.BindTo(parentGameplayClock.IsPaused);
            }

            referenceClock = gameplayClock ?? Clock;
            Clock = this;
        }

        public override bool UpdateSubTree()
        {
            stopwatch.Restart();

            int iteration = 0;

            do
            {
                // update clock is always trying to approach the aim time.
                // it should be provided as the original value each loop.
                updateClock(iteration++);

                if (state == PlaybackState.NotValid)
                    break;

                base.UpdateSubTree();
                UpdateSubTreeMasking();
            } while (state == PlaybackState.RequiresCatchUp && stopwatch.ElapsedMilliseconds < max_catchup_milliseconds);

            return true;
        }

        private void updateClock(int iteration)
        {
            if (waitingOnFrames.Value)
            {
                // if waiting on frames, run one update loop to determine if frames have arrived.
                state = PlaybackState.Valid;
            }
            else if (IsPaused.Value && !hasReplayAttached)
            {
                // time should not advance while paused, nor should anything run. No clock diagnostics
                // are emitted here: this branch is the paused-without-replay case, not the spectator
                // scenario being investigated, and no time stages have been computed yet.
                state = PlaybackState.NotValid;
                return;
            }
            else
            {
                state = PlaybackState.Valid;
            }

            double proposedTime = referenceClock.CurrentTime;

            // Diagnostics: the reference time as handed in by the (spectator) clock chain, before any
            // stability clamp or replay-frame snap. See log_clock.
            double logReferenceTime = proposedTime;

            if (FrameStablePlayback)
                // if we require frame stability, the proposed time will be adjusted to move at most one known
                // frame interval in the current direction.
                applyFrameStability(ref proposedTime);

            // Diagnostics: after the stability clamp, before the replay-frame snap.
            double logStabilisedTime = proposedTime;

            if (hasReplayAttached)
            {
                bool valid = updateReplay(ref proposedTime);

                if (!valid)
                    state = PlaybackState.NotValid;
            }

            // Diagnostics: after the replay-frame snap. logReplayTime != logStabilisedTime means the
            // snap moved the time — the leading suspect for uneven gameplay advancement.
            double logReplayTime = proposedTime;

            // TODO: replace IsDebugBuild with a framework flag which asserts we are in a test scene, interactively or otherwise.
            bool allowReferenceClockSeeks = hasReplayAttached || DebugUtils.IsNUnitRunning || DebugUtils.IsDebugBuild || !FrameStablePlayback;

            // This is a hotfix for ongoing bass issues we are trying to resolve (see https://www.un4seen.com/forum/?topic=20482.msg145474#msg145474)
            //
            // In testing this triggers *very* rarely even when set to super low values (10 ms). The cases we're worried about involve multi-second jumps.
            // A difference of more than 500 ms seems like a sane number we should never exceed.
            //
            // Double-checking against the parent clock ensures we don't accidentally freeze time when the game stutters due to a long running frame.
            if (!allowReferenceClockSeeks && Math.Abs(proposedTime - referenceClock.CurrentTime) > 500 && game?.Clock.ElapsedFrameTime <= 500)
            {
                if (invalidBassTimeLogCount < 10)
                {
                    invalidBassTimeLogCount++;
                    Logger.Log("Ignoring likely invalid time value provided by BASS during gameplay");
                    Logger.Log($"- provided: {referenceClock.CurrentTime:N2}");
                    Logger.Log($"- expected: {proposedTime:N2}");
                }

                state = PlaybackState.NotValid;

                // manualClock.CurrentTime is deliberately not read fresh here — it was not updated on
                // this rejected frame, so logging its stale value correctly shows that time did not move.
                logClockFrame(iteration, logReferenceTime, logStabilisedTime, logReplayTime);
                return;
            }

            invalidBassTimeLogCount = 0;

            // if the proposed time is the same as the current time, assume that the clock will continue progressing in the same direction as previously.
            // this avoids spurious flips in direction from -1 to 1 during rewinds.
            if (state == PlaybackState.Valid && proposedTime != manualClock.CurrentTime)
                direction = proposedTime >= manualClock.CurrentTime ? 1 : -1;

            double timeBehind = Math.Abs(proposedTime - referenceClock.CurrentTime);

            isCatchingUp.Value = timeBehind > 200;
            waitingOnFrames.Value = hasReplayAttached && state == PlaybackState.NotValid;

            manualClock.CurrentTime = proposedTime;
            manualClock.Rate = Math.Abs(referenceClock.Rate) * direction;
            manualClock.IsRunning = referenceClock.IsRunning;

            // determine whether catch-up is required.
            if (state == PlaybackState.Valid && timeBehind > 0)
                state = PlaybackState.RequiresCatchUp;

            // The manual clock time has changed in the above code. The framed clock now needs to be updated
            // to ensure that the its time is valid for our children before input is processed
            framedClock.ProcessFrame();

            if (framedClock.ElapsedFrameTime != 0)
                IsRewinding = framedClock.ElapsedFrameTime < 0;

            logClockFrame(iteration, logReferenceTime, logStabilisedTime, logReplayTime);
        }

        /// <summary>
        /// Emits one per-frame clock diagnostic line when <see cref="log_clock"/> is enabled and this
        /// is the selected instance. Captures the time value at each stage of the transformation from the
        /// reference clock's output to the value drawn, so that the stage introducing uneven advancement
        /// can be identified. See docs/superpowers/specs/2026-07-20-spectator-clock-logging-design.md.
        /// </summary>
        private void logClockFrame(int iteration, double referenceTime, double stabilisedTime, double replayTime)
        {
            if (!log_clock || instanceIndex != log_instance)
                return;

            // Shared timebase with the SpectatorPlayerClock log: raw Stopwatch ticks converted to ms.
            // The epoch is arbitrary, so only differences are meaningful.
            double ts = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

            Logger.Log(
                string.Create(CultureInfo.InvariantCulture,
                    $"[clock:fsc] ts={ts:F3} i={instanceIndex} it={iteration} ref={referenceTime:F3} stab={stabilisedTime:F3} rep={replayTime:F3} fin={manualClock.CurrentTime:F3} st={state} dir={direction} wait={(waitingOnFrames.Value ? 1 : 0)}"),
                LoggingTarget.Performance);
        }

        /// <summary>
        /// Attempt to advance replay playback for a given time.
        /// </summary>
        /// <param name="proposedTime">The time which is to be displayed.</param>
        /// <returns>Whether playback is still valid.</returns>
        private bool updateReplay(ref double proposedTime)
        {
            Debug.Assert(ReplayInputHandler != null);

            double? newTime;

            if (FrameStablePlayback)
            {
                // when stability is turned on, we shouldn't execute for time values the replay is unable to satisfy.
                newTime = ReplayInputHandler.SetFrameFromTime(proposedTime);
            }
            else
            {
                // when stability is disabled, we don't really care about accuracy.
                // looping over the replay will allow it to catch up and feed out the required values
                // for the current time.
                while ((newTime = ReplayInputHandler.SetFrameFromTime(proposedTime)) != proposedTime)
                {
                    if (newTime == null)
                    {
                        // special case for when the replay actually can't arrive at the required time.
                        // protects from potential endless loop.
                        break;
                    }
                }
            }

            if (newTime == null)
                return false;

            proposedTime = newTime.Value;
            return true;
        }

        /// <summary>
        /// Apply frame stability modifier to a time.
        /// </summary>
        /// <param name="proposedTime">The time which is to be displayed.</param>
        private void applyFrameStability(ref double proposedTime)
        {
            const double sixty_frame_time = 1000.0 / 60;

            if (firstConsumption)
            {
                // On the first update, frame-stability seeking would result in unexpected/unwanted behaviour.
                // Instead we perform an initial seek to the proposed time.

                // process frame (in addition to finally clause) to clear out ElapsedTime
                manualClock.CurrentTime = proposedTime;
                framedClock.ProcessFrame();

                firstConsumption = false;
                return;
            }

            if (manualClock.CurrentTime < GameplayStartTime)
                manualClock.CurrentTime = proposedTime = Math.Min(GameplayStartTime, proposedTime);
            else if (Math.Abs(manualClock.CurrentTime - proposedTime) > sixty_frame_time * 1.2f)
            {
                proposedTime = proposedTime > manualClock.CurrentTime
                    ? Math.Min(proposedTime, manualClock.CurrentTime + sixty_frame_time)
                    : Math.Max(proposedTime, manualClock.CurrentTime - sixty_frame_time);
            }
        }

        #region Delegation of IGameplayClock

        public IBindable<bool> IsPaused { get; } = new BindableBool();

        public bool IsRewinding { get; private set; }

        public double CurrentTime => framedClock.CurrentTime;

        public double Rate => framedClock.Rate;

        public bool IsRunning => framedClock.IsRunning;

        public void ProcessFrame() { }

        public double ElapsedFrameTime => framedClock.ElapsedFrameTime;

        public double FramesPerSecond => framedClock.FramesPerSecond;

        public double StartTime => parentGameplayClock?.StartTime ?? 0;

        private readonly AudioAdjustments gameplayAdjustments = new AudioAdjustments();

        public IAdjustableAudioComponent AdjustmentsFromMods => parentGameplayClock?.AdjustmentsFromMods ?? gameplayAdjustments;

        #endregion

        #region Delegation of IFrameStableClock

        IBindable<bool> IFrameStableClock.IsCatchingUp => isCatchingUp;
        IBindable<bool> IFrameStableClock.WaitingOnFrames => waitingOnFrames;

        #endregion

        private enum PlaybackState
        {
            /// <summary>
            /// Playback is not possible. Child hierarchy should not be processed.
            /// </summary>
            NotValid,

            /// <summary>
            /// Playback is running behind real-time. Catch-up will be attempted by processing more than once per
            /// game loop (limited to a sane maximum to avoid frame drops).
            /// </summary>
            RequiresCatchUp,

            /// <summary>
            /// In a valid state, progressing one child hierarchy loop per game loop.
            /// </summary>
            Valid
        }
    }
}
