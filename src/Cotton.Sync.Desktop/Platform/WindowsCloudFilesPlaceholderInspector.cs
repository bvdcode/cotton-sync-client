// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using static Cotton.Sync.Desktop.Platform.WindowsCloudFilesPlaceholderFactory;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesPlaceholderInspector(
        IWindowsCloudFilesNativeApi nativeApi,
        WindowsCloudFilesRegistrationManager registrationManager,
        WindowsCloudFilesPathGuard pathGuard,
        WindowsCloudFilesInSyncManager inSyncManager,
        WindowsCloudFilesNativeOperationExecutor operationExecutor)
    {
        public WindowsCloudFilesPlaceholderState GetState(
            SyncPairSettings syncPair,
            string? relativePath = null)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            WindowsCloudFilesSyncRootRegistration registration = registrationManager.CreateRegistration(syncPair);
            string? normalizedPath = null;
            string fullPlaceholderPath = registration.LocalRootPath;
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                normalizedPath = SyncPath.Normalize(relativePath);
                PlaceholderPath placeholderPath = ResolvePlaceholderPath(
                    registration.LocalRootPath,
                    normalizedPath);
                pathGuard.EnsureNoForeignReparsePointDescendant(
                    registration.LocalRootPath,
                    placeholderPath.BaseDirectoryPath);
                fullPlaceholderPath = Path.Combine(
                    placeholderPath.BaseDirectoryPath,
                    placeholderPath.RelativeFileName);
            }

            const string operation = "get-placeholder-state";
            try
            {
                return nativeApi.GetPlaceholderState(fullPlaceholderPath);
            }
            catch (Exception exception)
            {
                operationExecutor.RecordFailure(
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    exception);
                throw;
            }
        }

        public byte[] GetIdentity(SyncPairSettings syncPair, string relativePath)
        {
            return nativeApi.GetPlaceholderIdentity(ResolveTrackedPlaceholderPath(syncPair, relativePath));
        }

        public void UpdateIdentity(
            SyncPairSettings syncPair,
            string relativePath,
            byte[] placeholderIdentity)
        {
            ArgumentNullException.ThrowIfNull(placeholderIdentity);
            string fullPlaceholderPath = ResolveTrackedPlaceholderPath(syncPair, relativePath);
            nativeApi.UpdatePlaceholderIdentity(fullPlaceholderPath, placeholderIdentity);
            inSyncManager.NotifyShellPathUpdated(fullPlaceholderPath, isDirectory: false);
        }

        private string ResolveTrackedPlaceholderPath(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = registrationManager.CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            pathGuard.EnsureNoForeignReparsePointDescendant(
                registration.LocalRootPath,
                placeholderPath.BaseDirectoryPath);
            return Path.Combine(placeholderPath.BaseDirectoryPath, placeholderPath.RelativeFileName);
        }
    }
}
