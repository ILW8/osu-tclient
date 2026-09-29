// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Testing;
using osu.Game.Rulesets.UI;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Game.Tournament.Tests.Components
{
    public partial class TestSceneTournamentModIcon : TournamentTestScene
    {
        [Resolved]
        private TournamentStorage storage { get; set; } = null!;

        [Test]
        public void TestCustomModIconOnBeatmapPanel()
        {
            AddStep("write custom mod icon png", () =>
            {
                using var stream = storage.GetStream("Mods/ZZ.png", FileAccess.Write, FileMode.Create);
                using var image = new Image<Rgba32>(120, 60, new Rgba32(255, 0, 255));
                image.SaveAsPng(stream);
            });

            AddStep("show panel with custom mod", () => Child = new TournamentBeatmapPanel(CreateSampleBeatmap(), "ZZ")
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
            });

            AddUntilStep("custom sprite loaded", () => this.ChildrenOfType<TournamentModIcon>().SingleOrDefault()?.ChildrenOfType<Sprite>().Any() == true);

            AddAssert("custom sprite has non-zero size", () =>
            {
                var sprite = this.ChildrenOfType<TournamentModIcon>().Single().ChildrenOfType<Sprite>().Single();
                return sprite.DrawWidth > 0 && sprite.DrawHeight > 0;
            });
        }

        [Test]
        public void TestBuiltInModIconOnBeatmapPanel()
        {
            AddStep("show panel with built-in mod", () => Child = new TournamentBeatmapPanel(CreateSampleBeatmap(), "HD")
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
            });

            AddUntilStep("built-in icon loaded", () => this.ChildrenOfType<TournamentModIcon>().SingleOrDefault()?.ChildrenOfType<ModIcon>().Any() == true);

            AddAssert("built-in icon has non-zero size", () => this.ChildrenOfType<TournamentModIcon>().Single().DrawWidth > 0);
        }
    }
}
