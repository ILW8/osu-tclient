// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Tournament.Models;
using osuTK;

namespace osu.Game.Tournament.Screens.Gameplay.Components
{
    public partial class TeamScoreCumulative : CommaSeparatedScoreCounter
    {
        private OsuSpriteText displayedSpriteText = null!;
        private const int font_size = 50;
        private Bindable<bool> useCumulativeScore = null!;

        [Resolved]
        private LadderInfo ladder { get; set; } = null!;

        public TeamScoreCumulative()
        {
            Margin = new MarginPadding(8);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            useCumulativeScore = ladder.CumulativeScore.GetBoundCopy();
            useCumulativeScore.BindValueChanged(v => displayedSpriteText.Alpha = v.NewValue ? 1 : 0, true);
        }

        protected override OsuSpriteText CreateSpriteText() => base.CreateSpriteText().With(s =>
        {
            displayedSpriteText = s;
            displayedSpriteText.Spacing = new Vector2(-6);
            displayedSpriteText.Font = OsuFont.Torus.With(weight: FontWeight.SemiBold, size: font_size, fixedWidth: true);
        });
    }
}
