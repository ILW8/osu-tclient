// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Online.Multiplayer;
using osu.Game.Tournament.Models;

namespace osu.Game.Tournament.Components
{
    /// <summary>
    /// Shows a <see cref="RoomUserCard"/> for each MP-room user at the grid slot their spectator tile will occupy,
    /// so the gameplay area isn't empty before the map starts. Sits below the spectator tiles; <see cref="HideCard"/>
    /// is called as each tile is added and <see cref="ShowAllCards"/> when the tiles are torn down.
    /// </summary>
    public partial class RoomUserCardGrid : CompositeDrawable
    {
        [Resolved]
        private MultiplayerClient multiplayerClient { get; set; } = null!;

        [Resolved]
        private LadderInfo ladder { get; set; } = null!;

        private readonly TournamentPlayerGrid grid;
        private readonly Dictionary<int, RoomUserCard> cards = new Dictionary<int, RoomUserCard>();
        private readonly HashSet<int> hiddenUsers = new HashSet<int>();
        private Dictionary<int, int> slots = new Dictionary<int, int>();
        private IBindable<int> playersPerTeam = null!;

        public RoomUserCardGrid()
        {
            InternalChild = grid = new TournamentPlayerGrid { RelativeSizeAxes = Axes.Both };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Same capacity rule as TournamentSpectatorScreen.VisibleSlotCount, so cards and tiles share bounds.
            playersPerTeam = ladder.PlayersPerTeam.GetBoundCopy();
            playersPerTeam.BindValueChanged(e =>
            {
                grid.Capacity.Value = Math.Clamp(e.NewValue * 2, TournamentPlayerGrid.MIN_SLOTS, TournamentPlayerGrid.MAX_SLOTS);
                Scheduler.AddOnce(updateCards);
            }, true);

            multiplayerClient.RoomUpdated += onRoomUpdated;
        }

        // AddOnce collapses bursts (download progress) into one update per frame. While the gameplay screen is
        // hidden this drawable isn't updated, so queued work runs once it's shown again.
        private void onRoomUpdated() => Scheduler.AddOnce(updateCards);

        /// <summary>
        /// Hides a user's card because their spectator tile is now showing over it. Stays hidden across rebuilds until <see cref="ShowAllCards"/>.
        /// </summary>
        public void HideCard(int userId)
        {
            hiddenUsers.Add(userId);

            if (cards.TryGetValue(userId, out var card))
                card.Alpha = 0;
        }

        /// <summary>
        /// Shows every card again, for when the spectator tiles are torn down.
        /// </summary>
        public void ShowAllCards()
        {
            hiddenUsers.Clear();

            foreach (var card in cards.Values)
                card.Alpha = 1;
        }

        private void updateCards()
        {
            var users = multiplayerClient.Room?.Users.ToArray() ?? Array.Empty<MultiplayerRoomUser>();
            var newSlots = CardSlots(users.Select(u => (u.UserID, u.State, u.MatchState)), grid.Capacity.Value / 2);

            // Rebuild only when someone joins, leaves, switches team or loading starts. Status changes update in place
            // so frequent download-progress updates don't recreate avatars.
            if (newSlots.Count != slots.Count || newSlots.Except(slots).Any())
            {
                slots = newSlots;
                grid.Clear();
                cards.Clear();

                foreach (var user in users)
                {
                    if (!slots.TryGetValue(user.UserID, out int slot) || slot >= TournamentPlayerGrid.MAX_SLOTS)
                        continue;

                    var card = new RoomUserCard(user);
                    cards[user.UserID] = card;
                    grid.Add(card, slot);
                }
            }

            foreach (var user in users)
            {
                if (!cards.TryGetValue(user.UserID, out var card))
                    continue;

                card.UpdateStatus(user);
                card.Alpha = hiddenUsers.Contains(user.UserID) ? 0 : 1;
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (multiplayerClient.IsNotNull())
                multiplayerClient.RoomUpdated -= onRoomUpdated;
        }

        /// <summary>
        /// Which room users get a card, and in which slot. Before the map loads that's everyone not spectating
        /// (which excludes the tourney client itself). Once anyone is loading or playing, Idle users (subs sitting out)
        /// drop out, so the cards line up with the tiles <see cref="TournamentSpectatorScreen"/> is about to create.
        /// Ready users are kept: their switch to loading can arrive a frame or two after the first user's, and
        /// dropping them in between would rebuild every card.
        /// </summary>
        internal static Dictionary<int, int> CardSlots(
            IEnumerable<(int userId, MultiplayerUserState state, MatchUserState? matchState)> roomUsers,
            int playersPerTeam)
        {
            var users = roomUsers.ToList();

            Func<MultiplayerUserState, bool> include = users.Any(u => TournamentSpectatorScreen.IsParticipating(u.state))
                ? s => s != MultiplayerUserState.Idle && s != MultiplayerUserState.Spectating
                : s => s != MultiplayerUserState.Spectating;

            return TournamentSpectatorScreen.SnapshotSlots(users, playersPerTeam, include);
        }
    }
}
