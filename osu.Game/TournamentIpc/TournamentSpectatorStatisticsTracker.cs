// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Timing;
using osu.Game.Configuration;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Spectator;
using osu.Game.Rulesets.Scoring;
using osu.Game.Screens.Play.HUD;

namespace osu.Game.TournamentIpc
{
    [LongRunningLoad]
    public partial class TournamentSpectatorStatisticsTracker : CompositeDrawable
    {
        private readonly MultiplayerRoomUser[] playingUsers;

        protected readonly Dictionary<int, MultiplayerGameplayLeaderboard.TrackedUserData> UserScores = new Dictionary<int, MultiplayerGameplayLeaderboard.TrackedUserData>();

        public readonly SortedDictionary<int, BindableLong> TeamScores = new SortedDictionary<int, BindableLong>();

        private bool hasTeams => TeamScores.Count > 0;

        [Resolved(canBeNull: true)]
        protected TournamentFileBasedIPC? TournamentIpc { get; private set; }

        private SpectatorScoreProcessor scoreProcessor = null!;

        private Bindable<ScoringMode> scoringMode = null!;

        /// <summary>
        /// Tracks statistics on spectated players.
        /// </summary>
        /// <param name="users">Array of users in the room to track</param>
        public TournamentSpectatorStatisticsTracker(MultiplayerRoomUser[] users)
        {
            Logger.Log($"Created new {nameof(TournamentSpectatorStatisticsTracker)}");
            playingUsers = users;
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            scoringMode = config.GetBindable<ScoringMode>(OsuSetting.ScoreDisplayMode);
            var syntheticTeams = config.GetBindable<bool>(OsuSetting.SynthetizeTeamsInHeadToHead);

            int totalUsers = playingUsers.Length;
            int userIndex = 0;

            foreach (var user in playingUsers)
            {
                var synthetizedUser = user;

                if (syntheticTeams.Value)
                {
                    synthetizedUser = user;
                    synthetizedUser.MatchState = new TeamVersusUserState { TeamID = userIndex / totalUsers };
                }

                scoreProcessor = new SpectatorScoreProcessor(synthetizedUser.UserID);
                scoreProcessor.Mode.BindTo(scoringMode);
                scoreProcessor.TotalScore.BindValueChanged(_ => Scheduler.AddOnce(updateTeamScores));
                AddInternal(scoreProcessor);

                var trackedUser = new MultiplayerGameplayLeaderboard.TrackedUserData(synthetizedUser, scoreProcessor);
                UserScores[synthetizedUser.UserID] = trackedUser;

                if (trackedUser.Team is int team && !TeamScores.ContainsKey(team))
                {
                    var teamScoreBindable = new BindableLong();
                    TeamScores.Add(team, teamScoreBindable);
                }

                userIndex++;
            }

            // clear old scores, if any
            TournamentIpc?.UpdateTeamScores(TeamScores.Values.Select(bindableLong => bindableLong.Value).ToArray());
        }

        private void updateTeamScores()
        {
            if (!hasTeams)
                return;

            var teamScores = new Dictionary<int, long>();

            foreach (var u in UserScores.Values)
            {
                if (u.Team == null)
                    continue;

                if (teamScores.ContainsKey(u.Team.Value))
                {
                    teamScores[u.Team.Value] += u.ScoreProcessor.TotalScore.Value;
                }
                else
                {
                    teamScores[u.Team.Value] = u.ScoreProcessor.TotalScore.Value;
                }
            }

            foreach (var teamScore in teamScores)
            {
                TeamScores[teamScore.Key].Value = teamScore.Value;
            }

            TournamentIpc?.UpdateTeamScores(teamScores.OrderBy(score => score.Key)
                                                      .Select(score => score.Value)
                                                      .ToArray());
        }

        public void AddClock(int userId, IClock clock)
        {
            if (!UserScores.TryGetValue(userId, out var data))
                throw new ArgumentException(@"Provided user is not tracked by this leaderboard", nameof(userId));

            data.ScoreProcessor.ReferenceClock = clock;
        }

        protected override void Update()
        {
            base.Update();

            foreach (var (_, data) in UserScores)
                data.ScoreProcessor.UpdateScore();
        }
    }
}
