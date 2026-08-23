// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Game.Screens.Play;

namespace osu.Game.Screens.OnlinePlay.Multiplayer.Spectate
{
    /// <summary>
    /// Manages the synchronisation between one or more <see cref="SpectatorPlayerClock"/>s in relation to a master clock.
    /// </summary>
    public partial class SpectatorSyncManager : Component
    {
        /// <summary>
        /// The offset from the master clock to which player clocks should remain within to be considered in-sync.
        /// </summary>
        public const double SYNC_TARGET = 16;

        /// <summary>
        /// The offset from the master clock at which player clocks begin resynchronising.
        /// </summary>
        public const double MAX_SYNC_OFFSET = 50;

        /// <summary>
        /// The maximum a player may be ahead of the master while still easing back at the slow rate. Beyond this it is
        /// frozen until the master catches up to within <see cref="SYNC_TARGET"/>, so a large lead (e.g. a mid-game
        /// join offset) resolves by holding still instead of playing in slow-motion for seconds.
        /// </summary>
        public const double MAX_SLOWDOWN_OFFSET = 1000; // ponytail: tune; above this a 0.5x ease-back reads as slow-mo

        /// <summary>
        /// Abandon a player once its frame delivery falls more than this many milliseconds behind the live edge
        /// (the most-recent frame across all players). A beatmap-time offset cap, not a wall-clock timer.
        /// </summary>
        public const double MAX_LIVE_OFFSET = 30000;

        /// <summary>
        /// How far behind the slowest kept player's live edge the master clock rides, so every kept player keeps a
        /// small frame cushion. This is the anti-stutter mechanism. Tunable.
        /// </summary>
        public const double LIVE_EDGE_BUFFER = 200;

        /// <summary>
        /// Hysteresis band applied to <see cref="MAX_LIVE_OFFSET"/>: an abandoned player is only re-included once it
        /// recovers to comfortably within the cap (behind &lt; <see cref="MAX_LIVE_OFFSET"/> - this), so it doesn't
        /// flap at the threshold.
        /// </summary>
        public const double ABANDON_HYSTERESIS = 5000;

        /// <summary>
        /// The maximum delay to start gameplay, if any (but not all) player clocks are ready.
        /// </summary>
        public const double MAXIMUM_START_DELAY = 15000;

        /// <summary>
        /// An event which is invoked when gameplay is ready to start.
        /// </summary>
        public Action? ReadyToStart;

        public double CurrentMasterTime => masterClock.CurrentTime;

        /// <summary>
        /// The master clock which is used to control the timing of all player clocks clocks.
        /// </summary>
        private readonly GameplayClockContainer masterClock;

        /// <summary>
        /// The player clocks.
        /// </summary>
        private readonly List<SpectatorPlayerClock> playerClocks = new List<SpectatorPlayerClock>();

        /// <summary>
        /// Clocks that are no longer syncing but still playing out their remaining replay frames.
        /// </summary>
        private readonly List<SpectatorPlayerClock> drainingClocks = new List<SpectatorPlayerClock>();

        private MasterClockState masterState = MasterClockState.Synchronised;

        private bool hasStarted;

        private double? firstStartAttemptTime;

        public SpectatorSyncManager(GameplayClockContainer master)
        {
            masterClock = master;
        }

        /// <summary>
        /// Create a new managed <see cref="SpectatorPlayerClock"/>.
        /// </summary>
        /// <returns>The newly created <see cref="SpectatorPlayerClock"/>.</returns>
        public SpectatorPlayerClock CreateManagedClock(int userId = 0)
        {
            var clock = new SpectatorPlayerClock(masterClock, userId);
            playerClocks.Add(clock);
            return clock;
        }

        // Sub-second timestamp for log correlation (Logger stamps only whole seconds).
        private string stamp => $"t={Time.Current:F1}ms";

        /// <summary>
        /// Removes an <see cref="SpectatorPlayerClock"/>, stopping it from being managed by this <see cref="SpectatorSyncManager"/>.
        /// </summary>
        /// <param name="clock">The <see cref="SpectatorPlayerClock"/> to remove.</param>
        /// <param name="drain">Whether to let the clock drain remaining frames before stopping it.</param>
        public void RemoveManagedClock(SpectatorPlayerClock clock, bool drain = false)
        {
            playerClocks.Remove(clock);
            Logger.Log($"Removing managed clock from {nameof(SpectatorSyncManager)} (drain={drain}, {playerClocks.Count} remain)");

            if (drain)
            {
                if (!drainingClocks.Contains(clock))
                    drainingClocks.Add(clock);

                // "no longer managed" is a bit of a misnomer, but we're not syncing this clock against other
                // spectator clocks anymore after this point. Just let the clock run when it has frames available.
                clock.IsCatchingUp = clock.IsSlowingDown = clock.IsHalted = false;
                return;
            }

            drainingClocks.Remove(clock);
            clock.IsRunning = false;
        }

        protected override void Update()
        {
            base.Update();

            if (!attemptStart())
            {
                // Ensure all player clocks are stopped until the start succeeds.
                foreach (var clock in playerClocks)
                    clock.IsRunning = false;
                return;
            }

            foreach (var clock in drainingClocks)
                clock.IsRunning = !clock.WaitingOnFrames;

            updatePlayerCatchup();
            updateMasterState();
        }

        /// <summary>
        /// Attempts to start playback. Waits for all player clocks to have available frames for up to <see cref="MAXIMUM_START_DELAY"/> milliseconds.
        /// </summary>
        /// <returns>Whether playback was started and syncing should occur.</returns>
        private bool attemptStart()
        {
            if (hasStarted)
                return true;

            if (playerClocks.Count == 0)
                return false;

            int readyCount = playerClocks.Count(s => !s.WaitingOnFrames);

            if (readyCount == playerClocks.Count)
                return performStart();

            if (readyCount > 0)
            {
                firstStartAttemptTime ??= Time.Current;

                if (Time.Current - firstStartAttemptTime > MAXIMUM_START_DELAY)
                    return performStart();
            }

            bool performStart()
            {
                ReadyToStart?.Invoke();
                return hasStarted = true;
            }

            return false;
        }

        /// <summary>
        /// Updates the catchup states of all player clocks.
        /// </summary>
        private void updatePlayerCatchup()
        {
            for (int i = 0; i < playerClocks.Count; i++)
            {
                var clock = playerClocks[i];

                bool wasRunning = clock.IsRunning;
                bool wasCatchingUp = clock.IsCatchingUp;
                bool wasSlowingDown = clock.IsSlowingDown;
                bool wasHalted = clock.IsHalted;

                // How far this player's clock is out of sync, compared to the master clock.
                // A negative value means the player is running fast (ahead); a positive value means the player is running behind (catching up).
                double timeDelta = masterClock.CurrentTime - clock.CurrentTime;

                string reason;

                if (clock.Abandoned)
                {
                    // An abandoned player is excluded from master pacing, so chasing master at catchup_rate just burns
                    // its (frame-limited) buffer faster than frames arrive and stutters. Run at the natural rate and
                    // let it play out its own stream behind the cast.
                    clock.IsCatchingUp = false;
                    clock.IsSlowingDown = false;
                    clock.IsHalted = false;
                    clock.IsRunning = !clock.WaitingOnFrames;

                    reason = clock.WaitingOnFrames ? "abandoned (waiting on frames)" : "abandoned";
                }
                else
                {
                    // A player behind master runs fast (catchup_rate) to catch up; a player slightly ahead runs slow
                    // (slow_rate) to let master catch up smoothly; a player far ahead is frozen until master catches up.
                    // IsCatchingUp must stay false while ahead, otherwise updateMasterState may incorrectly pause the master clock.
                    if (clock.IsCatchingUp)
                    {
                        if (timeDelta <= SYNC_TARGET)
                            clock.IsCatchingUp = false;
                    }
                    else if (clock.IsSlowingDown)
                    {
                        // Stop the player clock from slowing down once its lead is back within the sync target.
                        if (timeDelta >= -SYNC_TARGET)
                            clock.IsSlowingDown = false;
                    }
                    else if (clock.IsHalted)
                    {
                        // Stay frozen until the master has caught up to within the sync target (no trailing slow-down).
                        if (timeDelta >= -SYNC_TARGET)
                            clock.IsHalted = false;
                    }
                    else if (timeDelta > MAX_SYNC_OFFSET)
                    {
                        // Behind by more than the maximum allowable sync offset: speed up to catch up.
                        clock.IsCatchingUp = true;
                    }
                    else if (timeDelta < -MAX_SLOWDOWN_OFFSET)
                    {
                        // Ahead by too much to ease back smoothly: freeze until master catches up.
                        clock.IsHalted = true;
                    }
                    else if (timeDelta < -MAX_SYNC_OFFSET)
                    {
                        // Ahead by a small amount: slow down to let master catch up.
                        clock.IsSlowingDown = true;
                    }

                    // Run whenever frames are available, unless frozen for being too far ahead.
                    clock.IsRunning = !clock.WaitingOnFrames && !clock.IsHalted;

                    reason = clock.WaitingOnFrames ? "waiting on frames" : clock.IsHalted ? "holding for master" : "in range";
                }

                logClockTransition(clock, wasRunning, wasCatchingUp, wasSlowingDown, wasHalted, timeDelta, reason);
            }
        }

        /// <summary>
        /// Updates abandoned state and paces the master clock behind the slowest kept player's live edge (Stop/Start only).
        /// </summary>
        private void updateMasterState()
        {
            // Once started, at least one player has real frames (attemptStart requires !WaitingOnFrames, which means a
            // finite LatestFrameTime), so liveEdge is finite here and the all-sentinel path below never fires.
            double liveEdge = playerClocks.Count == 0 ? double.NegativeInfinity : playerClocks.Max(c => c.LatestFrameTime);

            // Abandon update (with hysteresis).
            foreach (var clock in playerClocks)
            {
                bool wasAbandoned = clock.Abandoned;
                clock.Abandoned = UpdateAbandoned(clock.Abandoned, liveEdge, clock.LatestFrameTime);

                if (clock.Abandoned != wasAbandoned)
                    Logger.Log($"[spectator-sync {stamp}] u{clock.UserId} abandoned {wasAbandoned}->{clock.Abandoned} (edge={clock.LatestFrameTime:F0}ms, {liveEdge - clock.LatestFrameTime:F0}ms behind live)");
            }

            // Master pacing: ride LIVE_EDGE_BUFFER behind the slowest kept player's live edge.
            var keptEdges = playerClocks.Where(c => !c.Abandoned).Select(c => c.LatestFrameTime).ToList();
            MasterClockState newState = ShouldStopMaster(masterClock.CurrentTime, keptEdges) ? MasterClockState.TooFarAhead : MasterClockState.Synchronised;

            if (masterState == newState)
                return;

            masterState = newState;

            double ceiling = keptEdges.Count == 0 ? double.NaN : keptEdges.Min() - LIVE_EDGE_BUFFER;
            string snapshot = string.Join(", ", playerClocks.Select(c =>
                $"u{c.UserId}:{masterClock.CurrentTime - c.CurrentTime:+0;-0}ms edge={c.LatestFrameTime:F0}{(c.Abandoned ? " abandoned" : "")}{(c.WaitingOnFrames ? " starved" : "")}"));
            Logger.Log($"[spectator-sync {stamp}] master {masterClock.CurrentTime:F0}ms -> {masterState} ceiling={ceiling:F0} [{snapshot}]");

            switch (masterState)
            {
                case MasterClockState.Synchronised:
                    if (hasStarted)
                        masterClock.Start();

                    break;

                case MasterClockState.TooFarAhead:
                    masterClock.Stop();
                    break;
            }
        }

        /// <summary>
        /// Recomputes a player's abandoned state with hysteresis. A player is abandoned once it falls more than
        /// <see cref="MAX_LIVE_OFFSET"/> behind the live edge, and only re-included once it recovers to within
        /// <see cref="MAX_LIVE_OFFSET"/> - <see cref="ABANDON_HYSTERESIS"/>.
        /// </summary>
        /// <param name="abandoned">The player's current abandoned state.</param>
        /// <param name="liveEdge">The most-recent frame time across all players.</param>
        /// <param name="latestFrameTime">This player's most-recent frame time (NegativeInfinity if it has no frames).</param>
        /// <returns>The player's new abandoned state.</returns>
        internal static bool UpdateAbandoned(bool abandoned, double liveEdge, double latestFrameTime)
        {
            double behind = liveEdge - latestFrameTime;
            double threshold = abandoned ? MAX_LIVE_OFFSET - ABANDON_HYSTERESIS : MAX_LIVE_OFFSET;
            return behind > threshold;
        }

        /// <summary>
        /// Whether the master clock should be stopped this frame. The master is paced to ride
        /// <see cref="LIVE_EDGE_BUFFER"/> behind the slowest kept (non-abandoned) player's live edge, so every kept
        /// player keeps a small frame cushion. With no kept players the master keeps running so the cast plays out
        /// to the end.
        /// </summary>
        /// <param name="masterTime">The master clock's current time.</param>
        /// <param name="keptLiveEdges">Live-edge times of the non-abandoned players.</param>
        internal static bool ShouldStopMaster(double masterTime, IReadOnlyList<double> keptLiveEdges)
        {
            if (keptLiveEdges.Count == 0)
                return false;

            double ceiling = keptLiveEdges.Min() - LIVE_EDGE_BUFFER;
            return masterTime >= ceiling;
        }

        private void logClockTransition(SpectatorPlayerClock clock, bool wasRunning, bool wasCatchingUp, bool wasSlowingDown, bool wasHalted, double timeDelta, string reason)
        {
            if (clock.IsRunning == wasRunning && clock.IsCatchingUp == wasCatchingUp && clock.IsSlowingDown == wasSlowingDown && clock.IsHalted == wasHalted)
                return;

            Logger.Log($"[spectator-sync {stamp}] u{clock.UserId} "
                       + $"run {wasRunning}->{clock.IsRunning} catchup {wasCatchingUp}->{clock.IsCatchingUp} slow {wasSlowingDown}->{clock.IsSlowingDown} halt {wasHalted}->{clock.IsHalted} "
                       + $"(delta={timeDelta:+0;-0}ms, {reason})");
        }
    }
}
