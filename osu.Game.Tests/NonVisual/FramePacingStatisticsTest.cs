// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using osu.Game.Utils.FramePacing;

namespace osu.Game.Tests.NonVisual
{
    [TestFixture]
    public class FramePacingStatisticsTest
    {
        private static FrameSample[] samplesFrom(params double[] deltas) =>
            deltas.Select((d, i) => new FrameSample(i, i * 1000, d, 0, 0, 0, 0)).ToArray();

        [Test]
        public void TestEmptyInput()
        {
            var stats = FramePacingStatistics.Compute(Array.Empty<FrameSample>(), 20.0);

            ClassicAssert.AreEqual(0, stats.FrameCount);
            ClassicAssert.AreEqual(0.0, stats.Median);
            ClassicAssert.AreEqual(0.0, stats.Percentile95);
            ClassicAssert.AreEqual(0.0, stats.Percentile99);
            ClassicAssert.AreEqual(0.0, stats.Percentile999);
            ClassicAssert.AreEqual(0.0, stats.Maximum);
            ClassicAssert.AreEqual(0, stats.FramesOverThreshold);
            ClassicAssert.AreEqual(0.0, stats.MaximumConsecutiveDelta);
        }

        [Test]
        public void TestEmptyInputToStringReportsNoSamples()
        {
            var stats = FramePacingStatistics.Compute(Array.Empty<FrameSample>(), 20.0);

            ClassicAssert.AreEqual("no samples", stats.ToString());
        }

        [Test]
        public void TestSingleSampleHasNoConsecutiveDelta()
        {
            var stats = FramePacingStatistics.Compute(samplesFrom(16.7), 20.0);

            ClassicAssert.AreEqual(1, stats.FrameCount);
            ClassicAssert.AreEqual(16.7, stats.Maximum);
            ClassicAssert.AreEqual(0.0, stats.MaximumConsecutiveDelta);
        }

        [Test]
        public void TestPercentilesUseNearestRank()
        {
            // 100 samples, values 1..100.
            var stats = FramePacingStatistics.Compute(samplesFrom(Enumerable.Range(1, 100).Select(i => (double)i).ToArray()), 1000.0);

            ClassicAssert.AreEqual(100, stats.FrameCount);
            ClassicAssert.AreEqual(50.0, stats.Median);
            ClassicAssert.AreEqual(95.0, stats.Percentile95);
            ClassicAssert.AreEqual(99.0, stats.Percentile99);
            ClassicAssert.AreEqual(100.0, stats.Percentile999);
            ClassicAssert.AreEqual(100.0, stats.Maximum);
        }

        [Test]
        public void TestFramesOverThresholdIsExclusive()
        {
            var stats = FramePacingStatistics.Compute(samplesFrom(10.0, 20.0, 20.1, 50.0), 20.0);

            // 20.0 is not over the threshold; 20.1 and 50.0 are.
            ClassicAssert.AreEqual(2, stats.FramesOverThreshold);
        }

        [Test]
        public void TestMaximumConsecutiveDeltaIsAbsolute()
        {
            // Steps: +6.7, +30.0, -33.0, +1.6
            var stats = FramePacingStatistics.Compute(samplesFrom(10.0, 16.7, 46.7, 13.7, 15.3), 20.0);

            ClassicAssert.AreEqual(33.0, stats.MaximumConsecutiveDelta, 1e-9);
        }

        [Test]
        public void TestSmoothSeriesHasSmallConsecutiveDelta()
        {
            var stats = FramePacingStatistics.Compute(samplesFrom(16.6, 16.7, 16.6, 16.7, 16.6), 20.0);

            ClassicAssert.AreEqual(0, stats.FramesOverThreshold);
            ClassicAssert.Less(stats.MaximumConsecutiveDelta, 0.2);
        }
    }
}
