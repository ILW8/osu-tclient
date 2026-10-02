// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using osu.Framework.Bindables;
using osu.Game.Rulesets;

namespace osu.Game.Tournament.Models
{
    /// <summary>
    /// Holds the complete data required to operate the tournament system.
    /// </summary>
    [Serializable]
    public class LadderInfo
    {
        public Bindable<RulesetInfo?> Ruleset = new Bindable<RulesetInfo?>();

        public BindableList<TournamentMatch> Matches = new BindableList<TournamentMatch>();
        public BindableList<TournamentRound> Rounds = new BindableList<TournamentRound>();
        public BindableList<TournamentTeam> Teams = new BindableList<TournamentTeam>();

        // only used for serialisation
        public List<TournamentProgression> Progressions = new List<TournamentProgression>();

        [JsonIgnore] // updated manually in TournamentGameBase
        public Bindable<TournamentMatch?> CurrentMatch = new Bindable<TournamentMatch?>();

        public Bindable<int> ChromaKeyWidth = new BindableInt(1024)
        {
            MinValue = 640,
            MaxValue = 1366,
        };

        public Bindable<int> PlayersPerTeam = new BindableInt(4)
        {
            MinValue = 1,
            MaxValue = 4,
        };

        /// <summary>
        /// How far (in milliseconds) multiplayer-room spectating rides behind the slowest player's latest received frame.
        /// Larger values absorb more frame-delivery jitter at the cost of more delay.
        /// </summary>
        public Bindable<int> LiveEdgeBuffer = new BindableInt(200)
        {
            MinValue = 200,
            MaxValue = 5000,
            Precision = 200,
        };

        public Bindable<bool> AutoProgressScreens = new BindableBool(true);

        /// <summary>
        /// When enabled, the tournament client spectates a live multiplayer room directly
        /// (via MultiplayerMatchIPCInfo) instead of using the file-based IPC bridge to a stable client.
        /// </summary>
        public Bindable<bool> UseMultiplayerSpectating = new BindableBool();

        public Bindable<bool> SplitMapPoolByMods = new BindableBool(true);

        public Bindable<bool> DisplayTeamSeeds = new BindableBool();

        public Bindable<bool> DisplayPlayerNames = new BindableBool(true);

        /// <summary>
        /// When <c>true</c>, mutes UI sample playback (hover/click sounds) globally.
        /// Gameplay hitsounds are unaffected as they use per-skin sample stores.
        /// </summary>
        public Bindable<bool> MuteUISounds = new BindableBool(true);
    }
}
