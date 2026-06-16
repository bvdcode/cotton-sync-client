// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Updates
{
    internal sealed class DesktopUpdateInstaller : IDesktopUpdateInstaller
    {
        public void StartSilentInstall(
            string installerPath,
            bool launchAfterUpdate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
            if (!File.Exists(installerPath))
            {
                throw new FileNotFoundException("Cotton Sync update installer was not found.", installerPath);
            }

            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = BuildSilentInstallArguments(launchAfterUpdate),
                UseShellExecute = true,
                ErrorDialog = false,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory,
            });
            if (process is null)
            {
                throw new InvalidOperationException("Cotton Sync update installer could not be started.");
            }
        }

        internal static string BuildSilentInstallArguments(bool launchAfterUpdate)
        {
            string[] switches = launchAfterUpdate
                ?
                [
                    "/VERYSILENT",
                    "/SUPPRESSMSGBOXES",
                    "/NORESTART",
                    "/CLOSEAPPLICATIONS",
                    "/LaunchAfterUpdate=1",
                ]
                :
                [
                    "/VERYSILENT",
                    "/SUPPRESSMSGBOXES",
                    "/NORESTART",
                    "/CLOSEAPPLICATIONS",
                ];
            return string.Join(" ", switches);
        }
    }
}
