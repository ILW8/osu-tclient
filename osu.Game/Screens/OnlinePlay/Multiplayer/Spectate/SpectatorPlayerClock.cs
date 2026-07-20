// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using osu.Framework.Logging;
using osu.Framework.Timing;
using osu.Game.Screens.Play;

namespace osu.Game.Screens.OnlinePlay.Multiplayer.Spectate
{
    /// <summary>
    /// A clock which catches up using rate adjustment.
    /// </summary>
    public class SpectatorPlayerClock : IFrameBasedClock, IAdjustableClock
    {
        /// <summary>
        /// The catch up rate.
        /// </summary>
        private const double catchup_rate = 2;

        /// <summary>
        /// Essentially the opposite of <see cref="catchup_rate"/>
        /// </summary>
        private const double slow_rate = 0.5;

        /// <summary>
        /// Whether to emit per-frame clock diagnostics, for investigating gameplay time advancing
        /// unevenly across evenly delivered frames. Flip to <c>true</c> and rebuild to enable.
        /// See docs/superpowers/specs/2026-07-20-spectator-clock-logging-design.md.
        /// </summary>
        /// <remarks>
        /// <c>static readonly</c> rather than <c>const</c> so the guarded block does not read as
        /// compile-time-unreachable dead code; the JIT folds it away just as effectively.
        /// </remarks>
        private static readonly bool log_clock = false;

        /// <summary>
        /// Which instance emits diagnostics when <see cref="log_clock"/> is set. Instances are numbered in
        /// construction order, which follows PlayerArea construction — expected to line up with instance 0
        /// in the FrameStabilityContainer log, though the <see cref="UserId"/> field is what confirms which
        /// player this actually is.
        /// </summary>
        private const int log_instance = 0;

        private static int instanceCounter;

        private readonly int instanceIndex;

        private readonly GameplayClockContainer masterClock;

        /// <summary>The user this clock is spectating (logging only).</summary>
        public readonly int UserId;

        public double CurrentTime { get; private set; }

        /// <summary>
        /// Whether this clock is waiting on frames to continue playback.
        /// </summary>
        public bool WaitingOnFrames { get; set; } = true;

        /// <summary>
        /// The time of this player's most recently received replay frame (its live edge). A player with no frames
        /// reports <see cref="double.NegativeInfinity"/> so it reads as maximally behind and is treated as
        /// starved/abandoned rather than as the live edge.
        /// </summary>
        public double LatestFrameTime { get; set; } = double.NegativeInfinity;

        /// <summary>
        /// Whether this clock has been abandoned by the sync manager because its frame delivery fell too far behind
        /// the live edge. An abandoned player is excluded from master pacing so it no longer drags the cast.
        /// </summary>
        public bool Abandoned { get; set; }

        /// <summary>
        /// Whether this clock is behind the master clock and running at a higher rate to catch up to it.
        /// </summary>
        /// <remarks>
        /// Of note, this will be false if this clock is *ahead* of the master clock.
        /// </remarks>
        public bool IsCatchingUp { get; set; }

        /// <summary>
        /// Whether this clock is ahead of the master clock and running at a lower rate to let the master catch-up to it.
        /// </summary>
        /// <remarks>
        /// Mutually exclusive with <see cref="IsCatchingUp"/> and <see cref="IsHalted"/>.
        /// </remarks>
        public bool IsSlowingDown { get; set; }

        /// <summary>
        /// Whether this clock is frozen because it's too far ahead of the master to ease back smoothly, holding until
        /// the master catches up to within the sync target. While halted the clock does not advance (see
        /// <see cref="IsRunning"/>).
        /// </summary>
        /// <remarks>
        /// Mutually exclusive with <see cref="IsCatchingUp"/> and <see cref="IsSlowingDown"/>.
        /// </remarks>
        public bool IsHalted { get; set; }

        /// <summary>
        /// Whether this spectator clock should be running.
        /// Use instead of <see cref="Start"/> / <see cref="Stop"/> to control time.
        /// </summary>
        public bool IsRunning { get; set; }

        /// <summary>
        /// The master clock position last consumed by <see cref="ProcessFrame"/>. Used to detect whether the master
        /// produced a new frame since we last advanced, so that being processed multiple times per host frame does
        /// not advance us more than once.
        /// </summary>
        private double lastConsumedMasterTime;

        public SpectatorPlayerClock(GameplayClockContainer masterClock, int userId = 0)
        {
            this.masterClock = masterClock;
            UserId = userId;
            lastConsumedMasterTime = masterClock.CurrentTime;

            // Interlocked because managed clocks may be created off the update thread.
            instanceIndex = Interlocked.Increment(ref instanceCounter) - 1;
        }

        public void Reset() => CurrentTime = 0;

        public void Start()
        {
            // Our running state should only be managed by SpectatorSyncManager via IsRunning.
        }

        public void Stop()
        {
            // Our running state should only be managed by an SpectatorSyncManager via IsRunning.
        }

        public bool Seek(double position)
        {
            Logger.Log($"{nameof(SpectatorPlayerClock)} seeked to {position}");
            CurrentTime = position;
            return true;
        }

        public void ResetSpeedAdjustments()
        {
        }

        public double Rate
        {
            get => IsCatchingUp ? catchup_rate : IsSlowingDown ? slow_rate : 1;
            set => throw new NotImplementedException();
        }

        public void ProcessFrame()
        {
            // false on a repeat ProcessFrame within the same frame
            bool masterAdvanced = masterClock.CurrentTime > lastConsumedMasterTime;
            lastConsumedMasterTime = masterClock.CurrentTime;

            if (IsRunning)
            {
                double elapsedSource;

                if (masterClock.ElapsedFrameTime != 0)
                {
                    elapsedSource = masterAdvanced ? masterClock.ElapsedFrameTime : 0;
                }
                else
                {
                    elapsedSource = Math.Clamp(masterClock.CurrentTime - CurrentTime, 0, 16);
                }

                double elapsed = elapsedSource * Rate;

                CurrentTime += elapsed;
                ElapsedFrameTime = elapsed;
                FramesPerSecond = masterClock.FramesPerSecond;
            }
            else
            {
                ElapsedFrameTime = 0;
                FramesPerSecond = 0;
            }

            logClockFrame();
        }

        /// <summary>
        /// Emits one per-frame diagnostic line when <see cref="log_clock"/> is enabled and this is the
        /// selected instance, carrying this clock's own advancement together with the sync state and the
        /// master clock's advancement — so upstream causes of uneven time (rate switching, freezes, master
        /// stutter) can be separated from the replay-frame snapping downstream in FrameStabilityContainer.
        /// See docs/superpowers/specs/2026-07-20-spectator-clock-logging-design.md.
        /// </summary>
        private void logClockFrame()
        {
            if (!log_clock || instanceIndex != log_instance)
                return;

            // Shared timebase with the FrameStabilityContainer log: raw Stopwatch ticks converted to ms.
            // The epoch is arbitrary, so only differences are meaningful.
            double ts = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

            Logger.Log(
                string.Create(CultureInfo.InvariantCulture,
                    $"[clock:spc] ts={ts:F3} i={instanceIndex} uid={UserId} t={CurrentTime:F3} el={ElapsedFrameTime:F3} rate={Rate:F2} run={(IsRunning ? 1 : 0)} cu={(IsCatchingUp ? 1 : 0)} sd={(IsSlowingDown ? 1 : 0)} halt={(IsHalted ? 1 : 0)} wait={(WaitingOnFrames ? 1 : 0)} abd={(Abandoned ? 1 : 0)} mt={masterClock.CurrentTime:F3} mel={masterClock.ElapsedFrameTime:F3}"),
                LoggingTarget.Performance);
        }

        public double ElapsedFrameTime { get; private set; }

        public double FramesPerSecond { get; private set; }

        public FrameTimeInfo TimeInfo => new FrameTimeInfo { Elapsed = ElapsedFrameTime, Current = CurrentTime };
    }
}
