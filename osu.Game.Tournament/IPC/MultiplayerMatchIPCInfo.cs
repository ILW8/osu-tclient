// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets;
using osu.Game.Tournament.Models;
using LogLevel = osu.Framework.Logging.LogLevel;

namespace osu.Game.Tournament.IPC
{
    /// <summary>
    /// A <see cref="MatchIPCInfo"/> implementation that sources match data directly from a live
    /// multiplayer room via <see cref="MultiplayerClient"/>, replacing the file-based IPC bridge
    /// used with the stable client.
    ///
    /// This is a pure connection / data source: it joins a room as a spectator, mirrors the room's
    /// current beatmap / mods / chat channel onto the <see cref="MatchIPCInfo"/> bindables, and
    /// exposes connection state plus a <see cref="HasActiveSpectatorPlayers"/> signal. It does not
    /// render gameplay (that is the <c>TournamentSpectatorScreen</c>), derive team scores, or write
    /// any IPC snapshot.
    /// </summary>
    public partial class MultiplayerMatchIPCInfo : MatchIPCInfo
    {
        /// <summary>
        /// Delay before <see cref="TourneyState.Ranking"/> auto-resets back to <see cref="TourneyState.Idle"/>.
        /// A multiplayer room has no natural "returned to lobby" event, so anything gated on
        /// <c>State == Idle</c> (e.g. the gameplay-screen auto-advance) hangs off this timer.
        /// </summary>
        public const double RANKING_TO_IDLE_DELAY_MS = 20_000;

        /// <summary>
        /// Whether this client is currently connected to a multiplayer room.
        /// </summary>
        public IBindable<bool> IsConnected => isConnected;

        private readonly Bindable<bool> isConnected = new Bindable<bool>();

        /// <summary>
        /// The currently connected room ID, or null if not connected.
        /// </summary>
        public IBindable<long?> ConnectedRoomId => connectedRoomId;

        private readonly Bindable<long?> connectedRoomId = new Bindable<long?>();

        /// <summary>
        /// A user-facing error message from the last failed connection attempt, or null if none.
        /// </summary>
        public IBindable<string?> ConnectionError => connectionError;

        private readonly Bindable<string?> connectionError = new Bindable<string?>();

        /// <summary>
        /// A pending room invitation awaiting operator approval, or null if none.
        /// </summary>
        public IBindable<PendingInvite?> PendingInvite => pendingInvite;

        private readonly Bindable<PendingInvite?> pendingInvite = new Bindable<PendingInvite?>();

        /// <summary>
        /// <c>true</c> while at least one room user is in <see cref="MultiplayerUserState.Playing"/>.
        /// Derived from room state on this always-alive component, so the signal fires reliably
        /// regardless of which screen is currently visible.
        /// </summary>
        public IBindable<bool> HasActiveSpectatorPlayers => hasActiveSpectatorPlayers;

        private readonly Bindable<bool> hasActiveSpectatorPlayers = new Bindable<bool>();

        /// <summary>
        /// The user IDs currently participating in the round (used to construct a spectating display).
        /// Read on the update thread.
        /// </summary>
        public IReadOnlyList<int> CurrentParticipants =>
            multiplayerClient.Room?.Users.Where(u => isParticipatingInCurrentRound(u.State)).Select(u => u.UserID).ToArray()
            ?? Array.Empty<int>();

        [Resolved]
        private MultiplayerClient multiplayerClient { get; set; } = null!;

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        [Resolved]
        private IRulesetStore rulesetStore { get; set; } = null!;

        [Resolved]
        private BeatmapLookupCache beatmapLookupCache { get; set; } = null!;

        [Resolved]
        private BeatmapManager beatmapManager { get; set; } = null!;

        [Resolved]
        private BeatmapModelDownloader beatmapDownloader { get; set; } = null!;

        private string? connectedRoomPassword;
        private int lastBeatmapId;
        private ScheduledDelegate? scheduledRankingReset;

        private void recomputeHasActiveSpectatorPlayers()
        {
            // Room state, not spectatorClient.WatchedUserStates — the per-map TournamentSpectatorScreen
            // shares that watch and tears it down between maps, dropping the second map's Playing state.
            bool anyPlaying = multiplayerClient.Room?.Users.Any(u => u.State == MultiplayerUserState.Playing) == true;

            if (hasActiveSpectatorPlayers.Value != anyPlaying)
                hasActiveSpectatorPlayers.Value = anyPlaying;
        }

        /// <summary>
        /// Connects to a multiplayer room as a spectator.
        /// </summary>
        /// <param name="roomId">The room ID to connect to.</param>
        /// <param name="password">The room password, or null if the room has no password.</param>
        public async Task Connect(long roomId, string? password = null)
        {
            Logger.Log($"[MultiplayerMatchIPCInfo] Connecting to room {roomId}", LoggingTarget.Network);

            if (isConnected.Value)
                await Disconnect().ConfigureAwait(false);

            try
            {
                // Fetch room details from the API (can run on any thread).
                var getRoomRequest = new GetRoomRequest(roomId);
                await api.PerformAsync(getRoomRequest).ConfigureAwait(false);

                var apiRoom = getRoomRequest.Response;

                if (apiRoom == null)
                {
                    Logger.Log($"[MultiplayerMatchIPCInfo] Failed to fetch room {roomId}", LoggingTarget.Network, LogLevel.Error);
                    return;
                }

                apiRoom.RoomID = roomId;

                // JoinRoom and ToggleSpectate access MultiplayerClient.Room which asserts the update
                // thread. Schedule each call separately to guarantee both start on the update thread
                // (JoinRoom uses ConfigureAwait(false) internally, so the continuation after the first
                // await would otherwise land on a thread-pool thread).
                Schedule(() => connectionError.Value = null);
                await scheduleOnUpdateThread(() => multiplayerClient.JoinRoom(apiRoom, password)).ConfigureAwait(false);

                // Switch to spectator mode. Non-fatal if the server rejects it.
                try
                {
                    await scheduleOnUpdateThread(() => multiplayerClient.ToggleSpectate()).ConfigureAwait(false);
                }
                catch (Exception toggleEx)
                {
                    Logger.Log($"[MultiplayerMatchIPCInfo] Failed to toggle spectate (continuing): {toggleEx.GetType().Name}: {toggleEx.Message}",
                        LoggingTarget.Network, LogLevel.Important);
                }

                // Subscribe to events (safe from any thread — just delegate additions).
                multiplayerClient.RoomUpdated += onRoomUpdated;
                multiplayerClient.LoadRequested += onLoadRequested;
                multiplayerClient.GameplayStarted += onGameplayStarted;
                multiplayerClient.ResultsReady += onResultsReady;
                multiplayerClient.UserStateChanged += onUserStateChanged;
                multiplayerClient.GameplayAborted += onGameplayAborted;

                Schedule(() =>
                {
                    if (multiplayerClient.Room != null)
                    {
                        updateBeatmapFromRoom();
                        updateModsFromRoom();
                        updateChatChannelFromRoom();
                    }

                    // Seed the signal for a join-mid-gameplay.
                    recomputeHasActiveSpectatorPlayers();

                    // Seed State from the room snapshot so lobby-gated consumers react immediately
                    // rather than waiting for the first state-transition event (see DeriveState).
                    State.Value = DeriveState(multiplayerClient.Room?.Users.Select(u => u.State) ?? Enumerable.Empty<MultiplayerUserState>());

                    connectedRoomId.Value = roomId;
                    connectedRoomPassword = password;
                    isConnected.Value = true;

                    Logger.Log($"[MultiplayerMatchIPCInfo] Connected to room {roomId}", LoggingTarget.Network);
                });
            }
            catch (Exception e)
            {
                Logger.Log($"[MultiplayerMatchIPCInfo] Failed to connect to room {roomId}: {e.GetType().Name}: {e.Message}", LoggingTarget.Network, LogLevel.Error);

                string errorMessage = e.ToString().Contains("InvalidPasswordException", StringComparison.Ordinal)
                    ? "Invalid password"
                    : $"Failed to connect: {e.InnerException?.Message ?? e.Message}";

                Schedule(() => connectionError.Value = errorMessage);
                await Disconnect().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Disconnects from the current room (if connected) and connects again after a delay.
        /// </summary>
        /// <param name="delayMilliseconds">How long to wait before reconnecting</param>
        /// <param name="roomId">Room ID to reconnect to. Leave null to reuse the current room ID</param>
        /// <param name="password">Password to use when reconnecting. Leave null to reuse the current password</param>
        public async Task Reconnect(int delayMilliseconds = 500, long? roomId = null, string? password = null)
        {
            if (IsConnected.Value)
                Logger.Log($"[Reconnect] Disconnecting from {connectedRoomId.Value}");

            roomId ??= connectedRoomId.Value;
            password ??= connectedRoomPassword;

            Logger.Log($"[Reconnect] Connecting to {roomId}");

            if (roomId == null)
            {
                Schedule(() => connectionError.Value = "No room to reconnect to");
                return;
            }

            await Disconnect().ConfigureAwait(false);
            await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            await Connect(roomId.Value, password).ConfigureAwait(false);
        }

        /// <summary>
        /// Disconnects from the current multiplayer room.
        /// </summary>
        public async Task Disconnect()
        {
            multiplayerClient.RoomUpdated -= onRoomUpdated;
            multiplayerClient.LoadRequested -= onLoadRequested;
            multiplayerClient.GameplayStarted -= onGameplayStarted;
            multiplayerClient.ResultsReady -= onResultsReady;
            multiplayerClient.UserStateChanged -= onUserStateChanged;
            multiplayerClient.GameplayAborted -= onGameplayAborted;

            try
            {
                if (multiplayerClient.Room != null)
                    await multiplayerClient.LeaveRoom().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.Log($"[MultiplayerMatchIPCInfo] Error leaving room: {e.GetType().Name}: {e.Message}", LoggingTarget.Network, LogLevel.Error);
            }

            Schedule(() =>
            {
                cancelScheduledRankingReset();

                isConnected.Value = false;
                connectedRoomId.Value = null;
                connectedRoomPassword = null;
                lastBeatmapId = 0;

                // Room is gone — drop the signal to false.
                recomputeHasActiveSpectatorPlayers();

                // Reset the inherited MatchIPCInfo bindables to defaults.
                Beatmap.Value = null;
                Mods.Value = LegacyMods.None;
                State.Value = TourneyState.Idle;
                ChatChannel.Value = string.Empty;
                Score1.Value = 0;
                Score2.Value = 0;

                Logger.Log("[MultiplayerMatchIPCInfo] Disconnected from room", LoggingTarget.Network);
            });
        }

        /// <summary>
        /// Stores an incoming room invitation for the operator to accept or dismiss on the Update thread
        /// </summary>
        public void SetPendingInvite(PendingInvite invite) => Schedule(() => pendingInvite.Value = invite);

        /// <summary>
        /// Accepts the pending invite and connects to its room as a spectator.
        /// </summary>
        public void AcceptPendingInvite()
        {
            var invite = pendingInvite.Value;

            if (invite == null)
                return;

            pendingInvite.Value = null;
            Connect(invite.RoomId, invite.Password).FireAndForget();
        }

        /// <summary>
        /// Discards the pending invite without connecting.
        /// </summary>
        public void DismissPendingInvite() => pendingInvite.Value = null;

        /// <summary>
        /// Schedules an async operation to start on the update thread and returns a task that
        /// completes when the operation finishes.
        /// </summary>
        private Task scheduleOnUpdateThread(Func<Task> action)
        {
            var tcs = new TaskCompletionSource<bool>();

            Schedule(async () =>
            {
                try
                {
                    await action().ConfigureAwait(false);
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        #region Multiplayer event handlers

        private void onRoomUpdated()
        {
            Schedule(() =>
            {
                updateBeatmapFromRoom();
                updateModsFromRoom();
                updateChatChannelFromRoom();
                recomputeHasActiveSpectatorPlayers();
            });
        }

        private void onLoadRequested()
        {
            Schedule(() =>
            {
                // if LoadRequested fires while a player is still mid-map, ignore it (joining game mid-map)
                if (multiplayerClient.Room?.Users.Any(u => u.State == MultiplayerUserState.Playing) == true)
                {
                    Logger.Log("[MultiplayerMatchIPCInfo] onLoadRequested ignored: players already mid-map (join-into-live-game)");
                    return;
                }

                Logger.Log($"[MultiplayerMatchIPCInfo] onLoadRequested: state {State.Value} -> WaitingForClients");

                cancelScheduledRankingReset();
                State.Value = TourneyState.WaitingForClients;

                // Clear the previous map's totals; TournamentSpectatorScreen re-derives them once players start.
                Score1.Value = 0;
                Score2.Value = 0;
            });
        }

        private void onGameplayStarted()
        {
            Schedule(() =>
            {
                Logger.Log($"[MultiplayerMatchIPCInfo] onGameplayStarted: state {State.Value} -> Playing", LoggingTarget.Runtime);
                State.Value = TourneyState.Playing;
            });
        }

        private void onResultsReady()
        {
            Schedule(() =>
            {
                Logger.Log($"[MultiplayerMatchIPCInfo] onResultsReady: state {State.Value} -> Ranking (Idle reset in {RANKING_TO_IDLE_DELAY_MS}ms)", LoggingTarget.Runtime);

                State.Value = TourneyState.Ranking;

                // Auto-restore Idle so consumers gated on lobby state (gameplay-screen auto-advance)
                // get a deterministic "results have been shown long enough, room is back in lobby" signal.
                cancelScheduledRankingReset();
                scheduledRankingReset = Scheduler.AddDelayed(() =>
                {
                    if (State.Value == TourneyState.Ranking)
                        State.Value = TourneyState.Idle;
                }, RANKING_TO_IDLE_DELAY_MS);
            });
        }

        private void onGameplayAborted(GameplayAbortReason reason)
        {
            Schedule(() =>
            {
                Logger.Log($"[MultiplayerMatchIPCInfo] onGameplayAborted({reason}): state {State.Value} -> Idle", LoggingTarget.Runtime);
                cancelScheduledRankingReset();
                State.Value = TourneyState.Idle;
            });
        }

        private void cancelScheduledRankingReset()
        {
            scheduledRankingReset?.Cancel();
            scheduledRankingReset = null;
        }

        // Room state drives the push signal, so re-derive on every user-state change.
        private void onUserStateChanged(MultiplayerRoomUser user, MultiplayerUserState state) => Schedule(recomputeHasActiveSpectatorPlayers);

        private static bool isParticipatingInCurrentRound(MultiplayerUserState state)
            => state == MultiplayerUserState.WaitingForLoad
               || state == MultiplayerUserState.Loaded
               || state == MultiplayerUserState.ReadyForGameplay
               || state == MultiplayerUserState.Playing;

        /// <summary>
        /// Derives the tourney state from a room's current user states, for seeding
        /// <see cref="MatchIPCInfo.State"/> on connect. The per-round events
        /// (<see cref="onLoadRequested"/> etc.) only fire on transitions, so without this a room
        /// already sitting in the lobby when we join would stay at the Initialising default and
        /// nothing gated on <c>State == Idle</c> (lobby music, gameplay-screen auto-advance) would react.
        /// </summary>
        internal static TourneyState DeriveState(IEnumerable<MultiplayerUserState> states)
        {
            var list = states.ToList();

            if (list.Any(s => s == MultiplayerUserState.Playing))
                return TourneyState.Playing;

            // Someone is loading into the map but nobody is playing yet.
            if (list.Any(isParticipatingInCurrentRound))
                return TourneyState.WaitingForClients;

            return TourneyState.Idle;
        }

        #endregion

        #region Data mapping

        private void updateBeatmapFromRoom()
        {
            if (multiplayerClient.Room == null)
                return;

            // Guard explicitly rather than catching CurrentPlaylistItem's throw, which would mask real errors.
            if (multiplayerClient.Room.Playlist.Count == 0)
                return;

            int beatmapId = multiplayerClient.Room.CurrentPlaylistItem.BeatmapID;

            if (beatmapId == lastBeatmapId)
                return;

            lastBeatmapId = beatmapId;

            // Resolve from the beatmap ID alone (no tournament round-pool dependency). The API
            // lookup populates the SongBar; ensureBeatmapDownloaded makes the beatmap available
            // locally so the spectating display can render it.
            Task.Run(async () =>
            {
                var apiBeatmap = await beatmapLookupCache.GetBeatmapAsync(beatmapId).ConfigureAwait(false);

                if (apiBeatmap == null)
                    return;

                Schedule(() =>
                {
                    if (lastBeatmapId != beatmapId)
                        return;

                    Beatmap.Value = new TournamentBeatmap(apiBeatmap);

                    // Host picked the next map while results were showing: the round is over, so drop to
                    // Idle now rather than waiting out the fallback timer. Otherwise the later
                    // Ranking -> Idle bounces the gameplay screen back to the map pool after it has
                    // already advanced past it for the new pick.
                    if (State.Value == TourneyState.Ranking)
                    {
                        cancelScheduledRankingReset();
                        State.Value = TourneyState.Idle;
                    }
                });

                ensureBeatmapDownloaded(apiBeatmap);
            });
        }

        /// <summary>
        /// Downloads a beatmap set if it is not already available locally or being downloaded.
        /// </summary>
        private void ensureBeatmapDownloaded(APIBeatmap apiBeatmap)
        {
            Schedule(() =>
            {
                if (beatmapManager.QueryBeatmap(b => b.OnlineID == apiBeatmap.OnlineID) != null)
                    return;

                var beatmapSet = apiBeatmap.BeatmapSet;

                if (beatmapSet == null)
                {
                    Logger.Log($"[MultiplayerMatchIPCInfo] Cannot download beatmap {apiBeatmap.OnlineID}: no beatmap set info", LoggingTarget.Network, LogLevel.Important);
                    return;
                }

                if (beatmapDownloader.GetExistingDownload(beatmapSet) != null)
                    return;

                Logger.Log($"[MultiplayerMatchIPCInfo] Downloading beatmap set {beatmapSet.OnlineID}", LoggingTarget.Network);
                beatmapDownloader.Download(beatmapSet);
            });
        }

        private void updateModsFromRoom()
        {
            if (multiplayerClient.Room == null)
                return;

            if (multiplayerClient.Room.Playlist.Count == 0)
                return;

            var currentItem = multiplayerClient.Room.CurrentPlaylistItem;
            var rulesetInfo = rulesetStore.GetRuleset(currentItem.RulesetID);

            if (rulesetInfo == null)
                return;

            var ruleset = rulesetInfo.CreateInstance();
            var mods = currentItem.RequiredMods.Select(m => m.ToMod(ruleset)).ToArray();
            Mods.Value = ruleset.ConvertToLegacyMods(mods);
        }

        private void updateChatChannelFromRoom()
        {
            if (multiplayerClient.Room == null)
                return;

            // Raw numeric ChannelID (no "#mp_" prefix). The chat display joins it as
            // ChannelType.Multiplayer, skipping the REST JoinChannelRequest which fails/duplicates
            // for implicitly-joined room channels.
            ChatChannel.Value = multiplayerClient.Room.ChannelID.ToString();
        }

        #endregion

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (multiplayerClient.IsNotNull())
            {
                multiplayerClient.RoomUpdated -= onRoomUpdated;
                multiplayerClient.LoadRequested -= onLoadRequested;
                multiplayerClient.GameplayStarted -= onGameplayStarted;
                multiplayerClient.ResultsReady -= onResultsReady;
                multiplayerClient.UserStateChanged -= onUserStateChanged;
                multiplayerClient.GameplayAborted -= onGameplayAborted;
            }
        }
    }

    /// <summary>
    /// A multiplayer room invitation awaiting operator approval
    /// </summary>
    public record PendingInvite(long RoomId, string? Password, string RoomName);
}
