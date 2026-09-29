// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Spectator;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Screens.OnlinePlay.Multiplayer.Spectate;
using osu.Game.Screens.Play;
using osu.Game.Tests.Visual.Multiplayer;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;
using osuTK;

namespace osu.Game.Tournament.Tests.Components
{
    public partial class TestSceneTournamentSpectatorScreen : MultiplayerTestScene
    {
        private const int beatmap_online_id = 424243;

        [Cached]
        private readonly LadderInfo ladder = new LadderInfo();

        [Cached]
        private readonly MatchIPCInfo ipc = new MatchIPCInfo();

        [Resolved]
        private BeatmapManager beatmapManager { get; set; } = null!;

        [Resolved]
        private RealmAccess realm { get; set; } = null!;

        private TournamentSpectatorScreen spectatorScreen = null!;

        private bool beatmapImported;

        public override void SetUpSteps()
        {
            base.SetUpSteps();

            AddStep("import beatmap", importBeatmap);

            AddStep("join room", () => JoinRoom(CreateDefaultRoom()));
            WaitForJoined();

            AddStep("start play", () =>
            {
                foreach (int id in new[] { PLAYER_1_ID, PLAYER_2_ID })
                {
                    MultiplayerClient.AddUser(new APIUser { Id = id }, true);
                    SpectatorClient.SendStartPlay(id, beatmap_online_id);
                }
            });

            AddStep("load screen", () => LoadScreen(spectatorScreen = new TournamentSpectatorScreen(new[] { PLAYER_1_ID, PLAYER_2_ID })));
            AddUntilStep("wait for players to load", () =>
            {
                var areas = spectatorScreen.ChildrenOfType<PlayerArea>().ToArray();
                return areas.Length == 2 && areas.All(a => a.PlayerLoaded);
            });

            sendFrames(PLAYER_1_ID, 50);
            sendFrames(PLAYER_2_ID, 50);
            AddUntilStep("wait for player 2 running", () => clockOf(PLAYER_2_ID).IsRunning);
        }

        [Test]
        public void TestQuitHoldsTileWithoutHoldingOthers()
        {
            AddStep("player 2 quits", () => SpectatorClient.SendEndPlay(PLAYER_2_ID));
            AddUntilStep("player 2 held", () => !clockOf(PLAYER_2_ID).IsRunning);
            AddAssert("player 2 still watched", () => SpectatorClient.WatchedUserStates.ContainsKey(PLAYER_2_ID));

            sendFrames(PLAYER_1_ID, 100);
            AddUntilStep("master passes player 2's last frame", () => masterClock().CurrentTime > clockOf(PLAYER_2_ID).LatestFrameTime);
            AddUntilStep("player 1 running", () => clockOf(PLAYER_1_ID).IsRunning);
            AddAssert("player 2 still held", () => !clockOf(PLAYER_2_ID).IsRunning);
        }

        [Test]
        public void TestQuitArrivingWithFinalFramesStaysHeld()
        {
            // A real quit flushes the player's last frames just ahead of the Quit, so both can land in the same update.
            // Those frames must not be mistaken for the session resuming.
            AddStep("player 2 sends final frames and quits", () =>
            {
                SpectatorClient.SendFramesFromUser(PLAYER_2_ID, 10);
                SpectatorClient.SendEndPlay(PLAYER_2_ID);
            });
            AddUntilStep("player 2 held", () => !clockOf(PLAYER_2_ID).IsRunning);

            sendFrames(PLAYER_1_ID, 100);
            AddUntilStep("master passes player 2's last frame", () => masterClock().CurrentTime > clockOf(PLAYER_2_ID).LatestFrameTime);
            AddAssert("player 2 still held", () => !clockOf(PLAYER_2_ID).IsRunning);
        }

        [Test]
        public void TestTileResumesWhenFramesContinueAfterQuit()
        {
            AddStep("player 2 quits", () => SpectatorClient.SendEndPlay(PLAYER_2_ID));
            AddUntilStep("player 2 held", () => !clockOf(PLAYER_2_ID).IsRunning);

            sendFrames(PLAYER_1_ID, 100);
            reconnect(PLAYER_2_ID, framesFrom: 7000);

            AddUntilStep("player 2 running again", () => clockOf(PLAYER_2_ID).IsRunning);
            AddAssert("player 2 replay awaits further frames", () => !areaOf(PLAYER_2_ID).Score!.Replay.HasReceivedAllFrames);
            AddUntilStep("player 2 plays past the gap", () => clockOf(PLAYER_2_ID).CurrentTime > 7000);
        }

        [Test]
        public void TestPassAfterResumeCompletesTileReplay()
        {
            AddStep("player 2 quits", () => SpectatorClient.SendEndPlay(PLAYER_2_ID));
            AddUntilStep("player 2 held", () => !clockOf(PLAYER_2_ID).IsRunning);

            sendFrames(PLAYER_1_ID, 100);
            reconnect(PLAYER_2_ID, framesFrom: 7000);
            AddUntilStep("player 2 running again", () => clockOf(PLAYER_2_ID).IsRunning);

            AddStep("player 2 passes", () => SpectatorClient.SendEndPlay(PLAYER_2_ID, SpectatedUserState.Passed));
            AddUntilStep("player 2 replay complete", () => areaOf(PLAYER_2_ID).Score!.Replay.HasReceivedAllFrames);
            AddAssert("player 2 still running", () => clockOf(PLAYER_2_ID).IsRunning);
        }

        /// <summary>
        /// What a spectator-server reconnect looks like to a watcher: the same play is re-announced and frames continue
        /// after a gap.
        /// </summary>
        private void reconnect(int userId, double framesFrom) => AddStep($"{userId} reconnects", () =>
        {
            SpectatorClient.SendStartPlay(userId, beatmap_online_id);
            SpectatorClient.SendFramesFromUser(userId, 100, startTime: framesFrom);
        });

        private void importBeatmap()
        {
            if (beatmapImported)
                return;

            var working = beatmapManager.CreateNew(new OsuRuleset().RulesetInfo, new GuestUser());
            var beatmap = (Beatmap)working.Beatmap;
            beatmap.ControlPointInfo.Add(0, new TimingControlPoint { BeatLength = 500 });

            for (int i = 0; i < 200; i++)
                beatmap.HitObjects.Add(new HitCircle { StartTime = 1000 + i * 500, Position = new Vector2(i % 2 == 0 ? 100 : 400, 200) });

            beatmapManager.Save(working.BeatmapInfo, beatmap);
            realm.Write(r => r.Find<BeatmapInfo>(working.BeatmapInfo.ID)!.OnlineID = beatmap_online_id);

            beatmapImported = true;
        }

        private void sendFrames(int userId, int count) => AddStep($"send {count} frames for {userId}", () => SpectatorClient.SendFramesFromUser(userId, count));

        private PlayerArea areaOf(int userId) => spectatorScreen.ChildrenOfType<PlayerArea>().Single(a => a.UserId == userId);

        private SpectatorPlayerClock clockOf(int userId) => areaOf(userId).SpectatorPlayerClock;

        private MasterGameplayClockContainer masterClock() => spectatorScreen.ChildrenOfType<MasterGameplayClockContainer>().Single();
    }
}
