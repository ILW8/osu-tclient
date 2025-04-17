// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.Containers;
using osu.Game.Online.Multiplayer;
using osuTK;

namespace osu.Game.Screens.OnlinePlay.Multiplayer.Participants
{
    public partial class ParticipantsList : CompositeDrawable
    {
        private FillFlowContainer<ParticipantPanel> panels = null!;
        private ParticipantPanel? currentHostPanel;

        [Resolved]
        private MultiplayerClient client { get; set; } = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChild = new OsuScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                ScrollbarVisible = false,
                Child = panels = new FillFlowContainer<ParticipantPanel>
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 2)
                }
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            client.RoomUpdated += onRoomUpdated;
            updateState();
        }

        private void onRoomUpdated() => Scheduler.AddOnce(updateState);

        private void updateState()
        {
            if (client.Room == null)
                panels.Clear();
            else
            {
                // Remove panels for users no longer in the room.
                foreach (var p in panels)
                {
                    // Note that we *must* use reference equality here, as this call is scheduled and a user may have left and joined since it was last run.
                    if (client.Room.Users.All(u => !ReferenceEquals(p.User, u)))
                        p.Expire();
                }

                // Add panels for all users new to the room.
                foreach (var user in client.Room.Users.Except(panels.Select(p => p.User)))
                    panels.Add(new ParticipantPanel(user));

                // move spectators to very bottom
                for (int i = client.Room.Users.Count - 1; i >= 0; i--)
                {
                    if (client.Room.Users[i].State != MultiplayerUserState.Spectating)
                        continue;

                    var user = client.Room.Users[i];
                    client.Room.Users.RemoveAt(i);
                    client.Room.Users.Add(user);
                }

                // don't want to deal with it at the moment.
                const bool disable_host_sorting = true;

// ReSharper disable HeuristicUnreachableCode
#pragma warning disable CS0162 // Unreachable code detected
                if (!disable_host_sorting)
                {
                    if (currentHostPanel == null || !currentHostPanel.User.Equals(client.Room.Host))
                    {
                        // Reset position of previous host back to normal, if one existing.
                        if (currentHostPanel != null && panels.Contains(currentHostPanel))
                            panels.SetLayoutPosition(currentHostPanel, 0);

                        currentHostPanel = null;

                        // Change position of new host to display above all participants.
                        if (client.Room.Host != null)
                        {
                            currentHostPanel = panels.SingleOrDefault(u => u.User.Equals(client.Room.Host));

                            if (currentHostPanel != null)
                                panels.SetLayoutPosition(currentHostPanel, -1);
                        }
                    }
                }
#pragma warning restore CS0162 // Unreachable code detected
// ReSharper restore HeuristicUnreachableCode

                // sort users
                foreach ((var roomUser, int listPosition) in client.Room.Users.Select((value, i) => (value, i)))
                {
                    var panel = panels.SingleOrDefault(u => u.User.Equals(roomUser));

                    if (panel != null)
                        panels.SetLayoutPosition(panel, listPosition);
                }
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (client.IsNotNull())
                client.RoomUpdated -= onRoomUpdated;
        }
    }
}
