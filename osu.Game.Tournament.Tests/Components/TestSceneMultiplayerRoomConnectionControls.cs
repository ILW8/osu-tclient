// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Game.Online.Multiplayer;
using osu.Game.Tests.Visual;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IPC;

namespace osu.Game.Tournament.Tests.Components
{
    public partial class TestSceneMultiplayerRoomConnectionControls : OsuTestScene
    {
        private MultiplayerMatchIPCInfo connector = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create controls", () =>
            {
                connector = new MultiplayerMatchIPCInfo();

                Children = new Drawable[]
                {
                    connector,
                    new MultiplayerRoomConnectionControls(connector) { Width = 0.3f },
                };
            });
        }

        [Test]
        public void TestConnectBlockedDuringCooldownAfterDisconnect()
        {
            AddAssert("connect enabled", () => connectButton.Enabled.Value);

            AddStep("disconnect", () => connector.Disconnect().FireAndForget());
            AddUntilStep("connect disabled", () => !connectButton.Enabled.Value);

            AddStep("receive invite", () => connector.SetPendingInvite(new PendingInvite(1, null, "room")));
            AddUntilStep("invite pending", () => connector.PendingInvite.Value != null);
            AddStep("accept invite", () => connector.AcceptPendingInvite());
            AddAssert("invite still pending", () => connector.PendingInvite.Value != null);

            AddUntilStep("connect enabled after cooldown", () => connectButton.Enabled.Value);
        }

        private TourneyButton connectButton => this.ChildrenOfType<TourneyButton>().Single(b => b.Text == "Connect");
    }
}
