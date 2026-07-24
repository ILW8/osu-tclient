// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using osu.Desktop.LegacyIpc;
using osu.Desktop.Windows;
using osu.Framework;
using osu.Framework.Development;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.IPC;
using osu.Game.Tournament;
using SDL;
using Velopack;

namespace osu.Desktop
{
    public static class Program
    {
#if DEBUG
        private const string base_game_name = @"osu-development";
#else
        private const string base_game_name = @"osu";
#endif

        private static LegacyTcpIpcProvider? legacyIpc;

        // held for the process lifetime to keep the claim on this instance's data directory.
        private static Mutex? dataDirectoryMutex;

        private static bool isFirstRun;

        [STAThread]
        public static void Main(string[] args)
        {
            // IMPORTANT DON'T IGNORE: For general sanity, velopack's setup needs to run before anything else.
            // This has bitten us in the rear before (bricked updater), and although the underlying issue from
            // last time has been fixed, let's not tempt fate.
            setupVelopack(args);

            if (OperatingSystem.IsWindows())
            {
                var windowsVersion = Environment.OSVersion.Version;

                // While .NET 8 only supports Windows 10 and above, running on Windows 7/8.1 may still work. We are limited by realm currently, as they choose to only support 8.1 and higher.
                // See https://www.mongodb.com/docs/realm/sdk/dotnet/compatibility/
                if (windowsVersion.Major < 6 || (windowsVersion.Major == 6 && windowsVersion.Minor <= 2))
                {
                    unsafe
                    {
                        // If users running in compatibility mode becomes more of a common thing, we may want to provide better guidance or even consider
                        // disabling it ourselves.
                        // We could also better detect compatibility mode if required:
                        // https://stackoverflow.com/questions/10744651/how-i-can-detect-if-my-application-is-running-under-compatibility-mode#comment58183249_10744730
                        SDL3.SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR,
                            "Your operating system is too old to run osu!"u8,
                            "This version of osu! requires at least Windows 8.1 to run.\n"u8
                            + "Please upgrade your operating system or consider using an older version of osu!.\n\n"u8
                            + "If you are running a newer version of windows, please check you don't have \"Compatibility mode\" turned on for osu!"u8, null);
                        return;
                    }
                }
            }

            // NVIDIA profiles are based on the executable name of a process.
            // Lazer and stable share the same executable name.
            // Stable sets this setting to "Off", which may not be what we want, so let's force it back to the default "Auto" on startup.
            if (OperatingSystem.IsWindows())
                NVAPI.ThreadedOptimisations = NvThreadControlSetting.OGL_THREAD_CONTROL_DEFAULT;

            // Back up the cwd before DesktopGameHost changes it
            string cwd = Environment.CurrentDirectory;

            string gameName = base_game_name;
            bool tournamentClient = false;
            string? customDataPath = null;

            foreach (string arg in args)
            {
                string[] split = arg.Split('=');

                string key = split[0];
                string val = split.Length > 1 ? split[1] : string.Empty;

                switch (key)
                {
                    case "--tournament":
                        tournamentClient = true;
                        break;

                    case "--data-dir":
                        if (string.IsNullOrEmpty(val))
                            break;

                        // resolve against the original cwd (DesktopGameHost changes it later)
                        customDataPath = Path.GetFullPath(val, cwd);
                        break;

                    case "--debug-client-id":
                        if (!DebugUtils.IsDebugBuild)
                            throw new InvalidOperationException("Cannot use this argument in a non-debug build.");

                        if (!int.TryParse(val, out int clientID))
                            throw new ArgumentException("Provided client ID must be an integer.");

                        gameName = $"{base_game_name}-{clientID}";
                        break;
                }
            }

            var hostOptions = new HostOptions
            {
                IPCPipeName = !tournamentClient ? OsuGame.IPC_PIPE_NAME : null,
                FriendlyGameName = OsuGameBase.GAME_NAME,
            };

            using (DesktopGameHost host = Host.GetSuitableDesktopHost(gameName, hostOptions))
            {
                // Multiple instances may run concurrently as long as they don't share a data directory
                // Claimed via a system-wide mutex keyed on the effective data directory
                bool ownsDataDirectory = tryClaimDataDirectory(customDataPath, gameName);

                if (!host.IsPrimaryInstance)
                {
                    if (trySendIPCMessage(host, cwd, args))
                        return;

                    // allow a second instance only if it owns its own data directory (multiple instances are always allowed in debug).
                    if (!ownsDataDirectory && !DebugUtils.IsDebugBuild)
                    {
                        Logger.Log(@"Another osu! instance is already running with this data directory.", LoggingTarget.Runtime, LogLevel.Error);
                        return;
                    }
                }

                if (host.IsPrimaryInstance)
                {
                    try
                    {
                        Logger.Log("Starting legacy IPC provider...");
                        legacyIpc = new LegacyTcpIpcProvider();
                        legacyIpc.Bind();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to start legacy IPC provider");
                    }
                }

                if (tournamentClient)
                    host.Run(new TournamentGame { DataDirectoryOverride = customDataPath });
                else
                {
                    host.Run(new OsuGameDesktop(args)
                    {
                        IsFirstRun = isFirstRun,
                        EnableWebSocketServer = Environment.GetEnvironmentVariable("OSU_WEBSOCKET_SERVER") == "1",
                        DataDirectoryOverride = customDataPath,
                    });
                }
            }
        }

        private static bool trySendIPCMessage(IIpcHost host, string cwd, string[] args)
        {
            if (args.Length == 1 && args[0].StartsWith(OsuGameBase.OSU_PROTOCOL, StringComparison.Ordinal))
            {
                var osuSchemeLinkHandler = new OsuSchemeLinkIPCChannel(host);
                if (!osuSchemeLinkHandler.HandleLinkAsync(args[0]).Wait(3000))
                    throw new IPCTimeoutException(osuSchemeLinkHandler.GetType());

                return true;
            }

            if (args.Length > 0 && args[0].Contains('.')) // easy way to check for a file import in args
            {
                var importer = new ArchiveImportIPCChannel(host);

                foreach (string file in args)
                {
                    Console.WriteLine(@"Importing {0}", file);
                    if (!importer.ImportAsync(Path.GetFullPath(file, cwd)).Wait(3000))
                        throw new IPCTimeoutException(importer.GetType());
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Claims exclusive ownership of a data directory across processes so two instances can't share one.
        /// Returns <c>false</c> when another running instance already owns the same directory.
        /// </summary>
        private static bool tryClaimDataDirectory(string? customDataPath, string gameName)
        {
            // The default location maps 1:1 to the game name (see GameHost.GetDefaultGameStorage), so use it as the key
            // for that case; only --data-dir can point somewhere else.
            string key = customDataPath is null ? gameName : Path.TrimEndingDirectorySeparator(customDataPath);

            // paths are case-insensitive on Windows/macOS; normalise so the same directory maps to one key.
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                key = key.ToLowerInvariant();

            // hash to a fixed, mutex-name-safe token (paths contain invalid chars and can exceed the length limit).
            string name = "osu-datadir-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            dataDirectoryMutex = new Mutex(false, name);

            try
            {
                return dataDirectoryMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // previous owner crashed without releasing; ownership transfers to us.
                return true;
            }
        }

        private static void setupVelopack(string[] args)
        {
            // Arguments being present indicate the user is either starting the game in a special (aka tournament) mode,
            // or is running with pending imports via file association or otherwise.
            //
            // In both these scenarios, we'd hope the game does not attempt to update.
            //
            // Special consideration for velopack startup arguments, which must be handled during update.
            // See https://docs.velopack.io/integrating/hooks#command-line-hooks.
            if (args.Length > 0 && !args[0].StartsWith("--velo", StringComparison.Ordinal))
            {
                Logger.Log("Handling arguments, skipping velopack setup.");
                return;
            }

            if (OsuGameDesktop.IsPackageManaged)
            {
                Logger.Log("Updates are being managed by an external provider. Skipping Velopack setup.");
                return;
            }

            var app = VelopackApp.Build();

            app.OnFirstRun(_ => isFirstRun = true);

            if (OperatingSystem.IsWindows())
                configureWindows(app);

            app.Run();
        }

        [SupportedOSPlatform("windows")]
        private static void configureWindows(VelopackApp app)
        {
            app.OnFirstRun(_ => WindowsAssociationManager.InstallAssociations());
            app.OnAfterUpdateFastCallback(_ => WindowsAssociationManager.UpdateAssociations());
            app.OnBeforeUninstallFastCallback(_ => WindowsAssociationManager.UninstallAssociations());
        }
    }
}
