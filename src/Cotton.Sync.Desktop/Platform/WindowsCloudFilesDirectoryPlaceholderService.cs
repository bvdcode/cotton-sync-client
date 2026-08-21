// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using static Cotton.Sync.Desktop.Platform.WindowsCloudFilesPlaceholderFactory;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesDirectoryPlaceholderService(
        WindowsVirtualFilesRootSafetyPolicy rootSafety,
        IWindowsCloudFilesNativeApi nativeApi,
        IWindowsCloudFilesDiagnostics diagnostics,
        Func<string, bool> isReparsePoint,
        Func<string, bool> isCloudFilesReparsePoint,
        Func<string, FileAttributes> readFileAttributes,
        WindowsCloudFilesRegistrationManager registrationManager,
        WindowsCloudFilesNativeOperationExecutor operationExecutor,
        WindowsCloudFilesPathGuard pathGuard,
        WindowsCloudFilesInSyncManager inSyncManager)
    {
        private const int FileAttributePinned = 0x00080000;
        private const int FileAttributeUnpinned = 0x00100000;

        public void CreateDirectoryPlaceholder(RemoteDirectoryMaterializationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            WindowsVirtualFilesRootSafetyResult safety = rootSafety.Validate(request.LocalRootPath);
            if (!safety.IsSafe)
            {
                throw new InvalidOperationException(safety.Details);
            }

            Guid syncPairId = ParseSyncPairId(request.SyncPairId);
            string normalizedPath = SyncPath.Normalize(request.RelativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(safety.FullPath, normalizedPath);
            pathGuard.EnsureNoForeignReparsePointDescendant(safety.FullPath, placeholderPath.BaseDirectoryPath);
            byte[] syncRootIdentity = CreateSyncRootIdentity(syncPairId, request.RemoteRootNodeId);
            byte[] directoryIdentity = CreateDirectoryIdentity(request, normalizedPath);

            registrationManager.EnsureRegistered(request.SyncPairId, safety.FullPath, syncRootIdentity);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            bool directoryExists = Directory.Exists(fullPlaceholderPath);
            if (directoryExists && isReparsePoint(fullPlaceholderPath))
            {
                if (!isCloudFilesReparsePoint(fullPlaceholderPath))
                {
                    throw new InvalidOperationException("Virtual-files directory placeholder path cannot replace a non-Cloud Files reparse point.");
                }

                ValidateExistingDirectoryPlaceholderIdentity(
                    request,
                    syncPairId,
                    normalizedPath,
                    fullPlaceholderPath);

                try
                {
                    RepairExistingDirectoryPlaceholder(
                        request,
                        safety.FullPath,
                        normalizedPath,
                        placeholderPath,
                        fullPlaceholderPath,
                        directoryIdentity);
                }
                catch (Exception exception)
                {
                    operationExecutor.RecordFailure(
                        "convert-directory-placeholder",
                        request.SyncPairId,
                        safety.FullPath,
                        normalizedPath,
                        exception);
                    throw;
                }

                return;
            }

            WindowsCloudFilesNativePlaceholder directoryPlaceholder = CreateDirectoryNativePlaceholder(
                placeholderPath,
                directoryIdentity,
                request.RemoteDirectory);
            if (!directoryExists || TryDeleteEmptyDirectory(fullPlaceholderPath))
            {
                CreateRemoteDirectoryPlaceholder(
                    request,
                    safety.FullPath,
                    normalizedPath,
                    fullPlaceholderPath,
                    directoryPlaceholder);
                return;
            }

            ConvertExistingDirectoryPlaceholder(
                request,
                safety.FullPath,
                normalizedPath,
                fullPlaceholderPath,
                directoryIdentity,
                directoryPlaceholder);
        }

        private void ValidateExistingDirectoryPlaceholderIdentity(
            RemoteDirectoryMaterializationRequest request,
            Guid syncPairId,
            string normalizedPath,
            string fullPlaceholderPath)
        {
            WindowsCloudFilesDirectoryPlaceholderIdentity identity =
                WindowsCloudFilesDirectoryPlaceholderIdentity.Parse(
                    nativeApi.GetPlaceholderIdentity(fullPlaceholderPath));
            if (identity.SyncPairId != syncPairId
                || identity.RemoteRootNodeId != request.RemoteRootNodeId
                || identity.NodeId != request.RemoteDirectory.Id
                || !string.Equals(
                    SyncPath.ToKey(identity.RelativePath),
                    SyncPath.ToKey(normalizedPath),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Existing Cloud Files directory placeholder has a foreign or stale identity.");
            }
        }

        private void CreateRemoteDirectoryPlaceholder(
            RemoteDirectoryMaterializationRequest request,
            string localRootPath,
            string normalizedPath,
            string fullPlaceholderPath,
            WindowsCloudFilesNativePlaceholder directoryPlaceholder)
        {
            const string operation = "create-directory-placeholder";
            ApplyDirectoryPlaceholderOperation(
                request,
                localRootPath,
                normalizedPath,
                fullPlaceholderPath,
                directoryPlaceholder.BaseDirectoryPath,
                operation,
                () => operationExecutor.ExecuteWithTransientPathRetry(
                    () => nativeApi.CreatePlaceholder(directoryPlaceholder),
                    operation,
                    request.SyncPairId,
                    localRootPath,
                    normalizedPath),
                "Windows Cloud Files directory placeholder was created and marked in sync.");
        }

        private void ConvertExistingDirectoryPlaceholder(
            RemoteDirectoryMaterializationRequest request,
            string localRootPath,
            string normalizedPath,
            string fullPlaceholderPath,
            byte[] directoryIdentity,
            WindowsCloudFilesNativePlaceholder directoryPlaceholder)
        {
            const string operation = "convert-directory-placeholder";
            ApplyDirectoryPlaceholderOperation(
                request,
                localRootPath,
                normalizedPath,
                fullPlaceholderPath,
                directoryPlaceholder.BaseDirectoryPath,
                operation,
                () =>
                {
                    operationExecutor.ExecuteWithTransientPathRetry(
                        () => nativeApi.ConvertToPlaceholder(
                            fullPlaceholderPath,
                            directoryIdentity,
                            isDirectory: true,
                            markInSync: true),
                        operation,
                        request.SyncPairId,
                        localRootPath,
                        normalizedPath);
                    operationExecutor.ExecuteWithTransientPathRetry(
                        () => nativeApi.UpdatePlaceholder(directoryPlaceholder),
                        "update-directory-placeholder",
                        request.SyncPairId,
                        localRootPath,
                        normalizedPath);
                },
                "Windows Cloud Files directory placeholder was converted and marked in sync.");
        }

        private void ApplyDirectoryPlaceholderOperation(
            RemoteDirectoryMaterializationRequest request,
            string localRootPath,
            string normalizedPath,
            string fullPlaceholderPath,
            string baseDirectoryPath,
            string operation,
            Action placeholderOperation,
            string completedDetails)
        {
            try
            {
                WindowsCloudFilesPinState pinState = ResolveNewPlaceholderPinState(baseDirectoryPath);
                placeholderOperation();
                operationExecutor.ExecuteWithTransientPathRetry(
                    () => nativeApi.SetPinState(fullPlaceholderPath, pinState),
                    "set-pin-state",
                    request.SyncPairId,
                    localRootPath,
                    normalizedPath);
                operationExecutor.ExecuteWithTransientPathRetry(
                    () => inSyncManager.SetAndVerifyInSyncState(fullPlaceholderPath),
                    "set-in-sync-state",
                    request.SyncPairId,
                    localRootPath,
                    normalizedPath);
                _shellChangeNotifier.NotifyDirectoryUpdated(fullPlaceholderPath);
            }
            catch (Exception exception)
            {
                operationExecutor.RecordFailure(
                    operation,
                    request.SyncPairId,
                    localRootPath,
                    normalizedPath,
                    exception);
                throw;
            }

            diagnostics.Record(
                operation,
                "completed",
                request.SyncPairId,
                localRootPath,
                normalizedPath,
                completedDetails);
        }

        private static bool TryDeleteEmptyDirectory(string directoryPath)
        {
            try
            {
                using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(directoryPath).GetEnumerator();
                if (entries.MoveNext())
                {
                    return false;
                }

                Directory.Delete(directoryPath);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void RepairExistingDirectoryPlaceholder(
            RemoteDirectoryMaterializationRequest request,
            string localRootPath,
            string normalizedPath,
            PlaceholderPath placeholderPath,
            string fullPlaceholderPath,
            byte[] directoryIdentity)
        {
            WindowsCloudFilesPinState? existingPinState = ReadExistingPinState(fullPlaceholderPath);
            WindowsCloudFilesNativePlaceholder directoryPlaceholder = CreateDirectoryNativePlaceholder(
                placeholderPath,
                directoryIdentity,
                request.RemoteDirectory);
            operationExecutor.ExecuteWithTransientPathRetry(
                () => nativeApi.UpdatePlaceholder(directoryPlaceholder),
                "update-directory-placeholder",
                request.SyncPairId,
                localRootPath,
                normalizedPath);
            if (existingPinState.HasValue)
            {
                operationExecutor.ExecuteWithTransientPathRetry(
                    () => nativeApi.SetPinState(fullPlaceholderPath, existingPinState.Value),
                    "set-pin-state",
                    request.SyncPairId,
                    localRootPath,
                    normalizedPath);
            }
            operationExecutor.ExecuteWithTransientPathRetry(
                () => inSyncManager.SetAndVerifyInSyncState(fullPlaceholderPath),
                "set-in-sync-state",
                request.SyncPairId,
                localRootPath,
                normalizedPath);
            _shellChangeNotifier.NotifyDirectoryUpdated(fullPlaceholderPath);
            diagnostics.Record(
                "convert-directory-placeholder",
                "repaired-placeholder",
                request.SyncPairId,
                localRootPath,
                normalizedPath,
                "Windows Cloud Files directory placeholder already existed and was repaired.");
        }

        private WindowsCloudFilesPinState? ReadExistingPinState(string fullPlaceholderPath)
        {
            FileAttributes attributes;
            try
            {
                attributes = readFileAttributes(fullPlaceholderPath);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            if (HasRawAttribute(attributes, FileAttributePinned))
            {
                return WindowsCloudFilesPinState.Pinned;
            }

            if (HasRawAttribute(attributes, FileAttributeUnpinned))
            {
                return WindowsCloudFilesPinState.Unpinned;
            }

            return null;
        }

        private WindowsCloudFilesPinState ResolveNewPlaceholderPinState(string parentDirectoryPath)
        {
            return ReadExistingPinState(parentDirectoryPath) == WindowsCloudFilesPinState.Pinned
                ? WindowsCloudFilesPinState.Inherit
                : WindowsCloudFilesPinState.Unpinned;
        }
    }
}
