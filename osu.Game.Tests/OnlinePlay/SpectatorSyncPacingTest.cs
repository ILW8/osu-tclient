// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Game.Screens.OnlinePlay.Multiplayer.Spectate;

namespace osu.Game.Tests.OnlinePlay
{
    [TestFixture]
    public class SpectatorSyncPacingTest
    {
        // --- UpdateAbandoned ---

        [Test]
        public void NotAbandoned_withinCap_staysKept()
        {
            // Behind by less than the cap: not abandoned.
            double behind = SpectatorSyncManager.MAX_LIVE_OFFSET - 1000;
            Assert.That(SpectatorSyncManager.UpdateAbandoned(false, 100000, 100000 - behind), Is.False);
        }

        [Test]
        public void NotAbandoned_pastCap_becomesAbandoned()
        {
            // Behind by more than the cap: abandoned.
            double behind = SpectatorSyncManager.MAX_LIVE_OFFSET + 1000;
            Assert.That(SpectatorSyncManager.UpdateAbandoned(false, 100000, 100000 - behind), Is.True);
        }

        [Test]
        public void Abandoned_insideHysteresisBand_staysAbandoned()
        {
            // Below the cap but above (cap - hysteresis), so it does NOT flap back.
            double behind = SpectatorSyncManager.MAX_LIVE_OFFSET - SpectatorSyncManager.ABANDON_HYSTERESIS / 2;
            Assert.That(SpectatorSyncManager.UpdateAbandoned(true, 100000, 100000 - behind), Is.True);
        }

        [Test]
        public void Abandoned_comfortablyWithinCap_reincluded()
        {
            // Below (cap - hysteresis): re-included.
            double behind = SpectatorSyncManager.MAX_LIVE_OFFSET - SpectatorSyncManager.ABANDON_HYSTERESIS - 1000;
            Assert.That(SpectatorSyncManager.UpdateAbandoned(true, 100000, 100000 - behind), Is.False);
        }

        [Test]
        public void Sentinel_isMaximallyBehind_abandoned()
        {
            // A zero-frame player reports NegativeInfinity: infinitely behind -> abandoned.
            Assert.That(SpectatorSyncManager.UpdateAbandoned(false, 100000, double.NegativeInfinity), Is.True);
        }

        // --- ShouldStopMaster ---

        [Test]
        public void NoKeptPlayers_neverStops()
        {
            // All-abandoned / empty set: keep running to play out to the end.
            Assert.That(SpectatorSyncManager.ShouldStopMaster(999999, System.Array.Empty<double>()), Is.False);
        }

        [Test]
        public void MasterBelowCeiling_keepsRunning()
        {
            // ceiling = min(10000, 12000) - 200 = 9800; master 9000 < 9800 -> keep running.
            Assert.That(SpectatorSyncManager.ShouldStopMaster(9000, new[] { 10000d, 12000d }), Is.False);
        }

        [Test]
        public void MasterAtOrAboveCeiling_stops()
        {
            // ceiling = 10000 - 200 = 9800; master 9800 >= 9800 -> stop.
            Assert.That(SpectatorSyncManager.ShouldStopMaster(9800, new[] { 10000d, 12000d }), Is.True);
        }

        [Test]
        public void CeilingUsesSlowestKeptEdge()
        {
            // ceiling driven by the minimum edge (5000), not the max: 5000 - 200 = 4800; master 6000 > 4800 -> stop.
            Assert.That(SpectatorSyncManager.ShouldStopMaster(6000, new[] { 5000d, 30000d }), Is.True);
        }

        [Test]
        public void CeilingUsesGivenBuffer()
        {
            // ceiling = 10000 - 500 = 9500; master 9600 is under the default-buffer ceiling (9800) but over this one -> stop.
            Assert.That(SpectatorSyncManager.ShouldStopMaster(9600, new[] { 10000d, 12000d }, 500), Is.True);
        }
    }
}
