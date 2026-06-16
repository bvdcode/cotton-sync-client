// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Platform
{
    internal static class DesktopCloudFilesCapabilities
    {
        public static SyncPairModeCapabilitySnapshot CreateSyncPairModeCapabilities()
        {
            if (!OperatingSystem.IsWindows())
            {
                return new SyncPairModeCapabilitySnapshot(
                    false,
                    "Windows virtual files require the Windows Cloud Files API.");
            }

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
            {
                return new SyncPairModeCapabilitySnapshot(
                    false,
                    "Windows virtual files require Windows 10 version 1709 or newer.");
            }

            return new SyncPairModeCapabilitySnapshot(
                false,
                "Windows Cloud Files API is available, but Cotton Sync virtual files require StorageProvider/Desktop Bridge shell integration before this mode can be enabled.");
        }
    }
}
