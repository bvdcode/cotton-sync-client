// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using static Cotton.Sync.Desktop.Platform.WindowsCloudFilesPlaceholderFactory;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesFilePlaceholderService(
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
        private const int HResultCloudFileUnsuccessful = unchecked((int)0x80070185);
        private const int FileAttributePinned = 0x00080000;
        private const int FileAttributeUnpinned = 0x00100000;

        public IReadOnlyList<RemoteFilePlaceholderResult> CreateFilePlaceholders(
            IReadOnlyList<RemoteFilePlaceholderRequest> requests)
        {
            ArgumentNullException.ThrowIfNull(requests);
            if (requests.Count == 0)
            {
                return [];
            }

            PreparedFilePlaceholder[] prepared = PrepareFilePlaceholders(requests);
            RemoteFilePlaceholderResult[] results = new RemoteFilePlaceholderResult[prepared.Length];
            UpdateExistingFilePlaceholders(prepared, results);
            CreateNewFilePlaceholders(prepared, results);
            return results;
        }

        private PreparedFilePlaceholder[] PrepareFilePlaceholders(
            IReadOnlyList<RemoteFilePlaceholderRequest> requests)
        {
            HashSet<string> registeredRootPaths = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> checkedBaseDirectories = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> createdBaseDirectories = new(StringComparer.OrdinalIgnoreCase);
            PreparedFilePlaceholder[] prepared = new PreparedFilePlaceholder[requests.Count];
            for (int index = 0; index < requests.Count; index++)
            {
                prepared[index] = PrepareFilePlaceholder(
                    index,
                    requests[index],
                    registeredRootPaths,
                    checkedBaseDirectories,
                    createdBaseDirectories);
            }

            return prepared;
        }

        private void UpdateExistingFilePlaceholders(
            IReadOnlyList<PreparedFilePlaceholder> prepared,
            RemoteFilePlaceholderResult[] results)
        {
            foreach (PreparedFilePlaceholder item in prepared.Where(static item => item.UpdateExistingPlaceholder))
            {
                results[item.Index] = UpdateExistingFilePlaceholder(item);
            }
        }

        private RemoteFilePlaceholderResult UpdateExistingFilePlaceholder(PreparedFilePlaceholder item)
        {
            const string operation = "update-placeholder";
            WindowsCloudFilesPinState? existingPinState = ReadExistingPinState(item.FullPlaceholderPath);
            try
            {
                SyncPlaceholderHydrationState hydrationState = ApplyExistingFilePlaceholderUpdate(
                    item,
                    existingPinState);
                inSyncManager.NotifyShellPathUpdated(item.FullPlaceholderPath, isDirectory: false);
                return CreateFilePlaceholderResult(item, hydrationState);
            }
            catch (Exception exception)
            {
                RestorePinStateAfterUpdateFailure(item, existingPinState);
                operationExecutor.RecordFailure(
                    operation,
                    item.SyncPairId,
                    item.LocalRootPath,
                    item.NormalizedRelativePath,
                    exception);
                throw;
            }
        }

        private SyncPlaceholderHydrationState ApplyExistingFilePlaceholderUpdate(
            PreparedFilePlaceholder item,
            WindowsCloudFilesPinState? existingPinState)
        {
            bool restoreHydration = existingPinState == WindowsCloudFilesPinState.Pinned
                || item.ExistingHydrationState == SyncPlaceholderHydrationState.Hydrated;
            if (existingPinState == WindowsCloudFilesPinState.Pinned)
            {
                SetFilePlaceholderPinState(item, WindowsCloudFilesPinState.Unpinned);
            }

            operationExecutor.ExecuteWithTransientPathRetry(
                () => nativeApi.UpdatePlaceholder(item.Placeholder),
                "update-placeholder",
                item.SyncPairId,
                item.LocalRootPath,
                item.NormalizedRelativePath);
            if (restoreHydration)
            {
                return RestoreUpdatedPlaceholderHydration(item, existingPinState);
            }

            if (existingPinState == WindowsCloudFilesPinState.Unpinned)
            {
                SetFilePlaceholderPinState(item, WindowsCloudFilesPinState.Unpinned);
            }

            return SyncPlaceholderHydrationState.RemoteOnly;
        }

        private SyncPlaceholderHydrationState RestoreUpdatedPlaceholderHydration(
            PreparedFilePlaceholder item,
            WindowsCloudFilesPinState? existingPinState)
        {
            bool hydrationRestored = TryHydratePlaceholderOrDefer(item);
            if (existingPinState == WindowsCloudFilesPinState.Pinned)
            {
                SetFilePlaceholderPinState(item, WindowsCloudFilesPinState.Pinned);
            }

            if (!hydrationRestored)
            {
                return SyncPlaceholderHydrationState.RemoteOnly;
            }

            operationExecutor.ExecuteWithTransientPathRetry(
                () => inSyncManager.SetAndVerifyInSyncState(item.FullPlaceholderPath),
                "set-in-sync-state",
                item.SyncPairId,
                item.LocalRootPath,
                item.NormalizedRelativePath);
            return SyncPlaceholderHydrationState.Hydrated;
        }

        private void SetFilePlaceholderPinState(
            PreparedFilePlaceholder item,
            WindowsCloudFilesPinState pinState)
        {
            operationExecutor.ExecuteWithTransientPathRetry(
                () => nativeApi.SetPinState(item.FullPlaceholderPath, pinState),
                "set-pin-state",
                item.SyncPairId,
                item.LocalRootPath,
                item.NormalizedRelativePath);
        }

        private void RestorePinStateAfterUpdateFailure(
            PreparedFilePlaceholder item,
            WindowsCloudFilesPinState? existingPinState)
        {
            if (!existingPinState.HasValue)
            {
                return;
            }

            try
            {
                operationExecutor.ExecuteWithTransientPathRetry(
                    () => nativeApi.SetPinState(item.FullPlaceholderPath, existingPinState.Value),
                    "restore-pin-state",
                    item.SyncPairId,
                    item.LocalRootPath,
                    item.NormalizedRelativePath);
            }
            catch (Exception exception)
            {
                operationExecutor.RecordFailure(
                    "restore-pin-state",
                    item.SyncPairId,
                    item.LocalRootPath,
                    item.NormalizedRelativePath,
                    exception);
            }
        }

        private void CreateNewFilePlaceholders(
            IReadOnlyList<PreparedFilePlaceholder> prepared,
            RemoteFilePlaceholderResult[] results)
        {
            foreach (IGrouping<string, PreparedFilePlaceholder> group in prepared
                .Where(static item => !item.UpdateExistingPlaceholder)
                .GroupBy(static item => item.Placeholder.BaseDirectoryPath, StringComparer.OrdinalIgnoreCase))
            {
                PreparedFilePlaceholder[] batch = [.. group];
                CreateNativeFilePlaceholderBatch(batch);

                foreach (PreparedFilePlaceholder item in batch)
                {
                    results[item.Index] = FinalizeNewFilePlaceholder(item);
                }
            }
        }

        private void CreateNativeFilePlaceholderBatch(IReadOnlyList<PreparedFilePlaceholder> batch)
        {
            const string operation = "create-placeholders";
            try
            {
                nativeApi.CreatePlaceholders(batch.Select(static item => item.Placeholder).ToArray());
            }
            catch (Exception exception)
            {
                foreach (PreparedFilePlaceholder item in batch)
                {
                    operationExecutor.RecordFailure(
                        operation,
                        item.SyncPairId,
                        item.LocalRootPath,
                        item.NormalizedRelativePath,
                        exception);
                }

                throw;
            }
        }

        private RemoteFilePlaceholderResult FinalizeNewFilePlaceholder(PreparedFilePlaceholder item)
        {
            try
            {
                SyncPlaceholderHydrationState hydrationState = ApplyNewFilePlaceholderAvailability(item);
                return CreateFilePlaceholderResult(item, hydrationState);
            }
            catch (Exception exception)
            {
                operationExecutor.RecordFailure(
                    "apply-new-placeholder-availability",
                    item.SyncPairId,
                    item.LocalRootPath,
                    item.NormalizedRelativePath,
                    exception);
                throw;
            }
        }

        private SyncPlaceholderHydrationState ApplyNewFilePlaceholderAvailability(PreparedFilePlaceholder item)
        {
            WindowsCloudFilesPinState pinState = ResolveNewPlaceholderPinState(item.Placeholder.BaseDirectoryPath);
            bool isHydrated = pinState == WindowsCloudFilesPinState.Inherit
                && TryHydratePlaceholderOrDefer(item);
            SetFilePlaceholderPinState(item, pinState);
            if (!isHydrated)
            {
                return SyncPlaceholderHydrationState.RemoteOnly;
            }

            operationExecutor.ExecuteWithTransientPathRetry(
                () => inSyncManager.SetAndVerifyInSyncState(item.FullPlaceholderPath),
                "set-in-sync-state",
                item.SyncPairId,
                item.LocalRootPath,
                item.NormalizedRelativePath);
            inSyncManager.NotifyShellPathUpdated(item.FullPlaceholderPath, isDirectory: false);
            return SyncPlaceholderHydrationState.Hydrated;
        }

        private static RemoteFilePlaceholderResult CreateFilePlaceholderResult(
            PreparedFilePlaceholder item,
            SyncPlaceholderHydrationState hydrationState)
        {
            FileInfo? hydratedFile = hydrationState == SyncPlaceholderHydrationState.Hydrated
                ? new FileInfo(item.FullPlaceholderPath)
                : null;
            hydratedFile?.Refresh();
            return new RemoteFilePlaceholderResult(
                item.FileIdentity,
                hydrationState,
                hydratedFile?.Length,
                hydratedFile?.LastWriteTimeUtc);
        }

        private bool TryHydratePlaceholderOrDefer(PreparedFilePlaceholder item)
        {
            try
            {
                operationExecutor.ExecuteWithTransientPathRetry(
                    () => nativeApi.HydratePlaceholder(item.FullPlaceholderPath),
                    "hydrate-placeholder",
                    item.SyncPairId,
                    item.LocalRootPath,
                    item.NormalizedRelativePath);
                return true;
            }
            catch (WindowsCloudFilesNativeException exception)
                when (exception.Operation == "CfHydratePlaceholder"
                    && exception.HResult == HResultCloudFileUnsuccessful)
            {
                diagnostics.Record(
                    "hydrate-placeholder",
                    "deferred",
                    item.SyncPairId,
                    item.LocalRootPath,
                    item.NormalizedRelativePath,
                    "Immediate hydration was deferred; the inherited pin state remains available for retry.",
                    exception.HResult);
                return false;
            }
        }

        private PreparedFilePlaceholder PrepareFilePlaceholder(
            int index,
            RemoteFilePlaceholderRequest request,
            ISet<string> registeredRootPaths,
            ISet<string> checkedBaseDirectories,
            ISet<string> createdBaseDirectories)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(registeredRootPaths);
            ArgumentNullException.ThrowIfNull(checkedBaseDirectories);
            ArgumentNullException.ThrowIfNull(createdBaseDirectories);
            WindowsVirtualFilesRootSafetyResult safety = rootSafety.Validate(request.LocalRootPath);
            if (!safety.IsSafe)
            {
                throw new InvalidOperationException(safety.Details);
            }

            Guid syncPairId = ParseSyncPairId(request.SyncPairId);
            string normalizedPath = SyncPath.Normalize(request.RelativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(safety.FullPath, normalizedPath);
            if (checkedBaseDirectories.Add(placeholderPath.BaseDirectoryPath))
            {
                pathGuard.EnsureNoForeignReparsePointDescendant(safety.FullPath, placeholderPath.BaseDirectoryPath);
            }

            byte[] syncRootIdentity = CreateSyncRootIdentity(syncPairId, request.RemoteRootNodeId);
            byte[] fileIdentity = CreateFileIdentity(request, normalizedPath);

            if (registeredRootPaths.Add(safety.FullPath))
            {
                registrationManager.EnsureRegistered(request.SyncPairId, safety.FullPath, syncRootIdentity);
            }

            if (createdBaseDirectories.Add(placeholderPath.BaseDirectoryPath))
            {
                Directory.CreateDirectory(placeholderPath.BaseDirectoryPath);
            }

            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            bool updateExistingPlaceholder = File.Exists(fullPlaceholderPath) && isReparsePoint(fullPlaceholderPath);
            if (updateExistingPlaceholder)
            {
                ValidateExistingFilePlaceholderIdentity(request, syncPairId, normalizedPath, fullPlaceholderPath);
            }

            var nativePlaceholder = new WindowsCloudFilesNativePlaceholder(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName,
                fileIdentity,
                request.RemoteFile.SizeBytes,
                request.RemoteFile.CreatedAt,
                request.RemoteFile.UpdatedAt);

            return new PreparedFilePlaceholder(
                index,
                request.SyncPairId,
                safety.FullPath,
                normalizedPath,
                nativePlaceholder,
                fullPlaceholderPath,
                updateExistingPlaceholder,
                fileIdentity,
                request.ExistingHydrationState);
        }

        private void ValidateExistingFilePlaceholderIdentity(
            RemoteFilePlaceholderRequest request,
            Guid syncPairId,
            string normalizedPath,
            string fullPlaceholderPath)
        {
            if (!isCloudFilesReparsePoint(fullPlaceholderPath))
            {
                throw new RemoteFilePlaceholderUnavailableException(
                    normalizedPath,
                    "An existing non-Cloud Files reparse point blocks this cloud item.");
            }

            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Parse(
                nativeApi.GetPlaceholderIdentity(fullPlaceholderPath));
            if (identity.SyncPairId != syncPairId
                || identity.RemoteRootNodeId != request.RemoteRootNodeId
                || identity.NodeFileId != request.RemoteFile.Id
                || !string.Equals(
                    SyncPath.ToKey(identity.RelativePath),
                    SyncPath.ToKey(normalizedPath),
                    StringComparison.Ordinal))
            {
                throw new RemoteFilePlaceholderUnavailableException(
                    normalizedPath,
                    "An existing Cloud Files placeholder has a foreign or stale identity.");
            }
        }
    }
}
