# Frame Pacing Recorder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture per-frame update- and draw-thread timing in the osu! tournament client to a CSV, with operator-pressed hitch markers, so that inconsistent frame delivery can be quantified.

**Architecture:** Four small classes under `osu.Game/Utils/FramePacing/`. Three are pure and unit-tested (a fixed-capacity ring buffer, a percentile calculator, a CSV serialiser). The fourth is a `Drawable` that samples the update thread from `Update()` and the draw thread from a custom `DrawNode.Draw()`, writing into preallocated buffers with no allocation on either hot path. Hotkeys start/stop a session and drop markers; on stop the buffers are snapshotted and written off-thread.

**Tech Stack:** C# 12 / .NET 8, osu!framework 2026.527.0 (NuGet, `ppy.osu.Framework`), NUnit + `ClassicAssert` for tests.

**Spec:** `docs/superpowers/specs/2026-07-20-frame-pacing-recorder-design.md`

## Global Constraints

- Every new `.cs` file starts with the two-line licence header, verbatim:
  ```
  // Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
  // See the LICENCE file in the repository root for full licence text.
  ```
- Namespaces must match folder paths (enforced by ReSharper inspection). Files in `osu.Game/Utils/FramePacing/` use `namespace osu.Game.Utils.FramePacing`.
- Any class deriving from `Drawable` must be declared `partial` (osu!framework source generator requirement).
- **No allocation and no locking** in `FramePacingRecorder.Update()` or `RecordDrawFrame()`. A recorder that allocates per frame manufactures the GC hitches being investigated. This is the single most important constraint in the plan.
- Do **not** call `Invalidate(Invalidation.DrawNode)` per frame. `DrawNode.Draw()` is invoked every frame regardless of invalidation; invalidation only re-runs `ApplyState()`. Per-frame invalidation is the exact pattern under investigation (`TrianglesV2.cs:118`) and must not be reproduced here.
- Target framework APIs (verified against `ppy.osu.Framework` 2026.527.0 by reflection):
  - `GameHost.UpdateThread` / `.DrawThread` → `GameThread`, with public `.Clock` → `ThrottledFrameClock` and `.Thread` → `System.Threading.Thread`.
  - `ThrottledFrameClock` exposes public `ElapsedFrameTime` (ms), `TimeSlept` (ms), `MaximumUpdateHz`, `Throttling`.
  - `osu.Framework.Statistics.PerformanceMonitor` and `FrameStatistics` are **internal** — do not attempt to use them.
  - `Logger.Storage` is a public `Storage` with `GetStream(string, FileAccess, FileMode)`.
- Hotkeys are **Ctrl+Shift+R** (toggle recording) and **Ctrl+Shift+P** (drop marker). Do not use S, B, I, D, M, G, or W: `ScreenButton.OnKeyDown` (`osu.Game.Tournament/TournamentSceneManager.cs:308-316`) matches on `e.Key` alone without checking modifiers, so those letters are consumed by sidebar navigation even when modifiers are held.

---

### Task 1: Frame sample and ring buffer

**Files:**
- Create: `osu.Game/Utils/FramePacing/FrameSample.cs`
- Create: `osu.Game/Utils/FramePacing/FrameSampleBuffer.cs`
- Test: `osu.Game.Tests/NonVisual/FrameSampleBufferTest.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `readonly struct FrameSample` with public readonly fields `long FrameIndex`, `long TimestampTicks`, `double DeltaMilliseconds`, `double TimeSleptMilliseconds`, `int Gen0Collections`, `int Gen1Collections`, `int Gen2Collections`, and a constructor taking them in that order.
  - `class FrameSampleBuffer` with `FrameSampleBuffer(int capacity)`, `int Capacity`, `int TotalWritten`, `bool HasWrapped`, `void Add(in FrameSample)`, `void Clear()`, `FrameSample[] Snapshot()`.

**Design notes:** capacity must be a power of two so the write index can be masked instead of divided. The buffer **wraps**, keeping the most recent `Capacity` samples — this is deliberate, so the operator can leave recording on and still have the recent past available after pressing a marker. `Snapshot()` returns samples in chronological order and is the only member that allocates; it is called on flush, never on the hot path.

- [ ] **Step 1: Write the failing test**

Create `osu.Game.Tests/NonVisual/FrameSampleBufferTest.cs`:

```csharp
// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using osu.Game.Utils.FramePacing;

namespace osu.Game.Tests.NonVisual
{
    [TestFixture]
    public class FrameSampleBufferTest
    {
        private static FrameSample sample(long index, double delta) =>
            new FrameSample(index, index * 1000, delta, 0, 0, 0, 0);

        [Test]
        public void TestRejectsNonPowerOfTwoCapacity()
        {
            Assert.Throws<ArgumentException>(() => new FrameSampleBuffer(3));
        }

        [Test]
        public void TestRejectsZeroCapacity()
        {
            Assert.Throws<ArgumentException>(() => new FrameSampleBuffer(0));
        }

        [Test]
        public void TestEmptyBuffer()
        {
            var buffer = new FrameSampleBuffer(4);

            ClassicAssert.AreEqual(0, buffer.TotalWritten);
            ClassicAssert.IsFalse(buffer.HasWrapped);
            ClassicAssert.AreEqual(0, buffer.Snapshot().Length);
        }

        [Test]
        public void TestPartialFillPreservesOrder()
        {
            var buffer = new FrameSampleBuffer(4);

            buffer.Add(sample(0, 1.0));
            buffer.Add(sample(1, 2.0));

            var snapshot = buffer.Snapshot();

            ClassicAssert.AreEqual(2, snapshot.Length);
            ClassicAssert.AreEqual(0, snapshot[0].FrameIndex);
            ClassicAssert.AreEqual(1, snapshot[1].FrameIndex);
            ClassicAssert.AreEqual(2.0, snapshot[1].DeltaMilliseconds);
            ClassicAssert.IsFalse(buffer.HasWrapped);
        }

        [Test]
        public void TestExactFillDoesNotReportWrapped()
        {
            var buffer = new FrameSampleBuffer(4);

            for (int i = 0; i < 4; i++)
                buffer.Add(sample(i, i));

            ClassicAssert.AreEqual(4, buffer.Snapshot().Length);
            ClassicAssert.IsFalse(buffer.HasWrapped);
        }

        [Test]
        public void TestOverflowKeepsMostRecentInOrder()
        {
            var buffer = new FrameSampleBuffer(4);

            for (int i = 0; i < 6; i++)
                buffer.Add(sample(i, i));

            var snapshot = buffer.Snapshot();

            ClassicAssert.AreEqual(4, snapshot.Length);
            ClassicAssert.AreEqual(2, snapshot[0].FrameIndex);
            ClassicAssert.AreEqual(5, snapshot[3].FrameIndex);
            ClassicAssert.IsTrue(buffer.HasWrapped);
            ClassicAssert.AreEqual(6, buffer.TotalWritten);
        }

        [Test]
        public void TestClearResetsState()
        {
            var buffer = new FrameSampleBuffer(4);

            for (int i = 0; i < 6; i++)
                buffer.Add(sample(i, i));

            buffer.Clear();

            ClassicAssert.AreEqual(0, buffer.TotalWritten);
            ClassicAssert.IsFalse(buffer.HasWrapped);
            ClassicAssert.AreEqual(0, buffer.Snapshot().Length);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test osu.Game.Tests -c Debug --filter "FullyQualifiedName~FrameSampleBufferTest"
```

Expected: build failure — `The type or namespace name 'FramePacing' does not exist in the namespace 'osu.Game.Utils'`.

- [ ] **Step 3: Write `FrameSample`**

Create `osu.Game/Utils/FramePacing/FrameSample.cs`:

```csharp
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
        /// Time the clock spent sleeping during this frame, in milliseconds. Update thread only;
        /// always zero for draw samples. A long frame with no sleep indicates work overran the
        /// budget; a long frame that did sleep indicates the throttler mispredicted.
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
```

- [ ] **Step 4: Write `FrameSampleBuffer`**

Create `osu.Game/Utils/FramePacing/FrameSampleBuffer.cs`:

```csharp
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
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test osu.Game.Tests -c Debug --filter "FullyQualifiedName~FrameSampleBufferTest"
```

Expected: `Passed!  - Failed: 0, Passed: 7`.

- [ ] **Step 6: Commit**

```bash
git add osu.Game/Utils/FramePacing/FrameSample.cs osu.Game/Utils/FramePacing/FrameSampleBuffer.cs osu.Game.Tests/NonVisual/FrameSampleBufferTest.cs
git commit -m "add frame sample struct and ring buffer for frame pacing capture"
```

---

### Task 2: Pacing statistics

**Files:**
- Create: `osu.Game/Utils/FramePacing/FramePacingStatistics.cs`
- Test: `osu.Game.Tests/NonVisual/FramePacingStatisticsTest.cs`

**Interfaces:**
- Consumes: `FrameSample` from Task 1.
- Produces: `readonly struct FramePacingStatistics` with public readonly fields `int FrameCount`, `double Median`, `double Percentile95`, `double Percentile99`, `double Percentile999`, `double Maximum`, `int FramesOverThreshold`, `double MaximumConsecutiveDelta`; and `static FramePacingStatistics Compute(FrameSample[] samples, double thresholdMilliseconds)`.

**Design notes:** mean FPS is deliberately not computed — an even 60fps and a hitching 60fps have identical means. `MaximumConsecutiveDelta` is the largest absolute change in frame time between two adjacent frames, which is the jitter signal. Percentiles use nearest-rank on a sorted copy.

- [ ] **Step 1: Write the failing test**

Create `osu.Game.Tests/NonVisual/FramePacingStatisticsTest.cs`:

```csharp
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
            ClassicAssert.AreEqual(0.0, stats.Maximum);
            ClassicAssert.AreEqual(0, stats.FramesOverThreshold);
            ClassicAssert.AreEqual(0.0, stats.MaximumConsecutiveDelta);
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
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test osu.Game.Tests -c Debug --filter "FullyQualifiedName~FramePacingStatisticsTest"
```

Expected: build failure — `The name 'FramePacingStatistics' does not exist`.

- [ ] **Step 3: Write the implementation**

Create `osu.Game/Utils/FramePacing/FramePacingStatistics.cs`:

```csharp
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

        public override string ToString() =>
            $"frames: {FrameCount}, p50: {Median:F2}ms, p95: {Percentile95:F2}ms, p99: {Percentile99:F2}ms, "
            + $"p99.9: {Percentile999:F2}ms, max: {Maximum:F2}ms, over threshold: {FramesOverThreshold}, "
            + $"worst consecutive delta: {MaximumConsecutiveDelta:F2}ms";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test osu.Game.Tests -c Debug --filter "FullyQualifiedName~FramePacingStatisticsTest"
```

Expected: `Passed!  - Failed: 0, Passed: 6`.

- [ ] **Step 5: Commit**

```bash
git add osu.Game/Utils/FramePacing/FramePacingStatistics.cs osu.Game.Tests/NonVisual/FramePacingStatisticsTest.cs
git commit -m "add pacing statistics with percentiles and jitter metric"
```

---

### Task 3: CSV serialisation

**Files:**
- Create: `osu.Game/Utils/FramePacing/FramePacingCsvWriter.cs`
- Test: `osu.Game.Tests/NonVisual/FramePacingCsvWriterTest.cs`

**Interfaces:**
- Consumes: `FrameSample` from Task 1.
- Produces: `static class FramePacingCsvWriter` with `const string Header` and
  `static void Write(TextWriter writer, FrameSample[] updateSamples, FrameSample[] drawSamples, long[] markerTimestamps, long sessionStartTicks)`.

**Design notes:** the two series and the markers are merged into one time-ordered table, distinguished by a `thread` column holding `update`, `draw`, or `marker`. Marker rows carry a timestamp and nothing else. Timestamps are emitted as milliseconds since session start, derived from `Stopwatch.Frequency`, so the update and draw series are directly comparable. Ordering is by timestamp; ties are broken by the order the sources are supplied, which keeps output deterministic and testable. `CultureInfo.InvariantCulture` is mandatory — a comma decimal separator would corrupt the CSV on non-English systems, which is a realistic risk for tournament machines.

- [ ] **Step 1: Write the failing test**

Create `osu.Game.Tests/NonVisual/FramePacingCsvWriterTest.cs`:

```csharp
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

            ClassicAssert.AreEqual("3.000,marker,0,0.000,0.000,0,0,0", lines[1]);
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
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test osu.Game.Tests -c Debug --filter "FullyQualifiedName~FramePacingCsvWriterTest"
```

Expected: build failure — `The name 'FramePacingCsvWriter' does not exist`.

- [ ] **Step 3: Write the implementation**

Create `osu.Game/Utils/FramePacing/FramePacingCsvWriter.cs`:

```csharp
// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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
                    writeSample(writer, @"update", updateSamples[updateIndex++], sessionStartTicks);
                else if (drawTicks <= markerTicks)
                    writeSample(writer, @"draw", drawSamples[drawIndex++], sessionStartTicks);
                else
                    writeSample(writer, @"marker", new FrameSample(0, markerTimestamps[markerIndex++], 0, 0, 0, 0, 0), sessionStartTicks);
            }
        }

        private static void writeSample(TextWriter writer, string threadName, in FrameSample sample, long sessionStartTicks)
        {
            double timestampMilliseconds = (sample.TimestampTicks - sessionStartTicks) * 1000.0 / Stopwatch.Frequency;

            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{timestampMilliseconds:F3},{threadName},{sample.FrameIndex},{sample.DeltaMilliseconds:F3},{sample.TimeSleptMilliseconds:F3},{sample.Gen0Collections},{sample.Gen1Collections},{sample.Gen2Collections}"));
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test osu.Game.Tests -c Debug --filter "FullyQualifiedName~FramePacingCsvWriterTest"
```

Expected: `Passed!  - Failed: 0, Passed: 6`.

- [ ] **Step 5: Commit**

```bash
git add osu.Game/Utils/FramePacing/FramePacingCsvWriter.cs osu.Game.Tests/NonVisual/FramePacingCsvWriterTest.cs
git commit -m "add csv serialisation for captured frame series"
```

---

### Task 4: The recorder drawable

**Files:**
- Create: `osu.Game/Utils/FramePacing/FramePacingRecorder.cs`
- Test: `osu.Game.Tests/Visual/Utils/TestSceneFramePacingRecorder.cs`

**Interfaces:**
- Consumes: `FrameSample`, `FrameSampleBuffer`, `FramePacingStatistics`, `FramePacingCsvWriter` from Tasks 1–3.
- Produces: `public partial class FramePacingRecorder : Drawable` with `bool IsRecording`, `void StartRecording()`, `void StopRecording()`, `void AddMarker()`, and post-session results `FramePacingStatistics? LastUpdateStatistics`, `FramePacingStatistics? LastDrawStatistics`, `string? LastSessionPath`.

**Design notes — read before implementing:**

1. `DrawNode.Draw(IRenderer)` runs on the **draw thread**; `DrawNode.ApplyState()` runs on the update thread. The timestamp therefore belongs in `Draw`.
2. The framework keeps **multiple draw node instances per drawable** (triple buffering). Per-draw-node state would not accumulate correctly, so the draw node must call back into the drawable-owned buffer rather than hold its own.
3. Do **not** override `ApplyState()` and do **not** invalidate per frame. `Draw()` is called every frame regardless.
4. `AlwaysPresent = true` is required so the drawable is present for both draw node generation and non-positional input. `RelativeSizeAxes = Axes.Both` prevents the zero-size quad being culled as masked away. The drawable renders nothing because its draw node issues no geometry, so no alpha handling is needed.
5. Buffer capacity is `1 << 17` (131,072) samples per thread — about 9 minutes at 240fps, roughly 6MB per buffer.
6. The spike threshold is 20ms, matching `spike_time_ms` in `osu.Game/Graphics/UserInterface/FPSCounter.cs:35`, so the two agree on what counts as a spike.
7. Execution mode is detected empirically by comparing the managed thread ids observed in `Update()` and in the draw node, rather than by reading configuration. In single-threaded mode the two series share a cadence and the split conveys nothing, so the summary must state which mode was in effect.

- [ ] **Step 1: Write the failing test**

Create `osu.Game.Tests/Visual/Utils/TestSceneFramePacingRecorder.cs`:

```csharp
// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using NUnit.Framework.Legacy;
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
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test osu.Game.Tests -c Debug --filter "FullyQualifiedName~TestSceneFramePacingRecorder"
```

Expected: build failure — `The name 'FramePacingRecorder' does not exist`.

- [ ] **Step 3: Write the implementation**

Create `osu.Game/Utils/FramePacing/FramePacingRecorder.cs`:

```csharp
// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Timing;
using osuTK.Input;

namespace osu.Game.Utils.FramePacing
{
    /// <summary>
    /// Captures per-frame update- and draw-thread timing to a CSV for offline analysis of frame
    /// pacing. Toggle a session with Ctrl+Shift+R; press Ctrl+Shift+P to mark the moment a hitch is
    /// perceived, so that perception can be lined up against the captured series.
    /// </summary>
    /// <remarks>
    /// Neither sampling path allocates or locks. That is a hard requirement rather than an
    /// optimisation: a recorder that allocated per frame would manufacture the garbage collection
    /// hitches it exists to detect.
    /// </remarks>
    public partial class FramePacingRecorder : Drawable
    {
        /// <summary>
        /// Samples retained per thread. Roughly nine minutes at 240fps; the buffer wraps, so a
        /// session may be left running with the recent past always available.
        /// </summary>
        private const int buffer_capacity = 1 << 17;

        /// <summary>
        /// Frame duration above which a frame is counted as a spike. Matches the threshold used by
        /// the in-game FPS counter (see FPSCounter.spike_time_ms).
        /// </summary>
        private const double spike_threshold_ms = 20;

        [Resolved]
        private GameHost gameHost { get; set; } = null!;

        private readonly FrameSampleBuffer updateBuffer = new FrameSampleBuffer(buffer_capacity);
        private readonly FrameSampleBuffer drawBuffer = new FrameSampleBuffer(buffer_capacity);

        // Written only on the update thread, and only on the rare marker keypress.
        private readonly List<long> markers = new List<long>();

        private ThrottledFrameClock updateClock = null!;
        private ThrottledFrameClock drawClock = null!;

        private volatile bool recording;

        private long sessionStartTicks;
        private long updateFrameIndex;
        private long drawFrameIndex;

        private int updateThreadId;
        private int drawThreadId;

        public bool IsRecording => recording;

        public FramePacingStatistics? LastUpdateStatistics { get; private set; }
        public FramePacingStatistics? LastDrawStatistics { get; private set; }
        public int LastMarkerCount { get; private set; }
        public string? LastSessionPath { get; private set; }

        public FramePacingRecorder()
        {
            // Present so that draw nodes are generated and non-positional input is received; sized
            // to the parent so the draw quad is never culled as masked away. Nothing is rendered.
            RelativeSizeAxes = Axes.Both;
            AlwaysPresent = true;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            updateClock = gameHost.UpdateThread.Clock;
            drawClock = gameHost.DrawThread.Clock;
        }

        public void StartRecording()
        {
            if (recording)
                return;

            updateBuffer.Clear();
            drawBuffer.Clear();
            markers.Clear();

            updateFrameIndex = 0;
            drawFrameIndex = 0;
            updateThreadId = 0;
            drawThreadId = 0;

            LastUpdateStatistics = null;
            LastDrawStatistics = null;
            LastSessionPath = null;

            sessionStartTicks = Stopwatch.GetTimestamp();
            recording = true;

            Logger.Log(@"[frame pacing] recording started", LoggingTarget.Performance, LogLevel.Important);
        }

        public void StopRecording()
        {
            if (!recording)
                return;

            recording = false;

            var updateSamples = updateBuffer.Snapshot();
            var drawSamples = drawBuffer.Snapshot();
            long[] markerTimestamps = markers.ToArray();
            long startTicks = sessionStartTicks;

            LastUpdateStatistics = FramePacingStatistics.Compute(updateSamples, spike_threshold_ms);
            LastDrawStatistics = FramePacingStatistics.Compute(drawSamples, spike_threshold_ms);
            LastMarkerCount = markerTimestamps.Length;

            bool multithreaded = updateThreadId != drawThreadId && drawThreadId != 0;
            bool wrapped = updateBuffer.HasWrapped || drawBuffer.HasWrapped;

            Logger.Log($"[frame pacing] recording stopped — update: {LastUpdateStatistics}", LoggingTarget.Performance, LogLevel.Important);
            Logger.Log($"[frame pacing] draw: {LastDrawStatistics}", LoggingTarget.Performance, LogLevel.Important);

            string fileStem = $"frame-pacing-{DateTimeOffset.Now.ToString(@"yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";

            Task.Run(() => writeSession(fileStem, updateSamples, drawSamples, markerTimestamps, startTicks, multithreaded, wrapped));
        }

        public void AddMarker()
        {
            if (!recording)
                return;

            markers.Add(Stopwatch.GetTimestamp());
        }

        protected override void Update()
        {
            base.Update();

            if (!recording)
                return;

            updateThreadId = Environment.CurrentManagedThreadId;

            updateBuffer.Add(new FrameSample(
                updateFrameIndex++,
                Stopwatch.GetTimestamp(),
                updateClock.ElapsedFrameTime,
                updateClock.TimeSlept,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2)));
        }

        /// <summary>
        /// Called from the draw node, on the draw thread, once per delivered frame.
        /// </summary>
        private void recordDrawFrame()
        {
            if (!recording)
                return;

            drawThreadId = Environment.CurrentManagedThreadId;

            drawBuffer.Add(new FrameSample(
                drawFrameIndex++,
                Stopwatch.GetTimestamp(),
                drawClock.ElapsedFrameTime,
                0,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2)));
        }

        protected override bool OnKeyDown(KeyDownEvent e)
        {
            if (e.Repeat || !e.ControlPressed || !e.ShiftPressed)
                return base.OnKeyDown(e);

            switch (e.Key)
            {
                // Note: letters bound by the tournament sidebar (S, B, I, D, M, G, W) are consumed
                // there regardless of modifiers, so R and P are used here.
                case Key.R:
                    if (recording)
                        StopRecording();
                    else
                        StartRecording();
                    return true;

                case Key.P:
                    AddMarker();
                    return true;
            }

            return base.OnKeyDown(e);
        }

        private void writeSession(string fileStem, FrameSample[] updateSamples, FrameSample[] drawSamples,
                                  long[] markerTimestamps, long startTicks, bool multithreaded, bool wrapped)
        {
            try
            {
                var storage = Logger.Storage.GetStorageForDirectory(@"exports");

                string csvPath = $"{fileStem}.csv";

                using (var stream = storage.GetStream(csvPath, FileAccess.Write, FileMode.Create))
                using (var writer = new StreamWriter(stream))
                    FramePacingCsvWriter.Write(writer, updateSamples, drawSamples, markerTimestamps, startTicks);

                using (var stream = storage.GetStream($"{fileStem}-summary.txt", FileAccess.Write, FileMode.Create))
                using (var writer = new StreamWriter(stream))
                {
                    writer.WriteLine($"execution mode: {(multithreaded ? "multithreaded" : "single-threaded")}");

                    if (!multithreaded)
                        writer.WriteLine(@"  (update and draw share a cadence in this mode; the two series are not independent)");

                    writer.WriteLine($"update clock max hz: {updateClock.MaximumUpdateHz}");
                    writer.WriteLine($"draw clock max hz: {drawClock.MaximumUpdateHz}");
                    writer.WriteLine($"throttling: {updateClock.Throttling}");
                    writer.WriteLine($"spike threshold: {spike_threshold_ms}ms");
                    writer.WriteLine($"markers: {markerTimestamps.Length}");

                    if (wrapped)
                        writer.WriteLine(@"WARNING: buffer wrapped; only the most recent samples were retained");

                    writer.WriteLine();
                    writer.WriteLine($"update: {FramePacingStatistics.Compute(updateSamples, spike_threshold_ms)}");
                    writer.WriteLine($"draw:   {FramePacingStatistics.Compute(drawSamples, spike_threshold_ms)}");
                }

                LastSessionPath = storage.GetFullPath(csvPath);

                Logger.Log($"[frame pacing] written to {LastSessionPath}", LoggingTarget.Performance, LogLevel.Important);
            }
            catch (Exception e)
            {
                Logger.Error(e, @"[frame pacing] failed to write session");
            }
        }

        protected override DrawNode CreateDrawNode() => new FramePacingRecorderDrawNode(this);

        /// <summary>
        /// Timestamps each delivered draw frame. The framework keeps several draw node instances per
        /// drawable, so no state is held here — the sample is handed straight to the buffer owned by
        /// the drawable.
        /// </summary>
        private class FramePacingRecorderDrawNode : DrawNode
        {
            protected new FramePacingRecorder Source => (FramePacingRecorder)base.Source;

            public FramePacingRecorderDrawNode(FramePacingRecorder source)
                : base(source)
            {
            }

            protected override void Draw(IRenderer renderer)
            {
                base.Draw(renderer);

                Source.recordDrawFrame();
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test osu.Game.Tests -c Debug --filter "FullyQualifiedName~TestSceneFramePacingRecorder"
```

Expected: `Passed!  - Failed: 0, Passed: 5`.

If `TestRecordsUpdateFramesWhileRecording` fails with zero captured frames, the drawable is not being updated — confirm `Child = recorder` placed it in the hierarchy and that `AlwaysPresent` is set.

- [ ] **Step 5: Verify the draw-thread path is actually exercised**

The headless test renderer may not invoke draw nodes, so draw-series capture is confirmed separately here rather than being assumed.

Add a temporary assertion to the end of `TestRecordsUpdateFramesWhileRecording`:

```csharp
AddAssert("draw frames captured", () => recorder.LastDrawStatistics!.Value.FrameCount > 0);
```

Run:

```bash
dotnet test osu.Game.Tests -c Debug --filter "FullyQualifiedName~TestSceneFramePacingRecorder.TestRecordsUpdateFramesWhileRecording"
```

If it **passes**, keep the assertion. If it **fails**, the headless renderer does not exercise draw nodes: remove the assertion again and record in the commit message that draw capture is verified manually in Task 5 instead. Do not attempt to make the headless renderer draw.

- [ ] **Step 6: Commit**

```bash
git add osu.Game/Utils/FramePacing/FramePacingRecorder.cs osu.Game.Tests/Visual/Utils/TestSceneFramePacingRecorder.cs
git commit -m "add frame pacing recorder drawable with hotkey-driven capture"
```

---

### Task 5: Wire into the tournament client and verify end-to-end

**Files:**
- Modify: `osu.Game.Tournament/TournamentGameBase.cs` (near the existing `Add(new LazerIpc());` call, around line 239)

**Interfaces:**
- Consumes: `FramePacingRecorder` from Task 4.
- Produces: nothing consumed by later tasks.

**Design notes:** the recorder is added unconditionally, not only in multiplayer spectating mode — hitching is reported on the gameplay screen but the whole client is worth being able to measure, and the recorder costs nothing until a session is started. It is added at the game root so its non-positional input reaches it and its draw node covers the full window.

- [ ] **Step 1: Add the recorder to the tournament game**

In `osu.Game.Tournament/TournamentGameBase.cs`, add the using:

```csharp
using osu.Game.Utils.FramePacing;
```

Then locate the block containing `Add(new LazerIpc());`. That call sits inside an `if (ipc is MultiplayerMatchIPCInfo matchIpc)` branch; the recorder must go **outside** that branch so it is present in every mode. Immediately after the enclosing `if` block closes, add:

```csharp
// Frame pacing capture. Idle until a session is started with Ctrl+Shift+R.
Add(new FramePacingRecorder());
```

- [ ] **Step 2: Verify it builds**

```bash
dotnet build osu.Game.Tournament -c Debug
```

Expected: `Build succeeded.` with no warnings from the new file.

- [ ] **Step 3: Run the tournament client and capture a session**

```bash
dotnet run --project osu.Desktop -c Debug -- --tournament
```

Then, in the client:

1. Press **Ctrl+Shift+R** to start recording. Confirm `[frame pacing] recording started` appears in the log overlay (enable it in Settings → Debug → "Show log overlay" if not already on).
2. Navigate to the Gameplay screen and leave it for at least 30 seconds.
3. Press **Ctrl+Shift+P** two or three times, at moments when a hitch is perceived.
4. Press **Ctrl+Shift+R** to stop. Confirm the summary lines and a `[frame pacing] written to ...` path appear in the log.

- [ ] **Step 4: Verify the output**

Open the reported path. Confirm all of:

- The CSV has the header from `FramePacingCsvWriter.Header` and many rows.
- Rows with `thread` of both `update` **and** `draw` are present. If `draw` rows are absent, the draw node is not being invoked — check that `AlwaysPresent` and `RelativeSizeAxes` are set as in Task 4 and that the recorder is not nested inside a hidden container.
- The number of `marker` rows equals the number of times Ctrl+Shift+P was pressed.
- `timestamp_ms` increases monotonically and decimal points are `.` not `,`.
- The `-summary.txt` states the execution mode and the percentile lines.

Cross-check against the framework's own numbers: press **Ctrl+F11** twice to bring up the full frame statistics overlay and confirm the displayed frame rates are consistent with the recorded median. They will not match exactly — the overlay damps — but an order-of-magnitude disagreement means something is wrong with the capture.

- [ ] **Step 5: Confirm the recorder does not perturb what it measures**

Record a 30-second session on the Gameplay screen, then record a second 30-second session on the same screen. Compare the two `-summary.txt` files: the medians should agree closely. Then compare against the FPS counter with recording stopped entirely.

If starting a recording measurably shifts the median or introduces spikes, the sampling path is allocating. Re-check that nothing in `Update()` or `recordDrawFrame()` boxes, closes over locals, or calls a LINQ operator.

- [ ] **Step 6: Commit**

```bash
git add osu.Game.Tournament/TournamentGameBase.cs
git commit -m "add frame pacing recorder to tournament client"
```

---

## Verification checklist

Before considering the plan complete, confirm every item:

- [ ] `dotnet test osu.Game.Tests -c Debug --filter "FullyQualifiedName~FramePacing"` passes, along with `FrameSampleBufferTest`.
- [ ] `dotnet build osu.sln -c Debug` succeeds with no new warnings.
- [ ] A capture from the running tournament client contains both `update` and `draw` rows, plus the expected marker count.
- [ ] Starting a recording does not measurably change the median frame time (Task 5, Step 5).

## What this does not do

Recorded here so a clean result is not misread — these are from the spec's Limitations section:

1. In single-threaded execution mode the update and draw series collapse to one cadence and the split conveys nothing. The summary file states which mode was in effect; check it before comparing series.
2. This measures frame **production**, not **presentation**. The tournament client is forced to windowed mode when the window is too short (`osu.Game.Tournament/TournamentGame.cs:113`), leaving presentation to the compositor. An even recorded series alongside visibly stuttery capture output is a meaningful result — it redirects the investigation to the presentation path.
3. There is no per-phase attribution: the recorder says a frame was long, not which phase made it long. `PerformanceMonitor` is internal to the framework. If phase attribution becomes necessary, escalate to `./UseLocalFramework.sh` against an `../osu-framework` checkout at the tag matching 2026.527.0.

## Follow-on work (not in this plan)

Once a baseline exists, add runtime A/B toggles for individual suspects — beginning with the unconditional per-frame `Invalidate(Invalidation.DrawNode)` in `TrianglesV2.Update()` (`osu.Game/Graphics/Backgrounds/TrianglesV2.cs:118`) — so a change can be flipped mid-session and compared within a single run, cancelling out machine state and thermal drift.
