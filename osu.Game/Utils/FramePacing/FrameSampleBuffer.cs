// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Game.Utils.FramePacing
{
    /// <summary>
    /// A fixed-capacity ring buffer of <see cref="FrameSample"/>s. Writes never allocate and never
    /// lock. When full it wraps, retaining the most recent <see cref="Capacity"/> samples, so a
    /// recording session can be left running and still yield the recent past on demand.
    /// </summary>
    /// <remarks>
    /// Single-producer only. <see cref="Snapshot"/> is intended to be called after writing has
    /// stopped; a frame in flight at that moment may be missed, which is acceptable for this use.
    /// Because a <see cref="FrameSample"/> write is not atomic, a frame in flight may also be torn —
    /// a single retained sample combining fields from two different frames — rather than simply
    /// missing. That, too, is acceptable for this use, but a reader should not chase such a row as a
    /// genuine spike.
    /// </remarks>
    public class FrameSampleBuffer
    {
        private readonly FrameSample[] samples;
        private readonly int mask;

        private int writeIndex;

        /// <param name="capacity">Number of samples retained. Must be a positive power of two.</param>
        public FrameSampleBuffer(int capacity)
        {
            if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
                throw new ArgumentException($"Capacity must be a positive power of two (was {capacity}).", nameof(capacity));

            samples = new FrameSample[capacity];
            mask = capacity - 1;
        }

        public int Capacity => samples.Length;

        /// <summary>
        /// Total samples written, including any discarded by wrapping.
        /// </summary>
        public int TotalWritten => writeIndex;

        /// <summary>
        /// Whether any samples have been discarded by wrapping.
        /// </summary>
        public bool HasWrapped => writeIndex > samples.Length;

        public void Add(in FrameSample sample)
        {
            samples[writeIndex & mask] = sample;
            writeIndex++;
        }

        public void Clear() => writeIndex = 0;

        /// <summary>
        /// Retained samples in chronological order. Allocates; call off the hot path.
        /// </summary>
        public FrameSample[] Snapshot()
        {
            int count = Math.Min(writeIndex, samples.Length);
            int start = writeIndex - count;

            var result = new FrameSample[count];

            for (int i = 0; i < count; i++)
                result[i] = samples[(start + i) & mask];

            return result;
        }
    }
}
