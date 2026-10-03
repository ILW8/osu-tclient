// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics.Containers;
using osu.Game.Online.Multiplayer;

namespace osu.Game.Tournament.Components
{
    /// <summary>
    /// Shows a card for each MP-room user at the grid slot their spectator tile will occupy,
    /// so the gameplay area isn't empty before the map starts.
    /// </summary>
    public partial class RoomUserCardGrid : CompositeDrawable
    {
        /// <summary>
        /// Which room users get a card, and in which slot. Before the map loads that's everyone not spectating
        /// (which excludes the tourney client itself). Once anyone is loading or playing it's only those users,
        /// so the cards line up exactly with the tiles <see cref="TournamentSpectatorScreen"/> is about to create.
        /// </summary>
        internal static Dictionary<int, int> CardSlots(
            IEnumerable<(int userId, MultiplayerUserState state, MatchUserState? matchState)> roomUsers,
            int playersPerTeam)
        {
            var users = roomUsers.ToList();

            Func<MultiplayerUserState, bool> include = users.Any(u => TournamentSpectatorScreen.IsParticipating(u.state))
                ? TournamentSpectatorScreen.IsParticipating
                : s => s != MultiplayerUserState.Spectating;

            return TournamentSpectatorScreen.SnapshotSlots(users, playersPerTeam, include);
        }
    }
}
