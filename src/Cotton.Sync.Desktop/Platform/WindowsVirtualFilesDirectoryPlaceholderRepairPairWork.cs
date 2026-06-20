// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsVirtualFilesDirectoryPlaceholderRepairPairWork : ISyncPairWork
    {
        private readonly ISyncPairWork _inner;
        private readonly ISyncStateStore _stateStore;
        private readonly IWindowsCloudFilesAdapter _cloudFiles;
        private readonly ILocalChangeSuppression? _localChangeSuppression;
        private readonly IWindowsCloudFilesDiagnostics _diagnostics;

        public WindowsVirtualFilesDirectoryPlaceholderRepairPairWork(
            ISyncPairWork inner,
            ISyncStateStore stateStore,
            IWindowsCloudFilesAdapter cloudFiles,
            ILocalChangeSuppression? localChangeSuppression = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _cloudFiles = cloudFiles ?? throw new ArgumentNullException(nameof(cloudFiles));
            _localChangeSuppression = localChangeSuppression;
            _diagnostics = diagnostics ?? WindowsCloudFilesDiagnostics.Shared;
        }

        public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            await _inner.RunOnceAsync(syncPair, cancellationToken).ConfigureAwait(false);
            await RepairAfterFullWindowsVirtualFilesRunAsync(syncPair, cancellationToken).ConfigureAwait(false);
        }

        public async Task RunOnceAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(request);
            await _inner.RunOnceAsync(syncPair, request, cancellationToken).ConfigureAwait(false);
            if (request.IsFull)
            {
                await RepairAfterFullWindowsVirtualFilesRunAsync(syncPair, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task RepairAfterFullWindowsVirtualFilesRunAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                return;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            var directories = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in _stateStore
                               .LoadPairDirectoryEntriesAsync(syncPair.Id.ToString("D"), cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(entry.RelativePath) && entry.RemoteNodeId.HasValue)
                {
                    directories.Add(entry);
                }
            }

            if (directories.Count == 0)
            {
                stopwatch.Stop();
                RecordRepairSummary(
                    syncPair,
                    status: "skipped-empty-state",
                    candidateCount: 0,
                    repairedCount: 0,
                    stopwatch.ElapsedMilliseconds);
                return;
            }

            int repairedCount = 0;
            try
            {
                using IDisposable? burst = _localChangeSuppression?.SuppressProviderWriteBurst(
                    syncPair.Id,
                    syncPair.LocalRootPath);
                foreach (SyncStateEntry directory in directories
                             .OrderByDescending(static entry => GetDirectoryDepth(entry.RelativePath))
                             .ThenBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _localChangeSuppression?.SuppressProviderWrite(
                        syncPair.Id,
                        syncPair.LocalRootPath,
                        directory.RelativePath);
                    _cloudFiles.CreateDirectoryPlaceholder(CreateRequest(syncPair, directory));
                    repairedCount++;
                }
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                RecordRepairSummary(
                    syncPair,
                    status: "failed",
                    candidateCount: directories.Count,
                    repairedCount: repairedCount,
                    elapsedMilliseconds: stopwatch.ElapsedMilliseconds,
                    hResult: exception is WindowsCloudFilesNativeException nativeException ? nativeException.HResult : null);
                throw;
            }

            stopwatch.Stop();
            RecordRepairSummary(
                syncPair,
                status: "completed",
                candidateCount: directories.Count,
                repairedCount: repairedCount,
                elapsedMilliseconds: stopwatch.ElapsedMilliseconds);
        }

        private void RecordRepairSummary(
            SyncPairSettings syncPair,
            string status,
            int candidateCount,
            int repairedCount,
            long elapsedMilliseconds,
            int? hResult = null)
        {
            _diagnostics.Record(
                "repair-directory-placeholders",
                status,
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                null,
                $"Remote-backed directory candidates={candidateCount}; repaired={repairedCount}; elapsed={elapsedMilliseconds} ms.",
                hResult);
        }

        private static RemoteDirectoryMaterializationRequest CreateRequest(
            SyncPairSettings syncPair,
            SyncStateEntry directory)
        {
            return new RemoteDirectoryMaterializationRequest(
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                directory.RelativePath,
                new NodeDto
                {
                    Id = directory.RemoteNodeId!.Value,
                    Name = GetLeafName(directory.RelativePath),
                    CreatedAt = directory.SyncedAtUtc,
                    UpdatedAt = directory.SyncedAtUtc,
                });
        }

        private static int GetDirectoryDepth(string relativePath)
        {
            int depth = 1;
            foreach (char character in relativePath)
            {
                if (character == '/')
                {
                    depth++;
                }
            }

            return depth;
        }

        private static string GetLeafName(string relativePath)
        {
            string normalized = SyncPath.Normalize(relativePath);
            int lastSlashIndex = normalized.LastIndexOf('/');
            return lastSlashIndex < 0 ? normalized : normalized[(lastSlashIndex + 1)..];
        }
    }
}
