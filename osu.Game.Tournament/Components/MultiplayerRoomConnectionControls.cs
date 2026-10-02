// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Game.Graphics;
using osu.Game.Graphics.UserInterface;
using osu.Game.Tournament.IPC;
using osuTK;

namespace osu.Game.Tournament.Components
{
    /// <summary>
    /// Operator controls for connecting the tournament client to a live multiplayer room as a spectator.
    /// </summary>
    public partial class MultiplayerRoomConnectionControls : FillFlowContainer
    {
        private readonly MultiplayerMatchIPCInfo multiplayerIpc;

        private OsuTextBox roomIdTextBox = null!;
        private OsuPasswordTextBox passwordTextBox = null!;

        public MultiplayerRoomConnectionControls(MultiplayerMatchIPCInfo multiplayerIpc)
        {
            this.multiplayerIpc = multiplayerIpc;

            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Vertical;
            Spacing = new Vector2(0, 5f);
        }

        public override bool AcceptsFocus => true;

        [BackgroundDependencyLoader]
        private void load()
        {
            TourneyButton connectButton;
            TourneyButton disconnectButton;
            TourneyButton panicButton;
            TournamentSpriteText statusText;
            TextFlowContainer inviteText;
            TourneyButton acceptButton;
            TourneyButton dismissButton;

            Children = new Drawable[]
            {
                new TournamentSpriteText
                {
                    Text = "Multiplayer Room",
                    Font = OsuFont.GetFont(weight: FontWeight.Bold, size: 16),
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                },
                inviteText = new TextFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Colour = Colour4.Orange,
                    Alpha = 0,
                },
                acceptButton = new TourneyButton
                {
                    RelativeSizeAxes = Axes.X,
                    Text = "Accept invite",
                    Alpha = 0,
                    Action = multiplayerIpc.AcceptPendingInvite,
                },
                dismissButton = new TourneyButton
                {
                    RelativeSizeAxes = Axes.X,
                    Text = "Dismiss",
                    Alpha = 0,
                    Action = multiplayerIpc.DismissPendingInvite,
                },
                roomIdTextBox = new OsuTextBox
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 30,
                    PlaceholderText = "Room ID",
                    TabbableContentContainer = this,
                },
                passwordTextBox = new OsuPasswordTextBox
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 30,
                    PlaceholderText = "Password (optional)",
                    TabbableContentContainer = this,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 40,
                    Children = new Drawable[]
                    {
                        connectButton = new TourneyButton
                        {
                            RelativeSizeAxes = Axes.X,
                            Text = "Connect",
                            Action = performConnect,
                        },
                        disconnectButton = new TourneyButton
                        {
                            RelativeSizeAxes = Axes.X,
                            Text = "Disconnect",
                            Alpha = 0,
                            Action = () => fireAndForget(multiplayerIpc.Disconnect()),
                            // Set after Action: osu-framework's Button.Action setter side-effects
                            // Enabled.Value = true, which would otherwise undo the explicit disable here.
                            Enabled = { Value = false },
                        },
                    },
                },
                panicButton = new TourneyButton
                {
                    RelativeSizeAxes = Axes.X,
                    Text = "Panic",
                    Action = multiplayerIpc.Panic,
                    BackgroundColour = Colour4.FromHex("#FF0000"),
                },
                statusText = new TournamentSpriteText
                {
                    Text = "Disconnected",
                    Font = OsuFont.GetFont(size: 12),
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Colour = OsuColour.Gray(0.6f),
                },
            };

            roomIdTextBox.OnCommit += (_, _) => performConnect();
            passwordTextBox.OnCommit += (_, _) => performConnect();

            multiplayerIpc.IsConnected.BindValueChanged(connected =>
            {
                updateConnectEnabled();
                disconnectButton.Enabled.Value = connected.NewValue;
                panicButton.Enabled.Value = connected.NewValue;

                connectButton.FadeTo(connected.NewValue ? 0 : 1, 200);
                disconnectButton.FadeTo(connected.NewValue ? 1 : 0, 200);

                statusText.Text = connected.NewValue
                    ? $"Connected (Room {multiplayerIpc.ConnectedRoomId.Value})"
                    : "Disconnected";
                statusText.Colour = connected.NewValue ? Colour4.LightGreen : OsuColour.Gray(0.6f);
            }, true);

            multiplayerIpc.IsConnectOnCooldown.BindValueChanged(cooldown =>
            {
                updateConnectEnabled();
                acceptButton.Enabled.Value = !cooldown.NewValue;
            }, true);

            void updateConnectEnabled() => connectButton.Enabled.Value = !multiplayerIpc.IsConnected.Value && !multiplayerIpc.IsConnectOnCooldown.Value;

            multiplayerIpc.ConnectionError.BindValueChanged(error =>
            {
                if (error.NewValue != null)
                {
                    statusText.Text = error.NewValue;
                    statusText.Colour = Colour4.OrangeRed;
                }
            });

            multiplayerIpc.PendingInvite.BindValueChanged(invite =>
            {
                if (invite.NewValue != null)
                {
                    inviteText.Clear();
                    inviteText.AddText($"Invite to room {invite.NewValue.RoomId} ({invite.NewValue.RoomName})",
                        s => s.Font = OsuFont.GetFont(size: 12));
                    inviteText.FadeIn(200);
                    acceptButton.FadeIn(200);
                    dismissButton.FadeIn(200);
                }
                else
                {
                    inviteText.FadeOut(200);
                    acceptButton.FadeOut(200);
                    dismissButton.FadeOut(200);
                }
            }, true);
        }

        protected override void OnFocus(FocusEvent e)
        {
            base.OnFocus(e);
            GetContainingFocusManager()?.ChangeFocus(roomIdTextBox);
        }

        private void performConnect()
        {
            if (multiplayerIpc.IsConnected.Value || multiplayerIpc.IsConnectOnCooldown.Value)
                return;

            if (!long.TryParse(roomIdTextBox.Text, out long roomId))
            {
                Logger.Log("[MultiplayerRoomConnectionControls] Invalid room ID", LoggingTarget.Runtime, LogLevel.Error);
                return;
            }

            string? password = string.IsNullOrEmpty(passwordTextBox.Text) ? null : passwordTextBox.Text;
            fireAndForget(multiplayerIpc.Connect(roomId, password));
        }

        private static void fireAndForget(Task task)
        {
            task.ContinueWith(t =>
            {
                Logger.Log(t.Exception?.GetBaseException().ToString() ?? "Room connection task failed.", LoggingTarget.Runtime, LogLevel.Error);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
