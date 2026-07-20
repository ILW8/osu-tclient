// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Game.Utils.FramePacing;

namespace osu.Game.Tests.Visual.Utils
{
    public partial class TestSceneFramePacingRecorder : OsuTestScene
    {
        private FramePacingRecorder recorder = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create recorder", () => Child = recorder = new FramePacingRecorder());
        }

        [Test]
        public void TestDoesNotRecordUntilStarted()
        {
            AddWaitStep("wait some frames", 5);
            AddStep("stop (no-op)", () => recorder.StopRecording());
            AddAssert("no statistics produced", () => recorder.LastUpdateStatistics == null);
        }

        [Test]
        public void TestRecordsUpdateFramesWhileRecording()
        {
            AddStep("start recording", () => recorder.StartRecording());
            AddAssert("is recording", () => recorder.IsRecording);
            AddWaitStep("wait some frames", 10);
            AddStep("stop recording", () => recorder.StopRecording());

            AddAssert("is not recording", () => !recorder.IsRecording);
            AddUntilStep("update statistics produced", () => recorder.LastUpdateStatistics != null);
            AddAssert("update frames captured", () => recorder.LastUpdateStatistics!.Value.FrameCount > 0);
        }

        [Test]
        public void TestMarkersAreRetained()
        {
            AddStep("start recording", () => recorder.StartRecording());
            AddWaitStep("wait some frames", 3);
            AddStep("add two markers", () =>
            {
                recorder.AddMarker();
                recorder.AddMarker();
            });
            AddWaitStep("wait some frames", 3);
            AddStep("stop recording", () => recorder.StopRecording());

            AddAssert("markers retained", () => recorder.LastMarkerCount == 2);
        }

        [Test]
        public void TestRestartClearsPreviousSamples()
        {
            AddStep("start recording", () => recorder.StartRecording());
            AddWaitStep("wait many frames", 20);
            AddStep("stop recording", () => recorder.StopRecording());

            int firstCount = 0;
            AddUntilStep("first session captured", () => recorder.LastUpdateStatistics != null);
            AddStep("note first count", () => firstCount = recorder.LastUpdateStatistics!.Value.FrameCount);

            AddStep("start again", () => recorder.StartRecording());
            AddWaitStep("wait few frames", 3);
            AddStep("stop again", () => recorder.StopRecording());

            AddAssert("second session is shorter", () => recorder.LastUpdateStatistics!.Value.FrameCount < firstCount);
        }

        [Test]
        public void TestStatisticsAreSane()
        {
            AddStep("start recording", () => recorder.StartRecording());
            AddWaitStep("wait some frames", 20);
            AddStep("stop recording", () => recorder.StopRecording());

            AddUntilStep("statistics produced", () => recorder.LastUpdateStatistics != null);
            AddAssert("median is positive", () => recorder.LastUpdateStatistics!.Value.Median > 0);
            AddAssert("maximum is at least median", () => recorder.LastUpdateStatistics!.Value.Maximum >= recorder.LastUpdateStatistics!.Value.Median);
        }

        [Test]
        public void TestSessionIsWrittenToDisk()
        {
            string[] writtenLines = null!;

            AddStep("start recording", () => recorder.StartRecording());
            AddWaitStep("wait some frames", 10);
            AddStep("stop recording", () => recorder.StopRecording());

            // LastSessionPath is published from a background task after the write completes, so it
            // must be polled for rather than asserted on directly.
            AddUntilStep("session path published", () => recorder.LastSessionPath != null);
            AddAssert("session file exists on disk", () => File.Exists(recorder.LastSessionPath));

            AddStep("read written file", () => writtenLines = File.ReadAllLines(recorder.LastSessionPath!));
            AddAssert("file starts with CSV header", () => writtenLines[0] == FramePacingCsvWriter.Header);
            AddAssert("file has at least one data row", () => writtenLines.Length > 1);
        }
    }
}
