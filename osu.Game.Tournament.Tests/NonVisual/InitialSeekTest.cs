// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Game.Tournament.Components;

namespace osu.Game.Tournament.Tests.NonVisual
{
    [TestFixture]
    public class InitialSeekTest
    {
        [Test]
        public void AllWithinCap_seedsAtMinEdgeMinusBuffer()
        {
            // max 12000; all within the 30000 cap; min edge 10000 - LIVE_EDGE_BUFFER (200) = 9800.
            Assert.That(TournamentSpectatorScreen.ComputeInitialSeekTime(new[] { 10000d, 12000d, 11000d }), Is.EqualTo(9800d));
        }

        [Test]
        public void CustomBuffer_seedsAtMinEdgeMinusGivenBuffer()
        {
            // Same edges as above with a 500ms buffer: 10000 - 500 = 9500.
            Assert.That(TournamentSpectatorScreen.ComputeInitialSeekTime(new[] { 10000d, 12000d, 11000d }, 500), Is.EqualTo(9500d));
        }

        [Test]
        public void FarBehindPlayer_droppedBeforeMin()
        {
            // -25000 is 46000 behind the 21000 max edge (> 30000 cap): dropped. min of the rest 20000 - 200 = 19800.
            Assert.That(TournamentSpectatorScreen.ComputeInitialSeekTime(new[] { -25000d, 20000d, 21000d }), Is.EqualTo(19800d));
        }

        [Test]
        public void LatePlayerWithinCap_kept()
        {
            // max 30000; 5000 is 25000 behind (< 30000 cap): kept. seed 5000 - 200 = 4800.
            Assert.That(TournamentSpectatorScreen.ComputeInitialSeekTime(new[] { 5000d, 30000d }), Is.EqualTo(4800d));
        }

        [Test]
        public void Empty_returnsZero()
        {
            Assert.That(TournamentSpectatorScreen.ComputeInitialSeekTime(System.Array.Empty<double>()), Is.EqualTo(0d));
        }

        [Test]
        public void AllNoFrames_returnsZero()
        {
            // All-sentinel input (no frames anywhere): nothing kept, seed 0.
            Assert.That(TournamentSpectatorScreen.ComputeInitialSeekTime(new[] { double.NegativeInfinity, double.NegativeInfinity }), Is.EqualTo(0d));
        }
    }
}
