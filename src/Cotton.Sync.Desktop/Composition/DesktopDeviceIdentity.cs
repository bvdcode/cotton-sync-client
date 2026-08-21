// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;
using Cotton;

namespace Cotton.Sync.Desktop.Composition
{
    internal static class DesktopDeviceIdentity
    {
        public static string CreateUserAgent()
        {
            return "CottonSyncDesktop/" + CreateVersionLabel() + " (" + CreatePlatformLabel() + ")";
        }

        public static string CreateDeviceName()
        {
            string machineName = Environment.MachineName.Trim();
            string deviceName = string.IsNullOrWhiteSpace(machineName)
                ? "Cotton Sync Desktop"
                : "Cotton Sync Desktop (" + machineName + ")";
            return deviceName.Length <= CottonClientHeaders.DeviceNameMaxLength
                ? deviceName
                : deviceName[..CottonClientHeaders.DeviceNameMaxLength];
        }

        private static string CreateVersionLabel()
        {
            string productVersion = DesktopProductVersion.Current;
            int prereleaseStart = productVersion.IndexOf('-', StringComparison.Ordinal);
            string versionText = prereleaseStart > 0
                ? productVersion[..prereleaseStart]
                : productVersion;
            if (!Version.TryParse(versionText, out Version? version))
            {
                return "0.0.0";
            }

            return version.Major + "." + version.Minor + "." + Math.Max(0, version.Build);
        }

        private static string CreatePlatformLabel()
        {
            string os = OperatingSystem.IsWindows()
                ? "Windows"
                : OperatingSystem.IsLinux()
                    ? "Linux"
                    : OperatingSystem.IsMacOS()
                        ? "macOS"
                        : "Unknown OS";
            return os + "; " + RuntimeInformation.ProcessArchitecture;
        }
    }
}
