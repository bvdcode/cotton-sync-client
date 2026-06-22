// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal sealed record DesktopCloudFilesRegistrationDiagnosticsSnapshot(
        bool IsWindows,
        bool IsStorageProviderHelperAvailable,
        bool? IsStorageProviderSupported,
        int VirtualFilesSyncPairCount,
        int RegisteredSyncPairCount,
        int MissingSyncPairCount,
        int UnknownSyncPairCount,
        IReadOnlyList<DesktopCloudFilesSyncPairRegistrationSnapshot> SyncPairs)
    {
        public static DesktopCloudFilesRegistrationDiagnosticsSnapshot Create(
            IReadOnlyList<SyncPairSettings> syncPairs,
            IWindowsStorageProviderSyncRootRegistrar? registrar = null)
        {
            ArgumentNullException.ThrowIfNull(syncPairs);
            SyncPairSettings[] virtualFilePairs = syncPairs
                .Where(static pair => pair.Mode == SyncPairMode.WindowsVirtualFiles)
                .ToArray();
            bool isWindows = OperatingSystem.IsWindows();
            IWindowsStorageProviderSyncRootRegistrar? storageProviderRegistrar = isWindows
                ? registrar ?? WindowsStorageProviderSyncRootRegistrar.TryCreateDefault()
                : null;
            bool? isStorageProviderSupported = null;
            string? unavailableStatus = null;
            string? unavailableDetails = null;

            if (!isWindows)
            {
                unavailableStatus = "unsupported-platform";
                unavailableDetails = "Windows virtual files are only available on Windows.";
            }
            else if (storageProviderRegistrar is null)
            {
                unavailableStatus = "helper-unavailable";
                unavailableDetails = "Windows shell helper is not installed.";
            }
            else
            {
                try
                {
                    isStorageProviderSupported = storageProviderRegistrar.IsSupported();
                    if (isStorageProviderSupported == false)
                    {
                        unavailableStatus = "storage-provider-unsupported";
                        unavailableDetails = "Windows StorageProvider sync-root registration is not supported.";
                    }
                }
                catch (Exception exception)
                {
                    unavailableStatus = "query-failed";
                    unavailableDetails = CleanSingleLine(exception.Message);
                }
            }

            var pairs = new List<DesktopCloudFilesSyncPairRegistrationSnapshot>(virtualFilePairs.Length);
            foreach (SyncPairSettings syncPair in virtualFilePairs)
            {
                pairs.Add(CreatePairSnapshot(syncPair, storageProviderRegistrar, unavailableStatus, unavailableDetails));
            }

            int registeredCount = pairs.Count(static pair => string.Equals(pair.Status, "registered", StringComparison.Ordinal));
            int missingCount = pairs.Count(static pair => string.Equals(pair.Status, "not-registered", StringComparison.Ordinal));
            int unknownCount = pairs.Count - registeredCount - missingCount;
            return new DesktopCloudFilesRegistrationDiagnosticsSnapshot(
                isWindows,
                storageProviderRegistrar is not null,
                isStorageProviderSupported,
                virtualFilePairs.Length,
                registeredCount,
                missingCount,
                unknownCount,
                pairs);
        }

        private static DesktopCloudFilesSyncPairRegistrationSnapshot CreatePairSnapshot(
            SyncPairSettings syncPair,
            IWindowsStorageProviderSyncRootRegistrar? registrar,
            string? unavailableStatus,
            string? unavailableDetails)
        {
            if (!string.IsNullOrWhiteSpace(unavailableStatus) || registrar is null)
            {
                return new DesktopCloudFilesSyncPairRegistrationSnapshot(
                    syncPair.Id,
                    syncPair.DisplayName,
                    syncPair.LocalRootPath,
                    syncPair.IsEnabled,
                    IsExpectedRegistered: syncPair.IsEnabled,
                    IsRegistered: null,
                    unavailableStatus ?? "helper-unavailable",
                    unavailableDetails ?? "Windows shell helper is not installed.");
            }

            try
            {
                bool isRegistered = registrar.IsRegistered(syncPair.Id);
                return new DesktopCloudFilesSyncPairRegistrationSnapshot(
                    syncPair.Id,
                    syncPair.DisplayName,
                    syncPair.LocalRootPath,
                    syncPair.IsEnabled,
                    IsExpectedRegistered: syncPair.IsEnabled,
                    isRegistered,
                    isRegistered ? "registered" : "not-registered",
                    isRegistered
                        ? "StorageProvider sync root is registered."
                        : "StorageProvider sync root is missing.");
            }
            catch (Exception exception)
            {
                return new DesktopCloudFilesSyncPairRegistrationSnapshot(
                    syncPair.Id,
                    syncPair.DisplayName,
                    syncPair.LocalRootPath,
                    syncPair.IsEnabled,
                    IsExpectedRegistered: syncPair.IsEnabled,
                    IsRegistered: null,
                    "query-failed",
                    CleanSingleLine(exception.Message));
            }
        }

        private static string CleanSingleLine(string value)
        {
            return (string.IsNullOrWhiteSpace(value) ? "Operation could not be completed." : value)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }
    }

    internal sealed record DesktopCloudFilesSyncPairRegistrationSnapshot(
        Guid SyncPairId,
        string DisplayName,
        string LocalRootPath,
        bool IsEnabled,
        bool IsExpectedRegistered,
        bool? IsRegistered,
        string Status,
        string Details);
}
