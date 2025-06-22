﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
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
        private Bindable<bool> useLazerIpc = null!;

        [Resolved]
        private LadderInfo ladder { get; set; } = null!;

        public TeamScore(Bindable<long?> score, TeamColour colour, int count)
        {
            bool flip = colour == TeamColour.Blue;
            var anchor = flip ? Anchor.TopRight : Anchor.TopLeft;

            AutoSizeAxes = Axes.Both;

            InternalChild = counter = new TeamScoreStarCounter(count)
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
            useCumulativeScore.BindValueChanged(_ => updateVisibility());
            ladder.UseLazerIpc.BindValueChanged(_ => updateVisibility(), true);
        }

        private void updateVisibility()
        {
            // if using stable IPC and Cumulative score is true, when don't show stars (SSR)
            if (!ladder.UseLazerIpc.Value && useCumulativeScore.Value)
            {
                counter.Alpha = 0;
                return;
            }

            counter.Alpha = 1;
        }

        private void scoreChanged(ValueChangedEvent<long?> score) => counter.Current = score.NewValue ?? 0;

        public partial class TeamScoreStarCounter : StarCounter
        {
            public TeamScoreStarCounter(int count)
                : base(count)
            {
            }

            public override Star CreateStar() => new LightSquare();

            public partial class LightSquare : Star
            {
                private readonly Box box;

                public LightSquare()
                {
                    Size = new Vector2(22.5f);

                    InternalChildren = new Drawable[]
                    {
                        new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            Masking = true,
                            BorderColour = OsuColour.Gray(0.5f),
                            BorderThickness = 3,
                            Children = new Drawable[]
                            {
                                new Box
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
