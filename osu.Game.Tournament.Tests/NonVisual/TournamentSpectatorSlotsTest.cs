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
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, MultiplayerUserState, MatchUserState?)[]
            {
                (10, MultiplayerUserState.Spectating, null), // the tourney client itself — skipped
                (11, MultiplayerUserState.Playing, null), // slot 0
                (12, MultiplayerUserState.Idle, null), // skipped
                (13, MultiplayerUserState.Loaded, null), // slot 1
                (14, MultiplayerUserState.WaitingForLoad, null), // slot 2
            }, playersPerTeam: 4);

            Assert.That(slots.Count, Is.EqualTo(3));
            Assert.That(slots[11], Is.EqualTo(0));
            Assert.That(slots[13], Is.EqualTo(1));
            Assert.That(slots[14], Is.EqualTo(2));
            Assert.That(slots.ContainsKey(10), Is.False);
            Assert.That(slots.ContainsKey(12), Is.False);
        }

        [Test]
        public void TeamState_reservesLeftForRed_rightForBlue()
        {
            // blue listed first with no room-name hint, the red player should still take the left slot
            var slots = TournamentSpectatorScreen.SnapshotSlots(new (int, MultiplayerUserState, MatchUserState?)[]
            {
                (2, MultiplayerUserState.Playing, new TeamVersusUserState { TeamID = (int)TeamColour.Blue }),
                (1, MultiplayerUserState.Playing, new TeamVersusUserState { TeamID = (int)TeamColour.Red }),
            }, playersPerTeam: 1);

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

        [Test]
        public void CardSlots_beforeLoad_includesEveryoneButSpectators()
        {
            var slots = RoomUserCardGrid.CardSlots(new (int, MultiplayerUserState, MatchUserState?)[]
            {
                (10, MultiplayerUserState.Spectating, null), // the tourney client itself — no card
                (11, MultiplayerUserState.Idle, new TeamVersusUserState { TeamID = (int)TeamColour.Blue }),
                (12, MultiplayerUserState.Ready, new TeamVersusUserState { TeamID = (int)TeamColour.Red }),
                (13, MultiplayerUserState.Idle, new TeamVersusUserState { TeamID = (int)TeamColour.Red }),
            }, playersPerTeam: 2);

            Assert.That(slots.ContainsKey(10), Is.False);
            Assert.That(slots[12], Is.EqualTo(0)); // red block, input order
            Assert.That(slots[13], Is.EqualTo(1));
            Assert.That(slots[11], Is.EqualTo(2)); // blue block
        }

        [Test]
        public void CardSlots_onceLoading_matchesTileSnapshot()
        {
            var users = new (int, MultiplayerUserState, MatchUserState?)[]
            {
                (10, MultiplayerUserState.Spectating, null),
                (11, MultiplayerUserState.Idle, new TeamVersusUserState { TeamID = (int)TeamColour.Red }), // sub sitting out
                (12, MultiplayerUserState.WaitingForLoad, new TeamVersusUserState { TeamID = (int)TeamColour.Red }),
                (13, MultiplayerUserState.WaitingForLoad, new TeamVersusUserState { TeamID = (int)TeamColour.Blue }),
            };

            var slots = RoomUserCardGrid.CardSlots(users, playersPerTeam: 2);

            Assert.That(slots, Is.EquivalentTo(TournamentSpectatorScreen.SnapshotSlots(users, playersPerTeam: 2)));
            Assert.That(slots.ContainsKey(11), Is.False);
            Assert.That(slots[12], Is.EqualTo(0));
            Assert.That(slots[13], Is.EqualTo(2));
        }
    }
}
