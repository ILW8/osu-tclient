// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Game.Tests.Visual.Spectator;

namespace osu.Game.Tests.Visual.Gameplay
{
    public partial class TestSceneSpectatorClientRestartWatching : OsuTestScene
    {
        private const int user_id = 1234;

        private RecordingSpectatorClient client = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("add spectator client", () => Child = client = new RecordingSpectatorClient());
        }

        [Test]
        public void TestRestartsWatchOfUserWithoutState()
        {
            AddStep("watch user twice", () =>
            {
                client.WatchUser(user_id);
                client.WatchUser(user_id);
            });
            AddAssert("user has no state", () => client.WatchedUserStates.ContainsKey(user_id), () => Is.False);

            AddStep("restart watching", () => client.RestartWatching());
            AddAssert("watch ended and restarted", () => client.ServerCalls, () => Is.EqualTo(new[] { "start", "end", "start" }));

            // Ref counts are untouched: one of the two watchers releasing must not end the server-side watch.
            AddStep("release one watcher", () => client.StopWatchingUser(user_id));
            AddWaitStep("wait for deferred release", 3);
            AddAssert("still watched", () => client.ServerCalls, () => Is.EqualTo(new[] { "start", "end", "start" }));
        }

        private partial class RecordingSpectatorClient : TestSpectatorClient
        {
            public readonly List<string> ServerCalls = new List<string>();

            protected override Task WatchUserInternal(int userId)
            {
                ServerCalls.Add("start");
                return base.WatchUserInternal(userId);
            }

            protected override Task StopWatchingUserInternal(int userId)
            {
                ServerCalls.Add("end");
                return base.StopWatchingUserInternal(userId);
            }
        }
    }
}
