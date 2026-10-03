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
    /// A pre-gameplay stand-in for a room user's spectator tile: avatar, flag, username and ready / download status.
    /// </summary>
    public partial class RoomUserCard : CompositeDrawable
    {
        public readonly int UserId;

        private readonly TournamentSpriteText status;

        internal string Status => status.Text.ToString();

        public RoomUserCard(MultiplayerRoomUser user)
        {
            UserId = user.UserID;
            RelativeSizeAxes = Axes.Both;

            Color4 teamColour = (user.MatchState as TeamVersusUserState)?.TeamID switch
            {
                (int)TeamColour.Red => TournamentGame.COLOUR_RED,
                (int)TeamColour.Blue => TournamentGame.COLOUR_BLUE,
                _ => Color4.Gray,
            };

            InternalChild = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding(5),
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = 5,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Color4.Black,
                            Alpha = 0.6f,
                        },
                        new Box
                        {
                            RelativeSizeAxes = Axes.Y,
                            Width = 6,
                            Colour = teamColour,
                        },
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(8),
                            Children = new Drawable[]
                            {
                                new UpdateableAvatar(user.User, isInteractive: false)
                                {
                                    Anchor = Anchor.TopCentre,
                                    Origin = Anchor.TopCentre,
                                    Size = new Vector2(96),
                                    Masking = true,
                                    CornerRadius = 10,
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
                        },
                    },
                },
            };

            UpdateStatus(user);
        }

        public void UpdateStatus(MultiplayerRoomUser user) => status.Text = GetStatusText(user.State, user.BeatmapAvailability);

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
