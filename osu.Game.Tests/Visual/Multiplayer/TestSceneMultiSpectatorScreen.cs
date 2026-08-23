// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Rooms;
using osu.Game.Online.Spectator;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;
using osu.Game.Scoring;
using osu.Game.Screens.OnlinePlay.Multiplayer.Spectate;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Play.PlayerSettings;
using osu.Game.Storyboards;
using osu.Game.Tests.Beatmaps.IO;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Game.Tests.Visual.Multiplayer
{
    public partial class TestSceneMultiSpectatorScreen : MultiplayerTestScene
    {
        [Resolved]
        private OsuGameBase game { get; set; } = null!;

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        [Resolved]
        private BeatmapManager beatmapManager { get; set; } = null!;

        private MultiSpectatorScreen spectatorScreen = null!;
        private Room room = null!;

        private readonly List<MultiplayerRoomUser> playingUsers = new List<MultiplayerRoomUser>();

        private BeatmapSetInfo importedSet = null!;
        private BeatmapInfo importedBeatmap = null!;

        private int importedBeatmapId;

        /// <summary>
        /// Number of hit objects <see cref="TestPassedUserReachesResults"/> leaves the spectated beatmap with.
        /// </summary>
        private const int truncated_hit_object_count = 2;

        /// <summary>
        /// Number of judgements the objects left by that truncation produce, which is what the spectated player's
        /// final replay frame must report for the score to complete. See the comment in the test for why.
        /// </summary>
        private const int truncated_judgement_count = 4;

        /// <summary>
        /// Hit objects removed from the cached beatmap by <see cref="TestPassedUserReachesResults"/>, kept so they can
        /// be put back afterwards.
        /// </summary>
        private List<HitObject>? truncatedHitObjects;

        private HitObject[]? removedHitObjects;

        /// <summary>
        /// Strong reference to the truncated beatmap. <see cref="WorkingBeatmapCache"/> holds working beatmaps weakly,
        /// and a collected one is re-decoded from file, silently undoing the truncation.
        /// </summary>
        private WorkingBeatmap? truncatedWorkingBeatmap;

        [TearDownSteps]
        public void RestoreTruncatedBeatmap()
        {
            // Steps rather than a plain [TearDown], which the visual test browser does not run.
            AddStep("restore truncated beatmap", restoreTruncatedBeatmap);
        }

        /// <summary>
        /// Puts back whatever <see cref="TestPassedUserReachesResults"/> cut out of the shared beatmap. Idempotent, and
        /// a no-op if nothing was truncated.
        /// </summary>
        private void restoreTruncatedBeatmap()
        {
            if (truncatedHitObjects == null || removedHitObjects == null)
                return;

            truncatedHitObjects.AddRange(removedHitObjects);
            truncatedHitObjects = null;
            removedHitObjects = null;
            truncatedWorkingBeatmap = null;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            importedSet = BeatmapImportHelper.LoadOszIntoOsu(game, virtualTrack: true).GetResultSafely();
            importedBeatmap = importedSet.Beatmaps.First(b => b.Ruleset.OnlineID == 0);
            importedBeatmapId = importedBeatmap.OnlineID;
        }

        public override void SetUpSteps()
        {
            // Queued ahead of every other step, the base class' included: osu!framework abandons all remaining steps of
            // a test - its teardown steps along with them - as soon as one throws, so the teardown above only runs when
            // nothing went wrong. Restoring here as well means a failure part-way through the truncating test cannot
            // leak a two-object beatmap into the next test, in the headless runner and the test browser alike.
            AddStep("restore truncated beatmap", restoreTruncatedBeatmap);

            base.SetUpSteps();

            AddStep("clear playing users", () => playingUsers.Clear());

            AddStep("create room", () => room = CreateDefaultRoom());
            AddStep("join room", () => JoinRoom(room));
            WaitForJoined();
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(9)]
        public void TestGeneral(int count)
        {
            int[] userIds = getPlayerIds(count);

            start(userIds);
            loadSpectateScreen();

            sendFrames(userIds, 1000);
            AddWaitStep("wait a bit", 20);
        }

        [TestCase(2)]
        [TestCase(16)]
        public void TestTeams(int count)
        {
            int[] userIds = getPlayerIds(count);

            start(userIds, teams: true);
            loadSpectateScreen();

            sendFrames(userIds, 1000);
            AddWaitStep("wait a bit", 20);
        }

        [Test]
        public void TestMultipleStartRequests()
        {
            int[] userIds = getPlayerIds(2);

            start(userIds);
            loadSpectateScreen();

            sendFrames(userIds, 1000);
            AddWaitStep("wait a bit", 20);

            start(userIds);
        }

        [Test]
        public void TestDelayedStart()
        {
            AddStep("start players silently", () =>
            {
                OnlinePlayDependencies.MultiplayerClient.AddUser(new APIUser { Id = PLAYER_1_ID }, true);
                OnlinePlayDependencies.MultiplayerClient.AddUser(new APIUser { Id = PLAYER_2_ID }, true);

                playingUsers.Add(new MultiplayerRoomUser(PLAYER_1_ID));
                playingUsers.Add(new MultiplayerRoomUser(PLAYER_2_ID));
            });

            loadSpectateScreen(false);

            AddWaitStep("wait a bit", 10);
            AddStep("load player first_player_id", () => SpectatorClient.SendStartPlay(PLAYER_1_ID, importedBeatmapId));
            AddUntilStep("one player added", () => spectatorScreen.ChildrenOfType<Player>().Count() == 1);

            AddWaitStep("wait a bit", 10);
            AddStep("load player second_player_id", () => SpectatorClient.SendStartPlay(PLAYER_2_ID, importedBeatmapId));
            AddUntilStep("two players added", () => spectatorScreen.ChildrenOfType<Player>().Count() == 2);
        }

        [Test]
        public void TestSpectatorPlayerInteractiveElementsHidden()
        {
            HUDVisibilityMode originalConfigValue = default;

            AddStep("get original config hud visibility", () => originalConfigValue = config.Get<HUDVisibilityMode>(OsuSetting.HUDVisibilityMode));
            AddStep("set config hud visibility to always", () => config.SetValue(OsuSetting.HUDVisibilityMode, HUDVisibilityMode.Always));

            start(new[] { PLAYER_1_ID, PLAYER_2_ID });
            loadSpectateScreen(false);

            AddUntilStep("wait for player loaders", () => this.ChildrenOfType<PlayerLoader>().Count() == 2);
            AddAssert("all player loader settings hidden", () => this.ChildrenOfType<PlayerLoader>().All(l => !l.ChildrenOfType<FillFlowContainer<PlayerSettingsGroup>>().Any()));

            AddUntilStep("wait for players to load", () => spectatorScreen.AllPlayersLoaded);

            // components wrapped in skinnable target containers load asynchronously, potentially taking more than one frame to load.
            // therefore use until step rather than direct assert to account for that.
            AddUntilStep("all interactive elements removed", () => this.ChildrenOfType<Player>().All(p =>
                !p.ChildrenOfType<ReplaySettingsOverlay>().Any() &&
                !p.ChildrenOfType<HoldForMenuButton>().Any() &&
                p.ChildrenOfType<ArgonSongProgressBar>().SingleOrDefault()?.Interactive == false));

            AddStep("restore config hud visibility", () => config.SetValue(OsuSetting.HUDVisibilityMode, originalConfigValue));
        }

        [Test]
        public void TestTeamDisplay()
        {
            AddStep("start players", () =>
            {
                var player1 = OnlinePlayDependencies.MultiplayerClient.AddUser(new APIUser { Id = PLAYER_1_ID }, true);
                player1.MatchState = new TeamVersusUserState
                {
                    TeamID = 0,
                };

                var player2 = OnlinePlayDependencies.MultiplayerClient.AddUser(new APIUser { Id = PLAYER_2_ID }, true);
                player2.MatchState = new TeamVersusUserState
                {
                    TeamID = 1,
                };

                SpectatorClient.SendStartPlay(player1.UserID, importedBeatmapId);
                SpectatorClient.SendStartPlay(player2.UserID, importedBeatmapId);

                playingUsers.Add(player1);
                playingUsers.Add(player2);
            });

            loadSpectateScreen();

            sendFrames(PLAYER_1_ID, 1000);
            sendFrames(PLAYER_2_ID, 1000);

            AddWaitStep("wait a bit", 20);
        }

        [Test]
        public void TestPlayersMustStartSimultaneously()
        {
            start(new[] { PLAYER_1_ID, PLAYER_2_ID });
            loadSpectateScreen();

            // Send frames for one player only, both should remain paused.
            sendFrames(PLAYER_1_ID, 20);
            checkPausedInstant(PLAYER_1_ID);
            checkPausedInstant(PLAYER_2_ID);

            // Send frames for the other player, both should now start playing.
            sendFrames(PLAYER_2_ID, 20);
            checkRunningInstant(PLAYER_1_ID);
            checkRunningInstant(PLAYER_2_ID);
        }

        [Test]
        public void TestPlayersDoNotStartSimultaneouslyIfBufferingForMaximumStartDelay()
        {
            start(new[] { PLAYER_1_ID, PLAYER_2_ID });
            loadSpectateScreen();

            // Send frames for one player only, both should remain paused.
            sendFrames(PLAYER_1_ID, 1000);
            checkPausedInstant(PLAYER_1_ID);
            checkPausedInstant(PLAYER_2_ID);

            // Wait for the start delay seconds...
            AddWaitStep("wait maximum start delay seconds", (int)(SpectatorSyncManager.MAXIMUM_START_DELAY / TimePerAction));

            // Player 1 should start playing by itself, player 2 should remain paused.
            checkRunningInstant(PLAYER_1_ID);
            checkPausedInstant(PLAYER_2_ID);
        }

        [Test]
        public void TestMasterPacesBehindSlowestPlayer()
        {
            start(new[] { PLAYER_1_ID, PLAYER_2_ID });
            loadSpectateScreen();

            // Both players are fed, but player 2's buffer is much shorter than player 1's.
            sendFrames(PLAYER_1_ID, 40); // frames out to ~3900ms
            sendFrames(PLAYER_2_ID, 10); // frames out to ~900ms
            checkRunningInstant(PLAYER_1_ID);
            checkRunningInstant(PLAYER_2_ID);

            // The master rides LIVE_EDGE_BUFFER behind the slowest kept player rather than letting player 1 race
            // ahead, so it stops once it reaches player 2's short buffer. Both players are held together by this
            // pacing - neither is individually paused, so both still report as running.
            waitUntilMasterPaused();
            checkRunningInstant(PLAYER_1_ID);
            checkRunningInstant(PLAYER_2_ID);

            double pausedMasterTime = 0;
            AddStep("record master time", () => pausedMasterTime = masterClock().CurrentTime);
            AddWaitStep("wait a while", 10);
            AddAssert("master time did not advance", () => Math.Abs(masterClock().CurrentTime - pausedMasterTime) < 50);

            // Extending the slow player's buffer lifts the ceiling and the whole cast resumes together.
            sendFrames(PLAYER_2_ID, 40);
            waitUntilMasterRunning();
            checkRunningInstant(PLAYER_1_ID);
            checkRunningInstant(PLAYER_2_ID);
        }

        [Test]
        public void TestPlayersCatchUpAfterFallingBehind()
        {
            start(new[] { PLAYER_1_ID, PLAYER_2_ID });
            loadSpectateScreen();

            // Send initial frames for both players. A few more for player 1.
            sendFrames(PLAYER_1_ID, 1000);
            sendFrames(PLAYER_2_ID, 30);
            checkRunningInstant(PLAYER_1_ID);
            checkRunningInstant(PLAYER_2_ID);

            // Eventually player 2 will run out of frames and should pause.
            waitUntilPaused(PLAYER_2_ID);
            AddWaitStep("wait a few more frames", 10);

            // Send more frames for player 2. It should unpause.
            sendFrames(PLAYER_2_ID, 1000);
            checkRunningInstant(PLAYER_2_ID);

            // Player 2 should catch up to player 1 after unpausing.
            waitForCatchup(PLAYER_2_ID);
            AddWaitStep("wait a bit", 10);
        }

        [Test]
        public void TestPassedUserWaitsForLateFinalFrames()
        {
            start(PLAYER_1_ID);
            loadSpectateScreen();

            sendFrames(PLAYER_1_ID, 10); // frames out to ~900ms
            checkRunningInstant(PLAYER_1_ID);

            // SpectatorClient.EndPlaying sends the pass unconditionally while the player's final bundle can still be
            // sitting in the send queue, so a pass regularly arrives ahead of the frames it claims to follow.
            AddStep("pass with frames still outstanding", () => SpectatorClient.SendEndPlay(PLAYER_1_ID, SpectatedUserState.Passed));

            // The tile is no longer paced by the sync manager, but must still hold at the last frame it actually
            // received instead of playing the remainder of the map out on stale input.
            AddUntilStep("holds at last received frame", () => !getInstance(PLAYER_1_ID).SpectatorPlayerClock.IsRunning);
            AddAssert("did not run past received frames", () => getInstance(PLAYER_1_ID).SpectatorPlayerClock.CurrentTime < 2000);

            // The late bundle finally lands, and playback resumes rather than staying frozen as it did when a pass
            // stopped the clock outright.
            sendFrames(PLAYER_1_ID, 10);
            AddUntilStep("resumes on late frames", () => getInstance(PLAYER_1_ID).SpectatorPlayerClock.IsRunning);

            // The clock alone cannot tell the two ways out of the hold apart: giving up after final_frames_grace of
            // starvation resumes playback just as well, and well inside an until step's budget. Only the frames path
            // leaves the replay still expecting more, so that is what pins this test to the frames rather than to the
            // grace running out (which TestPassedUserGivesUpOnFinalFramesThatNeverArrive covers instead).
            AddAssert("resumed on the frames, not on the grace", () => !getPlayer(PLAYER_1_ID).Score.Replay.HasReceivedAllFrames);
        }

        [Test]
        public void TestPassedUserGivesUpOnFinalFramesThatNeverArrive()
        {
            start(PLAYER_1_ID);
            loadSpectateScreen();

            sendFrames(PLAYER_1_ID, 10); // frames out to ~900ms
            checkRunningInstant(PLAYER_1_ID);

            AddStep("pass with frames still outstanding", () => SpectatorClient.SendEndPlay(PLAYER_1_ID, SpectatedUserState.Passed));

            // Same starting point as TestPassedUserWaitsForLateFinalFrames, but this player's final bundle is simply
            // lost - a dropped connection, a server restart - and nothing further is ever sent.
            AddUntilStep("holds at last received frame", () => !getInstance(PLAYER_1_ID).SpectatorPlayerClock.IsRunning);
            AddAssert("still waiting on frames", () => !getPlayer(PLAYER_1_ID).Score.Replay.HasReceivedAllFrames);

            // The wait has to be bounded, or such a tile holds forever and its results screen never arrives - the very
            // freeze this whole mechanism exists to avoid. Once the starvation grace elapses the pass is taken at face
            // value and playback carries on.
            //
            // Deliberately no assertion on results here: every bundle's last frame carries a header, and
            // FramedReplayInputHandler re-emits it for as long as playback rests on that frame, which pins JudgedHits to
            // whatever statistics it holds. The default bundles sent above carry empty statistics, so the score can
            // never complete and the results screen can never be reached in this scenario.
            AddUntilStep("stops waiting on frames", () => getPlayer(PLAYER_1_ID).Score.Replay.HasReceivedAllFrames);
            AddUntilStep("playback resumes", () => getInstance(PLAYER_1_ID).SpectatorPlayerClock.IsRunning);
        }

        [Test]
        public void TestPassedUserReachesResults()
        {
            // Note that this test guards the original freeze only - it also passed under the superseded fix in
            // 3971fe4b5f, which marked all frames as received on the pass and force-ran the clock. The two tests above
            // are what pin the mechanism that replaced it.

            // The imported beatmap runs to ~206s, far too long to play out here. Cutting it down to its first couple of
            // objects (at 956ms and 1285ms) keeps MaxHits small enough for the score to complete within a second or so
            // of playback, which is what gates the results screen.
            AddStep("truncate spectated beatmap", () =>
            {
                // Resolved the same way SpectatorScreen does, by online id. Truncating the instance this fixture
                // imported is not enough: the realm can hold another copy of the same online beatmap (the visual test
                // browser runs against a persistent realm, unlike a headless run), and the spectator gets whichever
                // one the query returns.
                truncatedWorkingBeatmap = beatmapManager.GetWorkingBeatmap(beatmapManager.QueryBeatmap(b => b.OnlineID == importedBeatmapId), refetch: true);

                truncatedHitObjects = (List<HitObject>)truncatedWorkingBeatmap.Beatmap.HitObjects;
                removedHitObjects = truncatedHitObjects.Skip(truncated_hit_object_count).ToArray();
                truncatedHitObjects.RemoveRange(truncated_hit_object_count, removedHitObjects.Length);
            });

            start(PLAYER_1_ID);
            loadSpectateScreen();

            // Guards the setup above: if the spectator resolved a different copy of the beatmap, the truncation missed
            // and the results assertion below would otherwise just time out with no indication why.
            AddAssert("spectator loaded the truncated beatmap",
                () => getPlayer(PLAYER_1_ID).GameplayState.Beatmap.HitObjects.Count, () => Is.EqualTo(truncated_hit_object_count));

            // Frames out to ~900ms only. The master rides LIVE_EDGE_BUFFER behind that, so the tile parks at ~700ms,
            // short of the last object - exactly the position a real tile is in when a pass arrives, because the cast
            // always trails the live edge.
            sendFrames(PLAYER_1_ID, 10);
            checkRunningInstant(PLAYER_1_ID);

            AddStep("pass while short of last object", () => SpectatorClient.SendEndPlay(PLAYER_1_ID, SpectatedUserState.Passed));

            // The player's last frame, carrying the statistics they finished with, well past the final object. The tile
            // has to keep playing to reach it - freezing on the pass left the score incomplete and results unreachable.
            //
            // The statistics matter, and the reason is easy to trip over. FramedReplayInputHandler re-emits a
            // ReplayStatisticsFrameInput every frame for as long as the current replay frame carries a header, and
            // ResetFromReplayFrame recomputes JudgedHits from that header's statistics. A tile that has run dry rests
            // on the last frame it received, and every bundle's last frame carries a header - so whatever that header
            // says pins JudgedHits for good. With the empty statistics SendFramesFromUser produces by default it pins
            // it to zero, and HasCompleted (JudgedHits == MaxHits) can never fire no matter how much of the map plays
            // out. A real passed player's final header carries their full statistics, which is what is mirrored here.
            //
            // More than one frame, and landing at 2000ms/2100ms rather than in one lump, because a real replay's tail
            // is a run of frames: the score completes partway through it, when playback passes the last object, and
            // only then does the tile reach the last frame. A single frame far past the rest would leave the tile
            // resting on the previous bundle's header - empty statistics, JudgedHits pinned to zero - for the whole
            // gap, so the score could not complete until the moment playback starved, and the two would be
            // indistinguishable.
            AddStep("send final frames with completed statistics",
                () => SpectatorClient.SendFramesFromUser(PLAYER_1_ID, 2, startTime: 1000, initialResultCount: truncated_judgement_count));

            // The score completes partway through that tail, before playback reaches the last of those frames. Nothing
            // still outstanding can change it at that point, so the tile has to stop waiting there and then. Holding on
            // for the starvation grace instead froze every passed tile at its last frame for a full second - in solo
            // spectating, a second of dead air on the beatmap track.
            AddUntilStep("score completed", () => getPlayer(PLAYER_1_ID).ChildrenOfType<ScoreProcessor>().First().HasCompleted.Value);
            AddAssert("stopped waiting on frames", () => getPlayer(PLAYER_1_ID).Score.Replay.HasReceivedAllFrames);

            AddUntilStep("results screen shown", () => getInstance(PLAYER_1_ID).ChildrenOfType<MultiSpectatorResultsScreen>().Any());
        }

        [Test]
        [Explicit("Test relies on timing of arriving frames to exercise assertions which doesn't work headless.")]
        public void TestMaximisedUserIsAudioSource()
        {
            start(new[] { PLAYER_1_ID, PLAYER_2_ID });
            loadSpectateScreen();

            // With no frames, the synchronisation state will be TooFarAhead.
            // In this state, all players should be muted.
            assertMuted(PLAYER_1_ID, true);
            assertMuted(PLAYER_2_ID, true);

            // Send frames for both players.
            sendFrames(PLAYER_1_ID, 20);
            sendFrames(PLAYER_2_ID, 40);

            waitUntilRunning(PLAYER_1_ID);
            AddStep("maximise player 1", () =>
            {
                InputManager.MoveMouseTo(getInstance(PLAYER_1_ID));
                InputManager.Click(MouseButton.Left);
            });
            assertMuted(PLAYER_1_ID, false);
            assertMuted(PLAYER_2_ID, true);

            waitUntilPaused(PLAYER_1_ID);
            assertMuted(PLAYER_1_ID, false);
            assertMuted(PLAYER_2_ID, true);

            AddStep("minimise player 1", () =>
            {
                InputManager.MoveMouseTo(getInstance(PLAYER_1_ID));
                InputManager.Click(MouseButton.Left);
            });
            assertMuted(PLAYER_1_ID, true);
            assertMuted(PLAYER_2_ID, false);

            AddStep("maximise player 2", () =>
            {
                InputManager.MoveMouseTo(getInstance(PLAYER_2_ID));
                InputManager.Click(MouseButton.Left);
            });
            assertMuted(PLAYER_1_ID, true);
            assertMuted(PLAYER_2_ID, false);

            waitUntilPaused(PLAYER_2_ID);
            sendFrames(PLAYER_1_ID, 60);

            assertMuted(PLAYER_1_ID, true);
            assertMuted(PLAYER_2_ID, false);

            AddStep("minimise player 2", () =>
            {
                InputManager.MoveMouseTo(getInstance(PLAYER_2_ID));
                InputManager.Click(MouseButton.Left);
            });
            assertMuted(PLAYER_1_ID, false);
            assertMuted(PLAYER_2_ID, true);
        }

        [Test]
        [FlakyTest]
        public void TestMostInSyncUserIsAudioSourceIfNoneMaximised()
        {
            start(new[] { PLAYER_1_ID, PLAYER_2_ID });
            loadSpectateScreen();

            // With no frames, the synchronisation state will be TooFarAhead.
            // In this state, all players should be muted.
            assertMuted(PLAYER_1_ID, true);
            assertMuted(PLAYER_2_ID, true);

            // Feed both, but give player 1 only a tiny buffer while player 2 gets a large one, so player 1 soon
            // falls past the live-offset cap and is abandoned.
            sendFrames(PLAYER_1_ID, 5); // frames out to ~400ms
            sendFrames(PLAYER_2_ID, 400); // frames out to ~39900ms

            // While both players are running, one of them should be un-muted.
            waitUntilRunning(PLAYER_1_ID);
            assertOnePlayerNotMuted();

            // Player 1 runs out of its tiny buffer and is abandoned; the audio source must move to the still-running player 2.
            waitUntilPaused(PLAYER_1_ID);
            waitUntilRunning(PLAYER_2_ID);
            assertMuted(PLAYER_1_ID, true);
            assertMuted(PLAYER_2_ID, false);

            // ponytail: only covers the source moving off a dropped player. Under master pacing a well-fed player
            // never runs out, so the old "source swaps back and forth as players take turns starving" scenario can't
            // be set up deterministically; add it back if that path ever needs coverage.
        }

        [Test]
        public void TestSpectatingDuringGameplay()
        {
            int[] players = { PLAYER_1_ID, PLAYER_2_ID };

            start(players);
            sendFrames(players, 300);

            loadSpectateScreen();
            sendFrames(players, 300);

            AddUntilStep("playing from correct point in time", () => this.ChildrenOfType<DrawableRuleset>().All(r => r.FrameStableClock.CurrentTime > 30000));
        }

        [Test]
        public void TestSpectatingDuringGameplayWithLateFrames()
        {
            start(new[] { PLAYER_1_ID, PLAYER_2_ID });
            sendFrames(new[] { PLAYER_1_ID, PLAYER_2_ID }, 300);

            loadSpectateScreen();
            sendFrames(PLAYER_1_ID, 300);

            AddWaitStep("wait maximum start delay seconds", (int)(SpectatorSyncManager.MAXIMUM_START_DELAY / TimePerAction));
            waitUntilRunning(PLAYER_1_ID);

            sendFrames(PLAYER_2_ID, 300);
            AddUntilStep("player 2 playing from correct point in time", () => getPlayer(PLAYER_2_ID).ChildrenOfType<DrawableRuleset>().Single().FrameStableClock.CurrentTime > 30000);
        }

        [Test]
        public void TestGameplayRateAdjust()
        {
            start(getPlayerIds(4), mods: new[] { new APIMod(new OsuModDoubleTime()) });

            loadSpectateScreen();

            sendFrames(getPlayerIds(4), 300);

            AddUntilStep("wait for correct track speed",
                () => this.ChildrenOfType<MultiSpectatorPlayer>().All(player => player.ClockAdjustmentsFromMods.AggregateTempo.Value == 1.5));
        }

        [Test]
        public void TestPlayersLeaveWhileSpectating()
        {
            start(getPlayerIds(4));
            sendFrames(getPlayerIds(4), 300);

            loadSpectateScreen();

            for (int count = 3; count >= 0; count--)
            {
                int id = PLAYER_1_ID + count;

                end(id);
                AddUntilStep($"{id} area grayed", () => getInstance(id).Colour != Color4.White);
                AddUntilStep($"{id} score quit set", () => getLeaderboardScore(id).HasQuit.Value);
                sendFrames(getPlayerIds(count), 300);
            }

            Player? player = null;

            AddStep($"get {PLAYER_1_ID} player instance", () => player = getInstance(PLAYER_1_ID).ChildrenOfType<Player>().Single());

            start(new[] { PLAYER_1_ID });
            sendFrames(PLAYER_1_ID, 300);

            AddAssert($"{PLAYER_1_ID} player instance still same", () => getInstance(PLAYER_1_ID).ChildrenOfType<Player>().Single() == player);
            AddAssert($"{PLAYER_1_ID} area still grayed", () => getInstance(PLAYER_1_ID).Colour != Color4.White);
            AddAssert($"{PLAYER_1_ID} score quit still set", () => getLeaderboardScore(PLAYER_1_ID).HasQuit.Value);
        }

        /// <summary>
        /// Tests spectating with a beatmap that has a high <see cref="IBeatmap.AudioLeadIn"/> value.
        ///
        /// This test is not intended not to check the correct initial time value, but only to guard against
        /// gameplay potentially getting stuck in a stopped state due to lead in time being present.
        /// </summary>
        [Test]
        public void TestAudioLeadIn() => testLeadIn(b => b.Beatmap.AudioLeadIn = 2000);

        /// <summary>
        /// Tests spectating with a beatmap that has a storyboard element with a negative start time (i.e. intro storyboard element).
        ///
        /// This test is not intended not to check the correct initial time value, but only to guard against
        /// gameplay potentially getting stuck in a stopped state due to lead in time being present.
        /// </summary>
        [Test]
        public void TestIntroStoryboardElement() => testLeadIn(b =>
        {
            var sprite = new StoryboardSprite(StoryboardElementSource.Beatmap, "unknown", Anchor.TopLeft, Vector2.Zero);
            sprite.Commands.AddAlpha(Easing.None, -2000, 0, 0, 1);
            b.Storyboard.GetLayer("Background").Add(sprite);
        });

        [Test]
        public void TestFRankDisplay()
        {
            int[] userIds = getPlayerIds(1);

            start(userIds);
            loadSpectateScreen();

            sendFrames(userIds, 1000);
            AddUntilStep("player has F rank", () => this.ChildrenOfType<MultiSpectatorPlayer>().All(msp => msp.GameplayState.ScoreProcessor.Rank.Value == ScoreRank.F));
        }

        private void testLeadIn(Action<WorkingBeatmap>? applyToBeatmap = null)
        {
            start(PLAYER_1_ID);

            loadSpectateScreen(false, applyToBeatmap);

            // to ensure negative gameplay start time does not affect spectator, send frames exactly after StartGameplay().
            // (similar to real spectating sessions in which the first frames get sent between StartGameplay() and player load complete)
            AddStep("send frames at gameplay start", () => getInstance(PLAYER_1_ID).OnGameplayStarted += () => SpectatorClient.SendFramesFromUser(PLAYER_1_ID, 100));

            AddUntilStep("wait for player load", () => spectatorScreen.AllPlayersLoaded);

            AddUntilStep("wait for clock running", () => getInstance(PLAYER_1_ID).SpectatorPlayerClock.IsRunning);

            assertNotCatchingUp(PLAYER_1_ID);
            waitUntilRunning(PLAYER_1_ID);
        }

        private void loadSpectateScreen(bool waitForPlayerLoad = true, Action<WorkingBeatmap>? applyToBeatmap = null)
        {
            AddStep("load screen", () =>
            {
                Beatmap.Value = beatmapManager.GetWorkingBeatmap(importedBeatmap);
                Ruleset.Value = importedBeatmap.Ruleset;

                applyToBeatmap?.Invoke(Beatmap.Value);

                LoadScreen(spectatorScreen = new MultiSpectatorScreen(room, playingUsers.ToArray()));
            });

            AddUntilStep("wait for screen load", () => spectatorScreen.LoadState == LoadState.Loaded && (!waitForPlayerLoad || spectatorScreen.AllPlayersLoaded));
        }

        private void start(int userId, int? beatmapId = null) => start(new[] { userId }, beatmapId);

        private void start(int[] userIds, int? beatmapId = null, APIMod[]? mods = null, bool teams = false)
        {
            AddStep("start play", () =>
            {
                for (int i = 0; i < userIds.Length; i++)
                {
                    int id = userIds[i];
                    var user = new MultiplayerRoomUser(id)
                    {
                        User = new APIUser { Id = id },
                        Mods = mods ?? Array.Empty<APIMod>(),
                        MatchState = teams ? new TeamVersusUserState { TeamID = i % 2 } : null,
                    };

                    OnlinePlayDependencies.MultiplayerClient.AddUser(user, true);
                    SpectatorClient.SendStartPlay(id, beatmapId ?? importedBeatmapId, mods);

                    playingUsers.Add(user);
                }
            });
        }

        private void end(int userId)
        {
            AddStep($"end play for {userId}", () =>
            {
                var user = playingUsers.Single(u => u.UserID == userId);

                SpectatorClient.SendEndPlay(userId);
                OnlinePlayDependencies.MultiplayerClient.RemoveUser(user.User.AsNonNull());

                playingUsers.Remove(user);
            });
        }

        /// <summary>
        /// Send new frames on behalf of a user.
        /// Frames will last for count * 100 milliseconds.
        /// </summary>
        private void sendFrames(int userId, int count = 10) => sendFrames(new[] { userId }, count);

        private void sendFrames(int[] userIds, int count = 10)
        {
            AddStep("send frames", () =>
            {
                foreach (int id in userIds)
                    SpectatorClient.SendFramesFromUser(id, count);
            });
        }

        private void checkRunningInstant(int userId)
        {
            waitUntilRunning(userId);

            // Todo: The following should work, but is broken because SpectatorScreen retrieves the WorkingBeatmap via the BeatmapManager, bypassing the test scene clock and running real-time.
            // AddAssert($"{userId} is {(state ? "paused" : "playing")}", () => getPlayer(userId).ChildrenOfType<GameplayClockContainer>().First().GameplayClock.IsRunning != state);
        }

        private void checkPausedInstant(int userId)
        {
            waitUntilPaused(userId);

            // Todo: The following should work, but is broken because SpectatorScreen retrieves the WorkingBeatmap via the BeatmapManager, bypassing the test scene clock and running real-time.
            // AddAssert($"{userId} is {(state ? "paused" : "playing")}", () => getPlayer(userId).ChildrenOfType<GameplayClockContainer>().First().GameplayClock.IsRunning != state);
        }

        private void assertOnePlayerNotMuted() => AddAssert(nameof(assertOnePlayerNotMuted), () => spectatorScreen.ChildrenOfType<PlayerArea>().Count(p => !p.Mute) == 1);

        private void assertMuted(int userId, bool muted)
            => AddAssert($"{nameof(assertMuted)}({userId}, {muted})", () => getInstance(userId).Mute == muted);

        private void assertRunning(int userId)
            => AddAssert($"{nameof(assertRunning)}({userId})", () => getInstance(userId).SpectatorPlayerClock.IsRunning);

        private void waitUntilPaused(int userId)
            => AddUntilStep($"{nameof(waitUntilPaused)}({userId})", () => !getPlayer(userId).ChildrenOfType<GameplayClockContainer>().First().IsRunning);

        private void waitUntilRunning(int userId)
            => AddUntilStep($"{nameof(waitUntilRunning)}({userId})", () => getPlayer(userId).ChildrenOfType<GameplayClockContainer>().First().IsRunning);

        private void waitUntilMasterPaused() => waitUntilMasterRunning(false);

        private void waitUntilMasterRunning(bool running = true)
            => AddUntilStep($"wait for master {(running ? "running" : "paused")}", () => masterClock().IsRunning == running);

        private MasterGameplayClockContainer masterClock() => this.ChildrenOfType<MasterGameplayClockContainer>().Single();

        private void assertNotCatchingUp(int userId)
            => AddAssert($"{nameof(assertNotCatchingUp)}({userId})", () => !getInstance(userId).SpectatorPlayerClock.IsCatchingUp);

        private void waitForCatchup(int userId)
            => AddUntilStep($"{nameof(waitForCatchup)}({userId})", () => !getInstance(userId).SpectatorPlayerClock.IsCatchingUp);

        private Player getPlayer(int userId) => getInstance(userId).ChildrenOfType<Player>().Single();

        private PlayerArea getInstance(int userId) => spectatorScreen.ChildrenOfType<PlayerArea>().Single(p => p.UserId == userId);

        private DrawableGameplayLeaderboardScore getLeaderboardScore(int userId) => spectatorScreen.Leaderboard.ChildrenOfType<DrawableGameplayLeaderboardScore>().Single(s => s.User?.OnlineID == userId);

        private int[] getPlayerIds(int count) => Enumerable.Range(PLAYER_1_ID, count).ToArray();
    }
}
