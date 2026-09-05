// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Shell
{
    internal static class DesktopTrayIconAssetResolver
    {
        public static Uri Resolve(DesktopTrayStatusKind kind)
        {
            return kind switch
            {
                DesktopTrayStatusKind.Idle => Create("icon-192.png"),
                DesktopTrayStatusKind.Syncing => Create("tray-syncing.ico"),
                DesktopTrayStatusKind.Paused => Create("tray-paused.ico"),
                DesktopTrayStatusKind.Offline => Create("tray-offline.ico"),
                DesktopTrayStatusKind.Error => Create("tray-error.ico"),
                DesktopTrayStatusKind.SignedOut => Create("tray-signed-out.png"),
                DesktopTrayStatusKind.Uploading => Create("tray-uploading.ico"),
                DesktopTrayStatusKind.Downloading => Create("tray-downloading.ico"),
                DesktopTrayStatusKind.FreeingSpace => Create("tray-freeing-space.ico"),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown tray status cannot be resolved."),
            };
        }

        private static Uri Create(string assetName)
        {
            return new Uri("avares://Cotton.Sync.Desktop/Assets/" + assetName);
        }
    }
}
