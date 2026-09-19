// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Taiko;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.Models;
using osuTK;

namespace osu.Game.Tournament.Tests.Components
{
    [TestFixture]
    public partial class TestSceneSongBar : TournamentTestScene
    {
        private SongBar songBar = null!;
        private TournamentBeatmap ladderBeatmap = null!;

        [Resolved]
        private BeatmapManager beatmapManager { get; set; } = null!;

        [Resolved]
        private RealmAccess realm { get; set; } = null!;

        [SetUpSteps]
        public override void SetUpSteps()
        {
            base.SetUpSteps();

            AddStep("setup picks bans", () =>
            {
                ladderBeatmap = CreateSampleBeatmap();
                Ladder.CurrentMatch.Value!.PicksBans.Add(new BeatmapChoice
                {
                    BeatmapID = ladderBeatmap.OnlineID,
                    Team = TeamColour.Red,
                    Type = ChoiceType.Pick,
                });
            });

            AddStep("create bar", () => Child = songBar = new SongBar
            {
                RelativeSizeAxes = Axes.X,
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre
            });
            AddUntilStep("wait for loaded", () => songBar.IsLoaded);
        }

        [Test]
        public void TestSongBar()
        {
            AddStep("set beatmap", () =>
            {
                var beatmap = CreateAPIBeatmap(Ruleset.Value);

                beatmap.CircleSize = 3.4f;
                beatmap.ApproachRate = 6.8f;
                beatmap.OverallDifficulty = 5.5f;
                beatmap.StarRating = 4.56f;
                beatmap.DrainRate = 1.23f;
                beatmap.Length = 123456;
                beatmap.BPM = 133;
                beatmap.OnlineID = ladderBeatmap.OnlineID;

                songBar.Beatmap = new TournamentBeatmap(beatmap);
            });

            AddStep("set mods to HR", () => songBar.Mods = new Mod[] { new OsuModHardRock() });
            AddStep("set mods to DT", () => songBar.Mods = new Mod[] { new OsuModDoubleTime() });
            AddStep("set mods to HDHRDT", () => songBar.Mods = new Mod[] { new OsuModHidden(), new OsuModHardRock(), new OsuModDoubleTime() });

            AddStep("set mods to DA (CS 7, AR 9.5)", () => songBar.Mods = new Mod[]
            {
                new OsuModDifficultyAdjust { CircleSize = { Value = 7 }, ApproachRate = { Value = 9.5f } }
            });
            AddAssert("CS shows 7", () => statValue("CS"), () => Is.EqualTo("7"));
            AddAssert("AR shows 9.5", () => statValue("AR"), () => Is.EqualTo("9.5"));
            AddAssert("OD unchanged", () => statValue("OD"), () => Is.EqualTo("5.5"));

            AddStep("unset mods", () => songBar.Mods = System.Array.Empty<Mod>());
            AddAssert("CS reverts", () => statValue("CS"), () => Is.EqualTo("3.4"));

            AddToggleStep("toggle expanded", expanded => songBar.Expanded = expanded);

            AddStep("set null beatmap", () => songBar.Beatmap = null);

            AddStep("set ruleset to osu", () => Ruleset.Value = new OsuRuleset().RulesetInfo);
            AddStep("set ruleset to taiko", () => Ruleset.Value = new TaikoRuleset().RulesetInfo);
            AddStep("set ruleset to catch", () => Ruleset.Value = new CatchRuleset().RulesetInfo);
            AddStep("set ruleset to mania", () => Ruleset.Value = new ManiaRuleset().RulesetInfo);
        }

        [Test]
        public void TestLocallyComputedStarRating()
        {
            const int online_id = 424242;
            string nomodStarRating = null!;

            AddStep("import local beatmap", () =>
            {
                var working = beatmapManager.CreateNew(new OsuRuleset().RulesetInfo, new GuestUser());
                var beatmap = (Beatmap)working.Beatmap;
                beatmap.ControlPointInfo.Add(0, new TimingControlPoint { BeatLength = 500 });

                for (int i = 0; i < 20; i++)
                    beatmap.HitObjects.Add(new HitCircle { StartTime = i * 500, Position = new Vector2(i % 2 == 0 ? 100 : 400, 100 + i * 10) });

                beatmapManager.Save(working.BeatmapInfo, beatmap);
                realm.Write(r => r.Find<BeatmapInfo>(working.BeatmapInfo.ID)!.OnlineID = online_id);
            });

            AddStep("set beatmap with online SR", () =>
            {
                var beatmap = CreateAPIBeatmap(Ruleset.Value);
                beatmap.StarRating = 4.56f;
                beatmap.OnlineID = online_id;
                songBar.Mods = System.Array.Empty<Mod>();
                songBar.Beatmap = new TournamentBeatmap(beatmap);
            });

            AddUntilStep("SR computed locally", () => statValue("Star Rating"), () => Does.Not.EndWith("*"));
            AddStep("remember nomod SR", () => nomodStarRating = statValue("Star Rating"));

            AddStep("set mods to DT", () => songBar.Mods = new Mod[] { new OsuModDoubleTime() });
            AddUntilStep("SR recomputed for DT", () => statValue("Star Rating"), () => Is.Not.EqualTo(nomodStarRating).And.Not.EndWith("*"));
        }

        // DiffPiece emits "<heading>", " ", "<content>" as consecutive sprite texts.
        private string statValue(string heading)
        {
            var texts = songBar.ChildrenOfType<SpriteText>().ToList();
            int i = texts.FindIndex(t => t.Text.ToString() == heading);
            return texts[i + 2].Text.ToString();
        }
    }
}
