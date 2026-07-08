// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Game.Online.Multiplayer;
using osu.Game.Tournament.Components;

namespace osu.Game.Tournament.Tests.NonVisual
{
    [TestFixture]
    public class TournamentSpectatorSlotsTest
    {
        [Test]
        public void Snapshot_skipsNonParticipating_andAssignsSequentially()
        {
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, string?, MultiplayerUserState)[]
            {
                (10, "spectator", MultiplayerUserState.Spectating), // the tourney client itself — skipped
                (11, "dev1", MultiplayerUserState.Playing), // slot 0
                (12, "dev2", MultiplayerUserState.Idle), // skipped
                (13, "dev3", MultiplayerUserState.Loaded), // slot 1
                (14, "dev4", MultiplayerUserState.WaitingForLoad), // slot 2
            });

            Assert.That(slots.Count, Is.EqualTo(3));
            Assert.That(slots[11], Is.EqualTo(0));
            Assert.That(slots[13], Is.EqualTo(1));
            Assert.That(slots[14], Is.EqualTo(2));
            Assert.That(slots.ContainsKey(10), Is.False);
            Assert.That(slots.ContainsKey(12), Is.False);
        }

        [Test]
        public void RoomName_reservesLeftRightSlotsByUsername()
        {
            // Users are listed dev3-then-dev2, but the room name puts dev2 on the left.
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, string?, MultiplayerUserState)[]
            {
                (3, "dev3", MultiplayerUserState.Playing),
                (2, "dev2", MultiplayerUserState.Playing),
            }, "LGA: (dev2) vs (dev3)");

            Assert.That(slots[2], Is.EqualTo(0)); // Name 1 -> left
            Assert.That(slots[3], Is.EqualTo(1)); // Name 2 -> right
        }

        [Test]
        public void RoomName_keepsRightSlotWhenLeftUserAbsent()
        {
            // Only the right-hand (Name 2) user is participating; it must not shift into slot 0.
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, string?, MultiplayerUserState)[]
            {
                (2, "dev2", MultiplayerUserState.Idle), // Name 1 not playing -> no reservation
                (3, "dev3", MultiplayerUserState.Playing),
            }, "LGA: (dev2) vs (dev3)");

            Assert.That(slots.Count, Is.EqualTo(1));
            Assert.That(slots[3], Is.EqualTo(1));
        }

        [Test]
        public void RoomName_extraPlayersFillRemainingSlots()
        {
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, string?, MultiplayerUserState)[]
            {
                (5, "extra", MultiplayerUserState.Playing),
                (3, "dev3", MultiplayerUserState.Playing),
                (2, "dev2", MultiplayerUserState.Playing),
            }, "LGA: (dev2) vs (dev3)");

            Assert.That(slots[2], Is.EqualTo(0));
            Assert.That(slots[3], Is.EqualTo(1));
            Assert.That(slots[5], Is.EqualTo(2)); // fills the first free slot after the reservations
        }

        [Test]
        public void RoomName_fallsBackToSequentialWhenNameDoesNotMatchConvention()
        {
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, string?, MultiplayerUserState)[]
            {
                (3, "dev3", MultiplayerUserState.Playing),
                (2, "dev2", MultiplayerUserState.Playing),
            }, "just a casual room");

            Assert.That(slots[3], Is.EqualTo(0)); // input order preserved
            Assert.That(slots[2], Is.EqualTo(1));
        }

        [Test]
        public void IsParticipating_onlyForActiveGameplayStates()
        {
            Assert.That(TournamentSpectatorScreen.IsParticipating(MultiplayerUserState.Playing), Is.True);
            Assert.That(TournamentSpectatorScreen.IsParticipating(MultiplayerUserState.Loaded), Is.True);
            Assert.That(TournamentSpectatorScreen.IsParticipating(MultiplayerUserState.ReadyForGameplay), Is.True);
            Assert.That(TournamentSpectatorScreen.IsParticipating(MultiplayerUserState.WaitingForLoad), Is.True);

            Assert.That(TournamentSpectatorScreen.IsParticipating(MultiplayerUserState.Idle), Is.False);
            Assert.That(TournamentSpectatorScreen.IsParticipating(MultiplayerUserState.Ready), Is.False);
            Assert.That(TournamentSpectatorScreen.IsParticipating(MultiplayerUserState.Spectating), Is.False);
            Assert.That(TournamentSpectatorScreen.IsParticipating(MultiplayerUserState.Results), Is.False);
            Assert.That(TournamentSpectatorScreen.IsParticipating(MultiplayerUserState.FinishedPlay), Is.False);
        }
    }
}
