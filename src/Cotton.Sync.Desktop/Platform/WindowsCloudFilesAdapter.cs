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

        private const int MaximumFileIdentityLength = 4096;
        private static readonly Guid ProviderGuid = Guid.Parse("6453b9dc-e042-4a73-a675-c5b2aa6c9607");

        private readonly WindowsVirtualFilesRootSafetyPolicy _rootSafety;
        private readonly IWindowsCloudFilesNativeApi _nativeApi;

        public WindowsCloudFilesAdapter(
            WindowsVirtualFilesRootSafetyPolicy? rootSafety = null,
            IWindowsCloudFilesNativeApi? nativeApi = null)
        {
            _rootSafety = rootSafety ?? new WindowsVirtualFilesRootSafetyPolicy();
            _nativeApi = nativeApi ?? new WindowsCloudFilesNativeApi();
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
            byte[] syncRootIdentity = CreateSyncRootIdentity(syncPairId, request.RemoteRootNodeId);
            byte[] fileIdentity = CreateFileIdentity(request, normalizedPath);

            _nativeApi.RegisterSyncRoot(new WindowsCloudFilesNativeSyncRootRegistration(
                safety.FullPath,
                ProviderName,
                ResolveProviderVersion(),
                ProviderGuid,
                syncRootIdentity));
            Directory.CreateDirectory(placeholderPath.BaseDirectoryPath);
            _nativeApi.CreatePlaceholder(new WindowsCloudFilesNativePlaceholder(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName,
                fileIdentity,
                request.RemoteFile.SizeBytes,
                request.RemoteFile.CreatedAt,
                request.RemoteFile.UpdatedAt));

            return new RemoteFilePlaceholderResult(fileIdentity, SyncPlaceholderHydrationState.RemoteOnly);
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
            byte[] identity = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1,
                product = ProviderId,
                syncPairId = request.SyncPairId,
                remoteRootNodeId = request.RemoteRootNodeId,
                relativePath = normalizedPath,
                nodeFileId = request.RemoteFile.Id,
                nodeId = request.RemoteFile.NodeId,
                fileManifestId = request.RemoteFile.FileManifestId,
                originalNodeFileId = request.RemoteFile.OriginalNodeFileId,
                sizeBytes = request.RemoteFile.SizeBytes,
                contentHash = request.RemoteFile.ContentHash,
                eTag = request.RemoteFile.ETag,
                updatedAt = request.RemoteFile.UpdatedAt,
            });
            if (identity.Length > MaximumFileIdentityLength)
            {
                throw new InvalidOperationException(
                    "Virtual-files placeholder identity exceeds the Windows Cloud Files 4 KB limit.");
            }

            return identity;
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
