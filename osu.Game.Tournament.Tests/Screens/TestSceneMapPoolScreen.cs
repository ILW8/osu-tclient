// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.Models;
using osu.Game.Tournament.Screens.MapPool;
using osuTK;
using osuTK.Input;

namespace osu.Game.Tournament.Tests.Screens
{
    public partial class TestSceneMapPoolScreen : TournamentScreenTestScene
    {
        [Cached]
        private TournamentMatchChatDisplay chat = new TournamentMatchChatDisplay { Width = 0.5f };

        private MapPoolScreen screen = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            Add(screen = new TestMapPoolScreen { Width = 0.7f });
            Add(chat);
        }

        [SetUpSteps]
        public override void SetUpSteps()
        {
            AddStep("reset state", resetState);
        }

        private void resetState()
        {
            Ladder.SplitMapPoolByMods.Value = true;

            Ladder.CurrentMatch.Value = new TournamentMatch();
            Ladder.Matches.First().PicksBans.Clear();
            Ladder.Matches.First().Sets.Clear();
            Ladder.CurrentMatch.Value = Ladder.Matches.First();
        }

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
        });

        [Test]
        public void TestLazerGrandArena()
        {
            int pickIndex = 0;

            AddStep("load first weekend maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 4; i++)
                    addBeatmap("NM", $"NM map #{i}");
                for (int i = 0; i < 3; i++)
                    addBeatmap("HD", $"HD map #{i}");
                for (int i = 0; i < 3; i++)
                    addBeatmap("HR", $"HR map #{i}");
                for (int i = 0; i < 3; i++)
                    addBeatmap("DT", $"DT map #{i}");

                addBeatmap("LM", "LM1");
                addBeatmap("OG", "OG1");

                resetState();
                pickIndex = 0;
            });

            AddRepeatStep("perform picks/bans", () => clickBeatmapPanel(pickIndex++), 4 + 5 * 2); // 4 bans, up to 5 sets of 2 picks
        }

        [Test]
        public void TestLgaSetScoring()
        {
            AddStep("load first weekend maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 4; i++)
                    addBeatmap("NM", $"NM map #{i}");
                for (int i = 0; i < 2; i++)
                    addBeatmap("HD", $"HD map #{i}");
                for (int i = 0; i < 2; i++)
                    addBeatmap("HR", $"HR map #{i}");
                for (int i = 0; i < 3; i++)
                    addBeatmap("DT", $"DT map #{i}");

                resetState();
            });

            AddStep("set red pick", () => screen.ChildrenOfType<TourneyButton>().First(btn => btn.Text == "Red Pick").TriggerClick());
            AddStep("pick nm1", () => clickBeatmapPanel(0));
            AddStep("set scores on nm1", () => Ladder.CurrentMatch.Value!.MapScores["NM1"] = new Tuple<long, long>(Random.Shared.Next() % 1_000_000, Random.Shared.Next() % 1_000_000));

            AddStep("set blue pick", () => screen.ChildrenOfType<TourneyButton>().First(btn => btn.Text == "Blue Pick").TriggerClick());
            AddStep("pick nm2", () => clickBeatmapPanel(1));
            AddStep("set scores on nm2", () => Ladder.CurrentMatch.Value!.MapScores["NM2"] = new Tuple<long, long>(Random.Shared.Next() % 1_000_000, Random.Shared.Next() % 1_000_000));
        }

        [Test]
        public void TestMapIndicatorVisibility()
        {
            AddStep("load 15 maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 15; i++)
                    addBeatmap();
            });

            AddStep("use lazer ipc", () => Ladder.UseLazerIpc.Value = true);

            AddStep("reset state", resetState);

            AddStep("set red pick", () => screen.ChildrenOfType<TourneyButton>().First(btn => btn.Text == "Red Pick").TriggerClick());
            AddStep("pick first map", () => clickBeatmapPanel(0));
            AddStep("set blue pick", () => screen.ChildrenOfType<TourneyButton>().First(btn => btn.Text == "Blue Pick").TriggerClick());
            AddStep("pick first map", () => clickBeatmapPanel(1));

            AddStep("update current beatmap", () =>
            {
                var newTournamentBeatmap = Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.First(
                    b => screen.ChildrenOfType<TournamentBeatmapPanel>().ElementAt(1).Beatmap!.OnlineID == b.Beatmap!.OnlineID
                ).Beatmap;
                LazerIPCInfo.Beatmap.Value = newTournamentBeatmap;
            });

            AddStep("reset state", resetState);
            AddStep("set red pick", () => screen.ChildrenOfType<TourneyButton>().First(btn => btn.Text == "Red Pick").TriggerClick());
            AddStep("pick first map", () => clickBeatmapPanel(0));
            AddStep("set blue pick", () => screen.ChildrenOfType<TourneyButton>().First(btn => btn.Text == "Blue Pick").TriggerClick());
            AddStep("pick first map", () => clickBeatmapPanel(1));
            AddStep("update current beatmap", () =>
            {
                var newTournamentBeatmap = Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.First(
                    b => screen.ChildrenOfType<TournamentBeatmapPanel>().ElementAt(0).Beatmap!.OnlineID == b.Beatmap!.OnlineID
                ).Beatmap;
                LazerIPCInfo.Beatmap.Value = newTournamentBeatmap;
            });
            AddStep("update current beatmap", () =>
            {
                var newTournamentBeatmap = Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.First(
                    b => screen.ChildrenOfType<TournamentBeatmapPanel>().ElementAt(1).Beatmap!.OnlineID == b.Beatmap!.OnlineID
                ).Beatmap;
                LazerIPCInfo.Beatmap.Value = newTournamentBeatmap;
            });
        }

        [Test]
        public void TestLazerGrandArenaWeek2PickBan()
        {
            AddStep("load 15 maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 15; i++)
                    addBeatmap();
            });

            AddStep("update displayed maps", () => Ladder.SplitMapPoolByMods.Value = false);

            int pickBanIndex = 0;

            // ban AB
            AddRepeatStep("first ban phase", () => clickBeatmapPanel(pickBanIndex++), 2);
            // checkTotalPickBans(2);
            // checkLastPick(ChoiceType.Ban, TeamColour.Blue);

            // pick BAAB
            AddRepeatStep("first pick phase", () => clickBeatmapPanel(pickBanIndex++), 4);
            // checkTotalPickBans(6);
            // checkLastPick(ChoiceType.Pick, TeamColour.Blue);

            AddAssert("pick order has 4 maps", () => screen.ChildrenOfType<FillFlowContainer<TournamentBeatmapPanel>>().Last().Count == 4);

            // ban ABBA
            AddRepeatStep("second ban phase", () => clickBeatmapPanel(pickBanIndex++), 4);
            // checkTotalPickBans(10);
            // checkLastPick(ChoiceType.Ban, TeamColour.Red);

            // pick AB
            AddRepeatStep("second pick phase", () => clickBeatmapPanel(pickBanIndex++), 2);
            // checkTotalPickBans(12);
            // checkLastPick(ChoiceType.Pick, TeamColour.Blue);

            AddAssert("pick order has 6 maps", () => screen.ChildrenOfType<FillFlowContainer<TournamentBeatmapPanel>>().Last().Count == 6);

            // ban BA
            AddRepeatStep("last pick phase", () => clickBeatmapPanel(pickBanIndex++), 2);
            // checkTotalPickBans(14);
            // checkLastPick(ChoiceType.Ban, TeamColour.Red);

            AddAssert("picks and bans order conform to LGA week 2",
                () =>
                {
                    var expected = new List<(ChoiceType Type, TeamColour Colour)>
                    {
                        (ChoiceType.Ban, TeamColour.Red),
                        (ChoiceType.Ban, TeamColour.Blue),

                        (ChoiceType.Pick, TeamColour.Blue),
                        (ChoiceType.Pick, TeamColour.Red),
                        (ChoiceType.Pick, TeamColour.Red),
                        (ChoiceType.Pick, TeamColour.Blue),

                        (ChoiceType.Ban, TeamColour.Blue),
                        (ChoiceType.Ban, TeamColour.Red),
                        (ChoiceType.Ban, TeamColour.Red),
                        (ChoiceType.Ban, TeamColour.Blue),

                        (ChoiceType.Pick, TeamColour.Red),
                        (ChoiceType.Pick, TeamColour.Blue),

                        (ChoiceType.Ban, TeamColour.Blue),
                        (ChoiceType.Ban, TeamColour.Red),
                    };

                    var picksBans = Ladder.CurrentMatch.Value!.PicksBans;
                    if (picksBans.Count != expected.Count)
                        return false;

                    for (int i = 0; i < expected.Count; i++)
                    {
                        if (picksBans[i].Type != expected[i].Type || picksBans[i].Team != expected[i].Colour)
                            return false;
                    }

                    return true;
                });

            AddAssert("pick order has 7 maps", () => screen.ChildrenOfType<FillFlowContainer<TournamentBeatmapPanel>>().Last().Count == 7);
        }

        [Test]
        public void TestFewMaps()
        {
            AddStep("load few maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 8; i++)
                    addBeatmap();
            });

            AddStep("reset match", () =>
            {
                Ladder.CurrentMatch.Value = new TournamentMatch();
            });

            assertTwoWide();
        }

        [Test]
        public void TestJustEnoughMaps()
        {
            AddStep("load just enough maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 18; i++)
                    addBeatmap();
            });

            AddStep("reset state", resetState);

            assertTwoWide();
        }

        [Test]
        public void TestManyMaps()
        {
            AddStep("load many maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 19; i++)
                    addBeatmap();
            });

            AddStep("reset state", resetState);

            assertThreeWide();
        }

        [Test]
        public void TestJustEnoughMods()
        {
            AddStep("load many maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 11; i++)
                    addBeatmap(i > 4 ? Ruleset.Value.CreateInstance().AllMods.ElementAt(i).Acronym : "NM");
            });

            AddStep("reset state", resetState);

            assertTwoWide();
        }

        private void assertTwoWide() =>
            AddAssert("ensure layout width is 2", () => screen.ChildrenOfType<FillFlowContainer<FillFlowContainer<TournamentBeatmapPanel>>>().First().Padding.Left > 0);

        private void assertThreeWide() =>
            AddAssert("ensure layout width is 3", () => screen.ChildrenOfType<FillFlowContainer<FillFlowContainer<TournamentBeatmapPanel>>>().First().Padding.Left == 0);

        [Test]
        public void TestManyMods()
        {
            AddStep("load many maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 12; i++)
                    addBeatmap(i > 4 ? Ruleset.Value.CreateInstance().AllMods.ElementAt(i).Acronym : "NM");
            });

            AddStep("reset state", resetState);

            assertThreeWide();
        }

        [Test]
        public void TestSplitMapPoolByMods()
        {
            AddStep("load many maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 12; i++)
                    addBeatmap(i > 4 ? Ruleset.Value.CreateInstance().AllMods.ElementAt(i).Acronym : "NM");
            });

            AddStep("disable splitting map pool by mods", () => Ladder.SplitMapPoolByMods.Value = false);

            AddStep("reset state", resetState);
        }

        [Test]
        public void TestBanOrderMultipleBans()
        {
            AddStep("set ban count", () => Ladder.CurrentMatch.Value!.Round.Value!.BanCount.Value = 2);

            AddStep("load some maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 5; i++)
                    addBeatmap();
            });

            AddStep("update displayed maps", () => Ladder.SplitMapPoolByMods.Value = false);

            AddStep("start bans from blue team", () => screen.ChildrenOfType<TourneyButton>().First(btn => btn.Text == "Blue Ban").TriggerClick());

            AddStep("ban map", () => clickBeatmapPanel(0));
            checkTotalPickBans(1);
            checkLastPick(ChoiceType.Ban, TeamColour.Blue);

            AddStep("ban map", () => clickBeatmapPanel(1));
            checkTotalPickBans(2);
            checkLastPick(ChoiceType.Ban, TeamColour.Red);

            AddStep("ban map", () => clickBeatmapPanel(2));
            checkTotalPickBans(3);
            checkLastPick(ChoiceType.Ban, TeamColour.Red);

            AddStep("pick map", () => clickBeatmapPanel(3));
            checkTotalPickBans(4);
            checkLastPick(ChoiceType.Ban, TeamColour.Blue);

            AddStep("pick map", () => clickBeatmapPanel(4));
            checkTotalPickBans(5);
            checkLastPick(ChoiceType.Pick, TeamColour.Blue);
        }

        [Test]
        public void TestPickBanOrder()
        {
            AddStep("set ban count", () => Ladder.CurrentMatch.Value!.Round.Value!.BanCount.Value = 1);

            AddStep("load some maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 5; i++)
                    addBeatmap();
            });

            AddStep("update displayed maps", () => Ladder.SplitMapPoolByMods.Value = false);

            AddStep("start bans from blue team", () => screen.ChildrenOfType<TourneyButton>().First(btn => btn.Text == "Blue Ban").TriggerClick());

            AddStep("ban map", () => clickBeatmapPanel(0));
            checkTotalPickBans(1);
            checkLastPick(ChoiceType.Ban, TeamColour.Blue);

            AddStep("ban map", () => clickBeatmapPanel(1));
            checkTotalPickBans(2);
            checkLastPick(ChoiceType.Ban, TeamColour.Red);

            AddStep("pick map", () => clickBeatmapPanel(2));
            checkTotalPickBans(3);
            checkLastPick(ChoiceType.Pick, TeamColour.Red);

            AddStep("pick map", () => clickBeatmapPanel(3));
            checkTotalPickBans(4);
            checkLastPick(ChoiceType.Pick, TeamColour.Blue);

            AddStep("pick map", () => clickBeatmapPanel(4));
            checkTotalPickBans(5);
            checkLastPick(ChoiceType.Pick, TeamColour.Red);

            AddStep("reset match", () =>
            {
                Ladder.CurrentMatch.Value = new TournamentMatch();
                Ladder.CurrentMatch.Value = Ladder.Matches.First();
                Ladder.CurrentMatch.Value.PicksBans.Clear();
            });
        }

        [Test]
        public void TestMultipleTeamBans()
        {
            AddStep("set ban count", () => Ladder.CurrentMatch.Value!.Round.Value!.BanCount.Value = 3);

            AddStep("load some maps", () =>
            {
                Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Clear();

                for (int i = 0; i < 12; i++)
                    addBeatmap();
            });

            AddStep("update displayed maps", () => Ladder.SplitMapPoolByMods.Value = false);

            AddStep("start bans with red team", () => screen.ChildrenOfType<TourneyButton>().First(btn => btn.Text == "Red Ban").TriggerClick());

            AddStep("first ban", () => clickBeatmapPanel(0));
            AddAssert("red ban registered",
                () => Ladder.CurrentMatch.Value!.PicksBans.Count(pb => pb.Type == ChoiceType.Ban && pb.Team == TeamColour.Red),
                () => Is.EqualTo(1));

            AddStep("ban two more maps", () =>
            {
                clickBeatmapPanel(1);
                clickBeatmapPanel(2);
            });

            AddAssert("three bans registered",
                () => Ladder.CurrentMatch.Value!.PicksBans.Count(pb => pb.Type == ChoiceType.Ban),
                () => Is.EqualTo(3));
            AddAssert("both new bans for blue team",
                () => Ladder.CurrentMatch.Value!.PicksBans.Count(pb => pb.Type == ChoiceType.Ban && pb.Team == TeamColour.Blue),
                () => Is.EqualTo(2));

            AddStep("ban two more maps", () =>
            {
                clickBeatmapPanel(3);
                clickBeatmapPanel(4);
            });

            AddAssert("five bans registered",
                () => Ladder.CurrentMatch.Value!.PicksBans.Count(pb => pb.Type == ChoiceType.Ban),
                () => Is.EqualTo(5));
            AddAssert("both new bans for red team",
                () => Ladder.CurrentMatch.Value!.PicksBans.Count(pb => pb.Type == ChoiceType.Ban && pb.Team == TeamColour.Red),
                () => Is.EqualTo(3));

            AddStep("ban last map", () => clickBeatmapPanel(5));
            AddAssert("six bans registered",
                () => Ladder.CurrentMatch.Value!.PicksBans.Count(pb => pb.Type == ChoiceType.Ban),
                () => Is.EqualTo(6));
            AddAssert("red banned three",
                () => Ladder.CurrentMatch.Value!.PicksBans.Count(pb => pb.Type == ChoiceType.Ban && pb.Team == TeamColour.Red),
                () => Is.EqualTo(3));
            AddAssert("blue banned three",
                () => Ladder.CurrentMatch.Value!.PicksBans.Count(pb => pb.Type == ChoiceType.Ban && pb.Team == TeamColour.Blue),
                () => Is.EqualTo(3));

            AddStep("pick map", () => clickBeatmapPanel(6));
            AddAssert("one pick registered",
                () => Ladder.CurrentMatch.Value!.PicksBans.Count(pb => pb.Type == ChoiceType.Pick),
                () => Is.EqualTo(1));
            AddAssert("pick was blue's",
                () => Ladder.CurrentMatch.Value!.PicksBans.Last().Team,
                () => Is.EqualTo(TeamColour.Blue));

            AddStep("pick map", () => clickBeatmapPanel(7));
            AddAssert("two picks registered",
                () => Ladder.CurrentMatch.Value!.PicksBans.Count(pb => pb.Type == ChoiceType.Pick),
                () => Is.EqualTo(2));
            AddAssert("pick was red's",
                () => Ladder.CurrentMatch.Value!.PicksBans.Last().Team,
                () => Is.EqualTo(TeamColour.Red));

            AddStep("pick map", () => clickBeatmapPanel(8));
            AddAssert("three picks registered",
                () => Ladder.CurrentMatch.Value!.PicksBans.Count(pb => pb.Type == ChoiceType.Pick),
                () => Is.EqualTo(3));
            AddAssert("pick was blue's",
                () => Ladder.CurrentMatch.Value!.PicksBans.Last().Team,
                () => Is.EqualTo(TeamColour.Blue));

            AddStep("reset match", () =>
            {
                Ladder.CurrentMatch.Value = new TournamentMatch();
                Ladder.CurrentMatch.Value = Ladder.Matches.First();
                Ladder.CurrentMatch.Value.PicksBans.Clear();
            });
        }

        private void checkTotalPickBans(int expected) => AddAssert($"total pickbans is {expected}", () => Ladder.CurrentMatch.Value!.PicksBans, () => Has.Count.EqualTo(expected));

        private void checkLastPick(ChoiceType expectedChoice, TeamColour expectedColour) =>
            AddAssert($"last choice was {expectedChoice} by {expectedColour}",
                () => Ladder.CurrentMatch.Value!.PicksBans.Select(pb => (pb.Type, pb.Team)).Last(),
                () => Is.EqualTo((expectedChoice, expectedColour)));

        private void addBeatmap(string mods = "NM", string? titleOverride = null)
        {
            var newBeatmap = CreateSampleBeatmap(titleOverride);

            int modSlotIndex = Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Count(bm => bm.Mods == mods) + 1;

            Ladder.CurrentMatch.Value!.Round.Value!.Beatmaps.Add(new RoundBeatmap
            {
                Beatmap = newBeatmap,
                ID = newBeatmap.OnlineID,
                Mods = mods,
                SlotName = $"{mods}{modSlotIndex}"
            });
        }

        private void clickBeatmapPanel(int index)
        {
            InputManager.MoveMouseTo(screen.ChildrenOfType<TournamentBeatmapPanel>().ElementAt(index));
            InputManager.Click(MouseButton.Left);
        }

        private partial class TestMapPoolScreen : MapPoolScreen
        {
            // this is a bit of a test-specific workaround.
            // the way pick/ban is implemented is a bit funky; the screen itself is what handles the mouse there,
            // rather than the beatmap panels themselves.
            // in some extreme situations headless it may turn out that the panels overflow the screen,
            // and as such picking stops working anymore outside of the bounds of the screen drawable.
            // this override makes it so the screen sees all of the input at all times, making that impossible to happen.
            public override bool ReceivePositionalInputAt(Vector2 screenSpacePos) => true;
        }
    }
}
