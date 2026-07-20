// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using osu.Game.Utils.FramePacing;

namespace osu.Game.Tests.NonVisual
{
    [TestFixture]
    public class FramePacingCsvWriterTest
    {
        /// <summary>
        /// Ticks representing exactly one millisecond, so expected output is readable.
        /// </summary>
        private static long ms(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000.0);

        private static string[] write(FrameSample[] update, FrameSample[] draw, long[] markers)
        {
            var stringWriter = new StringWriter { NewLine = "\n" };
            FramePacingCsvWriter.Write(stringWriter, update, draw, markers, 0);
            return stringWriter.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        [Test]
        public void TestHeaderOnlyWhenNoData()
        {
            string[] lines = write(Array.Empty<FrameSample>(), Array.Empty<FrameSample>(), Array.Empty<long>());

            ClassicAssert.AreEqual(1, lines.Length);
            ClassicAssert.AreEqual(FramePacingCsvWriter.Header, lines[0]);
        }

        [Test]
        public void TestUpdateRowFormatting()
        {
            var update = new[] { new FrameSample(7, ms(10), 16.5, 4.25, 1, 2, 3) };

            string[] lines = write(update, Array.Empty<FrameSample>(), Array.Empty<long>());

            ClassicAssert.AreEqual(2, lines.Length);
            ClassicAssert.AreEqual("10.000,update,7,16.500,4.250,1,2,3", lines[1]);
        }

        [Test]
        public void TestDrawRowFormatting()
        {
            var draw = new[] { new FrameSample(2, ms(5), 8.0, 0, 0, 0, 0) };

            string[] lines = write(Array.Empty<FrameSample>(), draw, Array.Empty<long>());

            ClassicAssert.AreEqual("5.000,draw,2,8.000,0.000,0,0,0", lines[1]);
        }

        [Test]
        public void TestMarkerRowFormatting()
        {
            string[] lines = write(Array.Empty<FrameSample>(), Array.Empty<FrameSample>(), new[] { ms(3) });

            // A marker carries no frame data, so every column beyond timestamp and thread is empty
            // rather than zero (a zero would misrepresent, e.g., the monotonic GC counters as having
            // dropped back to zero at the marker).
            ClassicAssert.AreEqual("3.000,marker,,,,,,", lines[1]);
        }

        [Test]
        public void TestMarkerRowHasSameColumnCountAsHeader()
        {
            string[] lines = write(Array.Empty<FrameSample>(), Array.Empty<FrameSample>(), new[] { ms(3) });

            ClassicAssert.AreEqual(FramePacingCsvWriter.Header.Split(',').Length, lines[1].Split(',').Length);
        }

        [Test]
        public void TestSeriesAreMergedInTimestampOrder()
        {
            var update = new[]
            {
                new FrameSample(0, ms(1), 1.0, 0, 0, 0, 0),
                new FrameSample(1, ms(4), 3.0, 0, 0, 0, 0),
            };

            var draw = new[]
            {
                new FrameSample(0, ms(2), 2.0, 0, 0, 0, 0),
                new FrameSample(1, ms(5), 1.0, 0, 0, 0, 0),
            };

            string[] lines = write(update, draw, new[] { ms(3) });

            string[] threads = lines.Skip(1).Select(l => l.Split(',')[1]).ToArray();

            CollectionAssert.AreEqual(new[] { "update", "draw", "marker", "update", "draw" }, threads);
        }

        [Test]
        public void TestThreeWayTieResolvesUpdateThenDrawThenMarker()
        {
            var update = new[] { new FrameSample(0, ms(5), 1.0, 0, 0, 0, 0) };
            var draw = new[] { new FrameSample(0, ms(5), 1.0, 0, 0, 0, 0) };
            var markers = new[] { ms(5) };

            string[] lines = write(update, draw, markers);

            string[] threads = lines.Skip(1).Select(l => l.Split(',')[1]).ToArray();

            CollectionAssert.AreEqual(new[] { "update", "draw", "marker" }, threads);
        }

        [Test]
        public void TestTwoWayTieBetweenUpdateAndDrawResolvesUpdateFirst()
        {
            var update = new[] { new FrameSample(0, ms(5), 1.0, 0, 0, 0, 0) };
            var draw = new[] { new FrameSample(0, ms(5), 1.0, 0, 0, 0, 0) };

            string[] lines = write(update, draw, Array.Empty<long>());

            string[] threads = lines.Skip(1).Select(l => l.Split(',')[1]).ToArray();

            CollectionAssert.AreEqual(new[] { "update", "draw" }, threads);
        }

        [Test]
        public void TestTwoWayTieBetweenDrawAndMarkerResolvesDrawFirst()
        {
            var draw = new[] { new FrameSample(0, ms(5), 1.0, 0, 0, 0, 0) };
            var markers = new[] { ms(5) };

            string[] lines = write(Array.Empty<FrameSample>(), draw, markers);

            string[] threads = lines.Skip(1).Select(l => l.Split(',')[1]).ToArray();

            CollectionAssert.AreEqual(new[] { "draw", "marker" }, threads);
        }

        [Test]
        public void TestTimestampsAreRelativeToSessionStart()
        {
            var update = new[] { new FrameSample(0, ms(30), 1.0, 0, 0, 0, 0) };

            var stringWriter = new StringWriter { NewLine = "\n" };
            FramePacingCsvWriter.Write(stringWriter, update, Array.Empty<FrameSample>(), Array.Empty<long>(), ms(10));

            string[] lines = stringWriter.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            ClassicAssert.IsTrue(lines[1].StartsWith("20.000,", StringComparison.Ordinal), $"expected 20ms offset, got: {lines[1]}");
        }
    }
}
