// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Specialized;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Graphics;
using osu.Game.Tournament.Models;
using osuTK.Graphics;

namespace osu.Game.Tournament.Components
{
    public partial class TournamentBeatmapPanel : CompositeDrawable
    {
        public readonly IBeatmapInfo? Beatmap;

        private readonly string mod;

        public const float HEIGHT = 50;
        public const float WIDTH = 400;

        private readonly Bindable<TournamentMatch?> currentMatch = new Bindable<TournamentMatch?>();

        private Box flash = null!;
        private Container borderBox = null!;
        private SpriteIcon protectIndicator = null!;

        public TournamentBeatmapPanel(IBeatmapInfo? beatmap, string mod = "")
        {
            Beatmap = beatmap;
            this.mod = mod;

            Width = WIDTH;
            Height = HEIGHT;

            Padding = new MarginPadding { Left = 8 };
        }

        [BackgroundDependencyLoader]
        private void load(LadderInfo ladder)
        {
            currentMatch.BindValueChanged(matchChanged);
            currentMatch.BindTo(ladder.CurrentMatch);

            Masking = true;

            AddRangeInternal(new Drawable[]
            {
                borderBox = new Container
                {
                    Masking = true,
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Color4.Black,
                        },
                        new NoUnloadBeatmapSetCover
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = OsuColour.Gray(0.5f),
                            OnlineInfo = (Beatmap as IBeatmapSetOnlineInfo),
                        },
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Padding = new MarginPadding(15),
                            Direction = FillDirection.Vertical,
                            Children = new Drawable[]
                            {
                                new TournamentSpriteText
                                {
                                    Text = Beatmap?.GetDisplayTitleRomanisable(false, false) ?? (LocalisableString)@"unknown",
                                    Font = OsuFont.Torus.With(weight: FontWeight.Bold),
                                },
                                new FillFlowContainer
                                {
                                    AutoSizeAxes = Axes.Both,
                                    Direction = FillDirection.Horizontal,
                                    Children = new Drawable[]
                                    {
                                        new TournamentSpriteText
                                        {
                                            Text = "mapper",
                                            Padding = new MarginPadding { Right = 5 },
                                            Font = OsuFont.Torus.With(weight: FontWeight.Regular, size: 14)
                                        },
                                        new TournamentSpriteText
                                        {
                                            Text = Beatmap?.Metadata.Author.Username ?? "unknown",
                                            Padding = new MarginPadding { Right = 20 },
                                            Font = OsuFont.Torus.With(weight: FontWeight.Bold, size: 14)
                                        },
                                        new TournamentSpriteText
                                        {
                                            Text = "difficulty",
                                            Padding = new MarginPadding { Right = 5 },
                                            Font = OsuFont.Torus.With(weight: FontWeight.Regular, size: 14)
                                        },
                                        new TournamentSpriteText
                                        {
                                            Text = Beatmap?.DifficultyName ?? "unknown",
                                            Font = OsuFont.Torus.With(weight: FontWeight.Bold, size: 14)
                                        },
                                    }
                                }
                            },
                        },
                        flash = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Color4.Gray,
                            Blending = BlendingParameters.Additive,
                            Alpha = 0,
                        },
                    }
                },
                protectIndicator = new SpriteIcon
                {
                    Icon = FontAwesome.Solid.Lock,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Width = 14,
                    X = -6,
                    Alpha = 0,
                    // X = -24,
                    // Y = 30,
                    // Rotation = 40,
                    RelativeSizeAxes = Axes.Y,
                    // Margin = new MarginPadding { Right = 20, Vertical = 20 }
                }
            });

            if (!string.IsNullOrEmpty(mod))
            {
                AddInternal(new TournamentModIcon(mod)
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding(10),
                    Width = 60,
                    RelativeSizeAxes = Axes.Y,
                });
            }

            // AddInternal(});
        }

        private void matchChanged(ValueChangedEvent<TournamentMatch?> match)
        {
            if (match.OldValue != null)
                match.OldValue.PicksBans.CollectionChanged -= picksBansOnCollectionChanged;
            if (match.NewValue != null)
                match.NewValue.PicksBans.CollectionChanged += picksBansOnCollectionChanged;

            Scheduler.AddOnce(updateState);
        }

        private void picksBansOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => Scheduler.AddOnce(updateState);

        private BeatmapChoice? choice;

        private void updateState()
        {
            if (currentMatch.Value == null)
            {
                return;
            }

            // protected?
            var protectedChoice = currentMatch.Value.PicksBans.FirstOrDefault(p => p.BeatmapID == Beatmap?.OnlineID && p.Type == ChoiceType.Protect);

            if (protectedChoice != null)
            {
                protectIndicator.FadeIn();
                protectIndicator.Colour = TournamentGame.GetTeamColour(protectedChoice.Team);
            }
            else
            {
                protectIndicator.Alpha = 0;
                protectIndicator.Colour = Color4.White;
            }

            var newChoice = currentMatch.Value.PicksBans.LastOrDefault(p => p.BeatmapID == Beatmap?.OnlineID && p.Type != ChoiceType.Protect);

            bool shouldFlash = newChoice != choice;

            if (newChoice != null)
            {
                if (shouldFlash)
                    flash.FadeOutFromOne(500).Loop(0, 10);

                borderBox.BorderThickness = 6;
                borderBox.BorderColour = TournamentGame.GetTeamColour(newChoice.Team);

                switch (newChoice.Type)
                {
                    case ChoiceType.Pick:
                        borderBox.Colour = Color4.White;
                        borderBox.Alpha = 1;
                        break;

                    case ChoiceType.Ban:
                        borderBox.Colour = Color4.Gray;
                        borderBox.Alpha = 0.5f;
                        break;
                }
            }
            else
            {
                borderBox.Colour = Color4.White;
                borderBox.BorderThickness = 0;
                borderBox.Alpha = 1;
            }

            choice = newChoice;
        }

        private partial class NoUnloadBeatmapSetCover : UpdateableOnlineBeatmapSetCover
        {
            // As covers are displayed on stream, we want them to load as soon as possible.
            protected override double LoadDelay => 0;

            // Use DelayedLoadWrapper to avoid content unloading when switching away to another screen.
            protected override DelayedLoadWrapper CreateDelayedLoadWrapper(Func<Drawable> createContentFunc, double timeBeforeLoad)
                => new DelayedLoadWrapper(createContentFunc(), timeBeforeLoad);
        }
    }
}
