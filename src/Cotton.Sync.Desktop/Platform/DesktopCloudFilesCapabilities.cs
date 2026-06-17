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

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            {
                return new SyncPairModeCapabilitySnapshot(
                    false,
                    "Windows virtual files require Windows 10 version 1803 or newer.");
            }

            WindowsStorageProviderSyncRootRegistrar? storageProviderRegistrar =
                WindowsStorageProviderSyncRootRegistrar.TryCreateDefault();
            if (storageProviderRegistrar is null)
            {
                return new SyncPairModeCapabilitySnapshot(
                    false,
                    "Windows Cloud Files API is available, but the Cotton Sync Windows shell helper is not installed.");
            }

            try
            {
                if (!storageProviderRegistrar.IsSupported())
                {
                    return new SyncPairModeCapabilitySnapshot(
                        false,
                        "Windows Cloud Files API is available, but Windows StorageProvider sync-root registration is not supported on this device.");
                }
            }
            catch (Exception exception)
            {
                return new SyncPairModeCapabilitySnapshot(
                    false,
                    "Windows Cloud Files API is available, but Windows StorageProvider sync-root registration could not be verified: "
                    + exception.Message);
            }

            return new SyncPairModeCapabilitySnapshot(
                true,
                "Windows Cloud Files API, StorageProvider sync-root registration, and Explorer dehydration handling are available.");
        }
    }
}
