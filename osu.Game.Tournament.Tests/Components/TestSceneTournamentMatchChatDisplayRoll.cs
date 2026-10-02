// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Chat;
using osu.Game.Online.Multiplayer;
using osu.Game.Overlays.Chat;
using osu.Game.Tests.Visual;
using osu.Game.Tests.Visual.Multiplayer;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.IPC;
using osu.Game.Tournament.Models;

namespace osu.Game.Tournament.Tests.Components
{
    public partial class TestSceneTournamentMatchChatDisplayRoll : MultiplayerTestScene
    {
        private MultiplayerMatchIPCInfo ipc = null!;
        private TournamentMatchChatDisplay chatDisplay = null!;

        public override void SetUpSteps()
        {
            base.SetUpSteps();

            AddStep("join room", () => JoinRoom(CreateDefaultRoom()));
            WaitForJoined();

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
            AddStep("set room chat channel", () => ipc.ChatChannel.Value = "1");
            AddAssert("channel joined", () => chatDisplay.Channel.Value?.Id, () => Is.EqualTo(1));
        }

        [Test]
        public void TestRollShownInChat()
        {
            AddStep("add user", () => MultiplayerClient.AddUser(new APIUser { Id = PLAYER_1_ID }));
            AddStep("user rolls", () => MultiplayerClient.SendUserMatchRequest(PLAYER_1_ID, new RollRequest { Max = 100 }).WaitSafely());

            AddUntilStep("roll shown in chat", () => chatDisplay.ChildrenOfType<ChatLine>().Any(line =>
                line.Message is InfoMessage
                && line.Message.Content.StartsWith($"User {PLAYER_1_ID} rolled ", StringComparison.Ordinal)
                && line.Message.Content.EndsWith(" out of 100.", StringComparison.Ordinal)));
        }
    }
}
