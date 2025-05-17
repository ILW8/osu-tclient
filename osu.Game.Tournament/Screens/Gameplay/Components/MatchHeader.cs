// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;
using osuTK;

namespace osu.Game.Tournament.Screens.Gameplay.Components
{
    public partial class MatchHeader : Container
    {
        private const int cumulative_delta_height = 24;

        public partial class MatchCumulativeScoreDiffCounter : CommaSeparatedScoreCounter
        {
            protected override OsuSpriteText CreateSpriteText() => base.CreateSpriteText().With(s =>
            {
                s.Spacing = new Vector2(-2);
                s.Font = OsuFont.Torus.With(weight: FontWeight.Regular, size: cumulative_delta_height, fixedWidth: true);
            });
        }

        [Resolved]
        private MatchIPCInfo lazerIpc { get; set; } = null!;

        [Resolved]
        private LadderInfo ladder { get; set; } = null!;

        private TeamScoreDisplay teamDisplay1 = null!;
        private TeamScoreDisplay teamDisplay2 = null!;
        private DrawableTournamentHeaderLogo logo = null!;
        private MatchRoundDisplay roundDisplay = null!;
        private MatchCumulativeScoreDiffCounter cumulativeScoreDiffCounter = null!;
        private FillFlowContainer cumulativeScoreDiffCounterContainer = null!;
        private readonly Bindable<TournamentMatch?> currentMatch = new Bindable<TournamentMatch?>();
        private readonly Bindable<long?> team1Score = new Bindable<long?>();
        private readonly Bindable<long?> team2Score = new Bindable<long?>();
        private readonly BindableDictionary<string, Tuple<long, long>> matchScores = new BindableDictionary<string, Tuple<long, long>>();
        private Bindable<bool> useCumulativeScore = null!;
        private SpriteIcon leftWinningTriangle = null!;
        private SpriteIcon rightWinningTriangle = null!;

        private bool showScores = true;

        public bool ShowScores
        {
            get => showScores;
            set
            {
                if (value == showScores)
                    return;

                showScores = value;

                if (IsLoaded)
                    updateDisplay();
            }
        }

        private bool showLogo = true;

        public bool ShowLogo
        {
            get => showLogo;
            set
            {
                if (value == showLogo)
                    return;

                showLogo = value;

                if (IsLoaded)
                    updateDisplay();
            }
        }

        private bool showMatchRound = true;

        public bool ShowMatchRound
        {
            get => showMatchRound;

            set
            {
                if (value == showMatchRound)
                    return;

                showMatchRound = value;

                if (IsLoaded)
                    updateDisplay();
            }
        }

        [BackgroundDependencyLoader]
        private void load(LadderInfo ladder)
        {
            useCumulativeScore = ladder.CumulativeScore.GetBoundCopy();
            currentMatch.BindTo(ladder.CurrentMatch);

            RelativeSizeAxes = Axes.X;
            Height = 95;
            Children = new Drawable[]
            {
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Direction = FillDirection.Vertical,
                    Padding = new MarginPadding(20),
                    Spacing = new Vector2(5),
                    Children = new Drawable[]
                    {
                        logo = new DrawableTournamentHeaderLogo
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                            Alpha = showLogo ? 1 : 0
                        },
                        new DrawableTournamentHeaderText
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                        },
                        roundDisplay = new MatchRoundDisplay
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                            Scale = new Vector2(0.4f)
                        },
                        cumulativeScoreDiffCounterContainer = new FillFlowContainer
                        {
                            Direction = FillDirection.Horizontal,
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                            Spacing = new Vector2(16),
                            Margin = new MarginPadding { Top = 16 },
                            Children = new Drawable[]
                            {
                                leftWinningTriangle = new SpriteIcon
                                {
                                    Icon = FontAwesome.Solid.ChevronLeft,
                                    Width = 12,
                                    Height = 12,
                                    Margin = new MarginPadding
                                    {
                                        Vertical = 7
                                    },
                                    Alpha = 0,
                                    Anchor = Anchor.TopCentre,
                                    Origin = Anchor.TopCentre,
                                    AlwaysPresent = true
                                },
                                cumulativeScoreDiffCounter = new MatchCumulativeScoreDiffCounter
                                {
                                    Anchor = Anchor.TopCentre,
                                    Origin = Anchor.TopCentre,
                                },
                                rightWinningTriangle = new SpriteIcon
                                {
                                    Icon = FontAwesome.Solid.ChevronRight,
                                    Width = 12,
                                    Height = 12,
                                    Margin = new MarginPadding
                                    {
                                        Vertical = 7
                                    },
                                    Alpha = 0,
                                    Anchor = Anchor.TopCentre,
                                    Origin = Anchor.TopCentre,
                                    AlwaysPresent = true
                                },
                            }
                        }
                    }
                },
                teamDisplay1 = new TeamScoreDisplay(TeamColour.Red)
                {
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.TopLeft,
                },
                teamDisplay2 = new TeamScoreDisplay(TeamColour.Blue)
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                },
            };
        }

        private void updateScoreDelta()
        {
            if (ladder.CurrentMatch.Value == null)
                return;

            long scoreDelta = calculateScoreDelta();

            cumulativeScoreDiffCounter.Current.Value = Math.Abs(scoreDelta);

            leftWinningTriangle.FadeTo(scoreDelta > 0 ? 1 : 0, 200);
            rightWinningTriangle.FadeTo(scoreDelta < 0 ? 1 : 0, 200);
            return;

            long calculateScoreDelta()
            {
                int mapId = lazerIpc.Beatmap.Value?.OnlineID ?? 0;

                if (mapId <= 0)
                    return 0;

                var scores = MatchSet.FindSetByMapId(ladder.CurrentMatch.Value, mapId)?.GetSetScores(ladder.CurrentMatch.Value);

                return scores != null ? scores.Item1 - scores.Item2 : 0;
            }
        }

        private void matchChanged(ValueChangedEvent<TournamentMatch?> match)
        {
            team1Score.UnbindBindings();
            team2Score.UnbindBindings();
            matchScores.UnbindBindings();

            Scheduler.AddOnce(updateMatch);
        }

        private void updateMatch()
        {
            var match = currentMatch.Value;

            if (match == null) return;

            team1Score.BindTo(match.Team1Score);
            team2Score.BindTo(match.Team2Score);
            matchScores.BindTo(match.MapScores);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            currentMatch.BindValueChanged(matchChanged, true);
            useCumulativeScore.BindValueChanged(_ => updateDisplay(), true);
            lazerIpc.Beatmap.BindValueChanged(_ => updateScoreDelta(), true);
            matchScores.BindCollectionChanged((_, _) => updateScoreDelta());
        }

        private void updateDisplay()
        {
            teamDisplay1.ShowScore = showScores;
            teamDisplay2.ShowScore = showScores;
            cumulativeScoreDiffCounterContainer.FadeTo(showScores && useCumulativeScore.Value ? 1 : 0, 200);

            logo.Alpha = showLogo ? 1 : 0;
            roundDisplay.Alpha = showMatchRound ? 1 : 0;
        }
    }
}
