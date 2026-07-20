// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Game.Utils.FramePacing
{
    /// <summary>
    /// A single frame's timing measurement. Deliberately a struct with no reference fields so that
    /// buffers of these can be preallocated and written to without allocating on the hot path.
    /// </summary>
    public readonly struct FrameSample
    {
        /// <summary>
        /// Monotonically increasing index within this thread's series.
        /// </summary>
        public readonly long FrameIndex;

        /// <summary>
        /// A <see cref="System.Diagnostics.Stopwatch"/> timestamp, providing a time base shared
        /// between the update and draw series so the two can be correlated.
        /// </summary>
        public readonly long TimestampTicks;

        /// <summary>
        /// Time elapsed since the previous frame on this thread, in milliseconds.
        /// </summary>
        public readonly double DeltaMilliseconds;

        /// <summary>
        /// Time the clock spent sleeping during this frame, in milliseconds. Recorded for both the
        /// update and draw series. On the update thread, a long frame with no sleep indicates work
        /// overran the budget; a long frame that did sleep indicates the throttler mispredicted. On
        /// the draw thread this covers only the clock's own throttle sleep — time spent blocked in
        /// the renderer's buffer swap waiting on vsync/present is not included — so a long draw frame
        /// with zero sleep does not by itself imply a work overrun; it may simply be vsync-limited.
        /// </summary>
        public readonly double TimeSleptMilliseconds;

        public readonly int Gen0Collections;
        public readonly int Gen1Collections;
        public readonly int Gen2Collections;

        public FrameSample(long frameIndex, long timestampTicks, double deltaMilliseconds, double timeSleptMilliseconds,
                           int gen0Collections, int gen1Collections, int gen2Collections)
        {
            FrameIndex = frameIndex;
            TimestampTicks = timestampTicks;
            DeltaMilliseconds = deltaMilliseconds;
            TimeSleptMilliseconds = timeSleptMilliseconds;
            Gen0Collections = gen0Collections;
            Gen1Collections = gen1Collections;
            Gen2Collections = gen2Collections;
        }
    }
}
