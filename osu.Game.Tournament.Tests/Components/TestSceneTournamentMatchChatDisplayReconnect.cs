// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Game.Online.Chat;
using osu.Game.Tests.Visual;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;

namespace osu.Game.Tournament.Tests.Components
{
    public partial class TestSceneTournamentMatchChatDisplayReconnect : OsuTestScene
    {
        private MultiplayerMatchIPCInfo ipc = null!;
        private TournamentMatchChatDisplay chatDisplay = null!;

        [Test]
        public void TestChannelAlreadyJoinedBeforeDisplayJoins()
        {
            Channel websocketJoined = null!;

            AddStep("create chat display", () => Child = new DependencyProvidingContainer
            {
                RelativeSizeAxes = Axes.Both,
                CachedDependencies =
                [
                    (typeof(LadderInfo), new LadderInfo()),
                    // Not loaded: the chat display only needs the source type and its chat channel bindable.
                    (typeof(MatchIPCInfo), ipc = new MultiplayerMatchIPCInfo()),
                ],
                Child = chatDisplay = new TournamentMatchChatDisplay
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                },
            });
            AddUntilStep("wait for load", () => chatDisplay.IsLoaded);

            // On a reconnect the server's chat.channel.join websocket event lands before the IPC publishes the channel id.
            AddStep("channel joined via websocket", () =>
                websocketJoined = chatDisplay.ChildrenOfType<ChannelManager>().Single().JoinChannel(new Channel { Id = 2, Type = ChannelType.Multiplayer }));
            AddStep("connect", () => ipc.ChatChannel.Value = "2");
            AddAssert("display shows manager's channel", () => chatDisplay.Channel.Value, () => Is.SameAs(websocketJoined));
        }
    }
}
