// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;
using osuTK;

namespace osu.Game.Tournament.Screens.Gameplay.Components
{
    public partial class TeamScoreCumulative : CommaSeparatedScoreCounter<long>
    {
        private OsuSpriteText displayedSpriteText = null!;
        private const int font_size = 50;
        private Bindable<bool> useCumulativeScore = null!;

        [Resolved]
        private LadderInfo ladder { get; set; } = null!;

        private readonly Bindable<long?> currentTeamScore = new Bindable<long?>();
        private readonly BindableDictionary<string, Tuple<long, long>> mapScores = new BindableDictionary<string, Tuple<long, long>>();
        private readonly IBindable<TournamentBeatmap?> beatmap = new Bindable<TournamentBeatmap?>();
        private readonly TeamColour teamColour;

        public TeamScoreCumulative(TeamColour colour)
        {
            teamColour = colour;
        }

        [BackgroundDependencyLoader]
        private void load(LegacyMatchIPCInfo legacyIpc, MatchIPCInfo lazerIpc)
        {
            ladder.UseLazerIpc.BindValueChanged(
                vce =>
                {
                    beatmap.UnbindAll();
                    beatmap.BindTo(vce.NewValue ? lazerIpc.Beatmap : legacyIpc.Beatmap);
                },
                true);

            // delay cumulative score update until sets are updated
            beatmap.BindValueChanged(_ => Schedule(updateCumulativeScore), true);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            useCumulativeScore = ladder.CumulativeScore.GetBoundCopy();
            useCumulativeScore.BindValueChanged(v => displayedSpriteText.Alpha = v.NewValue ? 1 : 0, true);

            ladder.CurrentMatch.BindValueChanged(vce =>
            {
                mapScores.UnbindBindings();
                currentTeamScore.UnbindBindings();

                if (vce.NewValue == null)
                    return;

                var targetBindable = teamColour == TeamColour.Red ? ladder.CurrentMatch.Value?.Team1Score : ladder.CurrentMatch.Value?.Team2Score;

                if (targetBindable == null) return;

                currentTeamScore.BindTo(targetBindable);
                Logger.Log($"rebound currentTeamScore for team {teamColour}");
            }, true);

            mapScores.BindCollectionChanged((_, e) =>
            {
                // if (e.NewItems != null)
                //     Logger.Log($"mapScores changed: {string.Join(",", e.NewItems.Select(i => i.Key))}");

                updateCumulativeScore();
            }, true);

            currentTeamScore.BindValueChanged(vce =>
            {
                Logger.Log($"currentTeamScore for team {teamColour} changed: {vce.OldValue} -> {vce.NewValue}");
                updateCumulativeScoreStable(vce);
            }, true);
        }

        private void updateCumulativeScoreStable(ValueChangedEvent<long?> score) => Current.Value = score.NewValue ?? 0;

        private void updateCumulativeScore()
        {
            // don't check set scoring if using stable IPC
            if (!ladder.UseLazerIpc.Value)
                return;

            if (ladder.CurrentMatch.Value?.Round.Value == null)
                return;

            if (beatmap.Value == null)
                return;

            var scores = MatchSet.GetSetScores(ladder.CurrentMatch.Value, beatmap.Value.OnlineID);

            if (scores == null)
            {
                Current.Value = 0;
                return;
            }

            Current.Value = teamColour == TeamColour.Red ? scores.Item1 : scores.Item2;
        }

        protected override OsuSpriteText CreateSpriteText() => base.CreateSpriteText().With(s =>
        {
            displayedSpriteText = s;
            displayedSpriteText.Spacing = new Vector2(-6);
            displayedSpriteText.Font = OsuFont.Torus.With(weight: FontWeight.SemiBold, size: font_size, fixedWidth: true);
        });
    }
}
