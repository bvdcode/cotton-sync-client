// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesRegistrationManager(
        WindowsVirtualFilesRootSafetyPolicy rootSafety,
        IWindowsCloudFilesNativeApi nativeApi,
        IWindowsStorageProviderSyncRootRegistrar? storageProviderRegistrar,
        IWindowsCloudFilesDiagnostics diagnostics)
    {
        private const int HResultFileNotFound = unchecked((int)0x80070002);
        private const int HResultPathNotFound = unchecked((int)0x80070003);
        private readonly object _registrationGate = new();
        private readonly HashSet<string> _registeredRootPaths = new(StringComparer.OrdinalIgnoreCase);

        public WindowsCloudFilesSyncRootRegistration CreateRegistration(SyncPairSettings syncPair)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                throw new InvalidOperationException(
                    "Cloud Files registration requires a Windows virtual-files sync pair.");
            }

            WindowsVirtualFilesRootSafetyResult safety = rootSafety.Validate(syncPair.LocalRootPath);
            if (!safety.IsSafe)
            {
                throw new InvalidOperationException(safety.Details);
            }

            return new WindowsCloudFilesSyncRootRegistration(
                syncPair.Id,
                WindowsCloudFilesProviderMetadata.ProviderId,
                string.IsNullOrWhiteSpace(syncPair.DisplayName) ? "Cotton Sync" : syncPair.DisplayName.Trim(),
                safety.FullPath);
        }

        public void EnsureRegistered(string syncPairId, string localRootPath, byte[] syncRootIdentity)
        {
            lock (_registrationGate)
            {
                if (_registeredRootPaths.Contains(localRootPath))
                {
                    return;
                }

                try
                {
                    storageProviderRegistrar?.Register(new WindowsStorageProviderSyncRootRegistration(
                        Guid.Parse(syncPairId),
                        localRootPath,
                        WindowsCloudFilesProviderMetadata.ResolveVersion(),
                        WindowsStorageProviderSyncRootRegistrar.ResolveDefaultIconResource()));
                    nativeApi.RegisterSyncRoot(new WindowsCloudFilesNativeSyncRootRegistration(
                        localRootPath,
                        WindowsCloudFilesProviderMetadata.ProviderName,
                        WindowsCloudFilesProviderMetadata.ResolveVersion(),
                        WindowsCloudFilesProviderMetadata.ProviderGuid,
                        syncRootIdentity));
                    _registeredRootPaths.Add(localRootPath);
                }
                catch (Exception exception)
                {
                    RecordFailure("register-sync-root", syncPairId, localRootPath, exception);
                    throw;
                }
            }
        }

        public void Unregister(SyncPairSettings syncPair)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            Exception? failure = null;
            try
            {
                nativeApi.UnregisterSyncRoot(registration.LocalRootPath);
            }
            catch (WindowsCloudFilesNativeException exception) when (IsMissingSyncRoot(exception))
            {
                diagnostics.Record(
                    "unregister-sync-root",
                    "skipped",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    null,
                    "Windows Cloud Files sync root was already absent.",
                    exception.HResult);
            }
            catch (Exception exception)
            {
                failure = exception;
                RecordFailure("unregister-sync-root", syncPair.Id.ToString(), registration.LocalRootPath, exception);
            }

            try
            {
                storageProviderRegistrar?.Unregister(syncPair.Id, registration.LocalRootPath);
            }
            catch (Exception exception)
            {
                failure ??= exception;
                RecordFailure(
                    "unregister-storage-provider-sync-root",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    exception);
            }

            if (failure is not null)
            {
                throw failure;
            }

            diagnostics.Record(
                "unregister-sync-root",
                "completed",
                syncPair.Id.ToString(),
                registration.LocalRootPath,
                null,
                "Windows Cloud Files sync root was unregistered.");
            lock (_registrationGate)
            {
                _registeredRootPaths.Remove(registration.LocalRootPath);
            }
        }

        public WindowsCloudFilesConnection Connect(
            SyncPairSettings syncPair,
            IWindowsCloudFilesCallbackHandler callbackHandler)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(callbackHandler);
            try
            {
                WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
                EnsureRegistered(
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(
                        syncPair.Id,
                        syncPair.RemoteRootNodeId));
                return nativeApi.ConnectSyncRoot(new WindowsCloudFilesConnectionRequest(
                    registration.LocalRootPath,
                    callbackHandler));
            }
            catch (Exception exception)
            {
                RecordFailure("connect-sync-root", syncPair.Id.ToString(), syncPair.LocalRootPath, exception);
                throw;
            }
        }

        private void RecordFailure(
            string operation,
            string? syncPairId,
            string? localRootPath,
            Exception exception)
        {
            diagnostics.Record(
                operation,
                "failed",
                syncPairId,
                localRootPath,
                null,
                exception.Message,
                exception is WindowsCloudFilesNativeException nativeException ? nativeException.HResult : null);
        }

        private static bool IsMissingSyncRoot(WindowsCloudFilesNativeException exception)
        {
            return exception.Operation == "CfUnregisterSyncRoot"
                && (exception.HResult == HResultFileNotFound || exception.HResult == HResultPathNotFound);
        }
    }
}
