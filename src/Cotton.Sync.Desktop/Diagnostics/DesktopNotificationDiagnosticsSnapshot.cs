// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal record DesktopNotificationDiagnosticsSnapshot(
        string Platform,
        string AdapterName,
        bool IsSupported,
        bool IsDeliveryExecutableAvailable,
        bool IsIconAvailable,
        string AppName,
        string? AppUserModelId,
        bool IsInstalledAppIdentityVerified,
        string IdentityStatus,
        string Details)
    {
        public static DesktopNotificationDiagnosticsSnapshot FromCapability(
            DesktopNotificationCapabilitySnapshot capabilities)
        {
            ArgumentNullException.ThrowIfNull(capabilities);
            string identityStatus = capabilities.Platform switch
            {
                DesktopNotificationPlatform.Windows when capabilities.IsInstalledAppIdentityVerified =>
                    "installed-sender-identity",
                DesktopNotificationPlatform.Windows when capabilities.IsSupported =>
                    "debug-identity-only",
                DesktopNotificationPlatform.Linux when capabilities.IsSupported =>
                    "session-adapter",
                _ => "unsupported",
            };
            string details = identityStatus switch
            {
                "installed-sender-identity" =>
                    "Installed notification sender identity is verified.",
                "debug-identity-only" =>
                    "PowerShell toast delivery helper is available, but installed Start Menu AppUserModelID identity is not verified.",
                "session-adapter" =>
                    "Desktop notification session adapter is available.",
                _ =>
                    "Desktop notifications are not fully available.",
            };

            return new DesktopNotificationDiagnosticsSnapshot(
                capabilities.Platform.ToString(),
                capabilities.AdapterName,
                capabilities.IsSupported,
                !string.IsNullOrWhiteSpace(capabilities.ExecutablePath),
                !string.IsNullOrWhiteSpace(capabilities.IconPath),
                capabilities.AppName,
                capabilities.AppUserModelId,
                capabilities.IsInstalledAppIdentityVerified,
                identityStatus,
                details);
        }
    }
}
