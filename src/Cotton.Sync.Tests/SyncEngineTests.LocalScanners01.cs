// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {

        private class FakeLocalFileScanner :
            ILocalFileScanner,
            ILocalTreeScanner,
            ILocalFileMetadataPathLookupScanner,
            ILocalFilePresenceProbe,
            ILocalFileContentHasher
        {
            public FakeLocalFileScanner(params LocalFileSnapshot[] files)
            {
                Files = files.ToList();
            }

            public List<LocalDirectorySnapshot> Directories { get; } = [];

            public List<LocalFileSnapshot> Files { get; }

            public int ScanCalls { get; private set; }

            public int PathLookupCalls { get; private set; }

            public int ContentHashCalls { get; private set; }

            public Func<LocalFileSnapshot, string>? ContentHashFactory { get; init; }

            public Func<string, bool>? FileExistsFactory { get; init; }

            public bool? LastIncludeDirectoryDescendants { get; private set; }

            public Task<IReadOnlyList<LocalFileSnapshot>> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                ScanCalls++;
                return Task.FromResult<IReadOnlyList<LocalFileSnapshot>>(Files);
            }

            public Task<LocalTreeSnapshot> ScanTreeAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                ScanCalls++;
                return Task.FromResult(new LocalTreeSnapshot
                {
                    Directories = Directories,
                    Files = Files,
                });
            }

            public Task<LocalTreeLookupSnapshot> ScanPathMetadataLookupsAsync(
                string rootPath,
                IReadOnlyCollection<string> relativePaths,
                IProgress<LocalTreeScanProgress>? progress,
                bool includeDirectoryDescendants,
                CancellationToken cancellationToken = default)
            {
                PathLookupCalls++;
                LastIncludeDirectoryDescendants = includeDirectoryDescendants;
                LocalTreeLookupSnapshot snapshot = new LocalTreeLookupSnapshot();
                string[] requested = relativePaths.Select(SyncPath.Normalize).ToArray();
                HashSet<string> requestedKeys = new HashSet<string>(
                    requested.Select(SyncPath.ToKey),
                    StringComparer.OrdinalIgnoreCase);
                foreach (LocalDirectorySnapshot directory in Directories)
                {
                    if (ContainsRequestedPath(directory.RelativePath, requestedKeys, requested, includeDirectoryDescendants))
                    {
                        snapshot.DirectoriesByPath[SyncPath.ToKey(directory.RelativePath)] = directory;
                    }
                }

                foreach (LocalFileSnapshot file in Files)
                {
                    if (ContainsRequestedPath(file.RelativePath, requestedKeys, requested, includeDirectoryDescendants))
                    {
                        snapshot.FilesByPath[SyncPath.ToKey(file.RelativePath)] = file;
                    }
                }

                return Task.FromResult(snapshot);
            }

            private static bool ContainsRequestedPath(
                string relativePath,
                IReadOnlySet<string> requestedKeys,
                IReadOnlyCollection<string> requestedPaths,
                bool includeDirectoryDescendants)
            {
                string key = SyncPath.ToKey(relativePath);
                return requestedKeys.Contains(key)
                    || requestedPaths.Any(path => IsDescendantPath(path, relativePath))
                    || (includeDirectoryDescendants
                        && requestedPaths.Any(path => IsDescendantPath(relativePath, path)));
            }

            private static bool IsDescendantPath(string relativePath, string parentPath)
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                string normalizedParent = SyncPath.Normalize(parentPath).TrimEnd('/');
                return normalizedPath.Length > normalizedParent.Length
                    && normalizedPath.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase);
            }

            public Task<string> ComputeContentHashAsync(
                LocalFileSnapshot localFile,
                CancellationToken cancellationToken = default)
            {
                ContentHashCalls++;
                return Task.FromResult(ContentHashFactory?.Invoke(localFile) ?? localFile.ContentHash);
            }

            public bool FileExists(string rootPath, string relativePath)
            {
                if (FileExistsFactory is not null)
                {
                    return FileExistsFactory(relativePath);
                }

                string key = SyncPath.ToKey(relativePath);
                return Files.Any(file => string.Equals(
                    SyncPath.ToKey(file.RelativePath),
                    key,
                    StringComparison.OrdinalIgnoreCase));
            }
        }


        private class MetadataOnlyLocalFileScanner :
            ILocalFileScanner,
            ILocalTreeScanner,
            ILocalFileMetadataTreeScanner,
            ILocalFileMetadataTreeProgressScanner,
            ILocalFileContentHashProgressHasher
        {
            public MetadataOnlyLocalFileScanner(params LocalFileSnapshot[] files)
            {
                Files = files.ToList();
            }

            public List<LocalFileSnapshot> Files { get; }

            public int ContentHashCalls { get; private set; }

            public bool ReportMetadataScanProgress { get; init; }

            public Task<IReadOnlyList<LocalFileSnapshot>> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<LocalFileSnapshot>>(Files);
            }

            public Task<LocalTreeSnapshot> ScanTreeAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LocalTreeSnapshot
                {
                    Files = Files,
                });
            }

            public Task<LocalTreeSnapshot> ScanTreeMetadataAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LocalTreeSnapshot
                {
                    Files = Files,
                });
            }

            public Task<LocalTreeSnapshot> ScanTreeMetadataAsync(
                string rootPath,
                IProgress<LocalTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                if (ReportMetadataScanProgress)
                {
                    progress?.Report(new LocalTreeScanProgress(0, 0, currentPath: null));
                    for (int index = 0; index < Files.Count; index++)
                    {
                        progress?.Report(new LocalTreeScanProgress(index + 1, 0, Files[index].RelativePath));
                    }

                    progress?.Report(new LocalTreeScanProgress(Files.Count, 0, currentPath: null));
                }

                return ScanTreeMetadataAsync(rootPath, cancellationToken);
            }

            public Task<string> ComputeContentHashAsync(LocalFileSnapshot localFile, CancellationToken cancellationToken = default)
            {
                return ComputeContentHashAsync(localFile, progress: null, cancellationToken);
            }

            public Task<string> ComputeContentHashAsync(
                LocalFileSnapshot localFile,
                IProgress<SyncTransferProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                ContentHashCalls++;
                progress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Hash,
                    localFile.RelativePath,
                    transferredBytes: 0,
                    totalBytes: localFile.SizeBytes));
                progress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Hash,
                    localFile.RelativePath,
                    localFile.SizeBytes,
                    localFile.SizeBytes,
                    isCompleted: true));
                return Task.FromResult("precomputed-content-hash");
            }
        }


        private class LookupOnlyLocalFileScanner :
            ILocalFileScanner,
            ILocalTreeScanner,
            ILocalFileMetadataTreeLookupScanner,
            ILocalFileContentHasher
        {
            public LookupOnlyLocalFileScanner(params LocalFileSnapshot[] files)
            {
                Files = files.ToList();
            }

            public List<LocalFileSnapshot> Files { get; }

            public int LookupScanCalls { get; private set; }

            public int MetadataTreeScanCalls { get; private set; }

            public int TreeScanCalls { get; private set; }

            public Task<IReadOnlyList<LocalFileSnapshot>> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<LocalFileSnapshot>>(Files);
            }

            public Task<LocalTreeSnapshot> ScanTreeAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                TreeScanCalls++;
                return Task.FromResult(new LocalTreeSnapshot
                {
                    Files = Files,
                });
            }

            public Task<LocalTreeSnapshot> ScanTreeMetadataAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                MetadataTreeScanCalls++;
                return Task.FromResult(new LocalTreeSnapshot
                {
                    Files = Files,
                });
            }

            public Task<LocalTreeLookupSnapshot> ScanTreeMetadataLookupsAsync(
                string rootPath,
                IProgress<LocalTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                LookupScanCalls++;
                LocalTreeLookupSnapshot snapshot = new LocalTreeLookupSnapshot();
                foreach (LocalFileSnapshot file in Files)
                {
                    snapshot.FilesByPath.Add(SyncPath.ToKey(file.RelativePath), file);
                }

                return Task.FromResult(snapshot);
            }

            public Task<string> ComputeContentHashAsync(LocalFileSnapshot localFile, CancellationToken cancellationToken = default)
            {
                return Task.FromResult("precomputed-content-hash");
            }
        }
    }
}
