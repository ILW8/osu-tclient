// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.Models;
using osu.Game.Tournament.Screens.Editors;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Game.Tournament.Screens.Ladder.Components
{
    public partial class DrawableMatchTeam : DrawableTournamentTeam, IHasContextMenu
    {
        public partial class MatchTeamCumulativeScoreCounter : CommaSeparatedScoreCounter
        {
            public OsuSpriteText DisplayedSpriteText = null!;

            protected override double RollingDuration => 0;

            protected override OsuSpriteText CreateSpriteText() => base.CreateSpriteText().With(s =>
            {
                DisplayedSpriteText = s;
                DisplayedSpriteText.Font = OsuFont.Torus.With(size: 22);
            });
        }

        private readonly TournamentMatch match;
        private readonly bool losers;
        private MatchTeamCumulativeScoreCounter scoreText = null!;
        private Box background = null!;
        private Box backgroundRight = null!;

        private readonly Bindable<long?> score = new Bindable<long?>();
        private readonly BindableBool completed = new BindableBool();

        private Color4 colourWinner;

        private readonly Func<bool>? isWinner;
        private LadderEditorScreen ladderEditor = null!;

        [Resolved]
        private LadderInfo ladderInfo { get; set; } = null!;

        private void setCurrent()
        {
            //todo: tournamentgamebase?
            if (ladderInfo.CurrentMatch.Value != null)
                ladderInfo.CurrentMatch.Value.Current.Value = false;

            ladderInfo.CurrentMatch.Value = match;
            ladderInfo.CurrentMatch.Value.Current.Value = true;
        }

        [Resolved]
        private LadderEditorInfo? editorInfo { get; set; }

        public DrawableMatchTeam(TournamentTeam? team, TournamentMatch match, bool losers)
            : base(team)
        {
            this.match = match;
            this.losers = losers;

            Flag.Scale = new Vector2(0.54f);
            Flag.Anchor = Flag.Origin = Anchor.CentreLeft;

            AcronymText.Anchor = AcronymText.Origin = Anchor.CentreLeft;
            AcronymText.Padding = new MarginPadding { Left = 50 };
            AcronymText.Font = OsuFont.Torus.With(size: 22, weight: FontWeight.Bold);

            isWinner = () => match.Winner == Team;

            completed.BindTo(match.Completed);
            if (team != null)
                score.BindTo(team == match.Team1.Value ? match.Team1Score : match.Team2Score);
        }

        [BackgroundDependencyLoader(true)]
        private void load(LadderEditorScreen ladderEditor)
        {
            this.ladderEditor = ladderEditor;

            colourWinner = losers
                ? Color4Extensions.FromHex("#8E7F48")
                : Color4Extensions.FromHex("#1462AA");

            ladderInfo.Use1V1Mode.BindValueChanged(use1V1 =>
            {
                Size = new Vector2(use1V1.NewValue ? 300 : 200, 40);
            }, true);

            InternalChildren = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                },
                new Container
                {
                    Padding = new MarginPadding(5),
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        AcronymText,
                        Flag,
                    }
                },
                new Container
                {
                    Masking = true,
                    Width = 64,
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    RelativeSizeAxes = Axes.Y,
                    Children = new Drawable[]
                    {
                        backgroundRight = new Box
                        {
                            Colour = OsuColour.Gray(0.1f),
                            Alpha = 0.8f,
                            RelativeSizeAxes = Axes.Both,
                        },
                        scoreText = new MatchTeamCumulativeScoreCounter
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                        },
                    }
                }
            };

            completed.BindValueChanged(_ => updateWinStyle());

            score.BindValueChanged(val =>
            {
                switch (val.NewValue)
                {
                    case 0:
                    case null:
                        scoreText.DisplayedSpriteText.Text = val.NewValue?.ToString() ?? "";
                        break;

                    default:
                        scoreText.Current.Value = (double)val.NewValue;
                        break;
                }

                updateWinStyle();
            }, true);
        }

        protected override bool OnClick(ClickEvent e)
        {
            if (Team == null || editorInfo != null) return false;

            if (!match.Current.Value)
            {
                setCurrent();
                return true;
            }

            if (e.Button == MouseButton.Left)
            {
                if (score.Value == null)
                {
                    match.StartMatch();
                }
                else if (!match.Completed.Value)
                    score.Value++;
            }
            else
            {
                if (match.Progression.Value?.Completed.Value == true)
                    // don't allow changing scores if the match has a progression. can cause large data loss
                    return false;

                if (match.Completed.Value && match.Winner != Team)
                    // don't allow changing scores from the non-winner
                    return false;

                if (score.Value > 0)
                    score.Value--;
                else
                    match.CancelMatchStart();
            }

            return false;
        }

        private void updateWinStyle()
        {
            bool winner = completed.Value && isWinner?.Invoke() == true;

            background.FadeColour(winner ? Color4.White : Color4Extensions.FromHex("#444"), winner ? 500 : 0, Easing.OutQuint);
            backgroundRight.FadeColour(winner ? colourWinner : Color4Extensions.FromHex("#333"), winner ? 500 : 0, Easing.OutQuint);

            AcronymText.Colour = winner ? Color4.Black : Color4.White;

            scoreText.DisplayedSpriteText.Font = scoreText.DisplayedSpriteText.Font.With(weight: winner ? FontWeight.Bold : FontWeight.Regular);
        }

        public MenuItem[] ContextMenuItems
        {
            get
            {
                if (editorInfo == null)
                    return Array.Empty<MenuItem>();

                return new MenuItem[]
                {
                    new OsuMenuItem("Set as current", MenuItemType.Standard, setCurrent),
                    new OsuMenuItem("Join with", MenuItemType.Standard, () => ladderEditor.BeginJoin(match, false)),
                    new OsuMenuItem("Join with (loser)", MenuItemType.Standard, () => ladderEditor.BeginJoin(match, true)),
                    new OsuMenuItem("Remove", MenuItemType.Destructive, () => ladderEditor.Remove(match)),
                };
            }
        }
    }
}
