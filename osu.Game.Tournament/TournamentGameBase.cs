// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Online;
using osu.Game.Online.API.Requests;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IO;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;
using osu.Game.Users;
using osuTK.Input;

namespace osu.Game.Tournament
{
    [Cached(typeof(TournamentGameBase))]
    public partial class TournamentGameBase : OsuGameBase
    {
        public const string BRACKET_FILENAME = @"bracket.json";
        private LadderInfo ladder = new LadderInfo();
        private TournamentStorage storage = null!;
        private DependencyContainer dependencies = null!;
        private MatchIPCInfo ipc = null!;
        private BeatmapLookupCache beatmapCache = null!;
        private Bindable<string> configSkin = null!;
        private readonly BindableDouble uiSampleMuteAdjustment = new BindableDouble();

        protected Task BracketLoadTask => bracketLoadTaskCompletionSource.Task;

        private readonly TaskCompletionSource<bool> bracketLoadTaskCompletionSource = new TaskCompletionSource<bool>();

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            return dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
        }

        public override EndpointConfiguration CreateEndpoints()
        {
            if (UseDevelopmentServer)
                return base.CreateEndpoints();

            return new ProductionEndpointConfiguration();
        }

        public override void SetHost(GameHost host)
        {
            base.SetHost(host);

            if (host.Window != null)
                host.Window.Title = $"{Name} [tournament client]";
        }

        private TournamentSpriteText initialisationText = null!;

        [BackgroundDependencyLoader]
        private void load(Storage baseStorage)
        {
            Add(initialisationText = new TournamentSpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Font = OsuFont.Torus.With(size: 32),
            });

            Resources.AddStore(new DllResourceStore(typeof(TournamentGameBase).Assembly));

            dependencies.CacheAs<Storage>(storage = new TournamentStorage(baseStorage));
            dependencies.CacheAs(storage);

            dependencies.Cache(new TournamentVideoResourceStore(storage));

            Textures.AddTextureSource(new TextureLoaderStore(new StorageBackedResourceStore(storage)));

            dependencies.CacheAs(new StableInfo(storage));

            beatmapCache = dependencies.Get<BeatmapLookupCache>();

            // Keep the skin in sync with the main client. OsuGame does this, but the tourney client
            // bypasses OsuGame, so replicate it: load the shared persisted skin on startup and write
            // runtime changes back, so the embedded spectator gameplay matches the main game's skin.
            configSkin = LocalConfig.GetBindable<string>(OsuSetting.Skin);
            SkinManager.SetSkinFromConfiguration(configSkin.Value);
            SkinManager.CurrentSkinInfo.ValueChanged += skin => configSkin.Value = skin.NewValue.ID.ToString();
        }

        protected override void LoadComplete()
        {
            GlobalCursorDisplay.MenuCursor.AlwaysPresent = true; // required for tooltip display

            // we don't want to show the menu cursor as it would appear on stream output.
            GlobalCursorDisplay.MenuCursor.Alpha = 0;

            base.LoadComplete();

            Task.Run(readBracket);
        }

        private async Task readBracket()
        {
            try
            {
                if (storage.Exists(BRACKET_FILENAME))
                {
                    using (Stream stream = storage.GetStream(BRACKET_FILENAME, FileAccess.Read, FileMode.Open))
                    using (var sr = new StreamReader(stream))
                    {
                        ladder = JsonConvert.DeserializeObject<LadderInfo>(await sr.ReadToEndAsync().ConfigureAwait(false), new JsonPointConverter()) ?? ladder;
                    }
                }

                var resolvedRuleset = ladder.Ruleset.Value != null
                    ? RulesetStore.GetRuleset(ladder.Ruleset.Value.ShortName)
                    : RulesetStore.AvailableRulesets.First();

                // Must set to null initially to avoid the following re-fetch hitting `ShortName` based equality check.
                ladder.Ruleset.Value = null;
                ladder.Ruleset.Value = resolvedRuleset;

                bool addedInfo = false;

                // assign teams
                foreach (var match in ladder.Matches)
                {
                    match.Team1.Value = ladder.Teams.FirstOrDefault(t => t.Acronym.Value == match.Team1Acronym);
                    match.Team2.Value = ladder.Teams.FirstOrDefault(t => t.Acronym.Value == match.Team2Acronym);

                    foreach (var conditional in match.ConditionalMatches)
                    {
                        conditional.Team1.Value = ladder.Teams.FirstOrDefault(t => t.Acronym.Value == conditional.Team1Acronym);
                        conditional.Team2.Value = ladder.Teams.FirstOrDefault(t => t.Acronym.Value == conditional.Team2Acronym);
                        conditional.Round.Value = match.Round.Value;
                    }
                }

                // assign progressions
                foreach (var pair in ladder.Progressions)
                {
                    var src = ladder.Matches.FirstOrDefault(p => p.ID == pair.SourceID);
                    var dest = ladder.Matches.FirstOrDefault(p => p.ID == pair.TargetID);

                    if (src == null)
                        continue;

                    if (dest != null)
                    {
                        if (pair.Losers)
                            src.LosersProgression.Value = dest;
                        else
                            src.Progression.Value = dest;
                    }
                }

                // link matches to rounds
                foreach (var round in ladder.Rounds)
                {
                    foreach (int id in round.Matches)
                    {
                        var found = ladder.Matches.FirstOrDefault(p => p.ID == id);

                        if (found != null)
                        {
                            found.Round.Value = round;
                            if (round.StartDate.Value > found.Date.Value)
                                found.Date.Value = round.StartDate.Value;
                        }
                    }
                }

                addedInfo |= addPlayers();
                addedInfo |= await addRoundBeatmaps().ConfigureAwait(false);
                addedInfo |= await addSeedingBeatmaps().ConfigureAwait(false);

                if (addedInfo)
                    saveChanges();

                ladder.CurrentMatch.Value = ladder.Matches.FirstOrDefault(p => p.Current.Value);

                ladder.Ruleset.BindValueChanged(r =>
                {
                    // Refetch player rank data on next startup as the ruleset has changed.
                    foreach (var team in ladder.Teams)
                    {
                        foreach (var player in team.Players)
                            player.Rank = null;
                    }

                    SaveChanges();
                });
            }
            catch (Exception e)
            {
                bracketLoadTaskCompletionSource.SetException(e);
                return;
            }

            Schedule(() =>
            {
                Ruleset.BindTo(ladder.Ruleset);

                dependencies.Cache(ladder);

                // Live multiplayer-room spectating sources match data straight from a room; the
                // default file-based IPC bridges to a stable client.
                ipc = ladder.UseMultiplayerSpectating.Value ? new MultiplayerMatchIPCInfo() : new FileBasedIPC();
                dependencies.CacheAs(ipc);

                // Cache the concrete connector too so its room-connection surface can be resolved.
                if (ipc is MultiplayerMatchIPCInfo multiplayerIpc)
                    dependencies.CacheAs(multiplayerIpc);

                Add(ipc);

                if (ipc is MultiplayerMatchIPCInfo matchIpc)
                {
                    // Play the room beatmap on loop while connected and not actively spectating gameplay.
                    Add(new TournamentLobbyMusic());

                    // Write ipc.txt-style files for broadcast graphics
                    Add(new LazerIpc());

                    MultiplayerClient.PresentMatch = (room, password) =>
                    {
                        if (room.RoomID is long roomId)
                            matchIpc.SetPendingInvite(new PendingInvite(roomId, password, room.Name));
                    };

                    // room password is only available inside the Activated closure, no way around this
                    // roundabout dance afaik
                    MultiplayerClient.PostNotification = n => n.Activated?.Invoke();
                }

                applyUISampleMuting();

                bracketLoadTaskCompletionSource.SetResult(true);

                initialisationText.Expire();
            });
        }

        private void applyUISampleMuting()
        {
            // We can't AddAdjustment on Audio.Samples directly because built-in skins
            // (Argon, ArgonPro, Triangles) fall back to Audio.Samples for gameplay hitsounds,
            // which would mute those too. Per-sample adjustment also fails because
            // SampleStore.Get returns a fresh Sample wrapper each call.
            //
            // Instead, attach the adjustment to the per-name SampleBassFactory cached
            // inside Audio.Samples for each muted-prefix lookup. The factory adopts each
            // created sample as its child (via AddItem in onPlay), so the adjustment
            // cascades to all future plays of that sample without affecting other factories.
            // Prefixes cover UI/ (hover/click) and Keyboard/ (textbox typing/caret feedback).
            string[] mutedPrefixes = { @"UI/", @"Keyboard/" };

            ladder.MuteUISounds.BindValueChanged(muted => uiSampleMuteAdjustment.Value = muted.NewValue ? 0 : 1, true);

            var mutedLookups = new HashSet<string>(StringComparer.Ordinal);

            foreach (string resource in Audio.Samples.GetAvailableResources())
            {
                if (!mutedPrefixes.Any(p => resource.StartsWith(p, StringComparison.Ordinal)))
                    continue;

                mutedLookups.Add(Path.ChangeExtension(resource, null));
            }

            // Force factory creation for each lookup so the reflection step below sees them.
            foreach (string lookup in mutedLookups)
                Audio.Samples.Get(lookup);

            var factoriesField = Audio.Samples.GetType().GetField("factories", BindingFlags.NonPublic | BindingFlags.Instance);

            if (factoriesField?.GetValue(Audio.Samples) is IDictionary factories)
            {
                var mutedFactories = new List<AdjustableAudioComponent>();

                lock (factories)
                {
                    foreach (DictionaryEntry entry in factories)
                    {
                        if (entry.Key is string name
                            && mutedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))
                            && entry.Value is AdjustableAudioComponent factory)
                        {
                            mutedFactories.Add(factory);
                        }
                    }
                }

                foreach (var factory in mutedFactories)
                    factory.AddAdjustment(AdjustableProperty.Volume, uiSampleMuteAdjustment);
            }
            else
            {
                Logger.Log("Tournament UI sample muting: could not access SampleStore.factories via reflection (framework changed?)", level: LogLevel.Important);
            }
        }

        /// <summary>
        /// Add missing player info based on user IDs.
        /// </summary>
        private bool addPlayers()
        {
            var playersRequiringPopulation = ladder.Teams
                                                   .SelectMany(t => t.Players)
                                                   .Where(p => string.IsNullOrEmpty(p.Username)
                                                               || p.CountryCode == CountryCode.Unknown
                                                               || p.Rank == null).ToList();

            if (playersRequiringPopulation.Count == 0)
                return false;

            for (int i = 0; i < playersRequiringPopulation.Count; i++)
            {
                var p = playersRequiringPopulation[i];
                PopulatePlayer(p, immediate: true);
                updateLoadProgressMessage($"Populating user stats ({i} / {playersRequiringPopulation.Count})");
            }

            return true;
        }

        /// <summary>
        /// Add missing beatmap info based on beatmap IDs
        /// </summary>
        private async Task<bool> addRoundBeatmaps()
        {
            var beatmapsRequiringPopulation = ladder.Rounds
                                                    .SelectMany(r => r.Beatmaps)
                                                    .Where(b => (b.Beatmap == null || b.Beatmap?.OnlineID == 0) && b.ID > 0).ToList();

            if (beatmapsRequiringPopulation.Count == 0)
                return false;

            for (int i = 0; i < beatmapsRequiringPopulation.Count; i++)
            {
                var b = beatmapsRequiringPopulation[i];

                var populated = await beatmapCache.GetBeatmapAsync(b.ID).ConfigureAwait(false);
                if (populated != null)
                    b.Beatmap = new TournamentBeatmap(populated);

                updateLoadProgressMessage($"Populating round beatmaps ({i} / {beatmapsRequiringPopulation.Count})");
            }

            return true;
        }

        /// <summary>
        /// Add missing beatmap info based on beatmap IDs
        /// </summary>
        private async Task<bool> addSeedingBeatmaps()
        {
            var beatmapsRequiringPopulation = ladder.Teams
                                                    .SelectMany(r => r.SeedingResults)
                                                    .SelectMany(r => r.Beatmaps)
                                                    .Where(b => (b.Beatmap == null || b.Beatmap.OnlineID == 0) && b.ID > 0).ToList();

            if (beatmapsRequiringPopulation.Count == 0)
                return false;

            for (int i = 0; i < beatmapsRequiringPopulation.Count; i++)
            {
                var b = beatmapsRequiringPopulation[i];

                var populated = await beatmapCache.GetBeatmapAsync(b.ID).ConfigureAwait(false);
                if (populated != null)
                    b.Beatmap = new TournamentBeatmap(populated);

                updateLoadProgressMessage($"Populating seeding beatmaps ({i} / {beatmapsRequiringPopulation.Count})");
            }

            return true;
        }

        private void updateLoadProgressMessage(string s) => Schedule(() => initialisationText.Text = s);

        public void PopulatePlayer(TournamentUser user, Action? success = null, Action? failure = null, bool immediate = false)
        {
            var req = new GetUserRequest(user.OnlineID, ladder.Ruleset.Value);

            if (immediate)
            {
                API.Perform(req);
                populate();
            }
            else
            {
                req.Success += _ => { populate(); };
                req.Failure += _ =>
                {
                    user.OnlineID = 1;
                    failure?.Invoke();
                };

                API.Queue(req);
            }

            void populate()
            {
                var res = req.Response;

                if (res == null)
                    return;

                user.OnlineID = res.Id;

                user.Username = res.Username;
                user.CoverUrl = res.CoverUrl;
                user.CountryCode = res.CountryCode;
                user.Rank = res.Statistics?.GlobalRank;

                success?.Invoke();
            }
        }

        public void SaveChanges()
        {
            if (!bracketLoadTaskCompletionSource.Task.IsCompletedSuccessfully)
            {
                Logger.Log("Inhibiting bracket save as bracket parsing failed");
                return;
            }

            saveChanges();
        }

        private void saveChanges()
        {
            // Serialise before opening stream for writing, so if there's a failure it will leave the file in the previous state.
            string serialisedLadder = GetSerialisedLadder();

            using (var stream = storage.CreateFileSafely(BRACKET_FILENAME))
            using (var sw = new StreamWriter(stream))
                sw.Write(serialisedLadder);
        }

        public string GetSerialisedLadder()
        {
            foreach (var r in ladder.Rounds)
                r.Matches = ladder.Matches.Where(p => p.Round.Value == r).Select(p => p.ID).ToList();

            ladder.Progressions = ladder.Matches.Where(p => p.Progression.Value != null).Select(p => new TournamentProgression(p.ID, p.Progression.Value.AsNonNull().ID)).Concat(
                                            ladder.Matches.Where(p => p.LosersProgression.Value != null).Select(p => new TournamentProgression(p.ID, p.LosersProgression.Value.AsNonNull().ID, true)))
                                        .ToList();

            return JsonConvert.SerializeObject(ladder,
                new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore,
                    DefaultValueHandling = DefaultValueHandling.Ignore,
                    Converters = new JsonConverter[] { new JsonPointConverter() }
                });
        }

        protected override UserInputManager CreateUserInputManager() => new TournamentInputManager();

        private partial class TournamentInputManager : UserInputManager
        {
            protected override MouseButtonEventManager CreateButtonEventManagerFor(MouseButton button)
            {
                switch (button)
                {
                    case MouseButton.Right:
                        return new RightMouseManager(button);
                }

                return base.CreateButtonEventManagerFor(button);
            }

            private class RightMouseManager : MouseButtonEventManager
            {
                public RightMouseManager(MouseButton button)
                    : base(button)
                {
                }

                public override bool EnableDrag => true; // allow right-mouse dragging for absolute scroll in scroll containers.
                public override bool EnableClick => true;
                public override bool ChangeFocusOnClick => false;
            }
        }
    }
}
