// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    using Cotton.Sync.App.SyncPairs;

    internal static class DesktopPlatformCapabilities
    {
        public static bool IsAutostartSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

        private static bool _linuxTrayHostAvailable;

        public static bool IsTrayLifecycleSupported => OperatingSystem.IsWindows()
            || (OperatingSystem.IsLinux() && _linuxTrayHostAvailable);

        internal static void SetLinuxTrayHostAvailability(bool available)
        {
            _linuxTrayHostAvailable = available;
        }

        public static async Task InitializeAsync()
        {
            if (OperatingSystem.IsLinux())
            {
                _linuxTrayHostAvailable = await LinuxTrayHostProbe.IsAvailableAsync().ConfigureAwait(false);
            }
        }

        public static DesktopPlatformCapabilitySnapshot CreateSnapshot()
        {
            SyncPairModeCapabilitySnapshot syncPairModes = DesktopCloudFilesCapabilities.CreateSyncPairModeCapabilities();
            return new DesktopPlatformCapabilitySnapshot(
                ResolveOperatingSystemName(),
                NormalizeEnvironmentValue(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")),
                NormalizeEnvironmentValue(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")),
                IsAutostartSupported,
                IsTrayLifecycleSupported,
                ResolveTrayLifecycleDetails(),
                syncPairModes.IsWindowsVirtualFilesSupported,
                syncPairModes.WindowsVirtualFilesDetails);
        }

        private static string ResolveOperatingSystemName()
        {
            if (OperatingSystem.IsWindows())
            {
                return "Windows";
            }

            if (OperatingSystem.IsLinux())
            {
                return "Linux";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "macOS";
            }

            return "Unknown";
        }

        private static string ResolveTrayLifecycleDetails()
        {
            if (OperatingSystem.IsWindows())
            {
                return "Supported on Windows through the native tray lifecycle.";
            }

            if (OperatingSystem.IsLinux())
            {
                return _linuxTrayHostAvailable
                    ? "Supported through the desktop StatusNotifier host."
                    : "No StatusNotifier host is available in this desktop session; the application stays in a normal window.";
            }

            return "Tray lifecycle is not supported on this platform yet.";
        }

        private static string NormalizeEnvironmentValue(string? value)
        {
            string? normalized = value?.Trim();
            return string.IsNullOrEmpty(normalized) ? "Not reported" : normalized;
        }
    }
}
