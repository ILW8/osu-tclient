// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Screens;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Rooms;
using osu.Game.Online.Spectator;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Spectate;
using osu.Game.TournamentIpc;
using osu.Game.Users;
using osuTK;

namespace osu.Game.Screens.OnlinePlay.Multiplayer.Spectate
{
    /// <summary>
    /// A <see cref="SpectatorScreen"/> that spectates multiple users in a match.
    /// </summary>
    public partial class MultiSpectatorScreen : SpectatorScreen
    {
        // Isolates beatmap/ruleset to this screen.
        public override bool DisallowExternalBeatmapRulesetChanges => true;

        // We are managing our own adjustments. For now, this happens inside the Player instances themselves.
        public override bool? ApplyModTrackAdjustments => false;

        public override bool HideOverlaysOnEnter => true;

        /// <summary>
        /// Whether all spectating players have finished loading.
        /// </summary>
        public bool AllPlayersLoaded => instances.All(p => p.PlayerLoaded);

        /// <summary>
        /// Whether all spectating players are showing results.
        /// </summary>
        public bool AllPlayersInResults => instances.Where(p => p.PlayerLoaded && !p.HasQuit).All(p => p.InResultScreen);

        protected override UserActivity InitialActivity => new UserActivity.SpectatingMultiplayerGame(Beatmap.Value.BeatmapInfo, Ruleset.Value);

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        [Resolved]
        private OsuConfigManager configManager { get; set; } = null!;

        [Resolved]
        private MultiplayerClient multiplayerClient { get; set; } = null!;

        private IAggregateAudioAdjustment? boundAdjustments;

        private readonly PlayerArea[] instances;
        private MasterGameplayClockContainer masterClockContainer = null!;
        private FillFlowContainer leaderboardFlow = null!; // now used to load invisible chat component
        private SpectatorSyncManager syncManager = null!;
        private PlayerSettingsOverlay settingsOverlay = null!;
        private PlayerGrid grid = null!;
        private TournamentSpectatorStatisticsTracker statisticsTracker = null!;
        private PlayerArea? currentAudioSource;

        private Bindable<bool> showSettingsOverlay = null!;

        private readonly Room room;
        private readonly MultiplayerRoomUser[] users;

        private static MultiplayerRoomUser[] sortUsersByTeam(MultiplayerRoomUser[] users)
        {
            // check if users have team info, otherwise leave unchanged
            if ((users.FirstOrDefault()?.MatchState as TeamVersusUserState)?.TeamID == null)
                return users;

            return users.OrderBy(u => (u.MatchState as TeamVersusUserState)!.TeamID).ToArray();
        }

        /// <summary>
        /// Creates a new <see cref="MultiSpectatorScreen"/>.
        /// </summary>
        /// <param name="room">The room.</param>
        /// <param name="users">The players to spectate.</param>
        public MultiSpectatorScreen(Room room, MultiplayerRoomUser[] users)
            : base(sortUsersByTeam(users).Select(u => u.UserID).ToArray())
        {
            this.room = room;
            this.users = sortUsersByTeam(users);

            instances = new PlayerArea[UserIds.Count];
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // FillFlowContainer leaderboardFlow;
            // Container scoreDisplayContainer;

            InternalChildren = new Drawable[]
            {
                masterClockContainer = new MasterGameplayClockContainer(Beatmap.Value, 0)
                {
                    Child = new GridContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        // RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize) },
                        Content = new[]
                        {
                            // new Drawable[]
                            // {
                            //     scoreDisplayContainer = new Container
                            //     {
                            //         RelativeSizeAxes = Axes.X,
                            //         AutoSizeAxes = Axes.Y
                            //     },
                            // },
                            new Drawable[]
                            {
                                new GridContainer
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    ColumnDimensions = new[] { new Dimension(GridSizeMode.AutoSize) },
                                    Content = new[]
                                    {
                                        new Drawable[]
                                        {
                                            leaderboardFlow = new FillFlowContainer
                                            {
                                                Anchor = Anchor.CentreLeft,
                                                Origin = Anchor.CentreLeft,
                                                AutoSizeAxes = Axes.Both,
                                                Direction = FillDirection.Vertical,
                                                Spacing = new Vector2(5)
                                            },
                                            grid = new PlayerGrid { RelativeSizeAxes = Axes.Both }
                                        }
                                    }
                                }
                            }
                        }
                    }
                },
                syncManager = new SpectatorSyncManager(masterClockContainer)
                {
                    ReadyToStart = () =>
                    {
                        performInitialSeek();
                        setSettingsVisibility(showSettingsOverlay.Value);
                    },
                },
                settingsOverlay = new PlayerSettingsOverlay()
            };

            for (int i = 0; i < Math.Min(PlayerGrid.MAX_PLAYERS, UserIds.Count); i++)
                grid.Add(instances[i] = new PlayerArea(UserIds[i], syncManager.CreateManagedClock()));

            LoadComponentAsync(statisticsTracker = new TournamentSpectatorStatisticsTracker(users), _ =>
            {
                foreach (var instance in instances)
                    statisticsTracker.AddClock(instance.UserId, instance.SpectatorPlayerClock);

                AddInternal(statisticsTracker);
            });

            // LoadComponentAsync(leaderboard = new MultiSpectatorLeaderboard(users)
            // {
            //     Expanded = { Value = true },
            // }, _ =>
            // {
            //     foreach (var instance in instances)
            //         leaderboard.AddClock(instance.UserId, instance.SpectatorPlayerClock);
            //
            //     leaderboardFlow.Insert(0, leaderboard);
            //
            //     if (leaderboard.TeamScores.Count == 2)
            //     {
            //         LoadComponentAsync(new MatchScoreDisplay
            //         {
            //             Team1Score = { BindTarget = leaderboard.TeamScores.First().Value },
            //             Team2Score = { BindTarget = leaderboard.TeamScores.Last().Value },
            //         }, scoreDisplayContainer.Add);
            //     }
            // });

            LoadComponentAsync(new GameplayChatDisplay(room)
            {
                Expanded = { Value = true },
                Alpha = 0
            }, chat => leaderboardFlow.Insert(0, chat));

            multiplayerClient.ResultsReady += onResultsReady;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            BackButtonVisibility.Value = false;

            showSettingsOverlay = configManager.GetBindable<bool>(OsuSetting.ReplaySettingsOverlay);
            showSettingsOverlay.BindValueChanged(vce => setSettingsVisibility(vce.NewValue));

            masterClockContainer.Reset();

            // Start with adjustments from the first player to keep a sane state.
            bindAudioAdjustments(instances.First());
        }

        protected override void Dispose(bool isDisposing)
        {
            multiplayerClient.ResultsReady -= onResultsReady;

            base.Dispose(isDisposing);
        }

        private void setSettingsVisibility(bool visible)
        {
            if (visible)
                settingsOverlay.Show();
            else
                settingsOverlay.Hide();
        }

        protected override void Update()
        {
            base.Update();

            if (!isCandidateAudioSource(currentAudioSource?.SpectatorPlayerClock))
            {
                currentAudioSource = instances.Where(i => isCandidateAudioSource(i.SpectatorPlayerClock)).MinBy(i => Math.Abs(i.SpectatorPlayerClock.CurrentTime - syncManager.CurrentMasterTime));

                // Only bind adjustments if there's actually a valid source, else just use the previous ones to ensure no sudden changes to audio.
                if (currentAudioSource != null)
                    bindAudioAdjustments(currentAudioSource);

                foreach (var instance in instances)
                    instance.Mute = instance != currentAudioSource;
            }
        }

        private void bindAudioAdjustments(PlayerArea first)
        {
            if (boundAdjustments != null)
                masterClockContainer.AdjustmentsFromMods.UnbindAdjustments(boundAdjustments);

            boundAdjustments = first.ClockAdjustmentsFromMods;
            masterClockContainer.AdjustmentsFromMods.BindAdjustments(boundAdjustments);
        }

        private bool isCandidateAudioSource(SpectatorPlayerClock? clock)
            => clock?.IsRunning == true && !clock.IsCatchingUp && !clock.WaitingOnFrames;

        private void performInitialSeek()
        {
            // We want to start showing gameplay as soon as possible.
            // Each client may be in a different place in the beatmap, so we need to do our best to find a common
            // starting point.
            //
            // Preferring a lower value ensures that we don't have some clients stuttering to keep up.
            List<double> minFrameTimes = new List<double>();

            foreach (var instance in instances)
            {
                if (instance.Score == null)
                    continue;

                minFrameTimes.Add(instance.Score.Replay.Frames.MinBy(f => f.Time)?.Time ?? 0);
            }

            // Remove any outliers (only need to worry about removing those lower than the mean since we will take a Min() after).
            double mean = minFrameTimes.Average();
            minFrameTimes.RemoveAll(t => mean - t > 1000);

            double startTime = minFrameTimes.Min();

            masterClockContainer.Reset(startTime, true);
            Logger.Log($"Multiplayer spectator seeking to initial time of {startTime}");
        }

        protected override void OnNewPlayingUserState(int userId, SpectatorState spectatorState)
        {
        }

        private void onResultsReady()
        {
            if (multiplayerClient.LocalUser?.State != MultiplayerUserState.Spectating)
                return;

            if (!AllPlayersInResults)
            {
                Scheduler.AddDelayed(onResultsReady, 200);
                return;
            }

            // add conditional to wait for spectator players to all finish playing first
            Scheduler.AddDelayed(() =>
            {
                if (!this.IsCurrentScreen()) return;

                this.Exit();
            }, 20_000);
        }

        protected override void StartGameplay(int userId, SpectatorGameplayState spectatorGameplayState) => Schedule(() =>
        {
            var playerArea = instances.Single(i => i.UserId == userId);

            // The multiplayer spectator flow requires the client to return to a higher level screen
            // (ie. StartGameplay should only be called once per player).
            //
            // Meanwhile, the solo spectator flow supports multiple `StartGameplay` calls.
            // To ensure we don't crash out in an edge case where this is called more than once in multiplayer,
            // guard against re-entry for the same player.
            if (playerArea.Score != null)
                return;

            playerArea.LoadScore(spectatorGameplayState.Score);
        });

        protected override void FailGameplay(int userId) => Schedule(() =>
        {
            // We probably want to visualise this in the future.

            var instance = instances.Single(i => i.UserId == userId);
            syncManager.RemoveManagedClock(instance.SpectatorPlayerClock);
        });

        protected override void PassGameplay(int userId) => Schedule(() =>
        {
            var instance = instances.Single(i => i.UserId == userId);
            syncManager.RemoveManagedClock(instance.SpectatorPlayerClock);
        });

        protected override void QuitGameplay(int userId) => Schedule(() =>
        {
            RemoveUser(userId);

            var instance = instances.Single(i => i.UserId == userId);

            instance.FadeColour(colours.Gray4, 400, Easing.OutQuint);
            instance.HasQuit = true;
            syncManager.RemoveManagedClock(instance.SpectatorPlayerClock);
        });

        public override bool ShowBackButton => false;

        public override bool CursorVisible => false;

        public override bool OnBackButton()
        {
            if (multiplayerClient.Room == null)
                return base.OnBackButton();

            // On a manual exit, set the player back to idle unless gameplay has finished.
            // Of note, this doesn't cover exiting using alt-f4 or menu home option.
            if (multiplayerClient.Room.State != MultiplayerRoomState.Open)
                multiplayerClient.ChangeState(MultiplayerUserState.Idle);

            return base.OnBackButton();
        }
    }
}
