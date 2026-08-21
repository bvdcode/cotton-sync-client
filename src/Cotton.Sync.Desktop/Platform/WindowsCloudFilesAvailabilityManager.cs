// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using static Cotton.Sync.Desktop.Platform.WindowsCloudFilesPlaceholderFactory;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesAvailabilityManager(
        IWindowsCloudFilesNativeApi nativeApi,
        IWindowsCloudFilesDiagnostics diagnostics,
        Func<string, bool> isReparsePoint,
        WindowsCloudFilesRegistrationManager registrationManager,
        WindowsCloudFilesNativeOperationExecutor operationExecutor,
        WindowsCloudFilesPathGuard pathGuard,
        WindowsCloudFilesInSyncManager inSyncManager)
    {
        public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = registrationManager.CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            pathGuard.EnsureNoForeignReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            try
            {
                nativeApi.DehydratePlaceholder(fullPlaceholderPath);
            }
            catch (Exception exception)
            {
                operationExecutor.RecordFailure(
                    "dehydrate-placeholder",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    exception);
                throw;
            }

            diagnostics.Record(
                "dehydrate-placeholder",
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                normalizedPath,
                "Windows Cloud Files placeholder was dehydrated.");
            inSyncManager.NotifyShellPathUpdated(fullPlaceholderPath, isDirectory: false);
        }

        public async Task<bool> DehydratePlaceholderIfContentMatchesAsync(
            SyncPairSettings syncPair,
            string relativePath,
            string expectedContentHash,
            Action? contentValidated,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedContentHash);
            WindowsCloudFilesSyncRootRegistration registration = registrationManager.CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            pathGuard.EnsureNoForeignReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            bool contentMatched;
            try
            {
                contentMatched = await nativeApi
                    .DehydratePlaceholderIfContentMatchesAsync(
                        fullPlaceholderPath,
                        expectedContentHash,
                        contentValidated,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                operationExecutor.RecordFailure(
                    "dehydrate-placeholder",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    exception);
                throw;
            }

            if (!contentMatched)
            {
                return false;
            }

            diagnostics.Record(
                "dehydrate-placeholder",
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                normalizedPath,
                "Windows Cloud Files placeholder was atomically validated and dehydrated.");
            inSyncManager.NotifyShellPathUpdated(fullPlaceholderPath, isDirectory: false);
            return true;
        }

        public void HydratePlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = registrationManager.CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            pathGuard.EnsureNoForeignReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            const string operation = "hydrate-placeholder";
            if (!File.Exists(fullPlaceholderPath))
            {
                diagnostics.Record(
                    operation,
                    "skipped",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    "Windows Cloud Files hydration was skipped for a missing placeholder.");
                return;
            }

            if (!isReparsePoint(fullPlaceholderPath))
            {
                diagnostics.Record(
                    operation,
                    "skipped",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    "Windows Cloud Files hydration was skipped for a non-placeholder file.");
                return;
            }

            try
            {
                operationExecutor.ExecuteWithTransientPathRetry(
                    () => nativeApi.HydratePlaceholder(fullPlaceholderPath),
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath);
                operationExecutor.ExecuteWithTransientPathRetry(
                    () => nativeApi.SetPinState(fullPlaceholderPath, WindowsCloudFilesPinState.Pinned),
                    "set-pin-state",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath);
                operationExecutor.ExecuteWithTransientPathRetry(
                    () => inSyncManager.SetAndVerifyInSyncState(fullPlaceholderPath),
                    "set-in-sync-state",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath);
                inSyncManager.NotifyShellPathUpdated(fullPlaceholderPath, isDirectory: false);
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

            diagnostics.Record(
                operation,
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                normalizedPath,
                "Windows Cloud Files placeholder was hydrated for offline availability.");
        }

        public void PinPlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = registrationManager.CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            pathGuard.EnsureNoForeignReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            const string operation = "pin-placeholder";
            bool isFile = File.Exists(fullPlaceholderPath);
            bool isDirectory = Directory.Exists(fullPlaceholderPath);
            if (!isFile && !isDirectory)
            {
                diagnostics.Record(
                    operation,
                    "skipped",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    "Windows Cloud Files pin state was skipped for a missing placeholder.");
                return;
            }

            if (isFile && !isReparsePoint(fullPlaceholderPath))
            {
                diagnostics.Record(
                    operation,
                    "skipped",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    "Windows Cloud Files pin state was skipped for a non-placeholder file.");
                return;
            }

            try
            {
                operationExecutor.ExecuteWithTransientPathRetry(
                    () => nativeApi.SetPinState(fullPlaceholderPath, WindowsCloudFilesPinState.Pinned),
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath);
                inSyncManager.NotifyShellPathUpdated(fullPlaceholderPath, isDirectory);
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

            diagnostics.Record(
                operation,
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                normalizedPath,
                "Windows Cloud Files placeholder was pinned for offline availability.");
        }
    }
}
