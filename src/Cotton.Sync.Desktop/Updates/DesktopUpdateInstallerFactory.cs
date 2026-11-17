// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal static class DesktopUpdateInstallerFactory
    {
        public static IDesktopUpdateInstaller Create(string dataDirectory)
        {
            if (OperatingSystem.IsLinux())
            {
                return new LinuxDesktopUpdateInstaller(dataDirectory);
            }

            if (OperatingSystem.IsWindows())
            {
                return new DesktopUpdateInstaller(dataDirectory);
            }

            throw new PlatformNotSupportedException("Package installation is unavailable on this platform.");
        }
    }
}
