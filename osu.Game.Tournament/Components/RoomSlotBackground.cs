// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Layout;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Graphics;
using osu.Game.Screens.Menu;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;
using osuTK;

namespace osu.Game.Tournament.Components
{
    /// <summary>
    /// The beatmap cover and osu! logo behind a <see cref="RoomUserCardGrid"/> slot, shown whether or not a
    /// <see cref="RoomUserCard"/> occupies it.
    /// </summary>
    public partial class RoomSlotBackground : CompositeDrawable
    {
        private const float osu_logo_scale = 0.75f;

        private readonly IBindable<TournamentBeatmap?> beatmap = new Bindable<TournamentBeatmap?>();
        private readonly UpdateableOnlineBeatmapSetCover background;
        private readonly OsuLogo osuLogo;
        private readonly LayoutValue drawSizeLayout = new LayoutValue(Invalidation.DrawSize);

        private bool occupied;

        /// <summary>
        /// Whether a player occupies this slot. The logo only shows its triangles while one does.
        /// </summary>
        public bool Occupied
        {
            get => occupied;
            set
            {
                if (occupied == value)
                    return;

                occupied = value;
                osuLogo.Triangles = value;
            }
        }

        public RoomSlotBackground()
        {
            Masking = true;
            AddLayout(drawSizeLayout);

            InternalChildren = new Drawable[]
            {
                background = new TournamentBeatmapPanel.NoUnloadBeatmapSetCover
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = OsuColour.Gray(0.5f),
                },
                osuLogo = new TournamentOsuLogo
                {
                    Triangles = false,
                    Origin = Anchor.Centre,
                    Anchor = Anchor.Centre,
                },
            };
        }

        [BackgroundDependencyLoader]
        private void load(MatchIPCInfo ipc)
        {
            beatmap.BindTo(ipc.Beatmap);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            beatmap.BindValueChanged(b => background.OnlineInfo = b.NewValue, true);
        }

        protected override void Update()
        {
            base.Update();

            if (!drawSizeLayout.IsValid && osuLogo.SizeForFlow > 0)
            {
                osuLogo.Scale = new Vector2(osu_logo_scale * DrawHeight / osuLogo.SizeForFlow);
                drawSizeLayout.Validate();
            }
        }
    }
}
