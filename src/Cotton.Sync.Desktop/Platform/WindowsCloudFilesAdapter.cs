// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using static Cotton.Sync.Desktop.Platform.WindowsCloudFilesPlaceholderFactory;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesAdapter : IWindowsCloudFilesAdapter
    {
        public const string ProviderId = WindowsCloudFilesProviderMetadata.ProviderId;
        public const string ProviderName = WindowsCloudFilesProviderMetadata.ProviderName;

        private const int HResultCloudFileUnsuccessful = unchecked((int)0x80070185);
        private const int FileAttributePinned = 0x00080000;
        private const int FileAttributeUnpinned = 0x00100000;

        private readonly WindowsVirtualFilesRootSafetyPolicy _rootSafety;
        private readonly IWindowsCloudFilesNativeApi _nativeApi;
        private readonly IWindowsShellChangeNotifier _shellChangeNotifier;
        private readonly IWindowsCloudFilesDiagnostics _diagnostics;
        private readonly Func<string, bool> _isReparsePoint;
        private readonly Func<string, bool> _isCloudFilesReparsePoint;
        private readonly Func<string, FileAttributes> _readFileAttributes;
        private readonly WindowsCloudFilesRegistrationManager _registrationManager;
        private readonly WindowsCloudFilesNativeOperationExecutor _operationExecutor;

        public WindowsCloudFilesAdapter(
            WindowsVirtualFilesRootSafetyPolicy? rootSafety = null,
            IWindowsCloudFilesNativeApi? nativeApi = null,
            IWindowsStorageProviderSyncRootRegistrar? storageProviderRegistrar = null,
            IWindowsShellChangeNotifier? shellChangeNotifier = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null,
            Func<string, bool>? isReparsePoint = null,
            Func<string, bool>? isCloudFilesReparsePoint = null,
            Func<string, FileAttributes>? readFileAttributes = null,
            Action<TimeSpan>? transientRetryDelay = null)
        {
            _rootSafety = rootSafety ?? new WindowsVirtualFilesRootSafetyPolicy();
            _nativeApi = nativeApi ?? new WindowsCloudFilesNativeApi();
            IWindowsStorageProviderSyncRootRegistrar? registrar = storageProviderRegistrar
                ?? WindowsStorageProviderSyncRootRegistrar.TryCreateDefault();
            _shellChangeNotifier = shellChangeNotifier ?? new WindowsShellChangeNotifier();
            _diagnostics = diagnostics ?? WindowsCloudFilesDiagnostics.Shared;
            _isReparsePoint = isReparsePoint ?? WindowsCloudFilesReparsePointProbe.IsReparsePoint;
            _isCloudFilesReparsePoint = isCloudFilesReparsePoint
                ?? WindowsCloudFilesReparsePointProbe.IsCloudFilesReparsePoint;
            _readFileAttributes = readFileAttributes ?? File.GetAttributes;
            _operationExecutor = new WindowsCloudFilesNativeOperationExecutor(
                _diagnostics,
                transientRetryDelay ?? Thread.Sleep);
            _registrationManager = new WindowsCloudFilesRegistrationManager(
                _rootSafety,
                _nativeApi,
                registrar,
                _diagnostics);
        }

        public WindowsCloudFilesSyncRootRegistration CreateRegistration(SyncPairSettings syncPair)
        {
            return _registrationManager.CreateRegistration(syncPair);
        }

        public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return CreateFilePlaceholders([request])[0];
        }

        public RemoteFilePlaceholderResult RestoreMissingFilePlaceholder(
            SyncPairSettings syncPair,
            SyncStateEntry fileState)
        {
            return CreateFilePlaceholder(CreateMissingFilePlaceholderRequest(syncPair, fileState));
        }

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
                NotifyShellPathUpdated(item.FullPlaceholderPath, isDirectory: false);
                return CreateFilePlaceholderResult(item, hydrationState);
            }
            catch (Exception exception)
            {
                RestorePinStateAfterUpdateFailure(item, existingPinState);
                _operationExecutor.RecordFailure(
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

            _operationExecutor.ExecuteWithTransientPathRetry(
                () => _nativeApi.UpdatePlaceholder(item.Placeholder),
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

            _operationExecutor.ExecuteWithTransientPathRetry(
                () => SetAndVerifyInSyncState(item.FullPlaceholderPath),
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
            _operationExecutor.ExecuteWithTransientPathRetry(
                () => _nativeApi.SetPinState(item.FullPlaceholderPath, pinState),
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
                _operationExecutor.ExecuteWithTransientPathRetry(
                    () => _nativeApi.SetPinState(item.FullPlaceholderPath, existingPinState.Value),
                    "restore-pin-state",
                    item.SyncPairId,
                    item.LocalRootPath,
                    item.NormalizedRelativePath);
            }
            catch (Exception exception)
            {
                _operationExecutor.RecordFailure(
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
                _nativeApi.CreatePlaceholders(batch.Select(static item => item.Placeholder).ToArray());
            }
            catch (Exception exception)
            {
                foreach (PreparedFilePlaceholder item in batch)
                {
                    _operationExecutor.RecordFailure(
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
                _operationExecutor.RecordFailure(
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

            _operationExecutor.ExecuteWithTransientPathRetry(
                () => SetAndVerifyInSyncState(item.FullPlaceholderPath),
                "set-in-sync-state",
                item.SyncPairId,
                item.LocalRootPath,
                item.NormalizedRelativePath);
            NotifyShellPathUpdated(item.FullPlaceholderPath, isDirectory: false);
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
                _operationExecutor.ExecuteWithTransientPathRetry(
                    () => _nativeApi.HydratePlaceholder(item.FullPlaceholderPath),
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
                _diagnostics.Record(
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
            WindowsVirtualFilesRootSafetyResult safety = _rootSafety.Validate(request.LocalRootPath);
            if (!safety.IsSafe)
            {
                throw new InvalidOperationException(safety.Details);
            }

            Guid syncPairId = ParseSyncPairId(request.SyncPairId);
            string normalizedPath = SyncPath.Normalize(request.RelativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(safety.FullPath, normalizedPath);
            if (checkedBaseDirectories.Add(placeholderPath.BaseDirectoryPath))
            {
                EnsureNoReparsePointDescendant(safety.FullPath, placeholderPath.BaseDirectoryPath);
            }

            byte[] syncRootIdentity = CreateSyncRootIdentity(syncPairId, request.RemoteRootNodeId);
            byte[] fileIdentity = CreateFileIdentity(request, normalizedPath);

            if (registeredRootPaths.Add(safety.FullPath))
            {
                _registrationManager.EnsureRegistered(request.SyncPairId, safety.FullPath, syncRootIdentity);
            }

            if (createdBaseDirectories.Add(placeholderPath.BaseDirectoryPath))
            {
                Directory.CreateDirectory(placeholderPath.BaseDirectoryPath);
            }

            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            bool updateExistingPlaceholder = File.Exists(fullPlaceholderPath) && _isReparsePoint(fullPlaceholderPath);
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
            if (!_isCloudFilesReparsePoint(fullPlaceholderPath))
            {
                throw new RemoteFilePlaceholderUnavailableException(
                    normalizedPath,
                    "An existing non-Cloud Files reparse point blocks this cloud item.");
            }

            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Parse(
                _nativeApi.GetPlaceholderIdentity(fullPlaceholderPath));
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

        public void UnregisterSyncRoot(SyncPairSettings syncPair)
        {
            _registrationManager.Unregister(syncPair);
        }

        public void CreateDirectoryPlaceholder(RemoteDirectoryMaterializationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            WindowsVirtualFilesRootSafetyResult safety = _rootSafety.Validate(request.LocalRootPath);
            if (!safety.IsSafe)
            {
                throw new InvalidOperationException(safety.Details);
            }

            Guid syncPairId = ParseSyncPairId(request.SyncPairId);
            string normalizedPath = SyncPath.Normalize(request.RelativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(safety.FullPath, normalizedPath);
            EnsureNoReparsePointDescendant(safety.FullPath, placeholderPath.BaseDirectoryPath);
            byte[] syncRootIdentity = CreateSyncRootIdentity(syncPairId, request.RemoteRootNodeId);
            byte[] directoryIdentity = CreateDirectoryIdentity(request, normalizedPath);

            _registrationManager.EnsureRegistered(request.SyncPairId, safety.FullPath, syncRootIdentity);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            bool directoryExists = Directory.Exists(fullPlaceholderPath);
            if (directoryExists && _isReparsePoint(fullPlaceholderPath))
            {
                if (!_isCloudFilesReparsePoint(fullPlaceholderPath))
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
                    _operationExecutor.RecordFailure(
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
                    _nativeApi.GetPlaceholderIdentity(fullPlaceholderPath));
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
                () => _operationExecutor.ExecuteWithTransientPathRetry(
                    () => _nativeApi.CreatePlaceholder(directoryPlaceholder),
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
                    _operationExecutor.ExecuteWithTransientPathRetry(
                        () => _nativeApi.ConvertToPlaceholder(
                            fullPlaceholderPath,
                            directoryIdentity,
                            isDirectory: true,
                            markInSync: true),
                        operation,
                        request.SyncPairId,
                        localRootPath,
                        normalizedPath);
                    _operationExecutor.ExecuteWithTransientPathRetry(
                        () => _nativeApi.UpdatePlaceholder(directoryPlaceholder),
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
                _operationExecutor.ExecuteWithTransientPathRetry(
                    () => _nativeApi.SetPinState(fullPlaceholderPath, pinState),
                    "set-pin-state",
                    request.SyncPairId,
                    localRootPath,
                    normalizedPath);
                _operationExecutor.ExecuteWithTransientPathRetry(
                    () => SetAndVerifyInSyncState(fullPlaceholderPath),
                    "set-in-sync-state",
                    request.SyncPairId,
                    localRootPath,
                    normalizedPath);
                _shellChangeNotifier.NotifyDirectoryUpdated(fullPlaceholderPath);
            }
            catch (Exception exception)
            {
                _operationExecutor.RecordFailure(
                    operation,
                    request.SyncPairId,
                    localRootPath,
                    normalizedPath,
                    exception);
                throw;
            }

            _diagnostics.Record(
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
            _operationExecutor.ExecuteWithTransientPathRetry(
                () => _nativeApi.UpdatePlaceholder(directoryPlaceholder),
                "update-directory-placeholder",
                request.SyncPairId,
                localRootPath,
                normalizedPath);
            if (existingPinState.HasValue)
            {
                _operationExecutor.ExecuteWithTransientPathRetry(
                    () => _nativeApi.SetPinState(fullPlaceholderPath, existingPinState.Value),
                    "set-pin-state",
                    request.SyncPairId,
                    localRootPath,
                    normalizedPath);
            }
            _operationExecutor.ExecuteWithTransientPathRetry(
                () => SetAndVerifyInSyncState(fullPlaceholderPath),
                "set-in-sync-state",
                request.SyncPairId,
                localRootPath,
                normalizedPath);
            _shellChangeNotifier.NotifyDirectoryUpdated(fullPlaceholderPath);
            _diagnostics.Record(
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
                attributes = _readFileAttributes(fullPlaceholderPath);
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

        public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            EnsureNoReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            try
            {
                _nativeApi.DehydratePlaceholder(fullPlaceholderPath);
            }
            catch (Exception exception)
            {
                _operationExecutor.RecordFailure(
                    "dehydrate-placeholder",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    exception);
                throw;
            }

            _diagnostics.Record(
                "dehydrate-placeholder",
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                normalizedPath,
                "Windows Cloud Files placeholder was dehydrated.");
            NotifyShellPathUpdated(fullPlaceholderPath, isDirectory: false);
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
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            EnsureNoReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            bool contentMatched;
            try
            {
                contentMatched = await _nativeApi
                    .DehydratePlaceholderIfContentMatchesAsync(
                        fullPlaceholderPath,
                        expectedContentHash,
                        contentValidated,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _operationExecutor.RecordFailure(
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

            _diagnostics.Record(
                "dehydrate-placeholder",
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                normalizedPath,
                "Windows Cloud Files placeholder was atomically validated and dehydrated.");
            NotifyShellPathUpdated(fullPlaceholderPath, isDirectory: false);
            return true;
        }

        public void HydratePlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            EnsureNoReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            const string operation = "hydrate-placeholder";
            if (!File.Exists(fullPlaceholderPath))
            {
                _diagnostics.Record(
                    operation,
                    "skipped",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    "Windows Cloud Files hydration was skipped for a missing placeholder.");
                return;
            }

            if (!_isReparsePoint(fullPlaceholderPath))
            {
                _diagnostics.Record(
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
                _operationExecutor.ExecuteWithTransientPathRetry(
                    () => _nativeApi.HydratePlaceholder(fullPlaceholderPath),
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath);
                _operationExecutor.ExecuteWithTransientPathRetry(
                    () => _nativeApi.SetPinState(fullPlaceholderPath, WindowsCloudFilesPinState.Pinned),
                    "set-pin-state",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath);
                _operationExecutor.ExecuteWithTransientPathRetry(
                    () => SetAndVerifyInSyncState(fullPlaceholderPath),
                    "set-in-sync-state",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath);
                NotifyShellPathUpdated(fullPlaceholderPath, isDirectory: false);
            }
            catch (Exception exception)
            {
                _operationExecutor.RecordFailure(
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    exception);
                throw;
            }

            _diagnostics.Record(
                operation,
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                normalizedPath,
                "Windows Cloud Files placeholder was hydrated for offline availability.");
        }

        public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            EnsureNoReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            const string operation = "set-in-sync-state";
            bool isFile = File.Exists(fullPlaceholderPath);
            bool isDirectory = Directory.Exists(fullPlaceholderPath);
            if (!isFile && !isDirectory)
            {
                _diagnostics.Record(
                    operation,
                    "skipped",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    "Windows Cloud Files in-sync state was skipped for a missing placeholder.");
                return;
            }

            if (isFile && !_isReparsePoint(fullPlaceholderPath))
            {
                _diagnostics.Record(
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
                _operationExecutor.ExecuteWithTransientPathRetry(
                    () => SetAndVerifyInSyncState(fullPlaceholderPath),
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath);
                NotifyShellPathUpdated(fullPlaceholderPath, isDirectory);
            }
            catch (Exception exception)
            {
                _operationExecutor.RecordFailure(
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    exception);
                throw;
            }

            _diagnostics.Record(
                operation,
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                normalizedPath,
                "Windows Cloud Files placeholder was marked in sync.");
        }

        public void PinPlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            EnsureNoReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            const string operation = "pin-placeholder";
            bool isFile = File.Exists(fullPlaceholderPath);
            bool isDirectory = Directory.Exists(fullPlaceholderPath);
            if (!isFile && !isDirectory)
            {
                _diagnostics.Record(
                    operation,
                    "skipped",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    "Windows Cloud Files pin state was skipped for a missing placeholder.");
                return;
            }

            if (isFile && !_isReparsePoint(fullPlaceholderPath))
            {
                _diagnostics.Record(
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
                _operationExecutor.ExecuteWithTransientPathRetry(
                    () => _nativeApi.SetPinState(fullPlaceholderPath, WindowsCloudFilesPinState.Pinned),
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath);
                NotifyShellPathUpdated(fullPlaceholderPath, isDirectory);
            }
            catch (Exception exception)
            {
                _operationExecutor.RecordFailure(
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    exception);
                throw;
            }

            _diagnostics.Record(
                operation,
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                normalizedPath,
                "Windows Cloud Files placeholder was pinned for offline availability.");
        }

        public async Task<RemoteFilePlaceholderResult> FinalizeUploadedFilePlaceholderAsync(
            SyncPairSettings syncPair,
            SyncStateEntry fileState,
            CancellationToken cancellationToken = default)
        {
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            WindowsCloudFilesUploadFinalizationPreparation preparation =
                WindowsCloudFilesUploadFinalizationPolicy.Prepare(
                    syncPair,
                    registration,
                    fileState,
                    _isReparsePoint);
            string normalizedPath = preparation.NormalizedPath;
            string fullPlaceholderPath = preparation.FullPlaceholderPath;
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            EnsureNoReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
            WindowsCloudFilesUploadedFileFinalizationResult finalization;
            const string operation = "finalize-uploaded-file-placeholder";
            try
            {
                finalization = await _nativeApi
                    .FinalizeUploadedFileAsync(
                        preparation.Request,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!finalization.IsFinalized)
                {
                    throw new LocalFileUnavailableException(
                        normalizedPath,
                        fullPlaceholderPath,
                        "the file changed after upload and before Cloud Files finalization.");
                }

                VerifyInSyncState(fullPlaceholderPath);
                _shellChangeNotifier.NotifyItemUpdated(fullPlaceholderPath);
            }
            catch (LocalFileUnavailableException exception)
            {
                _operationExecutor.RecordFailure(operation, syncPair.Id.ToString(), registration.LocalRootPath, normalizedPath, exception);
                throw;
            }
            catch (WindowsCloudFilesNativeException exception) when (WindowsCloudFilesNativeOperationExecutor.IsSharingViolation(exception))
            {
                _operationExecutor.RecordFailure(operation, syncPair.Id.ToString(), registration.LocalRootPath, normalizedPath, exception);
                throw new LocalFileUnavailableException(
                    normalizedPath,
                    fullPlaceholderPath,
                    exception,
                    requiresExclusiveAccess: true);
            }
            catch (IOException exception)
            {
                _operationExecutor.RecordFailure(operation, syncPair.Id.ToString(), registration.LocalRootPath, normalizedPath, exception);
                throw new LocalFileUnavailableException(
                    normalizedPath,
                    fullPlaceholderPath,
                    exception,
                    requiresExclusiveAccess: true);
            }
            catch (Exception exception)
            {
                _operationExecutor.RecordFailure(operation, syncPair.Id.ToString(), registration.LocalRootPath, normalizedPath, exception);
                throw;
            }

            _diagnostics.Record(
                operation,
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                normalizedPath,
                "Uploaded local content was atomically validated and finalized as a Cloud Files placeholder.");
            return new RemoteFilePlaceholderResult(
                preparation.FileIdentity,
                SyncPlaceholderHydrationState.Hydrated,
                finalization.LocalSizeBytes,
                finalization.LocalLastWriteUtc);
        }









        public void SetSyncRootInSyncState(SyncPairSettings syncPair)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            const string operation = "set-sync-root-in-sync-state";
            try
            {
                _operationExecutor.ExecuteWithTransientPathRetry(
                    () => SetAndVerifyInSyncState(registration.LocalRootPath, allowPartialDirectory: true),
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    null);
                _shellChangeNotifier.NotifyDirectoryUpdated(registration.LocalRootPath);
            }
            catch (Exception exception)
            {
                _operationExecutor.RecordFailure(
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    null,
                    exception);
                throw;
            }

            _diagnostics.Record(
                operation,
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                null,
                "Windows Cloud Files sync root was marked in sync.");
        }

        public WindowsCloudFilesPlaceholderState GetPlaceholderState(SyncPairSettings syncPair, string? relativePath = null)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            string? normalizedPath = null;
            string fullPlaceholderPath = registration.LocalRootPath;
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                normalizedPath = SyncPath.Normalize(relativePath);
                PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
                EnsureNoReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
                fullPlaceholderPath = Path.Combine(
                    placeholderPath.BaseDirectoryPath,
                    placeholderPath.RelativeFileName);
            }

            const string operation = "get-placeholder-state";
            try
            {
                return _nativeApi.GetPlaceholderState(fullPlaceholderPath);
            }
            catch (Exception exception)
            {
                _operationExecutor.RecordFailure(
                    operation,
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath,
                    exception);
                throw;
            }
        }

        public byte[] GetPlaceholderIdentity(SyncPairSettings syncPair, string relativePath)
        {
            string fullPlaceholderPath = ResolveTrackedPlaceholderPath(syncPair, relativePath);
            return _nativeApi.GetPlaceholderIdentity(fullPlaceholderPath);
        }

        public void UpdatePlaceholderIdentity(
            SyncPairSettings syncPair,
            string relativePath,
            byte[] placeholderIdentity)
        {
            ArgumentNullException.ThrowIfNull(placeholderIdentity);
            string fullPlaceholderPath = ResolveTrackedPlaceholderPath(syncPair, relativePath);
            _nativeApi.UpdatePlaceholderIdentity(fullPlaceholderPath, placeholderIdentity);
            NotifyShellPathUpdated(fullPlaceholderPath, isDirectory: false);
        }

        private string ResolveTrackedPlaceholderPath(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            EnsureNoReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
            return Path.Combine(placeholderPath.BaseDirectoryPath, placeholderPath.RelativeFileName);
        }

        private void SetAndVerifyInSyncState(string filePath, bool allowPartialDirectory = false)
        {
            _nativeApi.SetInSyncState(filePath);
            VerifyInSyncState(filePath, allowPartialDirectory);
        }

        private void VerifyInSyncState(string filePath, bool allowPartialDirectory = false)
        {
            WindowsCloudFilesPlaceholderState state = _nativeApi.GetPlaceholderState(filePath);
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

        private void NotifyShellPathUpdated(string path, bool isDirectory)
        {
            if (isDirectory)
            {
                _shellChangeNotifier.NotifyDirectoryUpdated(path);
                return;
            }

            _shellChangeNotifier.NotifyItemUpdated(path);
        }

        private void EnsureNoReparsePointDescendant(string syncRootPath, string targetDirectoryPath)
        {
            string root = Path.GetFullPath(syncRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string target = Path.GetFullPath(targetDirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string relative = Path.GetRelativePath(root, target);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                throw new InvalidOperationException("Virtual-files placeholder path escaped the sync root.");
            }

            string current = root;
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.IsNullOrWhiteSpace(segment) || segment is "." or "..")
                {
                    continue;
                }

                current = Path.Combine(current, segment);
                if ((Directory.Exists(current) || File.Exists(current))
                    && _isReparsePoint(current)
                    && !_isCloudFilesReparsePoint(current))
                {
                    throw new InvalidOperationException("Virtual-files placeholder path cannot traverse a reparse point.");
                }
            }
        }

        public WindowsCloudFilesConnection ConnectSyncRoot(
            SyncPairSettings syncPair,
            IWindowsCloudFilesCallbackHandler callbackHandler)
        {
            return _registrationManager.Connect(syncPair, callbackHandler);
        }

        public void TransferData(WindowsCloudFilesTransferData transfer)
        {
            _nativeApi.TransferData(transfer);
        }








        internal static string CreateReparseTagOpenPath(string fullPath)
        {
            return WindowsCloudFilesReparsePointProbe.CreateOpenPath(fullPath);
        }

        internal static uint CreateReparseTagOpenFlags(string fullPath)
        {
            return WindowsCloudFilesReparsePointProbe.CreateOpenFlags(fullPath);
        }






    }
}
