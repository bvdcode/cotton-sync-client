// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Activities;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsVirtualFilesUploadFinalizationPairWork : ISyncPairWork
    {
        private readonly ISyncPairWork _inner;
        private readonly IAppActivityPublisher _activityPublisher;
        private readonly IWindowsCloudFilesAdapter _cloudFiles;
        private readonly ILocalChangeSuppression? _localChangeSuppression;

        public WindowsVirtualFilesUploadFinalizationPairWork(
            ISyncPairWork inner,
            IAppActivityPublisher activityPublisher,
            IWindowsCloudFilesAdapter cloudFiles,
            ILocalChangeSuppression? localChangeSuppression = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _activityPublisher = activityPublisher ?? throw new ArgumentNullException(nameof(activityPublisher));
            _cloudFiles = cloudFiles ?? throw new ArgumentNullException(nameof(cloudFiles));
            _localChangeSuppression = localChangeSuppression;
        }

        public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                await _inner.RunOnceAsync(syncPair, cancellationToken).ConfigureAwait(false);
                return;
            }

            await RunAndFinalizeUploadsAsync(
                syncPair,
                () => _inner.RunOnceAsync(syncPair, cancellationToken),
                cancellationToken).ConfigureAwait(false);
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
                () => _inner.RunOnceAsync(syncPair, request, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task RunAndFinalizeUploadsAsync(
            SyncPairSettings syncPair,
            Func<Task> runInnerAsync,
            CancellationToken cancellationToken)
        {
            var collector = new UploadedActivityCollector(syncPair.Id);
            using IDisposable subscription = _activityPublisher.Subscribe(collector);
            await runInnerAsync().ConfigureAwait(false);

            var finalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string relativePath in collector.GetUploadedPaths().SelectMany(CreateFinalizationPaths))
            {
                if (!finalizedPaths.Add(relativePath))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                _localChangeSuppression?.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, relativePath);
                _cloudFiles.SetInSyncState(syncPair, relativePath);
            }
        }

        private static IEnumerable<string> CreateFinalizationPaths(string relativePath)
        {
            yield return relativePath;

            string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int length = 1; length < segments.Length; length++)
            {
                yield return string.Join("/", segments.Take(length));
            }
        }

        private class UploadedActivityCollector : IObserver<AppSyncActivity>
        {
            private readonly Guid _syncPairId;
            private readonly object _gate = new();
            private readonly HashSet<string> _uploadedPaths = new(StringComparer.OrdinalIgnoreCase);

            public UploadedActivityCollector(Guid syncPairId)
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
                    || value.Type != SyncActivityKind.Uploaded
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

            public IReadOnlyList<string> GetUploadedPaths()
            {
                lock (_gate)
                {
                    return [.. _uploadedPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)];
                }
            }
        }
    }
}
