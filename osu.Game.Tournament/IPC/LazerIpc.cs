// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Threading;

namespace osu.Game.Tournament.IPC
{
    /// <summary>
    /// Writes live multiplayer-room state to ipc.txt style files while spectating. Only instantiated in
    /// multiplayer-spectating mode (see <c>TournamentGameBase</c>).
    ///
    /// <list type="bullet">
    /// <item><c>ipc-room-id.txt</c> — the connected room ID (empty when disconnected).</item>
    /// <item><c>ipc-state.txt</c> — the <see cref="TourneyState"/> as an integer (<c>3</c> = Playing, <c>4</c> = Ranking).</item>
    /// <item><c>ipc-scores.txt</c> — two lines: team 1 score, then team 2 score.</item>
    /// </list>
    /// </summary>
    public partial class LazerIpc : Component
    {
        private const double write_interval_ms = 200;

        [Resolved]
        private MultiplayerMatchIPCInfo ipc { get; set; } = null!;

        [Resolved]
        private Storage storage { get; set; } = null!;

        private Storage ipcStorage = null!;
        private ScheduledDelegate? scheduled;

        private volatile bool writing;

        private string? lastRoomId;
        private string? lastState;
        private string? lastScores;

        [BackgroundDependencyLoader]
        private void load()
        {
            ipcStorage = storage.GetStorageForDirectory("ipc");
            Logger.Log($"[LazerIpc] Writing tournament IPC files to: {ipcStorage.GetFullPath(string.Empty)}", LoggingTarget.Runtime);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Write once now so the files exist before the first consumer poll.
            writeTick();
            scheduled = Scheduler.AddDelayed(writeTick, write_interval_ms, true);
        }

        private void writeTick()
        {
            if (writing)
            {
                Logger.Log($"last writeTick took over {write_interval_ms}ms, skipping");
                return;
            }

            // Read the bindables on the update thread (they aren't thread-safe)
            (string roomId, string state, string scores) = format(ipc.ConnectedRoomId.Value, ipc.State.Value, ipc.Score1.Value, ipc.Score2.Value);

            writing = true;
            Task.Run(() =>
            {
                try
                {
                    writeIfChanged("ipc-room-id.txt", roomId, ref lastRoomId);
                    writeIfChanged("ipc-state.txt", state, ref lastState);
                    writeIfChanged("ipc-scores.txt", scores, ref lastScores);
                }
                finally
                {
                    writing = false;
                }
            });
        }

        private static (string roomId, string state, string scores) format(long? roomId, TourneyState state, long score1, long score2)
            => (roomId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ((int)state).ToString(CultureInfo.InvariantCulture),
                $"{score1.ToString(CultureInfo.InvariantCulture)}\n{score2.ToString(CultureInfo.InvariantCulture)}");

        private void writeIfChanged(string filename, string content, ref string? last)
        {
            if (content == last)
                return;

            try
            {
                using (var stream = ipcStorage.CreateFileSafely(filename))
                using (var writer = new StreamWriter(stream))
                    writer.Write(content);

                last = content;
            }
            catch (Exception e)
            {
                Logger.Log($"[LazerIpc] Failed to write {filename}: {e.GetType().Name}: {e.Message}", LoggingTarget.Runtime, LogLevel.Important);
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            scheduled?.Cancel();
        }
    }
}
