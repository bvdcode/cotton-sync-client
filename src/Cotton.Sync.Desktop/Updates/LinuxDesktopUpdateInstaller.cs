// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Globalization;

namespace Cotton.Sync.Desktop.Updates
{
    internal class LinuxDesktopUpdateInstaller(string dataDirectory) : IDesktopUpdateInstaller
    {
        internal const string InstallDirectory = "/opt/cotton-sync";
        private const string ApplicationPath = InstallDirectory + "/Cotton.Sync.Desktop";
        private const string RestartCommand =
            "for attempt in $(seq 1 600); do if ! kill -0 \"$1\" 2>/dev/null; then exec \"$2\" --data-dir \"$3\"; fi; sleep 0.1; done; exit 1";

        public async Task<DesktopUpdateInstallResult> StartInstallAsync(
            string installerPath,
            bool launchAfterUpdate,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
            if (!File.Exists(installerPath))
            {
                throw new FileNotFoundException("Cotton Sync update package was not found.", installerPath);
            }

            if (!string.Equals(Path.GetExtension(installerPath), ".deb", StringComparison.Ordinal))
            {
                throw new InvalidDataException("A Debian package is required for this installation.");
            }

            int processId = await LinuxPackageKitInstaller.InstallAsync(installerPath, cancellationToken)
                .ConfigureAwait(false);
            if (launchAfterUpdate)
            {
                using Process restart = Process.Start(CreateRestartStartInfo(Environment.ProcessId, dataDirectory))
                    ?? throw new InvalidOperationException("Cotton Sync restart could not be scheduled.");
            }

            return new DesktopUpdateInstallResult(processId, true, 0);
        }

        internal static ProcessStartInfo CreateRestartStartInfo(int processId, string dataDirectory)
        {
            ProcessStartInfo startInfo = new("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(RestartCommand);
            startInfo.ArgumentList.Add("cotton-sync-restart");
            startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(ApplicationPath);
            startInfo.ArgumentList.Add(Path.GetFullPath(dataDirectory));
            return startInfo;
        }
    }
}
