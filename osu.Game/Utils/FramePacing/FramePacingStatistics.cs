// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Game.Utils.FramePacing
{
    /// <summary>
    /// Summary statistics over a captured frame series, chosen to describe pacing rather than
    /// throughput. Mean frame rate is deliberately absent: an evenly delivered 60fps and a badly
    /// hitching 60fps have identical means.
    /// </summary>
    public readonly struct FramePacingStatistics
    {
        public readonly int FrameCount;
        public readonly double Median;
        public readonly double Percentile95;
        public readonly double Percentile99;
        public readonly double Percentile999;
        public readonly double Maximum;

        /// <summary>
        /// Number of frames whose duration exceeded the supplied threshold.
        /// </summary>
        public readonly int FramesOverThreshold;

        /// <summary>
        /// Largest absolute change in frame duration between two adjacent frames. This is the
        /// jitter signal: a series can sit at a good median and still be visibly uneven.
        /// </summary>
        public readonly double MaximumConsecutiveDelta;

        private FramePacingStatistics(int frameCount, double median, double percentile95, double percentile99,
                                      double percentile999, double maximum, int framesOverThreshold,
                                      double maximumConsecutiveDelta)
        {
            FrameCount = frameCount;
            Median = median;
            Percentile95 = percentile95;
            Percentile99 = percentile99;
            Percentile999 = percentile999;
            Maximum = maximum;
            FramesOverThreshold = framesOverThreshold;
            MaximumConsecutiveDelta = maximumConsecutiveDelta;
        }

        public static FramePacingStatistics Compute(FrameSample[] samples, double thresholdMilliseconds)
        {
            if (samples.Length == 0)
                return default;

            var sorted = new double[samples.Length];

            int overThreshold = 0;
            double maximumConsecutiveDelta = 0;

            for (int i = 0; i < samples.Length; i++)
            {
                double delta = samples[i].DeltaMilliseconds;

                sorted[i] = delta;

                if (delta > thresholdMilliseconds)
                    overThreshold++;

                if (i > 0)
                    maximumConsecutiveDelta = Math.Max(maximumConsecutiveDelta, Math.Abs(delta - samples[i - 1].DeltaMilliseconds));
            }

            Array.Sort(sorted);

            return new FramePacingStatistics(
                samples.Length,
                percentile(sorted, 0.50),
                percentile(sorted, 0.95),
                percentile(sorted, 0.99),
                percentile(sorted, 0.999),
                sorted[^1],
                overThreshold,
                maximumConsecutiveDelta);
        }

        /// <summary>
        /// Nearest-rank percentile over an ascending-sorted series.
        /// </summary>
        private static double percentile(double[] sorted, double proportion)
        {
            int rank = (int)Math.Ceiling(proportion * sorted.Length) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        }

        public override string ToString()
        {
            if (FrameCount == 0)
                return "no samples";

            return $"frames: {FrameCount}, p50: {Median:F2}ms, p95: {Percentile95:F2}ms, p99: {Percentile99:F2}ms, "
                   + $"p99.9: {Percentile999:F2}ms, max: {Maximum:F2}ms, over threshold: {FramesOverThreshold}, "
                   + $"worst consecutive delta: {MaximumConsecutiveDelta:F2}ms";
        }
    }
}
