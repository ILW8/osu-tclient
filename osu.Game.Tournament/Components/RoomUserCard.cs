// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Layout;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Graphics;
using osu.Game.Online;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Rooms;
using osu.Game.Screens.Menu;
using osu.Game.Tournament.IPC;
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
        private const float avatar_size = 64;
        private const float avatar_corner_radius = 7;
        private const float team_bar_width = 4;

        /// <summary>
        /// The "reference" card size at which the user info is drawn. This allows the user info to scale to fit within
        /// the card, so a wide card (two players per team) doesn't scale it past what the card's height suggests.
        /// </summary>
        private static readonly Vector2 user_info_design_size = new Vector2(512, 384);

        public readonly int UserId;

        /// <summary>
        /// The grid slot this card occupies. <see cref="RoomUserCardGrid"/> positions the card from it.
        /// </summary>
        public int Slot { get; set; }

        private readonly APIUser? apiUser;
        private readonly IBindable<TournamentBeatmap?> beatmap = new Bindable<TournamentBeatmap?>();
        private readonly UpdateableOnlineBeatmapSetCover background;
        private readonly Container teamBarContainer;
        private readonly Box teamBar;
        private readonly Container avatarContainer;
        private readonly OsuLogo osuLogo;
        private const float osu_logo_scale = 0.75f;
        private readonly FillFlowContainer userInfoContainer;
        private readonly LayoutValue drawSizeLayout = new LayoutValue(Invalidation.DrawSize);
        private readonly TournamentSpriteText status;

        internal string Status => status.Text.ToString();

        internal Color4 AccentColour { get; private set; }

        public RoomUserCard(MultiplayerRoomUser user)
        {
            UserId = user.UserID;
            apiUser = user.User;
            Masking = true;
            AddLayout(drawSizeLayout);

            InternalChild = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Children = new Drawable[]
                {
                    background = new TournamentBeatmapPanel.NoUnloadBeatmapSetCover
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = OsuColour.Gray(0.5f),
                    },
                    osuLogo = new TournamentOsuLogo
                    {
                        Origin = Anchor.Centre,
                        Anchor = Anchor.Centre,
                    },
                    userInfoContainer = new FillFlowContainer
                    {
                        Name = "User Info",
                        AutoSizeAxes = Axes.Both,
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                        Direction = FillDirection.Horizontal,
                        Spacing = new Vector2(8, 0),
                        Margin = new MarginPadding(4),
                        Children = new Drawable[]
                        {
                            new Container
                            {
                                Name = "User Avatar",
                                AutoSizeAxes = Axes.Both,
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Children = new Drawable[]
                                {
                                    // Runs under the avatar's left edge so its colour fills the rounded corners there. Two corner radii
                                    // wide so its own rounded right corners share their arc with the avatar's, leaving no gap.
                                    // Hidden until the avatar has loaded (see LoadComplete), so it never shows without the avatar over it.
                                    teamBarContainer = new Container
                                    {
                                        Width = team_bar_width + 2 * avatar_corner_radius,
                                        Height = avatar_size,
                                        Masking = true,
                                        CornerRadius = avatar_corner_radius,
                                        Alpha = 0,
                                        Child = teamBar = new Box { RelativeSizeAxes = Axes.Both },
                                    },
                                    avatarContainer = new Container
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
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(4),
                                Children = new Drawable[]
                                {
                                    new FillFlowContainer
                                    {
                                        Direction = FillDirection.Horizontal,
                                        AutoSizeAxes = Axes.Both,
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        Spacing = new Vector2(4),
                                        Children = new Drawable[]
                                        {
                                            new UpdateableFlag(user.User?.CountryCode ?? CountryCode.Unknown)
                                            {
                                                Anchor = Anchor.CentreLeft,
                                                Origin = Anchor.CentreLeft,
                                                Size = new Vector2(27, 18),
                                                Margin = new MarginPadding { Top = 2 } // eyeballed alignment with text
                                            },
                                            new TournamentSpriteText
                                            {
                                                Anchor = Anchor.CentreLeft,
                                                Origin = Anchor.CentreLeft,
                                                Text = user.User?.Username ?? $"User {user.UserID}",
                                                Font = OsuFont.Torus.With(size: 28, weight: FontWeight.Bold),
                                                ShadowColour = Color4.Black.Opacity(0.6f),
                                                ShadowOffset = new Vector2(0.04f),
                                            },
                                        }
                                    },
                                    status = new TournamentSpriteText
                                    {
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        Font = OsuFont.Torus.With(size: 20, weight: FontWeight.SemiBold),
                                    },
                                }
                            }
                        },
                    }
                }
            };

            UpdateFrom(user);
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

            // DrawableAvatar fetches its texture during load and fades itself in (over 300ms) on LoadComplete. The bar waits
            // for that to finish: while both are part-transparent, the bar shows through the avatar.
            LoadComponentAsync(new DrawableAvatar(apiUser), avatar =>
            {
                avatarContainer.Add(avatar);
                teamBarContainer.Delay(300).FadeIn(300, Easing.OutQuint);
            });
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

            (string text, Color4 colour) = GetStatus(user.State, user.BeatmapAvailability);
            status.Text = text;
            status.FadeColour(colour, 200, Easing.OutQuint);
        }

        // Static, as UpdateFrom first runs from the constructor (before dependency injection).
        private static readonly OsuColour colours = new OsuColour();

        /// <summary>
        /// The player's own download state takes priority (they can't ready up without the map), then their ready state.
        /// Colours match the multiplayer lobby's participant list (<see cref="osu.Game.Screens.OnlinePlay.Multiplayer.Participants.StateDisplay"/>).
        /// </summary>
        internal static (string text, Color4 colour) GetStatus(MultiplayerUserState state, BeatmapAvailability availability) => availability.State switch
        {
            DownloadState.NotDownloaded => ("No map", colours.RedLight),
            DownloadState.Downloading => ($"Downloading {availability.DownloadProgress ?? 0:0%}", colours.Blue),
            DownloadState.Importing => ("Importing", colours.Yellow),
            _ => state switch
            {
                MultiplayerUserState.Idle => ("Not ready", Color4.White),
                MultiplayerUserState.Ready => ("Ready", Color4Extensions.FromHex("#AADD00")),
                MultiplayerUserState.WaitingForLoad => ("Loading", colours.Yellow),
                MultiplayerUserState.Loaded or MultiplayerUserState.ReadyForGameplay => ("Loading", colours.YellowLight),
                MultiplayerUserState.Playing => ("Playing", colours.BlueLight),
                MultiplayerUserState.FinishedPlay or MultiplayerUserState.Results => ("Finished", colours.BlueLighter),
                _ => (string.Empty, Color4.White),
            },
        };

        protected override void Update()
        {
            base.Update();

            if (!drawSizeLayout.IsValid && osuLogo.SizeForFlow > 0)
            {
                osuLogo.Scale = new Vector2(osu_logo_scale * DrawHeight / osuLogo.SizeForFlow);
                userInfoContainer.Scale = new Vector2(Math.Min(DrawWidth / user_info_design_size.X, DrawHeight / user_info_design_size.Y));
                drawSizeLayout.Validate();
            }
        }
    }
}
