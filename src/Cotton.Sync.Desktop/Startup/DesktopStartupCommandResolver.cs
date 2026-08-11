// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal static class DesktopStartupCommandResolver
    {
        public static DesktopStartupCommand Resolve(DesktopStartupOptions options)
        {
            DesktopStartupCommand command = ResolveSmokeCommand(options);
            if (command != DesktopStartupCommand.None)
            {
                return command;
            }

            command = ResolveShellCommand(options);
            if (command != DesktopStartupCommand.None)
            {
                return command;
            }

            return ResolveMaintenanceCommand(options);
        }

        private static DesktopStartupCommand ResolveSmokeCommand(DesktopStartupOptions options)
        {
            if (options.RunSelfTest)
            {
                return DesktopStartupCommand.SelfTest;
            }

            if (options.RunLiveSyncSmoke)
            {
                return DesktopStartupCommand.LiveSyncSmoke;
            }

            if (options.RunWindowsVirtualFilesSmoke)
            {
                return DesktopStartupCommand.WindowsVirtualFilesSmoke;
            }

            if (options.RunUpdateDiscoverySmoke)
            {
                return DesktopStartupCommand.UpdateDiscoverySmoke;
            }

            if (options.RunUpdateInstallSmoke)
            {
                return DesktopStartupCommand.UpdateInstallSmoke;
            }

            return DesktopStartupCommand.None;
        }

        private static DesktopStartupCommand ResolveShellCommand(DesktopStartupOptions options)
        {
            if (options.RunShellShareLinkSmoke)
            {
                return DesktopStartupCommand.ShellShareLinkSmoke;
            }

            if (options.RunSocketCleanupSmoke)
            {
                return DesktopStartupCommand.SocketCleanupSmoke;
            }

            if (options.ShellShareLinkTargetPath is not null)
            {
                return DesktopStartupCommand.ResolveShellShareLink;
            }

            if (options.ShellCopyShareLinkTargetPath is not null)
            {
                return DesktopStartupCommand.CopyShellShareLink;
            }

            return DesktopStartupCommand.None;
        }

        private static DesktopStartupCommand ResolveMaintenanceCommand(DesktopStartupOptions options)
        {
            if (options.CleanupCloudFiles)
            {
                return DesktopStartupCommand.CleanupCloudFiles;
            }

            if (options.ExportDiagnostics)
            {
                return DesktopStartupCommand.ExportDiagnostics;
            }

            return DesktopStartupCommand.None;
        }
    }
}
