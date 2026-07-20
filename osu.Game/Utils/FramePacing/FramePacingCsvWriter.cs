// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace osu.Game.Utils.FramePacing
{
    /// <summary>
    /// Serialises captured frame series to CSV. The update series, draw series and operator markers
    /// are merged into one time-ordered table distinguished by a thread column, so that a hitch in
    /// one series can be lined up against the other and against the moment it was perceived.
    /// </summary>
    public static class FramePacingCsvWriter
    {
        public const string Header = "timestamp_ms,thread,frame_index,delta_ms,time_slept_ms,gc_gen0,gc_gen1,gc_gen2";

        public static void Write(TextWriter writer, FrameSample[] updateSamples, FrameSample[] drawSamples,
                                 long[] markerTimestamps, long sessionStartTicks)
        {
            writer.WriteLine(Header);

            int updateIndex = 0;
            int drawIndex = 0;
            int markerIndex = 0;

            while (updateIndex < updateSamples.Length || drawIndex < drawSamples.Length || markerIndex < markerTimestamps.Length)
            {
                long updateTicks = updateIndex < updateSamples.Length ? updateSamples[updateIndex].TimestampTicks : long.MaxValue;
                long drawTicks = drawIndex < drawSamples.Length ? drawSamples[drawIndex].TimestampTicks : long.MaxValue;
                long markerTicks = markerIndex < markerTimestamps.Length ? markerTimestamps[markerIndex] : long.MaxValue;

                // Ties resolve update, then draw, then marker, keeping output deterministic.
                if (updateTicks <= drawTicks && updateTicks <= markerTicks)
                    writeSample(writer, "update", updateSamples[updateIndex++], sessionStartTicks);
                else if (drawTicks <= markerTicks)
                    writeSample(writer, "draw", drawSamples[drawIndex++], sessionStartTicks);
                else
                    writeMarker(writer, markerTimestamps[markerIndex++], sessionStartTicks);
            }
        }

        private static void writeSample(TextWriter writer, string threadName, in FrameSample sample, long sessionStartTicks)
        {
            double timestampMilliseconds = (sample.TimestampTicks - sessionStartTicks) * 1000.0 / Stopwatch.Frequency;

            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{timestampMilliseconds:F3},{threadName},{sample.FrameIndex},{sample.DeltaMilliseconds:F3},{sample.TimeSleptMilliseconds:F3},{sample.Gen0Collections},{sample.Gen1Collections},{sample.Gen2Collections}"));
        }

        /// <summary>
        /// A marker carries no frame data, so the columns other than timestamp and thread are left
        /// empty rather than zero. A zero there would read as a real value when plotting the
        /// monotonic GC counters against time — appearing as a false drop back to zero at every
        /// marker, exactly where an analyst hunting a GC correlation is looking.
        /// </summary>
        private static void writeMarker(TextWriter writer, long timestampTicks, long sessionStartTicks)
        {
            double timestampMilliseconds = (timestampTicks - sessionStartTicks) * 1000.0 / Stopwatch.Frequency;

            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{timestampMilliseconds:F3},marker") + new string(',', 6));
        }
    }
}
