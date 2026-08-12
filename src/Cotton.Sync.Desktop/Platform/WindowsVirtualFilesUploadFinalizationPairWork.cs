// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Activities;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Nodes;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsVirtualFilesUploadFinalizationPairWork : ISyncPairWork
    {
        private readonly ISyncPairWork _inner;
        private readonly IAppActivityPublisher _activityPublisher;
        private readonly ISyncStateStore _stateStore;
        private readonly IWindowsCloudFilesAdapter _cloudFiles;
        private readonly ILocalChangeSuppression? _localChangeSuppression;
        private readonly IAppRunProgressPublisher? _runProgressPublisher;

        public WindowsVirtualFilesUploadFinalizationPairWork(
            ISyncPairWork inner,
            IAppActivityPublisher activityPublisher,
            ISyncStateStore stateStore,
            IWindowsCloudFilesAdapter cloudFiles,
            ILocalChangeSuppression? localChangeSuppression = null,
            IAppRunProgressPublisher? runProgressPublisher = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _activityPublisher = activityPublisher ?? throw new ArgumentNullException(nameof(activityPublisher));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _cloudFiles = cloudFiles ?? throw new ArgumentNullException(nameof(cloudFiles));
            _localChangeSuppression = localChangeSuppression;
            _runProgressPublisher = runProgressPublisher;
        }

        public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
        {
            await RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken).ConfigureAwait(false);
        }

        public async Task RunOnceAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(request);
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                await _inner.RunOnceAsync(syncPair, request, cancellationToken).ConfigureAwait(false);
                return;
            }

            await RunAndFinalizeUploadsAsync(
                syncPair,
                request,
                () => _inner.RunOnceAsync(syncPair, request, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task RunAndFinalizeUploadsAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            Func<Task> runInnerAsync,
            CancellationToken cancellationToken)
        {
            CloudFilesFinalizationActivityCollector collector = new(syncPair.Id);
            using IDisposable subscription = _activityPublisher.Subscribe(collector);
            await runInnerAsync().ConfigureAwait(false);

            var finalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> finalizationPaths = collector.GetPaths();
            if (finalizationPaths.Count == 0)
            {
                return;
            }

            DateTime startedAtUtc = DateTime.UtcNow;
            int finalizedCount = 0;
            int totalCount = CountFinalizationItems(finalizationPaths);
            PublishFinalizationProgress(syncPair.Id, request, startedAtUtc, finalizedCount, totalCount, isCompleted: false);
            try
            {
                foreach (string relativePath in finalizationPaths)
                {
                    await FinalizeUploadedPathAsync(
                            syncPair,
                            relativePath,
                            finalizedPaths,
                            () =>
                            {
                                finalizedCount++;
                                PublishFinalizationProgress(
                                    syncPair.Id,
                                    request,
                                    startedAtUtc,
                                    finalizedCount,
                                    totalCount,
                                    isCompleted: false);
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                _cloudFiles.SetSyncRootInSyncState(syncPair);
                finalizedCount++;
            }
            finally
            {
                PublishFinalizationProgress(
                    syncPair.Id,
                    request,
                    startedAtUtc,
                    finalizedCount,
                    totalCount,
                    isCompleted: true);
            }
        }

        private async Task FinalizeUploadedPathAsync(
            SyncPairSettings syncPair,
            string relativePath,
            HashSet<string> finalizedPaths,
            Action recordFinalizedPath,
            CancellationToken cancellationToken)
        {
            if (finalizedPaths.Add(relativePath))
            {
                await FinalizeTrackedPathAsync(syncPair, relativePath, cancellationToken).ConfigureAwait(false);
                recordFinalizedPath();
            }

            await FinalizeAncestorDirectoriesAsync(
                    syncPair,
                    relativePath,
                    finalizedPaths,
                    recordFinalizedPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task FinalizeTrackedPathAsync(
            SyncPairSettings syncPair,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SuppressMetadataWrite(syncPair, relativePath);
            SyncStateEntry? state = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (state is null)
            {
                throw CreateMissingFinalizationStateException(relativePath);
            }

            switch (state.Kind)
            {
                case SyncEntryKind.File:
                    await FinalizeFilePlaceholderAsync(syncPair, relativePath, state, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                case SyncEntryKind.Directory:
                    FinalizeDirectoryPlaceholder(syncPair, relativePath, state);
                    return;
                default:
                    throw CreateMissingFinalizationStateException(relativePath);
            }
        }

        private async Task FinalizeFilePlaceholderAsync(
            SyncPairSettings syncPair,
            string relativePath,
            SyncStateEntry state,
            CancellationToken cancellationToken)
        {
            RemoteFilePlaceholderResult placeholder = await _cloudFiles
                .FinalizeUploadedFilePlaceholderAsync(syncPair, state, cancellationToken)
                .ConfigureAwait(false);
            if (placeholder.PlaceholderIdentity is not { Length: > 0 })
            {
                throw new InvalidOperationException(
                    "Uploaded Cloud Files finalization did not return placeholder identity for "
                    + relativePath
                    + ".");
            }

            state.PlaceholderIdentity = placeholder.PlaceholderIdentity;
            state.PlaceholderHydrationState = placeholder.HydrationState;
            state.LocalSizeBytes = placeholder.LocalSizeBytes ?? state.LocalSizeBytes;
            state.LocalLastWriteUtc = placeholder.LocalLastWriteUtc?.ToUniversalTime() ?? state.LocalLastWriteUtc;
            state.SyncedAtUtc = DateTime.UtcNow;
            await _stateStore.UpsertAsync(state, cancellationToken).ConfigureAwait(false);
        }

        private async Task FinalizeAncestorDirectoriesAsync(
            SyncPairSettings syncPair,
            string relativePath,
            HashSet<string> finalizedPaths,
            Action recordFinalizedPath,
            CancellationToken cancellationToken)
        {
            foreach (string directoryPath in CreateAncestorDirectoryPaths(relativePath).Reverse())
            {
                if (!finalizedPaths.Add(directoryPath))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                SuppressMetadataWrite(syncPair, directoryPath);
                SyncStateEntry? directoryState = await _stateStore
                    .GetAsync(syncPair.Id.ToString("D"), directoryPath, cancellationToken)
                    .ConfigureAwait(false);
                if (directoryState is { Kind: SyncEntryKind.Directory })
                {
                    FinalizeDirectoryPlaceholder(syncPair, directoryPath, directoryState);
                    recordFinalizedPath();
                    continue;
                }

                _cloudFiles.SetInSyncState(syncPair, directoryPath);
                recordFinalizedPath();
            }
        }

        private void SuppressMetadataWrite(SyncPairSettings syncPair, string relativePath)
        {
            _localChangeSuppression?.SuppressProviderMetadataWrite(
                syncPair.Id,
                syncPair.LocalRootPath,
                relativePath);
        }

        private static InvalidOperationException CreateMissingFinalizationStateException(string relativePath)
        {
            return new InvalidOperationException(
                "Uploaded Cloud Files finalization requires synced file or directory state for "
                + relativePath
                + ".");
        }

        private void FinalizeDirectoryPlaceholder(
            SyncPairSettings syncPair,
            string relativePath,
            SyncStateEntry directoryState)
        {
            if (directoryState.RemoteNodeId is not Guid remoteNodeId)
            {
                _cloudFiles.SetInSyncState(syncPair, relativePath);
                return;
            }

            _cloudFiles.CreateDirectoryPlaceholder(new RemoteDirectoryMaterializationRequest(
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                relativePath,
                new NodeDto
                {
                    Id = remoteNodeId,
                    Name = relativePath.Split('/')[^1],
                    CreatedAt = directoryState.SyncedAtUtc,
                    UpdatedAt = directoryState.SyncedAtUtc,
                }));
        }

        private void PublishFinalizationProgress(
            Guid syncPairId,
            SyncRunRequest request,
            DateTime startedAtUtc,
            int finalizedCount,
            int totalCount,
            bool isCompleted)
        {
            _runProgressPublisher?.Publish(new AppRunProgress(
                syncPairId,
                SyncRunProgressStage.FinalizingCloudFiles,
                finalizedCount,
                totalCount,
                string.Empty,
                startedAtUtc,
                isCompleted,
                DateTime.UtcNow,
                causes: request.Causes,
                isFull: request.IsFull,
                requestedPathCount: request.IsFull ? 0 : request.LocalChangedPaths.Count));
        }

        private static int CountFinalizationItems(IReadOnlyList<string> paths)
        {
            var finalizationPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                finalizationPaths.Add(path);
                foreach (string directoryPath in CreateAncestorDirectoryPaths(path))
                {
                    finalizationPaths.Add(directoryPath);
                }
            }

            return finalizationPaths.Count + 1;
        }

        private static IEnumerable<string> CreateAncestorDirectoryPaths(string relativePath)
        {
            string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int length = 1; length < segments.Length; length++)
            {
                yield return string.Join("/", segments.Take(length));
            }
        }

        private class CloudFilesFinalizationActivityCollector : IObserver<AppSyncActivity>
        {
            private readonly Guid _syncPairId;
            private readonly object _gate = new();
            private readonly HashSet<string> _uploadedPaths = new(StringComparer.OrdinalIgnoreCase);

            public CloudFilesFinalizationActivityCollector(Guid syncPairId)
            {
                _syncPairId = syncPairId;
            }

            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
            }

            public void OnNext(AppSyncActivity value)
            {
                ArgumentNullException.ThrowIfNull(value);
                if (value.SyncPairId != _syncPairId
                    || value.Type is not (SyncActivityKind.Uploaded or SyncActivityKind.Converged)
                    || string.IsNullOrWhiteSpace(value.ItemPath))
                {
                    return;
                }

                string normalizedPath;
                try
                {
                    normalizedPath = SyncPath.Normalize(value.ItemPath);
                }
                catch (ArgumentException)
                {
                    return;
                }

                lock (_gate)
                {
                    _uploadedPaths.Add(normalizedPath);
                }
            }

            public IReadOnlyList<string> GetPaths()
            {
                lock (_gate)
                {
                    return [.. _uploadedPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)];
                }
            }
        }
    }
}
