// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Rooms;
using osu.Game.Tests.Visual.Multiplayer;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.Models;
using osu.Game.Users;
using osuTK;

namespace osu.Game.Tournament.Tests.Components
{
    public partial class TestSceneRoomUserCardGrid : MultiplayerTestScene
    {
        [Cached]
        private readonly LadderInfo ladder = new LadderInfo();

        private RoomUserCardGrid grid = null!;
        private APIUser[] users = null!;

        public override void SetUpSteps()
        {
            base.SetUpSteps();

            AddStep("join team versus room", () => JoinRoom(CreateDefaultRoom(MatchType.TeamVersus)));
            WaitForJoined();

            // The tourney client joins as a spectator; mirror that so the local user gets no card.
            AddStep("local user spectates", () => MultiplayerClient.ChangeState(MultiplayerUserState.Spectating).WaitSafely());

            AddStep("players per team = 2", () => ladder.PlayersPerTeam.Value = 2);

            AddStep("create grid", () => Child = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(1024, 512),
                Child = grid = new RoomUserCardGrid { RelativeSizeAxes = Axes.Both },
            });

            AddStep("add four users", () =>
            {
                users = Enumerable.Range(1, 4).Select(i => new APIUser { Id = 2000 + i, Username = $"Player {i}", CountryCode = CountryCode.AU }).ToArray();

                foreach (var user in users)
                    MultiplayerClient.AddUser(user);
            });

            AddUntilStep("four cards", () => cards().Select(c => c.UserId).OrderBy(id => id), () => Is.EqualTo(new[] { 2001, 2002, 2003, 2004 }));
        }

        [Test]
        public void TestStatusUpdatesInPlace()
        {
            RoomUserCard card = null!;

            AddStep("grab card", () => card = cardOf(2001));
            AddUntilStep("not ready", () => card.Status == "Not ready");

            AddStep("downloading 45%", () => MultiplayerClient.ChangeUserBeatmapAvailability(2001, BeatmapAvailability.Downloading(0.45f)));
            AddUntilStep("shows progress", () => card.Status == "Downloading 45%");

            AddStep("downloaded and ready", () =>
            {
                MultiplayerClient.ChangeUserBeatmapAvailability(2001, BeatmapAvailability.LocallyAvailable());
                MultiplayerClient.ChangeUserState(2001, MultiplayerUserState.Ready);
            });
            AddUntilStep("ready", () => card.Status == "Ready");
            AddAssert("same card instance", () => cardOf(2001), () => Is.SameAs(card));
        }

        [Test]
        public void TestJoinAndLeaveKeepOtherCards()
        {
            RoomUserCard[] before = null!;
            RoomUserCard joined = null!;

            AddStep("grab cards", () => before = cards());

            AddStep("user 5 joins", () => MultiplayerClient.AddUser(new APIUser { Id = 2005, Username = "Player 5" }));
            AddUntilStep("five cards", () => cards().Length == 5);
            AddStep("grab user 5's card", () => joined = cardOf(2005));

            // Moves user 5 out of overflow into the freed slot.
            AddStep("user 4 leaves", () => MultiplayerClient.RemoveUser(users[3]));
            AddUntilStep("four cards", () => cards().Length == 4);

            AddAssert("users 1-3 kept their cards", () => before.Where(c => c.UserId != 2004).All(c => cards().Contains(c)));
            AddAssert("user 5 kept its card", () => cardOf(2005), () => Is.SameAs(joined));
        }

        [Test]
        public void TestTeamSwitchUpdatesColour()
        {
            AddAssert("user 1 is blue", () => cardOf(2001).AccentColour, () => Is.EqualTo(TournamentGame.COLOUR_BLUE));
            AddStep("user 1 switches to red", () => MultiplayerClient.SendUserMatchRequest(2001, new ChangeTeamRequest { TeamID = (int)TeamColour.Red }).WaitSafely());
            AddUntilStep("user 1 is red", () => cardOf(2001).AccentColour, () => Is.EqualTo(TournamentGame.COLOUR_RED));
        }

        [Test]
        public void TestLocalUserNeverShown()
        {
            // A host abort resets every user to Idle on the server, the tourney client's own spectating user included.
            AddStep("local user knocked back to idle", () => MultiplayerClient.ChangeState(MultiplayerUserState.Idle).WaitSafely());
            AddWaitStep("let room update arrive", 5);
            AddAssert("still only the four players", () => cards().Select(c => c.UserId).OrderBy(id => id), () => Is.EqualTo(new[] { 2001, 2002, 2003, 2004 }));
        }

        [Test]
        public void TestLoadingNarrowsToLoadingUsers()
        {
            AddStep("users 1-3 start loading", () =>
            {
                foreach (int id in new[] { 2001, 2002, 2003 })
                    MultiplayerClient.ChangeUserState(id, MultiplayerUserState.WaitingForLoad);
            });
            AddUntilStep("three cards", () => cards().Length == 3);
            AddAssert("idle user has no card", () => cards().All(c => c.UserId != 2004));
        }

        [Test]
        public void TestHideAndShowAll()
        {
            AddStep("hide user 1", () => grid.HideCard(2001));
            AddAssert("user 1 hidden", () => cardOf(2001).Alpha == 0);

            AddStep("user 4 leaves (re-slots)", () => MultiplayerClient.RemoveUser(users[3]));
            AddUntilStep("three cards", () => cards().Length == 3);
            AddAssert("user 1 still hidden after rebuild", () => cardOf(2001).Alpha == 0);

            AddStep("show all", () => grid.ShowAllCards());
            AddAssert("all visible", () => cards().All(c => c.Alpha == 1));
        }

        [Test]
        public void TestCatchesUpAfterBeingHidden()
        {
            // A hidden tournament screen stops updating, so its queued room updates only apply once shown again.
            AddStep("hide grid", () => grid.Alpha = 0);
            AddStep("user 1 leaves", () => MultiplayerClient.RemoveUser(users[0]));
            AddWaitStep("let room update arrive", 2);
            AddStep("show grid", () => grid.Alpha = 1);
            AddUntilStep("three cards", () => cards().Length == 3);
        }

        [Test]
        public void TestMoreUsersThanSlots()
        {
            AddStep("players per team = 1", () => ladder.PlayersPerTeam.Value = 1);
            AddStep("add five more users", () =>
            {
                for (int i = 5; i <= 9; i++)
                    MultiplayerClient.AddUser(new APIUser { Id = 2000 + i, Username = $"Player {i}" });
            });
            AddUntilStep("cards capped at grid max", () => cards().Length == TournamentPlayerGrid.MAX_SLOTS);
        }

        private RoomUserCard[] cards() => grid.ChildrenOfType<RoomUserCard>().ToArray();

        private RoomUserCard cardOf(int userId) => cards().Single(c => c.UserId == userId);
    }
}
