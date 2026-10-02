// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Graphics;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Screens.Ranking;
using FontWeight = osu.Game.Graphics.FontWeight;
using OsuFont = osu.Game.Graphics.OsuFont;

namespace osu.Game.Screens.OnlinePlay.Multiplayer.Spectate
{
    /// <summary>
    /// A single spectated player within a <see cref="MultiSpectatorScreen"/>.
    /// </summary>
    public partial class MultiSpectatorPlayer : SpectatorPlayer
    {
        /// <summary>
        /// All adjustments applied to the clock of this <see cref="MultiSpectatorPlayer"/> which come from mods.
        /// </summary>
        public IAggregateAudioAdjustment ClockAdjustmentsFromMods => clockAdjustmentsFromMods;

        private readonly AudioAdjustments clockAdjustmentsFromMods = new AudioAdjustments();
        private readonly SpectatorPlayerClock spectatorPlayerClock;
        private readonly bool showPlayerName;
        private readonly bool allowFail;

        // purposefully cached as empty - the multi spectator screen already has one leaderboard, on the left of all the player instances
        [Cached(typeof(IGameplayLeaderboardProvider))]
        private readonly EmptyGameplayLeaderboardProvider leaderboardProvider = new EmptyGameplayLeaderboardProvider();

        /// <summary>
        /// Creates a new <see cref="MultiSpectatorPlayer"/>.
        /// </summary>
        /// <param name="score">The score containing the player's replay.</param>
        /// <param name="spectatorPlayerClock">The clock controlling the gameplay running state.</param>
        /// <param name="showFailingLayer">Whether the low-health red failing overlay should be shown for this player.</param>
        /// <param name="showPlayerName">Whether the player's username should be displayed above their gameplay.</param>
        /// <param name="allowFail">Whether the score should be marked as failed (F rank) when the locally-tracked health drains to zero.</param>
        public MultiSpectatorPlayer(Score score, SpectatorPlayerClock spectatorPlayerClock, bool showFailingLayer = true, bool showPlayerName = true, bool allowFail = true)
            : base(score, new PlayerConfiguration { AllowUserInteraction = false, ShowFailingOverlay = showFailingLayer })
        {
            this.spectatorPlayerClock = spectatorPlayerClock;
            this.showPlayerName = showPlayerName;
            this.allowFail = allowFail;

            ShowSettingsOverlay = false;
        }

        [BackgroundDependencyLoader]
        private void load(CancellationToken cancellationToken)
        {
            // HUD overlay may not be loaded if load has been cancelled early.
            if (cancellationToken.IsCancellationRequested)
                return;

            if (!LoadedBeatmapSuccessfully)
                return;

            // also applied in `MultiplayerPlayer.load()`
            ScoreProcessor.ApplyNewJudgementsWhenFailed = true;

            HUDOverlay.HoldToQuit.Expire();

            // Player username display
            if (showPlayerName)
            {
                GameplayClockContainer.Add(new OsuTextFlowContainer(cp => cp.Font = OsuFont.Style.Title.With(size: 60, weight: FontWeight.SemiBold))
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    AutoSizeAxes = Axes.Both,
                    Text = Score.ScoreInfo.User.Username,
                    Y = 50,
                });
            }
        }

        protected override void Update()
        {
            // The player clock's running state is controlled externally, but the local pausing state needs to be updated to start/stop gameplay.
            if (GameplayClockContainer.IsRunning)
                GameplayClockContainer.Start();
            else
                GameplayClockContainer.Stop();

            base.Update();
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();

            // This is required because the frame stable clock is set to WaitingOnFrames = false for one frame.
            spectatorPlayerClock.WaitingOnFrames = DrawableRuleset.FrameStableClock.WaitingOnFrames.Value || Score.Replay.Frames.Count == 0;

            // Live edge for master pacing: the time of the most-recent received frame, or a "maximally behind"
            // sentinel when no frames have arrived yet.
            spectatorPlayerClock.LatestFrameTime = Score.Replay.Frames.Count > 0 ? Score.Replay.Frames[^1].Time : double.NegativeInfinity;
        }

        protected override GameplayClockContainer CreateGameplayClockContainer(WorkingBeatmap beatmap, double gameplayStart)
        {
            // Importantly, we don't want to apply decoupling because SpectatorPlayerClock updates its IsRunning directly.
            // If we applied decoupling, this state change wouldn't actually cause the clock to stop.
            // TODO: Can we just use Start/Stop rather than this workaround, now that DecouplingClock is more sane?
            var gameplayClockContainer = new GameplayClockContainer(spectatorPlayerClock, applyOffsets: false, requireDecoupling: false);
            clockAdjustmentsFromMods.BindAdjustments(gameplayClockContainer.AdjustmentsFromMods);
            return gameplayClockContainer;
        }

        protected override ResultsScreen CreateResults(ScoreInfo score) => new MultiSpectatorResultsScreen(score);

        // Health is not transmitted with spectator frames, so the locally-tracked health can drain to zero while the user is passing
        // (e.g. when spectating began mid-map and the objects before it were judged as misses).
        protected override bool CheckModsAllowFailure() => allowFail && base.CheckModsAllowFailure();

        protected override void PerformFail()
        {
            // base logic intentionally suppressed - failing in multiplayer only marks the score with F rank
            // see also: `MultiplayerPlayer.PerformFail()`
            ScoreProcessor.FailScore(Score.ScoreInfo);
        }

        protected override void ConcludeFailedScore(Score score)
            => throw new NotSupportedException($"{nameof(MultiSpectatorPlayer)} should never be calling {nameof(ConcludeFailedScore)}. Failing in multiplayer only marks the score with F rank.");
    }
}
