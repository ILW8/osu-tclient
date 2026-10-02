// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Humanizer;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Chat;
using osu.Game.Online.Multiplayer;
using osu.Game.Overlays.Chat;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;

namespace osu.Game.Tournament.Components
{
    public partial class TournamentMatchChatDisplay : StandAloneChatDisplay
    {
        private readonly Bindable<string> channelName = new Bindable<string>();

        private ChannelManager? manager;

        [Resolved]
        private LadderInfo ladderInfo { get; set; } = null!;

        [Resolved]
        private MultiplayerClient multiplayerClient { get; set; } = null!;

        public TournamentMatchChatDisplay()
        {
            RelativeSizeAxes = Axes.X;
            Height = 144;
            Anchor = Anchor.BottomLeft;
            Origin = Anchor.BottomLeft;

            CornerRadius = 0;
        }

        [BackgroundDependencyLoader]
        private void load(MatchIPCInfo ipc, IAPIProvider api)
        {
            AddInternal(manager = new ChannelManager(api));
            Channel.BindTo(manager.CurrentChannel);

            bool isMultiplayerSource = ipc is MultiplayerMatchIPCInfo;

            // Rolls aren't chat messages; the server broadcasts them as match events.
            if (isMultiplayerSource)
                multiplayerClient.MatchEvent += onMatchEvent;

            channelName.BindTo(ipc.ChatChannel);
            channelName.BindValueChanged(c =>
            {
                if (int.TryParse(c.OldValue, out int oldChannelId) && oldChannelId > 0)
                {
                    var joinedChannel = manager.JoinedChannels.SingleOrDefault(ch => ch.Id == oldChannelId);
                    if (joinedChannel != null)
                        manager.LeaveChannel(joinedChannel);
                }

                if (int.TryParse(c.NewValue, out int newChannelId) && newChannelId > 0)
                {
                    var channel = new Channel
                    {
                        Id = newChannelId,
                        // Multiplayer room channels are joined implicitly via SignalR when the
                        // MultiplayerClient joins the room; ChannelType.Multiplayer tells the
                        // ChannelManager to skip the REST JoinChannelRequest (which fails/duplicates).
                        Type = isMultiplayerSource ? ChannelType.Multiplayer : ChannelType.Public
                    };

                    // JoinChannel returns the manager's backing instance, which differs from ours if the
                    // channel was already joined (e.g. the room's websocket join landed first on a reconnect).
                    manager.CurrentChannel.Value = manager.JoinChannel(channel);
                }
            }, true);
        }

        private void onMatchEvent(MatchServerEvent ev)
        {
            switch (ev)
            {
                case RollEvent rollEvent:
                    var user = multiplayerClient.Room?.Users.SingleOrDefault(u => u.UserID == rollEvent.UserID)?.User ?? APIUser.UnknownUser(rollEvent.UserID);
                    string text = $"{user.Username} rolled {"point".ToQuantity(rollEvent.Result)} out of {rollEvent.Max}.";
                    Channel.Value?.AddNewMessages(new InfoMessage(text));
                    break;
            }
        }

        public void Expand() => this.FadeIn(300);

        public void Contract() => this.FadeOut(200);

        protected override ChatLine? CreateMessage(Message message)
        {
            if (message.Content.StartsWith("!mp", StringComparison.Ordinal))
                return null;

            return new MatchMessage(message, ladderInfo);
        }

        protected override StandAloneDrawableChannel CreateDrawableChannel(Channel channel) => new MatchChannel(channel);

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (multiplayerClient.IsNotNull())
                multiplayerClient.MatchEvent -= onMatchEvent;
        }

        public partial class MatchChannel : StandAloneDrawableChannel
        {
            public MatchChannel(Channel channel)
                : base(channel)
            {
                ScrollbarVisible = false;
            }
        }

        protected partial class MatchMessage : StandAloneMessage
        {
            public MatchMessage(Message message, LadderInfo info)
                : base(message)
            {
                if (info.CurrentMatch.Value is TournamentMatch match)
                {
                    if (match.Team1.Value?.Players.Any(u => u.OnlineID == Message.Sender.OnlineID) == true)
                        UsernameColour = TournamentGame.COLOUR_RED;
                    else if (match.Team2.Value?.Players.Any(u => u.OnlineID == Message.Sender.OnlineID) == true)
                        UsernameColour = TournamentGame.COLOUR_BLUE;
                }
            }
        }
    }
}
