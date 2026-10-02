// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Threading;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays.Settings;
using osu.Game.Screens;
using osu.Game.Screens.Play.PlayerSettings;
using osu.Game.Skinning;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;
using osu.Game.Tournament.Screens.Gameplay.Components;
using osu.Game.Tournament.Screens.MapPool;
using osu.Game.Tournament.Screens.TeamWin;
using osuTK.Graphics;

namespace osu.Game.Tournament.Screens.Gameplay
{
    public partial class GameplayScreen : BeatmapInfoScreen
    {
        private readonly BindableBool warmup = new BindableBool();

        public readonly Bindable<TourneyState> State = new Bindable<TourneyState>();
        private MatchIPCInfo ipc = null!;

        [Resolved]
        private TournamentSceneManager? sceneManager { get; set; }

        [Resolved]
        private TournamentMatchChatDisplay chat { get; set; } = null!;

        private Container chroma = null!;

        // Multiplayer-room spectating: the connector (if active), the host container for the embedded
        // spectating display, and the current round's display screen.
        private MultiplayerMatchIPCInfo? multiplayerIpc;
        private Container gameplayHost = null!;
        private TournamentSpectatorScreen? spectatorScreen;

        [BackgroundDependencyLoader]
        private void load(MatchIPCInfo ipc, AudioManager audio, SkinManager skins, OsuConfigManager config)
        {
            this.ipc = ipc;

            LabelledSwitchButton chatToggle;
            ControlPanel controlPanel;

            AddRangeInternal(new Drawable[]
            {
                new TourneyVideo("gameplay")
                {
                    Loop = true,
                    RelativeSizeAxes = Axes.Both,
                },
                header = new MatchHeader
                {
                    ShowLogo = false,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Y = 110,
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Children = new[]
                    {
                        chroma = new Container
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                            Height = 512,
                            Children = new Drawable[]
                            {
                                new ChromaArea
                                {
                                    Name = "Left chroma",
                                    RelativeSizeAxes = Axes.Both,
                                    Width = 0.5f,
                                },
                                new ChromaArea
                                {
                                    Name = "Right chroma",
                                    RelativeSizeAxes = Axes.Both,
                                    Anchor = Anchor.TopRight,
                                    Origin = Anchor.TopRight,
                                    Width = 0.5f,
                                }
                            }
                        },
                    }
                },
                scoreDisplay = new TournamentMatchScoreDisplay
                {
                    Y = -147,
                    Anchor = Anchor.BottomCentre,
                    Origin = Anchor.TopCentre,
                },
                controlPanel = new ControlPanel
                {
                    Children = new Drawable[]
                    {
                        new LabelledSwitchButton
                        {
                            Label = "Warmup",
                            Current = warmup,
                        },
                        chatToggle = new LabelledSwitchButton
                        {
                            Label = "Show chat",
                        },
                        new SettingsSlider<int>
                        {
                            LabelText = "Chroma width",
                            Current = LadderInfo.ChromaKeyWidth,
                            KeyboardStep = 1,
                        },
                        new SettingsSlider<int>
                        {
                            LabelText = "Players per team",
                            Current = LadderInfo.PlayersPerTeam,
                            KeyboardStep = 1,
                        },
                    }
                }
            });

            multiplayerIpc = ipc as MultiplayerMatchIPCInfo;

            if (multiplayerIpc == null)
            {
                // File-IPC mode: the "Show chat" toggle doubles as the manual Idle/Playing switch.
                State.BindValueChanged(_ => chatToggle.Current.Value = State.Value == TourneyState.Idle, true);
                chatToggle.Current.BindValueChanged(v => State.Value = v.NewValue ? TourneyState.Idle : TourneyState.Playing);
            }
            else
            {
                // MP-room mode: State is connector-owned, so the toggle overrides chat visibility only.
                chatToggle.Current.BindValueChanged(v =>
                {
                    if (v.NewValue)
                        chat.Expand();
                    else
                        chat.Contract();
                });
            }

            LadderInfo.ChromaKeyWidth.BindValueChanged(width => chroma.Width = width.NewValue, true);

            warmup.BindValueChanged(w => header.ShowScores = !w.NewValue, true);

            if (multiplayerIpc != null)
            {
                // remove green chroma boxes
                chroma.Clear(true);

                // Embedded spectating display, hosted over the chroma area (sized to the chroma region).
                chroma.Add(gameplayHost = new Container { RelativeSizeAxes = Axes.Both, Alpha = 0 });

                // Volume controls for the embedded spectating audio (only the tourney client plays it in
                // MP-room mode). Bound straight to the framework audio bindables, which already persist
                // across restarts via framework config.
                controlPanel.AddRange(new Drawable[]
                {
                    new ControlPanel.Spacer(),
                    new TournamentSpriteText
                    {
                        Text = "Skin",
                        Font = OsuFont.GetFont(weight: FontWeight.Bold, size: 16),
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                    },
                    // Drives the whole client's skin, which the embedded spectator gameplay renders with.
                    // Random skin is dropped: it re-rolls on every reselect, which is useless for a streamer.
                    new SettingsDropdown<Live<SkinInfo>>
                    {
                        Items = skins.GetAllUsableSkins().Where(s => s.ID != SkinInfo.RANDOM_SKIN),
                        Current = skins.CurrentSkinInfo,
                    },
                    new ControlPanel.Spacer(),
                    new VisualSettings
                    {
                        RelativeSizeAxes = Axes.X,
                        Width = 1,
                    },
                    new LabelledSwitchButton
                    {
                        Label = "Beatmap hitsounds",
                        Current = config.GetBindable<bool>(OsuSetting.BeatmapHitsounds),
                    },
                    new ControlPanel.Spacer(),
                    new TournamentSpriteText
                    {
                        Text = "Volume",
                        Font = OsuFont.GetFont(weight: FontWeight.Bold, size: 16),
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                    },
                    new SettingsSlider<double>
                    {
                        LabelText = "Master",
                        Current = audio.Volume,
                        KeyboardStep = 0.01f,
                    },
                    new SettingsSlider<double>
                    {
                        LabelText = "Music",
                        Current = audio.VolumeTrack,
                        KeyboardStep = 0.01f,
                    },
                    new SettingsSlider<double>
                    {
                        LabelText = "Effects",
                        Current = audio.VolumeSample,
                        KeyboardStep = 0.01f,
                    },
                    new ControlPanel.Spacer(),
                    new SettingsSlider<int>
                    {
                        LabelText = "Live edge buffer (ms)",
                        Current = LadderInfo.LiveEdgeBuffer,
                        KeyboardStep = 50,
                    },
                });
            }
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            State.BindTo(ipc.State);
            State.BindValueChanged(_ => updateState(), true);

            if (multiplayerIpc != null)
            {
                multiplayerIpc.IsConnected.BindValueChanged(c =>
                {
                    gameplayHost.FadeTo(c.NewValue ? 1 : 0, 300);

                    // On disconnect the connector returns to Idle (not WaitingForClients), so tear down
                    // here too — otherwise the stale screen lingers and a later reconnect won't re-push.
                    if (!c.NewValue)
                        teardownSpectatorScreen();
                }, true);

                // Auto-advance / push driven off the non-Drawable signal so a hidden screen can't miss it.
                multiplayerIpc.HasActiveSpectatorPlayers.BindValueChanged(_ => updateSpectatorScreen(), true);

                // A new round is loading — tear down the previous round's tiles synchronously so they
                // don't persist into the next round (never via Schedule, which a hidden screen would defer).
                State.BindValueChanged(s =>
                {
                    if (s.NewValue == TourneyState.WaitingForClients)
                        teardownSpectatorScreen();
                });

                // Next map picked after a round: tear down the tiles so the global-beatmap lease
                // (SpectatorScreen holds it) is released and lobby music can play the new map. Synchronous
                // so it fires even while hidden. The screen advance is left to updateState's Idle path —
                // the connector drops Ranking -> Idle on this same map change.
                ipc.Beatmap.BindValueChanged(_ =>
                {
                    if (State.Value == TourneyState.Ranking || State.Value == TourneyState.Idle)
                        teardownSpectatorScreen();
                });
            }
        }

        private void updateSpectatorScreen()
        {
            if (multiplayerIpc == null)
                return;

            if (multiplayerIpc.HasActiveSpectatorPlayers.Value && spectatorScreen == null)
                pushSpectatorScreen();
        }

        private void pushSpectatorScreen()
        {
            // Synchronous teardown of any previous round before pushing the new one.
            gameplayHost.Clear(true);

            var stack = new OsuScreenStack { RelativeSizeAxes = Axes.Both };
            gameplayHost.Add(stack);

            spectatorScreen = new TournamentSpectatorScreen(multiplayerIpc!.CurrentParticipants.ToArray());
            stack.Push(spectatorScreen);
        }

        private void teardownSpectatorScreen()
        {
            gameplayHost.Clear(true);
            spectatorScreen = null;
        }

        protected override void CurrentMatchChanged(ValueChangedEvent<TournamentMatch?> match)
        {
            base.CurrentMatchChanged(match);

            if (match.NewValue == null)
                return;

            warmup.Value = match.NewValue.Team1Score.Value + match.NewValue.Team2Score.Value == 0;
            scheduledScreenChange?.Cancel();
        }

        private ScheduledDelegate? scheduledScreenChange;
        private ScheduledDelegate? scheduledContract;

        private TournamentMatchScoreDisplay scoreDisplay = null!;

        private TourneyState lastState;
        private MatchHeader header = null!;

        private void contract()
        {
            if (!IsLoaded)
                return;

            scheduledContract?.Cancel();

            SongBar.Expanded = false;
            scoreDisplay.FadeOut(100);
            using (chat.BeginDelayedSequence(500))
                chat.Expand();
        }

        private void expand()
        {
            if (!IsLoaded)
                return;

            scheduledContract?.Cancel();

            chat.Contract();

            using (BeginDelayedSequence(300))
            {
                scoreDisplay.FadeIn(100);
                SongBar.Expanded = true;
            }
        }

        private void updateState()
        {
            try
            {
                scheduledScreenChange?.Cancel();

                if (State.Value == TourneyState.Ranking)
                {
                    if (warmup.Value || CurrentMatch.Value == null) return;

                    // count once on the transition into Ranking; a call to Show() can re-run updateState() while still
                    // in Ranking state
                    if (lastState != TourneyState.Ranking)
                    {
                        if (ipc.Score1.Value > ipc.Score2.Value)
                            CurrentMatch.Value.Team1Score.Value++;
                        else
                            CurrentMatch.Value.Team2Score.Value++;
                    }
                }

                switch (State.Value)
                {
                    case TourneyState.Idle:
                        contract();

                        if (LadderInfo.AutoProgressScreens.Value)
                        {
                            const float delay_before_progression = 1000;

                            // if we've returned to idle and the last screen was ranking
                            // we should automatically proceed after a short delay
                            if (lastState == TourneyState.Ranking && !warmup.Value)
                            {
                                if (CurrentMatch.Value?.Completed.Value == true)
                                    scheduledScreenChange = Scheduler.AddDelayed(() => { sceneManager?.SetScreen(typeof(TeamWinScreen)); }, delay_before_progression);
                                else if (CurrentMatch.Value?.Completed.Value == false)
                                    scheduledScreenChange = Scheduler.AddDelayed(() => { sceneManager?.SetScreen(typeof(MapPoolScreen)); }, delay_before_progression);
                            }
                        }

                        break;

                    case TourneyState.Ranking:
                        scheduledContract = Scheduler.AddDelayed(contract, 10000);
                        break;

                    default:
                        expand();
                        break;
                }
            }
            finally
            {
                lastState = State.Value;
            }
        }

        public override void Hide()
        {
            scheduledScreenChange?.Cancel();
            base.Hide();
        }

        public override void Show()
        {
            updateState();

            // Re-check on show in case the Playing transition arrived while this screen was hidden.
            updateSpectatorScreen();

            base.Show();
        }

        private partial class ChromaArea : CompositeDrawable
        {
            [BackgroundDependencyLoader]
            private void load()
            {
                // chroma key area for stable gameplay. The player grid always fully packs its area
                // (two teams' worth of tiles), so the chroma covers the whole region.
                Colour = new Color4(0, 255, 0, 255);

                InternalChild = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                };
            }
        }
    }
}
