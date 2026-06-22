// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Updates
{
    internal sealed class DesktopUpdateInstaller : IDesktopUpdateInstaller
    {
        private static readonly TimeSpan EarlyFailureProbeTimeout = TimeSpan.FromSeconds(2);

        public DesktopUpdateInstallResult StartSilentInstall(
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

            int processId = process.Id;
            bool exitedDuringProbe = process.WaitForExit((int)EarlyFailureProbeTimeout.TotalMilliseconds);
            int? exitCode = exitedDuringProbe ? process.ExitCode : null;
            if (exitedDuringProbe && process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Cotton Sync update installer exited before installing the update. Exit code: "
                    + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ".");
            }

            return new DesktopUpdateInstallResult(processId, exitedDuringProbe, exitCode);
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
                    "/FORCECLOSEAPPLICATIONS",
                    "/LaunchAfterUpdate=1",
                ]
                :
                [
                    "/VERYSILENT",
                    "/SUPPRESSMSGBOXES",
                    "/NORESTART",
                    "/CLOSEAPPLICATIONS",
                    "/FORCECLOSEAPPLICATIONS",
                ];
            return string.Join(" ", switches);
        }
    }
}
