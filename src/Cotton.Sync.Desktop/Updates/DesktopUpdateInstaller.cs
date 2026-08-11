// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal class DesktopUpdateInstaller : IDesktopUpdateInstaller
    {
        private static readonly TimeSpan EarlyFailureProbeTimeout = TimeSpan.FromSeconds(2);

        private readonly IDesktopUpdateInstallerProcessLauncher _processLauncher;

        public DesktopUpdateInstaller()
            : this(new DesktopUpdateInstallerProcessLauncher(EarlyFailureProbeTimeout))
        {
        }

        internal DesktopUpdateInstaller(IDesktopUpdateInstallerProcessLauncher processLauncher)
        {
            _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        }

        public DesktopUpdateInstallResult StartSilentInstall(
            string installerPath,
            bool launchAfterUpdate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
            if (!File.Exists(installerPath))
            {
                throw new FileNotFoundException("Cotton Sync update installer was not found.", installerPath);
            }

            return _processLauncher.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = BuildSilentInstallArguments(launchAfterUpdate),
                UseShellExecute = true,
                ErrorDialog = false,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory,
            });
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
