// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using JetBrains.Annotations;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Overlays.Chat;
using osu.Game.Resources.Localisation.Web;
using osu.Game.TournamentIpc;
using osu.Game.Screens.OnlinePlay;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Game.Online.Chat
{
    /// <summary>
    /// Display a chat channel in an insolated region.
    /// </summary>
    public partial class StandAloneChatDisplay : CompositeDrawable
    {
        [Cached]
        public readonly Bindable<Channel> Channel = new Bindable<Channel>();

        [Resolved(canBeNull: true)]
        [CanBeNull]
        protected TournamentFileBasedIPC TournamentIpc { get; private set; }

        [Resolved]
        protected MultiplayerClient Client { get; private set; }

        private BeatmapModelDownloader beatmapsDownloader = null!;

        private BeatmapLookupCache beatmapLookupCache = null!;

        private BeatmapDownloadTracker beatmapDownloadTracker = null!;

        private IDisposable selectionOperation;

        [Resolved]
        private OngoingOperationTracker operationTracker { get; set; } = null!;

        [Resolved(typeof(Room), nameof(Room.Playlist), canBeNull: true)]
        private BindableList<PlaylistItem> roomPlaylist { get; set; }

        protected readonly ChatTextBox TextBox;

        private ChannelManager channelManager;

        private StandAloneDrawableChannel drawableChannel;

        private readonly bool postingTextBox;

        protected readonly Box Background;

        private const float text_box_height = 30;

        /// <summary>
        /// Construct a new instance.
        /// </summary>
        /// <param name="postingTextBox">Whether a textbox for posting new messages should be displayed.</param>
        public StandAloneChatDisplay(bool postingTextBox = false)
        {
            const float corner_radius = 10;

            this.postingTextBox = postingTextBox;
            CornerRadius = corner_radius;
            Masking = true;

            InternalChildren = new Drawable[]
            {
                Background = new Box
                {
                    Colour = Color4.Black,
                    Alpha = 0.8f,
                    RelativeSizeAxes = Axes.Both
                },
            };

            if (postingTextBox)
            {
                AddInternal(TextBox = new ChatTextBox
                {
                    RelativeSizeAxes = Axes.X,
                    Height = text_box_height,
                    PlaceholderText = ChatStrings.InputPlaceholder,
                    CornerRadius = corner_radius,
                    ReleaseFocusOnCommit = false,
                    HoldFocus = true,
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                });

                TextBox.OnCommit += postMessage;
            }

            Channel.BindValueChanged(channelChanged);
        }

        [BackgroundDependencyLoader(true)]
        private void load(ChannelManager manager, BeatmapModelDownloader beatmaps, BeatmapLookupCache beatmapsCache)
        {
            channelManager ??= manager;
            beatmapsDownloader = beatmaps;
            beatmapLookupCache = beatmapsCache;

            AddInternal(beatmapDownloadTracker = new BeatmapDownloadTracker(new BeatmapSetInfo()));
        }

        protected virtual StandAloneDrawableChannel CreateDrawableChannel(Channel channel) =>
            new StandAloneDrawableChannel(channel);

        private void postMessage(TextBox sender, bool newText)
        {
            string text = TextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(text))
                return;

            if (text[0] == '/')
                channelManager?.PostCommand(text.Substring(1), Channel.Value);
            else
            {
                channelManager?.PostMessage(text, target: Channel.Value);

                string[] parts = text.Split();

                if (parts.Length == 3 && parts[0] == @"!mp" && parts[1] == @"map" && int.TryParse(parts[2], out int onlineID))
                {
                    beatmapLookupCache.GetBeatmapAsync(onlineID).ContinueWith(task => Schedule(() =>
                    {
                        APIBeatmap beatmapInfo = task.GetResultSafely();

                        if (beatmapInfo?.BeatmapSet == null) return;

                        RemoveInternal(beatmapDownloadTracker, true);
                        AddInternal(beatmapDownloadTracker = new BeatmapDownloadTracker(beatmapInfo.BeatmapSet));

                        beatmapDownloadTracker.State.BindValueChanged(changeEvent =>
                        {
                            switch (changeEvent.NewValue)
                            {
                                case DownloadState.LocallyAvailable:
                                    addPlaylistItem(beatmapInfo);
                                    return;

                                case DownloadState.NotDownloaded:
                                    beatmapsDownloader.Download(beatmapInfo.BeatmapSet);
                                    break;
                            }
                        });
                    }));
                }
            }

            TextBox.Text = string.Empty;
        }

        private void addPlaylistItem(APIBeatmap beatmapInfo)
        {
            // ensure user is host
            if (!Client.IsHost)
                return;

            selectionOperation = operationTracker.BeginOperation();

            var item = new PlaylistItem(beatmapInfo)
            {
                RulesetID = beatmapInfo.Ruleset.OnlineID,
                RequiredMods = Array.Empty<APIMod>(),
                AllowedMods = Array.Empty<APIMod>()
            };

            // PlaylistItem item
            var multiplayerItem = new MultiplayerPlaylistItem
            {
                ID = 0,
                BeatmapID = item.Beatmap.OnlineID,
                BeatmapChecksum = item.Beatmap.MD5Hash,
                RulesetID = item.RulesetID,
                RequiredMods = item.RequiredMods,
                AllowedMods = item.AllowedMods
            };

            var itemsToRemove = roomPlaylist?.ToArray() ?? Array.Empty<PlaylistItem>();
            Task addPlaylistItemTask = Client.AddPlaylistItem(multiplayerItem);

            addPlaylistItemTask.FireAndForget(onSuccess: () =>
            {
                selectionOperation?.Dispose();

                foreach (var playlistItem in itemsToRemove)
                    Client.RemovePlaylistItem(playlistItem.ID).FireAndForget();
            }, onError: _ =>
            {
                selectionOperation?.Dispose();
            });
        }

        protected virtual ChatLine CreateMessage(Message message)
        {
            TournamentIpc?.AddChatMessage(message);
            return new StandAloneMessage(message);
        }

        private void channelChanged(ValueChangedEvent<Channel> e)
        {
            drawableChannel?.Expire();

            if (e.OldValue != null)
                TextBox?.Current.UnbindFrom(e.OldValue.TextBoxMessage);

            if (e.NewValue == null) return;

            TextBox?.Current.BindTo(e.NewValue.TextBoxMessage);

            TournamentIpc?.ClearChatMessages();
            drawableChannel = CreateDrawableChannel(e.NewValue);
            drawableChannel.CreateChatLineAction = CreateMessage;
            drawableChannel.Padding = new MarginPadding { Bottom = postingTextBox ? text_box_height : 0 };

            AddInternal(drawableChannel);
        }

        public partial class ChatTextBox : HistoryTextBox
        {
            protected override bool OnKeyDown(KeyDownEvent e)
            {
                // Chat text boxes are generally used in places where they retain focus, but shouldn't block interaction with other
                // elements on the same screen.
                if (!HoldFocus)
                {
                    switch (e.Key)
                    {
                        case Key.Up:
                        case Key.Down:
                            return false;
                    }
                }

                return base.OnKeyDown(e);
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();

                BackgroundUnfocused = new Color4(10, 10, 10, 10);
                BackgroundFocused = new Color4(10, 10, 10, 255);
            }

            protected override void OnFocusLost(FocusLostEvent e)
            {
                base.OnFocusLost(e);
                FocusLost?.Invoke();
            }

            public Action FocusLost;
        }

        public partial class StandAloneDrawableChannel : DrawableChannel
        {
            public Func<Message, ChatLine> CreateChatLineAction;

            public StandAloneDrawableChannel(Channel channel)
                : base(channel)
            {
            }

            protected override ChatLine CreateChatLine(Message m) => CreateChatLineAction(m);

            protected override DaySeparator CreateDaySeparator(DateTimeOffset time) => new StandAloneDaySeparator(time);
        }

        protected partial class StandAloneDaySeparator : DaySeparator
        {
            protected override float TextSize => 14;
            protected override float LineHeight => 1;
            protected override float Spacing => 5;
            protected override float DateAlign => 125;

            public StandAloneDaySeparator(DateTimeOffset date)
                : base(date)
            {
            }

            [BackgroundDependencyLoader]
            private void load(OsuColour colours)
            {
                Height = 25;
                Colour = colours.Yellow;
            }
        }

        protected partial class StandAloneMessage : ChatLine
        {
            protected override float FontSize => 15;
            protected override float Spacing => 5;
            protected override float UsernameWidth => 75;

            public StandAloneMessage(Message message)
                : base(message)
            {
            }
        }
    }
}
