// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
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

        public partial class SetMapResult : CompositeDrawable
        {
            public readonly IBindable<long?> Player1Score = new Bindable<long?>();
            public readonly IBindable<long?> Player2Score = new Bindable<long?>();

            private TournamentSpriteText slotText = null!;
            private long mapId = 0;

            private SetMapScoreCounter p1ScoreCounter = null!;
            private SetMapScoreCounter p2ScoreCounter = null!;

            [Resolved]
            protected LadderInfo LadderInfo { get; private set; } = null!;

            protected readonly Bindable<TournamentMatch?> CurrentMatch = new Bindable<TournamentMatch?>();

            protected override void LoadComplete()
            {
                base.LoadComplete();

                CurrentMatch.BindTo(LadderInfo.CurrentMatch);
                CurrentMatch.BindValueChanged(_ => updateSlotName(), true);
            }

            public long MapID
            {
                get => mapId;
                set
                {
                    mapId = value;

                    updateSlotName();
                }
            }

            // lookup slot name from ladder
            private void updateSlotName()
            {
                if (CurrentMatch.Value == null)
                    return;

                if (mapId == 0)
                {
                    slotText.Text = string.Empty;
                    return;
                }

                var poolMap = CurrentMatch.Value.Round.Value?.Beatmaps.FirstOrDefault(bm => bm.ID == mapId);

                slotText.Text = poolMap?.SlotName ?? "??";

                Logger.Log($"Setting mapid for {TestName}: {poolMap}");
            }

            public readonly string TestName = "";

            public SetMapResult(string? name = null)
            {
                Height = 32;
                Anchor = Anchor.CentreLeft;
                Origin = Anchor.CentreLeft;

                if (name != null)
                    TestName = name;
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
                        Child = slotText = new TournamentSpriteText
                        {
                            Origin = Anchor.Centre,
                            Anchor = Anchor.Centre,
                            Text = "",
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

                // todo: bind score to something...?
                Player1Score.BindValueChanged(val => updatePlayerScore(p1ScoreCounter, val), true);
                Player2Score.BindValueChanged(val => updatePlayerScore(p2ScoreCounter, val), true);

                void updatePlayerScore(SetMapScoreCounter counter, ValueChangedEvent<long?> changeEvent)
                {
                    switch (changeEvent.NewValue)
                    {
                        case 0:
                        case null:
                            counter.DisplayedSpriteText.Text = changeEvent.NewValue?.ToString() ?? "";
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
        private long mapId = 0;

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

        private SetMapResult map1Result = null!;
        private SetMapResult map2Result = null!;

        private readonly BindableList<SetMapResult> mapResults = new BindableList<SetMapResult>();
        public IBindableList<SetMapResult> MapResults => mapResults;

        public BindableLong Map1Id = new BindableLong();
        public BindableLong Map2Id = new BindableLong();

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
                new GridContainer
                {
                    Name = "slots",
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
                            map1Result = new SetMapResult("slot1")
                            {
                                RelativeSizeAxes = Axes.X,
                            },
                            map2Result = new SetMapResult("slot2")
                            {
                                RelativeSizeAxes = Axes.X,
                            }
                        }
                    }
                }
            });

            // mapResults.Add(map1Result);
            // mapResults.Add(map2Result);

            Map1Id.BindValueChanged(vce =>
            {
                map1Result.MapID = vce.NewValue;
            }, true);
            Map2Id.BindValueChanged(vce =>
            {
                map2Result.MapID = vce.NewValue;
            }, true);
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
