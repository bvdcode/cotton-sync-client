// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using static Cotton.Sync.Desktop.Platform.WindowsCloudFilesPlaceholderFactory;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesInSyncManager(
        IWindowsCloudFilesNativeApi nativeApi,
        IWindowsShellChangeNotifier shellChangeNotifier,
        IWindowsCloudFilesDiagnostics diagnostics,
        Func<string, bool> isReparsePoint,
        WindowsCloudFilesRegistrationManager registrationManager,
        WindowsCloudFilesPathGuard pathGuard,
        WindowsCloudFilesNativeOperationExecutor operationExecutor)
    {
        public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = registrationManager.CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            pathGuard.EnsureNoForeignReparsePointDescendant(
                registration.LocalRootPath,
                placeholderPath.BaseDirectoryPath);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            const string operation = "set-in-sync-state";
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
                    "Windows Cloud Files in-sync state was skipped for a missing placeholder.");
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
                    "Windows Cloud Files in-sync state was skipped for a non-placeholder file.");
                return;
            }

            try
            {
                operationExecutor.ExecuteWithTransientPathRetry(
                    () => SetAndVerifyInSyncState(fullPlaceholderPath),
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath);
                NotifyShellPathUpdated(fullPlaceholderPath, isDirectory);
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
                "Windows Cloud Files placeholder was marked in sync.");
        }

        public void SetSyncRootInSyncState(SyncPairSettings syncPair)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            WindowsCloudFilesSyncRootRegistration registration = registrationManager.CreateRegistration(syncPair);
            const string operation = "set-sync-root-in-sync-state";
            try
            {
                operationExecutor.ExecuteWithTransientPathRetry(
                    () => SetAndVerifyInSyncState(registration.LocalRootPath, allowPartialDirectory: true),
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    null);
                shellChangeNotifier.NotifyDirectoryUpdated(registration.LocalRootPath);
            }
            catch (Exception exception)
            {
                operationExecutor.RecordFailure(
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    null,
                    exception);
                throw;
            }

            diagnostics.Record(
                operation,
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                null,
                "Windows Cloud Files sync root was marked in sync after placeholder finalization.");
        }

        public void SetAndVerifyInSyncState(string filePath, bool allowPartialDirectory = false)
        {
            nativeApi.SetInSyncState(filePath);
            VerifyInSyncState(filePath, allowPartialDirectory);
        }

        public void VerifyInSyncState(string filePath, bool allowPartialDirectory = false)
        {
            WindowsCloudFilesPlaceholderState state = nativeApi.GetPlaceholderState(filePath);
            if (!state.HasFlag(WindowsCloudFilesPlaceholderState.InSync))
            {
                throw new InvalidOperationException(
                    "Windows Cloud Files placeholder did not report in-sync state after the native update. State: "
                    + state
                    + ".");
            }

            if (Directory.Exists(filePath)
                && !allowPartialDirectory
                && state.HasFlag(WindowsCloudFilesPlaceholderState.Partial))
            {
                throw new InvalidOperationException(
                    "Windows Cloud Files directory did not report fully populated state after the native update. State: "
                    + state
                    + ".");
            }
        }

        public void NotifyShellPathUpdated(string path, bool isDirectory)
        {
            if (isDirectory)
            {
                shellChangeNotifier.NotifyDirectoryUpdated(path);
                return;
            }

            shellChangeNotifier.NotifyItemUpdated(path);
        }
    }
}
