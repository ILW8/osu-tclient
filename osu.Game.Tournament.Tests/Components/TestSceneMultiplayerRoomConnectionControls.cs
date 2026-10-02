// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Tests.Visual.Multiplayer;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IPC;

namespace osu.Game.Tournament.Tests.Components
{
    public partial class TestSceneMultiplayerRoomConnectionControls : MultiplayerTestScene
    {
        private MultiplayerMatchIPCInfo connector = null!;

        public override void SetUpSteps()
        {
            base.SetUpSteps();

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
        public void TestConnectDisabledForCooldownAfterDisconnect()
        {
            Room room = null!;

            AddAssert("connect enabled", () => connectButton.Enabled.Value);

            AddStep("add room", () => MultiplayerClient.AddServerSideRoom(room = CreateDefaultRoom(), API.LocalUser.Value));
            AddStep("connect", () => connector.Connect(room.RoomID!.Value).FireAndForget());
            AddUntilStep("connected", () => connector.IsConnected.Value);
            AddAssert("connect disabled", () => !connectButton.Enabled.Value);

            AddStep("disconnect", () => connector.Disconnect().FireAndForget());
            AddUntilStep("disconnected", () => !connector.IsConnected.Value);
            AddAssert("connect still disabled", () => !connectButton.Enabled.Value);

            AddUntilStep("connect enabled after cooldown", () => connectButton.Enabled.Value);
        }

        private TourneyButton connectButton => this.ChildrenOfType<TourneyButton>().Single(b => b.Text == "Connect");
    }
}
