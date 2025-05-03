﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;

namespace osu.Game.Tournament.Screens
{
    public abstract partial class BeatmapInfoScreen : TournamentMatchScreen
    {
        protected readonly SongBar SongBar;
        private readonly IBindable<TournamentBeatmap?> beatmap = new Bindable<TournamentBeatmap?>();

        protected BeatmapInfoScreen()
        {
            AddInternal(SongBar = new SongBar
            {
                Anchor = Anchor.BottomRight,
                Origin = Anchor.BottomRight,
                Depth = float.MinValue,
            });
        }

        [BackgroundDependencyLoader]
        private void load(LegacyMatchIPCInfo legacyIpc, MatchIPCInfo lazerIpc, LadderInfo ladder)
        {
            ladder.UseLazerIpc.BindValueChanged(
                vce =>
                {
                    beatmap.UnbindAll();
                    beatmap.BindTo(vce.NewValue ? lazerIpc.Beatmap : legacyIpc.Beatmap);
                },
                true);

            beatmap.BindValueChanged(beatmapChanged, true);
            legacyIpc.Mods.BindValueChanged(modsChanged, true);
        }

        private void modsChanged(ValueChangedEvent<LegacyMods> mods)
        {
            SongBar.Mods = mods.NewValue;
        }

        private void beatmapChanged(ValueChangedEvent<TournamentBeatmap?> beatmap)
        {
            SongBar.FadeOutFromOne(200, Easing.OutQuint).Then().FadeIn(300, Easing.OutQuint);
            Scheduler.AddDelayed(() => SongBar.Beatmap = beatmap.NewValue, 200);
        }
    }
}
