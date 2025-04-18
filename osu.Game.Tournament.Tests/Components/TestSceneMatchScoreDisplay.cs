// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Utils;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Screens.Gameplay.Components;

namespace osu.Game.Tournament.Tests.Components
{
    public partial class TestSceneMatchScoreDisplay : TournamentTestScene
    {
        [Cached(Type = typeof(LegacyMatchIPCInfo))]
        private LegacyMatchIPCInfo legacyMatchInfo = new LegacyMatchIPCInfo();

        // public TestSceneMatchScoreDisplay()
        // {
        //     legacyMatchInfo.Score1.Value = 727;
        //
        //     Add(new TournamentMatchScoreDisplay
        //     {
        //         Anchor = Anchor.Centre,
        //         Origin = Anchor.Centre,
        //     });
        // }

        [Test]
        public void TestScoreUpdate()
        {
            AddStep("disable cumulative score", () => Ladder.CumulativeScore.Value = false);
            AddStep("use legacy ipc", () => Ladder.UseLazerIpc.Value = false);

            AddStep("setup screen", () =>
            {
                Children = new Drawable[]
                {
                    new TournamentMatchScoreDisplay
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                    }
                };
            });

            AddRepeatStep("update scores", () =>
            {
                int amount = (int)((RNG.NextDouble() - 0.5) * 10_000);

                Logger.Log($"updating score with {amount}");

                if (amount < 0)
                    legacyMatchInfo.Score1.Value -= amount;
                else
                    legacyMatchInfo.Score2.Value += amount;
            }, 100);
        }
    }
}
