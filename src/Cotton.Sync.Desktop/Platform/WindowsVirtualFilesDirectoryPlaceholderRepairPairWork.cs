// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
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
        private readonly IAppRunProgressPublisher? _runProgressPublisher;

        public WindowsVirtualFilesDirectoryPlaceholderRepairPairWork(
            ISyncPairWork inner,
            ISyncStateStore stateStore,
            IWindowsCloudFilesAdapter cloudFiles,
            ILocalChangeSuppression? localChangeSuppression = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null,
            IAppRunProgressPublisher? runProgressPublisher = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _cloudFiles = cloudFiles ?? throw new ArgumentNullException(nameof(cloudFiles));
            _localChangeSuppression = localChangeSuppression;
            _diagnostics = diagnostics ?? WindowsCloudFilesDiagnostics.Shared;
            _runProgressPublisher = runProgressPublisher;
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
            await RepairAfterWindowsVirtualFilesRunAsync(syncPair, request, cancellationToken).ConfigureAwait(false);
        }

        private async Task RepairAfterFullWindowsVirtualFilesRunAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            await RepairAfterWindowsVirtualFilesRunAsync(
                    syncPair,
                    SyncRunRequest.Full,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task RepairAfterWindowsVirtualFilesRunAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                return;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            IReadOnlyList<SyncStateEntry> directories = request.IsFull
                ? await LoadFullRepairDirectoriesAsync(syncPair, cancellationToken).ConfigureAwait(false)
                : await LoadScopedRepairDirectoriesAsync(syncPair, request, cancellationToken).ConfigureAwait(false);

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
            DateTime startedAtUtc = DateTime.UtcNow;
            PublishRepairProgress(syncPair.Id, startedAtUtc, repairedCount, directories.Count, isCompleted: false);
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
                    PublishRepairProgress(syncPair.Id, startedAtUtc, repairedCount, directories.Count, isCompleted: false);
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
            PublishRepairProgress(syncPair.Id, startedAtUtc, repairedCount, directories.Count, isCompleted: true);
            RecordRepairSummary(
                syncPair,
                status: "completed",
                candidateCount: directories.Count,
                repairedCount: repairedCount,
                elapsedMilliseconds: stopwatch.ElapsedMilliseconds);
        }

        private void PublishRepairProgress(
            Guid syncPairId,
            DateTime startedAtUtc,
            int repairedCount,
            int totalCount,
            bool isCompleted)
        {
            _runProgressPublisher?.Publish(new AppRunProgress(
                syncPairId,
                SyncRunProgressStage.FinalizingCloudFiles,
                repairedCount,
                totalCount,
                string.Empty,
                startedAtUtc,
                isCompleted,
                DateTime.UtcNow));
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

        private async Task<IReadOnlyList<SyncStateEntry>> LoadFullRepairDirectoriesAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            var directories = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in _stateStore
                               .LoadPairDirectoryEntriesAsync(syncPair.Id.ToString("D"), cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                AddRepairDirectory(directories, entry);
            }

            return directories;
        }

        private async Task<IReadOnlyList<SyncStateEntry>> LoadScopedRepairDirectoriesAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            string syncPairId = syncPair.Id.ToString("D");
            var directories = new List<SyncStateEntry>();
            var requestedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ancestorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in request.LocalChangedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryNormalizePath(path, out string normalizedPath))
                {
                    continue;
                }

                requestedKeys.Add(normalizedPath);
                foreach (string ancestor in CreateAncestorDirectoryPaths(normalizedPath))
                {
                    ancestorKeys.Add(ancestor);
                }
            }

            await foreach (SyncStateEntry entry in _stateStore
                               .LoadEntriesByPathKeysAsync(syncPairId, ancestorKeys, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                AddRepairDirectory(directories, entry);
            }

            foreach (string requestedKey in requestedKeys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                await foreach (SyncStateEntry entry in _stateStore
                                   .LoadDirectoryEntriesByPathPrefixAsync(syncPairId, requestedKey, cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    AddRepairDirectory(directories, entry);
                }
            }

            return directories
                .GroupBy(static entry => SyncPath.ToKey(entry.RelativePath), StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToArray();
        }

        private static void AddRepairDirectory(ICollection<SyncStateEntry> directories, SyncStateEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.RelativePath)
                && entry.Kind == SyncEntryKind.Directory
                && entry.RemoteNodeId.HasValue)
            {
                directories.Add(entry);
            }
        }

        private static bool TryNormalizePath(string path, out string normalizedPath)
        {
            normalizedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                normalizedPath = SyncPath.Normalize(path);
                return !string.IsNullOrWhiteSpace(normalizedPath) && normalizedPath != ".";
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static IEnumerable<string> CreateAncestorDirectoryPaths(string relativePath)
        {
            string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int length = 1; length < segments.Length; length++)
            {
                yield return string.Join("/", segments.Take(length));
            }
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
