// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Logging;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Tournament.Models;
using osu.Game.Tournament.Screens.Ladder.Components;
using osuTK.Graphics;

namespace osu.Game.Tournament.Components
{
    public partial class TournamentSetPanel : CompositeDrawable
    {
        private partial class SetMapScoreCounter : DrawableMatchTeam.MatchTeamCumulativeScoreCounter
        {
            protected override OsuSpriteText CreateSpriteText() => base.CreateSpriteText().With(s =>
            {
                DisplayedSpriteText = s;
                DisplayedSpriteText.Font = OsuFont.Torus.With(size: 16);
            });
        }

        private partial class SetMapResult : CompositeDrawable
        {
            public readonly IBindable<long?> Player1Score = new Bindable<long?>();
            public readonly IBindable<long?> Player2Score = new Bindable<long?>();

            private SetMapScoreCounter p1ScoreCounter = null!;
            private SetMapScoreCounter p2ScoreCounter = null!;

            public SetMapResult()
            {
                Height = 32;
                Anchor = Anchor.CentreLeft;
                Origin = Anchor.CentreLeft;
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                InternalChildren = new Drawable[]
                {
                    new Container
                    {
                        Width = 0.3f,
                        RelativeSizeAxes = Axes.Both,
                        Child = new TournamentSpriteText
                        {
                            Origin = Anchor.Centre,
                            Anchor = Anchor.Centre,
                            Text = "NM1",
                            Font = OsuFont.Torus.With(weight: FontWeight.Bold, size: 22),
                        },
                    },
                    new Container
                    {
                        Masking = true,
                        Width = 0.35f,
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        RelativeSizeAxes = Axes.Both,
                        RelativePositionAxes = Axes.X,
                        X = 0.3f,
                        Children = new Drawable[]
                        {
                            // new Box
                            // {
                            //     Colour = OsuColour.Gray(0.1f),
                            //     Alpha = 0.8f,
                            //     RelativeSizeAxes = Axes.Both,
                            // },
                            p1ScoreCounter = new SetMapScoreCounter
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                            },
                        }
                    },
                    new Container
                    {
                        Masking = true,
                        Width = 0.35f,
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        RelativeSizeAxes = Axes.Both,
                        RelativePositionAxes = Axes.X,
                        X = 0.65f,
                        Children = new Drawable[]
                        {
                            p2ScoreCounter = new SetMapScoreCounter
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                            },
                        }
                    }
                };

                Player1Score.BindValueChanged(val => updatePlayerScore(p1ScoreCounter, val), true);
                Player2Score.BindValueChanged(val => updatePlayerScore(p2ScoreCounter, val), true);

                // testing code, remove!!!
                {
                    var a = new Bindable<long?>();
                    var b = new Bindable<long?>();

                    Player1Score.BindTo(a);
                    Player2Score.BindTo(b);

                    a.Value = Random.Shared.NextInt64() % 1_200_000;
                    b.Value = Random.Shared.NextInt64() % 1_200_000;
                }

                void updatePlayerScore(SetMapScoreCounter counter, ValueChangedEvent<long?> changeEvent)
                {
                    switch (changeEvent.NewValue)
                    {
                        case 0:
                        case null:
                            counter.DisplayedSpriteText.Text = changeEvent.NewValue?.ToString() ?? "0";
                            break;

                        default:
                            counter.Current.Value = (double)changeEvent.NewValue;
                            break;
                    }
                }
            }
        }

        public float HEIGHT = TournamentBeatmapPanel.HEIGHT;
        public float WIDTH = TournamentBeatmapPanel.WIDTH;

        private TeamColour? winnerColour = null;

        public TeamColour? Winner
        {
            get => winnerColour;
            set
            {
                winnerColour = value;
                updateWinState();
            }
        }

        public readonly BindableList<TournamentBeatmapPanel> BeatmapPanels = new BindableList<TournamentBeatmapPanel>();

        public TournamentSetPanel()
        {
            Width = WIDTH;
            Height = HEIGHT;
            Masking = true;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            AddRangeInternal(new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Color4.Black,
                    Alpha = 0.35f,
                },
                // new FillFlowContainer
                // {
                //     AutoSizeAxes = Axes.Both,
                //     Anchor = Anchor.TopCentre,
                //     Origin = Anchor.TopCentre,
                //     Padding = new MarginPadding(15),
                //     Spacing = new Vector2(15),
                //     Direction = FillDirection.Horizontal,
                //     Children = new Drawable[]
                //     {
                //         new TournamentSpriteText
                //         {
                //             Text = "aba",
                //             Font = OsuFont.Torus.With(weight: FontWeight.Bold),
                //         },
                //         new TournamentSpriteText
                //         {
                //             Text = "aboba",
                //             Font = OsuFont.Torus.With(weight: FontWeight.Bold),
                //         },
                //     }
                // },
                new GridContainer
                {
                    Name = "slots",
                    // Margin = new MarginPadding { Top = main_content_height },
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding(5),
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    ColumnDimensions = new[]
                    {
                        new Dimension(),
                        new Dimension()
                    },
                    RowDimensions = new[] { new Dimension() },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            // new TournamentSpriteText
                            // {
                            //     Origin = Anchor.Centre,
                            //     Anchor = Anchor.Centre,
                            //     Text = "NM1",
                            //     Font = OsuFont.Torus.With(weight: FontWeight.Bold),
                            // },
                            // new TournamentSpriteText
                            // {
                            //     Origin = Anchor.Centre,
                            //     Anchor = Anchor.Centre,
                            //     Text = "NM2",
                            //     Font = OsuFont.Torus.With(weight: FontWeight.Bold),
                            // },
                            new SetMapResult
                            {
                                RelativeSizeAxes = Axes.X,
                            },
                            new SetMapResult
                            {
                                RelativeSizeAxes = Axes.X,
                            }
                        }
                    }
                }
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            BeatmapPanels.BindCollectionChanged((sender, args) =>
            {
                Logger.Log($"TournamentSetPanel beatmap panels collection changed: {args.NewItems}");
            });
        }

        /// <summary>
        /// Add/remove colored border depending on winner
        /// </summary>
        private void updateWinState()
        {
            if (winnerColour != null)
            {
                BorderThickness = 6;

                BorderColour = TournamentGame.GetTeamColour((TeamColour)winnerColour);
            }
            else
            {
                Colour = Color4.White;
                BorderThickness = 0;
                Alpha = 1;
            }
        }
    }
}
