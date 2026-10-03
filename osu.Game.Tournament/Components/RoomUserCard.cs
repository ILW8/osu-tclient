// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
using osu.Game.Online;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Rooms;
using osu.Game.Tournament.Models;
using osu.Game.Users;
using osu.Game.Users.Drawables;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Tournament.Components
{
    /// <summary>
    /// A pre-gameplay stand-in for a room user's spectator tile: avatar with a team-coloured bar, flag, username and
    /// ready / download status.
    /// </summary>
    public partial class RoomUserCard : CompositeDrawable
    {
        private const float avatar_size = 96;
        private const float avatar_corner_radius = 10;
        private const float team_bar_width = 6;

        public readonly int UserId;

        /// <summary>
        /// The grid slot this card occupies. <see cref="RoomUserCardGrid"/> positions the card from it.
        /// </summary>
        public int Slot { get; set; }

        private readonly Box teamBar;
        private readonly TournamentSpriteText status;

        internal string Status => status.Text.ToString();

        internal Color4 AccentColour { get; private set; }

        public RoomUserCard(MultiplayerRoomUser user)
        {
            UserId = user.UserID;
            Masking = true;

            InternalChild = new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(8),
                Children = new Drawable[]
                {
                    new Container
                    {
                        AutoSizeAxes = Axes.Both,
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Children = new Drawable[]
                        {
                            // Runs under the avatar's left edge so its colour fills the rounded corners there. Two corner radii
                            // wide so its own rounded right corners share their arc with the avatar's, leaving no gap.
                            new Container
                            {
                                Width = team_bar_width + 2 * avatar_corner_radius,
                                Height = avatar_size,
                                Masking = true,
                                CornerRadius = avatar_corner_radius,
                                Child = teamBar = new Box { RelativeSizeAxes = Axes.Both },
                            },
                            new UpdateableAvatar(user.User, isInteractive: false)
                            {
                                X = team_bar_width,
                                Size = new Vector2(avatar_size),
                                Masking = true,
                                CornerRadius = avatar_corner_radius,
                            },
                        },
                    },
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Direction = FillDirection.Horizontal,
                        Spacing = new Vector2(8, 0),
                        Children = new Drawable[]
                        {
                            new UpdateableFlag(user.User?.CountryCode ?? CountryCode.Unknown)
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Size = new Vector2(30, 20),
                            },
                            new TournamentSpriteText
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Text = user.User?.Username ?? $"User {user.UserID}",
                                Font = OsuFont.Torus.With(size: 28, weight: FontWeight.Bold),
                            },
                        },
                    },
                    status = new TournamentSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Font = OsuFont.Torus.With(size: 20, weight: FontWeight.SemiBold),
                    },
                },
            };

            UpdateFrom(user);
        }

        /// <summary>
        /// Refreshes what can change while the card is up: team colour and status line.
        /// </summary>
        public void UpdateFrom(MultiplayerRoomUser user)
        {
            teamBar.Colour = AccentColour = (user.MatchState as TeamVersusUserState)?.TeamID switch
            {
                (int)TeamColour.Red => TournamentGame.COLOUR_RED,
                (int)TeamColour.Blue => TournamentGame.COLOUR_BLUE,
                _ => Color4.Gray,
            };

            status.Text = GetStatusText(user.State, user.BeatmapAvailability);
        }

        /// <summary>
        /// The player's own download state takes priority (they can't ready up without the map), then their ready state.
        /// </summary>
        internal static string GetStatusText(MultiplayerUserState state, BeatmapAvailability availability) => availability.State switch
        {
            DownloadState.NotDownloaded => "Missing map",
            DownloadState.Downloading => $"Downloading {availability.DownloadProgress ?? 0:0%}",
            DownloadState.Importing => "Importing",
            _ => state switch
            {
                MultiplayerUserState.Idle => "Not ready",
                MultiplayerUserState.Ready => "Ready",
                MultiplayerUserState.WaitingForLoad or MultiplayerUserState.Loaded or MultiplayerUserState.ReadyForGameplay => "Loading",
                MultiplayerUserState.Playing => "Playing",
                MultiplayerUserState.FinishedPlay or MultiplayerUserState.Results => "Finished",
                _ => string.Empty,
            },
        };
    }
}
