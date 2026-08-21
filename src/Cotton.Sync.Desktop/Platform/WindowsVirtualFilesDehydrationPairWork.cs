// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Collections.Concurrent;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsVirtualFilesDehydrationPairWork : ISyncPairWork
    {
        private const int AvailabilityStateWriteBatchSize = 128;
        private const int FileAttributePinned = 0x00080000;
        private const int FileAttributeUnpinned = 0x00100000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;

        private readonly ISyncPairWork _inner;
        private readonly ISyncStateStore _stateStore;
        private readonly IWindowsCloudFilesAdapter _cloudFiles;
        private readonly ILocalFileContentHasher _contentHasher;
        private readonly IWindowsCloudFilesDiagnostics _diagnostics;
        private readonly ILocalChangeSuppression? _localChangeSuppression;
        private readonly IAppRunProgressPublisher? _runProgressPublisher;
        private readonly Func<string, WindowsVirtualFileDiskState?> _readDiskState;
        private readonly ConcurrentDictionary<Guid, byte> _availabilityRecoveryCompleted = new();

        public WindowsVirtualFilesDehydrationPairWork(
            ISyncPairWork inner,
            ISyncStateStore stateStore,
            IWindowsCloudFilesAdapter cloudFiles,
            ILocalFileContentHasher? contentHasher = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null,
            Func<string, WindowsVirtualFileDiskState?>? readDiskState = null,
            ILocalChangeSuppression? localChangeSuppression = null,
            IAppRunProgressPublisher? runProgressPublisher = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _cloudFiles = cloudFiles ?? throw new ArgumentNullException(nameof(cloudFiles));
            _contentHasher = contentHasher ?? new LocalFileScanner();
            _diagnostics = diagnostics ?? WindowsCloudFilesDiagnostics.Shared;
            _localChangeSuppression = localChangeSuppression;
            _runProgressPublisher = runProgressPublisher;
            _readDiskState = readDiskState ?? ReadDiskState;
        }

        public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
        {
            return _inner.RunOnceAsync(syncPair, cancellationToken);
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

            await RecoverAvailabilityIfRequiredAsync(syncPair, request, cancellationToken).ConfigureAwait(false);
            WindowsVirtualFilesAvailabilityRun run = await CreateAvailabilityRunAsync(
                    syncPair,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            await ProcessAvailabilityPathsAsync(syncPair, run, cancellationToken).ConfigureAwait(false);
            await RunRemainingSyncAsync(syncPair, run, cancellationToken).ConfigureAwait(false);
        }



























































































    }
}
