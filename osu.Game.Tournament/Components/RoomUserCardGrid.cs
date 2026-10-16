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
using osuTK;

namespace osu.Game.Tournament.Components
{
    /// <summary>
    /// Shows a <see cref="RoomSlotBackground"/> in every slot and a <see cref="RoomUserCard"/> over it for each MP-room user
    /// at the grid slot their spectator tile will occupy, so the gameplay area isn't empty before the map starts, nor where
    /// a slot has no player. Sits below the spectator tiles; <see cref="HideCard"/> is called as each tile finishes fading
    /// in over its card and <see cref="ShowAllCards"/> when the tiles are torn down.
    /// </summary>
    public partial class RoomUserCardGrid : CompositeDrawable
    {
        [Resolved]
        private MultiplayerClient multiplayerClient { get; set; } = null!;

        [Resolved]
        private LadderInfo ladder { get; set; } = null!;

        private readonly RoomSlotBackground[] backgrounds;
        private readonly Dictionary<int, RoomUserCard> cards = new Dictionary<int, RoomUserCard>();
        private readonly HashSet<int> hiddenUsers = new HashSet<int>();
        private IBindable<int> playersPerTeam = null!;

        // Same capacity rule as TournamentSpectatorScreen.VisibleSlotCount, so cards and tiles share bounds.
        private int capacity => Math.Clamp(playersPerTeam.Value * 2, TournamentPlayerGrid.MIN_SLOTS, TournamentPlayerGrid.MAX_SLOTS);

        public RoomUserCardGrid()
        {
            InternalChildren = backgrounds = Enumerable.Range(0, TournamentPlayerGrid.MAX_SLOTS).Select(_ => new RoomSlotBackground { Depth = 1 }).ToArray();
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            playersPerTeam = ladder.PlayersPerTeam.GetBoundCopy();
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            playersPerTeam.BindValueChanged(_ => Scheduler.AddOnce(updateCards), true);

            multiplayerClient.RoomUpdated += onRoomUpdated;
        }

        // AddOnce collapses bursts (download progress) into one update per frame. While the gameplay screen is
        // hidden this drawable isn't updated, so queued work runs once it's shown again.
        private void onRoomUpdated() => Scheduler.AddOnce(updateCards);

        /// <summary>
        /// Hides a user's card because their spectator tile is now showing over it. Stays hidden until <see cref="ShowAllCards"/>.
        /// </summary>
        public void HideCard(int userId)
        {
            hiddenUsers.Add(userId);

            // Immediately rather than on the next Update; the tile already covers it.
            updateVisibility();
        }

        /// <summary>
        /// Shows every card again, for when the spectator tiles are torn down.
        /// </summary>
        public void ShowAllCards()
        {
            hiddenUsers.Clear();
            updateVisibility();
        }

        // Cards are kept across room updates and only moved when their slot changes, so a join or leave only adds or
        // removes that user's card instead of recreating (and reloading the avatars of) everyone's.
        private void updateCards()
        {
            // Neither the tourney client's own user nor referees ever get a card. The tourney client joins as a spectator,
            // but a host abort resets every user on the server to Idle, spectators included, so the Spectating filter
            // alone doesn't exclude it. Referees can't play and sit in the room as Idle.
            int? localUserId = multiplayerClient.LocalUser?.UserID;
            var users = multiplayerClient.Room?.Users.Where(u => u.UserID != localUserId && u.Role != MultiplayerRoomUserRole.Referee).ToArray()
                        ?? Array.Empty<MultiplayerRoomUser>();
            var slots = CardSlots(users.Select(u => (u.UserID, u.State, u.MatchState)), capacity / 2)
                        .Where(s => s.Value < TournamentPlayerGrid.MAX_SLOTS)
                        .ToDictionary(s => s.Key, s => s.Value);

            foreach (int userId in cards.Keys.Except(slots.Keys).ToArray())
            {
                RemoveInternal(cards[userId], true);
                cards.Remove(userId);
            }

            foreach (var user in users)
            {
                if (!slots.TryGetValue(user.UserID, out int slot))
                    continue;

                if (!cards.TryGetValue(user.UserID, out var card))
                    AddInternal(cards[user.UserID] = card = new RoomUserCard(user));

                card.Slot = slot;
                card.UpdateFrom(user);
            }
        }

        protected override void Update()
        {
            base.Update();

            // Same layout as TournamentPlayerGrid, which positions the tiles.
            int perTeam = capacity / 2;

            for (int i = 0; i < backgrounds.Length; i++)
                layout(backgrounds[i], i, perTeam);

            foreach (var card in cards.Values)
                layout(card, card.Slot, perTeam);

            updateVisibility();
        }

        private void layout(Drawable drawable, int slot, int perTeam)
        {
            var bounds = TournamentPlayerGrid.SlotBounds(slot, perTeam);

            drawable.Position = new Vector2(bounds.X * DrawWidth, bounds.Y * DrawHeight);
            drawable.Size = new Vector2(bounds.Width * DrawWidth, bounds.Height * DrawHeight);
        }

        private void updateVisibility()
        {
            foreach (var card in cards.Values)
                card.Alpha = hiddenUsers.Contains(card.UserId) || card.Slot >= capacity ? 0 : 1;

            for (int i = 0; i < backgrounds.Length; i++)
            {
                int slot = i;
                var card = cards.Values.FirstOrDefault(c => c.Slot == slot);

                backgrounds[i].Alpha = slot >= capacity || (card != null && hiddenUsers.Contains(card.UserId)) ? 0 : 1;
                backgrounds[i].Occupied = card != null;
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
        /// dropping them in between would remove and re-add their cards.
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
