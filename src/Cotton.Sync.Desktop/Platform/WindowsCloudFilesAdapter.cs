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

        private readonly WindowsVirtualFilesRootSafetyPolicy _rootSafety;
        private readonly IWindowsCloudFilesNativeApi _nativeApi;
        private readonly IWindowsShellChangeNotifier _shellChangeNotifier;
        private readonly IWindowsCloudFilesDiagnostics _diagnostics;
        private readonly Func<string, bool> _isReparsePoint;
        private readonly Func<string, bool> _isCloudFilesReparsePoint;
        private readonly Func<string, FileAttributes> _readFileAttributes;
        private readonly WindowsCloudFilesRegistrationManager _registrationManager;
        private readonly WindowsCloudFilesNativeOperationExecutor _operationExecutor;
        private readonly WindowsCloudFilesPathGuard _pathGuard;
        private readonly WindowsCloudFilesInSyncManager _inSyncManager;
        private readonly WindowsCloudFilesPlaceholderInspector _placeholderInspector;
        private readonly WindowsCloudFilesFilePlaceholderService _filePlaceholderService;

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
            _pathGuard = new WindowsCloudFilesPathGuard(_isReparsePoint, _isCloudFilesReparsePoint);
            _inSyncManager = new WindowsCloudFilesInSyncManager(
                _nativeApi,
                _shellChangeNotifier,
                _diagnostics,
                _isReparsePoint,
                _registrationManager,
                _pathGuard,
                _operationExecutor);
            _placeholderInspector = new WindowsCloudFilesPlaceholderInspector(
                _nativeApi,
                _registrationManager,
                _pathGuard,
                _inSyncManager,
                _operationExecutor);
            _filePlaceholderService = new WindowsCloudFilesFilePlaceholderService(
                _rootSafety,
                _nativeApi,
                _diagnostics,
                _isReparsePoint,
                _isCloudFilesReparsePoint,
                _readFileAttributes,
                _registrationManager,
                _operationExecutor,
                _pathGuard,
                _inSyncManager);
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

            return _filePlaceholderService.CreateFilePlaceholders(requests);
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
            _pathGuard.EnsureNoForeignReparsePointDescendant(safety.FullPath, placeholderPath.BaseDirectoryPath);
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
                    () => _inSyncManager.SetAndVerifyInSyncState(fullPlaceholderPath),
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
                () => _inSyncManager.SetAndVerifyInSyncState(fullPlaceholderPath),
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
            _pathGuard.EnsureNoForeignReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
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
            _inSyncManager.NotifyShellPathUpdated(fullPlaceholderPath, isDirectory: false);
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
            _pathGuard.EnsureNoForeignReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
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
            _inSyncManager.NotifyShellPathUpdated(fullPlaceholderPath, isDirectory: false);
            return true;
        }

        public void HydratePlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            _pathGuard.EnsureNoForeignReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
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
                    () => _inSyncManager.SetAndVerifyInSyncState(fullPlaceholderPath),
                    "set-in-sync-state",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    normalizedPath);
                _inSyncManager.NotifyShellPathUpdated(fullPlaceholderPath, isDirectory: false);
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
            _inSyncManager.SetInSyncState(syncPair, relativePath);
        }

        public void PinPlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            string normalizedPath = SyncPath.Normalize(relativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(registration.LocalRootPath, normalizedPath);
            _pathGuard.EnsureNoForeignReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
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
                _inSyncManager.NotifyShellPathUpdated(fullPlaceholderPath, isDirectory);
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
            _pathGuard.EnsureNoForeignReparsePointDescendant(registration.LocalRootPath, placeholderPath.BaseDirectoryPath);
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

                _inSyncManager.VerifyInSyncState(fullPlaceholderPath);
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
            _inSyncManager.SetSyncRootInSyncState(syncPair);
        }

        public WindowsCloudFilesPlaceholderState GetPlaceholderState(
            SyncPairSettings syncPair,
            string? relativePath = null)
        {
            return _placeholderInspector.GetState(syncPair, relativePath);
        }

        public byte[] GetPlaceholderIdentity(SyncPairSettings syncPair, string relativePath)
        {
            return _placeholderInspector.GetIdentity(syncPair, relativePath);
        }

        public void UpdatePlaceholderIdentity(
            SyncPairSettings syncPair,
            string relativePath,
            byte[] placeholderIdentity)
        {
            _placeholderInspector.UpdateIdentity(syncPair, relativePath, placeholderIdentity);
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
