// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Avalonia;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;

namespace Cotton.Sync.Desktop
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            DesktopStartupOptions startupOptions = DesktopStartupOptions.Parse(args);
            if (startupOptions.PrintVersion)
            {
                Console.Out.WriteLine(DesktopAppVersion.Current);
                return 0;
            }

            if (startupOptions.RunWindowsVirtualFilesSmoke)
            {
                int startupEnvironmentExitCode = DesktopWindowsVirtualFilesSmokeRunner
                    .PrepareStartupEnvironmentAsync(startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
                if (startupEnvironmentExitCode != 0)
                {
                    return startupEnvironmentExitCode;
                }
            }

            DesktopAppPaths paths = DesktopStartupPathResolver.Resolve(startupOptions);
            DesktopTraceLogging.Install(paths);
            DesktopUnhandledExceptionReporter.Install();
            if (!startupOptions.RunSelfTest
                && !startupOptions.ExportDiagnostics
                && !startupOptions.CleanupCloudFiles
                && !startupOptions.RunWindowsVirtualFilesSmoke
                && !startupOptions.RunLiveSyncSmoke
                && !startupOptions.RunUpdateDiscoverySmoke
                && !startupOptions.RunUpdateInstallSmoke
                && !startupOptions.RunShellShareLinkSmoke
                && !startupOptions.RunSocketCleanupSmoke
                && startupOptions.ShellShareLinkTargetPath is null
                && startupOptions.ShellCopyShareLinkTargetPath is null
                && DesktopPendingUpdateStartup.TryStartPendingUpdate(paths, DesktopAppVersion.Current))
            {
                return 0;
            }

            if (startupOptions.RunSelfTest)
            {
                return DesktopCommandLineRunner
                    .RunSelfTestAsync(paths, startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
            }

            if (startupOptions.RunLiveSyncSmoke)
            {
                return DesktopCommandLineRunner
                    .RunLiveSyncSmokeAsync(paths, startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
            }

            if (startupOptions.RunWindowsVirtualFilesSmoke)
            {
                return DesktopCommandLineRunner
                    .RunWindowsVirtualFilesSmokeAsync(paths, startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
            }

            if (startupOptions.RunUpdateDiscoverySmoke)
            {
                return DesktopCommandLineRunner
                    .RunUpdateDiscoverySmokeAsync(paths, startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
            }

            if (startupOptions.RunUpdateInstallSmoke)
            {
                return DesktopCommandLineRunner
                    .RunUpdateInstallSmokeAsync(paths, startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
            }

            if (startupOptions.RunShellShareLinkSmoke)
            {
                return DesktopCommandLineRunner
                    .RunShellShareLinkSmokeAsync(paths, startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
            }

            if (startupOptions.RunSocketCleanupSmoke)
            {
                return DesktopCommandLineRunner
                    .RunSocketCleanupSmokeAsync(paths, startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
            }

            if (startupOptions.ShellShareLinkTargetPath is not null)
            {
                return DesktopCommandLineRunner
                    .RunShellShareLinkTargetAsync(paths, startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
            }

            if (startupOptions.ShellCopyShareLinkTargetPath is not null)
            {
                return DesktopCommandLineRunner
                    .RunShellShareLinkCopyAsync(paths, startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
            }

            if (startupOptions.CleanupCloudFiles)
            {
                return DesktopCommandLineRunner
                    .RunCloudFilesCleanupAsync(paths, startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
            }

            if (startupOptions.ExportDiagnostics)
            {
                return DesktopCommandLineRunner
                    .RunExportDiagnosticsAsync(paths, startupOptions, Console.Out)
                    .GetAwaiter()
                    .GetResult();
            }

            DesktopAppIdentity.ApplyToCurrentProcess();
            using DesktopInstallerRuntimeMutex installerMutex = DesktopInstallerRuntimeMutex.CreateForCurrentPlatform();
            using DesktopSingleInstanceGuard? singleInstance = DesktopSingleInstanceGuard
                .TryAcquire(paths.SingleInstanceLockPath);
            if (singleInstance is null)
            {
                if (!startupOptions.StartMinimizedToTray)
                {
                    DesktopSingleInstanceActivation
                        .TryRequestShowAsync(paths.SingleInstanceLockPath)
                        .GetAwaiter()
                        .GetResult();
                }

                return 0;
            }

            App.StartupOptions = startupOptions;
            App.StartupPaths = paths;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
        }

    }
}
