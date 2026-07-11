// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.Models;

namespace osu.Game.Tournament.Tests.NonVisual
{
    [TestFixture]
    public class TournamentSpectatorSlotsTest
    {
        [Test]
        public void Snapshot_skipsNonParticipating_andAssignsSequentially()
        {
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, string?, MultiplayerUserState, MatchUserState?)[]
            {
                (10, "spectator", MultiplayerUserState.Spectating, null), // the tourney client itself — skipped
                (11, "dev1", MultiplayerUserState.Playing, null), // slot 0
                (12, "dev2", MultiplayerUserState.Idle, null), // skipped
                (13, "dev3", MultiplayerUserState.Loaded, null), // slot 1
                (14, "dev4", MultiplayerUserState.WaitingForLoad, null), // slot 2
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
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, string?, MultiplayerUserState, MatchUserState?)[]
            {
                (3, "dev3", MultiplayerUserState.Playing, null),
                (2, "dev2", MultiplayerUserState.Playing, null),
            }, "LGA: (dev2) vs (dev3)");

            Assert.That(slots[2], Is.EqualTo(0)); // Name 1 -> left
            Assert.That(slots[3], Is.EqualTo(1)); // Name 2 -> right
        }

        [Test]
        public void RoomName_keepsRightSlotWhenLeftUserAbsent()
        {
            // Only the right-hand (Name 2) user is participating; it must not shift into slot 0.
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, string?, MultiplayerUserState, MatchUserState?)[]
            {
                (2, "dev2", MultiplayerUserState.Idle, null), // Name 1 not playing -> no reservation
                (3, "dev3", MultiplayerUserState.Playing, null),
            }, "LGA: (dev2) vs (dev3)");

            Assert.That(slots.Count, Is.EqualTo(1));
            Assert.That(slots[3], Is.EqualTo(1));
        }

        [Test]
        public void RoomName_extraPlayersFillRemainingSlots()
        {
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, string?, MultiplayerUserState, MatchUserState?)[]
            {
                (5, "extra", MultiplayerUserState.Playing, null),
                (3, "dev3", MultiplayerUserState.Playing, null),
                (2, "dev2", MultiplayerUserState.Playing, null),
            }, "LGA: (dev2) vs (dev3)");

            Assert.That(slots[2], Is.EqualTo(0));
            Assert.That(slots[3], Is.EqualTo(1));
            Assert.That(slots[5], Is.EqualTo(2)); // fills the first free slot after the reservations
        }

        [Test]
        public void RoomName_fallsBackToSequentialWhenNameDoesNotMatchConvention()
        {
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, string?, MultiplayerUserState, MatchUserState?)[]
            {
                (3, "dev3", MultiplayerUserState.Playing, null),
                (2, "dev2", MultiplayerUserState.Playing, null),
            }, "just a casual room");

            Assert.That(slots[3], Is.EqualTo(0)); // input order preserved
            Assert.That(slots[2], Is.EqualTo(1));
        }

        [Test]
        public void TeamState_reservesLeftForRed_rightForBlue()
        {
            // blue first with no room-name hint, the red player should still take the left slot
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, string?, MultiplayerUserState, MatchUserState?)[]
            {
                (2, "blueP", MultiplayerUserState.Playing, new TeamVersusUserState { TeamID = (int)TeamColour.Blue }),
                (1, "redP", MultiplayerUserState.Playing, new TeamVersusUserState { TeamID = (int)TeamColour.Red }),
            });

            Assert.That(slots[1], Is.EqualTo(0)); // red: left
            Assert.That(slots[2], Is.EqualTo(1)); // blue: right
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
