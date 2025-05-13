// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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

        public partial class SetMapResultDisplay : CompositeDrawable
        {
            public event Action? ResultChanged;

            // todo: make these private?
            public readonly Bindable<long?> ScoreRed = new Bindable<long?>();
            public readonly Bindable<long?> ScoreBlue = new Bindable<long?>();

            private TournamentSpriteText slotText = null!;
            private long mapId = 0;

            public long MapID
            {
                get => mapId;
                set
                {
                    mapId = value;

                    updateSlotName();
                }
            }

            private SetMapScoreCounter scoreCounterRed = null!;
            private SetMapScoreCounter scoreCounterBlue = null!;

            [Resolved]
            protected LadderInfo LadderInfo { get; private set; } = null!;

            protected readonly Bindable<TournamentMatch?> CurrentMatch = new Bindable<TournamentMatch?>();

            protected override void LoadComplete()
            {
                base.LoadComplete();

                CurrentMatch.BindTo(LadderInfo.CurrentMatch);
                CurrentMatch.BindValueChanged(_ => updateSlotName(), true);
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

                CurrentMatch.Value.MapScores.BindCollectionChanged((_, args) =>
                {
                    if (args.NewItems == null)
                        return;

                    foreach ((string key, var value) in args.NewItems)
                    {
                        if (key != slotText.Text)
                            continue;

                        ScoreRed.Value = value.Item1;
                        ScoreBlue.Value = value.Item2;

                        ResultChanged?.Invoke();
                    }
                });
            }

            public readonly string TestName = "";

            public SetMapResultDisplay(string? name = null)
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
                            scoreCounterRed = new SetMapScoreCounter
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
                            scoreCounterBlue = new SetMapScoreCounter
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                            },
                        }
                    }
                };

                ScoreRed.BindValueChanged(val => updatePlayerScore(scoreCounterRed, val), true);
                ScoreBlue.BindValueChanged(val => updatePlayerScore(scoreCounterBlue, val), true);
                return;

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

        public const float HEIGHT = TournamentBeatmapPanel.HEIGHT;
        public const float WIDTH = TournamentBeatmapPanel.WIDTH;

        private TeamColour? winnerColour = null;
        // private long mapId = 0;

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

        private SetMapResultDisplay map1ResultDisplay = null!;
        private SetMapResultDisplay map2ResultDisplay = null!;

        private readonly BindableList<SetMapResultDisplay> mapResults = new BindableList<SetMapResultDisplay>();
        public IBindableList<SetMapResultDisplay> MapResults => mapResults; // does this need to be a bindable list?

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
                            map1ResultDisplay = new SetMapResultDisplay("slot1")
                            {
                                RelativeSizeAxes = Axes.X,
                            },
                            map2ResultDisplay = new SetMapResultDisplay("slot2")
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
                map1ResultDisplay.MapID = vce.NewValue;
            }, true);
            Map2Id.BindValueChanged(vce =>
            {
                map2ResultDisplay.MapID = vce.NewValue;
            }, true);

            map1ResultDisplay.ResultChanged += checkWinState;
            map2ResultDisplay.ResultChanged += checkWinState;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            BeatmapPanels.BindCollectionChanged((sender, args) =>
            {
                Logger.Log($"TournamentSetPanel beatmap panels collection changed: {args.NewItems}");
            });
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            map1ResultDisplay.ResultChanged -= checkWinState;
            map2ResultDisplay.ResultChanged -= checkWinState;
        }

        /// <summary>
        /// Checks the score(s) of the set and set a winner if appropriate
        /// </summary>
        private void checkWinState()
        {
            if (map1ResultDisplay.ScoreRed.Value == null || map2ResultDisplay.ScoreRed.Value == null)
            {
                Winner = null;
                return;
            }

            long redScore = (map1ResultDisplay.ScoreRed.Value ?? 0) + (map2ResultDisplay.ScoreRed.Value ?? 0);
            long blueScore = (map1ResultDisplay.ScoreBlue.Value ?? 0) + (map2ResultDisplay.ScoreBlue.Value ?? 0);

            if (redScore < blueScore)
            {
                Winner = TeamColour.Blue;
                return;
            }

            Winner = TeamColour.Red;
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
