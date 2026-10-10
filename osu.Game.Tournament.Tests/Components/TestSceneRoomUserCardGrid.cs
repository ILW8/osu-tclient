// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Rooms;
using osu.Game.Tests.Visual.Multiplayer;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;
using osu.Game.Users;
using osuTK;

namespace osu.Game.Tournament.Tests.Components
{
    public partial class TestSceneRoomUserCardGrid : MultiplayerTestScene
    {
        /// <summary>
        /// The placeholder users' names, in join order (user IDs 2001 onwards). Setup adds the first four; <see cref="TestFullRoom"/> adds the rest.
        /// </summary>
        private static readonly string[] usernames =
        {
            "mrekk",
            "maliszewski",
            "SERBIANTRUCKER13",
            "Azer",
            "ThePooN",
            "Trosk-",
            "LeoFLT",
            "Player 8",
        };

        [Cached]
        private readonly LadderInfo ladder = new LadderInfo();

        [Cached]
        private readonly MatchIPCInfo ipc = new MatchIPCInfo();

        private RoomUserCardGrid grid = null!;
        private static readonly APIUser[] users = usernames.Select((name, i) => new APIUser { Id = 2001 + i, Username = name, CountryCode = CountryCode.AU }).ToArray();

        public override void SetUpSteps()
        {
            base.SetUpSteps();

            // The test client drops a joining user's APIUser and re-fetches it, which the default handler answers with
            // "User {id}" and no country. Answer for the placeholder users ourselves; anything else (the local user) falls through.
            AddStep("serve placeholder users", () =>
            {
                var api = (DummyAPIAccess)API;
                var fallback = api.HandleRequest;

                api.HandleRequest = request =>
                {
                    if (request is GetUsersRequest getUsers && getUsers.UserIds.All(id => users.Any(u => u.Id == id)))
                    {
                        getUsers.TriggerSuccess(new GetUsersResponse { Users = users.Where(u => getUsers.UserIds.Contains(u.Id)).ToList() });
                        return true;
                    }

                    return fallback?.Invoke(request) ?? false;
                };
            });

            AddStep("join team versus room", () => JoinRoom(CreateDefaultRoom(MatchType.TeamVersus)));
            WaitForJoined();

            // The tourney client joins as a spectator; mirror that so the local user gets no card.
            AddStep("local user spectates", () => MultiplayerClient.ChangeState(MultiplayerUserState.Spectating).WaitSafely());

            AddSliderStep("players per team", 1, 4, 2, v => ladder.PlayersPerTeam.Value = v);

            // Cards show the current beatmap's cover behind them.
            AddStep("set beatmap", () => ipc.Beatmap.Value = new TournamentBeatmap
            {
                Covers = new BeatmapSetOnlineCovers { Cover = "https://assets.ppy.sh/beatmaps/1/covers/cover.jpg" },
            });

            AddStep("create grid", () => Child = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(1024, 512),
                Child = grid = new RoomUserCardGrid { RelativeSizeAxes = Axes.Both },
            });

            AddStep("add four users", () =>
            {
                foreach (var user in users.Take(4))
                    MultiplayerClient.AddUser(user);
            });

            AddUntilStep("four cards", () => cards().Select(c => c.UserId).OrderBy(id => id), () => Is.EqualTo(new[] { 2001, 2002, 2003, 2004 }));
        }

        [Test]
        public void TestFullRoom()
        {
            AddStep("add users 5-8", () =>
            {
                foreach (var user in users.Skip(4))
                    MultiplayerClient.AddUser(user);
            });
            AddUntilStep("eight cards", () => cards().Length == 8);

            // The test client balances joins across teams, so this is four a side.
            AddStep("players per team = 4", () => ladder.PlayersPerTeam.Value = 4);
            AddUntilStep("all visible", () => cards().All(c => c.Alpha == 1));
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
        public void TestRefereeNeverShown()
        {
            // Referees join through the server's referee hub with the Referee role; they never play.
            AddStep("referee joins", () => MultiplayerClient.AddUser(new MultiplayerRoomUser(2010)
            {
                User = new APIUser { Id = 2010, Username = "Referee" },
                Role = MultiplayerRoomUserRole.Referee,
            }));
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
