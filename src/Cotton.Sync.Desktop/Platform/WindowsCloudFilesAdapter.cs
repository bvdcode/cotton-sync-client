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
        public const string ProviderName = "Cotton Sync";

        private static readonly Guid ProviderGuid = Guid.Parse("6453b9dc-e042-4a73-a675-c5b2aa6c9607");

        private readonly WindowsVirtualFilesRootSafetyPolicy _rootSafety;
        private readonly IWindowsCloudFilesNativeApi _nativeApi;
        private readonly IWindowsCloudFilesDiagnostics _diagnostics;
        private readonly Func<string, bool> _isReparsePoint;
        private readonly object _registrationGate = new();
        private readonly HashSet<string> _registeredRootPaths = new(StringComparer.OrdinalIgnoreCase);

        public WindowsCloudFilesAdapter(
            WindowsVirtualFilesRootSafetyPolicy? rootSafety = null,
            IWindowsCloudFilesNativeApi? nativeApi = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null,
            Func<string, bool>? isReparsePoint = null)
        {
            _rootSafety = rootSafety ?? new WindowsVirtualFilesRootSafetyPolicy();
            _nativeApi = nativeApi ?? new WindowsCloudFilesNativeApi();
            _diagnostics = diagnostics ?? WindowsCloudFilesDiagnostics.Shared;
            _isReparsePoint = isReparsePoint ?? IsReparsePoint;
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
            try
            {
                _nativeApi.CreatePlaceholder(new WindowsCloudFilesNativePlaceholder(
                    placeholderPath.BaseDirectoryPath,
                    placeholderPath.RelativeFileName,
                    fileIdentity,
                    request.RemoteFile.SizeBytes,
                    request.RemoteFile.CreatedAt,
                    request.RemoteFile.UpdatedAt));
            }
            catch (Exception exception)
            {
                RecordFailure("create-placeholder", request.SyncPairId, safety.FullPath, normalizedPath, exception);
                throw;
            }

            return new RemoteFilePlaceholderResult(fileIdentity, SyncPlaceholderHydrationState.RemoteOnly);
        }

        public void UnregisterSyncRoot(SyncPairSettings syncPair)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            WindowsCloudFilesSyncRootRegistration registration = CreateRegistration(syncPair);
            try
            {
                _nativeApi.UnregisterSyncRoot(registration.LocalRootPath);
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
            catch (Exception exception)
            {
                RecordFailure("unregister-sync-root", syncPair.Id.ToString(), registration.LocalRootPath, null, exception);
                throw;
            }
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
