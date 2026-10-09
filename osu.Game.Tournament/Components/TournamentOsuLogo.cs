// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Game.Screens.Menu;
using osu.Game.Tournament.Models;

namespace osu.Game.Tournament.Components
{
    public partial class TournamentOsuLogo : OsuLogo
    {
        private MenuLogoVisualisation visualisation = null!;
        private IBindable<float> amplitude = null!;

        // Called from the base constructor, so only assign here; the bindable is wired up in load.
        protected override MenuLogoVisualisation CreateMenuLogoVisualisation() => visualisation = new MenuLogoVisualisation();

        [BackgroundDependencyLoader]
        private void load(LadderInfo ladder)
        {
            amplitude = ladder.LogoVisualiserAmplitude.GetBoundCopy();
            amplitude.BindValueChanged(a => visualisation.Magnitude = a.NewValue, true);
        }
    }
}
