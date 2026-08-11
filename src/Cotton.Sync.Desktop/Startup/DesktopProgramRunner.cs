// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Avalonia;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;

namespace Cotton.Sync.Desktop.Startup
{
    internal static class DesktopProgramRunner
    {
        public static int Run(string[] args, Func<AppBuilder> createApp)
        {
            DesktopStartupOptions startupOptions = DesktopStartupOptions.Parse(args);
            if (startupOptions.PrintVersion)
            {
                Console.Out.WriteLine(DesktopAppVersion.Current);
                return 0;
            }

            int preparationExitCode = PrepareStartupEnvironment(startupOptions);
            if (preparationExitCode != 0)
            {
                return preparationExitCode;
            }

            DesktopAppPaths paths = DesktopStartupPathResolver.Resolve(startupOptions);
            DesktopTraceLogging.Install(paths);
            DesktopUnhandledExceptionReporter.Install();
            DesktopStartupCommand command = DesktopStartupCommandResolver.Resolve(startupOptions);
            if (command == DesktopStartupCommand.None
                && DesktopPendingUpdateStartup.TryStartPendingUpdate(paths, DesktopAppVersion.Current))
            {
                return 0;
            }

            if (command != DesktopStartupCommand.None)
            {
                return RunCommand(command, paths, startupOptions);
            }

            return RunDesktopApplication(args, startupOptions, paths, createApp);
        }

        private static int PrepareStartupEnvironment(DesktopStartupOptions startupOptions)
        {
            if (!startupOptions.RunWindowsVirtualFilesSmoke)
            {
                return 0;
            }

            return DesktopWindowsVirtualFilesSmokeRunner
                .PrepareStartupEnvironmentAsync(startupOptions, Console.Out)
                .GetAwaiter()
                .GetResult();
        }

        private static int RunCommand(
            DesktopStartupCommand command,
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions)
        {
            Task<int> commandTask = command switch
            {
                DesktopStartupCommand.SelfTest => DesktopCommandLineRunner.RunSelfTestAsync(
                    paths,
                    startupOptions,
                    Console.Out),
                DesktopStartupCommand.LiveSyncSmoke => DesktopCommandLineRunner.RunLiveSyncSmokeAsync(
                    paths,
                    startupOptions,
                    Console.Out),
                DesktopStartupCommand.WindowsVirtualFilesSmoke => DesktopCommandLineRunner.RunWindowsVirtualFilesSmokeAsync(
                    paths,
                    startupOptions,
                    Console.Out),
                DesktopStartupCommand.UpdateDiscoverySmoke => DesktopCommandLineRunner.RunUpdateDiscoverySmokeAsync(
                    paths,
                    startupOptions,
                    Console.Out),
                DesktopStartupCommand.UpdateInstallSmoke => DesktopCommandLineRunner.RunUpdateInstallSmokeAsync(
                    paths,
                    startupOptions,
                    Console.Out),
                DesktopStartupCommand.ShellShareLinkSmoke => DesktopCommandLineRunner.RunShellShareLinkSmokeAsync(
                    paths,
                    startupOptions,
                    Console.Out),
                DesktopStartupCommand.SocketCleanupSmoke => DesktopCommandLineRunner.RunSocketCleanupSmokeAsync(
                    paths,
                    startupOptions,
                    Console.Out),
                DesktopStartupCommand.ResolveShellShareLink => DesktopCommandLineRunner.RunShellShareLinkTargetAsync(
                    paths,
                    startupOptions,
                    Console.Out),
                DesktopStartupCommand.CopyShellShareLink => DesktopCommandLineRunner.RunShellShareLinkCopyAsync(
                    paths,
                    startupOptions,
                    Console.Out),
                DesktopStartupCommand.CleanupCloudFiles => DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                    paths,
                    startupOptions,
                    Console.Out),
                DesktopStartupCommand.ExportDiagnostics => DesktopCommandLineRunner.RunExportDiagnosticsAsync(
                    paths,
                    startupOptions,
                    Console.Out),
                DesktopStartupCommand.None => throw new ArgumentOutOfRangeException(
                    nameof(command),
                    command,
                    "A command is required."),
                _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported startup command."),
            };
            return commandTask.GetAwaiter().GetResult();
        }

        private static int RunDesktopApplication(
            string[] args,
            DesktopStartupOptions startupOptions,
            DesktopAppPaths paths,
            Func<AppBuilder> createApp)
        {
            DesktopAppIdentity.ApplyToCurrentProcess();
            using DesktopInstallerRuntimeMutex installerMutex = DesktopInstallerRuntimeMutex.CreateForCurrentPlatform();
            using DesktopSingleInstanceGuard? singleInstance = DesktopSingleInstanceGuard
                .TryAcquire(paths.SingleInstanceLockPath);
            if (singleInstance is null)
            {
                RequestExistingInstanceActivation(startupOptions, paths);
                return 0;
            }

            App.StartupOptions = startupOptions;
            App.StartupPaths = paths;
            createApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }

        private static void RequestExistingInstanceActivation(
            DesktopStartupOptions startupOptions,
            DesktopAppPaths paths)
        {
            if (!startupOptions.StartMinimizedToTray)
            {
                DesktopSingleInstanceActivation
                    .TryRequestShowAsync(paths.SingleInstanceLockPath)
                    .GetAwaiter()
                    .GetResult();
            }
        }
    }
}
