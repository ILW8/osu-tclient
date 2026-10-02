// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Screens;
using osu.Game.Screens.OnlinePlay.Multiplayer.Spectate;
using osu.Game.Tests.Visual.Multiplayer;
using osu.Game.Tests.Visual.OnlinePlay;
using osu.Game.Tests.Visual.Spectator;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;
using osuTK;

namespace osu.Game.Tournament.Tests.Components
{
    /// <summary>
    /// The panic rebuild as done by GameplayScreen.onPanic and MultiplayerMatchIPCInfo.Panic: the tile screen's
    /// <see cref="OsuScreenStack"/> is torn down, a new one is pushed, and every spectator watch is restarted.
    /// </summary>
    public partial class TestSceneTournamentSpectatorPanic : MultiplayerTestScene
    {
        private const int beatmap_online_id = 424244;

        [Cached]
        private readonly LadderInfo ladder = new LadderInfo();

        [Cached]
        private readonly MatchIPCInfo ipc = new MatchIPCInfo();

        [Resolved]
        private BeatmapManager beatmapManager { get; set; } = null!;

        [Resolved]
        private RealmAccess realm { get; set; } = null!;

        private DeferredResendSpectatorClient client => (DeferredResendSpectatorClient)SpectatorClient;

        private Container gameplayHost = null!;
        private SlowDisposingScreenStack oldStack = null!;
        private TournamentSpectatorScreen oldScreen = null!;
        private TournamentSpectatorScreen newScreen = null!;

        private bool beatmapImported;

        public override void SetUpSteps()
        {
            base.SetUpSteps();

            AddStep("import beatmap", importBeatmap);

            AddStep("join room", () => JoinRoom(CreateDefaultRoom()));
            WaitForJoined();

            AddStep("start play", () =>
            {
                MultiplayerClient.AddUser(new APIUser { Id = PLAYER_1_ID }, true);
                SpectatorClient.SendStartPlay(PLAYER_1_ID, beatmap_online_id);
            });

            AddStep("show tiles", () =>
            {
                Child = gameplayHost = new Container { RelativeSizeAxes = Axes.Both };
                pushScreen(oldStack = new SlowDisposingScreenStack(), oldScreen = new TournamentSpectatorScreen(new[] { PLAYER_1_ID }));
            });
            AddUntilStep("wait for tile", () => oldScreen.ChildrenOfType<PlayerArea>().SingleOrDefault()?.PlayerLoaded == true);
            AddStep("send frames", () => SpectatorClient.SendFramesFromUser(PLAYER_1_ID, 50));
        }

        /// <summary>
        /// A torn-down screen with tiles takes a while to dispose asynchronously, so the new screen watches its users first.
        /// The old screen's release of its watch must then leave the new screen's watch in place, or the server's resend of
        /// the user's state (a round trip after the restart) is dropped and the tiles never come back.
        /// </summary>
        [Test]
        public void TestPanicWithOldScreenDisposedAfterNewScreenWatches()
        {
            int watchCallsBeforePanic = 0;

            AddStep("panic", () =>
            {
                client.HoldResends = true;
                watchCallsBeforePanic = client.WatchUserCalls;

                gameplayHost.Clear(true);
                pushScreen(new OsuScreenStack(), newScreen = new TournamentSpectatorScreen(new[] { PLAYER_1_ID }));

                client.RestartWatching();
            });

            AddUntilStep("new screen watches", () => client.WatchUserCalls > watchCallsBeforePanic);

            AddStep("let old screen dispose", () => oldStack.AllowDisposal.Set());
            AddUntilStep("old screen disposed", () => oldStack.DisposalCompleted);
            AddWaitStep("wait for deferred unwatches", 5);

            AddStep("server resends state", () => client.ReleaseResends());
            AddUntilStep("player still watched", () => client.WatchedUserStates.ContainsKey(PLAYER_1_ID));
            AddUntilStep("new screen has tile", () => newScreen.ChildrenOfType<PlayerArea>().Any());
        }

        private void pushScreen(OsuScreenStack stack, TournamentSpectatorScreen screen)
        {
            stack.RelativeSizeAxes = Axes.Both;
            gameplayHost.Add(stack);
            stack.Push(screen);
        }

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

        protected override OnlinePlayTestSceneDependencies CreateOnlinePlayDependencies() => new DeferredResendDependencies();

        private class DeferredResendDependencies : MultiplayerTestSceneDependencies
        {
            protected override TestSpectatorClient CreateSpectatorClient() => new DeferredResendSpectatorClient();
        }

        /// <summary>
        /// Holds back its async disposal until <see cref="AllowDisposal"/> is set, like a stack whose tiles take a while to dispose.
        /// </summary>
        private partial class SlowDisposingScreenStack : OsuScreenStack
        {
            public readonly ManualResetEventSlim AllowDisposal = new ManualResetEventSlim();

            public volatile bool DisposalCompleted;

            protected override void Dispose(bool isDisposing)
            {
                AllowDisposal.Wait(TimeSpan.FromSeconds(10));
                base.Dispose(isDisposing);
                DisposalCompleted = true;
            }
        }

        /// <summary>
        /// Holds back the server's resend of a user's state on a new watch, which a real server sends a round trip later.
        /// </summary>
        private partial class DeferredResendSpectatorClient : TestSpectatorClient
        {
            public bool HoldResends { get; set; }

            public int WatchUserCalls { get; private set; }

            private readonly List<int> heldResends = new List<int>();

            public override void WatchUser(int userId)
            {
                WatchUserCalls++;
                base.WatchUser(userId);
            }

            protected override Task WatchUserInternal(int userId)
            {
                if (!HoldResends)
                    return base.WatchUserInternal(userId);

                heldResends.Add(userId);
                return Task.CompletedTask;
            }

            public void ReleaseResends()
            {
                HoldResends = false;

                foreach (int userId in heldResends)
                    base.WatchUserInternal(userId);

                heldResends.Clear();
            }
        }
    }
}
