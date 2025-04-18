// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics.UserInterface;
using osu.Game.Tournament.Models;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Tournament.Screens.Gameplay.Components
{
    public partial class TeamScore : CompositeDrawable
    {
        private readonly Bindable<long?> currentTeamScore = new Bindable<long?>();
        private readonly StarCounter counter;
        private Bindable<bool> useCumulativeScore = null!;

        [Resolved]
        private LadderInfo ladder { get; set; } = null!;

        public TeamScore(Bindable<long?> score, TeamColour colour, int count)
        {
            bool flip = colour == TeamColour.Blue;
            var anchor = flip ? Anchor.TopRight : Anchor.TopLeft;

            AutoSizeAxes = Axes.Both;

            InternalChild = counter = new TeamScoreStarCounter(count, colour)
            {
                Anchor = anchor,
                Scale = flip ? new Vector2(-1, 1) : Vector2.One,
            };

            currentTeamScore.BindValueChanged(scoreChanged);
            currentTeamScore.BindTo(score);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            useCumulativeScore = ladder.CumulativeScore.GetBoundCopy();
            useCumulativeScore.BindValueChanged(v => counter.Alpha = v.NewValue ? 0 : 1, true);
        }

        private void scoreChanged(ValueChangedEvent<long?> score) => counter.Current = score.NewValue ?? 0;

        public partial class TeamScoreStarCounter : StarCounter
        {
            public TeamScoreStarCounter(int count, TeamColour colour)
                : base(count)
            {
                Stars.ForEach(s =>
                {
                    if (s is not LightSquare lightSquare)
                        return;

                    lightSquare.UpdateTeam(colour);
                });
            }

            public override Star CreateStar() => new LightSquare();

            public partial class LightSquare : Star
            {
                private readonly Box box;
                private readonly Box backgroundBox;

                public void UpdateTeam(TeamColour colour)
                {
                    box.Colour = TournamentGame.GetTeamColour(colour);
                    backgroundBox.Colour = TournamentGame.GetTeamScoreBackgroundColour(colour);

                    Colour4 colour4TeamScoreColour = TournamentGame.GetTeamColour(colour);
                    EdgeEffect = new EdgeEffectParameters
                    {
                        Type = EdgeEffectType.Glow,
                        Colour = colour4TeamScoreColour.Opacity(0.1f),
                        Hollow = true,
                        Radius = 20,
                        Roundness = 10,
                    };
                }

                public LightSquare()
                {
                    Size = new Vector2(22.5f);

                    InternalChildren = new Drawable[]
                    {
                        new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            Masking = true,
                            Children = new Drawable[]
                            {
                                backgroundBox = new Box
                                {
                                    Colour = Color4.Transparent,
                                    RelativeSizeAxes = Axes.Both,
                                    AlwaysPresent = true,
                                },
                            }
                        },
                        box = new Box
                        {
                            Colour = Color4Extensions.FromHex("#FFE8AD"),
                            RelativeSizeAxes = Axes.Both,
                        },
                    };

                    Masking = true;
                    EdgeEffect = new EdgeEffectParameters
                    {
                        Type = EdgeEffectType.Glow,
                        Colour = Color4Extensions.FromHex("#FFE8AD").Opacity(0.1f),
                        Hollow = true,
                        Radius = 20,
                        Roundness = 10,
                    };
                }

                public override void DisplayAt(float scale)
                {
                    box.FadeTo(scale, 500, Easing.OutQuint);
                    FadeEdgeEffectTo(0.2f * scale, 500, Easing.OutQuint);
                }
            }
        }
    }
}
