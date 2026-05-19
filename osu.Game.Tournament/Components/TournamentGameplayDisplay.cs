// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Spectator;
using osu.Game.Replays;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using osu.Game.Screens.OnlinePlay.Multiplayer.Spectate;
using osu.Game.Screens.Play;
using osu.Game.Screens.Spectate;
using osu.Game.Tournament.IPC;
using Realms;

namespace osu.Game.Tournament.Components
{
    /// <summary>
    /// Embeds actual gameplay rendering from a multiplayer room into the tournament overlay.
    /// Arranges 2–8 <see cref="PlayerArea"/> instances in a <see cref="TournamentPlayerGrid"/>,
    /// with a runtime <see cref="VisibleSlotCount"/> cap and a one-time snapshot of
    /// <c>room.Users</c> fixing each user's slot index for the match.
    /// Manages the spectator lifecycle: watches user states, resolves beatmaps,
    /// creates scores, and handles clock synchronization.
    /// </summary>
    public partial class TournamentGameplayDisplay : CompositeDrawable
    {
        [Resolved]
        private SpectatorClient spectatorClient { get; set; } = null!;

        [Resolved]
        private MultiplayerClient multiplayerClient { get; set; } = null!;

        [Resolved]
        private BeatmapManager beatmapManager { get; set; } = null!;

        [Resolved]
        private RulesetStore rulesetStore { get; set; } = null!;

        [Resolved]
        private RealmAccess realm { get; set; } = null!;

        private readonly MultiplayerMatchIPCInfo multiplayerIpc;

        /// <summary>
        /// The outer container holding all gameplay-related drawables.
        /// Recreated each time gameplay starts.
        /// </summary>
        private Container gameplayContainer = null!;

        /// <summary>
        /// Container for <see cref="PlayerArea"/>s. A child of <see cref="masterClockContainer"/>
        /// so that the master clock's <see cref="IGameplayClock"/> is in the DI chain
        /// (matching <see cref="MultiSpectatorScreen"/>'s hierarchy).
        /// </summary>
        private TournamentPlayerGrid playerAreasContainer = null!;

        private MasterGameplayClockContainer? masterClockContainer;
        private SpectatorSyncManager? syncManager;

        /// <summary>
        /// Player areas keyed by user ID. Created on-demand when a player starts gameplay,
        /// so the sync manager only tracks clocks that have actual scores loaded.
        /// </summary>
        private readonly Dictionary<int, PlayerArea> playerAreas = new Dictionary<int, PlayerArea>();

        /// <summary>
        /// Snapshot of <c>room.Users</c> taken when gameplay begins. Maps user ID to a
        /// stable slot index in <see cref="playerAreasContainer"/>. Users that join
        /// after the snapshot is taken receive no slot.
        /// </summary>
        private readonly Dictionary<int, int> snapshottedSlots = new Dictionary<int, int>();

        /// <summary>
        /// The number of player tiles the operator wants to show simultaneously.
        /// Bound to <see cref="TournamentPlayerGrid.Capacity"/> in
        /// <see cref="setupGameplayInfrastructure"/>. Runtime only — not persisted.
        /// </summary>
        public BindableInt VisibleSlotCount { get; } = new BindableInt(TournamentPlayerGrid.MIN_SLOTS)
        {
            MinValue = TournamentPlayerGrid.MIN_SLOTS,
            MaxValue = TournamentPlayerGrid.MAX_SLOTS,
        };

        private readonly IBindableDictionary<int, SpectatorState> watchedStates = new BindableDictionary<int, SpectatorState>();
        private readonly Dictionary<int, SpectatorGameplayState> gameplayStates = new Dictionary<int, SpectatorGameplayState>();

        /// <summary>
        /// The player area currently providing audio.
        /// </summary>
        private PlayerArea? currentAudioSource;

        private IAggregateAudioAdjustment? boundAdjustments;

        private IDisposable? realmSubscription;
        private bool gameplayActive;

        /// <summary>
        /// Grace period to wait for a terminal <see cref="SpectatedUserState"/> after the multiplayer hub indicates a user has ended play.
        /// If the spectator stream does not deliver a terminal state within this window, the local replay is forcibly marked complete
        /// so the natural completion flow inside <see cref="MultiSpectatorPlayer"/> can push the results screen.
        /// </summary>
        private const double terminal_state_grace_period_ms = 2000;

        private readonly Dictionary<int, ScheduledDelegate> pendingForceTerminations = new Dictionary<int, ScheduledDelegate>();

        public TournamentGameplayDisplay(MultiplayerMatchIPCInfo multiplayerIpc)
        {
            this.multiplayerIpc = multiplayerIpc;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.Both;
            Masking = true;

            InternalChild = gameplayContainer = new Container
            {
                RelativeSizeAxes = Axes.Both,
            };

            // Watch for beatmap downloads completing so we can start gameplay.
            realmSubscription = realm.RegisterForNotifications(
                r => r.All<BeatmapSetInfo>().Where(s => !s.DeletePending), beatmapsChanged);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            watchedStates.BindTo(spectatorClient.WatchedUserStates);
            watchedStates.BindCollectionChanged(onWatchedStatesChanged, true);

            multiplayerIpc.IsConnected.BindValueChanged(connected =>
            {
                if (!connected.NewValue)
                    teardownGameplay();
            });

            multiplayerClient.LoadRequested += onLoadRequested;
            multiplayerClient.GameplayAborted += onGameplayAborted;
            multiplayerClient.UserStateChanged += onMultiplayerUserStateChanged;
        }

        protected override void Update()
        {
            base.Update();

            if (gameplayActive)
                updateAudioSource();
        }

        private void onWatchedStatesChanged(object? sender, NotifyDictionaryChangedEventArgs<int, SpectatorState> e)
        {
            switch (e.Action)
            {
                case NotifyDictionaryChangedAction.Add:
                case NotifyDictionaryChangedAction.Replace:
                    foreach ((int userId, SpectatorState state) in e.NewItems.AsNonNull())
                        onUserStateChanged(userId, state);
                    break;
            }
        }

        /// <summary>
        /// Replays current <see cref="SpectatedUserState.Playing"/> entries through
        /// <see cref="onUserStateChanged"/> so users who began playing while this display's
        /// scheduler was paused (screen hidden) get a <c>PlayerArea</c>. Idempotent per user.
        /// </summary>
        public void RefreshActiveSpectators()
        {
            foreach (var entry in watchedStates)
            {
                if (entry.Value.State == SpectatedUserState.Playing)
                    onUserStateChanged(entry.Key, entry.Value);
            }
        }

        /// <summary>
        /// Tears down all live player areas and gameplay infrastructure. Safe to call when no
        /// gameplay is active. Used by the gameplay screen to clear the player grid as part of
        /// auto-advancing back to the map pool after results.
        /// </summary>
        public void TeardownGameplay() => teardownGameplay();

        private void onUserStateChanged(int userId, SpectatorState newState)
        {
            if (newState.RulesetID == null || newState.BeatmapID == null)
                return;

            // Only process users from our room.
            if (multiplayerClient.Room == null)
                return;

            if (multiplayerClient.Room.Users.All(u => u.UserID != userId))
                return;

            switch (newState.State)
            {
                case SpectatedUserState.Playing:
                    tryStartGameplay(userId);
                    break;

                case SpectatedUserState.Passed:
                case SpectatedUserState.Failed:
                    onPlayerFinished(userId);
                    break;

                case SpectatedUserState.Quit:
                    onPlayerFinished(userId);
                    onPlayerQuit(userId);
                    break;
            }
        }

        private void onLoadRequested()
        {
            // Synchronous teardown: this drawable's Scheduler stops while GameplayScreen is hidden,
            // so a Schedule()'d teardown could fire after the next round's PlayerAreas exist and wipe them.
            teardownGameplay();
        }

        private void onGameplayAborted(GameplayAbortReason _) => teardownGameplay();

        private void onPlayerFinished(int userId)
        {
            if (gameplayStates.TryGetValue(userId, out var state))
                state.Score.Replay.HasReceivedAllFrames = true;

            gameplayStates.Remove(userId);

            // Clock cleanup is deferred until the inner Player transitions to its results screen
            // (see onPlayerShowingResults). Removing the managed clock here freezes the per-player
            // SpectatorPlayerClock — when the local replay hasn't yet caught up to the end of received
            // frames, ScoreProcessor.HasCompleted never fires and MultiSpectatorResultsScreen never appears.
        }

        private void onPlayerQuit(int userId)
        {
            if (!playerAreas.TryGetValue(userId, out var area))
                return;

            area.FadeColour(new Colour4(68, 68, 68, 255), 400, Easing.OutQuint);

            // Quit users don't progress to results, so detach the clock immediately.
            if (syncManager != null)
                syncManager.RemoveManagedClock(area.SpectatorPlayerClock);
        }

        /// <summary>
        /// Invoked when a <see cref="PlayerArea"/>'s inner <see cref="MultiSpectatorPlayer"/> is about to push its results screen.
        /// At this point local gameplay has run to completion and it is safe to detach the per-player clock from the sync manager.
        /// </summary>
        private void onPlayerShowingResults(PlayerArea instance) => Schedule(() =>
        {
            if (syncManager == null)
                return;

            Logger.Log($"Player area for user {instance.UserId} is showing results; releasing managed clock.");
            syncManager.RemoveManagedClock(instance.SpectatorPlayerClock);
        });

        /// <summary>
        /// The multiplayer hub is authoritative on whether a user has ended play. The spectator hub occasionally fails to deliver
        /// the matching terminal <see cref="SpectatedUserState"/>, leaving the corresponding <see cref="PlayerArea"/> stuck on
        /// <see cref="SpectatorPlayerClock.WaitingOnFrames"/> with no transition to results. When the multiplayer hub reports a
        /// transition to <see cref="MultiplayerUserState.FinishedPlay"/> or <see cref="MultiplayerUserState.Results"/>, start a
        /// grace timer; if no terminal spectator state arrives within the window, locally mark the replay complete so the natural
        /// completion flow inside <see cref="MultiSpectatorPlayer"/> can run.
        /// </summary>
        private void onMultiplayerUserStateChanged(MultiplayerRoomUser user, MultiplayerUserState newState) => Schedule(() =>
        {
            int userId = user.UserID;

            // If the user has somehow regressed to a pre-completion state, cancel any pending force-termination.
            if (newState < MultiplayerUserState.FinishedPlay)
            {
                cancelPendingForceTermination(userId);
                return;
            }

            if (newState != MultiplayerUserState.FinishedPlay && newState != MultiplayerUserState.Results)
                return;

            if (!playerAreas.TryGetValue(userId, out var playerArea))
                return;

            // Already scheduled — do not double up.
            if (pendingForceTerminations.ContainsKey(userId))
                return;

            pendingForceTerminations[userId] = Scheduler.AddDelayed(() => forceTerminateIfStuck(userId, playerArea), terminal_state_grace_period_ms);
        });

        private void cancelPendingForceTermination(int userId)
        {
            if (!pendingForceTerminations.TryGetValue(userId, out var pending))
                return;

            pending.Cancel();
            pendingForceTerminations.Remove(userId);
        }

        private void forceTerminateIfStuck(int userId, PlayerArea playerArea)
        {
            pendingForceTerminations.Remove(userId);

            // Nothing to do if a terminal spectator state arrived during the grace window, or if the player never started.
            if (playerArea.Score == null || playerArea.Score.Replay.HasReceivedAllFrames)
                return;

            Logger.Log($"Spectator stream did not deliver a terminal state for user {userId} within {terminal_state_grace_period_ms}ms of multiplayer-side end-of-play; forcing replay completion to unblock results.");

            // Unblocks FramedReplayInputHandler.WaitingForFrame so the per-player clock can advance past the last received frame,
            // remaining hit objects judge (auto-missing if frames were also dropped), ScoreProcessor.HasCompleted fires, and the
            // existing Player.progressToResults path pushes MultiSpectatorResultsScreen as usual.
            playerArea.Score.Replay.HasReceivedAllFrames = true;
        }

        private void tryStartGameplay(int userId)
        {
            if (!watchedStates.TryGetValue(userId, out var spectatorState))
                return;

            if (gameplayStates.ContainsKey(userId))
                return;

            // Get user info from the multiplayer room — already populated by MultiplayerClient.JoinRoom.
            // This avoids an async lookup that would delay PlayerArea creation and cause sync gaps.
            var roomUser = multiplayerClient.Room?.Users.FirstOrDefault(u => u.UserID == userId);
            var user = roomUser?.User;

            if (user == null)
                return;

            var resolvedRuleset = rulesetStore.AvailableRulesets.FirstOrDefault(r => r.OnlineID == spectatorState.RulesetID)?.CreateInstance();
            if (resolvedRuleset == null)
                return;

            var resolvedBeatmap = beatmapManager.QueryBeatmap(b => b.OnlineID == spectatorState.BeatmapID);

            if (resolvedBeatmap == null)
            {
                Logger.Log($"[TournamentGameplayDisplay] Beatmap {spectatorState.BeatmapID} not locally available for user {userId}. Waiting for download...", LoggingTarget.Runtime);
                return;
            }

            var score = new Score
            {
                ScoreInfo = new ScoreInfo
                {
                    BeatmapInfo = resolvedBeatmap,
                    User = user,
                    Mods = spectatorState.Mods.Select(m => m.ToMod(resolvedRuleset)).ToArray(),
                    Ruleset = resolvedRuleset.RulesetInfo,
                },
                Replay = new Replay { HasReceivedAllFrames = false },
            };

            var gameplayState = new SpectatorGameplayState(score, resolvedRuleset, beatmapManager.GetWorkingBeatmap(resolvedBeatmap));
            gameplayStates[userId] = gameplayState;

            Logger.Log($"[TournamentGameplayDisplay] Starting gameplay for user {userId} ({user.Username})", LoggingTarget.Runtime);
            loadUserIntoPlayerArea(userId, gameplayState);
        }

        private void loadUserIntoPlayerArea(int userId, SpectatorGameplayState gameplayState)
        {
            // Ensure master clock + sync manager are set up (created once per gameplay session).
            if (masterClockContainer == null)
                setupGameplayInfrastructure(gameplayState.Beatmap);

            Debug.Assert(syncManager != null);

            // Don't create a second area for this user.
            if (playerAreas.ContainsKey(userId))
                return;

            // Only users present at snapshot time receive a slot. Users who join the
            // multiplayer room after gameplay began do not appear in the grid.
            if (!snapshottedSlots.TryGetValue(userId, out int slotIndex))
                return;

            // Create managed clock and PlayerArea on-demand so the sync manager
            // only tracks clocks that have actual scores to play.
            // The low-health red overlay is suppressed because failed players are
            // still rendered in the tournament grid and the tint is undesirable on broadcast.
            var playerArea = new PlayerArea(userId, syncManager.CreateManagedClock(), showFailingLayer: false)
            {
                RelativeSizeAxes = Axes.Both,
            };
            playerArea.OnShowingResults += () => onPlayerShowingResults(playerArea);

            playerAreas[userId] = playerArea;
            playerAreasContainer.Add(playerArea, slotIndex);
            playerArea.LoadScore(gameplayState.Score);

            // Bind audio adjustments from the first loaded player to keep the master clock in sync.
            if (boundAdjustments == null)
                bindAudioAdjustments(playerArea);
        }

        private void setupGameplayInfrastructure(WorkingBeatmap workingBeatmap)
        {
            teardownGameplay();

            gameplayActive = true;

            // MasterGameplayClockContainer accesses the track in its constructor.
            if (!workingBeatmap.TrackLoaded)
                workingBeatmap.LoadTrack();

            playerAreasContainer = new TournamentPlayerGrid { RelativeSizeAxes = Axes.Both };
            // Bind the grid's Capacity TO the display's VisibleSlotCount (not the other way
            // around) so the operator's slider value survives a grid rebuild instead of being
            // reset to MIN_SLOTS each time gameplay restarts.
            playerAreasContainer.Capacity.BindTo(VisibleSlotCount);

            snapshottedSlots.Clear();
            if (multiplayerClient.Room != null)
            {
                foreach (var kvp in BuildSnapshottedSlots(
                             multiplayerClient.Room.Users,
                             multiplayerClient.Room.Settings.Name,
                             TournamentPlayerGrid.MAX_SLOTS))
                {
                    snapshottedSlots[kvp.Key] = kvp.Value;
                }
            }

            masterClockContainer = new MasterGameplayClockContainer(workingBeatmap, 0)
            {
                // PlayerAreas are children of the master clock container so that the master's
                // IGameplayClock is in their DI chain (matching MultiSpectatorScreen's hierarchy).
                Child = playerAreasContainer,
            };

            syncManager = new SpectatorSyncManager(masterClockContainer)
            {
                ReadyToStart = performInitialSeek,
            };

            gameplayContainer.Children = new Drawable[]
            {
                masterClockContainer,
                syncManager,
            };

            // Reset the master clock but don't start it yet —
            // performInitialSeek will seek and start once player clocks have frames.
            masterClockContainer.Reset();
        }

        private void teardownGameplay()
        {
            if (!gameplayActive)
                return;

            gameplayActive = false;
            gameplayStates.Clear();
            snapshottedSlots.Clear();

            foreach (var pending in pendingForceTerminations.Values)
                pending.Cancel();
            pendingForceTerminations.Clear();

            if (syncManager != null)
            {
                foreach (var area in playerAreas.Values)
                    syncManager.RemoveManagedClock(area.SpectatorPlayerClock);
            }

            if (boundAdjustments != null && masterClockContainer != null)
                masterClockContainer.AdjustmentsFromMods.UnbindAdjustments(boundAdjustments);

            boundAdjustments = null;
            currentAudioSource = null;

            // Stop the master clock before clearing so the beatmap track doesn't keep playing.
            masterClockContainer?.Stop();

            playerAreasContainer.Capacity.UnbindFrom(VisibleSlotCount);

            playerAreas.Clear();
            gameplayContainer.Clear();
            masterClockContainer = null;
            syncManager = null;
        }

        /// <summary>
        /// Seeks the master clock to the earliest available frame time across all loaded players
        /// and starts playback. Mirrors <see cref="MultiSpectatorScreen"/>'s initial seek logic.
        /// </summary>
        private void performInitialSeek()
        {
            Debug.Assert(masterClockContainer != null);

            var minFrameTimes = new List<double>();

            foreach (var area in playerAreas.Values)
            {
                if (area.Score == null)
                    continue;

                var minFrame = area.Score.Replay.Frames.MinBy(f => f.Time);

                if (minFrame != null)
                    minFrameTimes.Add(minFrame.Time);
            }

            if (minFrameTimes.Count == 0)
            {
                masterClockContainer.Reset(0, true);
                return;
            }

            // Remove outliers — if one player's earliest frame is >1s behind the mean,
            // exclude it to avoid seeking too far back.
            double mean = minFrameTimes.Average();
            minFrameTimes.RemoveAll(t => mean - t > 1000);

            double startTime = minFrameTimes.Count > 0 ? minFrameTimes.Min() : 0;

            Logger.Log($"[TournamentGameplayDisplay] Seeking to initial time {startTime:N0}ms", LoggingTarget.Runtime);
            masterClockContainer.Reset(startTime, true);

        }

        #region Audio source management

        /// <summary>
        /// Selects the best audio source each frame (matching <see cref="MultiSpectatorScreen"/>'s logic)
        /// and ensures mod rate adjustments are bound to the master clock.
        /// </summary>
        private void updateAudioSource()
        {
            if (syncManager == null || masterClockContainer == null)
                return;

            // If the current source is still viable, keep using it.
            if (isCandidateAudioSource(currentAudioSource?.SpectatorPlayerClock))
                return;

            // Pick the running player clock closest to the master clock time.
            currentAudioSource = playerAreas.Values
                                            .Where(a => isCandidateAudioSource(a.SpectatorPlayerClock))
                                            .MinBy(a => Math.Abs(a.SpectatorPlayerClock.CurrentTime - syncManager.CurrentMasterTime));

            if (currentAudioSource != null)
                bindAudioAdjustments(currentAudioSource);

            foreach (var area in playerAreas.Values)
                area.Mute = area != currentAudioSource;
        }

        private void bindAudioAdjustments(PlayerArea source)
        {
            if (boundAdjustments != null && masterClockContainer != null)
                masterClockContainer.AdjustmentsFromMods.UnbindAdjustments(boundAdjustments);

            boundAdjustments = source.ClockAdjustmentsFromMods;
            masterClockContainer?.AdjustmentsFromMods.BindAdjustments(boundAdjustments);
        }

        private static bool isCandidateAudioSource(SpectatorPlayerClock? clock)
            => clock?.IsRunning == true && !clock.IsCatchingUp && !clock.WaitingOnFrames;

        #endregion

        private static readonly Regex room_name_teams_regex = new Regex(
            @"^[^:]*:\s*\((?<p1>.+?)\)\s+vs\s+\((?<p2>.+?)\)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static (string p1, string p2)? tryParseRoomNameTeams(string? roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName))
                return null;

            var match = room_name_teams_regex.Match(roomName);

            if (!match.Success)
                return null;

            return (match.Groups["p1"].Value.Trim(), match.Groups["p2"].Value.Trim());
        }

        /// <summary>
        /// Projects room users into a user-id→slot-index map for the spectator grid, truncated
        /// to <paramref name="maxSlots"/>. Only users whose state indicates participation in
        /// the current round (<see cref="MultiplayerUserState.WaitingForLoad"/>,
        /// <see cref="MultiplayerUserState.Loaded"/>, <see cref="MultiplayerUserState.ReadyForGameplay"/>,
        /// <see cref="MultiplayerUserState.Playing"/>) are given a slot. Users in
        /// <see cref="MultiplayerUserState.Idle"/>, <see cref="MultiplayerUserState.Ready"/>,
        /// <see cref="MultiplayerUserState.Spectating"/>, or post-play states never produce
        /// gameplay frames for this round, and reserving a slot for them would leave the grid
        /// rendering one fewer tile than the capacity slider reads. When <paramref name="roomName"/>
        /// follows the convention <c>"ACRONYM: (Name 1) vs (Name 2)"</c>, slot 0 is reserved for
        /// the user whose username matches Name 1 (left) and slot 1 for Name 2 (right).
        /// Remaining users fill from the first free slot in room join order. Pure function of
        /// its arguments — extracted for unit testing.
        /// </summary>
        internal static Dictionary<int, int> BuildSnapshottedSlots(
            IEnumerable<MultiplayerRoomUser> roomUsers,
            string? roomName,
            int maxSlots)
        {
            var pendingUsers = roomUsers
                               .Where(u => isParticipatingInCurrentRound(u.State))
                               .ToList();

            var result = new Dictionary<int, int>();

            if (tryParseRoomNameTeams(roomName) is { } names)
            {
                reserveSlot(names.p1, 0);
                reserveSlot(names.p2, 1);
            }

            int nextSlot = 0;
            foreach (var user in pendingUsers)
            {
                while (nextSlot < maxSlots && result.ContainsValue(nextSlot))
                    nextSlot++;

                if (nextSlot >= maxSlots)
                    break;

                result[user.UserID] = nextSlot++;
            }

            return result;

            void reserveSlot(string username, int slotIndex)
            {
                var matched = pendingUsers.FirstOrDefault(u =>
                    string.Equals(u.User?.Username, username, StringComparison.OrdinalIgnoreCase));
                if (matched == null)
                    return;

                result[matched.UserID] = slotIndex;
                pendingUsers.Remove(matched);
            }
        }

        private static bool isParticipatingInCurrentRound(MultiplayerUserState state)
            => state == MultiplayerUserState.WaitingForLoad
               || state == MultiplayerUserState.Loaded
               || state == MultiplayerUserState.ReadyForGameplay
               || state == MultiplayerUserState.Playing;

        private void beatmapsChanged(IRealmCollection<BeatmapSetInfo> items, ChangeSet? changes)
        {
            if (changes?.InsertedIndices == null)
                return;

            if (multiplayerClient.Room == null)
                return;

            // When a beatmap is downloaded, try to start gameplay for any users waiting on it.
            foreach (int c in changes.InsertedIndices)
            {
                var beatmapSet = items[c];

                foreach (var user in multiplayerClient.Room.Users)
                {
                    int userId = user.UserID;

                    if (gameplayStates.ContainsKey(userId))
                        continue;

                    if (!watchedStates.TryGetValue(userId, out var state))
                        continue;

                    if (state.State != SpectatedUserState.Playing)
                        continue;

                    if (beatmapSet.Beatmaps.Any(b => b.OnlineID == state.BeatmapID))
                    {
                        Schedule(() => tryStartGameplay(userId));
                    }
                }
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            realmSubscription?.Dispose();

            if (multiplayerClient.IsNotNull())
            {
                multiplayerClient.LoadRequested -= onLoadRequested;
                multiplayerClient.GameplayAborted -= onGameplayAborted;
                multiplayerClient.UserStateChanged -= onMultiplayerUserStateChanged;
            }

            foreach (var pending in pendingForceTerminations.Values)
                pending.Cancel();
            pendingForceTerminations.Clear();
        }
    }
}
