// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Reflection;
using System.Text.Json;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsCloudFilesAdapter : IWindowsCloudFilesAdapter
    {
        public const string ProviderId = "Cotton.Sync.Desktop";
        public const string ProviderName = "Cotton Cloud";

        private const int HResultFileNotFound = unchecked((int)0x80070002);
        private const int HResultPathNotFound = unchecked((int)0x80070003);
        private static readonly Guid ProviderGuid = Guid.Parse("6453b9dc-e042-4a73-a675-c5b2aa6c9607");
        private static readonly TimeSpan[] TransientPathRetryDelays =
        [
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(75),
            TimeSpan.FromMilliseconds(150),
        ];

        private readonly WindowsVirtualFilesRootSafetyPolicy _rootSafety;
        private readonly IWindowsCloudFilesNativeApi _nativeApi;
        private readonly IWindowsStorageProviderSyncRootRegistrar? _storageProviderRegistrar;
        private readonly IWindowsCloudFilesDiagnostics _diagnostics;
        private readonly Func<string, bool> _isReparsePoint;
        private readonly Action<TimeSpan> _transientRetryDelay;
        private readonly object _registrationGate = new();
        private readonly HashSet<string> _registeredRootPaths = new(StringComparer.OrdinalIgnoreCase);

        public WindowsCloudFilesAdapter(
            WindowsVirtualFilesRootSafetyPolicy? rootSafety = null,
            IWindowsCloudFilesNativeApi? nativeApi = null,
            IWindowsStorageProviderSyncRootRegistrar? storageProviderRegistrar = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null,
            Func<string, bool>? isReparsePoint = null,
            Action<TimeSpan>? transientRetryDelay = null)
        {
            _rootSafety = rootSafety ?? new WindowsVirtualFilesRootSafetyPolicy();
            _nativeApi = nativeApi ?? new WindowsCloudFilesNativeApi();
            _storageProviderRegistrar = storageProviderRegistrar ?? WindowsStorageProviderSyncRootRegistrar.TryCreateDefault();
            _diagnostics = diagnostics ?? WindowsCloudFilesDiagnostics.Shared;
            _isReparsePoint = isReparsePoint ?? IsReparsePoint;
            _transientRetryDelay = transientRetryDelay ?? Thread.Sleep;
        }

        public WindowsCloudFilesSyncRootRegistration CreateRegistration(SyncPairSettings syncPair)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                throw new InvalidOperationException("Cloud Files registration requires a Windows virtual-files sync pair.");
            }

            WindowsVirtualFilesRootSafetyResult safety = _rootSafety.Validate(syncPair.LocalRootPath);
            if (!safety.IsSafe)
            {
                throw new InvalidOperationException(safety.Details);
            }

            return new WindowsCloudFilesSyncRootRegistration(
                syncPair.Id,
                ProviderId,
                string.IsNullOrWhiteSpace(syncPair.DisplayName) ? "Cotton Sync" : syncPair.DisplayName.Trim(),
                safety.FullPath);
        }

        public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
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
            byte[] fileIdentity = CreateFileIdentity(request, normalizedPath);

            EnsureSyncRootRegistered(request.SyncPairId, safety.FullPath, syncRootIdentity);

            Directory.CreateDirectory(placeholderPath.BaseDirectoryPath);
            var nativePlaceholder = new WindowsCloudFilesNativePlaceholder(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName,
                fileIdentity,
                request.RemoteFile.SizeBytes,
                request.RemoteFile.CreatedAt,
                request.RemoteFile.UpdatedAt);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            bool updateExistingPlaceholder = File.Exists(fullPlaceholderPath) && _isReparsePoint(fullPlaceholderPath);
            string operation = updateExistingPlaceholder ? "update-placeholder" : "create-placeholder";
            try
            {
                if (updateExistingPlaceholder)
                {
                    ExecuteNativeOperationWithTransientPathRetry(
                        () => _nativeApi.UpdatePlaceholder(nativePlaceholder),
                        operation,
                        request.SyncPairId,
                        safety.FullPath,
                        normalizedPath);
                }
                else
                {
                    _nativeApi.CreatePlaceholder(nativePlaceholder);
                    operation = "set-pin-state";
                    ExecuteNativeOperationWithTransientPathRetry(
                        () => _nativeApi.SetPinState(fullPlaceholderPath, WindowsCloudFilesPinState.Unpinned),
                        operation,
                        request.SyncPairId,
                        safety.FullPath,
                        normalizedPath);
                }
            }
            catch (Exception exception)
            {
                RecordFailure(
                    operation,
                    request.SyncPairId,
                    safety.FullPath,
                    normalizedPath,
                    exception);
                throw;
            }

            return new RemoteFilePlaceholderResult(fileIdentity, SyncPlaceholderHydrationState.RemoteOnly);
        }

        public void UnregisterSyncRoot(SyncPairSettings syncPair)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            Exception? failure = null;
            try
            {
                _nativeApi.UnregisterSyncRoot(registration.LocalRootPath);
            }
            catch (Exception exception)
            {
                failure = exception;
                RecordFailure("unregister-sync-root", syncPair.Id.ToString(), registration.LocalRootPath, null, exception);
            }

            try
            {
                _storageProviderRegistrar?.Unregister(syncPair.Id);
            }
            catch (Exception exception)
            {
                failure ??= exception;
                RecordFailure(
                    "unregister-storage-provider-sync-root",
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    null,
                    exception);
            }

            if (failure is not null)
            {
                throw failure;
            }

            _diagnostics.Record(
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
                RecordFailure(
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
                if ((Directory.Exists(current) || File.Exists(current)) && _isReparsePoint(current))
                {
                    throw new InvalidOperationException("Virtual-files placeholder path cannot traverse a reparse point.");
                }
            }
        }

        private void EnsureSyncRootRegistered(
            string syncPairId,
            string localRootPath,
            byte[] syncRootIdentity)
        {
            lock (_registrationGate)
            {
                if (_registeredRootPaths.Contains(localRootPath))
                {
                    return;
                }

                try
                {
                    _storageProviderRegistrar?.Register(new WindowsStorageProviderSyncRootRegistration(
                        Guid.Parse(syncPairId),
                        localRootPath,
                        ResolveProviderVersion(),
                        WindowsStorageProviderSyncRootRegistrar.ResolveDefaultIconResource()));
                    _nativeApi.RegisterSyncRoot(new WindowsCloudFilesNativeSyncRootRegistration(
                        localRootPath,
                        ProviderName,
                        ResolveProviderVersion(),
                        ProviderGuid,
                        syncRootIdentity));
                    _registeredRootPaths.Add(localRootPath);
                }
                catch (Exception exception)
                {
                    RecordFailure("register-sync-root", syncPairId, localRootPath, null, exception);
                    throw;
                }
            }
        }

        public WindowsCloudFilesConnection ConnectSyncRoot(
            SyncPairSettings syncPair,
            IWindowsCloudFilesCallbackHandler callbackHandler)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(callbackHandler);
            try
            {
                WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
                EnsureSyncRootRegistered(
                    syncPair.Id.ToString(),
                    registration.LocalRootPath,
                    CreateSyncRootIdentity(syncPair.Id, syncPair.RemoteRootNodeId));
                return _nativeApi.ConnectSyncRoot(new WindowsCloudFilesConnectionRequest(
                    registration.LocalRootPath,
                    callbackHandler));
            }
            catch (Exception exception)
            {
                RecordFailure("connect-sync-root", syncPair.Id.ToString(), syncPair.LocalRootPath, null, exception);
                throw;
            }
        }

        public void TransferData(WindowsCloudFilesTransferData transfer)
        {
            _nativeApi.TransferData(transfer);
        }

        private static PlaceholderPath ResolvePlaceholderPath(string syncRootPath, string normalizedRelativePath)
        {
            string[] segments = normalizedRelativePath.Split('/');
            if (segments.Any(static segment => segment is "." or ".."))
            {
                throw new InvalidOperationException("Virtual-files placeholder paths cannot contain '.' or '..' segments.");
            }

            string root = Path.GetFullPath(syncRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string relativeFileName = segments[^1];
            string baseDirectoryPath = root;
            foreach (string segment in segments[..^1])
            {
                baseDirectoryPath = Path.Combine(baseDirectoryPath, segment);
            }

            baseDirectoryPath = Path.GetFullPath(baseDirectoryPath);
            string finalPath = Path.GetFullPath(Path.Combine(baseDirectoryPath, relativeFileName));
            if (!IsSamePathOrChild(baseDirectoryPath, root) || !IsSamePathOrChild(finalPath, root))
            {
                throw new InvalidOperationException("Virtual-files placeholder path escaped the sync root.");
            }

            return new PlaceholderPath(baseDirectoryPath, relativeFileName);
        }

        private void RecordFailure(
            string operation,
            string? syncPairId,
            string? localRootPath,
            string? relativePath,
            Exception exception)
        {
            _diagnostics.Record(
                operation,
                "failed",
                syncPairId,
                localRootPath,
                relativePath,
                exception.Message,
                exception is WindowsCloudFilesNativeException nativeException ? nativeException.HResult : null);
        }

        private void ExecuteNativeOperationWithTransientPathRetry(
            Action operation,
            string operationName,
            string? syncPairId,
            string? localRootPath,
            string? relativePath)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    operation();
                    return;
                }
                catch (WindowsCloudFilesNativeException exception)
                    when (IsTransientPathOpenFailure(exception) && attempt < TransientPathRetryDelays.Length)
                {
                    _diagnostics.Record(
                        operationName,
                        "retrying",
                        syncPairId,
                        localRootPath,
                        relativePath,
                        exception.Message,
                        exception.HResult);
                    _transientRetryDelay(TransientPathRetryDelays[attempt]);
                }
            }
        }

        private static bool IsTransientPathOpenFailure(WindowsCloudFilesNativeException exception)
        {
            return exception.Operation == "CreateFile"
                && (exception.HResult == HResultFileNotFound || exception.HResult == HResultPathNotFound);
        }

        private static bool IsSamePathOrChild(string candidatePath, string rootPath)
        {
            string normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string normalizedCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static Guid ParseSyncPairId(string syncPairId)
        {
            if (!Guid.TryParse(syncPairId, out Guid parsed))
            {
                throw new ArgumentException("Virtual-files placeholder request contains an invalid sync pair id.", nameof(syncPairId));
            }

            return parsed;
        }

        private static bool IsReparsePoint(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        private static byte[] CreateSyncRootIdentity(Guid syncPairId, Guid remoteRootNodeId)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1,
                product = ProviderId,
                syncPairId,
                remoteRootNodeId,
            });
        }

        private static byte[] CreateFileIdentity(RemoteFilePlaceholderRequest request, string normalizedPath)
        {
            return WindowsCloudFilesPlaceholderIdentity.Create(request, normalizedPath).ToBytes();
        }

        private static string ResolveProviderVersion()
        {
            Assembly assembly = typeof(WindowsCloudFilesAdapter).Assembly;
            string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            string version = string.IsNullOrWhiteSpace(informationalVersion)
                ? assembly.GetName().Version?.ToString() ?? "unknown"
                : informationalVersion;
            int metadataStart = version.IndexOf('+', StringComparison.Ordinal);
            if (metadataStart > 0)
            {
                version = version[..metadataStart];
            }

            return version.Length <= 255 ? version : version[..255];
        }

        private sealed record PlaceholderPath(string BaseDirectoryPath, string RelativeFileName);
    }
}
