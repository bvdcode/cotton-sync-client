// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Shell
{
    internal static class DesktopTaskbarOverlayIconAssetResolver
    {
        public static string? Resolve(DesktopTrayStatusKind kind)
        {
            string? assetName = kind switch
            {
                DesktopTrayStatusKind.Unknown => throw new ArgumentOutOfRangeException(
                    nameof(kind),
                    kind,
                    "Unknown taskbar status cannot be resolved."),
                DesktopTrayStatusKind.SignedOut => null,
                DesktopTrayStatusKind.Idle => null,
                DesktopTrayStatusKind.Syncing => "taskbar-syncing.ico",
                DesktopTrayStatusKind.Paused => "taskbar-paused.ico",
                DesktopTrayStatusKind.Offline => "taskbar-offline.ico",
                DesktopTrayStatusKind.Error => "taskbar-error.ico",
                DesktopTrayStatusKind.Uploading => "taskbar-uploading.ico",
                DesktopTrayStatusKind.Downloading => "taskbar-downloading.ico",
                DesktopTrayStatusKind.FreeingSpace => "taskbar-freeing-space.ico",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(kind),
                    kind,
                    "Unsupported taskbar status cannot be resolved."),
            };
            return assetName is null
                ? null
                : Path.Combine(AppContext.BaseDirectory, "Assets", assetName);
        }
    }
}
