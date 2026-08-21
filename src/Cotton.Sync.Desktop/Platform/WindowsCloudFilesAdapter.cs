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
        private readonly WindowsCloudFilesDirectoryPlaceholderService _directoryPlaceholderService;
        private readonly WindowsCloudFilesAvailabilityManager _availabilityManager;

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
            _directoryPlaceholderService = new WindowsCloudFilesDirectoryPlaceholderService(
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
            _availabilityManager = new WindowsCloudFilesAvailabilityManager(
                _nativeApi,
                _diagnostics,
                _isReparsePoint,
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
            _directoryPlaceholderService.CreateDirectoryPlaceholder(request);
        }









        public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            _availabilityManager.DehydratePlaceholder(syncPair, relativePath);
        }

        public async Task<bool> DehydratePlaceholderIfContentMatchesAsync(
            SyncPairSettings syncPair,
            string relativePath,
            string expectedContentHash,
            Action? contentValidated,
            CancellationToken cancellationToken = default)
        {
            return await _availabilityManager
                .DehydratePlaceholderIfContentMatchesAsync(
                    syncPair,
                    relativePath,
                    expectedContentHash,
                    contentValidated,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public void HydratePlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            _availabilityManager.HydratePlaceholder(syncPair, relativePath);
        }

        public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
        {
            _inSyncManager.SetInSyncState(syncPair, relativePath);
        }

        public void PinPlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            _availabilityManager.PinPlaceholder(syncPair, relativePath);
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
