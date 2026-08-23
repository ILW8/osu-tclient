// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Online.Spectator;
using osu.Game.Replays;
using osu.Game.Rulesets.Replays;
using osu.Game.Rulesets.Replays.Types;
using osu.Game.Scoring;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Ranking;

namespace osu.Game.Screens.Play
{
    public abstract partial class SpectatorPlayer : Player
    {
        [Resolved]
        protected SpectatorClient SpectatorClient { get; private set; } = null!;

        private readonly Score score;

        private const double final_frame_grace_ms = 1000;

        private double? starvedSince;

        public bool ShowSettingsOverlay { get; init; } = true;

        protected SpectatorPlayer(Score score, PlayerConfiguration? configuration = null)
            : base(configuration)
        {
            this.score = score;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            if (ShowSettingsOverlay && GameplayClockContainer != null)
            {
                var replayOverlay = new ReplayOverlay();
                GameplayClockContainer.Add(replayOverlay);

                // TODO: This should be customised for `MultiplayerSpectatorPlayer` to be static and only show the player name.
                // Or maybe we should completely redesign this to show the user avatar and other things if that happens.
                OsuTextFlowContainer message = new OsuTextFlowContainer(cp => cp.Font = OsuFont.Style.Body) { AutoSizeAxes = Axes.Both };
                message.AddText("Watching ");
                message.AddText(Score.ScoreInfo.User.Username, s => s.Font = s.Font.With(weight: FontWeight.SemiBold));
                message.AddText(" play ");
                message.AddText(Beatmap.Value.BeatmapInfo.GetDisplayTitleRomanisable(), s => s.Font = s.Font.With(weight: FontWeight.SemiBold));
                message.AddText(" live", s => s.Font = s.Font.With(weight: FontWeight.Bold));

                replayOverlay.SetMessage(new ScrollingMessage(message)
                {
                    Y = 96,
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                });
            }
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            DrawableRuleset.FrameStableClock.WaitingOnFrames.BindValueChanged(waiting =>
            {
                if (GameplayClockContainer is MasterGameplayClockContainer master)
                {
                    if (master.UserPlaybackRate.Value > 1 && waiting.NewValue)
                        master.UserPlaybackRate.Value = 1;
                }
            }, true);
        }

        protected override void Update()
        {
            base.Update();

            updateReplayCompletion();
        }

        /// <summary>
        /// Updates whether all frames for this replay have been received.
        /// </summary>
        /// <remarks>
        /// <see cref="SpectatedUserState.Passed"/> can arrive before the player's final frames, so we don't set
        /// <see cref="Replay.HasReceivedAllFrames"/> based on Passed alone. Instead, we set it once the score processor
        /// has completed, or once a passed player's playback has run dry for longer than
        /// <see cref="final_frame_grace_ms"/> (frames arriving reset that grace). Otherwise, playback stalls
        /// indefinitely at the last received frame and leaves the score incomplete, preventing the results screen
        /// from showing.
        /// </remarks>
        private void updateReplayCompletion()
        {
            if (score.Replay.HasReceivedAllFrames
                || !LoadedBeatmapSuccessfully
                || !SpectatorClient.WatchedUserStates.TryGetValue(score.ScoreInfo.User.OnlineID, out var userState))
                return;

            if (ScoreProcessor.HasCompleted.Value)
            {
                score.Replay.HasReceivedAllFrames = true;
                return;
            }

            if (!DrawableRuleset.FrameStableClock.WaitingOnFrames.Value || userState.State != SpectatedUserState.Passed)
            {
                starvedSince = null;
                return;
            }

            starvedSince ??= Time.Current;

            if (Time.Current - starvedSince >= final_frame_grace_ms)
                score.Replay.HasReceivedAllFrames = true;
        }

        protected override void StartGameplay()
        {
            base.StartGameplay();

            // Start gameplay along with the very first arrival frame (the latest one).
            score.Replay.Frames.Clear();
            SpectatorClient.OnNewFrames += userSentFrames;
        }

        private void userSentFrames(int userId, FrameDataBundle bundle)
        {
            if (userId != score.ScoreInfo.User.OnlineID)
                return;

            if (!LoadedBeatmapSuccessfully)
                return;

            if (!this.IsCurrentScreen())
                return;

            bool isFirstBundle = score.Replay.Frames.Count == 0;

            foreach (var frame in bundle.Frames)
            {
                IConvertibleReplayFrame convertibleFrame = GameplayState.Ruleset.CreateConvertibleReplayFrame()!;
                convertibleFrame.FromLegacy(frame, GameplayState.Beatmap);

                var convertedFrame = (ReplayFrame)convertibleFrame;
                convertedFrame.Time = frame.Time;
                convertedFrame.Header = frame.Header;

                score.Replay.Frames.Add(convertedFrame);
            }

            if (isFirstBundle && score.Replay.Frames.Count > 0)
                SetGameplayStartTime(score.Replay.Frames[0].Time);
        }

        protected override Score CreateScore(IBeatmap beatmap) => score;

        protected override ResultsScreen CreateResults(ScoreInfo score)
            => new SpectatorResultsScreen(score);

        protected override void PrepareReplay()
        {
            DrawableRuleset?.SetReplayScore(score);
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            SpectatorClient.OnNewFrames -= userSentFrames;

            return base.OnExiting(e);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (SpectatorClient.IsNotNull())
                SpectatorClient.OnNewFrames -= userSentFrames;
        }
    }
}
