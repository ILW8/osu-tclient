// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Screens.OnlinePlay.Multiplayer.Spectate;
using osu.Game.Screens.Play;
using osu.Game.Tests.Visual;

namespace osu.Game.Tests.OnlinePlay
{
    // NOTE: This test scene never calls ProcessFrame on clocks.
    // The current tests are fine without this as they are testing very static scenarios, but it's worth knowing
    // if adding further tests to this class.
    [HeadlessTest]
    public partial class TestSceneCatchUpSyncManager : OsuTestScene
    {
        private GameplayClockContainer master = null!;
        private SpectatorSyncManager syncManager = null!;

        private Dictionary<SpectatorPlayerClock, int> clocksById = null!;
        private SpectatorPlayerClock player1 = null!;
        private SpectatorPlayerClock player2 = null!;

        [SetUp]
        public void Setup()
        {
            syncManager = new SpectatorSyncManager(master = new GameplayClockContainer(new TestManualClock(), false, false));
            player1 = syncManager.CreateManagedClock();
            player2 = syncManager.CreateManagedClock();

            clocksById = new Dictionary<SpectatorPlayerClock, int>
            {
                { player1, 1 },
                { player2, 2 }
            };

            Schedule(() =>
            {
                Children = new Drawable[]
                {
                    syncManager,
                    master
                };
            });
        }

        [Test]
        public void TestPlayerClocksStartWhenAllHaveFrames()
        {
            setWaiting(() => player1, false);
            assertPlayerClockState(() => player1, false);
            assertPlayerClockState(() => player2, false);

            setWaiting(() => player2, false);
            assertPlayerClockState(() => player1, true);
            assertPlayerClockState(() => player2, true);
        }

        [Test]
        public void TestReadyPlayersStartWhenReadyForMaximumDelayTime()
        {
            setWaiting(() => player1, false);
            AddWaitStep($"wait {SpectatorSyncManager.MAXIMUM_START_DELAY} milliseconds", (int)Math.Ceiling(SpectatorSyncManager.MAXIMUM_START_DELAY / TimePerAction));
            assertPlayerClockState(() => player1, true);
            assertPlayerClockState(() => player2, false);
        }

        [Test]
        public void TestPlayerClockDoesNotCatchUpWhenSlightlyOutOfSync()
        {
            setAllWaiting(false);

            setMasterTime(SpectatorSyncManager.SYNC_TARGET + 1);
            assertCatchingUp(() => player1, false);
        }

        [Test]
        public void TestPlayerClockStartsCatchingUpWhenTooFarBehind()
        {
            setAllWaiting(false);

            setMasterTime(SpectatorSyncManager.MAX_SYNC_OFFSET + 1);
            assertCatchingUp(() => player1, true);
            assertCatchingUp(() => player2, true);
        }

        [Test]
        public void TestPlayerClockKeepsCatchingUpWhenSlightlyOutOfSync()
        {
            setAllWaiting(false);

            setMasterTime(SpectatorSyncManager.MAX_SYNC_OFFSET + 1);
            setPlayerClockTime(() => player1, SpectatorSyncManager.SYNC_TARGET + 1);
            assertCatchingUp(() => player1, true);
        }

        [Test]
        public void TestPlayerClockStopsCatchingUpWhenInSync()
        {
            setAllWaiting(false);

            setMasterTime(SpectatorSyncManager.MAX_SYNC_OFFSET + 2);
            setPlayerClockTime(() => player1, SpectatorSyncManager.SYNC_TARGET);
            assertCatchingUp(() => player1, false);
            assertCatchingUp(() => player2, true);
        }

        [Test]
        public void TestPlayerClockDoesNotStopWhenSlightlyAhead()
        {
            setAllWaiting(false);

            setPlayerClockTime(() => player1, -SpectatorSyncManager.SYNC_TARGET);
            assertCatchingUp(() => player1, false);
            assertPlayerClockState(() => player1, true);
        }

        [Test]
        public void TestPlayerClockSlowsDownWhenSlightlyAhead()
        {
            setAllWaiting(false);

            // Ahead by more than MAX_SYNC_OFFSET but within MAX_SLOWDOWN_OFFSET: ease back at the slow rate, keep running.
            setPlayerClockTime(() => player1, -SpectatorSyncManager.MAX_SYNC_OFFSET - 1);
            assertSlowingDown(() => player1, true);
            assertHalted(() => player1, false);
            assertPlayerClockState(() => player1, true);
        }

        [Test]
        public void TestPlayerClockStopsWhenTooFarAheadAndStartsWhenBackInSync()
        {
            setAllWaiting(false);

            // Ahead by more than MAX_SLOWDOWN_OFFSET: freeze rather than slow down (IsRunning = false, not slowing).
            setPlayerClockTime(() => player1, -SpectatorSyncManager.MAX_SLOWDOWN_OFFSET - 1);
            assertHalted(() => player1, true);
            assertSlowingDown(() => player1, false);
            assertPlayerClockState(() => player1, false);

            // Master catches up to within the sync target: resume at normal rate, no trailing slow-down.
            setMasterTime(SpectatorSyncManager.MAX_SLOWDOWN_OFFSET + 1);
            assertHalted(() => player1, false);
            assertSlowingDown(() => player1, false);
            assertPlayerClockState(() => player1, true);
        }

        [Test]
        public void TestInSyncPlayerClockDoesNotStartIfWaitingOnFrames()
        {
            setAllWaiting(false);

            assertPlayerClockState(() => player1, true);
            setWaiting(() => player1, true);
            assertPlayerClockState(() => player1, false);
        }

        [Test]
        public void TestMasterStopsAtCeilingAndStartsBelowIt()
        {
            AddStep("start master when ready", () => syncManager.ReadyToStart = () => master.Start());

            setLatestFrameTime(() => player1, 100000);
            setLatestFrameTime(() => player2, 100000);
            setAllWaiting(false);

            // ceiling = min(100000, 100000) - LIVE_EDGE_BUFFER = 99800.
            setMasterTime(99900);
            assertMasterRunning(false);

            setMasterTime(50000);
            assertMasterRunning(true);
        }

        [Test]
        public void TestPlayerAbandonedPastCapAndReincludedWhenRecovered()
        {
            setLatestFrameTime(() => player1, 100000);
            setLatestFrameTime(() => player2, 100000 - SpectatorSyncManager.MAX_LIVE_OFFSET - 10000); // 40s behind
            setAllWaiting(false);

            assertAbandoned(() => player1, false);
            assertAbandoned(() => player2, true);

            // Recover to comfortably within (cap - hysteresis): re-included.
            setLatestFrameTime(() => player2, 100000 - (SpectatorSyncManager.MAX_LIVE_OFFSET - SpectatorSyncManager.ABANDON_HYSTERESIS - 1000));
            assertAbandoned(() => player2, false);
        }

        [Test]
        public void TestAbandonedPlayerDoesNotCatchUp()
        {
            setLatestFrameTime(() => player1, 100000);
            setLatestFrameTime(() => player2, 100000 - SpectatorSyncManager.MAX_LIVE_OFFSET - 10000); // abandoned
            setAllWaiting(false);

            // Master well ahead of player2: a kept player here would catch up (and stutter against its frame
            // buffer), but an abandoned one must run at the natural rate instead.
            setMasterTime(SpectatorSyncManager.MAX_SYNC_OFFSET + 1000);

            assertAbandoned(() => player2, true);
            assertCatchingUp(() => player2, false);
            assertPlayerClockState(() => player2, true);
        }

        [Test]
        public void TestDrainedPlayerClockStaysStoppedAfterNonDrainingRemoval()
        {
            setAllWaiting(false);
            assertPlayerClockState(() => player1, true);

            // A drained clock is no longer paced, but keeps running while it still has frames to play out.
            removeClock(() => player1, true);
            assertPlayerClockState(() => player1, true);

            // A subsequent non-draining removal must stop it for good, even though frames remain available
            // (WaitingOnFrames is false here, which is exactly the state the drain loop would resume it in).
            removeClock(() => player1, false);
            assertPlayerClockState(() => player1, false);

            AddWaitStep("wait for several updates", 5);
            assertPlayerClockState(() => player1, false);
        }

        [Test]
        public void TestRepeatedDrainingRemovalsStillStopOnNonDrainingRemoval()
        {
            setAllWaiting(false);

            // Draining the same clock twice must not register it twice, else a single non-draining removal
            // would only undo one of the registrations and the clock would keep being resumed.
            removeClock(() => player1, true);
            removeClock(() => player1, true);
            assertPlayerClockState(() => player1, true);

            removeClock(() => player1, false);
            assertPlayerClockState(() => player1, false);

            AddWaitStep("wait for several updates", 5);
            assertPlayerClockState(() => player1, false);
        }

        private void setWaiting(Func<SpectatorPlayerClock> playerClock, bool waiting)
            => AddStep($"set player clock {clocksById[playerClock()]} waiting = {waiting}", () => playerClock().WaitingOnFrames = waiting);

        private void setAllWaiting(bool waiting) => AddStep($"set all player clocks waiting = {waiting}", () =>
        {
            player1.WaitingOnFrames = waiting;
            player2.WaitingOnFrames = waiting;
        });

        private void setMasterTime(double time)
            => AddStep($"set master = {time}", () => master.Seek(time));

        /// <summary>
        /// clock.Time = master.Time - offsetFromMaster
        /// </summary>
        private void setPlayerClockTime(Func<SpectatorPlayerClock> playerClock, double offsetFromMaster)
            => AddStep($"set player clock {clocksById[playerClock()]} = master - {offsetFromMaster}", () => playerClock().Seek(master.CurrentTime - offsetFromMaster));

        private void setLatestFrameTime(Func<SpectatorPlayerClock> playerClock, double time)
            => AddStep($"set player clock {clocksById[playerClock()]} latest frame = {time}", () => playerClock().LatestFrameTime = time);

        private void removeClock(Func<SpectatorPlayerClock> playerClock, bool drain)
            => AddStep($"remove player clock {clocksById[playerClock()]} (drain = {drain})", () => syncManager.RemoveManagedClock(playerClock(), drain));

        private void assertMasterRunning(bool running)
            => AddAssert($"master {(running ? "is" : "is not")} running", () => master.IsRunning == running);

        private void assertAbandoned(Func<SpectatorPlayerClock> playerClock, bool abandoned)
            => AddAssert($"player clock {clocksById[playerClock()]} {(abandoned ? "is" : "is not")} abandoned", () => playerClock().Abandoned == abandoned);

        private void assertCatchingUp(Func<SpectatorPlayerClock> playerClock, bool catchingUp) =>
            AddAssert($"player clock {clocksById[playerClock()]} {(catchingUp ? "is" : "is not")} catching up", () => playerClock().IsCatchingUp == catchingUp);

        private void assertSlowingDown(Func<SpectatorPlayerClock> playerClock, bool slowingDown) =>
            AddAssert($"player clock {clocksById[playerClock()]} {(slowingDown ? "is" : "is not")} slowing down", () => playerClock().IsSlowingDown == slowingDown);

        private void assertHalted(Func<SpectatorPlayerClock> playerClock, bool halted) =>
            AddAssert($"player clock {clocksById[playerClock()]} {(halted ? "is" : "is not")} halted", () => playerClock().IsHalted == halted);

        private void assertPlayerClockState(Func<SpectatorPlayerClock> playerClock, bool running)
            => AddAssert($"player clock {clocksById[playerClock()]} {(running ? "is" : "is not")} running", () => playerClock().IsRunning == running);

        private class TestManualClock : ManualClock, IAdjustableClock
        {
            public TestManualClock()
            {
                IsRunning = true;
            }

            public void Start() => IsRunning = true;

            public void Stop() => IsRunning = false;

            public bool Seek(double position)
            {
                CurrentTime = position;
                return true;
            }

            public void Reset()
            {
                IsRunning = false;
                CurrentTime = 0;
            }

            public void ResetSpeedAdjustments()
            {
            }
        }
    }
}
