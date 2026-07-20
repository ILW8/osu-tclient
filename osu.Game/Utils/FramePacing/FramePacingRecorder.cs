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

        // Written on the draw thread (recordDrawFrame), read on the update thread (StopRecording).
        // volatile guarantees the update thread observes a fresh value rather than a stale zero —
        // which matters because a stale zero here is indistinguishable from "the draw node was
        // never invoked" (see the empty-draw-series check in writeSession).
        private volatile int drawThreadId;

        // Incremented on the update thread both when a session's flush is dispatched and when a new
        // session starts. A background writeSession task captures the value current at dispatch time
        // and only publishes lastSessionPath if it is still current when the write completes, so a
        // stale flush from a previous session cannot resurrect LastSessionPath over a newer one —
        // including the case where a new session has started (and nulled lastSessionPath) before the
        // previous session's flush has completed. volatile gives the background thread's read a
        // guaranteed-visible view of the update thread's writes.
        private volatile int sessionGeneration;

        // Backing field for LastSessionPath: written from the background writeSession task, read
        // from the update thread. volatile provides the store/load barrier that a plain field would
        // lack; reference assignment is already atomic, so this only affects visibility ordering.
        private volatile string? lastSessionPath;

        public bool IsRecording => recording;

        public FramePacingStatistics? LastUpdateStatistics { get; private set; }
        public FramePacingStatistics? LastDrawStatistics { get; private set; }
        public int LastMarkerCount { get; private set; }
        public string? LastSessionPath => lastSessionPath;

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
            lastSessionPath = null;
            ++sessionGeneration;

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

            var updateStatistics = FramePacingStatistics.Compute(updateSamples, spike_threshold_ms);
            var drawStatistics = FramePacingStatistics.Compute(drawSamples, spike_threshold_ms);

            LastUpdateStatistics = updateStatistics;
            LastDrawStatistics = drawStatistics;
            LastMarkerCount = markerTimestamps.Length;

            // Snapshotted here (on the update thread) so that writeSession — which runs later, on a
            // background thread — sees a consistent pair rather than racing a field that could still
            // be updated by a subsequent session.
            int capturedUpdateThreadId = updateThreadId;
            int capturedDrawThreadId = drawThreadId;
            bool wrapped = updateBuffer.HasWrapped || drawBuffer.HasWrapped;

            double updateMaximumHz = updateClock.MaximumUpdateHz;
            double drawMaximumHz = drawClock.MaximumUpdateHz;
            bool throttling = updateClock.Throttling;

            Logger.Log($"[frame pacing] recording stopped — update: {LastUpdateStatistics}", LoggingTarget.Performance, LogLevel.Important);
            Logger.Log($"[frame pacing] draw: {LastDrawStatistics}", LoggingTarget.Performance, LogLevel.Important);

            // Millisecond resolution avoids a stop→start→stop cycle within the same second producing
            // two writes that collide on the same path.
            string fileStem = $"frame-pacing-{DateTimeOffset.Now.ToString(@"yyyyMMdd-HHmmss.fff", CultureInfo.InvariantCulture)}";

            int generation = ++sessionGeneration;

            Task.Run(() => writeSession(fileStem, updateSamples, drawSamples, markerTimestamps, startTicks,
                capturedUpdateThreadId, capturedDrawThreadId, wrapped,
                updateMaximumHz, drawMaximumHz, throttling, generation, updateStatistics, drawStatistics));
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
                drawClock.TimeSlept,
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
                                  long[] markerTimestamps, long startTicks, int updateThreadIdSnapshot, int drawThreadIdSnapshot, bool wrapped,
                                  double updateMaximumHz, double drawMaximumHz, bool throttling, int generation,
                                  FramePacingStatistics updateStatistics, FramePacingStatistics drawStatistics)
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
                    // The draw thread id is only ever assigned inside recordDrawFrame(), so an empty
                    // draw series and a genuinely single-threaded run are otherwise indistinguishable.
                    // Checking emptiness first avoids misreporting "the draw node never ran" as
                    // "execution is single-threaded".
                    if (drawSamples.Length == 0)
                        writer.WriteLine(@"WARNING: no draw samples captured - the draw node was never invoked");
                    else
                    {
                        bool multithreaded = updateThreadIdSnapshot != drawThreadIdSnapshot;

                        writer.WriteLine($"execution mode: {(multithreaded ? "multithreaded" : "single-threaded")}");

                        if (!multithreaded)
                            writer.WriteLine(@"  (update and draw share a cadence in this mode; the two series are not independent)");
                    }

                    writer.WriteLine($"update clock max hz: {updateMaximumHz}");
                    writer.WriteLine($"draw clock max hz: {drawMaximumHz}");
                    writer.WriteLine($"throttling: {throttling}");
                    writer.WriteLine($"spike threshold: {spike_threshold_ms}ms");
                    writer.WriteLine($"markers: {markerTimestamps.Length}");
                    writer.WriteLine(@"  (a marker's timestamp lags the frame that caused the perceived hitch by roughly human "
                                      + "reaction time; examine nearby preceding frames, not just the marker row)");

                    if (wrapped)
                        writer.WriteLine(@"WARNING: buffer wrapped; only the most recent samples were retained");

                    writer.WriteLine();
                    writer.WriteLine($"update: {updateStatistics}");
                    writer.WriteLine($"draw:   {drawStatistics}");
                }

                string path = storage.GetFullPath(csvPath);

                if (generation == sessionGeneration)
                    lastSessionPath = path;

                Logger.Log($"[frame pacing] written to {path}", LoggingTarget.Performance, LogLevel.Important);
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
