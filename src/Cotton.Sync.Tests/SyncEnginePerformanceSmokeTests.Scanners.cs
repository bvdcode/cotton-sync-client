// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Tests
{
    public partial class SyncEnginePerformanceSmokeTests
    {
        private class StaticRemoteTreeCrawler : IRemoteTreeStreamingCrawler, IRemotePathLookupCrawler
        {
            private readonly IReadOnlyList<RemoteFileSnapshot> _files;
            private readonly IReadOnlyList<RemoteDirectorySnapshot> _directories;

            public StaticRemoteTreeCrawler(
                IReadOnlyList<RemoteFileSnapshot> files,
                IReadOnlyList<RemoteDirectorySnapshot>? directories = null)
            {
                _files = files;
                _directories = directories ?? [];
            }

            public int FullCrawlCalls { get; private set; }

            public int PathCrawlCalls { get; private set; }

            public int StreamingCrawlCalls { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                FullCrawlCalls++;
                return Task.FromResult(new RemoteTreeSnapshot
                {
                    RootNode = new NodeDto
                    {
                        Id = rootNodeId,
                        Name = "root",
                    },
                    Directories = _directories.ToList(),
                    Files = _files.ToList(),
                });
            }

            public async Task<NodeDto> CrawlStreamingAsync(
                Guid rootNodeId,
                IRemoteTreeStreamSink sink,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                StreamingCrawlCalls++;
                NodeDto root = new NodeDto
                {
                    Id = rootNodeId,
                    Name = "root",
                };
                progress?.Report(new RemoteTreeScanProgress(0, 0, currentPath: null));
                for (int index = 0; index < _directories.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RemoteDirectorySnapshot directory = _directories[index];
                    await sink.AddDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
                    if (index == 0 || (index + 1) % 100 == 0)
                    {
                        progress?.Report(new RemoteTreeScanProgress(0, index + 1, directory.RelativePath));
                    }
                }

                for (int index = 0; index < _files.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RemoteFileSnapshot file = _files[index];
                    await sink.AddFileAsync(file, cancellationToken).ConfigureAwait(false);
                    if (index == 0 || (index + 1) % 100 == 0)
                    {
                        progress?.Report(new RemoteTreeScanProgress(index + 1, 0, file.RelativePath));
                    }
                }

                progress?.Report(new RemoteTreeScanProgress(_files.Count, _directories.Count, currentPath: null));
                return root;
            }

            public Task<RemoteTreeLookupSnapshot> CrawlPathLookupsAsync(
                Guid rootNodeId,
                IReadOnlyCollection<string> relativePaths,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                PathCrawlCalls++;
                RemoteTreeLookupSnapshot snapshot = new RemoteTreeLookupSnapshot
                {
                    RootNode = new NodeDto
                    {
                        Id = rootNodeId,
                        Name = "root",
                    },
                };
                HashSet<string> requested = new HashSet<string>(relativePaths.Select(SyncPath.ToKey), StringComparer.OrdinalIgnoreCase);
                foreach (RemoteDirectorySnapshot directory in _directories)
                {
                    if (requested.Contains(SyncPath.ToKey(directory.RelativePath)))
                    {
                        snapshot.DirectoriesByPath[SyncPath.ToKey(directory.RelativePath)] = directory;
                    }
                }

                foreach (RemoteFileSnapshot file in _files)
                {
                    if (requested.Contains(SyncPath.ToKey(file.RelativePath)))
                    {
                        snapshot.FilesByPath[SyncPath.ToKey(file.RelativePath)] = file;
                    }
                }

                return Task.FromResult(snapshot);
            }
        }

        private class EmptyLocalFileScanner : ILocalFileScanner
        {
            public Task<IReadOnlyList<LocalFileSnapshot>> ScanAsync(
                string rootPath,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<LocalFileSnapshot>>(Array.Empty<LocalFileSnapshot>());
            }
        }

        private class FailOnFullScanLocalFileScanner :
            ILocalFileScanner,
            ILocalFileMetadataPathLookupScanner,
            ILocalFilePresenceProbe
        {
            private readonly Dictionary<string, LocalFileSnapshot> _filesByPath;

            public FailOnFullScanLocalFileScanner(IEnumerable<SyncStateEntry> baselineEntries)
            {
                _filesByPath = baselineEntries.ToDictionary(
                    entry => SyncPath.ToKey(entry.RelativePath),
                    entry => new LocalFileSnapshot
                    {
                        RelativePath = entry.RelativePath,
                        FullPath = Path.Combine("virtual-root", entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                        SizeBytes = entry.RemoteSizeBytes ?? 0,
                        LastWriteUtc = entry.SyncedAtUtc,
                        IsCloudFilesPlaceholder = true,
                        IsCloudFilesOnlineOnlyPlaceholder =
                            entry.PlaceholderHydrationState != SyncPlaceholderHydrationState.Hydrated,
                    },
                    StringComparer.OrdinalIgnoreCase);
            }

            public int ScanCalls { get; private set; }

            public int PathLookupCalls { get; private set; }

            public Task<IReadOnlyList<LocalFileSnapshot>> ScanAsync(
                string rootPath,
                CancellationToken cancellationToken = default)
            {
                ScanCalls++;
                throw new InvalidOperationException("VFS repeat-pass performance smoke must not run a full local placeholder-tree scan.");
            }

            public Task<LocalTreeLookupSnapshot> ScanPathMetadataLookupsAsync(
                string rootPath,
                IReadOnlyCollection<string> relativePaths,
                IProgress<LocalTreeScanProgress>? progress,
                bool includeDirectoryDescendants,
                CancellationToken cancellationToken = default)
            {
                PathLookupCalls++;
                LocalTreeLookupSnapshot snapshot = new();
                foreach (string relativePath in relativePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string key = SyncPath.ToKey(relativePath);
                    if (_filesByPath.TryGetValue(key, out LocalFileSnapshot? file))
                    {
                        snapshot.FilesByPath[key] = file;
                    }
                }

                return Task.FromResult(snapshot);
            }

            public bool FileExists(string rootPath, string relativePath)
            {
                return _filesByPath.ContainsKey(SyncPath.ToKey(relativePath));
            }
        }

        private class ScopedPathOnlyLocalScanner :
            ILocalFileScanner,
            ILocalFileMetadataPathLookupScanner,
            ILocalFilePresenceProbe,
            ILocalFileContentHasher
        {
            private readonly string _relativePathKey;
            private readonly LocalFileSnapshot _file;

            public ScopedPathOnlyLocalScanner(string relativePath, LocalFileSnapshot file)
            {
                _relativePathKey = SyncPath.ToKey(relativePath);
                _file = file;
            }

            public int FullScanCalls { get; private set; }

            public int PathLookupCalls { get; private set; }

            public Task<IReadOnlyList<LocalFileSnapshot>> ScanAsync(
                string rootPath,
                CancellationToken cancellationToken = default)
            {
                FullScanCalls++;
                throw new InvalidOperationException("1M logical hot-path smoke must not run a full local scan.");
            }

            public Task<LocalTreeLookupSnapshot> ScanPathMetadataLookupsAsync(
                string rootPath,
                IReadOnlyCollection<string> relativePaths,
                IProgress<LocalTreeScanProgress>? progress,
                bool includeDirectoryDescendants,
                CancellationToken cancellationToken = default)
            {
                PathLookupCalls++;
                LocalTreeLookupSnapshot snapshot = new LocalTreeLookupSnapshot();
                if (relativePaths.Select(SyncPath.ToKey).Contains(_relativePathKey, StringComparer.OrdinalIgnoreCase))
                {
                    snapshot.FilesByPath[_relativePathKey] = _file;
                }

                return Task.FromResult(snapshot);
            }

            public bool FileExists(string rootPath, string relativePath)
            {
                return string.Equals(SyncPath.ToKey(relativePath), _relativePathKey, StringComparison.OrdinalIgnoreCase);
            }

            public Task<string> ComputeContentHashAsync(
                LocalFileSnapshot localFile,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(localFile.ContentHash);
            }
        }

    }
}
