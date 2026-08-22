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
        private class CountingRemoteFilePlaceholderWriter :
            IRemoteFilePlaceholderWriter,
            IRemoteFilePlaceholderPopulationObserver
        {
            private static readonly byte[] PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E];
            private int _count;
            private int _active;
            private int _maxConcurrent;
            private int _beginPopulationCalls;
            private int _endPopulationCalls;
            private string _firstRelativePath = string.Empty;
            private string _lastRelativePath = string.Empty;

            public TimeSpan OperationDelay { get; init; }

            public int Count => Volatile.Read(ref _count);

            public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

            public int BeginPopulationCalls => Volatile.Read(ref _beginPopulationCalls);

            public int EndPopulationCalls => Volatile.Read(ref _endPopulationCalls);

            public string FirstRelativePath => Volatile.Read(ref _firstRelativePath);

            public string LastRelativePath => Volatile.Read(ref _lastRelativePath);

            public IDisposable BeginPopulation(string syncPairId, string localRootPath)
            {
                Interlocked.Increment(ref _beginPopulationCalls);
                return new PopulationLease(this);
            }

            public async Task<RemoteFilePlaceholderResult> CreatePlaceholderAsync(
                RemoteFilePlaceholderRequest request,
                CancellationToken cancellationToken = default)
            {
                int active = Interlocked.Increment(ref _active);
                try
                {
                    int observed;
                    do
                    {
                        observed = Volatile.Read(ref _maxConcurrent);
                        if (active <= observed)
                        {
                            break;
                        }
                    }
                    while (Interlocked.CompareExchange(ref _maxConcurrent, active, observed) != observed);

                    if (OperationDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(OperationDelay, cancellationToken).ConfigureAwait(false);
                    }

                    int count = Interlocked.Increment(ref _count);
                    if (count == 1)
                    {
                        Volatile.Write(ref _firstRelativePath, request.RelativePath);
                    }

                    Volatile.Write(ref _lastRelativePath, request.RelativePath);
                    return new RemoteFilePlaceholderResult(PlaceholderIdentity);
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }

            private class PopulationLease : IDisposable
            {
                private CountingRemoteFilePlaceholderWriter? _owner;

                public PopulationLease(CountingRemoteFilePlaceholderWriter owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    CountingRemoteFilePlaceholderWriter? owner = Interlocked.Exchange(ref _owner, null);
                    if (owner is not null)
                    {
                        Interlocked.Increment(ref owner._endPopulationCalls);
                    }
                }
            }
        }

        private class GuardedRemoteFileSynchronizer : IRemoteFileSynchronizer
        {
            public int UploadCalls { get; private set; }

            public int DownloadCalls { get; private set; }

            public int DeleteCalls { get; private set; }

            public int MoveCalls { get; private set; }

            public Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile = null,
                CancellationToken cancellationToken = default)
            {
                UploadCalls++;
                throw new InvalidOperationException("No-op performance smoke must not upload files.");
            }

            public Task DownloadFileAsync(Guid nodeFileId, Stream destination, CancellationToken cancellationToken = default)
            {
                DownloadCalls++;
                throw new InvalidOperationException("No-op performance smoke must not download files.");
            }

            public Task DeleteFileAsync(
                Guid nodeFileId,
                bool skipTrash = false,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                DeleteCalls++;
                throw new InvalidOperationException("No-op performance smoke must not delete files.");
            }

            public Task<NodeFileManifestDto> MoveFileAsync(
                Guid rootNodeId,
                string relativePath,
                NodeFileManifestDto existingRemoteFile,
                CancellationToken cancellationToken = default)
            {
                MoveCalls++;
                throw new InvalidOperationException("No-op performance smoke must not move files.");
            }
        }

        private class RecordingRemoteFileSynchronizer : IRemoteFileSynchronizer
        {
            public List<UploadCall> Uploads { get; } = [];

            public List<string> UploadInputContentHashes { get; } = [];

            public List<TimeSpan> UploadStartedAt { get; } = [];

            public Stopwatch? MeasurementStopwatch { get; set; }

            public int UploadCalls { get; private set; }

            public int DownloadCalls { get; private set; }

            public int DeleteCalls { get; private set; }

            public int MoveCalls { get; private set; }

            public async Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile = null,
                CancellationToken cancellationToken = default)
            {
                UploadCalls++;
                UploadInputContentHashes.Add(localFile.ContentHash);
                UploadStartedAt.Add(MeasurementStopwatch?.Elapsed ?? TimeSpan.Zero);
                string contentHash = string.IsNullOrWhiteSpace(localFile.ContentHash)
                    ? await HashFileAsync(localFile.FullPath, cancellationToken).ConfigureAwait(false)
                    : localFile.ContentHash;
                NodeFileManifestDto uploaded = RemoteFile(relativePath, contentHash, localFile.SizeBytes);
                uploaded.Id = existingRemoteFile?.Id ?? uploaded.Id;
                uploaded.NodeId = existingRemoteFile?.NodeId ?? rootNodeId;
                uploaded.UpdatedAt = localFile.LastWriteUtc;
                Uploads.Add(new UploadCall(rootNodeId, relativePath, localFile, existingRemoteFile, uploaded));
                return uploaded;
            }

            private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
            {
                await using FileStream stream = File.OpenRead(path);
                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                return Convert.ToHexStringLower(hash);
            }

            public Task DownloadFileAsync(Guid nodeFileId, Stream destination, CancellationToken cancellationToken = default)
            {
                DownloadCalls++;
                throw new InvalidOperationException("Initial upload performance smoke must not download files.");
            }

            public Task DeleteFileAsync(
                Guid nodeFileId,
                bool skipTrash = false,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                DeleteCalls++;
                throw new InvalidOperationException("Initial upload performance smoke must not delete files.");
            }

            public Task<NodeFileManifestDto> MoveFileAsync(
                Guid rootNodeId,
                string relativePath,
                NodeFileManifestDto existingRemoteFile,
                CancellationToken cancellationToken = default)
            {
                MoveCalls++;
                throw new InvalidOperationException("Initial upload performance smoke must not move files.");
            }
        }

    }
}
