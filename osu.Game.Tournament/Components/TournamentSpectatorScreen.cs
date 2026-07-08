// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Game.Beatmaps;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Spectator;
using osu.Game.Screens.OnlinePlay.Multiplayer.Spectate;
using osu.Game.Screens.Play;
using osu.Game.Screens.Spectate;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;

namespace osu.Game.Tournament.Components
{
    /// <summary>
    /// A <see cref="SpectatorScreen"/> that renders all spectated players of a multiplayer round
    /// simultaneously, embedded in the tournament overlay's gameplay area.
    ///
    /// It inherits the upstream watch → state → gameplay pipeline from <see cref="SpectatorScreen"/>
    /// and composes the same building blocks <see cref="MultiSpectatorScreen"/> uses
    /// (<see cref="MasterGameplayClockContainer"/> + <see cref="SpectatorSyncManager"/> +
    /// <see cref="PlayerArea"/> tiles in a <see cref="TournamentPlayerGrid"/>) — but without the
    /// results screen / leaderboard / chat chrome that would fight the embedded chroma layout.
    /// </summary>
    public partial class TournamentSpectatorScreen : SpectatorScreen
    {
        // Isolate the beatmap/ruleset to this screen so the overlay's global selection isn't disturbed.
        public override bool DisallowExternalBeatmapRulesetChanges => true;

        // Audio adjustments are managed per-player below.
        public override bool? ApplyModTrackAdjustments => false;

        /// <summary>
        /// The number of tiles shown in the grid. Defaults to the participant count (clamped to the
        /// grid's slot range); operator-tunable.
        /// </summary>
        public readonly BindableInt VisibleSlotCount = new BindableInt(TournamentPlayerGrid.MIN_SLOTS)
        {
            MinValue = TournamentPlayerGrid.MIN_SLOTS,
            MaxValue = TournamentPlayerGrid.MAX_SLOTS,
        };

        [Resolved]
        private MultiplayerClient multiplayerClient { get; set; } = null!;

        [Resolved]
        private MatchIPCInfo ipc { get; set; } = null!;

        [Resolved]
        private LadderInfo ladder { get; set; } = null!;

        private MasterGameplayClockContainer masterClockContainer = null!;
        private SpectatorSyncManager syncManager = null!;
        private TournamentPlayerGrid grid = null!;

        private readonly Dictionary<int, PlayerArea> playerAreas = new Dictionary<int, PlayerArea>();
        private readonly Dictionary<int, int> slots = new Dictionary<int, int>(); // userId -> slot index

        // Live team-score bar, derived from spectated frames (no file-based IPC in this mode).
        private readonly Dictionary<int, SpectatorScoreProcessor> scoreProcessors = new Dictionary<int, SpectatorScoreProcessor>();
        private readonly Dictionary<int, int> teamByUser = new Dictionary<int, int>(); // userId -> TeamVersus TeamID

        private PlayerArea? currentAudioSource;
        private IAggregateAudioAdjustment? boundAdjustments;

        private bool gameplayStarted;

        public TournamentSpectatorScreen(int[] users)
            : base(users)
        {
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // The master clock + grid are built lazily on the first started player (setupGameplayInfrastructure),
            // because the tournament client's global Beatmap.Value is the dummy beatmap — the real working
            // beatmap for the round only arrives via the resolved SpectatorGameplayState.
            VisibleSlotCount.Value = Math.Clamp(Users.Count, TournamentPlayerGrid.MIN_SLOTS, TournamentPlayerGrid.MAX_SLOTS);
        }

        private void setupGameplayInfrastructure(WorkingBeatmap working)
        {
            gameplayStarted = true;

            // MasterGameplayClockContainer accesses working.Track in its constructor, which throws
            // unless the track has been loaded first (the resolved room beatmap arrives un-loaded).
            if (!working.TrackLoaded)
                working.LoadTrack();

            // TournamentLobbyMusic loops the room beatmap for lobby music via Track.Looping = true, and
            // GetWorkingBeatmap hands out a cached shared WorkingBeatmap — so this is the SAME Track the
            // master clock is about to drive. A leftover looping flag makes the track wrap to the start
            // at map end instead of completing, dragging every synced tile back to 0. Gameplay owns a
            // non-looping track; assert that here rather than depending on the lobby music's teardown timing.
            working.Track.Looping = false;

            InternalChildren = new Drawable[]
            {
                // PlayerArea tiles are nested under the master clock container to mirror MultiSpectatorScreen's
                // layout; the master-clock link itself is established by SpectatorSyncManager.CreateManagedClock()
                // passing the master GameplayClockContainer into each SpectatorPlayerClock by reference (not via DI).
                masterClockContainer = new MasterGameplayClockContainer(working, 0)
                {
                    Child = grid = new TournamentPlayerGrid { RelativeSizeAxes = Axes.Both },
                },
                // The sync manager is a fire-and-forget sibling (NOT nested under the clock container);
                // its Update() drives per-player catch-up and the master clock state.
                syncManager = new SpectatorSyncManager(masterClockContainer)
                {
                    ReadyToStart = performInitialSeek,
                },
            };

            grid.Capacity.BindTo(VisibleSlotCount);
            masterClockContainer.Reset();
        }

        protected override void OnNewPlayingUserState(int userId, SpectatorState spectatorState)
        {
        }

        protected override void StartGameplay(int userId, SpectatorGameplayState spectatorGameplayState) => Schedule(() =>
        {
            if (playerAreas.ContainsKey(userId))
                return;

            // Build the shared clock + grid from the first resolved working beatmap.
            if (!gameplayStarted)
                setupGameplayInfrastructure(spectatorGameplayState.Beatmap);

            // Snapshot the round's participant → slot mapping once, on the first player to start.
            if (slots.Count == 0)
                snapshotSlotsFromRoom();

            int slot = assignSlot(userId);

            if (slot >= TournamentPlayerGrid.MAX_SLOTS)
            {
                Logger.Log($"[TournamentSpectator] user {userId} exceeds the {TournamentPlayerGrid.MAX_SLOTS}-slot grid; not rendering a tile.", level: LogLevel.Important);
                return;
            }

            var area = new PlayerArea(userId, syncManager.CreateManagedClock(userId), showFailingLayer: false, showPlayerName: ladder.DisplayPlayerNames.Value);
            playerAreas[userId] = area;
            grid.Add(area, slot);
            area.LoadScore(spectatorGameplayState.Score);

            addScoreProcessor(userId, area);
        });

        // Reference the player's synced clock so the total tracks on-screen playback, and snapshot
        // the user's MP-room team once (it doesn't change mid-map) for bucketing in updateTeamScores.
        private void addScoreProcessor(int userId, PlayerArea area)
        {
            var processor = new SpectatorScoreProcessor(userId)
            {
                ReferenceClock = area.SpectatorPlayerClock,
            };

            scoreProcessors[userId] = processor;
            AddInternal(processor);

            if ((multiplayerClient.Room?.Users.FirstOrDefault(u => u.UserID == userId)?.MatchState as TeamVersusUserState)?.TeamID is int team)
                teamByUser[userId] = team;
        }

        protected override void PassGameplay(int userId) => Schedule(() => removeClock(userId));

        protected override void FailGameplay(int userId) => Schedule(() => removeClock(userId));

        protected override void QuitGameplay(int userId) => Schedule(() =>
        {
            RemoveUser(userId);
            removeClock(userId);
        });

        private void removeClock(int userId)
        {
            if (playerAreas.TryGetValue(userId, out var area))
                syncManager.RemoveManagedClock(area.SpectatorPlayerClock);
        }

        protected override void Update()
        {
            base.Update();
            checkAudioSource();
            updateTeamScores();
        }

        // Writes live per-team totals to the overlay score bar; empty -> 0/0, which resets it between rounds.
        private void updateTeamScores()
        {
            foreach (var processor in scoreProcessors.Values)
                processor.UpdateScore();

            var entries = scoreProcessors.Select(kv =>
                (team: teamByUser.TryGetValue(kv.Key, out int t) ? (int?)t : null, score: kv.Value.TotalScore.Value));

            (ipc.Score1.Value, ipc.Score2.Value) = SumTeamScores(entries);
        }

        /// <summary>
        /// Sums per-user totals by TeamVersus TeamID: team 0 -> Red/Score1, other -> Blue/Score2,
        /// null (e.g. a HeadToHead room with no team state) -> neither.
        /// </summary>
        internal static (long red, long blue) SumTeamScores(IEnumerable<(int? team, long score)> entries)
        {
            long red = 0, blue = 0;

            foreach ((int? team, long score) in entries)
            {
                if (team == 0)
                    red += score;
                else if (team != null)
                    blue += score;
            }

            return (red, blue);
        }

        /// <summary>
        /// Picks a single player tile to act as the audio source and mutes the rest, preferring the
        /// running, in-sync clock closest to the master time. Avoids a cacophony of overlapping audio.
        /// </summary>
        private void checkAudioSource()
        {
            // Nothing to manage until the first player has started (lazy infrastructure setup).
            if (!gameplayStarted)
                return;

            // Keep the current source if it's still a good candidate.
            if (currentAudioSource != null && isCandidateAudioSource(currentAudioSource.SpectatorPlayerClock))
                return;

            currentAudioSource = playerAreas.Values
                                            .Where(a => isCandidateAudioSource(a.SpectatorPlayerClock))
                                            .MinBy(a => Math.Abs(a.SpectatorPlayerClock.CurrentTime - syncManager.CurrentMasterTime));

            // Only rebind if a valid source exists; otherwise keep the previous adjustments to avoid sudden audio changes.
            if (currentAudioSource != null)
                bindAudioAdjustments(currentAudioSource);

            foreach (var area in playerAreas.Values)
                area.Mute = area != currentAudioSource;
        }

        private void bindAudioAdjustments(PlayerArea source)
        {
            if (boundAdjustments != null)
                masterClockContainer.AdjustmentsFromMods.UnbindAdjustments(boundAdjustments);

            boundAdjustments = source.ClockAdjustmentsFromMods;
            masterClockContainer.AdjustmentsFromMods.BindAdjustments(boundAdjustments);
        }

        private static bool isCandidateAudioSource(SpectatorPlayerClock? clock)
            => clock?.IsRunning == true && !clock.IsCatchingUp && !clock.WaitingOnFrames && !clock.Abandoned;

        private void snapshotSlotsFromRoom()
        {
            var roomUsers = multiplayerClient.Room?.Users.Select(u => (u.UserID, u.User?.Username, u.State))
                            ?? Enumerable.Empty<(int, string?, MultiplayerUserState)>();

            foreach ((int userId, int slot) in SnapshotSlots(roomUsers, multiplayerClient.Room?.Settings.Name))
                slots[userId] = slot;
        }

        private int assignSlot(int userId)
        {
            if (slots.TryGetValue(userId, out int existing))
                return existing;

            // Fallback for a participant that started gameplay but wasn't in the snapshot: take the
            // lowest free slot (room-name reservations can leave a gap, so slots.Count could collide).
            int slot = 0;
            while (slots.ContainsValue(slot))
                slot++;

            return slots[userId] = slot;
        }

        /// <summary>
        /// Projects a room's users onto a stable slot map, including only users in an active gameplay
        /// state (so Idle/Ready/Spectating users — including the tourney client itself — don't reserve
        /// a tile).
        ///
        /// When <paramref name="roomName"/> follows the convention "ACRONYM: (Name 1) vs (Name 2)",
        /// slot 0 (rendered left) is reserved for the participating user whose username matches Name 1
        /// and slot 1 (right) for Name 2; remaining users fill the rest in input order. Falls back to
        /// plain sequential assignment when the name doesn't match the convention or no username matches.
        /// </summary>
        internal static Dictionary<int, int> SnapshotSlots(
            IEnumerable<(int userId, string? username, MultiplayerUserState state)> roomUsers,
            string? roomName = null)
        {
            var participating = roomUsers.Where(u => IsParticipating(u.state)).ToList();
            var result = new Dictionary<int, int>();

            if (tryParseRoomNameTeams(roomName) is { } names)
            {
                reserveSlot(names.p1, 0);
                reserveSlot(names.p2, 1);
            }

            int next = 0;

            foreach (var user in participating)
            {
                if (result.ContainsKey(user.userId))
                    continue;

                while (result.ContainsValue(next))
                    next++;

                result[user.userId] = next++;
            }

            return result;

            void reserveSlot(string username, int slot)
            {
                foreach (var user in participating)
                {
                    if (result.ContainsKey(user.userId))
                        continue;

                    if (string.Equals(user.username, username, StringComparison.OrdinalIgnoreCase))
                    {
                        result[user.userId] = slot;
                        return;
                    }
                }
            }
        }

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

        internal static bool IsParticipating(MultiplayerUserState state)
            => state == MultiplayerUserState.WaitingForLoad
               || state == MultiplayerUserState.Loaded
               || state == MultiplayerUserState.ReadyForGameplay
               || state == MultiplayerUserState.Playing;

        private void performInitialSeek()
        {
            // Seed the master already delayed to the slowest kept player so no client has to stutter to catch up.
            // Uses each player's live edge (latest received frame); a player with no frames reports the "maximally
            // behind" sentinel so it's dropped by ComputeInitialSeekTime rather than dragging the seed.
            var liveEdges = playerAreas.Values
                                       .Where(a => a.Score != null)
                                       .Select(a => a.Score!.Replay.Frames.Count > 0 ? a.Score.Replay.Frames[^1].Time : double.NegativeInfinity)
                                       .ToList();

            double startTime = ComputeInitialSeekTime(liveEdges);
            masterClockContainer.Reset(startTime, true);
            Logger.Log($"[TournamentSpectator] initial seek to {startTime}");
        }

        /// <summary>
        /// Computes the initial master-clock seek time from the players' live edges (latest received frame each):
        /// drop players more than <see cref="SpectatorSyncManager.MAX_LIVE_OFFSET"/> behind the furthest edge, then
        /// seed at the minimum remaining edge minus <see cref="SpectatorSyncManager.LIVE_EDGE_BUFFER"/> so the master
        /// starts already delayed to the slowest kept player. Returns 0 when there are no usable edges.
        /// </summary>
        internal static double ComputeInitialSeekTime(IEnumerable<double> liveEdges)
        {
            var edges = liveEdges.ToList();

            if (edges.Count == 0)
                return 0;

            double liveEdge = edges.Max();
            var kept = edges.Where(e => liveEdge - e <= SpectatorSyncManager.MAX_LIVE_OFFSET).ToList();

            if (kept.Count == 0)
                return 0;

            return kept.Min() - SpectatorSyncManager.LIVE_EDGE_BUFFER;
        }
    }
}
