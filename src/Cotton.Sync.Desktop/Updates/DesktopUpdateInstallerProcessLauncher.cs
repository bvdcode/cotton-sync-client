// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Globalization;

namespace Cotton.Sync.Desktop.Updates
{
    internal class DesktopUpdateInstallerProcessLauncher : IDesktopUpdateInstallerProcessLauncher
    {
        private readonly TimeSpan _earlyFailureProbeTimeout;

        public DesktopUpdateInstallerProcessLauncher(TimeSpan earlyFailureProbeTimeout)
        {
            _earlyFailureProbeTimeout = earlyFailureProbeTimeout;
        }

        public DesktopUpdateInstallResult Start(ProcessStartInfo startInfo)
        {
            ArgumentNullException.ThrowIfNull(startInfo);
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Cotton Sync update installer could not be started.");
            }

            int processId = process.Id;
            bool exitedDuringProbe = process.WaitForExit((int)_earlyFailureProbeTimeout.TotalMilliseconds);
            int? exitCode = exitedDuringProbe ? process.ExitCode : null;
            if (exitedDuringProbe && process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Cotton Sync update installer exited before installing the update. Exit code: "
                    + process.ExitCode.ToString(CultureInfo.InvariantCulture)
                    + ".");
            }

            return new DesktopUpdateInstallResult(processId, exitedDuringProbe, exitCode);
        }
    }
}
